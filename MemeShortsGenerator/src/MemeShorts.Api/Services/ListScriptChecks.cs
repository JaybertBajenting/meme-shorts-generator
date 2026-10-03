using System.Text.RegularExpressions;
using MemeShorts.Api.Models;

namespace MemeShorts.Api.Services;

/// <summary>
/// What was drawn for one video (see CodexCliClient.PickListStyle): the card for the prompt, and the
/// choices the code enforces afterwards - TitleShout is "all", "some" or "none"; TitleTrail adds
/// "..." (10% of his); TargetWords comes from one of his own videos (34-108); ComparisonAllowed says
/// whether the "comparison to a game, show or meme" device was drawn.
/// </summary>
public sealed record ListStyle(string Card, string TitleShout, bool TitleTrail, int TargetWords, bool ComparisonAllowed);

/// <summary>This channel's own past, which a new script must not repeat.</summary>
public sealed record ListHistory(
    IReadOnlyList<string> RecentScripts,
    IReadOnlyList<string> RecentTitles,
    IReadOnlyList<string> PastSubjects)
{
    public static readonly ListHistory Empty = new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>
/// The checks a new script must pass, each learned from a batch that failed it (2026-09-30, 30
/// scripts against his 20): the same opener tic across videos (", and man," in 7 of 22), game
/// comparisons in videos that never drew that device (Minecraft respawn, Fortnite skin, Clippy
/// respawned), spec sheets (31 controls, 869 F, 1,250 gauss), subjects coming back (glue pizza a
/// third time, hostile benches a second time with the same bench), and titles reusing a shouted
/// judgement ("A MISTAKE" three times in a day).
///
/// A HARD problem is always re-asked. A soft one is re-asked on the first attempts only, so a
/// small stylistic miss never costs a whole video.
/// </summary>
public static class ListScriptChecks
{
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "but", "of", "to", "in", "on", "at", "for", "with", "is", "are", "was",
        "were", "be", "it", "its", "it's", "this", "that", "these", "those", "so", "i", "you", "your", "my", "me",
        "we", "they", "he", "she", "just", "now", "got", "get", "has", "have", "had", "do", "does", "did", "not",
        "no", "can", "will", "from", "by", "as", "if", "then", "than", "up", "out", "into", "about", "what", "why",
        "how", "who", "one", "all", "some", "there", "here", "i'm", "that's", "s",
    };

    /// <summary>Fillers and adverbs whose repetition across videos is a tic, even as single words.</summary>
    private static readonly HashSet<string> TicWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "man", "dude", "bro", "buddy", "wait", "weirdly", "literally", "kinda", "okay", "oh", "yeah", "honestly",
        "basically", "lowkey", "apparently", "somehow", "suddenly", "officially",
    };

    private static readonly Regex GameReference = new(
        @"\b(minecraft|fortnite|respawn\w*|npc|dlc|speedrun\w*|boss fight|side quest|clippy|mario|zelda|among us|roblox|skyrim|pok[eé]mon|level(?:ed)? up|patch notes|nerf(?:ed)?|loot|spawn\w*|final boss|cheat code|hitbox|lag(?:ging)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Number = new(@"\d[\d,.]*", RegexOptions.Compiled);

    public static List<(string Why, bool Hard)> Problems(ListIdea idea, ListStyle style, ListHistory history)
    {
        var problems = new List<(string, bool)>();
        var script = string.Join(" ", idea.Items.Select(i => i.Text));
        var words = idea.Items.Sum(i => Regex.Matches(i.Text, @"\b[\p{L}\p{N}']+\b").Count);

        // Length follows the drawn video; beats follow the words (a new picture every ~1.2 s, 4-5 words).
        int lo = (int)(style.TargetWords * 0.7) - 3, hi = (int)(style.TargetWords * 1.3) + 5;
        if (words < lo || words > hi)
            problems.Add(($"{words} spoken words against a target of {style.TargetWords}", true));
        if (idea.Items.Count < Math.Max(6, words / 8) || idea.Items.Count > words / 3 + 3)
            problems.Add(($"{idea.Items.Count} beats for {words} words", true));

        var subject = string.IsNullOrWhiteSpace(idea.Subject) ? idea.Title : idea.Subject!;
        if (RepeatsSubject(subject, history.PastSubjects) is { } past)
            problems.Add(($"the subject '{subject}' was already covered ('{past}')", true));

        if (IsStaleEnding(EndingText(script), history.RecentScripts.Select(EndingWords).Where(e => e.Count > 0).ToList()))
            problems.Add(($"a stock or reused ending: '{idea.Items[^1].Text}'", false));

        if (RepeatedOpener(script, history.RecentScripts, subject + " " + idea.Title) is { } tic)
            problems.Add(($"an opener tic recent videos already used: '{tic}'", false));

        if (!style.ComparisonAllowed)
        {
            var own = (subject + " " + idea.Title).ToLowerInvariant();
            var game = GameReference.Matches(script).Select(m => m.Value).FirstOrDefault(g => !own.Contains(g.ToLowerInvariant()));
            if (game is not null)
                problems.Add(($"a game/meme comparison ('{game}') this video didn't draw", false));
        }

        var numbers = Number.Matches(script).Count;
        if (numbers > 3)
            problems.Add(($"{numbers} numbers - a spec sheet, not a reaction", false));

        var titleWords = Regex.Matches(idea.Title, @"[\p{L}\p{N}'’]+").Count;
        if (titleWords > 9)
            problems.Add(($"a {titleWords}-word title - he'd use the everyday name", false));
        if (ReusedShout(idea.Title, history.RecentTitles) is { } shout)
            problems.Add(($"the title reuses '{shout}' from a recent title", false));

        return problems;
    }

    // --- the thing written as a person ----------------------------------------------------------

    private const string Roles =
        "manager|lawyer|therapist|boss|assistant|agent|publicist|intern|bodyguard|babysitter|landlord|roommate|" +
        "coworker|supervisor|manners|feelings|emotions|opinions?|plans|dreams|homework|attendance|job|career|" +
        "hobby|hobbies|personality|vibes|trust issues|anxiety|ego|pajamas|wardrobe|outfit|bedtime|lunch break|" +
        "day off|rent|taxes|therapy|crush|social life|side hustle|resume|lawsuit|union|diary|skincare routine|" +
        "morning routine|body language|energy|breaking-news powers|escape report|press conference|meeting";
    private const string Have =
        "need|needs|needed|got|gets|has|have|had|having|want|wants|wanted|deserves?|owns?|own|hired|joined|keeps|kept";

    /// <summary>
    /// A thing written as if it were a person with a human job, habit or life, as a punchline: "my
    /// typo got a manager", "why does my keyboard need manners", "is my phone doing attendance",
    /// "your blank email asks for help". Nobody reacting out loud talks like that, and CobbleCat
    /// never does - tested against his 20 transcripts: 0 hits, against our 35 scripts: 8.
    /// Talking TO the thing ("why are you shaped like this?") is his and is not matched.
    /// </summary>
    private static readonly Regex PersonAsJoke = new(string.Join("|",
        $@"\b(?:{Have})\s+(?:a |an |its |their |some |the |his |her |any )?(?:own |tiny |little |whole |new )?(?:{Roles})\b",
        @"\b(?:doing|does|did|do)\s+(?:its |their |the )?(?:attendance|homework|taxes|paperwork|cardio|yoga|karate)\b",
        @"\b(?:failed|passed|took)\s+(?:a |an |its |the )?(?:test|exam|quiz)\b",
        @"\bfiling\s+(?:a |an )?(?:[a-z]+\s+){0,2}(?:report|complaint|lawsuit)\b",
        @"\basks?\s+for\s+help\b",
        @"\b(?:clocked (?:in|out)|called in sick|went on strike|got (?:promoted|fired|hired)|doing peekaboo|throwing a tantrum)\b",
        @"\b(?:accepting|approving|judging)\s+[a-z]+\s+behavior\b"),
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Fillers piled up back to back ("Kinda, actually, forgot milk.", "Literally, why..., I guess?")
    /// - a script padding out its drawn filler count. He uses one at a time.
    /// </summary>
    private static readonly Regex FillerPile = new(
        @"\b(?:like|literally|kinda|actually|wait|oh|so|okay|honestly|basically|i guess)\s*,?\s+(?:like|literally|kinda|actually|wait|oh|so|okay|honestly|basically|i guess)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Beats the read-aloud pass must rewrite: the thing written as a person, fillers piled up, and
    /// - when this video did not draw a comparison - game/show/meme comparisons ("like Minecraft
    /// loot dropping out of weather", "like Mario Kart items" got through in 2 of 5 on 2026-09-30,
    /// because a soft check only blocks the first two attempts).
    /// </summary>
    public static List<int> MustRewriteBeats(IReadOnlyList<string> beats, bool comparisonAllowed, string ownWords)
    {
        var found = new SortedSet<int>(PersonAsJokeBeats(beats));
        foreach (var i in BeatsMatching(beats, FillerPile)) found.Add(i);
        if (!comparisonAllowed)
        {
            var own = ownWords.ToLowerInvariant();
            foreach (var i in BeatsMatching(beats, GameReference, m => !own.Contains(m.Value.ToLowerInvariant())))
                found.Add(i);
        }
        return found.ToList();
    }

    private static IEnumerable<int> BeatsMatching(IReadOnlyList<string> beats, Regex rx, Func<Match, bool>? keep = null)
    {
        var starts = new List<int>();
        var text = "";
        foreach (var b in beats)
        {
            starts.Add(text.Length);
            text += b + " ";
        }
        var found = new SortedSet<int>();
        foreach (Match m in rx.Matches(text))
        {
            if (keep is not null && !keep(m)) continue;
            for (int i = 0; i < beats.Count; i++)
            {
                int s = starts[i], e = s + beats[i].Length;
                if (s < m.Index + m.Length && m.Index < e) found.Add(i);
            }
        }
        return found;
    }

    /// <summary>Indexes of the beats that hold a <see cref="PersonAsJoke"/> line (a match may span two beats).</summary>
    public static List<int> PersonAsJokeBeats(IReadOnlyList<string> beats)
    {
        var starts = new List<int>();
        var text = "";
        foreach (var b in beats)
        {
            starts.Add(text.Length);
            text += b + " ";
        }
        var found = new SortedSet<int>();
        foreach (Match m in PersonAsJoke.Matches(text))
            for (int i = 0; i < beats.Count; i++)
            {
                int s = starts[i], e = s + beats[i].Length;
                if (s < m.Index + m.Length && m.Index < e) found.Add(i);
            }
        return found.ToList();
    }

    /// <summary>Applies the drawn title choices the model does not reliably follow itself.</summary>
    public static string StyleTitle(string title, ListStyle style)
    {
        title = title.Trim();
        // 26% of his titles are shouted outright; asked to, the model wrote 0 of 32.
        if (style.TitleShout == "all") title = title.ToUpperInvariant();
        // 10% of his trail off ("Firefox's DARK FUTURE..."); ours never did.
        if (style.TitleTrail && !title.EndsWith("...") && !title.EndsWith("…") && !title.EndsWith("?") && !title.EndsWith("!"))
            title = title.TrimEnd('.') + "...";
        return title;
    }

    // --- endings -------------------------------------------------------------------------------

    /// <summary>The words of a script's last sentence in order, lower case, "thanks" read as "thank".</summary>
    public static string EndingText(string script)
    {
        var sentences = Sentences(script);
        if (sentences.Count == 0) return "";
        return string.Join(" ", Regex.Matches(sentences[^1].ToLowerInvariant(), @"[a-z']+")
            .Select(m => m.Value.Trim('\''))
            .Where(w => w.Length > 0)
            .Select(w => w == "thanks" ? "thank" : w));
    }

    public static HashSet<string> EndingWords(string script) =>
        EndingText(script).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    /// <summary>
    /// Stock refusals that fit the end of ANY video. In 8 test scripts: "no thanks!" three times,
    /// then "oh please!", "oh, I'm done!", "oh, absolutely not!". Asking in the prompt was not enough.
    /// </summary>
    private static readonly Regex GenericEnding = new(
        @"^(?:(?:oh|okay|ok|nah|yeah|nope|um|so|and)\s+)*(?:no\s+thank(?:\s+you)?|i'?m\s+(?:so\s+)?(?:done|out)|absolutely\s+not|oh\s+please|please\s+no|nope|hard\s+pass|not\s+today|no\s+way|no)\s*$",
        RegexOptions.Compiled);

    /// <summary>A generic refusal, or 60%+ the same words as a recent script's last sentence.</summary>
    public static bool IsStaleEnding(string endingText, IReadOnlyList<HashSet<string>> recent)
    {
        var ending = endingText.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (ending.Count == 0) return false;
        if (GenericEnding.IsMatch(endingText)) return true;
        return recent.Any(r =>
        {
            if (r.SetEquals(ending)) return true;
            var shared = r.Intersect(ending).Count();
            return shared >= 2 && shared >= 0.6 * r.Union(ending).Count();
        });
    }

    // --- openers -------------------------------------------------------------------------------

    /// <summary>
    /// A word pair, or a filler/adverb, in this script's first sentence that the first sentences of
    /// two or more of the last 8 scripts already had - ", and man," or "weirdly [adjective]". Words
    /// of the subject and title don't count: two videos about AI may both say "AI".
    /// </summary>
    private static string? RepeatedOpener(string script, IReadOnlyList<string> recent, string ownWords)
    {
        var first = Sentences(script).FirstOrDefault();
        if (first is null) return null;
        var own = Tokens(ownWords).ToHashSet();
        var mine = Features(first, own);
        var counts = new Dictionary<string, int>();
        foreach (var r in recent.Take(8))
        {
            var f = Sentences(r).FirstOrDefault();
            if (f is null) continue;
            foreach (var feat in Features(f, own)) counts[feat] = counts.GetValueOrDefault(feat) + 1;
        }
        return mine.FirstOrDefault(f => counts.GetValueOrDefault(f) >= 2);
    }

    private static HashSet<string> Features(string sentence, HashSet<string> own)
    {
        var t = Tokens(sentence);
        var set = new HashSet<string>();
        foreach (var w in t)
            if (!own.Contains(w) && (TicWords.Contains(w) || (w.EndsWith("ly") && w.Length > 4)))
                set.Add(w);
        for (int i = 0; i + 1 < t.Count; i++)
        {
            if (own.Contains(t[i]) || own.Contains(t[i + 1])) continue;
            if (Stop.Contains(t[i]) && Stop.Contains(t[i + 1])) continue;   // "and the", "it is"
            set.Add(t[i] + " " + t[i + 1]);
        }
        return set;
    }

    // --- subjects and titles -------------------------------------------------------------------

    /// <summary>A past subject that already contains every content word of this one ("hostile benches").</summary>
    private static string? RepeatsSubject(string subject, IReadOnlyList<string> past)
    {
        var mine = Content(subject);
        if (mine.Count == 0) return null;
        return past.FirstOrDefault(p =>
        {
            var theirs = Content(p);
            return mine.All(theirs.Contains);
        });
    }

    /// <summary>A shouted word or phrase (2+ capital letters) this title shares with a recent one. NOT is his favourite, so it may repeat.</summary>
    private static string? ReusedShout(string title, IReadOnlyList<string> recentTitles)
    {
        var mine = Shouts(title);
        if (mine.Count == 0) return null;
        var recent = recentTitles.Where(t => Shouts(t).Count < Regex.Matches(t, @"[\p{L}']+").Count) // skip fully shouted titles
            .SelectMany(Shouts).ToHashSet();
        return mine.FirstOrDefault(recent.Contains);
    }

    private static List<string> Shouts(string title) =>
        Regex.Matches(title, @"\b[A-Z][A-Z'’]+(?:\s+[A-Z][A-Z'’]+)*\b")
            .Select(m => m.Value)
            .Where(s => s != "NOT" && s.Length >= 2 && !Regex.IsMatch(s, @"^(AI|TV|USB|NASA|QR|UK|US|PC|OK)$"))
            .ToList();

    private static HashSet<string> Content(string s) =>
        Tokens(s).Where(w => !Stop.Contains(w) && !TicWords.Contains(w)).Select(CommonsRelevance.Singular).ToHashSet();

    private static List<string> Tokens(string s) =>
        Regex.Matches(s.ToLowerInvariant(), @"[a-z0-9']+").Select(m => m.Value.Trim('\'')).Where(w => w.Length > 0).ToList();

    private static List<string> Sentences(string script) =>
        Regex.Split(script.Trim(), @"(?<=[.!?])\s+").Where(s => s.Trim().Length > 0).ToList();
}
