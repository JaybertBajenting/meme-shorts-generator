using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MemeShorts.Api.Models;

namespace MemeShorts.Api.Services;

/// <summary>
/// Turns a pasted social post into the line-by-line reveal the reference channel uses.
///
/// Two jobs, both of which have to agree with each other or the video desyncs:
///   1. WRAP the text into explicit display lines. The wrap happens HERE, not in CSS, because
///      each line needs its own reveal timestamp — if Remotion re-wrapped the text itself, the
///      line boundaries the timings were computed against would no longer be the ones drawn.
///   2. ALIGN those lines to real speech, so a line appears exactly as its first word is said.
/// </summary>
public static class RepostComposer
{
    /// <summary>Font the card draws body text in — must match PostCard.tsx.</summary>
    private const string FontFamily = "Segoe UI";
    private const float FontPx = 31f;

    /// <summary>
    /// Usable text width inside the 960px card (916px after 22px padding either side).
    ///
    /// MEASURED against the reference's own line breaks: GDI+ sets the same string about 1.2%
    /// WIDER than Chrome does (904.7px here vs 894px in an actual render), so measuring against
    /// the full 916 would still land ~19px clear of the edge. 908 reproduces the reference's
    /// wrap exactly while keeping that slack — and the error is in the safe direction, since
    /// over-measuring wraps a word early rather than clipping it (PostCard hides overflow).
    /// </summary>
    private const float WrapWidthPx = 908f;

    /// <summary>Split into paragraphs on blank lines; single newlines stay hard breaks.</summary>
    private static readonly Regex ParagraphSplit = new(@"\r?\n[ \t]*\r?\n", RegexOptions.Compiled);

    /// <summary>Emoji and pictographs: drawn on the card, never read aloud.</summary>
    private static readonly Regex Pictographs = new(
        // \p{Cs} matches surrogate code units, which covers every astral-plane emoji; the rest
        // are BMP symbol blocks (arrows, dingbats, misc symbols) plus the variation selector.
        "[\\p{Cs}←-⇿⌀-➿☀-⛿⬀-⯿️⃣]",
        RegexOptions.Compiled);

    private static readonly Regex Money = new(
        @"(?<neg>-)?(?<sym>[£$€])(?<num>\d[\d,]*(?:\.\d+)?)", RegexOptions.Compiled);

