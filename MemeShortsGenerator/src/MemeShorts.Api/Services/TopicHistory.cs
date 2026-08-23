using System.Text.RegularExpressions;
using MemeShorts.Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace MemeShorts.Api.Services;

/// <summary>
/// Durable record of every topic that's been generated, so the same short is never made twice.
/// Backed by SQLite (same state.db as the other durable state), keyed on a normalized form of the
/// topic so trivial variations ("Microwaving Food!" vs "microwaving food") collide. Survives
/// restarts. A topic is reserved when a job is created and released if that job fails.
/// </summary>
public sealed class TopicHistory
{
    private readonly string _connString;
    private readonly ILogger<TopicHistory> _log;
    private readonly object _lock = new();

    public TopicHistory(IOptions<PipelineOptions> pipe, IWebHostEnvironment env, ILogger<TopicHistory> log)
    {
        _log = log;
        var dbPath = Path.IsPathRooted(pipe.Value.StateDbPath)
            ? pipe.Value.StateDbPath
            : Path.Combine(env.ContentRootPath, pipe.Value.StateDbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        Init();
    }

    /// <summary>Collapse to a comparable key: lowercase, trimmed, single-spaced, punctuation-stripped.</summary>
    public static string Normalize(string topic)
    {
        var s = (topic ?? "").Trim().ToLowerInvariant();
        s = Regex.Replace(s, @"[^\p{L}\p{Nd}\s]", " "); // drop punctuation/symbols
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    /// <summary>True if a topic with this normalized form has already been recorded.</summary>
    public bool Exists(string topic)
    {
        var norm = Normalize(topic);
        if (norm.Length == 0) return false;
        lock (_lock)
        {
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1 FROM topics WHERE norm = $n LIMIT 1;";
                cmd.Parameters.AddWithValue("$n", norm);
                return cmd.ExecuteScalar() is not null;
            }
            catch (Exception ex) { _log.LogWarning(ex, "Topic exists-check failed; treating as new."); return false; }
        }
    }

    /// <summary>
    /// Record a topic as taken. Returns true if it was newly reserved, false if it already existed
    /// (duplicate). Atomic via INSERT OR IGNORE so concurrent batch requests can't double-book.
    /// </summary>
    public bool TryReserve(string topic, string jobId)
    {
        var norm = Normalize(topic);
        if (norm.Length == 0) return true;
        lock (_lock)
        {
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT OR IGNORE INTO topics (norm, topic, job_id, created_utc) VALUES ($n, $t, $j, $c);";
                cmd.Parameters.AddWithValue("$n", norm);
                cmd.Parameters.AddWithValue("$t", topic);
                cmd.Parameters.AddWithValue("$j", jobId);
                cmd.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToString("o"));
                return cmd.ExecuteNonQuery() > 0; // rows affected == 0 -> was a duplicate
            }
            catch (Exception ex) { _log.LogWarning(ex, "Topic reserve failed for '{Topic}'.", topic); return true; }
        }
    }

    /// <summary>Free the topic reserved by a job (e.g. its generation failed), so it can be retried.</summary>
    public void Release(string jobId)
    {
        lock (_lock)
        {
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM topics WHERE job_id = $j;";
                cmd.Parameters.AddWithValue("$j", jobId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { _log.LogWarning(ex, "Topic release failed for job {Job}.", jobId); }
        }
    }

    /// <summary>The most recently recorded topics (original text), newest first — to steer Codex away from repeats.</summary>
    public IReadOnlyList<string> Recent(int take = 40)
    {
        lock (_lock)
        {
            var list = new List<string>();
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT topic FROM topics ORDER BY id DESC LIMIT $n;";
                cmd.Parameters.AddWithValue("$n", take);
                using var r = cmd.ExecuteReader();
                while (r.Read()) list.Add(r.GetString(0));
            }
            catch (Exception ex) { _log.LogWarning(ex, "Recent-topics read failed."); }
            return list;
        }
    }

    private void Init()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE IF NOT EXISTS topics (" +
                "  id INTEGER PRIMARY KEY AUTOINCREMENT," +
                "  norm TEXT NOT NULL UNIQUE," +
                "  topic TEXT NOT NULL," +
                "  job_id TEXT," +
                "  created_utc TEXT NOT NULL);";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not init topics table."); }
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
