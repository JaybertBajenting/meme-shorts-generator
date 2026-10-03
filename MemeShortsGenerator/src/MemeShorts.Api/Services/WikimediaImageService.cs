using System.Net.Http.Headers;
using System.Text.Json;

namespace MemeShorts.Api.Services;

/// <summary>
/// One picture found on Wikimedia Commons, already downloaded into the job folder.
/// <paramref name="SourceTitle"/> is the Commons page title, which identifies the picture
/// across searches — two different search terms often return the same file.
/// </summary>
public sealed record FoundImage(string FileName, string Credit, string SourceTitle);

public interface IStockImageService
{
    /// <summary>
    /// Find a picture for a search term and save it into <paramref name="destDir"/>.
    /// Null when nothing usable came back — the caller decides whether to drop the line.
    /// </summary>
    /// <param name="exclude">
    /// Commons page titles already used elsewhere in this video. Searches for neighbouring
    /// lines overlap heavily ("Sholes and Glidden typewriter" and "Remington No 1 typewriter"
    /// share six of eight results), so without this the same photo appears twice.
    /// </param>
    /// <param name="kind">A <see cref="PictureKind"/>; decides drawn-first vs Commons-first.</param>
    Task<FoundImage?> FetchAsync(string term, string destDir, string saveAsWithoutExt,
        IReadOnlyCollection<string>? exclude = null, CancellationToken ct = default,
        bool exactTermOnly = false, string? kind = null);
}

/// <summary>
/// Pictures from Wikimedia Commons, which is the only image source here that is both searchable
/// by plain words and safe to publish: everything on it carries an explicit licence, and the
/// credit line comes back with the file so the description can carry the attribution those
/// licences require.
/// </summary>
public sealed class WikimediaImageService : IStockImageService
{
    private const string Api = "https://commons.wikimedia.org/w/api.php";

    private readonly IHttpClientFactory _http;
    private readonly ILogger<WikimediaImageService> _log;

    // Commons throttles a burst of back-to-back searches with 429s. One video fires a search
    // per line plus up to three term variants each, which is more than enough to trip it, so
    // requests are serialised and spaced.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _lastRequestUtc = DateTime.MinValue;
    private static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(400);

    public WikimediaImageService(IHttpClientFactory http, ILogger<WikimediaImageService> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>Send a request no sooner than <see cref="MinGap"/> after the previous one.</summary>
    private static async Task<HttpResponseMessage> PacedGetAsync(HttpClient client, string url, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var since = DateTime.UtcNow - _lastRequestUtc;
            if (since < MinGap) await Task.Delay(MinGap - since, ct);
            _lastRequestUtc = DateTime.UtcNow;
            return await client.GetAsync(url, ct);
        }
        finally { Gate.Release(); }
    }

    /// <summary>A picture the search offered, before anything has been downloaded.</summary>
    private readonly record struct Candidate(
        string Title, string Url, string Credit, int Width, int Height, int SearchIndex, string[] Tokens);

    // Each rejected candidate costs a full download, so stop after a few rather than working
    // through everything eight results per variant can offer.
    private const int MaxDownloads = 4;

