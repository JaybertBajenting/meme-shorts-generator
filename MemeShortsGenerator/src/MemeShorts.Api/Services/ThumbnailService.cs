using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.Versioning;

namespace MemeShorts.Api.Services;

public interface IThumbnailService
{
    /// <summary>
    /// Compose a 1280x720 thumbnail: the gameplay background (or solid dark if none) + the panel
    /// image + the hook text. Returns the path, or null.
    /// </summary>
    string? Generate(string panelImagePath, string hookText, string outputJpgPath, string? backgroundImagePath = null);
}

/// <summary>
/// Builds a YouTube thumbnail (1280x720) by compositing a meme/data panel over the
/// gameplay background with solid accent shapes and a large outlined hook caption.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ThumbnailService : IThumbnailService
{
    private static readonly Color[] AccentPalette =
    {
        Color.FromArgb(255, 245, 67, 54),    // red
        Color.FromArgb(255, 255, 202, 40),   // yellow
        Color.FromArgb(255, 0, 200, 83),     // green
        Color.FromArgb(255, 41, 121, 255),   // blue
        Color.FromArgb(255, 171, 71, 188),   // purple
        Color.FromArgb(255, 255, 112, 67),   // orange
        Color.FromArgb(255, 0, 188, 212),    // cyan
    };

    private static readonly Color[] TextFillPalette =
    {
        Color.White,
        Color.FromArgb(255, 255, 238, 88),
        Color.FromArgb(255, 255, 255, 141),
    };

    public string? Generate(string panelImagePath, string hookText, string outputJpgPath, string? backgroundImagePath = null)
    {
        const int W = 1280, H = 720;
        Directory.CreateDirectory(Path.GetDirectoryName(outputJpgPath)!);

        var text = (hookText ?? "").Trim().ToUpperInvariant();
        var seed = StableHash($"{text}|{Path.GetDirectoryName(outputJpgPath)}");
        var variant = (int)(seed % 5);
        var accent = AccentPalette[(int)((seed / 5) % AccentPalette.Length)];
        var textFill = TextFillPalette[(int)((seed / 17) % TextFillPalette.Length)];

        using var bmp = new Bitmap(W, H, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        DrawBackground(g, W, H, backgroundImagePath);
        DrawAccentShapes(g, W, H, variant, accent);

        var (cardBox, textBox, rotate, centerText) = LayoutFor(variant, W, H);
        DrawPanelCard(g, panelImagePath, cardBox, accent, rotate);

        if (text.Length > 0)
        {
            DrawTextPlate(g, textBox, accent, centerText);
            DrawFittedOutlinedText(g, text, textBox, textFill, centerText);
        }

        bmp.Save(outputJpgPath, ImageFormat.Jpeg);
        return outputJpgPath;
    }

    private static void DrawBackground(Graphics g, int width, int height, string? backgroundImagePath)
    {
        if (!string.IsNullOrWhiteSpace(backgroundImagePath) && File.Exists(backgroundImagePath))
        {
            try
            {
                using var bgimg = Image.FromFile(backgroundImagePath);
                float scale = Math.Max((float)width / bgimg.Width, (float)height / bgimg.Height);
                float bw = bgimg.Width * scale, bh = bgimg.Height * scale;
                g.DrawImage(bgimg, (width - bw) / 2f, (height - bh) / 2f, bw, bh);
            }
            catch
            {
                FillFallbackBackground(g, width, height);
            }
        }
        else
        {
            FillFallbackBackground(g, width, height);
        }

        // Dark solid scrim keeps both the card and outlined text readable over bright gameplay.
        using var scrim = new SolidBrush(Color.FromArgb(82, 0, 0, 0));
        g.FillRectangle(scrim, 0, 0, width, height);
    }

    private static void FillFallbackBackground(Graphics g, int width, int height)
    {
        using var solid = new SolidBrush(Color.FromArgb(255, 0x0f, 0x17, 0x2a));
        g.FillRectangle(solid, 0, 0, width, height);
    }

    private static (RectangleF Card, RectangleF Text, float Rotate, bool CenterText) LayoutFor(int variant, int width, int height) =>
        variant switch
        {
            0 => (new RectangleF(690, 64, 535, 535), new RectangleF(48, 86, 590, height - 172), 4f, false),
            1 => (new RectangleF(330, 40, 620, 500), new RectangleF(80, 545, width - 160, 140), -3f, true),
            2 => (new RectangleF(350, 198, 580, 480), new RectangleF(70, 36, width - 140, 150), 2f, true),
            3 => (new RectangleF(58, 78, 585, 552), new RectangleF(690, 84, 540, height - 168), -4f, false),
            _ => (new RectangleF(632, 46, 590, 590), new RectangleF(52, 48, 560, 300), 3f, false),
        };

    private static void DrawAccentShapes(Graphics g, int width, int height, int variant, Color accent)
    {
        using var strong = new SolidBrush(Color.FromArgb(230, accent));
        using var soft = new SolidBrush(Color.FromArgb(110, accent));
        using var dark = new SolidBrush(Color.FromArgb(122, 0, 0, 0));

        switch (variant)
        {
            case 0:
                g.FillRectangle(strong, width - 660, 38, 580, height - 76);
                g.FillEllipse(soft, width - 760, -90, 360, 360);
                break;
            case 1:
                g.FillRectangle(strong, 0, height - 205, width, 205);
                g.FillRectangle(dark, 0, height - 185, width, 185);
                break;
            case 2:
                g.FillRectangle(strong, 0, 0, width, 196);
                g.FillEllipse(soft, -140, 125, 420, 420);
                break;
            case 3:
                g.FillRectangle(strong, 36, 42, 630, height - 84);
                g.FillEllipse(soft, width - 285, height - 270, 360, 360);
                break;
            default:
                g.FillRectangle(strong, 24, 28, 610, 340);
                g.FillRectangle(soft, width - 675, 0, 675, height);
                break;
        }
    }

    private static void DrawPanelCard(Graphics g, string panelImagePath, RectangleF box, Color accent, float rotation)
    {
        if (!File.Exists(panelImagePath)) return;

        try
        {
            using var panel = Image.FromFile(panelImagePath);
            var state = g.Save();
            g.TranslateTransform(box.Left + box.Width / 2f, box.Top + box.Height / 2f);
            g.RotateTransform(rotation);

            var local = new RectangleF(-box.Width / 2f, -box.Height / 2f, box.Width, box.Height);
            using (var shadow = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                g.FillRectangle(shadow, local.Left + 16, local.Top + 20, local.Width, local.Height);
            using (var backing = new SolidBrush(accent))
                g.FillRectangle(backing, local.Left - 16, local.Top - 16, local.Width + 32, local.Height + 32);
            using (var white = new SolidBrush(Color.White))
                g.FillRectangle(white, local);
            using (var border = new Pen(Color.Black, 8f))
                g.DrawRectangle(border, local.Left, local.Top, local.Width, local.Height);

            var imageBox = Inset(local, 22, 22);
            float scale = Math.Min(imageBox.Width / panel.Width, imageBox.Height / panel.Height);
            float dw = panel.Width * scale, dh = panel.Height * scale;
            g.DrawImage(panel, imageBox.Left + (imageBox.Width - dw) / 2f, imageBox.Top + (imageBox.Height - dh) / 2f, dw, dh);

            g.Restore(state);
        }
        catch
        {
            // Bad image input should not fail the whole job; the hook text still renders.
        }
    }

    private static void DrawTextPlate(Graphics g, RectangleF box, Color accent, bool centerText)
    {
        var plate = Inset(box, -10, -8);
        using var black = new SolidBrush(Color.FromArgb(centerText ? 135 : 100, 0, 0, 0));
        using var color = new SolidBrush(Color.FromArgb(centerText ? 90 : 120, accent));
        g.FillRectangle(black, plate);

        if (centerText)
            g.FillRectangle(color, plate.Left, plate.Top, plate.Width, 12);
        else
            g.FillRectangle(color, plate.Left, plate.Top, 18, plate.Height);
    }

    private static void DrawFittedOutlinedText(Graphics g, string text, RectangleF box, Color fill, bool center)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string wrapped = words.Length <= 2 ? string.Join("\n", words)
            : WrapEvenly(words, words.Length <= 4 ? 2 : 3);

        FontFamily family;
        var disposeFamily = false;
        try
        {
            family = new FontFamily("Arial Black");
            disposeFamily = true;
        }
        catch
        {
            family = FontFamily.GenericSansSerif;
        }

        try
        {
            using var path = new GraphicsPath();
            using var fmt = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near };
            path.AddString(wrapped, family, (int)FontStyle.Bold, 100f, new PointF(0, 0), fmt);

            var bounds = path.GetBounds();
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            float scale = Math.Min(box.Width / bounds.Width, box.Height / bounds.Height);
            scale = Math.Min(scale, 3.45f);

            float x = center
                ? box.Left + (box.Width - bounds.Width * scale) / 2f - bounds.Left * scale
                : box.Left - bounds.Left * scale;
            float y = box.Top + (box.Height - bounds.Height * scale) / 2f - bounds.Top * scale;

            var m = new Matrix(scale, 0, 0, scale, x, y);
            path.Transform(m);

            using var shadow = new Pen(Color.FromArgb(180, 0, 0, 0), 18f) { LineJoin = LineJoin.Round };
            using var outline = new Pen(Color.Black, 10f) { LineJoin = LineJoin.Round };
            using var fillBrush = new SolidBrush(fill);
            g.DrawPath(shadow, path);
            g.DrawPath(outline, path);
            g.FillPath(fillBrush, path);
        }
        finally
        {
            if (disposeFamily) family.Dispose();
        }
    }

    private static RectangleF Inset(RectangleF rect, float dx, float dy) =>
        new(rect.Left + dx, rect.Top + dy, rect.Width - dx * 2f, rect.Height - dy * 2f);

    private static string WrapEvenly(string[] words, int lines)
    {
        var result = new List<string>();
        int per = (int)Math.Ceiling(words.Length / (double)lines);
        for (int i = 0; i < words.Length; i += per)
            result.Add(string.Join(" ", words.Skip(i).Take(per)));
        return string.Join("\n", result);
    }

    private static uint StableHash(string value)
    {
        unchecked
        {
            const uint offset = 2166136261;
            const uint prime = 16777619;
            uint hash = offset;
            foreach (var ch in value)
            {
                hash ^= ch;
                hash *= prime;
            }
            return hash;
        }
    }
}
