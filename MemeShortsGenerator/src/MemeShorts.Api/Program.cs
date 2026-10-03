using System.Text;
using System.Text.Json;
using MemeShorts.Api.Models;
using MemeShorts.Api.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Load .env (ASP.NET doesn't read it natively). Added last so it overrides appsettings.json
// but only for keys that are actually present + non-empty.
builder.Configuration.AddInMemoryCollection(DotEnv.Read());

// ---- Options ----
builder.Services.Configure<PipelineOptions>(builder.Configuration.GetSection("Pipeline"));
builder.Services.Configure<TtsOptions>(builder.Configuration.GetSection("Tts"));
builder.Services.Configure<CodexOptions>(builder.Configuration.GetSection("Codex"));
builder.Services.Configure<RemotionOptions>(builder.Configuration.GetSection("Remotion"));
builder.Services.Configure<CaptionsOptions>(builder.Configuration.GetSection("Captions"));
builder.Services.Configure<UploadOptions>(builder.Configuration.GetSection("Upload"));
builder.Services.Configure<FacebookOptions>(builder.Configuration.GetSection("Facebook"));
builder.Services.Configure<InstagramOptions>(builder.Configuration.GetSection("Instagram"));
builder.Services.Configure<TitleStyleOptions>(builder.Configuration.GetSection("Titles"));
builder.Services.Configure<PerformanceOptions>(builder.Configuration.GetSection("Performance"));
builder.Services.Configure<ChannelsOptions>(builder.Configuration.GetSection("Channels"));
builder.Services.Configure<ProfanityOptions>(builder.Configuration.GetSection("Profanity"));
builder.Services.Configure<DriveOptions>(builder.Configuration.GetSection("Drive"));

// ---- Services ----
builder.Services.AddHttpClient(); // for ElevenLabs
builder.Services.AddHttpClient("wikimedia", WikimediaImageService.Configure);
// Commons first; when it has nothing, Codex draws one rather than leaving the line pictureless.
builder.Services.AddSingleton<WikimediaImageService>();
builder.Services.Configure<PicturesOptions>(builder.Configuration.GetSection("Pictures"));
builder.Services.AddHttpClient("openverse", StockPhotoSetup.Configure);
builder.Services.AddHttpClient("pexels", StockPhotoSetup.Configure);
builder.Services.AddSingleton<OpenverseImageService>();
builder.Services.AddSingleton<PexelsImageService>();
builder.Services.AddSingleton<IStockImageService, FallbackImageService>();

// TTS provider is selected at startup. ElevenLabs is used only when a key + voice are
// actually present (so a stray Provider="ElevenLabs" with no key can't break generation);
// set Provider="Windows" to force the free offline voice even when a key is present.
var elevenKey = builder.Configuration["Tts:ElevenLabsApiKey"];
var elevenVoice = builder.Configuration["Tts:ElevenLabsVoiceId"];
var hasEleven = !string.IsNullOrWhiteSpace(elevenKey) && !string.IsNullOrWhiteSpace(elevenVoice);
var ttsProvider = builder.Configuration["Tts:Provider"] ?? "Windows";
var useEleven = hasEleven && !ttsProvider.Equals("Windows", StringComparison.OrdinalIgnoreCase);
if (useEleven)
    builder.Services.AddScoped<ITtsService, ElevenLabsTtsService>();
else
    builder.Services.AddScoped<ITtsService, WindowsTtsService>();

builder.Services.AddSingleton<ICodexClient, CodexCliClient>();
builder.Services.AddSingleton<IWhisperCaptionService, WhisperCaptionService>();
builder.Services.AddSingleton<BackgroundHistory>();
builder.Services.AddSingleton<TopicHistory>();
builder.Services.AddSingleton<SuggestionHistory>();
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<IPanelRenderer, LocalPanelRenderer>();
builder.Services.AddSingleton<IVideoRenderer, RemotionRenderer>();
builder.Services.AddSingleton<UploadScheduler>();
builder.Services.AddSingleton<IYouTubeUploader, YouTubeUploadService>();

// Meta (Facebook Page Reels + Instagram Reels). Both talk to the Graph API through one client.
// Uploads stream a whole mp4, so the client gets no overall timeout — each uploader applies its
// own via a linked CancellationTokenSource instead.
builder.Services.AddHttpClient(MetaGraphClient.HttpClientName,
    c => c.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton<MetaGraphClient>();
builder.Services.AddSingleton<MetaCredentialStore>();
builder.Services.AddSingleton<VoiceSettingsStore>();
builder.Services.AddSingleton<ISocialUploader, FacebookReelsUploader>();
builder.Services.AddSingleton<ISocialUploader, InstagramReelsUploader>();
builder.Services.AddHostedService<SocialPublishWorker>();
builder.Services.AddSingleton<ITitleStyleService, TitleStyleService>();
builder.Services.AddSingleton<IChannelPerformanceService, ChannelPerformanceService>();
builder.Services.AddSingleton<IThumbnailService, ThumbnailService>();
// Cuts a pasted screenshot into the bands the repost video reveals one at a time.
builder.Services.AddSingleton<PostSliceService>();
builder.Services.AddSingleton<DiagramSliceService>();
// OCR reads the words off a pasted meme so they don't have to be retyped. Guarded on the OS
// version the WinRT API needs; anywhere else the text box just stays empty.
builder.Services.AddSingleton<IOcrService>(sp =>
    OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
        ? new WindowsOcrService(sp.GetRequiredService<ILogger<WindowsOcrService>>())
        : new NullOcrService());
// Blurs the swear words out of a pasted screenshot before it is filmed.
builder.Services.AddSingleton<ProfanityFilter>();
builder.Services.AddSingleton<ImageCensorService>();
// Turns a photo shot on a plain background into a cutout for the list format's cards.
builder.Services.AddSingleton<CutoutService>();
// Checks the FINISHED mp4, not the plan. Everything else here validates the slicing
// decision, which is how a video with a half-empty card reached Review marked "Info".
builder.Services.AddSingleton<RenderValidator>();
builder.Services.AddSingleton<ChannelRegistry>();
builder.Services.AddScoped<PipelineService>();
builder.Services.AddSingleton<JobManager>();
builder.Services.AddSingleton<ListAutoMaker>();
builder.Services.AddSingleton<ListBatchRunner>();
builder.Services.AddSingleton<DriveArchiver>();
// One instance serves both as the background mover and as "Move all" behind the Settings tab.
builder.Services.AddSingleton<DriveAutoMover>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DriveAutoMover>());

builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://localhost:5180");

var app = builder.Build();

// Eagerly construct the background history so its DB loads (and logs) at startup,
// not lazily on the first render.
app.Services.GetRequiredService<BackgroundHistory>();

// Keep heavy child-process temp (Remotion bundles, Chrome, ffmpeg) OFF the C: drive:
// point TEMP/TMP at a folder under the (D:) content root. Child processes inherit these.
var renderTmp = Path.Combine(app.Environment.ContentRootPath, "render-tmp");
Directory.CreateDirectory(renderTmp);
Environment.SetEnvironmentVariable("TMP", renderTmp);
Environment.SetEnvironmentVariable("TEMP", renderTmp);
app.Logger.LogInformation("Child-process TEMP -> {Tmp}", renderTmp);

// Sweep what crashed renders left behind. Remotion cleans up its bundle and frame folders when a
// render ENDS; when one dies - killed, out of memory, out of disk - they simply stay. They had
// been accumulating since August: 376 folders holding 7.4GB, which filled the D: drive outright
// and then failed every later render at the first file copy ("ENOSPC ... bg.mp4"), a failure that
// reads like a bug in the video and is nothing of the kind.
//
// Best-effort and time-based: anything still being written by a live render is far newer than the
// cutoff, and a folder that will not delete is skipped rather than killing startup.
try
{
    var cutoff = DateTime.UtcNow.AddHours(-6);
    var swept = 0L;
    foreach (var dir in Directory.EnumerateDirectories(renderTmp))
    {
        try
        {
            if (Directory.GetLastWriteTimeUtc(dir) >= cutoff) continue;
            swept += new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)
                                           .Sum(f => f.Length);
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    if (swept > 0)
        app.Logger.LogInformation("Swept {MB:N0} MB of stale render temp from {Tmp}.",
            swept / 1024d / 1024, renderTmp);
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Could not sweep {Tmp}.", renderTmp);
}

// Surface the active providers at startup so config changes (e.g. switching to
// ElevenLabs) are visibly confirmed in the log.
{
    var captions = app.Configuration["Captions:Source"] ?? "Whisper";
    var panels = app.Configuration["Codex:PanelSource"] ?? "Codex";
    var ttsActive = useEleven ? "ElevenLabs (key+voice set)"
        : hasEleven ? "Windows (key set but Provider=Windows)"
        : "Windows";
    app.Logger.LogInformation("Providers — TTS: {Tts}, Captions: {Captions}, Panels: {Panels}",
        ttsActive, captions, panels);
}

app.UseDefaultFiles();
app.UseStaticFiles();

// ---- API ----
app.MapGet("/api/health", (IConfiguration cfg, IYouTubeUploader uploader,
    IEnumerable<ISocialUploader> social, MetaCredentialStore creds) => Results.Ok(new
{
    tts = useEleven ? "ElevenLabs" : "Windows",
    ttsModel = useEleven ? cfg["Tts:ElevenLabsModelId"] : null,
    memeDelivery = useEleven ? "shouts (every beat)" : null,
    captions = cfg["Captions:Source"] ?? "Whisper",
    panels = cfg["Codex:PanelSource"] ?? "Codex",
    codexModel = cfg["Codex:Model"] ?? "(Codex default)",
    elevenLabsConfigured = !string.IsNullOrWhiteSpace(cfg["Tts:ElevenLabsApiKey"])
                        && !string.IsNullOrWhiteSpace(cfg["Tts:ElevenLabsVoiceId"]),
    uploadEnabled = string.Equals(cfg["Upload:Enabled"], "true", StringComparison.OrdinalIgnoreCase),
    uploadConfigured = uploader.Configured,
    // When true, finished videos wait for approve/reject before uploading; when auto-upload
    // is on they're pre-approved and upload straight away.
    autoUpload = string.Equals(cfg["Upload:AutoUpload"], "true", StringComparison.OrdinalIgnoreCase),
    facebookConfigured = social.Any(s => s.Platform == "facebook" && s.Configured),
    instagramConfigured = social.Any(s => s.Platform == "instagram" && s.Configured),
    // Credentials saved but the platform switched off — approvals silently skip it, so the UI
    // needs to be able to call that out rather than just showing "not configured".
    facebookOffWithCreds = !creds.Facebook.Enabled
                        && !string.IsNullOrWhiteSpace(creds.Facebook.AccountId)
                        && !string.IsNullOrWhiteSpace(creds.Facebook.Token),
    instagramOffWithCreds = !creds.Instagram.Enabled
                         && !string.IsNullOrWhiteSpace(creds.Instagram.AccountId)
                         && !string.IsNullOrWhiteSpace(creds.Instagram.Token),
}));

// ElevenLabs voice, editable at runtime so voices can be auditioned without a restart.
app.MapGet("/api/settings/voice", (VoiceSettingsStore voice) => Results.Ok(new
{
    voiceId = voice.VoiceId,
    speechRate = voice.SpeechRate,
    savedInApp = voice.VoiceOverridden,
}));

app.MapPost("/api/settings/voice", (VoiceRequest req, VoiceSettingsStore voice) =>
{
    voice.Save(req.VoiceId, req.SpeechRate);
    return Results.Ok(new { voiceId = voice.VoiceId, speechRate = voice.SpeechRate });
});

app.MapPost("/api/settings/voice/reset", (VoiceSettingsStore voice) =>
{
    voice.Reset();
    return Results.Ok(new { voiceId = voice.VoiceId, speechRate = voice.SpeechRate });
});

// Re-voice a finished job with the CURRENT voice and re-render it. Reuses the stored storyboard
// and panel art, so auditioning a voice costs TTS + render only, never image generation.
app.MapPost("/api/jobs/{id}/revoice", (string id, JobManager jobs) =>
{
    var job = jobs.Revoice(id);
    return job is null ? Results.NotFound() : Results.Ok(JobStatusDto.From(job));
});

// Overrule the finished-video check for one job and send it to review. Goes no further than the
// review queue — approving it is still a separate, manual step.
app.MapPost("/api/jobs/{id}/accept-check", (string id, JobManager jobs) =>
{
    var job = jobs.AcceptFailedCheck(id);
    return job is null ? Results.NotFound() : Results.Ok(JobStatusDto.From(job));
});

// Facebook/Instagram credentials, editable at runtime from the Settings page.
// The token is NEVER returned in full — only a masked tail, enough to confirm which one is
// loaded without handing a working credential back to the browser.
app.MapGet("/api/settings/meta", (MetaCredentialStore creds) =>
{
    var fb = creds.Facebook;
    var ig = creds.Instagram;
    return Results.Ok(new
    {
        facebook = new
        {
            enabled = fb.Enabled,
            pageId = fb.AccountId,
            tokenSet = !string.IsNullOrWhiteSpace(fb.Token),
            tokenMasked = MetaCredentialStore.Mask(fb.Token),
            usable = fb.Usable,
            savedInApp = creds.FacebookOverridden,
        },
        instagram = new
        {
            enabled = ig.Enabled,
            igUserId = ig.AccountId,
            tokenSet = !string.IsNullOrWhiteSpace(ig.Token),
            tokenMasked = MetaCredentialStore.Mask(ig.Token),
            usable = ig.Usable,
            savedInApp = creds.InstagramOverridden,
        },
    });
});

// Save credentials. An omitted/blank token means "keep the stored one", so the masked value
// the UI displays can be submitted back untouched without wiping the real token.
app.MapPost("/api/settings/meta", (MetaSettingsRequest req, MetaCredentialStore creds) =>
{
    if (req.Facebook is { } f) creds.SaveFacebook(f.Enabled, f.PageId, f.Token);
    if (req.Instagram is { } i) creds.SaveInstagram(i.Enabled, i.IgUserId, i.Token);
    return Results.Ok(new { ok = true });
});

// Drop app-saved credentials so the .env / appsettings values apply again.
app.MapPost("/api/settings/meta/reset", (string? platform, MetaCredentialStore creds) =>
{
    if (platform is null or "facebook") creds.ClearFacebook();
    if (platform is null or "instagram") creds.ClearInstagram();
    return Results.Ok(new { ok = true });
});

// Verify the Facebook/Instagram credentials without posting anything — resolves each
// configured token to the account it controls, so setup mistakes surface immediately.
app.MapPost("/api/meta/verify", async (IEnumerable<ISocialUploader> social, CancellationToken ct) =>
{
    var configured = social.Where(s => s.Configured).ToList();
    if (configured.Count == 0)
        return Results.BadRequest(new { ok = false, message = "Neither Facebook nor Instagram is configured." });

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(TimeSpan.FromSeconds(60));

    var results = new List<MetaVerifyResult>();
    foreach (var s in configured)
    {
        try { results.Add(new MetaVerifyResult(s.Platform, true, await s.VerifyAsync(cts.Token))); }
        catch (Exception ex) { results.Add(new MetaVerifyResult(s.Platform, false, ex.Message)); }
    }

    return Results.Ok(new { ok = results.All(r => r.Ok), results });
});

// Manually (re)upload a finished job to YouTube.
app.MapPost("/api/jobs/{id}/upload", (string id, JobManager jobs) =>
{
    var job = jobs.RequestUpload(id);
    return job is null ? Results.NotFound() : Results.Ok(JobStatusDto.From(job));
});

// What the channel's real view counts say — the winners/flops now steering topic generation.
app.MapGet("/api/performance", async (bool? refresh, IChannelPerformanceService performance, CancellationToken ct) =>
{
    if (!performance.Configured)
        return Results.Ok(new { configured = false, message = "Set Performance:ChannelUrl to your channel to enable this." });

    var snap = await performance.GetAsync(refresh ?? false, ct);
    return Results.Ok(new
    {
        configured = true,
        steeringTopics = snap.HasSignal,
        medianViews = snap.MedianViews,
        videoCount = snap.All.Count,
        fetchedUtc = snap.FetchedUtc == DateTimeOffset.MinValue ? null : snap.FetchedUtc.ToString("o"),
        winners = snap.Winners.Select(Describe),
        flops = snap.Flops.Select(Describe),
    });

    static object Describe(VideoPerformance v) => new
    {
        videoId = v.VideoId,
        title = v.Title,
        topic = v.Topic,
        views = v.Views,
        multiple = Math.Round(v.Multiple, 2),
        publishedUtc = v.PublishedUtc?.ToString("o"),
    };
});

// Upcoming scheduled publish times.
app.MapGet("/api/schedule", (UploadScheduler scheduler) =>
    Results.Ok(scheduler.Upcoming().Select(d => d.ToString("o"))));

// Verify-only: run the OAuth consent (no upload). Confirms test-user access + caches token.
app.MapPost("/api/youtube/authorize", async (IYouTubeUploader uploader) =>
{
    if (!uploader.Configured)
        return Results.BadRequest(new { ok = false, message = "Upload not configured (Upload:Enabled + client secrets)." });
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
    try
    {
        await uploader.AuthorizeAsync(cts.Token);
        return Results.Ok(new { ok = true, message = "Authorized — consent succeeded, you're a test user, token cached." });
    }
    catch (OperationCanceledException)
    {
        return Results.Ok(new { ok = false, message = "Consent didn't complete. If the browser said you're not a test user, add your Gmail under OAuth consent screen → Audience → Test users, then retry." });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { ok = false, message = ex.Message });
    }
});

app.MapPost("/api/generate", (GenerateRequest req, JobManager jobs) =>
{
    if (string.IsNullOrWhiteSpace(req.Topic))
        return Results.BadRequest(new { error = "topic is required" });

    var topic = req.Topic.Trim();
    // Never make the same short twice (durable check).
    if (jobs.TopicExists(topic))
        return Results.Conflict(new { error = $"Already generated a video about “{topic}”. Try a different topic." });

    var job = jobs.Create(topic, req.Format, req.Channel);
    return Results.Ok(new { id = job.Id });
});

// Read the words off a pasted image so they don't have to be retyped. Deliberately separate
// from /api/generate/repost: this runs the moment the picture is pasted, and the result is
// dropped into an editable box so OCR mistakes get fixed BEFORE anything renders.
// Dry-run the slicer on a pasted picture and report what it decided, WITHOUT rendering.
//
// The slicer works out a layout per screenshot rather than matching known formats, so a shape
// it has not met before yields a wrong plan and there is no case to add in advance. This runs
// the same code the pipeline runs and checks the plan is self-consistent, so a bad one is
// caught in a second or two instead of after a three-minute render.
app.MapPost("/api/repost/preflight", async (
    HttpRequest http, IOcrService ocr, PostSliceService slicer, DiagramSliceService diagrams,
    ProfanityFilter profanity, CancellationToken ct) =>
{
    if (!http.HasFormContentType)
        return Results.BadRequest(new { error = "expected a multipart form with an image" });
    var form = await http.ReadFormAsync(ct);
    var image = form.Files["image"];
    if (image is null || image.Length == 0)
        return Results.BadRequest(new { error = "no image supplied" });
    if (!OperatingSystem.IsWindows() || !ocr.Available)
        return Results.BadRequest(new { error = "OCR is not available on this machine" });

    var typed = (form["text"].ToString() ?? "")
        .Replace("\r\n", "\n").Split('\n')
        .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

    var tmp = Path.Combine(Path.GetTempPath(), $"pre_{Guid.NewGuid():n}{Path.GetExtension(image.FileName)}");
    try
    {
        await using (var dest = File.Create(tmp)) await image.CopyToAsync(dest, ct);

        var ocrLines = await ocr.ReadLinesAsync(tmp, ct);
        var bands = slicer.Analyze(tmp, ocrLines, diagrams, typed);
        var spoken = bands.NarrationOrder()
            .Select(x => RepostComposer.FixOcrConfusions(
                RepostComposer.StripLeadingArtifact(x.Segment.Text.Trim())))
            .ToList();

        var cropTops = PostSliceService.CropTops(bands.Blocks, bands.Width, bands.HeaderBottom);
        var ink = PostSliceService.InkGrid(tmp, bands.Background, 4);
        var issues = PostPlanValidator.Validate(bands, spoken, ink, 4);
        return Results.Ok(new
        {
            width = bands.Width,
            height = bands.Height,
            headerBottom = bands.HeaderBottom,
            isDiagram = bands.IsDiagram,
            gutter = bands.Gutter,
            // Why the columns did or did not survive. Both halves of a comparison arriving at
            // once is the most reported defect in this format, and the reason was previously
            // only in a log line nobody sees.
            columnNote = bands.ColumnNote,
            severity = PostPlanValidator.Worst(issues).ToString(),
            issues = issues.Select(i => new { severity = i.Severity.ToString(), i.Code, i.Message }),
            spoken,
            // What would be blurred out of the picture, and where. Reported here rather than
            // only in the render log because a FALSE positive - an innocent word covered up -
            // is invisible in the finished video unless you happen to be looking at that frame.
            profanity = profanity.Scan(ocrLines)
                .Select(h => new { h.Word, h.Left, h.Top, h.Right, h.Bottom }),
            // The raw OCR geometry, so a bad slice plan can be traced back to what the reader
            // actually saw rather than guessed at from the finished blocks.
            ocr = ocrLines.Select(l => new { l.Text, l.Left, l.Top, l.Right, l.Bottom }),
            // cropTop is where the slice actually STARTS DRAWING - a caption reaches up to keep
            // the line spacing above it. Reported because the reveal is built from it, not from
            // Top, and a check that reads Top alone cannot see the whole class of defect where
            // two slices end up drawing the same rows.
            slices = bands.Blocks.Select(b => new
            {
                b.Top, b.Bottom, b.IsMedia, b.Text,
                cropTop = cropTops.TryGetValue(b, out var ct) ? ct : b.Top,
                side = b.Side.ToString(), b.Group, b.XLeft, b.XRight,
            }),
        });
    }
    finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
});

// Blur the swear words out of one picture and hand the censored PNG straight back.
//
// The pipeline does this on its own before every repost render; this is the way to SEE the
// result without spending a render on it - what got covered, how hard, and whether an innocent
// word was caught. Same code, same OCR pass, so what comes back is what the video will show.
app.MapPost("/api/repost/censor", async (
    HttpRequest http, IOcrService ocr, ImageCensorService censor, CancellationToken ct) =>
{
    if (!http.HasFormContentType)
        return Results.BadRequest(new { error = "expected a multipart form with an image" });
    var form = await http.ReadFormAsync(ct);
    var image = form.Files["image"];
    if (image is null || image.Length == 0)
        return Results.BadRequest(new { error = "no image supplied" });
    if (!OperatingSystem.IsWindows() || !ocr.Available)
        return Results.BadRequest(new { error = "OCR is not available on this machine" });

    var dir = Directory.CreateTempSubdirectory("censor_").FullName;
    var tmp = Path.Combine(dir, "post" + Path.GetExtension(image.FileName));
    try
    {
        await using (var dest = File.Create(tmp)) await image.CopyToAsync(dest, ct);

        var lines = await ocr.ReadLinesAsync(tmp, ct);
        var censored = censor.Censor(tmp, lines);
        // Nothing to cover: hand back exactly what was sent, so the caller can save the result
        // unconditionally instead of branching on whether the post happened to swear.
        var bytes = await File.ReadAllBytesAsync(censored is null ? tmp : Path.Combine(dir, censored), ct);
        return Results.File(bytes, censored is null ? image.ContentType ?? "image/png" : "image/png");
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
});

// Run the post-render checks against a job's existing mp4.
//
// Here so a suspect video can be examined without re-rendering it, and so the validator itself
// can be tested against videos already known to be bad - a validator that passes the videos
// Matthew rejected would be worse than none.
app.MapPost("/api/jobs/{id}/validate-render", async (
    string id, bool? columns, JobManager jobs, RenderValidator validator, CancellationToken ct) =>
{
    var job = jobs.Get(id);
    if (job is null) return Results.NotFound();
    var mp4 = Path.Combine(job.OutputDir, "short.mp4");
    if (!File.Exists(mp4)) return Results.BadRequest(new { error = "this job has no rendered video" });
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "windows only" });

    var seconds = await validator.ProbeSecondsAsync(mp4, ct);
    var issues = await validator.ValidateAsync(mp4, seconds, columns ?? true, ct);
    return Results.Ok(new
    {
        id,
        seconds,
        ok = issues.Count == 0,
        issues = issues.Select(i => new { fault = i.Fault.ToString(), i.Message }),
    });
});

