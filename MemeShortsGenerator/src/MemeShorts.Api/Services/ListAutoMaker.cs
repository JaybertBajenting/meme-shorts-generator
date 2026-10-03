using System.Text;
using System.Text.Json;
using MemeShorts.Api.Models;
using Microsoft.Extensions.Options;

namespace MemeShorts.Api.Services;

/// <summary>What happened to one hands-off list video.</summary>
public sealed record ListAutoResult(
    string? JobId, string? Title, int Lines, int DroppedLines, string? Source,
    string? Error, bool QuotaHit)
{
    public bool Ok => JobId is not null;
}

/// <summary>
/// One-click list short: Codex invents the subject, the lines and a picture per line, the pictures
/// are drawn or looked up, and the job is queued for rendering and review. Shared by the single
/// "make one" endpoint and the batch runner, so both behave the same.
///
/// Pictures are gathered in a staging folder BEFORE the job exists. If Codex runs out of quota
/// part-way, the video is dropped there instead of being rendered with holes and photo-search
/// stand-ins - an unattended batch would otherwise keep producing those until someone looked.
/// </summary>
public sealed class ListAutoMaker
{
    private readonly JobManager _jobs;
    private readonly ChannelRegistry _channels;
    private readonly ICodexClient _codex;
    private readonly ITitleStyleService _titleStyle;
    private readonly IStockImageService _images;
    private readonly SuggestionHistory _suggested;
    private readonly PipelineOptions _pipe;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ListAutoMaker> _log;

    public ListAutoMaker(JobManager jobs, ChannelRegistry channels, ICodexClient codex,
        ITitleStyleService titleStyle, IStockImageService images, SuggestionHistory suggested,
        IOptions<PipelineOptions> pipe, IWebHostEnvironment env, ILogger<ListAutoMaker> log)
    {
        _jobs = jobs;
        _channels = channels;
        _codex = codex;
        _titleStyle = titleStyle;
        _images = images;
        _suggested = suggested;
        _pipe = pipe.Value;
        _env = env;
        _log = log;
    }

