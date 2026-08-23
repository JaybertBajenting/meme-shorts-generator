using System.Globalization;
using MemeShorts.Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace MemeShorts.Api.Services;

/// <summary>One published video, joined to the topic that generated it.</summary>
/// <param name="Topic">The generated topic, when this video came from a job. Empty for manual uploads.</param>
/// <param name="Multiple">Views as a multiple of the channel median — the breakout score.</param>
public sealed record VideoPerformance(
    string VideoId, string Title, string? Topic, long Views, DateTimeOffset? PublishedUtc, double Multiple)
{
    /// <summary>What to show the brainstormer: the topic if known, else the published title.</summary>
    public string Label => string.IsNullOrWhiteSpace(Topic) ? Title : Topic!;
}

/// <summary>Winners, flops, and the stats behind them.</summary>
public sealed record PerformanceSnapshot(
    IReadOnlyList<VideoPerformance> Winners,
    IReadOnlyList<VideoPerformance> Flops,
    IReadOnlyList<VideoPerformance> All,
    long MedianViews,
    DateTimeOffset FetchedUtc)
{
    /// <summary>Enough judgeable history to steer generation?</summary>
    public bool HasSignal => Winners.Count > 0;

    public static PerformanceSnapshot Empty { get; } = new(
        Array.Empty<VideoPerformance>(), Array.Empty<VideoPerformance>(),
        Array.Empty<VideoPerformance>(), 0, DateTimeOffset.MinValue);
}

public interface IChannelPerformanceService
{
    bool Configured { get; }

    /// <summary>Winners/flops for the channel, cached. Never throws — returns Empty on failure.</summary>
    Task<PerformanceSnapshot> GetAsync(bool forceRefresh = false, CancellationToken ct = default);
}

/// <summary>
/// Pulls the channel's real view counts with yt-dlp and turns them into "make more like THIS"
/// evidence for topic generation.
///
/// Two details make the output trustworthy:
///  - <b>Age floor</b> — videos younger than MinAgeDays are excluded, so a short posted this
///    morning is never scored as a flop for simply not having accumulated views yet.
///  - <b>Median, not mean</b> — a single 6k breakout would drag a mean far above the typical
///    video and make everything else look like a failure.
///
/// Counts are ROUNDED by YouTube on public listings (1,300 / 1,400 / 6,700), so small gaps are
/// meaningless; only the large multiples are treated as signal.
/// </summary>
public sealed class ChannelPerformanceService : IChannelPerformanceService
{
    private readonly PerformanceOptions _opt;
    private readonly JobStore _jobs;
    private readonly string _connString;
    private readonly ILogger<ChannelPerformanceService> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private PerformanceSnapshot? _cache;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public ChannelPerformanceService(IOptions<PerformanceOptions> opt, IOptions<PipelineOptions> pipe,
        JobStore jobs, IWebHostEnvironment env, ILogger<ChannelPerformanceService> log)
    {
        _opt = opt.Value; _jobs = jobs; _log = log;
        var dbPath = Path.IsPathRooted(pipe.Value.StateDbPath)
            ? pipe.Value.StateDbPath
            : Path.Combine(env.ContentRootPath, pipe.Value.StateDbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        Init();
    }

    public bool Configured => _opt.Enabled && !string.IsNullOrWhiteSpace(_opt.ChannelUrl);

    public async Task<PerformanceSnapshot> GetAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!Configured) return PerformanceSnapshot.Empty;

        if (!forceRefresh && _cache is { } cached &&
            DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromHours(_opt.CacheHours))
            return cached;

