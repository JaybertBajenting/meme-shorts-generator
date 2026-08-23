using MemeShorts.Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace MemeShorts.Api.Services;

/// <summary>
/// Remembers recently-used background start offsets (per source clip) so consecutive
/// renders don't grab overlapping footage. Backed by SQLite, so the history SURVIVES
/// server restarts. An in-memory cache fronts the DB for fast picking; every pick is
/// written through to disk.
/// </summary>
public sealed class BackgroundHistory
{
    private const int Keep = 8; // remember the last N offsets per clip
    private readonly object _lock = new();
    private readonly Dictionary<string, List<double>> _recent = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _connString;
    private readonly ILogger<BackgroundHistory> _log;

    public BackgroundHistory(IOptions<PipelineOptions> pipe, IWebHostEnvironment env, ILogger<BackgroundHistory> log)
    {
        _log = log;
        var dbPath = Path.IsPathRooted(pipe.Value.StateDbPath)
            ? pipe.Value.StateDbPath
            : Path.Combine(env.ContentRootPath, pipe.Value.StateDbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();

        InitAndLoad();
    }

    /// <summary>
    /// Pick a start offset in [0, maxStart] at least <paramref name="minGap"/> seconds away from
    /// this clip's recent picks (best-effort if the clip is too short). Records the choice.
    /// </summary>
    public double PickSpacedOffset(string clip, double maxStart, double minGap)
    {
        if (maxStart <= 0) return 0;

        lock (_lock)
        {
            if (!_recent.TryGetValue(clip, out var recent))
                _recent[clip] = recent = new List<double>();

            double chosen = -1;
            for (int attempt = 0; attempt < 40; attempt++)
            {
                double cand = Random.Shared.NextDouble() * maxStart;
                if (recent.TrueForAll(r => Math.Abs(cand - r) >= minGap)) { chosen = cand; break; }
            }

            // Clip too short to honor minGap: pick the MOST-separated spot from a batch.
            if (chosen < 0)
            {
                double bestDist = -1;
                for (int k = 0; k < 64; k++)
                {
                    double cand = Random.Shared.NextDouble() * maxStart;
                    double dist = recent.Count == 0 ? maxStart : recent.Min(r => Math.Abs(cand - r));
                    if (dist > bestDist) { bestDist = dist; chosen = cand; }
                }
            }

            recent.Add(chosen);
            if (recent.Count > Keep) recent.RemoveRange(0, recent.Count - Keep);
            Persist(clip, chosen);
            return chosen;
        }
    }

    private void InitAndLoad()
    {
        try
        {
            using var conn = Open();
            using (var create = conn.CreateCommand())
            {
                create.CommandText =
                    "CREATE TABLE IF NOT EXISTS bg_offsets (" +
                    "  id INTEGER PRIMARY KEY AUTOINCREMENT," +
                    "  clip TEXT NOT NULL," +
                    "  offset_sec REAL NOT NULL," +
                    "  created_utc TEXT NOT NULL);";
                create.ExecuteNonQuery();
            }

            using var sel = conn.CreateCommand();
            sel.CommandText = "SELECT clip, offset_sec FROM bg_offsets ORDER BY id ASC;";
            using var reader = sel.ExecuteReader();
            while (reader.Read())
            {
                var clip = reader.GetString(0);
                var offset = reader.GetDouble(1);
                if (!_recent.TryGetValue(clip, out var list))
                    _recent[clip] = list = new List<double>();
                list.Add(offset);
                if (list.Count > Keep) list.RemoveRange(0, list.Count - Keep);
            }
            _log.LogInformation("Background history loaded ({Clips} clip(s)).", _recent.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not init background history DB; continuing in-memory only.");
        }
    }

    private void Persist(string clip, double offset)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            // Insert the new pick, then prune to the last Keep rows for this clip.
            cmd.CommandText =
                "INSERT INTO bg_offsets (clip, offset_sec, created_utc) VALUES ($clip, $off, $ts);" +
                "DELETE FROM bg_offsets WHERE clip = $clip AND id NOT IN (" +
                "  SELECT id FROM bg_offsets WHERE clip = $clip ORDER BY id DESC LIMIT $keep);";
            cmd.Parameters.AddWithValue("$clip", clip);
            cmd.Parameters.AddWithValue("$off", offset);
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$keep", Keep);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not persist background offset; kept in memory.");
        }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connString);
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout = 4000;"; // tolerate brief concurrent writers
            pragma.ExecuteNonQuery();
        }
        return conn;
    }
}
