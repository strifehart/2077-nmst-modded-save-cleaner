using Nmst.SaveCleaner.Core;
using Nmst.SaveCleaner.Core.Logging;
using Nmst.SaveCleaner.Core.Saves;
using Nmst.SaveCleaner.ViewModels;

namespace Nmst.SaveCleaner.Tests;

public class FolderTests
{
    private sealed class Picker(string? pick) : IPlatformServices
    {
        public Task<bool> ConfirmAsync(string title, string message, string confirmText) => Task.FromResult(true);
        public Task AlertAsync(string title, string message) => Task.CompletedTask;
        public Task<string?> PickFolderAsync(string title, string? startFolder) => Task.FromResult(pick);
        public void OpenFolder(string path) { }
        public void OpenUrl(string url) { }
        public void CopyToClipboard(string text) { }
        public void Quit() { }
    }

    private static string FakeSave(string savesRoot, string name)
    {
        var d = Directory.CreateDirectory(Path.Combine(savesRoot, name)).FullName;
        File.WriteAllText(Path.Combine(d, SaveInfo.SavFileName), "x");
        return d;
    }

    [Fact]
    public async Task Chosen_folder_is_remembered_and_default_clears_it()
    {
        using var t = new TempDir();
        var settings = Path.Combine(t.Root, "settings.json");
        var def = Directory.CreateDirectory(Path.Combine(t.Root, "default")).FullName;
        var mine = Directory.CreateDirectory(Path.Combine(t.Root, "mine")).FullName;

        var vm = new MainViewModel(new Picker(mine), def, t.Backups, null, settingsPath: settings);
        Assert.False(vm.IsCustomFolder);
        await vm.ChangeFolderCommand.ExecuteAsync(null);
        Assert.Equal(mine, vm.SavesFolder);
        Assert.True(vm.IsCustomFolder);

        var next = new MainViewModel(new Picker(null), def, t.Backups, null, settingsPath: settings);
        Assert.Equal(mine, next.SavesFolder);

        await next.UseDefaultFolderCommand.ExecuteAsync(null);
        Assert.Equal(def, next.SavesFolder);
        Assert.Null(AppSettings.Load(settings).SavesFolder);
    }

    [Fact]
    public void A_remembered_folder_that_is_gone_falls_back_to_the_default()
    {
        using var t = new TempDir();
        var settings = Path.Combine(t.Root, "settings.json");
        new AppSettings { SavesFolder = Path.Combine(t.Root, "deleted") }.Save(settings);
        var def = Directory.CreateDirectory(Path.Combine(t.Root, "default")).FullName;

        var vm = new MainViewModel(new Picker(null), def, t.Backups, null, settingsPath: settings);

        Assert.Equal(def, vm.SavesFolder);
        Assert.Contains(vm.LogEntries, e => e.Level == LogLevel.Warn && e.Message.Contains("gone"));
    }

    [Fact]
    public async Task Empty_or_missing_folder_shows_no_saves()
    {
        using var t = new TempDir();
        var vm = new MainViewModel(new Picker(null), Path.Combine(t.Root, "nowhere"), t.Backups, null) { DisclaimerAccepted = true };
        await vm.ReloadCommand.ExecuteAsync(null);
        Assert.True(vm.ShowNoSaves);
        Assert.False(vm.ShowEmptyState);

        var empty = Directory.CreateDirectory(Path.Combine(t.Root, "empty")).FullName;
        Directory.CreateDirectory(Path.Combine(empty, "ManualSave-1"));   // the game leaves empty folders behind
        var vm2 = new MainViewModel(new Picker(null), empty, t.Backups, null) { DisclaimerAccepted = true };
        await vm2.ReloadCommand.ExecuteAsync(null);
        Assert.True(vm2.ShowNoSaves);
        Assert.Contains("No Cyberpunk 2077 saves", vm2.NoSavesMessage);
    }

