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
        @"^[^\p{L}\p{Nd}]*(tweet|retweet(ed)?|reply|replies|repost(ed)?|share[ds]?|follow(ing|ers)?|gif|video|photo|album|verified|promoted|sponsored|translate post|show (more|this thread)|imessage|sms|mms|facetime|messages|messenger|whatsapp|telegram|snapchat)[^\p{L}\p{Nd}]*$",
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
    // Segments are LENGTH-CAPPED because a caption whose spaces OCR lost is one long token, and
    // one hyphen anywhere in it then made it a username. "INSTALL THE AI DIRECTLY INTO THE WI-FI"
    // came back as "INSTAUTBEAIDIRECTLYIHTOTHEWI-R" - the hyphen is from WI-FI - and was dropped
    // as chrome, which left a blank strip in the card where the manager's line should be AND cost
    // the typed text a slice to align to, so the voice ran two panels' captions together. Real
    // handles are short: "the-lad-bible", "u/spez", "cool_guy_92".
    private static readonly Regex UsernameSlugLine = new(
        @"^\s*[a-z0-9]{1,15}(?:[-_][a-z0-9]{1,15}){1,3}\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A month and day with no year: "June 30th", "Jun 3". Every message in a
    /// screenshotted chat carries one, and read aloud they bury the conversation.</summary>
    private static readonly Regex ShortDateLine = new(
        @"^[^\p{L}\p{Nd}]*(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\s*\d{1,2}(st|nd|rd|th)?[^\p{L}\p{Nd}]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Chat and mail app furniture: "Completed", "Details", "Delivered", "Read".</summary>
    //
    // The clock time has to be allowed AFTER a word as well as before one. It was only in the
    // opening alternative, so "Delivered" matched and "Today 1:01 PM" did not - and an iMessage
    // screenshot opened by reading its own timestamp aloud while the card revealed the phone's
    // title bar. Each alternative still has to consume a WHOLE word, so "Read this now" and
    // "Sent from my iPhone" are untouched.
    private const string ChromeWord =
        @"(?:completed|details|delivered|read|sent|seen|today|yesterday|now|edited|forwarded|\d{1,2}:\d{2}(?: ?[ap]\.?m\.?)?)";

    private static readonly Regex AppChromeLine = new(
        @"^[^\p{L}\p{Nd}]*" + ChromeWord + @"(?:\s+" + ChromeWord + @")*[^\p{L}\p{Nd}]*$",
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

    /// <summary>
    /// A watermark: a bare domain on its own line. "gflip.com" sat under a meme and was read out
    /// as part of the joke. Whole-line only, so a caption that mentions a site survives.
    /// </summary>
    private static readonly Regex WatermarkLine = new(
        @"^[^\p{L}\p{Nd}]*(?:https?://)?(?:www\.)?[a-z0-9][a-z0-9\-]{1,30}\.(?:com|net|org|io|co|gg|me|tv|ru|it)(?:/\S*)?[^\p{L}\p{Nd}]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsFileMetadata(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && (FileNameLine.IsMatch(text) || FileSizeLine.IsMatch(text) || BoardChromeLine.IsMatch(text)
            || VideoStatsLine.IsMatch(text) || DurationBadge.IsMatch(text)
            || WatermarkLine.IsMatch(text) || IsPostChrome(text));

    /// <summary>
    /// OCR reads DRAWINGS too. A cartoon alien's eyes come back as the characters "00", which
    /// the voice then dutifully says as "zero zero". Anything this short with no letters in it
    /// is picture noise, never words worth reading.
    /// </summary>
    /// <summary>
    /// Letters and digits only, lower-cased, single-spaced - enough to ask whether the typed post
    /// contains a row of OCR without punctuation or spacing getting in the way. OCR that mangles
    /// the row simply fails to match, which falls back to judging the row on its layout alone.
    /// </summary>
    private static string Normalise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

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

    /// <param name="allowColumns">
    /// False re-slices the post as one full-width stack. It is the fallback the pipeline drops to
    /// when a column split produces a card with rows that no slice shows - a plain line-by-line
    /// reveal is always safe, where a broken two-column layout is not.
    /// </param>
    public PostBands Analyze(string imagePath, IReadOnlyList<OcrLineBox> ocrLines,
        DiagramSliceService? diagrams = null, IReadOnlyList<string>? typedLines = null,
        bool allowColumns = true)
    {
        using var bmp = new Bitmap(imagePath);
        int w = bmp.Width, h = bmp.Height;
        var background = ModalColour(bmp);

        // A flow diagram reads in arrow order, which no amount of band slicing can recover â€”
        // the trees diagram's bottom row runs right to left. It only claims images with several
        // arrows in them, so everything else falls through to the usual path.
        // A COMIC read as a post can only ever look wrong: its panels are read one after
        // another, not stacked as rows of text. Checked before the flow diagram because a grid
        // has no arrows and would otherwise fall through to band slicing.
        if (TryComicGrid(bmp, ocrLines, background) is { } grid) return grid;

        if (diagrams?.TryAnalyze(imagePath, ocrLines, background, typedLines) is { } diagram)
            return diagram;

        // Throw out picture noise and attachment metadata BEFORE anything is grouped. Doing it
        // afterwards was too late: "283 KB PNG" sits level with the first line of the post, so
        // the two merged into one row and the combined text no longer looked like metadata â€”
        // the size got read out as part of the joke.
        var lines = ocrLines.Where(l => !IsNoise(l.Text) && !IsFileMetadata(l.Text)).ToList();

        // Where the CHROME was. Dropping it from the text is only half the job: the rows it
        // occupied then read as a text-free stretch, became a picture block, and stayed on
        // screen — an iMessage screenshot still showed "iMessage" and "Today 1:01 PM" under the
        // contact's name while the voice, correctly, said neither. Those rows are cut out of the
        // card further down so the reveal goes straight from the name to the first message.
        var chromeRows = ocrLines
            .Where(l => IsFileMetadata(l.Text))
            .Select(l => (Top: l.Top - 2, Bottom: l.Bottom + 2))
            .ToList();

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
        List<int>? gutters = null;

        // Walk top to bottom, turning every sizeable text-free stretch into a picture block.
        var blocks = new List<PostBlock>();
        int cursor = 0;
        int currentGroup = 0;
        bool prevSplit = false;
        // Each comparison gets ONE gutter shared by all its bands, found by intersecting what
        // every band allows. Letting each band choose its own put the columns at 306px on the
        // heading row and 330px on the punchline row, so the card's centre seam zig-zagged.
        var bandList = GroupIntoBands(rows, gapLimit);
        var groupGutter = new Dictionary<int, List<int>>();

        // Which comparison each band belongs to, decided HERE so the walk below cannot arrive at
        // a different answer - it used to re-derive the id from whether the previous band split,
        // which is only the same thing while every splitting band agrees on a gutter.
        var bandGroup = new Dictionary<int, int>();

        // Where the post splits by WORDS anywhere, that is the split it means, and a gutter
        // justified only by a PICTURE on one side may no longer open a comparison of its own: a
        // comic panel with a character on the left and speech on the right offers exactly that
        // shape, and it opened a second comparison whose gutter contradicted the real one below.
        // Where NO band splits by words, that picture-justified gutter is all there is, and a
        // post whose other half is nothing but photographs still splits on it - demanding words
        // outright cost one meme its picture entirely.
        var anyTextGutter = allowColumns && bandList.Any(
            b => FindColumnGutters(lines, b, w, bmp, background, textBothSides: true).Count > 0);

        for (int i = 0, g0 = 0; i < bandList.Count; )
        {
            if (!allowColumns
                || FindColumnGutters(lines, bandList[i], w, bmp, background,
                                     textBothSides: anyTextGutter).Count == 0) { i++; continue; }

            // Bands share a gutter only while they AGREE on one. A band whose words sit to one
            // side of a PICTURE splits on its own - a comic panel with a character on the left
            // and speech on the right - and forcing it to share with the real comparison below
            // left the two with no gutter in common at all. The intersection came out empty, the
            // split collapsed to a single column, one-sided threw it away, and the punchline
            // ("There are 4 rules") was merged into the same full-width row as the setup it
            // answers and revealed at the same moment.
            int end = i + 1;
            var shared = FindColumnGutters(lines, bandList[i], w, bmp, background);
            while (end < bandList.Count
                   && FindColumnGutters(lines, bandList[end], w, bmp, background).Count > 0)
            {
                var combined = FindColumnGutters(
                    lines, bandList.GetRange(i, end - i + 1).SelectMany(b => b).ToList(),
                    w, bmp, background);
                if (combined.Count == 0) break;
                shared = combined;
                end++;
            }

            for (int b = i; b < end; b++) bandGroup[b] = g0;
            groupGutter[g0++] = shared;
            i = end;
        }

        for (int bandIndex = 0; bandIndex < bandList.Count; bandIndex++)
        {
            var band = bandList[bandIndex];
            if (band[0].Top - cursor >= gapLimit)
                blocks.Add(new PostBlock(cursor, band[0].Top, null, true));

            var splitsHere = bandGroup.TryGetValue(bandIndex, out var bandGroupId);
            var candidate = splitsHere ? groupGutter[bandGroupId] : null;

            // A band with words on ONE side only still belongs to that column. Only the first
            // half of this meme has text in both columns; treating the second half as full
            // width ran its slice straight across the picture beside it, tearing that picture
            // into two pieces with the caption wedged between them.
            if (candidate is null && prevSplit && gutters is { Count: > 0 })
            {
                var inBand = lines
                    .Where(l => l.Top >= band[0].Top - 2 && l.Bottom <= band[^1].Bottom + 2)
                    .ToList();
                // Words in ONE column only still belong to that column, so the band keeps the
                // comparison's gutters rather than going full width across its neighbour.
                if (inBand.Count > 0
                    && ColumnEdges(gutters, w).Any(c => inBand.All(l => l.Left >= c.X0 && l.Right <= c.X1)))
                    candidate = gutters;
            }

            gutters ??= candidate;

            if (candidate is { } g)
            {
                // One block per ROW PER COLUMN, each cropped to its own half. Cropping
                // horizontally is what lets the left column build line by line on its own â€”
                // a full-width slice would drag the right column's words in with it.
                //
                // Consecutive split bands share ONE group: a meme captioned above AND below its
                // photos is a single comparison, and giving each band its own id put the reading
                // order back to heading/heading/punchline/punchline.
                // A band that split on its own carries the id decided above; one that only
                // joined through the fallback belongs to the comparison already open.
                currentGroup = splitsHere ? bandGroupId : currentGroup;
                var group = currentGroup;
                var any = false;
                var edges = ColumnEdges(g, w);
                for (int c = 0; c < edges.Count; c++)
                {
                    var (x0, x1) = edges[c];
                    var side = SideOfColumn(c, edges.Count);
                    var colLines = lines
                        .Where(l => l.Top >= band[0].Top - 2 && l.Bottom <= band[^1].Bottom + 2)
                        .Where(l => l.Left >= x0 && l.Right <= x1)
                        .ToList();
                    if (colLines.Count == 0) continue;

                    any = true;
                    foreach (var row in MergeIntoRows(colLines))
                        blocks.Add(row with
                        {
                            Side = side,
                            Column = c,
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
        if (gutters is { Count: > 0 } gx)
        {
            var group = blocks.FirstOrDefault(b => b.Group is not null)?.Group;
            blocks.RemoveAll(b => b.IsMedia);

            // LEFT first, then RIGHT â€” the right column starts where the left column's pictures
            // actually end, not at the text gutter. Otherwise the right column's empty strips
            // overlap the left column's photos, fail the "is this blank?" test because a photo
            // is in them, and get drawn straight over those photos.
            // How far the columns already walked actually reach. A picture may run past the text
            // gutter, so the NEXT column starts where the previous column's pictures end.
            var contentEdge = gx[0];

            // Pictures that run the WHOLE width of the post belong to neither column - the
            // wojak pair above a two-caption meme, the wolves photo between a heading and its
            // two punchlines. Tracked here so the right column's walk does not claim them a
            // second time.
            var shared = new List<(int Top, int Bottom, int Column)>();

            var colEdges = ColumnEdges(gx, w);
            for (int c = 0; c < colEdges.Count; c++)
            {
                var side = SideOfColumn(c, colEdges.Count);
                var x0 = c == 0 ? 0 : Math.Max(colEdges[c].X0, contentEdge);
                var x1 = colEdges[c].X1;

                // Full-width text belongs to BOTH columns and must stop the walk in each. A
                // heading spanning the post — "THE SQUIDHOOD SPECTRUM" — is not in either
                // column's own list, so the search for a picture ran straight over it and the
                // picture swallowed the title, drawing those words a second time.
                var colText = blocks
                    .Where(b => !b.IsMedia && (b.Column == c || b.Column is null))
                    .OrderBy(b => b.Top).ToList();

                var at = 0;
                foreach (var t in colText.Append(new PostBlock(h, h, null, false)))
                {
                    if (t.Top - at >= gapLimit && !IsBlank(bmp, at, t.Top, background, x0, x1)
                        && !shared.Any(s => s.Top <= at && s.Bottom >= t.Top))
                    {
                        // The picture's WIDTH comes from the picture, not from the text's
                        // gutter. That gutter is only where no line of text crosses, and a
                        // photo happily runs past it - clipping to it sheared this meme's
                        // portraits off down their right-hand side.
                        var (px0, px1) = ContentExtent(
                            bmp, at, t.Top, background, side, x0, x1, w,
                            c > 0 ? x0 : 0);

                        // A picture may overflow its column - portraits run past the text gutter
                        // and clipping them to it shears their edges off. But that licence only
                        // holds for TWO columns. On a four-panel meme the four photographs sit
                        // side by side with only a thin dark seam between them, so the measure
                        // runs straight across all four and the first column claims the lot:
                        // 0..713 of a 719px post, leaving the other three columns 6px slivers.
                        if (colEdges.Count > 2)
                        {
                            px0 = Math.Max(px0, x0);
                            px1 = Math.Min(px1, x1);
                            if (px1 - px0 < 8) { at = Math.Max(at, t.Bottom); continue; }
                        }

                        // A picture spanning the WHOLE post is shared, not the left column's.
                        //
                        // This is what broke every comparison built as "one wide picture, two
                        // captions under it". The left column's walk measured that picture
                        // correctly as running the full width - and the rule below, that the
                        // right column begins where the left column's pictures end, then moved
                        // the right column's left edge to the far side of the post. The right
                        // column came out zero pixels wide, the sliver guard called the whole
                        // split a failure, and both captions were merged back into one
                        // full-width slice that revealed at once. Measured on "internet
                        // boycotts" and "two wolves": both reported sliver:1 while holding a
                        // perfectly good gutter.
                        // ...and only where the OTHER column has no words either. This walk
                        // only knows about its own column's text, so on a meme whose caption is
                        // written across the picture the gap between two left-column lines
                        // looks empty while the right column is full of words. Claiming that as
                        // a full-width picture drew a slice straight over them: overlapping
                        // slices, which the validator correctly calls Broken. Measured on the
                        // "math test" meme - a 534px picture on top of eight caption rows.
                        //
                        // A full-width picture is CUT AROUND the other column's text rather than
                        // losing its claim to them. This walk stops at its OWN column's lines, so
                        // a neighbour's caption a few rows higher left the picture overlapping it
                        // by 10px - enough to fail the test above, after which the picture stayed
                        // the left column's, was measured at 958 of 960px, and pushed the right
                        // column into a 2px sliver against the edge of the post ("3 rules", whose
                        // punchline sits 10 rows above the setup it answers).
                        //
                        // Cut around, not shortened TO the caption: trimming to the first line
                        // threw away everything below it, and 660px of one post - most of the
                        // picture - was left in no slice at all.
                        var segments = new List<(int Top, int Bottom)>();
                        if (px1 - px0 >= w * 0.85)
                        {
                            var y = at;
                            foreach (var tb in blocks
                                         .Where(b => !b.IsMedia && b.Bottom > at && b.Top < t.Top)
                                         .OrderBy(b => b.Top))
                            {
                                if (tb.Top - y >= gapLimit) segments.Add((y, tb.Top));
                                y = Math.Max(y, tb.Bottom);
                            }
                            if (t.Top - y >= gapLimit) segments.Add((y, t.Top));
                        }

                        if (segments.Count > 0)
                        {
                            foreach (var seg in segments)
                            {
                                shared.Add((seg.Top, seg.Bottom, c));
                                blocks.Add(new PostBlock(seg.Top, seg.Bottom, null, true)
                                {
                                    XLeft = px0, XRight = px1,
                                });
                            }
                        }
                        else
                        {
                            // Only a real overhang into the very next column moves the boundary.
                            // A picture measured right across the post is not an overhang, it is
                            // a bad measurement, and letting it push the boundary collapsed every
                            // column after it.
                            if (c < colEdges.Count - 1 && px1 <= colEdges[c + 1].X1)
                                contentEdge = Math.Max(contentEdge, px1);
                            blocks.Add(new PostBlock(at, t.Top, null, true)
                            {
                                XLeft = px0, XRight = px1, Side = side, Column = c, Group = group,
                            });
                        }
                    }
                    at = Math.Max(at, t.Bottom);
                }
            }

            // A picture measured across the WHOLE post is shared only if the other columns hold
            // nothing of their own in those rows. Two panels SIDE BY SIDE measure full width
            // too - the outer extent runs from the left edge of one to the right edge of the
            // other, and nothing blank between them stops it. Neither meme that exposed this
            // had a seam to find: the pixel columns either side of the gutter measured just as
            // busy as the picture itself (std 64), so there is nothing to detect in the pixels.
            //
            // The tell is what the OTHER columns claimed. A genuine shared picture is bounded
            // by the same text in every column, so every column's claim covers the SAME rows;
            // independent panels reach past it. Without this the wide claim swallowed the
            // neighbour's panel and, taking the earliest column's turn, put the punchline on
            // screen before a word was read - "me scrolling / the algorithm" gave its answer
            // away at 0ms, and the 2x2 "new AI tool" grid showed both bottom panels 10s early.
            foreach (var s in shared)
            {
                var i = blocks.FindIndex(b =>
                    b.IsMedia && b.Column is null && b.Top == s.Top && b.Bottom == s.Bottom);
                if (i < 0) continue;

                // ...and a SLIVER is not a panel. Where a column has already collapsed to a
                // few pixels against the edge of the post - which happens on its own, six
                // screenshots in the corpus carry one - that remnant is not evidence of a
                // second panel, and reading it as one demoted a genuinely shared picture and
                // tore a 27% hole in the card ("me trying to explain my mental illness", whose
                // right column is 5px wide at x=970..975 of a 975px post).
                var independent = blocks.Any(b =>
                    b.IsMedia && b.Column is { } bc && bc != s.Column
                    && b.Top < s.Bottom && b.Bottom > s.Top
                    && (b.XRight ?? w) - (b.XLeft ?? 0) >= w * 0.15
                    && (b.Top < s.Top - 4 || b.Bottom > s.Bottom + 4));
                if (!independent) continue;

                blocks[i] = blocks[i] with
                {
                    Column = s.Column,
                    Side = SideOfColumn(s.Column, colEdges.Count),
                    Group = group,
                    XLeft = Math.Max(blocks[i].XLeft ?? 0, s.Column == 0 ? 0 : colEdges[s.Column].X0),
                    XRight = Math.Min(blocks[i].XRight ?? w, colEdges[s.Column].X1),
                };
            }
        }

        // A picture may reach PAST the text gutter, so the far column has to begin where that
        // picture actually ends. Without this the right column's slices still started at the
        // gutter and carried a strip of the left column's photo along with them — the same
        // pixels drawn twice, which is what tore pictures apart before.
        if (gutters is { Count: > 0 })
        {
            // Only where they actually MEET. Shifting every right-hand slice by the widest
            // overhang anywhere in the post pushed the captions of "internet boycotts" from
            // x=432 to x=708 because of a picture 300px further down, cutting the first word
            // off every line of the right column. An overhang is a local collision, so it moves
            // only the slices it collides with.
            blocks = blocks.Select(b =>
            {
                if (b.Column is not { } col || col == 0) return b;
                var edge = blocks
                    .Where(o => o.Column is { } oc && oc < col)
                    .Where(o => (o.XRight ?? 0) > (b.XLeft ?? 0))
                    .Where(o => o.Top < b.Bottom && o.Bottom > b.Top)
                    .Select(o => o.XRight ?? 0)
                    .DefaultIfEmpty(0)
                    .Max();
                // Never past the slice's own right edge. A picture in the first column running
                // almost the whole width pushed every later column's slices beyond their own
                // right edge - x=713..319 - which is not a crop at all.
                var limit = (b.XRight ?? int.MaxValue) - 8;
                return edge > (b.XLeft ?? 0) && edge < limit ? b with { XLeft = edge } : b;
            }).ToList();

            // ...and CLOSE THE GAP that shift opens.
            //
            // A slice is moved right when a picture in an earlier column overlaps ANY of its
            // rows, but the whole slice moves while the picture reaches only part of the way up
            // it. The rows the picture never reaches are then drawn by nothing at all, between
            // the gutter and the shifted edge, and the card shows the group's own dark ground
            // through the hole. On the Apple keynote meme that hole was a 24-row band at
            // x=168..492 lying straight across Steve Jobs' eyes, like a censor bar.
            //
            // Each slice therefore reaches to whatever the NEXT column actually starts at over
            // its own rows, rather than stopping at the nominal gutter. It never reaches into a
            // neighbour - the neighbour's own left edge is the limit - so this fills the hole
            // without drawing anything twice, and where a column has no neighbour on those rows
            // the slice simply runs to the edge of the post, which is what the post looks like.
            blocks = blocks.Select(b =>
            {
                if (b.Column is not { } col) return b;
                var neighbour = blocks
                    .Where(o => o.Column is { } oc && oc > col)
                    .Where(o => o.Top < b.Bottom && o.Bottom > b.Top)
                    .Select(o => o.XLeft ?? w)
                    .DefaultIfEmpty(w)
                    .Min();
                return neighbour > (b.XRight ?? 0) ? b with { XRight = neighbour } : b;
            }).ToList();
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

        // A row the TYPED POST also says is the post, not chrome.
        //
        // Nothing in the LAYOUT separates the first bubble of a chat screenshot from a username:
        // one line, a gap beneath it, more text below. That gap - between the first two bubbles -
        // was read as an avatar, so "Aunt, on my way back home" became the byline: left on screen
        // from the first frame, never spoken, and its slice stretched down over the absorbed gap
        // drew the same rows as the picture there, which failed the finished video's own check.
        // What the layout cannot tell, the typed post can. The words in the picture are typed in;
        // the byline is the one thing that is not.
        var typedWords = Normalise(string.Join(" ", typedLines ?? Array.Empty<string>()));
        if (headerIndex >= 0 && typedWords.Length > 0)
        {
            var headerWords = Normalise(blocks[headerIndex].Text);
            if (headerWords.Length > 0 && typedWords.Contains(headerWords)) headerBottom = 0;
        }

        if (!headerIsSingleRow || (!absorbedAvatar && !readsLikeByline)) headerBottom = 0;

        // Attachment filename/size lines are dropped outright â€” not read and not shown. This
        // happens AFTER the picture gaps are worked out, so removing them cannot make the photo
        // above them swallow the text below.
        var skipped = blocks.RemoveAll(b => !b.IsMedia && b.Top >= headerBottom && IsFileMetadata(b.Text));
        if (skipped > 0)
            _log.LogInformation("Skipped {N} attachment metadata line(s) in {File}.",
                skipped, Path.GetFileName(imagePath));

        // Cut the chrome's rows out of the PICTURE blocks too, or the card still displays the
        // words nobody reads. Reuses the same Uncovered helper the abandoned-column merge uses.
        if (chromeRows.Count > 0)
        {
            var trimmed = new List<PostBlock>();
            var lost = 0;
            foreach (var b in blocks)
            {
                if (!b.IsMedia) { trimmed.Add(b); continue; }
                var parts = Uncovered(b.Top, b.Bottom, chromeRows).ToList();
                if (parts.Count == 1 && parts[0].Top == b.Top && parts[0].Bottom == b.Bottom)
                {
                    trimmed.Add(b);
                    continue;
                }
                lost++;
                foreach (var (t, bo) in parts)
                    if (bo - t >= gapLimit) trimmed.Add(b with { Top = t, Bottom = bo });
            }
            if (lost > 0)
            {
                _log.LogInformation("Cut {N} chrome row(s) out of the picture blocks in {File}.",
                    lost, Path.GetFileName(imagePath));
                blocks = trimmed;
            }
        }

        _log.LogInformation(
            "Sliced {File}: {W}x{H}, {Lines} caption line(s), {Media} picture block(s), header ends {Header}.",
            Path.GetFileName(imagePath), w, h,
            blocks.Count(b => !b.IsMedia && b.Top >= headerBottom), blocks.Count(b => b.IsMedia), headerBottom);

        var groupsBefore = blocks.Where(b => b.Group is not null).Select(b => b.Group).Distinct().Count();
        blocks = RejoinSplitPictures(blocks, w);

        // Which comparison groups have BOTH halves on photographic content. Measured here, where
        // the bitmap is still in scope.
        var photoBacked = new HashSet<int?>();
        foreach (var grp in blocks.Where(b => b.Group is not null && !b.IsMedia).GroupBy(b => b.Group))
        {
            var sides = grp.GroupBy(b => b.Column).ToList();
            if (sides.Count < 2) continue;
            if (sides.All(side => ColourConcentration(
                    bmp,
                    side.Min(b => b.Top), side.Max(b => b.Bottom),
                    side.Min(b => Math.Max(0, b.XLeft ?? 0)),
                    side.Max(b => Math.Min(w, b.XRight ?? w))) < 0.45))
                photoBacked.Add(grp.Key);
        }

        blocks = AbandonSliverColumns(blocks, w, gapLimit, photoBacked, out var doomReasons);
        var groupsAfter = blocks.Where(b => b.Group is not null).Select(b => b.Group).Distinct().Count();
        string note;
        if (groupsBefore == 0)
            note = "no gutter found";
        else if (groupsBefore != groupsAfter)
            note = $"{groupsBefore} found, {groupsAfter} kept - discarded by "
                 + (string.IsNullOrEmpty(doomReasons) ? "(rejoin)" : doomReasons);
        else
            note = $"{groupsBefore} comparison group(s) kept";
        _log.LogInformation("Column split: {Note}.", note);

        // Where the comparison actually ends. Done before the hole patch, because demoting a
        // trailing section changes which lane covers those rows.
        blocks = EndComparisonAtLastSharedRow(blocks, gapLimit);

        // The same picture twice - once full width, once as a column - is not a duplicate the
        // card can absorb: slices are STACKED, so a repeat adds its own height and pushes the
        // rest of the post off the bottom of the frame. It is what left "two wolves" showing a
        // title, a blank middle and one line.
        blocks = DropDuplicatePictures(blocks, w);

        // One spoken line per slice, so nothing can arrive in threes.
        blocks = OneLinePerSlice(blocks, lines);

        // A slice lying inside a comparison's rows has to BE part of that comparison.
        blocks = AbsorbIntoOverlappingGroup(blocks);

        // Rows that no slice would ever show.
        //
        // A column split hands each picture to a side, so a picture straddling the gutter loses
        // whatever rows sit above the first text row of its column - the boycotts meme clipped
        // 115px off the top of the wojak's head. The pre-render check then blamed the SPLIT and
        // re-sliced full width, which is exactly the "left and right at the same time" defect.
        // Patching the hole keeps both the split and the picture.
        // Two comparisons must not share rows: the card gives each its own box and stacks them,
        // so an overlap draws those rows twice, one above the other.
        blocks = SeparateGroups(blocks);

        blocks = PatchHoles(blocks, bmp, background, headerBottom, gapLimit);

        // ...and the holes that one cannot see, because it counts ROWS: inside a comparison, half
        // a row can be drawn by nobody while the row still counts as covered.
        blocks = PatchColumnHoles(blocks, bmp, background, w);

        blocks = SeparateOverlaps(blocks);

        return new PostBands(w, h, blocks, headerBottom, background,
            blocks.Any(b => b.Column is not null) ? gutters?.FirstOrDefault() : null)
        {
            ColumnNote = note,
        };
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
            var left = media.Where(b => b.Column == 0).ToList();
            var right = media.Where(b => b.Column is > 0).ToList();
            if (left.Count == 0 || right.Count == 0) continue;

            foreach (var l in left)
            {
                // Whatever lies on the same rows in the columns to the right - on a three-column
                // post one picture can be cut into three pieces, not two.
                var partners = right
                    .Where(x =>
                    {
                        var overlap = Math.Min(l.Bottom, x.Bottom) - Math.Max(l.Top, x.Top);
                        var shorter = Math.Min(l.Height, x.Height);
                        return shorter > 0 && overlap > shorter * 0.8;
                    })
                    .OrderBy(x => x.Column)
                    .ToList();
                if (partners.Count == 0) continue;
                var r = partners[^1];

                var x0 = Math.Min(l.XLeft ?? 0, partners.Min(x => x.XLeft ?? 0));
                var x1 = Math.Max(l.XRight ?? width, partners.Max(x => x.XRight ?? width));
                if (x1 - x0 < width * 0.9) continue;          // not actually one full-width picture

                // The rejoined picture must contain NO text. Without this the two halves of a
                // threaded conversation — avatars down the left, replies down the right — looked
                // like one picture and merged into a single block swallowing every line of text
                // inside it, which put 974px overlaps back into ten of the saved screenshots.
                var top = Math.Min(l.Top, partners.Min(x => x.Top));
                var bottom = Math.Max(l.Bottom, partners.Max(x => x.Bottom));
                if (result.Any(b => !b.IsMedia && b.Bottom > top && b.Top < bottom)) continue;

                result.Remove(l);
                foreach (var part in partners) { result.Remove(part); right.Remove(part); }
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


    /// <summary>Overlap the last misalignment test measured, for the diagnostic note.</summary>
    [ThreadStatic] private static double MisalignRatio;

    private static List<PostBlock> AbandonSliverColumns(List<PostBlock> blocks, int width,
        int gapLimit, IReadOnlySet<int?> photoBackedGroups, out string doomReasons)
    {
        // A column narrower than a tenth of the post is not a column.
        //
        // TEXT only. Counting media here was added to catch a chat thread that split with a 3px
        // picture column — but the reworked gutter scorer rejects that split at source now, and
        // including media instead condemned a legitimate two-panel meme whose photo columns the
        // extent measurement had clipped. The scorer is the right place for that judgement.
        var slivers = blocks
            .Where(b => b.Group is not null && !b.IsMedia)
            .GroupBy(b => (b.Group, b.Column))
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
            .Where(g => g.Select(b => b.Column).Distinct().Count() < 2)
            .Select(g => g.Key);

        // And neither is a pair of columns whose text sits at completely different HEIGHTS.
        // A real comparison reads across: both columns carry text on roughly the same rows. On
        // one Instagram thread the left column held text on rows 160-287, 604-660 and 715-735
        // while the right column held a single row band at 667-681 - no overlap whatsoever - so
        // for nearly every row the right third of the post belonged to nothing and rendered as
        // black patches beside the text.
        //
        // The measure is how much of the SHORTER column falls inside the taller one, NOT how
        // much the two share between them. Sharing punishes a column for having one line more
        // than its partner, which is the ordinary case: "two wolves" reads "One wants to
        // comfortably drive a Corolla station wagon or Camry" on the left against "The other
        // wants to drive a 1953 black Mercedes-Benz" on the right - three rows against two,
        // perfectly aligned, and it scored 38% and was thrown away. By containment it is 100%,
        // while the Instagram thread and any alternating chat thread stay at 0%.
        var misaligned = blocks
            .Where(b => b.Group is not null && !b.IsMedia)
            .GroupBy(b => b.Group)
            .Where(g =>
            {
                // Per COLUMN, and judged on the WORST pair of neighbours - a three-column
                // comparison is only as aligned as its least aligned pair.
                var rows = new Dictionary<int, HashSet<int>>();
                foreach (var b in g)
                {
                    if (b.Column is not { } col) continue;
                    if (!rows.TryGetValue(col, out var set)) rows[col] = set = new HashSet<int>();
                    for (int y = b.Top; y < b.Bottom; y++) set.Add(y);
                }
                if (rows.Count < 2) return false;                  // handled by oneSided
                var ordered = rows.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
                var worst = ordered
                    .Zip(ordered.Skip(1), (a, b) => (A: a, B: b))
                    .Where(pair => pair.A.Count > 0 && pair.B.Count > 0)
                    .Select(pair =>
                    {
                        var small = pair.A.Count < pair.B.Count ? pair.A : pair.B;
                        var big = pair.A.Count < pair.B.Count ? pair.B : pair.A;
                        return (double)small.Count(y => big.Contains(y)) / small.Count;
                    })
                    .DefaultIfEmpty(1.0)
                    .Min();
                var l = ordered[0];
                var r = ordered[^1];
                var shorter = l.Count < r.Count ? l : r;
                var taller = l.Count < r.Count ? r : l;
                if (shorter.Count == 0) return false;              // handled by oneSided
                // A comparison whose two halves sit on PHOTOGRAPHS is a two-panel meme, and its
                // captions are routinely diagonal - one over the top-left picture, one over the
                // bottom-right. The Tarantino meme scores 7% overlap and is perfectly valid; the
                // chat thread this guard exists to reject is flat fills instead.
                if (photoBackedGroups.Contains(g.Key)) return false;
                MisalignRatio = worst;
                // The threshold answers what this guard is FOR: splits where the two columns
                // do not meet at all. The layouts it must reject sit at 0% - the Instagram
                // thread above, and any alternating chat screenshot, whose messages take turns
                // down the page and never stand beside each other. Real comparisons sit far
                // above it: 100% for "internet boycotts", 58% for "two wolves", whose right
                // column carries one extra caption below the left column's picture.
                return MisalignRatio < 0.25;
            })
            .Select(g => g.Key);

        var slv = slivers.ToList(); var one = oneSided.ToList(); var mis = misaligned.ToList();
        doomReasons = string.Join(", ",
            new[] { slv.Count > 0 ? $"sliver:{slv.Count}" : null,
                    one.Count > 0 ? $"one-sided:{one.Count}" : null,
                    mis.Count > 0 ? $"misaligned:{mis.Count} (rows share {MisalignRatio:P0})" : null }
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
            // Runs are chained by TEXT alone, and the pictures are fitted around them
            // afterwards.
            //
            // Chaining pictures in as well collapses the whole post. A split that is about to
            // be abandoned was measured inside a column that does not really exist, and on a
            // meme whose caption is written across the artwork that bogus column holds no text
            // at all - so its one "text-free gap" is the entire picture. Chained in, that one
            // block overlaps every caption row and drags all of them into a single slice: the
            // "math test" meme put its eight lines on screen at once for exactly this reason.
            // The picture is not lost, it is clipped below to whatever rows the text does not
            // already cover - and it never needed those rows anyway, because a text slice is
            // itself a crop of the picture and shows them.
            var span = blocks.Where(b => b.Group == group).ToList();
            var top = span.Min(b => b.Top);
            var floor = span.Max(b => b.Bottom);

            // A full-width picture found during the column walk belongs to no column, so it is
            // not in the group - but it covers these same rows, and left alone it is drawn over
            // the merged slices. It is clipped here too.
            var loose = blocks
                .Where(b => b.Group is null && b.IsMedia && b.Top < floor && b.Bottom > top)
                .ToList();
            foreach (var m in loose) kept.Remove(m);

            var pictures = span.Where(b => b.IsMedia).Concat(loose).ToList();
            var side = span.Where(b => !b.IsMedia).OrderBy(b => b.Top).ToList();
            var covered = new List<(int Top, int Bottom)>();
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
                covered.Add((run.Min(b => b.Top), bottom));
            }

            // Whatever rows of the group's pictures the merged text does not already show.
            //
            // Coalesced, and held to the same gapLimit the walk above uses to tell a picture
            // from line spacing. Without both, the leftovers of one picture behind eight
            // caption rows came back as seven 11px strips and two adjacent halves - and each
            // strip is a "picture" to the renderer, which reveals it in its own right.
            // Against EVERY line of text in the post, not only the ones merged out of this group.
            // A picture is being flattened to full width here, so any text it now spans would be
            // drawn over - and with three columns the text it spans is routinely a neighbour's,
            // which this group's own runs know nothing about.
            var allText = covered
                .Concat(kept.Where(b => !b.IsMedia && b.Bottom > b.Top).Select(b => (b.Top, b.Bottom)))
                .Concat(blocks.Where(b => !b.IsMedia && b.Bottom > b.Top).Select(b => (b.Top, b.Bottom)))
                .ToList();

            var leftovers = pictures
                .SelectMany(pic => Uncovered(pic.Top, pic.Bottom, allText))
                .OrderBy(r => r.Top)
                .ToList();
            for (int i = 0; i < leftovers.Count; i++)
            {
                var (pTop, pBottom) = leftovers[i];
                while (i + 1 < leftovers.Count && leftovers[i + 1].Top <= pBottom)
                    pBottom = Math.Max(pBottom, leftovers[++i].Bottom);
                if (pBottom - pTop >= gapLimit)
                    kept.Add(new PostBlock(pTop, pBottom, null, true) { Side = PostSide.Full });
            }
        }

        return kept.OrderBy(b => b.Top).ThenBy(b => b.IsMedia).ToList();
    }

    /// <summary>
    /// Drop a picture already drawn, and drawn wider, by another picture.
    ///
    /// A photo running the full width of a comparison is caught twice: once as the shared
    /// full-width picture it is, and again as the right column's media, because the column walk
    /// sees its right half sitting in that column. Both then reach the card, which stacks its
    /// slices - so the post came out a picture taller than itself, and everything below the
    /// repeat fell off the bottom of the frame.
    /// </summary>
    private static List<PostBlock> DropDuplicatePictures(List<PostBlock> blocks, int width)
    {
        var media = blocks.Where(b => b.IsMedia).ToList();
        if (media.Count < 2) return blocks;

        // "No bound" means the card's edge, not infinity: comparing against a null as if it were
        // a real number is how the AI art meme kept a picture that was wholly inside another.
        (int X0, int X1) Span(PostBlock b) =>
            (Math.Max(0, b.XLeft ?? 0), Math.Min(width, b.XRight ?? width));
        long Area(PostBlock b)
        {
            var (x0, x1) = Span(b);
            return (long)Math.Max(0, x1 - x0) * Math.Max(0, b.Bottom - b.Top);
        }

        var drop = new HashSet<PostBlock>();
        foreach (var m in media)
        {
            if (drop.Contains(m)) continue;
            var (mx0, mx1) = Span(m);
            var mw = Math.Max(1, mx1 - mx0);
            var mh = Math.Max(1, m.Bottom - m.Top);

            foreach (var other in media)
            {
                if (ReferenceEquals(other, m) || drop.Contains(other)) continue;
                if (Area(other) <= Area(m)) continue;               // keep the whole picture

                // CONTAINED, not merely overlapping. At 90% overlap a picture starting ten rows
                // above the one that swallowed it lost those rows to nothing at all, and the
                // column it belonged to rendered a blank strip where they had been.
                if (other.Top > m.Top + 4 || other.Bottom < m.Bottom - 4) continue;

                var (ox0, ox1) = Span(other);
                if (ox0 > mx0 + 4 || ox1 < mx1 - 4) continue;

                drop.Add(m);
                break;
            }
        }
        return drop.Count == 0 ? blocks : blocks.Where(b => !drop.Contains(b)).ToList();
    }

    /// <summary>
    /// End a comparison where its columns stop running side by side: rows below both of them
    /// belong to the post, not to a side.
    ///
    /// A gutter runs the height of a band, so a caption sitting UNDER the two columns - the
    /// speaker label on the boycotts meme, "Zucken-Musk:" - is swept into whichever side it
    /// happens to overlap. It is then read in the MIDDLE of the comparison, between the left
    /// column and the right one, which is neither where it is written nor where anyone reading
    /// the meme would say it. Anything starting a clear gap below the last row the columns
    /// share goes back to full width, and so does everything after it: the section is over.
    ///
    /// The gap is what separates this from a column that simply runs a line longer than its
    /// neighbour. A continuation line follows at line spacing; a new section is set off by a
    /// picture or by white space, which is the same distinction the band walk already makes.
    /// </summary>
    private static List<PostBlock> EndComparisonAtLastSharedRow(List<PostBlock> blocks, int gapLimit)
    {
        var demoted = new HashSet<PostBlock>();
        foreach (var grp in blocks.Where(b => b.Group is not null).GroupBy(b => b.Group))
        {
            var perSide = grp.Where(b => !b.IsMedia)
                             .GroupBy(b => b.Column)
                             .Select(side => side.Max(b => b.Bottom))
                             .ToList();
            if (perSide.Count < 2) continue;
            var shared = perSide.Min();

            // Measured against the row's OWN column, not against the shortest one. Columns are
            // routinely different lengths - the 2026 column of "applying for a job" runs twelve
            // items against three - and demoting everything below the shortest column tore the
            // long column's tail out of it, so those lines were read after the comparison had
            // already moved on. What marks a NEW section is a gap far wider than the spacing
            // that column has been using.
            foreach (var lane in grp.GroupBy(b => b.Column))
            {
                var rows = lane.OrderBy(b => b.Top).ToList();
                var spacings = rows.Zip(rows.Skip(1), (a, b) => b.Top - a.Bottom)
                                   .Where(gap => gap >= 0)
                                   .OrderBy(gap => gap)
                                   .ToList();
                var typical = spacings.Count > 0 ? spacings[spacings.Count / 2] : 0;
                var breakAt = Math.Max(gapLimit, typical * 2 + 8);

                PostBlock? section = null;
                for (int i = 1; i < rows.Count && section is null; i++)
                    if (rows[i].Top - rows[i - 1].Bottom >= breakAt && rows[i].Top > shared)
                        section = rows[i];
                if (section is null) continue;
                foreach (var b in rows.Where(b => b.Top >= section.Top)) demoted.Add(b);
            }
        }
        if (demoted.Count == 0) return blocks;

        // A caption row spans the card once it is no longer in a column, so its x bounds go.
        // A PICTURE keeps its own, because widening it across the card makes it overlap the
        // other column's artwork - which is a picture drawn twice, and a card taller than the
        // post it came from.
        return blocks
            .Select(b => demoted.Contains(b)
                ? b with
                {
                    Side = PostSide.Full,
                    Group = null,
                    XLeft = b.IsMedia ? b.XLeft : null,
                    XRight = b.IsMedia ? b.XRight : null,
                    Segments = b.Segments.Select(sg => sg with { Side = PostSide.Full }).ToList(),
                }
                : b)
            .ToList();
    }

    /// <summary>
    /// A comic laid out as a GRID of panels, read left to right and top to bottom.
    ///
    /// The dividers are the giveaway: near-black lines, a few pixels thick, running the whole way
    /// across a light page. Measured against all 151 saved screenshots this claims 7, and all 7
    /// are real grids - the rest of the corpus is untouched, which matters because a rule this
    /// shape could easily swallow every dark-mode screenshot (an early version claimed 24).
    ///
    /// The card already knows how to show panels: <c>IsDiagram</c> puts ONE copy of the picture
    /// on screen with the panels not yet reached covered over.
    /// </summary>
    private static PostBands? TryComicGrid(
        Bitmap bmp, IReadOnlyList<OcrLineBox> ocrLines, string background)
    {
        int w = bmp.Width, h = bmp.Height;
        if (w < 200 || h < 200) return null;

        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bytes = new byte[data.Stride * h];
        try { Marshal.Copy(data.Scan0, bytes, 0, bytes.Length); }
        finally { bmp.UnlockBits(data); }

        var darkInRow = new int[h];
        var darkInCol = new int[w];
        var light = 0;
        for (int y = 0; y < h; y++)
        {
            var row = y * data.Stride;
            for (int x = 0; x < w; x++)
            {
                var i = row + x * 4;
                var luma = (bytes[i + 2] * 299 + bytes[i + 1] * 587 + bytes[i] * 114) / 1000;
                if (luma < 70) { darkInRow[y]++; darkInCol[x]++; }
                else if (luma > 150) light++;
            }
        }

        // Black lines on a light page. A dark-mode screenshot is dark nearly everywhere, and its
        // rows of white text look exactly like dividers from this distance.
        if (light < (long)w * h * 0.5) return null;

        var rows = Dividers(darkInRow, w, h);
        var cols = Dividers(darkInCol, h, w);
        if (rows.Count is 0 or > 3 || cols.Count is 0 or > 3) return null;

        var yEdges = Cells(rows, h);
        var xEdges = Cells(cols, w);
        if (yEdges.Any(c => c.Hi - c.Lo < h * 0.20)) return null;
        if (xEdges.Any(c => c.Hi - c.Lo < w * 0.25)) return null;

        var blocks = new List<PostBlock>();
        foreach (var yc in yEdges)
            foreach (var xc in xEdges)
            {
                var text = string.Join(" ", ocrLines
                    .Where(l => (l.Top + l.Bottom) / 2 >= yc.Lo && (l.Top + l.Bottom) / 2 < yc.Hi)
                    .Where(l => (l.Left + l.Right) / 2 >= xc.Lo && (l.Left + l.Right) / 2 < xc.Hi)
                    .OrderBy(l => l.Top)
                    .Select(l => l.Text.Trim())
                    .Where(t => t.Length > 0));

                blocks.Add(new PostBlock(yc.Lo, yc.Hi, text, false)
                {
                    XLeft = xc.Lo,
                    XRight = xc.Hi,
                    Segments = new[] { new PostSegment(text, PostSide.Full) },
                });
            }

        return new PostBands(w, h, blocks, 0, background) { IsDiagram = true, ColumnNote = $"comic grid, {yEdges.Count}x{xEdges.Count} panels" };
    }

    /// <summary>Runs of near-fully-dark lines: thin, and not the picture's own border.</summary>
    private static List<(int Lo, int Hi)> Dividers(int[] darkCount, int across, int along)
    {
        var found = new List<(int Lo, int Hi)>();
        var i = 0;
        while (i < along)
        {
            if (darkCount[i] < across * 0.92) { i++; continue; }
            var j = i;
            while (j + 1 < along && darkCount[j + 1] >= across * 0.92) j++;

            var thickness = j - i + 1;
            if (thickness >= 3 && thickness <= along * 0.03 && i > along * 0.10 && j < along * 0.90)
                found.Add((i, j + 1));
            i = j + 1;
        }
        return found;
    }

    /// <summary>The strips between the dividers - the panels themselves.</summary>
    private static List<(int Lo, int Hi)> Cells(List<(int Lo, int Hi)> dividers, int size)
    {
        var cells = new List<(int Lo, int Hi)>();
        var at = 0;
        foreach (var (lo, hi) in dividers)
        {
            cells.Add((at, lo));
            at = hi;
        }
        cells.Add((at, size));
        return cells;
    }

    /// <summary>
    /// Cut a slice that holds several written rows into one slice per row.
    ///
    /// A slice appears when the first of its lines is spoken, so a slice holding three lines puts
    /// all three on screen at once - which is what the Tarantino meme did with "Companies that
    /// fired employees / because of AI now realising / the cost of subscriptions is larger". Text
    /// over a photo is what merges them: the rows sit close and the walk cannot see the spacing.
    ///
    /// Rows that are side by side rather than stacked are left alone. Those are two columns that
    /// were deliberately merged into one full-width row, and splitting them would put the right
    /// column back on its own after the split was abandoned as unsafe.
    /// </summary>
    private static List<PostBlock> OneLinePerSlice(
        List<PostBlock> blocks, IReadOnlyList<OcrLineBox> ocrLines)
    {
        var result = new List<PostBlock>();
        foreach (var b in blocks)
        {
            if (b.IsMedia) { result.Add(b); continue; }

            // By the row's CENTRE, so every row belongs to exactly one slice. Taking any row that
            // merely overlapped the slice gave a row to both of two neighbours - OCR boxes lap
            // over each other wherever a descender reaches into the line below - and the second
            // copy came out with no height at all: 19 screenshots went Broken with "a slice ends
            // above where it starts".
            var rows = ocrLines
                .Where(l => (l.Top + l.Bottom) / 2 >= b.Top && (l.Top + l.Bottom) / 2 < b.Bottom)
                .Where(l => (l.Left + l.Right) / 2 >= (b.XLeft ?? 0)
                         && (l.Left + l.Right) / 2 < (b.XRight ?? int.MaxValue))
                .OrderBy(l => l.Top)
                .ToList();
            if (rows.Count < 2) { result.Add(b); continue; }

            // Group the rows into WRITTEN LINES first. Two boxes that overlap down most of their
            // height are beside each other - a subtitle and the backdrop lettering behind it, or
            // the two halves of a merged comparison row - and belong to the same line. Only what
            // is left stacked gets a slice of its own.
            var lineRows = new List<List<OcrLineBox>> { new() { rows[0] } };
            for (int i = 1; i < rows.Count; i++)
            {
                var group = lineRows[^1];
                var top = group.Min(l => l.Top);
                var bottom = group.Max(l => l.Bottom);
                var overlap = Math.Min(bottom, rows[i].Bottom) - Math.Max(top, rows[i].Top);
                var shorter = Math.Min(bottom - top, rows[i].Height);
                if (shorter > 0 && overlap > shorter * 0.5) group.Add(rows[i]);
                else lineRows.Add(new List<OcrLineBox> { rows[i] });
            }
            if (lineRows.Count < 2) { result.Add(b); continue; }

            for (int i = 0; i < lineRows.Count; i++)
            {
                // The slices meet exactly, so the space between two written lines is shown by one
                // of them rather than by neither.
                var top = i == 0 ? b.Top : lineRows[i].Min(l => l.Top);
                var bottom = i == lineRows.Count - 1 ? b.Bottom : lineRows[i + 1].Min(l => l.Top);
                if (bottom <= top) continue;
                var text = string.Join(" ", lineRows[i].OrderBy(l => l.Left)
                                                       .Select(l => l.Text.Trim())
                                                       .Where(x => x.Length > 0));
                result.Add(new PostBlock(top, bottom, text, false)
                {
                    Side = b.Side,
                    Group = b.Group,
                    XLeft = b.XLeft,
                    XRight = b.XRight,
                    Segments = new[] { new PostSegment(text, b.Side) },
                });
            }
        }
        return result.OrderBy(b => b.Top).ThenBy(b => b.IsMedia).ToList();
    }

    /// <summary>
    /// Pull a full-width slice that sits inside a comparison's rows into that comparison.
    ///
    /// The card draws a comparison as ONE positioned box and stacks everything else after it, so
    /// a slice whose rows fall inside that box is drawn twice: once by the box, once again
    /// underneath it. That is the repeated strip of car below the two wolves ("pretending to be
    /// the devil" sits inside the columns' picture rows) and most of what made the AI art card a
    /// wall of cyan - 312 rows of it re-drawn below the panel they belong to.
    ///
    /// Joining the group makes the card position it at its own row instead, which is where it is
    /// written anyway.
    /// </summary>
    private static List<PostBlock> AbsorbIntoOverlappingGroup(List<PostBlock> blocks)
    {
        var groups = blocks.Where(b => b.Group is not null)
                           .GroupBy(b => b.Group!.Value)
                           .ToDictionary(g => g.Key, g => (Top: g.Min(b => b.Top), Bottom: g.Max(b => b.Bottom)));
        if (groups.Count == 0) return blocks;

        return blocks.Select(b =>
        {
            if (b.Group is not null) return b;
            foreach (var (id, span) in groups)
                if (b.Top < span.Bottom && b.Bottom > span.Top)
                    return b with { Group = id, Side = PostSide.Full };
            return b;
        }).ToList();
    }

    /// <summary>
    /// Where each caption's crop begins - THE one definition of it, shared by the code that
    /// builds the reveal, the pass that patches holes and the check that hunts for them.
    ///
    /// A caption reaches upward to keep the line spacing written above it, but never past
    /// something already drawn. The card STACKS its slices, so a slice reaching back over the
    /// picture above it draws those rows a SECOND time: the boycotts wojaks came out with their
    /// chins repeated below a seam, and the two wolves with a second strip of car beneath the
    /// cars. The reach is therefore bounded by the previous slice in the same column AND by the
    /// lowest edge of anything above it that overlaps it horizontally, in whatever lane that is.
    /// </summary>
    public static Dictionary<PostBlock, int> CropTops(
        IReadOnlyList<PostBlock> blocks, int width, int headerBottom)
    {
        var textHeights = blocks.Where(b => !b.IsMedia && b.Bottom > b.Top)
                                .Select(b => b.Bottom - b.Top)
                                .OrderBy(x => x)
                                .ToList();
        var typicalLine = textHeights.Count > 0 ? textHeights[textHeights.Count / 2] : 30;
        var chainLimit = Math.Max(20, (int)(typicalLine * 1.5));

        var tops = new Dictionary<PostBlock, int>();
        foreach (var lane in blocks.Where(b => b.Top >= headerBottom).GroupBy(b => (b.Group, b.Column)))
        {
            var prevBottom = headerBottom;
            foreach (var b in lane.OrderBy(b => b.Top))
            {
                if (!b.IsMedia)
                {
                    var reach = Math.Max(Math.Min(prevBottom, b.Top), b.Top - chainLimit);
                    tops[b] = Math.Max(reach, DrawnAbove(blocks, b, width, headerBottom));
                }
                prevBottom = Math.Max(prevBottom, b.Bottom);
            }
        }
        return tops;
    }

    /// <summary>The lowest edge above this slice that something overlapping it already draws.</summary>
    private static int DrawnAbove(IReadOnlyList<PostBlock> blocks, PostBlock b, int width, int floor)
    {
        var x0 = b.XLeft ?? 0;
        var x1 = b.XRight ?? width;
        foreach (var other in blocks)
        {
            if (ReferenceEquals(other, b) || other.Top >= b.Top) continue;
            var ox0 = other.XLeft ?? 0;
            var ox1 = other.XRight ?? width;
            if (Math.Min(x1, ox1) <= Math.Max(x0, ox0)) continue;
            floor = Math.Max(floor, Math.Min(other.Bottom, b.Top));
        }
        return floor;
    }

    /// <summary>
    /// Emit a picture slice for any interior stretch of the post that nothing else draws.
    ///
    /// Coverage is measured the way BuildBlocks actually crops - each lane reaches up to the
    /// slice before it, bounded by about one line of text - so this patches only holes that
    /// would really render blank, and never lays a slice over rows something already shows.
    /// Drawing a row twice is its own defect: it is how a post's title once appeared at the
    /// top of the card and again halfway down it.
    /// </summary>
    private static List<PostBlock> PatchHoles(
        List<PostBlock> blocks, Bitmap bmp, string background, int headerBottom, int gapLimit)
    {
        var tops = CropTops(blocks, bmp.Width, headerBottom);
        var ranges = blocks
            .Select(b => (Top: b.IsMedia || !tops.TryGetValue(b, out var t) ? b.Top : t, b.Bottom))
            .Where(r => r.Bottom > r.Top)
            .ToList();
        if (ranges.Count == 0) return blocks;

        ranges.Sort((a, b) => a.Top.CompareTo(b.Top));
        var holes = new List<(int Top, int Bottom)>();
        var reach = ranges[0].Bottom;
        foreach (var (top, bottom) in ranges.Skip(1))
        {
            if (top > reach) holes.Add((reach, top));
            reach = Math.Max(reach, bottom);
        }

        var patched = blocks.ToList();
        foreach (var (top, bottom) in holes)
        {
            // Blank spacing needs no slice; only rows actually carrying something do.
            if (bottom - top < gapLimit) continue;
            if (IsBlank(bmp, top, bottom, background)) continue;
            patched.Add(new PostBlock(top, bottom, null, true) { Side = PostSide.Full });
        }
        return patched.OrderBy(b => b.Top).ThenBy(b => b.IsMedia).ToList();
    }

    /// <summary>
    /// Stop one comparison's pictures running into the next one's rows.
    ///
    /// The card lays each comparison out as its OWN box and stacks the boxes, so two comparisons
    /// that overlap in rows draw those rows twice - once in each box - and the post comes out
    /// taller than itself. The picture walk causes it: it measures a column's gap from one of its
    /// captions to the next, and the next caption can belong to the comparison BELOW. On the Apple
    /// keynote meme the first comparison's photo ran to row 628 while the second began at 588, so
    /// the 2026 presenter's head was drawn once in each box, one above the other.
    ///
    /// Only pictures are clipped: a caption never spans a comparison boundary, and cutting one
    /// would cut its words in half.
    /// </summary>
    private static List<PostBlock> SeparateGroups(List<PostBlock> blocks)
    {
        var spans = blocks
            .Where(b => b.Group is not null)
            .GroupBy(b => b.Group!.Value)
            .Select(g => (Id: g.Key, Top: g.Min(b => b.Top)))
            .OrderBy(g => g.Top)
            .ToList();
        if (spans.Count < 2) return blocks;

        return blocks
            .Select(b =>
            {
                if (!b.IsMedia || b.Group is not { } id) return b;
                var mine = spans.First(s => s.Id == id).Top;
                var next = spans.Where(s => s.Top > mine).Select(s => (int?)s.Top).Min();
                return next is { } limit && b.Top < limit && b.Bottom > limit
                    ? b with { Bottom = limit }
                    : b;
            })
            .Where(b => b.Bottom > b.Top)
            .ToList();
    }

    /// <summary>
    /// Patch what a COMPARISON leaves uncovered, which <see cref="PatchHoles"/> cannot see.
    ///
    /// That pass works in whole rows: a row is covered if any slice draws part of it. Inside a
    /// comparison that is not enough, because each slice is cropped to its own column - so a row
    /// whose left column has nothing on it counts as covered while half of it is drawn by nobody.
    /// The card then shows its own dark ground through the hole, and on the Apple keynote meme
    /// those holes lay across two presenters' faces like censor bars. The finished-video check
    /// missed them for exactly the same reason: it counts rows, not columns.
    ///
    /// Each gap becomes a picture of the post at those rows and those columns, so what is drawn
    /// is the post itself rather than a guess.
    /// </summary>
    private static List<PostBlock> PatchColumnHoles(
        List<PostBlock> blocks, Bitmap bmp, string background, int width)
    {
        var patched = blocks.ToList();

        foreach (var group in blocks.Where(b => b.Group is not null)
                                    .Select(b => b.Group!.Value)
                                    .Distinct()
                                    .ToList())
        {
            var inGroup = blocks.Where(b => b.Group == group && b.Bottom > b.Top).ToList();
            if (inGroup.Count == 0) continue;

            // Which column a gap belongs to, so it is revealed on that column's turn rather than
            // arriving with the group. Judged on the gap's middle against each column's spread.
            var columns = inGroup
                .Where(b => b.Column is not null)
                .GroupBy(b => b.Column!.Value)
                .Select(g => (
                    Column: g.Key,
                    X0: g.Min(b => b.XLeft ?? 0),
                    X1: g.Max(b => b.XRight ?? width),
                    Side: g.First().Side))
                .ToList();

            // Cut at every row boundary in the POST that falls inside this group, not just the
            // group's own: bands otherwise straddle a neighbouring comparison's edge, and a
            // caption that ends 2px short of the band was missed as cover - patching a copy of
            // it over the original.
            var groupTop = inGroup.Min(b => b.Top);
            var groupBottom = inGroup.Max(b => b.Bottom);
            var cuts = blocks
                .Where(b => b.Bottom > b.Top)
                .SelectMany(b => new[] { b.Top, b.Bottom })
                .Concat(new[] { groupTop, groupBottom })
                .Where(y => y >= groupTop && y <= groupBottom)
                .Distinct()
                .OrderBy(y => y)
                .ToList();

            for (int i = 0; i + 1 < cuts.Count; i++)
            {
                int top = cuts[i], bottom = cuts[i + 1];
                if (bottom - top < 8) continue;

                // Coverage is measured against EVERY slice, not just this group's. Groups overlap
                // in rows - one comparison's picture runs on past where the next one starts - and
                // counting only its own made the second group's caption look like a hole in the
                // first, which patched a copy of it straight over the original.
                var covered = blocks
                    .Where(b => b.Bottom > b.Top && b.Top <= top && b.Bottom >= bottom)
                    .Select(b => (X0: Math.Max(0, b.XLeft ?? 0), X1: Math.Min(width, b.XRight ?? width)))
                    .OrderBy(r => r.X0)
                    .ToList();

                // A band nothing covers at all is a whole-row hole, which PatchHoles owns.
                if (covered.Count == 0) continue;

                var x = 0;
                foreach (var (cx0, cx1) in covered)
                {
                    if (cx0 - x >= 8) AddGap(group, columns, top, bottom, x, cx0);
                    x = Math.Max(x, cx1);
                }
                if (width - x >= 8) AddGap(group, columns, top, bottom, x, width);
            }
        }

        return patched.OrderBy(b => b.Top).ThenBy(b => b.IsMedia).ToList();

        void AddGap(
            int group,
            List<(int Column, int X0, int X1, PostSide Side)> columns,
            int top, int bottom, int x0, int x1)
        {
            // Blank ground needs no slice - only a gap with something IN it is a hole worth
            // drawing, which keeps the patch off the margins every comparison has.
            if (IsBlank(bmp, top, bottom, background, x0, x1)) return;

            var middle = (x0 + x1) / 2;
            var owner = columns
                .Where(c => c.X0 <= middle && c.X1 >= middle)
                .Select(c => ((int, PostSide)?)(c.Column, c.Side))
                .FirstOrDefault();

            patched.Add(new PostBlock(top, bottom, null, true)
            {
                XLeft = x0,
                XRight = x1,
                Group = group,
                Column = owner?.Item1,
                Side = owner?.Item2 ?? PostSide.Full,
            });
        }
    }

    /// <summary>The parts of top..bottom that none of <paramref name="covered"/> overlaps.</summary>
    private static IEnumerable<(int Top, int Bottom)> Uncovered(
        int top, int bottom, IEnumerable<(int Top, int Bottom)> covered)
    {
        var at = top;
        foreach (var (cTop, cBottom) in covered.OrderBy(c => c.Top))
        {
            if (cBottom <= at || cTop >= bottom) continue;
            if (cTop > at) yield return (at, cTop);
            at = Math.Max(at, cBottom);
            if (at >= bottom) yield break;
        }
        if (at < bottom) yield return (at, bottom);
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
        foreach (var lane in blocks.GroupBy(b => (b.Group, b.Column)))
        {
            var ordered = lane.OrderBy(b => b.Top).ToList();
            for (int i = 1; i < ordered.Count; i++)
            {
                var prev = ordered[i - 1];
                var cur = ordered[i];
                if (prev.Bottom <= cur.Top) continue;

                // Sharing rows is not enough - they have to share PIXELS. Two slices side by side
                // in one lane overlap in rows by design, and pushing those apart cut "iPhone Duo"
                // off at the waist to make room for a patch filling the hole beside it, which
                // drew nothing of the caption and lost half its letters.
                var pLeft = prev.XLeft ?? 0;
                var pRight = prev.XRight ?? int.MaxValue;
                var cLeft = cur.XLeft ?? 0;
                var cRight = cur.XRight ?? int.MaxValue;
                if (Math.Min(pRight, cRight) - Math.Max(pLeft, cLeft) <= 0) continue;

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
        => FindColumnGutters(lines, band, width, bmp, background).FirstOrDefault() is var g and > 0 ? g : null;

    /// <summary>The columns a set of gutters implies, left to right, as x ranges.</summary>

    private static List<(int X0, int X1)> ColumnEdges(IReadOnlyList<int> gutters, int width)
    {
        var edges = new List<(int, int)>();
        var at = 0;
        foreach (var g in gutters)
        {
            edges.Add((at, g));
            at = g;
        }
        edges.Add((at, width));
        return edges;
    }

    /// <summary>
    /// The Left/Right label the renderer and the spotlight still speak, derived from the column
    /// index: the first column reads as Left, every later one as Right. Three columns cannot be
    /// said in that vocabulary - <c>Column</c> is the truth, and this is only the shorthand.
    /// </summary>
    private static PostSide SideOfColumn(int column, int total)
        => total <= 1 ? PostSide.Full : column == 0 ? PostSide.Left : PostSide.Right;

    /// <summary>
    /// EVERY gutter in this band, left to right - a comparison is not always two columns.
    ///
    /// "Applying for a job in... 1998 / 2010 / 2026" is three, and while this returned only the
    /// first gutter the middle column was swept in with the right one: its lines were merged into
    /// the right column's rows ("Create an account Apply online") and 2010 was never revealed or
    /// read on its own. 17 of the saved screenshots are three or four columns.
    ///
    /// A gutter is a vertical line no word crosses, with content on both sides of it. Scanning
    /// the whole width rather than the middle 40% is what finds the second and third ones; the
    /// columns those imply are then checked for width, so a ragged right margin cannot pose as
    /// one.
    /// </summary>
    /// <param name="textBothSides">
    /// Only count a gutter with WORDS on both sides of it. A gutter justified by a picture alone
    /// is real enough to keep a comparison going, but it must never START one: a comic panel with
    /// a character on the left and speech on the right offers exactly that shape, and letting it
    /// open a comparison split the panel into a near-full-width picture drawn over its own words
    /// and a 6px sliver column.
    /// </param>
    private static List<int> FindColumnGutters(IReadOnlyList<OcrLineBox> lines, List<PostBlock> band,
        int width, Bitmap? bmp = null, string? background = null, bool textBothSides = false)
    {
        var found = new List<int>();
        if (band.Count == 0) return found;

        // Counted in WORDS, not in rows. The three headings "1998 / 2010 / 2026" sit on one row,
        // so a band of a single row was refused a split outright - the headings stayed one
        // full-width slice reading "1998 2026 2010" and belonged to no column at all.
        int top = band[0].Top, bottom = band[^1].Bottom;
        var inBand = lines.Where(l => l.Top >= top - 2 && l.Bottom <= bottom + 2).ToList();
        if (inBand.Count < 3) return found;

        for (int x = (int)(width * 0.15); x <= (int)(width * 0.85); x += 4)
        {
            if (inBand.Any(l => l.Left < x && l.Right > x)) continue;
            if (found.Count > 0 && x - found[^1] < width * 0.12) continue;   // one gutter, not a band of them

            var textLeft = inBand.Any(l => l.Right <= x && l.Left >= LastEdge(found));
            var textRight = inBand.Any(l => l.Left >= x);
            if (textLeft && textRight) { found.Add(x); continue; }

            // A column with a PICTURE and no words in it is still a column. This meme's right
            // half is nothing but photographs — once its one scrap of text was dropped as OCR
            // noise there was no text there at all, the post stopped counting as two columns,
            // and full-width slices went back to cutting those photographs apart.
            if (textBothSides || bmp is null || background is null) continue;
            if (textLeft && !IsBlank(bmp, top, bottom, background, x, width)) { found.Add(x); continue; }
            if (textRight && !IsBlank(bmp, top, bottom, background, LastEdge(found), x)) found.Add(x);
        }

        // Every column the gutters imply has to be wide enough to BE a column. Without this a
        // short last line leaves a clear lane to its right that looks exactly like a gutter.
        while (found.Count > 0 && !ColumnsAreWideEnough(found, width))
            found.RemoveAt(found.Count - 1);

        return found;

        static int LastEdge(List<int> gutters) => gutters.Count == 0 ? 0 : gutters[^1];
    }

    /// <summary>No column narrower than an eighth of the post - that is a margin, not a column.</summary>
    private static bool ColumnsAreWideEnough(List<int> gutters, int width)
    {
        var edges = new List<int> { 0 };
        edges.AddRange(gutters);
        edges.Add(width);
        for (int i = 1; i < edges.Count; i++)
            if (edges[i] - edges[i - 1] < width * 0.125) return false;
        return true;
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
    /// <summary>
    /// How concentrated a region's colours are: the share of pixels in the two commonest bins.
    ///
    /// This is the signal that separates a TWO-PANEL MEME from a CHAT SCREENSHOT, which geometry
    /// cannot do - both are text left, text right, with a gap down the middle, and an earlier
    /// attempt to tell them apart by shape scored them identically (0.83 each). A chat bubble is
    /// a flat fill plus lettering, so two colours cover most of it. A photograph is not.
    /// </summary>
    private static double ColourConcentration(Bitmap bmp, int top, int bottom, int x0, int x1)
    {
        top = Math.Clamp(top, 0, bmp.Height - 1);
        bottom = Math.Clamp(bottom, top + 1, bmp.Height);
        x0 = Math.Clamp(x0, 0, bmp.Width - 1);
        x1 = Math.Clamp(x1, x0 + 1, bmp.Width);

        var bins = new Dictionary<int, int>();
        var total = 0;
        var stepY = Math.Max(1, (bottom - top) / 120);
        var stepX = Math.Max(1, (x1 - x0) / 120);
        for (int y = top; y < bottom; y += stepY)
            for (int x = x0; x < x1; x += stepX)
            {
                var c = bmp.GetPixel(x, y);
                // 32 levels per channel: tolerant of JPEG noise, still separates real colours.
                var key = (c.R >> 3) << 10 | (c.G >> 3) << 5 | (c.B >> 3);
                bins[key] = bins.GetValueOrDefault(key) + 1;
                total++;
            }
        if (total == 0) return 1;
        return (double)bins.Values.OrderByDescending(v => v).Take(2).Sum() / total;
    }

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

