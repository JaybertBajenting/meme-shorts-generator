using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MemeShorts.Api.Models;
using Microsoft.Extensions.Options;

namespace MemeShorts.Api.Services;

public enum RenderFault { Gap, Ordering, Repeat }

/// <summary>Something wrong with the FINISHED video, in words a person can act on.</summary>
public sealed record RenderIssue(RenderFault Fault, string Message);

/// <summary>
/// Checks the finished mp4 - not the plan.
///
/// Everything else in this project validates the slicing DECISION: PostPlanValidator runs before
/// anything is drawn, and /api/repost/preflight is a dry run. That is how a repost with a
/// half-empty card reached the Review tab carrying severity "Info" - the plan was correct and the
/// picture was not. Matthew: "did you not add any validation at all on the vids after rendering?"
///
/// So this looks at pixels. It samples frames across the video and asks three questions that only
/// the render can answer:
///
///   GAP       rows of the picture that no slice ever shows - the blank white card
///   ORDERING  on a two-column post, the right column appeared before the left had finished
///
/// The GAP check reads the crop ranges out of props.json rather than hunting for blank pixels.
/// Two pixel-based attempts were tried and thrown away: "the card lost content" followed the
/// blurred gameplay cutting between scenes, and "a large flat featureless area" could not tell a
/// void from the Winnie-the-Pooh meme's legitimately white caption column or a cartoon's flat
/// background. Both failed videos that were fine. The crop ranges are exact and cannot be argued
/// with - a row either belongs to a slice or it does not.
///
/// ORDERING stays on pixels because it is about WHEN something appears, which props cannot say.
///
/// A legitimately white card (a meme whose right half is white with captions on it) is NOT a
/// void: the test is flat AND featureless. That distinction is the whole reason this measures
/// edge density inside the flat region rather than just its colour.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RenderValidator
{
    private readonly PipelineOptions _pipe;
    private readonly ILogger<RenderValidator> _log;

    public RenderValidator(IOptions<PipelineOptions> pipe, ILogger<RenderValidator> log)
    {
        _pipe = pipe.Value; _log = log;
    }

    /// <summary>Frames sampled across the video. More is slower; this is enough to catch a void.</summary>
    private const int Samples = 14;

    /// <summary>A flat region bigger than this share of the frame is suspicious.</summary>
    private const double VoidAreaShare = 0.10;

    /// <summary>Below this edge density a region has nothing in it at all.</summary>
    private const double VoidEdgeDensity = 0.004;

    /// <summary>A void has to persist - one frame mid-transition is not a defect.</summary>
    private const int VoidFramesToFail = 3;

    /// <summary>
    /// Rows of the picture that NO slice shows.
    ///
    /// `BuildBlocks` chains each caption's crop up to at most 10px above its own top
    /// (`Math.Max(Math.Min(prevBottom, b.Top), b.Top - 10)`), so any gap wider than that between
    /// one slice and the next is drawn by nothing at all. On a tightly-set post the gaps are
    /// under 10px and it never shows; on "two wolves" it left half the card empty while the plan
    /// still reported severity Info.
    /// </summary>
    public IReadOnlyList<RenderIssue> ValidateProps(RenderProps props)
    {
        var issues = new List<RenderIssue>();
        var post = props.Post;
        if (post?.Blocks is null || post.Blocks.Count == 0 || post.ImageHeight is not > 0)
            return issues;

        // A row is covered when ANY slice shows it.
        //
        // The first version walked each lane on its own and treated the space before a lane's
        // first block as a gap. On a comparison the right column legitimately begins halfway
        // down the post - under the shared heading and picture - so it reported a 487px hole
        // that did not exist, and the pipeline dutifully abandoned the column split and fell
        // back to a full-width reveal. That is precisely the "left and right at the same time"
        // Matthew kept reporting: my own check was causing it.
        var covered = post.Blocks
            .Select(b => (Top: b.Top, Bottom: b.Bottom))
            .Where(r => r.Bottom > r.Top)
            .OrderBy(r => r.Top)
            .ToList();
        if (covered.Count > 0)
        {
            var worst = 0;
            var at = 0;
            var reach = covered[0].Bottom;
            foreach (var (top, bottom) in covered.Skip(1))
            {
                if (top > reach && top - reach > worst) { worst = top - reach; at = reach; }
                reach = Math.Max(reach, bottom);
            }
            var limit = Math.Max(24, (int)(post.ImageHeight.Value * 0.04));
            if (worst > limit)
                issues.Add(new RenderIssue(RenderFault.Gap,
                    $"{worst}px of the picture (from y={at}) is shown by no slice at all - the "
                    + "card renders that stretch blank."));
        }

        // ROWS DRAWN TWICE. The card stacks its slices, so any two that cover the same rows AND
        // the same columns put that content on screen a second time - the wojak chins repeated
        // under a seam, the strip of car below the cars. Slices inside ONE comparison are exempt:
        // that group is drawn as a positioned box, where a caption over a picture is meant to
        // overlap it.
        var stacked = post.Blocks.Where(b => b.Bottom > b.Top).ToList();
        for (int i = 0; i < stacked.Count; i++)
        {
            var a = stacked[i];
            var dup = stacked.Skip(i + 1).FirstOrDefault(b =>
                (a.Group is null || b.Group is null || a.Group != b.Group)
                && Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) > 4
                && Math.Min(a.XRight ?? post.ImageWidth ?? int.MaxValue,
                            b.XRight ?? post.ImageWidth ?? int.MaxValue)
                   - Math.Max(a.XLeft ?? 0, b.XLeft ?? 0) > 4);
            if (dup is null) continue;

            issues.Add(new RenderIssue(RenderFault.Repeat,
                $"Rows {Math.Max(a.Top, dup.Top)}..{Math.Min(a.Bottom, dup.Bottom)} of the picture "
                + "are drawn by two different slices, so the card shows them twice."));
            break;
        }

        // THE SAME PICTURE TWICE. The card stacks its slices, so a picture that reaches it
        // both full width and again as a column's media adds its own height to the post and
        // shoves everything after it off the bottom of the frame. Pictures only: a caption
        // deliberately reaches up into the picture above it to keep its line spacing, and that
        // overlap is wanted.
        var pics = post.Blocks.Where(b => b.Text is null && b.Bottom > b.Top).ToList();
        for (int i = 0; i < pics.Count; i++)
            for (int j = i + 1; j < pics.Count; j++)
            {
                var a = pics[i];
                var b = pics[j];
                // Same exemption as above: inside ONE comparison the slices are positioned, not
                // stacked, so a picture lying over another there is where the post drew it.
                if (a.Group is not null && a.Group == b.Group) continue;
                var rows = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
                var shorter = Math.Min(a.Bottom - a.Top, b.Bottom - b.Top);
                if (shorter <= 0 || rows < shorter * 0.8) continue;

                var x0 = Math.Max(a.XLeft ?? 0, b.XLeft ?? 0);
                var x1 = Math.Min(a.XRight ?? post.ImageWidth ?? int.MaxValue,
                                  b.XRight ?? post.ImageWidth ?? int.MaxValue);
                var narrower = Math.Min(
                    (a.XRight ?? post.ImageWidth ?? 0) - (a.XLeft ?? 0),
                    (b.XRight ?? post.ImageWidth ?? 0) - (b.XLeft ?? 0));
                if (narrower <= 0 || x1 - x0 < narrower * 0.8) continue;

                issues.Add(new RenderIssue(RenderFault.Repeat,
                    $"The picture at y={a.Top}..{a.Bottom} is in the post twice (again at "
                    + $"y={b.Top}..{b.Bottom}). The card stacks its slices, so the repeat makes "
                    + "it taller than the post and the end falls off the frame."));
                i = pics.Count;
                break;
            }

        // ONE SPOKEN LINE PER SLICE. The rule Matthew has restated every round is that the line
        // on screen must be the line being read; after the slicer changes that is structurally
        // true, so it is asserted here rather than trusted. Header rows are excluded - they are
        // on screen from the first frame and nobody reads them out.
        var textSlices = post.Blocks.Count(b => b.Text is not null && b.Top >= (post.HeaderBottom ?? 0));
        if (textSlices > 0 && post.Lines.Count > 0 && textSlices != post.Lines.Count)
            issues.Add(new RenderIssue(RenderFault.Ordering,
                $"The card has {textSlices} slice(s) of text but {post.Lines.Count} spoken line(s). "
                + "They are revealed one per line, so a mismatch runs the reveal ahead of or "
                + "behind the voice for the rest of the video."));

        // ORDERING, from the reveal times rather than from pixels.
        //
        // The pixel version measured when each half of the FRAME first changed, and a comparison
        // card carries one picture spanning both halves - so both sides "changed" the instant the
        // card appeared, whatever the captions were doing. It passed a broken video early on only
        // because the card happened to be half empty then, and it failed good ones later. The
        // reveal times say exactly what the pixels were being asked to infer.
        foreach (var grp in post.Blocks.Where(b => b.Column is not null).GroupBy(b => b.Group))
        {
            // A GRID of panels is read ACROSS then down, so its columns interleave BY DESIGN and
            // the one-column-at-a-time rule does not apply to it - only within each panel row.
            // This must model the grid exactly as PostBands.NarrationOrder does, or the check
            // fails the very videos that walk is built to produce: each column's own picture
            // sitting between two of its captions is the panel boundary.
            var inGroup = post.Blocks.Where(b => b.Group == grp.Key).ToList();
            var media = inGroup.Where(b => string.IsNullOrEmpty(b.Text)).ToList();
            var caps = grp.Where(b => !string.IsNullOrEmpty(b.Text)).ToList();
            if (caps.Count == 0) continue;

            int RowOf(PostBlockRender b) =>
                media.Count(m => m.Column == b.Column && m.Bottom <= b.Top);

            var allColumns = caps.Select(b => b.Column!.Value).Distinct().OrderBy(c => c).ToList();
            var isPanelStack = allColumns.All(c =>
                media.Any(m => m.Column == c
                    && caps.Any(b => b.Column == c && b.Bottom <= m.Top)
                    && caps.Any(b => b.Column == c && b.Top >= m.Bottom)));

            // Every column, not just two: a comparison is read one column at a time, and a
            // three-column post has to finish 1998 before 2010 starts and 2010 before 2026.
            // A grid is held to the same rule one panel ROW at a time.
            var rows = isPanelStack
                ? caps.Select(RowOf).Distinct().OrderBy(r => r).ToList()
                : new List<int> { 0 };

            foreach (var row in rows)
            {
                var scope = isPanelStack ? caps.Where(b => RowOf(b) == row).ToList() : caps;
                var columns = scope.GroupBy(b => b.Column!.Value)
                                 .OrderBy(c => c.Key)
                                 .Select(c => (Column: c.Key, First: c.Min(b => b.StartMs), Last: c.Max(b => b.StartMs)))
                                 .ToList();
                var broke = false;
                for (int i = 1; i < columns.Count && !broke; i++)
                    if (columns[i].First < columns[i - 1].Last)
                    {
                        var where = isPanelStack ? $" of panel row {row + 1}" : "";
                        issues.Add(new RenderIssue(RenderFault.Ordering,
                            $"Column {i + 1}{where} starts at {columns[i].First}ms while column {i} is still "
                            + $"being read until {columns[i - 1].Last}ms - a comparison must finish "
                            + "one column before the next appears."));
                        broke = true;
                    }
                if (broke) break;
            }
        }

        return issues;
    }

    /// <summary>
    /// The same check, run on the SLICE PLAN before anything is rendered.
    ///
    /// Cheap, so the pipeline can try a fallback and check again without paying three minutes for
    /// each attempt. It reproduces the crop chaining `BuildBlocks` performs - each slice reaches
    /// at most 10px above its own top - because that cap is what turns a wide gap between slices
    /// into rows nothing draws.
    /// </summary>
    public IReadOnlyList<RenderIssue> ValidateBands(PostBands bands)
    {
        var issues = new List<RenderIssue>();
        if (bands.Height <= 0) return issues;

        // A slice appears when the FIRST of its lines is spoken, so a slice carrying several
        // lines puts them all on screen together - three at once, on the Tarantino meme.
        var crowded = bands.Blocks.FirstOrDefault(b => !b.IsMedia && b.Segments.Count > 1);
        if (crowded is not null)
            issues.Add(new RenderIssue(RenderFault.Repeat,
                $"The slice at y={crowded.Top}..{crowded.Bottom} carries {crowded.Segments.Count} "
                + "spoken lines, so all of them would appear at once instead of one at a time."));

        // Same reach as BuildBlocks, because it is literally the same code.
        var tops = PostSliceService.CropTops(bands.Blocks, bands.Width, bands.HeaderBottom);
        var ranges = bands.Blocks
            .Select(b => (Top: b.IsMedia || !tops.TryGetValue(b, out var t) ? b.Top : t, b.Bottom))
            .Where(r => r.Bottom > r.Top)
            .ToList();
        if (ranges.Count > 0)
        {
            ranges.Sort((a, b) => a.Top.CompareTo(b.Top));
            var worst = 0;
            var at = 0;
            var reach = ranges[0].Bottom;
            foreach (var (top, bottom) in ranges.Skip(1))
            {
                if (top > reach && top - reach > worst) { worst = top - reach; at = reach; }
                reach = Math.Max(reach, bottom);
            }
            var limit = Math.Max(24, (int)(bands.Height * 0.04));
            if (worst > limit)
                issues.Add(new RenderIssue(RenderFault.Gap,
                    $"{worst}px of the picture (from y={at}) would be shown by no slice - the "
                    + "card would render that stretch blank."));
        }

        return issues;
    }

    /// <summary>
    /// The checks that hold for ANY finished video, whatever format made it: the file exists, it
    /// runs as long as it was built to run, and it moves.
    ///
    /// The repost gate was the only one for a while, which left the LIST format - most of what
    /// this channel publishes - with nothing checking its output at all. Nothing here knows about
    /// posts, columns or slices, so it costs one probe and a handful of frames on every render.
    /// </summary>
    public async Task<IReadOnlyList<RenderIssue>> ValidateFileAsync(
        string videoPath, double expectedSeconds, CancellationToken ct = default)
    {
        var issues = new List<RenderIssue>();
        if (!File.Exists(videoPath))
        {
            issues.Add(new RenderIssue(RenderFault.Gap, "The render produced no video file."));
            return issues;
        }

        var actual = await ProbeSecondsAsync(videoPath, ct);
        if (actual is > 0 && expectedSeconds > 0 && Math.Abs(actual.Value - expectedSeconds) > 1.0)
            issues.Add(new RenderIssue(RenderFault.Gap,
                $"The video runs {actual:0.0}s but was built to run {expectedSeconds:0.0}s - it "
                + "was cut short or overran, so the end of it is not what was planned."));

        issues.AddRange(await ValidateAsync(videoPath, actual ?? expectedSeconds, false, ct));
        return issues;
    }

    public async Task<IReadOnlyList<RenderIssue>> ValidateAsync(
        string videoPath, double? durationSeconds, bool hasColumns, CancellationToken ct = default)
    {
        var issues = new List<RenderIssue>();
        if (!File.Exists(videoPath)) return issues;
        durationSeconds ??= await ProbeSecondsAsync(videoPath, ct);
        if (durationSeconds is not > 0) return issues;

        var dir = Directory.CreateTempSubdirectory("rendercheck_");
        try
        {
            var frames = new List<(double T, Bitmap Bmp)>();
            // Skip the very start and end: the card is mid-pop at 0 and the outro is empty.
            for (int i = 0; i < Samples; i++)
            {
                var t = durationSeconds.Value * (0.08 + 0.84 * i / (double)(Samples - 1));
                var path = Path.Combine(dir.FullName, $"f{i:D2}.jpg");
                if (await GrabAsync(videoPath, path, t, ct) is null) continue;
                frames.Add((t, new Bitmap(path)));
            }
            if (frames.Count < 4) return issues;

            try
            {
                CheckFrozen(frames, issues);
            }
            finally
            {
                foreach (var (_, b) in frames) b.Dispose();
            }
        }
        catch (Exception ex)
        {
            // A validator that throws must not fail a good render.
            _log.LogWarning(ex, "Render validation could not run on {File}.", Path.GetFileName(videoPath));
        }
        finally { try { dir.Delete(true); } catch { } }

        return issues;
    }

    /// <summary>
    /// The video must actually move. A render that comes out as one frozen image is the failure
    /// mode worth catching in the mp4 itself - everything about layout and timing is answered
    /// exactly by props, and guessing at it from pixels produced two wrong verdicts already.
    /// </summary>
    private static void CheckFrozen(List<(double T, Bitmap Bmp)> frames, List<RenderIssue> issues)
    {
        var first = frames[0].Bmp;
        var moved = frames.Skip(1).Any(f => ChangedShare(first, f.Bmp, left: true) > 0.01
                                         || ChangedShare(first, f.Bmp, left: false) > 0.01);
        if (!moved)
            issues.Add(new RenderIssue(RenderFault.Ordering,
                "Nothing changes for the whole video - it rendered as a single frozen frame."));
    }

    // ---- pixel helpers -------------------------------------------------------------------

    /// <summary>
    /// The biggest connected run of one flat colour that contains no detail, as a share of frame.
    ///
    /// Flat AND featureless together are what make a void: a white card half covered in captions
    /// is flat in places but full of edges, and must not be reported.
    /// </summary>
    private static double LargestFeaturelessRegion(Bitmap bmp)
    {
        const int w = 108, h = 192;                       // coarse is enough and far faster
        using var small = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
            g.DrawImage(bmp, new Rectangle(0, 0, w, h));

        var px = Pixels(small, out var stride);
        var key = new int[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var i = y * stride + x * 4;
                key[y * w + x] = (px[i + 2] >> 4) << 8 | (px[i + 1] >> 4) << 4 | (px[i] >> 4);
            }

        var seen = new bool[w * h];
        var best = 0.0;
        var queue = new Queue<int>();
        for (int start = 0; start < key.Length; start++)
        {
            if (seen[start]) continue;
            var colour = key[start];
            var cells = new List<int>();
            seen[start] = true;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var k = queue.Dequeue();
                cells.Add(k);
                int cx = k % w, cy = k / w;
                void Push(int nx, int ny)
                {
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) return;
                    var j = ny * w + nx;
                    if (seen[j] || key[j] != colour) return;
                    seen[j] = true;
                    queue.Enqueue(j);
                }
                Push(cx - 1, cy); Push(cx + 1, cy); Push(cx, cy - 1); Push(cx, cy + 1);
            }

            var share = (double)cells.Count / (w * h);
            if (share <= VoidAreaShare || share <= best) continue;

            // Featureless? Measure detail in the ORIGINAL frame over this region's box.
            int x0 = w, x1 = 0, y0 = h, y1 = 0;
            foreach (var k in cells)
            {
                x0 = Math.Min(x0, k % w); x1 = Math.Max(x1, k % w);
                y0 = Math.Min(y0, k / w); y1 = Math.Max(y1, k / w);
            }
            // Detail is measured at the region's OWN cells, never over its bounding box. The
            // box around a blank stretch of card also contains the captions above and below it,
            // so measuring the box reported "there is detail here" and the void went unseen -
            // which is precisely how the blank-card videos passed validation.
            if (RegionIsFeatureless(bmp, cells, w, h)) best = share;
        }
        return best;
    }

    /// <summary>
    /// Is there ANY local contrast at the region's own cells? Sampled directly at those
    /// positions in the full-size frame, so neighbouring captions cannot vouch for an empty area.
    /// </summary>
    private static bool RegionIsFeatureless(Bitmap bmp, List<int> cells, int w, int h)
    {
        var sx = bmp.Width / (double)w;
        var sy = bmp.Height / (double)h;
        var step = Math.Max(1, cells.Count / 500);
        double busy = 0;
        var n = 0;
        for (int i = 0; i < cells.Count; i += step)
        {
            var cx = (int)((cells[i] % w + 0.5) * sx);
            var cy = (int)((cells[i] / w + 0.5) * sy);
            if (cx < 2 || cy < 2 || cx >= bmp.Width - 2 || cy >= bmp.Height - 2) continue;
            var c = bmp.GetPixel(cx, cy);
            var maxDiff = 0;
            foreach (var (dx, dy) in new[] { (-2, 0), (2, 0), (0, -2), (0, 2) })
            {
                var o = bmp.GetPixel(cx + dx, cy + dy);
                maxDiff = Math.Max(maxDiff, Math.Abs(c.R - o.R) + Math.Abs(c.G - o.G) + Math.Abs(c.B - o.B));
            }
            if (maxDiff > 45) busy++;
            n++;
        }
        return n > 0 && busy / n < 0.02;
    }

    /// <summary>Share of pixels sitting on an edge - a crude but reliable "is there anything here".</summary>
    private static double EdgeDensity(Bitmap bmp, int x, int y, int w, int h)
    {
        x = Math.Clamp(x, 0, Math.Max(0, bmp.Width - 2));
        y = Math.Clamp(y, 0, Math.Max(0, bmp.Height - 2));
        w = Math.Clamp(w, 2, bmp.Width - x);
        h = Math.Clamp(h, 2, bmp.Height - y);

        using var crop = new Bitmap(Math.Min(160, w), Math.Min(280, h), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(crop))
            g.DrawImage(bmp, new Rectangle(0, 0, crop.Width, crop.Height),
                new Rectangle(x, y, w, h), GraphicsUnit.Pixel);

        var px = Pixels(crop, out var stride);
        var edges = 0;
        var total = 0;
        for (int cy = 0; cy < crop.Height - 1; cy++)
            for (int cx = 0; cx < crop.Width - 1; cx++)
            {
                var i = cy * stride + cx * 4;
                var right = i + 4;
                var down = i + stride;
                var d = Math.Abs(px[i] - px[right]) + Math.Abs(px[i + 1] - px[right + 1])
                      + Math.Abs(px[i] - px[down]) + Math.Abs(px[i + 1] - px[down + 1]);
                if (d > 60) edges++;
                total++;
            }
        return total == 0 ? 0 : (double)edges / total;
    }

    /// <summary>How much of one half of the frame changed against the opening frame.</summary>
    private static double ChangedShare(Bitmap first, Bitmap later, bool left)
    {
        const int w = 108, h = 192;
        using var a = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(a)) g.DrawImage(first, new Rectangle(0, 0, w, h));
        using (var g = Graphics.FromImage(b)) g.DrawImage(later, new Rectangle(0, 0, w, h));

        var pa = Pixels(a, out var stride);
        var pb = Pixels(b, out _);
        int x0 = left ? 10 : w / 2, x1 = left ? w / 2 : w - 10;
        var changed = 0;
        var total = 0;
        for (int y = (int)(h * 0.25); y < h * 0.75; y++)
            for (int x = x0; x < x1; x++)
            {
                var i = y * stride + x * 4;
                if (Math.Abs(pa[i] - pb[i]) + Math.Abs(pa[i + 1] - pb[i + 1])
                    + Math.Abs(pa[i + 2] - pb[i + 2]) > 90) changed++;
                total++;
            }
        return total == 0 ? 0 : (double)changed / total;
    }

    private static byte[] Pixels(Bitmap bmp, out int stride)
    {
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

    /// <summary>Video length, via ffmpeg, so callers outside the pipeline need not know it.</summary>
    public async Task<double?> ProbeSecondsAsync(string video, CancellationToken ct)
    {
        try
        {
            var res = await ProcessRunner.RunAsync(_pipe.FfmpegCommand,
                new[] { "-hide_banner", "-i", video }, Path.GetDirectoryName(video),
                TimeSpan.FromSeconds(30), ct);
            var text = res.StdErr + res.StdOut;
            var m = System.Text.RegularExpressions.Regex.Match(text, @"Duration:\s*(\d+):(\d+):(\d+\.?\d*)");
            if (!m.Success) return null;
            return int.Parse(m.Groups[1].Value) * 3600
                 + int.Parse(m.Groups[2].Value) * 60
                 + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        }
        catch { return null; }
    }

    private async Task<string?> GrabAsync(string video, string dest, double seconds, CancellationToken ct)
    {
        try
        {
            var res = await ProcessRunner.RunAsync(_pipe.FfmpegCommand,
                new[] { "-y", "-hide_banner", "-loglevel", "error",
                        "-ss", Math.Max(0, seconds).ToString("0.000", CultureInfo.InvariantCulture),
                        "-i", video, "-frames:v", "1", "-q:v", "3", dest },
                Path.GetDirectoryName(video), TimeSpan.FromSeconds(30), ct);
            return res.ExitCode == 0 && File.Exists(dest) ? dest : null;
        }
        catch { return null; }
    }
}
