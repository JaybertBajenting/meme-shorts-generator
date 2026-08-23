using MemeShorts.Api.Models;
using Microsoft.Extensions.Options;

namespace MemeShorts.Api.Services;

public interface ITitleStyleService
{
    /// <summary>Recent titles from the GLOBAL reference channel (yt-dlp), cached.</summary>
    Task<IReadOnlyList<string>> GetStyleTitlesAsync(CancellationToken ct = default);

    /// <summary>Recent titles from a SPECIFIC channel (per profile), cached per channel URL.</summary>
    Task<IReadOnlyList<string>> GetStyleTitlesAsync(string? channelUrl, string? tab, CancellationToken ct = default);
}

/// <summary>
/// Fetches a reference channel's recent video titles with yt-dlp so Codex can match that
/// channel's titling style. Cached in-memory (refreshed every CacheHours) to avoid hitting
/// yt-dlp on every render.
/// </summary>
public sealed class TitleStyleService : ITitleStyleService
{
    private readonly TitleStyleOptions _opt;
    private readonly ILogger<TitleStyleService> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, (IReadOnlyList<string> titles, DateTimeOffset at)> _cache = new();

    public TitleStyleService(IOptions<TitleStyleOptions> opt, ILogger<TitleStyleService> log)
    {
        _opt = opt.Value; _log = log;
    }

    public Task<IReadOnlyList<string>> GetStyleTitlesAsync(CancellationToken ct = default) =>
        GetStyleTitlesAsync(_opt.ReferenceChannel, _opt.Tab, ct);

    public async Task<IReadOnlyList<string>> GetStyleTitlesAsync(string? channelUrl, string? tab, CancellationToken ct = default)
    {
        if (!_opt.Enabled || string.IsNullOrWhiteSpace(channelUrl))
            return Array.Empty<string>();

        var channel = channelUrl.TrimEnd('/');
        var url = channel.Contains("/shorts") || channel.Contains("/videos")
            ? channel
            : $"{channel}/{(string.IsNullOrWhiteSpace(tab) ? _opt.Tab : tab)}";

        if (_cache.TryGetValue(url, out var hit) && DateTimeOffset.UtcNow - hit.at < TimeSpan.FromHours(_opt.CacheHours))
            return hit.titles;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(url, out hit) && DateTimeOffset.UtcNow - hit.at < TimeSpan.FromHours(_opt.CacheHours))
                return hit.titles;

            var args = new[]
            {
                "--flat-playlist", "--no-warnings", "--ignore-errors",
                "--print", "%(title)s",
                "--playlist-end", _opt.Count.ToString(),
                url,
            };

            var res = await ProcessRunner.RunAsync(_opt.YtDlpPath, args, workingDir: null,
                TimeSpan.FromSeconds(_opt.TimeoutSeconds), ct);

            var titles = res.StdOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .Distinct()
                .Take(_opt.Count)
                .ToList();

            if (titles.Count > 0)
            {
                _cache[url] = (titles, DateTimeOffset.UtcNow);
                _log.LogInformation("Fetched {N} style titles from {Url}.", titles.Count, url);
                return titles;
            }

            _log.LogWarning("yt-dlp returned no titles from {Url} (exit {Code}). {Err}",
                url, res.ExitCode, Trunc(res.StdErr));
            return _cache.TryGetValue(url, out var prev) ? prev.titles : Array.Empty<string>();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Title-style fetch failed; continuing without style examples.");
            return _cache.TryGetValue(url, out var prev) ? prev.titles : Array.Empty<string>();
        }
        finally { _lock.Release(); }
    }

    private static string Trunc(string s, int n = 300) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
