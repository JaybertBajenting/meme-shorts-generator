using System.Text.Json;
using MemeShorts.Api.Models;
using Microsoft.Extensions.Options;

namespace MemeShorts.Api.Services;

/// <summary>
/// Posts a finished short to a Facebook Page as a Reel, using the Graph API's three-phase
/// video_reels flow:
///
///   1. start  — POST /{page-id}/video_reels?upload_phase=start  -> video_id + upload_url
///   2. upload — POST the mp4 bytes to that rupload.facebook.com URL
///   3. finish — POST /{page-id}/video_reels?upload_phase=finish with the description and
///               either PUBLISHED (now) or SCHEDULED (+ scheduled_publish_time).
///
/// Auth is a long-lived PAGE access token pasted into config — no runtime consent flow, so
/// unlike YouTube this never needs a browser. See secrets/README.txt for how to mint one.
/// </summary>
public sealed class FacebookReelsUploader : ISocialUploader
{
    /// <summary>Facebook rejects a scheduled time outside this window from now.</summary>
    private static readonly TimeSpan MinLead = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxLead = TimeSpan.FromDays(75);

    private readonly FacebookOptions _opt;
    private readonly MetaCredentialStore _creds;
    private readonly MetaGraphClient _graph;
    private readonly ILogger<FacebookReelsUploader> _log;

    public FacebookReelsUploader(IOptions<FacebookOptions> opt, MetaCredentialStore creds,
        MetaGraphClient graph, ILogger<FacebookReelsUploader> log)
    {
        _opt = opt.Value; _creds = creds; _graph = graph; _log = log;
    }

    public string Platform => "facebook";

    // Read live each time (not cached at construction) so credentials saved from the Settings
    // page take effect immediately, without restarting the app.
    private string PageId => _creds.Facebook.AccountId;
    private string Token => _creds.Facebook.Token;

    public bool Configured => _creds.Facebook.Usable;

    public bool SupportsNativeScheduling => true;

    // Facebook holds the post itself, so the app never waits on a slot for it.
    public bool DeferUntilSlot => false;
    public TimeSpan? MissedSlotGrace => null;

    public async Task<SocialPostResult> PublishAsync(SocialPost post, CancellationToken ct = default)
    {
        if (!Configured) throw new InvalidOperationException("Facebook publishing is not configured (Facebook:Enabled, PageId, PageAccessToken).");
        if (!File.Exists(post.VideoPath)) throw new FileNotFoundException($"Video not found: {post.VideoPath}");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(60, _opt.TimeoutSeconds)));
        var token = timeout.Token;

        // 1. start
        var start = await _graph.PostAsync(_opt.ApiVersion, $"{PageId}/video_reels",
            new Dictionary<string, string>
            {
                ["upload_phase"] = "start",
                ["access_token"] = Token,
            }, token);

        var videoId = Str(start, "video_id")
            ?? throw new MetaGraphException("Facebook did not return a video_id from the reel upload start phase.");
        var uploadUrl = Str(start, "upload_url")
            ?? throw new MetaGraphException("Facebook did not return an upload_url from the reel upload start phase.");

        // 2. upload
        await _graph.UploadFileAsync(uploadUrl, post.VideoPath, Token, token);

        // 3. finish
        var form = new Dictionary<string, string>
        {
            ["upload_phase"] = "finish",
            ["video_id"] = videoId,
            ["description"] = SocialCaption.Build(_opt.CaptionTemplate, post.Title, post.Description,
                _opt.Hashtags is { Length: > 0 } ? _opt.Hashtags : FacebookOptions.DefaultHashtags),
            ["access_token"] = Token,
        };

        var scheduledFor = ResolveSchedule(post.ScheduledForUtc);
        if (scheduledFor is { } when)
        {
            form["video_state"] = "SCHEDULED";
            form["scheduled_publish_time"] = when.ToUnixTimeSeconds().ToString();
        }
        else
        {
            form["video_state"] = "PUBLISHED";
        }

        await _graph.PostAsync(_opt.ApiVersion, $"{PageId}/video_reels", form, token);

        _log.LogInformation("Facebook reel {VideoId} {Disposition}.", videoId,
            scheduledFor is { } s ? $"scheduled for {s:u}" : "published");

        return new SocialPostResult(videoId, $"https://www.facebook.com/reel/{videoId}", scheduledFor ?? DateTimeOffset.UtcNow);
    }

    public async Task<string> VerifyAsync(CancellationToken ct = default)
    {
        if (!Configured) throw new InvalidOperationException("Facebook publishing is not configured (Facebook:Enabled, PageId, PageAccessToken).");

        var page = await _graph.GetAsync(_opt.ApiVersion, PageId,
            new Dictionary<string, string>
            {
                ["fields"] = "id,name",
                ["access_token"] = Token,
            }, ct);

        var name = Str(page, "name") ?? "(unnamed)";
        var id = Str(page, "id") ?? PageId;
        return $"Facebook Page: {name} ({id})";
    }

    /// <summary>
    /// Clamp the requested slot to what Facebook accepts. Too soon or too far out (or scheduling
    /// turned off) means publish now rather than fail the post over a timing technicality.
    /// </summary>
    private DateTimeOffset? ResolveSchedule(DateTimeOffset? requested)
    {
        if (!_opt.UseSchedule || requested is not { } when) return null;

        var lead = when - DateTimeOffset.UtcNow;
        if (lead < MinLead)
        {
            _log.LogInformation("Facebook slot {When:u} is under the 10-minute minimum lead; publishing now.", when);
            return null;
        }
        if (lead > MaxLead)
        {
            _log.LogWarning("Facebook slot {When:u} is beyond the 75-day limit; publishing now.", when);
            return null;
        }
        return when;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
