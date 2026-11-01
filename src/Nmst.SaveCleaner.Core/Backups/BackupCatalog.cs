using Nmst.SaveCleaner.Core.Saves;

namespace Nmst.SaveCleaner.Core.Backups;

/// <summary>Why a backup is kept no matter what: never offered for cleanup.</summary>
[Flags]
public enum BackupProtection
{
    None = 0,
    /// <summary>The save's current version as the game wrote it, before the app changed it. Can't be recreated.</summary>
    Original = 1,
    /// <summary>Made on purpose with "Back up".</summary>
    Manual = 2,
    /// <summary>Needed by "Undo last batch".</summary>
    Undo = 4,
}

/// <summary>Kinds of backup the cleanup offers, safest first.</summary>
public enum CleanupGroup
{
    /// <summary>An in-between state (typically the cleaned save) that cleaning again recreates.</summary>
    BeforeRestore,
    /// <summary>The save folder is gone (deleted in game or by hand).</summary>
    SaveGone,
    /// <summary>A save the game has since overwritten in an auto/quick slot.</summary>
    OldVersion,
    /// <summary>Byte for byte the save as it is now: the save itself is that copy.</summary>
    SameAsSave,
}

public sealed record CleanupCandidate(BackupInfo Backup, CleanupGroup Group);

public static class BackupCatalog
{
    public const string BeforeRestore = "Before restore";

    /// <summary>
    /// Protection of one backup, given all backups of its save (<paramref name="siblings"/>), the save as it is now
    /// (null when gone) and the backup ids the current undo needs.
    /// </summary>
    public static BackupProtection Protection(BackupInfo b, IReadOnlyList<BackupInfo> siblings, SaveMetadata? current, IReadOnlySet<string> undoIds)
    {
        var p = BackupProtection.None;
        if (b.Reason == RestorePlanner.Manual) p |= BackupProtection.Manual;
        if (undoIds.Contains(b.Id)) p |= BackupProtection.Undo;
        if (OriginalOf(siblings, current)?.Id == b.Id) p |= BackupProtection.Original;
        return p;
    }

    /// <summary>
    /// The oldest "Before clean" backup of the save's current version: same playthrough and same in-game save time
    /// (cleaning never changes either). Null when the save is gone or was never cleaned in this version.
    /// </summary>
    public static BackupInfo? OriginalOf(IReadOnlyList<BackupInfo> siblings, SaveMetadata? current)
    {
        if (current is null) return null;
        return siblings
            .Where(b => b.Reason == RestorePlanner.BeforeClean && b.Metadata is { } m
                        && m.PlaythroughId == current.PlaythroughId && m.Timestamp == current.Timestamp)
            .MinBy(b => b.Created);
    }

    /// <summary>
    /// Every backup the cleanup may offer, across all saves, with its group. Protected backups and backups taken
    /// from another saves folder are never included.
    /// </summary>
    public static IReadOnlyList<CleanupCandidate> Candidates(BackupManager backups, string savesFolder, IReadOnlySet<string> undoIds)
    {
        var result = new List<CleanupCandidate>();
        foreach (var name in backups.SaveNames())
        {
            var all = backups.List(name);
            var folder = Path.Combine(savesFolder, name);
            bool exists = File.Exists(Path.Combine(folder, SaveInfo.SavFileName));
            var current = exists ? SaveScanner.Read(folder).Metadata : null;
            foreach (var b in all)
            {
                if (b.IsFromOtherFolder(folder)) continue;
                if (Protection(b, all, current, undoIds) != BackupProtection.None) continue;
                CleanupGroup? group =
                    !exists ? CleanupGroup.SaveGone
                    : BackupManager.IsSameAs(b, folder) ? CleanupGroup.SameAsSave
                    : b.Reason == BeforeRestore ? CleanupGroup.BeforeRestore
                    : b.Reason == RestorePlanner.BeforeClean && !SameVersion(b.Metadata, current) ? CleanupGroup.OldVersion
                    : null;
                if (group is { } g) result.Add(new(b, g));
            }
        }
        return result;
    }

    private static bool SameVersion(SaveMetadata? a, SaveMetadata? b) =>
        a is not null && b is not null && a.PlaythroughId == b.PlaythroughId && a.Timestamp == b.Timestamp;
}
