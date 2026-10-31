using Nmst.SaveCleaner.Core.Logging;
using Nmst.SaveCleaner.Core.Saves;
using Nmst.SaveCleaner.ViewModels;

namespace Nmst.SaveCleaner.Tests;

public class ViewModelTests
{
    private sealed class FakePlatform : IPlatformServices
    {
        public List<string> Confirms { get; } = [];
        public Task<bool> ConfirmAsync(string title, string message, string confirmText) { Confirms.Add(message); return Task.FromResult(true); }
        public Task AlertAsync(string title, string message) => Task.CompletedTask;
        public string? Pick { get; set; }
        public Task<string?> PickFolderAsync(string title, string? startFolder) => Task.FromResult(Pick);
        public void OpenFolder(string path) { }
        public void OpenUrl(string url) { }
        public void CopyToClipboard(string text) { }
        public void Quit() { }
    }

    private static async Task WaitForFlags(MainViewModel vm)
    {
        var end = DateTime.Now.AddSeconds(30);
        while (vm.StatusLine.Contains("checking") || vm.StatusLine.Length == 0)
        {
            if (DateTime.Now > end) throw new TimeoutException(vm.StatusLine + " | " + string.Join(" / ", vm.LogEntries) + " | focused=" + vm.Focused?.Name + " state=" + vm.Focused?.State + " readable=" + vm.Focused?.IsReadable);
            await Task.Delay(20);
        }
    }

    [Fact]
    public void Disclaimer_must_be_ticked_and_is_never_preaccepted()
    {
        using var t = new TempDir();
        var vm = new MainViewModel(new FakePlatform(), t.Saves, t.Backups, null);
        Assert.False(vm.DisclaimerAccepted);
        Assert.False(vm.AcceptDisclaimerCommand.CanExecute(null));
        vm.DisclaimerTicked = true;
        Assert.True(vm.AcceptDisclaimerCommand.CanExecute(null));
        Assert.False(new MainViewModel(new FakePlatform(), t.Saves, t.Backups, null).DisclaimerAccepted);
    }

    [Fact]
    public async Task Batch_clean_cleans_tagged_skips_clean_and_backs_up_each()
    {
        var tagged = RealSaves.Find((m, s) => m.IsModded && s == true);
        var sticky = RealSaves.Find((m, s) => !m.IsModded && s == true);
        var clean = RealSaves.Find((m, s) => !m.IsModded && s == false);
        if (tagged is null || sticky is null || clean is null) return;

        using var t = new TempDir();
        foreach (var s in new[] { tagged, sticky, clean }) t.CopySave(s);
        var platform = new FakePlatform();
        var vm = new MainViewModel(platform, t.Saves, t.Backups, null) { IsGameRunning = () => false };
        await vm.ReloadCommand.ExecuteAsync(null);
        await WaitForFlags(vm);

        vm.SelectAllCommand.Execute(null);
        await Task.Delay(50); // batch stats update once, posted after the checkbox changes
        Assert.Equal(3, vm.CheckedCount);
        Assert.Equal(2, vm.BatchToClean);
        Assert.Equal(1, vm.BatchClean);

        await vm.CleanCheckedCommand.ExecuteAsync(null);

        Assert.Single(platform.Confirms);
        Assert.Contains("1 already clean save is skipped", platform.Confirms[0]);
        foreach (var dir in Directory.GetDirectories(t.Saves))
        {
            var info = SaveScanner.Read(dir);
            Assert.False(info.Metadata!.IsModded);
            Assert.NotEqual(true, ModdedFlag.ReadSticky(info.SavPath));
        }
        Assert.Equal(2, Directory.GetDirectories(t.Backups).Length);
        Assert.False(Directory.Exists(Path.Combine(t.Backups, Path.GetFileName(clean))));
        Assert.Contains(vm.LogEntries, e => e.Level == LogLevel.Done && e.Message == "2 cleaned, 1 skipped, 0 failed");
        Assert.Equal(3, vm.BatchClean);
    }

    [Fact]
    public async Task Clearing_the_search_brings_every_row_back()
    {
        var any = RealSaves.Find((_, _) => true);
        if (any is null) return;
        using var t = new TempDir();
        t.CopySave(any);
        var vm = new MainViewModel(new FakePlatform(), t.Saves, t.Backups, null) { IsGameRunning = () => false };
        await vm.ReloadCommand.ExecuteAsync(null);
        await WaitForFlags(vm);
        int all = vm.Rows.Count;

        vm.SearchText = "no save is called this";
        Assert.True(vm.Rows.Count < all);

        vm.ClearSearchCommand.Execute(null);
        Assert.Equal("", vm.SearchText);
        Assert.Equal(all, vm.Rows.Count);
    }

    [Fact]
    public async Task Nothing_is_written_while_the_game_runs()
    {
        var tagged = RealSaves.Find((m, s) => m.IsModded && s == true);
        if (tagged is null) return;
        using var t = new TempDir();
        var save = t.CopySave(tagged);
        var before = Directory.GetFiles(save).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes);
        var vm = new MainViewModel(new FakePlatform(), t.Saves, t.Backups, null) { IsGameRunning = () => true };
        await vm.ReloadCommand.ExecuteAsync(null);
        await WaitForFlags(vm);

        await vm.CleanFocusedCommand.ExecuteAsync(null);

        foreach (var (name, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(save, name!)));
        Assert.False(Directory.Exists(t.Backups) && Directory.GetDirectories(t.Backups).Length > 0);
        Assert.Contains(vm.LogEntries, e => e.Level == LogLevel.Warn);
    }
}
