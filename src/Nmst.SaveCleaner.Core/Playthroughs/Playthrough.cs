using Nmst.SaveCleaner.Core.Saves;

namespace Nmst.SaveCleaner.Core.Playthroughs;

/// <summary>
/// Saves that share a playthroughID: the game creates it when a new game starts and every save of that run keeps it.
/// This is what the game's own load menu groups by. Saves without one go into a single ungrouped bucket.
/// </summary>
public sealed record Playthrough(string? Id, IReadOnlyList<SaveInfo> Saves)
{
    public bool IsUngrouped => Id is null;
    public string LifePath => Saves.Select(s => s.Metadata?.LifePath).FirstOrDefault(l => !string.IsNullOrEmpty(l)) ?? "";
    public TimeSpan MaxPlayTime => Saves.Select(s => s.Metadata?.PlayTime ?? TimeSpan.Zero).DefaultIfEmpty().Max();
    public int MaxLevel => Saves.Select(s => s.Metadata?.Level ?? 0).DefaultIfEmpty().Max();
    public DateTime Latest => Saves.Select(s => s.Timestamp).DefaultIfEmpty().Max();

    /// <summary>Groups saves like the load menu: newest playthrough first, newest save first, ungrouped last.</summary>
    public static IReadOnlyList<Playthrough> Group(IEnumerable<SaveInfo> saves)
    {
        var groups = saves
            .GroupBy(s => s.PlaythroughId)
            .Select(g => new Playthrough(g.Key, g.OrderByDescending(s => s.Timestamp).ToList()))
            .ToList();
        return groups.Where(g => !g.IsUngrouped).OrderByDescending(g => g.Latest)
            .Concat(groups.Where(g => g.IsUngrouped))
            .ToList();
    }

    /// <summary>"Corporate" → "Corpo", "StreetKid" → "Streetkid", as the game's menus show it.</summary>
    public static string LifePathLabel(string lifePath) => lifePath switch
    {
        "Corporate" => "Corpo",
        "StreetKid" => "Streetkid",
        "Nomad" => "Nomad",
        "" => "Unknown",
        _ => lifePath,
    };
}
