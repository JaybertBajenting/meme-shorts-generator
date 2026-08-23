namespace MemeShorts.Api.Services;

/// <summary>
/// Ticks once a minute and publishes any destination whose reserved slot has come due.
///
/// This exists because Instagram's Content Publishing API has no scheduling — media_publish
/// goes live the instant it's called. YouTube (publishAt) and Facebook (scheduled_publish_time)
/// hand the timing to the platform and never reach this worker; Instagram reels sit in
/// <see cref="Models.UploadState.Scheduled"/> until their slot arrives and this wakes them up.
///
/// Consequence worth knowing: the app has to be running at the slot for an Instagram reel to
/// go out. If it isn't, the reel publishes on the next start so long as it's still inside
/// Instagram:SkipIfMissedByMinutes; past that it's marked failed and waits for a manual retry.
/// </summary>
public sealed class SocialPublishWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly JobManager _jobs;
    private readonly IEnumerable<ISocialUploader> _social;
    private readonly ILogger<SocialPublishWorker> _log;

    public SocialPublishWorker(JobManager jobs, IEnumerable<ISocialUploader> social, ILogger<SocialPublishWorker> log)
    {
        _jobs = jobs; _social = social; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Nothing to wait on if no platform needs the app to do its timing.
        if (!_social.Any(s => s.Configured && !s.SupportsNativeScheduling))
        {
            _log.LogInformation("Scheduled-publish worker idle — no platform needs app-side scheduling.");
            return;
        }

        _log.LogInformation("Scheduled-publish worker started (checking every {Interval}).", Interval);
        using var timer = new PeriodicTimer(Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var published = await _jobs.PublishDueAsync(stoppingToken);
                if (published > 0) _log.LogInformation("Published {Count} scheduled post(s).", published);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let one bad tick kill the worker — the next slot still needs it.
                _log.LogError(ex, "Scheduled-publish tick failed; will retry next interval.");
            }

            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) { break; }
        }
    }
}
