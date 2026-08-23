using System.Text.RegularExpressions;

namespace MemeShorts.Api.Services;

/// <summary>
/// Renders a platform caption from a template. Placeholders mirror the YouTube
/// DescriptionTemplate so the config reads the same way across destinations:
/// <c>{title}</c>, <c>{description}</c> (the full YouTube description, music attribution
/// included) and <c>{tags}</c> (the configured hashtags).
/// </summary>
public static class SocialCaption
{
    /// <summary>Instagram's caption cap; Facebook's is far higher, so this is safe for both.</summary>
    public const int MaxLength = 2200;

    public static string Build(string template, string title, string description, IReadOnlyList<string> hashtags)
    {
        var tags = string.Join(" ", hashtags
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Select(t => t.StartsWith('#') ? t : "#" + t.Replace(" ", "")));

        var text = (string.IsNullOrWhiteSpace(template) ? "{title}\n\n{tags}" : template)
            .Replace("{title}", title ?? "")
            .Replace("{description}", description ?? "")
            .Replace("{tags}", tags);

        // A template whose placeholders resolved to nothing leaves runs of blank lines behind.
        text = Regex.Replace(text.Replace("\r\n", "\n"), @"\n{3,}", "\n\n").Trim();

        return text.Length <= MaxLength ? text : text[..MaxLength].TrimEnd();
    }
}
