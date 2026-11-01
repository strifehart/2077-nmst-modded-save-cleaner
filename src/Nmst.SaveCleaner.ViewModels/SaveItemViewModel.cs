using CommunityToolkit.Mvvm.ComponentModel;
using Nmst.SaveCleaner.Core.Localization;
using Nmst.SaveCleaner.Core.Playthroughs;
using Nmst.SaveCleaner.Core.Saves;

namespace Nmst.SaveCleaner.ViewModels;

public sealed partial class SaveItemViewModel : ObservableObject
{
    private readonly GameNames _names;

    public SaveItemViewModel(SaveInfo info, GameNames names, int backupCount)
    {
        _names = names;
        Info = info;
        _backupCount = backupCount;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(ListLine), nameof(Location), nameof(TrackedQuest), nameof(Objective), nameof(MainQuest),
        nameof(MetaLine), nameof(PlayTime), nameof(Level), nameof(StreetCred), nameof(LifePath), nameof(Patch), nameof(Body), nameof(Reflexes),
        nameof(Tech), nameof(Intelligence), nameof(Cool), nameof(StateDetail), nameof(Pills), nameof(BuildLine), nameof(BuildDetails),
        nameof(OriginalName), nameof(IsRenamed), nameof(WasLine), nameof(RenamedFromLine), nameof(IsInRotatingSlot))]
    private SaveInfo _info;

    /// <summary>Other folders holding the same game save (same playthrough and in-game save time); empty when none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDuplicates), nameof(DuplicateLine))]
    private IReadOnlyList<string> _duplicateOf = [];
    public bool HasDuplicates => DuplicateOf.Count > 0;
    public string DuplicateLine => DuplicateOf.Count switch
    {
        0 => "",
        1 => "Same save as " + DuplicateOf[0],
        _ => $"Same save as {DuplicateOf[0]} and {DuplicateOf.Count - 1} more",
    };

    /// <summary>Starred by the user (kept in saves.json, never in the save).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StarTip))]
    private bool _isStarred;
    public string StarTip => IsStarred ? "Starred · click to unstar" : "Star this save";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel), nameof(StateDetail), nameof(IsTagged), nameof(IsClean))]
    private ModdedState _state = ModdedState.Unknown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateDetail))]
    private bool? _sticky;

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isFocused;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackups))]
    private int _backupCount;

    /// <summary>Total size of this save's backups; filled in when the save is focused and its backups are listed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BuildLine), nameof(BuildDetails))]
    private long _backupsBytes;

    /// <summary>Why the current search matched ("quest", "objective"…); empty when it matched the name or there's no search.</summary>
    [ObservableProperty] private string _matchHint = "";

    private SaveMetadata? M => Info.Metadata;

    public string Name => Info.FolderName;

    /// <summary>The name the game gave the save, kept in its metadata however the folder is renamed.</summary>
    public string OriginalName => M?.Name ?? "";
    /// <summary>
    /// Has a name of its own. A folder still in the game's pattern but with another number (ManualSave-1307 holding
    /// ManualSave-1306) is a copied save, not a renamed one.
    /// </summary>
    public bool IsRenamed => OriginalName.Length > 0 && !string.Equals(OriginalName, Name, StringComparison.OrdinalIgnoreCase)
                             && !SaveRenamer.IsGameName(Name);
    public string WasLine => IsRenamed ? "was " + OriginalName : "";
    public string RenamedFromLine => IsRenamed ? "Renamed from " + OriginalName : "";
    /// <summary>Still in an auto/quick slot the game reuses: "Keep this autosave" moves it out.</summary>
    public bool IsInRotatingSlot => SaveInfo.KindOf(Name) is SaveKind.Auto or SaveKind.Quick;
    public bool IsReadable => M is not null;
    public bool IsTagged => State is ModdedState.Tagged or ModdedState.StickyOnly;
    public bool IsClean => State == ModdedState.Clean;
    public bool HasBackups => BackupCount > 0;
    public string StateLabel => IsReadable ? Format.State(State) : "Unreadable";
    public string StateDetail => M is null ? Info.Error ?? "" : Format.StateDetail(M.IsModded, Sticky);

    public string ListLine => M is null ? Info.Error ?? "Unreadable" : $"Lv {M.Level} · {Format.Date(Info.Timestamp)} · {M.Patch}";
    public string Location => _names.Text(M?.LocationName) ?? "Unknown location";
    public string TrackedQuest => _names.Text(M?.TrackedQuest) ?? _names.QuestFromEntry(M?.TrackedQuestEntry) ?? "No tracked quest";
    public string Objective => _names.Objective(M?.TrackedQuestEntry) ?? "";
    public string MainQuest => _names.Text(M?.MainQuest) ?? "—";
    public string MetaLine => $"{Location} · {Format.DateTime(Info.Timestamp)}";
    public string PlayTime => M is null ? "—" : Format.Duration(M.PlayTime);
    public string Level => M?.Level.ToString() ?? "—";
    public string StreetCred => M?.StreetCred.ToString() ?? "—";
    public string LifePath => Playthrough.LifePathLabel(M?.LifePath ?? "");
    public string Patch => M?.Patch ?? "?";
    public int Body => M?.Attributes.Body ?? 0;
    public int Reflexes => M?.Attributes.Reflexes ?? 0;
    public int Tech => M?.Attributes.TechnicalAbility ?? 0;
    public int Intelligence => M?.Attributes.Intelligence ?? 0;
    public int Cool => M?.Attributes.Cool ?? 0;
    public int FinishedQuests => M?.FinishedQuests.Count ?? 0;
    public string? ScreenshotPath => Info.ScreenshotPath;
    public string BuildLine => M is null ? "" :
        $"build {ShortBuild(M.BuildId)} · save format {M.SaveVersion} · playthrough {M.PlaythroughId} · {Format.Size(Info.SizeBytes)}" +
        (BackupsBytes > 0 ? $" · {Format.Size(Info.SizeBytes + BackupsBytes)} with backups" : "");

    /// <summary>"3.0.5813403  P4CL: 13555055  Stream: //R6.Root/R6.Release" → "3.0.5813403" (the rest is CDPR's source-control detail).</summary>
    private static string ShortBuild(string build) => build.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    /// <summary>One tooltip explaining every value in <see cref="BuildLine"/>.</summary>
    public IReadOnlyList<TechDetail> BuildDetails => M is null ? [] :
    [
        new("Saved with", ShortBuild(M.BuildId), "The game build that made this save."),
        new("Playthrough started on", M.InitialBuildId.Length > 0 ? ShortBuild(M.InitialBuildId) : "unknown", "The game build this playthrough began on."),
        new("Save format", M.SaveVersion.ToString(), "The save file format's version."),
        new("Playthrough ID", M.PlaythroughId, "Shared by every save from the same playthrough."),
        new("Size on disk", Format.Size(Info.SizeBytes), "All of this save's files together."),
        .. BackupsBytes > 0
            ? new[] { new TechDetail("With backups", Format.Size(Info.SizeBytes + BackupsBytes),
                $"This save plus its {BackupCount} backup{(BackupCount == 1 ? "" : "s")} ({Format.Size(BackupsBytes)}).") }
            : [],
    ];

    public IReadOnlyList<string> Pills
    {
        get
        {
            if (M is null) return [];
            var p = new List<string> { "Patch " + M.Patch };
            if (M.Difficulty.Length > 0) p.Add(M.Difficulty + " difficulty");
            if (M.HasPhantomLiberty) p.Add("Phantom Liberty");
            if (M.BodyGender.Length > 0) p.Add(M.BodyGender == "Female" ? "Female V" : "Male V");
            if (M.IsPointOfNoReturn) p.Add("Point of no return");
            if (M.IsEndGameSave) p.Add("End game");
            return p;
        }
    }

    /// <summary>Case-insensitive search over the folder name (and original name), tracked quest, objective and main quest.</summary>
    public bool Matches(string query)
    {
        if (query.Length == 0) { MatchHint = ""; return true; }
        bool Has(string s) => s.Contains(query, StringComparison.OrdinalIgnoreCase);
        if (Has(Name)) { MatchHint = ""; return true; }
        if (M is null) return false;
        if (IsRenamed && Has(OriginalName)) { MatchHint = "Was " + OriginalName; return true; }
        if (Has(TrackedQuest)) { MatchHint = "Quest: " + TrackedQuest; return true; }
        if (Has(Objective)) { MatchHint = "Objective: " + Objective; return true; }
        if (Has(MainQuest)) { MatchHint = "Main quest: " + MainQuest; return true; }
        if (Has(Location)) { MatchHint = "Location: " + Location; return true; }
        MatchHint = "";
        return false;
    }
}

/// <summary>One labelled value in the technical footer's tooltip, with what it means.</summary>
public sealed record TechDetail(string Label, string Value, string Description);
