using System.Text;
using System.Text.RegularExpressions;
using MemeShorts.Api.Models;
using Microsoft.Extensions.Options;

namespace MemeShorts.Api.Services;

/// <summary>A swear word found in a pasted picture, and the box it occupies in IMAGE pixels.</summary>
public sealed record ProfanityHit(string Word, int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>
/// Finds the swear words in a pasted screenshot so they can be blurred out before it is filmed.
///
/// The geometry is free: OCR already reports a box per WORD, so a hit is covered exactly where
/// it sits instead of censoring a whole line. Nothing is rewritten or re-typeset - the video
/// still shows the original screenshot's pixels, minus a smear over the word.
///
/// Two rules keep it off innocent words, which matters more than catching every last spelling:
///
///   - most entries match a WHOLE word (plus ordinary endings), so "class", "assume", "spicy",
///     "hello", "shell", "cocktail" and "Dickinson" are all left alone. Only stems that are
///     profane in EVERY word they appear in ("fuck", "shit", "cunt") match as substrings, and
///     those carry an allow list for the handful of real words containing them - Scunthorpe
///     being the famous one;
///   - the checks run on a NORMALISED token: lowercased, leet folded back to letters ("sh1t",
///     "f@ck"), punctuation dropped ("f.u.c.k", "fuck!"), elongation collapsed ("fuuuuck").
///     Masked spellings ("f*ck", "s**t") match through a separate wildcard pass.
/// </summary>
public sealed class ProfanityFilter
{
    private readonly ProfanityOptions _opt;

    /// <summary>Stems that are profane inside ANY word, so they match as substrings.</summary>
    private static readonly string[] StrongStems =
    {
        "fuck", "shit", "cunt", "whore", "wank", "jizz", "faggot", "cocksuck",
        "asshole", "arsehole", "dickhead", "dumbass", "jackass", "nigg",
    };

    /// <summary>Whole words (plus ordinary endings). A substring match here would be wrong.</summary>
    private static readonly string[] StrongWords =
    {
        "bitch", "bitchy", "dick", "cock", "pussy", "prick", "slut", "skank", "douche",
        "douchebag", "twat", "tit", "tits", "titty", "titties", "tiddy", "tiddies",
        "bollock", "bollocks",
        "jerkoff", "blowjob", "handjob", "boner", "dildo", "cum", "cumshot", "pube", "pubes",
        "nigger", "nigga", "fag", "dyke", "tranny", "retard", "wetback", "chink", "spic",
        "kike", "gook", "bastard",
    };

    /// <summary>
    /// The mild end. Kept separate because blurring "damn" and "hell" visibly censors posts
    /// nobody would call profane; <see cref="ProfanityOptions.IncludeMild"/> decides.
    /// </summary>
    private static readonly string[] MildWords =
    {
        "ass", "arse", "damn", "goddamn", "goddamnit", "damnit", "dammit", "hell",
        "crap", "crappy", "piss", "bugger", "wtf", "stfu", "omfg", "lmfao",
    };

    /// <summary>
    /// Real words that contain a strong stem. Without these Scunthorpe gets blurred - which is
    /// exactly why substring matching is limited to stems in the first place.
    /// </summary>
    private static readonly string[] InnocentContaining =
    {
        "scunthorpe", "penistone", "shiitake", "niggl", "niggard", "cockburn",
    };

    /// <summary>
    /// Endings a swear word takes without becoming a different word. Deliberately NOT "y" or
    /// "ish": those turn "spic" into "spicy" and would blur an innocent word.
    /// </summary>
    private static readonly string[] Endings =
    {
        "", "s", "es", "ed", "d", "ing", "in", "ings", "er", "ers", "az", "z",
    };

    /// <summary>Digits and symbols a post uses in place of letters.</summary>
    private static readonly Dictionary<char, char> Leet = new()
    {
        ['4'] = 'a', ['@'] = 'a', ['8'] = 'b', ['3'] = 'e', ['6'] = 'g', ['9'] = 'g',
        ['1'] = 'i', ['!'] = 'i', ['|'] = 'i', ['0'] = 'o', ['5'] = 's', ['$'] = 's',
        ['7'] = 't', ['+'] = 't', ['2'] = 'z',
    };

    /// <summary>
    /// Characters standing in for a letter the writer would not print. Kept OUT of
    /// <see cref="Leet"/> on purpose: these mean "any letter", not one specific letter.
    /// </summary>
    private const string MaskChars = "*#%&";

    /// <summary>Punctuation shaved off both ends before anything else: "fuck!", "**shit**".</summary>
    private static readonly char[] EdgePunctuation =
        " \t.,!?;:\"'`()[]{}<>*-_/\\|~^‘’“”…–—".ToCharArray();

    private readonly HashSet<string> _words;
    private readonly List<string> _stems;
    private readonly List<string> _allow;
    private readonly List<string> _all;

    public ProfanityFilter(IOptions<ProfanityOptions> opt)
    {
        _opt = opt.Value;

        var allow = InnocentContaining.Concat(_opt.Allow ?? Array.Empty<string>())
            .Select(Normalize).Where(w => w.Length > 0).ToHashSet();

        _words = StrongWords
            .Concat(_opt.IncludeMild ? MildWords : Array.Empty<string>())
            .Concat(_opt.Extra ?? Array.Empty<string>())
            .Select(Normalize)
            .Where(w => w.Length > 0 && !allow.Contains(w))
            .ToHashSet();

        _stems = StrongStems.Select(Normalize).Where(s => !allow.Contains(s)).ToList();
        _allow = allow.ToList();
        _all = _words.Concat(_stems).Distinct().ToList();
    }

    public bool Enabled => _opt.Enabled;

    /// <summary>
    /// Every swear word in an OCR pass, with where it sits.
    ///
    /// Adjacent words are also tested JOINED, because OCR splits a word in two of its own accord
    /// ("fu ck"). The join is only tried when the SECOND word is innocent on its own - otherwise
    /// every word preceding a swear word joins into one ("the" + "fuck") and gets blurred along
    /// with it, which covers half the sentence.
    /// </summary>
    public IReadOnlyList<ProfanityHit> Scan(IReadOnlyList<OcrLineBox> lines)
    {
        var hits = new List<ProfanityHit>();
        if (!_opt.Enabled) return hits;

        foreach (var line in lines)
        {
            // No word geometry (the null OCR, or an engine reporting only lines): fall back to
            // the line's own box, so the word is still covered even if generously.
            if (line.Words.Count == 0)
            {
                if (line.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(IsProfane))
                    hits.Add(new ProfanityHit(line.Text, line.Left, line.Top, line.Right, line.Bottom));
                continue;
            }

            for (int i = 0; i < line.Words.Count; i++)
            {
                var word = line.Words[i];
                if (IsProfane(word.Text))
                {
                    hits.Add(new ProfanityHit(word.Text, word.Left, word.Top, word.Right, word.Bottom));
                    continue;
                }

                if (i + 1 < line.Words.Count
                    && !IsProfane(line.Words[i + 1].Text)
                    && IsProfane(word.Text + line.Words[i + 1].Text))
                {
                    var next = line.Words[i + 1];
                    hits.Add(new ProfanityHit(word.Text + " " + next.Text,
                        Math.Min(word.Left, next.Left), Math.Min(word.Top, next.Top),
                        Math.Max(word.Right, next.Right), Math.Max(word.Bottom, next.Bottom)));
                    i++;
                }
            }
        }

        return hits;
    }

    /// <summary>Is this one token a swear word?</summary>
    public bool IsProfane(string raw)
    {
        var token = raw.Trim(EdgePunctuation);
        if (token.Length == 0) return false;

        var norm = Normalize(token);
        if (Matches(norm)) return true;

        // "sh1t" comes back from OCR as "shlt", not "sh1t" - the reader resolves the digit to a
        // letter before this ever sees it, so folding "1" to "i" is too late and "l" has to be
        // folded as well. Tried as an EXTRA spelling rather than a blanket substitution, and
        // only for the l-to-i confusion: no real word turns profane under it ("shlt" is not a
        // word), whereas swapping letters freely would start inventing hits.
        if (norm.Contains('l') && Matches(norm.Replace('l', 'i'))) return true;

        // "fuuuuck". No English word has a letter three times in a row, so the collapsed
        // spellings are only ever computed for a token that was deliberately stretched - which
        // is what stops "ass" from being tested as "as".
        if (HasRun(norm, 3) && (Matches(Collapse(norm, 1)) || Matches(Collapse(norm, 2))))
            return true;

        return MaskedMatch(token);
    }

    private bool Matches(string norm)
    {
        if (norm.Length < 3) return false;
        if (_allow.Any(norm.Contains)) return false;

        foreach (var ending in Endings)
            if (norm.Length > ending.Length
                && norm.EndsWith(ending, StringComparison.Ordinal)
                && _words.Contains(norm[..(norm.Length - ending.Length)]))
                return true;

        return _stems.Any(norm.Contains);
    }

    /// <summary>
    /// "f*ck" / "s**t": each mask stands for one unknown letter, so the token becomes a pattern
    /// and the word list is searched for anything that fits it. Only reached when the token
    /// really contains a mask character, and only while two real letters survive - otherwise
    /// "***" would match every word there is.
    /// </summary>
    private bool MaskedMatch(string token)
    {
        if (!token.Any(MaskChars.Contains)) return false;

        var pattern = new StringBuilder("^");
        int letters = 0;
        foreach (var raw in token.ToLowerInvariant())
        {
            if (MaskChars.Contains(raw)) { pattern.Append('.'); continue; }
            var ch = Leet.TryGetValue(raw, out var mapped) ? mapped : raw;
            if (ch is >= 'a' and <= 'z') { pattern.Append(ch); letters++; }
        }
        if (letters < 2) return false;
        pattern.Append('$');

        var rx = new Regex(pattern.ToString(), RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        try { return _all.Any(w => rx.IsMatch(w)); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    /// <summary>Lowercase, leet folded back to letters, everything else dropped.</summary>
    private static string Normalize(string token)
    {
        var sb = new StringBuilder(token.Length);
        foreach (var raw in token.ToLowerInvariant())
        {
            var ch = Leet.TryGetValue(raw, out var mapped) ? mapped : raw;
            if (ch is >= 'a' and <= 'z') sb.Append(ch);
        }
        return sb.ToString();
    }

    private static bool HasRun(string s, int n)
    {
        int run = 1;
        for (int i = 1; i < s.Length; i++)
        {
            run = s[i] == s[i - 1] ? run + 1 : 1;
            if (run >= n) return true;
        }
        return false;
    }

    /// <summary>Shorten every run of the same letter to at most <paramref name="keep"/>.</summary>
    private static string Collapse(string s, int keep)
    {
        var sb = new StringBuilder(s.Length);
        int run = 0;
        for (int i = 0; i < s.Length; i++)
        {
            run = i > 0 && s[i] == s[i - 1] ? run + 1 : 0;
            if (run < keep) sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
