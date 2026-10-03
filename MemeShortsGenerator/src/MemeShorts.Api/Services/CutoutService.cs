using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MemeShorts.Api.Services;

/// <summary>
/// Turns a photograph shot on a plain background into a CUTOUT - the subject on transparency.
///
/// This is the last measured difference between our list shorts and CobbleCat's. His artwork is
/// cut out: a typewriter, a portrait, a keyboard, sitting on the card's own white with no photo
/// rectangle around it. Ours were rectangular stock photographs, which is what makes a card look
/// like a slideshow instead of a sticker.
///
/// Deliberately NOT a segmentation model. The only case this handles is the one it can handle
/// reliably - a subject on a plain, light, uniform background, which is exactly the kind of
/// picture Commons returns for an object. Anything else is left as a photograph, because a bad
/// cutout (a subject with half a background still attached, or a hole punched through it) looks
/// far worse on a white card than an honest rectangle. Every rule below is a reason to REFUSE.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CutoutService
{
    private readonly ILogger<CutoutService> _log;

    public CutoutService(ILogger<CutoutService> log) => _log = log;

    /// <summary>How far a pixel may stray from the background colour and still count as background.</summary>
    private const int Tolerance = 30;

    /// <summary>The border has to actually BE uniform for "plain background" to mean anything.</summary>
    private const double MaxBorderSpread = 22;

    /// <summary>A dark background cut onto a white card leaves a dark fringe around the subject.</summary>
    private const int MinBorderBrightness = 140;

    private const double MinBackgroundFraction = 0.12;
    private const double MaxBackgroundFraction = 0.88;

    // THE THREE RULES THAT ACTUALLY MATTER, added after the first version produced a genuinely
    // bad cutout and shipped it: a museum photo of a stone on a grey wall came back moth-eaten,
    // half the wall still attached, which is far worse than the plain photograph. Measured over
    // the sample set, these separate that failure from a clean cut cleanly:
    //
    //                       stone (bad)   drawing (good)
    //   tolerance drift        0.18           0.00
    //   ragged edge            0.096          0.015
    //   border coverage        0.95           0.96
    //
    /// <summary>
    /// Flooding at a looser tolerance must not find much MORE background. A truly uniform
    /// backdrop barely changes; a gradient wall grows by tens of percent, and a gradient is
    /// exactly what produces a ragged half-removed mask.
    /// </summary>
    private const double MaxToleranceDrift = 0.06;

    /// <summary>
    /// How much of the picture sits on the boundary between kept and removed. A clean cut is a
    /// thin antialiased outline; a moth-eaten one is boundary almost everywhere.
    /// </summary>
    private const double MaxRaggedEdge = 0.04;

    /// <summary>A subject on a plain background has that background along nearly all of the
    /// picture's own border. 0.17 was the dark cargo-hold interior; 0.95+ is a real backdrop.</summary>
    private const double MinBorderCoverage = 0.85;

    /// <summary>
    /// Write a cutout of <paramref name="imagePath"/> next to it, or return null when this
    /// picture is not one that can be cut out safely.
    /// </summary>
    public string? TryCutout(string imagePath)
    {
        try
        {
            using var src = new Bitmap(imagePath);
            int w = src.Width, h = src.Height;
            if (w < 80 || h < 80) return null;

            var px = ReadArgb(src, out int stride);

            if (!BorderIsPlain(px, w, h, stride, out var bg))
                return null;

            var isBackground = FloodFromBorder(px, w, h, stride, bg, Tolerance);

            var count = 0;
            for (int i = 0; i < isBackground.Length; i++) if (isBackground[i]) count++;
            var fraction = (double)count / (w * h);
            if (fraction < MinBackgroundFraction || fraction > MaxBackgroundFraction)
            {
                _log.LogInformation("No cutout for {File}: background is {Pct:P0} of the picture.",
                    Path.GetFileName(imagePath), fraction);
                return null;
            }

            var coverage = BorderCoverage(isBackground, w, h);
            if (coverage < MinBorderCoverage)
            {
                _log.LogInformation("No cutout for {File}: background covers only {Pct:P0} of the "
                    + "picture's border, so it is not a backdrop.", Path.GetFileName(imagePath), coverage);
                return null;
            }

            var loose = FloodFromBorder(px, w, h, stride, bg, Tolerance + 15);
            var looseCount = 0;
            for (int i = 0; i < loose.Length; i++) if (loose[i]) looseCount++;
            var drift = Math.Abs((double)looseCount / (w * h) - fraction);
            if (drift > MaxToleranceDrift)
            {
                _log.LogInformation("No cutout for {File}: background is a gradient, not a flat "
                    + "colour (loosening the tolerance found {Drift:P0} more of it).",
                    Path.GetFileName(imagePath), drift);
                return null;
            }

            var ragged = RaggedEdge(isBackground, w, h);
            if (ragged > MaxRaggedEdge)
            {
                _log.LogInformation("No cutout for {File}: the cut is moth-eaten ({Pct:P1} of the "
                    + "picture is edge).", Path.GetFileName(imagePath), ragged);
                return null;
            }

            // The subject has to be ONE thing. A mask that leaves a scatter of specks is a
            // picture whose background merely happened to be light in places.
            if (!SubjectIsCoherent(isBackground, w, h, out var biggestShare))
            {
                _log.LogInformation("No cutout for {File}: subject is fragmented "
                    + "(largest piece is {Share:P0} of it).", Path.GetFileName(imagePath), biggestShare);
                return null;
            }

            var outPath = Path.Combine(Path.GetDirectoryName(imagePath)!,
                Path.GetFileNameWithoutExtension(imagePath) + ".cutout.png");
            WriteCutout(px, w, h, stride, isBackground, outPath);
            _log.LogInformation("Cut out {File}: background was {Pct:P0}, subject one piece.",
                Path.GetFileName(imagePath), fraction);
            return Path.GetFileName(outPath);
        }
        catch (Exception ex)
        {
            // Never fail a render over this - the photograph is a perfectly good fallback.
            _log.LogWarning(ex, "Cutout failed for {File}; keeping the photograph.",
                Path.GetFileName(imagePath));
            return null;
        }
    }

    private static byte[] ReadArgb(Bitmap src, out int stride)
    {
        using var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));

        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            stride = data.Stride;
            var buf = new byte[data.Stride * bmp.Height];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            return buf;
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>Is the frame around the picture one flat colour, and a light one?</summary>
    private static bool BorderIsPlain(byte[] px, int w, int h, int stride, out (int B, int G, int R) bg)
    {
        var band = Math.Max(2, Math.Min(w, h) / 60);
        double sb = 0, sg = 0, sr = 0;
        var n = 0;
        var samples = new List<(int B, int G, int R)>();
        for (int y = 0; y < h; y++)
        {
            var edgeRow = y < band || y >= h - band;
            for (int x = 0; x < w; x += edgeRow ? 1 : Math.Max(1, w - band - 1))
            {
                if (!edgeRow && x >= band && x < w - band) continue;
                var i = y * stride + x * 4;
                sb += px[i]; sg += px[i + 1]; sr += px[i + 2];
                if (samples.Count < 20000) samples.Add((px[i], px[i + 1], px[i + 2]));
                n++;
            }
        }
        if (n == 0) { bg = default; return false; }

        bg = ((int)(sb / n), (int)(sg / n), (int)(sr / n));
        if ((bg.B + bg.G + bg.R) / 3 < MinBorderBrightness) return false;

        double var2 = 0;
        foreach (var (b, g, r) in samples)
            var2 += (b - bg.B) * (b - bg.B) + (g - bg.G) * (g - bg.G) + (r - bg.R) * (r - bg.R);
        return Math.Sqrt(var2 / (3.0 * samples.Count)) <= MaxBorderSpread;
    }

    /// <summary>
    /// Background is what the border colour REACHES, not merely what matches it. Flooding inward
    /// is what keeps a white shirt or a bright highlight inside the subject opaque - matching on
    /// colour alone punches holes straight through the middle of the thing.
    /// </summary>
    private static bool[] FloodFromBorder(byte[] px, int w, int h, int stride, (int B, int G, int R) bg, int tolerance)
    {
        var seen = new bool[w * h];
        var queue = new Queue<int>();

        void Seed(int x, int y)
        {
            var k = y * w + x;
            if (seen[k]) return;
            var i = y * stride + x * 4;
            if (Math.Abs(px[i] - bg.B) > tolerance
                || Math.Abs(px[i + 1] - bg.G) > tolerance
                || Math.Abs(px[i + 2] - bg.R) > tolerance) return;
            seen[k] = true;
            queue.Enqueue(k);
        }

        for (int x = 0; x < w; x++) { Seed(x, 0); Seed(x, h - 1); }
        for (int y = 0; y < h; y++) { Seed(0, y); Seed(w - 1, y); }

        while (queue.Count > 0)
        {
            var k = queue.Dequeue();
            int x = k % w, y = k / w;
            if (x > 0) Seed(x - 1, y);
            if (x < w - 1) Seed(x + 1, y);
            if (y > 0) Seed(x, y - 1);
            if (y < h - 1) Seed(x, y + 1);
        }
        return seen;
    }

    /// <summary>How much of the picture's own border was classified as background.</summary>
    private static double BorderCoverage(bool[] isBackground, int w, int h)
    {
        var on = 0;
        var total = 0;
        for (int x = 0; x < w; x++)
        {
            if (isBackground[x]) on++;
            if (isBackground[(h - 1) * w + x]) on++;
            total += 2;
        }
        for (int y = 0; y < h; y++)
        {
            if (isBackground[y * w]) on++;
            if (isBackground[y * w + (w - 1)]) on++;
            total += 2;
        }
        return total == 0 ? 0 : (double)on / total;
    }

    /// <summary>Fraction of pixels sitting on the kept/removed boundary. Thin outline = clean.</summary>
    private static double RaggedEdge(bool[] isBackground, int w, int h)
    {
        var edge = 0;
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                var k = y * w + x;
                var me = isBackground[k];
                if (me != isBackground[k - 1] || me != isBackground[k + 1]
                    || me != isBackground[k - w] || me != isBackground[k + w]) edge++;
            }
        return (double)edge / (w * h);
    }

    /// <summary>Is what is left one object, rather than confetti?</summary>
    private static bool SubjectIsCoherent(bool[] isBackground, int w, int h, out double biggestShare)
    {
        var seen = new bool[w * h];
        var subject = 0;
        for (int i = 0; i < isBackground.Length; i++) if (!isBackground[i]) subject++;
        biggestShare = 0;
        if (subject == 0) return false;

        var best = 0;
        var queue = new Queue<int>();
        for (int start = 0; start < isBackground.Length; start++)
        {
            if (isBackground[start] || seen[start]) continue;
            var size = 0;
            seen[start] = true;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var k = queue.Dequeue();
                size++;
                int x = k % w, y = k / w;
                void Push(int nx, int ny)
                {
                    var j = ny * w + nx;
                    if (seen[j] || isBackground[j]) return;
                    seen[j] = true;
                    queue.Enqueue(j);
                }
                if (x > 0) Push(x - 1, y);
                if (x < w - 1) Push(x + 1, y);
                if (y > 0) Push(x, y - 1);
                if (y < h - 1) Push(x, y + 1);
            }
            if (size > best) best = size;
        }
        biggestShare = (double)best / subject;
        return biggestShare >= 0.80;
    }

    /// <summary>
    /// Write the subject with the background transparent, and SOFTEN the boundary: a hard
    /// one-pixel edge on a photograph reads as a bad cut, which is the thing this is trying to
    /// avoid. The alpha is the fraction of a 3x3 neighbourhood that is subject.
    /// </summary>
    private static void WriteCutout(byte[] px, int w, int h, int stride, bool[] isBackground, string outPath)
    {
        using var outBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = outBmp.LockBits(new Rectangle(0, 0, w, h),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buf = new byte[data.Stride * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var src = y * stride + x * 4;
                    var dst = y * data.Stride + x * 4;

                    var solid = 0;
                    var total = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            total++;
                            if (!isBackground[ny * w + nx]) solid++;
                        }

                    buf[dst] = px[src];
                    buf[dst + 1] = px[src + 1];
                    buf[dst + 2] = px[src + 2];
                    buf[dst + 3] = (byte)(total == 0 ? 0 : 255 * solid / total);
                }
            }
            Marshal.Copy(buf, 0, data.Scan0, buf.Length);
        }
        finally { outBmp.UnlockBits(data); }

        outBmp.Save(outPath, ImageFormat.Png);
    }
}
