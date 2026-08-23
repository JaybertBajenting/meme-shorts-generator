using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MemeShorts.Api.Models;

namespace MemeShorts.Api.Services;

/// <summary>
/// Handles the one layout the band slicer cannot express: a FLOW DIAGRAM, where arrows — not
/// position — say what order to read in.
///
/// The trees diagram runs Tree → Cut tree → Paper making, then DOWN and back LEFT to Papers →
/// "Save Trees". Read as rows or columns it comes out backwards, because the bottom row flows
/// right to left. So the arrows are found and followed instead.
///
/// Deliberately narrow: it only claims an image when several coloured arrows are found. Any
/// other picture falls through to the normal band slicing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DiagramSliceService
{
    private readonly ILogger<DiagramSliceService> _log;

    public DiagramSliceService(ILogger<DiagramSliceService> log) => _log = log;

    private sealed record Arrow(Rectangle Box, Point Tail, Point Head);

    private sealed record Panel(Rectangle Box, string Text, int Order);

    /// <summary>
    /// Returns null when this is not a flow diagram — the caller then slices normally.
    ///
    /// <paramref name="preferredOrder"/> is the typed post text, when there is any. Following
    /// arrows automatically needs each panel's exact extent, and on a real diagram those
    /// extents overlap the arrows themselves, so the deduced order is unreliable. Typing the
    /// labels in the order you want them read settles it exactly.
    /// </summary>
    public PostBands? TryAnalyze(string imagePath, IReadOnlyList<OcrLineBox> ocrLines, string background,
        IReadOnlyList<string>? preferredOrder = null)
    {
        if (ocrLines.Count < 3) return null;

        using var bmp = new Bitmap(imagePath);
        int w = bmp.Width, h = bmp.Height;

        var arrows = FindArrows(bmp);
        if (arrows.Count < 2)
            return null;

        // Labels: OCR lines merged into captions when they sit directly under one another.
        var labels = MergeLabels(ocrLines);
        if (labels.Count < 3) return null;

        // Each label owns the picture nearest to it. Rather than segmenting shapes, every
        // non-background pixel is simply given to the closest label, and the panel is the box
        // around everything that label owns — which is enough to crop by.
        var panels = BuildPanels(bmp, labels, background);
        if (panels.Count < 3) return null;

        // Diagram mode is only ever entered DELIBERATELY: the typed lines have to name the
        // panels, in order. Arrows alone are not enough evidence — red bodywork in a photo of
        // two trucks read as arrows, and an ordinary meme was rendered as a flow chart, mostly
        // blank and read out with a pause after every fragment. A misfire here wrecks the whole
        // video, while the cost of requiring the order is one line typed into the box.
        var ordered = OrderByTypedText(panels, preferredOrder);
        if (ordered is null)
        {
            _log.LogInformation(
                "{File} has {N} arrow-like mark(s) but the typed lines do not name its panels; "
                + "slicing it as an ordinary post.", Path.GetFileName(imagePath), arrows.Count);
            return null;
        }

        _log.LogInformation(
            "Diagram in {File}: {Arrows} arrow(s), {Panels} panel(s). Order: {Order}",
            Path.GetFileName(imagePath), arrows.Count, ordered.Count,
            string.Join(" -> ", ordered.Select(p => p.Text)));

        var blocks = ordered.Select(p => new PostBlock(p.Box.Top, p.Box.Bottom, p.Text, false)
        {
            XLeft = p.Box.Left,
            XRight = p.Box.Right,
            Segments = new[] { new PostSegment(p.Text, PostSide.Full) },
        }).ToList();

        return new PostBands(w, h, blocks, 0, background) { IsDiagram = true };
    }

    // ---- arrows ---------------------------------------------------------------------------

    /// <summary>Saturated, strongly-coloured blobs that are small and elongated — connectors.</summary>
    private static List<Arrow> FindArrows(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bytes = new byte[data.Stride * h];
        try { Marshal.Copy(data.Scan0, bytes, 0, bytes.Length); }
        finally { bmp.UnlockBits(data); }

        bool Vivid(int x, int y)
        {
            int i = y * data.Stride + x * 4;
            int b = bytes[i], g = bytes[i + 1], r = bytes[i + 2];
            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            // Strong hue, well away from grey — arrows are drawn to stand out from the artwork.
            return max > 110 && max - min > 80 && r > g + 50 && r > b + 50;
        }

        const int step = 2;
        var seen = new bool[w / step + 1, h / step + 1];
        var arrows = new List<Arrow>();

        for (int sx = 0; sx < w / step; sx++)
        for (int sy = 0; sy < h / step; sy++)
        {
            if (seen[sx, sy] || !Vivid(sx * step, sy * step)) continue;

            var pts = new List<Point>();
            var queue = new Queue<(int, int)>();
            queue.Enqueue((sx, sy));
            seen[sx, sy] = true;
            while (queue.Count > 0)
            {
                var (cx, cy) = queue.Dequeue();
                pts.Add(new Point(cx * step, cy * step));
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= w / step || ny >= h / step) continue;
                    if (seen[nx, ny] || !Vivid(nx * step, ny * step)) continue;
                    seen[nx, ny] = true;
                    queue.Enqueue((nx, ny));
                }
            }

            if (pts.Count < 20) continue;
            int x0 = pts.Min(p => p.X), x1 = pts.Max(p => p.X);
            int y0 = pts.Min(p => p.Y), y1 = pts.Max(p => p.Y);
            var box = Rectangle.FromLTRB(x0, y0, x1, y1);

            // An arrow is SMALL, ELONGATED, and floats in empty space between panels. Without
            // all three, a red car in a photo was being read as a pile of arrows and an
            // ordinary meme got treated as a flow diagram.
            if (box.Width > w * 0.20 || box.Height > h * 0.20) continue;
            if (box.Width < 6 || box.Height < 6) continue;

            var aspect = (double)Math.Max(box.Width, box.Height) / Math.Max(1, Math.Min(box.Width, box.Height));
            if (aspect < 1.25) continue;

            if (!SurroundedByBackground(bytes, data.Stride, w, h, box)) continue;

            var horizontal = box.Width >= box.Height;
            // The HEAD is the heavy end. These arrows are a thin shaft into a fat triangle, so
            // the head carries more pixels — testing for the narrow end pointed them backwards.
            Point tail, head;
            if (horizontal)
            {
                int third = Math.Max(1, box.Width / 3);
                int right = pts.Count(p => p.X >= x1 - third);
                int left = pts.Count(p => p.X <= x0 + third);
                head = right >= left ? new Point(x1, box.Top + box.Height / 2) : new Point(x0, box.Top + box.Height / 2);
                tail = right >= left ? new Point(x0, box.Top + box.Height / 2) : new Point(x1, box.Top + box.Height / 2);
            }
            else
            {
                int third = Math.Max(1, box.Height / 3);
                int bottom = pts.Count(p => p.Y >= y1 - third);
                int topN = pts.Count(p => p.Y <= y0 + third);
                head = bottom >= topN ? new Point(box.Left + box.Width / 2, y1) : new Point(box.Left + box.Width / 2, y0);
                tail = bottom >= topN ? new Point(box.Left + box.Width / 2, y0) : new Point(box.Left + box.Width / 2, y1);
            }

            arrows.Add(new Arrow(box, tail, head));
        }

        return arrows;
    }

    /// <summary>
    /// Is this blob floating in empty space? A connector sits in the gap between panels, while
    /// a red object inside a photograph is hemmed in by more photograph. Judged on the ring of
    /// pixels just outside the blob's box.
    /// </summary>
    private static bool SurroundedByBackground(byte[] bytes, int stride, int w, int h, Rectangle box)
    {
        int pad = 6, near = 0, total = 0;
        for (int x = box.Left - pad; x <= box.Right + pad; x += 3)
        for (int y = box.Top - pad; y <= box.Bottom + pad; y += 3)
        {
            var outside = x < box.Left || x > box.Right || y < box.Top || y > box.Bottom;
            if (!outside || x < 0 || y < 0 || x >= w || y >= h) continue;
            int i = y * stride + x * 4;
            // Near-white is the usual diagram ground; anything pale and unsaturated counts.
            int max = Math.Max(bytes[i + 2], Math.Max(bytes[i + 1], bytes[i]));
            int min = Math.Min(bytes[i + 2], Math.Min(bytes[i + 1], bytes[i]));
            if (max > 190 && max - min < 40) near++;
            total++;
        }
        return total > 0 && (double)near / total > 0.55;
    }

    // ---- panels ---------------------------------------------------------------------------

    private static List<(Rectangle Box, string Text)> MergeLabels(IReadOnlyList<OcrLineBox> lines)
    {
        var labels = new List<(Rectangle Box, string Text)>();
        foreach (var l in lines.OrderBy(l => l.Top).ThenBy(l => l.Left))
        {
            var box = Rectangle.FromLTRB(l.Left, l.Top, l.Right, l.Bottom);
            var merged = false;
            for (int i = 0; i < labels.Count; i++)
            {
                var (b, t) = labels[i];
                // Same caption when stacked directly beneath and horizontally overlapping.
                var overlaps = box.Left < b.Right && box.Right > b.Left;
                if (overlaps && box.Top - b.Bottom < l.Height * 1.2 && box.Top >= b.Top)
                {
                    labels[i] = (Rectangle.Union(b, box), $"{t} {l.Text}".Trim());
                    merged = true;
                    break;
                }
            }
            if (!merged) labels.Add((box, l.Text.Trim()));
        }
        return labels;
    }

    private static List<Panel> BuildPanels(
        Bitmap bmp, List<(Rectangle Box, string Text)> labels, string backgroundHex)
    {
        int w = bmp.Width, h = bmp.Height;
        int br = Convert.ToInt32(backgroundHex.Substring(1, 2), 16);
        int bg = Convert.ToInt32(backgroundHex.Substring(3, 2), 16);
        int bb = Convert.ToInt32(backgroundHex.Substring(5, 2), 16);

        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bytes = new byte[data.Stride * h];
        try { Marshal.Copy(data.Scan0, bytes, 0, bytes.Length); }
        finally { bmp.UnlockBits(data); }

        var boxes = labels.Select(l => l.Box).ToArray();
        var owned = labels.Select(_ => Rectangle.Empty).ToArray();

        // Pixels go to the nearest label, but only within a SHORT reach. Letting every pixel
        // find an owner however far away made each panel sprawl across the whole diagram, so
        // the boxes overlapped and matching an arrow to a panel became a coin toss.
        // Far enough for a caption to reach the picture standing above it — the "Save Trees"
        // poster sits some 360px over its label — while the bias below keeps panels from
        // swallowing their neighbours.
        var reach = Math.Max(w, h) * 0.42;

        for (int y = 0; y < h; y += 3)
        for (int x = 0; x < w; x += 3)
        {
            int i = y * data.Stride + x * 4;
            if (Math.Abs(bytes[i + 2] - br) + Math.Abs(bytes[i + 1] - bg) + Math.Abs(bytes[i] - bb) < 60)
                continue; // background

            int best = -1;
            double bestD = double.MaxValue;
            for (int k = 0; k < boxes.Length; k++)
            {
                double dx = Math.Max(0, Math.Max(boxes[k].Left - x, x - boxes[k].Right));
                double dy = Math.Max(0, Math.Max(boxes[k].Top - y, y - boxes[k].Bottom));
                var d = Math.Sqrt(dx * dx + dy * dy);

                // A caption sits UNDER its picture, so a label above this pixel is a poor
                // owner for it. Plain nearest-label put the "Save Trees" poster into the
                // "Tree" panel — it sat closer to the label above it — and the poster then
                // appeared in the opening frame instead of last.
                if (boxes[k].Bottom < y) d *= 6.0;

                if (d < bestD) { bestD = d; best = k; }
            }
            if (best < 0 || bestD > reach) continue;

            var p = new Rectangle(x, y, 3, 3);
            owned[best] = owned[best].IsEmpty ? p : Rectangle.Union(owned[best], p);
        }

        var panels = new List<Panel>();
        for (int k = 0; k < labels.Count; k++)
        {
            var box = owned[k].IsEmpty ? labels[k].Box : Rectangle.Union(owned[k], labels[k].Box);
            box = Rectangle.Intersect(box, new Rectangle(0, 0, w, h));
            if (box.Width > 4 && box.Height > 4) panels.Add(new Panel(box, labels[k].Text, k));
        }
        return panels;
    }

    // ---- order ----------------------------------------------------------------------------

    /// <summary>
    /// Order the panels to match the typed lines, pairing each line with the panel whose own
    /// words it most resembles. Null when nothing was typed, or too little of it matched to
    /// trust — then the arrows decide instead.
    /// </summary>
    private static List<Panel>? OrderByTypedText(List<Panel> panels, IReadOnlyList<string>? typed)
    {
        if (typed is null || typed.Count < 2) return null;

        static string Key(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        var left = panels.ToList();
        var ordered = new List<Panel>();
        foreach (var line in typed)
        {
            var want = Key(line);
            if (want.Length == 0) continue;

            var best = left
                .Select(p => (Panel: p, Score: Overlap(want, Key(p.Text))))
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();
            if (best.Panel is null || best.Score < 0.34) continue;

            ordered.Add(best.Panel);
            left.Remove(best.Panel);
        }

        // Needs to account for most of the diagram, or it is not really an ordering.
        if (ordered.Count < Math.Max(2, panels.Count - 1)) return null;
        ordered.AddRange(left.OrderBy(p => p.Box.Top).ThenBy(p => p.Box.Left));
        return ordered;

        // Longest common SUBSEQUENCE over the longer string. Counting shared characters instead
        // scored "tree" a perfect match against "papermakingprocessinfactory" — every letter of
        // the short word appears somewhere in the long one — so every label paired with the
        // wrong panel. Order and length both have to count.
        static double Overlap(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0) return 0;
            var dp = new int[a.Length + 1, b.Length + 1];
            for (int i = a.Length - 1; i >= 0; i--)
                for (int j = b.Length - 1; j >= 0; j--)
                    dp[i, j] = a[i] == b[j]
                        ? dp[i + 1, j + 1] + 1
                        : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            return (double)dp[0, 0] / Math.Max(a.Length, b.Length);
        }
    }

    /// <summary>
    /// Walk the arrows: each one links the panel behind its tail to the one at its head. The
    /// start is the panel nothing points at; anything the arrows never mention is appended in
    /// reading order so no part of the diagram is silently dropped.
    /// </summary>
    private static List<Panel> FollowArrows(List<Panel> panels, List<Arrow> arrows)
    {
        // Look for the panel lying BEHIND the tail and the one AHEAD of the head, along the
        // arrow's own axis. Plain nearest-box matching failed: a panel's box often reaches past
        // the arrow beside it, so both ends landed on the same panel and the link was dropped.
        int Along(Point from, int dx, int dy)
        {
            int best = -1;
            double bestD = double.MaxValue;
            for (int k = 0; k < panels.Count; k++)
            {
                var b = panels[k].Box;
                // Must lie in the direction of travel, and share the arrow's lane.
                double gap;
                if (dx != 0)
                {
                    if (from.Y < b.Top - 40 || from.Y > b.Bottom + 40) continue;
                    gap = dx > 0 ? b.Left - from.X : from.X - b.Right;
                }
                else
                {
                    if (from.X < b.Left - 40 || from.X > b.Right + 40) continue;
                    gap = dy > 0 ? b.Top - from.Y : from.Y - b.Bottom;
                }
                if (gap < -20) continue;              // behind us
                if (gap < bestD) { bestD = gap; best = k; }
            }
            return best;
        }

        var next = new Dictionary<int, int>();
        var pointedAt = new HashSet<int>();
        foreach (var a in arrows)
        {
            int dx = Math.Sign(a.Head.X - a.Tail.X), dy = Math.Sign(a.Head.Y - a.Tail.Y);
            int from = Along(a.Tail, -dx, -dy);       // backwards from the tail
            int to = Along(a.Head, dx, dy);           // onwards from the head
            if (from < 0 || to < 0 || from == to) continue;
            next.TryAdd(from, to);
            pointedAt.Add(to);
        }

        var order = new List<Panel>();
        var used = new HashSet<int>();
        var start = Enumerable.Range(0, panels.Count).FirstOrDefault(k => !pointedAt.Contains(k), -1);
        for (int at = start; at >= 0 && used.Add(at);)
        {
            order.Add(panels[at]);
            at = next.TryGetValue(at, out var n) ? n : -1;
        }

        foreach (var k in Enumerable.Range(0, panels.Count).Where(k => !used.Contains(k))
                     .OrderBy(k => panels[k].Box.Top).ThenBy(k => panels[k].Box.Left))
            order.Add(panels[k]);

        return order;
    }
}
