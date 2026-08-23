namespace MemeShorts.Api.Models;

/// <summary>
/// A named channel "profile" — per-channel overrides selected at generation time. Null/empty
/// fields fall back to the global options. The first profile (or one named "default") is used
/// when no channel is specified.
/// </summary>
public sealed class ChannelProfile
{
    public string Id { get; set; } = "default";
    public string Name { get; set; } = "Default";

    /// <summary>"meme" (Codex image panels) or "finance" (Remotion data visuals in the box).</summary>
    public string ContentType { get; set; } = "meme";
    /// <summary>Optional fixed format hint passed to the script generator.</summary>
    public string? Format { get; set; }

    // --- Title style (yt-dlp reference) ---
    public string? TitleReferenceChannel { get; set; }
    public string? TitleTab { get; set; }

    // --- Assets / voice (override globals when set) ---
    public string? BackgroundDir { get; set; }
    public string? MusicDir { get; set; }
    /// <summary>
    /// The channel's mascot sprite (transparent PNG), used on every list short unless a job
    /// supplies its own. A channel-level asset rather than a per-video upload: the character is
    /// the same every time, and re-attaching it by hand for each video is how it gets forgotten.
    /// </summary>
    public string? MascotPath { get; set; }
    public string? WindowsVoice { get; set; }
    public int? WindowsRate { get; set; }
    /// <summary>Meme panels: "Codex" or "Local". Ignored for finance.</summary>
    public string? PanelSource { get; set; }

    // --- Upload (per-channel; credentials can be wired later) ---
    public ChannelUpload? Upload { get; set; }
}

public sealed class ChannelUpload
{
    public bool? Enabled { get; set; }
    public bool? AutoUpload { get; set; }
    public string? ClientSecretsPath { get; set; }
    public string? TokenStorePath { get; set; }
    public string[]? PublishTimesLocal { get; set; }
    public string[]? Tags { get; set; }
    public string? CategoryId { get; set; }
    public string? TitleSuffix { get; set; }
    public string? DescriptionTemplate { get; set; }
}

/// <summary>Bound from configuration section "Channels".</summary>
public sealed class ChannelsOptions
{
    public List<ChannelProfile> Profiles { get; set; } = new();
}

/// <summary>
/// Fully-resolved per-job settings (profile overlaid on global defaults). The pipeline,
/// uploader, and scheduler read this instead of raw globals so everything is channel-aware.
/// </summary>
public sealed class EffectiveChannel
{
    public string Id { get; init; } = "default";
    public string Name { get; init; } = "Default";
    public string ContentType { get; init; } = "meme";
    public string? Format { get; init; }

    public string? TitleReferenceChannel { get; init; }
    public string TitleTab { get; init; } = "shorts";

    public string BackgroundDir { get; init; } = "";
    public string MusicDir { get; init; } = "";
    /// <summary>Channel mascot sprite, or null for a channel that doesn't use one.</summary>
    public string? MascotPath { get; init; }
    public string WindowsVoice { get; init; } = "";
    public int WindowsRate { get; init; }
    public string PanelSource { get; init; } = "Codex";

    public bool UploadEnabled { get; init; }
    public bool AutoUpload { get; init; }
    public string ClientSecretsPath { get; init; } = "";
    public string TokenStorePath { get; init; } = "";
    public string[] PublishTimesLocal { get; init; } = Array.Empty<string>();
    public string[] Tags { get; init; } = Array.Empty<string>();
    public string CategoryId { get; init; } = "24";
    public string TitleSuffix { get; init; } = "";
    public string DescriptionTemplate { get; init; } = "{title}";

    public bool IsFinance => ContentType.Equals("finance", StringComparison.OrdinalIgnoreCase);

    /// <summary>A hand-supplied post read verbatim — no topic, no Codex, no generated artwork.</summary>
    public bool IsRepost => ContentType.Equals("repost", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A run-through of supplied pictures — one per spoken line — on a drifting card with
    /// karaoke captions over it. You bring the pictures; the script is typed or written by Codex.
    /// </summary>
    public bool IsList => ContentType.Equals("list", StringComparison.OrdinalIgnoreCase);
}
