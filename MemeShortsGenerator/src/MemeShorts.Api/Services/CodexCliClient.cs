using System.Text.Json;
using System.Text.RegularExpressions;
using MemeShorts.Api.Models;
using Microsoft.Extensions.Options;

namespace MemeShorts.Api.Services;

/// <summary>
/// Drives the Codex CLI for (1) the storyboard JSON and (2) each panel PNG.
/// The exact command + base args are configurable so you can wire your proven
/// `codex exec` invocation without touching code.
/// </summary>
public sealed class CodexCliClient : ICodexClient
{
    private readonly CodexOptions _opt;
    private readonly ILogger<CodexCliClient> _log;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public CodexCliClient(IOptions<CodexOptions> opt, ILogger<CodexCliClient> log)
    {
        _opt = opt.Value;
        _log = log;
    }

    public async Task<Storyboard?> GenerateStoryboardAsync(string topic, string? format, IReadOnlyList<string>? styleTitles = null, string contentType = "meme", CancellationToken ct = default)
    {
        var prompt = contentType.Equals("finance", StringComparison.OrdinalIgnoreCase)
            ? Prompts.FinanceStoryboard(topic, styleTitles)
            : Prompts.Storyboard(topic, format, styleTitles);

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            var lastMsgFile = Path.Combine(Path.GetTempPath(), $"codex_sb_{Guid.NewGuid():n}.txt");
            try
            {
                var res = await RunCodexAsync(prompt, workingDir: null, extraArgs: new[] { "-o", lastMsgFile }, ct);
                string output = File.Exists(lastMsgFile)
                    ? await File.ReadAllTextAsync(lastMsgFile, ct)
                    : res.StdOut;

                var json = JsonExtract.FirstJsonObject(output);
                if (json is not null)
                {
                    var storyboard = JsonSerializer.Deserialize<Storyboard>(json, JsonOpts);
                    if (storyboard is { Beats.Count: > 0 }) return storyboard;
                }

                _log.LogWarning(
                    "Codex storyboard attempt {Attempt}/2 returned no usable JSON (exit {Code}). STDERR: {Err} STDOUT: {Out} FILE: {File}",
                    attempt, res.ExitCode, Trunc(res.StdErr), Trunc(res.StdOut), Trunc(output));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Codex storyboard attempt {Attempt}/2 failed.", attempt);
            }
            finally
            {
                try { if (File.Exists(lastMsgFile)) File.Delete(lastMsgFile); } catch { /* ignore */ }
            }

            if (attempt == 1)
                _log.LogInformation("Retrying Codex storyboard once for topic '{Topic}'.", topic);
        }

