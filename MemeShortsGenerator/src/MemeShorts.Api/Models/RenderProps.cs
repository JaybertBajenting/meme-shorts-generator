using System.Text.Json.Serialization;

namespace MemeShorts.Api.Models;

/// <summary>
/// The exact JSON contract passed to the Remotion composition via --props.
/// All asset paths are filenames relative to the job's --public-dir.
/// </summary>
public sealed class RenderProps
{
    [JsonPropertyName("width")] public int Width { get; set; } = 1080;
    [JsonPropertyName("height")] public int Height { get; set; } = 1920;
    [JsonPropertyName("fps")] public int Fps { get; set; } = 30;

    [JsonPropertyName("durationInFrames")] public int DurationInFrames { get; set; }

    /// <summary>
    /// Stable per-job seed for list-card framing and mascot timing. Without this, every video
    /// with the same number of beats repeats the exact same edit pattern.
    /// </summary>
    [JsonPropertyName("visualSeed")] public int VisualSeed { get; set; }

    /// <summary>Background clip filename (e.g. "bg.mp4"), or null to use the animated fallback.</summary>
    [JsonPropertyName("background")] public string? Background { get; set; }

    /// <summary>Background clip length in frames, so Remotion can loop it. 0/null = no loop.</summary>
    [JsonPropertyName("backgroundLoopFrames")] public int? BackgroundLoopFrames { get; set; }

    /// <summary>Background music filename (e.g. "music.mp3"), or null for none.</summary>
    [JsonPropertyName("music")] public string? Music { get; set; }
    [JsonPropertyName("musicVolume")] public double MusicVolume { get; set; } = 0.18;
    /// <summary>Music length in frames; set only when the track is shorter than the video (then it loops).</summary>
    [JsonPropertyName("musicLoopFrames")] public int? MusicLoopFrames { get; set; }

    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("format")] public string Format { get; set; } = "relatable_chaos";
    /// <summary>"meme" or "finance" — Remotion uses this to render panel art vs data visuals in the card.</summary>
    [JsonPropertyName("contentType")] public string ContentType { get; set; } = "meme";

    /// <summary>List shorts: sticker that perches on the card. Null = none.</summary>
    [JsonPropertyName("mascot")] public string? Mascot { get; set; }

    /// <summary>List shorts: every pose of the mascot, main pose first. Null = just <see cref="Mascot"/>.</summary>
    [JsonPropertyName("mascotPoses")] public List<string>? MascotPoses { get; set; }

    /// <summary>Blur applied to the gameplay behind the card, in px. Null = sharp.</summary>
    [JsonPropertyName("backgroundBlur")] public double? BackgroundBlur { get; set; }

    [JsonPropertyName("beats")] public List<RenderBeat> Beats { get; set; } = new();

    /// <summary>
    /// List shorts: ONE voice take for the whole script, played from frame 0. When set, the
    /// beats carry no audio of their own. Voicing each line as its own clip left a gap and a
    /// fresh intonation at every picture change - with a picture every ~1.2s that dragged the
    /// delivery to ~150 wpm against the reference's 201.
    /// </summary>
    [JsonPropertyName("narration")] public string? Narration { get; set; }

    /// <summary>Word-level captions in @remotion/captions shape.</summary>
    [JsonPropertyName("captions")] public List<Caption> Captions { get; set; } = new();

    [JsonPropertyName("fakeComment")] public RenderFakeComment? FakeComment { get; set; }

    /// <summary>Set on "repost" jobs; the post card replaces the panel entirely.</summary>
    [JsonPropertyName("post")] public RenderPost? Post { get; set; }
}

/// <summary>
/// A found social post reproduced as the video's only visual. Author fields are optional —
/// leave them null for a bare image (a Reddit meme has no header worth inventing).
/// </summary>
public sealed class RenderPost
{
    [JsonPropertyName("authorName")] public string? AuthorName { get; set; }
    [JsonPropertyName("authorDegree")] public string? AuthorDegree { get; set; }
    [JsonPropertyName("headline")] public string? Headline { get; set; }
    [JsonPropertyName("linkText")] public string? LinkText { get; set; }
    [JsonPropertyName("meta")] public string? Meta { get; set; }

    /// <summary>Filenames in the job dir (Remotion's --public-dir).</summary>
    [JsonPropertyName("avatar")] public string? Avatar { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }

    [JsonPropertyName("lines")] public List<PostLine> Lines { get; set; } = new();

    // --- Slice mode: the screenshot IS the card, revealed band by band ------------------
    // All values are pixel offsets in the SOURCE image, so the composition can scale the
    // whole thing to the card width without the backend knowing the render size.

    /// <summary>Source image size; null means fall back to drawing Lines as text.</summary>
    [JsonPropertyName("imageWidth")] public int? ImageWidth { get; set; }
    [JsonPropertyName("imageHeight")] public int? ImageHeight { get; set; }

    /// <summary>Bottom of the always-visible header (avatar/name row).</summary>
    [JsonPropertyName("headerBottom")] public int? HeaderBottom { get; set; }

    /// <summary>
    /// The slices of the screenshot, in the order they are stacked. Each is a crop of the
    /// source image that appears at its own moment — which covers a photo at the bottom, a
    /// photo in the middle, and a post that is nothing but text, with one mechanism.
    /// </summary>
    [JsonPropertyName("blocks")] public List<PostBlockRender> Blocks { get; set; } = new();

