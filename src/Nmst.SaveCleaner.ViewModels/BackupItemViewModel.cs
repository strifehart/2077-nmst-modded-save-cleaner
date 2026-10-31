using Nmst.SaveCleaner.Core.Backups;
using Nmst.SaveCleaner.Core.Localization;
using Nmst.SaveCleaner.Core.Saves;

namespace Nmst.SaveCleaner.ViewModels;

/// <summary>One field of a backup card, flagged when it differs from the current save.</summary>
public sealed record CompareField(string Label, string Value, bool Differs);

public sealed class BackupItemViewModel
{
    public BackupItemViewModel(BackupInfo backup, SaveItemViewModel current, GameNames names)
    {
        Backup = backup;
        var m = backup.Metadata;
        var c = current.Info.Metadata;
        string state = Format.State(backup.State);
        string saved = m?.Timestamp is { } t ? Format.DateTime(t) : "?";
        string play = m is null ? "?" : Format.Duration(m.PlayTime);
        string level = m?.Level.ToString() ?? "?";
        string loc = names.Text(m?.LocationName) ?? "?";
        string quest = names.Text(m?.TrackedQuest) ?? names.QuestFromEntry(m?.TrackedQuestEntry) ?? "—";

        Fields =
        [
            new("Saved in game", saved, c?.Timestamp is { } ct && Format.DateTime(ct) != saved),
            new("Modded state", state, current.StateLabel != state),
            new("Playtime", play, current.PlayTime != play),
            new("Level", level, current.Level != level),
            new("Location", loc, current.Location != loc),
            new("Tracked quest", quest, current.TrackedQuest != quest),
            new("Size", Format.Size(backup.SizeBytes), false),
        ];
        DiffCount = Fields.Count(f => f.Differs);
        IsFromOtherFolder = backup.IsFromOtherFolder(current.Info.FolderPath);
    }

    /// <summary>Taken from a same-named save in another saves folder (backups are filed by folder name).</summary>
    public bool IsFromOtherFolder { get; }
    public string OtherFolderNote => IsFromOtherFolder ? "From another saves folder: " + Backup.SourceFolder : "";

    public BackupInfo Backup { get; }
    public IReadOnlyList<CompareField> Fields { get; }
    public int DiffCount { get; }
    public string Title => $"{Backup.Reason} · {Format.DateTime(Backup.Created)}";
    public string DiffSummary => DiffCount == 0 ? "Same as the current save" : $"{DiffCount} difference{(DiffCount == 1 ? "" : "s")} from the current save";
    public bool IsTaggedBackup => Backup.State is ModdedState.Tagged or ModdedState.StickyOnly;
    public bool HasDifferences => DiffCount > 0;
}