        return null;
    }

    public async Task<IReadOnlyList<string>> BrainstormTopicsAsync(int count, string contentType, IReadOnlyList<string>? styleTitles = null,
        IReadOnlyList<string>? avoidTopics = null, PerformanceSnapshot? performance = null, CancellationToken ct = default)
    {
        if (count <= 0) return Array.Empty<string>();
        var prompt = Prompts.TopicBrainstorm(count, contentType, styleTitles, avoidTopics, performance);
        var lastMsgFile = Path.Combine(Path.GetTempPath(), $"codex_topics_{Guid.NewGuid():n}.txt");
        try
        {
            var res = await RunCodexAsync(prompt, workingDir: null, extraArgs: new[] { "-o", lastMsgFile }, ct);
            string output = File.Exists(lastMsgFile) ? await File.ReadAllTextAsync(lastMsgFile, ct) : res.StdOut;

            var json = JsonExtract.FirstJsonObject(output);
            if (json is null)
            {
                _log.LogWarning("Codex topic brainstorm returned no JSON (exit {Code}).\nSTDERR: {Err}", res.ExitCode, Trunc(res.StdErr));
                return Array.Empty<string>();
            }

            var parsed = JsonSerializer.Deserialize<TopicList>(json, JsonOpts);
            var topics = (parsed?.Topics ?? new())
                .Select(t => t?.Trim() ?? "")
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(count)
                .ToList();
            return topics;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Codex topic brainstorm failed.");
            return Array.Empty<string>();
        }
        finally
        {
            try { if (File.Exists(lastMsgFile)) File.Delete(lastMsgFile); } catch { /* ignore */ }
        }
    }

    private sealed class TopicList { public List<string>? Topics { get; set; } }

    /// <summary>
    /// Share of self-picked subjects that react to something from the last few weeks. 7 of his 20
    /// latest Shorts do: 4 react to an announcement (a new ChatGPT model, a new game mod, a
    /// furniture company's game mod) and 3 to an online trend. Codex only knows what it was trained
    /// on, so those scripts run with live web search (checked 2026-09-27: it found five real
    /// gadgets from that month, each with a working source link).
    /// </summary>
    private const double NewsShare = 0.3;

    public async Task<ListIdea?> GenerateListIdeaAsync(IReadOnlyList<string> winningTitles,
        string? seedTopic = null, IReadOnlyList<string>? avoid = null, CancellationToken ct = default,
        ListHistory? history = null)
    {
        history ??= ListHistory.Empty;
        var rng = Random.Shared;
        var news = string.IsNullOrWhiteSpace(seedTopic) && rng.NextDouble() < NewsShare;
        // A random steer only when the caller didn't name a subject. Codex answers an identical
        // prompt identically, so "suggest again" needs something in the prompt to actually move.
        // News gets one too: without it, two test drafts in a row found the month's biggest
        // story (the same keychain gadget).
        var steer = string.IsNullOrWhiteSpace(seedTopic) ? PickSteer(rng) : null;
        // A script that misses a check is re-asked (see ListScriptChecks); only a HARD miss - wrong
        // length, a subject already covered - is refused on the last attempt, so a small stylistic
        // slip never costs the whole video.
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            var style = PickListStyle(rng, history);
            var prompt = Prompts.ListIdea(winningTitles, seedTopic, avoid, steer, style, history,
                news ? DateTime.Now : null);
            var idea = await TryListIdeaAsync(prompt, ct, news);
            if (idea is null)
            {
                if (!news) return null;
                // A failed search is not a failed video: fall back to an evergreen subject.
                _log.LogInformation("News script attempt failed; switching to an evergreen subject.");
                news = false;
                steer = PickSteer(rng);
                continue;
            }

            var problems = ListScriptChecks.Problems(idea, style, history);
            var blocking = attempt < 3 ? problems : problems.Where(p => p.Hard).ToList();
            if (blocking.Count > 0)
            {
                _log.LogWarning("List idea attempt {N} ('{Title}') re-asked: {Why}.",
                    attempt, idea.Title, string.Join("; ", problems.Select(p => p.Why)));
                continue;
            }
            if (problems.Count > 0)
                _log.LogInformation("Accepted '{Title}' on the last attempt despite: {Why}.",
                    idea.Title, string.Join("; ", problems.Select(p => p.Why)));

            await ReadAloudPassAsync(idea, style, ct);
            idea.Title = ListScriptChecks.StyleTitle(idea.Title, style);
            if (!string.IsNullOrWhiteSpace(idea.Source))
            {
                _log.LogInformation("News script '{Title}' is based on {Source}.", idea.Title, idea.Source);
                // Something from the last few weeks has no photograph in Commons yet, and an
                // exact-name search there finds an older, different model or nothing. Draw it.
                foreach (var item in idea.Items.Where(i => PictureKind.Normalize(i.Kind) == PictureKind.Real))
                    item.Kind = PictureKind.Object;
            }
            return idea;
        }
        return null;
    }

    private async Task<ListIdea?> TryListIdeaAsync(string prompt, CancellationToken ct, bool search = false)
    {
        var lastMsgFile = Path.Combine(Path.GetTempPath(), $"codex_listidea_{Guid.NewGuid():n}.txt");
        try
        {
            var res = await RunCodexAsync(prompt, workingDir: null, extraArgs: new[] { "-o", lastMsgFile }, ct, search);
            string output = File.Exists(lastMsgFile) ? await File.ReadAllTextAsync(lastMsgFile, ct) : res.StdOut;

            var json = JsonExtract.FirstJsonObject(output);
            if (json is null)
            {
                _log.LogWarning("Codex list idea returned no JSON (exit {Code}).\nSTDERR: {Err}",
                    res.ExitCode, Trunc(res.StdErr));
                return null;
            }

            var idea = JsonSerializer.Deserialize<ListIdea>(json, JsonOpts);
            if (idea is null) return null;

            // A line with no picture cannot be staged, so drop it rather than fail the whole idea.
            idea.Items = idea.Items
                .Where(i => !string.IsNullOrWhiteSpace(i.Text) && !string.IsNullOrWhiteSpace(i.Image))
                .ToList();
            idea.Title = (idea.Title ?? "").Trim('"', '“', '”', ' ');
            // Length, endings, openers and the rest are judged by ListScriptChecks.
            return idea.Items.Count == 0 ? null : idea;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Codex list idea failed.");
            return null;
        }
        finally
        {
            try { if (File.Exists(lastMsgFile)) File.Delete(lastMsgFile); } catch { /* temp */ }
        }
    }

    public async Task<string?> GenerateRepostTitleAsync(string postText, IReadOnlyList<string>? styleTitles = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(postText)) return null;
        var prompt = Prompts.RepostTitle(postText, styleTitles);
        var lastMsgFile = Path.Combine(Path.GetTempPath(), $"codex_reposttitle_{Guid.NewGuid():n}.txt");
        try
        {
            var res = await RunCodexAsync(prompt, workingDir: null, extraArgs: new[] { "-o", lastMsgFile }, ct);
            string output = File.Exists(lastMsgFile) ? await File.ReadAllTextAsync(lastMsgFile, ct) : res.StdOut;

            var json = JsonExtract.FirstJsonObject(output);
            if (json is null)
            {
                _log.LogWarning("Codex repost title returned no JSON (exit {Code}).\nSTDERR: {Err}", res.ExitCode, Trunc(res.StdErr));
                return null;
            }

            var title = JsonSerializer.Deserialize<TitleOnly>(json, JsonOpts)?.Title?.Trim();
            // Codex sometimes wraps the title in quotes despite being told not to.
            title = title?.Trim('"', '“', '”', ' ');
            return string.IsNullOrWhiteSpace(title) ? null : title;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Codex repost title failed.");
            return null;
        }
        finally
        {
            try { if (File.Exists(lastMsgFile)) File.Delete(lastMsgFile); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Subject areas to push a fresh suggestion towards, weighted by how often each turns up in
    /// CobbleCat's latest 200 Shorts (counted 2026-09-27).
    ///
    /// The old list was chosen for what Wikimedia Commons has photographs of - castles, coins,
    /// ships, old machines - and it steered every script into old-timey Wikipedia trivia (Coral
    /// Castle, an 1860 board game, a 1920s rail zeppelin). He almost never does history (~3%):
    /// his subjects are modern things his audience uses or sees online. Pictures are drawn now,
    /// so the picture source no longer has to decide the topic.
    /// </summary>
    private static readonly (int Weight, string Area)[] ListSteers =
    {
        (9, "computer and phone gadgets people actually use - mice, keyboards, controllers, remotes, chargers, cables, earbuds"),
        (7, "AI, apps and the internet - chatbots, AI features nobody asked for, browsers, search, cookies, wifi, websites"),
        (6, "video games and consoles - weird official accessories, game mechanics, console history, mods"),
        (6, "badly or bizarrely designed everyday things - chairs, signs, logos, packaging, buttons, public spaces"),
        (6, "everyday habits and body stuff everyone relates to - brushing teeth, sleep, cramps, sneezing, eyesight"),
        (5, "animals and nature being unexpectedly weird"),
        (4, "words and names - strange official colour names, place names, acronyms, product names, slang"),
        (4, "food, snacks and grocery stores - weird products, how stores trick you, food fails"),
        (3, "space and science you can picture - planets, weather, light, sound, materials"),
        (2, "maps and countries, as a quirky comparison rather than a history lesson"),
    };

    private static string PickSteer(Random rng)
    {
        var roll = rng.Next(ListSteers.Sum(s => s.Weight));
        foreach (var (weight, area) in ListSteers)
        {
            if (roll < weight) return area;
            roll -= weight;
        }
        return ListSteers[^1].Area;
    }

    /// <summary>
    /// How THIS video opens, which two devices it leans on, and how it ends - drawn at random in
    /// the proportions measured across CobbleCat's 20 latest Shorts (2026-09-26).
    ///
    /// Asking the model to "vary it" does nothing: every call reads the same prompt, so every
    /// script used every device, opened "X is real, and I hate..." (4 of 5) and ended on a shouted
    /// ALL-CAPS command (5 of 5, twice in a row). His videos each pick a different mix. Drawing
    /// the mix here is the only way one call can differ from the next.
    /// </summary>
    internal static ListStyle PickListStyle(Random rng, ListHistory history)
    {
        // Counted off his titles and transcripts: ~40% of his Shorts are ROUNDUPS ("these remotes",
        // "these designs", "the two worst mice") going through a few real examples with a quick
        // reaction to each; the rest follow one thing.
        var format = rng.NextDouble() < 0.4
            ? "FORMAT: a ROUNDUP - 3 to 5 real examples of one kind of thing (real products, real signs, " +
              "real names...), each shown and reacted to in a line or two, getting worse or weirder as " +
              "it goes. Beats that talk about the same example share its picture."
            : "FORMAT: ONE thing - what it is, the detail that makes it absurd, and your reaction.";
        // Counted off his 20 transcripts.
        var opener = Weighted(rng,
            (7, "a flat, confident statement of the surprising claim - no opinion yet"),
            (6, "the claim plus your immediate gut reaction to it, in the same sentence (do not use the words \"I hate\")"),
            (2, "an incredulous question about the thing on screen"),
            (1, "a deadpan piece of if-this-then-that literal logic about it"),
            (1, "the object itself talking in the first person, as a little role-play"));
        var ending = Weighted(rng,
            // Worded so each ending has to be about the thing: "a plain exclamation" and "as if you
            // gave up" came back as "no thanks!" or "oh, I'm done!" in 4 of 8 test scripts.
            (6, "a plain exclamation, in normal case, about a detail of this exact thing"),
            (5, "a shouted line about this thing, or a scream, in ALL CAPS"),
            (4, "an incredulous question about a detail of this thing"),
            (3, "a deadpan or completely unrelated remark, as if your brain just changed the subject"),
            (2, "breaking off mid-word, cut short by your own outrage"));
        // Forcing two devices into every script made each one read like a checklist - five of six
        // talked to the object, and one printed "Official announcer voice:" into the narration.
        // Weighted by a hand count of his 20 (2026-09-27): a back-and-forth or skit in ~4, a
        // question to the makers in ~4, an objection knocked down in 3, talking TO the thing in 3,
        // the thing's own voice in 3; about a quarter use none.
        var pool = new List<(int Weight, string Text)>
        {
            (4, "one question aimed at whoever made, named or sold it"),
            (4, "a quick back-and-forth: an imagined voice (a viewer, a fan of the thing, or the thing itself) says one short line in quotes and you answer it"),
            (3, "voicing a viewer's objection and knocking it straight down"),
            (3, "talking straight TO the thing for a line, asking it why it is like this"),
            (3, "giving the thing one short line of its own, as if it could talk"),
            (3, "one comparison to a game, show or meme the audience knows"),
            (3, "reading its actual product name or slogan out loud in disbelief"),
        };
        var nDevices = int.Parse(Weighted(rng, (3, "0"), (5, "1"), (2, "2")));
        var devices = new List<string>();
        for (int d = 0; d < nDevices; d++)
        {
            var pick = Weighted(rng, pool.ToArray());
            devices.Add(pick);
            pool.RemoveAll(p => p.Text == pick);
        }
        var deviceLine = devices.Count == 0
            ? "No special device this time: just say what it is plainly and react to it."
            : $"Use {string.Join(" and ", devices)} - nothing else from that kind of list.";
        // How often it says "you", exclaims, says I/me, asks, uses fillers and points at the
        // picture - and how long it is - taken TOGETHER from one of his own Shorts. The prompt used
        // to give rates ("about one sentence in five"), and the batch came back with the average
        // roughly right and no spread: 7 of 7 had exactly one or no exclamation, every one asked
        // something, and every script ran 54-77 words where his run 34-108.
        var (you, exclaims, firstPerson, questions, fillers, pointing, targetWords) = HisProfiles[rng.Next(HisProfiles.Length)];
        // The drawn opening and ending must not contradict the counts.
        if (opener.StartsWith("an incredulous question") || ending.StartsWith("an incredulous question"))
            questions = Math.Max(questions, 1);
        if (ending.StartsWith("a plain exclamation") || ending.StartsWith("a shouted line"))
            exclaims = Math.Max(exclaims, 1);
        var countsLine = string.Join(" ",
            you == 0
                ? "Never say \"you\" or \"your\"."
                : $"Say \"you\" or \"your\" about {Times(you)} - to the viewer (\"you can...\", \"your phone\") or straight to the thing on screen - but only where a person would; one fewer beats a forced one.",
            exclaims == 0
                ? "No sentence ends in \"!\"."
                : $"{exclaims} sentence{(exclaims == 1 ? "" : "s")} end{(exclaims == 1 ? "s" : "")} in \"!\" - short loud reactions.",
            firstPerson == 0
                ? "Never say \"I\", \"me\" or \"my\": react without talking about yourself."
                : $"Say \"I\", \"me\" or \"my\" {Times(firstPerson)} in total.",
            questions == 0
                ? "Ask no questions."
                : $"Ask {questions} question{(questions == 1 ? "" : "s")}.",
            // Ours ran at about half his fillers and half his pointing (3.1 vs 5.6 and 1.6 vs 2.5
            // per 100 words) - and the few we had were the SAME ones, ", and man," in 7 of 22.
            fillers == 0
                ? "No casual fillers."
                : $"Use casual spoken fillers {Times(fillers)} (like, literally, wait, oh, so, kinda, I guess, dude...) - a different mix from the latest scripts.",
            pointing == 0
                ? ""
                : $"Point at the picture with \"this\" or \"these\" {Times(pointing)}.",
            $"LENGTH: about {targetWords} spoken words - his run from 34 to 108, and this one is {targetWords}.");
        // Calling the object dude/buddy/bro: about one of his videos in four.
        var address = rng.NextDouble() < 0.25
            ? "You may call the thing dude, buddy or bro once."
            : "Do not address the thing directly as dude, buddy, bro, sir, or by name.";
        // TITLE shape. "<thing> is <JUDGEMENT>" was 56% of our 32 titles and is 17% of his 200 -
        // the old weights gave "starts with the subject's name and makes a blunt claim" 9 of 20,
        // and the model always made that claim "is SO WRONG". His other shapes: a NOT (his most
        // used shouted word, 13 of 200: "that is NOT a hamster", "do NOT break your chocolate bar
        // like this"), the thing DOING something ("our keyboards are HOLDING US BACK?!", "English
        // makes NO SENSE"), questions, "the"/"these" judgements.
        var titleShape = Weighted(rng,
            (3, "is the plain verdict \"<the thing> is/are <one judgement>\""),
            (5, "starts with the thing's everyday name and says what it DOES, HAS or MAKES, with a real verb - not \"is <judgement>\""),
            (3, "starts with \"the\" and a superlative or a reveal about the thing"),
            (2, "uses NOT - denying something about it, or warning the viewer not to do something"),
            (2, "starts with \"these\", \"this\" or \"that\" and passes judgement"),
            (2, "is a \"why is / why do / why are ...\" question of disbelief"),
            (2, "is a \"how are / how did ...\" question of disbelief"),
            (1, "is a first-person reaction to it, as if you just swore off something"));
        // His 200: 26% entirely shouted, 42% one or a few shouted words, 32% none. Asked for
        // "nearly all of it in ALL CAPS" 3 times in 10, the model wrote 0 of 32 - so the code
        // capitalises it afterwards (ListScriptChecks.StyleTitle).
        var titleShout = Weighted(rng, (26, "all"), (42, "some"), (32, "none"));
        var shoutText = titleShout switch
        {
            "all" => "one short, fully shouted line (it will be set in ALL CAPS)",
            "some" => "one or two words in ALL CAPS",
            _ => "no ALL-CAPS words",
        };
        var trail = rng.NextDouble() < 0.10;
        // Opinions only. "ILLEGAL", "LYING" and "A SCAM" were dropped: they turn a title into a
        // factual claim ("the meanest benches are ILLEGAL", "Labo VR was lying") that is false.
        // Words the latest titles already shouted are left out ("A MISTAKE" 3 times in a day).
        var recentShouts = history.RecentTitles.SelectMany(t => Regex.Matches(t, @"\b[A-Z][A-Z'’]+(?:\s+[A-Z][A-Z'’]+)*\b")
            .Select(m => m.Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var judgements = new[] { "WORST", "DUMBEST", "SO BAD", "CRAZY", "INSANE", "TERRIFYING", "CURSED", "WILD",
                                 "NOT", "A MISTAKE", "GENIUS", "OUT OF CONTROL", "SO WRONG", "EVERYWHERE",
                                 "NO SENSE", "OVERKILL" };
        var fresh = judgements.Where(j => j == "NOT" || !recentShouts.Contains(j)).ToArray();
        var judgement = fresh[rng.Next(fresh.Length)];
        var titleLine = $"TITLE: it {titleShape}, with {shoutText}"
                        + (trail ? ", trailing off with \"...\"" : "")
                        + $". If it needs a judgement word, lean towards {judgement} or your own - not UNHINGED.";
        var card = $"{format} Open with {opener}. {deviceLine} {address} End with {ending}. {countsLine} "
                   + "Never write the name of a device or a stage direction into the narration. " + titleLine;
        return new ListStyle(card, titleShout, trail, targetWords,
            devices.Any(d => d.StartsWith("one comparison to a game")));

        static string Times(int n) => n switch { 1 => "once", 2 => "twice", _ => $"{n} times" };

        static string Weighted(Random r, params (int weight, string text)[] options)
        {
            var roll = r.Next(options.Sum(o => o.weight));
            foreach (var (weight, text) in options)
            {
                if (roll < weight) return text;
                roll -= weight;
            }
            return options[^1].text;
        }
    }

    /// <summary>
    /// (you/your, "!" sentences, I/me/my, questions, casual fillers, "this/these/look", spoken
    /// words) in 18 of his 20 latest Shorts, counted off the whisper transcripts (2026-09-27, the
    /// last three on 2026-09-30). Left out: one that is mostly music and another person's clip, and
    /// one where half the words are a chatbot demo's voice.
    /// </summary>
    private static readonly (int You, int Exclaims, int FirstPerson, int Questions, int Fillers, int Pointing, int Words)[] HisProfiles =
    {
        (3, 1, 2, 2, 1, 2, 72), (1, 2, 0, 1, 2, 0, 67), (3, 1, 1, 3, 7, 3, 52), (3, 5, 3, 1, 3, 0, 57),
        (0, 2, 1, 0, 1, 0, 46), (0, 1, 0, 0, 4, 0, 41), (4, 1, 2, 0, 1, 0, 51), (0, 0, 2, 2, 3, 3, 67),
        (5, 3, 3, 3, 4, 1, 60), (1, 0, 0, 1, 4, 1, 34), (0, 0, 1, 0, 5, 1, 89), (5, 0, 4, 3, 0, 0, 62),
        (1, 0, 0, 1, 3, 1, 48), (1, 1, 1, 1, 6, 0, 64), (2, 2, 2, 3, 4, 2, 61), (1, 1, 0, 1, 7, 4, 40),
        (2, 1, 1, 1, 1, 2, 61), (2, 0, 1, 2, 3, 2, 45),
    };

    /// <summary>
    /// Reads the finished script back as a viewer would hear it and replaces lines nobody says out
    /// loud - the thing written as a person ("my typo got a manager!") or a clever written-only
    /// metaphor ("a cookout in dust form"). The pattern in ListScriptChecks catches the common
    /// shapes for free and they are handed over as must-fix; the model finds the rest. One short
    /// Codex call (~20k tokens). If it fails the script is kept as it was.
    /// </summary>
    private async Task ReadAloudPassAsync(ListIdea idea, ListStyle style, CancellationToken ct)
    {
        var beats = idea.Items.Select(i => i.Text).ToList();
        var caught = ListScriptChecks.MustRewriteBeats(beats, style.ComparisonAllowed, (idea.Subject ?? "") + " " + idea.Title);
        var lastMsgFile = Path.Combine(Path.GetTempPath(), $"codex_readaloud_{Guid.NewGuid():n}.txt");
        try
        {
            var res = await RunCodexAsync(Prompts.ReadAloudReview(beats, caught, style.ComparisonAllowed), workingDir: null,
                extraArgs: new[] { "-o", lastMsgFile }, ct);
            var output = File.Exists(lastMsgFile) ? await File.ReadAllTextAsync(lastMsgFile, ct) : res.StdOut;
            var json = JsonExtract.FirstJsonObject(output);
            var rewrites = json is null ? null : JsonSerializer.Deserialize<ReadAloudResult>(json, JsonOpts)?.Rewrites;
            if (rewrites is null)
            {
                _log.LogWarning("Read-aloud pass returned nothing usable for '{Title}'; keeping the script.", idea.Title);
                return;
            }
            foreach (var r in rewrites)
            {
                var i = r.Beat - 1;
                if (i < 0 || i >= idea.Items.Count || string.IsNullOrWhiteSpace(r.Text)) continue;
                _log.LogInformation("Read-aloud: beat {N} '{Old}' -> '{New}'.", r.Beat, idea.Items[i].Text, r.Text.Trim());
                idea.Items[i].Text = r.Text.Trim();
            }
            var left = ListScriptChecks.MustRewriteBeats(idea.Items.Select(i => i.Text).ToList(), style.ComparisonAllowed,
                (idea.Subject ?? "") + " " + idea.Title);
            if (left.Count > 0)
                _log.LogWarning("Read-aloud pass left a line it should have rewritten in '{Title}': beats {Beats}.",
                    idea.Title, string.Join(", ", left.Select(b => b + 1)));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Read-aloud pass failed for '{Title}'; keeping the script.", idea.Title);
        }
        finally
        {
            try { if (File.Exists(lastMsgFile)) File.Delete(lastMsgFile); } catch { /* temp */ }
        }
    }

    private sealed class ReadAloudResult { public List<ReadAloudRewrite>? Rewrites { get; set; } }
    private sealed class ReadAloudRewrite { public int Beat { get; set; } public string? Text { get; set; } }

    private sealed class TitleOnly { public string? Title { get; set; } }

    public async Task<bool> GenerateStockImageAsync(string subject, string outputPngPath, CancellationToken ct = default,
        bool isolated = true)
    {
        if (string.IsNullOrWhiteSpace(subject)) return false;
        var dir = Path.GetDirectoryName(outputPngPath)!;
        Directory.CreateDirectory(dir);

        var prompt = Prompts.StockImage(subject, isolated);
        var startUtc = DateTime.UtcNow.AddSeconds(-3);
        var work = PrivateWorkspace(dir);
        try
        {
            var res = await RunCodexAsync(prompt, workingDir: work, extraArgs: null, ct);
            if (File.Exists(outputPngPath) && new FileInfo(outputPngPath).Length > 0) return true;

            var generated = LocateGeneratedImage(res, startUtc) ?? NewestImageIn(work);
            if (generated is not null)
            {
                File.Copy(generated, outputPngPath, overwrite: true);
                _log.LogInformation("Codex drew a picture for '{Subject}'.", subject);
                return true;
            }
            _log.LogWarning("Codex produced no image for '{Subject}' (exit {Code}). {Err}",
                subject, res.ExitCode, Trunc(res.StdErr));
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Codex image generation failed for '{Subject}'.", subject);
            return false;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// A folder of this call's own to run Codex in. Asked to "save the PNG", newer Codex CLIs
    /// write it into their working directory under a name of their choosing. Several pictures for
    /// one video are generated side by side, so a shared folder would make "the image Codex just
    /// saved" ambiguous; a private one makes it whatever is in there.
    /// </summary>
    private static string PrivateWorkspace(string parent)
    {
        var work = Path.Combine(parent, "_codex_" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(work);
        return work;
    }

    private static string? NewestImageIn(string dir) =>
        Directory.Exists(dir)
            ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(f => f.Extension.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" && f.Length > 0)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName
            : null;

    public async Task<bool> GeneratePanelAsync(Beat beat, int width, int height, string outputPngPath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(outputPngPath)!;
        Directory.CreateDirectory(dir);

        var prompt = Prompts.PanelImage(beat, width, height);
        // image_gen writes to $CODEX_HOME/generated_images/<session>/ig_*.png. The Windows
        // sandbox blocks Codex from moving the file, so we locate it by session id and copy.
        var startUtc = DateTime.UtcNow.AddSeconds(-3);
        // Panels are generated in parallel (PanelConcurrency), so each call gets its own folder.
        var work = PrivateWorkspace(dir);

        try
        {
            var res = await RunCodexAsync(prompt, workingDir: work, extraArgs: null, ct);

            // Codex may have saved straight to the workspace in some setups.
            if (File.Exists(outputPngPath) && new FileInfo(outputPngPath).Length > 0)
                return true;

            var generated = LocateGeneratedImage(res, startUtc) ?? NewestImageIn(work);
            if (generated is not null)
            {
                File.Copy(generated, outputPngPath, overwrite: true);
                return true;
            }

            _log.LogWarning("Codex panel: no image found for '{Caption}' (exit {Code}). {Err}",
                beat.Caption, res.ExitCode, Trunc(res.StdErr));
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Codex panel generation failed for beat '{Caption}'.", beat.Caption);
            return false;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Find the PNG that image_gen just produced for this Codex session.</summary>
    private string? LocateGeneratedImage(ProcessResult res, DateTime startUtc)
    {
        var genRoot = GeneratedImagesRoot();
        if (!Directory.Exists(genRoot)) return null;

        // Prefer the session-specific folder (safe under concurrent panel generation).
        //
        // ANY png in it, not just ig_*.png: CLI v0.155 names them call_<id>.png / exec-<id>.png,
        // and with the old filter every picture Codex drew was thrown away as "no image" - the
        // list short then held the previous picture over the line it was meant to show.
        var combined = res.StdOut + "\n" + res.StdErr;
        var m = System.Text.RegularExpressions.Regex.Match(combined, @"session id:\s*([0-9a-fA-F-]{36})");
        if (m.Success)
        {
            var sessDir = Path.Combine(genRoot, m.Groups[1].Value);
            if (Directory.Exists(sessDir))
            {
                var inSession = new DirectoryInfo(sessDir).GetFiles("*.png")
                    .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                if (inSession is not null) return inSession.FullName;
            }
        }

        // Fallback: newest ig_*.png produced after this call started. Deliberately NOT widened to
        // any png: other Codex sessions on this machine write exec-*.png into the same root all
        // the time, and "newest anywhere" would grab one of theirs.
        return new DirectoryInfo(genRoot).GetFiles("ig_*.png", SearchOption.AllDirectories)
            .Where(f => f.LastWriteTimeUtc >= startUtc)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
    }

    private string GeneratedImagesRoot()
    {
        var home = !string.IsNullOrWhiteSpace(_opt.CodexHome)
            ? _opt.CodexHome
            : (Environment.GetEnvironmentVariable("CODEX_HOME")
               ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));
        return Path.Combine(home, _opt.GeneratedImagesDir);
    }

    private const int StdinPromptOver = 20_000;

    private async Task<ProcessResult> RunCodexAsync(string prompt, string? workingDir, IEnumerable<string>? extraArgs,
        CancellationToken ct, bool search = false)
    {
        // ArgumentList passes the (multi-line) prompt as a single argv entry — no quoting headaches.
        var baseArgs = _opt.BaseArgs is { Length: > 0 } ? _opt.BaseArgs : CodexOptions.DefaultBaseArgs;
        var args = new List<string>(baseArgs);
        // Live web search is a top-level flag: `codex --search exec ...`. `exec` does not take it.
        if (search) args.Insert(0, "--search");
        if (!string.IsNullOrWhiteSpace(_opt.Model))
        {
            args.Add("-m");
            args.Add(_opt.Model);
        }
        // Lower reasoning effort = far faster generation (config flag must precede the prompt).
        if (!string.IsNullOrWhiteSpace(_opt.ReasoningEffort))
        {
            args.Add("-c");
            args.Add($"model_reasoning_effort=\"{_opt.ReasoningEffort}\"");
        }
        if (extraArgs is not null) args.AddRange(extraArgs);
        // Windows refuses a command line past ~32k characters. A list prompt carries the channel's
        // recent scripts and every past subject, so a long one goes through stdin ("-"), which
        // `codex exec` reads when the prompt argument is "-".
        string? stdin = null;
        if (prompt.Length > StdinPromptOver)
        {
            args.Add("-");
            stdin = prompt;
        }
        else
        {
            args.Add(prompt);
        }
        var res = await ProcessRunner.RunAsync(
            _opt.Command, args, workingDir,
            TimeSpan.FromSeconds(_opt.TimeoutSeconds), ct, stdinText: stdin);
        CodexQuota.Observe(res);
        if (CodexQuota.Exhausted)
            _log.LogWarning("{Quota}", CodexQuota.Describe());
        return res;
    }

    private static string Trunc(string s, int n = 600) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
