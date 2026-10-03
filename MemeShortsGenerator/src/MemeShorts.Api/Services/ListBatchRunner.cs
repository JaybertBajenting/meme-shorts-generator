namespace MemeShorts.Api.Services;

/// <summary>A snapshot of the current (or last) list batch, for the Generate page to poll.</summary>
public sealed record ListBatchStatus(
    string State,              // "idle", "running", "stopping", "finished", "stopped", "quota"
    string? Channel,
    int Requested,
    int Attempted,
    string? Step,
    string? Message,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<ListBatchVideo> Videos,
    IReadOnlyList<string> Errors);

public sealed record ListBatchVideo(string JobId, string Title, int DroppedLines, string? Source);

/// <summary>
/// Makes N hands-off list videos one after another in the background, so a batch is one click on
/// the Generate page instead of a person (or an assistant) submitting them one at a time.
///
/// One at a time on purpose: each video's script is shown the channel's latest scripts so it does
/// not repeat their endings and subjects, and that only works if the previous one is finished.
/// It stops by itself when Codex runs out of quota - see <see cref="CodexQuota"/> - rather than
/// burning the rest of the batch on videos with no pictures. State lives in memory: a restart of
/// the API ends the batch (videos already made stay in Review).
/// </summary>
public sealed class ListBatchRunner
{
    public const int MaxCount = 30;
    /// <summary>Consecutive non-quota failures before giving up - something else is wrong.</summary>
    private const int MaxFailuresInARow = 3;

    private readonly ListAutoMaker _maker;
    private readonly ILogger<ListBatchRunner> _log;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;

    private string _state = "idle";
    private string? _channel;
    private int _requested, _attempted;
    private string? _step, _message;
    private DateTimeOffset? _startedAt, _finishedAt;
    private readonly List<ListBatchVideo> _videos = new();
    private readonly List<string> _errors = new();

    public ListBatchRunner(ListAutoMaker maker, ILogger<ListBatchRunner> log)
    {
        _maker = maker;
        _log = log;
    }

    public ListBatchStatus Status()
    {
        lock (_gate)
            return new ListBatchStatus(_state, _channel, _requested, _attempted, _step, _message,
                _startedAt, _finishedAt, _videos.ToList(), _errors.ToList());
    }

    /// <summary>Starts a batch, or returns null when one is already running.</summary>
    public ListBatchStatus? Start(string? channel, int count)
    {
        lock (_gate)
        {
            if (_state is "running" or "stopping") return null;
            _cts = new CancellationTokenSource();
            _state = "running";
            _channel = channel;
            _requested = Math.Clamp(count, 1, MaxCount);
            _attempted = 0;
            _step = "Starting";
            _message = null;
            _startedAt = DateTimeOffset.UtcNow;
            _finishedAt = null;
            _videos.Clear();
            _errors.Clear();
        }
        var token = _cts.Token;
        _ = Task.Run(() => RunAsync(channel, token));
        return Status();
    }

    /// <summary>Stops the batch now. The video being made is dropped; finished ones stay in Review.</summary>
    public ListBatchStatus Stop()
    {
        lock (_gate)
        {
            if (_state == "running")
            {
                _state = "stopping";
                _step = "Stopping";
                _cts?.Cancel();
            }
        }
        return Status();
    }

    private async Task RunAsync(string? channel, CancellationToken ct)
    {
        int failuresInARow = 0;
        string end = "finished";
        string? message = null;
        try
        {
            while (true)
            {
                int n;
                lock (_gate)
                {
                    if (_videos.Count >= _requested) break;
                    n = _videos.Count + 1;
                    _attempted++;
                    _step = $"Writing and drawing video {n} of {_requested}";
                }
                ct.ThrowIfCancellationRequested();

                var result = await _maker.MakeAsync(channel, null, ct);
                if (result.Ok)
                {
                    failuresInARow = 0;
                    lock (_gate)
                        _videos.Add(new ListBatchVideo(result.JobId!, result.Title ?? "", result.DroppedLines, result.Source));
                    _log.LogInformation("List batch: video {N} queued ({Id}, '{Title}').", n, result.JobId, result.Title);
                    continue;
                }

                lock (_gate) _errors.Add(result.Error ?? "Unknown error.");
                if (result.QuotaHit)
                {
                    end = "quota";
                    message = result.Error;
                    break;
                }
                if (++failuresInARow >= MaxFailuresInARow)
                {
                    end = "stopped";
                    message = $"Stopped after {MaxFailuresInARow} failures in a row: {result.Error}";
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            end = "stopped";
            message = "Stopped. The video that was being made was dropped; finished ones are in Review.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "List batch crashed.");
            end = "stopped";
            message = "The batch crashed: " + ex.Message;
            lock (_gate) _errors.Add(ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                _state = end;
                _step = null;
                _message = message ?? $"Made {_videos.Count} of {_requested}. They render one at a time and land in Review.";
                _finishedAt = DateTimeOffset.UtcNow;
            }
            _log.LogInformation("List batch ended ({State}): {Message}", end, message);
        }
    }
}