    public async Task<FoundImage?> FetchAsync(
        string term, string destDir, string saveAsWithoutExt,
        IReadOnlyCollection<string>? exclude = null, CancellationToken ct = default,
        bool exactTermOnly = false, string? kind = null)
    {
        var cleaned = term.Trim();
        if (cleaned.Length == 0) return null;

        var skip = new HashSet<string>(exclude ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var downloads = 0;

        // Try the phrase, then progressively shorter versions of it. A specific term returns a
        // better picture when it hits, but "1936 Edward VIII brass threepence obverse" hits
        // nothing at all — the first few words usually still do.
        foreach (var attempt in exactTermOnly ? new[] { cleaned } : Variants(cleaned).ToArray())
        {
            var candidates = await SearchAsync(attempt, ct);
            if (candidates is null)
            {
                // The search FAILED rather than came back empty. Falling through to a shorter
                // variant here is how "vintage school alphabet book ampersand" quietly became a
                // search for "vintage" and returned a photo of a typewriter. A broken search is
                // not evidence that the term was too specific — give up and report nothing.
                _log.LogWarning("Commons search errored for '{Term}'; not degrading the term.", term);
                return null;
            }

            foreach (var c in candidates)
            {
                if (skip.Contains(c.Title) || !tried.Add(c.Title)) continue;
                // An exact-name lookup wants THAT thing, so a file that merely shares a word with
                // it is no answer - see CommonsRelevance. Checked before downloading, so a
                // rejection costs nothing; the caller draws the picture instead.
                if (exactTermOnly && !CommonsRelevance.Names(cleaned, c.Tokens))
                {
                    _log.LogInformation("Commons: skipping {Title} for '{Term}' — it doesn't name the thing.",
                        c.Title, term);
                    continue;
                }
                if (CommonsRelevance.ShowsPeople(c.Tokens))
                {
                    _log.LogInformation("Commons: skipping {Title} for '{Term}' — it shows a person.", c.Title, term);
                    continue;
                }
                if (downloads >= MaxDownloads) break;
                downloads++;

                var saved = await DownloadAsync(c.Url, destDir, saveAsWithoutExt, ct);
                if (saved is null) continue;

                // The size the search reports is a promise, not a fact: Commons listed
                // Sholesglidden2.png at 1400x2275 and then served a 384px rendition, which
                // rendered as a blurry smear filling a 1010px card. Measure what landed.
                var path = Path.Combine(destDir, saved);
                if (!BigEnough(path, out var w, out var h))
                {
                    _log.LogInformation(
                        "Commons: skipping {Title} for '{Term}' — served {W}x{H}, too small.",
                        c.Title, term, w, h);
                    try { File.Delete(path); } catch { /* best effort */ }
                    continue;
                }

                _log.LogInformation("Commons: '{Term}' -> {File} ({Credit}).", term, saved, c.Credit);
                return new FoundImage(saved, c.Credit, c.Title);
            }
        }

        _log.LogWarning("Commons found nothing usable for '{Term}'.", term);
        return null;

        static IEnumerable<string> Variants(string s)
        {
            yield return s;
            var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 3) yield return string.Join(' ', words.Take(3));
            if (words.Length > 1) yield return words[0];
        }
    }

    /// <summary>Read the saved file's real pixel size and judge whether it can fill a card.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static bool BigEnough(string path, out int width, out int height)
    {
        width = height = 0;
        try
        {
            using var img = System.Drawing.Image.FromFile(path);
            width = img.Width;
            height = img.Height;
        }
        catch
        {
            return false;   // unreadable is as useless as tiny
        }
        return width >= 500 && height >= 400;
    }

    /// <summary>
    /// Every usable result for a term, best first. A list rather than a single pick, because
    /// the best one is only a guess until it has been downloaded and measured.
    /// Returns NULL when the search itself failed, which is a different thing from an empty
    /// list, and the caller must not treat the two the same way.
    /// </summary>
    private Task<IReadOnlyList<Candidate>?> SearchAsync(string term, CancellationToken ct) =>
        // filetype:bitmap keeps out SVG icons, PDFs and audio, which the search happily returns
        // otherwise and which are useless as a card.
        CandidatesAsync(
            $"{Api}?action=query&format=json&generator=search&gsrnamespace=6&gsrlimit=8" +
            $"&gsrsearch={Uri.EscapeDataString($"filetype:bitmap {term}")}" +
            "&prop=imageinfo&iiprop=url|extmetadata|size&iiurlwidth=1400", term, ct);

    /// <summary>
    /// A GET against a Wikimedia API, waiting out 429s and 5xx. Null when it failed, which is a
    /// different thing from an empty answer.
    /// </summary>
    private async Task<JsonDocument?> GetJsonAsync(string url, string term, CancellationToken ct)
    {
        var client = _http.CreateClient("wikimedia");
        HttpResponseMessage? res = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            res?.Dispose();
            res = await PacedGetAsync(client, url, ct);
            if (res.IsSuccessStatusCode) break;

            // 429 and 5xx are worth waiting out; anything else is not going to fix itself.
            var retryable = (int)res.StatusCode == 429 || (int)res.StatusCode >= 500;
            if (!retryable || attempt == 4)
            {
                _log.LogWarning("Wikimedia request failed ({Code}) for '{Term}'.", (int)res.StatusCode, term);
                res.Dispose();
                return null;
            }

            var wait = res.Headers.RetryAfter?.Delta
                       ?? TimeSpan.FromMilliseconds(700 * Math.Pow(2, attempt - 1));
            _log.LogInformation("Wikimedia {Code} for '{Term}'; waiting {Ms}ms (attempt {N}/4).",
                (int)res.StatusCode, term, (int)wait.TotalMilliseconds, attempt);
            await Task.Delay(wait, ct);
        }

