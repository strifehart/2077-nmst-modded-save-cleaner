using Nmst.SaveCleaner.Core.Backups;
using Nmst.SaveCleaner.Core.Logging;
using Nmst.SaveCleaner.Core.Saves;
using Nmst.SaveCleaner.ViewModels;

namespace Nmst.SaveCleaner.Tests;

/// <summary>Deleting saves to the app's trash, restoring them, and deleting them for good.</summary>
public class TrashTests
{
    private static readonly DateTime Created = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime Written = new(2024, 6, 7, 8, 9, 10, DateTimeKind.Utc);

    private static string? Any() => RealSaves.Find((_, _) => true);

    [Fact]
    public void Delete_then_restore_brings_the_save_back_exactly()
    {
        var src = Any();
        if (src is null) return;
        using var t = new TempDir();
        var save = t.CopySave(src);
        foreach (var f in Directory.GetFiles(save)) { File.SetCreationTimeUtc(f, Created); File.SetLastWriteTimeUtc(f, Written); }
        Directory.SetCreationTimeUtc(save, Created); Directory.SetLastWriteTimeUtc(save, Written);
        Directory.SetLastWriteTimeUtc(t.Saves, Written);
        var trash = new SaveTrash(Path.Combine(t.Root, "Trash"), new ActivityLog(null));

        var item = trash.Delete(SaveScanner.Read(save));
        Assert.False(Directory.Exists(save));
        Assert.Empty(SaveScanner.Scan(t.Saves));                            // the game (and the app) no longer see it
        Assert.Equal(Written, Directory.GetLastWriteTimeUtc(t.Saves));
        Assert.Equal(Path.GetFileName(save), Assert.Single(trash.List()).SaveName);

        var back = trash.Restore(item, t.Saves);
        Assert.Equal(save, back);
        Assert.Empty(trash.List());
        foreach (var f in Directory.GetFiles(src))
        {
            var r = Path.Combine(back, Path.GetFileName(f));
            Assert.True(BackupManager.FilesEqual(f, r), Path.GetFileName(f));
            Assert.Equal(Created, File.GetCreationTimeUtc(r));
            Assert.Equal(Written, File.GetLastWriteTimeUtc(r));
        }
        Assert.Equal(Directory.GetFiles(src).Length, Directory.GetFiles(back).Length);   // no trash.json left behind
        Assert.Equal(Created, Directory.GetCreationTimeUtc(back));
        Assert.Equal(Written, Directory.GetLastWriteTimeUtc(back));
        Assert.Equal(Written, Directory.GetLastWriteTimeUtc(t.Saves));
    }

    [Fact]
    public void A_name_taken_since_is_never_overwritten()
    {
        var src = Any();
        if (src is null) return;
        using var t = new TempDir();
        var save = t.CopySave(src);
        var trash = new SaveTrash(Path.Combine(t.Root, "Trash"), new ActivityLog(null));
        var item = trash.Delete(SaveScanner.Read(save));
        t.CopySave(src);                                                      // the game reused the slot

        var back = trash.Restore(item, t.Saves);
        Assert.Equal(Path.GetFileName(save) + " restored", Path.GetFileName(back));
        Assert.Equal(2, SaveScanner.Scan(t.Saves).Count);
    }

    [Fact]
    public void Deleting_forever_removes_it_from_the_trash()
    {
        var src = Any();
        if (src is null) return;
        using var t = new TempDir();
        var trash = new SaveTrash(Path.Combine(t.Root, "Trash"), new ActivityLog(null));
        var item = trash.Delete(SaveScanner.Read(t.CopySave(src)));
        trash.DeleteForever(item);
        Assert.Empty(trash.List());
        Assert.Empty(Directory.GetDirectories(Path.Combine(t.Root, "Trash")));
    }

    [Fact]
    public void The_trash_sorts_by_deletion_time_or_by_name_in_natural_order()
    {
        var now = DateTime.Now;
        (int Count, bool Emptying) deleted = default;
        TrashItem Item(string name, int minutesAgo) => new($@"C:\nowhere\{name}", name, @"C:\saves", now.AddMinutes(-minutesAgo), null, 1, null);
        var vm = new TrashViewModel([Item("ManualSave-1069", 5), Item("Judy x FV Scene", 1), Item("ManualSave-11", 3), Item("zzz", 9)],
            Nmst.SaveCleaner.Core.Localization.GameNames.Empty, _ => Task.CompletedTask, (items, emptying) => { deleted = (items.Count, emptying); return Task.CompletedTask; }, () => { });

        Assert.Equal(["Judy x FV Scene", "ManualSave-11", "ManualSave-1069", "zzz"], vm.Rows.Select(r => r.Name));   // newest deletion first
        vm.Rows.Single(r => r.Name == "zzz").IsChecked = true;

        vm.SortByNameCommand.Execute(null);
        Assert.Equal(["Judy x FV Scene", "ManualSave-11", "ManualSave-1069", "zzz"], vm.Rows.Select(r => r.Name));   // 11 before 1069
        Assert.Equal("Name A–Z", vm.NameSortLabel);
        vm.SortByNameCommand.Execute(null);                                    // again: reversed
        Assert.Equal(["zzz", "ManualSave-1069", "ManualSave-11", "Judy x FV Scene"], vm.Rows.Select(r => r.Name));
        Assert.True(vm.Rows.Single(r => r.Name == "zzz").IsChecked);          // ticks survive a re-sort

        vm.SortByDeletedCommand.Execute(null);
        Assert.Equal("zzz", vm.Rows.Last().Name);                              // oldest deletion last

        // the delete buttons say what they cover: the ticked saves, or the whole trash
        Assert.Equal("Delete permanently", vm.DeleteForeverText);
        vm.Rows[0].IsChecked = true;
        Assert.Equal("Delete 2 permanently", vm.DeleteForeverText);
        vm.DeleteForeverCommand.Execute(null);
        Assert.Equal((2, false), deleted);
        vm.EmptyCommand.Execute(null);
        Assert.Equal((4, true), deleted);
    }

    [Fact]
    public async Task The_app_deletes_and_restores_through_the_trash_view()
    {
        var src = Any();
        if (src is null) return;
        using var t = new TempDir();
        var save = t.CopySave(src);
        var vm = new MainViewModel(new ViewModelTests.FakePlatform(), t.Saves, t.Backups, null) { MinBusyTime = TimeSpan.Zero };
        await vm.ReloadCommand.ExecuteAsync(null);
        vm.ToggleStarCommand.Execute(vm.Focused);

        await vm.DeleteFocusedCommand.ExecuteAsync(null);
        Assert.False(Directory.Exists(save));
        Assert.Null(vm.Focused);

        await vm.OpenTrashCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.TrashView!.Rows);
        row.IsChecked = true;
        await vm.TrashView.RestoreCommand.ExecuteAsync(null);

        Assert.True(Directory.Exists(save));
        Assert.Empty(vm.TrashView.Rows);
        Assert.True(vm.Focused!.IsStarred);                                   // the star came back with it
    }
}
