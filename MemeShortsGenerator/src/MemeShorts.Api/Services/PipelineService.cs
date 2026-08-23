using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using MemeShorts.Api.Models;
using Microsoft.Extensions.Options;

namespace MemeShorts.Api.Services;

/// <summary>
/// Orchestrates one short end-to-end:
/// storyboard -> panels -> TTS -> captions -> props.json -> Remotion render.
/// </summary>
public sealed class PipelineService
{
    private readonly ICodexClient _codex;
    private readonly ITtsService _tts;
    private readonly IPanelRenderer _localPanels;
    private readonly IVideoRenderer _renderer;
    private readonly PipelineOptions _pipe;
    private readonly CodexOptions _codexOpt;
    private readonly IWhisperCaptionService _whisper;
    private readonly BackgroundHistory _bgHistory;
    private readonly ITitleStyleService _titleStyle;
    private readonly IThumbnailService _thumbnailer;
    private readonly ChannelRegistry _channels;
    private readonly PostSliceService _slicer;
    private readonly DiagramSliceService _diagrams;
    private readonly IOcrService _ocr;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<PipelineService> _log;

    private static readonly JsonSerializerOptions PropsJson = new() { WriteIndented = true };

    public PipelineService(
        ICodexClient codex, ITtsService tts, IPanelRenderer localPanels, IVideoRenderer renderer,
        IWhisperCaptionService whisper, BackgroundHistory bgHistory,
        ITitleStyleService titleStyle, IThumbnailService thumbnailer, ChannelRegistry channels,
        PostSliceService slicer, DiagramSliceService diagrams, IOcrService ocr,
        IOptions<PipelineOptions> pipe, IOptions<CodexOptions> codexOpt,
        IWebHostEnvironment env, ILogger<PipelineService> log)
    {
        _codex = codex; _tts = tts; _localPanels = localPanels; _renderer = renderer;
        _whisper = whisper; _bgHistory = bgHistory;
        _titleStyle = titleStyle; _thumbnailer = thumbnailer; _channels = channels;
        _slicer = slicer; _diagrams = diagrams; _ocr = ocr;
        _pipe = pipe.Value; _codexOpt = codexOpt.Value; _env = env; _log = log;
    }

    public async Task RunAsync(GenerationJob job, CancellationToken ct)
    {
        job.State = JobState.Running;

        // 0. Resolve the channel profile (content type, assets, voice, title style…).
        var ch = _channels.Resolve(job.ChannelId);
        _log.LogInformation("Job {Id} on channel '{Channel}' (contentType={Type}).", job.Id, ch.Id, ch.ContentType);

        // Repost: the post IS the content. No topic, no Codex, no generated artwork — so this
        // branches out before the storyboard rather than threading null checks through it.
        if (ch.IsRepost)
        {
            await RunRepostAsync(job, ch, ct);
            return;
        }

        // List: you bring the pictures AND the lines, so there is nothing to write or draw.
        if (ch.IsList)
        {
            await RunListAsync(job, ch, ct);
            return;
        }

        // 1. Storyboard (Codex, with deterministic fallback) — informed by the channel's title style.
        Stage(job, "writing storyboard", 5);
        var styleTitles = await _titleStyle.GetStyleTitlesAsync(ch.TitleReferenceChannel, ch.TitleTab, ct);
        // Validation used to run once and throw, so a single over-long storyboard killed the whole
        // job (and burned the topic). Codex overshoots the word budget often enough that this must
        // be a retry, not a hard failure — regenerating is cheap next to the render that follows.
        // Re-voicing: keep the script exactly as it was, so only the audio and render change.
        var reuse = job.ReuseStoryboardAndPanels && job.Storyboard is not null;
        if (reuse) _log.LogInformation("Job {Id} re-voicing: reusing stored storyboard + panels.", job.Id);

        var storyboardAttempts = Math.Clamp(_codexOpt.StoryboardAttempts, 1, 10);
        Storyboard? sb = reuse ? job.Storyboard : null;
        string storyboardError = "";

        for (int attempt = 1; sb is null && attempt <= storyboardAttempts; attempt++)
        {
            var candidate = await _codex.GenerateStoryboardAsync(job.Topic, ch.Format ?? job.Format, styleTitles, ch.ContentType, ct);
            if (candidate is null)
            {
                storyboardError = "Codex returned no storyboard.";
            }
            else if (ch.IsFinance || TryValidateMemeStoryboard(candidate, out storyboardError))
            {
                sb = candidate;
                break;
            }

            if (attempt < storyboardAttempts)
                _log.LogWarning("Storyboard attempt {Attempt}/{Total} rejected for '{Topic}': {Error} Retrying.",
                    attempt, storyboardAttempts, job.Topic, storyboardError);
        }

        if (sb is null)
        {
            if (ch.IsFinance)
            {
                _log.LogWarning("Using finance fallback storyboard for topic '{Topic}'.", job.Topic);
                sb = FallbackStoryboard.Finance(job.Topic);
            }
            else
            {
                throw new InvalidOperationException(
                    $"Codex storyboard rejected after {storyboardAttempts} attempts: {storyboardError} Stopped before TTS.");
            }
        }

        job.Storyboard = sb;

        var jobDir = job.OutputDir;
        Directory.CreateDirectory(jobDir);

        int fps = _pipe.Fps;
        int card = _pipe.CardSize;
        int gap = _pipe.BeatGapMs;

        int n = sb.Beats.Count;

        // Layout choice: does the artwork change per beat, or is there one fixed hero image?
        // Needed by both panel generation and the RenderBeat mapping below.
        var sequence = LayoutNeedsImagePerBeat(sb.Format);
        // chart_gag draws its chart natively in Remotion (fixed axes, changing data), so it needs
        // no generated artwork at all — matching the reference channel's one-frame chart videos.
        var nativeChart = IsChartGag(sb.Format) && sb.Beats.All(b => b.Visual is not null);

        // Re-voice keeps the existing artwork, but only if it actually survived cleanup.
        var panelsOnDisk = reuse && Directory.EnumerateFiles(jobDir, "panel_*.png").Any();
        if (reuse && !panelsOnDisk && !nativeChart)
            _log.LogWarning("Job {Id} re-voice: panel art missing, regenerating it.", job.Id);

        // 2. Panels — meme only, not for natively-drawn charts, and not when reusing existing art.
        if (!ch.IsFinance && !nativeChart && !panelsOnDisk)
        {
            // How many images this video needs depends on its FORMAT.
            //
            // The reference channel does not use one layout. Measured across 12 videos: some hold
            // a single artwork and stack text under it (giraffe, karma), while others swap the
            // picture every beat (the shirt photos, each superpower diagram, the Pringles can
            // arriving beside the Lay's bag). Generating one image for everything made the
            // second group impossible; generating one PER BEAT for everything made the first
            // group look like a slideshow of unrelated redraws. So the format decides.
            var panelCount = sequence ? n : 1;
            _log.LogInformation("Format '{Format}' -> {Count} panel image(s).", sb.Format, panelCount);

            Stage(job, "drawing panels", 12);
            var doneCount = 0;
            using var sem = new SemaphoreSlim(Math.Max(1, _codexOpt.PanelConcurrency));
            await Task.WhenAll(Enumerable.Range(0, panelCount).Select(async i =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    await GeneratePanelAsync(sb.Beats[i], card, card, Path.Combine(jobDir, $"panel_{i}.png"), ch.PanelSource, ct);
                    int d = Interlocked.Increment(ref doneCount);
                    Stage(job, $"drawing panels {d}/{panelCount}", 12 + d * 50 / Math.Max(1, panelCount));
                }
                finally { sem.Release(); }
            }));

