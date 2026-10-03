using System.Collections.Concurrent;
using MemeShorts.Api.Models;
using Microsoft.Extensions.Options;

namespace MemeShorts.Api.Services;

/// <summary>
/// In-memory job registry + background execution. One job runs the pipeline on a
/// Task; the browser polls status. (For production, swap to a durable queue.)
/// </summary>
public sealed class JobManager
{
    private readonly ConcurrentDictionary<string, GenerationJob> _jobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new();
    private readonly ConcurrentDictionary<string, Task> _tasks = new();
    private readonly IServiceProvider _sp;
    private readonly PipelineOptions _pipe;
    private readonly UploadOptions _upload;
    private readonly IYouTubeUploader _uploader;
    private readonly IReadOnlyList<ISocialUploader> _social;
    private readonly UploadScheduler _scheduler;
    private readonly TopicHistory _topics;
    private readonly JobStore _store;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<JobManager> _log;

    // Caps how many jobs run at once. Heavy renders (headless Chrome + ffmpeg) crash under
    // contention if several run together, so jobs queue and process N-at-a-time.
    private readonly SemaphoreSlim _gate;

    public JobManager(IServiceProvider sp, IOptions<PipelineOptions> pipe, IOptions<UploadOptions> upload,
        IYouTubeUploader uploader, IEnumerable<ISocialUploader> social, UploadScheduler scheduler,
        TopicHistory topics, JobStore store, IWebHostEnvironment env, ILogger<JobManager> log)
    {
        _sp = sp; _pipe = pipe.Value; _upload = upload.Value; _uploader = uploader; _scheduler = scheduler;
        _social = social.ToList();
        _topics = topics; _store = store; _env = env; _log = log;
        _gate = new SemaphoreSlim(Math.Max(1, _pipe.MaxConcurrentJobs));

        // Auto-approve: a persisted runtime toggle, seeded from the config default (Upload:AutoUpload).
        var saved = _store.GetSetting("autoApprove");
        _autoApprove = saved is not null ? saved == "true" : _upload.AutoUpload;
        _paused = _store.GetSetting("generationPaused") == "true";

        Restore();
    }

    private volatile bool _autoApprove;
    private volatile bool _paused;

    public bool GenerationPaused => _paused;

    /// <summary>When true, finished videos are approved automatically (and uploaded if configured).</summary>
    public bool AutoApprove => _autoApprove;

    /// <summary>Flip the auto-approve toggle. Turning it ON also approves everything already waiting.</summary>
    public void SetAutoApprove(bool enabled)
    {
        _autoApprove = enabled;
        _store.SetSetting("autoApprove", enabled ? "true" : "false");
        _log.LogInformation("Auto-approve set to {Enabled}.", enabled);

        if (!enabled) return;
        // Approve everything currently pending review so the toggle is immediately effective.
        foreach (var job in _jobs.Values.Where(j => j.State == JobState.Done && j.Review == ReviewState.Pending).ToList())
            Approve(job.Id);
    }

    /// <summary>
    /// Reload persisted jobs at startup so a restart/crash/session-teardown doesn't lose them.
    /// Finished jobs come back ready to review (their mp4/thumb are on disk); jobs that were
    /// mid-flight (queued/running) are re-queued to run again so a batch completes.
    /// </summary>
    private void Restore()
    {
        List<GenerationJob> restored;
        try { restored = _store.LoadAll(); }
        catch (Exception ex) { _log.LogWarning(ex, "Job restore failed; starting empty."); return; }

        var resume = new List<GenerationJob>();
        var cancelUnfinishedOnce = Path.Combine(_env.ContentRootPath, "data", "cancel-unfinished-jobs.once");
        var cancelOnStartup = File.Exists(cancelUnfinishedOnce);
        var canceled = 0;
        foreach (var job in restored)
        {
            if (cancelOnStartup && job.State is JobState.Queued or JobState.Running)
            {
                _topics.Release(job.Id);
                _store.Delete(job.Id);
                TryDeleteOutput(job);
                canceled++;
                continue;
            }

            _jobs[job.Id] = job;
            if (job.State is JobState.Queued or JobState.Running or JobState.Paused
                || (job.State == JobState.Failed && job.Stage == "paused"))
            {
                _topics.TryReserve(job.Topic, job.Id);
                job.State = _paused ? JobState.Paused : JobState.Queued;
                job.Stage = _paused ? "paused" : "queued (resuming after restart)";
                Persist(job);
                if (!_paused) resume.Add(job);
            }
        }
        if (cancelOnStartup)
        {
            try { File.Delete(cancelUnfinishedOnce); } catch { /* one-time marker cleanup */ }
            _log.LogInformation("Deleted {Canceled} unfinished job(s) requested before startup.", canceled);
        }
        if (restored.Count > 0)
            _log.LogInformation("Restored {Total} job(s); re-queuing {Resume} unfinished.", restored.Count, resume.Count);

        // Kick unfinished jobs off the gate (one at a time via the semaphore).
        foreach (var job in resume)
            Start(job);
    }

