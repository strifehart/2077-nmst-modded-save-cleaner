using System.Text.RegularExpressions;
using Nmst.SaveCleaner.Core.Backups;
using Nmst.SaveCleaner.Core.Logging;

namespace Nmst.SaveCleaner.Core.Saves;

public enum CleanOutcome { Cleaned, Skipped, Failed }

public sealed record CleanResult(string SaveName, CleanOutcome Outcome, string Message);

/// <summary>
/// Clears the modded tag in place: always backs the save up first, writes through temp files, re-reads the result
/// and, if the check fails, puts the backup back.
/// </summary>
public sealed partial class TagCleaner(BackupManager backups, ActivityLog log)
{
    [GeneratedRegex(@"""isModded""\s*:\s*true")]
    private static partial Regex IsModdedTrue();

    public CleanResult Clean(string saveFolder)
    {
        var name = Path.GetFileName(saveFolder);
        try
        {
            var save = SaveScanner.Read(saveFolder);
            if (save.Metadata is null) return Fail(name, save.Error ?? "unreadable save", null);

            var sav = SavFile.Load(save.SavPath);
            bool? sticky = ModdedFlag.ReadSticky(sav);
            bool json = save.Metadata.IsModded;
            if (!json && sticky != true)
            {
                log.Skip($"{name}: already clean");
                return new CleanResult(name, CleanOutcome.Skipped, "already clean");
            }

            var backup = backups.Create(save, "Before clean");
            try
            {
                if (sticky == true) WriteSav(save, sav);
                if (json) WriteMetadata(save);

                var after = SaveScanner.Read(saveFolder);
                bool? stickyAfter = ModdedFlag.ReadSticky(after.SavPath);
                if (after.Metadata is null || after.Metadata.IsModded || stickyAfter == true)
                    throw new InvalidDataException("the save still reads as modded after writing");
            }
            catch (Exception e)
            {
                BackupManager.TryDeleteFile(save.SavPath + ".tmp");
                BackupManager.TryDeleteFile(save.MetadataPath + ".tmp");
                try { backups.Restore(backup, Path.GetDirectoryName(saveFolder)!, backupCurrent: false); }
                catch (Exception r) { return Fail(name, e.Message, $"putting the backup back also failed ({r.Message}); it's still in {backup.FolderPath}"); }
                return Fail(name, e.Message, "the backup was put back, the save is unchanged");
            }

            log.Ok($"{name}: {(sticky == true ? "ModdingSystem 01 → 00" : "no sticky flag")}{(json ? ", isModded true → false" : "")}, verified");
            return new CleanResult(name, CleanOutcome.Cleaned, "cleaned");
        }
        catch (Exception e)
        {
            return Fail(name, e.Message, "nothing was changed");
        }
    }

    private static void WriteSav(SaveInfo save, SavFile sav)
    {
        var node = sav.FindNode(SavFile.ModdingSystemNode)!;
        int flagPos = node.Offset + ModdedFlag.FlagOffsetInNode;
        var bytes = sav.WithByte(flagPos, 0);

        // the rewritten file must decompress to exactly the original data with only the flag byte changed
        // (the header holds the chunk table, whose sizes/offsets legitimately move, so compare from DataStart)
        var expected = sav.ReadAllLogical();
        expected[flagPos] = 0;
        var check = SavFile.Parse(bytes);
        if (check.DataStart != sav.DataStart || !check.ReadAllLogical().AsSpan(sav.DataStart).SequenceEqual(expected.AsSpan(sav.DataStart)))
            throw new InvalidDataException("rewritten sav.dat differs beyond the flag byte");
        if (check.Nodes.Count != sav.Nodes.Count) throw new InvalidDataException("rewritten sav.dat has a different node table");

        var tmp = save.SavPath + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, save.SavPath, true);
    }

    private static void WriteMetadata(SaveInfo save)
    {
        var text = File.ReadAllText(save.MetadataPath);
        var updated = IsModdedTrue().Replace(text, @"""isModded"": false", 1);
        if (SaveMetadata.Parse(updated).IsModded) throw new InvalidDataException("couldn't clear isModded in metadata.9.json");
        var tmp = save.MetadataPath + ".tmp";
        File.WriteAllText(tmp, updated);
        File.Move(tmp, save.MetadataPath, true);
    }

    private CleanResult Fail(string name, string reason, string? note)
    {
        log.Error($"{name}: {reason}{(note is null ? "" : " · " + note)}");
        return new CleanResult(name, CleanOutcome.Failed, reason);
    }
}