    /// <summary>
    /// Drop a leading fragment that isn't a word. A round avatar overhangs the first caption
    /// line, so OCR picks up a sliver of it as junk like "9'" or "•" — harmless on screen (the
    /// picture is never redrawn) but it gets READ ALOUD. Anything that is a plain word or a
    /// plain number is left alone, so a line starting "5 things" survives intact.
    /// </summary>
    public static string StripLeadingArtifact(string line)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts[0].Length > 2) return line;
        var head = parts[0];
        bool isWord = head.All(char.IsLetter);
        bool isNumber = head.All(char.IsDigit);
        return isWord || isNumber ? line : parts[1];
    }

    /// <summary>
    /// Fix the letter pairs OCR reliably confuses. Capital-I and lowercase-L are identical in
    /// most sans-serif faces, so "AI" comes back as "Al" — which the voice then reads as the
    /// name "Al" instead of "A.I.". This only touches what is SPOKEN; the screen always shows
    /// the screenshot's own pixels, which were right all along.
    /// </summary>
    public static string FixOcrConfusions(string text) =>
        // Deliberately narrow: only the exact token "Al". A post about someone called Al would
        // be misread, but on this kind of content "AI" is overwhelmingly the intended word.
        Regex.Replace(text, @"\bAl\b", "AI");

    /// <summary>Width of a string as the card will draw it, in px. Exposed for calibration.</summary>
    public static float MeasureWidth(string s)
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using var font = new Font(FontFamily, FontPx, FontStyle.Regular, GraphicsUnit.Pixel);
        return g.MeasureString(s, font, int.MaxValue, StringFormat.GenericTypographic).Width;
    }

    /// <summary>Wrap pasted post text into the exact lines the card will draw (times unset).</summary>
    public static List<PostLine> Wrap(string text)
    {
        var lines = new List<PostLine>();
        if (string.IsNullOrWhiteSpace(text)) return lines;

        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using var font = new Font(FontFamily, FontPx, FontStyle.Regular, GraphicsUnit.Pixel);
        // GenericTypographic drops the ~1em of side bearing MeasureString otherwise adds, which
        // would wrap every line several words early.
        var fmt = StringFormat.GenericTypographic;

        float Width(string s) => g.MeasureString(s, font, int.MaxValue, fmt).Width;

        foreach (var paragraph in ParagraphSplit.Split(text.Replace("\r\n", "\n").Trim()))
        {
            if (string.IsNullOrWhiteSpace(paragraph)) continue;
            var first = true;
            foreach (var segment in paragraph.Split('\n'))
            {
                var words = segment.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0) continue;

                var current = new StringBuilder();
                foreach (var word in words)
                {
                    var candidate = current.Length == 0 ? word : $"{current} {word}";
                    if (current.Length > 0 && Width(candidate) > WrapWidthPx)
                    {
                        lines.Add(new PostLine { Text = current.ToString(), NewParagraph = first });
                        first = false;
                        current.Clear().Append(word);
                    }
                    else
                    {
                        current.Clear().Append(candidate);
                    }
                }
                if (current.Length > 0)
                {
                    lines.Add(new PostLine { Text = current.ToString(), NewParagraph = first });
                    first = false;
                }
            }
        }
        return lines;
    }

    /// <summary>
    /// What the voice should say for a line: emoji removed, currency spoken the way the
    /// reference read it ("£30" -> "30 pounds", "-£45" -> "minus 45 pounds"). The DISPLAY text
    /// is never altered — the card still shows the symbols.
    /// </summary>
    public static string Spoken(string displayLine)
    {
        var s = Money.Replace(displayLine, m =>
        {
            var unit = m.Groups["sym"].Value switch
            {
                "£" => "pounds",
                "€" => "euros",
                _ => "dollars",
            };
            var num = m.Groups["num"].Value;
            // "1 pounds" reads badly; the singular only matters for exactly one.
            if (decimal.TryParse(num.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
                && v == 1m)
                unit = unit[..^1];
            return (m.Groups["neg"].Success ? "minus " : "") + num + " " + unit;
        });
        s = Pictographs.Replace(s, " ");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// <summary>The full verbatim narration for a wrapped post.</summary>
    public static string SpokenScript(IEnumerable<PostLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var s = Spoken(line.Text);
            if (s.Length == 0) continue;
            if (sb.Length > 0)
            {
                // A paragraph break is where the delivery pauses. Posts routinely end a
                // paragraph on an emoji or on nothing at all, which strips to no punctuation —
                // without this the sentences ran together ("…credit card Instead of calling
                // the police…") and the voice read straight through the beat.
                if (line.NewParagraph && !".!?:;…".Contains(sb[^1])) sb.Append('.');
                sb.Append(' ');
            }
            sb.Append(s);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Stamp each line with the moment its first word is spoken.
    ///
    /// Whisper mishears the odd word, so this walks both sequences forward together and only
    /// trusts EXACT normalised matches, skipping anything it cannot pair off within a small
    /// lookahead. A line that anchors nothing inherits the previous line's time rather than
    /// jumping the reveal somewhere arbitrary, and times are forced monotonic so a late match
    /// can never make a line appear before the one above it.
    /// </summary>
    /// <summary>
    /// Time each card line to the moment its first word is actually spoken.
    /// Returns the indices of lines that were NEVER heard in the audio.
    ///
    /// An unheard line is not a curiosity - it occupies a reveal slot while nothing reads it,
    /// so every line after it appears while the PREVIOUS line is still being said and the whole
    /// post runs a beat behind the voice. A stray header line did exactly that. Do not ignore.
    /// </summary>
    public static IReadOnlyList<int> Align(List<PostLine> lines, IReadOnlyList<WordTiming> heard)
    {
        // token -> which line it belongs to, in reading order
        var owners = new List<int>();
        var tokens = new List<string>();
        for (int i = 0; i < lines.Count; i++)
        {
            foreach (var t in Spoken(lines[i].Text).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                tokens.Add(Normalize(t));
                owners.Add(i);
            }
        }

        var spoken = heard.Select(x => Normalize(x.Text)).ToList();

        // Longest common subsequence, NOT a greedy forward scan with a small lookahead. The
        // greedy version only advanced its cursor on a match, so a single word the voice
        // pronounced differently from the text ("swaos.jpg" read as "swaos dot jay peg") threw
        // it out of step permanently — every line after that inherited one timestamp and the
        // whole post appeared at once. LCS skips insertions on either side and recovers.
        int n = tokens.Count, m = spoken.Count;
        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                dp[i, j] = tokens[i].Length > 0 && tokens[i] == spoken[j]
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var anchors = new int?[lines.Count];
        for (int i = 0, j = 0; i < n && j < m;)
        {
            if (tokens[i].Length > 0 && tokens[i] == spoken[j])
            {
                anchors[owners[i]] ??= heard[j].StartMs;
                i++; j++;
            }
            else if (dp[i + 1, j] >= dp[i, j + 1]) i++;
            else j++;
        }

        int last = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            var t = anchors[i] ?? last;
            if (t < last) t = last;
            lines[i].StartMs = t;
            last = t;
        }
        // The first line is on screen from frame 0 — the reference never opens on an empty card.
        if (lines.Count > 0) lines[0].StartMs = 0;

        return Enumerable.Range(0, lines.Count)
            .Where(i => anchors[i] is null && Spoken(lines[i].Text).Trim().Length > 0)
            .ToList();
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c) || c == '\'') sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
