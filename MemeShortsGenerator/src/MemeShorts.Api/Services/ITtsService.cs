namespace MemeShorts.Api.Services;

public interface ITtsService
{
    /// <summary>
    /// Synthesize <paramref name="text"/> to a WAV file at <paramref name="outputWavPath"/>.
    /// <paramref name="emotion"/> is an optional delivery hint (e.g. "panicked", "whispering")
    /// performed by expressive models (ElevenLabs eleven_v3); ignored by others.
    /// <paramref name="windowsRateOverride"/> lets a channel profile set its own SAPI pace;
    /// cloud providers ignore it.
    /// Returns the measured duration in milliseconds (used to time the panels/captions).
    /// </summary>
    Task<int> SynthesizeAsync(string text, string outputWavPath, string? voiceOverride = null,
        string? emotion = null, CancellationToken ct = default, int? windowsRateOverride = null);
}
