namespace Nmst.SaveCleaner.Core.Backups;

/// <summary>Which backup a bulk restore picks for each save.</summary>
public enum RestoreRule
{
    /// <summary>The most recent "Before clean" backup: the save as it was before the app cleaned it.</summary>
    UndoCleaning,
    /// <summary>The most recent backup made with "Back up".</summary>
    LatestManual,
    /// <summary>The most recent backup of any kind (can be a "Before restore" copy of the state you restored away from).</summary>
    Newest,
}

/// <summary>
/// The plan for one save: every restore point it could use (newest first), the one picked, and why it's skipped
/// (Chosen null) or worth a look.
/// </summary>
public sealed record RestorePlanEntry(
    string SaveFolderName,
    IReadOnlyList<BackupInfo> Candidates,
    BackupInfo? Chosen,
    string? SkipReason,
    bool NewerExists,
    bool ChangedSinceBatch = false);

public static class RestorePlanner
{
    public const string BeforeClean = "Before clean";
    public const string Manual = "Manual";

    /// <summary>
    /// Picks a backup for one save by the rule. Backups taken from a different saves folder are never picked
    /// automatically (they stay selectable), and a backup identical to the save as it is now is skipped.
    /// </summary>
    public static RestorePlanEntry Plan(string saveFolder, IReadOnlyList<BackupInfo> backups, RestoreRule rule)
    {
        var name = Path.GetFileName(saveFolder);
        var own = backups.Where(b => !b.IsFromOtherFolder(saveFolder)).ToList();   // newest first, as BackupManager.List returns them
        var chosen = rule switch
        {
            RestoreRule.UndoCleaning => own.FirstOrDefault(b => b.Reason == BeforeClean),
            RestoreRule.LatestManual => own.FirstOrDefault(b => b.Reason == Manual),
            _ => own.FirstOrDefault(),
        };
        if (chosen is null)
        {
            string why = backups.Count == 0 ? "no backups"
                : own.Count == 0 ? "only backups from another saves folder"
                : rule switch { RestoreRule.UndoCleaning => "no Before clean backup", RestoreRule.LatestManual => "no manual backup", _ => "no backups" };
            return new(name, backups, null, why, false);
        }
        if (BackupManager.IsSameAs(chosen, saveFolder)) return new(name, backups, null, "already the same as the backup", false);
        return new(name, backups, chosen, null, own.Any(b => b.Created > chosen.Created));
    }

    /// <summary>
    /// Plans undoing a batch: each save gets the backup that batch made of it. Saves the game wrote to since the batch
    /// are flagged (and left out unless picked by hand), since undoing would lose that progress.
    /// </summary>
    public static IReadOnlyList<RestorePlanEntry> PlanUndo(BatchRecord batch, BackupManager backups, string savesFolder) =>
        batch.Saves.Select(s =>
        {
            var all = backups.List(s.SaveFolderName);
            var mine = all.FirstOrDefault(b => b.Id == s.BackupId);
            if (mine is null) return new RestorePlanEntry(s.SaveFolderName, all, null, "its backup from that batch is gone", false);
            var folder = Path.Combine(savesFolder, s.SaveFolderName);
            if (BackupManager.IsSameAs(mine, folder)) return new RestorePlanEntry(s.SaveFolderName, all, null, "already the same as before the batch", false);
            return new RestorePlanEntry(s.SaveFolderName, all, mine, null, false, BatchLog.ChangedSince(s, savesFolder));
        }).ToList();
}
