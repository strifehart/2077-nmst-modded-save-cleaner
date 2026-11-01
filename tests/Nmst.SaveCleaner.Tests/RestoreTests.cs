using Nmst.SaveCleaner.Core.Backups;
using Nmst.SaveCleaner.Core.Logging;
using Nmst.SaveCleaner.Core.Saves;
using Nmst.SaveCleaner.ViewModels;

namespace Nmst.SaveCleaner.Tests;

/// <summary>Bulk restore (rule + review) and "Undo last batch".</summary>
public class RestoreTests
{
    private static List<string> TaggedSaves(int n) => !RealSaves.Available ? [] :
        SaveScanner.Scan(RealSaves.Folder)
            .Where(s => s.Metadata?.IsModded == true)
            .Where(s => { try { return ModdedFlag.ReadSticky(s.SavPath) == true; } catch { return false; } })
            .Take(n).Select(s => s.FolderPath).ToList();

    private static bool SameFiles(string a, string b) =>
        Directory.GetFiles(a).All(f => BackupManager.FilesEqual(f, Path.Combine(b, Path.GetFileName(f))));

    [Fact]
    public void Undo_cleaning_rule_picks_the_before_clean_backup_and_flags_newer_ones()
    {
        var src = TaggedSaves(1).FirstOrDefault();
        if (src is null) return;
        using var t = new TempDir();
        var save = t.CopySave(src);
        var log = new ActivityLog(null);
        var backups = new BackupManager(t.Backups, log);
        var cleaner = new TagCleaner(backups, log);

        cleaner.Clean(save);                                   // Before clean (tagged original)
        Thread.Sleep(5);
        backups.Create(SaveScanner.Read(save), "Manual");       // Manual, newer, identical to the cleaned save

        var plan = RestorePlanner.Plan(save, backups.List(Path.GetFileName(save)), RestoreRule.UndoCleaning);
        Assert.Equal("Before clean", plan.Chosen?.Reason);
        Assert.True(plan.NewerExists);

        // the manual backup is the save as it is now: nothing to restore
        var manual = RestorePlanner.Plan(save, backups.List(Path.GetFileName(save)), RestoreRule.LatestManual);
        Assert.Null(manual.Chosen);
        Assert.Equal("already the same as the backup", manual.SkipReason);
    }

    [Fact]
    public void Backing_up_an_unchanged_save_reuses_the_identical_backup()
    {
        var src = TaggedSaves(1).FirstOrDefault();
        if (src is null) return;
        using var t = new TempDir();
        var save = t.CopySave(src);
        var backups = new BackupManager(t.Backups, new ActivityLog(null));

        var first = backups.Create(SaveScanner.Read(save), "Manual");
        var second = backups.Create(SaveScanner.Read(save), "Manual");
        Assert.False(first.WasExisting);
        Assert.True(second.WasExisting);
        Assert.Equal(first.Id, second.Id);
        Assert.Single(backups.List(Path.GetFileName(save)));
    }

    [Fact]
    public void Clean_undo_clean_undo_keeps_just_two_backups()
    {
        var src = TaggedSaves(1).FirstOrDefault();
        if (src is null) return;
        using var t = new TempDir();
        var save = t.CopySave(src);
        var log = new ActivityLog(null);
        var backups = new BackupManager(t.Backups, log);
        var cleaner = new TagCleaner(backups, log);
        var name = Path.GetFileName(save);

        for (int round = 0; round < 2; round++)
        {
            var cleaned = cleaner.Clean(save);
            Assert.Equal(CleanOutcome.Cleaned, cleaned.Outcome);
            var original = backups.List(name).Single(b => b.Id == cleaned.BackupId);
            backups.Restore(original, t.Saves);
            Assert.True(SameFiles(src, save));
        }
        // one Before clean (the tagged original) + one Before restore (the cleaned state), reused on the second round
        Assert.Equal(["Before clean", "Before restore"], backups.List(name).Select(b => b.Reason).Order().ToArray());
    }

    [Fact]
    public void A_rule_with_no_matching_backup_skips_the_save_and_says_why()
    {
        var src = TaggedSaves(1).FirstOrDefault();
        if (src is null) return;
        using var t = new TempDir();
        var save = t.CopySave(src);
        var backups = new BackupManager(t.Backups, new ActivityLog(null));
        backups.Create(SaveScanner.Read(save), "Manual");

        var plan = RestorePlanner.Plan(save, backups.List(Path.GetFileName(save)), RestoreRule.UndoCleaning);
        Assert.Null(plan.Chosen);
        Assert.Equal("no Before clean backup", plan.SkipReason);
    }

