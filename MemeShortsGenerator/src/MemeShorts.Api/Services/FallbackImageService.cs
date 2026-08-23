using MemeShorts.Api.Models;

namespace MemeShorts.Api.Services;

/// <summary>
/// Wikimedia first, then Codex draws one.
///
/// Commons is an archive of photographs people happened to upload, so for a specific subject it
/// simply may hold nothing — "external tank foam" and "space shuttle liftoff" both came back
/// empty on one draft, leaving two lines of a five-line video with no picture at all. Those
/// lines then had to hold the previous picture over, which reads as a stall.
///
/// A drawn picture is a worse source than a real photograph (it is invented, and it costs a
/// minute), so it is only ever the SECOND choice — never a substitute for a search that would
/// have succeeded.
/// </summary>
public sealed class FallbackImageService : IStockImageService
{
    private readonly WikimediaImageService _commons;
    private readonly ICodexClient _codex;
    private readonly ILogger<FallbackImageService> _log;

    public FallbackImageService(WikimediaImageService commons, ICodexClient codex,
        ILogger<FallbackImageService> log)
    {
        _commons = commons;
        _codex = codex;
        _log = log;
    }

    public async Task<FoundImage?> FetchAsync(string term, string destDir, string saveAsWithoutExt,
        IReadOnlyCollection<string>? exclude = null, CancellationToken ct = default,
        bool exactTermOnly = false)
    {
        // A caller asking for the exact term only wants the archive, not an invention.
        if (exactTermOnly)
            return await _commons.FetchAsync(term, destDir, saveAsWithoutExt, exclude, ct, true);

        // The EXACT term first. Commons also accepts progressively shorter versions of a phrase,
        // and those nearly always match something — "a single blue sock drying on a radiator"
        // shortened to "a single blue" and returned a water lily. So a loosely-related photo,
        // not an empty result, is the usual failure, and a fallback that only fires on "nothing
        // found" would almost never fire at all.
        var found = await _commons.FetchAsync(term, destDir, saveAsWithoutExt, exclude, ct,
            exactTermOnly: true);
        if (found is not null) return found;

        _log.LogInformation("Commons had nothing for the exact term '{Term}'; asking Codex to draw it.", term);
        var fileName = saveAsWithoutExt + ".png";
        var path = Path.Combine(destDir, fileName);
        if (!await _codex.GenerateStockImageAsync(term, path, ct)
            || !File.Exists(path) || new FileInfo(path).Length == 0)
        {
            // Codex could not draw it either — only now is a loosely-related photograph better
            // than nothing, because the alternative is a line with no picture at all.
            _log.LogInformation("Codex could not draw '{Term}'; falling back to a broader search.", term);
            return await _commons.FetchAsync(term, destDir, saveAsWithoutExt, exclude, ct);
        }

        // The source title is what stops the SAME picture being used twice in one video. A drawn
        // one is unique by construction, so the term keeps them apart.
        return new FoundImage(fileName, "Illustration generated for this video.", $"generated:{term}");
    }
}
