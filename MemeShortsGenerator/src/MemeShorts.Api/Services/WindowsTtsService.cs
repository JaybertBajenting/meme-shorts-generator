using System.Runtime.Versioning;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using MemeShorts.Api.Models;
using Microsoft.Extensions.Options;

namespace MemeShorts.Api.Services;

/// <summary>
/// Microsoft Windows TTS via the classic SAPI5 desktop voices (System.Speech).
/// Free, offline, no credits. Swap to ElevenLabs by setting Tts:Provider.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTtsService : ITtsService
{
    private readonly TtsOptions _opt;
    private readonly ILogger<WindowsTtsService> _log;

    public WindowsTtsService(IOptions<TtsOptions> opt, ILogger<WindowsTtsService> log)
    {
        _opt = opt.Value;
        _log = log;
    }

    public Task<int> SynthesizeAsync(string text, string outputWavPath, string? voiceOverride = null,
        string? emotion = null, CancellationToken ct = default)
    {
        // SAPI has no emotion control; `emotion` is ignored here (used by ElevenLabs v3).
        var voice = string.IsNullOrWhiteSpace(voiceOverride) ? _opt.WindowsVoice : voiceOverride;
        // System.Speech is synchronous; run it off the request thread.
        return Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputWavPath)!);

            using var synth = new SpeechSynthesizer();

            if (!string.IsNullOrWhiteSpace(voice))
            {
                var match = synth.GetInstalledVoices()
                    .Where(v => v.Enabled)
                    .FirstOrDefault(v => v.VoiceInfo.Name.Contains(voice, StringComparison.OrdinalIgnoreCase));
                if (match is not null) synth.SelectVoice(match.VoiceInfo.Name);
                else _log.LogWarning("SAPI voice matching '{Voice}' not found; using default.", voice);
            }

            synth.Rate = Math.Clamp(_opt.WindowsRate, -10, 10);

            // Fixed PCM format so WavUtils duration math is exact.
            var fmt = new SpeechAudioFormatInfo(44100, AudioBitsPerSample.Sixteen, AudioChannel.Mono);
            synth.SetOutputToWaveFile(outputWavPath, fmt);
            synth.Speak(text);
            synth.SetOutputToNull();

            return WavUtils.GetDurationMs(outputWavPath);
        }, ct);
    }
}