    /// <summary>
    /// On a two-column comparison meme: where the gutter sits, as a fraction of image width,
    /// and which half is being read when. The half NOT being read is blurred back, so the eye
    /// follows the voice instead of hunting across both columns.
    /// </summary>
    /// <summary>
    /// A flow diagram: slices are positioned where they SIT in the picture and uncovered in
    /// arrow order, rather than stacked into a card that grows downward.
    /// </summary>
    [JsonPropertyName("diagram")] public bool Diagram { get; set; }

    [JsonPropertyName("gutterFrac")] public double? GutterFrac { get; set; }

    [JsonPropertyName("spotlights")] public List<PostSpotlight> Spotlights { get; set; } = new();

    /// <summary>The screenshot's own background colour, so the growing edge never flashes white.</summary>
    [JsonPropertyName("cardBackground")] public string? CardBackground { get; set; }

    // Card geometry + motion. Null = use the measured defaults baked into PostCard.tsx.
    [JsonPropertyName("topStart")] public int? TopStart { get; set; }
    [JsonPropertyName("topEnd")] public int? TopEnd { get; set; }
    [JsonPropertyName("driftStartMs")] public int? DriftStartMs { get; set; }
    [JsonPropertyName("driftPxPerSec")] public double? DriftPxPerSec { get; set; }
}

/// <summary>
/// One stacked slice of the screenshot: which rows of the source image to show, and when.
/// The text is carried only for debugging — the visual is always the screenshot's own pixels.
/// </summary>
/// <summary>Which column is being read from <see cref="StartMs"/> until the next entry.</summary>
public sealed class PostSpotlight
{
    [JsonPropertyName("startMs")] public int StartMs { get; set; }
    /// <summary>"left", "right", or "full" to clear the blur entirely.</summary>
    [JsonPropertyName("side")] public string Side { get; set; } = "full";
}

public sealed class PostBlockRender
{
    [JsonPropertyName("top")] public int Top { get; set; }
    [JsonPropertyName("bottom")] public int Bottom { get; set; }
    [JsonPropertyName("startMs")] public int StartMs { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }

    /// <summary>Horizontal crop, in image pixels. Null spans the full width.</summary>
    [JsonPropertyName("xLeft")] public int? XLeft { get; set; }
    [JsonPropertyName("xRight")] public int? XRight { get; set; }

    /// <summary>"left" / "right" on a comparison, otherwise null. Shorthand for <see cref="Column"/>.</summary>
    [JsonPropertyName("side")] public string? Side { get; set; }

    /// <summary>
    /// Which column of a comparison this slice sits in, from the left; null spans the post.
    /// A comparison can have three ("1998 / 2010 / 2026"), which "left"/"right" cannot say.
    /// </summary>
    [JsonPropertyName("column")] public int? Column { get; set; }

    /// <summary>
    /// Comparison id. Slices sharing one are laid out SIDE BY SIDE instead of stacked, so the
    /// second column arrives beside the first rather than underneath it.
    /// </summary>
    [JsonPropertyName("group")] public int? Group { get; set; }
}

/// <summary>One PRE-WRAPPED display line and the moment it appears (text fallback only).</summary>
public sealed class PostLine
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("startMs")] public int StartMs { get; set; }
    [JsonPropertyName("newParagraph")] public bool NewParagraph { get; set; }
}

public sealed class RenderBeat
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("caption")] public string Caption { get; set; } = "";
    [JsonPropertyName("panel")] public string Panel { get; set; } = "";   // e.g. "panel_0.png"
    [JsonPropertyName("audio")] public string Audio { get; set; } = "";   // e.g. "beat_0.wav"
    [JsonPropertyName("startMs")] public int StartMs { get; set; }
    [JsonPropertyName("endMs")] public int EndMs { get; set; }
    [JsonPropertyName("options")] public List<string>? Options { get; set; }
    /// <summary>Finance: the data-driven visual to render in the card (null for meme beats).</summary>
    [JsonPropertyName("visual")] public BeatVisual? Visual { get; set; }
    /// <summary>
    /// List: the picture's own width/height ratio, so the card can take the MEDIA's shape the way
    /// the reference channel's does. His cards run 0.64-0.99 (portrait to square) because his
    /// source material is vertical - phone video and screenshots; ours come out landscape because
    /// Commons photographs are. Imposing a fixed aspect instead just crops the picture.
    /// </summary>
    [JsonPropertyName("panelAspect")] public double? PanelAspect { get; set; }
    /// <summary>List: foreground video filename. Null means render <see cref="Panel"/>.</summary>
    [JsonPropertyName("clip")] public string? Clip { get; set; }
    /// <summary>List: source clip length at the composition frame rate, used for looping.</summary>
    [JsonPropertyName("clipLoopFrames")] public int? ClipLoopFrames { get; set; }
    /// <summary>List: transparent subject cutout; render contained on white instead of photo-cover.</summary>
    [JsonPropertyName("isCutout")] public bool IsCutout { get; set; }
}

/// <summary>Matches the @remotion/captions Caption type exactly.</summary>
public sealed class Caption
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("startMs")] public int StartMs { get; set; }
    [JsonPropertyName("endMs")] public int EndMs { get; set; }
    [JsonPropertyName("timestampMs")] public int? TimestampMs { get; set; }
    [JsonPropertyName("confidence")] public double? Confidence { get; set; }
}

public sealed class RenderFakeComment
{
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("startMs")] public int StartMs { get; set; }
}
