using MemeShorts.Api.Models;
using Microsoft.Extensions.Options;

namespace MemeShorts.Api.Services;

/// <summary>
/// Holds the configured channel profiles and resolves an <see cref="EffectiveChannel"/> by
/// overlaying a profile onto the global option defaults. If no profiles are configured, a
/// single "default" channel mirroring the globals is returned (current behaviour).
/// </summary>
public sealed class ChannelRegistry
{
    private readonly List<ChannelProfile> _profiles;
    private readonly PipelineOptions _pipe;
    private readonly TtsOptions _tts;
    private readonly CodexOptions _codex;
    private readonly TitleStyleOptions _titles;
    private readonly UploadOptions _upload;

    public ChannelRegistry(
        IOptions<ChannelsOptions> channels, IOptions<PipelineOptions> pipe, IOptions<TtsOptions> tts,
        IOptions<CodexOptions> codex, IOptions<TitleStyleOptions> titles, IOptions<UploadOptions> upload)
    {
        _profiles = channels.Value.Profiles ?? new();
        _pipe = pipe.Value; _tts = tts.Value; _codex = codex.Value; _titles = titles.Value; _upload = upload.Value;
    }

    /// <summary>Profiles for the UI dropdown (id + name + contentType).</summary>
    public IReadOnlyList<ChannelProfile> Profiles =>
        _profiles.Count > 0 ? _profiles : new List<ChannelProfile> { new() { Id = "default", Name = "Default", ContentType = "meme" } };

    public string DefaultId => Profiles[0].Id;

    public EffectiveChannel Resolve(string? channelId)
    {
        var profile = _profiles.FirstOrDefault(p => string.Equals(p.Id, channelId, StringComparison.OrdinalIgnoreCase))
                      ?? _profiles.FirstOrDefault(p => string.Equals(p.Id, "default", StringComparison.OrdinalIgnoreCase))
                      ?? _profiles.FirstOrDefault();

        // No profiles configured: synthesize one from the globals.
        profile ??= new ChannelProfile { Id = "default", Name = "Default", ContentType = "meme" };

        var u = profile.Upload;
        return new EffectiveChannel
        {
            Id = profile.Id,
            Name = profile.Name,
            ContentType = profile.ContentType,
            Format = profile.Format,

            TitleReferenceChannel = profile.TitleReferenceChannel ?? _titles.ReferenceChannel,
            TitleTab = profile.TitleTab ?? _titles.Tab,

            BackgroundDir = profile.BackgroundDir ?? _pipe.BackgroundDir,
            MusicDir = profile.MusicDir ?? _pipe.MusicDir,
            MascotPath = profile.MascotPath,
            WindowsVoice = profile.WindowsVoice ?? _tts.WindowsVoice,
            WindowsRate = profile.WindowsRate ?? _tts.WindowsRate,
            PanelSource = profile.PanelSource ?? _codex.PanelSource,

            UploadEnabled = u?.Enabled ?? _upload.Enabled,
            AutoUpload = u?.AutoUpload ?? _upload.AutoUpload,
            ClientSecretsPath = u?.ClientSecretsPath ?? _upload.ClientSecretsPath,
            TokenStorePath = u?.TokenStorePath ?? _upload.TokenStorePath,
            PublishTimesLocal = FirstNonEmpty(u?.PublishTimesLocal, _upload.PublishTimesLocal, UploadOptions.DefaultPublishTimes),
            Tags = FirstNonEmpty(u?.Tags, _upload.Tags, UploadOptions.DefaultTags),
            CategoryId = u?.CategoryId ?? _upload.CategoryId,
            TitleSuffix = u?.TitleSuffix ?? _upload.TitleSuffix,
            DescriptionTemplate = u?.DescriptionTemplate ?? _upload.DescriptionTemplate,
        };
    }

    private static string[] FirstNonEmpty(params string[]?[] arrays) =>
        arrays.FirstOrDefault(a => a is { Length: > 0 }) ?? Array.Empty<string>();
}
