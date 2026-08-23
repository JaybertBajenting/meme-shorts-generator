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
    Task<FoundImage?> FetchAsync(string term, string destDir, string saveAsWithoutExt,
        IReadOnlyCollection<string>? exclude = null, CancellationToken ct = default,
        bool exactTermOnly = false);
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
    private readonly record struct Candidate(string Title, string Url, string Credit, int Width, int Height);

    // Each rejected candidate costs a full download, so stop after a few rather than working
    // through everything eight results per variant can offer.
    private const int MaxDownloads = 4;

    public async Task<FoundImage?> FetchAsync(
        string term, string destDir, string saveAsWithoutExt,
        IReadOnlyCollection<string>? exclude = null, CancellationToken ct = default,
        bool exactTermOnly = false)
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
    private static bool BigEnough(string path, out int width, out int height)
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
    private async Task<IReadOnlyList<Candidate>?> SearchAsync(string term, CancellationToken ct)
    {
        // filetype:bitmap keeps out SVG icons, PDFs and audio, which the search happily returns
        // otherwise and which are useless as a card.
        var query =
            $"{Api}?action=query&format=json&generator=search&gsrnamespace=6&gsrlimit=8" +
            $"&gsrsearch={Uri.EscapeDataString($"filetype:bitmap {term}")}" +
            "&prop=imageinfo&iiprop=url|extmetadata|size&iiurlwidth=1400";

        var client = _http.CreateClient("wikimedia");

        HttpResponseMessage? res = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            res?.Dispose();
            res = await PacedGetAsync(client, query, ct);
            if (res.IsSuccessStatusCode) break;

            // 429 and 5xx are worth waiting out; anything else is not going to fix itself.
            var retryable = (int)res.StatusCode == 429 || (int)res.StatusCode >= 500;
            if (!retryable || attempt == 4)
            {
                _log.LogWarning("Commons search failed ({Code}) for '{Term}'.", (int)res.StatusCode, term);
                res.Dispose();
                return null;
            }

            var wait = res.Headers.RetryAfter?.Delta
                       ?? TimeSpan.FromMilliseconds(700 * Math.Pow(2, attempt - 1));
            _log.LogInformation("Commons {Code} for '{Term}'; waiting {Ms}ms (attempt {N}/4).",
                (int)res.StatusCode, term, (int)wait.TotalMilliseconds, attempt);
            await Task.Delay(wait, ct);
        }

        using var response = res!;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
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
            var credit = $"{title.Replace("File:", "")} — {MetaValue(meta, "Artist")} " +
                         $"({MetaValue(meta, "LicenseShortName")}), via Wikimedia Commons";

            found.Add(new Candidate(title, thumb.GetString()!, Tidy(credit), w, h));
        }

        return found.OrderByDescending(c => Score(c.Width, c.Height)).ToList();

        // Ranking by raw pixel count picked the biggest scan, which on Commons is usually a tall
        // page from a book — it sits in a landscape-ish card as a sliver with bars either side.
        // Shape matters more than size once a picture is big enough to fill the card at all.
        static double Score(int w, int h)
        {
            const double Ideal = 1.4;                       // a little wider than tall
            var offShape = Math.Abs(Math.Log((double)w / h / Ideal));
            var size = Math.Log(Math.Min((long)w * h, 4_000_000));
            return size - offShape * 3.0;
        }

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
