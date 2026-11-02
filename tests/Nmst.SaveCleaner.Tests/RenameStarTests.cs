using Nmst.SaveCleaner.Core.Backups;
using Nmst.SaveCleaner.Core.Logging;
using Nmst.SaveCleaner.Core.Saves;
using Nmst.SaveCleaner.ViewModels;

namespace Nmst.SaveCleaner.Tests;

/// <summary>Renaming saves (folder rename, dates kept, backups follow) and starring them.</summary>
public class RenameStarTests
{
    private static readonly DateTime Created = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime Written = new(2024, 6, 7, 8, 9, 10, DateTimeKind.Utc);

    private static string? Tagged() => RealSaves.Find((m, s) => m.IsModded && s == true);
    private static string? Any() => RealSaves.Find((_, _) => true);

    private static (BackupManager, BatchLog, ActivityLog) Core(TempDir t)
    {
        var log = new ActivityLog(null);
        return (new BackupManager(t.Backups, log), new BatchLog(Path.Combine(t.Backups, "batches.json")), log);
    }

    [Fact]
    public void Renaming_moves_the_folder_and_keeps_every_date_and_byte()
    {
        var src = Any();
        if (src is null) return;
        using var t = new TempDir();
        var save = t.CopySave(src);
        foreach (var f in Directory.GetFiles(save)) { File.SetCreationTimeUtc(f, Created); File.SetLastWriteTimeUtc(f, Written); }
        Directory.SetCreationTimeUtc(save, Created); Directory.SetLastWriteTimeUtc(save, Written);
        Directory.SetCreationTimeUtc(t.Saves, Created); Directory.SetLastWriteTimeUtc(t.Saves, Written);
        var (backups, batches, log) = Core(t);

        var renamed = SaveRenamer.Rename(SaveScanner.Read(save), "Before Konpeki", backups, batches, log);

        Assert.False(Directory.Exists(save));
        Assert.Equal(Path.Combine(t.Saves, "Before Konpeki"), renamed);
        foreach (var f in Directory.GetFiles(src))
        {
            var r = Path.Combine(renamed, Path.GetFileName(f));
            Assert.True(BackupManager.FilesEqual(f, r), Path.GetFileName(f));
            Assert.Equal(Created, File.GetCreationTimeUtc(r));
            Assert.Equal(Written, File.GetLastWriteTimeUtc(r));
        }
        Assert.Equal(Created, Directory.GetCreationTimeUtc(renamed));
        Assert.Equal(Written, Directory.GetLastWriteTimeUtc(renamed));
        Assert.Equal(Written, Directory.GetLastWriteTimeUtc(t.Saves));       // the saves folder's own modified date too
        Assert.Equal(Path.GetFileName(src), SaveScanner.Read(renamed).Metadata!.Name);   // the original name stays in the save
    }

    [Fact]
    public void Backups_and_undo_follow_the_new_name()
    {
        var src = Tagged();
        if (src is null) return;
        using var t = new TempDir();
        var save = t.CopySave(src);
        var (backups, batches, log) = Core(t);
        var cleaned = new TagCleaner(backups, log).Clean(save);
        batches.Add(new BatchRecord("b1", BatchKind.Clean, DateTime.UtcNow, t.Saves, [BatchLog.Snapshot(save, cleaned.BackupId!)]));

        var renamed = SaveRenamer.Rename(SaveScanner.Read(save), "Kept one", backups, batches, log);

        Assert.Empty(backups.List(Path.GetFileName(save)));
        var moved = Assert.Single(backups.List("Kept one"));
        Assert.False(moved.IsFromOtherFolder(renamed));                       // still this save's backup, not "another folder"
        Assert.Equal("Kept one", Assert.Single(batches.Last(t.Saves)!.Saves).SaveFolderName);
        Assert.Equal("Before clean", RestorePlanner.Plan(renamed, backups.List("Kept one"), RestoreRule.UndoCleaning).Chosen?.Reason);
        Assert.False(BatchLog.ChangedSince(batches.Last(t.Saves)!.Saves[0], t.Saves));   // renaming isn't "the game saved over it"
    }

    [Fact]
    public void Names_are_checked()
    {
        var src = Any();
        if (src is null) return;
        using var t = new TempDir();
        var save = SaveScanner.Read(t.CopySave(src));
        Directory.CreateDirectory(Path.Combine(t.Saves, "Taken"));
        var original = save.Metadata!.Name;

        Assert.Null(SaveRenamer.Validate(save, "Before Konpeki"));
        Assert.NotNull(SaveRenamer.Validate(save, "  "));
        Assert.NotNull(SaveRenamer.Validate(save, new string('x', SaveRenamer.MaxLength + 1)));
        Assert.NotNull(SaveRenamer.Validate(save, "a:b"));
        Assert.NotNull(SaveRenamer.Validate(save, "trailing."));
        Assert.NotNull(SaveRenamer.Validate(save, "CON"));
        Assert.NotNull(SaveRenamer.Validate(save, "taken"));                  // another save already has it (any case)
        Assert.NotNull(SaveRenamer.Validate(save, "AutoSave-9"));             // the game reuses those
        Assert.NotNull(SaveRenamer.Validate(save, "ManualSave-99999"));
        Assert.NotNull(SaveRenamer.Validate(save, save.FolderName));          // no change
        Assert.Null(SaveRenamer.Validate(save, "Kept AutoSave-9 2025-07-05")); // only the exact pattern is refused

        // once renamed, its own original name (a game pattern) is allowed again: that's "Reset name"
        var (backups, batches, log) = Core(t);
        var renamed = SaveScanner.Read(SaveRenamer.Rename(save, "Something else", backups, batches, log));
        Assert.Null(SaveRenamer.Validate(renamed, original));
        Assert.Equal(save.Kind, renamed.Kind);                                // still known as a manual/auto save from its metadata
    }