    private static async Task<RestoreReviewViewModel> WaitForReview(MainViewModel vm)
    {
        var end = DateTime.Now.AddSeconds(30);
        while (vm.Review is null) { if (DateTime.Now > end) throw new TimeoutException("no review"); await Task.Delay(20); }
        return vm.Review;
    }

    private static async Task<(TempDir, MainViewModel, List<string>, List<string>)> CleanedBatch(int n)
    {
        var sources = TaggedSaves(n);
        var t = new TempDir();
        var saves = sources.Select(t.CopySave).ToList();
        var vm = new MainViewModel(new ViewModelTests.FakePlatform(), t.Saves, t.Backups, null) { IsGameRunning = () => false, MinBusyTime = TimeSpan.Zero };
        await vm.ReloadCommand.ExecuteAsync(null);
        await ViewModelTests.WaitForFlags(vm);
        vm.SelectTaggedCommand.Execute(null);
        await Task.Delay(50);
        await vm.CleanCheckedCommand.ExecuteAsync(null);
        return (t, vm, sources, saves);
    }

    [Fact]
    public async Task Bulk_restore_undoes_cleaning_for_every_selected_save()
    {
        if (TaggedSaves(2).Count < 2) return;
        var (t, vm, sources, saves) = await CleanedBatch(2);
        using var _ = t;
        Assert.All(saves, s => Assert.False(SaveScanner.Read(s).Metadata!.IsModded));

        vm.SelectAllCommand.Execute(null);
        await Task.Delay(50);
        var run = vm.RestoreCheckedCommand.ExecuteAsync(null);
        var review = await WaitForReview(vm);
        Assert.Equal(2, review.RestoreCount);          // one Before clean each: both ready, nothing to check
        Assert.Equal(2, review.Ready.Count);
        Assert.Empty(review.NeedsLook);
        review.ConfirmCommand.Execute(null);
        await run;

        for (int i = 0; i < saves.Count; i++) Assert.True(SameFiles(sources[i], saves[i]), Path.GetFileName(saves[i]));
        Assert.Equal(BatchKind.Restore, vm.LastBatch?.Kind);
    }

    [Fact]
    public async Task Undo_last_batch_leaves_out_saves_changed_since_and_restores_the_rest()
    {
        if (TaggedSaves(2).Count < 2) return;
        var (t, vm, sources, saves) = await CleanedBatch(2);
        using var _ = t;
        Assert.Equal(BatchKind.Clean, vm.LastBatch?.Kind);
        Assert.Equal(2, vm.LastBatch!.Saves.Count);

        // the game "saves over" the second one after the batch
        var meta = Path.Combine(saves[1], SaveInfo.MetadataFile);
        File.SetLastWriteTimeUtc(meta, DateTime.UtcNow);

        var run = vm.UndoLastBatchCommand.ExecuteAsync(null);
        var review = await WaitForReview(vm);
        Assert.Equal(1, review.RestoreCount);
        var changed = Assert.Single(review.NeedsLook);
        Assert.Equal(Path.GetFileName(saves[1]), changed.Name);
        Assert.False(changed.IsIncluded);
        review.ConfirmCommand.Execute(null);
        await run;

        Assert.True(SameFiles(sources[0], saves[0]));                         // undone: the tagged original is back
        Assert.False(SaveScanner.Read(saves[1]).Metadata!.IsModded);          // left alone
    }

    [Fact]
    public async Task Cancelling_the_review_changes_nothing()
    {
        if (TaggedSaves(2).Count < 2) return;
        var (t, vm, _, saves) = await CleanedBatch(2);
        using var __ = t;
        var before = saves.Select(s => File.ReadAllBytes(Path.Combine(s, SaveInfo.SavFileName))).ToList();

        var run = vm.UndoLastBatchCommand.ExecuteAsync(null);
        (await WaitForReview(vm)).CancelCommand.Execute(null);
        await run;

        Assert.Null(vm.Review);
        for (int i = 0; i < saves.Count; i++) Assert.Equal(before[i], File.ReadAllBytes(Path.Combine(saves[i], SaveInfo.SavFileName)));
    }
}