        using var response = res!;
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<IReadOnlyList<Candidate>?> CandidatesAsync(string query, string term, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(query, term, ct);
        if (doc is null) return null;
        if (!doc.RootElement.TryGetProperty("query", out var q) ||
            !q.TryGetProperty("pages", out var pages)) return Array.Empty<Candidate>();

        var found = new List<Candidate>();
        foreach (var page in pages.EnumerateObject())
        {
            if (!page.Value.TryGetProperty("imageinfo", out var infos) || infos.GetArrayLength() == 0) continue;
            var info = infos[0];
            if (!info.TryGetProperty("thumburl", out var thumb)) continue;

            var w = info.TryGetProperty("thumbwidth", out var tw) ? tw.GetInt32() : 0;
            var h = info.TryGetProperty("thumbheight", out var th) ? th.GetInt32() : 0;
            if (w < 400 || h < 300) continue;                    // too small to fill a card
            if (h > w * 2.2) continue;                           // a tall banner crops badly

            var meta = info.TryGetProperty("extmetadata", out var m) ? m : default;
            var title = page.Value.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var searchIndex = page.Value.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var rank)
                ? rank
                : int.MaxValue;
            var credit = $"{title.Replace("File:", "")} — {MetaValue(meta, "Artist")} " +
                         $"({MetaValue(meta, "LicenseShortName")}), via Wikimedia Commons";

            // What the file says it shows, for CommonsRelevance: the name alone misses correct
            // photos whose file is called after the place it was taken ("Takanawa-Gateway-City...
            // 119.jpg" is the Dyson Airblade Wash+Dry; only its description says so).
            var tokens = CommonsRelevance.Tokens(string.Join(' ', title,
                RawMeta(meta, "ObjectName"), RawMeta(meta, "ImageDescription"), RawMeta(meta, "Categories")));
            found.Add(new Candidate(title, thumb.GetString()!, Tidy(credit), w, h, searchIndex, tokens));
        }

        // MediaWiki already ranked these by textual relevance. Keep that ordering: sorting all
        // eight only by portrait shape promoted attractive but unrelated results over the file
        // that actually matched the phrase. Shape is only a tie-breaker (or the fallback when
        // the API omitted an index).
        return found
            .OrderBy(c => c.SearchIndex)
            .ThenByDescending(c => Score(c.Width, c.Height))
            .ToList();

        // The reference foreground is normally phone-video portrait or a near-square screenshot.
        // Prefer that shape once an image is large enough, or every automatic job gets a wide
        // stock-photo crop even though the renderer is correctly using media-shaped cards.
        static double Score(int w, int h)
        {
            const double Ideal = 0.9;                       // portrait-to-square
            var offShape = Math.Abs(Math.Log((double)w / h / Ideal));
            var size = Math.Log(Math.Min((long)w * h, 4_000_000));
            return size - offShape * 3.0;
        }

        static string RawMeta(JsonElement meta, string key) =>
            meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty(key, out var el)
            && el.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : "";

        static string MetaValue(JsonElement meta, string key) =>
            meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty(key, out var el)
            && el.TryGetProperty("value", out var v)
                ? Tidy(v.GetString() ?? "")
                : "unknown";

