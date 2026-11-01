using System.Text.Json;

namespace Nmst.SaveCleaner.Core.Backups;

public enum BatchKind { Clean, Restore }

/// <summary>
/// One save touched by a batch: the backup the batch made of it first (what an undo puts back), plus the size and
/// modified time of its files right after the batch, to notice when the game wrote to it since.
/// </summary>
public sealed record BatchSave(string SaveFolderName, string BackupId, long SavLength, DateTime SavWrittenUtc, long MetadataLength, DateTime MetadataWrittenUtc);

/// <summary>A clean or restore run over one or more saves (a single save counts as a batch of one).</summary>
public sealed record BatchRecord(string Id, BatchKind Kind, DateTime TimeUtc, string SavesFolder, IReadOnlyList<BatchSave> Saves);

/// <summary>
/// The recent batches, newest last, in <c>batches.json</c> next to the backups. It only points at backups, so losing
/// it loses nothing but the "Undo last batch" shortcut. Reading never throws.
/// </summary>
public sealed class BatchLog(string path)
{
    private const int Keep = 20;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string NewId() => DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..6];

    public IReadOnlyList<BatchRecord> All()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<List<BatchRecord>>(File.ReadAllText(path)) ?? [] : []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { return []; }
    }

    /// <summary>The most recent batch run on this saves folder.</summary>
    public BatchRecord? Last(string savesFolder) =>
        All().LastOrDefault(b => string.Equals(Norm(b.SavesFolder), Norm(savesFolder), StringComparison.OrdinalIgnoreCase));

    public void Add(BatchRecord record)
    {
        var list = All().Append(record).TakeLast(Keep).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, Json));
        File.Move(tmp, path, true);
    }

    /// <summary>Size + modified time of a save's two data files, as a batch leaves them.</summary>
    public static BatchSave Snapshot(string saveFolder, string backupId)
    {
        var sav = new FileInfo(Path.Combine(saveFolder, Saves.SaveInfo.SavFileName));
        var meta = new FileInfo(Path.Combine(saveFolder, Saves.SaveInfo.MetadataFile));
        return new BatchSave(Path.GetFileName(saveFolder), backupId,
            sav.Exists ? sav.Length : -1, sav.Exists ? sav.LastWriteTimeUtc : default,
            meta.Exists ? meta.Length : -1, meta.Exists ? meta.LastWriteTimeUtc : default);
    }

    /// <summary>True when the save no longer looks the way the batch left it (the game saved over it, or it's gone).</summary>
    public static bool ChangedSince(BatchSave s, string savesFolder) => Snapshot(Path.Combine(savesFolder, s.SaveFolderName), s.BackupId) != s;

    private static string Norm(string p) { try { return Path.GetFullPath(p).TrimEnd('\\', '/'); } catch { return p; } }
}
