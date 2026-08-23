using System.Text.Json;
using MemeShorts.Api.Models;
using Microsoft.Extensions.Options;

namespace MemeShorts.Api.Services;

/// <summary>A single transcribed word, times in milliseconds relative to its clip.</summary>
public sealed record WordTiming(string Text, int StartMs, int EndMs);

public interface IWhisperCaptionService
{
    bool Enabled { get; }

    /// <summary>
    /// Transcribe each wav (one model load for all). Returns word timings keyed by the
    /// wav path passed in. Returns null on failure (caller falls back to even spread).
    /// </summary>
    Task<Dictionary<string, List<WordTiming>>?> TranscribeAsync(IReadOnlyList<string> wavPaths, CancellationToken ct = default);
}

/// <summary>
/// Word-level timing via faster-whisper (Python). Matches the user's existing
/// transcription workflow (small.en, CPU, int8). Output feeds CaptionBuilder so the
/// on-card caption tokens highlight exactly when each word is spoken.
/// </summary>
public sealed class WhisperCaptionService : IWhisperCaptionService
{
    private readonly CaptionsOptions _opt;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<WhisperCaptionService> _log;

    public WhisperCaptionService(IOptions<CaptionsOptions> opt, IWebHostEnvironment env, ILogger<WhisperCaptionService> log)
    {
        _opt = opt.Value;
        _env = env;
        _log = log;
    }

    public bool Enabled => _opt.Source.Equals("Whisper", StringComparison.OrdinalIgnoreCase);

    public async Task<Dictionary<string, List<WordTiming>>?> TranscribeAsync(IReadOnlyList<string> wavPaths, CancellationToken ct = default)
    {
        if (!Enabled || wavPaths.Count == 0) return null;

        var script = Path.Combine(_env.ContentRootPath, "tools", "whisper_words.py");
        if (!File.Exists(script))
        {
            _log.LogWarning("whisper_words.py not found at {Path}; using even caption spread.", script);
            return null;
        }

        var args = new List<string> { script, _opt.WhisperModel, _opt.WhisperDevice, _opt.WhisperCompute };
        args.AddRange(wavPaths);

        try
        {
            var res = await ProcessRunner.RunAsync(
                _opt.PythonPath, args, workingDir: _env.ContentRootPath,
                TimeSpan.FromSeconds(_opt.TimeoutSeconds), ct);

            if (!res.Ok)
            {
                _log.LogWarning("Whisper exited {Code}: {Err}", res.ExitCode, Trunc(res.StdErr));
                return null;
            }

            var json = JsonExtract.FirstJsonArray(res.StdOut);
            if (json is null)
            {
                _log.LogWarning("Whisper produced no JSON. Output: {Out}", Trunc(res.StdOut));
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var map = new Dictionary<string, List<WordTiming>>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var file = item.GetProperty("file").GetString() ?? "";
                var words = new List<WordTiming>();
                foreach (var w in item.GetProperty("words").EnumerateArray())
                {
                    var text = w.GetProperty("text").GetString() ?? "";
                    int start = (int)Math.Round(w.GetProperty("start").GetDouble() * 1000);
                    int end = (int)Math.Round(w.GetProperty("end").GetDouble() * 1000);
                    if (!string.IsNullOrWhiteSpace(text)) words.Add(new WordTiming(text, start, end));
                }
                map[file] = words;
            }
            return map;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Whisper transcription failed; using even caption spread.");
            return null;
        }
    }

    private static string Trunc(string s, int n = 500) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
