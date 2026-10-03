using System.Collections.Generic;
using System.IO;

namespace MemeShorts.Api.Services;

/// <summary>
/// Minimal .env loader. ASP.NET Core does not read .env files natively, so this
/// parses a KEY=VALUE file and maps friendly names to the app's config keys, returning
/// a dictionary suitable for <c>IConfigurationBuilder.AddInMemoryCollection</c>.
///
/// Supported keys in the .env file:
///   ELEVENLABS_API_KEY    -> Tts:ElevenLabsApiKey
///   ELEVENLABS_VOICE_ID   -> Tts:ElevenLabsVoiceId
///   ELEVENLABS_MODEL_ID   -> Tts:ElevenLabsModelId
///   TTS_PROVIDER          -> Tts:Provider
///   FACEBOOK_PAGE_ID / FACEBOOK_PAGE_ACCESS_TOKEN / FACEBOOK_ENABLED   -> Facebook:*
///   INSTAGRAM_USER_ID / INSTAGRAM_ACCESS_TOKEN / INSTAGRAM_ENABLED     -> Instagram:*
/// Any other key is passed through as-is, with "__" converted to ":" so you can also
/// write raw config keys (e.g. Tts__ElevenLabsApiKey).
/// Empty values are ignored so they never blank out appsettings.json.
/// </summary>
public static class DotEnv
{
    private static readonly Dictionary<string, string> Aliases = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["ELEVENLABS_API_KEY"] = "Tts:ElevenLabsApiKey",
        ["ELEVENLABS_VOICE_ID"] = "Tts:ElevenLabsVoiceId",
        ["ELEVENLABS_MODEL_ID"] = "Tts:ElevenLabsModelId",
        ["ELEVENLABS_OUTPUT_FORMAT"] = "Tts:ElevenLabsOutputFormat",
        ["ELEVENLABS_MAX_RERENDERS"] = "Tts:ElevenLabsMaxRerenders",
        ["ELEVENLABS_STABILITY"] = "Tts:ElevenLabsStability",
        ["ELEVENLABS_SIMILARITY_BOOST"] = "Tts:ElevenLabsSimilarityBoost",
        ["ELEVENLABS_STYLE"] = "Tts:ElevenLabsStyle",
        ["ELEVENLABS_SPEAKER_BOOST"] = "Tts:ElevenLabsSpeakerBoost",
        ["TTS_PROVIDER"] = "Tts:Provider",

        // Meta publishing. Keep the long-lived tokens in .env — appsettings.json is committed.
        ["FACEBOOK_ENABLED"] = "Facebook:Enabled",
        ["FACEBOOK_PAGE_ID"] = "Facebook:PageId",
        ["FACEBOOK_PAGE_ACCESS_TOKEN"] = "Facebook:PageAccessToken",
        ["INSTAGRAM_ENABLED"] = "Instagram:Enabled",
        ["INSTAGRAM_USER_ID"] = "Instagram:IgUserId",
        ["INSTAGRAM_ACCESS_TOKEN"] = "Instagram:AccessToken",

        // Stock photos looked up before Codex draws a picture (see StockPhotoSources).
        ["PEXELS_API_KEY"] = "Pictures:PexelsApiKey",
    };

    /// <summary>
    /// Reads the .env file (searching the current directory and up to 5 parents) and
    /// returns config-key/value pairs. Returns an empty dictionary if no file is found.
    /// </summary>
    public static Dictionary<string, string?> Read(string? startDir = null)
    {
        var result = new Dictionary<string, string?>();
        var path = Find(startDir ?? Directory.GetCurrentDirectory());
        if (path is null) return result;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim().Trim('"', '\'');
            if (val.Length == 0) continue; // don't override appsettings with blanks

            if (Aliases.TryGetValue(key, out var mapped))
                result[mapped] = val;
            else
                result[key.Replace("__", ":")] = val;
        }
        return result;
    }

    private static string? Find(string dir)
    {
        var current = new DirectoryInfo(dir);
        for (var i = 0; i < 6 && current is not null; i++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, ".env");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
