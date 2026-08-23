using System.Text.Json.Serialization;

namespace MemeShorts.Api.Models;

/// <summary>
/// The structured "script" Codex returns for one short. Mirrors the schema we
/// hand to Codex in <see cref="Services.Prompts"/>. Everything stylistic
/// (layout, animation, fonts) lives in the renderer; Codex only varies content.
/// </summary>
public sealed class Storyboard
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "Untitled";

    /// <summary>Click-optimized YouTube title in the reference channel's style.</summary>
    [JsonPropertyName("youtube_title")]
    public string? YouTubeTitle { get; set; }

    /// <summary>Short 2-4 word bold hook for the thumbnail.</summary>
    [JsonPropertyName("thumbnail_text")]
    public string? ThumbnailText { get; set; }

    /// <summary>single_visual_reveal | relatable_chaos | relatable_list | absurd_debate | chart_gag | escalating_corrections | internal_monologue.</summary>
    [JsonPropertyName("format")]
    public string Format { get; set; } = "relatable_chaos";

    [JsonPropertyName("beats")]
    public List<Beat> Beats { get; set; } = new();

    [JsonPropertyName("fake_comment")]
    public FakeComment? FakeComment { get; set; }
}

/// <summary>One panel = one caption = one TTS line = one timeline beat.</summary>
public sealed class Beat
{
    [JsonPropertyName("caption")]
    public string Caption { get; set; } = "";

    /// <summary>What the narrator says. Usually equals the caption.</summary>
    [JsonPropertyName("tts")]
    public string Tts { get; set; } = "";

    /// <summary>
    /// Emotion/delivery for the line, e.g. "excited", "panicked", "whispering", "deadpan",
    /// "shouting". Performed by ElevenLabs eleven_v3 as an audio tag (not spoken); ignored by
    /// other models/voices. Null = neutral.
    /// </summary>
    [JsonPropertyName("emotion")]
    public string? Emotion { get; set; }

    /// <summary>Named rage face from the library (e.g. "mother_of_god"), or null.</summary>
    [JsonPropertyName("rage_face")]
    public string? RageFace { get; set; }

    /// <summary>Free-text description of an object to draw (e.g. "a microwave showing 0:05"), or null.</summary>
    [JsonPropertyName("drawing")]
    public string? Drawing { get; set; }

    /// <summary>
    /// Visual treatment for meme panels: doodle, photo, or graphic. Missing values keep the
    /// original doodle fallback so older storyboard JSON remains compatible.
    /// </summary>
    [JsonPropertyName("visual_style")]
    public string? VisualStyle { get; set; }

    /// <summary>For "absurd_debate": the two labelled options. Otherwise null.</summary>
    [JsonPropertyName("options")]
    public List<string>? Options { get; set; }

    /// <summary>Finance channel: the data-driven visual rendered inside the card. Null for meme beats.</summary>
    [JsonPropertyName("visual")]
    public BeatVisual? Visual { get; set; }

    /// <summary>Pacing hint from Codex in seconds. Treated as a hint only — real TTS duration wins.</summary>
    [JsonPropertyName("duration_s")]
    public double DurationS { get; set; } = 2.0;
}

/// <summary>A finance "in the box" visual. One of stat/chart/comparison/tip per the `type`.</summary>
public sealed class BeatVisual
{
    [JsonPropertyName("type")] public string Type { get; set; } = "tip"; // stat | chart | comparison | tip
    [JsonPropertyName("stat")] public StatVisual? Stat { get; set; }
    [JsonPropertyName("chart")] public ChartVisual? Chart { get; set; }
    [JsonPropertyName("comparison")] public ComparisonVisual? Comparison { get; set; }
    [JsonPropertyName("tip")] public TipVisual? Tip { get; set; }
}

public sealed class StatVisual
{
    [JsonPropertyName("from")] public string? From { get; set; }
    [JsonPropertyName("to")] public string To { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    /// <summary>A short change badge, e.g. "+8%/yr" or "20% saved".</summary>
    [JsonPropertyName("delta")] public string? Delta { get; set; }
    /// <summary>2-3 supporting rows (label + value) shown as mini bars under the big number.</summary>
    [JsonPropertyName("breakdown")] public List<ChartPoint>? Breakdown { get; set; }
}

public sealed class ChartVisual
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "bar"; // bar | line | pie
    [JsonPropertyName("unit")] public string? Unit { get; set; }
    [JsonPropertyName("points")] public List<ChartPoint> Points { get; set; } = new();
}

public sealed class ChartPoint
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("value")] public double Value { get; set; }
}

public sealed class ComparisonVisual
{
    [JsonPropertyName("leftTitle")] public string LeftTitle { get; set; } = "";
    [JsonPropertyName("left")] public List<string> Left { get; set; } = new();
    [JsonPropertyName("rightTitle")] public string RightTitle { get; set; } = "";
    [JsonPropertyName("right")] public List<string> Right { get; set; } = new();
    /// <summary>Optional bold takeaway banner shown under the two columns.</summary>
    [JsonPropertyName("result")] public string? Result { get; set; }
}

public sealed class TipVisual
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("icon")] public string? Icon { get; set; }
    /// <summary>2-3 short supporting bullets shown under the headline.</summary>
    [JsonPropertyName("points")] public List<string>? Points { get; set; }
}

public sealed class FakeComment
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = "user";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}
