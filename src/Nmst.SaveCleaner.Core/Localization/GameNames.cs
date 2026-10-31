using System.Text.Json;

namespace Nmst.SaveCleaner.Core.Localization;

/// <summary>
/// English quest, objective and location names for the LocKey codes saves store. Built from the game files by
/// tools/build-names.js and embedded as Resources/names_en.json.
/// </summary>
public sealed class GameNames
{
    private readonly Dictionary<string, string> _names;
    private readonly Dictionary<string, string> _quests;
    private readonly Dictionary<string, string> _objectives;

    private GameNames(Dictionary<string, string> names, Dictionary<string, string> quests, Dictionary<string, string> objectives)
    {
        _names = names; _quests = quests; _objectives = objectives;
    }

    public static GameNames Empty { get; } = new([], [], []);

    private sealed record Table(Dictionary<string, string> Names, Dictionary<string, string> Quests, Dictionary<string, string> Objectives);

    public static GameNames LoadEmbedded()
    {
        using var s = typeof(GameNames).Assembly.GetManifestResourceStream("Nmst.SaveCleaner.Core.Resources.names_en.json");
        if (s is null) return Empty;
        var t = JsonSerializer.Deserialize<Table>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return t is null ? Empty : new GameNames(t.Names, t.Quests, t.Objectives);
    }

    /// <summary>"LocKey#12380" → "Chippin' In". Returns null for empty or unknown keys.</summary>
    public string? Text(string? locKey)
    {
        if (string.IsNullOrEmpty(locKey)) return null;
        var k = locKey.StartsWith("LocKey#", StringComparison.Ordinal) ? locKey[7..] : locKey;
        return _names.TryGetValue(k, out var v) ? v : null;
    }

    /// <summary>Objective text for a trackedQuestEntry path; null for "[nothing]" or unknown paths.</summary>
    public string? Objective(string? entryPath) =>
        !string.IsNullOrEmpty(entryPath) && _objectives.TryGetValue(entryPath, out var v) ? v : null;

    /// <summary>Quest title from a trackedQuestEntry path, used when trackedQuest itself is empty.</summary>
    public string? QuestFromEntry(string? entryPath)
    {
        if (string.IsNullOrEmpty(entryPath)) return null;
        var parts = entryPath.Split('/');
        for (int n = parts.Length; n > 0; n--)
            if (_quests.TryGetValue(string.Join('/', parts.Take(n)), out var v)) return v;
        return null;
    }
}
