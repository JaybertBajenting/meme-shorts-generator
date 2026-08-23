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
    [JsonPropertyName("items")] public List<ListIdeaItem> Items { get; set; } = new();
}

public sealed class ListIdeaItem
{
    /// <summary>The spoken line.</summary>
    [JsonPropertyName("text")] public string Text { get; set; } = "";

    /// <summary>Plain words to search Wikimedia Commons for, e.g. "two pence coin obverse".</summary>
    [JsonPropertyName("image")] public string Image { get; set; } = "";
}