        // Commons returns HTML in these fields (links round the author name, mostly).
        static string Tidy(string s)
        {
            var text = System.Text.RegularExpressions.Regex.Replace(s, "<.*?>", " ");
            text = System.Net.WebUtility.HtmlDecode(text);
            return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        }
    }

    /// <summary>
    /// The main picture of the Wikipedia article about <paramref name="term"/>, when there is one
    /// and it is freely licensed. For a named thing or a plain object ("hagfish", "Game Boy Color",
    /// "blister pack") the article's own picture is usually exactly it, where a Commons search by
    /// words returns whatever file happens to share a word.
    ///
    /// Only pictures that live ON COMMONS are taken. Many articles lead with a copyrighted file kept
    /// on English Wikipedia under fair use - a logo, a game cover, a screenshot - and those are not
    /// ours to publish. The article must name the thing (CommonsRelevance) and must not be a
    /// disambiguation page.
    /// </summary>
    public async Task<FoundImage?> FetchFromWikipediaAsync(string term, string destDir, string saveAsWithoutExt,
        IReadOnlyCollection<string>? exclude, CancellationToken ct)
    {
        var cleaned = term.Trim();
        if (cleaned.Length == 0) return null;
        var skip = new HashSet<string>((exclude ?? Array.Empty<string>()).Select(NormTitle), StringComparer.OrdinalIgnoreCase);

        using var doc = await GetJsonAsync(
            "https://en.wikipedia.org/w/api.php?action=query&format=json&generator=search&gsrlimit=3" +
            $"&gsrsearch={Uri.EscapeDataString(cleaned)}&prop=pageimages|description|pageprops" +
            "&piprop=original|name&ppprop=disambiguation&redirects=1", cleaned, ct);
        if (doc is null || !doc.RootElement.TryGetProperty("query", out var q) || !q.TryGetProperty("pages", out var pages))
            return null;

        var articles = pages.EnumerateObject().Select(p => p.Value)
            .OrderBy(p => p.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var i) ? i : int.MaxValue)
            .ToList();
        foreach (var page in articles)
        {
            var title = page.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            if (page.TryGetProperty("pageprops", out var props) && props.TryGetProperty("disambiguation", out _)) continue;
            var description = page.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            if (!CommonsRelevance.Names(cleaned, CommonsRelevance.Tokens(title + " " + description))) continue;
            if (!page.TryGetProperty("pageimage", out var pi) || !page.TryGetProperty("original", out var original)) continue;
            var source = original.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "";
            if (!source.Contains("/wikipedia/commons/", StringComparison.OrdinalIgnoreCase)) continue;   // fair-use file
            if (source.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) continue;                    // logo or diagram

            var fileTitle = "File:" + pi.GetString();
            if (skip.Contains(NormTitle(fileTitle))) continue;
            var found = await CandidatesAsync(
                $"{Api}?action=query&format=json&titles={Uri.EscapeDataString(fileTitle)}" +
                "&prop=imageinfo&iiprop=url|extmetadata|size&iiurlwidth=1400", cleaned, ct);
            if (found is not { Count: > 0 }) continue;
            var c = found[0];
            // The article is about the thing; its picture can still be of someone holding it.
            if (CommonsRelevance.ShowsPeople(c.Tokens)) continue;
            var saved = await DownloadAsync(c.Url, destDir, saveAsWithoutExt, ct);
            if (saved is null) continue;
            if (!BigEnough(Path.Combine(destDir, saved), out var w, out var h))
            {
                try { File.Delete(Path.Combine(destDir, saved)); } catch { /* best effort */ }
                continue;
            }
            _log.LogInformation("Wikipedia: '{Term}' -> article '{Article}', {File} ({W}x{H}).", cleaned, title, c.Title, w, h);
            return new FoundImage(saved, c.Credit, c.Title);
        }
        return null;

        static string NormTitle(string t) => t.Replace('_', ' ').Trim();
    }

    private async Task<string?> DownloadAsync(string url, string destDir, string nameWithoutExt, CancellationToken ct)
    {
        try
        {
            var client = _http.CreateClient("wikimedia");
            using var res = await PacedGetAsync(client, url, ct);
            if (!res.IsSuccessStatusCode)
            {
                _log.LogWarning("Commons download failed ({Code}) for {Url}.", (int)res.StatusCode, url);
                return null;
            }

            var ext = (res.Content.Headers.ContentType?.MediaType ?? "") switch
            {
                "image/jpeg" => ".jpg",
                "image/webp" => ".webp",
                "image/gif" => ".gif",
                _ => ".png",
            };
            var fileName = nameWithoutExt + ext;
            await using var fs = File.Create(Path.Combine(destDir, fileName));
            await res.Content.CopyToAsync(fs, ct);
            return fileName;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not download {Url} from Commons.", url);
            return null;
        }
    }

    /// <summary>Commons asks for a descriptive User-Agent and blocks requests without one.</summary>
    public static void Configure(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(45);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MemeShortsGenerator/1.0 (+personal project)");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
    }
}
