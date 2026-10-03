using MemeShorts.Api.Models;
using Microsoft.Extensions.Options;

namespace MemeShorts.Api.Services;

/// <summary>
/// Moves a finished video's files to Google Drive, and back - copied from YtPipeline's
/// RcloneArchiver (Downloads\Youtube Pipeline Plan), through the same "gdrive" rclone remote.
///
/// rclone uploads straight to Google: copy, then check every file against the Drive copy, and only
/// when that passes delete the local copies. A failed or stopped upload deletes nothing and can
/// simply be started again (rclone skips what already arrived). Copying into Google Drive for
/// desktop's G: folder instead frees nothing - the Drive app keeps its own cached copy on this PC
/// (YtPipeline measured 2.2 GB left behind by five moved videos).
///
/// The thumbnail and the small .json/.txt files stay: the thumbnail keeps the video recognisable in
/// the lists, and list.json is what the script writer reads to avoid repeating recent videos.
/// </summary>
public sealed class DriveArchiver
{
    private const long KeepSmallTextUnder = 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    private readonly JobManager _jobs;
    private readonly JobStore _store;
    private readonly DriveOptions _opt;
    private readonly ILogger<DriveArchiver> _log;
    // One transfer at a time: rclone already uses parallel streams, and the auto-mover and the
    // buttons must never work on the same folder together.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DriveArchiver(JobManager jobs, JobStore store, IOptions<DriveOptions> opt, ILogger<DriveArchiver> log)
    {
        _jobs = jobs;
        _store = store;
        _opt = opt.Value;
        _log = log;
    }

    public string RemoteRoot => _opt.Remote.TrimEnd('/', '\\');

    /// <summary>Automatic moving, as set in the Settings tab (falls back to appsettings Drive:AutoMove).</summary>
    public bool AutoMove
    {
        get => _store.GetSetting("driveAutoMove") is { } v ? v == "true" : _opt.AutoMove;
        set => _store.SetSetting("driveAutoMove", value ? "true" : "false");
    }

    public string RcloneExecutable =>
        File.Exists(_opt.RcloneExecutable) || !Path.IsPathRooted(_opt.RcloneExecutable) ? _opt.RcloneExecutable : "rclone";

    /// <summary>What the automatic mover would move now: published everywhere, or rejected.</summary>
    public bool IsDoneWith(GenerationJob job) =>
        CanMove(job, out _) && (job.Review == ReviewState.Rejected || _jobs.IsPublishedEverywhere(job));

    /// <summary>Can this job's files be moved at all right now? <paramref name="why"/> says why not.</summary>
    public bool CanMove(GenerationJob job, out string why)
    {
        why = "";
        if (job.Drive is not null) why = $"It is already in Google Drive ({job.Drive.Remote}).";
        else if (job.DriveTransfer is not null) why = $"It is {job.DriveTransfer} right now.";
        else if (job.State is not (JobState.Done or JobState.Failed)) why = "It is still being made.";
        else if (job.Upload == UploadState.Uploading
                 || job.Facebook.State is UploadState.Uploading or UploadState.Scheduled
                 || job.Instagram.State is UploadState.Uploading or UploadState.Scheduled)
            why = "It is still being published, and the upload needs the file.";
        else if (job.Review == ReviewState.Approved && !_jobs.IsPublishedEverywhere(job)
                 && job.Upload != UploadState.Failed)
            why = "It is approved but not published everywhere yet.";
        else if (!Directory.Exists(job.OutputDir) || MovableFiles(job).Count == 0)
            why = "Its files are not on this PC.";
        return why.Length == 0;
    }

    public async Task MoveAsync(GenerationJob job, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!CanMove(job, out var why)) throw new InvalidOperationException(why);
            job.DriveTransfer = "moving";
            job.DriveError = null;

            var remote = $"{RemoteRoot}/{job.ChannelId}/{job.Id}";
            var files = Directory.EnumerateFiles(job.OutputDir, "*", SearchOption.AllDirectories).ToList();
            var movable = MovableFiles(job);
            var bytes = movable.Sum(f => new FileInfo(f).Length);
            _log.LogInformation("Moving job {Id} ({Files} files, {Mb:F0} MB) to {Remote}.",
                job.Id, files.Count, bytes / 1048576d, remote);

            // copy, not move: nothing here is touched until the check below has passed. The whole
            // folder goes up, kept files included, so the Drive copy is complete on its own.
            await RunRclone(["copy", job.OutputDir, remote], ct);
            await RunRclone(["check", job.OutputDir, remote, "--one-way"], ct);

            // Recorded before anything is deleted, so an interrupted clean-up still leaves a job
            // that correctly says where its files are.
            job.Drive = new DriveCopy { Remote = remote, MovedUtc = DateTimeOffset.UtcNow, Files = files.Count, Bytes = bytes };
            _jobs.Save(job);

