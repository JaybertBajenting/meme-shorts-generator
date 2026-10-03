using MemeShorts.Api.Models;

namespace MemeShorts.Api.Services;

public interface ICodexClient
{
    /// <summary>Ask Codex for a storyboard. Returns null if the CLI fails or returns no parseable JSON.</summary>
    Task<Storyboard?> GenerateStoryboardAsync(string topic, string? format, IReadOnlyList<string>? styleTitles = null, string contentType = "meme", CancellationToken ct = default);

    /// <summary>Ask Codex to brainstorm <paramref name="count"/> fresh video topics, avoiding any in
    /// <paramref name="avoidTopics"/> (already made). Pass <paramref name="performance"/> to steer
    /// toward what actually earned views on this channel. Empty list if the CLI fails.</summary>
    Task<IReadOnlyList<string>> BrainstormTopicsAsync(int count, string contentType, IReadOnlyList<string>? styleTitles = null,
        IReadOnlyList<string>? avoidTopics = null, PerformanceSnapshot? performance = null, CancellationToken ct = default);

    /// <summary>Ask Codex to render one panel PNG to <paramref name="outputPngPath"/>. Returns true on success.</summary>
    Task<bool> GeneratePanelAsync(Beat beat, int width, int height, string outputPngPath, CancellationToken ct = default);

    /// <summary>
    /// Draw a plain picture of <paramref name="subject"/> — the fallback for when a photo
    /// archive has nothing. Returns false if nothing was produced.
    /// </summary>
    /// <param name="isolated">True: one object on plain white, to be cut out onto a white sticker.
    /// False: a full-frame photograph of the scene.</param>
    Task<bool> GenerateStockImageAsync(string subject, string outputPngPath, CancellationToken ct = default,
        bool isolated = true);

    /// <summary>
    /// Title a reposted post in the reference channel's voice. Reposts have no storyboard to
    /// take a title from, and the post's own first line is rarely a hook. Null if the CLI fails,
    /// so the caller can fall back rather than lose the video.
    /// </summary>
    Task<string?> GenerateRepostTitleAsync(string postText, IReadOnlyList<string>? styleTitles = null,
        CancellationToken ct = default);

    /// <summary>
    /// Invent a list short: a title, and a run of lines each paired with a plain-words search
    /// term for the picture that should be on screen while it is read.
    ///
    /// <paramref name="winningTitles"/> are the best-performing titles from the reference
    /// channel — used to aim at the SUBJECTS that work in this niche (word oddities, puns,
    /// object trivia), not to rewrite any particular video. <paramref name="history"/> is this
    /// channel's own past - latest scripts and titles, every subject covered - so the new one does
    /// not repeat them. Null if the CLI fails.
    /// </summary>
    Task<ListIdea?> GenerateListIdeaAsync(IReadOnlyList<string> winningTitles, string? seedTopic = null,
        IReadOnlyList<string>? avoid = null, CancellationToken ct = default,
        ListHistory? history = null);
}
