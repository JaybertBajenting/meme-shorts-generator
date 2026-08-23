using System.Text.Json;
using MemeShorts.Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace MemeShorts.Api.Services;

/// <summary>
/// Durable storage for generation jobs (same SQLite state.db). Without this, jobs live only in
/// memory, so a server restart / crash / session teardown loses everything — including a big
/// batch mid-render. Each job is upserted as a JSON blob keyed by id; the whole set is reloaded
/// on startup. Finished videos/thumbnails on disk (out/&lt;id&gt;/) are referenced by the restored
/// paths, so they play again immediately.
/// </summary>
public sealed class JobStore
{
    private readonly string _connString;
    private readonly ILogger<JobStore> _log;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public JobStore(IOptions<PipelineOptions> pipe, IWebHostEnvironment env, ILogger<JobStore> log)
    {
        _log = log;
        var dbPath = Path.IsPathRooted(pipe.Value.StateDbPath)
            ? pipe.Value.StateDbPath
            : Path.Combine(env.ContentRootPath, pipe.Value.StateDbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        Init();
    }

    /// <summary>Insert or update a job's persisted snapshot. Best-effort (never throws into the pipeline).</summary>
    public void Save(GenerationJob job)
    {
        try
        {
            var json = JsonSerializer.Serialize(PersistedJob.From(job), Json);
            lock (_lock)
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO jobs (id, created_utc, json) VALUES ($id, $c, $j) " +
                    "ON CONFLICT(id) DO UPDATE SET json = $j, created_utc = $c;";
                cmd.Parameters.AddWithValue("$id", job.Id);
                cmd.Parameters.AddWithValue("$c", job.CreatedAt.ToString("o"));
                cmd.Parameters.AddWithValue("$j", json);
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not persist job {Id}.", job.Id); }
    }

    /// <summary>Load every persisted job (newest first), rebuilt into live GenerationJob objects.</summary>
    public List<GenerationJob> LoadAll()
    {
        var jobs = new List<GenerationJob>();
        lock (_lock)
        {
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT json FROM jobs ORDER BY created_utc DESC;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    try
                    {
                        var p = JsonSerializer.Deserialize<PersistedJob>(r.GetString(0), Json);
                        if (p is not null) jobs.Add(p.ToJob());
                    }
                    catch (Exception ex) { _log.LogWarning(ex, "Skipping a corrupt persisted job row."); }
                }
            }
            catch (Exception ex) { _log.LogWarning(ex, "Could not load persisted jobs."); }
        }
        return jobs;
    }

    /// <summary>Remove a job snapshot after the user cancels and deletes an unfinished job.</summary>
    public void Delete(string id)
    {
        lock (_lock)
        {
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM jobs WHERE id = $id;";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { _log.LogWarning(ex, "Could not delete job {Id}.", id); }
        }
    }