    private void Persist(GenerationJob job) => _store.Save(job);

    /// <summary>Has a short with this topic already been generated? (durable, survives restarts)</summary>
    public bool TopicExists(string topic) => _topics.Exists(topic);

    /// <summary>
    /// What a new list script must not repeat: the latest scripts and titles, and every subject the
    /// channel has covered. The scripts are there because the writer once saw only SUBJECTS, so each
    /// video was fine alone but a batch repeated itself (2 of 7 ended "NO THANK YOU!"). Rejected
    /// videos count - on 2026-09-29 rejected subjects came straight back (glue pizza a third time,
    /// hostile benches a second time), because the writer saw only the 30 newest titles.
    /// </summary>
    public ListHistory ListHistory(string channelId)
    {
        var jobs = _jobs.Values.Where(j => j.ChannelId == channelId && j.Format == "list")
            .OrderByDescending(j => j.CreatedAt).ToList();
        var scripts = jobs.Select(j => ReadListScript(Path.Combine(j.OutputDir, "list.json")))
            .Where(s => s.Length > 0).Take(10).ToList();
        var titles = jobs.Select(j => j.YouTubeTitle ?? j.Topic).Where(t => !string.IsNullOrWhiteSpace(t))
            .Take(12).ToList();
        var subjects = jobs.Select(j => ListSubject(j)).Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(120).ToList();
        return new ListHistory(scripts, titles, subjects);
    }

