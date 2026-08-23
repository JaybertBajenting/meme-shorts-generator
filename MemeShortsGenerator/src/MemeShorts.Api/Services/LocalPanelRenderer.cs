using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using MemeShorts.Api.Models;

namespace MemeShorts.Api.Services;

/// <summary>
/// Offline fallback panel renderer (System.Drawing). Draws a crude white card
/// with a placeholder rage face so the pipeline runs without Codex. Codex output
/// is far better — this just guarantees an end-to-end demo. Caption text is NOT
/// drawn here; Remotion overlays it from the captions track.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LocalPanelRenderer : IPanelRenderer
{
    public Task GenerateAsync(Beat beat, int width, int height, string outputPngPath, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPngPath)!);

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.White);

        using var ink = new Pen(Color.Black, Math.Max(4, width / 180f));
        // Reserve top ~22% for the caption overlay; draw the face below it.
        int top = (int)(height * 0.28);
        int faceSize = (int)(Math.Min(width, height) * 0.42);
        int cx = width / 2;
        int cy = top + faceSize / 2;
        var faceRect = new Rectangle(cx - faceSize / 2, cy - faceSize / 2, faceSize, faceSize);

        DrawFace(g, ink, faceRect, beat.RageFace);

        if (beat.Options is { Count: 2 })
            DrawOptions(g, ink, width, height);

        bmp.Save(outputPngPath, ImageFormat.Png);
        return Task.CompletedTask;
    }

    private static void DrawFace(Graphics g, Pen ink, Rectangle r, string? face)
    {
        g.DrawEllipse(ink, r);
        int eyeY = r.Top + r.Height / 3;
        int eyeR = r.Width / 12;
        int lx = r.Left + r.Width / 3;
        int rx = r.Right - r.Width / 3;
        using var fill = new SolidBrush(Color.Black);

        switch ((face ?? "").ToLowerInvariant())
        {
            case "scream":
            case "fffuuu":
                // wide eyes, big open mouth
                g.DrawEllipse(ink, lx - eyeR, eyeY - eyeR, eyeR * 2, eyeR * 2);
                g.DrawEllipse(ink, rx - eyeR, eyeY - eyeR, eyeR * 2, eyeR * 2);
                g.FillEllipse(fill, r.Left + r.Width / 4, r.Bottom - r.Height / 3, r.Width / 2, r.Height / 4);
                break;
            case "mother_of_god":
            case "are_you_kidding_me":
                g.FillEllipse(fill, lx - eyeR, eyeY - eyeR, eyeR * 2, eyeR * 2);
                g.FillEllipse(fill, rx - eyeR, eyeY - eyeR, eyeR * 2, eyeR * 2);
                g.DrawArc(ink, r.Left + r.Width / 4, r.Bottom - r.Height / 2, r.Width / 2, r.Height / 3, 20, 140); // flat-ish frown
                break;
            case "happy":
                g.FillEllipse(fill, lx - eyeR, eyeY - eyeR, eyeR * 2, eyeR * 2);
                g.FillEllipse(fill, rx - eyeR, eyeY - eyeR, eyeR * 2, eyeR * 2);
                g.DrawArc(ink, r.Left + r.Width / 4, eyeY, r.Width / 2, r.Height / 3, 20, 140);  // smile
                break;
            case "cry":
                g.DrawArc(ink, lx - eyeR, eyeY - eyeR, eyeR * 2, eyeR * 2, 180, 180);
                g.DrawArc(ink, rx - eyeR, eyeY - eyeR, eyeR * 2, eyeR * 2, 180, 180);
                g.DrawArc(ink, r.Left + r.Width / 4, r.Bottom - r.Height / 3, r.Width / 2, r.Height / 3, 200, 140);
                break;
            default: // poker / determined / yao_ming
                g.FillEllipse(fill, lx - eyeR, eyeY - eyeR, eyeR * 2, eyeR * 2);
                g.FillEllipse(fill, rx - eyeR, eyeY - eyeR, eyeR * 2, eyeR * 2);
                g.DrawLine(ink, r.Left + r.Width / 4, r.Bottom - r.Height / 3, r.Right - r.Width / 4, r.Bottom - r.Height / 3);
                break;
        }
    }

    private static void DrawOptions(Graphics g, Pen ink, int width, int height)
    {
        using var font = new Font("Arial", height / 22f, FontStyle.Bold);
        using var brush = new SolidBrush(Color.Black);
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        int r = height / 16;
        DrawCircleNum(g, ink, brush, font, fmt, width / 4, (int)(height * 0.85), r, "1");
        DrawCircleNum(g, ink, brush, font, fmt, 3 * width / 4, (int)(height * 0.85), r, "2");
    }

    private static void DrawCircleNum(Graphics g, Pen ink, Brush brush, Font font, StringFormat fmt, int cx, int cy, int r, string n)
    {
        g.DrawEllipse(ink, cx - r, cy - r, r * 2, r * 2);
        g.DrawString(n, font, brush, new Rectangle(cx - r, cy - r, r * 2, r * 2), fmt);
    }
}