    /// <summary>Read a persisted app setting (durable across restarts). Null if unset.</summary>
    public string? GetSetting(string key)
    {
        lock (_lock)
        {
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT value FROM settings WHERE key = $k;";
                cmd.Parameters.AddWithValue("$k", key);
                return cmd.ExecuteScalar() as string;
            }
            catch (Exception ex) { _log.LogWarning(ex, "GetSetting({Key}) failed.", key); return null; }
        }
    }

    /// <summary>Persist an app setting.</summary>
    public void SetSetting(string key, string value)
    {
        lock (_lock)
        {
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO settings (key, value) VALUES ($k, $v) " +
                                  "ON CONFLICT(key) DO UPDATE SET value = $v;";
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$v", value);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { _log.LogWarning(ex, "SetSetting({Key}) failed.", key); }
        }
    }

    /// <summary>Remove a persisted setting so its configured default applies again.</summary>
    public void DeleteSetting(string key)
    {
        lock (_lock)
        {
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM settings WHERE key = $k;";
                cmd.Parameters.AddWithValue("$k", key);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { _log.LogWarning(ex, "DeleteSetting({Key}) failed.", key); }
        }
    }

    private void Init()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE IF NOT EXISTS jobs (" +
                "  id TEXT PRIMARY KEY," +
                "  created_utc TEXT NOT NULL," +
                "  json TEXT NOT NULL);" +
                "CREATE TABLE IF NOT EXISTS settings (" +
                "  key TEXT PRIMARY KEY," +
                "  value TEXT NOT NULL);";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not init jobs/settings tables."); }
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

    /// <summary>Flat, serialization-friendly mirror of a job (GenerationJob hides some fields with [JsonIgnore]).</summary>
    private sealed class PersistedJob
    {
        public string Id { get; set; } = "";
        public string Topic { get; set; } = "";
        public string? Format { get; set; }
        public string ChannelId { get; set; } = "default";
        public bool AutoGenerated { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string State { get; set; } = "Queued";
        public string Stage { get; set; } = "queued";
        public int Percent { get; set; }
        public string? Error { get; set; }
        public string? VideoPath { get; set; }
        public string OutputDir { get; set; } = "";
        public string? ThumbnailPath { get; set; }
        public string? MusicAttribution { get; set; }
        public Storyboard? Storyboard { get; set; }
        public string? YouTubeTitle { get; set; }
        public string? YouTubeDescription { get; set; }
        public string[]? YouTubeTags { get; set; }
        public string? YouTubeCategoryId { get; set; }
        public string Review { get; set; } = "None";
        public DateTimeOffset? ReviewedAt { get; set; }
        public string Upload { get; set; } = "None";
        public string? YouTubeVideoId { get; set; }
        public DateTimeOffset? PublishAtUtc { get; set; }
        public string? UploadError { get; set; }
        // Null on rows written before Facebook/Instagram existed — ToJob() defaults them.
        public PersistedPlatform? Facebook { get; set; }
        public PersistedPlatform? Instagram { get; set; }

        public static PersistedJob From(GenerationJob j) => new()
        {
            Id = j.Id, Topic = j.Topic, Format = j.Format, ChannelId = j.ChannelId,
            AutoGenerated = j.AutoGenerated, CreatedAt = j.CreatedAt,
            State = j.State.ToString(), Stage = j.Stage, Percent = j.Percent, Error = j.Error,
            VideoPath = j.VideoPath, OutputDir = j.OutputDir, ThumbnailPath = j.ThumbnailPath,
            MusicAttribution = j.MusicAttribution, Storyboard = j.Storyboard,
            YouTubeTitle = j.YouTubeTitle, YouTubeDescription = j.YouTubeDescription,
            YouTubeTags = j.YouTubeTags, YouTubeCategoryId = j.YouTubeCategoryId,
            Review = j.Review.ToString(), ReviewedAt = j.ReviewedAt,
            Upload = j.Upload.ToString(), YouTubeVideoId = j.YouTubeVideoId,
            PublishAtUtc = j.PublishAtUtc, UploadError = j.UploadError,
            Facebook = PersistedPlatform.From(j.Facebook),
            Instagram = PersistedPlatform.From(j.Instagram),
        };

        public GenerationJob ToJob()
        {
            var j = new GenerationJob
            {
                Id = Id, Topic = Topic, Format = Format, ChannelId = ChannelId,
                AutoGenerated = AutoGenerated, CreatedAt = CreatedAt,
            };
            j.State = Enum.TryParse<JobState>(State, out var s) ? s : JobState.Queued;
            j.Stage = Stage;
            j.Percent = Percent;
            j.Error = Error;
            j.VideoPath = VideoPath;
            j.OutputDir = OutputDir;
            j.ThumbnailPath = ThumbnailPath;
            j.MusicAttribution = MusicAttribution;
            j.Storyboard = Storyboard;
            j.YouTubeTitle = YouTubeTitle;
            j.YouTubeDescription = YouTubeDescription;
            j.YouTubeTags = YouTubeTags;
            j.YouTubeCategoryId = YouTubeCategoryId;
            j.Review = Enum.TryParse<ReviewState>(Review, out var rv) ? rv : ReviewState.None;
            j.ReviewedAt = ReviewedAt;
            j.Upload = Enum.TryParse<UploadState>(Upload, out var up) ? up : UploadState.None;
            j.YouTubeVideoId = YouTubeVideoId;
            j.PublishAtUtc = PublishAtUtc;
            j.UploadError = UploadError;
            j.Facebook = Facebook?.ToPlatform() ?? new PlatformPublish();
            j.Instagram = Instagram?.ToPlatform() ?? new PlatformPublish();
            return j;
        }
    }

    /// <summary>Serialization mirror of <see cref="PlatformPublish"/> (enum stored as its name).</summary>
    private sealed class PersistedPlatform
    {
        public string State { get; set; } = "None";
        public string? PostId { get; set; }
        public string? PermalinkUrl { get; set; }
        public string? Error { get; set; }
        public DateTimeOffset? PublishAtUtc { get; set; }

        public static PersistedPlatform From(PlatformPublish p) => new()
        {
            State = p.State.ToString(), PostId = p.PostId, PermalinkUrl = p.PermalinkUrl,
            Error = p.Error, PublishAtUtc = p.PublishAtUtc,
        };

        public PlatformPublish ToPlatform() => new()
        {
            State = Enum.TryParse<UploadState>(State, out var s) ? s : UploadState.None,
            PostId = PostId, PermalinkUrl = PermalinkUrl, Error = Error, PublishAtUtc = PublishAtUtc,
        };
    }
}
