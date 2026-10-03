using System.Net.Http.Headers;
using System.Text.Json;
using MemeShorts.Api.Models;
using Microsoft.Extensions.Options;

namespace MemeShorts.Api.Services;

/// <summary>
/// Shared plumbing for the extra photo sources: paced requests, a download that is measured before
/// it is kept, and the same "does it actually show the thing" check Commons uses.
/// </summary>
public abstract class StockPhotoSource
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _last = DateTime.MinValue;
    private static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(700);

    protected readonly IHttpClientFactory Http;
    protected readonly ILogger Log;

    protected StockPhotoSource(IHttpClientFactory http, ILogger log)
    {
        Http = http;
        Log = log;
    }

    protected abstract string ClientName { get; }

    private static readonly System.Text.RegularExpressions.Regex People = new(
        @"\b(man|men|woman|women|person|people|girl|boy|child|children|kid|kids|doctor|patient|nurse|lady|guy|" +
        @"couple|portrait|face|faces|selfie|family|teen|teenager|baby|student|worker|customer|model|smiling)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex Graphic = new(
        @"\b(mockup|mock-up|psd|vector|illustration|template|clipart|clip art|icon|logo|poster|flyer|infographic|" +
        @"render|3d|drawing|cartoon|screenshot)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// A photo described as showing a person. Real photos never carry people here: a scene with
    /// real faces under mocking narration is how two TV personalities and a child got into a video
    /// (2026-09-27). Stock "a doctor checks a patient's oxygen levels" for "finger pulse oximeter"
    /// is the same risk. Hands are fine and are not matched.
    /// </summary>
    protected static bool ShowsPeople(string description) => People.IsMatch(description);

    /// <summary>A mockup, template, poster or drawing tagged as a photo ("toothpaste tube mockup psd").</summary>
    protected static bool IsGraphic(string description) => Graphic.IsMatch(description);

    protected async Task<HttpResponseMessage> PacedGetAsync(string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var since = DateTime.UtcNow - _last;
            if (since < MinGap) await Task.Delay(MinGap - since, ct);
            _last = DateTime.UtcNow;
            return await Http.CreateClient(ClientName).GetAsync(url, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Downloads a picture and keeps it only if it is really large enough to fill a card.</summary>
    protected async Task<string?> DownloadAsync(string url, string destDir, string nameWithoutExt, CancellationToken ct)
    {
        try
        {
            using var res = await PacedGetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return null;
            var ext = (res.Content.Headers.ContentType?.MediaType ?? "") switch
            {
                "image/jpeg" => ".jpg",
                "image/webp" => ".webp",
                "image/png" => ".png",
                _ => ".jpg",
            };
            var fileName = nameWithoutExt + ext;
            var path = Path.Combine(destDir, fileName);
            await using (var fs = File.Create(path))
                await res.Content.CopyToAsync(fs, ct);
            if (OperatingSystem.IsWindows() && WikimediaImageService.BigEnough(path, out _, out _)) return fileName;
            try { File.Delete(path); } catch { /* best effort */ }
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LogInformation("Could not download {Url}: {Why}", url, ex.Message);
            return null;
        }
    }
}

/// <summary>
/// Openverse (openverse.org, run by WordPress): hundreds of millions of openly licensed pictures -
/// Flickr and others - searchable by words, each with its creator and licence. Only licences that
/// allow commercial use AND modification are asked for (the video crops and cuts pictures out), and
/// only photographs. A result is kept only when its own title or tags name the thing
/// (CommonsRelevance), which is what stops a loose word match becoming the picture.
/// </summary>
public sealed class OpenverseImageService : StockPhotoSource
{
    public OpenverseImageService(IHttpClientFactory http, ILogger<OpenverseImageService> log) : base(http, log) { }

    protected override string ClientName => "openverse";

    public async Task<FoundImage?> FetchAsync(string term, string destDir, string saveAs,
        IReadOnlyCollection<string>? exclude, CancellationToken ct)
    {
        var url = "https://api.openverse.org/v1/images/?page_size=12&category=photograph&mature=false" +
                  $"&license_type=commercial,modification&q={Uri.EscapeDataString(term)}";
        JsonDocument doc;
        try
        {
            using var res = await PacedGetAsync(url, ct);
            if (!res.IsSuccessStatusCode)
            {
                Log.LogInformation("Openverse search failed ({Code}) for '{Term}'.", (int)res.StatusCode, term);
                return null;
            }
            doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LogInformation("Openverse search failed for '{Term}': {Why}", term, ex.Message);
            return null;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("results", out var results)) return null;
            foreach (var r in results.EnumerateArray())
            {
                var id = Str(r, "id");
                if (exclude?.Contains("openverse:" + id) == true) continue;
                var w = r.TryGetProperty("width", out var wv) && wv.ValueKind == JsonValueKind.Number ? wv.GetInt32() : 0;
                var h = r.TryGetProperty("height", out var hv) && hv.ValueKind == JsonValueKind.Number ? hv.GetInt32() : 0;
                if ((w > 0 && w < 600) || (h > 0 && h < 450)) continue;

                var title = Str(r, "title");
                var tags = r.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                    ? t.EnumerateArray().Select(x => Str(x, "name")).Where(x => x.Length > 0).ToList()
                    : new List<string>();
                // The phrase must be in the title or inside ONE tag: joined together, neighbouring
                // tags made "juice box" out of a meat-juice ad's "juice" and "box" (2026-09-30).
                var named = CommonsRelevance.Names(term, CommonsRelevance.Tokens(title))
                            || tags.Any(tag => CommonsRelevance.Names(term, CommonsRelevance.Tokens(tag)));
                var all = title + " " + string.Join(' ', tags);
                if (!named || ShowsPeople(all) || IsGraphic(all)) continue;

                var saved = await DownloadAsync(Str(r, "url"), destDir, saveAs, ct);
                if (saved is null) continue;
                var license = $"{Str(r, "license").ToUpperInvariant()} {Str(r, "license_version")}".Trim();
                var credit = $"{title} — {Str(r, "creator")} ({license}), via Openverse ({Str(r, "source")})";
                Log.LogInformation("Openverse: '{Term}' -> '{Title}' ({License}).", term, title, license);
                return new FoundImage(saved, credit, "openverse:" + id);
            }
        }
        return null;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

/// <summary>
/// Pexels (pexels.com): free stock photos of everyday things, free for commercial use and to modify.
/// Pexels' own search is loose, so a photo is kept only when its written description (alt text) or
/// page name says what the script asked for.
/// </summary>
public sealed class PexelsImageService : StockPhotoSource
{
    private readonly PicturesOptions _opt;

    public PexelsImageService(IHttpClientFactory http, IOptions<PicturesOptions> opt, ILogger<PexelsImageService> log)
        : base(http, log) => _opt = opt.Value;

    protected override string ClientName => "pexels";

    public bool Configured => !string.IsNullOrWhiteSpace(_opt.PexelsApiKey);

    public async Task<FoundImage?> FetchAsync(string term, string destDir, string saveAs,
        IReadOnlyCollection<string>? exclude, CancellationToken ct)
    {
        if (!Configured) return null;
        JsonDocument doc;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.pexels.com/v1/search?per_page=15&query={Uri.EscapeDataString(term)}");
            req.Headers.TryAddWithoutValidation("Authorization", _opt.PexelsApiKey);
            using var res = await Http.CreateClient(ClientName).SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                Log.LogInformation("Pexels search failed ({Code}) for '{Term}'.", (int)res.StatusCode, term);
                return null;
            }
            doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LogInformation("Pexels search failed for '{Term}': {Why}", term, ex.Message);
            return null;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("photos", out var photos)) return null;
            foreach (var p in photos.EnumerateArray())
            {
                var id = p.TryGetProperty("id", out var iv) ? iv.ToString() : "";
                if (exclude?.Contains("pexels:" + id) == true) continue;
                var alt = p.TryGetProperty("alt", out var a) ? a.GetString() ?? "" : "";
                // The page name spells out the photo: /photo/close-up-of-a-toothpaste-tube-3059960/
                var page = p.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var described = alt + " " + page.Replace('-', ' ');
                if (!CommonsRelevance.NamesInDescription(term, CommonsRelevance.Tokens(described))) continue;
                if (ShowsPeople(described) || IsGraphic(described)) continue;
                if (!p.TryGetProperty("src", out var src) || !src.TryGetProperty("large2x", out var big)) continue;

                var saved = await DownloadAsync(big.GetString() ?? "", destDir, saveAs, ct);
                if (saved is null) continue;
                var who = p.TryGetProperty("photographer", out var ph) ? ph.GetString() ?? "" : "";
                Log.LogInformation("Pexels: '{Term}' -> '{Alt}' by {Who}.", term, alt, who);
                return new FoundImage(saved, $"Photo by {who} on Pexels", "pexels:" + id);
            }
        }
        return null;
    }
}

/// <summary>Adds the extra photo sources' HTTP clients (a descriptive User-Agent, as both ask for).</summary>
public static class StockPhotoSetup
{
    public static void Configure(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(45);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MemeShortsGenerator/1.0 (+personal project)");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
    }
}
