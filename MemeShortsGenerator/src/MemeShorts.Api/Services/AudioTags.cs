using System.Text.RegularExpressions;

namespace MemeShorts.Api.Services;

/// <summary>
/// Per-sentence delivery cues for a list short's single voice take.
///
/// Measured 2026-09-26 by transcribing the finished videos: the scripts wrote an exclamation into
/// ~15% of sentences (his: 20%), but whisper heard 0-17% in our audio against his 20% - "red card,
/// goodbye!" came back with a full stop, a shouted "WHY?" as a plain "Why?". Whisper punctuates by
/// how a line SOUNDS, so the script was right and the delivery was flat: the whole take went to
/// the voice with no cue at all.
///
/// eleven_v3 performs inline tags such as "[excited]" without speaking them (verified earlier on
/// the meme path). Any other voice would read the brackets out, so <see cref="Strip"/> removes
/// exactly these tags - nothing else in brackets - before a non-v3 voice sees the text.
/// </summary>
public static class AudioTags
{
    public const string Excited = "[excited]";
    public const string Shouting = "[shouting]";

    private static readonly Regex OurTags = new(@"\[(excited|shouting)\]\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Sentences = new(@"(?<=[.!?…])\s+", RegexOptions.Compiled);

    /// <summary>Remove the cues this class adds; leaves every other bracketed word alone.</summary>
    public static string Strip(string text) => OurTags.Replace(text, "");

    /// <summary>
    /// Split a script into sentences and cue each: a sentence that is mostly capitals is
    /// SHOUTED, one that ends in "!" is EXCITED, the rest are left to the voice.
    /// <paramref name="speakable"/> turns written text into what the voice should read (it lowers
    /// long capitals, which the voice would otherwise spell out) and runs after the decision,
    /// because the decision needs the capitals.
    /// </summary>
    public static string Cue(string script, Func<string, string> speakable)
    {
        var parts = Sentences.Split(script.Trim())
            .Where(s => s.Length > 0)
            .Select(s =>
            {
                var shouted = IsShouted(s);
                var tag = shouted ? Shouting : s.TrimEnd('"', '\'', ')', '”', '’').EndsWith('!') ? Excited : null;
                var spoken = speakable(shouted ? LowerShout(s) : s);
                return tag is null ? spoken : $"{tag} {spoken}";
            });
        return string.Join(" ", parts);
    }

    // Kept in capitals inside a shouted sentence because they really are spelled out.
    private static readonly HashSet<string> Acronyms = new(StringComparer.Ordinal)
    {
        "AI", "TV", "USA", "UK", "EU", "UN", "NASA", "CEO", "GPS", "DNA", "CPU", "GPU", "PC", "USB",
        "FBI", "CIA", "NBA", "NFL", "DIY", "ID", "IQ", "OMG", "WW", "WWII", "UFO", "ATM", "RAM",
    };

    /// <summary>
    /// In a SHOUTED sentence the tag carries the emphasis, so every capitalised word is lowered
    /// except real acronyms. The per-word rule elsewhere keeps capitals of 3 letters or fewer so
    /// that "AI" is still spelled out - which in "WHY IS IT SPINNING" left "IS" and "IT" to be
    /// read out as letters.
    /// </summary>
    private static string LowerShout(string sentence) =>
        Regex.Replace(sentence, @"[A-Za-z']{2,}", m =>
        {
            var w = m.Value;
            var letters = w.Replace("'", "");
            return letters.All(char.IsUpper) && !Acronyms.Contains(letters) ? w.ToLowerInvariant() : w;
        });

    /// <summary>At least half the sentence's words of 2+ letters are written in capitals.</summary>
    private static bool IsShouted(string sentence)
    {
        var words = Regex.Matches(sentence, @"[A-Za-z']{2,}").Select(m => m.Value.Replace("'", "")).Where(w => w.Length >= 2).ToList();
        if (words.Count == 0) return false;
        var caps = words.Count(w => w.All(char.IsUpper));
        return caps >= 2 && caps * 2 >= words.Count || caps == words.Count;
    }
}
