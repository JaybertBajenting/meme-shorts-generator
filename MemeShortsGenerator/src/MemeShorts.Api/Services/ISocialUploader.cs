namespace MemeShorts.Api.Services;

/// <summary>One finished short, resolved by the caller into everything a platform needs.</summary>
/// <param name="VideoPath">Absolute path to the rendered mp4.</param>
/// <param name="Title">Short human title (the YouTube title, minus the suffix).</param>
/// <param name="Description">Full description text, already including music attribution.</param>
/// <param name="ScheduledForUtc">
/// Desired go-live time, or null for "now". Platforms that can't schedule server-side ignore
/// this — <see cref="SocialPublishWorker"/> is what holds those back until the slot.
/// </param>
public sealed record SocialPost(
    string VideoPath,
    string Title,
    string Description,
    DateTimeOffset? ScheduledForUtc);

/// <param name="PostId">Platform id for the created post.</param>
/// <param name="PermalinkUrl">Link to the post, when the platform hands one back.</param>
/// <param name="ScheduledForUtc">When it goes live — echoes the request, or now if published.</param>
public sealed record SocialPostResult(string PostId, string? PermalinkUrl, DateTimeOffset? ScheduledForUtc);

/// <summary>
/// A non-YouTube publish destination. YouTube keeps its own <see cref="IYouTubeUploader"/>
/// because its OAuth/consent flow and Data API client look nothing like the Graph API ones.
/// </summary>
public interface ISocialUploader
{
    /// <summary>"facebook" | "instagram" — matches the per-platform state on the job.</summary>
    string Platform { get; }

    /// <summary>True when enabled AND the ids/token needed to call the API are present.</summary>
    bool Configured { get; }

    /// <summary>
    /// True when the platform can hold the post itself until a future time. False means the
    /// app must sit on it and call <see cref="PublishAsync"/> at the slot instead.
    /// </summary>
    bool SupportsNativeScheduling { get; }

    /// <summary>
    /// True when the app should hold this post back and publish it at the job's slot, rather
    /// than as soon as it's approved. Only consulted when the platform can't schedule itself;
    /// turning it off makes a batch of approvals all go live at once.
    /// </summary>
    bool DeferUntilSlot { get; }

    /// <summary>
    /// How late a slot missed while the app was down may still be published. Null = no limit
    /// (always publish late). Anything older is failed rather than posted at the wrong time.
    /// </summary>
    TimeSpan? MissedSlotGrace { get; }

    Task<SocialPostResult> PublishAsync(SocialPost post, CancellationToken ct = default);

    /// <summary>
    /// Confirm the configured id + token actually work, returning the account they resolve to
    /// (e.g. "Page: Meme Shorts (123…)"). Lets the setup be checked without posting anything.
    /// </summary>
    Task<string> VerifyAsync(CancellationToken ct = default);
}
