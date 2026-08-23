using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using MemeShorts.Api.Models;

namespace MemeShorts.Api.Services;

/// <summary>
/// Cuts a pasted post screenshot into the slices the video reveals one at a time.
///
/// The reference channel's actual mechanic, confirmed by zooming into his frames: the text on
/// screen carries JPEG ringing, so it is the ORIGINAL screenshot's pixels, not text redrawn in
/// a matching font. Redrawing it puts the words on screen twice, because the screenshot already
/// contains them.
///
/// The line positions come from the OCR engine rather than from pixel-gap analysis. Gap
/// analysis was rewritten three times and broke on every new layout â€” an avatar bridging the
/// rows, page chrome down both margins leaving no blank row anywhere, tight line spacing, a
/// photo in the middle instead of at the end. OCR already reports exactly where each line sits,
/// so a picture is simply a stretch with no text in it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PostSliceService
{
    private readonly ILogger<PostSliceService> _log;

    public PostSliceService(ILogger<PostSliceService> log) => _log = log;

    /// <summary>A gap this many times the typical line height counts as a picture, not spacing.</summary>
    private const double MediaGapRatio = 1.6;

    // Image-board posts print the attachment's filename and size above the text. They are real
    // lines, so OCR reads them and they would be narrated ("swaos dot jay peg, one point oh two
    // megabyte") â€” several seconds of nothing before the joke starts. Both patterns must match
    // the WHOLE line, so a caption that merely mentions a file is left alone.
    private static readonly Regex FileNameLine = new(
        @"^\s*\S+\.(?:jpe?g|png|gif|gifv|webp|bmp|mp4|webm|mov)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FileSizeLine = new(
        @"^\s*\d+(?:\.\d+)?\s*[KMG]?B\b[\s,]*(?:jpe?g|png|gif|gifv|webp|bmp|mp4|webm|mov)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The page's own furniture, not the post: an image board prints a reply count and a link to
    // the thread under every post, and read aloud it lands as a line of the joke.
    private static readonly Regex BoardChromeLine = new(
        @"^\s*(view\s+thread|reply|\d+\s*repl(y|ies)[\s/,\d]*(images?)?|\d+\s*images?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // An embedded video brings YouTube's own furniture with it â€” the uploader, the view count,
    // how long ago, and the length stamped on the thumbnail. The post is the title and the
    // words under it; the statistics line is not part of what anybody wrote.
    private static readonly Regex VideoStatsLine = new(
        @"\d[\d.,]*\s*[KMB]?\s*views?\b.*\bago\b|\bviews?\b.*â€¢|â€¢.*\bviews?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The duration badge in a thumbnail's corner: "12:34", "1:02:33".
    private static readonly Regex DurationBadge = new(
        @"^\s*\d{1,2}:\d{2}(:\d{2})?\s*$", RegexOptions.Compiled);

    // A social post arrives wrapped in the site's own furniture, and OCR reads all of it. Left
    // in, the voice announces "Tweet", the @handle, "8:55 PM 1/9/19 Twitter for iPhone" and
    // "Likes 1,142 75 Retweets" before reaching a single word the author wrote.
    //
    // Every rule here matches a WHOLE line and nothing else. Two earlier attempts at inferring
    // which lines were chrome from the post's structure silently ate real punchlines, so these
    // deliberately describe the exact furniture instead of reasoning about layout.

    /// <summary>A lone UI word: "Tweet", "Reply", "GIF", "Follow".</summary>
    private static readonly Regex UiWordLine = new(
        @"^[^\p{L}\p{Nd}]*(tweet|retweet(ed)?|reply|replies|repost(ed)?|share[ds]?|follow(ing|ers)?|gif|video|photo|album|verified|promoted|sponsored|translate post|show (more|this thread))[^\p{L}\p{Nd}]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A relative timestamp on its own: "17h", "3d", "2w", or a bare "ago".</summary>
    // Leading junk is allowed because the separator bullet before a timestamp comes back
    // from OCR as an unmapped glyph: the real line is "� 17h", not "17h".
    private static readonly Regex RelativeTimeLine = new(
        @"^[^\p{L}\p{Nd}]*(?:\d+\s*(?:[smhdwy]|second|minute|hour|day|week|month|year)s?(?:\s*ago)?|ago)[^\p{L}\p{Nd}]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A date on its own: "Aug 23, 2022", "23 August 2022", "June 30th".</summary>
    private static readonly Regex DateLine = new(
        @"^\s*(?:(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?\s+\d{1,2}|\d{1,2}\s+(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*)\,?\s+\d{4}\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The posted-at line under a tweet: a clock time plus the client or platform.</summary>
    private static readonly Regex PostedAtLine = new(
        @"\d{1,2}:\d{2}\s*(AM|PM)?\b.*\b(twitter|x|facebook|instagram|tiktok|threads|tumblr|reddit)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Engagement counts, in either order: "1,142 Likes", "Likes 1,142 75 Retweets".</summary>
    private static readonly Regex EngagementLine = new(
        @"^\s*(?:[\d\.,]+\s*[KMkm]?\s*|(?:likes?|retweets?|reposts?|replies|comments?|shares?|views?|quotes?|bookmarks?)\s*)+$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A username slug with no spaces in it: "official-boob-posts", "some_user_123".</summary>
    private static readonly Regex UsernameSlugLine = new(
        @"^\s*[a-z0-9]+(?:[-_][a-z0-9]+){1,}\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A month and day with no year: "June 30th", "Jun 3". Every message in a
    /// screenshotted chat carries one, and read aloud they bury the conversation.</summary>
    private static readonly Regex ShortDateLine = new(
        @"^[^\p{L}\p{Nd}]*(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\s*\d{1,2}(st|nd|rd|th)?[^\p{L}\p{Nd}]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Chat and mail app furniture: "Completed", "Details", "Delivered", "Read".</summary>
    private static readonly Regex AppChromeLine = new(
        @"^[^\p{L}\p{Nd}]*(completed|details|delivered|read|sent|seen|today|yesterday|now|edited|forwarded|\d{1,2}:\d{2}( ?[ap]m)?)(\s+(completed|details|delivered|read|sent|seen|today|yesterday|now|edited))*[^\p{L}\p{Nd}]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsPostChrome(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return false;
        // An engagement line must actually mention an engagement word; the pattern alone would
        // otherwise swallow a line that is nothing but numbers, and "1,142" on its own could be
        // the point of the post.
        if (EngagementLine.IsMatch(t)
            && Regex.IsMatch(t, @"\b(likes?|retweets?|reposts?|replies|comments?|shares?|views?|quotes?|bookmarks?)\b",
                             RegexOptions.IgnoreCase))
            return true;
        return UiWordLine.IsMatch(t) || RelativeTimeLine.IsMatch(t) || DateLine.IsMatch(t)
            || ShortDateLine.IsMatch(t) || AppChromeLine.IsMatch(t)
            || PostedAtLine.IsMatch(t) || HandleLine.IsMatch(t) || UsernameSlugLine.IsMatch(t);
    }

    private static bool IsFileMetadata(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && (FileNameLine.IsMatch(text) || FileSizeLine.IsMatch(text) || BoardChromeLine.IsMatch(text)
            || VideoStatsLine.IsMatch(text) || DurationBadge.IsMatch(text) || IsPostChrome(text));

    /// <summary>
    /// OCR reads DRAWINGS too. A cartoon alien's eyes come back as the characters "00", which
    /// the voice then dutifully says as "zero zero". Anything this short with no letters in it
    /// is picture noise, never words worth reading.
    /// </summary>
    private static bool IsNoise(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return true;
        var alphanumeric = t.Where(char.IsLetterOrDigit).ToArray();
        if (alphanumeric.Length == 0) return true;                       // punctuation only
        if (alphanumeric.Length <= 1) return true;                       // a stray mark

        // One character over and over is never a word. A row of "??????" comes back from OCR as
        // "QQQQQQ" â€” letters, so the test above lets it through â€” and the voice then spells it
        // out. Same for "!!!!" and other stretched-out punctuation.
        if (alphanumeric.Length >= 3 && alphanumeric.All(c => c == alphanumeric[0])) return true;

        // A stray single LETTER means OCR lost its footing â€” "i sleep" written across a photo
        // came back as "Q rsleep", and the voice read out the Q. Only "a" and "i" stand alone
        // in English, and digits are fine ("Step 1:"), so anything else on its own condemns a
        // short line. Length-capped, because a long caption is worth reading even with a
        // mangled character in it.
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 3 && words.Any(word =>
            {
                var bare = new string(word.Where(char.IsLetterOrDigit).ToArray());
                return bare.Length == 1 && char.IsLetter(bare[0]) && !"aAiI".Contains(bare[0]);
            }))
            return true;

        return alphanumeric.Length <= 3 && !alphanumeric.Any(char.IsLetter);
    }

    // A post's byline names who posted and when â€” it is chrome, not the joke, so it stays on
    // screen without being read out. Layout alone cannot always spot it (on an image board the
    // thumbnail sits BESIDE the text, so no full-width picture follows the byline), but the
    // shape of the words themselves is unmistakable.
    private static readonly Regex BylineLike = new(
        @"No\.\d{4,}|\b\d{2}/\d{2}/\d{2}\b|\bFollow\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>An @handle on a line of its own â€” the second half of a social byline.</summary>
    private static readonly Regex HandleLine = new(
        @"^\s*@\w[\w.]*\s*$", RegexOptions.Compiled);

    public PostBands Analyze(string imagePath, IReadOnlyList<OcrLineBox> ocrLines,
        DiagramSliceService? diagrams = null, IReadOnlyList<string>? typedLines = null)
    {
        using var bmp = new Bitmap(imagePath);
        int w = bmp.Width, h = bmp.Height;
        var background = ModalColour(bmp);

        // A flow diagram reads in arrow order, which no amount of band slicing can recover â€”
        // the trees diagram's bottom row runs right to left. It only claims images with several
        // arrows in them, so everything else falls through to the usual path.
        if (diagrams?.TryAnalyze(imagePath, ocrLines, background, typedLines) is { } diagram)
            return diagram;

        // Throw out picture noise and attachment metadata BEFORE anything is grouped. Doing it
        // afterwards was too late: "283 KB PNG" sits level with the first line of the post, so
        // the two merged into one row and the combined text no longer looked like metadata â€”
        // the size got read out as part of the joke.
        var lines = ocrLines.Where(l => !IsNoise(l.Text) && !IsFileMetadata(l.Text)).ToList();
        var dropped = ocrLines.Count - lines.Count;
        if (dropped > 0)
            _log.LogInformation("Ignored {N} noise/metadata line(s) in {File}.",
                dropped, Path.GetFileName(imagePath));

        if (lines.Count == 0)
        {
            _log.LogWarning("No text found in {File}; showing the whole picture at once.",
                Path.GetFileName(imagePath));
            return new PostBands(w, h,
                new[] { new PostBlock(0, h, null, true) }, 0, background);
        }

        // Words on the same visual row arrive as separate OCR lines (a username and a timestamp
        // sitting side by side, for instance). Merge anything that overlaps vertically.
        var rows = MergeIntoRows(lines);

        var typical = rows.Select(r => r.Height).OrderBy(x => x).ElementAt(rows.Count / 2);
        var gapLimit = Math.Max(12, (int)(typical * MediaGapRatio));

        // A comparison meme puts two independent columns side by side. Merging across them
        // interleaves the halves a row at a time â€” "Cars in / Cars in", "action movies /
        // horror movies" â€” so the voice reads shredded nonsense. Each column is read top to
        // bottom on its own instead.
        //
        // Detected PER BAND, not once for the whole post: a full-width title above the columns
        // crosses every candidate gutter and defeated a global test, which is how the squid
        // meme ended up reading "I am the legend that haunts the say blblbl in smol".
        int? gutter = null;

        // Walk top to bottom, turning every sizeable text-free stretch into a picture block.
        var blocks = new List<PostBlock>();
        int cursor = 0;
        int nextGroup = 0, currentGroup = 0;
        bool prevSplit = false;
        // Each comparison gets ONE gutter shared by all its bands, found by intersecting what
        // every band allows. Letting each band choose its own put the columns at 306px on the
        // heading row and 330px on the punchline row, so the card's centre seam zig-zagged.
        var bandList = GroupIntoBands(rows, gapLimit);
        var groupGutter = new Dictionary<int, int?>();
        for (int i = 0, g0 = 0; i < bandList.Count; )
        {
            if (FindColumnGutter(lines, bandList[i], w, bmp, background) is null) { i++; continue; }
            int end = i;
            while (end < bandList.Count && FindColumnGutter(lines, bandList[end], w, bmp, background) is not null) end++;
            groupGutter[g0++] = FindColumnGutter(lines, bandList.GetRange(i, end - i).SelectMany(b => b).ToList(), w, bmp, background);
            i = end;
        }

        foreach (var band in bandList)
        {
            if (band[0].Top - cursor >= gapLimit)
                blocks.Add(new PostBlock(cursor, band[0].Top, null, true));

            var splitsHere = FindColumnGutter(lines, band, w, bmp, background) is not null;
            var candidate = splitsHere
                ? groupGutter.GetValueOrDefault(prevSplit ? currentGroup : nextGroup)
                : null;

            // A band with words on ONE side only still belongs to that column. Only the first
            // half of this meme has text in both columns; treating the second half as full
            // width ran its slice straight across the picture beside it, tearing that picture
            // into two pieces with the caption wedged between them.
            var oneSided = PostSide.Full;
            if (candidate is null && prevSplit && gutter is { } active)
            {
                var inBand = lines
                    .Where(l => l.Top >= band[0].Top - 2 && l.Bottom <= band[^1].Bottom + 2)
                    .ToList();
                if (inBand.Count > 0 && inBand.All(l => l.Right <= active)) oneSided = PostSide.Left;
                else if (inBand.Count > 0 && inBand.All(l => l.Left >= active)) oneSided = PostSide.Right;
                if (oneSided != PostSide.Full) candidate = active;
            }

            gutter ??= candidate;

            if (candidate is { } g)
            {
                // One block per ROW PER COLUMN, each cropped to its own half. Cropping
                // horizontally is what lets the left column build line by line on its own â€”
                // a full-width slice would drag the right column's words in with it.
                //
                // Consecutive split bands share ONE group: a meme captioned above AND below its
                // photos is a single comparison, and giving each band its own id put the reading
                // order back to heading/heading/punchline/punchline.
                if (!prevSplit) currentGroup = nextGroup++;
                var group = currentGroup;
                var any = false;
                foreach (var (side, x0, x1) in new[]
                         {
                             (PostSide.Left, 0, g),
                             (PostSide.Right, g, w),
                         })
                {
                    var colLines = lines
                        .Where(l => l.Top >= band[0].Top - 2 && l.Bottom <= band[^1].Bottom + 2)
                        .Where(l => side == PostSide.Left ? l.Right <= g : l.Left >= g)
                        .ToList();
                    if (colLines.Count == 0) continue;

                    any = true;
                    foreach (var row in MergeIntoRows(colLines))
                        blocks.Add(row with
                        {
                            Side = side,
                            XLeft = x0,
                            XRight = x1,
                            Group = group,
                            Segments = new[] { new PostSegment(row.Text ?? "", side) },
                        });
                }
                prevSplit = any;
            }
            else
            {
                blocks.AddRange(band.Select(r => r with
                {
                    Segments = new[] { new PostSegment(r.Text ?? "", PostSide.Full) },
                }));
                prevSplit = false;
            }
            cursor = band[^1].Bottom;
        }
        if (h - cursor >= gapLimit) blocks.Add(new PostBlock(cursor, h, null, true));

        // With columns, a picture's extent is found WITHIN ITS OWN COLUMN. Measuring text-free
        // gaps across the full width ended the top picture at the row where the OTHER column's
        // caption began â€” so it came out sliced off at 96px with blank space under it. Each
        // column is walked separately, which finds each picture whole.
        //
        // This is not the same as cutting a picture down the gutter: nothing is halved here,
        // each one is simply measured in the column it actually occupies.
        if (gutter is { } gx)
        {
            var group = blocks.FirstOrDefault(b => b.Group is not null)?.Group;
            blocks.RemoveAll(b => b.IsMedia);

            // LEFT first, then RIGHT â€” the right column starts where the left column's pictures
            // actually end, not at the text gutter. Otherwise the right column's empty strips
            // overlap the left column's photos, fail the "is this blank?" test because a photo
            // is in them, and get drawn straight over those photos.
            var leftContentEdge = gx;

            foreach (var side in new[] { PostSide.Left, PostSide.Right })
            {
                var x0 = side == PostSide.Left ? 0 : Math.Max(gx, leftContentEdge);
                var x1 = side == PostSide.Left ? gx : w;

                // Full-width text belongs to BOTH columns and must stop the walk in each. A
                // heading spanning the post — "THE SQUIDHOOD SPECTRUM" — is not in either
                // column's own list, so the search for a picture ran straight over it and the
                // picture swallowed the title, drawing those words a second time.
                var colText = blocks
                    .Where(b => !b.IsMedia && (b.Side == side || b.Side == PostSide.Full))
                    .OrderBy(b => b.Top).ToList();

                var at = 0;
                foreach (var t in colText.Append(new PostBlock(h, h, null, false)))
                {
                    if (t.Top - at >= gapLimit && !IsBlank(bmp, at, t.Top, background, x0, x1))
                    {
                        // The picture's WIDTH comes from the picture, not from the text's
                        // gutter. That gutter is only where no line of text crosses, and a
                        // photo happily runs past it â€” clipping to it sheared this meme's
                        // portraits off down their right-hand side.
                        var (px0, px1) = ContentExtent(
                            bmp, at, t.Top, background, side, x0, x1, w,
                            side == PostSide.Right ? x0 : 0);
                        if (side == PostSide.Left) leftContentEdge = Math.Max(leftContentEdge, px1);
                        blocks.Add(new PostBlock(at, t.Top, null, true)
                        {
                            XLeft = px0, XRight = px1, Side = side, Group = group,
                        });
                    }
                    at = Math.Max(at, t.Bottom);
                }
            }
        }

        // A picture may reach PAST the text gutter, so the far column has to begin where that
        // picture actually ends. Without this the right column's slices still started at the
        // gutter and carried a strip of the left column's photo along with them — the same
        // pixels drawn twice, which is what tore pictures apart before.
        if (gutter is not null)
        {
            var leftEdge = blocks.Where(b => b.Side == PostSide.Left)
                .Select(b => b.XRight ?? 0)
                .DefaultIfEmpty(0)
                .Max();
            if (leftEdge > 0)
                blocks = blocks.Select(b =>
                    b.Side == PostSide.Right && (b.XLeft ?? 0) < leftEdge
                        ? b with { XLeft = leftEdge }
                        : b).ToList();
        }

        // Drop "pictures" that are just padding. Generous whitespace between the byline and the
        // caption reads as a text-free stretch, and since pictures get moved to the end of the
        // stack an empty one would tack a blank strip onto the bottom of the card.
        blocks.RemoveAll(b => b.IsMedia && IsBlank(bmp, b.Top, b.Bottom, background));

        // The first text row is the post's byline (username, or "Anonymous" and a post number).
        // It stays on screen from the first frame instead of being read out.
        var headerBottom = blocks.FirstOrDefault(b => !b.IsMedia)?.Bottom ?? 0;

        // A round avatar hangs below the byline text, so the rows under it read as a text-free
        // stretch. It is part of the header, not a photo â€” absorb any short picture block that
        // sits directly beneath it, while leaving a real photo (many lines tall) alone.
        var headerIndex = blocks.FindIndex(b => !b.IsMedia);
        var absorbedAvatar = false;
        while (headerIndex >= 0 && headerIndex + 1 < blocks.Count
               && blocks[headerIndex + 1].IsMedia
               && blocks[headerIndex + 1].Height < typical * 3)
        {
            headerBottom = blocks[headerIndex + 1].Bottom;
            blocks.RemoveAt(headerIndex + 1);
            absorbedAvatar = true;
        }

        // A byline has to LOOK like one â€” a post number, a datestamp, a "Follow" â€” or have an
        // avatar tucked beside it. "Sits on one line above a picture" is not enough on its own:
        // a meme's opening caption does exactly that, and treating it as chrome left it on
        // screen unread. That silently ate "Tree. Paper making process. Cut tree in factory".
        var headerText = headerIndex >= 0 ? blocks[headerIndex].Text ?? "" : "";
        var readsLikeByline = BylineLike.IsMatch(headerText);

        // A social byline is a display name with an @handle beneath it. Neither line looks like
        // chrome on its own, so both were being read out â€” the post opened by announcing
        // "Davie, at DavidLeeroa" before ever reaching the actual joke. The handle underneath
        // is what makes the pair unmistakable, and both belong to the header.
        var below = headerIndex >= 0 && headerIndex + 1 < blocks.Count ? blocks[headerIndex + 1] : null;
        if (below is { IsMedia: false } && HandleLine.IsMatch(below.Text ?? ""))
        {
            readsLikeByline = true;
            headerBottom = below.Bottom;
        }

        // And a byline is ONE line â€” never a caption that runs to several rows.
        var headerIsSingleRow = headerIndex >= 0 && blocks[headerIndex].Height <= typical * 1.6;
        if (!headerIsSingleRow || (!absorbedAvatar && !readsLikeByline)) headerBottom = 0;

        // Attachment filename/size lines are dropped outright â€” not read and not shown. This
        // happens AFTER the picture gaps are worked out, so removing them cannot make the photo
        // above them swallow the text below.
        var skipped = blocks.RemoveAll(b => !b.IsMedia && b.Top >= headerBottom && IsFileMetadata(b.Text));
        if (skipped > 0)
            _log.LogInformation("Skipped {N} attachment metadata line(s) in {File}.",
                skipped, Path.GetFileName(imagePath));

        _log.LogInformation(
            "Sliced {File}: {W}x{H}, {Lines} caption line(s), {Media} picture block(s), header ends {Header}.",
            Path.GetFileName(imagePath), w, h,
            blocks.Count(b => !b.IsMedia && b.Top >= headerBottom), blocks.Count(b => b.IsMedia), headerBottom);

        var groupsBefore = blocks.Where(b => b.Group is not null).Select(b => b.Group).Distinct().Count();
        blocks = RejoinSplitPictures(blocks, w);
        blocks = AbandonSliverColumns(blocks, w);
        var groupsAfter = blocks.Where(b => b.Group is not null).Select(b => b.Group).Distinct().Count();
        if (groupsBefore != groupsAfter)
        {
            _log.LogInformation("Column split: {Before} group(s) found, {After} kept — discarded by {Why}.",
                groupsBefore, groupsAfter,
                string.IsNullOrEmpty(DoomReasons) ? "(rejoin)" : DoomReasons);
        }
        else if (groupsBefore > 0)
        {
            _log.LogInformation("Column split: {N} comparison group(s) kept.", groupsBefore);
        }
        blocks = SeparateOverlaps(blocks);

        return new PostBands(w, h, blocks, headerBottom, background,
            blocks.Any(b => b.Segments.Any(s => s.Side != PostSide.Full)) ? gutter : null);
    }

    /// <summary>
    /// Throw away a column split that produced a column too narrow to be one.
    ///
    /// The gutter is the first vertical line in the middle 40% of the post that no text crosses.
    /// On a FOUR column meme that lands between columns one and two, and the "right column" then
    /// gets clamped to whatever scraps of text OCR found near the far edge — measured at FOUR
    /// pixels wide on a 719px post. Everything between the two columns then belonged to no slice
    /// and rendered as a black block for the whole video.
    ///
    /// Falling back to a plain full-width reveal loses the side-by-side effect, but a plain
    /// correct video beats a broken one, and it degrades on layouts nobody has taught it yet
    /// rather than producing something unwatchable.
    /// </summary>
    /// <summary>
    /// Put back together a single picture that the column split cut in half.
    ///
    /// The gutter is found from the rows where TEXT sits. When a post ends in a two-panel
    /// comparison, that gutter is real for the captions at the bottom — and then gets applied to
    /// the big picture above them, which spans the whole post. The picture became a left half
    /// and a right half covering the same rows, neither ever drawn as one, leaving a tall blank
    /// void through the middle of the card.
    ///
    /// Two media slices on opposite sides of the same group, covering essentially the same rows
    /// and together spanning the post, are one picture. Rejoin them and take them out of the
    /// comparison, leaving the group to the captions that actually justified the split.
    /// </summary>
    private static List<PostBlock> RejoinSplitPictures(List<PostBlock> blocks, int width)
    {
        var result = new List<PostBlock>(blocks);
        foreach (var group in blocks.Where(b => b.Group is not null).Select(b => b.Group).Distinct().ToList())
        {
            var media = result.Where(b => b.Group == group && b.IsMedia).ToList();
            var left = media.Where(b => b.Side == PostSide.Left).ToList();
            var right = media.Where(b => b.Side == PostSide.Right).ToList();
            if (left.Count == 0 || right.Count == 0) continue;

            foreach (var l in left)
            {
                var r = right.FirstOrDefault(x =>
                {
                    var overlap = Math.Min(l.Bottom, x.Bottom) - Math.Max(l.Top, x.Top);
                    var shorter = Math.Min(l.Height, x.Height);
                    return shorter > 0 && overlap > shorter * 0.8;
                });
                if (r is null) continue;

                var x0 = Math.Min(l.XLeft ?? 0, r.XLeft ?? 0);
                var x1 = Math.Max(l.XRight ?? width, r.XRight ?? width);
                if (x1 - x0 < width * 0.9) continue;          // not actually one full-width picture

                // The rejoined picture must contain NO text. Without this the two halves of a
                // threaded conversation — avatars down the left, replies down the right — looked
                // like one picture and merged into a single block swallowing every line of text
                // inside it, which put 974px overlaps back into ten of the saved screenshots.
                var top = Math.Min(l.Top, r.Top);
                var bottom = Math.Max(l.Bottom, r.Bottom);
                if (result.Any(b => !b.IsMedia && b.Bottom > top && b.Top < bottom)) continue;

                result.Remove(l);
                result.Remove(r);
                right.Remove(r);
                result.Add(new PostBlock(top, bottom, null, true)
                {
                    Side = PostSide.Full,
                    XLeft = null,
                    XRight = null,
                    Group = null,
                });
            }
        }
        return result.OrderBy(b => b.Top).ThenBy(b => b.IsMedia).ToList();
    }

    /// <summary>Why the last call discarded a column split — for the log, not for logic.</summary>
    private static string DoomReasons = "";

    private static List<PostBlock> AbandonSliverColumns(List<PostBlock> blocks, int width)
    {
        // A column narrower than a tenth of the post is not a column.
        //
        // TEXT only. Counting media here was added to catch a chat thread that split with a 3px
        // picture column — but the reworked gutter scorer rejects that split at source now, and
        // including media instead condemned a legitimate two-panel meme whose photo columns the
        // extent measurement had clipped. The scorer is the right place for that judgement.
        var slivers = blocks
            .Where(b => b.Group is not null && !b.IsMedia)
            .GroupBy(b => (b.Group, b.Side))
            .Where(g => g.Max(b => Math.Min(width, b.XRight ?? width))
                      - g.Min(b => Math.Max(0, b.XLeft ?? 0)) < width * 0.12)
            .Select(g => g.Key.Group);

        // Neither is a "comparison" with only one side to it. The expanding-brain meme split a
        // left text column away and then found nothing at all on the right, so the entire right
        // half of the post — every one of the brain pictures — belonged to no slice and rendered
        // black for the whole video. One column is not a comparison; it is a failed split.
        var oneSided = blocks
            .Where(b => b.Group is not null)
            .GroupBy(b => b.Group)
            .Where(g => g.Select(b => b.Side).Distinct().Count() < 2)
            .Select(g => g.Key);

        // And neither is a pair of columns whose text sits at completely different HEIGHTS.
        // A real comparison reads across: both columns carry text on roughly the same rows. On
        // one Instagram thread the left column held text on rows 160-287, 604-660 and 715-735
        // while the right column held a single row band at 667-681 — no overlap whatsoever — so
        // for nearly every row the right third of the post belonged to nothing and rendered as
        // black patches beside the text.
        var misaligned = blocks
            .Where(b => b.Group is not null && !b.IsMedia)
            .GroupBy(b => b.Group)
            .Where(g =>
            {
                var rows = new Dictionary<PostSide, HashSet<int>>();
                foreach (var b in g)
                {
                    if (!rows.TryGetValue(b.Side, out var set)) rows[b.Side] = set = new HashSet<int>();
                    for (int y = b.Top; y < b.Bottom; y++) set.Add(y);
                }
                if (!rows.TryGetValue(PostSide.Left, out var l) || !rows.TryGetValue(PostSide.Right, out var r))
                    return false;                      // handled by oneSided
                var both = l.Count < r.Count ? l.Count(y => r.Contains(y)) : r.Count(y => l.Contains(y));
                var union = l.Count + r.Count - both;
                return union > 0 && (double)both / union < 0.40;
            })
            .Select(g => g.Key);

        var slv = slivers.ToList(); var one = oneSided.ToList(); var mis = misaligned.ToList();
        DoomReasons = string.Join(", ",
            new[] { slv.Count > 0 ? $"sliver:{slv.Count}" : null,
                    one.Count > 0 ? $"one-sided:{one.Count}" : null,
                    mis.Count > 0 ? $"misaligned:{mis.Count}" : null }
            .Where(x => x is not null)!);
        var doomed = slv.Concat(one).Concat(mis).ToHashSet();
        if (doomed.Count == 0) return blocks;

        var kept = blocks.Where(b => b.Group is null || !doomed.Contains(b.Group)).ToList();

        foreach (var group in doomed)
        {
            // Side-by-side blocks share rows, so they cannot simply become full-width blocks —
            // they would sit on top of each other. Anything overlapping vertically MERGES into
            // one full-width slice, read left column first, which is how those rows would have
            // been read had the post never been treated as two columns.
            //
            // Text and pictures merge in the SAME pass. Merging them separately left a text
            // slice and a picture slice covering the same rows of the one full-width lane, which
            // is the same overlap defect in a new place.
            var side = blocks.Where(b => b.Group == group).OrderBy(b => b.Top).ToList();
            while (side.Count > 0)
            {
                var run = new List<PostBlock> { side[0] };
                int bottom = side[0].Bottom;
                side.RemoveAt(0);
                for (int i = 0; i < side.Count;)
                {
                    if (side[i].Top < bottom)
                    {
                        bottom = Math.Max(bottom, side[i].Bottom);
                        run.Add(side[i]);
                        side.RemoveAt(i);
                    }
                    else i++;
                }

                var ordered = run.OrderBy(b => b.XLeft ?? 0).ThenBy(b => b.Top).ToList();
                var text = string.Join(" ", ordered.Where(b => !b.IsMedia)
                                                   .Select(b => b.Text)
                                                   .Where(t => !string.IsNullOrWhiteSpace(t)));
                var segments = ordered.Where(b => !b.IsMedia)
                                      .SelectMany(b => b.Segments)
                                      .Select(sg => sg with { Side = PostSide.Full })
                                      .ToList();

                kept.Add(new PostBlock(
                    run.Min(b => b.Top), bottom,
                    segments.Count > 0 ? text : null,
                    segments.Count == 0)
                {
                    Side = PostSide.Full,
                    XLeft = null,
                    XRight = null,
                    Group = null,
                    Segments = segments,
                });
            }
        }

        return kept.OrderBy(b => b.Top).ThenBy(b => b.IsMedia).ToList();
    }

    /// <summary>
    /// Push apart slices in the same column that share rows.
    ///
    /// OCR line boxes overlap slightly wherever a descender on one row reaches into the next,
    /// and those boxes become slice bounds directly — so consecutive slices could claim the
    /// same 7px of the picture. Both then draw it, and the sliver of the previous line reappears
    /// under the next one. Measured across 103 saved screenshots this hit 11 of them, always by
    /// a handful of pixels. The boundary is moved to the midpoint of the disputed strip, so
    /// neither slice loses more than half of it.
    /// </summary>
    private static List<PostBlock> SeparateOverlaps(List<PostBlock> blocks)
    {
        foreach (var lane in blocks.GroupBy(b => (b.Group, b.Side)))
        {
            var ordered = lane.OrderBy(b => b.Top).ToList();
            for (int i = 1; i < ordered.Count; i++)
            {
                var prev = ordered[i - 1];
                var cur = ordered[i];
                if (prev.Bottom <= cur.Top) continue;

                var mid = (prev.Bottom + cur.Top) / 2;
                // Never invert a slice: a badly nested pair is left for the validator to report
                // rather than silently squashed into something with no height.
                if (mid <= prev.Top || mid >= cur.Bottom) continue;

                blocks[blocks.IndexOf(prev)] = prev with { Bottom = mid };
                blocks[blocks.IndexOf(cur)] = cur with { Top = mid };
                ordered[i - 1] = prev with { Bottom = mid };
                ordered[i] = cur with { Top = mid };
            }
        }
        return blocks;
    }

    private static List<PostBlock> MergeIntoRows(IReadOnlyList<OcrLineBox> ocrLines)
    {
        var rows = new List<PostBlock>();
        foreach (var line in ocrLines.OrderBy(l => l.Top))
        {
            var last = rows.Count > 0 ? rows[^1] : null;
            if (last is not null && line.Top < last.Bottom - Math.Min(8, last.Height / 3))
            {
                rows[^1] = last with
                {
                    Bottom = Math.Max(last.Bottom, line.Bottom),
                    Text = $"{last.Text} {line.Text}".Trim(),
                };
            }
            else
            {
                rows.Add(new PostBlock(line.Top, line.Bottom, line.Text, false));
            }
        }
        return rows;
    }

    /// <summary>Runs of rows with no picture-sized gap between them â€” one paragraph-ish group.</summary>
    private static List<List<PostBlock>> GroupIntoBands(List<PostBlock> rows, int gapLimit)
    {
        var bands = new List<List<PostBlock>>();
        foreach (var row in rows)
        {
            if (bands.Count == 0 || row.Top - bands[^1][^1].Bottom >= gapLimit)
                bands.Add(new List<PostBlock>());
            bands[^1].Add(row);
        }
        return bands;
    }

    /// <summary>
    /// The x of a vertical gutter that no line in THIS BAND crosses, or null when the band is a
    /// single column. The band must be at least two rows tall: a byline with a timestamp off to
    /// the right is one row holding two boxes, and splitting that into "columns" would tear the
    /// author away from the date.
    /// </summary>
    ///
    /// A SCORING version of this was tried and reverted (see below). It ranked every candidate
    /// gap on whether both sides carried text, neither side was a sliver, and the two sides' text
    /// sat at similar heights — which correctly rejected the four-column meme, the expanding
    /// brain, the split picture and the $1M thread. It could NOT separate a chat screenshot from
    /// a real comparison, because the two are geometrically identical: text left, text right,
    /// similar heights, a gap down the middle. It also pushed Warn from 8 to 17 across the saved
    /// corpus (fat-line 3 -> 12) by merging more content into tall single slices.
    /// Doing this properly needs a signal geometry does not carry — message bubbles, reading
    /// order — not a better threshold. The downstream guards (AbandonSliverColumns,
    /// RejoinSplitPictures, the 2-D hole check) currently do the rejecting instead.
    private static int? FindColumnGutter(IReadOnlyList<OcrLineBox> lines, List<PostBlock> band, int width,
        Bitmap? bmp = null, string? background = null)
    {
        if (band.Count < 2) return null;

        int top = band[0].Top, bottom = band[^1].Bottom;
        var inBand = lines.Where(l => l.Top >= top - 2 && l.Bottom <= bottom + 2).ToList();
        if (inBand.Count < 3) return null;

        // Only look through the middle of the post; a margin gap is not a gutter.
        for (int x = (int)(width * 0.30); x <= (int)(width * 0.70); x += 4)
        {
            if (inBand.Any(l => l.Left < x && l.Right > x)) continue;

            var textLeft = inBand.Any(l => l.Right <= x);
            var textRight = inBand.Any(l => l.Left >= x);
            if (textLeft && textRight) return x;

            // A column with a PICTURE and no words in it is still a column. This meme's right
            // half is nothing but photographs — once its one scrap of text was dropped as OCR
            // noise there was no text there at all, the post stopped counting as two columns,
            // and full-width slices went back to cutting those photographs apart.
            if (bmp is null || background is null) continue;
            if (textLeft && !IsBlank(bmp, top, bottom, background, x, width)) return x;
            if (textRight && !IsBlank(bmp, top, bottom, background, 0, x)) return x;
        }
        return null;
    }

    /// <summary>
    /// This band's words as one segment per column â€” read down the left, then down the right.
    /// Null when the band does not actually straddle the gutter (a full-width heading sitting
    /// above two columns).
    /// </summary>
    private static IReadOnlyList<PostSegment>? SplitByGutter(
        IReadOnlyList<OcrLineBox> lines, List<PostBlock> band, int gutter)
    {
        int top = band[0].Top, bottom = band[^1].Bottom;
        var inBand = lines.Where(l => l.Top >= top - 2 && l.Bottom <= bottom + 2).ToList();

        var left = inBand.Where(l => l.Right <= gutter).OrderBy(l => l.Top).ToList();
        var right = inBand.Where(l => l.Left >= gutter).OrderBy(l => l.Top).ToList();
        if (left.Count == 0 || right.Count == 0) return null;

        var leftText = string.Join(" ", left.Select(l => l.Text)).Trim();
        var rightText = string.Join(" ", right.Select(l => l.Text)).Trim();

        // A stop after the first half, or the voice runs one caption straight into the other.
        if (leftText.Length > 0 && !".!?:".Contains(leftText[^1])) leftText += ".";

        return new[]
        {
            new PostSegment(leftText, PostSide.Left),
            new PostSegment(rightText, PostSide.Right),
        };
    }

    /// <summary>
    /// Is this line WRITTEN ON A PHOTOGRAPH rather than on the post's own background?
    ///
    /// A caption typed into a post sits on the card's flat background, so most of the pixels
    /// around its letters are that colour. A caption burned into a photo â€” "i sleep" across a
    /// picture of a sleeping man â€” sits on the photo instead, and OCR reads it badly: that one
    /// came back as "Q rsleep", which the voice then spelled out as the last thing in the video.
    /// It is part of the picture, so it is left to be seen rather than read.
    /// </summary>
    private static bool SitsOnAPhoto(Bitmap bmp, OcrLineBox line, string backgroundHex)
    {
        // Only ever discards a SHORT fragment. Judging the surface under a line is inexact â€”
        // closely-set lines put their neighbour's letters into the sample â€” and when it misfired
        // on a real caption it cost whole sentences of the post. A label burned onto a photo is
        // a word or two ("i sleep", "real shit"); anything longer is left alone whatever the
        // measurement says, so the worst case is reading two stray words rather than losing a
        // line of the joke.
        if (line.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 3) return false;

        var x0 = Math.Clamp(line.Left, 0, bmp.Width - 1);
        var x1 = Math.Clamp(line.Right, x0 + 1, bmp.Width);
        var y0 = Math.Clamp(line.Top, 0, bmp.Height - 1);
        var y1 = Math.Clamp(line.Bottom, y0 + 1, bmp.Height);

        // Sample the surface just ABOVE and BELOW the line, never the line itself. Measuring
        // inside the box mixes the letters in with what they sit on, and the threshold that
        // then caught "i sleep" on its photo also threw away plain greentext on a plain card.
        // The strips either side hold no letters, so they show the surface directly.
        var histogram = new Dictionary<int, int>();
        var total = 0;
        foreach (var band in new[] { (y0 - 7, y0 - 2), (y1 + 2, y1 + 7) })
        {
            for (int y = band.Item1; y < band.Item2; y++)
            {
                if (y < 0 || y >= bmp.Height) continue;
                for (int x = x0; x < x1; x += 2)
                {
                    var p = bmp.GetPixel(x, y);
                    var key = (p.R / 24 << 8) | (p.G / 24 << 4) | (p.B / 24);
                    histogram[key] = histogram.GetValueOrDefault(key) + 1;
                    total++;
                }
            }
        }
        if (total < 20) return false;

        // A flat surface â€” card, speech bubble, caption box â€” is overwhelmingly one colour.
        // A photograph never is.
        return histogram.Values.Max() / (double)total < 0.55;
    }

    /// <summary>
    /// How far a picture actually reaches across, measured from its own pixels.
    ///
    /// The gutter only says where no LINE OF TEXT crosses; a photo beside a column of text
    /// routinely runs past it. Cropping to the gutter therefore sheared the portraits in the
    /// 2015/2026 meme down their right-hand side, leaving grey steps over them. Growing outward
    /// from the column until the background takes over finds the picture's real edge instead.
    ///
    /// The search never crosses into the far side's TEXT, so a picture can widen into the empty
    /// space beside a caption without swallowing the caption itself.
    /// </summary>
    private static (int Left, int Right) ContentExtent(
        Bitmap bmp, int top, int bottom, string backgroundHex,
        PostSide side, int x0, int x1, int width, int leftLimit)
    {
        const int step = 6;
        var left = x0;
        var right = x1;

        if (side == PostSide.Left)
        {
            while (right + step < width && !IsBlank(bmp, top, bottom, backgroundHex, right, right + step))
                right += step;
        }
        else
        {
            // Never back past where the left column's own pictures end.
            while (left - step > leftLimit && !IsBlank(bmp, top, bottom, backgroundHex, left - step, left))
                left -= step;
        }

        return (Math.Max(0, left), Math.Min(width, right));
    }

    /// <summary>
    /// Is this region almost entirely the card's background colour? The x bounds matter on a
    /// two-column post: the gap beside a picture is empty in ITS column while the other column
    /// is full of words.
    /// </summary>
    private static bool IsBlank(Bitmap bmp, int top, int bottom, string backgroundHex,
        int? xLeft = null, int? xRight = null)
    {
        int br = Convert.ToInt32(backgroundHex.Substring(1, 2), 16);
        int bg = Convert.ToInt32(backgroundHex.Substring(3, 2), 16);
        int bb = Convert.ToInt32(backgroundHex.Substring(5, 2), 16);

        top = Math.Max(0, top);
        bottom = Math.Min(bmp.Height, bottom);
        if (bottom <= top) return true;

        var x0 = Math.Clamp(xLeft ?? 0, 0, bmp.Width - 1);
        var x1 = Math.Clamp(xRight ?? bmp.Width, x0 + 1, bmp.Width);

        var rect = new Rectangle(x0, top, x1 - x0, bottom - top);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        // Copy ROW BY ROW into a packed buffer, never Stride * Height in one go.
        //
        // Locking a SUB-RECTANGLE hands back a Scan0 pointing into the middle of the bitmap
        // with the FULL bitmap's stride, so only Width*4 bytes of the final row belong to us.
        // Asking for Stride * Height read past the end of the allocation, and on a screenshot
        // whose slice reached the bottom-right that killed the entire process with
        // "Fatal error. Internal CLR error. (0x80131506)" — no exception, no failed job, the
        // whole API gone. Every other LockBits here takes the full bitmap, where the two are
        // the same; this one is the exception.
        int rowBytes = rect.Width * 4;
        var bytes = new byte[(long)rowBytes * rect.Height];
        try
        {
            for (int y = 0; y < rect.Height; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), bytes, y * rowBytes, rowBytes);
        }
        finally { bmp.UnlockBits(data); }

        long same = 0, total = 0;
        for (int y = 0; y < rect.Height; y += 2)
        {
            int row = y * rowBytes;
            for (int x = 0; x < rect.Width; x += 3)
            {
                int i = row + x * 4;
                if (Math.Abs(bytes[i + 2] - br) + Math.Abs(bytes[i + 1] - bg) + Math.Abs(bytes[i] - bb) < 60)
                    same++;
                total++;
            }
        }
        return total > 0 && (double)same / total > 0.95;
    }

    /// <summary>
    /// A coarse map of where the picture actually has content, one cell per <paramref name="step"/>
    /// pixels. Used to tell a real hole in the slice plan from ordinary blank margin: a stretch
    /// no slice covers only matters if there was something there to show.
    /// </summary>
    public static bool[,] InkGrid(string imagePath, string backgroundHex, int step)
    {
        using var bmp = new Bitmap(imagePath);
        int br = Convert.ToInt32(backgroundHex.Substring(1, 2), 16);
        int bg = Convert.ToInt32(backgroundHex.Substring(3, 2), 16);
        int bb = Convert.ToInt32(backgroundHex.Substring(5, 2), 16);

        int gw = Math.Max(1, bmp.Width / step), gh = Math.Max(1, bmp.Height / step);
        var grid = new bool[gw, gh];

        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bytes = new byte[data.Stride * bmp.Height];
        try { Marshal.Copy(data.Scan0, bytes, 0, bytes.Length); }
        finally { bmp.UnlockBits(data); }

        for (int y = 0; y < bmp.Height; y++)
        {
            int row = y * data.Stride, gy = y / step;
            if (gy >= gh) break;
            for (int x = 0; x < bmp.Width; x++)
            {
                int gx = x / step;
                if (gx >= gw) break;
                if (grid[gx, gy]) continue;
                int i = row + x * 4;
                if (Math.Abs(bytes[i + 2] - br) + Math.Abs(bytes[i + 1] - bg) + Math.Abs(bytes[i] - bb) >= 60)
                    grid[gx, gy] = true;
            }
        }
        return grid;
    }

    private static string ModalColour(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bytes = new byte[data.Stride * h];
        try { Marshal.Copy(data.Scan0, bytes, 0, bytes.Length); }
        finally { bmp.UnlockBits(data); }

        var histogram = new Dictionary<int, int>();
        for (int y = 0; y < h; y += 2)
        {
            int row = y * data.Stride;
            for (int x = 0; x < w; x += 3)
            {
                int i = row + x * 4;
                int key = (bytes[i + 2] / 8 << 12) | (bytes[i + 1] / 8 << 6) | (bytes[i] / 8);
                histogram[key] = histogram.GetValueOrDefault(key) + 1;
            }
        }
        var modal = histogram.MaxBy(kv => kv.Value).Key;
        return $"#{(modal >> 12) * 8:x2}{((modal >> 6) & 63) * 8:x2}{(modal & 63) * 8:x2}";
    }
}