        await _lock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _cache is { } hit &&
                DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromHours(_opt.CacheHours))
                return hit;

            var fetched = await FetchAsync(ct);
            if (fetched.Count == 0)
            {
                // Fall back to whatever we stored last time so a yt-dlp hiccup doesn't
                // silently drop the channel back to un-steered topic generation.
                var stored = ReadStored();
                if (stored.Count == 0) return _cache ?? PerformanceSnapshot.Empty;
                _log.LogWarning("Live view-count fetch returned nothing; using {N} stored rows.", stored.Count);
                fetched = stored;
            }
            else
            {
                Store(fetched);
            }

            _cache = Build(fetched);
            _cachedAt = DateTimeOffset.UtcNow;

            _log.LogInformation(
                "Channel performance: {Total} videos, median {Median:N0} views, {Winners} winner(s), {Flops} flop(s).",
                _cache.All.Count, _cache.MedianViews, _cache.Winners.Count, _cache.Flops.Count);
            return _cache;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Channel performance fetch failed; topic generation continues unsteered.");
            return _cache ?? PerformanceSnapshot.Empty;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Raw rows straight from yt-dlp (id, views, upload date, title).</summary>
    private async Task<List<(string Id, long Views, DateTimeOffset? Published, string Title)>> FetchAsync(CancellationToken ct)
    {
        var channel = _opt.ChannelUrl.TrimEnd('/');
        var url = channel.Contains("/shorts") || channel.Contains("/videos")
            ? channel
            : $"{channel}/{(string.IsNullOrWhiteSpace(_opt.Tab) ? "shorts" : _opt.Tab)}";

        var args = new[]
        {
            "--flat-playlist", "--no-warnings", "--ignore-errors",
            "--print", "%(id)s\t%(view_count)s\t%(upload_date)s\t%(title)s",
            "--playlist-end", _opt.Count.ToString(),
            url,
        };

        var res = await ProcessRunner.RunAsync(_opt.YtDlpPath, args, workingDir: null,
            TimeSpan.FromSeconds(_opt.TimeoutSeconds), ct);

        var rows = new List<(string, long, DateTimeOffset?, string)>();
        foreach (var line in res.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split('\t');
            if (parts.Length < 4) continue;
            if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var views)) continue;

            DateTimeOffset? published = DateTime.TryParseExact(parts[2], "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
                ? new DateTimeOffset(d, TimeSpan.Zero)
                : null;

            rows.Add((parts[0].Trim(), views, published, string.Join("\t", parts.Skip(3)).Trim()));
        }

        if (rows.Count == 0)
            _log.LogWarning("yt-dlp returned no view data for {Url} (exit {Code}).", url, res.ExitCode);

        return rows;
    }

    /// <summary>Join to job topics, drop videos too young to judge, then rank by breakout multiple.</summary>
    private PerformanceSnapshot Build(List<(string Id, long Views, DateTimeOffset? Published, string Title)> rows)
    {
        // videoId -> (topic, when the job was created). The job date stands in for the upload
        // date when yt-dlp's flat listing omits it, which it often does.
        var byVideo = _jobs.LoadAll()
            .Where(j => !string.IsNullOrWhiteSpace(j.YouTubeVideoId))
            .GroupBy(j => j.YouTubeVideoId!)
            .ToDictionary(g => g.Key, g => g.First());

        var now = DateTimeOffset.UtcNow;
        var minAge = TimeSpan.FromDays(Math.Max(0, _opt.MinAgeDays));

        var judgeable = new List<(string Id, long Views, DateTimeOffset? Published, string Title, string? Topic)>();
        foreach (var r in rows)
        {
            byVideo.TryGetValue(r.Id, out var job);
            var published = r.Published ?? job?.CreatedAt;
            if (published is { } p && now - p < minAge) continue; // too young to judge
            judgeable.Add((r.Id, r.Views, published, r.Title, job?.Topic));
        }

        if (judgeable.Count == 0) return PerformanceSnapshot.Empty;

        var sortedViews = judgeable.Select(v => v.Views).OrderBy(v => v).ToList();
        long median = sortedViews[sortedViews.Count / 2];
        if (median <= 0) median = 1;

        var all = judgeable
            .Select(v => new VideoPerformance(v.Id, v.Title, v.Topic, v.Views, v.Published, (double)v.Views / median))
            .OrderByDescending(v => v.Views)
            .ToList();

        // Below the minimum sample the spread is mostly YouTube's impression roulette, not topic
        // quality — report the numbers but publish no winners, so nothing steers generation.
        if (all.Count < _opt.MinVideosForSignal)
        {
            _log.LogInformation(
                "Only {N} judgeable video(s) (need {Min}) — reporting performance but not steering topics.",
                all.Count, _opt.MinVideosForSignal);
            return new PerformanceSnapshot(Array.Empty<VideoPerformance>(), Array.Empty<VideoPerformance>(),
                all, median, DateTimeOffset.UtcNow);
        }

        var winners = all.Where(v => v.Multiple >= _opt.WinnerMultiple)
            .Take(Math.Max(0, _opt.WinnerCount)).ToList();

        var flops = all.Where(v => v.Multiple <= _opt.FlopMultiple)
            .OrderBy(v => v.Views)
            .Take(Math.Max(0, _opt.FlopCount)).ToList();

        return new PerformanceSnapshot(winners, flops, all, median, DateTimeOffset.UtcNow);
    }

    // --- persistence: keeps the last good snapshot across restarts / yt-dlp outages ---

    private void Store(List<(string Id, long Views, DateTimeOffset? Published, string Title)> rows)
    {
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            foreach (var r in rows)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    "INSERT INTO video_performance (video_id, views, published_utc, title, updated_utc) " +
                    "VALUES ($v, $n, $p, $t, $u) " +
                    "ON CONFLICT(video_id) DO UPDATE SET views = $n, published_utc = $p, title = $t, updated_utc = $u;";
                cmd.Parameters.AddWithValue("$v", r.Id);
                cmd.Parameters.AddWithValue("$n", r.Views);
                cmd.Parameters.AddWithValue("$p", (object?)r.Published?.ToString("o") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$t", r.Title);
                cmd.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not persist channel performance."); }
    }

    private List<(string Id, long Views, DateTimeOffset? Published, string Title)> ReadStored()
    {
        var rows = new List<(string, long, DateTimeOffset?, string)>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT video_id, views, published_utc, title FROM video_performance;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                DateTimeOffset? published = !r.IsDBNull(2) &&
                    DateTimeOffset.TryParse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
                    ? d : null;
                rows.Add((r.GetString(0), r.GetInt64(1), published, r.GetString(3)));
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not read stored channel performance."); }
        return rows;
    }

    private void Init()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE IF NOT EXISTS video_performance (" +
                "  video_id TEXT PRIMARY KEY," +
                "  views INTEGER NOT NULL," +
                "  published_utc TEXT," +
                "  title TEXT NOT NULL," +
                "  updated_utc TEXT NOT NULL);";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not init video_performance table."); }
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connString);
        c.Open();
        using var p = c.CreateCommand();
        p.CommandText = "PRAGMA busy_timeout = 4000;";
        p.ExecuteNonQuery();
        return c;
    }
}
