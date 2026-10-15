namespace AISuitePanel;

/// <summary>
/// Remembers which dotted aliases a tool has already burned, so the same address
/// is never handed out twice. Storage is per tool:
/// <c>%APPDATA%\AISuitePanel\used-aliases.json</c>.
/// </summary>
public static class AliasGuard
{
    private static readonly Dictionary<string, HashSet<string>> Used = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static void Load()
    {
        var data = Json.Load(AppPaths.UsedAliases, () => new Dictionary<string, List<string>>());

        lock (Gate)
        {
            Used.Clear();
            foreach (var (toolId, aliases) in data)
                Used[toolId] = new HashSet<string>(aliases, StringComparer.OrdinalIgnoreCase);
        }
    }

    public static bool IsUsed(string toolId, string alias)
    {
        lock (Gate)
            return Used.TryGetValue(toolId, out var set) && set.Contains(alias);
    }

    public static void Mark(string toolId, string alias)
    {
        bool changed;

        lock (Gate)
        {
            if (!Used.TryGetValue(toolId, out var set))
                Used[toolId] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            changed = set.Add(alias);
        }

        if (changed) Save();
    }

    public static int Count(string toolId)
    {
        lock (Gate)
            return Used.TryGetValue(toolId, out var set) ? set.Count : 0;
    }

    /// <summary>Forgets the history of one tool (not wired to a button yet).</summary>
    public static void Reset(string toolId)
    {
        lock (Gate)
            Used.Remove(toolId);

        Save();
    }

    private static void Save()
    {
        Dictionary<string, List<string>> snapshot;

        lock (Gate)
            snapshot = Used.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());

        Json.Save(AppPaths.UsedAliases, snapshot);
    }
}
