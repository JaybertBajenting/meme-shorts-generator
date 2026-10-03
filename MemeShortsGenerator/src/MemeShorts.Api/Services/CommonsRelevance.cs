using System.Net;
using System.Text.RegularExpressions;

namespace MemeShorts.Api.Services;

/// <summary>
/// Does a Commons file actually show the thing a script named? Used for exact-name lookups.
///
/// The Commons search ranks by loose text relevance, and its top hit for a specific name is often
/// something else that shares a word: "Windows Recall" -> a statistics chart titled
/// "Precision-Recall tradeoff" (twice, as the opening picture), "The True Size" -> a planet size
/// chart, "Game Boy Color" -> a Game Boy Advance. Those went straight into videos.
///
/// Rule, checked against the file's title + object name + description + categories:
/// every run of capitalised words in the term (its proper name) must appear there as one phrase -
/// a leading brand word may be missing when at least two words remain ("AI Overviews" for
/// "Google AI Overviews", but not "Recall" for "Windows Recall"). A term with no capitalised words
/// needs its words together as a phrase, plurals aside. Tried on 19 real searches (2026-09-28): it
/// rejected all three wrong picks above, found the right Game Boy and the Africa map instead, and
/// kept every correct pick (Dyson Airblade Wash+Dry, Camden bench, Dreamcast fishing controller...).
/// It cannot tell a map of Greenland from a photo taken in Greenland.
/// </summary>
public static class CommonsRelevance
{
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "of", "and", "on", "in", "at", "for", "to", "with", "its", "his", "her",
        "their", "from", "by", "or",
    };

    private static readonly HashSet<string> PeopleWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "man", "men", "woman", "women", "person", "people", "girl", "boy", "child", "children", "kid", "kids",
        "doctor", "patient", "nurse", "lady", "guy", "couple", "portrait", "selfie", "family", "teen",
        "teenager", "baby", "student", "worker", "customer", "posing", "smiling", "himself", "herself",
    };

    /// <summary>
    /// Does a file's own description say a person is in it? No real photo here carries a person:
    /// "A man posing with two game controllers, while playing on his Xbox One.jpg" came back from
    /// Commons for a game-controller line (2026-09-30), and a Commons search once put two TV
    /// personalities and a child under mocking narration. The picture is drawn instead.
    /// </summary>
    public static bool ShowsPeople(string[] tokens) => tokens.Any(PeopleWords.Contains);

    /// <summary>Lower-case word tokens; camelCase and letter/digit joins are split ("Skatestopper2").</summary>
    public static string[] Tokens(string s)
    {
        s = WebUtility.HtmlDecode(Regex.Replace(s, "<.*?>", " "));
        s = Regex.Replace(s, "([a-z])([A-Z])", "$1 $2");
        s = Regex.Replace(s, @"(?<=[A-Za-z])(?=\d)|(?<=\d)(?=[A-Za-z])", " ");
        return Regex.Split(s.ToLowerInvariant(), "[^0-9a-z]+").Where(t => t.Length > 0).ToArray();
    }

    public static bool Names(string term, string[] text)
    {
        var runs = ProperNameRuns(term);
        if (runs.Count > 0)
            return runs.All(run =>
            {
                var phrase = Tokens(string.Join(' ', run));
                return ContainsPhrase(text, phrase)
                       || (phrase.Length >= 3 && ContainsPhrase(text, phrase[1..]));
            });

        // A plain description needs its words TOGETHER, in order, plurals aside. Each word merely
        // appearing somewhere let "foldable phone box" match a train seat's fold-down table and "two
        // phone screens" match an old phone (2026-09-30): a file's categories list is long enough to
        // hold every common word.
        var words = Tokens(term).SkipWhile(t => Stop.Contains(t)).Select(Singular).ToArray();
        return ContainsPhrase(text.Select(Singular).ToArray(), words);
    }

    /// <summary>
    /// The same check against a ONE-SENTENCE description written about that exact photo (Pexels'
    /// alt text): a plain term needs each of its words in it, not necessarily together - "phone on
    /// desk" is "a blue smartphone on a desk". The phrase rule exists because Commons categories
    /// hold every common word; a single sentence about one photo doesn't. Proper names still need
    /// their phrase.
    /// </summary>
    public static bool NamesInDescription(string term, string[] description)
    {
        if (ProperNameRuns(term).Count > 0) return Names(term, description);
        var have = description.Select(Singular).ToHashSet();
        var need = Tokens(term).Where(t => !Stop.Contains(t)).Select(Singular).ToList();
        return need.Count > 0 && need.All(have.Contains);
    }

    /// <summary>Plural to singular, well enough to match "phones"/"phone", "benches"/"bench", "batteries"/"battery".</summary>
    public static string Singular(string w)
    {
        if (w.Length <= 3) return w;
        if (w.EndsWith("ies")) return w[..^3] + "y";
        if (w.EndsWith("ches") || w.EndsWith("shes") || w.EndsWith("xes") || w.EndsWith("sses")) return w[..^2];
        if (w.EndsWith('s') && !w.EndsWith("ss") && !w.EndsWith("us")) return w[..^1];
        return w;
    }

    /// <summary>"Mustard Skittles by French's" -> [Mustard Skittles] [French's]. Leading articles dropped.</summary>
    private static List<List<string>> ProperNameRuns(string term)
    {
        var runs = new List<List<string>>();
        List<string>? current = null;
        foreach (var word in term.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var proper = char.IsUpper(word[0]) || word.Any(char.IsDigit);
            if (proper && !(current is null && Stop.Contains(word)))
            {
                current ??= new List<string>();
                current.Add(word);
            }
            else if (current is not null)
            {
                runs.Add(current);
                current = null;
            }
        }
        if (current is not null) runs.Add(current);
        return runs;
    }

    private static bool ContainsPhrase(string[] hay, string[] needle)
    {
        if (needle.Length == 0) return false;
        for (int i = 0; i + needle.Length <= hay.Length; i++)
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle))
                return true;
        return false;
    }
}
