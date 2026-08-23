namespace MemeShorts.Api.Services;

/// <summary>
/// What Codex has already offered this session, including ideas that were DISCARDED.
///
/// Discarded ideas leave no trace anywhere else — no job, no topic reservation — so without
/// this, pressing "discard" then "suggest" rebuilt a byte-identical prompt and Codex returned
/// the same idea again. Held in memory only: a restart may repeat a discarded idea once, which
/// is a fair trade for not polluting the durable topic history with ideas that were rejected.
/// </summary>
public sealed class SuggestionHistory
{
    private const int Keep = 40;
    private readonly LinkedList<string> _recent = new();
    private readonly object _lock = new();

    public void Add(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        lock (_lock)
        {
            _recent.AddFirst(title.Trim());
            while (_recent.Count > Keep) _recent.RemoveLast();
        }
    }

    public IReadOnlyList<string> Recent(int take = Keep)
    {
        lock (_lock) return _recent.Take(take).ToList();
    }
}