    [Fact]
    public void Steam_library_paths_are_read_from_libraryfolders_vdf()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0" { "path"  "/home/deck/.local/share/Steam"  "apps" { "1091500" "0" } }
                "1" { "path"  "/run/media/mmcblk0p1" }
                "2" { "path"  "D:\\SteamLibrary" }
            }
            """;
        Assert.Equal(["/home/deck/.local/share/Steam", "/run/media/mmcblk0p1", @"D:\SteamLibrary"], SavesLocator.LibraryPaths(vdf));
    }

    [Fact]
    public void Proton_candidates_cover_every_steam_library()
    {
        using var t = new TempDir();
        var home = Directory.CreateDirectory(Path.Combine(t.Root, "home")).FullName;
        var steam = Directory.CreateDirectory(Path.Combine(home, ".local", "share", "Steam", "steamapps")).FullName;
        var sd = Path.Combine(t.Root, "sdcard");
        File.WriteAllText(Path.Combine(steam, "libraryfolders.vdf"), $"\"libraryfolders\" {{ \"1\" {{ \"path\" \"{sd.Replace(@"\", @"\\")}\" }} }}");

        var c = SavesLocator.ProtonCandidates(home).ToList();

        string Tail(string lib) => Path.Combine(lib, "steamapps", "compatdata", "1091500", "pfx", "drive_c", "users", "steamuser", "Saved Games", "CD Projekt Red", "Cyberpunk 2077");
        Assert.Contains(Path.GetFullPath(Tail(Path.Combine(home, ".local", "share", "Steam"))), c);
        Assert.Contains(Path.GetFullPath(Tail(sd)), c);
        Assert.Equal(c.Count, c.Distinct().Count());
    }

    [Fact]
    public void Detects_this_machines_saves_folder()
    {
        if (!OperatingSystem.IsWindows() || !RealSaves.Available) return;
        var found = SavesLocator.Detect();
        Assert.True(SavesLocator.HasSaves(found), found);
        Assert.EndsWith(Path.Combine("CD Projekt Red", "Cyberpunk 2077"), found.Replace('/', Path.DirectorySeparatorChar));
    }
}

public class BusyTimeTests
{
    [Fact]
    public async Task Quick_operations_keep_the_progress_bar_up_for_the_minimum_time()
    {
        var src = RealSaves.Find((m, s) => !m.IsModded && s == false);
        if (src is null) return;
        using var t = new TempDir();
        t.CopySave(src);
        var vm = new MainViewModel(new ViewModelTestsPlatform(), t.Saves, t.Backups, null) { MinBusyTime = TimeSpan.FromMilliseconds(500) };
        await vm.ReloadCommand.ExecuteAsync(null);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        double peak = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.Progress)) peak = Math.Max(peak, vm.Progress); };
        await vm.BackupCheckedCommand.ExecuteAsync(null);

        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(500), $"busy for only {watch.ElapsedMilliseconds} ms");
        Assert.Equal(1, peak);            // the bar reaches full before it goes away
        Assert.False(vm.IsBusy);
        Assert.Equal("Backed up 1 save", vm.ResultText);
    }
}

file sealed class ViewModelTestsPlatform : IPlatformServices
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText) => Task.FromResult(true);
    public Task AlertAsync(string title, string message) => Task.CompletedTask;
    public Task<string?> PickFolderAsync(string title, string? startFolder) => Task.FromResult<string?>(null);
    public void OpenFolder(string path) { }
    public void OpenUrl(string url) { }
    public void CopyToClipboard(string text) { }
    public void Quit() { }
}

public class LogPanelTests
{
    [Fact]
    public void Errors_are_counted_and_the_log_moves_between_drawer_and_window()
    {
        using var t = new TempDir();
        var vm = new MainViewModel(new ViewModelTestsPlatform(), t.Saves, t.Backups, null);
        vm.Log.Info("hello");
        vm.Log.Error("boom");
        Assert.Equal(1, vm.LogErrorCount);
        Assert.True(vm.HasLogErrors);

        vm.ToggleLogCommand.Execute(null);
        Assert.True(vm.IsLogOpen);
        vm.PopOutLogCommand.Execute(null);
        Assert.False(vm.IsLogOpen);          // never both at once
        Assert.True(vm.IsLogPoppedOut);
        vm.DockLogCommand.Execute(null);
        Assert.True(vm.IsLogOpen);
        Assert.False(vm.IsLogPoppedOut);

        vm.ClearLogCommand.Execute(null);
        Assert.Equal(0, vm.LogErrorCount);
        Assert.Empty(vm.LogEntries);
    }
}

public class MiniPlayerTests
{
    [Fact]
    public void Mini_player_label_follows_the_track_and_mute()
    {
        using var t = new TempDir();
        var vm = new MainViewModel(new ViewModelTestsPlatform(), t.Saves, t.Backups, null);
        Assert.Equal("Music", vm.MiniPlayerTitle);
        vm.NowPlaying = "Laser City";
        Assert.Equal("Laser City", vm.MiniPlayerTitle);
        vm.MusicMuted = true;
        Assert.Equal("Music muted", vm.MiniPlayerTitle);
        vm.ToggleMusicPanelCommand.Execute(null);
        Assert.True(vm.IsMusicPanelOpen);
    }
}
