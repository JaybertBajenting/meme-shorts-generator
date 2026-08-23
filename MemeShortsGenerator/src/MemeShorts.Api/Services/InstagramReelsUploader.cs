using System.Text.Json;
using MemeShorts.Api.Models;
using Microsoft.Extensions.Options;

namespace MemeShorts.Api.Services;

/// <summary>
/// Posts a finished short to Instagram as a Reel via the Graph Content Publishing API:
///
///   1. container — POST /{ig-user-id}/media?media_type=REELS&amp;upload_type=resumable
///                  -> container id + rupload URI
///   2. upload    — POST the mp4 bytes to that URI
///   3. poll      — GET /{container-id}?fields=status_code until FINISHED (IG transcodes first)
///   4. publish   — POST /{ig-user-id}/media_publish?creation_id={container-id}
///
/// The resumable path matters here: the classic flow wants a public <c>video_url</c> that
/// Instagram fetches, which a machine rendering to a local disk can't provide.
///
/// NOTE: there is no scheduling in this API — step 4 goes live immediately. Holding a reel
/// until its slot is <see cref="SocialPublishWorker"/>'s job, and the whole sequence runs then.
/// Requires an Instagram Business/Creator account linked to a Facebook Page.
/// </summary>
public sealed class InstagramReelsUploader : ISocialUploader
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly InstagramOptions _opt;
    private readonly MetaCredentialStore _creds;
    private readonly MetaGraphClient _graph;
    private readonly ILogger<InstagramReelsUploader> _log;

    public InstagramReelsUploader(IOptions<InstagramOptions> opt, MetaCredentialStore creds,
        MetaGraphClient graph, ILogger<InstagramReelsUploader> log)
    {
        _opt = opt.Value; _creds = creds; _graph = graph; _log = log;
    }

    public string Platform => "instagram";

    // Read live so Settings-page changes apply without a restart.
    private string IgUserId => _creds.Instagram.AccountId;
    private string Token => _creds.Instagram.Token;

    public bool Configured => _creds.Instagram.Usable;

    /// <summary>Instagram's API publishes on call — it cannot hold a post for later.</summary>
    public bool SupportsNativeScheduling => false;

    public bool DeferUntilSlot => _opt.UseSchedule;

    public TimeSpan? MissedSlotGrace =>
        _opt.SkipIfMissedByMinutes <= 0 ? null : TimeSpan.FromMinutes(_opt.SkipIfMissedByMinutes);

    public async Task<SocialPostResult> PublishAsync(SocialPost post, CancellationToken ct = default)
    {
        if (!Configured) throw new InvalidOperationException("Instagram publishing is not configured (Instagram:Enabled, IgUserId, AccessToken).");
        if (!File.Exists(post.VideoPath)) throw new FileNotFoundException($"Video not found: {post.VideoPath}");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(120, _opt.TimeoutSeconds + _opt.ProcessingTimeoutSeconds)));
        var token = timeout.Token;

        // 1. container
        var caption = SocialCaption.Build(_opt.CaptionTemplate, post.Title, post.Description,
            _opt.Hashtags is { Length: > 0 } ? _opt.Hashtags : InstagramOptions.DefaultHashtags);

        var container = await _graph.PostAsync(_opt.ApiVersion, $"{IgUserId}/media",
            new Dictionary<string, string>
            {
                ["media_type"] = "REELS",
                ["upload_type"] = "resumable",
                ["caption"] = caption,
                ["share_to_feed"] = _opt.ShareToFeed ? "true" : "false",
                ["access_token"] = Token,
            }, token);

        var containerId = Str(container, "id")
            ?? throw new MetaGraphException("Instagram did not return a media container id.");
        var uploadUri = Str(container, "uri") ?? Str(container, "upload_uri")
            ?? throw new MetaGraphException("Instagram did not return a resumable upload URI. Confirm the account is a Business/Creator account linked to a Page.");

        // 2. upload
        await _graph.UploadFileAsync(uploadUri, post.VideoPath, Token, token);

        // 3. poll — publishing before transcoding finishes fails with a misleading error
        await WaitForContainerAsync(containerId, token);

        // 4. publish
        var published = await _graph.PostAsync(_opt.ApiVersion, $"{IgUserId}/media_publish",
            new Dictionary<string, string>
            {
                ["creation_id"] = containerId,
                ["access_token"] = Token,
            }, token);

        var mediaId = Str(published, "id") ?? containerId;
        var permalink = await TryGetPermalinkAsync(mediaId, token);

        _log.LogInformation("Instagram reel {MediaId} published.", mediaId);
        return new SocialPostResult(mediaId, permalink, DateTimeOffset.UtcNow);
    }

    public async Task<string> VerifyAsync(CancellationToken ct = default)
    {
        if (!Configured) throw new InvalidOperationException("Instagram publishing is not configured (Instagram:Enabled, IgUserId, AccessToken).");

        var me = await _graph.GetAsync(_opt.ApiVersion, IgUserId,
            new Dictionary<string, string>
            {
                ["fields"] = "id,username",
                ["access_token"] = Token,
            }, ct);

        var username = Str(me, "username");
        var id = Str(me, "id") ?? IgUserId;
        return username is { Length: > 0 }
            ? $"Instagram: @{username} ({id})"
            // No username field means the id resolved to something that isn't an IG Business account.
            : $"Instagram id {id} responded, but returned no username — check it's the Instagram Business account id, not the Page id.";
    }

    /// <summary>Block until Instagram has transcoded the upload, or give up with its reason.</summary>
    private async Task WaitForContainerAsync(string containerId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, _opt.ProcessingTimeoutSeconds));

        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await _graph.GetAsync(_opt.ApiVersion, containerId,
                new Dictionary<string, string>
                {
                    ["fields"] = "status_code,status",
                    ["access_token"] = Token,
                }, ct);

            var code = Str(status, "status_code") ?? "IN_PROGRESS";
            switch (code)
            {
                case "FINISHED":
                    return;
                case "ERROR":
                    throw new MetaGraphException($"Instagram could not process the video: {Str(status, "status") ?? "no detail given"}");
                case "EXPIRED":
                    throw new MetaGraphException("The Instagram media container expired before it could be published (containers last 24 hours).");
                default:
                    await Task.Delay(PollInterval, ct);
                    break;
            }
        }

        throw new TimeoutException(
            $"Instagram was still processing the reel after {_opt.ProcessingTimeoutSeconds}s (Instagram:ProcessingTimeoutSeconds).");
    }

    /// <summary>Best-effort permalink for the UI — never fail a published reel over it.</summary>
    private async Task<string?> TryGetPermalinkAsync(string mediaId, CancellationToken ct)
    {
        try
        {
            var media = await _graph.GetAsync(_opt.ApiVersion, mediaId,
                new Dictionary<string, string>
                {
                    ["fields"] = "permalink",
                    ["access_token"] = Token,
                }, ct);
            return Str(media, "permalink");
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not read the Instagram permalink for {MediaId}.", mediaId);
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
