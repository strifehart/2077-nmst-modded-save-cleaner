using Nmst.SaveCleaner.Core.Backups;
using Nmst.SaveCleaner.Core.Logging;
using Nmst.SaveCleaner.Core.Saves;

namespace Nmst.SaveCleaner.Tests;

/// <summary>Forces things to go wrong halfway and checks that saves and backups are never left in a mixed state.</summary>
public class FailureTests
{
    private static (BackupManager, TagCleaner) Setup(TempDir t)
    {
        var log = new ActivityLog(null);
        var b = new BackupManager(t.Backups, log);
        return (b, new TagCleaner(b, log));
    }

    [Fact]
    public void A_clean_that_fails_halfway_is_rolled_back_without_extra_backups_or_temp_files()
    {
        var src = RealSaves.Find((m, s) => m.IsModded && s == true);
        if (src is null) return;
        using var t = new TempDir();
        var (backups, cleaner) = Setup(t);
        var save = t.CopySave(src);
        // sav.dat gets rewritten first; then writing the metadata fails because its temp path is taken by a folder
        Directory.CreateDirectory(Path.Combine(save, SaveInfo.MetadataFile + ".tmp"));

        var r = cleaner.Clean(save);

        Assert.Equal(CleanOutcome.Failed, r.Outcome);
        Assert.True(BackupManager.FilesEqual(Path.Combine(src, SaveInfo.SavFileName), Path.Combine(save, SaveInfo.SavFileName)), "sav.dat was not put back");
        Assert.True(BackupManager.FilesEqual(Path.Combine(src, SaveInfo.MetadataFile), Path.Combine(save, SaveInfo.MetadataFile)));
        Assert.False(File.Exists(Path.Combine(save, SaveInfo.SavFileName + ".tmp")));
        var b = Assert.Single(backups.List(Path.GetFileName(save)));   // only "Before clean", no "Before restore" of the broken state
        Assert.Equal("Before clean", b.Reason);
    }

    [Fact]
    public void A_backup_that_fails_leaves_no_half_finished_folder()
    {
        var src = RealSaves.Find((m, s) => !m.IsModded && s == false);
        if (src is null) return;
        using var t = new TempDir();
        var (backups, _) = Setup(t);
        var save = t.CopySave(src);

        using (new FileStream(Path.Combine(save, SaveInfo.ScreenshotFile), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(() => backups.Create(SaveScanner.Read(save), "Manual"));

        var dir = Path.Combine(t.Backups, Path.GetFileName(save));
        Assert.Empty(Directory.Exists(dir) ? Directory.GetDirectories(dir) : []);
    }

    [Fact]
    public void A_restore_that_fails_halfway_changes_nothing()
    {
        var src = RealSaves.Find((m, s) => !m.IsModded && s == false);
        var other = RealSaves.Find((m, s) => m.IsModded && s == true);
        if (src is null || other is null) return;
        using var t = new TempDir();
        var (backups, _) = Setup(t);
        var save = t.CopySave(src);
        var b = backups.Create(SaveScanner.Read(save), "Manual");
        // make the current save different from the backup, then block the last file of the restore
        foreach (var f in Directory.GetFiles(other)) File.Copy(f, Path.Combine(save, Path.GetFileName(f)), true);
        var current = Directory.GetFiles(save).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes);
        Directory.CreateDirectory(Path.Combine(save, SaveInfo.ScreenshotFile + ".restoring"));

        Assert.ThrowsAny<Exception>(() => backups.Restore(b, t.Saves, backupCurrent: false));

        foreach (var (name, bytes) in current) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(save, name)));
        Assert.Empty(Directory.GetFiles(save, "*.restoring"));
    }

    [Fact]
    public void Backups_from_another_saves_folder_are_recognised()
    {
        var b = new BackupInfo("x", "ManualSave-1", DateTime.Now, "Manual", null, null, 0, @"C:\A\Cyberpunk 2077\ManualSave-1");
        Assert.True(b.IsFromOtherFolder(@"C:\B\Cyberpunk 2077\ManualSave-1"));
        Assert.False(b.IsFromOtherFolder(@"c:\a\cyberpunk 2077\ManualSave-1\"));
    }

    [Fact]
    public void Folder_detection_never_throws_on_garbage()
    {
        using var t = new TempDir();
        var steam = Directory.CreateDirectory(Path.Combine(t.Root, "home", ".local", "share", "Steam", "steamapps")).FullName;
        File.WriteAllText(Path.Combine(steam, "libraryfolders.vdf"), "\"path\" \"\0bad|<>path\" garbage {{{");
        _ = SavesLocator.ProtonCandidates(Path.Combine(t.Root, "home")).ToList();
        Assert.False(string.IsNullOrEmpty(SavesLocator.Detect()));
        Assert.False(SavesLocator.HasSaves(Path.Combine(t.Root, "missing")));
    }
}
