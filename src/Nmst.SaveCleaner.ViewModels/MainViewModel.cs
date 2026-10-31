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

    // ---------- disclaimer (shown on every launch, never remembered) ----------

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
        Log.Info("Disclaimer accepted for this session");
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
            foreach (var i in items.Values) i.PropertyChanged += OnItemChanged;
            _all = items.Values.ToList();
            _groups = Playthrough.Group(saves)
                .Select(p => new PlaythroughGroupViewModel(p, p.Saves.Select(s => items[s.FolderPath]).ToList(), RebuildRows))
                .ToList();
            if (_groups.Count > 0) _groups[0].IsExpanded = true;
            RebuildRows();
            Focused = _groups.FirstOrDefault()?.Items.FirstOrDefault();
            UpdateBatch();
            UpdateStatus();
            Log.Info($"Scanned {SavesFolder}: {_all.Count} saves in {_groups.Count(g => !g.Playthrough.IsUngrouped)} playthroughs");
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
        var rows = new List<object>();
        foreach (var g in _groups)
        {
            var visible = g.Items.Where(i => i.Matches(q)).ToList();
            g.VisibleCount = visible.Count;
            if (q.Length > 0 && visible.Count == 0) continue;
            rows.Add(g);
            if (g.IsExpanded || q.Length > 0) rows.AddRange(visible);
        }
        Rows = new ObservableCollection<object>(rows);
        NoMatches = q.Length > 0 && rows.Count == 0;
    }

    [ObservableProperty] private bool _noMatches;

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
        foreach (var b in _backups.List(Focused.Name)) FocusedBackups.Add(new BackupItemViewModel(b, Focused, _names));
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
                        var r = _cleaner.Clean(item.Info.FolderPath);
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
        Log.Done($"{ok} cleaned, {skipped} skipped, {failed} failed");
        ShowResult(failed > 0 ? $"{failed} failed, {ok} cleaned · see the log" : $"{ok} cleaned" + (skipped > 0 ? $", {skipped} skipped" : ""), failed > 0);
        if (Focused is not null && todo.Contains(Focused)) LoadFocusedBackups();
        UpdateBatch(); UpdateStatus();
    }

    [RelayCommand]
    private async Task BackupCheckedAsync()
    {
        var targets = (IsBatch ? _all.Where(i => i.IsChecked) : Focused is null ? [] : [Focused]).Where(i => i.IsReadable).ToList();
        if (targets.Count == 0 || IsBusy) return;
        BeginBusy("Backing up…");
        try
        {
            for (int n = 0; n < targets.Count; n++)
            {
                var item = targets[n];
                BusyText = $"Backing up {n + 1}/{targets.Count} · {item.Name}";
                Progress = (double)n / targets.Count;
                try { await Task.Run(() => _backups.Create(item.Info, "Manual")); }
                catch (Exception e) { Log.Error($"{item.Name}: backup failed · {e.Message}"); ShowResult($"{item.Name}: backup failed · see the log", true); }
                item.BackupCount = _backups.List(item.Name).Count;
            }
        }
        finally { await EndBusyAsync(); }
        Log.Done($"Backed up {targets.Count} save{(targets.Count == 1 ? "" : "s")}");
        ShowResult($"Backed up {targets.Count} save{(targets.Count == 1 ? "" : "s")}", false);
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
        try
        {
            var (info, state, sticky) = await Task.Run(() =>
            {
                _backups.Restore(backup.Backup, SavesFolder);
                var i = SaveScanner.Read(item.Info.FolderPath);
                var (s, st) = ReadState(i);
                return (i, s, st);
            });
            item.Info = info; item.Sticky = sticky; item.State = state;
            Log.Done($"{item.Name} restored · now {Format.State(state).ToLowerInvariant()}");
            ShowResult($"{item.Name} restored", false);
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
        if (!await _platform.ConfirmAsync("Delete backup",
                $"Permanently delete the {backup.Backup.Reason.ToLowerInvariant()} backup of {backup.Backup.SaveFolderName} from {Format.DateTime(backup.Backup.Created)}? This can't be undone.",
                "Delete"))
            return;
        if (IsBusy) return;
        BeginBusy($"Deleting backup of {backup.Backup.SaveFolderName}");
        bool ok = true;
        try { await Task.Run(() => _backups.Delete(backup.Backup)); }
        catch (Exception e) { ok = false; Log.Error($"Couldn't delete backup: {e.Message}"); }
        finally { await EndBusyAsync(); }
        ShowResult(ok ? "Backup deleted" : "Couldn't delete the backup · see the log", !ok);
        LoadFocusedBackups();
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
    private int _resultToken;

    private void ShowResult(string text, bool error)
    {
        ResultText = text;
        ResultIsError = error;
        ResultVisible = true;
        int token = ++_resultToken;
        if (!error) _ = HideResultLater(token);
    }

    private async Task HideResultLater(int token)
    {
        await Task.Delay(6000);
        OnUi(() => { if (token == _resultToken) ResultVisible = false; });
    }

    [RelayCommand]
    private void DismissResult()
    {
        _resultToken++;
        ResultVisible = false;
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
