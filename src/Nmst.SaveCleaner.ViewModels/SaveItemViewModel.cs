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
        nameof(Tech), nameof(Intelligence), nameof(Cool), nameof(StateDetail), nameof(Pills), nameof(BuildLine), nameof(BuildDetails))]
    private SaveInfo _info;

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

    /// <summary>Why the current search matched ("quest", "objective"…); empty when it matched the name or there's no search.</summary>
    [ObservableProperty] private string _matchHint = "";

    private SaveMetadata? M => Info.Metadata;

    public string Name => Info.FolderName;
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
        $"build {M.BuildId.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()} · save v{M.SaveVersion} · playthrough {M.PlaythroughId} · {Format.Size(Info.SizeBytes)}";

    /// <summary>One tooltip explaining every value in <see cref="BuildLine"/>.</summary>
    public IReadOnlyList<TechDetail> BuildDetails => M is null ? [] :
    [
        new("Build", M.BuildId, "The internal build of the game that wrote this save. More precise than the patch number."),
        new("Started on", M.InitialBuildId.Length > 0 ? M.InitialBuildId : "unknown", "The game build this playthrough began on."),
        new("Save version", M.SaveVersion.ToString(), "The save-file format. CDPR raises it when the structure changes, so check it first if a patch breaks cleaning."),
        new("Playthrough", M.PlaythroughId, "The run this save belongs to. The list groups saves by it, like the game's load menu."),
        new("Size", Format.Size(Info.SizeBytes), "The whole save folder: sav.dat, metadata.9.json and screenshot.png."),
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

    /// <summary>Case-insensitive search over the folder name, tracked quest, objective and main quest.</summary>
    public bool Matches(string query)
    {
        if (query.Length == 0) { MatchHint = ""; return true; }
        bool Has(string s) => s.Contains(query, StringComparison.OrdinalIgnoreCase);
        if (Has(Name)) { MatchHint = ""; return true; }
        if (M is null) return false;
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