            foreach (var file in movable)
                File.Delete(file);
            foreach (var dir in Directory.EnumerateDirectories(job.OutputDir, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            _log.LogInformation("Job {Id} is in Google Drive; freed {Mb:F0} MB here.", job.Id, bytes / 1048576d);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            job.DriveError = ex.Message;
            _jobs.Save(job);
            throw;
        }
        finally
        {
            job.DriveTransfer = null;
            _gate.Release();
        }
    }

    /// <summary>Downloads a moved job's files, checks them, and clears the move. The Drive copy stays.</summary>
    public async Task BringBackAsync(GenerationJob job, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var remote = job.Drive?.Remote ?? throw new InvalidOperationException("This video's files are already on this PC.");
            job.DriveTransfer = "bringing back";
            job.DriveError = null;
            Directory.CreateDirectory(job.OutputDir);

            await RunRclone(["copy", remote, job.OutputDir], ct);
            await RunRclone(["check", remote, job.OutputDir, "--one-way"], ct);

            job.Drive = null;
            _jobs.Save(job);
            _log.LogInformation("Job {Id} is back on this PC (the Drive copy was left as it is).", job.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            job.DriveError = ex.Message;
            _jobs.Save(job);
            throw;
        }
        finally
        {
            job.DriveTransfer = null;
            _gate.Release();
        }
    }

    /// <summary>Everything in the job folder except the thumbnail and small .json/.txt files.</summary>
    private static List<string> MovableFiles(GenerationJob job)
    {
        if (!Directory.Exists(job.OutputDir)) return new List<string>();
        var thumb = job.ThumbnailPath is null ? null : Path.GetFullPath(job.ThumbnailPath);
        return Directory.EnumerateFiles(job.OutputDir, "*", SearchOption.AllDirectories)
            .Where(f =>
            {
                if (thumb is not null && string.Equals(Path.GetFullPath(f), thumb, StringComparison.OrdinalIgnoreCase))
                    return false;
                var ext = Path.GetExtension(f).ToLowerInvariant();
                return !(ext is ".json" or ".txt" && new FileInfo(f).Length < KeepSmallTextUnder);
            })
            .ToList();
    }

    /// <summary>The rclone settings file to pass explicitly - see DriveOptions.RcloneConfig.</summary>
    public string RcloneConfigPath => !string.IsNullOrWhiteSpace(_opt.RcloneConfig)
        ? _opt.RcloneConfig
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "rclone", "rclone.conf");

    private async Task RunRclone(string[] args, CancellationToken ct)
    {
        var config = RcloneConfigPath;
        try
        {
            using var _ = File.OpenRead(config);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Can't read rclone's settings at {config} ({ex.GetType().Name}), so nothing was moved. " +
                "If the app was started from another tool's window, close it and start it from a normal one.");
        }

        args = [.. args, "--config", config];
        var res = await ProcessRunner.RunAsync(RcloneExecutable, args, workingDir: null, Timeout, ct);
        if (res.Ok) return;
        // rclone puts the reason on its last error line ("didn't find section in config file",
        // "3 differences found"); the exit code alone says nothing.
        var why = res.StdErr.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0)
                  ?? $"exit code {res.ExitCode}";
        throw new InvalidOperationException($"rclone {args[0]} failed, and nothing on this PC was deleted: {why}");
    }
}

/// <summary>
/// Every few minutes, moves videos that are done with - published to every destination, or
/// rejected - to Google Drive. One at a time, oldest first. A video whose move failed waits an
/// hour before it is tried again, so a broken sign-in doesn't retry the same upload all day.
/// </summary>
public sealed class DriveAutoMover : BackgroundService
{
    private static readonly TimeSpan RetryFailedAfter = TimeSpan.FromHours(1);

    private readonly DriveArchiver _drive;
    private readonly JobManager _jobs;
    private readonly DriveOptions _opt;
    private readonly ILogger<DriveAutoMover> _log;
    // Written by the timer loop and by "Move all" from the Settings tab.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _failedAt = new();

    public DriveAutoMover(DriveArchiver drive, JobManager jobs, IOptions<DriveOptions> opt, ILogger<DriveAutoMover> log)
    {
        _drive = drive;
        _jobs = jobs;
        _opt = opt.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, _opt.CheckMinutes)));
        do
        {
            if (_drive.AutoMove)
                await MoveDueAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Moves every video that is done with. Also used by "Move all" in the Settings tab.</summary>
    public async Task<int> MoveDueAsync(CancellationToken ct, bool ignoreRecentFailures = false)
    {
        var moved = 0;
        var due = _jobs.All()
            .Where(_drive.IsDoneWith)
            .Where(j => ignoreRecentFailures || !_failedAt.TryGetValue(j.Id, out var at)
                        || DateTimeOffset.UtcNow - at > RetryFailedAfter)
            .OrderBy(j => j.CreatedAt)
            .ToList();
        foreach (var job in due)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await _drive.MoveAsync(job, ct);
                _failedAt.TryRemove(job.Id, out _);
                moved++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _failedAt[job.Id] = DateTimeOffset.UtcNow;
                _log.LogWarning("Could not move job {Id} to Google Drive: {Why}", job.Id, ex.Message);
            }
        }
        return moved;
    }
}
