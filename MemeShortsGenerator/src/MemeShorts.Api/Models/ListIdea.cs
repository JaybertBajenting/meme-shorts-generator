using System.Text.Json.Serialization;

namespace MemeShorts.Api.Models;

/// <summary>
/// A list short invented by Codex: the title, and a run of lines each carrying the search term
/// for the picture that should be on screen while it is read.
///
/// The search term is the whole reason this is a separate shape from a plain script — the
/// format is one picture per line, so the words and the picture have to be decided together.
/// </summary>
public sealed class ListIdea
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";

    /// <summary>
    /// What it is about in a few plain words. Titles hide the subject ("the clingiest AI keychain"),
    /// so remembering only titles let two drafts in a row pick the same product.
    /// </summary>
    [JsonPropertyName("subject")] public string? Subject { get; set; }
    [JsonPropertyName("items")] public List<ListIdeaItem> Items { get; set; } = new();

    /// <summary>
    /// For a script about something recent, the web page its facts came from - kept so the facts
    /// can be checked before the video is approved. Empty for evergreen subjects.
    /// </summary>
    [JsonPropertyName("source")] public string? Source { get; set; }
}

public sealed class ListIdeaItem
{
    /// <summary>The spoken line.</summary>
    [JsonPropertyName("text")] public string Text { get; set; } = "";

    /// <summary>Plain words to search Wikimedia Commons for, e.g. "two pence coin obverse".</summary>
    [JsonPropertyName("image")] public string Image { get; set; } = "";

    /// <summary>
    /// What kind of picture this is, which decides where it comes from - see
    /// <see cref="Services.PictureKind"/>. "object", "scene" or "real"; blank means the old
    /// Commons-first order.
    /// </summary>
    [JsonPropertyName("kind")] public string? Kind { get; set; }
}
