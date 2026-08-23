using MemeShorts.Api.Models;

namespace MemeShorts.Api.Services;

/// <summary>
/// A deterministic storyboard used when Codex is unavailable, so the pipeline is
/// always demonstrable end-to-end. It mimics the "relatable_chaos" escalation.
/// </summary>
public static class FallbackStoryboard
{
    public static Storyboard For(string topic)
    {
        string t = string.IsNullOrWhiteSpace(topic) ? "doing the thing" : topic.Trim();
        string T = char.ToUpperInvariant(t[0]) + t[1..];

        return new Storyboard
        {
            Title = T,
            Format = "relatable_chaos",
            Beats = new()
            {
                new Beat { Caption = T, Tts = T, RageFace = "poker", DurationS = 1.8 },
                new Beat { Caption = "Everything is fine...", Tts = "Everything is fine", RageFace = "happy", DurationS = 1.8 },
                new Beat { Caption = "wait, what was that?", Tts = "Wait, what was that", RageFace = "are_you_kidding_me", DurationS = 2.0 },
                new Beat { Caption = "it's getting worse", Tts = "It is getting worse", RageFace = "cry", DurationS = 2.0 },
                new Beat { Caption = "MOTHER OF...", Tts = "Mother of pearl", RageFace = "mother_of_god", DurationS = 1.8 },
                new Beat { Caption = "WHY DOES THIS ALWAYS HAPPEN", Tts = "Why does this always happen", RageFace = "fffuuu", DurationS = 2.2 },
            },
            FakeComment = new FakeComment
            {
                Username = "Evan",
                Text = "WHY DOES THIS JUST RANDOMLY HAPPEN?!?",
            },
        };
    }

    /// <summary>Deterministic finance storyboard used when Codex is unavailable.</summary>
    public static Storyboard Finance(string topic)
    {
        string t = string.IsNullOrWhiteSpace(topic) ? "compound interest" : topic.Trim();
        string T = char.ToUpperInvariant(t[0]) + t[1..];

        return new Storyboard
        {
            Title = T,
            YouTubeTitle = $"The Truth About {T}",
            ThumbnailText = "DO THIS NOW",
            Format = "finance",
            Beats = new()
            {
                new Beat { Caption = "Most people get this wrong", Tts = "Most people get this completely wrong", DurationS = 2.0,
                    Visual = new BeatVisual { Type = "tip", Tip = new TipVisual { Text = "Time beats timing.", Icon = "⏳" } } },
                new Beat { Caption = "$200/month, 40 years", Tts = "Two hundred dollars a month for forty years", DurationS = 2.4,
                    Visual = new BeatVisual { Type = "stat", Stat = new StatVisual { From = "$96,000 in", To = "$620,000", Label = "at 8% return", Delta = "+546%",
                        Breakdown = new() { new() { Label = "You put in", Value = 96 }, new() { Label = "Growth", Value = 524 } } } } },
                new Beat { Caption = "Where it goes", Tts = "Here's where the money actually goes", DurationS = 2.4,
                    Visual = new BeatVisual { Type = "chart", Chart = new ChartVisual { Kind = "bar", Unit = "$k",
                        Points = new() { new() { Label = "Contributions", Value = 96 }, new() { Label = "Growth", Value = 524 } } } } },
                new Beat { Caption = "Saver vs Investor", Tts = "This is the difference between a saver and an investor", DurationS = 2.6,
                    Visual = new BeatVisual { Type = "comparison", Comparison = new ComparisonVisual {
                        LeftTitle = "Saver", Left = new() { "0% growth", "Loses to inflation", "$96k" },
                        RightTitle = "Investor", Right = new() { "8% growth", "Beats inflation", "$620k" },
                        Result = "Same money in. 6x more out." } } },
                new Beat { Caption = "Start today", Tts = "The best time to start was yesterday. The second best is today.", DurationS = 2.4,
                    Visual = new BeatVisual { Type = "tip", Tip = new TipVisual { Text = "Start now, even small.", Icon = "🚀",
                        Points = new() { "Automate $200/mo", "Low-cost index fund", "Don't touch it" } } } },
            },
            FakeComment = new FakeComment { Username = "Marcus", Text = "wish i started 10 years ago 😭" },
        };
    }
}
