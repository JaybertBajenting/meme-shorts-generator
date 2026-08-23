namespace MemeShorts.Api.Services;

public static class JsonExtract
{
    /// <summary>
    /// Pulls the first balanced top-level JSON object out of arbitrary text
    /// (Codex may print log lines around the JSON). Brace-aware, string-aware.
    /// </summary>
    public static string? FirstJsonObject(string text) => FirstBalanced(text, '{', '}');

    /// <summary>Pulls the first balanced top-level JSON array out of arbitrary text.</summary>
    public static string? FirstJsonArray(string text) => FirstBalanced(text, '[', ']');

    private static string? FirstBalanced(string text, char open, char close)
    {
        int start = text.IndexOf(open);
        if (start < 0) return null;

        bool inString = false, escape = false;
        int depth = 0;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == open) depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0) return text.Substring(start, i - start + 1);
            }
        }
        return null;
    }
}
