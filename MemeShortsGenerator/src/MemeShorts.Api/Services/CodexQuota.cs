using System.Text.RegularExpressions;

namespace MemeShorts.Api.Services;

/// <summary>
/// Whether the Codex account has run out, read off the CLI's own output.
///
/// Out of quota, every call still "finishes": it prints "You've hit your usage limit ... try again
/// at 10:59 PM" and exits, and each caller sees an empty result. Twice (2026-09-27) that happened
/// mid-video and the video was made anyway - drawn pictures missing, and the object pictures
/// replaced by loose photo matches (an old painting for "glasses", a kitchen scale for
/// "countdown", real people's faces the first time). Anything unattended has to be able to ask
/// "did Codex just run out?" and stop instead.
/// </summary>
public static class CodexQuota
{
    private static readonly Regex RetryAt = new(@"try again at ([^.\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // The image tool's own limit arrives as JSON instead: {"type":"usage_limit_reached",
    // "message":"The usage limit has been reached", ..., "resets_at":1790549022} (2026-09-28).
    private static readonly Regex ResetsAt = new(@"resets_at\\?""?\s*:\s*(\d{9,11})", RegexOptions.Compiled);
    private static readonly object Gate = new();
    private static long _hits;
    private static bool _exhausted;
    private static string? _retryAt;

    /// <summary>How many calls have come back out of quota since start-up. Compare before/after a piece of work.</summary>
    public static long Hits => Interlocked.Read(ref _hits);

    /// <summary>True from the first out-of-quota answer until a later call succeeds.</summary>
    public static bool Exhausted { get { lock (Gate) return _exhausted; } }

    /// <summary>"10:59 PM", when the CLI said so.</summary>
    public static string? RetryAtText { get { lock (Gate) return _retryAt; } }

    public static string Describe() => RetryAtText is { } at
        ? $"Codex hit its usage limit (it resets at {at})."
        : "Codex hit its usage limit.";

    /// <summary>"6:43 AM", or "Mon 6:43 AM" when it is not today.</summary>
    private static string FormatLocal(long unixSeconds)
    {
        var t = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime();
        return t.Date == DateTime.Now.Date ? t.ToString("h:mm tt") : t.ToString("ddd h:mm tt");
    }

    /// <summary>Called with every finished Codex CLI run.</summary>
    internal static void Observe(ProcessResult res)
    {
        var text = res.StdOut + "\n" + res.StdErr;
        if (text.Contains("usage limit", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _hits);
            var when = RetryAt.Match(text) is { Success: true } at ? at.Groups[1].Value.Trim()
                : ResetsAt.Match(text) is { Success: true } unix ? FormatLocal(long.Parse(unix.Groups[1].Value))
                : null;
            lock (Gate)
            {
                _exhausted = true;
                if (when is not null) _retryAt = when;
            }
        }
        else if (res.ExitCode == 0)
        {
            lock (Gate) _exhausted = false;
        }
    }
}
