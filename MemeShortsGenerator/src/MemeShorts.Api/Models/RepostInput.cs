using System.Text.Json.Serialization;

namespace MemeShorts.Api.Models;

/// <summary>
/// A found post supplied by hand: the picture plus the words to read out.
///
/// Persisted as repost.json INSIDE the job directory rather than on the job record, so it
/// survives a restart and a re-voice for free, and the uploaded image sits next to it.
/// </summary>
public sealed class RepostInput
{
    /// <summary>Post text, read verbatim and revealed one wrapped line at a time. May be empty.</summary>
    [JsonPropertyName("text")] public string Text { get; set; } = "";

    /// <summary>Uploaded image filename inside the job dir (e.g. "post_image.png"), or null.</summary>
    [JsonPropertyName("image")] public string? Image { get; set; }

    /// <summary>YouTube title. Falls back to the first line of the post when blank.</summary>
    [JsonPropertyName("title")] public string? Title { get; set; }

    // Optional social-post chrome. All null = a bare image on the card, which is the default:
    // a Reddit meme has no author header worth inventing.
    [JsonPropertyName("authorName")] public string? AuthorName { get; set; }
    [JsonPropertyName("headline")] public string? Headline { get; set; }
    [JsonPropertyName("meta")] public string? Meta { get; set; }
}
