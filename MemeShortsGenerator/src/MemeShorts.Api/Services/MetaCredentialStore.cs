using MemeShorts.Api.Models;
using Microsoft.Extensions.Options;

namespace MemeShorts.Api.Services;

/// <summary>Live Meta credentials for one platform.</summary>
public sealed record MetaCredentials(bool Enabled, string AccountId, string Token)
{
    /// <summary>Enabled AND both fields present — i.e. the uploader can actually call the API.</summary>
    public bool Usable => Enabled
        && !string.IsNullOrWhiteSpace(AccountId)
        && !string.IsNullOrWhiteSpace(Token);
}

/// <summary>
/// Runtime-editable Facebook/Instagram credentials, so they can be set from the Settings page
/// instead of only from .env (which needs a process restart to take effect).
///
/// Resolution order is: value saved from the UI, else the configured .env/appsettings value.
/// Saving an empty token is treated as "leave the stored one alone" so the UI can round-trip a
/// masked token without wiping it.
///
/// Values live in the same SQLite settings table the auto-approve toggle uses, so they survive
/// restarts. Note this puts a long-lived access token in the local state DB — acceptable for a
/// single-user app on the user's own machine, and no worse than the .env file beside it, but it
/// is why the API never returns the token back to the browser in full.
/// </summary>
public sealed class MetaCredentialStore
{
    private const string FbEnabledKey = "meta.facebook.enabled";
    private const string FbPageIdKey = "meta.facebook.pageId";
    private const string FbTokenKey = "meta.facebook.token";
    private const string IgEnabledKey = "meta.instagram.enabled";
    private const string IgUserIdKey = "meta.instagram.igUserId";
    private const string IgTokenKey = "meta.instagram.token";

    private readonly JobStore _store;
    private readonly FacebookOptions _fb;
    private readonly InstagramOptions _ig;
    private readonly ILogger<MetaCredentialStore> _log;

    public MetaCredentialStore(JobStore store, IOptions<FacebookOptions> fb,
        IOptions<InstagramOptions> ig, ILogger<MetaCredentialStore> log)
    {
        _store = store; _fb = fb.Value; _ig = ig.Value; _log = log;
    }

    public MetaCredentials Facebook => new(
        Enabled: ReadBool(FbEnabledKey, _fb.Enabled),
        AccountId: Read(FbPageIdKey, _fb.PageId),
        Token: Read(FbTokenKey, _fb.PageAccessToken));

    public MetaCredentials Instagram => new(
        Enabled: ReadBool(IgEnabledKey, _ig.Enabled),
        AccountId: Read(IgUserIdKey, _ig.IgUserId),
        Token: Read(IgTokenKey, _ig.AccessToken));

    /// <summary>True when the value came from the UI rather than .env — shown in the settings page.</summary>
    public bool FacebookOverridden => _store.GetSetting(FbPageIdKey) is not null || _store.GetSetting(FbTokenKey) is not null;
    public bool InstagramOverridden => _store.GetSetting(IgUserIdKey) is not null || _store.GetSetting(IgTokenKey) is not null;

    public void SaveFacebook(bool? enabled, string? pageId, string? token)
    {
        if (enabled is { } e) _store.SetSetting(FbEnabledKey, e ? "true" : "false");
        if (pageId is not null) _store.SetSetting(FbPageIdKey, pageId.Trim());
        if (!string.IsNullOrWhiteSpace(token)) _store.SetSetting(FbTokenKey, token.Trim());
        _log.LogInformation("Facebook credentials updated from settings (token {Changed}).",
            string.IsNullOrWhiteSpace(token) ? "unchanged" : "replaced");
    }

    public void SaveInstagram(bool? enabled, string? igUserId, string? token)
    {
        if (enabled is { } e) _store.SetSetting(IgEnabledKey, e ? "true" : "false");
        if (igUserId is not null) _store.SetSetting(IgUserIdKey, igUserId.Trim());
        if (!string.IsNullOrWhiteSpace(token)) _store.SetSetting(IgTokenKey, token.Trim());
        _log.LogInformation("Instagram credentials updated from settings (token {Changed}).",
            string.IsNullOrWhiteSpace(token) ? "unchanged" : "replaced");
    }

    /// <summary>Drop the UI-saved values so the .env/appsettings ones take over again.</summary>
    public void ClearFacebook()
    {
        foreach (var k in new[] { FbEnabledKey, FbPageIdKey, FbTokenKey }) _store.DeleteSetting(k);
    }

    public void ClearInstagram()
    {
        foreach (var k in new[] { IgEnabledKey, IgUserIdKey, IgTokenKey }) _store.DeleteSetting(k);
    }

    /// <summary>Last 4 characters only — enough to confirm which token is loaded, useless if leaked.</summary>
    public static string Mask(string? token) =>
        string.IsNullOrWhiteSpace(token) ? "" :
        token.Length <= 4 ? new string('*', token.Length) :
        new string('*', 8) + token[^4..];

    private string Read(string key, string fallback)
    {
        var v = _store.GetSetting(key);
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }

    private bool ReadBool(string key, bool fallback)
    {
        var v = _store.GetSetting(key);
        return v is null ? fallback : v == "true";
    }
}
