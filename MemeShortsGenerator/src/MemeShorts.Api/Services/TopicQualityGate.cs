using System.Text.RegularExpressions;

namespace MemeShorts.Api.Services;

/// <summary>
/// Local sanity checks for auto-brainstormed topics. This runs before jobs are queued,
/// so weak topics never reach storyboard, TTS, panel generation, or rendering.
/// </summary>
public static class TopicQualityGate
{
    private static readonly Regex WordRegex = new("[a-z0-9]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SpaceRegex = new(@"\s+", RegexOptions.Compiled);

    private static readonly Regex[] ReferencePatterns =
    {
        new(@"\bwhich .+ would you (pick|choose)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bwhat .+ would you choose\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bwhat .+ looks? like\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bhow to .+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bhow .+ (works?|gets?|became|becomes|made|updates?|fails?|popular|wins?)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bwhy .+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bsecret behind .+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\b(real world|real life|irl) vs .+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\b.+ vs .+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bevolution of .+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\b(worst|best|goofiest|coolest|most|funniest|evilest|hardest) .+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bways to .+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bwhen you .+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bthis is (why|how|what) .+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\b.+ explained\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\b.+ memes?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\b.+ be like\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\b.+ experience\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };

    // Only genuinely contentless placeholders belong here — a topic naming a real subject is
    // never rejected outright.
    //
    // "plugging in a usb experience" and "self checkout experience" used to sit in this list.
    // Channel view data showed they were the #3 and #11 best-performing videos ever made, so
    // the gate was silently blocking the channel's strongest format. Do not re-add specific
    // everyday-frustration premises here.
    private static readonly string[] HardRejects =
    {
        "funny relatable stuff",
        "random meme",
        "meme idea",
        "everyday annoyance",
        "weird thing",
        "absurd thing",
        "something random",
        "generic relatable",
    };

    private static readonly string[] NonsenseSignals =
    {
        "eating itself",
        "filing taxes",
        "has lawyers",
        "legal team",
        "commits crime",
        "gets arrested",
        "goes to court",
        "existential crisis",
    };

    // The gate requires one of these to appear, so anything missing from this list is rejected
    // outright. Kept broad on purpose: the channel's breakouts are all ordinary household /
    // machine / game subjects, and an over-tight list here silently starves that category.
    private static readonly string[] FamiliarSubjects =
    {
        "adult", "alarm", "animal", "apple", "bag", "bath", "bathroom", "bed", "boss", "box",
        "browser", "bus", "button", "cake", "camel", "car", "cartoon", "cat", "centaur", "cereal",
        "cereal box", "chair", "chart",
        "chess", "chip", "cloud", "coffee", "comic", "computer", "cookie", "cup", "daughter",
        "dinosaur", "disaster", "doctor", "door", "elevator", "electricity", "exit door", "food",
        "fortune cookie", "fridge", "game", "giraffe", "hand", "headphone",
        "guy", "home", "house", "ice cream", "internet", "joke", "juice box", "keyboard", "karma", "life", "life hack",
        "light", "line", "loading", "loading bar", "logo", "map", "mascot", "meme", "minecraft", "movie",
        "newspaper", "note", "package", "paper", "parent", "phone", "physics", "power", "printer", "product",
        "remote", "remote button", "rent", "restaurant",
        "people", "person", "road", "school", "shirt", "shopping cart", "shark", "shower", "sign", "snack",
        "soda", "soda mascot", "store", "straw",
        "superpower", "swimming", "teacher", "toilet paper", "trash can", "usb", "vampire", "vending machine", "water",
        "well", "wifi", "world",
    };

    private static readonly string[] ComedySignals =
    {
        "after", "behind", "be like", "choose", "coolest", "difficult", "difficulty", "evil", "evilest",
        "evolution", "explained", "fails", "goofiest", "hardest", "hidden", "how", "made",
        "meta", "perfect", "pick", "play", "popular", "prank", "really", "save", "secret",
        "truth", "update", "updating", "vs", "want", "wants", "wear", "when", "which", "why",
        "with your hands", "wrong", "on the run",
    };

    private static readonly string[] AbstractSubjects =
    {
        "algorithm", "circle", "concept", "emotion", "graph", "idea", "number", "shape", "square",
        "triangle",
    };

    public static bool IsAcceptable(string? topic, string contentType, out string reason)
    {
        var clean = Normalize(topic);
        if (clean.Length == 0)
        {
            reason = "empty topic";
            return false;
        }

        var words = WordRegex.Matches(clean).Select(m => m.Value).ToArray();
        if (words.Length < 3)
        {
            reason = "too short to be a clear premise";
            return false;
        }

        if (ContainsAny(clean, HardRejects))
        {
            reason = "generic or known weak premise";
            return false;
        }

        if (IsGeneric(words))
        {
            reason = "too vague/generic";
            return false;
        }

        if (contentType.Equals("finance", StringComparison.OrdinalIgnoreCase))
        {
            reason = "";
            return true;
        }

        if (words.Length > 11)
        {
            reason = "too long for a clean Shorts premise";
            return false;
        }

        bool hasReferencePattern = ReferencePatterns.Any(r => r.IsMatch(clean));
        bool hasFamiliarSubject = ContainsAny(clean, FamiliarSubjects);
        bool hasComedySignal = ContainsAny(clean, ComedySignals);
        bool hasAbstractSubject = ContainsAny(clean, AbstractSubjects);
        int subjectCount = CountMatches(clean, FamiliarSubjects);

        // NOTE: membership in FamiliarSubjects is a scoring signal, NOT a hard requirement.
        // It used to be a hard reject, which meant any everyday subject missing from that
        // hand-maintained list ("velcro", "zipper", "escalator", …) was thrown out before it
        // could ever be made — starving the exact relatable-frustration category that produces
        // this channel's breakouts. Grounding is now judged by the rules below plus the score.

        if (ContainsAny(clean, NonsenseSignals) && !hasReferencePattern)
        {
            reason = "random abstract/personified action without a clear hook";
            return false;
        }

        if (hasAbstractSubject && !hasReferencePattern && subjectCount < 2)
        {
            reason = "abstract topic needs a clear reference-style hook";
            return false;
        }

        // Truly unanchored: no title hook, no comedy signal, no recognizable subject — AND too
        // thin to be a premise on its own. A longer, specific noun phrase is allowed through:
        // the channel's #5 video ("the emotional support traffic cone") has no hook word at all,
        // it just names something absurd and concrete.
        bool isDescriptive = words.Length >= 5;
        if (!hasReferencePattern && !hasComedySignal && !hasFamiliarSubject && !isDescriptive)
        {
            reason = "missing a clear comedy/title hook";
            return false;
        }

        var score = 0;
        if (hasReferencePattern) score += 3;
        if (hasFamiliarSubject) score += 2;
        if (hasComedySignal) score += 2;
        if (subjectCount >= 2) score += 2;
        if (isDescriptive) score += 2; // a fleshed-out premise, even without a listed subject
        // No "experience" penalty: the channel's #3 all-time video is an "... Experience" topic.
        if (hasAbstractSubject && !hasReferencePattern) score -= 2;

        // Threshold is 2, not 4: FamiliarSubjects membership used to be mandatory, so every
        // surviving topic carried a guaranteed +2. Now that it's optional, the old threshold
        // would reject grounded everyday topics whose noun simply isn't on the list.
        if (score < 2)
        {
            reason = "not grounded enough for the reference-channel style";
            return false;
        }

        reason = "";
        return true;
    }

    public static string FamilyKey(string? topic, string contentType)
    {
        var clean = Normalize(topic);
        if (clean.Length == 0) return "";

        if (contentType.Equals("finance", StringComparison.OrdinalIgnoreCase))
            return "finance:" + clean;

        var subject = FindMainSubject(clean);
        if (!string.IsNullOrWhiteSpace(subject))
            return "meme:" + CanonicalSubject(subject);

        var fallbackWords = WordRegex.Matches(clean)
            .Select(m => m.Value)
            .Where(w => !FamilyStopWords.Contains(w))
            .Take(3)
            .ToArray();

        return fallbackWords.Length == 0 ? "meme:" + clean : "meme:" + string.Join(" ", fallbackWords);
    }

    private static string Normalize(string? topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return "";
        var lower = topic.Trim().ToLowerInvariant();
        lower = WordRegex.Replace(lower, m => m.Value);
        lower = Regex.Replace(lower, @"[^a-z0-9]+", " ");
        return SpaceRegex.Replace(lower, " ").Trim();
    }

    private static bool ContainsAny(string clean, IEnumerable<string> terms) =>
        terms.Any(t => ContainsTerm(clean, t));

    private static int CountMatches(string clean, IEnumerable<string> terms) =>
        terms.Count(t => ContainsTerm(clean, t));

    private static bool ContainsTerm(string clean, string term)
    {
        if (term.Contains(' ', StringComparison.Ordinal))
            return clean.Contains(term, StringComparison.OrdinalIgnoreCase);
        return Regex.IsMatch(clean, $@"\b{Regex.Escape(term)}s?\b", RegexOptions.IgnoreCase);
    }

    private static bool IsGeneric(IReadOnlyCollection<string> words)
    {
        var generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "about", "absurd", "annoying", "comedy", "everyday", "funny", "generic",
            "idea", "meme", "memes", "random", "relatable", "short", "stuff", "thing",
            "things", "weird",
        };
        return words.Count(w => !generic.Contains(w)) == 0;
    }

    private static string? FindMainSubject(string clean)
    {
        return FamiliarSubjects
            .Select(term => new { Term = term, Index = IndexOfTerm(clean, term) })
            .Where(x => x.Index >= 0 && !FamilyIgnoreSubjects.Contains(x.Term))
            .OrderBy(x => x.Index)
            .ThenByDescending(x => x.Term.Length)
            .Select(x => x.Term)
            .FirstOrDefault();
    }

    private static int IndexOfTerm(string clean, string term)
    {
        if (!ContainsTerm(clean, term)) return -1;
        var singularIndex = clean.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (singularIndex >= 0) return singularIndex;
        if (!term.Contains(' ', StringComparison.Ordinal)) return -1;

        var lastSpace = term.LastIndexOf(' ');
        var plural = term[..lastSpace] + " " + term[(lastSpace + 1)..] + "s";
        return clean.IndexOf(plural, StringComparison.OrdinalIgnoreCase);
    }

    private static string CanonicalSubject(string subject) => subject switch
    {
        "cookie" or "fortune cookie" => "fortune cookie",
        "box" or "juice box" => "juice box",
        "loading" or "loading bar" => "loading",
        "remote" or "remote button" => "remote button",
        "soda" or "soda mascot" or "mascot" => "mascot",
        "cereal" or "cereal box" => "cereal",
        _ => subject,
    };

    private static readonly HashSet<string> FamilyIgnoreSubjects = new(StringComparer.OrdinalIgnoreCase)
    {
        "adult", "chart", "guy", "life", "people", "person", "world",
    };

    private static readonly HashSet<string> FamilyStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "about", "after", "again", "an", "are", "behind", "be", "being", "do", "does",
        "explained", "for", "from", "gets", "goes", "how", "in", "into", "is", "made",
        "of", "predict", "predicts", "really", "secret", "the", "this", "to", "vs", "what",
        "when", "which", "why", "with", "work", "works", "would", "you",
    };
}
