using Nmst.SaveCleaner.Core.Backups;
using Nmst.SaveCleaner.Core.Localization;
using Nmst.SaveCleaner.Core.Logging;
using Nmst.SaveCleaner.Core.Playthroughs;
using Nmst.SaveCleaner.Core.Saves;

namespace Nmst.SaveCleaner.Tests;

public class CoreTests
{
    private static (BackupManager, TagCleaner, List<LogEntry>) Setup(TempDir t)
    {
        var entries = new List<LogEntry>();
        var log = new ActivityLog(null);
        log.EntryAdded += entries.Add;
        var backups = new BackupManager(t.Backups, log);
        return (backups, new TagCleaner(backups, log), entries);
    }

    [Fact]
    public void Every_real_save_parses_and_has_a_readable_sticky_flag()
    {
        if (!RealSaves.Available) return;
        var saves = SaveScanner.Scan(RealSaves.Folder);
        var readable = saves.Where(s => s.Metadata is not null).ToList();
        Assert.NotEmpty(readable);
        foreach (var s in readable)
        {
            var sav = SavFile.Load(s.SavPath);
            ModdedFlag.ReadSticky(sav); // null is fine: saves from before the flag existed have no ModdingSystem node
            Assert.False(string.IsNullOrEmpty(s.Metadata!.PlaythroughId), s.FolderName + " has no playthroughID");
        }
    }

    [Fact]
    public void Clean_tagged_save_backs_up_changes_only_the_flags_and_verifies()
    {
        var src = RealSaves.Find((m, sticky) => m.IsModded && sticky == true);
        if (src is null) return;
        using var t = new TempDir();
        var (backups, cleaner, _) = Setup(t);
        var save = t.CopySave(src);
        var before = SavFile.Load(Path.Combine(save, SaveInfo.SavFileName));
        var beforeData = before.ReadAllLogical();
        var flagPos = before.FindNode(SavFile.ModdingSystemNode)!.Offset + ModdedFlag.FlagOffsetInNode;
        var beforeMeta = File.ReadAllText(Path.Combine(save, SaveInfo.MetadataFile));

        var r = cleaner.Clean(save);

        Assert.True(r.Outcome == CleanOutcome.Cleaned, r.Message);
        var after = SavFile.Load(Path.Combine(save, SaveInfo.SavFileName));
        Assert.False(ModdedFlag.ReadSticky(after));
        beforeData[flagPos] = 0;
        Assert.Equal(beforeData[before.DataStart..], after.ReadAllLogical()[before.DataStart..]);
        Assert.Equal(before.Nodes, after.Nodes);
        var afterMeta = File.ReadAllText(Path.Combine(save, SaveInfo.MetadataFile));
        Assert.Equal(beforeMeta.Replace("\"isModded\": true", "\"isModded\": false"), afterMeta);
        Assert.False(SaveMetadata.Parse(afterMeta).IsModded);

        var b = Assert.Single(backups.List(Path.GetFileName(save)));
        Assert.Equal("Before clean", b.Reason);
        Assert.Equal(ModdedState.Tagged, b.State);
        foreach (var f in Directory.GetFiles(src))
            Assert.True(BackupManager.FilesEqual(f, Path.Combine(b.FolderPath, Path.GetFileName(f))));
    }

    [Fact]
    public void Clean_sticky_only_save_clears_the_sav_flag()
    {
        var src = RealSaves.Find((m, sticky) => !m.IsModded && sticky == true);
        if (src is null) return;
        using var t = new TempDir();
        var (_, cleaner, _) = Setup(t);
        var save = t.CopySave(src);
        var metaBefore = File.ReadAllBytes(Path.Combine(save, SaveInfo.MetadataFile));

        var r = cleaner.Clean(save);
        Assert.True(r.Outcome == CleanOutcome.Cleaned, r.Message);
        Assert.False(ModdedFlag.ReadSticky(Path.Combine(save, SaveInfo.SavFileName)));
        Assert.Equal(metaBefore, File.ReadAllBytes(Path.Combine(save, SaveInfo.MetadataFile)));
    }

    [Fact]
    public void Clean_skips_a_clean_save_without_backing_it_up()
    {
        var src = RealSaves.Find((m, sticky) => !m.IsModded && sticky == false);
        if (src is null) return;
        using var t = new TempDir();
        var (backups, cleaner, log) = Setup(t);
        var save = t.CopySave(src);

        Assert.Equal(CleanOutcome.Skipped, cleaner.Clean(save).Outcome);
        Assert.Empty(backups.List(Path.GetFileName(save)));
        Assert.Contains(log, e => e.Level == LogLevel.Skip);
        foreach (var f in Directory.GetFiles(src))
            Assert.True(BackupManager.FilesEqual(f, Path.Combine(save, Path.GetFileName(f))));
    }