            var invalidPanels = Enumerable.Range(0, panelCount)
                .Select(i => Path.Combine(jobDir, $"panel_{i}.png"))
                .Where(path => !File.Exists(path) || new FileInfo(path).Length < 1024)
                .Select(Path.GetFileName)
                .ToList();
            if (invalidPanels.Count > 0)
                throw new InvalidOperationException(
                    $"Codex imagegen produced no usable image for: {string.Join(", ", invalidPanels)}. Stopped before TTS.");
        }

        // 3. TTS — sequential (System.Speech is fast and not thread-safe to share).
        Stage(job, "voicing beats", 64);
        var beats = new List<RenderBeat>();
        var wavPaths = new List<string>();   // absolute, parallel to beats — for whisper
        int cursor = 0;

        for (int i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            var beat = sb.Beats[i];

            var audioName = $"beat_{i}.wav";
            var wavPath = Path.Combine(jobDir, audioName);
            var spoken = string.IsNullOrWhiteSpace(beat.Tts) ? beat.Caption : beat.Tts;
            // Use the storyboard's per-beat emotion. This used to be hardcoded to "shouts" for
            // every meme beat to guarantee constant energy, but measurement showed it backfired:
            // our narration already peaks LOUDER than the reference channel (0.89 vs 0.63), and
            // pinning every line at maximum FLATTENS pitch variation — spread fell from ~145 Hz
            // unforced to 75-140 Hz forced, against the reference's 171 Hz. Its delivery varies;
            // ours was one continuous shout. Set Tts:ForceEmotion to override globally if needed.
            var delivery = beat.Emotion;
            int dur = await _tts.SynthesizeAsync(spoken, wavPath, ch.WindowsVoice, delivery, ct);
            if (dur <= 0) dur = (int)Math.Round(beat.DurationS * 1000);

            int start = cursor;
            int end = start + dur;
            beats.Add(new RenderBeat
            {
                Index = i,
                Caption = beat.Caption,
                // Sequence formats get their own image; text-stack formats all point at the hero.
                // Native charts have no image at all — Visual carries the drawing instead.
                Panel = (ch.IsFinance || nativeChart) ? "" : (sequence ? $"panel_{i}.png" : "panel_0.png"),
                Audio = audioName,
                StartMs = start,
                EndMs = end,
                Options = beat.Options,
                Visual = beat.Visual,
            });
            wavPaths.Add(wavPath);
            cursor = end + gap;
        }

        // 4. Captions (real word timing via whisper, even-spread fallback)
        Stage(job, "timing captions", 75);
        var captions = await BuildCaptionsAsync(beats, wavPaths, ct);
        int lastEnd = beats.Count > 0 ? beats[^1].EndMs : 0;
        int totalMs = lastEnd + _pipe.OutroMs;

        // 5. Background — random clip + random section, trimmed to just what we need.
        var (backgroundName, backgroundLoopFrames) = await TryStageBackgroundAsync(jobDir, ch.BackgroundDir, fps, totalMs, ct);
        var (musicName, musicLoopFrames, musicAttribution) = await TryStageMusicAsync(jobDir, ch.MusicDir, fps, totalMs, ct);
        job.MusicAttribution = musicAttribution;

        // Resolve the exact YouTube metadata now, so the UI can show it for review.
        var ytTitle = string.IsNullOrWhiteSpace(sb.YouTubeTitle) ? sb.Title : sb.YouTubeTitle!;
        if (!ch.IsFinance) ytTitle = PackageMemeTitle(ytTitle);
        job.YouTubeTitle = Truncate(ytTitle + ch.TitleSuffix, 100);
        var desc = ch.DescriptionTemplate.Replace("{title}", ytTitle);
        if (!string.IsNullOrWhiteSpace(musicAttribution)) desc += "\n\n" + musicAttribution.Trim();
        job.YouTubeDescription = desc;
        job.YouTubeTags = ch.Tags;
        job.YouTubeCategoryId = ch.CategoryId;

        var props = new RenderProps
        {
            Width = _pipe.Width,
            Height = _pipe.Height,
            Fps = fps,
            DurationInFrames = Math.Max(1, (int)Math.Ceiling(totalMs / 1000.0 * fps)),
            Background = backgroundName,
            BackgroundLoopFrames = backgroundLoopFrames,
            Music = musicName,
            MusicVolume = _pipe.MusicVolume,
            MusicLoopFrames = musicLoopFrames,
            Title = sb.Title,
            Format = sb.Format,
            ContentType = ch.ContentType,
            Beats = beats,
            Captions = captions,
            FakeComment = sb.FakeComment is null ? null : new RenderFakeComment
            {
                Username = sb.FakeComment.Username,
                Text = sb.FakeComment.Text,
                // Slide in during the last beat and linger through the outro.
                StartMs = beats.Count > 0 ? beats[^1].StartMs : 0,
            },
        };

        var propsPath = Path.Combine(jobDir, "props.json");
        WriteJsonNoBom(propsPath, props);

        // 5. Render
        Stage(job, "rendering video", 85);
        var outMp4 = Path.Combine(jobDir, "short.mp4");
        job.VideoPath = await _renderer.RenderAsync(propsPath, jobDir, outMp4, ct);

        // 6. Thumbnail
        if (_pipe.GenerateThumbnail && OperatingSystem.IsWindows())
            await TryGenerateThumbnailAsync(job, sb, beats, ch, outMp4, jobDir, ct);

        // 7. Keep only the final video + thumbnail; drop the per-job scratch (bg clip, frames,
        //    audio, props) so out/ stays lean across big batches.
        if (_pipe.CleanupIntermediates)
            // Panel art is KEPT so a job can be re-voiced later without paying for image
            // generation again — auditioning a new voice then costs only TTS + render. The heavy
            // scratch (bg.mp4, music, per-beat wavs) is still dropped; panels are ~1MB each.
            CleanupJobScratch(jobDir, new[] { outMp4, job.ThumbnailPath }
                .Concat(Directory.EnumerateFiles(jobDir, "panel_*.png"))
                .ToArray());

        Stage(job, "done", 100);
        job.State = JobState.Done;
    }

    /// <summary>
    /// A found post, read out verbatim over the background — the reference channel's other
    /// format. Everything upstream of TTS is replaced by "wrap the pasted text"; everything
    /// downstream (background, music, upload metadata, thumbnail, review gate) is shared.
    /// </summary>
    private async Task RunRepostAsync(GenerationJob job, EffectiveChannel ch, CancellationToken ct)
    {
        var jobDir = job.OutputDir;
        Directory.CreateDirectory(jobDir);
        int fps = _pipe.Fps;

        var inputPath = Path.Combine(jobDir, "repost.json");
        if (!File.Exists(inputPath))
            throw new InvalidOperationException(
                "This repost job has no repost.json — submit it through the upload form, not the topic box.");

        var input = JsonSerializer.Deserialize<RepostInput>(await File.ReadAllTextAsync(inputPath, ct))
            ?? throw new InvalidOperationException("repost.json could not be read.");

        // 1. Slice the screenshot into the bands the video reveals. The pasted image already
        //    contains the post's words, so they are NEVER redrawn — the card is the picture
        //    itself, uncovered one caption line at a time.
        Stage(job, "slicing the post", 8);
        PostBands? bands = null;
        var bandTexts = new List<string>();
        var imagePath = input.Image is null ? null : Path.Combine(jobDir, input.Image);

        // Typed text WINS. It is the corrected version of what OCR misread, so ignoring it
        // whenever slicing happened to succeed (which is the normal case) made the box a lie.
        var typedLines = (input.Text ?? "")
            .Replace("\r\n", "\n").Split('\n')
            .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

        if (imagePath is not null && File.Exists(imagePath) && OperatingSystem.IsWindows())
        {
            // ONE OCR pass over the whole picture gives both the words and where each line
            // sits, so the slicing and the narration can never disagree about what a line is.
            var ocrLines = await _ocr.ReadLinesAsync(imagePath, ct);
            bands = _slicer.Analyze(imagePath, ocrLines, _diagrams, typedLines);
            // One narration line per SEGMENT, not per block: a comparison meme's band holds two
            // columns, and each needs its own timing so the spotlight can follow the voice.
            // NarrationOrder decides the sequence — one side of a comparison before the other.
            bandTexts.AddRange(bands.NarrationOrder()
                .Select(x => RepostComposer.FixOcrConfusions(
                    RepostComposer.StripLeadingArtifact(x.Segment.Text.Trim()))));
            _log.LogInformation("Repost {Id}: {N} caption line(s): {Sample}",
                job.Id, bandTexts.Count, string.Join(" | ", bandTexts.Take(3)));

            // Check the PLAN before spending a render on it.
            //
            // The slicer infers a layout from each screenshot instead of matching known
            // formats, so a shape it has not met produces a wrong plan and there is no case to
            // add ahead of time. These checks know nothing about any site's layout — they only
            // ask whether the plan is self-consistent. A plan that fails them would have become
            // a broken video discovered three minutes later in Review; failing here says why,
            // in seconds, while the picture is still in hand.
            var ink = PostSliceService.InkGrid(imagePath, bands.Background, 4);
            var issues = PostPlanValidator.Validate(bands, bandTexts, ink, 4);
            foreach (var i in issues.Where(i => i.Severity != PlanSeverity.Broken))
                _log.LogWarning("Repost {Id} slice warning [{Code}]: {Message}", job.Id, i.Code, i.Message);

            var broken = issues.Where(i => i.Severity == PlanSeverity.Broken).ToList();
            if (broken.Count > 0)
            {
                foreach (var i in broken)
                    _log.LogError("Repost {Id} slice BROKEN [{Code}]: {Message}", job.Id, i.Code, i.Message);
                throw new InvalidOperationException(
                    "This picture could not be cut up correctly, so no video was made:\n"
                    + string.Join("\n", broken.Select(i => "  - " + i.Message))
                    + "\nTry a tighter crop of just the post, or type the lines in by hand.");
            }
        }

        var sliced = bands is not null && bandTexts.Count > 0;
        // In slice mode the narration is one entry per revealed band; typed lines replace the
        // OCR'd ones positionally, which is what editing a prefilled box naturally produces.
        var spokenPer = typedLines.Count > 0
            ? typedLines
            : sliced ? bandTexts : RepostComposer.Wrap(input.Text ?? "").Select(l => l.Text).ToList();

        var useTyped = !sliced;

        // One continuous read. A diagram does NOT use this script for its audio — it voices each
        // label separately further down — but it is still what the title is written from.
        var script = string.Join(" ", spokenPer.Select(RepostComposer.Spoken).Where(s => s.Length > 0));

        if (string.IsNullOrWhiteSpace(script))
            throw new InvalidOperationException(
                "Nothing to read out — no words were found in the picture and none were typed in.");

        // 2. One clip for the whole post, not one per beat — it is a single continuous read.
        Stage(job, "voicing the post", 30);
        var audioName = "vo.wav";
        var wavPath = Path.Combine(jobDir, audioName);
        var lines = spokenPer.Select(t => new PostLine { Text = t }).ToList();
        int voiceMs;

        // A diagram is read one label at a time with an EXACT pause between them. Leaving the
        // gaps to the voice gave 1.0s after "Tree" but 2.3s after "Cut tree" — the second panel
        // sat there long enough to feel like a stall. Voicing each label separately and
        // stitching in fixed silence also means the reveal times are known outright, with no
        // transcription step to infer them.
        if (bands?.IsDiagram ?? false)
        {
            voiceMs = await VoiceEachLineAsync(spokenPer, lines, wavPath, jobDir, ch, ct);
            Stage(job, "timing lines", 60);
        }
        else
        {
            voiceMs = await _tts.SynthesizeAsync(script, wavPath, ch.WindowsVoice, null, ct);
            if (voiceMs <= 0) voiceMs = (int)Math.Round(script.Split(' ').Length / 190.0 * 60_000);

            // 3. Anchor each line to the word that introduces it.
            Stage(job, "timing lines", 60);
            List<WordTiming>? heard = null;
            if (_whisper.Enabled)
            {
                var timings = await _whisper.TranscribeAsync(new[] { wavPath }, ct);
                timings?.TryGetValue(wavPath, out heard);
            }

            if (heard is { Count: > 0 })
            {
                var unspoken = RepostComposer.Align(lines, heard);
                if (unspoken.Count > 0)
                {
                    // A card line nobody reads still takes a reveal slot, so everything after it
                    // lands a beat behind the voice - the text appears while the PREVIOUS line is
                    // still being spoken. This is what a stray header line did, and it is only
                    // visible by comparing the audio with the reveal, never by reading the plan.
                    foreach (var i in unspoken)
                        _log.LogWarning("Repost {Id}: card line {N} is never spoken: {Text}",
                            job.Id, i, lines[i].Text);

                    // Drop them. A line with no narration behind it is not content the viewer
                    // was ever going to hear, and leaving it in desynchronises the whole post.
                    var drop = unspoken.ToHashSet();
                    lines = lines.Where((_, i) => !drop.Contains(i)).ToList();
                    _log.LogWarning("Repost {Id}: dropped {N} unspoken card line(s); re-timing.",
                        job.Id, drop.Count);
                    RepostComposer.Align(lines, heard);
                }
            }
            else
            {
                // Even spread: worse than speech alignment, but a whisper outage should not
                // stop a render.
                _log.LogWarning("Repost {Id}: no word timings; spreading lines evenly.", job.Id);
                for (int i = 0; i < lines.Count; i++)
                    lines[i].StartMs = (int)((double)i / Math.Max(1, lines.Count) * voiceMs);
            }
        }

        int totalMs = voiceMs + _pipe.OutroMs;

        // 4. Background + music — identical to the meme path, including the dense-keyframe trim.
        var (backgroundName, backgroundLoopFrames) = await TryStageBackgroundAsync(jobDir, ch.BackgroundDir, fps, totalMs, ct);
        var (musicName, musicLoopFrames, musicAttribution) = await TryStageMusicAsync(jobDir, ch.MusicDir, fps, totalMs, ct);
        job.MusicAttribution = musicAttribution;

        // Title: written by Codex in the reference channel's voice unless one was typed in.
        // The post's own first line is a poor hook — it is the setup, not the sell — so it is
        // only the last resort if Codex is unavailable.
        var title = input.Title?.Trim() ?? "";
        if (title.Length == 0)
        {
            Stage(job, "titling", 80);
            var styleTitles = await _titleStyle.GetStyleTitlesAsync(ch.TitleReferenceChannel, ch.TitleTab, ct);
            // In slice mode the words live in the OCR'd bands, not in input.Text — which is
            // empty whenever the picture carries its own text (i.e. the normal case). Passing
            // input.Text here made the titler bail instantly and fall back every time.
            var postWords = useTyped ? input.Text : string.Join("\n", bandTexts);
            title = await _codex.GenerateRepostTitleAsync(postWords, styleTitles, ct) ?? "";
            if (title.Length > 0)
                _log.LogInformation("Repost {Id} titled by Codex: {Title}", job.Id, title);
            else
                _log.LogWarning("Repost {Id}: no Codex title; falling back to the post's first line.", job.Id);
        }
        if (title.Length == 0) title = lines.Count > 0 ? lines[0].Text : job.Topic;

        var built = sliced
            ? BuildBlocks(bands!, lines)
            : (Blocks: new List<PostBlockRender>(), Spotlights: new List<PostSpotlight>());

        job.YouTubeTitle = Truncate(PackageMemeTitle(title) + ch.TitleSuffix, 100);
        var desc = ch.DescriptionTemplate.Replace("{title}", title);
        if (!string.IsNullOrWhiteSpace(musicAttribution)) desc += "\n\n" + musicAttribution.Trim();
        job.YouTubeDescription = desc;
        job.YouTubeTags = ch.Tags;
        job.YouTubeCategoryId = ch.CategoryId;

        var props = new RenderProps
        {
            Width = _pipe.Width,
            Height = _pipe.Height,
            Fps = fps,
            DurationInFrames = Math.Max(1, (int)Math.Ceiling(totalMs / 1000.0 * fps)),
            Background = backgroundName,
            BackgroundLoopFrames = backgroundLoopFrames,
            Music = musicName,
            MusicVolume = _pipe.MusicVolume,
            MusicLoopFrames = musicLoopFrames,
            Title = title,
            Format = "repost",
            ContentType = ch.ContentType,
            Post = new RenderPost
            {
                AuthorName = input.AuthorName,
                Headline = input.Headline,
                Meta = input.Meta,
                Image = input.Image,
                Lines = lines,
                // Slice geometry — present only when a screenshot was sliced, which is what
                // switches the composition from drawing text to uncovering the picture.
                ImageWidth = bands?.Width,
                ImageHeight = bands?.Height,
                HeaderBottom = bands?.HeaderBottom,
                CardBackground = bands?.Background,
                Blocks = built.Blocks,
                Spotlights = built.Spotlights,
                Diagram = bands?.IsDiagram ?? false,
                GutterFrac = bands?.Gutter is { } g && bands.Width > 0 ? (double)g / bands.Width : null,
            },
            // One beat carrying the single voice clip; MemeShort places it on the timeline.
            Beats = new List<RenderBeat>
            {
                new() { Index = 0, Caption = "", Panel = "", Audio = audioName, StartMs = 0, EndMs = voiceMs },
            },
        };

        WriteJsonNoBom(Path.Combine(jobDir, "props.json"), props);

        Stage(job, "rendering video", 85);
        var outMp4 = Path.Combine(jobDir, "short.mp4");
        job.VideoPath = await _renderer.RenderAsync(Path.Combine(jobDir, "props.json"), jobDir, outMp4, ct);

        // Thumbnail: a real frame near the end, where the card is fully grown — same idea as the
        // meme path, but there are no beats to pick from so it is just a fixed fraction.
        if (_pipe.GenerateThumbnail)
        {
            var thumb = await GrabFrameAsync(outMp4, Path.Combine(jobDir, "thumb.jpg"), totalMs * 0.9 / 1000.0, ct);
            if (thumb is not null) job.ThumbnailPath = thumb;
        }

        // Keep the source post + image so the job can be re-voiced without re-uploading.
        if (_pipe.CleanupIntermediates)
            CleanupJobScratch(jobDir, new[] { outMp4, job.ThumbnailPath, inputPath }
                .Concat(input.Image is null ? Array.Empty<string>() : new[] { Path.Combine(jobDir, input.Image) })
                .ToArray());

        Stage(job, "done", 100);
        job.State = JobState.Done;
    }

    /// <summary>
    /// Lay the slices out in the order the video stacks them, each with the moment it appears.
    ///
    /// Pictures are always moved to the END. On a post whose photo sits in the middle with the
    /// joke written underneath (4chan-style), leaving it in place meant the text below it was
    /// treated as part of the picture and never read out at all. Moving it last also makes it
    /// the payoff, which is what the format wants.
    /// </summary>
    private static string? SideName(PostSide side) => side switch
    {
        PostSide.Left => "left",
        PostSide.Right => "right",
        _ => null,
    };

    private static (List<PostBlockRender> Blocks, List<PostSpotlight> Spotlights) BuildBlocks(
        PostBands bands, List<PostLine> lines)
    {
        var blocks = new List<PostBlockRender>();
        var spotlights = new List<PostSpotlight>();

        // A diagram's slices are already whole panels in arrow order — they keep their own
        // rectangles and simply light up one after another, so none of the stacking, crop
        // chaining or column logic below applies.
        if (bands.IsDiagram)
        {
            var panels = bands.CaptionLines.ToList();
            for (int i = 0; i < panels.Count; i++)
            {
                var at = lines.Count == 0 ? 0
                    : lines[Math.Clamp(
                        panels.Count <= 1 ? 0 : (int)Math.Round(i * (lines.Count - 1.0) / (panels.Count - 1)),
                        0, lines.Count - 1)].StartMs;
                blocks.Add(new PostBlockRender
                {
                    Top = panels[i].Top,
                    Bottom = panels[i].Bottom,
                    XLeft = panels[i].XLeft,
                    XRight = panels[i].XRight,
                    StartMs = at,
                    Text = panels[i].Text,
                });
            }
            return (blocks, spotlights);
        }

        // Header first, on screen from frame 0.
        foreach (var b in bands.Blocks.Where(b => !b.IsMedia && b.Top < bands.HeaderBottom))
            blocks.Add(new PostBlockRender
            {
                // Header spans down to HeaderBottom, which may include the avatar hanging
                // below the byline text.
                Top = b.Top, Bottom = Math.Max(b.Bottom, bands.HeaderBottom),
                StartMs = 0, Text = b.Text,
            });

        var captions = bands.CaptionLines.ToList();

        // Where each caption's crop starts. Each COLUMN is chained on its own: a caption takes
        // the gap above it from the previous slice IN ITS OWN COLUMN, so the original line
        // spacing survives. Chaining down the document instead meant a tall photo in the left
        // column pushed the chain past every line on the right, and those lines lost the space
        // above them and came out crammed together.
        //
        // Keyed by the block, not by its top edge: two columns routinely start a slice on the
        // same row, and keying by row silently merged them.
        var cropTops = new Dictionary<PostBlock, int>();
        foreach (var lane in bands.Blocks
                     .Where(b => b.Top >= bands.HeaderBottom)
                     .GroupBy(b => (b.Group, b.Side)))
        {
            var prevBottom = bands.HeaderBottom;
            foreach (var b in lane.OrderBy(b => b.Top))
            {
                if (!b.IsMedia)
                    cropTops[b] = Math.Max(Math.Min(prevBottom, b.Top), b.Top - 10);
                prevBottom = Math.Max(prevBottom, b.Bottom);
            }
        }

        // Walk the SPOKEN order, not the top-to-bottom one — on a comparison meme they differ,
        // and the reveal has to follow the voice.
        var spoken = bands.NarrationOrder().ToList();
        var totalSegments = Math.Max(1, spoken.Count);

        int TimeOfSegment(int index) =>
            lines.Count == 0 ? 0
                : lines[Math.Clamp(
                    totalSegments <= 1 ? 0 : (int)Math.Round(index * (lines.Count - 1.0) / (totalSegments - 1)),
                    0, lines.Count - 1)].StartMs;

        // A slice appears with whichever of its segments is spoken FIRST; a later one only
        // moves the spotlight across.
        var firstSpokenAt = new Dictionary<PostBlock, int>();
        for (int i = 0; i < spoken.Count; i++)
        {
            var (block, segment) = spoken[i];
            var at = TimeOfSegment(i);
            if (!firstSpokenAt.ContainsKey(block)) firstSpokenAt[block] = at;

            // A full-width line after a column pair must CLEAR the blur. Without this the last
            // column stayed dimmed over lines spanning the whole post — the alien meme has a
            // two-column panel followed by full-width captions.
            var side = segment.Side switch
            {
                PostSide.Left => "left",
                PostSide.Right => "right",
                _ => "full",
            };
            if (side != "full" || (spotlights.Count > 0 && spotlights[^1].Side != "full"))
                spotlights.Add(new PostSpotlight { StartMs = at, Side = side });
        }

        foreach (var caption in captions)
        {
            blocks.Add(new PostBlockRender
            {
                Top = cropTops.TryGetValue(caption, out var ct) ? ct : caption.Top,
                Bottom = caption.Bottom,
                StartMs = firstSpokenAt.TryGetValue(caption, out var at) ? at : 0,
                Text = caption.Text,
                XLeft = caption.XLeft,
                XRight = caption.XRight,
                Side = SideName(caption.Side),
                Group = caption.Group,
            });
        }

        // Clear the blur for the last beat, so the final frame — and the thumbnail taken from
        // it — shows the whole meme rather than half of it dimmed.
        if (spotlights.Count > 0 && lines.Count > 0)
            spotlights.Add(new PostSpotlight { StartMs = lines[^1].StartMs + 900, Side = "full" });

        var isSinglePhotoPost = bands.Media.Count() == 1;

        // Every picture keeps its own place in the post — nothing is ever hoisted to the end.
        // A post built out of PANELS (several pictures) reveals each one with the row it heads,
        // so a quote is never torn away from the photo beside it. A post with ONE picture shows
        // it from the very first frame and lets the words build around it, wherever it sits:
        // above the text on an image board, below it on Instagram, between on a news article.
        foreach (var m in bands.Media)
        {
            var at = isSinglePhotoPost
                ? 0
                : blocks.FirstOrDefault(b => b.Top >= m.Bottom && b.Text is not null)?.StartMs
                  ?? blocks.LastOrDefault(b => b.Bottom <= m.Top && b.Text is not null)?.StartMs
                  ?? 0;

            // A picture inside a comparison is whole and full width, so it belongs to the
            // comparison rather than to one column: it arrives with the FIRST line spoken in
            // the group, and stays for the rest of it.
            if (m.Group is not null)
            {
                var firstInGroup = captions
                    .Where(c => c.Group == m.Group)
                    .OrderBy(c => firstSpokenAt.TryGetValue(c, out var t) ? t : int.MaxValue)
                    .FirstOrDefault();
                at = firstInGroup is not null && firstSpokenAt.TryGetValue(firstInGroup, out var ft)
                    ? ft
                    : 0;
            }

            var insertAt = blocks.FindIndex(b => b.Top >= m.Bottom);
            var block = new PostBlockRender
            {
                Top = m.Top,
                Bottom = m.Bottom,
                StartMs = at,
                XLeft = m.XLeft,
                XRight = m.XRight,
                Side = SideName(m.Side),
                Group = m.Group,
            };
            if (insertAt >= 0) blocks.Insert(insertAt, block); else blocks.Add(block);
        }

        return (blocks, spotlights);
    }

    /// <summary>
    /// A LIST SHORT: a run of supplied pictures, one spoken line each, over blurred gameplay.
    ///
    /// Deliberately reuses the meme path's beat machinery — one voice clip per beat, whisper
    /// word timings, background and music staging — because the only thing that differs is
    /// where the pictures come from. Nothing is written or drawn here; both halves are supplied.
    /// </summary>
    private async Task RunListAsync(GenerationJob job, EffectiveChannel ch, CancellationToken ct)
    {
        var jobDir = job.OutputDir;
        var fps = _pipe.Fps;

        var inputPath = Path.Combine(jobDir, "list.json");
        if (!File.Exists(inputPath))
            throw new InvalidOperationException(
                "This list job has no list.json — submit it through the upload form, not the topic box.");
        var input = JsonSerializer.Deserialize<ListInput>(await File.ReadAllTextAsync(inputPath, ct))
            ?? throw new InvalidOperationException("list.json could not be read.");

        var items = input.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.Text) && !string.IsNullOrWhiteSpace(i.Image))
            .ToList();
        if (items.Count == 0)
            throw new InvalidOperationException("Nothing to make — every item needs a picture and a line.");

        // 1. One voice clip per line, laid end to end with the usual small gap between beats.
        Stage(job, "voicing the lines", 25);
        var beats = new List<RenderBeat>();
        var wavPaths = new List<string>();
        var cursor = 0;
        var gap = _pipe.BeatGapMs;

        for (int i = 0; i < items.Count; i++)
        {
            var audioName = $"vo_{i}.wav";
            var wavPath = Path.Combine(jobDir, audioName);
            var dur = await _tts.SynthesizeAsync(items[i].Text, wavPath, ch.WindowsVoice, null, ct);
            if (dur <= 0) dur = (int)Math.Round(items[i].Text.Split(' ').Length / 190.0 * 60_000);

            beats.Add(new RenderBeat
            {
                Index = i,
                Caption = items[i].Text,
                Panel = items[i].Image,
                Audio = audioName,
                StartMs = cursor,
                EndMs = cursor + dur,
            });
            wavPaths.Add(wavPath);
            cursor += dur + gap;
        }

        // 2. Word timings drive the karaoke captions, exactly as on the meme path.
        Stage(job, "timing captions", 60);
        var captions = await BuildCaptionsAsync(beats, wavPaths, ct);
        var totalMs = beats[^1].EndMs + _pipe.OutroMs;

        Stage(job, "staging background", 70);
        var (backgroundName, backgroundLoopFrames) =
            await TryStageBackgroundAsync(jobDir, ch.BackgroundDir, fps, totalMs, ct);
        var (musicName, musicLoopFrames, musicAttribution) =
            await TryStageMusicAsync(jobDir, ch.MusicDir, fps, totalMs, ct);
        job.MusicAttribution = musicAttribution;

        // Fall back to the channel's own mascot when the job didn't bring one, so every video
        // carries the character without it having to be attached by hand each time.
        var mascotName = input.Mascot;
        if (string.IsNullOrWhiteSpace(mascotName)) mascotName = TryStageMascot(jobDir, ch.MascotPath);

        Stage(job, "titling", 80);
        var title = (input.Title ?? "").Trim();
        if (title.Length == 0)
        {
            var styleTitles = await _titleStyle.GetStyleTitlesAsync(ch.TitleReferenceChannel, ch.TitleTab, ct);
            title = await _codex.GenerateRepostTitleAsync(
                        string.Join(" ", items.Select(i => i.Text)), styleTitles, ct)
                    ?? items[0].Text;
        }
        job.YouTubeTitle = Truncate(PackageMemeTitle(title) + ch.TitleSuffix, 100);
        var desc = ch.DescriptionTemplate.Replace("{title}", title);
        if (!string.IsNullOrWhiteSpace(musicAttribution)) desc += "\n\n" + musicAttribution.Trim();
        job.YouTubeDescription = desc;
        job.YouTubeTags = ch.Tags;
        job.YouTubeCategoryId = ch.CategoryId;

        var props = new RenderProps
        {
            Width = _pipe.Width,
            Height = _pipe.Height,
            Fps = fps,
            DurationInFrames = Math.Max(1, (int)Math.Ceiling(totalMs / 1000.0 * fps)),
            Background = backgroundName,
            BackgroundLoopFrames = backgroundLoopFrames,
            // The reference blurs its gameplay hard, so the card is the only thing in focus.
            BackgroundBlur = 14,
            Music = musicName,
            MusicVolume = _pipe.MusicVolume,
            MusicLoopFrames = musicLoopFrames,
            Title = title,
            Format = "list",
            ContentType = ch.ContentType,
            Mascot = mascotName,
            Beats = beats,
            Captions = captions,
        };

        WriteJsonNoBom(Path.Combine(jobDir, "props.json"), props);

        Stage(job, "rendering video", 85);
        var outMp4 = Path.Combine(jobDir, "short.mp4");
        job.VideoPath = await _renderer.RenderAsync(Path.Combine(jobDir, "props.json"), jobDir, outMp4, ct);

        if (_pipe.GenerateThumbnail)
        {
            var thumb = await GrabFrameAsync(outMp4, Path.Combine(jobDir, "thumb.jpg"), totalMs * 0.5 / 1000.0, ct);
            if (thumb is not null) job.ThumbnailPath = thumb;
        }

        // Keep the pictures and the script, so the job can be re-voiced without re-uploading.
        // image_credits.txt stays too: the Commons licences require attribution, and cleanup was
        // deleting the only record of it before it ever reached a description.
        if (_pipe.CleanupIntermediates)
            CleanupJobScratch(jobDir, new[]
                {
                    outMp4, job.ThumbnailPath, inputPath, Path.Combine(jobDir, "image_credits.txt"),
                }
                .Concat(items.Select(i => Path.Combine(jobDir, i.Image)))
                .Concat(mascotName is null ? Array.Empty<string>() : new[] { Path.Combine(jobDir, mascotName) })
                .ToArray());

        Stage(job, "done", 100);
        job.State = JobState.Done;
    }

    /// <summary>How long the voice waits between a diagram's labels.</summary>
    private const int DiagramGapMs = 1100;

    /// <summary>
    /// Voice each label on its own and stitch them together with a fixed silence, filling in
    /// each line's exact start time as it goes. Returns the finished clip's length.
    ///
    /// Reading the labels as one script and asking the transcriber where they fell produced
    /// wildly uneven gaps — the voice pauses however it likes at a paragraph break — and a
    /// one-word panel could sit on screen three times as long as its neighbour.
    /// </summary>
    private async Task<int> VoiceEachLineAsync(
        List<string> spoken, List<PostLine> lines, string wavPath, string jobDir,
        EffectiveChannel ch, CancellationToken ct)
    {
        var parts = new List<string>();
        var at = 0;

        for (int i = 0; i < spoken.Count; i++)
        {
            var text = RepostComposer.Spoken(spoken[i]);
            if (text.Length == 0) { if (i < lines.Count) lines[i].StartMs = at; continue; }

            var part = Path.Combine(jobDir, $"_vo_{i}.wav");
            var ms = await _tts.SynthesizeAsync(text, part, ch.WindowsVoice, null, ct);
            if (ms <= 0 || !File.Exists(part))
            {
                ms = (int)Math.Round(text.Split(' ').Length / 190.0 * 60_000);
                if (!File.Exists(part)) continue;
            }

            if (i < lines.Count) lines[i].StartMs = at;
            parts.Add(part);
            at += ms + DiagramGapMs;
        }

        if (parts.Count == 0) return 0;

        // Concatenate with the gap padded onto the end of every clip but the last.
        var listFile = Path.Combine(jobDir, "_vo_list.txt");
        var filter = string.Join("", parts.Select((_, i) =>
            i < parts.Count - 1
                ? $"[{i}:a]apad=pad_dur={DiagramGapMs / 1000.0:0.###}[a{i}];"
                : $"[{i}:a]anull[a{i}];"));
        filter += string.Join("", parts.Select((_, i) => $"[a{i}]")) + $"concat=n={parts.Count}:v=0:a=1[out]";

        var args = string.Join(" ", parts.Select(p => $"-i \"{p}\""))
                   + $" -filter_complex \"{filter}\" -map \"[out]\" -y \"{wavPath}\"";
        try
        {
            await RunFfmpegAsync(args, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not stitch the per-label voiceover; falling back to one read.");
            return 0;
        }
        finally
        {
            foreach (var p in parts) { try { File.Delete(p); } catch { /* scratch */ } }
            try { File.Delete(listFile); } catch { /* scratch */ }
        }

        var total = Math.Max(at - DiagramGapMs, 1);
        _log.LogInformation("Diagram voiceover: {N} label(s), {Gap}ms apart, {Total}ms total.",
            parts.Count, DiagramGapMs, total);
        return total;
    }

    private async Task RunFfmpegAsync(string args, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(_pipe.FfmpegCommand, args)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi)
                         ?? throw new InvalidOperationException("ffmpeg would not start.");
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg exited {proc.ExitCode}: {Tail(stderr)}");

        static string Tail(string s) => s.Length <= 400 ? s : s[^400..];
    }

    /// <summary>Grab a single frame as a jpg. Returns null if ffmpeg produced nothing.</summary>
    private async Task<string?> GrabFrameAsync(string videoPath, string dest, double seconds, CancellationToken ct)
    {
        if (!File.Exists(videoPath)) return null;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            var res = await ProcessRunner.RunAsync(_pipe.FfmpegCommand,
                new[] { "-y", "-hide_banner", "-loglevel", "error",
                        "-ss", Math.Max(0, seconds).ToString("0.000", ci), "-i", videoPath,
                        "-frames:v", "1", "-q:v", "3", dest },
                workingDir: null, TimeSpan.FromSeconds(60), ct);
            if (File.Exists(dest) && new FileInfo(dest).Length > 0) return dest;
            _log.LogWarning("Frame grab failed (exit {Code}).", res.ExitCode);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Frame grab error."); }
        return null;
    }

    /// <summary>Delete every file in the job dir except the ones to keep (the final mp4 + thumbnail).</summary>
    private void CleanupJobScratch(string jobDir, params string?[] keep)
    {
        try
        {
            var keepSet = new HashSet<string>(
                keep.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => Path.GetFullPath(k!)),
                StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(jobDir))
            {
                if (keepSet.Contains(Path.GetFullPath(f))) continue;
                try { File.Delete(f); } catch { /* best effort — a locked temp file isn't fatal */ }
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Job scratch cleanup failed for {Dir}.", jobDir); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private async Task TryGenerateThumbnailAsync(GenerationJob job, Storyboard sb, List<RenderBeat> beats, EffectiveChannel ch, string videoPath, string jobDir, CancellationToken ct)
    {
        try
        {
            // Meme Shorts should look like the Shorts grid/reference channel: a real vertical
            // frame with the Minecraft background and the white meme card, not a separate
            // 16:9 custom graphic.
            if (!ch.IsFinance)
            {
                var frame = await ExtractMemeThumbnailAsync(sb, beats, videoPath, jobDir, ct);
                if (frame is not null)
                {
                    job.ThumbnailPath = frame;
                    _log.LogInformation("Thumbnail (vertical frame): {Path}", frame);
                    return;
                }
                _log.LogWarning("Meme frame-grab failed; falling back to composite thumbnail.");
            }

            // Finance (and rare meme fallback): composite the data/panel card + hook text.
            string panel;
            if (ch.IsFinance)
            {
                panel = await ExtractFinanceCardAsync(sb, beats, videoPath, jobDir, ct);
            }
            else
            {
                int idx = BestMemeThumbnailBeat(sb, beats);
                panel = Path.Combine(jobDir, $"panel_{idx}.png");
            }

            var hook = !string.IsNullOrWhiteSpace(sb.ThumbnailText) ? sb.ThumbnailText!
                     : (beats.Count > 0 ? beats[0].Caption : sb.Title);

            var bgFrame = await ExtractBgFrameAsync(jobDir, ct);
            var thumb = _thumbnailer.Generate(panel, hook, Path.Combine(jobDir, "thumb.jpg"), bgFrame);
            job.ThumbnailPath = thumb;
            _log.LogInformation("Thumbnail generated: {Path}", thumb);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Thumbnail generation failed (non-fatal).");
        }
    }

    private static int BestMemeThumbnailBeat(Storyboard sb, List<RenderBeat> beats)
    {
        if (beats.Count == 0) return 0;

        // Beat 1 is prompted as the hook/first-frame, so prefer it when it has a real drawing.
        if (sb.Beats.Count > 0 && !string.IsNullOrWhiteSpace(sb.Beats[0].Drawing)) return 0;

        int last = beats.Count - 1;
        bool skippable(int i) => beats.Count > 1 && i == last; // avoid the fake-comment beat

        for (int i = 0; i < beats.Count && i < sb.Beats.Count; i++)
            if (!skippable(i) && !string.IsNullOrWhiteSpace(sb.Beats[i].Drawing)) return i;
        for (int i = 0; i < beats.Count && i < sb.Beats.Count; i++)
            if (!skippable(i) && !string.IsNullOrWhiteSpace(sb.Beats[i].RageFace)) return i;

        return 0;
    }

    /// <summary>
    /// Grab a real frame from the rendered short to use AS the thumbnail — a single card over the
    /// gameplay background, exactly as it appears in the video (like the reference channel). Picks
    /// a strong, settled beat (rage face &gt; drawing) and avoids the final beat (the fake comment
    /// slides in there). Returns the jpg path, or null if extraction failed.
    /// </summary>
    private async Task<string?> ExtractMemeThumbnailAsync(Storyboard sb, List<RenderBeat> beats, string videoPath, string jobDir, CancellationToken ct)
    {
        if (beats.Count == 0 || !File.Exists(videoPath)) return null;

        int last = beats.Count - 1;
        bool skippable(int i) => beats.Count > 1 && i == last; // don't grab the fake-comment beat

        int idx = sb.Beats.Count > 0 && !string.IsNullOrWhiteSpace(sb.Beats[0].Drawing) ? 0 : -1;
        for (int i = 0; i < beats.Count && idx < 0; i++)
            if (!skippable(i) && !string.IsNullOrWhiteSpace(sb.Beats[i].Drawing)) idx = i;
        for (int i = 0; i < beats.Count && idx < 0; i++)
            if (!skippable(i) && !string.IsNullOrWhiteSpace(sb.Beats[i].RageFace)) idx = i;
        if (idx < 0) idx = 0;

        var b = beats[idx];
        // ~65% into the beat: the card has fully popped in and settled.
        double sec = (b.StartMs + 0.65 * (b.EndMs - b.StartMs)) / 1000.0;

        var dest = Path.Combine(jobDir, "thumb.jpg");
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            var res = await ProcessRunner.RunAsync(_pipe.FfmpegCommand,
                new[] { "-y", "-hide_banner", "-loglevel", "error",
                        "-ss", sec.ToString("0.000", ci), "-i", videoPath,
                        "-frames:v", "1", "-q:v", "3", dest },
                workingDir: null, TimeSpan.FromSeconds(30), ct);

            if (File.Exists(dest) && new FileInfo(dest).Length > 0) return dest;
            _log.LogWarning("Thumbnail frame-grab produced no file (exit {Code}).", res.ExitCode);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Thumbnail frame-grab error."); }
        return null;
    }

    /// <summary>Extract a 1280x720 gameplay frame from the job's bg.mp4 for the thumbnail background.</summary>
    private async Task<string?> ExtractBgFrameAsync(string jobDir, CancellationToken ct)
    {
        var bg = Path.Combine(jobDir, "bg.mp4");
        if (!File.Exists(bg)) return null;
        var dest = Path.Combine(jobDir, "thumb_bg.png");
        try
        {
            var res = await ProcessRunner.RunAsync(_pipe.FfmpegCommand,
                new[] { "-y", "-hide_banner", "-ss", "1", "-i", bg,
                        "-vf", "scale=1280:720:force_original_aspect_ratio=increase,crop=1280:720", "-vframes", "1", dest },
                workingDir: null, TimeSpan.FromSeconds(30), ct);
            if (File.Exists(dest) && new FileInfo(dest).Length > 0) return dest;
            _log.LogWarning("Thumbnail bg-frame extract failed (exit {Code}).", res.ExitCode);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Thumbnail bg-frame extract error."); }
        return null;
    }

    /// <summary>Crop the finance data card out of a settled frame of the rendered video.</summary>
    private async Task<string> ExtractFinanceCardAsync(Storyboard sb, List<RenderBeat> beats, string videoPath, string jobDir, CancellationToken ct)
    {
        // Prefer the most visual beat: chart > comparison > stat > anything.
        string[] order = { "chart", "comparison", "stat", "tip" };
        int idx = -1;
        foreach (var type in order)
        {
            idx = sb.Beats.FindIndex(b => string.Equals(b.Visual?.Type, type, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) break;
        }
        if (idx < 0 || idx >= beats.Count) idx = Math.Max(0, beats.Count - 1);

        var b = beats[idx];
        double sec = (b.StartMs + 0.7 * (b.EndMs - b.StartMs)) / 1000.0;

        // Finance card geometry in the 1080x1920 frame (matches FinanceStage FCARD), cropped
        // slightly inside to avoid the rounded corners bleeding the gameplay background.
        var dest = Path.Combine(jobDir, "fin_card.png");
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var res = await ProcessRunner.RunAsync(_pipe.FfmpegCommand,
            new[] { "-y", "-hide_banner", "-ss", sec.ToString("0.000", ci), "-i", videoPath,
                    "-vf", "crop=742:892:169:434", "-vframes", "1", dest },
            workingDir: null, TimeSpan.FromSeconds(30), ct);

        if (File.Exists(dest) && new FileInfo(dest).Length > 0) return dest;
        _log.LogWarning("Finance card crop failed (exit {Code}); thumbnail will be text-only.", res.ExitCode);
        return Path.Combine(jobDir, "fin_card_missing.png"); // non-existent -> ThumbnailService draws text-only
    }

    /// <summary>
    /// Build word-level captions. Transcribes the per-beat audio with whisper (one model
    /// load for all beats) to get real word timing; falls back to even spread per beat.
    /// </summary>
    private async Task<List<Caption>> BuildCaptionsAsync(List<RenderBeat> beats, List<string> wavPaths, CancellationToken ct)
    {
        Dictionary<string, List<WordTiming>>? timings = null;
        if (_whisper.Enabled)
            timings = await _whisper.TranscribeAsync(wavPaths, ct);

        if (timings is null)
            return CaptionBuilder.Build(beats); // even spread

        var captions = new List<Caption>();
        for (int i = 0; i < beats.Count; i++)
        {
            timings.TryGetValue(wavPaths[i], out var words);
            captions.AddRange(CaptionBuilder.BuildForBeat(beats[i].Caption, beats[i].StartMs, beats[i].EndMs, words));
        }
        return captions;
    }

    /// <summary>
    /// Does this format swap the artwork every beat, or hold one image and stack text under it?
    ///
    /// Derived from the reference channel: "what X looks like" / option-reveal / correction
    /// videos show a NEW picture each beat (each superpower, each shirt), whereas list and
    /// monologue videos keep one artifact on screen and add lines beneath it.
    /// </summary>
    /// <summary>
    /// Native chart rendering is OFF. The chart style does not yet match the reference channel
    /// (proportions and line weight were still wrong on the last comparison), so rather than ship
    /// charts that look off, chart_gag falls back to the normal image path: the beat art is drawn
    /// like any other panel and the captions stack under it.
    ///
    /// To re-enable: flip this to true, re-enable the format in Prompts.Storyboard, and restore
    /// the "INCLUDE CHART-SHAPED TOPICS" block in the topic brainstorm. MemeChart.tsx and the
    /// chart schema are left intact for that.
    /// </summary>
    private const bool NativeChartsEnabled = false;

    private static bool IsChartGag(string? format) =>
        NativeChartsEnabled
        && string.Equals((format ?? "").Trim(), "chart_gag", StringComparison.OrdinalIgnoreCase);

    private static bool LayoutNeedsImagePerBeat(string? format) =>
        (format ?? "").Trim().ToLowerInvariant() switch
        {
            "single_visual_reveal" => true,
            "escalating_corrections" => true,
            "absurd_debate" => true,
            _ => false,   // relatable_list, relatable_chaos, internal_monologue, chart_gag
        };

    private static bool TryValidateMemeStoryboard(Storyboard storyboard, out string error)
    {
        if (storyboard.Beats.Count is < 3 or > 6)
        {
            error = $"expected 3-6 progressive reveal beats, received {storyboard.Beats.Count}.";
            return false;
        }

        // chart_gag draws natively from `visual` and has no artwork, so the drawing/visual_style
        // requirements below do not apply to it — it needs chart data on every beat instead.
        // chart_gag with chart data renders natively. WITHOUT it, fall through to the normal
        // image path rather than failing — Codex picks this format for ~1 in 3 videos, so a hard
        // rejection here would fail a third of all generations if it ever stops emitting
        // `visual`. A drawn chart is a worse chart, not a broken video.
        var chartBeats = storyboard.Beats.Count(b => b.Visual?.Chart is not null && b.Visual.Chart.Points.Count >= 3);
        if (IsChartGag(storyboard.Format) && chartBeats == storyboard.Beats.Count)
        {
            // The joke depends on the frame never moving, so the axis labels must be identical.
            var axis = string.Join("|", storyboard.Beats[0].Visual!.Chart!.Points.Select(p => p.Label));
            if (storyboard.Beats.Any(b => string.Join("|", b.Visual!.Chart!.Points.Select(p => p.Label)) != axis))
            {
                error = "chart_gag beats must all use the SAME axis labels — only the values may change.";
                return false;
            }
            error = "";
            return true;
        }

        var validStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "doodle", "photo", "graphic" };
        var missingDrawings = storyboard.Beats.Count(beat => string.IsNullOrWhiteSpace(beat.Drawing));
        if (missingDrawings > 1)
        {
            error = $"{missingDrawings} beats have no drawing description.";
            return false;
        }

        if (storyboard.Beats.Any(beat => !validStyles.Contains(beat.VisualStyle ?? "")))
        {
            error = "every beat must specify visual_style as doodle, photo, or graphic.";
            return false;
        }

        if (storyboard.Beats.Select(beat => beat.VisualStyle).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
        {
            error = "all progressive reveal beats must use the same visual_style.";
            return false;
        }

        if (storyboard.Beats.Any(beat => string.IsNullOrWhiteSpace(beat.Caption)))
        {
            error = "one or more beats have an empty caption.";
            return false;
        }

        static int WordCount(string? value) => string.IsNullOrWhiteSpace(value)
            ? 0
            : value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        // 22, not 14: `tts` is now a full conversational sentence rather than a copy of the
        // short on-screen caption, so a natural spoken line legitimately runs longer.
        if (storyboard.Beats.Any(beat => WordCount(string.IsNullOrWhiteSpace(beat.Tts) ? beat.Caption : beat.Tts) > 22))
        {
            error = "one or more beats exceed 22 spoken words.";
            return false;
        }

        // Reference-channel scripts measured 27-74 spoken words (mean ~40), so the ceiling is 75
        // — its observed maximum. A tighter 60 was tried and rejected real generations at 78 and
        // 85 words: it contradicted the prompt's own instruction to write full spoken sentences.
        var totalWords = storyboard.Beats.Sum(beat =>
            WordCount(string.IsNullOrWhiteSpace(beat.Tts) ? beat.Caption : beat.Tts));
        if (totalWords is < 20 or > 75)
        {
            error = $"expected 20-75 total spoken words, received {totalWords}.";
            return false;
        }

        if (FindClicheEscalation(storyboard) is { } cliche)
        {
            error = $"uses the banned courtroom/government escalation cliche (\"{cliche}\").";
            return false;
        }

        if (FindRoboticWriting(storyboard) is { } robotic)
        {
            error = robotic;
            return false;
        }

        error = "";
        return true;
    }

    /// <summary>
    /// Catch the structural tells of machine-written dialogue. The prompt asks for these too,
    /// but prompt-level bans have repeatedly leaked (the long-banned "mother of-" still reached
    /// 14% of scripts), so the ones that can be measured are enforced here and drive a retry.
    ///
    /// Only unambiguous, countable tells — nothing that needs taste to judge.
    /// </summary>
    private static string? FindRoboticWriting(Storyboard storyboard)
    {
        var lines = storyboard.Beats
            .Select(b => (string.IsNullOrWhiteSpace(b.Tts) ? b.Caption : b.Tts) ?? "")
            .Where(l => l.Trim().Length > 0)
            .ToList();
        if (lines.Count < 3) return null;

        // A 1-2 word final beat is the "Soup." / "Peel." / "Cricket." non-sequitur ending: the
        // script stops instead of resolving. Real endings state something; these state nothing,
        // and they were the loudest reason the output read as machine-written.
        var lastWords = lines[^1].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (lastWords <= 2)
            return $"the final beat (\"{lines[^1].Trim()}\") is a one-word non-sequitur — the ending must resolve the premise.";

        // Captions are now the panel's visible content (a stacked list that stays on screen), not
        // a throwaway overlay, so they get the same scrutiny as the spoken line. Real failures
        // that reached video: "NOZZLE YOGA" as a closing line, and the list
        // "USB LOGIC / BACK TO START / TINY PLASTIC TONGUE".
        var caps = storyboard.Beats.Select(b => (b.Caption ?? "").Trim()).Where(c => c.Length > 0).ToList();
        // Dialogue is exempt: its captions are spoken lines, and real speech is full of short
        // exclamations ("Hey Dave!", "Yeah...") that are correct here but would look like stubs.
        var isDialogue = string.Equals((storyboard.Format ?? "").Trim(), "character_dialogue",
            StringComparison.OrdinalIgnoreCase);
        if (caps.Count >= 3 && !isDialogue)
        {
            var stub = caps.FirstOrDefault(c =>
                c.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length < 3);
            if (stub is not null)
                return $"caption \"{stub}\" is too short to stand on its own — captions stay on screen as a list.";

            var lastCap = caps[^1].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            if (lastCap < 4)
                return $"the final caption (\"{caps[^1]}\") must state the answer, not trail off.";
        }

        // Tic words measured across past scripts: "once" reached 41% of them, "mother" 14%.
        foreach (var tic in new[] { "once", "mother" })
        {
            if (lines.Any(l => Regex.IsMatch(l, $@"\b{tic}\b", RegexOptions.IgnoreCase)))
                return $"uses the overused tic word \"{tic}\".";
        }

        // Same opening word across several beats = one template with the nouns swapped.
        var openers = lines
            .Select(l => Regex.Match(l.TrimStart(), @"^[A-Za-z']+").Value.ToLowerInvariant())
            .Where(w => w.Length > 0)
            .ToList();
        var repeatedOpener = openers.GroupBy(w => w).FirstOrDefault(g => g.Count() >= 3);
        if (repeatedOpener is not null)
            return $"{repeatedOpener.Count()} beats all open with \"{repeatedOpener.Key}\" — vary the sentence shapes.";

        // Uniform line length is the loudest AI tell: real speech mixes short and long.
        // Line-length uniformity is NOT enforced here any more. It was tried twice — first as
        // stdev < 1.5, then as spread <= 2 — and both rejected the large majority of real
        // attempts (9/11/11/11/10, 12/11/11/12/11, 13/15/14/13 …), burning several Codex calls
        // per job and pushing some to outright failure. Codex simply does not vary sentence
        // length on demand reliably enough for this to be a gate.
        //
        // The prompt still asks for varied rhythm, and the checks that remain here are all
        // precise ones with a clear right answer. A fuzzy stylistic preference does not belong
        // in a hard validator.

        return null;
    }

    /// <summary>
    /// Words that mark the legal/bureaucratic escalation rut. Measured across 141 stored
    /// storyboards, 28% escalated this way and "martial law" recurred across unrelated topics —
    /// the prompt alone did not stop it (the long-banned "mother of-" still leaked into 14%),
    /// so this is enforced in code. Backed by the storyboard retry loop, a hit just regenerates.
    ///
    /// Deliberately narrow: only phrases with no plausible innocent use in these scripts.
    /// Ambiguous words ("law" as in physics, "evidence", "committee") are left to the prompt so
    /// a legitimate script is never rejected three times and killed.
    /// </summary>
    private static readonly string[] ClicheEscalationTerms =
    {
        "martial law", "courtroom", "verdict", "treason", "felony", "lawsuit", "subpoena",
        "testify", "jurisdiction", "tribunal", "indict", "misdemeanor", "litigation",
        "war crime", "geneva",
    };

    private static string? FindClicheEscalation(Storyboard storyboard)
    {
        var text = string.Join(" ",
            storyboard.Beats.Select(b => $"{b.Caption} {b.Tts}")
                .Append(storyboard.FakeComment?.Text ?? ""))
            .ToLowerInvariant();

        return ClicheEscalationTerms.FirstOrDefault(t => text.Contains(t, StringComparison.Ordinal));
    }

    private static string PackageMemeTitle(string title)
    {
        var clean = title.Trim();
        while (clean.EndsWith("#Shorts", StringComparison.OrdinalIgnoreCase))
            clean = clean[..^7].TrimEnd();

        string[] emojis = ["😂", "🤣", "💀", "🤔", "😭"];
        if (emojis.Any(clean.EndsWith)) return clean;

        uint hash = 2166136261;
        foreach (char c in clean)
        {
            hash ^= char.ToLowerInvariant(c);
            hash *= 16777619;
        }
        return $"{clean} {emojis[hash % (uint)emojis.Length]}";
    }

    private async Task GeneratePanelAsync(Beat beat, int w, int h, string path, string panelSource, CancellationToken ct)
    {
        bool useCodex = string.Equals(panelSource, "Codex", StringComparison.OrdinalIgnoreCase);
        if (useCodex)
        {
            var ok = await _codex.GeneratePanelAsync(beat, w, h, path, ct);
            if (ok) return;
            throw new InvalidOperationException(
                $"Codex imagegen returned no image for '{beat.Caption}'. Stopped before TTS; no placeholder was used.");
        }
        await _localPanels.GenerateAsync(beat, w, h, path, ct);
    }

    /// <summary>
    /// Stage the background: pick a RANDOM clip from the backgrounds folder and a RANDOM
    /// start offset within it, then trim just the seconds this short needs into the job's
    /// bg.mp4. Keeps every render's background unique and lets source clips be any length
    /// (we never copy the whole file). Returns (filename, loopFrames?).
    /// </summary>
    /// <summary>
    /// Copy the channel's mascot sprite into the job folder and return its local name, or null
    /// when the channel has none (or the file has gone missing — a missing sticker is not worth
    /// failing a whole video over).
    /// </summary>
    private string? TryStageMascot(string jobDir, string? mascotPath)
    {
        if (string.IsNullOrWhiteSpace(mascotPath)) return null;
        var src = Path.IsPathRooted(mascotPath)
            ? mascotPath
            : Path.Combine(_env.ContentRootPath, mascotPath);
        if (!File.Exists(src))
        {
            _log.LogWarning("Channel mascot not found at {Path}; rendering without it.", src);
            return null;
        }

        var name = "mascot" + Path.GetExtension(src);
        try
        {
            File.Copy(src, Path.Combine(jobDir, name), overwrite: true);
            return name;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not stage the mascot from {Path}.", src);
            return null;
        }
    }

    private async Task<(string? name, int? loopFrames)> TryStageBackgroundAsync(string jobDir, string backgroundDir, int fps, int totalMs, CancellationToken ct)
    {
        var dir = Path.IsPathRooted(backgroundDir)
            ? backgroundDir
            : Path.Combine(_env.ContentRootPath, backgroundDir);
        if (!Directory.Exists(dir)) return (null, null);

        var vidExts = new[] { ".mp4", ".mkv", ".mov", ".webm", ".avi" };
        var clips = new DirectoryInfo(dir).GetFiles("*.*")
            .Where(f => vidExts.Contains(f.Extension.ToLowerInvariant()))
            .ToArray();
        if (clips.Length == 0) return (null, null);

        var clip = _pipe.BackgroundRandomize
            ? clips[Random.Shared.Next(clips.Length)]
            : clips.OrderByDescending(f => f.LastWriteTimeUtc).First();

        var dest = Path.Combine(jobDir, "bg.mp4");
        int neededSec = (int)Math.Ceiling(totalMs / 1000.0) + 1; // slack so the loop never ends early

        double startSec = 0;
        var srcSeconds = await ProbeDurationSecondsAsync(clip.FullName, ct);
        if (_pipe.BackgroundRandomize && srcSeconds is double sd && sd > neededSec + 1)
        {
            double maxStart = sd - neededSec - 0.5;
            double minGap = Math.Max(neededSec, _pipe.BackgroundMinGapSec); // >= segment length => no overlap
            startSec = _bgHistory.PickSpacedOffset(clip.FullName, maxStart, minGap);
        }

        _log.LogInformation("Background: {Clip} @ {Start:0.0}s (+{Len}s)", clip.Name, startSec, neededSec);

        bool trimmed = await TrimAsync(clip.FullName, startSec, neededSec, dest, ct);
        if (!trimmed)
        {
            try { File.Copy(clip.FullName, dest, overwrite: true); }
            catch (Exception ex) { _log.LogWarning(ex, "Background copy fallback failed."); return (null, null); }
        }

        var destSeconds = await ProbeDurationSecondsAsync(dest, ct);
        int? loopFrames = destSeconds is double s && s > 0 ? Math.Max(1, (int)Math.Floor(s * fps)) : null;
        return ("bg.mp4", loopFrames);
    }

    /// <summary>
    /// Copy a random music track into the job dir as music.&lt;ext&gt; (Remotion staticFile) and,
    /// if it's shorter than the video, return its length so Remotion loops it.
    /// </summary>
    private async Task<(string? name, int? loopFrames, string? attribution)> TryStageMusicAsync(string jobDir, string musicDir, int fps, int totalMs, CancellationToken ct)
    {
        var dir = Path.IsPathRooted(musicDir)
            ? musicDir
            : Path.Combine(_env.ContentRootPath, musicDir);
        if (!Directory.Exists(dir)) return (null, null, null);

        var exts = new[] { ".mp3", ".m4a", ".wav", ".ogg", ".flac" };
        var tracks = new DirectoryInfo(dir).GetFiles("*.*")
            .Where(f => exts.Contains(f.Extension.ToLowerInvariant()))
            .ToArray();
        if (tracks.Length == 0) return (null, null, null);

        var track = _pipe.MusicRandomize
            ? tracks[Random.Shared.Next(tracks.Length)]
            : tracks.OrderByDescending(f => f.LastWriteTimeUtc).First();

        var destName = "music" + track.Extension.ToLowerInvariant();
        var dest = Path.Combine(jobDir, destName);
        try { File.Copy(track.FullName, dest, overwrite: true); }
        catch (Exception ex) { _log.LogWarning(ex, "Could not stage music track."); return (null, null, null); }

        // CC attribution: a sidecar "<track>.txt" next to the audio file (if present).
        string? attribution = null;
        var sidecar = Path.ChangeExtension(track.FullName, ".txt");
        if (File.Exists(sidecar))
        {
            try { attribution = (await File.ReadAllTextAsync(sidecar, ct)).Trim(); } catch { /* ignore */ }
        }
        _log.LogInformation("Music: {Track} @ {Vol:0.00} volume (attribution: {Attr})",
            track.Name, _pipe.MusicVolume, attribution is null ? "none" : "yes");

        // Only loop if the track is shorter than the video; these tracks are minutes long so usually not.
        var secs = await ProbeDurationSecondsAsync(dest, ct);
        int? loopFrames = (secs is double s && s > 0 && s * 1000 < totalMs)
            ? Math.Max(1, (int)Math.Floor(s * fps))
            : null;
        return (destName, loopFrames, attribution);
    }

    /// <summary>Trim lenSec seconds starting at startSec into a muted mp4 via ffmpeg.</summary>
    private async Task<bool> TrimAsync(string src, double startSec, int lenSec, string dest, CancellationToken ct)
    {
        try
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var res = await ProcessRunner.RunAsync(
                _pipe.FfmpegCommand,
                new[]
                {
                    "-y", "-hide_banner",
                    "-ss", startSec.ToString("0.000", ci), "-i", src, "-t", lenSec.ToString(ci),
                    // Scale + centre-crop to the OUTPUT resolution here, not in the browser.
                    // Source gameplay is 2560x1440; without this the staged clip stays 1440p and
                    // Remotion's compositor decodes an 11 MB frame (2560*1440*3) for every frame
                    // of the render, which OOM-crashed it outright. Cropping in ffmpeg once cuts
                    // that to 1080*1920*3 and produces exactly what the composition displays
                    // anyway (Background.tsx was already object-fit cropping it to this).
                    // setsar=1 matters: scaling with force_original_aspect_ratio can leave a
                    // fractional pixel aspect (observed SAR 10240:10239), which makes the
                    // decoder report a display size that disagrees with the frame size.
                    "-vf", $"scale={_pipe.Width}:{_pipe.Height}:force_original_aspect_ratio=increase," +
                           $"crop={_pipe.Width}:{_pipe.Height},setsar=1",
                    "-an", "-c:v", "libx264", "-preset", _pipe.BackgroundReencodePreset,
                    "-crf", "23", "-pix_fmt", "yuv420p",
                    // DENSE KEYFRAMES — this is what stops Remotion stalling mid-render.
                    // Remotion's OffthreadVideo seeks to arbitrary timestamps, and each seek must
                    // decode forward from the previous keyframe. Left to itself x264 put them
                    // ~1.9s apart (max 2.9s) = up to 174 frames of decoding per seek at 60fps,
                    // which no longer fits the bounded frame cache, so it re-decoded constantly
                    // and blew the 123s delayRender budget ("waiting for the page to render the
                    // React component at frame N"). Half a second between keyframes caps that at
                    // ~30 frames. Costs a few MB per clip; the clip is transient scratch anyway.
                    "-g", "30", "-keyint_min", "30", "-sc_threshold", "0",
                    "-movflags", "+faststart", dest,
                },
                workingDir: null, TimeSpan.FromSeconds(180), ct);

            if (File.Exists(dest) && new FileInfo(dest).Length > 0) return true;
            _log.LogWarning("Background trim produced no file (exit {Code}). {Err}", res.ExitCode, TailErr(res.StdErr));
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Background trim failed.");
            return false;
        }
    }

    private static string TailErr(string s, int n = 400) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[^n..]);

    private static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= n ? s : s[..n]);

    /// <summary>Parse a clip's duration in seconds from `ffmpeg -i` stderr. Null if unknown.</summary>
    private async Task<double?> ProbeDurationSecondsAsync(string mp4Path, CancellationToken ct)
    {
        try
        {
            // `ffmpeg -i <file>` prints "Duration: HH:MM:SS.ss" to stderr and exits non-zero
            // (no output file specified) — that's expected; we just parse stderr.
            var res = await ProcessRunner.RunAsync(
                _pipe.FfmpegCommand,
                new[] { "-hide_banner", "-i", mp4Path },
                workingDir: null, TimeSpan.FromSeconds(30), ct);

            var m = System.Text.RegularExpressions.Regex.Match(res.StdErr, @"Duration:\s*(\d+):(\d+):(\d+)\.(\d+)");
            if (m.Success)
            {
                double seconds = int.Parse(m.Groups[1].Value) * 3600
                               + int.Parse(m.Groups[2].Value) * 60
                               + int.Parse(m.Groups[3].Value)
                               + double.Parse("0." + m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (seconds > 0) return seconds;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ffmpeg duration probe failed for {Path}.", mp4Path);
        }
        return null;
    }

    /// <summary>
    /// BOM-less UTF-8 write. (Same lesson as the CapCut drafts: a UTF-8 BOM
    /// breaks downstream JSON consumers.)
    /// </summary>
    private static void WriteJsonNoBom(string path, object value)
    {
        var json = JsonSerializer.Serialize(value, PropsJson);
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void Stage(GenerationJob job, string stage, int percent)
    {
        job.Stage = stage;
        job.Percent = percent;
        _log.LogInformation("[{Job}] {Stage} ({Percent}%)", job.Id, stage, percent);
    }
}
