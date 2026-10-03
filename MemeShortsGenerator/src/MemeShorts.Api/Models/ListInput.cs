using System.Text.Json.Serialization;

namespace MemeShorts.Api.Models;

/// <summary>
/// A "list short": a run of pictures, one spoken line each, on a drifting card with karaoke
/// captions over it.
///
/// Nothing is generated — you supply both halves. Its whole job is to pair picture i with
/// line i, which is what the format is: a rapid list where each item has its own visual.
/// </summary>
public sealed class ListInput
{
    [JsonPropertyName("items")] public List<ListItem> Items { get; set; } = new();

    /// <summary>YouTube title. Falls back to the first line when blank.</summary>
    [JsonPropertyName("title")] public string? Title { get; set; }

    /// <summary>Sticker filename in the job dir that perches on the card, or null for none.</summary>
    [JsonPropertyName("mascot")] public string? Mascot { get; set; }

    /// <summary>What the video is about, in a few words - kept so later scripts don't repeat it.</summary>
    [JsonPropertyName("subject")] public string? Subject { get; set; }
}

public sealed class ListItem
{
    /// <summary>Picture filename inside the job dir, e.g. "item_0.png".</summary>
    [JsonPropertyName("image")] public string Image { get; set; } = "";

    /// <summary>The line spoken while this picture is up.</summary>
    [JsonPropertyName("text")] public string Text { get; set; } = "";
}
