using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using MemeShorts.Api.Models;
using Microsoft.Extensions.Options;

namespace MemeShorts.Api.Services;

/// <summary>
/// Blurs the swear words out of a pasted screenshot before it is filmed.
///
/// The censored copy is written NEXT TO the original at the same size, and only that copy is
/// handed to the renderer. Same width and height is the whole trick: every slice, column crop
/// and reveal boundary was measured on the original, so covering a word changes what is on
/// screen without moving a single band.
///
/// The blur is a squeeze-and-stretch - the region is resampled down to a few pixels tall and
/// drawn back at full size. That is what actually destroys the glyphs; a small-radius blur on
/// large text stays readable, and a hard black box would look like a redaction rather than the
/// blur that was asked for. The vertical cap means a 120px headline is smeared as thoroughly as
/// a 20px comment.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ImageCensorService
{
    private readonly ProfanityFilter _filter;
    private readonly ProfanityOptions _opt;
    private readonly ILogger<ImageCensorService> _log;

    /// <summary>Passes of squeeze-and-stretch. Two smooths out the first pass's blockiness.</summary>
    private const int Passes = 2;

    /// <summary>Never leave more than this many rows of detail in a covered word.</summary>
    private const int MaxRows = 6;

    public ImageCensorService(ProfanityFilter filter, IOptions<ProfanityOptions> opt,
        ILogger<ImageCensorService> log)
    {
        _filter = filter; _opt = opt.Value; _log = log;
    }

    /// <summary>
    /// Write a copy of <paramref name="imagePath"/> with every swear word blurred out.
    ///
    /// Returns the censored copy's FILE NAME (it sits in the same folder, which is the render's
    /// public dir), or null when the picture was clean and the original can be used as it is.
    /// </summary>
    public string? Censor(string imagePath, IReadOnlyList<OcrLineBox> ocrLines)
    {
        if (!_opt.Enabled || !File.Exists(imagePath)) return null;

        var hits = _filter.Scan(ocrLines);
        if (hits.Count == 0) return null;

        var dir = Path.GetDirectoryName(imagePath)!;
        var name = Path.GetFileNameWithoutExtension(imagePath) + ".censored.png";
        var outPath = Path.Combine(dir, name);

        try
        {
            // Copied into a fresh 32bpp surface rather than edited in place: the source may be
            // a palettised or CMYK JPEG, and Graphics.FromImage refuses those outright.
            using var src = new Bitmap(imagePath);
            using var work = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(work))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));
            }

            var regions = Merge(hits, src.Width, src.Height);
            foreach (var region in regions) Blur(work, region);

            work.Save(outPath, ImageFormat.Png);

            _log.LogInformation("Blurred {Hits} swear word(s) in {N} patch(es) of {File}: {Words}",
                hits.Count, regions.Count, Path.GetFileName(imagePath),
                string.Join(", ", hits.Select(h => h.Word).Distinct().Take(8)));
            return name;
        }
        catch (Exception ex)
        {
            // A picture that cannot be censored must not take the whole render down with it -
            // but it must not be filmed uncensored either, so the job is failed on purpose.
            _log.LogError(ex, "Could not blur {File}.", Path.GetFileName(imagePath));
            throw new InvalidOperationException(
                "Found profanity in the picture but could not blur it out, so no video was made: "
                + ex.Message, ex);
        }
    }

    /// <summary>
    /// Pad each hit, then fuse the ones that touch.
    ///
    /// Two swear words side by side blurred separately leave a sharp strip of readable pixels
    /// between them; one patch over both reads as a single smear, which is what a censored post
    /// is supposed to look like.
    /// </summary>
    private static List<Rectangle> Merge(IReadOnlyList<ProfanityHit> hits, int width, int height)
    {
        var rects = hits.Select(h => Pad(h, width, height)).Where(r => r is { Width: > 0, Height: > 0 }).ToList();

        for (bool merged = true; merged;)
        {
            merged = false;
            for (int i = 0; i < rects.Count && !merged; i++)
                for (int j = i + 1; j < rects.Count; j++)
                {
                    var probe = Rectangle.Inflate(rects[i], 4, 4);
                    if (probe.IntersectsWith(rects[j]))
                    {
                        rects[i] = Rectangle.Union(rects[i], rects[j]);
                        rects.RemoveAt(j);
                        merged = true;
                        break;
                    }
                }
        }

        return rects;
    }

    /// <summary>
    /// Grow the word's box a little. OCR boxes hug the glyphs, and a blur that stops exactly at
    /// the ink leaves a legible outline of the word's shape behind.
    /// </summary>
    private static Rectangle Pad(ProfanityHit hit, int width, int height)
    {
        int padX = Math.Max(4, hit.Height / 3);
        int padY = Math.Max(3, hit.Height / 5);

        int left = Math.Clamp(hit.Left - padX, 0, Math.Max(0, width - 1));
        int top = Math.Clamp(hit.Top - padY, 0, Math.Max(0, height - 1));
        int right = Math.Clamp(hit.Right + padX, left, width);
        int bottom = Math.Clamp(hit.Bottom + padY, top, height);

        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private void Blur(Bitmap bmp, Rectangle region)
    {
        int strength = Math.Clamp(_opt.Strength, 2, 40);

        for (int pass = 0; pass < Passes; pass++)
        {
            int rows = Math.Clamp(region.Height / strength, 1, MaxRows);
            int cols = Math.Max(1, (int)Math.Round(region.Width * (double)rows / Math.Max(1, region.Height)));

            using var small = new Bitmap(cols, rows, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(bmp, new Rectangle(0, 0, cols, rows), region, GraphicsUnit.Pixel);
            }

            using var g2 = Graphics.FromImage(bmp);
            g2.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g2.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g2.CompositingMode = CompositingMode.SourceCopy;
            // TileFlipXY, or bilinear sampling past the edge of the shrunken copy draws a
            // half-transparent border around every patch.
            using var attrs = new ImageAttributes();
            attrs.SetWrapMode(WrapMode.TileFlipXY);
            g2.DrawImage(small, region, 0, 0, cols, rows, GraphicsUnit.Pixel, attrs);
        }
    }
}