    /// <summary>The subject a list job was about: its recorded subject, else its script's first sentence, else its title.</summary>
    private static string ListSubject(GenerationJob job)
    {
        var path = Path.Combine(job.OutputDir, "list.json");
        try
        {
            if (File.Exists(path))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("subject", out var s) && s.GetString() is { Length: > 0 } subject)
                    return subject;
            }
        }
        catch { /* fall through */ }
        var first = System.Text.RegularExpressions.Regex.Split(ReadListScript(path), @"(?<=[.!?])\s+")
            .FirstOrDefault(s => s.Trim().Length > 0) ?? "";
        if (first.Length > 90) first = first[..90];
        return first.Length > 0 ? first : job.Topic;
    }

    private static string ReadListScript(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("items", out var items)) return "";
            return string.Join(" ", items.EnumerateArray()
                .Select(i => i.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "")
                .Where(t => t.Length > 0));
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Recent topics already made — to steer auto-generation away from repeats.</summary>
    public IReadOnlyList<string> RecentTopics(int take = 40) => _topics.Recent(take);

    /// <param name="prepare">
    /// Runs once the job dir is known but BEFORE the job can start — used to drop input files
    /// (an uploaded image, repost.json) into the job directory. It has to happen inside Create
    /// because Start() may run the pipeline immediately, which would otherwise race the write.
    /// </param>
    public GenerationJob Create(string topic, string? format, string? channelId = null,
        bool autoGenerated = false, Action<GenerationJob>? prepare = null)
    {
        var job = new GenerationJob
        {
            Topic = topic,
            Format = format,
            ChannelId = string.IsNullOrWhiteSpace(channelId) ? "default" : channelId,
            AutoGenerated = autoGenerated,
        };
        var root = Path.IsPathRooted(_pipe.OutputRoot)
            ? _pipe.OutputRoot
            : Path.Combine(_env.ContentRootPath, _pipe.OutputRoot);
        job.OutputDir = Path.Combine(root, job.Id);
        _jobs[job.Id] = job;

        if (prepare is not null)
        {
            Directory.CreateDirectory(job.OutputDir);
            prepare(job);
        }

        // Reserve the topic so it's never generated again (released if this job fails).
        _topics.TryReserve(job.Topic, job.Id);
        Persist(job); // durable from the moment it's queued

        if (_paused)
        {
            job.State = JobState.Paused;
            job.Stage = "paused";
            Persist(job);
        }
        else Start(job);
        return job;
    }

    public GenerationJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

    public IEnumerable<GenerationJob> Recent(int take = 50) =>
        _jobs.Values.OrderByDescending(j => j.CreatedAt).Take(take);

    /// <summary>Every job, in no particular order.</summary>
    public IEnumerable<GenerationJob> All() => _jobs.Values;

    /// <summary>Persist a job another service changed (the Drive mover records moves this way).</summary>
    public void Save(GenerationJob job) => Persist(job);

    /// <summary>
    /// Has an approved video gone out to EVERY configured destination? Only then is its file no
    /// longer needed here. Instagram holds the file until its slot (it sits in Scheduled), and a
    /// failed destination may be retried from the Review tab, so both count as not done.
    /// </summary>
    public bool IsPublishedEverywhere(GenerationJob job)
    {
        if (job.Review != ReviewState.Approved || !AnyDestinationConfigured) return false;
        if (_uploader.Configured && job.Upload != UploadState.Uploaded) return false;
        return _social.Where(s => s.Configured)
            .All(s => job.PlatformState(s.Platform).State == UploadState.Uploaded);
    }

    private const string InDriveMessage = "This video's files are in Google Drive. Bring it back first.";

    /// <summary>Jobs that have a reserved/known publish time — for the upload calendar.</summary>
    public IEnumerable<GenerationJob> Scheduled() =>
        _jobs.Values.Where(j => j.PublishAtUtc is not null).OrderBy(j => j.PublishAtUtc);

    /// <summary>Stop and permanently remove every queued/running job. Finished jobs are untouched.</summary>
    public int CancelAndDeleteUnfinished()
    {
        var targets = _jobs.Values
            .Where(j => j.State is JobState.Queued or JobState.Running or JobState.Paused)
            .ToList();

        foreach (var job in targets)
        {
            if (_cancellations.TryRemove(job.Id, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
            _jobs.TryRemove(job.Id, out _);
            _topics.Release(job.Id);
            _store.Delete(job.Id);
            TryDeleteOutput(job);
        }

        if (targets.Count > 0)
            _log.LogInformation("Canceled and deleted {Count} unfinished job(s).", targets.Count);
        return targets.Count;
    }

    /// <summary>Whether the human-review gate is active (upload path is configured + enabled).</summary>
    public bool ReviewGateActive => AnyDestinationConfigured;

    /// <summary>Any destination — YouTube, Facebook or Instagram — ready to take an approved short.</summary>
    private bool AnyDestinationConfigured => _uploader.Configured || _social.Any(s => s.Configured);

    public async Task<int> PauseAllAsync()
    {
        _paused = true;
        _store.SetSetting("generationPaused", "true");
        var targets = _jobs.Values
            .Where(j => j.State is JobState.Queued or JobState.Running)
            .ToList();

        foreach (var job in targets)
        {
            job.State = JobState.Paused;
            job.Stage = "paused";
            Persist(job);
            if (_cancellations.TryGetValue(job.Id, out var cts)) cts.Cancel();
        }

        var settling = targets
            .Select(j => _tasks.TryGetValue(j.Id, out var task) ? task : Task.CompletedTask)
            .ToArray();
        await Task.WhenAll(settling);
        return targets.Count;
    }

    public int ResumeAll()
    {
        _paused = false;
        _store.SetSetting("generationPaused", "false");
        var targets = _jobs.Values.Where(j => j.State == JobState.Paused).ToList();
        foreach (var job in targets)
        {
            job.State = JobState.Queued;
            job.Stage = "queued (resuming)";
            job.Percent = 0;
            job.Error = null;
            Persist(job);
            Start(job);
        }
        return targets.Count;
    }

    private void Start(GenerationJob job)
    {
        if (_paused) return;
        var cts = new CancellationTokenSource();
        _cancellations[job.Id] = cts;
        var task = Task.Run(() => ExecuteAsync(job, cts.Token));
        _tasks[job.Id] = task;
    }

    private async Task ExecuteAsync(GenerationJob job, CancellationToken ct)
    {
        // Wait for a free slot — extra jobs stay "queued" until one ahead finishes.
        var enteredGate = false;
        try
        {
            await _gate.WaitAsync(ct);
            enteredGate = true;
            // Each job gets its own DI scope so scoped services (HttpClient, etc.) are fresh.
            using var scope = _sp.CreateScope();
            var pipeline = scope.ServiceProvider.GetRequiredService<PipelineService>();
            await pipeline.RunAsync(job, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (_paused && _jobs.ContainsKey(job.Id))
            {
                job.State = JobState.Paused;
                job.Stage = "paused";
                Persist(job);
                _log.LogInformation("Job {Id} paused.", job.Id);
            }
            else _log.LogInformation("Job {Id} canceled.", job.Id);
        }
        catch (Exception ex)
        {
            if (_paused && _jobs.ContainsKey(job.Id))
            {
                job.State = JobState.Paused;
                job.Stage = "paused";
                job.Error = null;
                _log.LogInformation("Job {Id} stopped at a pause checkpoint.", job.Id);
            }
            else
            {
                _log.LogError(ex, "Job {Id} failed.", job.Id);
                job.State = JobState.Failed;
                job.Error = ex.Message;
                // Free the topic so a failed generation can be retried / re-suggested.
                _topics.Release(job.Id);
            }
        }
        finally
        {
            if (enteredGate) _gate.Release();
            if (_cancellations.TryRemove(job.Id, out var cts)) cts.Dispose();
            _tasks.TryRemove(job.Id, out _);
        }

        // A cancel/delete can remove the job before its pipeline has fully unwound. Do not
        // recreate that job's persisted snapshot when cancellation reaches this point.
        if (!_jobs.ContainsKey(job.Id)) return;

        // Every finished render lands in the review queue for a human approve/reject — even if
        // YouTube upload isn't configured yet (approving just won't upload until it is).
        if (job.State == JobState.Done && job.VideoPath is not null)
        {
            // Auto-approve = a standing "pre-approval": skip the manual gate. Upload if configured,
            // otherwise just mark it approved.
            if (_autoApprove)
            {
                job.Review = ReviewState.Approved;
                job.ReviewedAt = DateTimeOffset.UtcNow;
                if (AnyDestinationConfigured)
                    await UploadInternalAsync(job);
                else
                    job.Stage = "approved (upload not configured)";
            }
            else
            {
                job.Review = ReviewState.Pending;
                job.Stage = "awaiting review";
            }
        }

        Persist(job); // capture the settled state (done/pending, failed, or uploaded)
    }

    private void TryDeleteOutput(GenerationJob job)
    {
        if (string.IsNullOrWhiteSpace(job.OutputDir)) return;

        var root = Path.IsPathRooted(_pipe.OutputRoot)
            ? _pipe.OutputRoot
            : Path.Combine(_env.ContentRootPath, _pipe.OutputRoot);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullOutput = Path.GetFullPath(job.OutputDir);
        if (!fullOutput.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(fullOutput)) return;

        try { Directory.Delete(fullOutput, recursive: true); }
        catch (Exception ex) { _log.LogWarning(ex, "Could not delete canceled job output {OutputDir}.", fullOutput); }
    }

    /// <summary>Approve a finished job for upload. Returns the job, or null if not found.</summary>
    /// <summary>
    /// Send a video that failed its own check to review anyway.
    ///
    /// The check errs on the side of failing: it calls a stretch of picture that no slice draws a
    /// fault, which it usually is, but not always - Matthew watched one whose 78px "blank stretch"
    /// read perfectly and said "this pass for me, put it on review". This is the manual override
    /// for exactly that, and it goes no further than the review queue: the video still has to be
    /// approved by hand before it can upload, and what the check objected to is kept on the job so
    /// the review card can say what was overruled.
    /// </summary>
    public GenerationJob? AcceptFailedCheck(string id)
    {
        var job = Get(id);
        if (job is null) return null;
        if (job.State != JobState.Failed || job.VideoPath is null || !File.Exists(job.VideoPath))
        {
            job.Error = "Only a failed job that still has its video can be sent to review.";
            return job;
        }

        _log.LogInformation("Job {Id} sent to review despite its check: {Fault}", job.Id, job.Error);
        job.CheckOverride = job.Error;
        job.Error = null;
        job.State = JobState.Done;
        job.Percent = 100;
        job.Stage = "sent to review despite the check";
        job.Review = ReviewState.Pending;
        Persist(job);
        return job;
    }

    public GenerationJob? Approve(string id)
    {
        var job = Get(id);
        if (job is null) return null;
        if (job.State != JobState.Done || job.VideoPath is null)
        {
            job.UploadError = "Job has no finished video to approve.";
            return job;
        }
        if (job.Drive is not null || job.DriveTransfer is not null)
        {
            job.UploadError = InDriveMessage;
            return job;
        }
        if (job.Upload is UploadState.Uploading or UploadState.Uploaded) return job;
        job.Review = ReviewState.Approved;
        job.ReviewedAt = DateTimeOffset.UtcNow;

        if (AnyDestinationConfigured)
        {
            _ = Task.Run(() => UploadInternalAsync(job));
        }
        else
        {
            // Approved, but there's nowhere to upload yet. Record it so the UI can explain.
            job.Stage = "approved (upload not configured)";
            job.UploadError = "No upload destination configured — approved but not uploaded.";
            _log.LogInformation("Job {Id} approved but upload not configured.", job.Id);
        }
        Persist(job);
        return job;
    }

    /// <summary>Reject a finished job — it will not be uploaded. Returns the job, or null if not found.</summary>
    public GenerationJob? Reject(string id)
    {
        var job = Get(id);
        if (job is null) return null;
        if (job.Upload == UploadState.Uploaded)
        {
            job.UploadError = "Already uploaded — can't reject.";
            return job;
        }
        job.Review = ReviewState.Rejected;
        job.ReviewedAt = DateTimeOffset.UtcNow;
        job.Stage = "rejected";
        _log.LogInformation("Job {Id} rejected — will not upload.", job.Id);
        Persist(job);
        return job;
    }

    /// <summary>
    /// Re-render a finished job with the CURRENT voice, reusing its storyboard and panel art.
    /// For auditioning voices: costs TTS + render, never image generation. Refuses once the job
    /// has been uploaded, since the published video can no longer be swapped.
    /// </summary>
    public GenerationJob? Revoice(string id)
    {
        var job = Get(id);
        if (job is null) return null;
        // A meme job remakes from its stored storyboard; a repost or list job has no storyboard
        // but DOES keep the inputs the pipeline reads (the screenshot + repost.json, or the
        // pictures + list.json), so it can be remade just as well. Requiring a storyboard meant
        // there was no way to redo a repost at all — and re-queuing it as a new job is refused
        // by the duplicate-topic guard, so those videos were simply stuck as they were.
        if (job.Drive is not null || job.DriveTransfer is not null)
        {
            job.Error = InDriveMessage;
            return job;
        }
        var hasStoredInput = File.Exists(Path.Combine(job.OutputDir, "repost.json"))
                          || File.Exists(Path.Combine(job.OutputDir, "list.json"));
        if (job.Storyboard is null && !hasStoredInput)
        {
            job.Error = "Nothing stored to remake this video from.";
            return job;
        }
        if (job.Upload == UploadState.Uploaded || job.Facebook.State == UploadState.Uploaded)
        {
            job.Error = "Already uploaded — re-voicing would not change the published video.";
            return job;
        }
        if (job.State is JobState.Queued or JobState.Running) return job;

        job.ReuseStoryboardAndPanels = job.Storyboard is not null;
        job.State = JobState.Queued;
        job.Stage = "queued (re-voicing)";
        job.Percent = 0;
        job.Error = null;
        // Send it back through review so the new audio gets judged before it can upload.
        job.Review = ReviewState.None;
        job.ReviewedAt = null;
        Persist(job);
        Start(job);
        _log.LogInformation("Job {Id} queued for re-voicing.", job.Id);
        return job;
    }

    /// <summary>Manually (re)upload a finished job. Returns the job, or null if not found.</summary>
    public GenerationJob? RequestUpload(string id)
    {
        var job = Get(id);
        if (job is null) return null;
        if (job.State != JobState.Done || job.VideoPath is null)
        {
            job.UploadError = "Job has no finished video to upload.";
            return job;
        }
        if (job.Drive is not null || job.DriveTransfer is not null)
        {
            job.UploadError = InDriveMessage;
            return job;
        }
        if (job.Upload == UploadState.Uploading) return job;
        _ = Task.Run(() => UploadInternalAsync(job));
        return job;
    }

    /// <summary>
    /// Push an approved short to every configured destination. Each is independent: one
    /// platform failing never blocks the others, and every destination shares the job's single
    /// reserved slot so a short goes live at the same time everywhere.
    ///
    /// Destinations already marked Uploaded are skipped, so a retry only re-runs what failed
    /// instead of duplicating posts.
    /// </summary>
    private async Task UploadInternalAsync(GenerationJob job)
    {
        var social = _social.Where(s => s.Configured).ToList();
        if (!_uploader.Configured && social.Count == 0)
        {
            job.Upload = UploadState.Failed;
            job.UploadError = "No upload destination configured (set Upload:Enabled + client secrets, or Facebook/Instagram credentials).";
            Persist(job);
            return;
        }
        if (job.VideoPath is null || !File.Exists(job.VideoPath))
        {
            job.Upload = UploadState.Failed;
            job.UploadError = "Video file missing.";
            Persist(job);
            return;
        }

        // One slot per job, reused on retry so a partially-failed job publishes at the time it
        // already advertised rather than jumping the queue with a fresh reservation.
        var reservedNow = job.PublishAtUtc is null;
        var slot = job.PublishAtUtc ?? _scheduler.ReserveNextSlot(job.Id);
        job.PublishAtUtc = slot;

        var title = job.YouTubeTitle ?? job.Storyboard?.Title ?? job.Topic;
        var description = job.YouTubeDescription ?? title;
        var anySucceeded = false;

        if (_uploader.Configured && job.Upload != UploadState.Uploaded)
        {
            try
            {
                job.Upload = UploadState.Uploading;
                job.Stage = "uploading to youtube";
                var res = await _uploader.UploadAsync(job.VideoPath, title, description, job.YouTubeTags,
                    job.YouTubeCategoryId ?? "24", slot, job.ThumbnailPath, CancellationToken.None);
                job.YouTubeVideoId = res.VideoId;
                job.PublishAtUtc = res.PublishAtUtc;
                job.Upload = UploadState.Uploaded;
                job.UploadError = null;
                anySucceeded = true;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "YouTube upload failed for job {Id}.", job.Id);
                job.Upload = UploadState.Failed;
                job.UploadError = ex.Message;
            }
            Persist(job);
        }
        else if (job.Upload == UploadState.Uploaded)
        {
            anySucceeded = true;
        }

        foreach (var uploader in social)
        {
            var state = job.PlatformState(uploader.Platform);
            if (state.State == UploadState.Uploaded) { anySucceeded = true; continue; }

            // Platforms that can't schedule server-side wait for SocialPublishWorker to call
            // them at the slot — publishing now would ignore the pacing entirely.
            if (!uploader.SupportsNativeScheduling && uploader.DeferUntilSlot && slot > DateTimeOffset.UtcNow)
            {
                state.State = UploadState.Scheduled;
                state.PublishAtUtc = slot;
                state.Error = null;
                _log.LogInformation("Job {Id} queued for {Platform} at {Slot:u}.", job.Id, uploader.Platform, slot);
                anySucceeded = true;
                Persist(job);
                continue;
            }

            if (await PublishToPlatformAsync(job, uploader, title, description, slot)) anySucceeded = true;
        }

        // Nothing landed anywhere — hand the slot back rather than burning it, but only if this
        // call is what reserved it (a retry must not release the slot an earlier success used).
        if (!anySucceeded && reservedNow)
        {
            _scheduler.ReleaseSlot(job.Id);
            job.PublishAtUtc = null;
        }

        job.Stage = DescribeDestinations(job);
        Persist(job); // persist uploaded/publishAt (or the failure) so it survives a restart
    }

    /// <summary>Run one social platform's publish, recording success or failure on the job.</summary>
    private async Task<bool> PublishToPlatformAsync(GenerationJob job, ISocialUploader uploader,
        string title, string description, DateTimeOffset slot)
    {
        var state = job.PlatformState(uploader.Platform);
        try
        {
            state.State = UploadState.Uploading;
            state.Error = null;
            job.Stage = $"uploading to {uploader.Platform}";
            Persist(job);

            var scheduleFor = uploader.SupportsNativeScheduling ? slot : (DateTimeOffset?)null;
            var res = await uploader.PublishAsync(
                new SocialPost(job.VideoPath!, title, description, scheduleFor), CancellationToken.None);

            state.PostId = res.PostId;
            state.PermalinkUrl = res.PermalinkUrl;
            state.PublishAtUtc = res.ScheduledForUtc ?? DateTimeOffset.UtcNow;
            state.State = UploadState.Uploaded;
            state.Error = null;
            Persist(job);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "{Platform} publish failed for job {Id}.", uploader.Platform, job.Id);
            state.State = UploadState.Failed;
            state.Error = ex.Message;
            Persist(job);
            return false;
        }
    }

    /// <summary>
    /// Publish every destination whose reserved slot has come due. Called on a timer by
    /// <see cref="SocialPublishWorker"/> — this is what makes Instagram's "no scheduling"
    /// API behave like YouTube's and Facebook's server-side scheduling.
    /// </summary>
    public async Task<int> PublishDueAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var published = 0;

        foreach (var uploader in _social.Where(s => s.Configured))
        {
            var due = _jobs.Values
                .Where(j => j.Review == ReviewState.Approved
                         && j.PlatformState(uploader.Platform).State == UploadState.Scheduled
                         && j.PlatformState(uploader.Platform).PublishAtUtc is { } at && at <= now)
                .OrderBy(j => j.PlatformState(uploader.Platform).PublishAtUtc)
                .ToList();

            foreach (var job in due)
            {
                if (ct.IsCancellationRequested) return published;

                var state = job.PlatformState(uploader.Platform);
                var slot = state.PublishAtUtc!.Value;

                // The app was off when this slot passed. Posting a long-stale reel at a random
                // hour is worse than not posting it, so fail it and let the user decide.
                if (uploader.MissedSlotGrace is { } grace && now - slot > grace)
                {
                    _log.LogWarning("Job {Id} missed its {Platform} slot {Slot:u} by more than {Grace}; skipping.",
                        job.Id, uploader.Platform, slot, grace);
                    state.State = UploadState.Failed;
                    state.Error = $"Missed its {slot:g} UTC slot by more than {grace.TotalHours:0.#}h " +
                                  "(the app wasn't running). Retry to publish it now.";
                    job.Stage = DescribeDestinations(job);
                    Persist(job);
                    continue;
                }

                var title = job.YouTubeTitle ?? job.Storyboard?.Title ?? job.Topic;
                var description = job.YouTubeDescription ?? title;
                if (job.VideoPath is null || !File.Exists(job.VideoPath))
                {
                    state.State = UploadState.Failed;
                    state.Error = "Video file missing.";
                    Persist(job);
                    continue;
                }

                if (await PublishToPlatformAsync(job, uploader, title, description, slot)) published++;
                job.Stage = DescribeDestinations(job);
                Persist(job);
            }
        }

        return published;
    }

    /// <summary>A one-line "where did this land" summary for the job's stage text.</summary>
    private string DescribeDestinations(GenerationJob job)
    {
        var parts = new List<string>();
        if (_uploader.Configured) parts.Add($"youtube {Word(job.Upload)}");
        foreach (var s in _social.Where(s => s.Configured))
            parts.Add($"{s.Platform} {Word(job.PlatformState(s.Platform).State)}");
        return parts.Count == 0 ? "approved (upload not configured)" : string.Join(", ", parts);

        static string Word(UploadState s) => s switch
        {
            UploadState.Uploaded => "ok",
            UploadState.Failed => "failed",
            UploadState.Scheduled => "scheduled",
            UploadState.Uploading => "uploading",
            _ => "pending",
        };
    }
}
