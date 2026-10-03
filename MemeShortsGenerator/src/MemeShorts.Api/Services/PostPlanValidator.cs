using MemeShorts.Api.Models;

namespace MemeShorts.Api.Services;

/// <summary>How badly wrong a slice plan looks.</summary>
public enum PlanSeverity { Info, Warn, Broken }

public sealed record PlanIssue(PlanSeverity Severity, string Code, string Message);

/// <summary>
/// Sanity-checks a slice plan BEFORE anything is rendered.
///
/// The slicer infers a layout from each screenshot rather than matching it against a list of
/// known formats, so a screenshot in a shape it has never met produces a plan that is wrong in
/// a way no format-specific rule could anticipate. Nothing here knows about any particular
/// site's layout — it only asks whether the plan is SELF-CONSISTENT: do slices overlap, is
/// anything left uncovered, is there something to say, are the coordinates sane. A plan can be
/// wrong about a layout nobody has seen and still fail these checks, which is the point.
/// </summary>
public static class PostPlanValidator
{
    public static IReadOnlyList<PlanIssue> Validate(PostBands bands, IReadOnlyList<string> spoken,
        bool[,]? ink = null, int inkStep = 4)
    {
        var issues = new List<PlanIssue>();
        var blocks = bands.Blocks;

        void Add(PlanSeverity s, string code, string msg) => issues.Add(new PlanIssue(s, code, msg));

        if (blocks.Count == 0)
        {
            Add(PlanSeverity.Broken, "no-slices", "The picture produced no slices at all.");
            return issues;
        }

        // --- coordinates have to make sense on their own terms ---
        foreach (var b in blocks)
        {
            if (b.Bottom <= b.Top)
                Add(PlanSeverity.Broken, "inverted", $"A slice ends above where it starts (top {b.Top}, bottom {b.Bottom}).");
            if (b.Top < 0 || b.Bottom > bands.Height)
                Add(PlanSeverity.Broken, "out-of-bounds", $"A slice runs outside the picture (top {b.Top}, bottom {b.Bottom}, picture {bands.Height}px).");
            if (b.XLeft is { } xl && b.XRight is { } xr && xr <= xl)
                Add(PlanSeverity.Broken, "inverted-x", $"A slice's right edge is left of its left edge ({xl} -> {xr}).");
        }

        // --- slices in the same lane must not share pixels ---
        // This is the "the lines overlap it" failure: two slices covering the same rows in the
        // same column render on top of each other.
        // Panels side by side are their own lane. A comic grid's two top panels cover exactly
        // the same rows and are not on top of each other at all - they sit beside one another,
        // which is what the x bounds say.
        foreach (var lane in blocks.GroupBy(b => (b.Group, b.Column, b.XLeft, b.XRight)))
        {
            var lanes = lane.OrderBy(b => b.Top).ToList();
            for (int i = 1; i < lanes.Count; i++)
            {
                var prev = lanes[i - 1];
                var cur = lanes[i];
                var overlap = prev.Bottom - cur.Top;
                // A pixel or two of touching is just rounding; a real overlap eats a line.
                if (overlap > 3)
                    Add(PlanSeverity.Broken, "overlap",
                        $"Two slices in the same column overlap by {overlap}px (rows {cur.Top}-{prev.Bottom}).");
            }
        }

        // --- there has to be something to say ---
        var captions = bands.CaptionLines.ToList();
        if (captions.Count == 0)
            Add(PlanSeverity.Broken, "no-captions", "No spoken lines were found below the header.");
        if (spoken.Count(s => !string.IsNullOrWhiteSpace(s)) == 0)
            Add(PlanSeverity.Broken, "nothing-spoken", "Every line came out blank, so there is no narration.");

        // --- a single slice swallowing the post means the split failed ---
        var textBlocks = blocks.Where(b => !b.IsMedia).ToList();
        if (textBlocks.Count > 0)
        {
            var heights = textBlocks.Select(b => b.Height).OrderBy(h => h).ToList();
            var median = heights[heights.Count / 2];
            if (median > 0)
            {
                foreach (var b in textBlocks.Where(b => b.Height > median * 4 && b.Height > 120))
                    Add(PlanSeverity.Warn, "fat-line",
                        $"One text slice is {b.Height}px tall against a typical {median}px — several rows were probably merged into one.");
            }
        }

        // --- content the plan never covers ---
        // Rows below the header that belong to no slice are content that will never appear.
        var covered = new bool[Math.Max(1, bands.Height)];
        foreach (var b in blocks)
            for (int y = Math.Max(0, b.Top); y < Math.Min(bands.Height, b.Bottom); y++)
                covered[y] = true;

        int gapStart = -1, worstGap = 0, worstAt = 0;
        for (int y = Math.Max(0, bands.HeaderBottom); y < bands.Height; y++)
        {
            if (!covered[y]) { if (gapStart < 0) gapStart = y; }
            else if (gapStart >= 0)
            {
                if (y - gapStart > worstGap) { worstGap = y - gapStart; worstAt = gapStart; }
                gapStart = -1;
            }
        }
        if (gapStart >= 0 && bands.Height - gapStart > worstGap) { worstGap = bands.Height - gapStart; worstAt = gapStart; }
        // Ordinary spacing between lines is small; a big unclaimed run is a dropped chunk.
        if (worstGap > bands.Height * 0.18 && worstGap > 140)
            Add(PlanSeverity.Warn, "uncovered",
                $"{worstGap}px of the picture (from y={worstAt}) is in no slice — that part will never be shown or read.");

        // --- holes in the plan, in TWO dimensions ---
        //
        // Checking each axis on its own is not enough and hid the worst failure seen so far.
        // On a four-column meme the gutter was found at x=319 of 719 and the right column came
        // out FOUR pixels wide, so x 319-715 of the text rows belonged to no slice and rendered
        // as bare black card for the whole video. Every row was covered by something and every
        // column was covered by something, so both one-dimensional checks reported it clean.
        // The hole only exists as an area.
        var textish = blocks.Where(b => !b.IsMedia).ToList();
        if (textish.Count > 0)
        {
            int tx0 = textish.Min(b => Math.Max(0, b.XLeft ?? 0));
            int tx1 = textish.Max(b => Math.Min(bands.Width, b.XRight ?? bands.Width));
            int ty0 = textish.Min(b => Math.Max(0, b.Top));
            int ty1 = textish.Max(b => Math.Min(bands.Height, b.Bottom));

            if (tx1 > tx0 && ty1 > ty0)
            {
                // Coarse grid: precision is irrelevant, a hole worth reporting is enormous.
                const int Step = 4;
                int gw = Math.Max(1, (tx1 - tx0) / Step), gh = Math.Max(1, (ty1 - ty0) / Step);
                var cell = new bool[gw, gh];
                foreach (var b in blocks)
                {
                    int bx0 = Math.Max(tx0, b.XLeft ?? 0), bx1 = Math.Min(tx1, b.XRight ?? bands.Width);
                    int by0 = Math.Max(ty0, b.Top), by1 = Math.Min(ty1, b.Bottom);
                    for (int x = bx0; x < bx1; x += Step)
                        for (int y = by0; y < by1; y += Step)
                        {
                            int gx = (x - tx0) / Step, gy = (y - ty0) / Step;
                            if (gx >= 0 && gx < gw && gy >= 0 && gy < gh) cell[gx, gy] = true;
                        }
                }

                // Count only cells that HAVE something in them. Without this, the blank margin
                // beside a short left-aligned line counts as a hole and every ordinary post
                // trips the check — measured 32-55% "holes" on three posts that render fine.
                int empty = 0, considered = 0;
                for (int x = 0; x < gw; x++)
                    for (int y = 0; y < gh; y++)
                    {
                        if (ink is not null)
                        {
                            int ix = (tx0 + x * Step) / inkStep, iy = (ty0 + y * Step) / inkStep;
                            if (ix < 0 || iy < 0 || ix >= ink.GetLength(0) || iy >= ink.GetLength(1)) continue;
                            if (!ink[ix, iy]) continue;
                        }
                        considered++;
                        if (!cell[x, y]) empty++;
                    }

                // Thresholds measured over 108 saved screenshots: holes tail off smoothly from
                // 15% upward and only the >50% cases are the genuinely broken column splits.
                // Flagging at 12% would have condemned 30 of 108, most of which render fine.
                var share = considered == 0 ? 0 : (double)empty / considered;
                if (share > 0.25 && considered > 40)
                    Add(share > 0.50 ? PlanSeverity.Broken : PlanSeverity.Warn, "hole",
                        $"{share:P0} of the area the post's text sits in belongs to no slice, so it renders as a blank block for the whole video.");
            }
        }

        // A column a few pixels wide is not a column. This is the cause behind the hole above,
        // reported separately so the log says WHY rather than only that a hole exists.
        foreach (var g in blocks.Where(b => b.Group is not null && !b.IsMedia).GroupBy(b => (b.Group, b.Column)))
        {
            var w = g.Max(b => Math.Min(bands.Width, b.XRight ?? bands.Width))
                  - g.Min(b => Math.Max(0, b.XLeft ?? 0));
            if (w < bands.Width * 0.12)
                Add(PlanSeverity.Broken, "sliver-column",
                    $"Column {(g.Key.Column ?? 0) + 1} is only {w}px wide against a {bands.Width}px post — the column split landed in the wrong place.");
        }

        // --- half a comparison is worse than none ---
        foreach (var g in blocks.Where(b => b.Group is not null).GroupBy(b => b.Group))
        {
            var columns = g.Select(b => b.Column).Distinct().ToList();
            if (columns.Count == 1)
                Add(PlanSeverity.Warn, "one-sided-comparison",
                    $"A side-by-side group only has column {(columns[0] ?? 0) + 1} — the rest is missing.");
        }

        // --- the header should be a header, not the post ---
        if (bands.HeaderBottom > bands.Height * 0.4)
            Add(PlanSeverity.Warn, "huge-header",
                $"The always-visible header covers {100 * bands.HeaderBottom / Math.Max(1, bands.Height)}% of the picture, so most of the post never animates in.");

        // --- OCR noise read as dozens of lines ---
        if (captions.Count > 40)
            Add(PlanSeverity.Warn, "too-many-lines",
                $"{captions.Count} separate spoken lines were found, which usually means picture detail was read as text.");

        return issues;
    }

    public static PlanSeverity Worst(IReadOnlyList<PlanIssue> issues) =>
        issues.Count == 0 ? PlanSeverity.Info : issues.Max(i => i.Severity);
}
