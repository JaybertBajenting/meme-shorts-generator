namespace MemeShorts.Api.Models;

/// <summary>Which half of a two-column post a slice belongs to.</summary>
public enum PostSide { Full, Left, Right }

/// <summary>
/// A run of words spoken as one unit. A plain line is a single <see cref="PostSide.Full"/>
/// segment; on a comparison meme each column's line is its own, so each can be timed —
/// and revealed — on its own.
/// </summary>
public sealed record PostSegment(string Text, PostSide Side);

/// <summary>
/// One slice of a post screenshot, in IMAGE pixel space. Either a line of text (with what it
/// says) or a stretch of picture.
///
/// A slice is usually the full width of the post. On a two-column comparison it is only its
/// own column's half — that is what lets the left column build line by line on its own before
/// the right one appears beside it.
/// </summary>
public sealed record PostBlock(int Top, int Bottom, string? Text, bool IsMedia)
{
    public int Height => Bottom - Top;

    /// <summary>The spoken runs inside this slice.</summary>
    public IReadOnlyList<PostSegment> Segments { get; init; } = Array.Empty<PostSegment>();

    /// <summary>Which column this slice belongs to; Full spans the whole post.</summary>
    public PostSide Side { get; init; } = PostSide.Full;

    /// <summary>
    /// Which column of the comparison this slice sits in, counted from the left; null spans the
    /// whole post.
    ///
    /// A comparison is not always two columns. "Applying for a job in... 1998 / 2010 / 2026" is
    /// three, and while Left/Right was the only vocabulary the middle column was swept in with
    /// the right one - its lines merged into the right column's rows ("Create an account Apply
    /// online") and 2010 was never revealed or read on its own. <see cref="Side"/> is now only
    /// the shorthand the renderer speaks; this is the truth.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>Horizontal crop. Null spans the full width.</summary>
    public int? XLeft { get; init; }
    public int? XRight { get; init; }

    /// <summary>
    /// Comparison id. Slices sharing one are laid out SIDE BY SIDE rather than stacked, which
    /// is how the second column ends up beside the first instead of underneath it.
    /// </summary>
    public int? Group { get; init; }
}

/// <summary>
/// A post screenshot broken into the pieces the video reveals.
///
/// The split comes from the OCR engine's own line geometry, not from pixel gaps: OCR knows
/// exactly which rows are text, which survives tight line spacing, page chrome down the
/// margins, and a photo sitting in the middle of the post rather than at the end.
/// </summary>
public sealed record PostBands(
    int Width,
    int Height,
    IReadOnlyList<PostBlock> Blocks,
    /// <summary>Bottom of the always-visible header (the author/metadata row).</summary>
    int HeaderBottom,
    /// <summary>The card's own background, as #rrggbb.</summary>
    string Background,
    /// <summary>x of the gutter between columns, or null on a single-column post.</summary>
    int? Gutter = null)
{
    /// <summary>
    /// A flow diagram: slices are placed where they SIT in the picture and uncovered in arrow
    /// order, instead of being stacked into a growing card.
    /// </summary>
    public bool IsDiagram { get; init; }

    /// <summary>
    /// What happened to the column split, in a few words ("2 group(s) kept", "discarded by
    /// misaligned:1", "no gutter found").
    ///
    /// Both columns arriving at once is the single most reported defect in this format, and up
    /// to now the only record of WHY a split was thrown away was a log line in a detached
    /// console nobody reads. Carried on the plan so preflight can report it.
    /// </summary>
    public string ColumnNote { get; init; } = "";

    /// <summary>Text slices below the header — the ones revealed and read aloud.</summary>
    public IEnumerable<PostBlock> CaptionLines =>
        Blocks.Where(b => !b.IsMedia && b.Top >= HeaderBottom);

    public IEnumerable<PostBlock> Media => Blocks.Where(b => b.IsMedia);

    /// <summary>
    /// The spoken runs in the order they are READ, which is not always top to bottom.
    ///
    /// A comparison is finished on one side before the other starts: the whole left column —
    /// heading, then punchline — then the whole right column. Reading it band by band instead
    /// interleaved the halves, so neither comparison ever landed.
    /// </summary>
    public IEnumerable<(PostBlock Block, PostSegment Segment)> NarrationOrder()
    {
        var caps = CaptionLines.ToList();
        for (int i = 0; i < caps.Count;)
        {
            if (caps[i].Group is not { } group)
            {
                foreach (var s in caps[i].Segments) yield return (caps[i], s);
                i++;
                continue;
            }

            int end = i;
            while (end < caps.Count && caps[end].Group == group) end++;

            var inGroup = caps.GetRange(i, end - i);

            // A comparison can also hold FULL-width rows - a line written across both columns,
            // or one sitting over the picture beneath them. They are read where they are
            // written: the ones above the columns first, the ones below them last. Leaving them
            // out of this walk left them unspoken, and an unspoken slice is on screen from the
            // first frame.
            var columnTop = inGroup.Where(b => b.Column is not null)
                                   .Select(b => (int?)b.Top)
                                   .Min() ?? int.MaxValue;

            foreach (var b in inGroup.Where(b => b.Column is null && b.Top < columnTop)
                                     .OrderBy(b => b.Top))
                foreach (var s in b.Segments)
                    yield return (b, s);

            var columns = inGroup.Where(b => b.Column is not null)
                                 .Select(b => b.Column!.Value)
                                 .Distinct()
                                 .OrderBy(c => c)
                                 .ToList();

            // A GRID of panels is not a comparison, and is not read like one.
            //
            // Reading each column out whole is right for a table - the whole of 1998, then the
            // whole of 2010. A 2x2 meme is the other shape: four panels, each with its own
            // caption, written and read ACROSS then down. Reading it column-first said "my
            // manager asking if we can install the AI directly into the Wi-Fi" at 4.9s while
            // that caption stayed hidden until 14.0s, because the typed post - and so the
            // voice - is in panel order.
            //
            // The tell is each column's OWN picture sitting BETWEEN two of its captions: that
            // is a panel boundary. A table's columns are unbroken lists of text, and a photo
            // above or below a column is not a boundary, so neither is mistaken for a grid.
            var groupMedia = Blocks.Where(m => m.IsMedia && m.Group == group).ToList();
            int RowOf(PostBlock b) =>
                groupMedia.Count(m => m.Column == b.Column && m.Bottom <= b.Top);

            var isPanelStack = columns.Count > 0 && columns.All(c =>
                groupMedia.Any(m => m.Column == c
                    && inGroup.Any(b => b.Column == c && b.Bottom <= m.Top)
                    && inGroup.Any(b => b.Column == c && b.Top >= m.Bottom)));

            if (isPanelStack)
            {
                var lastRow = inGroup.Where(b => b.Column is not null)
                                     .Select(RowOf)
                                     .DefaultIfEmpty(0)
                                     .Max();
                for (int row = 0; row <= lastRow; row++)
                    foreach (var column in columns)
                        foreach (var b in inGroup
                                     .Where(b => b.Column == column && RowOf(b) == row)
                                     .OrderBy(b => b.Top))
                            foreach (var s in b.Segments)
                                yield return (b, s);
            }
            else
            {
                foreach (var column in columns)
                    foreach (var b in inGroup.Where(b => b.Column == column).OrderBy(b => b.Top))
                        foreach (var s in b.Segments)
                            yield return (b, s);
            }

            foreach (var b in inGroup.Where(b => b.Column is null && b.Top >= columnTop)
                                     .OrderBy(b => b.Top))
                foreach (var s in b.Segments)
                    yield return (b, s);

            i = end;
        }
    }
}
