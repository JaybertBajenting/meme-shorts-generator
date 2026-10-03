using System.Text;

namespace MemeShorts.Api.Services;

/// <summary>
/// Times every written word of a script against ONE continuous voice take.
///
/// The list format voices the whole script in a single pass (see RunListAsync) and then cuts the
/// picture at each line's first word. That needs a time for every written token, so this maps
/// the script's tokens onto whisper's heard words with a longest common subsequence - the same
/// reason as <see cref="RepostComposer.Align"/>: one word the voice says differently from the
/// text ("20%" heard as "twenty percent") must not knock every later word out of step. Tokens
/// with no exact match are placed proportionally between the matched ones either side.
/// </summary>
public static class NarrationAligner
{
    public sealed record TimedToken(string Text, int Line, int StartMs, int EndMs);

    public static List<TimedToken> Align(IReadOnlyList<string> lines, IReadOnlyList<WordTiming>? heard, int totalMs)
    {
        var tokens = new List<(string text, int line)>();
        for (int i = 0; i < lines.Count; i++)
            foreach (var t in lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                tokens.Add((t, i));
        var result = new List<TimedToken>(tokens.Count);
        if (tokens.Count == 0) return result;

        var starts = new int?[tokens.Count];
        var ends = new int?[tokens.Count];

        if (heard is { Count: > 0 })
        {
            var a = tokens.Select(t => Normalize(t.text)).ToList();
            var b = heard.Select(w => Normalize(w.Text)).ToList();
            int n = a.Count, m = b.Count;
            var dp = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    dp[i, j] = a[i].Length > 0 && a[i] == b[j]
                        ? dp[i + 1, j + 1] + 1
                        : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            for (int i = 0, j = 0; i < n && j < m;)
            {
                if (a[i].Length > 0 && a[i] == b[j])
                {
                    starts[i] = heard[j].StartMs;
                    ends[i] = heard[j].EndMs;
                    i++; j++;
                }
                else if (dp[i + 1, j] >= dp[i, j + 1]) i++;
                else j++;
            }
        }

        // Fill the gaps: an unmatched run sits between the matched word before it (or 0) and
        // the matched word after it (or the end of the take), shared out by written length.
        int k = 0;
        while (k < tokens.Count)
        {
            if (starts[k] is not null) { k++; continue; }
            int runStart = k;
            while (k < tokens.Count && starts[k] is null) k++;
            int from = runStart == 0 ? 0 : ends[runStart - 1]!.Value;
            int to = k < tokens.Count ? starts[k]!.Value : Math.Max(from + 1, totalMs);
            int weight = 0;
            for (int q = runStart; q < k; q++) weight += tokens[q].text.Length + 1;
            int cursor = from;
            for (int q = runStart; q < k; q++)
            {
                int span = Math.Max(1, (to - from) * (tokens[q].text.Length + 1) / Math.Max(1, weight));
                starts[q] = cursor;
                ends[q] = Math.Min(to, cursor + span);
                cursor = ends[q]!.Value;
            }
        }

        int last = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            // Monotonic: a late match can never put a word before the one it follows.
            int s = Math.Max(last, starts[i]!.Value);
            int e = Math.Max(s + 1, ends[i]!.Value);
            result.Add(new TimedToken(tokens[i].text, tokens[i].line, s, e));
            last = s;
        }
        return result;
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
