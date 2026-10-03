using MemeShorts.Api.Models;
using Microsoft.Extensions.Options;

namespace MemeShorts.Api.Services;

/// <summary>
/// What a list picture is, which decides where it comes from. Chosen per line by the script.
///
/// Matthew chose (2026-09-26) to have Codex draw pictures first: Commons matches search terms
/// against file NAMES, and across three test videos it kept returning the wrong thing - "potassium
/// sample" gave potassium permanganate, "kitchens" a road sign for Kitchener, "granite block" a set
/// of wooden fish drums. Only about 1 picture in 14 cut out onto a white sticker, where about 40%
/// of CobbleCat's cards are one.
///
/// But a drawing of one SPECIFIC real thing - the Schienenzeppelin, London Bridge, a named town
/// hall - is a plausible guess that is wrong in its details, so those still try a real photograph
/// first. That is the "real" kind.
/// </summary>
public static class PictureKind
{
    /// <summary>One thing (a bone, a violin, a traffic cone): a real photo when one exists, else drawn on white.</summary>
    public const string Object = "object";
    /// <summary>A generic situation or place (a crowded platform, a desert): drawn as a full photo.</summary>
    public const string Scene = "scene";
    /// <summary>One specific real thing that exists or existed: a real photo first, drawn if none.</summary>
    public const string Real = "real";

    public static string? Normalize(string? kind) => (kind ?? "").Trim().ToLowerInvariant() switch
    {
        Object => Object,
        Scene => Scene,
        Real => Real,
        _ => null,
    };

    /// <summary>
    /// Only scenes are drawn without looking for a real photo first. Objects were drawn first
    /// too until 2026-09-30, when real photos became trustworthy - the Wikipedia article's own
    /// picture plus CommonsRelevance - and Matthew asked to draw only when no real picture exists.
    /// </summary>
    public static bool DrawnFirst(string? kind) => Normalize(kind) is Scene;
}

/// <summary>
/// Picture sourcing for list shorts, real photos first: the Wikipedia article's picture, then the
/// exact Commons term (both checked by CommonsRelevance), and a Codex drawing only when neither has
/// the thing. Scenes are the exception and are always drawn: a real photo of a situation shows real,
/// identifiable people.
///
/// Why real first (2026-09-30): each drawing costs a Codex call (~30k tokens) and a Plus-plan
/// limit that runs out after a few videos, and a drawing of a real product is a guess. The old
/// reason for drawing objects first - Commons matching file names, "potassium sample" returning
/// potassium permanganate - is what the Wikipedia lookup and the relevance check now handle.
/// </summary>
public sealed class FallbackImageService : IStockImageService
{
    private readonly WikimediaImageService _commons;
    private readonly OpenverseImageService _openverse;
    private readonly PexelsImageService _pexels;
    private readonly PicturesOptions _pictures;
    private readonly ICodexClient _codex;
    private readonly ILogger<FallbackImageService> _log;

    public FallbackImageService(WikimediaImageService commons, OpenverseImageService openverse,
        PexelsImageService pexels, IOptions<PicturesOptions> pictures, ICodexClient codex,
        ILogger<FallbackImageService> log)
    {
        _commons = commons;
        _openverse = openverse;
        _pexels = pexels;
        _pictures = pictures.Value;
        _codex = codex;
        _log = log;
    }

    public async Task<FoundImage?> FetchAsync(string term, string destDir, string saveAsWithoutExt,
        IReadOnlyCollection<string>? exclude = null, CancellationToken ct = default,
        bool exactTermOnly = false, string? kind = null)
    {
        // A caller asking for the exact term only wants the archive, not an invention.
        if (exactTermOnly)
            return await ArchiveAsync(term, destDir, saveAsWithoutExt, exclude, ct);

        kind = PictureKind.Normalize(kind);
        if (kind == PictureKind.Scene)
        {
            // A SCENE never comes from Commons or Openverse. Scenes are situations with people in
            // them, and on 2026-09-27, with Codex out of quota mid-video, a Commons search put
            // identifiable real people on screen - two TV personalities and a young child - under
            // narration mocking what was shown. Pexels is stock photography (consenting models,
            // described photo by photo), and any photo described as showing a person is skipped,
            // so a people-free scene - an empty bus stop, a bathroom counter - can be real.
            if (_pictures.UsePexels
                && await _pexels.FetchAsync(term, destDir, saveAsWithoutExt, exclude, ct) is { } stock)
                return stock;
            var drawnScene = await DrawAsync(term, destDir, saveAsWithoutExt, isolated: false, ct);
            if (drawnScene is null)
                _log.LogWarning("Codex could not draw scene '{Term}'; leaving it empty rather than using a photo of real people.", term);
            return drawnScene;
        }

        // Real photos first. Only the EXACT term is searched: Commons also accepts shorter versions
        // of a phrase, and those nearly always match something - "a single blue sock drying on a
        // radiator" shortened to "a single blue" and returned a water lily.
        var found = await ArchiveAsync(term, destDir, saveAsWithoutExt, exclude, ct);
        if (found is not null) return found;

        _log.LogInformation("No real photo of '{Term}'; asking Codex to draw it.", term);
        // An object on white, cut out as a sticker; a real thing as a photograph in its setting.
        var drawn = await DrawAsync(term, destDir, saveAsWithoutExt, isolated: kind != PictureKind.Real, ct);
        if (drawn is null)
            _log.LogWarning("Codex could not draw '{Term}'; leaving the visual empty.", term);
        return drawn;
    }

    /// <summary>
    /// A real photo that names the thing, from the most exact source to the broadest: the Wikipedia
    /// article's own picture, the exact Commons term, Pexels, then Openverse (Pexels' descriptions
    /// matched far more reliably in a 12-term test; Openverse's tags returned a mockup and a
    /// poster). Every source applies the same relevance check, so a photo that only shares a word
    /// never gets through; when none has an accurate one, the caller draws it (Matthew, 2026-09-30).
    /// </summary>
    private async Task<FoundImage?> ArchiveAsync(string term, string destDir, string saveAsWithoutExt,
        IReadOnlyCollection<string>? exclude, CancellationToken ct) =>
        await _commons.FetchFromWikipediaAsync(term, destDir, saveAsWithoutExt, exclude, ct)
        ?? await _commons.FetchAsync(term, destDir, saveAsWithoutExt, exclude, ct, exactTermOnly: true)
        ?? (_pictures.UsePexels ? await _pexels.FetchAsync(term, destDir, saveAsWithoutExt, exclude, ct) : null)
        ?? (_pictures.UseOpenverse ? await _openverse.FetchAsync(term, destDir, saveAsWithoutExt, exclude, ct) : null);

    private async Task<FoundImage?> DrawAsync(string term, string destDir, string saveAsWithoutExt,
        bool isolated, CancellationToken ct)
    {
        var fileName = saveAsWithoutExt + ".png";
        var path = Path.Combine(destDir, fileName);
        if (!await _codex.GenerateStockImageAsync(term, path, ct, isolated)
            || !File.Exists(path) || new FileInfo(path).Length == 0)
            return null;

        // The source title is what stops the SAME picture being used twice in one video. A drawn
        // one is unique by construction, so the term keeps them apart.
        return new FoundImage(fileName, "Illustration generated for this video.", $"generated:{term}");
    }
}
