using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MemeShorts.Api.Models;
using Microsoft.Extensions.Options;

namespace MemeShorts.Api.Services;

/// <summary>
/// ElevenLabs TTS. Requests the configured output format and writes a 16-bit mono 44.1kHz
/// WAV at the target path so the rest of the pipeline (duration math, whisper timing,
/// Remotion &lt;Audio&gt;) is unchanged. MP3 (the tier-safe default) is transcoded via ffmpeg;
/// raw PCM (Pro tier only) is wrapped directly.
///
/// Quality guard: every synthesized beat is scanned for hard clipping; a distorted take is
/// re-requested with a fresh seed (up to <see cref="TtsOptions.ElevenLabsMaxRerenders"/> times)
/// and the cleanest take is kept. Activate by setting Tts:Provider = "ElevenLabs" + ApiKey + VoiceId.
/// </summary>
public sealed class ElevenLabsTtsService : ITtsService
{
    private const int SampleRate = 44100;
    // A take is "distorted" if it has a flat-topped run this long, or this fraction of clipped samples.
    private const int FlatRunThreshold = 8;
    private const double ClipRatioThreshold = 0.002;

    private readonly TtsOptions _opt;
    private readonly PipelineOptions _pipe;
    private readonly HttpClient _http;
    private readonly ILogger<ElevenLabsTtsService> _log;

    private readonly VoiceSettingsStore _voice;

    public ElevenLabsTtsService(IOptions<TtsOptions> opt, IOptions<PipelineOptions> pipe,
        VoiceSettingsStore voice, IHttpClientFactory factory, ILogger<ElevenLabsTtsService> log)
    {
        _opt = opt.Value;
        _voice = voice;
        _pipe = pipe.Value;
        _http = factory.CreateClient("elevenlabs");
        _log = log;
    }

    public async Task<int> SynthesizeAsync(string text, string outputWavPath, string? voiceOverride = null,
        string? emotion = null, CancellationToken ct = default, int? windowsRateOverride = null)
    {
        if (string.IsNullOrWhiteSpace(_opt.ElevenLabsApiKey) || string.IsNullOrWhiteSpace(_voice.VoiceId))
            throw new InvalidOperationException("ElevenLabs requires Tts:ElevenLabsApiKey and Tts:ElevenLabsVoiceId.");

        var fmt = string.IsNullOrWhiteSpace(_opt.ElevenLabsOutputFormat) ? "mp3_44100_128" : _opt.ElevenLabsOutputFormat.Trim();
        var attempts = Math.Max(0, _opt.ElevenLabsMaxRerenders) + 1;

        // eleven_v3 performs emotion via an inline audio tag like "[panicked]". Other models would
        // read the brackets literally, so only prepend the tag for the v3 model family.
        // ForceEmotion (when set) overrides the per-beat emotion so every line has the same
        // constant high-energy delivery (e.g. always "shouting").
        var effectiveEmotion = string.IsNullOrWhiteSpace(_opt.ForceEmotion) ? emotion : _opt.ForceEmotion;
        // The list path cues sentences inline (AudioTags). Only v3 performs them; any other model
        // would say "excited" out loud.
        var spoken = IsV3Model(_opt.ElevenLabsModelId) ? text : AudioTags.Strip(text);
        if (!string.IsNullOrWhiteSpace(effectiveEmotion) && IsV3Model(_opt.ElevenLabsModelId))
        {
            var tag = effectiveEmotion.Trim().Trim('[', ']');
            spoken = $"[{tag}] {spoken}";
        }

        string? bestTmp = null;
        WavUtils.PeakStats bestStats = default;

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            // First attempt uses the voice's natural seed; retries vary it to get a different take.
            int? seed = attempt == 0 ? null : Random.Shared.Next(1, int.MaxValue);
            var candidate = outputWavPath + (attempt == 0 ? ".cand0.wav" : $".cand{attempt}.wav");

            await SynthesizeOnceAsync(spoken, candidate, fmt, seed, ct);
            var stats = WavUtils.AnalyzePeaks(candidate);

            bool distorted = stats.MaxClipRun >= FlatRunThreshold || stats.ClipRatio > ClipRatioThreshold;

            // Keep the take with the fewest clipped samples seen so far.
            if (bestTmp is null || stats.ClippedSamples < bestStats.ClippedSamples)
            {
                if (bestTmp is not null) TryDelete(bestTmp);
                bestTmp = candidate;
                bestStats = stats;
            }
            else
            {
                TryDelete(candidate);
            }

            if (!distorted) break;

            if (attempt + 1 < attempts)
                _log.LogWarning("ElevenLabs take distorted (peak {Peak:0.0} dB, clipRun {Run}, clipRatio {Ratio:0.000%}); re-rendering \"{Text}\".",
                    stats.PeakDb, stats.MaxClipRun, stats.ClipRatio, Trunc(text));
            else
                _log.LogWarning("ElevenLabs still clipping after {N} re-renders for \"{Text}\"; keeping cleanest take (clipRun {Run}).",
                    attempts - 1, Trunc(text), bestStats.MaxClipRun);
        }