    [Fact]
    public void Stars_are_kept_outside_the_save_and_survive_restarts()
    {
        var src = Any();
        if (src is null) return;
        using var t = new TempDir();
        var save = t.CopySave(src);
        var before = Directory.GetFiles(save).ToDictionary(Path.GetFileName, File.ReadAllBytes);
        var path = Path.Combine(t.Root, "saves.json");

        var notes = new SaveNotes(path);
        notes.SetStarred(SaveScanner.Read(save), true);
        notes.Save();

        Assert.True(new SaveNotes(path).IsStarred(SaveScanner.Read(save)));
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(save).Select(Path.GetFileName).Order());   // no file added to the save
        foreach (var (name, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(save, name!)));
    }

    /// <summary>Two folders holding the same save: the source under its own name and a copy named "Copy A".</summary>
    private static (string, string) TwoCopies(TempDir t, string src)
    {
        var a = t.CopySave(src);
        var b = Directory.CreateDirectory(Path.Combine(t.Saves, "Copy A")).FullName;
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(b, Path.GetFileName(f)));
        return (a, b);
    }

    [Fact]
    public void Each_copy_of_a_save_has_its_own_star()
    {
        var src = Any();
        if (src is null) return;
        using var t = new TempDir();
        var (a, b) = TwoCopies(t, src);
        var notes = new SaveNotes(Path.Combine(t.Root, "saves.json"));

        notes.SetStarred(SaveScanner.Read(a), true);
        Assert.True(notes.IsStarred(SaveScanner.Read(a)));
        Assert.False(notes.IsStarred(SaveScanner.Read(b)));
        Assert.Equal(SaveNotes.IdentityOf(SaveScanner.Read(a).Metadata), SaveNotes.IdentityOf(SaveScanner.Read(b).Metadata));
    }

    [Fact]
    public async Task Duplicates_are_marked_and_filtered_and_old_stars_carry_over()
    {
        var src = Any();
        if (src is null) return;
        using var t = new TempDir();
        var (a, b) = TwoCopies(t, src);
        // a saves.json from before stars were per copy: keyed by identity alone
        var id = SaveNotes.IdentityOf(SaveScanner.Read(a).Metadata)!;
        File.WriteAllText(Path.Combine(t.Root, "saves.json"),
            $$"""{ "Version": 1, "Saves": { "{{id}}": { "Starred": true, "StarredUtc": null, "Renames": [] } } }""");

        var vm = new MainViewModel(new ViewModelTests.FakePlatform(), t.Saves, t.Backups, null) { MinBusyTime = TimeSpan.Zero };
        await vm.ReloadCommand.ExecuteAsync(null);
        vm.ExpandAllCommand.Execute(null);
        var items = vm.Rows.OfType<SaveItemViewModel>().ToList();
        var ia = items.Single(i => i.Name == Path.GetFileName(a));
        var ib = items.Single(i => i.Name == "Copy A");

        Assert.True(ia.IsStarred && ib.IsStarred);                              // what looked starred stays starred
        Assert.Equal(["Copy A"], ia.DuplicateOf);
        Assert.Equal("Same save as " + Path.GetFileName(a), ib.DuplicateLine);
        Assert.Equal(2, vm.DuplicateCount);
        vm.Filter = MainViewModel.ListFilter.Duplicates;
        Assert.Equal(2, vm.Rows.OfType<SaveItemViewModel>().Count());

        // from now on they're separate: unstarring one leaves the other
        vm.ToggleStarCommand.Execute(ib);
        Assert.True(ia.IsStarred);
        Assert.False(ib.IsStarred);
        var notes = new SaveNotes(Path.Combine(t.Root, "saves.json"));
        Assert.True(notes.IsStarred(SaveScanner.Read(a)));
        Assert.False(notes.IsStarred(SaveScanner.Read(b)));

        // deleting one copy leaves no duplicates: the filter goes back to all saves and its button goes
        vm.FocusSaveCommand.Execute(ib);
        await vm.DeleteFocusedCommand.ExecuteAsync(null);
        Assert.Equal(0, vm.DuplicateCount);
        Assert.Equal(MainViewModel.ListFilter.All, vm.Filter);
        Assert.False(vm.HasDuplicateSaves);
        Assert.Equal(2, vm.ShowAllSpan);
    }

    private static async Task<T> WaitFor<T>(Func<T?> get) where T : class
    {
        var end = DateTime.Now.AddSeconds(30);
        while (true)
        {
            if (get() is { } v) return v;
            if (DateTime.Now > end) throw new TimeoutException();
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task A_starred_save_keeps_its_star_through_rename_and_reset_and_the_filter_shows_it()
    {
        var src = Tagged();
        var other = RealSaves.Find((m, s) => s == false || !m.IsModded);
        if (src is null || other is null) return;
        using var t = new TempDir();
        var save = t.CopySave(src);
        t.CopySave(other);
        var vm = new MainViewModel(new ViewModelTests.FakePlatform(), t.Saves, t.Backups, null) { MinBusyTime = TimeSpan.Zero };
        await vm.ReloadCommand.ExecuteAsync(null);
        var item = vm.Rows.OfType<SaveItemViewModel>().Concat(vm.Rows.OfType<PlaythroughGroupViewModel>().SelectMany(g => g.Items))
            .First(i => i.Name == Path.GetFileName(save));
        var original = item.OriginalName;   // the game's name (the folder may already be renamed)

        vm.ToggleStarCommand.Execute(item);
        Assert.True(item.IsStarred);
        vm.ShowStarredOnly = true;
        Assert.Equal([item.Name], vm.Rows.OfType<SaveItemViewModel>().Select(i => i.Name));
        Assert.Equal(1, vm.StarredCount);

        // a group can be collapsed while filtering, and a new filter opens it again
        var group = vm.Rows.OfType<PlaythroughGroupViewModel>().Single();
        group.ToggleExpandedCommand.Execute(null);
        Assert.Empty(vm.Rows.OfType<SaveItemViewModel>());
        Assert.Single(vm.Rows.OfType<PlaythroughGroupViewModel>());
        vm.Filter = MainViewModel.ListFilter.All;
        vm.ShowStarredOnly = true;
        Assert.Single(vm.Rows.OfType<SaveItemViewModel>());

        // rename through the dialog
        vm.FocusSaveCommand.Execute(item);
        var run = vm.RenameFocusedCommand.ExecuteAsync(null);
        var dialog = await WaitFor(() => vm.RenameDialog);
        dialog.Name = "AutoSave-3";
        Assert.False(dialog.IsValid);
        dialog.Name = "Before Konpeki";
        Assert.True(dialog.IsValid);
        dialog.ConfirmCommand.Execute(null);
        await run;

        Assert.True(Directory.Exists(Path.Combine(t.Saves, "Before Konpeki")), string.Join(" / ", vm.LogEntries.TakeLast(5)));
        var renamed = vm.Focused!;
        Assert.Equal("Before Konpeki", renamed.Name);
        Assert.True(renamed.IsStarred);                                        // the star followed the save
        Assert.True(renamed.IsRenamed);
        Assert.Equal("Renamed from " + original, renamed.RenamedFromLine);
        Assert.True(new SaveNotes(Path.Combine(t.Root, "saves.json")).RenamesOf(renamed.Info).Count == 1);

        vm.Filter = MainViewModel.ListFilter.Renamed;
        Assert.Contains(vm.Rows.OfType<SaveItemViewModel>(), i => i.Name == "Before Konpeki");
        Assert.True(vm.RenamedCount >= 1);
        vm.Filter = MainViewModel.ListFilter.All;

        // reset back to the game's name
        await vm.ResetNameCommand.ExecuteAsync(null);
        Assert.Equal(original, vm.Focused!.Name);
        vm.Filter = MainViewModel.ListFilter.Renamed;   // nothing renamed now, unless the test folder had renamed saves already
        Assert.DoesNotContain(vm.Rows.OfType<SaveItemViewModel>(), i => i.Name == original);
        vm.Filter = MainViewModel.ListFilter.All;
        Assert.True(vm.Focused.IsStarred);
        Assert.False(vm.Focused.IsRenamed);
    }

    [Theory]
    [InlineData("AutoSave-3", true, "Keep this autosave")]
    [InlineData("QuickSave-2", true, "Keep this quicksave")]
    [InlineData("ManualSave-12", false, "Keep this autosave")]
    public void Keep_this_slot_names_the_kind_of_save(string folder, bool rotating, string title)
    {
        var item = new SaveItemViewModel(new SaveInfo(Path.Combine(@"C:saves", folder), null, null, 0, DateTime.Now), Nmst.SaveCleaner.Core.Localization.GameNames.Empty, 0);
        Assert.Equal(rotating, item.IsInRotatingSlot);
        if (rotating) Assert.Equal(title, item.KeepSlotTitle);
    }

    [Theory]
    [InlineData("VeryHard", "Very Hard")]
    [InlineData("Story", "Story")]
    [InlineData("Easy", "Easy")]
    public void Difficulty_reads_like_the_game_menu(string stored, string shown) => Assert.Equal(shown, Format.Difficulty(stored));
}
