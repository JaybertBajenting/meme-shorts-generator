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

            foreach (var side in new[] { PostSide.Left, PostSide.Right })
                foreach (var b in caps.GetRange(i, end - i).Where(b => b.Side == side).OrderBy(b => b.Top))
                    foreach (var s in b.Segments)
                        yield return (b, s);

            i = end;
        }
    }
}