        // Promote the chosen take to the final path.
        if (File.Exists(outputWavPath)) TryDelete(outputWavPath);
        File.Move(bestTmp!, outputWavPath);
        return WavUtils.GetDurationMs(outputWavPath);
    }

    private async Task SynthesizeOnceAsync(string text, string wavPath, string fmt, int? seed, CancellationToken ct)
    {
        var url = $"https://api.elevenlabs.io/v1/text-to-speech/{_voice.VoiceId}?output_format={fmt}";

        var body = new Dictionary<string, object>
        {
            ["text"] = text,
            ["model_id"] = _opt.ElevenLabsModelId,
        };
        if (seed is not null) body["seed"] = seed.Value;
        var vs = BuildVoiceSettings();
        if (vs is not null) body["voice_settings"] = vs;
        var payload = JsonSerializer.Serialize(body);

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("xi-api-key", _opt.ElevenLabsApiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"ElevenLabs {(int)resp.StatusCode} ({fmt}): {err}");
        }

        var audio = await resp.Content.ReadAsByteArrayAsync(ct);
        Directory.CreateDirectory(Path.GetDirectoryName(wavPath)!);

        if (fmt.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase))
        {
            var rate = ParsePcmRate(fmt, SampleRate);
            WavUtils.WritePcmAsWav(audio, wavPath, rate, channels: 1, bitsPerSample: 16);
        }
        else
        {
            await TranscodeToWavAsync(audio, wavPath, ct);
        }
    }

    /// <summary>
    /// ffmpeg -af chain that shapes delivery: trim the padding ElevenLabs leaves on each clip,
    /// then apply tempo. Empty when both are disabled, so the transcode stays a plain copy.
    /// </summary>
    private string BuildDeliveryFilters()
    {
        var parts = new List<string>();

        if (_opt.ElevenLabsTrimSilence)
        {
            // silenceremove only trims the START, so the tail is done by reversing, trimming
            // the (now leading) tail, and reversing back. 0.06s is kept at each end so plosives
            // and trailing consonants aren't cut off.
            const string trimHead = "silenceremove=start_periods=1:start_silence=0.06:start_threshold=-45dB:detection=peak";
            parts.Add(trimHead);
            parts.Add("areverse");
            parts.Add(trimHead);
            parts.Add("areverse");
        }

        var rate = _voice.SpeechRate;
        if (rate > 0 && Math.Abs(rate - 1.0) > 0.001)
        {
            // A single atempo instance is valid over 0.5–2.0; clamp rather than chain, since
            // anything beyond this range is unusable for narration anyway.
            var clamped = Math.Clamp(rate, 0.5, 2.0);
            parts.Add($"atempo={clamped.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        return string.Join(",", parts);
    }

    private async Task TranscodeToWavAsync(byte[] audio, string outputWavPath, CancellationToken ct)
    {
        var tmp = outputWavPath + ".src.mp3";
        await File.WriteAllBytesAsync(tmp, audio, ct);
        try
        {
            var args = new List<string>
            {
                "-y", "-hide_banner", "-loglevel", "error", "-i", tmp,
            };

            var filters = BuildDeliveryFilters();
            if (filters.Length > 0)
            {
                args.Add("-af");
                args.Add(filters);
            }

            args.AddRange(new[] { "-ar", SampleRate.ToString(), "-ac", "1", "-c:a", "pcm_s16le", outputWavPath });

            var res = await ProcessRunner.RunAsync(
                _pipe.FfmpegCommand, args.ToArray(),
                workingDir: null, TimeSpan.FromSeconds(60), ct);

            if (!File.Exists(outputWavPath) || new FileInfo(outputWavPath).Length == 0)
                throw new InvalidOperationException(
                    $"ffmpeg failed to transcode ElevenLabs audio to WAV (exit {res.ExitCode}): {res.StdErr}");
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    private static int ParsePcmRate(string fmt, int fallback)
    {
        var parts = fmt.Split('_');
        return parts.Length >= 2 && int.TryParse(parts[1], out var r) && r > 0 ? r : fallback;
    }

    private static bool IsV3Model(string? modelId) =>
        !string.IsNullOrWhiteSpace(modelId) && modelId.Contains("_v3", StringComparison.OrdinalIgnoreCase);

    /// <summary>Voice settings object (or null if none configured) for the request body.</summary>
    private Dictionary<string, object>? BuildVoiceSettings()
    {
        var vs = new Dictionary<string, object>();
        if (_opt.ElevenLabsStability is { } st) vs["stability"] = st;
        if (_opt.ElevenLabsSimilarityBoost is { } sb) vs["similarity_boost"] = sb;
        if (_opt.ElevenLabsStyle is { } sty) vs["style"] = sty;
        if (_opt.ElevenLabsSpeakerBoost is { } boost) vs["use_speaker_boost"] = boost;
        // Clamped to the API's accepted range — out-of-range values fail the whole request.
        if (_opt.ElevenLabsSpeed is { } speed) vs["speed"] = Math.Clamp(speed, 0.7, 1.2);
        return vs.Count > 0 ? vs : null;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static string Trunc(string s) => s.Length <= 40 ? s : s[..40] + "…";
}
