using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using MemeShorts.Api.Models;
using Microsoft.Extensions.Options;

namespace MemeShorts.Api.Services;

public sealed record UploadResult(string VideoId, DateTimeOffset PublishAtUtc);

public interface IYouTubeUploader
{
    /// <summary>True when uploading is enabled AND the client-secrets file exists.</summary>
    bool Configured { get; }

    /// <summary>Run ONLY the OAuth consent flow (no upload) — verifies test-user access and caches the token.</summary>
    Task AuthorizeAsync(CancellationToken ct = default);

    /// <summary>Upload a video as PRIVATE with a scheduled publishAt (auto-goes public then). Metadata is pre-resolved.</summary>
    Task<UploadResult> UploadAsync(string videoPath, string title, string description, string[]? tags, string categoryId,
        DateTimeOffset publishAtUtc, string? thumbnailPath = null, CancellationToken ct = default);
}

/// <summary>
/// Uploads a finished short to YouTube via the Data API v3. Sets privacyStatus=private +
/// publishAt, so YouTube keeps it private until the scheduled time then publishes it.
///
/// Auth is the OAuth "installed app" flow: first upload opens a browser for one-time consent;
/// the refresh token is then cached to disk and reused. Requires the user's own Google Cloud
/// OAuth client (see README). Not runnable without those credentials.
/// </summary>
public sealed class YouTubeUploadService : IYouTubeUploader
{
    private readonly UploadOptions _opt;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<YouTubeUploadService> _log;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private YouTubeService? _service;

    public YouTubeUploadService(IOptions<UploadOptions> opt, IWebHostEnvironment env, ILogger<YouTubeUploadService> log)
    {
        _opt = opt.Value; _env = env; _log = log;
    }

    private string SecretsPath => Resolve(_opt.ClientSecretsPath);
    private string TokenPath => Resolve(_opt.TokenStorePath);
    private string Resolve(string p) => Path.IsPathRooted(p) ? p : Path.Combine(_env.ContentRootPath, p);

    public bool Configured => _opt.Enabled && File.Exists(SecretsPath);

    public async Task AuthorizeAsync(CancellationToken ct = default)
    {
        if (!_opt.Enabled) throw new InvalidOperationException("Uploading is disabled (Upload:Enabled = false).");
        if (!File.Exists(SecretsPath)) throw new FileNotFoundException($"OAuth client secrets not found at {SecretsPath}.");

        // Force a clean consent: drop the cached service and any stored token (which may be
        // revoked/expired — testing-mode refresh tokens die after ~7 days). Otherwise the auth
        // broker silently reuses the dead token and the next upload fails with invalid_grant.
        await _authLock.WaitAsync(ct);
        try
        {
            _service = null;
            try { await new FileDataStore(TokenPath, fullPath: true).ClearAsync(); }
            catch (Exception ex) { _log.LogWarning(ex, "Could not clear cached YouTube token before re-consent."); }
        }
        finally { _authLock.Release(); }

        await GetServiceAsync(ct); // opens the browser for fresh consent; no upload
    }

    public async Task<UploadResult> UploadAsync(string videoPath, string title, string description, string[]? tags, string categoryId,
        DateTimeOffset publishAtUtc, string? thumbnailPath = null, CancellationToken ct = default)
    {
        if (!_opt.Enabled) throw new InvalidOperationException("Uploading is disabled (Upload:Enabled = false).");
        if (!File.Exists(SecretsPath)) throw new FileNotFoundException($"OAuth client secrets not found at {SecretsPath}.");
        if (!File.Exists(videoPath)) throw new FileNotFoundException($"Video not found: {videoPath}");

        var yt = await GetServiceAsync(ct);

        var video = new Video
        {
            Snippet = new VideoSnippet
            {
                Title = Trim(title, 100),
                Description = Trim(description, 4900),
                Tags = tags,
                CategoryId = string.IsNullOrWhiteSpace(categoryId) ? "24" : categoryId,
            },
            Status = new VideoStatus
            {
                PrivacyStatus = "private",
                PublishAtDateTimeOffset = publishAtUtc, // auto-publish at this time
                SelfDeclaredMadeForKids = false,
                // "Altered content" / AI-use disclosure. These are crude cartoon doodles, not
                // realistic synthetic media of real people/events, so declare "No" explicitly.
                ContainsSyntheticMedia = false,
            },
        };

        await using var fs = File.OpenRead(videoPath);
        var request = yt.Videos.Insert(video, "snippet,status", fs, "video/mp4");
        string? videoId = null;
        request.ResponseReceived += v => videoId = v.Id;

        var progress = await request.UploadAsync(ct);
        if (progress.Status != UploadStatus.Completed)
            throw new InvalidOperationException($"YouTube upload failed: {progress.Exception?.Message}", progress.Exception);

        _log.LogInformation("Uploaded video {Id}, publishAt {When:u}.", videoId, publishAtUtc);

        // Custom thumbnail (best-effort — needs a verified channel; never fail the upload over it).
        if (!string.IsNullOrWhiteSpace(videoId) && !string.IsNullOrWhiteSpace(thumbnailPath) && File.Exists(thumbnailPath))
        {
            try
            {
                await using var thumb = File.OpenRead(thumbnailPath);
                var set = yt.Thumbnails.Set(videoId, thumb, "image/jpeg");
                var tp = await set.UploadAsync(ct);
                if (tp.Status != UploadStatus.Completed)
                    _log.LogWarning("Thumbnail set failed: {Err}", tp.Exception?.Message);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Could not set custom thumbnail (channel may need verification)."); }
        }

        return new UploadResult(videoId ?? "", publishAtUtc);
    }

    private async Task<YouTubeService> GetServiceAsync(CancellationToken ct)
    {
        if (_service is not null) return _service;
        await _authLock.WaitAsync(ct);
        try
        {
            if (_service is not null) return _service;

            await using var stream = File.OpenRead(SecretsPath);
            var secrets = (await GoogleClientSecrets.FromStreamAsync(stream)).Secrets;

            // First run opens a browser for consent; the refresh token is cached in TokenPath.
            var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                secrets,
                new[] { YouTubeService.Scope.YoutubeUpload },
                "user",
                ct,
                new FileDataStore(TokenPath, fullPath: true));

            _service = new YouTubeService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "MemeShortsGenerator",
            });
            return _service;
        }
        finally { _authLock.Release(); }
    }

    private static string Trim(string s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max]);
}