    [Fact]
    public void Corrupt_save_fails_without_changes()
    {
        var src = RealSaves.Find((m, sticky) => m.IsModded && sticky == true);
        if (src is null) return;
        using var t = new TempDir();
        var (backups, cleaner, _) = Setup(t);
        var save = t.CopySave(src);
        var sav = Path.Combine(save, SaveInfo.SavFileName);
        var bytes = File.ReadAllBytes(sav);
        File.WriteAllBytes(sav, bytes[..(bytes.Length / 2)]);
        var corrupt = File.ReadAllBytes(sav);

        Assert.Equal(CleanOutcome.Failed, cleaner.Clean(save).Outcome);
        Assert.Equal(corrupt, File.ReadAllBytes(sav));
        Assert.Empty(backups.List(Path.GetFileName(save)));
    }

    [Fact]
    public void Restore_puts_the_original_back_and_backs_up_the_current_state_first()
    {
        var src = RealSaves.Find((m, sticky) => m.IsModded && sticky == true);
        if (src is null) return;
        using var t = new TempDir();
        var (backups, cleaner, _) = Setup(t);
        var save = t.CopySave(src);
        cleaner.Clean(save);
        var name = Path.GetFileName(save);
        var beforeClean = backups.List(name).Single();

        backups.Restore(beforeClean, t.Saves);

        foreach (var f in Directory.GetFiles(src))
            Assert.True(BackupManager.FilesEqual(f, Path.Combine(save, Path.GetFileName(f))), Path.GetFileName(f));
        var all = backups.List(name);
        Assert.Equal(2, all.Count);
        Assert.Equal("Before restore", all[0].Reason);
        Assert.Equal(ModdedState.Clean, all[0].State);
    }

    [Fact]
    public void Restore_recreates_a_deleted_save()
    {
        var src = RealSaves.Find((m, sticky) => !m.IsModded && sticky == false);
        if (src is null) return;
        using var t = new TempDir();
        var (backups, _, _) = Setup(t);
        var save = t.CopySave(src);
        var b = backups.Create(SaveScanner.Read(save), "Manual");
        Directory.Delete(save, true);

        backups.Restore(b, t.Saves);

        foreach (var f in Directory.GetFiles(src))
            Assert.True(BackupManager.FilesEqual(f, Path.Combine(save, Path.GetFileName(f))));
        Assert.Single(backups.List(Path.GetFileName(save)));
    }

    [Fact]
    public void Names_resolve_quests_objectives_and_locations()
    {
        var n = GameNames.LoadEmbedded();
        Assert.Equal("Chippin' In", n.Text("LocKey#12380"));
        Assert.Equal("Northside", n.Text("LocKey#10964"));
        Assert.Equal("Dog Eat Dog", n.Text("LocKey#81863"));
        Assert.Equal("Wait a day for Rogue to call.", n.Objective("quests/side_quest/sq031_rogue/02_afterlife/00_wait_for_rogue_call"));
        Assert.Null(n.Objective("[nothing]"));
        Assert.Null(n.Text(""));
    }

    [Fact]
    public void Grouping_matches_the_game_menu()
    {
        var a = RealSaves.Path("ManualSave-1186");
        if (a is null) return;
        var saves = SaveScanner.Scan(RealSaves.Folder);
        var group = Playthrough.Group(saves).Single(g => g.Saves.Any(s => s.FolderName == "ManualSave-1186"));
        Assert.Equal(["ManualSave-1188", "ManualSave-1187", "ManualSave-1186"], group.Saves.Select(s => s.FolderName));
        Assert.Equal("Corpo", Playthrough.LifePathLabel(group.LifePath));
        Assert.Equal(1, group.MaxLevel);
        Assert.Equal(18, (int)group.MaxPlayTime.TotalMinutes);
    }

    [Theory]
    [InlineData(2310, "", "2.31")]
    [InlineData(2500, "", "2.5")]
    [InlineData(1060, "", "1.06")]
    [InlineData(2310, "2.31", "2.31")]
    public void Patch_label(int gameVersion, string buildPatch, string expected) =>
        Assert.Equal(expected, new SaveMetadata { GameVersion = gameVersion, BuildPatch = buildPatch }.Patch);
}