    public async Task<ListAutoResult> MakeAsync(string? channel, string? topic, CancellationToken ct)
    {
        var ch = _channels.Resolve(string.IsNullOrWhiteSpace(channel) ? null : channel);
        if (!ch.IsList)
            return Fail($"channel '{ch.Id}' is not a list channel");

        var quotaBefore = CodexQuota.Hits;
        var styleTitles = await _titleStyle.GetStyleTitlesAsync(ch.TitleReferenceChannel, ch.TitleTab, ct);
        if (styleTitles.Count == 0)
            _log.LogWarning("No reference titles for {Channel}; Codex will pick a subject unaided.", ch.Id);

        var avoid = _jobs.RecentTopics(30).Concat(_suggested.Recent(20)).Distinct().ToList();
        var idea = await _codex.GenerateListIdeaAsync(styleTitles, topic, avoid, ct, _jobs.ListHistory(ch.Id));
        if (idea is null)
            return CodexQuota.Hits > quotaBefore
                ? Fail(CodexQuota.Describe() + " No video was made.", quota: true)
                : Fail("Codex could not come up with one. Try again, or type a subject.");
        // Title plus subject: a title can hide what it is about ("the clingiest AI keychain").
        _suggested.Add(string.IsNullOrWhiteSpace(idea.Subject) ? idea.Title : $"{idea.Title} (about: {idea.Subject})");

        var staging = Path.Combine(OutputRoot(), "_drafts", "auto_" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(staging);
        try
        {
            var found = await FetchPicturesAsync(idea, staging, ct);
            if (CodexQuota.Hits > quotaBefore)
            {
                _log.LogWarning("Dropped list idea '{Title}': Codex ran out of quota while drawing it.", idea.Title);
                return Fail($"{CodexQuota.Describe()} \"{idea.Title}\" was dropped rather than made with missing pictures.",
                    quota: true);
            }
            return CreateJob(ch.Id, idea, staging, found);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Every picture the script asks for, keyed by line. The same term as the line before means
    /// "keep this picture on screen" - a sentence still talking about the same thing. Searching
    /// again would swap in a different photo of it, so only the FIRST line of each run fetches.
    /// Fetched three at a time: most are drawn now, at 1-2 minutes each (Codex:PanelConcurrency).
    /// </summary>
    private async Task<Dictionary<int, FoundImage?>> FetchPicturesAsync(ListIdea idea, string dir, CancellationToken ct)
    {
        var used = new List<string>();      // Commons titles already spoken for, so no picture repeats
        using var gate = new SemaphoreSlim(3);
        // The same thing mentioned again later gets the SAME picture. Searching again with the first
        // picture excluded finds the second-best match, and for a one-word name that is often
        // something else: the second "Neptune" came back as an ocean observatory's map
        // (2026-09-30).
        var firstByTerm = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sameAs = new Dictionary<int, int>();
        foreach (var i in Enumerable.Range(0, idea.Items.Count).Where(i => !Holds(idea, i)))
        {
            var term = idea.Items[i].Image.Trim();
            if (firstByTerm.TryGetValue(term, out var first)) sameAs[i] = first;
            else firstByTerm[term] = i;
        }
        var fetches = firstByTerm.Values.ToDictionary(i => i, async i =>
        {
            await gate.WaitAsync(ct);
            try
            {
                string[] exclude;
                lock (used) exclude = used.ToArray();
                var got = await _images.FetchAsync(idea.Items[i].Image, dir, $"item_{i}", exclude, ct,
                    kind: idea.Items[i].Kind);
                if (got is not null) lock (used) used.Add(got.SourceTitle);
                return got;
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(fetches.Values);
        var found = fetches.ToDictionary(f => f.Key, f => f.Value.Result);
        foreach (var (i, first) in sameAs) found[i] = found[first];
        return found;
    }

    private static bool Holds(ListIdea idea, int i) => i > 0 && string.Equals(idea.Items[i].Image.Trim(),
        idea.Items[i - 1].Image.Trim(), StringComparison.OrdinalIgnoreCase);

    private ListAutoResult CreateJob(string channelId, ListIdea idea, string staging, Dictionary<int, FoundImage?> found)
    {
        var topic = string.IsNullOrWhiteSpace(idea.Title) ? idea.Items[0].Text : idea.Title;
        if (topic.Length > 120) topic = topic[..120];

        var missing = new List<string>();
        var job = _jobs.Create(topic, "list", channelId, autoGenerated: true, prepare: j =>
        {
            // Synchronous inside prepare: the job folder has to be complete before the pipeline
            // picks it up, or a restart mid-copy would leave a job that cannot render.
            foreach (var file in Directory.EnumerateFiles(staging))
                File.Copy(file, Path.Combine(j.OutputDir, Path.GetFileName(file)), overwrite: true);

            var input = new ListInput { Title = idea.Title, Subject = idea.Subject };
            var credits = new List<string>();
            for (int i = 0; i < idea.Items.Count; i++)
            {
                var item = idea.Items[i];
                if (Holds(idea, i) && input.Items.Count > 0)
                {
                    input.Items.Add(new ListItem { Image = input.Items[^1].Image, Text = item.Text });
                    continue;
                }
                var picture = found.TryGetValue(i, out var f) ? f : null;
                if (picture is null)
                {
                    missing.Add(item.Image);
                    // HOLD the previous picture rather than dropping the line. Dropping loses a
                    // step of the argument - the first run lost the line explaining the runes and
                    // the payoff then referred back to something never said. A line held over the
                    // wrong-ish picture still makes sense; a missing line does not.
                    if (input.Items.Count > 0)
                        input.Items.Add(new ListItem { Image = input.Items[^1].Image, Text = item.Text });
                    continue;
                }
                input.Items.Add(new ListItem { Image = picture.FileName, Text = item.Text });
                if (!credits.Contains(picture.Credit)) credits.Add(picture.Credit);   // a reused picture is credited once
            }

            File.WriteAllText(Path.Combine(j.OutputDir, "list.json"),
                JsonSerializer.Serialize(input, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // Commons licences require credit, so it travels with the job and lands in the
            // description rather than being remembered by hand.
            if (credits.Count > 0)
                File.WriteAllText(Path.Combine(j.OutputDir, "image_credits.txt"),
                    string.Join("\n", credits), new UTF8Encoding(false));
            // A news script's facts come from one web page; keep it next to the video so they can be
            // checked before approving. Not in image_credits.txt, which goes into the description.
            if (!string.IsNullOrWhiteSpace(idea.Source))
                File.WriteAllText(Path.Combine(j.OutputDir, "source.txt"), idea.Source.Trim(), new UTF8Encoding(false));
        });

        if (missing.Count > 0)
            _log.LogWarning("List idea '{Title}': no picture for {N} line(s): {Terms}",
                idea.Title, missing.Count, string.Join("; ", missing));

        return new ListAutoResult(job.Id, idea.Title, idea.Items.Count, missing.Count, idea.Source, null, false);
    }

    private string OutputRoot() => Path.IsPathRooted(_pipe.OutputRoot)
        ? _pipe.OutputRoot
        : Path.Combine(_env.ContentRootPath, _pipe.OutputRoot);

    private static ListAutoResult Fail(string error, bool quota = false) =>
        new(null, null, 0, 0, null, error, quota);
}
