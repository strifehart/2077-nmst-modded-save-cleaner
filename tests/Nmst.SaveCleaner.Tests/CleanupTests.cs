using Nmst.SaveCleaner.Core.Backups;
using Nmst.SaveCleaner.Core.Logging;
using Nmst.SaveCleaner.Core.Saves;
using Nmst.SaveCleaner.ViewModels;

namespace Nmst.SaveCleaner.Tests;

/// <summary>Protected backups (original / manual / undo) and "Clean up backups".</summary>
public class CleanupTests
{
    private static string? Tagged() => RealSaves.Find((m, s) => m.IsModded && s == true);

    private static (TempDir, string, BackupManager, TagCleaner) Setup(string src)
    {
        var t = new TempDir();
        var save = t.CopySave(src);
        var log = new ActivityLog(null);
        var backups = new BackupManager(t.Backups, log);
        return (t, save, backups, new TagCleaner(backups, log));
    }

    [Fact]
    public void The_before_clean_backup_of_the_current_version_is_the_protected_original()
    {
        var src = Tagged();
        if (src is null) return;
        var (t, save, backups, cleaner) = Setup(src);
        using var _ = t;
        var name = Path.GetFileName(save);

        cleaner.Clean(save);
        backups.Create(SaveScanner.Read(save), "Manual");
        var all = backups.List(name);
        var current = SaveScanner.Read(save).Metadata;
        var original = all.Single(b => b.Reason == "Before clean");
        var manual = all.Single(b => b.Reason == "Manual");

        Assert.Equal(BackupProtection.Original, BackupCatalog.Protection(original, all, current, new HashSet<string>()));
        Assert.Equal(BackupProtection.Manual, BackupCatalog.Protection(manual, all, current, new HashSet<string>()));
        Assert.Equal(BackupProtection.Original | BackupProtection.Undo, BackupCatalog.Protection(original, all, current, new HashSet<string> { original.Id }));
        Assert.Empty(BackupCatalog.Candidates(backups, t.Saves, new HashSet<string>()));   // nothing unprotected yet
    }

    [Fact]
    public void Cleanup_offers_before_restore_copies_and_same_as_save_but_never_the_original()
    {
        var src = Tagged();
        if (src is null) return;
        var (t, save, backups, cleaner) = Setup(src);
        using var _ = t;
        var name = Path.GetFileName(save);

        var cleaned = cleaner.Clean(save);
        backups.Restore(backups.List(name).Single(b => b.Id == cleaned.BackupId), t.Saves);   // undo: Before restore = the cleaned state
        var c1 = BackupCatalog.Candidates(backups, t.Saves, new HashSet<string>());
        Assert.Equal(CleanupGroup.BeforeRestore, Assert.Single(c1).Group);

        cleaner.Clean(save);   // cleaned again: the save now equals that Before restore copy
        var c2 = BackupCatalog.Candidates(backups, t.Saves, new HashSet<string>());
        Assert.Equal(CleanupGroup.SameAsSave, Assert.Single(c2).Group);
        Assert.DoesNotContain(c2, c => c.Backup.Reason == "Before clean");
    }

    [Fact]
    public void Backups_of_a_deleted_save_are_offered_as_gone()
    {
        var src = Tagged();
        if (src is null) return;
        var (t, save, backups, cleaner) = Setup(src);
        using var _ = t;

        cleaner.Clean(save);
        Directory.Delete(save, true);
        var c = Assert.Single(BackupCatalog.Candidates(backups, t.Saves, new HashSet<string>()));
        Assert.Equal(CleanupGroup.SaveGone, c.Group);
    }

    [Fact]
    public async Task Cleaning_up_deletes_the_ticked_backups_and_keeps_the_original()
    {
        var src = Tagged();
        if (src is null) return;
        var (t, save, backups, cleaner) = Setup(src);
        using var _ = t;
        var name = Path.GetFileName(save);
        var cleaned = cleaner.Clean(save);
        backups.Restore(backups.List(name).Single(b => b.Id == cleaned.BackupId), t.Saves);

        var vm = new MainViewModel(new ViewModelTests.FakePlatform(), t.Saves, t.Backups, null) { IsGameRunning = () => false, MinBusyTime = TimeSpan.Zero };
        await vm.ReloadCommand.ExecuteAsync(null);
        var run = vm.CleanUpBackupsCommand.ExecuteAsync(null);
        var end = DateTime.Now.AddSeconds(30);
        while (vm.Cleanup is null) { if (DateTime.Now > end) throw new TimeoutException(); await Task.Delay(20); }
        var group = Assert.Single(vm.Cleanup.Groups);
        Assert.Equal("Before restore copies", group.Title);
        vm.Cleanup.ConfirmCommand.Execute(null);
        await run;

        var left = Assert.Single(backups.List(name));
        Assert.Equal("Before clean", left.Reason);
    }
}
