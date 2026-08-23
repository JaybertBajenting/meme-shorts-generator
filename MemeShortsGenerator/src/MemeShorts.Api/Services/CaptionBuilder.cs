using MemeShorts.Api.Models;

namespace MemeShorts.Api.Services;

/// <summary>
/// Builds word-level captions in the exact @remotion/captions shape.
///
/// We always display the STYLED caption tokens (e.g. "MOTHER OF..."), but time them
/// from real speech when whisper word timings are available; otherwise we spread them
/// evenly (length-weighted) across the beat.
/// </summary>
public static class CaptionBuilder
{
    /// <summary>Even spread for every beat (no whisper). Kept for the "Even" caption source.</summary>
    public static List<Caption> Build(IReadOnlyList<RenderBeat> beats)
    {
        var captions = new List<Caption>();
        foreach (var beat in beats)
            captions.AddRange(BuildForBeat(beat.Caption, beat.StartMs, beat.EndMs, words: null));
        return captions;
    }

    /// <summary>
    /// Captions for one beat. The displayed tokens come from <paramref name="captionText"/>;
    /// timings come from <paramref name="words"/> (whisper, relative to the beat clip) when
    /// supplied, else they are spread evenly across [beatStartMs, beatEndMs].
    /// </summary>
    public static List<Caption> BuildForBeat(string captionText, int beatStartMs, int beatEndMs, IReadOnlyList<WordTiming>? words)
    {
        var tokens = captionText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return new();

        var spans = (words is { Count: > 0 })
            ? AlignToSpeech(tokens, words, beatStartMs, beatEndMs)
            : EvenSpread(tokens, beatStartMs, beatEndMs);

        var result = new List<Caption>(tokens.Length);
        for (int i = 0; i < tokens.Length; i++)
        {
            var (start, end) = spans[i];
            result.Add(new Caption
            {
                // @remotion/captions expects a leading space to join tokens cleanly.
                Text = (i == 0 ? "" : " ") + tokens[i],
                StartMs = start,
                EndMs = end,
                TimestampMs = start + (end - start) / 2,
                Confidence = words is { Count: > 0 } ? 0.95 : 0.5,
            });
        }
        return result;
    }

    /// <summary>Map styled tokens onto whisper word timings (absolute = beatStart + wordTime).</summary>
    private static (int start, int end)[] AlignToSpeech(string[] tokens, IReadOnlyList<WordTiming> words, int beatStartMs, int beatEndMs)
    {
        var spans = new (int, int)[tokens.Length];

        if (tokens.Length == words.Count)
        {
            // 1:1 — caption matches the spoken words (the common case).
            for (int i = 0; i < tokens.Length; i++)
                spans[i] = (beatStartMs + words[i].StartMs, beatStartMs + words[i].EndMs);
            return spans;
        }

        // Counts differ (caption stylized vs spoken): spread styled tokens across the
        // ACTUAL speech span [firstWordStart, lastWordEnd] weighted by token length.
        int speechStart = beatStartMs + words[0].StartMs;
        int speechEnd = beatStartMs + words[^1].EndMs;
        return EvenSpread(tokens, speechStart, Math.Max(speechStart + 1, speechEnd));
    }

    private static (int start, int end)[] EvenSpread(string[] tokens, int startMs, int endMs)
    {
        var spans = new (int, int)[tokens.Length];
        int span = Math.Max(1, endMs - startMs);
        int totalWeight = tokens.Sum(t => t.Length + 1);
        int cursor = startMs;

        for (int i = 0; i < tokens.Length; i++)
        {
            int weight = tokens[i].Length + 1;
            int wordMs = Math.Max(1, span * weight / totalWeight);
            int s = cursor;
            int e = (i == tokens.Length - 1) ? endMs : Math.Min(endMs, cursor + wordMs);
            spans[i] = (s, e);
            cursor = e;
        }
        return spans;
    }
}