app.MapPost("/api/ocr", async (HttpRequest http, IOcrService ocr, CancellationToken ct) =>
{
    if (!ocr.Available)
        return Results.Ok(new { text = "", available = false });
    if (!http.HasFormContentType)
        return Results.BadRequest(new { error = "expected a multipart form with an image" });

    var form = await http.ReadFormAsync(ct);
    var image = form.Files["image"];
    if (image is null || image.Length == 0)
        return Results.BadRequest(new { error = "no image supplied" });
    if (image.Length > 25 * 1024 * 1024)
        return Results.BadRequest(new { error = "image is larger than 25 MB" });

    // OCR needs a real file on disk (StorageFile), and this one is throwaway — it is only the
    // source for the text, while the copy that gets rendered is saved by the generate endpoint.
    var tmp = Path.Combine(Path.GetTempPath(), $"ocr_{Guid.NewGuid():n}{Path.GetExtension(image.FileName)}");
    try
    {
        await using (var dest = File.Create(tmp))
            await image.CopyToAsync(dest, ct);
        var text = await ocr.ReadAsync(tmp, ct);
        return Results.Ok(new { text, available = true });
    }
    finally
    {
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* temp file cleanup is best effort */ }
    }
});

// Repost: a post you found yourself. Multipart because it carries the image; the file and a
// repost.json land in the job dir before the pipeline can start (JobManager.Create's prepare
// hook), so a restart mid-job still has everything it needs to resume.
app.MapPost("/api/generate/repost", async (HttpRequest http, JobManager jobs, ChannelRegistry channels) =>
{
    if (!http.HasFormContentType)
        return Results.BadRequest(new { error = "expected a multipart form (image + text)" });

    var form = await http.ReadFormAsync();
    var text = form["text"].ToString().Trim();
    var image = form.Files["image"];

    // Either is enough: with a picture the words are read off it, and typed text is the
    // correction for when OCR gets them wrong.
    if (string.IsNullOrWhiteSpace(text) && image is null)
        return Results.BadRequest(new { error = "paste a picture, or type the post text" });
    if (image is not null && image.Length > 25 * 1024 * 1024)
        return Results.BadRequest(new { error = "image is larger than 25 MB" });

    // A clipboard paste arrives with a generic or missing filename ("image.png", or nothing at
    // all), so the content type is the reliable signal and the extension is only a hint.
    var ext = Path.GetExtension(image?.FileName ?? "").ToLowerInvariant();
    if (image is not null && string.IsNullOrEmpty(ext))
        ext = (image.ContentType ?? "").ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => "",
        };
    if (image is not null && ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".gif"))
        return Results.BadRequest(new { error = $"unsupported image type '{image.ContentType}'" });

    var channelId = form["channel"].ToString();
    var ch = channels.Resolve(string.IsNullOrWhiteSpace(channelId) ? null : channelId);
    if (!ch.IsRepost)
        return Results.BadRequest(new { error = $"channel '{ch.Id}' is not a repost channel" });

    var title = form["title"].ToString().Trim();
    // Topic is the job's identity in the UI and the duplicate guard. A pasted post usually has
    // neither a title nor typed text — the words live in the picture — so it needs a unique
    // stand-in. A constant fallback made every repost after the first collide with itself and
    // get rejected as a duplicate.
    // Identify a post by its OPENING WORDS, not its first line. OCR splits a caption wherever it
    // wraps, so the first line is often a fragment — one meme's was simply "people", and every
    // later meme starting with that word was then rejected as a duplicate of it.
    var opening = string.Join(" ", text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0))
        .Trim();
    if (opening.Length > 80) opening = opening[..80];

    var realTopic = !string.IsNullOrWhiteSpace(title) ? title
                  : !string.IsNullOrWhiteSpace(opening) ? opening
                  : null;
    // Milliseconds, not seconds. Queueing a pile of screenshots posts them back to back, and at
    // second resolution several land on the same stamp.
    var topic = realTopic ?? $"repost {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}";
    if (topic.Length > 120) topic = topic[..120];
    // Only meaningful when the topic came from REAL words — two pastes of the same post are a
    // genuine duplicate, two pastes of different posts are not. Applying it to the generated
    // stand-in as well silently dropped screenshots out of a batch: they carry no typed text,
    // so their topics differ only by a timestamp that collides under rapid submission.
    if (realTopic is not null && jobs.TopicExists(topic))
        return Results.Conflict(new { error = $"Already made a video from “{topic}”." });

    var job = jobs.Create(topic, "repost", ch.Id, autoGenerated: false, prepare: j =>
    {
        string? imageName = null;
        if (image is not null)
        {
            imageName = "post_image" + ext;
            using var dest = File.Create(Path.Combine(j.OutputDir, imageName));
            image.CopyTo(dest);
        }
        var input = new RepostInput
        {
            Text = text,
            Image = imageName,
            Title = NullIfBlank(title),
            AuthorName = NullIfBlank(form["authorName"].ToString()),
            Headline = NullIfBlank(form["headline"].ToString()),
            Meta = NullIfBlank(form["meta"].ToString()),
        };
        File.WriteAllText(Path.Combine(j.OutputDir, "repost.json"),
            JsonSerializer.Serialize(input, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    });

    return Results.Ok(new { id = job.Id });

    static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
});

