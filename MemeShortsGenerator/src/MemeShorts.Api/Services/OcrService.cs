using System.Runtime.Versioning;
using System.Text;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace MemeShorts.Api.Services;

/// <summary>One line of text OCR found, with where it sits in the image.</summary>
public sealed record OcrLineBox(string Text, int Top, int Bottom, int Left, int Right)
{
    public int Height => Bottom - Top;
}

public interface IOcrService
{
    bool Available { get; }

    /// <summary>Read the text baked into an image. Returns "" when nothing legible was found.</summary>
    Task<string> ReadAsync(string imagePath, CancellationToken ct = default);

    /// <summary>
    /// Read the text AND where each line sits. The geometry is the point: inferring caption
    /// lines from pixel gaps breaks on every new layout (tight line spacing, page chrome down
    /// the margins, a photo in the middle), whereas the OCR engine already knows exactly which
    /// rows are text and which are not.
    /// </summary>
    Task<IReadOnlyList<OcrLineBox>> ReadLinesAsync(string imagePath, CancellationToken ct = default);
}

/// <summary>
/// Reads the words off a pasted meme using the OCR engine built into Windows — no extra install
/// and no language data to ship, which is why this beats Tesseract here.
///
/// The result is a STARTING POINT, never the final script: meme fonts and heavy compression trip
/// OCR up, so the UI drops the text into an editable box for correction before anything renders.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsOcrService : IOcrService
{
    private readonly ILogger<WindowsOcrService> _log;
    private readonly OcrEngine? _engine;

    public WindowsOcrService(ILogger<WindowsOcrService> log)
    {
        _log = log;
        try
        {
            // User profile languages first, so a non-English Windows still reads its own text;
            // en-US is the fallback because the posts being reposted are English.
            _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                      ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"));
            if (_engine is null) _log.LogWarning("No Windows OCR engine available on this machine.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Windows OCR unavailable; the post text will have to be typed.");
        }
    }

    public bool Available => _engine is not null;

    public async Task<string> ReadAsync(string imagePath, CancellationToken ct = default)
    {
        if (_engine is null || !File.Exists(imagePath)) return "";

        // StorageFile rather than an in-memory stream: the WinRT buffer/stream interop helpers
        // are the fiddly part of this API, and the image is already on disk anyway.
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(imagePath)).AsTask(ct);
        using var stream = await file.OpenAsync(FileAccessMode.Read).AsTask(ct);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);
        using var bitmap = await decoder.GetSoftwareBitmapAsync().AsTask(ct);

        var result = await _engine.RecognizeAsync(bitmap).AsTask(ct);

        // One OCR line per output line. That keeps a meme's own top/bottom split intact — the
        // composer only re-wraps a line that is too wide for the card, and single newlines are
        // hard breaks rather than paragraph gaps, so nothing gets invented here.
        var sb = new StringBuilder();
        foreach (var line in result.Lines)
        {
            var text = line.Text.Trim();
            if (text.Length > 0) sb.AppendLine(text);
        }

        var read = sb.ToString().Trim();
        _log.LogInformation("OCR read {Lines} line(s), {Chars} chars from {File}.",
            result.Lines.Count, read.Length, Path.GetFileName(imagePath));
        return read;
    }

    public async Task<IReadOnlyList<OcrLineBox>> ReadLinesAsync(string imagePath, CancellationToken ct = default)
    {
        if (_engine is null || !File.Exists(imagePath)) return Array.Empty<OcrLineBox>();

        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(imagePath)).AsTask(ct);
        using var stream = await file.OpenAsync(FileAccessMode.Read).AsTask(ct);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);
        using var bitmap = await decoder.GetSoftwareBitmapAsync().AsTask(ct);
        var result = await _engine.RecognizeAsync(bitmap).AsTask(ct);

        var lines = new List<OcrLineBox>();
        foreach (var line in result.Lines)
        {
            var text = line.Text.Trim();
            if (text.Length == 0 || line.Words.Count == 0) continue;

            // The line has no rect of its own — union its words'.
            double top = double.MaxValue, bottom = 0, left = double.MaxValue, right = 0;
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                top = Math.Min(top, r.Y);
                bottom = Math.Max(bottom, r.Y + r.Height);
                left = Math.Min(left, r.X);
                right = Math.Max(right, r.X + r.Width);
            }
            lines.Add(new OcrLineBox(text, (int)Math.Floor(top), (int)Math.Ceiling(bottom),
                (int)Math.Floor(left), (int)Math.Ceiling(right)));
        }

        lines.Sort((a, b) => a.Top.CompareTo(b.Top));
        _log.LogInformation("OCR located {N} line(s) in {File}.", lines.Count, Path.GetFileName(imagePath));
        return lines;
    }
}

/// <summary>Used when the OCR engine could not be created — the text box simply stays empty.</summary>
public sealed class NullOcrService : IOcrService
{
    public bool Available => false;
    public Task<string> ReadAsync(string imagePath, CancellationToken ct = default) => Task.FromResult("");
    public Task<IReadOnlyList<OcrLineBox>> ReadLinesAsync(string imagePath, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OcrLineBox>>(Array.Empty<OcrLineBox>());
}
