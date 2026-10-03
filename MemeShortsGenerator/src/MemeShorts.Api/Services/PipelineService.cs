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
    private readonly ImageCensorService _censor;
    private readonly CutoutService _cutout;
    private readonly RenderValidator _renderCheck;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<PipelineService> _log;

    private static readonly JsonSerializerOptions PropsJson = new() { WriteIndented = true };

    public PipelineService(
        ICodexClient codex, ITtsService tts, IPanelRenderer localPanels, IVideoRenderer renderer,
        IWhisperCaptionService whisper, BackgroundHistory bgHistory,
        ITitleStyleService titleStyle, IThumbnailService thumbnailer, ChannelRegistry channels,
        PostSliceService slicer, DiagramSliceService diagrams, IOcrService ocr,
        ImageCensorService censor, CutoutService cutout, RenderValidator renderCheck,
        IOptions<PipelineOptions> pipe, IOptions<CodexOptions> codexOpt,
        IWebHostEnvironment env, ILogger<PipelineService> log)
    {
        _codex = codex; _tts = tts; _localPanels = localPanels; _renderer = renderer;
        _whisper = whisper; _bgHistory = bgHistory;
        _titleStyle = titleStyle; _thumbnailer = thumbnailer; _channels = channels;
        _slicer = slicer; _diagrams = diagrams; _ocr = ocr; _censor = censor; _cutout = cutout; _renderCheck = renderCheck;
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

        // CHECK THE FINISHED VIDEO. Every format, not just reposts - a list short reaching the
        // Review tab frozen or cut short is exactly as bad as a repost doing it, and for a long
        // while nothing looked at one at all.
        if (OperatingSystem.IsWindows())
        {
            Stage(job, "checking the video", 95);
            var faults = await _renderCheck.ValidateFileAsync(outMp4, totalMs / 1000.0, ct);
            if (faults.Count > 0)
            {
                job.VideoPath = null;
                throw new InvalidOperationException(
                    "The finished video did not pass its own check, so it was not published:\n"
                    + string.Join("\n", faults.Select(f => $"  - {f.Message}")));
            }
        }

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
        // What the RENDERER is given. It becomes a censored copy of the pasted picture the
        // moment a swear word is found in it; the pasted original is left on disk untouched so
        // the job can be re-voiced or re-diagnosed from what actually arrived.
        var renderImage = input.Image;

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

            // Would this plan leave rows of the picture that nothing draws? Checked BEFORE the
            // render, because it is free and a render is three minutes. A column split is the
            // usual culprit, and a plain full-width reveal is always safe - so if the split is
            // the thing producing blank rows, drop it and re-slice rather than shipping a card
            // with a hole in it.
            var gaps = _renderCheck.ValidateBands(bands);
            if (gaps.Count > 0 && bands.Gutter is not null)
            {
                _log.LogWarning("Repost {Id}: the column split would leave blank rows ({Why}). "
                    + "Re-slicing as one full-width stack.", job.Id,
                    string.Join("; ", gaps.Select(g => g.Message)));
                bands = _slicer.Analyze(imagePath, ocrLines, _diagrams, typedLines, allowColumns: false);
                gaps = _renderCheck.ValidateBands(bands);
            }
            if (gaps.Count > 0)
                _log.LogWarning("Repost {Id}: blank rows remain after falling back ({Why}); the "
                    + "post-render check will decide.", job.Id,
                    string.Join("; ", gaps.Select(g => g.Message)));
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

            // Blur any swear words out of the picture. This runs on the SAME OCR pass the
            // slicing came from, so a hit is covered exactly where that word sits and every
            // band boundary still lines up - the censored copy is the same size, pixel for
            // pixel. Only the picture is censored; the voice still reads the post verbatim.
            renderImage = _censor.Censor(imagePath, ocrLines) ?? input.Image;
        }

        var sliced = bands is not null && bandTexts.Count > 0;

        // The typed box is the post written CORRECTLY; the slicer holds it cut into the bands
        // the video reveals. They rarely agree on line count, so the two are matched by content
        // and each slice speaks the typed spelling of the words it aligns to.
        //
        // Both simpler rules have now been shipped and both were wrong. Applying typed lines
        // POSITIONALLY smeared five stored paragraphs across twelve slices - the huge blank gaps
        // - and also let a frozen "Ethan" byline be read aloud. Refusing typed text whenever the
        // counts differed then dropped the narration back to raw OCR, and the voice said
        // "eleven-m deleting this app and" because OCR reads "I'm" as "11m". Matching keeps the
        // right words AND one line per slice.
        var spokenPer = sliced ? bandTexts.ToList() : RepostComposer.Wrap(input.Text ?? "").Select(l => l.Text).ToList();

        // A COMIC is written one panel at a time, and so is the box: its blank-line separated
        // paragraphs are the panels' captions, in order. Where the counts line up that mapping is
        // exact, and far better than matching words - most of a comic's lettering is drawn rather
        // than typed, so panel one of the AI art meme anchored on three words of its own and then
        // read the whole of panel two's paragraph as well.
        var paragraphs = (input.Text ?? "")
            .Replace("\r\n", "\n")
            .Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(para => string.Join(" ", para.Split('\n')
                                                 .Select(x => x.Trim())
                                                 .Where(x => x.Length > 0)))
            .Where(para => para.Length > 0)
            .ToList();
        var speaking = Enumerable.Range(0, bandTexts.Count)
                                 .Where(i => bandTexts[i].Trim().Length > 0)
                                 .ToList();

        if (sliced && (bands?.IsDiagram ?? false) && paragraphs.Count > 0
            && paragraphs.Count == speaking.Count)
        {
            spokenPer = bandTexts.Select(_ => "").ToList();
            for (int i = 0; i < speaking.Count; i++) spokenPer[speaking[i]] = paragraphs[i];
            _log.LogInformation("Repost {Id}: {N} panel(s) read from the typed paragraphs.",
                job.Id, paragraphs.Count);
        }
        else if (sliced && typedLines.Count > 0)
        {
            spokenPer = RepostComposer.RewordFromTyped(
                bandTexts, string.Join(" ", typedLines), out var reworded, out var unmatchedSlices);

            // A slice whose words appear NOWHERE in the typed post is something OCR took off the
            // picture rather than words anyone wrote: "AL" read off the Tarantino meme's "AI" and
            // spoken "ay-ell", or the Pooh meme's "PAYEDJOIWORICOUT" and "BLUNLOADINCTRUCKS,",
            // which are its outlined caption read a second time and badly. Those slices keep
            // their place in the reveal, so the rows still show, and are given nothing to say.
            //
            // Only when the typed post really is a transcription of this picture, though - if
            // barely any slice matched it, the box holds something else (a title, a note) and its
            // silence would mute the whole video.
            if (reworded * 2 >= bandTexts.Count)
            {
                foreach (var i in unmatchedSlices)
                {
                    if (string.IsNullOrWhiteSpace(spokenPer[i])) continue;
                    _log.LogInformation("Repost {Id}: slice {N} (\"{Text}\") appears nowhere in "
                        + "the typed post; revealing it without speaking it.",
                        job.Id, i, spokenPer[i]);
                    spokenPer[i] = "";
                }
            }
            _log.LogInformation(
                "Repost {Id}: re-worded {N} of {Total} slice(s) from the typed text; the rest keep "
                + "their own reading.", job.Id, reworded, bandTexts.Count);
        }
        else if (!sliced && typedLines.Count > 0)
        {
            spokenPer = typedLines;
        }

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
                var unmatched = RepostComposer.Align(lines, heard);

                // Only lines OUTSIDE the spoken span are dropped. A line the transcript missed
                // that sits BETWEEN two it did hear was certainly read out - the voice reads the
                // script top to bottom - and it is usually a coinage the transcript spells its
                // own way ("Zucken-Musk"). Dropping those threw away real content AND left one
                // more slice than narration line, so every reveal after it ran ahead of the
                // voice. Align now interpolates their timing, so they can simply stay.
                var matchedIdx = Enumerable.Range(0, lines.Count)
                    .Where(i => !unmatched.Contains(i))
                    .ToList();
                var unspoken = matchedIdx.Count == 0
                    ? unmatched.ToList()
                    : unmatched.Where(i => i < matchedIdx[0] || i > matchedIdx[^1]).ToList();
                foreach (var i in unmatched.Where(i => !unspoken.Contains(i)))
                    _log.LogInformation("Repost {Id}: card line {N} is not in the transcript but "
                        + "sits inside it, so it was spoken; keeping it: {Text}",
                        job.Id, i, lines[i].Text);

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
            var postWords = useTyped ? input.Text ?? "" : string.Join("\n", bandTexts);
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
                Image = renderImage,
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
        //
        // Taken BEFORE the check so a video that fails it still has a poster in the Failed tab.
        if (_pipe.GenerateThumbnail)
        {
            var thumb = await GrabFrameAsync(outMp4, Path.Combine(jobDir, "thumb.jpg"), totalMs * 0.9 / 1000.0, ct);
            if (thumb is not null) job.ThumbnailPath = thumb;
        }

        // CHECK THE FINISHED VIDEO. Everything else in this pipeline validates the plan, which is
        // how a card with half of it blank reached the Review tab marked "Info". Matthew asked for
        // this directly - "i dont wanna see any broken or mistakes" - and chose failing the job
        // over showing a flagged one, so a video that does not pass never becomes reviewable.
        //
        // The file is KEPT and stays watchable. Failing the job used to null VideoPath too, which
        // left nothing to look at but a one-line message - "what picture was this" - and no way to
        // judge whether the fault was real. The job is Failed, and Approve refuses anything that
        // is not Done, so a video that lands here still cannot be published; it can only be
        // watched in the Failed tab and re-rendered once the cause is fixed.
        if (OperatingSystem.IsWindows())
        {
            Stage(job, "checking the video", 95);
            var faults = _renderCheck.ValidateProps(props)
                .Concat(await _renderCheck.ValidateAsync(
                    outMp4, totalMs / 1000.0, props.Post?.GutterFrac is not null, ct))
                .ToList();
            if (faults.Count > 0)
                throw new InvalidOperationException(
                    "The finished video did not pass its own check, so it was not published:\n"
                    + string.Join("\n", faults.Select(f => $"  - {f.Message}")));
        }

        // Keep the source post + image so the job can be re-voiced without re-uploading.
        if (_pipe.CleanupIntermediates)
            // props.json is KEPT. It is a few KB and the only record of what the reveal actually
            // did - which slice appeared when, and in which column. Without it neither the
            // post-render check nor anyone debugging a bad video can see anything but pixels.
            CleanupJobScratch(jobDir, new[] { outMp4, job.ThumbnailPath, inputPath,
                    Path.Combine(jobDir, "props.json") }
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

        // Where each rendered slice sits in the ORIGINAL post, used to order them at the end.
        // Kept separately because a slice's own Top is its CROP, which reaches upward.
        var sourceTop = new Dictionary<PostBlockRender, int>();

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
        {
            var header = new PostBlockRender
            {
                // Header spans down to HeaderBottom, which may include the avatar hanging
                // below the byline text.
                Top = b.Top, Bottom = Math.Max(b.Bottom, bands.HeaderBottom),
                StartMs = 0, Text = b.Text,
            };
            sourceTop[header] = b.Top;
            blocks.Add(header);
        }

        var captions = bands.CaptionLines.ToList();

        // Where each caption's crop starts - one shared definition, in PostSliceService, so the
        // reveal, the hole patch and the post-render check can never model different rules.
        var cropTops = PostSliceService.CropTops(bands.Blocks, bands.Width, bands.HeaderBottom);

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
            var line = new PostBlockRender
            {
                Top = cropTops.TryGetValue(caption, out var ct) ? ct : caption.Top,
                Bottom = caption.Bottom,
                StartMs = firstSpokenAt.TryGetValue(caption, out var at) ? at : 0,
                Text = caption.Text,
                XLeft = caption.XLeft,
                XRight = caption.XRight,
                Side = SideName(caption.Side),
                Column = caption.Column,
                Group = caption.Group,
            };
            sourceTop[line] = caption.Top;
            blocks.Add(line);
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
            // Measured on the captions' OWN edges, never on their chained crops. A slice
            // reaches up into the picture above it to keep the line spacing, so comparing
            // against that reached-back edge made a picture wait for the SECOND line it heads
            // instead of the first: the boycotts meme played its opening line over a blank card
            // and only then revealed the panel the line was describing.
            int? SpokenAt(PostBlock c) => firstSpokenAt.TryGetValue(c, out var t) ? t : null;
            var below = captions.Where(c => c.Text is not null && c.Top >= m.Bottom)
                                .Select(SpokenAt).Where(t => t is not null).ToList();
            var above = captions.Where(c => c.Text is not null && c.Bottom <= m.Top)
                                .Select(SpokenAt).Where(t => t is not null).ToList();
            var at = isSinglePhotoPost ? 0
                : below.Count > 0 ? below.Min()!.Value
                : above.Count > 0 ? above.Max()!.Value
                : 0;

            // A picture inside a comparison is whole and full width, so it belongs to the
            // comparison rather than to one column: it arrives with the FIRST line spoken in
            // the group, and stays for the rest of it.
            if (m.Group is not null)
            {
                // ...and a picture sitting IN a column belongs to that column, so it waits for
                // that column's turn. Giving every picture the group's first line put the right
                // half's artwork on screen while the left column was still being read - which is
                // the "you have shown left and right at the same time" defect itself, and it is
                // what the two wolves and AI art memes were doing.
                // Which line of that column it waits for depends on where the picture SITS.
                // A picture ABOVE its column's lines arrives with the first of them - that is
                // the wolves photo, read under a heading. A picture BELOW them is the column's
                // punchline and has to wait for the lines above it to be read. Handing every
                // picture the column's FIRST line put "I SEE THAT! HEY COMPANIES!!" on screen
                // at 0ms, under four captions that had not been read yet - the answer given
                // away before the question, which is the whole point of the format.
                var mine = captions
                    .Where(c => c.Group == m.Group
                             && (m.Side == PostSide.Full || c.Side == m.Side))
                    .ToList();
                int Spoken(PostBlock c) => firstSpokenAt.TryGetValue(c, out var t) ? t : int.MaxValue;

                // The first line BELOW it: the reveal reaches the picture's rows as that line is
                // read, so nothing appears ahead of the line it follows.
                var anchor = mine.Where(c => c.Top >= m.Bottom - 4).OrderBy(Spoken).FirstOrDefault()
                    // Nothing below it - the picture ends the column, so it waits for the LAST
                    // line above it.
                    ?? mine.Where(c => c.Bottom <= m.Top + 4)
                           .OrderByDescending(c => firstSpokenAt.TryGetValue(c, out var t) ? t : int.MinValue)
                           .FirstOrDefault()
                    ?? mine.OrderBy(Spoken).FirstOrDefault();

                at = anchor is not null && firstSpokenAt.TryGetValue(anchor, out var ft) ? ft : 0;
            }

            var block = new PostBlockRender
            {
                Top = m.Top,
                Bottom = m.Bottom,
                StartMs = at,
                XLeft = m.XLeft,
                XRight = m.XRight,
                Side = SideName(m.Side),
                Column = m.Column,
                Group = m.Group,
            };
            sourceTop[block] = m.Top;
            blocks.Add(block);
        }

        // THE ORDER OF THIS LIST IS THE LAYOUT: the card stacks what it is given, top to bottom.
        //
        // So it has to be the post's own order, measured on the original rows - a slice's Top is
        // its crop, which reaches upward into whatever is above it, and ordering on that put the
        // first caption of "two wolves" ABOVE the photo it is written under. Every slice of a
        // comparison carries the group's first row, which both keeps the columns where they
        // belong in the post and keeps them in ONE run: split into two runs, the card draws the
        // group's box twice and the post comes out taller than itself.
        var groupRow = bands.Blocks
            .Where(b => b.Group is not null)
            .GroupBy(b => b.Group!.Value)
            .ToDictionary(g => g.Key, g => g.Min(b => b.Top));

        int RowOf(PostBlockRender b) =>
            b.Group is { } g && groupRow.TryGetValue(g, out var top)
                ? top
                : sourceTop.TryGetValue(b, out var own) ? own : b.Top;

        blocks = blocks
            .OrderBy(RowOf)
            .ThenBy(b => sourceTop.TryGetValue(b, out var own) ? own : b.Top)
            .ToList();

        return (blocks, spotlights);
    }

    /// <summary>
    /// A LIST SHORT: a run of supplied pictures, one spoken line each, over blurred gameplay.
    ///
    /// Deliberately reuses the meme path's beat machinery — one voice clip per beat, whisper
    /// word timings, background and music staging — because the only thing that differs is
    /// where the pictures come from. Nothing is written or drawn here; both halves are supplied.
    /// </summary>
    /// <summary>
    /// A picture's width/height, so the list card can take the media's own shape. Null when it
    /// cannot be read - the card then falls back to its default.
    /// </summary>
    private double? PictureAspect(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return null;
        try
        {
            using var img = System.Drawing.Image.FromFile(path);
            if (img.Width <= 0 || img.Height <= 0) return null;
            return (double)img.Width / img.Height;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read the size of {File}.", Path.GetFileName(path));
            return null;
        }
    }

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

        // A picture cut out in an EARLIER render sits on disk under its cutout name while
        // list.json still names the original, so every re-render asked the renderer for a file
        // that no longer existed and 404'd. That made re-rendering ANY list short impossible -
        // the format this channel publishes most - and it was invisible until one was tried.
        var renamed = false;
        for (int i = 0; i < items.Count; i++)
        {
            var name = items[i].Image!;
            if (File.Exists(Path.Combine(jobDir, name))) continue;
            var cutoutName = Path.GetFileNameWithoutExtension(name) + ".cutout.png";
            if (File.Exists(Path.Combine(jobDir, cutoutName)))
            {
                _log.LogInformation("List {Id}: {Old} was cut out previously; using {New}.",
                    job.Id, name, cutoutName);
                items[i] = new ListItem { Image = cutoutName, Text = items[i].Text };
                renamed = true;
            }
        }

        // 0. Cut the subject out of any picture that was shot on a plain background.
        //
        // The last measured difference from the reference channel: his artwork is a cutout
        // sitting on the card's own white, ours were photo rectangles. Only pictures that can be
        // cut SAFELY are converted - the rest stay photographs, because a botched cutout on a
        // white card looks worse than an honest rectangle. See CutoutService.
        var cut = 0;
        if (OperatingSystem.IsWindows())
        {
            Stage(job, "cutting out pictures", 15);
            for (int i = 0; i < items.Count; i++)
            {
                var path = Path.Combine(jobDir, items[i].Image!);
                if (!File.Exists(path) || IsVideoMedia(path)) continue;
                var cutout = _cutout.TryCutout(path);
                if (cutout is null) continue;
                items[i] = new ListItem { Image = cutout, Text = items[i].Text };
                cut++;
            }
            _log.LogInformation("List {Id}: cut out {N} of {Total} picture(s).", job.Id, cut, items.Count);
        }

        // The stored input must name the files that now exist - both the ones just cut out and
        // the ones a PREVIOUS render cut. Without this the cutout is a one-way door: it replaces
        // the picture on disk and the job can never be re-rendered again.
        if (cut > 0 || renamed)
        {
            input.Items = items;
            WriteJsonNoBom(inputPath, input);
        }

        // 1. ONE voice take for the whole script, not a clip per line.
        //
        // The reference cuts to a new picture every ~1.2s (measured across 20 of his Shorts), so
        // a line is now a few words, and voicing each as its own clip put a gap and a fresh
        // intonation at every cut. One take reads like one person talking - his median is
        // 201 wpm with the first word at 0.0s - and the pictures are cut at word timings after.
        Stage(job, "voicing the script", 25);
        var lines = items.Select(i => i.Text.Trim()).ToList();
        const string narrationName = "narration.wav";
        var narrationPath = Path.Combine(jobDir, narrationName);
        // Cued per SENTENCE, not per line: a sentence spans several picture lines, and cueing only
        // the fragment that carries the "!" made the voice perk up for its last two words.
        var speechMs = await _tts.SynthesizeAsync(
            AudioTags.Cue(string.Join(" ", lines), SpeakableListLine), narrationPath, ch.WindowsVoice, null, ct, ch.WindowsRate);
        if (speechMs <= 0)
            speechMs = (int)Math.Round(lines.Sum(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length) / 200.0 * 60_000);

        // 2. Word timings for the take, then every written word placed on them.
        Stage(job, "timing captions", 60);
        List<WordTiming>? heard = null;
        if (_whisper.Enabled)
        {
            var timings = await _whisper.TranscribeAsync(new[] { narrationPath }, ct);
            if (timings is not null && timings.TryGetValue(narrationPath, out var w)) heard = w;
        }
        if (heard is null)
            _log.LogWarning("List {Id}: no word timings for the narration; spreading lines by length.", job.Id);

        // PACE. The first one-take render measured 160 wpm with the voice's global rate at 1.05;
        // his median is 201. Rather than move the global rate (the meme format shares it and was
        // tuned separately), measure THIS take and time-stretch it to his pace. atempo keeps the
        // pitch, and the cap keeps a very slow take from turning into chipmunk speech.
        if (heard is { Count: >= 5 })
        {
            var spanMs = Math.Max(1, heard[^1].EndMs - heard[0].StartMs);
            var wordCount = lines.Sum(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
            var wpm = wordCount / (spanMs / 60_000.0);
            var factor = Math.Clamp(ListTargetWpm / wpm, 1.0, 1.35);
            if (factor > 1.02)
            {
                var stretched = Path.Combine(jobDir, "narration_fast.wav");
                try
                {
                    await RunFfmpegAsync(
                        $"-y -i \"{narrationPath}\" -filter:a \"atempo={factor.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}\" \"{stretched}\"",
                        ct);
                    File.Move(stretched, narrationPath, overwrite: true);
                    heard = heard.Select(w => new WordTiming(w.Text,
                        (int)Math.Round(w.StartMs / factor), (int)Math.Round(w.EndMs / factor))).ToList();
                    speechMs = (int)Math.Round(speechMs / factor);
                    _log.LogInformation("List {Id}: take read at {Wpm:0} wpm; sped up x{Factor:0.00} to ~{Target}.",
                        job.Id, wpm, factor, ListTargetWpm);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "List {Id}: could not speed the narration up; keeping it as read.", job.Id);
                }
            }
        }
        var timed = NarrationAligner.Align(lines, heard, speechMs);

        // His picture lands a hair BEFORE the word it shows - 0.1-0.2s ahead on the gelatin
        // short (jello at 3.25s for "gelatin" at 3.46s). Cutting exactly on the word reads late.
        const int pictureLeadMs = 120;
        var minBeatMs = (int)Math.Ceiling(1000.0 / fps) * 3;
        var beats = new List<RenderBeat>();
        for (int i = 0; i < items.Count; i++)
        {
            var first = timed.FirstOrDefault(t => t.Line == i);
            var start = i == 0 ? 0 : Math.Max(beats[^1].StartMs + minBeatMs, (first?.StartMs ?? beats[^1].EndMs) - pictureLeadMs);
            if (beats.Count > 0) beats[^1].EndMs = start;

            var mediaName = items[i].Image!;
            var mediaPath = Path.Combine(jobDir, mediaName);
            var isClip = IsVideoMedia(mediaPath);
            var clipSeconds = isClip ? await ProbeDurationSecondsAsync(mediaPath, ct) : null;
            var mediaAspect = isClip
                ? await ProbeVideoAspectAsync(mediaPath, ct)
                : PictureAspect(mediaPath);

            beats.Add(new RenderBeat
            {
                Index = i,
                Caption = items[i].Text,
                Panel = isClip ? "" : mediaName,
                Clip = isClip ? mediaName : null,
                ClipLoopFrames = isClip && clipSeconds is double seconds
                    ? Math.Max(1, (int)Math.Round(seconds * fps))
                    : null,
                IsCutout = !isClip && mediaName.EndsWith(".cutout.png", StringComparison.OrdinalIgnoreCase),
                Audio = "",
                StartMs = start,
                EndMs = Math.Max(start + minBeatMs, speechMs),
                PanelAspect = mediaAspect,
            });
        }

        var captions = timed.Select((t, k) => new Caption
        {
            // @remotion/captions expects a leading space to join tokens cleanly.
            Text = (k == 0 ? "" : " ") + t.Text,
            StartMs = t.StartMs,
            EndMs = t.EndMs,
            TimestampMs = t.StartMs + (t.EndMs - t.StartMs) / 2,
            Confidence = heard is null ? 0.5 : 0.95,
        }).ToList();

        // His videos stop 0.03-0.4s after the last word (median ~0.15s). The old 700ms tail was
        // tuned for the meme format's read-aloud comment and is dead air here. 300 still measured
        // 0.47-0.63s of silence at the end - the voice's own trailing breath comes on top.
        const int listTailMs = 100;
        var totalMs = beats[^1].EndMs + listTailMs;
        beats[^1].EndMs = totalMs;

        Stage(job, "staging background", 70);
        var (backgroundName, backgroundLoopFrames) =
            await TryStageBackgroundAsync(jobDir, ch.BackgroundDir, fps, totalMs, ct);
        var (musicName, musicLoopFrames, musicAttribution) =
            await TryStageMusicAsync(jobDir, ch.MusicDir, fps, totalMs, ct);
        job.MusicAttribution = musicAttribution;

        // Fall back to the channel's own mascot when the job didn't bring one, so every video
        // carries the character without it having to be attached by hand each time.
        var mascotName = input.Mascot;
        var usesChannelMascot = string.IsNullOrWhiteSpace(mascotName);
        if (usesChannelMascot) mascotName = TryStageMascot(jobDir, ch.MascotPath);
        // Poses only exist for the channel's own character; a mascot uploaded with the job is one image.
        var mascotPoses = usesChannelMascot
            ? TryStageMascotPoses(jobDir, ch.MascotPath, mascotName)
            : mascotName is null ? new List<string>() : new List<string> { mascotName };

        Stage(job, "titling", 80);
        var title = (input.Title ?? "").Trim();
        if (title.Length == 0)
        {
            var styleTitles = await _titleStyle.GetStyleTitlesAsync(ch.TitleReferenceChannel, ch.TitleTab, ct);
            title = await _codex.GenerateRepostTitleAsync(
                        string.Join(" ", items.Select(i => i.Text)), styleTitles, ct)
                    ?? items[0].Text;
        }
        job.YouTubeTitle = Truncate(PackageListTitle(title) + ch.TitleSuffix, 100);
        var desc = ch.DescriptionTemplate.Replace("{title}", title);
        if (!string.IsNullOrWhiteSpace(musicAttribution)) desc += "\n\n" + musicAttribution.Trim();
        var imageCreditsPath = Path.Combine(jobDir, "image_credits.txt");
        if (File.Exists(imageCreditsPath))
        {
            var imageCredits = (await File.ReadAllTextAsync(imageCreditsPath, ct)).Trim();
            if (imageCredits.Length > 0) desc += "\n\nImage credits:\n" + imageCredits;
        }
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
            // Measured 2026-09-26 as the 10-90% width of edges outside the card: his p25 32-37px
            // on Minecraft footage, ours 24px at blur 14 - so ~1.45x, i.e. 20.
            BackgroundBlur = 20,
            Music = musicName,
            MusicVolume = _pipe.MusicVolume,
            MusicLoopFrames = musicLoopFrames,
            Title = title,
            Format = "list",
            ContentType = ch.ContentType,
            Mascot = mascotName,
            MascotPoses = mascotPoses.Count > 1 ? mascotPoses : null,
            VisualSeed = StableVisualSeed(job.Id),
            Beats = beats,
            Narration = narrationName,
            Captions = captions,
        };

        WriteJsonNoBom(Path.Combine(jobDir, "props.json"), props);

        Stage(job, "rendering video", 85);
        var outMp4 = Path.Combine(jobDir, "short.mp4");
        job.VideoPath = await _renderer.RenderAsync(Path.Combine(jobDir, "props.json"), jobDir, outMp4, ct);

        // CHECK THE FINISHED VIDEO. Every format, not just reposts - a list short reaching the
        // Review tab frozen or cut short is exactly as bad as a repost doing it, and for a long
        // while nothing looked at one at all.
        if (OperatingSystem.IsWindows())
        {
            Stage(job, "checking the video", 95);
            var faults = await _renderCheck.ValidateFileAsync(outMp4, totalMs / 1000.0, ct);
            if (faults.Count > 0)
            {
                job.VideoPath = null;
                throw new InvalidOperationException(
                    "The finished video did not pass its own check, so it was not published:\n"
                    + string.Join("\n", faults.Select(f => $"  - {f.Message}")));
            }
        }

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

    /// <summary>CobbleCat's narration pace: median 201 wpm across his 20 latest Shorts (p10 191, p90 227).</summary>
    private const double ListTargetWpm = 200;

    /// <summary>
    /// A list line as the VOICE should get it. The script shouts a word or two for the caption
    /// ("is ACTUALLY a WEAPON"), and the voice reads a long all-caps word as an initialism -
    /// "THE ALGORITHM" came out of a finished video as "The Algoramp". Caps of 4+ letters are
    /// lowered for the audio only; short ones ("AI", "CEO") stay caps so they are still spelled.
    /// The captions keep the written casing.
    /// </summary>
    private static string SpeakableListLine(string line) =>
        string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w =>
        {
            var letters = w.Count(char.IsLetter);
            return letters >= 4 && w.Where(char.IsLetter).All(char.IsUpper) ? w.ToLowerInvariant() : w;
        }));

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
            if (text.Length == 0)
            {
                // A panel nobody reads out is still a panel - the AI art comic's punchline has no
                // words in it at all. Given no time of its own it was revealed after the video had
                // already ended, so it never appeared. It gets a beat like every other panel.
                if (i < lines.Count) lines[i].StartMs = at;
                at += DiagramGapMs * 2;
                continue;
            }

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

        // Long enough to hold the last panel, which may be a silent one - the audio ends before
        // the reveal does, and the composition has to outlast the audio for it to be seen.
        var total = Math.Max(at - DiagramGapMs, 1);
        if (lines.Count > 0) total = Math.Max(total, lines[^1].StartMs + DiagramGapMs * 2);
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

    /// <summary>
    /// The list (CobbleCat) channel's emoji, in the proportions of his latest 200 titles: 70% carry
    /// one, and it is 😭 far more than anything else (109 of them), then 🤣 (34); 😂 is rare and
    /// 💀 / 🤔 never appear. <see cref="PackageMemeTitle"/> was picking uniformly from a set built
    /// for the other channel, so most of ours ended 😂, 💀 or 🤔.
    /// </summary>
    private static string PackageListTitle(string title)
    {
        var clean = title.Trim();
        while (clean.EndsWith("#Shorts", StringComparison.OrdinalIgnoreCase))
            clean = clean[..^7].TrimEnd();
        // Keep an emoji the script already chose.
        if (clean.Length > 0 && char.IsSurrogate(clean[^1])) return clean;

        uint hash = 2166136261;
        foreach (char c in clean)
        {
            hash ^= char.ToLowerInvariant(c);
            hash *= 16777619;
        }
        var roll = hash % 100;
        var emoji = roll switch
        {
            < 30 => "",
            < 72 => "😭",
            < 84 => "🤣",
            < 90 => "😭😭",
            < 94 => "🤯",
            < 97 => "🤣😭",
            _ => "🥹",
        };
        return emoji.Length == 0 ? clean : $"{clean} {emoji}";
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

    /// <summary>
    /// Extra poses of the channel mascot: any "&lt;mascot name&gt;_*.png" beside the main sprite
    /// (cobblecat_point.png, cobblecat_shock.png, ...). The reference character switches pose on
    /// some picture cuts; one static sprite could not. Returns the staged names, main pose first.
    /// </summary>
    private List<string> TryStageMascotPoses(string jobDir, string? mascotPath, string? mainName)
    {
        var poses = new List<string>();
        if (mainName is null) return poses;
        poses.Add(mainName);
        if (string.IsNullOrWhiteSpace(mascotPath)) return poses;
        var src = Path.IsPathRooted(mascotPath) ? mascotPath : Path.Combine(_env.ContentRootPath, mascotPath);
        var dir = Path.GetDirectoryName(src);
        if (dir is null || !Directory.Exists(dir)) return poses;
        foreach (var file in Directory.GetFiles(dir, Path.GetFileNameWithoutExtension(src) + "_*.png").OrderBy(f => f))
        {
            var suffix = Path.GetFileNameWithoutExtension(file)[(Path.GetFileNameWithoutExtension(src).Length + 1)..];
            var name = $"mascot_{suffix}.png";
            try
            {
                File.Copy(file, Path.Combine(jobDir, name), overwrite: true);
                poses.Add(name);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not stage mascot pose {Path}.", file);
            }
        }
        return poses;
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

    private static bool IsVideoMedia(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase);
    }

    private static int StableVisualSeed(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var ch in value)
            {
                hash ^= ch;
                hash *= 16777619;
            }
            return (int)(hash & 0x7fffffff);
        }
    }

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

    /// <summary>Read the first video stream's display aspect from ffmpeg diagnostics.</summary>
    private async Task<double?> ProbeVideoAspectAsync(string videoPath, CancellationToken ct)
    {
        try
        {
            var res = await ProcessRunner.RunAsync(
                _pipe.FfmpegCommand,
                new[] { "-hide_banner", "-i", videoPath },
                workingDir: null, TimeSpan.FromSeconds(30), ct);

            var match = Regex.Match(
                res.StdErr,
                @"Video:.*?(?<width>\d{2,5})x(?<height>\d{2,5})",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (match.Success
                && int.TryParse(match.Groups["width"].Value, out var width)
                && int.TryParse(match.Groups["height"].Value, out var height)
                && width > 0 && height > 0)
            {
                return (double)width / height;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ffmpeg aspect probe failed for {Path}.", videoPath);
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
