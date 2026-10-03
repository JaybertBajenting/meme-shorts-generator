using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using System.Text;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace MemeShorts.Api.Services;

/// <summary>One word OCR found, with where it sits in the image.</summary>
public sealed record OcrWordBox(string Text, int Top, int Bottom, int Left, int Right);

/// <summary>One line of text OCR found, with where it sits in the image.</summary>
public sealed record OcrLineBox(string Text, int Top, int Bottom, int Left, int Right)
{
    public int Height => Bottom - Top;

    /// <summary>
    /// The line's individual words, each with its own box. Slicing only needs the line, but
    /// blurring a swear word needs to know where THAT word sits - covering the whole line would
    /// censor the joke around it.
    /// </summary>
    public IReadOnlyList<OcrWordBox> Words { get; init; } = Array.Empty<OcrWordBox>();
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
            var words = new List<OcrWordBox>(line.Words.Count);
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                top = Math.Min(top, r.Y);
                bottom = Math.Max(bottom, r.Y + r.Height);
                left = Math.Min(left, r.X);
                right = Math.Max(right, r.X + r.Width);
                words.Add(new OcrWordBox(word.Text,
                    (int)Math.Floor(r.Y), (int)Math.Ceiling(r.Y + r.Height),
                    (int)Math.Floor(r.X), (int)Math.Ceiling(r.X + r.Width)));
            }
            lines.Add(new OcrLineBox(text, (int)Math.Floor(top), (int)Math.Ceiling(bottom),
                (int)Math.Floor(left), (int)Math.Ceiling(right)) { Words = words });
        }

        lines.Sort((a, b) => a.Top.CompareTo(b.Top));

        var improved = await RetryHardLinesAsync(imagePath, lines, ct);
        _log.LogInformation("OCR located {N} line(s) in {File}{Fixed}.",
            lines.Count, Path.GetFileName(imagePath),
            improved > 0 ? $", re-read {improved} at higher resolution" : "");
        return lines;
    }

    /// <summary>
    /// Read the hard lines again, larger.
    ///
    /// Windows OCR is reliable on body text and unreliable on the heavy outlined display faces
    /// memes are captioned in: "GO WORK YOUR CRINGE 9 TO 5" came back as "GO WORKPUR CRINGE 5",
    /// and a Winnie-the-Pooh panel as "P.EO!LEWHO$ET PAYEDJOIWORICOUT". The voice then says the
    /// misread words while the card shows the real ones — which reads as the narration being out
    /// of sync when it is really the reading that is wrong.
    ///
    /// The same engine does much better on the same glyphs at 3x, so the suspect lines get a
    /// second pass on an upscaled crop. Only lines that are LIKELY wrong are retried — garbled
    /// ones, and unusually large ones, which is where display faces live — and a new reading only
    /// wins if it scores better as English. Everything else is left exactly as it was.
    /// </summary>
    private async Task<int> RetryHardLinesAsync(
        string imagePath, List<OcrLineBox> lines, CancellationToken ct)
    {
        if (_engine is null || lines.Count == 0) return 0;

        var heights = lines.Select(l => l.Height).OrderBy(h => h).ToArray();
        var typical = Math.Max(1, heights[heights.Length / 2]);

        var suspects = Enumerable.Range(0, lines.Count)
            .Where(i => Garbled(lines[i].Text) || lines[i].Height > typical * 1.6)
            .ToList();
        if (suspects.Count == 0) return 0;

        var tmp = Directory.CreateTempSubdirectory("ocr_retry_");
        var fixedCount = 0;
        try
        {
            using var full = new Bitmap(imagePath);
            foreach (var i in suspects)
            {
                var line = lines[i];
                var better = await ReadCropAsync(full, line, tmp.FullName, i, ct);
                if (better is null) continue;
                if (Score(better) > Score(line.Text) + 0.5)
                {
                    lines[i] = line with { Text = better };
                    fixedCount++;
                    _log.LogInformation("Re-read a line at 3x: \"{Old}\" -> \"{New}\".",
                        line.Text, better);
                }
            }
        }
        catch (Exception ex)
        {
            // A failed retry must never cost the original reading.
            _log.LogWarning(ex, "High-resolution OCR retry failed for {File}.",
                Path.GetFileName(imagePath));
        }
        finally { try { tmp.Delete(true); } catch { } }
        return fixedCount;
    }

    /// <summary>OCR one line's box again, upscaled 3x with its contrast pushed.</summary>
    private async Task<string?> ReadCropAsync(
        Bitmap full, OcrLineBox line, string tmpDir, int index, CancellationToken ct)
    {
        const int scale = 3;
        var pad = Math.Max(4, line.Height / 4);
        var x = Math.Clamp(line.Left - pad, 0, Math.Max(0, full.Width - 1));
        var y = Math.Clamp(line.Top - pad, 0, Math.Max(0, full.Height - 1));
        var w = Math.Min(full.Width - x, line.Right - line.Left + pad * 2);
        var h = Math.Min(full.Height - y, line.Bottom - line.Top + pad * 2);
        if (w < 8 || h < 8) return null;

        var path = Path.Combine(tmpDir, $"line_{index}.png");
        using (var crop = new Bitmap(w * scale, h * scale, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(crop))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                // Contrast pushed a little: the outlined meme faces sit on busy artwork, and the
                // extra separation between fill and stroke is most of what the engine gains.
                using var attrs = new ImageAttributes();
                attrs.SetColorMatrix(new ColorMatrix(new[]
                {
                    new[] { 1.35f, 0f, 0f, 0f, 0f },
                    new[] { 0f, 1.35f, 0f, 0f, 0f },
                    new[] { 0f, 0f, 1.35f, 0f, 0f },
                    new[] { 0f, 0f, 0f, 1f, 0f },
                    new[] { -0.15f, -0.15f, -0.15f, 0f, 1f },
                }));
                g.DrawImage(full, new Rectangle(0, 0, w * scale, h * scale),
                    x, y, w, h, GraphicsUnit.Pixel, attrs);
            }
            crop.Save(path, ImageFormat.Png);
        }

        var file = await StorageFile.GetFileFromPathAsync(path).AsTask(ct);
        using var stream = await file.OpenAsync(FileAccessMode.Read).AsTask(ct);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);
        using var bmp = await decoder.GetSoftwareBitmapAsync().AsTask(ct);
        var result = await _engine!.RecognizeAsync(bmp).AsTask(ct);

        var text = string.Join(" ", result.Lines.Select(l => l.Text.Trim()).Where(t => t.Length > 0));
        return text.Length == 0 ? null : text;
    }

    /// <summary>Does this reading look like mangled OCR rather than words?</summary>
    private static bool Garbled(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length < 4) return false;
        var letters = t.Count(char.IsLetter);
        if (letters == 0) return true;
        // Symbols wedged INSIDE words are the signature: "P.EO!LEWHO$ET".
        var oddInside = t.Count(c => "!@#$%^&*_=+|~<>".Contains(c));
        return oddInside >= 2 || (double)letters / t.Length < 0.6;
    }

    /// <summary>
    /// How much this reading looks like English. Word-like tokens count most: a misread tends to
    /// weld words together ("WORKPUR" for "WORK YOUR"), so the truer reading usually has more of
    /// them.
    /// </summary>
    private static double Score(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return 0;
        var tokens = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wordLike = tokens.Count(tok =>
        {
            var w = new string(tok.Where(char.IsLetter).ToArray());
            return w.Length >= 2 && w.Any(c => "aeiouAEIOU".Contains(c));
        });
        var letterRatio = (double)t.Count(char.IsLetter) / t.Length;
        var oddInside = t.Count(c => "!@#$%^&*_=+|~<>".Contains(c));
        return wordLike + letterRatio * 2 - oddInside;
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
