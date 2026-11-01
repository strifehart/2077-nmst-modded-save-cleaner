using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nmst.SaveCleaner.Core;
using Nmst.SaveCleaner.Core.Backups;
using Nmst.SaveCleaner.Core.Localization;
using Nmst.SaveCleaner.Core.Logging;
using Nmst.SaveCleaner.Core.Playthroughs;
using Nmst.SaveCleaner.Core.Saves;

namespace Nmst.SaveCleaner.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 5000;

    private readonly IPlatformServices _platform;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly GameNames _names = GameNames.LoadEmbedded();
    private readonly BackupManager _backups;
    private readonly TagCleaner _cleaner;
    private readonly BatchLog _batches;
    private readonly SaveNotes _notes;
    private List<SaveItemViewModel> _all = [];
    private List<PlaythroughGroupViewModel> _groups = [];
    private CancellationTokenSource? _flagScan;
    private readonly IMusicPlayer? _music;
    private readonly string? _settingsPath;
    private readonly AppSettings _settings;

    public MainViewModel(IPlatformServices platform, string savesFolder, string backupsRoot, string? logFile,
        IMusicPlayer? music = null, string? settingsPath = null)
    {
        _platform = platform;
        _music = music;
        _settingsPath = settingsPath;
        _settings = settingsPath is null ? new AppSettings() : AppSettings.Load(settingsPath);
        _musicMuted = _settings.MusicMuted;
        _musicVolume = Math.Round(_settings.MusicVolume * 100);
        _disclaimerAccepted = _settings.AcceptedTermsVersion >= DisclaimerText.Version;
        if (music is not null) music.NowPlayingChanged += () => OnUi(() => NowPlaying = music.NowPlaying);
        Log = new ActivityLog(logFile);
        Log.EntryAdded += e => OnUi(() =>
        {
            LogEntries.Add(e);
            while (LogEntries.Count > MaxLogLines) LogEntries.RemoveAt(0);
            if (e.Level == LogLevel.Error) LogErrorCount++;
            OnPropertyChanged(nameof(LogCount));
        });
        _backups = new BackupManager(backupsRoot, Log);
        _cleaner = new TagCleaner(_backups, Log);
        _batches = new BatchLog(Path.Combine(backupsRoot, "batches.json"));
        _notes = new SaveNotes(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(backupsRoot))!, "saves.json"));

        // a folder picked with "Change folder" wins over the detected one, as long as it still exists
        _defaultSavesFolder = savesFolder;
        _savesFolder = savesFolder;
        if (!string.IsNullOrEmpty(_settings.SavesFolder))
        {
            if (Directory.Exists(_settings.SavesFolder)) _savesFolder = _settings.SavesFolder;
            else Log.Warn($"The saves folder you chose before is gone ({_settings.SavesFolder}); using {savesFolder}");
        }
    }

    private readonly string _defaultSavesFolder;

    public ActivityLog Log { get; }
    public ObservableCollection<LogEntry> LogEntries { get; } = [];
    public int LogCount => LogEntries.Count;
    public string BackupsRoot => _backups.Root;
    public string Disclaimer => DisclaimerText.Body;

    // ---------- disclaimer (until the current terms version is accepted once) ----------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptDisclaimerCommand))]
    private bool _disclaimerTicked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoSaves))]
    private bool _disclaimerAccepted;

    [RelayCommand(CanExecute = nameof(DisclaimerTicked))]
    private async Task AcceptDisclaimer()
    {
        DisclaimerAccepted = true;
        _settings.AcceptedTermsVersion = DisclaimerText.Version;
        SaveSettingsNow();
        Log.Info($"Disclaimer accepted (terms v{DisclaimerText.Version})");
        await ReloadAsync();
    }

    /// <summary>Loads the saves straight away when the current terms were accepted on an earlier launch.</summary>
    public async Task StartAsync()
    {
        if (!DisclaimerAccepted) return;
        Log.Info($"Terms v{DisclaimerText.Version} already accepted");
        await ReloadAsync();
    }

    [RelayCommand] private void Quit() => _platform.Quit();

    // ---------- list ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomFolder))]
    private string _savesFolder;
    [ObservableProperty] private ObservableCollection<object> _rows = [];
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _statusLine = "";
    [ObservableProperty] private bool _isLoading;

    partial void OnSearchTextChanged(string value) => RebuildRows();

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (IsBusy) return;   // a running clean/restore holds on to the current list
        _flagScan?.Cancel();
        IsLoading = true;
        try
        {
            if (!Directory.Exists(SavesFolder))
            {
                Log.Error($"Saves folder not found: {SavesFolder}");
                _all = []; _groups = []; RebuildRows(); Focused = null;
                StatusLine = "Saves folder not found";
                NoSavesFound = true;
                NoSavesMessage = $"There's no folder at {SavesFolder}. If the game saves somewhere else, point the app at that folder.";
                return;
            }
            var (saves, counts) = await Task.Run(() => (SaveScanner.Scan(SavesFolder), _backups.Counts()));
            NoSavesFound = saves.Count == 0;
            NoSavesMessage = NoSavesFound
                ? $"No Cyberpunk 2077 saves in {SavesFolder}. Pick the folder that holds your ManualSave-… / AutoSave-… folders."
                : "";
            var items = saves.ToDictionary(s => s.FolderPath, s => new SaveItemViewModel(s, _names, counts.GetValueOrDefault(s.FolderName)));
            foreach (var i in items.Values) { i.IsStarred = _notes.IsStarred(i.Info); i.PropertyChanged += OnItemChanged; }
            _all = items.Values.ToList();
            _groups = Playthrough.Group(saves)
                .Select(p => new PlaythroughGroupViewModel(p, p.Saves.Select(s => items[s.FolderPath]).ToList(), RebuildRows))
                .ToList();
            if (_groups.Count > 0) _groups[0].IsExpanded = true;
            RebuildRows();
            Focused = _groups.FirstOrDefault()?.Items.FirstOrDefault();
            UpdateBatch();
            UpdateStatus();
            RefreshLastBatch();
            Log.Info($"Scanned {SavesFolder}:{_all.Count} saves in {_groups.Count(g => !g.Playthrough.IsUngrouped)} playthroughs");
            StartFlagScan();
        }
        catch (Exception e)
        {
            Log.Error("Scan failed: " + e.Message);
        }
        finally { IsLoading = false; }
    }

    /// <summary>Reads the sticky sav.dat flag of every save in the background (one chunk each), newest saves first.</summary>
    private void StartFlagScan()
    {
        var cts = _flagScan = new CancellationTokenSource();
        var todo = _groups.SelectMany(g => g.Items).Where(i => i.IsReadable).ToList();
        Task.Run(() =>
        {
            // results go to the UI in chunks, each applied together with the status refresh it affects
            foreach (var chunk in todo.Chunk(25))
            {
                if (cts.IsCancellationRequested) return;
                var results = chunk.Select(i => (Item: i, Result: ReadState(i.Info))).ToList();
                OnUi(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    foreach (var (item, (state, sticky)) in results) { item.Sticky = sticky; item.State = state; }
                    UpdateStatus();
                    UpdateBatch();
                });
            }
        }, cts.Token);
    }

    private static (ModdedState, bool?) ReadState(SaveInfo info)
    {
        if (info.Metadata is null) return (ModdedState.Unknown, null);
        try
        {
            bool? sticky = ModdedFlag.ReadSticky(info.SavPath);
            return (SaveInfo.Combine(info.Metadata.IsModded, sticky ?? false), sticky);
        }
        catch { return (info.Metadata.IsModded ? ModdedState.Tagged : ModdedState.Unknown, null); }
    }

    private void RebuildRows()
    {
        var q = SearchText.Trim();
        bool filtering = q.Length > 0 || ShowStarredOnly;
        var rows = new List<object>();
        foreach (var g in _groups)
        {
            var visible = g.Items.Where(i => i.Matches(q) && (!ShowStarredOnly || i.IsStarred)).ToList();
            g.VisibleCount = visible.Count;
            if (filtering && visible.Count == 0) continue;
            rows.Add(g);
            if (g.IsExpanded || filtering) rows.AddRange(visible);
        }
        Rows = new ObservableCollection<object>(rows);
        NoMatches = filtering && rows.Count == 0;
        NoMatchesText = ShowStarredOnly && q.Length == 0
            ? "No starred saves yet. Click the star on a save to keep it here."
            : ShowStarredOnly ? "No starred saves match your search." : "No saves, quests or places match your search.";
        StarredCount = _all.Count(i => i.IsStarred);
    }

    [ObservableProperty] private bool _noMatches;
    [ObservableProperty] private string _noMatchesText = "";

    // ---------- stars (saves.json in the app's folder, keyed by playthrough + in-game save time) ----------

    /// <summary>Show only starred saves in the list (a filter, separate from ticking saves).</summary>
    [ObservableProperty] private bool _showStarredOnly;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StarredLabel))]
    private int _starredCount;
    public string StarredLabel => $"★ Starred {StarredCount}";

    partial void OnShowStarredOnlyChanged(bool value) => RebuildRows();

    [RelayCommand] private void ShowAll() => ShowStarredOnly = false;
    [RelayCommand] private void ShowStarred() => ShowStarredOnly = true;

    [RelayCommand]
    private void ToggleStar(SaveItemViewModel? item)
    {
        item ??= Focused;
        if (item is null) return;
        SetStars([item], !item.IsStarred);
    }

    [RelayCommand] private void StarChecked() => SetStars(_all.Where(i => i.IsChecked).ToList(), true);
    [RelayCommand] private void UnstarChecked() => SetStars(_all.Where(i => i.IsChecked).ToList(), false);

    private void SetStars(List<SaveItemViewModel> items, bool starred)
    {
        if (items.Count == 0) return;
        foreach (var i in items) { i.IsStarred = starred; _notes.SetStarred(i.Info, starred); }
        try { _notes.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("Couldn't save stars · " + e.Message);
            ShowResult("Couldn't save stars · see the log", true);
        }
        if (items.Count > 1) ShowResult($"{(starred ? "Starred" : "Unstarred")} {items.Count} saves", false);
        if (ShowStarredOnly && !starred) RebuildRows();
        else StarredCount = _all.Count(i => i.IsStarred);
    }

    // ---------- rename (renames the save folder: that's what the game's Load menu shows) ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRenameOpen))]
    private RenameDialogViewModel? _renameDialog;
    public bool IsRenameOpen => RenameDialog is not null;

    [RelayCommand]
    private async Task RenameFocusedAsync()
    {
        if (Focused is not { } item || IsBusy || IsRenameOpen) return;
        await AskAndRenameAsync(item, "Rename save",
            "The new name is what the game's Load menu shows. Nothing inside the save changes, and its file and folder dates stay as they are.",
            item.Name);
    }

    /// <summary>Moves an auto/quick save out of the slot the game keeps reusing, so it can't be overwritten.</summary>
    [RelayCommand]
    private async Task KeepAutosaveAsync()
    {
        if (Focused is not { } item || IsBusy || IsRenameOpen) return;
        var suggestion = $"Kept {item.Name} {item.Info.Timestamp:yyyy-MM-dd HH.mm}";
        await AskAndRenameAsync(item, "Keep this autosave",
            $"The game reuses {item.Name} for later saves. Giving it a name of its own moves it out of that slot so it's never overwritten. Nothing inside the save changes.",
            suggestion);
    }

    [RelayCommand]
    private async Task ResetNameAsync()
    {
        if (Focused is not { IsRenamed: true } item || IsBusy) return;
        if (SaveRenamer.Validate(item.Info, item.OriginalName) is { } why)
        {
            ShowResult($"Can't reset to {item.OriginalName}: {why.ToLowerInvariant()}", true);
            return;
        }
        if (!await _platform.ConfirmAsync("Reset name", $"Rename {item.Name} back to {item.OriginalName}, the name the game gave it?", "Reset"))
            return;
        await RenameAsync(item, item.OriginalName);
    }

    private async Task AskAndRenameAsync(SaveItemViewModel item, string title, string intro, string suggestion)
    {
        var dialog = new RenameDialogViewModel(title, intro, item.Name, item.IsRenamed ? item.OriginalName : "", suggestion,
            n => SaveRenamer.Validate(item.Info, n));
        RenameDialog = dialog;
        bool go = await dialog.Result;
        RenameDialog = null;
        if (go) await RenameAsync(item, dialog.Name.Trim());
    }

    private async Task RenameAsync(SaveItemViewModel item, string newName)
    {
        if (!await CanWriteAsync()) return;
        var old = item.Name;
        _flagScan?.Cancel();   // its reads keep files in the folder open, which blocks a rename (the reload restarts it)
        try
        {
            await Task.Run(() => SaveRenamer.Rename(item.Info, newName, _backups, _batches, Log));
        }
        catch (Exception e)
        {
            Log.Error($"{old}: rename failed · {e.Message}");
            ShowResult($"{old}: rename failed · see the log", true);
            return;
        }
        _notes.AddRename(item.Info, old, newName);
        try { _notes.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn("Couldn't record the rename history · " + e.Message); }

        // the folder path changed: rescan, then put the focus back on the save under its new name
        var group = _groups.FirstOrDefault(g => g.Items.Contains(item))?.Id;
        await ReloadAsync();
        if (_all.FirstOrDefault(i => i.Name == newName) is { } renamed)
        {
            if (_groups.FirstOrDefault(g => g.Items.Contains(renamed)) is { } g) g.IsExpanded = true;
            else if (group is not null && _groups.FirstOrDefault(x => x.Id == group) is { } g2) g2.IsExpanded = true;
            RebuildRows();
            Focused = renamed;
        }
        ShowResult($"{old} renamed to {newName}", false);
    }

    private void UpdateStatus()
    {
        int tagged = _all.Count(i => i.IsTagged), clean = _all.Count(i => i.IsClean);
        int pending = _all.Count(i => i.IsReadable && i.State == ModdedState.Unknown);
        int unreadable = _all.Count(i => !i.IsReadable);
        StatusLine = $"{_all.Count} saves · {_groups.Count(g => !g.Playthrough.IsUngrouped)} playthroughs · {tagged} tagged · {clean} clean"
                     + (unreadable > 0 ? $" · {unreadable} unreadable" : "")
                     + (pending > 0 ? $" · checking {pending}…" : "");
    }

    [RelayCommand]
    private async Task ChangeFolderAsync()
    {
        if (IsBusy) return;
        string? picked;
        try { picked = await _platform.PickFolderAsync("Choose the Cyberpunk 2077 saves folder", Directory.Exists(SavesFolder) ? SavesFolder : null); }
        catch (Exception e)
        {
            Log.Error("Couldn't open the folder picker · " + e.Message);
            ShowResult("Couldn't open the folder picker · see the log", true);
            return;
        }
        if (picked is null) return;
        await UseFolderAsync(picked);
    }

    [RelayCommand]
    private Task UseDefaultFolderAsync() => IsBusy ? Task.CompletedTask : UseFolderAsync(_defaultSavesFolder);

    /// <summary>Switches folder and remembers it, so the next launch opens the same one (null = the detected default).</summary>
    private async Task UseFolderAsync(string folder)
    {
        SavesFolder = folder;
        _settings.SavesFolder = PathsEqual(folder, _defaultSavesFolder) ? null : folder;
        SaveSettingsNow();
        Log.Info(_settings.SavesFolder is null ? $"Using the default saves folder: {folder}" : $"Saves folder set to {folder} (remembered)");
        await ReloadAsync();
    }

    public bool IsCustomFolder => !PathsEqual(SavesFolder, _defaultSavesFolder);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoSaves), nameof(ShowEmptyState))]
    private bool _noSavesFound;

    [ObservableProperty] private string _noSavesMessage = "";

    public bool ShowNoSaves => NoSavesFound && DisclaimerAccepted;
    public bool ShowEmptyState => IsEmpty && !NoSavesFound;

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    [RelayCommand] private void OpenSavesFolder() => _platform.OpenFolder(SavesFolder);
    [RelayCommand] private void OpenBackupsFolder() { Directory.CreateDirectory(_backups.Root); _platform.OpenFolder(_backups.Root); }
    [RelayCommand] private void OpenFocusedFolder() { if (Focused is not null) _platform.OpenFolder(Focused.Info.FolderPath); }

    [RelayCommand] private void ExpandAll() { foreach (var g in _groups) g.IsExpanded = true; RebuildRows(); }
    [RelayCommand] private void CollapseAll() { foreach (var g in _groups) g.IsExpanded = false; RebuildRows(); }

    // ---------- selection ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBatch), nameof(IsSingle), nameof(IsEmpty), nameof(ShowEmptyState))]
    private int _checkedCount;

    public bool IsBatch => CheckedCount >= 2;
    public bool IsSingle => !IsBatch && Focused is not null;
    public bool IsEmpty => !IsBatch && Focused is null;

    [ObservableProperty] private int _batchToClean;
    [ObservableProperty] private int _batchClean;
    [ObservableProperty] private int _batchUnknown;
    [ObservableProperty] private int _batchPlaythroughs;
    [ObservableProperty] private int _batchWithBackups;
    [ObservableProperty] private string _batchSize = "";
    [ObservableProperty] private IReadOnlyList<SaveItemViewModel> _checkedItems = [];
    [ObservableProperty] private string _checkedMore = "";

    private const int MaxSelectionChips = 60;
    private bool _batchPending;

    // ticking a whole group flips hundreds of checkboxes; recompute once afterwards, not once per box
    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(SaveItemViewModel.IsChecked) || _batchPending) return;
        if (_ui is null) { UpdateBatch(); return; }
        _batchPending = true;
        _ui.Post(_ => { _batchPending = false; UpdateBatch(); }, null);
    }

    private void UpdateBatch()
    {
        var c = _all.Where(i => i.IsChecked).ToList();
        CheckedItems = c.Take(MaxSelectionChips).ToList();
        CheckedMore = c.Count > MaxSelectionChips ? $"and {c.Count - MaxSelectionChips} more" : "";
        CheckedCount = c.Count;
        BatchToClean = c.Count(i => i.IsTagged);
        BatchClean = c.Count(i => i.IsClean);
        BatchUnknown = c.Count - BatchToClean - BatchClean;
        BatchPlaythroughs = c.Select(i => i.Info.PlaythroughId ?? "").Distinct().Count();
        BatchWithBackups = c.Count(i => i.HasBackups);
        BatchSize = Format.Size(c.Where(i => i.IsTagged).Sum(i => i.Info.SizeBytes));
        CleanCheckedCommand.NotifyCanExecuteChanged();
        RestoreCheckedCommand.NotifyCanExecuteChanged();
    }

    private void SetChecked(Func<SaveItemViewModel, bool> pick)
    {
        foreach (var i in _all) i.IsChecked = pick(i);
    }

    [RelayCommand] private void ClearSearch() => SearchText = "";

    [RelayCommand] private void SelectAll() => SetChecked(i => i.Matches(SearchText.Trim()));
    [RelayCommand] private void SelectNone() => SetChecked(_ => false);
    [RelayCommand] private void SelectTagged() => SetChecked(i => i.IsTagged);
    [RelayCommand] private void InvertSelection() => SetChecked(i => !i.IsChecked);

    // ---------- focused save + backups ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSingle), nameof(IsEmpty), nameof(ShowEmptyState))]
    private SaveItemViewModel? _focused;

    public ObservableCollection<BackupItemViewModel> FocusedBackups { get; } = [];

    partial void OnFocusedChanged(SaveItemViewModel? oldValue, SaveItemViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsFocused = false;
        if (newValue is not null) newValue.IsFocused = true;
        LoadFocusedBackups();
    }

    private void LoadFocusedBackups()
    {
        FocusedBackups.Clear();
        if (Focused is null) return;
        var all = _backups.List(Focused.Name);
        var undo = UndoBackupIds();
        foreach (var b in all)
            FocusedBackups.Add(new BackupItemViewModel(b, Focused, _names, BackupCatalog.Protection(b, all, Focused.Info.Metadata, undo)));
        Focused.BackupCount = FocusedBackups.Count;
    }

    [RelayCommand]
    private void FocusSave(SaveItemViewModel item)
    {
        if (IsBatch) SetChecked(_ => false);
        Focused = item;
    }

    // ---------- write operations: always backed up, never while the game runs ----------

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isLogOpen;

    [RelayCommand] private void ToggleLog() => IsLogOpen = !IsLogOpen;
    [RelayCommand] private void CopyLog() => _platform.CopyToClipboard(string.Join(Environment.NewLine, LogEntries));
    [RelayCommand] private void ClearLog() { LogEntries.Clear(); LogErrorCount = 0; OnPropertyChanged(nameof(LogCount)); }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLogErrors))]
    private int _logErrorCount;
    public bool HasLogErrors => LogErrorCount > 0;

    /// <summary>The log in its own window instead of the docked panel (e.g. to put it on another monitor).</summary>
    [ObservableProperty] private bool _isLogPoppedOut;

    [RelayCommand]
    private void PopOutLog()
    {
        IsLogOpen = false;
        IsLogPoppedOut = true;
    }

    [RelayCommand]
    private void DockLog()
    {
        IsLogPoppedOut = false;
        IsLogOpen = true;
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        if (Log.FilePath is { } f && Path.GetDirectoryName(f) is { } dir && Directory.Exists(dir)) _platform.OpenFolder(dir);
    }

    /// <summary>Replaceable for tests; the real check looks for Cyberpunk2077.exe.</summary>
    public Func<bool> IsGameRunning { get; set; } = GameProcess.IsRunning;

    private async Task<bool> CanWriteAsync()
    {
        if (IsBusy) return false;
        if (!IsGameRunning()) return true;
        Log.Warn("Cyberpunk 2077 is running; nothing was changed");
        ShowResult("Close the game first: nothing was changed", true);
        await _platform.AlertAsync("Close the game first",
            "Cyberpunk 2077 is running. The game caches its save list and can overwrite saves, so the app doesn't change saves while it runs.");
        return false;
    }

    private bool CanCleanChecked() => BatchToClean > 0 || BatchUnknown > 0;

    [RelayCommand(CanExecute = nameof(CanCleanChecked))]
    private async Task CleanCheckedAsync()
    {
        var targets = _all.Where(i => i.IsChecked).ToList();
        await CleanAsync(targets);
    }

    [RelayCommand]
    private async Task CleanFocusedAsync()
    {
        if (Focused is not null) await CleanAsync([Focused]);
    }

    private async Task CleanAsync(List<SaveItemViewModel> targets)
    {
        var todo = targets.Where(t => !t.IsClean && t.IsReadable).ToList();
        var skip = targets.Count - todo.Count;
        if (todo.Count == 0)
        {
            Log.Skip($"Nothing to clean: all {targets.Count} selected saves are already clean");
            ShowResult("Nothing to clean: already clean", false);
            return;
        }
        if (!await CanWriteAsync()) return;
        if (todo.Count > 1 && !await _platform.ConfirmAsync("Clean modded tag",
                $"Clean {todo.Count} saves? Each one is backed up first (about {Format.Size(todo.Sum(t => t.Info.SizeBytes))} of backups)."
                + (skip > 0 ? $"\n\n{skip} already clean save{(skip == 1 ? " is" : "s are")} skipped." : ""), "Clean"))
            return;

        Log.Info($"Clean modded tag: {targets.Count} selected, {todo.Count} to clean, {skip} already clean");
        foreach (var t in targets.Where(t => t.IsClean)) Log.Skip($"{t.Name}: already clean");

        BeginBusy("Cleaning…");
        int ok = 0, skipped = skip, failed = 0;
        var batchId = BatchLog.NewId();
        var touched = new List<BatchSave>();
        try
        {
            for (int n = 0; n < todo.Count; n++)
            {
                var item = todo[n];
                BusyText = $"Cleaning {n + 1}/{todo.Count} · {item.Name}";
                Progress = (double)n / todo.Count;
                try
                {
                    var (result, info, state, sticky) = await Task.Run(() =>
                    {
                        var r = _cleaner.Clean(item.Info.FolderPath, batchId);
                        if (r.BackupId is { } id) lock (touched) touched.Add(BatchLog.Snapshot(item.Info.FolderPath, id));
                        var i = SaveScanner.Read(item.Info.FolderPath);
                        var (s, st) = ReadState(i);
                        return (r, i, s, st);
                    });
                    item.Info = info; item.Sticky = sticky; item.State = state;
                    item.BackupCount = _backups.List(item.Name).Count;
                    switch (result.Outcome) { case CleanOutcome.Cleaned: ok++; break; case CleanOutcome.Skipped: skipped++; break; default: failed++; break; }
                }
                catch (Exception e)
                {
                    failed++;
                    Log.Error($"{item.Name}: {e.Message}");
                }
            }
        }
        finally { await EndBusyAsync(); }
        bool undoable = RecordBatch(batchId, BatchKind.Clean, touched);
        Log.Done($"{ok} cleaned, {skipped} skipped, {failed} failed");
        ShowResult(failed > 0 ? $"{failed} failed, {ok} cleaned · see the log" : $"{ok} cleaned" + (skipped > 0 ? $", {skipped} skipped" : ""), failed > 0, undoable);
        if (Focused is not null && todo.Contains(Focused)) LoadFocusedBackups();
        UpdateBatch(); UpdateStatus();
    }

    [RelayCommand]
    private async Task BackupCheckedAsync()
    {
        var targets = (IsBatch ? _all.Where(i => i.IsChecked) : Focused is null ? [] : [Focused]).Where(i => i.IsReadable).ToList();
        if (targets.Count == 0 || IsBusy) return;
        BeginBusy("Backing up…");
        int made = 0, already = 0, failed = 0;
        try
        {
            for (int n = 0; n < targets.Count; n++)
            {
                var item = targets[n];
                BusyText = $"Backing up {n + 1}/{targets.Count} · {item.Name}";
                Progress = (double)n / targets.Count;
                try
                {
                    var b = await Task.Run(() => _backups.Create(item.Info, "Manual"));
                    if (b.WasExisting) already++; else made++;
                }
                catch (Exception e) { failed++; Log.Error($"{item.Name}: backup failed · {e.Message}"); }
                item.BackupCount = _backups.List(item.Name).Count;
            }
        }
        finally { await EndBusyAsync(); }
        // an identical backup already there counts as backed up: nothing new was needed
        string text = failed > 0 ? $"{failed} backup{(failed == 1 ? "" : "s")} failed · see the log"
            : already == 0 ? $"Backed up {made} save{(made == 1 ? "" : "s")}"
            : made == 0 ? (already == 1 ? "Already backed up: an identical backup exists" : $"Already backed up: all {already} have an identical backup")
            : $"Backed up {made} save{(made == 1 ? "" : "s")} · {already} already had an identical backup";
        Log.Done(text);
        ShowResult(text, failed > 0);
        LoadFocusedBackups(); UpdateBatch();
    }

    [RelayCommand]
    private async Task RestoreAsync(BackupItemViewModel backup)
    {
        if (Focused is null || !await CanWriteAsync()) return;
        var otherFolder = backup.IsFromOtherFolder
            ? $"\n\nCareful: this backup was taken from {backup.Backup.SourceFolder}, a different saves folder. It's a save with the same name, not necessarily the same save."
            : "";
        if (!await _platform.ConfirmAsync("Restore from backup",
                $"Replace {Focused.Name} with the backup from {Format.DateTime(backup.Backup.Created)} ({backup.Backup.Reason.ToLowerInvariant()})?\n\nThe current save is backed up first, so you can undo this.{otherFolder}",
                "Restore"))
            return;
        BeginBusy("Restoring " + Focused.Name);
        var item = Focused;
        var batchId = BatchLog.NewId();
        var touched = new List<BatchSave>();
        try
        {
            var (info, state, sticky) = await Task.Run(() =>
            {
                var before = _backups.Restore(backup.Backup, SavesFolder, batchId: batchId);
                if (before is not null) touched.Add(BatchLog.Snapshot(item.Info.FolderPath, before.Id));
                var i = SaveScanner.Read(item.Info.FolderPath);
                var (s, st) = ReadState(i);
                return (i, s, st);
            });
            item.Info = info; item.Sticky = sticky; item.State = state;
            Log.Done($"{item.Name} restored · now {Format.State(state).ToLowerInvariant()}");
            ShowResult($"{item.Name} restored", false, RecordBatch(batchId, BatchKind.Restore, touched));
        }
        catch (Exception e)
        {
            Log.Error($"{item.Name}: restore failed · {e.Message}");
            ShowResult($"{item.Name}: restore failed · see the log", true);
        }
        finally { await EndBusyAsync(); }
        LoadFocusedBackups(); UpdateStatus();
    }

    [RelayCommand]
    private async Task DeleteBackupAsync(BackupItemViewModel backup)
    {
        var b = backup.Backup;
        // protected backups can still be deleted by hand, with a warning saying what's lost
        string warning = backup.IsOriginal
            ? $"\n\nThis is the original of {b.SaveFolderName}: the save as the game wrote it, before the app cleaned it. It can't be recreated."
            : backup.IsUndo ? "\n\nUndo last batch needs this backup; without it, this save can't be undone."
            : backup.IsManual ? "\n\nYou made this backup yourself." : "";
        if (!await _platform.ConfirmAsync(backup.IsProtected ? "Delete a kept backup" : "Delete backup",
                $"Permanently delete the {b.Reason.ToLowerInvariant()} backup of {b.SaveFolderName} from {Format.DateTime(b.Created)}? This can't be undone.{warning}",
                "Delete"))
            return;
        if (IsBusy) return;
        BeginBusy($"Deleting backup of {b.SaveFolderName}");
        bool ok = true;
        try { await Task.Run(() => _backups.Delete(b)); }
        catch (Exception e) { ok = false; Log.Error($"Couldn't delete backup: {e.Message}"); }
        finally { await EndBusyAsync(); }
        ShowResult(ok ? "Backup deleted" : "Couldn't delete the backup · see the log", !ok);
        LoadFocusedBackups();
        _ = UpdateBackupsSummaryAsync();
    }

    // ---------- backups menu + clean up backups ----------

    /// <summary>"77 MB in 26 backups", refreshed when the Backups menu opens.</summary>
    [ObservableProperty] private string _backupsSummary = "";

    public async Task UpdateBackupsSummaryAsync()
    {
        var (size, count) = await Task.Run(() => (_backups.TotalSize(), _backups.Counts().Values.Sum()));
        BackupsSummary = count == 0 ? "No backups yet" : $"{Format.Size(size)} in {count} backup{(count == 1 ? "" : "s")}";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCleanupOpen))]
    private CleanupReviewViewModel? _cleanup;
    public bool IsCleanupOpen => Cleanup is not null;

    [RelayCommand]
    private async Task CleanUpBackupsAsync()
    {
        if (IsBusy || IsReviewOpen || IsCleanupOpen) return;
        var undo = UndoBackupIds();
        var (candidates, total) = await Task.Run(() =>
            (BackupCatalog.Candidates(_backups, SavesFolder, undo), _backups.Counts().Values.Sum()));
        if (candidates.Count == 0)
        {
            ShowResult(total == 0 ? "No backups yet" : "Nothing to clean up: every backup is kept for a reason", false);
            return;
        }
        var review = new CleanupReviewViewModel(candidates, total - candidates.Count);
        Cleanup = review;
        bool go = await review.Result;
        Cleanup = null;
        var chosen = review.Chosen;
        if (!go || chosen.Count == 0) return;
        if (!await _platform.ConfirmAsync("Delete backups",
                $"Permanently delete {chosen.Count} backup{(chosen.Count == 1 ? "" : "s")} ({Format.Size(review.ChosenSize)})? This can't be undone.", "Delete"))
            return;

        BeginBusy("Deleting backups…");
        int ok = 0, failed = 0;
        long freed = 0;
        try
        {
            for (int n = 0; n < chosen.Count; n++)
            {
                var b = chosen[n];
                BusyText = $"Deleting {n + 1}/{chosen.Count} · {b.SaveFolderName}";
                Progress = (double)n / chosen.Count;
                try { await Task.Run(() => _backups.Delete(b)); ok++; freed += b.SizeBytes; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed++; Log.Error($"{b.SaveFolderName}: couldn't delete backup {b.Id} · {e.Message}"); }
            }
        }
        finally { await EndBusyAsync(); }
        Log.Done($"Clean up backups: {ok} deleted ({Format.Size(freed)}), {failed} failed");
        ShowResult(failed > 0 ? $"{failed} couldn't be deleted, {ok} deleted · see the log" : $"Deleted {ok} backup{(ok == 1 ? "" : "s")} · freed {Format.Size(freed)}", failed > 0);
        var counts = await Task.Run(_backups.Counts);
        foreach (var i in _all) i.BackupCount = counts.GetValueOrDefault(i.Name);
        LoadFocusedBackups(); UpdateBatch();
        await UpdateBackupsSummaryAsync();
    }

    // ---------- bulk restore + undo last batch (both reviewed in RestoreReview before anything changes) ----------

    /// <summary>The open review; null when none is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReviewOpen))]
    private RestoreReviewViewModel? _review;
    public bool IsReviewOpen => Review is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUndoLastBatch), nameof(UndoLastBatchText))]
    [NotifyCanExecuteChangedFor(nameof(UndoLastBatchCommand))]
    private BatchRecord? _lastBatch;
    public bool CanUndoLastBatch => LastBatch is not null;
    public string UndoLastBatchText => LastBatch is not { } b ? "" :
        $"Undo the last {(b.Kind == BatchKind.Clean ? "clean" : "restore")} · {b.Saves.Count} save{(b.Saves.Count == 1 ? "" : "s")} · {Format.DateTime(b.TimeUtc.ToLocalTime())}";

    private void RefreshLastBatch() => LastBatch = _batches.Last(SavesFolder);

    /// <summary>The backups the current undo would put back: protected until the next batch.</summary>
    private IReadOnlySet<string> UndoBackupIds() => LastBatch?.Saves.Select(s => s.BackupId).ToHashSet() ?? [];

    /// <summary>Remembers what a clean/restore touched so it can be undone; true when there's something to undo.</summary>
    private bool RecordBatch(string id, BatchKind kind, List<BatchSave> touched)
    {
        if (touched.Count == 0) return false;
        try { _batches.Add(new BatchRecord(id, kind, DateTime.UtcNow, SavesFolder, touched)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Couldn't record this batch for Undo · " + e.Message);
            return false;
        }
        RefreshLastBatch();
        return true;
    }

    private IReadOnlyList<RestoreOption> OptionsFor(RestorePlanEntry e)
    {
        var folder = Path.Combine(SavesFolder, e.SaveFolderName);
        return [new RestoreOption("Don't restore", null),
            .. e.Candidates.Select(b => new RestoreOption(
                $"{b.Reason} · {Format.DateTime(b.Created)} · {Format.State(b.State)}" + (b.IsFromOtherFolder(folder) ? " · from another saves folder" : ""), b))];
    }

    private bool CanRestoreChecked() => BatchWithBackups > 0;

    [RelayCommand(CanExecute = nameof(CanRestoreChecked))]
    private async Task RestoreCheckedAsync()
    {
        var targets = _all.Where(i => i.IsChecked && i.HasBackups).ToList();
        if (targets.Count == 0 || IsBusy || IsReviewOpen) return;
        // read every save's backups once; switching the rule only re-picks from these
        var lists = await Task.Run(() => targets.ToDictionary(t => t.Info.FolderPath, t => _backups.List(t.Name)));
        IReadOnlyList<RestorePlanEntry> Plan(RestoreRule rule) => lists.Select(kv => RestorePlanner.Plan(kv.Key, kv.Value, rule)).ToList();
        var plan = await Task.Run(() => Plan(RestoreRule.UndoCleaning));
        int without = CheckedCount - targets.Count;
        await ReviewAndRestoreAsync(new RestoreReviewViewModel(
            "Restore selected saves",
            $"Pick which restore point the {targets.Count} selected save{(targets.Count == 1 ? "" : "s")} with backups go back to."
            + (without > 0 ? $" {without} selected save{(without == 1 ? " has" : "s have")} no backups and {(without == 1 ? "is" : "are")} left out." : ""),
            plan, OptionsFor, Plan));
    }

    [RelayCommand(CanExecute = nameof(CanUndoLastBatch))]
    private async Task UndoLastBatchAsync()
    {
        if (LastBatch is not { } batch || IsBusy || IsReviewOpen) return;
        DismissResult();
        var plan = await Task.Run(() => RestorePlanner.PlanUndo(batch, _backups, SavesFolder));
        string what = batch.Kind == BatchKind.Clean ? "clean" : "restore";
        await ReviewAndRestoreAsync(new RestoreReviewViewModel(
            "Undo the last " + what,
            $"Puts the {batch.Saves.Count} save{(batch.Saves.Count == 1 ? "" : "s")} from the {what} on {Format.DateTime(batch.TimeUtc.ToLocalTime())} back the way "
            + "they were before it. Saves the game has written to since are left out unless you pick them.",
            plan, OptionsFor, null));
    }

    private async Task ReviewAndRestoreAsync(RestoreReviewViewModel review)
    {
        Review = review;
        bool go = await review.Result;
        Review = null;
        if (!go) return;
        var chosen = review.Chosen;
        if (chosen.Count == 0 || !await CanWriteAsync()) return;

        Log.Info($"{review.Title}: {chosen.Count} save{(chosen.Count == 1 ? "" : "s")}");
        BeginBusy("Restoring…");
        int ok = 0, failed = 0;
        bool recreated = false;
        var batchId = BatchLog.NewId();
        var touched = new List<BatchSave>();
        try
        {
            for (int n = 0; n < chosen.Count; n++)
            {
                var (name, backup) = chosen[n];
                BusyText = $"Restoring {n + 1}/{chosen.Count} · {name}";
                Progress = (double)n / chosen.Count;
                var folder = Path.Combine(SavesFolder, name);
                try
                {
                    var (info, state, sticky) = await Task.Run(() =>
                    {
                        var before = _backups.Restore(backup, SavesFolder, batchId: batchId);
                        if (before is not null) touched.Add(BatchLog.Snapshot(folder, before.Id));
                        var i = SaveScanner.Read(folder);
                        var (s, st) = ReadState(i);
                        return (i, s, st);
                    });
                    if (_all.FirstOrDefault(i => i.Name == name) is { } item)
                    {
                        item.Info = info; item.Sticky = sticky; item.State = state;
                        item.BackupCount = _backups.List(name).Count;
                    }
                    else recreated = true;   // the save had been deleted: it shows up after a reload
                    Log.Ok($"{name}: restored · now {Format.State(state).ToLowerInvariant()}");
                    ok++;
                }
                catch (Exception e)
                {
                    failed++;
                    Log.Error($"{name}: restore failed · {e.Message}");
                }
            }
        }
        finally { await EndBusyAsync(); }
        bool undoable = RecordBatch(batchId, BatchKind.Restore, touched);
        Log.Done($"{ok} restored, {failed} failed");
        ShowResult(failed > 0 ? $"{failed} failed, {ok} restored · see the log" : $"{ok} restored", failed > 0, undoable);
        if (recreated) await ReloadAsync();
        LoadFocusedBackups(); UpdateBatch(); UpdateStatus();
    }

    /// <summary>
    /// Most operations finish in a fraction of a second, which would flash the progress bar for a single frame.
    /// The bar stays up at least this long (then holds full briefly) so you can see it run. Zero in tests.
    /// </summary>
    public TimeSpan MinBusyTime { get; set; } = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan HoldFull = TimeSpan.FromMilliseconds(350);
    private readonly System.Diagnostics.Stopwatch _busyClock = new();

    private void BeginBusy(string text)
    {
        IsBusy = true;
        BusyText = text;
        Progress = 0;
        _busyClock.Restart();
    }

    private async Task EndBusyAsync()
    {
        Progress = 1;
        if (MinBusyTime > TimeSpan.Zero)
        {
            var remaining = MinBusyTime - _busyClock.Elapsed;
            await Task.Delay((remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero) + HoldFull);
        }
        IsBusy = false; Progress = 0; BusyText = "";
    }

    // ---------- status bar result (fades out; errors stay until dismissed) ----------

    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private bool _resultIsError;
    [ObservableProperty] private bool _resultVisible;
    /// <summary>The result is from a clean/restore that the "Undo" next to it can take back.</summary>
    [ObservableProperty] private bool _resultCanUndo;
    private int _resultToken;

    private void ShowResult(string text, bool error, bool canUndo = false)
    {
        ResultText = text;
        ResultIsError = error;
        ResultCanUndo = canUndo && LastBatch is not null;
        ResultVisible = true;
        int token = ++_resultToken;
        if (!error) _ = HideResultLater(token);
    }

    private async Task HideResultLater(int token)
    {
        await Task.Delay(ResultCanUndo ? 12000 : 6000);   // longer when there's an Undo to click
        OnUi(() => { if (token == _resultToken) { ResultVisible = false; ResultCanUndo = false; } });
    }

    [RelayCommand]
    private void DismissResult()
    {
        _resultToken++;
        ResultVisible = false;
        ResultCanUndo = false;
    }

    // ---------- music (mute + volume remembered in settings.json) ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MiniPlayerTitle))]
    private bool _musicMuted;
    [ObservableProperty] private double _musicVolume;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MiniPlayerTitle))]
    private string _nowPlaying = "";
    /// <summary>The full player sliding up from the status bar's mini player.</summary>
    [ObservableProperty] private bool _isMusicPanelOpen;

    /// <summary>What the status bar's mini player says.</summary>
    public string MiniPlayerTitle => MusicMuted ? "Music muted" : NowPlaying.Length > 0 ? NowPlaying : "Music";
    [ObservableProperty] private bool _isMusicPaused;
    [ObservableProperty] private double _trackProgress;   // 0..1
    [ObservableProperty] private string _trackPositionText = "0:00";
    [ObservableProperty] private string _trackDurationText = "0:00";
    public bool MusicAvailable => _music?.IsAvailable == true;
    private int _saveToken;
    private Timer? _musicClock;

    /// <summary>Called by the view once the window is up. Muted users never hear a note.</summary>
    public void StartMusic()
    {
        if (_music is null || !_music.IsAvailable) return;
        _music.Volume = MusicVolume / 100;
        if (!MusicMuted) TryPlayMusic();
        // the flyout's position bar and play/pause state follow the player a few times a second
        _musicClock ??= new Timer(_ => OnUi(RefreshTrackState), null, 250, 250);
    }

    private void TryPlayMusic()
    {
        try { _music?.Play(); }
        catch (Exception e) { Log.Warn("Music is off: no usable audio device · " + e.Message); }
        OnPropertyChanged(nameof(MusicAvailable));
    }

    private void RefreshTrackState()
    {
        if (_music is null) return;
        OnPropertyChanged(nameof(MusicAvailable));   // the device can disappear mid-session
        double pos = _music.Position, dur = _music.Duration;
        TrackProgress = dur > 0 ? Math.Clamp(pos / dur, 0, 1) : 0;
        TrackPositionText = Format.Clock(pos);
        TrackDurationText = Format.Clock(dur);
        IsMusicPaused = _music.IsPaused;
    }

    partial void OnMusicMutedChanged(bool value)
    {
        if (_music is not null) { if (value) _music.Stop(); else { _music.Volume = MusicVolume / 100; TryPlayMusic(); } }
        _settings.MusicMuted = value;
        SaveSettingsSoon();
    }

    partial void OnMusicVolumeChanged(double value)
    {
        if (_music is not null) _music.Volume = value / 100;
        _settings.MusicVolume = value / 100;
        SaveSettingsSoon();
    }

    [RelayCommand] private void ToggleMute() => MusicMuted = !MusicMuted;
    [RelayCommand] private void ToggleMusicPanel() => IsMusicPanelOpen = !IsMusicPanelOpen;
    [RelayCommand] private void NextTrack() { if (!MusicMuted) _music?.Next(); }
    [RelayCommand] private void PreviousTrack() { if (!MusicMuted) _music?.Previous(); }
    [RelayCommand] private void PlayPause() { if (!MusicMuted) { _music?.TogglePause(); RefreshTrackState(); } }

    /// <summary>Jumps to a point in the current track; <paramref name="fraction"/> is 0..1 along the position bar.</summary>
    public void SeekTrack(double fraction)
    {
        if (_music is null || MusicMuted) return;
        _music.Seek(Math.Clamp(fraction, 0, 1) * _music.Duration);
        RefreshTrackState();
    }
    [RelayCommand] private void OpenLink(string url) => _platform.OpenUrl(url);

    // dragging the slider fires many changes; write the file once it settles
    private async void SaveSettingsSoon()
    {
        if (_settingsPath is null) return;
        int token = ++_saveToken;
        await Task.Delay(400);
        if (token == _saveToken) _settings.Save(_settingsPath);
    }

    public void SaveSettingsNow() { if (_settingsPath is not null) _settings.Save(_settingsPath); }

    private void OnUi(Action a)
    {
        if (_ui is null || SynchronizationContext.Current == _ui) a();
        else _ui.Post(_ => a(), null);
    }
}
