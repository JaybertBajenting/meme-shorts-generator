using MemeShorts.Api.Models;
using Microsoft.Extensions.Options;

namespace MemeShorts.Api.Services;

/// <summary>
/// Runtime-editable ElevenLabs voice, so voices can be auditioned from the Settings page without
/// editing .env and restarting. Same resolution rule as the Meta credentials: a value saved in
/// the app wins, otherwise the configured .env/appsettings value applies.
///
/// SpeechRate lives here too because it is voice-dependent — a naturally faster voice overshoots
/// the reference channel's ~190 WPM at a rate tuned for a slower one.
/// </summary>
public sealed class VoiceSettingsStore
{
    private const string VoiceKey = "tts.voiceId";
    private const string RateKey = "tts.speechRate";

    private readonly JobStore _store;
    private readonly TtsOptions _opt;
    private readonly ILogger<VoiceSettingsStore> _log;

    public VoiceSettingsStore(JobStore store, IOptions<TtsOptions> opt, ILogger<VoiceSettingsStore> log)
    {
        _store = store; _opt = opt.Value; _log = log;
    }

    /// <summary>Voice actually used for synthesis right now.</summary>
    public string VoiceId
    {
        get
        {
            var v = _store.GetSetting(VoiceKey);
            return string.IsNullOrWhiteSpace(v) ? _opt.ElevenLabsVoiceId : v;
        }
    }

    /// <summary>Post-processing tempo (ffmpeg atempo) actually applied right now.</summary>
    public double SpeechRate
    {
        get
        {
            var v = _store.GetSetting(RateKey);
            return double.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0
                ? d
                : _opt.ElevenLabsSpeechRate;
        }
    }

    /// <summary>True when the value came from the Settings page rather than .env.</summary>
    public bool VoiceOverridden => !string.IsNullOrWhiteSpace(_store.GetSetting(VoiceKey));

    public void Save(string? voiceId, double? speechRate)
    {
        if (!string.IsNullOrWhiteSpace(voiceId))
        {
            _store.SetSetting(VoiceKey, voiceId.Trim());
            _log.LogInformation("TTS voice set to {Voice} from settings.", Short(voiceId.Trim()));
        }
        if (speechRate is { } r && r > 0)
            _store.SetSetting(RateKey, Math.Clamp(r, 0.5, 2.0).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Drop the app-saved values so .env applies again.</summary>
    public void Reset()
    {
        _store.DeleteSetting(VoiceKey);
        _store.DeleteSetting(RateKey);
        _log.LogInformation("TTS voice/rate reset to the configured defaults.");
    }

    private static string Short(string id) => id.Length <= 8 ? id : id[..8] + "…";
}
