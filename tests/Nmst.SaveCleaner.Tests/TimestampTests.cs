using Nmst.SaveCleaner.Core.Backups;
using Nmst.SaveCleaner.Core.Logging;
using Nmst.SaveCleaner.Core.Saves;

namespace Nmst.SaveCleaner.Tests;

/// <summary>
/// Cleaning, backing up and restoring must keep a save's file and folder dates (created + modified), so Explorer,
/// sync tools and the user's own sorting still see the save as it was.
/// </summary>
public class TimestampTests
{
    private static readonly DateTime Created = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime Written = new(2024, 6, 7, 8, 9, 10, DateTimeKind.Utc);

    private static (BackupManager, TagCleaner) Setup(TempDir t)
    {
        var log = new ActivityLog(null);
        var b = new BackupManager(t.Backups, log);
        return (b, new TagCleaner(b, log));
    }

    /// <summary>Copies a save and stamps every file (and the folder) with the same old dates.</summary>
    private static string OldSave(TempDir t, string source)
    {
        var save = t.CopySave(source);
        foreach (var f in Directory.GetFiles(save)) { File.SetCreationTimeUtc(f, Created); File.SetLastWriteTimeUtc(f, Written); }
        Directory.SetCreationTimeUtc(save, Created);
        Directory.SetLastWriteTimeUtc(save, Written);
        return save;
    }

    private static void AssertOldDates(string folder, bool checkFolder = true)
    {
        foreach (var f in Directory.GetFiles(folder).Where(f => Path.GetFileName(f) != "backup.json"))
        {
            Assert.Equal(Created, File.GetCreationTimeUtc(f));
            Assert.Equal(Written, File.GetLastWriteTimeUtc(f));
        }
        if (!checkFolder) return;
        Assert.Equal(Created, Directory.GetCreationTimeUtc(folder));
        Assert.Equal(Written, Directory.GetLastWriteTimeUtc(folder));
    }

    [Fact]
    public void Cleaning_keeps_the_dates()
    {
        var tagged = RealSaves.Find((m, s) => m.IsModded && s == true);
        if (tagged is null) return;
        using var t = new TempDir();
        var save = OldSave(t, tagged);
        var (_, cleaner) = Setup(t);

        Assert.Equal(CleanOutcome.Cleaned, cleaner.Clean(save).Outcome);

        AssertOldDates(save);
    }

    [Fact]
    public void Backups_keep_the_dates_and_restore_puts_them_back()
    {
        var tagged = RealSaves.Find((m, s) => m.IsModded && s == true);
        if (tagged is null) return;
        using var t = new TempDir();
        var save = OldSave(t, tagged);
        var (backups, cleaner) = Setup(t);

        var b = backups.Create(SaveScanner.Read(save), "Manual");
        AssertOldDates(b.FolderPath, checkFolder: false);   // the backup folder itself is new, its files aren't

        cleaner.Clean(save);
        backups.Restore(b, t.Saves);
        AssertOldDates(save);
    }

    [Fact]
    public void Restoring_a_deleted_save_brings_its_dates_back()
    {
        var any = RealSaves.Find((_, _) => true);
        if (any is null) return;
        using var t = new TempDir();
        var save = OldSave(t, any);
        var (backups, _) = Setup(t);

        var b = backups.Create(SaveScanner.Read(save), "Manual");
        Directory.Delete(save, true);
        backups.Restore(b, t.Saves);
        AssertOldDates(save);
    }
}