// List short: a run of photos or clips with one spoken line each. The multipart field keeps its
// old "images" name for API compatibility; item i is paired with line i of the script.
app.MapPost("/api/generate/list", async (HttpRequest http, JobManager jobs, ChannelRegistry channels) =>
{
    if (!http.HasFormContentType)
        return Results.BadRequest(new { error = "expected a multipart form (media + lines)" });

    var form = await http.ReadFormAsync();
    var images = form.Files.Where(f => f.Name == "images").ToList();
    var lines = form["lines"].ToString()
        .Replace("\r\n", "\n").Split('\n')
        .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    if (images.Count == 0) return Results.BadRequest(new { error = "add at least one photo or clip" });
    if (lines.Count == 0) return Results.BadRequest(new { error = "write one line per media item" });
    if (lines.Count != images.Count)
        return Results.BadRequest(new
        {
            error = $"{images.Count} media item(s) but {lines.Count} line(s) — the format pairs them one to one",
        });
    if (images.Any(i => i.Length > 25 * 1024 * 1024))
        return Results.BadRequest(new { error = "one of the media files is larger than 25 MB" });

    var mediaExtensions = images.Select(MediaExtFor).ToList();
    if (mediaExtensions.Any(ext => ext is null))
        return Results.BadRequest(new
        {
            error = "use PNG, JPEG, WebP, GIF, MP4, WebM, MOV, or M4V media",
        });

    var channelId = form["channel"].ToString();
    var ch = channels.Resolve(string.IsNullOrWhiteSpace(channelId) ? null : channelId);
    if (!ch.IsList)
        return Results.BadRequest(new { error = $"channel '{ch.Id}' is not a list channel" });

    var mascot = form.Files["mascot"];
    var mascotExtension = mascot is null ? null : ImageExtFor(mascot);
    if (mascot is not null && mascotExtension is null)
        return Results.BadRequest(new { error = "the mascot must be a PNG, JPEG, WebP, or GIF image" });
    var title = form["title"].ToString().Trim();
    var topic = title.Length > 0 ? title : string.Join(" ", lines).Trim();
    if (topic.Length > 120) topic = topic[..120];

    var job = jobs.Create(topic, "list", ch.Id, autoGenerated: false, prepare: j =>
    {
        var input = new ListInput { Title = NullIfBlankList(title) };
        for (int i = 0; i < images.Count; i++)
        {
            var ext = mediaExtensions[i]!;
            var name = $"item_{i}{ext}";
            using (var dest = File.Create(Path.Combine(j.OutputDir, name))) images[i].CopyTo(dest);
            input.Items.Add(new ListItem { Image = name, Text = lines[i] });
        }
        if (mascot is not null)
        {
            var name = "mascot" + mascotExtension!;
            using var dest = File.Create(Path.Combine(j.OutputDir, name));
            mascot.CopyTo(dest);
            input.Mascot = name;
        }
        File.WriteAllText(Path.Combine(j.OutputDir, "list.json"),
            JsonSerializer.Serialize(input, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    });

    return Results.Ok(new { id = job.Id });

    static string? NullIfBlankList(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // A clipboard paste can have a generic filename, so prefer a recognised MIME type and use
    // the extension as a fallback for browsers that submit application/octet-stream.
    static string? MediaExtFor(IFormFile f)
    {
        var contentType = (f.ContentType ?? "").ToLowerInvariant();
        var fromType = contentType switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "video/quicktime" => ".mov",
            "video/x-m4v" => ".m4v",
            _ => null,
        };
        if (fromType is not null) return fromType;

        var ext = Path.GetExtension(f.FileName ?? "").ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif"
            or ".mp4" or ".webm" or ".mov" or ".m4v"
            ? ext
            : null;
    }

    static string? ImageExtFor(IFormFile f) => MediaExtFor(f) is { } ext
        && ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif"
            ? ext
            : null;
});

// ---- List short drafts -------------------------------------------------------------------
//
// Codex picks the subject and Commons supplies the pictures, but the search only finds a good
// match about three times in five — abstract lines ("the runes spell H and B") have no obvious
// photograph. So a draft is staged FIRST and shown for a few seconds of checking, where a bad
// picture can be re-searched, rather than discovered after a three-minute render.
static string DraftDir(PipelineOptions pipe, IWebHostEnvironment env, string id)
{
    var root = Path.IsPathRooted(pipe.OutputRoot) ? pipe.OutputRoot
        : Path.Combine(env.ContentRootPath, pipe.OutputRoot);
    return Path.Combine(root, "_drafts", id);
}

app.MapPost("/api/list/draft", async (
    ListAutoRequest req, ChannelRegistry channels, ICodexClient codex, ITitleStyleService titleStyle,
    IStockImageService images, JobManager jobs, SuggestionHistory suggested,
    IOptions<PipelineOptions> pipe, IWebHostEnvironment env,
    CancellationToken ct) =>
{
    var ch = channels.Resolve(string.IsNullOrWhiteSpace(req.Channel) ? null : req.Channel);
    if (!ch.IsList) return Results.BadRequest(new { error = $"channel '{ch.Id}' is not a list channel" });

    var styleTitles = await titleStyle.GetStyleTitlesAsync(ch.TitleReferenceChannel, ch.TitleTab, ct);
    // Everything already made PLUS everything already offered and thrown away this session.
    // Without the second half, discarding a suggestion changed nothing and the next press of
    // "suggest" handed back the very same idea.
    var avoid = jobs.RecentTopics(30).Concat(suggested.Recent(20)).Distinct().ToList();
    var idea = await codex.GenerateListIdeaAsync(styleTitles, req.Topic, avoid, ct,
        jobs.ListHistory(ch.Id));
    if (idea is null)
        return Results.Problem("Codex could not come up with one. Try again, or type a subject.");
    // Title plus subject: a title can hide what it is about ("the clingiest AI keychain").
    suggested.Add(string.IsNullOrWhiteSpace(idea.Subject) ? idea.Title : $"{idea.Title} (about: {idea.Subject})");

    var id = Guid.NewGuid().ToString("n")[..8];
    var dir = DraftDir(pipe.Value, env, id);
    Directory.CreateDirectory(dir);

    var staged = new List<object>();
    var used = new List<string>();      // Commons titles already spoken for, so no picture repeats
    FoundImage? previous = null;
    for (int i = 0; i < idea.Items.Count; i++)
    {
        var item = idea.Items[i];
        // Same term as the line before = hold the same picture (see /api/generate/list/auto).
        var holds = i > 0 && string.Equals(item.Image.Trim(), idea.Items[i - 1].Image.Trim(),
            StringComparison.OrdinalIgnoreCase);
        var kind = PictureKind.Normalize(item.Kind);
        // The draft opens straight away so the script can be read while pictures arrive. Drawn
        // pictures (objects, scenes) come back as placeholders for the page to fill, three at a
        // time; a real named thing gets the quick exact Commons lookup here, and the page draws
        // it only if that found nothing.
        var found = holds ? previous
            : PictureKind.DrawnFirst(kind) ? null
            : await images.FetchAsync(item.Image, dir, $"item_{i}", used, ct, exactTermOnly: true);
        previous = found;
        if (found is not null) used.Add(found.SourceTitle);
        staged.Add(new
        {
            text = item.Text,
            term = item.Image,
            kind,
            file = found?.FileName,
            credit = found?.Credit,
            source = found?.SourceTitle,
            url = found is null ? null : $"/api/list/draft/{id}/image/{found.FileName}",
        });
    }

    return Results.Ok(new { id, title = idea.Title, source = idea.Source, items = staged });
});

// Re-search ONE line's picture without touching the rest of the draft.
app.MapPost("/api/list/draft/{id}/image", async (
    string id, ReplaceImageRequest req, IStockImageService images,
    IOptions<PipelineOptions> pipe, IWebHostEnvironment env, CancellationToken ct) =>
{
    var dir = DraftDir(pipe.Value, env, id);
    if (!Directory.Exists(dir)) return Results.NotFound(new { error = "draft has expired" });

    // A new filename each time, or the browser keeps showing the cached old picture.
    // Exclude is what the OTHER lines are already using, so a re-search cannot land on a
    // picture that is on screen elsewhere in the same video.
    var found = await images.FetchAsync(
        req.Term, dir, $"item_{req.Index}_{Guid.NewGuid():n}"[..14], req.Exclude, ct, kind: req.Kind);
    if (found is null) return Results.Ok(new
    {
        file = (string?)null,
        message = $"Codex could not create “{req.Term}” and no exact Commons image was found.",
    });

    return Results.Ok(new
    {
        file = found.FileName,
        credit = found.Credit,
        source = found.SourceTitle,
        url = $"/api/list/draft/{id}/image/{found.FileName}",
    });
});

app.MapGet("/api/list/draft/{id}/image/{file}", (
    string id, string file, IOptions<PipelineOptions> pipe, IWebHostEnvironment env) =>
{
    // Filename comes from the URL, so refuse anything that could climb out of the draft folder.
    if (file.Contains("..") || Path.IsPathRooted(file) || file.Contains('/') || file.Contains('\\'))
        return Results.BadRequest();
    var path = Path.Combine(DraftDir(pipe.Value, env, id), file);
    if (!File.Exists(path)) return Results.NotFound();
    var mime = Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/png",
    };
    return Results.File(path, mime);
});

// Approve the draft: copy the chosen pictures into a real job and queue it.
app.MapPost("/api/list/draft/{id}/confirm", (
    string id, ConfirmDraftRequest req, JobManager jobs, ChannelRegistry channels,
    IOptions<PipelineOptions> pipe, IWebHostEnvironment env) =>
{
    var ch = channels.Resolve(string.IsNullOrWhiteSpace(req.Channel) ? null : req.Channel);
    if (!ch.IsList) return Results.BadRequest(new { error = $"channel '{ch.Id}' is not a list channel" });

    var dir = DraftDir(pipe.Value, env, id);
    if (!Directory.Exists(dir)) return Results.NotFound(new { error = "draft has expired" });

    var items = (req.Items ?? new List<ConfirmDraftItem>())
        .Where(i => !string.IsNullOrWhiteSpace(i.Text))
        .ToList();
    if (items.All(i => string.IsNullOrWhiteSpace(i.File)))
        return Results.BadRequest(new { error = "nothing left to make" });

    var topic = string.IsNullOrWhiteSpace(req.Title) ? items[0].Text : req.Title!;
    if (topic.Length > 120) topic = topic[..120];

    var job = jobs.Create(topic, "list", ch.Id, autoGenerated: true, prepare: j =>
    {
        var input = new ListInput { Title = req.Title };
        for (int i = 0; i < items.Count; i++)
        {
            var src = string.IsNullOrWhiteSpace(items[i].File) ? null : Path.Combine(dir, items[i].File!);
            if (src is null || !File.Exists(src))
            {
                // HOLD the previous picture rather than dropping a line whose search came up
                // empty — the last line is usually the payoff, and losing it silently turns a
                // finished script into one that stops mid-argument. Nothing to hold on line 1.
                if (input.Items.Count > 0)
                    input.Items.Add(new ListItem { Image = input.Items[^1].Image, Text = items[i].Text });
                continue;
            }
            // Renamed on the way in so the job folder is ordered even when a picture was
            // swapped and carries a random name from the re-search.
            var name = $"item_{i}{Path.GetExtension(src)}";
            File.Copy(src, Path.Combine(j.OutputDir, name), overwrite: true);
            input.Items.Add(new ListItem { Image = name, Text = items[i].Text });
        }
        File.WriteAllText(Path.Combine(j.OutputDir, "list.json"),
            JsonSerializer.Serialize(input, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var credits = items.Select(i => i.Credit).Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        if (credits.Count > 0)
            File.WriteAllText(Path.Combine(j.OutputDir, "image_credits.txt"),
                string.Join("\n", credits), new UTF8Encoding(false));
    });

    try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    return Results.Ok(new { id = job.Id });
});

// One-click list short: Codex invents the subject, the lines AND a search term per line, then
// each picture is fetched from Wikimedia Commons. Nothing is supplied by hand.
//
// Reference-channel titles go in only to aim at the KIND of subject that works in this niche —
// the prompt is explicit that it must write a new video rather than a version of a listed one.
app.MapPost("/api/generate/list/auto", async (
    ListAutoRequest req, ListAutoMaker maker, CancellationToken ct) =>
{
    var r = await maker.MakeAsync(req.Channel, req.Topic, ct);
    if (!r.Ok)
        return r.QuotaHit
            ? Results.Json(new { error = r.Error }, statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Problem(r.Error);
    return Results.Ok(new
    {
        id = r.JobId,
        title = r.Title,
        lines = r.Lines,
        droppedLines = r.DroppedLines,
        source = r.Source,
    });
});

// Several hands-off list videos from one click on the Generate page, made one after another in
// the background (see ListBatchRunner). The page polls GET for progress.
app.MapPost("/api/generate/list/batch", (ListBatchRequest req, ListBatchRunner batch, JobManager jobs,
    ChannelRegistry channels) =>
{
    var ch = channels.Resolve(string.IsNullOrWhiteSpace(req.Channel) ? null : req.Channel);
    if (!ch.IsList)
        return Results.BadRequest(new { error = $"channel '{ch.Id}' is not a list channel" });
    if (jobs.GenerationPaused)
        return Results.Conflict(new { error = "Generation is paused. Resume it first." });
    if (req.Count < 1 || req.Count > ListBatchRunner.MaxCount)
        return Results.BadRequest(new { error = $"Pick between 1 and {ListBatchRunner.MaxCount} videos." });
    var started = batch.Start(ch.Id, req.Count);
    return started is null
        ? Results.Conflict(new { error = "A batch is already running. Stop it or wait for it to finish." })
        : Results.Ok(started);
});

app.MapGet("/api/generate/list/batch", (ListBatchRunner batch) => Results.Ok(batch.Status()));

app.MapPost("/api/generate/list/batch/stop", (ListBatchRunner batch) => Results.Ok(batch.Stop()));

// Hands-off batch: brainstorm N topics with Codex (in the channel's style) and queue a job
// for each. Returns the created job ids so the UI can track them. Each lands in the review
// queue when finished (approve -> upload, reject -> discard).
app.MapPost("/api/generate/auto", async (AutoGenerateRequest req, ICodexClient codex,
    ITitleStyleService titleStyle, IChannelPerformanceService performance, ChannelRegistry channels,
    JobManager jobs, ILoggerFactory lf, CancellationToken ct) =>
{
    var log = lf.CreateLogger("AutoGenerate");
    int count = Math.Clamp(req.Count <= 0 ? 3 : req.Count, 1, 20);

    var ch = channels.Resolve(req.Channel);
    var styleTitles = await titleStyle.GetStyleTitlesAsync(ch.TitleReferenceChannel, ch.TitleTab, ct);
    // Real view counts from the channel — steers topics toward what actually broke out.
    var perf = await performance.GetAsync(ct: ct);
    var alreadyMade = jobs.RecentTopics(60);

    // De-dupe helper: skip anything already generated (durable) or already picked this batch.
    var picked = new List<string>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var seenFamilies = new HashSet<string>(
        alreadyMade.Select(t => TopicQualityGate.FamilyKey(t, ch.ContentType)).Where(k => k.Length > 0),
        StringComparer.OrdinalIgnoreCase);
    void tryAdd(IEnumerable<string> candidates)
    {
        foreach (var t in candidates)
        {
            if (picked.Count >= count) break;
            var topic = t.Trim();
            if (topic.Length == 0) continue;
            if (!seen.Add(TopicHistory.Normalize(topic))) continue; // dup within this batch
            if (jobs.TopicExists(topic)) continue;                  // already made before
            if (!TopicQualityGate.IsAcceptable(topic, ch.ContentType, out var rejectReason))
            {
                log.LogInformation("Auto-gen rejected weak topic '{Topic}': {Reason}", topic, rejectReason);
                continue;
            }
            var family = TopicQualityGate.FamilyKey(topic, ch.ContentType);
            if (family.Length > 0 && !seenFamilies.Add(family))
            {
                log.LogInformation("Auto-gen rejected similar topic '{Topic}' (family {Family}).", topic, family);
                continue;
            }
            picked.Add(topic);
        }
    }

    // Ask Codex for a fresh batch, telling it what's already been made. Retry once (over-asking)
    // if de-duping leaves us short, then fall back to reference titles + built-in seeds.
    for (int attempt = 1; attempt <= 3 && picked.Count < count; attempt++)
    {
        var avoid = alreadyMade.Concat(picked).ToList();
        int requestCount = Math.Min(60, Math.Max(count * 2, (count - picked.Count) * 4));
        log.LogInformation(
            "Auto-gen brainstorm attempt {Attempt}/3: requesting {RequestCount} candidates for {Remaining} remaining slots.",
            attempt, requestCount, count - picked.Count);
        tryAdd(await codex.BrainstormTopicsAsync(requestCount, ch.ContentType, styleTitles, avoid, perf, ct));
    }
    var fromCodex = picked.Count;
    if (picked.Count < count)
    {
        // Reaching here usually means Codex itself failed (quota exhausted, CLI/model mismatch,
        // timeout) — not that it ran out of ideas. The seeds are a fixed built-in list, so a
        // batch built from them is far less varied; make that loud rather than silent.
        log.LogWarning(
            "Auto-gen: Codex supplied only {Got}/{Want} usable topics; topping up from the STATIC built-in seed list. " +
            "If this repeats, check the Codex CLI (quota/model/auth) — seed batches are much less varied.",
            picked.Count, count);
        tryAdd(AutoGenerateSeeds.For(ch.ContentType));
    }

    if (picked.Count < count)
        return Results.Conflict(new
        {
            error = $"Requested {count} videos, but only {picked.Count} new validated topics were available. No partial batch was queued; try again or request fewer videos.",
            requested = count,
            available = picked.Count,
        });

    if (picked.Count == 0)
        return Results.Conflict(new { error = "Couldn't find any new topics — everything suggested has already been made. Try again or add source material." });

    var created = picked
        .Select(t => jobs.Create(t, req.Format, req.Channel, autoGenerated: true))
        .Select(j => new { id = j.Id, topic = j.Topic })
        .ToList();

    return Results.Ok(new
    {
        count = created.Count,
        channel = ch.Id,
        jobs = created,
        fromCodex = Math.Min(fromCodex, created.Count),
        fromFallbackSeeds = Math.Max(0, created.Count - fromCodex),
        performanceSteered = perf.HasSignal,
    });
});

// Preview brainstormed topics WITHOUT generating anything (cheap — no renders).
app.MapGet("/api/topics/suggest", async (int? count, string? channel, ICodexClient codex,
    ITitleStyleService titleStyle, IChannelPerformanceService performance, ChannelRegistry channels,
    JobManager jobs, CancellationToken ct) =>
{
    int n = Math.Clamp(count ?? 8, 1, 20);
    var ch = channels.Resolve(channel);
    var styleTitles = await titleStyle.GetStyleTitlesAsync(ch.TitleReferenceChannel, ch.TitleTab, ct);
    var perf = await performance.GetAsync(ct: ct);
    var alreadyMade = jobs.RecentTopics(60);
    var topics = new List<string>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var seenFamilies = new HashSet<string>(
        alreadyMade.Select(t => TopicQualityGate.FamilyKey(t, ch.ContentType)).Where(k => k.Length > 0),
        StringComparer.OrdinalIgnoreCase);

    void tryAddSuggestion(IEnumerable<string> candidates)
    {
        foreach (var t in candidates)
        {
            if (topics.Count >= n) break;
            var topic = t.Trim();
            if (topic.Length == 0) continue;
            if (!seen.Add(TopicHistory.Normalize(topic))) continue;
            if (jobs.TopicExists(topic)) continue;
            if (!TopicQualityGate.IsAcceptable(topic, ch.ContentType, out _)) continue;
            var family = TopicQualityGate.FamilyKey(topic, ch.ContentType);
            if (family.Length > 0 && !seenFamilies.Add(family)) continue;
            topics.Add(topic);
        }
    }

    tryAddSuggestion(await codex.BrainstormTopicsAsync(n * 4, ch.ContentType, styleTitles, alreadyMade, perf, ct));
    var fromCodex = topics.Count;
    if (topics.Count < n)
        tryAddSuggestion(AutoGenerateSeeds.For(ch.ContentType));

    // Surface the fallback. When Codex is down or out of quota this endpoint would otherwise
    // return a static built-in list that looks identical to real brainstormed output.
    return Results.Ok(new
    {
        channel = ch.Id,
        topics,
        fromCodex,
        fromFallbackSeeds = topics.Count - fromCodex,
        performanceSteered = perf.HasSignal,
    });
});

// Approve a finished video -> upload to YouTube (private, scheduled to the next slot).
app.MapPost("/api/jobs/{id}/approve", (string id, JobManager jobs) =>
{
    var job = jobs.Approve(id);
    return job is null ? Results.NotFound() : Results.Ok(JobStatusDto.From(job));
});

// Reject a finished video -> it will not be uploaded.
app.MapPost("/api/jobs/{id}/reject", (string id, JobManager jobs) =>
{
    var job = jobs.Reject(id);
    return job is null ? Results.NotFound() : Results.Ok(JobStatusDto.From(job));
});

// Upload calendar: every job with a known publish time (uploaded or scheduled).
app.MapGet("/api/calendar", (JobManager jobs) =>
    Results.Ok(jobs.Scheduled().Select(JobStatusDto.From)));

// Runtime settings (persisted).
app.MapGet("/api/settings", (JobManager jobs, JobStore store) => Results.Ok(new
{
    autoApprove = jobs.AutoApprove,
    generationPaused = jobs.GenerationPaused,
    // Which mode the Generate tab opens on. Persisted so switching tabs (which unmounts the
    // view) doesn't drop you back on Meme Shorts every time.
    channel = store.GetSetting("defaultChannel"),
    // Off by default: whole-image OCR also scoops up the username/handle row, so it fills the
    // box with junk you then have to clear. The per-line OCR the renderer uses is separate and
    // always runs — this toggle only controls the convenience prefill.
    ocrAutofill = store.GetSetting("ocrAutofill") == "true",
}));

app.MapPost("/api/settings/ocr-autofill", (ToggleRequest req, JobStore store) =>
{
    store.SetSetting("ocrAutofill", req.Enabled ? "true" : "false");
    return Results.Ok(new { ocrAutofill = req.Enabled });
});

app.MapPost("/api/settings/auto-approve", (ToggleRequest req, JobManager jobs) =>
{
    jobs.SetAutoApprove(req.Enabled);
    return Results.Ok(new { autoApprove = jobs.AutoApprove });
});

app.MapPost("/api/settings/channel", (ChannelRequest req, JobStore store, ChannelRegistry channels) =>
{
    var id = (req.Channel ?? "").Trim();
    // Validate against the registry so a stale id can't wedge the UI on a channel that no
    // longer exists — the pipeline would resolve it to the default anyway.
    if (id.Length > 0 && !channels.Profiles.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
        return Results.BadRequest(new { error = $"unknown channel '{id}'" });

    if (id.Length == 0) store.DeleteSetting("defaultChannel");
    else store.SetSetting("defaultChannel", id);
    return Results.Ok(new { channel = id.Length == 0 ? null : id });
});

app.MapPost("/api/generation/pause", async (JobManager jobs) =>
    Results.Ok(new { paused = true, affected = await jobs.PauseAllAsync() }));

app.MapPost("/api/generation/resume", (JobManager jobs) =>
    Results.Ok(new { paused = false, resumed = jobs.ResumeAll() }));

// Channels available for the dropdown.
app.MapGet("/api/channels", (ChannelRegistry channels) =>
    Results.Ok(channels.Profiles.Select(p => new { id = p.Id, name = p.Name, contentType = p.ContentType })));

app.MapGet("/api/jobs/{id}", (string id, JobManager jobs) =>
{
    var job = jobs.Get(id);
    return job is null ? Results.NotFound() : Results.Ok(JobStatusDto.From(job));
});

app.MapGet("/api/jobs", (JobManager jobs) =>
    Results.Ok(jobs.Recent().Select(JobStatusDto.From)));

// Stop and discard unfinished work. Completed videos remain in the review/history queue.
app.MapDelete("/api/jobs/unfinished", (JobManager jobs) =>
    Results.Ok(new { deleted = jobs.CancelAndDeleteUnfinished() }));

app.MapGet("/api/jobs/{id}/thumbnail", (string id, JobManager jobs) =>
{
    var job = jobs.Get(id);
    if (job?.ThumbnailPath is null || !File.Exists(job.ThumbnailPath))
        return Results.NotFound();
    return Results.File(job.ThumbnailPath, "image/jpeg");
});

app.MapGet("/api/jobs/{id}/video", (string id, JobManager jobs) =>
{
    var job = jobs.Get(id);
    if (job?.Drive is { } drive)
        return Results.NotFound(new { error = $"This video is in Google Drive ({drive.Remote}). Bring it back to play it here." });
    if (job?.VideoPath is null || !File.Exists(job.VideoPath))
        return Results.NotFound();
    return Results.File(job.VideoPath, "video/mp4", enableRangeProcessing: true);
});

// --- Google Drive (see DriveArchiver) --------------------------------------------------------
// Both directions run in the background; the Review tab polls the job's driveTransfer/driveError.
app.MapPost("/api/jobs/{id}/drive/move", (string id, JobManager jobs, DriveArchiver drive, ILogger<Program> log) =>
{
    var job = jobs.Get(id);
    if (job is null) return Results.NotFound();
    if (!drive.CanMove(job, out var why)) return Results.Conflict(new { error = why });
    _ = Task.Run(async () =>
    {
        try { await drive.MoveAsync(job, CancellationToken.None); }
        catch (Exception ex) { log.LogWarning("Move of {Id} to Google Drive failed: {Why}", id, ex.Message); }
    });
    return Results.Accepted(value: new { started = true });
});

app.MapPost("/api/jobs/{id}/drive/restore", (string id, JobManager jobs, DriveArchiver drive, ILogger<Program> log) =>
{
    var job = jobs.Get(id);
    if (job is null) return Results.NotFound();
    if (job.Drive is null) return Results.Conflict(new { error = "This video's files are already on this PC." });
    if (job.DriveTransfer is not null) return Results.Conflict(new { error = $"It is {job.DriveTransfer} right now." });
    _ = Task.Run(async () =>
    {
        try { await drive.BringBackAsync(job, CancellationToken.None); }
        catch (Exception ex) { log.LogWarning("Bring-back of {Id} from Google Drive failed: {Why}", id, ex.Message); }
    });
    return Results.Accepted(value: new { started = true });
});

app.MapGet("/api/drive", (JobManager jobs, DriveArchiver drive) =>
{
    var all = jobs.All().ToList();
    var inDrive = all.Where(j => j.Drive is not null).ToList();
    return Results.Ok(new
    {
        remote = drive.RemoteRoot,
        autoMove = drive.AutoMove,
        inDrive = inDrive.Count,
        freedBytes = inDrive.Sum(j => j.Drive!.Bytes),
        waiting = all.Count(drive.IsDoneWith),
        moving = all.FirstOrDefault(j => j.DriveTransfer is not null) is { } busy
            ? new { id = busy.Id, what = busy.DriveTransfer } : null,
        failed = all.Where(j => j.DriveError is not null && j.Drive is null)
            .Select(j => new { id = j.Id, error = j.DriveError }).Take(5),
    });
});

app.MapPost("/api/drive/auto", (ToggleRequest req, DriveArchiver drive) =>
{
    drive.AutoMove = req.Enabled;
    return Results.Ok(new { autoMove = drive.AutoMove });
});

// "Move all published & rejected now": the same pass the automatic mover makes, started at once.
app.MapPost("/api/drive/move-all", (JobManager jobs, DriveArchiver drive, DriveAutoMover mover) =>
{
    var due = jobs.All().Count(drive.IsDoneWith);
    _ = Task.Run(() => mover.MoveDueAsync(CancellationToken.None, ignoreRecentFailures: true));
    return Results.Accepted(value: new { queued = due });
});

// Storyboard JSON (handy for debugging what Codex produced)
app.MapGet("/api/jobs/{id}/storyboard", (string id, JobManager jobs) =>
{
    var job = jobs.Get(id);
    return job?.Storyboard is null ? Results.NotFound() : Results.Ok(job.Storyboard);
});

app.Run();

record GenerateRequest(string Topic, string? Format, string? Channel);
record MetaVerifyResult(string Platform, bool Ok, string Message);
record VoiceRequest(string? VoiceId, double? SpeechRate);

/// <summary>Blank/absent Token means "keep the stored one" — see the endpoint comment.</summary>
record MetaPlatformSettings(bool? Enabled, string? PageId, string? IgUserId, string? Token);
record MetaSettingsRequest(MetaPlatformSettings? Facebook, MetaPlatformSettings? Instagram);
record AutoGenerateRequest(int Count, string? Format, string? Channel);

/// <summary>One-click list short. Topic is optional — blank lets Codex pick the subject.</summary>
record ListAutoRequest(string? Topic, string? Channel);
record ListBatchRequest(int Count, string? Channel);

/// <summary>Re-search one draft line's picture, e.g. after Codex's own term missed.</summary>
record ReplaceImageRequest(int Index, string Term, List<string>? Exclude, string? Kind = null);

record ConfirmDraftRequest(string? Title, string? Channel, List<ConfirmDraftItem>? Items);
record ConfirmDraftItem(string Text, string? File, string? Credit);
record ToggleRequest(bool Enabled);
record ChannelRequest(string? Channel);

/// <summary>Last-resort topic seeds so hands-off "Generate" always produces videos even if
/// Codex and yt-dlp are both unavailable.</summary>
static class AutoGenerateSeeds
{
    // Varied/clever — matches the reference channel's real range (visual gags, charts, hypotheticals,
    // absurd how-tos, hot takes), NOT generic everyday-annoyance clips.
    private static readonly string[] Meme =
    {
        "which soda mascot would you pick", "the secret behind snack bags", "evolution of the cereal box",
        "what a dinosaur centaur looks like", "how loading bars work", "ways to hide a note",
        "what superpower would you choose", "how to trap yourself with a chair", "real life vs game logic",
        "shopping carts explained", "most difficult hand signs", "coolest way to save electricity",
        "why alarm clocks feel personal", "which cake slice would you choose", "evolution of the computer mouse",
        "secret behind bathroom hand dryers", "worst chair for a waiting room", "why coffee lids betray you",
        "how phone chargers choose sides", "most confusing road signs", "which movie snack would you pick",
        "ways to hide a note at school", "real life vs computer updates", "secret behind vending machine spirals",
        "evolution of the shopping cart wheel", "why headphones form knots", "which animal would win at chess",
        "how elevator buttons rank floors", "worst food to eat quietly", "what a cat designed house looks like",
        "coolest way to carry snack bags", "why printers wait until deadlines", "real life vs game tutorials",
        "evolution of the school desk", "which cereal shape would you pick", "secret behind takeout boxes",
        "most difficult doors to open", "how restaurant menus hide prices", "why wifi disappears upstairs",
        "which bathroom stall would you choose", "worst movie theater seat", "how traffic lights judge cars",
        "real life vs cat physics", "evolution of phone cases", "which snack package would you pick",
        "why ice cream melts instantly", "most suspicious exit doors", "secret behind keyboard shortcuts",
        "how teachers detect hidden notes", "which shower temperature would you choose", "worst place for a button",
        "what a dinosaur designed car looks like", "real life vs comic book logic", "evolution of toilet paper rolls",
        "why fridge shelves are never equal", "which browser tab would you trust", "most difficult cups to hold",
        "secret behind cereal prizes", "why remote buttons stop responding", "how school bells control everyone",
        "which restaurant table would you choose", "real life vs cat alarm clocks", "evolution of the movie snack",
        "why computer updates take forever", "most confusing bathroom signs", "secret behind phone battery warnings",
    };
    private static readonly string[] Finance =
    {
        "why your savings never grow", "compound interest explained simply", "the 50/30/20 budget rule",
        "how inflation eats your money", "index funds vs picking stocks", "why broke people stay broke",
        "the true cost of car payments", "emergency fund basics", "good debt vs bad debt",
        "how much you need to retire",
    };

    public static IReadOnlyList<string> For(string contentType) =>
        contentType.Equals("finance", StringComparison.OrdinalIgnoreCase) ? Finance : Meme;
}
