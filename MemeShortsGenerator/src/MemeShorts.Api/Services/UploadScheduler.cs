using System.Globalization;
using MemeShorts.Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace MemeShorts.Api.Services;

/// <summary>
/// Assigns each uploaded short a "go public" time (publishAt). Slots are the configured
/// daily times (e.g. 09:00 / 13:00 / 17:00). Each new slot is the first one strictly AFTER
/// the previously reserved slot — so the schedule chains forward (3/day) regardless of when
/// uploads actually happen. Reservations persist in SQLite, surviving restarts.
/// </summary>
public sealed class UploadScheduler
{
    private readonly UploadOptions _opt;
    private readonly string _connString;
    private readonly ILogger<UploadScheduler> _log;
    private readonly object _lock = new();

    public UploadScheduler(IOptions<UploadOptions> opt, IOptions<PipelineOptions> pipe, IWebHostEnvironment env, ILogger<UploadScheduler> log)
    {
        _opt = opt.Value;
        _log = log;
        var dbPath = Path.IsPathRooted(pipe.Value.StateDbPath)
            ? pipe.Value.StateDbPath
            : Path.Combine(env.ContentRootPath, pipe.Value.StateDbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        Init();
    }

    private TimeZoneInfo Tz =>
        string.IsNullOrWhiteSpace(_opt.TimeZoneId) ? TimeZoneInfo.Local : ResolveTz(_opt.TimeZoneId);

    /// <summary>Reserve and persist the next publish slot for a job. Returns the UTC publish time.</summary>
    public DateTimeOffset ReserveNextSlot(string jobId)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            var last = ReadLastSlotUtc();
            // Chain off the later of "now" and the previously reserved slot.
            var anchor = last is { } l && l > now ? l : now;
            var slot = NextSlotAfter(anchor);
            WriteSlot(jobId, slot);
            _log.LogInformation("Reserved publish slot {Slot:u} for job {Job}.", slot, jobId);
            return slot;
        }
    }

    /// <summary>Release a reservation (e.g. the upload failed) so its slot isn't wasted.</summary>
    public void ReleaseSlot(string jobId)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM publish_schedule WHERE job_id = $j;";
            cmd.Parameters.AddWithValue("$j", jobId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Preview the next N upcoming reserved+future slots (for the UI).</summary>
    public IReadOnlyList<DateTimeOffset> Upcoming(int take = 10)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT publish_at_utc FROM publish_schedule WHERE publish_at_utc >= $now ORDER BY publish_at_utc ASC LIMIT $n;";
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$n", take);
            var list = new List<DateTimeOffset>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (DateTimeOffset.TryParse(r.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d))
                    list.Add(d);
            return list;
        }
    }

    /// <summary>The earliest configured daily slot strictly after <paramref name="afterUtc"/>.</summary>
    private DateTimeOffset NextSlotAfter(DateTimeOffset afterUtc)
    {
        var tz = Tz;
        var times = ParseTimes(_opt.PublishTimesLocal);
        var localAfter = TimeZoneInfo.ConvertTime(afterUtc, tz);

        for (int day = 0; day <= 31; day++)
        {
            var date = localAfter.Date.AddDays(day);
            foreach (var t in times)
            {
                var localSlot = new DateTime(date.Year, date.Month, date.Day, t.Hours, t.Minutes, 0, DateTimeKind.Unspecified);
                var slotUtc = new DateTimeOffset(localSlot, tz.GetUtcOffset(localSlot)).ToUniversalTime();
                if (slotUtc > afterUtc) return slotUtc;
            }
        }
        // Should never happen with valid times; fall back to +1h.
        return afterUtc.AddHours(1);
    }

    private static List<TimeSpan> ParseTimes(string[] times)
    {
        var src = times is { Length: > 0 } ? times : UploadOptions.DefaultPublishTimes;
        var parsed = src
            .Select(s => TimeSpan.TryParseExact(s.Trim(), "hh\\:mm", CultureInfo.InvariantCulture, out var ts) ? ts : (TimeSpan?)null)
            .Where(ts => ts.HasValue).Select(ts => ts!.Value)
            .OrderBy(ts => ts).ToList();
        if (parsed.Count == 0) parsed.Add(new TimeSpan(9, 0, 0));
        return parsed;
    }

    private static TimeZoneInfo ResolveTz(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Local; }
    }

    private DateTimeOffset? ReadLastSlotUtc()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(publish_at_utc) FROM publish_schedule;";
        var v = cmd.ExecuteScalar();
        if (v is string s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d))
            return d;
        return null;
    }

    private void WriteSlot(string jobId, DateTimeOffset slotUtc)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO publish_schedule (job_id, publish_at_utc, created_utc) VALUES ($j, $p, $c);";
        cmd.Parameters.AddWithValue("$j", jobId);
        cmd.Parameters.AddWithValue("$p", slotUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private void Init()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE IF NOT EXISTS publish_schedule (" +
                "  id INTEGER PRIMARY KEY AUTOINCREMENT," +
                "  job_id TEXT NOT NULL," +
                "  publish_at_utc TEXT NOT NULL," +
                "  created_utc TEXT NOT NULL);";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not init publish_schedule table."); }
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
