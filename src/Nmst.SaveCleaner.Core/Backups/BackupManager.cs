using System.Globalization;
using System.Text.Json;
using Nmst.SaveCleaner.Core.Logging;
using Nmst.SaveCleaner.Core.Saves;

namespace Nmst.SaveCleaner.Core.Backups;

public sealed record BackupInfo(string FolderPath, string SaveFolderName, DateTime Created, string Reason, SaveMetadata? Metadata, bool? StickyFlag, long SizeBytes,
    string SourceFolder = "")
{
    /// <summary>
    /// True when the backup was taken from a save with the same name in a different saves folder (backups are filed
    /// by folder name, so switching saves folders can show another folder's backups).
    /// </summary>
    public bool IsFromOtherFolder(string currentSaveFolder) =>
        SourceFolder.Length > 0 && !string.Equals(Normalise(SourceFolder), Normalise(currentSaveFolder),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Normalise(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>The save folder's own dates when the backup was taken; null for backups made before these were kept.</summary>
    public FileDates? FolderDates { get; init; }

    /// <summary>The batch operation (clean / restore) that made this backup; null for manual backups and old ones.</summary>
    public string? BatchId { get; init; }

    /// <summary>Set when Create found an identical backup and returned it instead of making a new copy.</summary>
    public bool WasExisting { get; init; }

    public string Id => Path.GetFileName(FolderPath);
    public ModdedState State => Metadata is null ? ModdedState.Unknown : SaveInfo.Combine(Metadata.IsModded, StickyFlag);
}

/// <summary>
/// Full copies of save folders, kept outside the game's saves folder:
/// <c>&lt;root&gt;\&lt;save folder name&gt;\&lt;yyyyMMdd-HHmmss-fff&gt;\</c> with the save's files plus backup.json.
/// Backups are never deleted automatically.
/// </summary>
public sealed class BackupManager(string root, ActivityLog log)
{
    private const string InfoFile = "backup.json";
    private const string IdFormat = "yyyyMMdd-HHmmss-fff";

    public string Root => root;

    /// <summary>FolderCreatedUtc/FolderWrittenUtc are the save folder's own dates (absent in backups from before 1.0.1).</summary>
    private sealed record BackupJson(string Reason, DateTime CreatedUtc, string Source, string App,
        DateTime? FolderCreatedUtc = null, DateTime? FolderWrittenUtc = null, string? BatchId = null);

    /// <summary>
    /// Copies every file of the save into a new backup and verifies the copy byte for byte. Each copy keeps the
    /// original's created/modified dates, and the save folder's dates go into backup.json, so a restore can put the
    /// save back exactly as it was. If this save already has a backup with exactly the same files (same bytes and
    /// modified dates, taken from this same folder), no new copy is made and that backup is returned instead, with
    /// <see cref="BackupInfo.WasExisting"/> set.
    /// </summary>
    public BackupInfo Create(SaveInfo save, string reason, string? batchId = null)
    {
        if (FindIdentical(save) is { } existing)
        {
            log.Skip($"{save.FolderName}: already backed up exactly like this ({existing.Reason.ToLowerInvariant()} {existing.Id}), no new copy");
            return existing with { WasExisting = true };
        }

        var created = DateTime.Now;
        var dir = Path.Combine(root, save.FolderName, created.ToString(IdFormat, CultureInfo.InvariantCulture));
        while (Directory.Exists(dir)) { created = created.AddMilliseconds(1); dir = Path.Combine(root, save.FolderName, created.ToString(IdFormat, CultureInfo.InvariantCulture)); }
        var partial = dir + ".partial";
        if (Directory.Exists(partial)) Directory.Delete(partial, true);
        Directory.CreateDirectory(partial);
        try
        {
            foreach (var src in Directory.GetFiles(save.FolderPath))
            {
                var dst = Path.Combine(partial, Path.GetFileName(src));
                File.Copy(src, dst);
                if (!FilesEqual(src, dst)) throw new IOException($"backup copy of {Path.GetFileName(src)} doesn't match the original");
                FileDates.Copy(src, dst);
            }
            var folder = FileDates.Of(save.FolderPath);
            var info = new BackupJson(reason, created.ToUniversalTime(), save.FolderPath, AppInfo.Name, folder.CreatedUtc, folder.WrittenUtc, batchId);
            File.WriteAllText(Path.Combine(partial, InfoFile), JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
            Directory.Move(partial, dir);
        }
        catch
        {
            TryDeleteDir(partial);   // don't leave half a backup lying around
            throw;
        }

        var b = Read(dir, save.FolderName)!;
        log.Ok($"{save.FolderName}: backed up ({reason.ToLowerInvariant()}) → {b.Id}");
        return b;
    }

    /// <summary>Backups of one save, newest first.</summary>
    public IReadOnlyList<BackupInfo> List(string saveFolderName)
    {
        try
        {
            var dir = Path.Combine(root, saveFolderName);
            if (!Directory.Exists(dir)) return [];
            return Directory.GetDirectories(dir)
                .Where(d => !d.EndsWith(".partial", StringComparison.Ordinal))
                .Select(d => Read(d, saveFolderName))
                .OfType<BackupInfo>()
                .OrderByDescending(b => b.Created)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warn($"{saveFolderName}: couldn't read its backups · {e.Message}");
            return [];
        }
    }

    /// <summary>Number of backups per save folder name (cheap: no files are parsed).</summary>
    public IReadOnlyDictionary<string, int> Counts()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(root)) return counts;
            foreach (var d in Directory.GetDirectories(root))
            {
                try { counts[Path.GetFileName(d)] = Directory.GetDirectories(d).Count(x => !x.EndsWith(".partial", StringComparison.Ordinal)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Warn("Couldn't read the backups folder · " + e.Message); }
        return counts;
    }

    public long TotalSize() => Directory.Exists(root)
        ? new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;

    /// <summary>
    /// Puts a backup back into the saves folder. The current save (if it still exists) is backed up first, so a
    /// restore can always be undone; <paramref name="backupCurrent"/> is false only for the cleaner's own rollback,
    /// where the "current" save is the half-written one (and backing it up could fail on a full disk). A save that was
    /// deleted is recreated. Every file is copied and verified next to its target first and only then swapped in, so
    /// a failure never leaves a mix of old and restored files. Returns the "Before restore" backup it made, if any.
    /// </summary>
    public BackupInfo? Restore(BackupInfo backup, string savesFolder, bool backupCurrent = true, string? batchId = null)
    {
        var target = Path.Combine(savesFolder, backup.SaveFolderName);
        BackupInfo? before = null;
        if (backupCurrent && Directory.Exists(target) && Directory.GetFiles(target).Length > 0)
            before = Create(SaveScanner.Read(target), "Before restore", batchId);
        Directory.CreateDirectory(target);

        var staged = new List<(string Tmp, string Dst)>();
        try
        {
            foreach (var src in Directory.GetFiles(backup.FolderPath).Where(f => Path.GetFileName(f) != InfoFile))
            {
                var dst = Path.Combine(target, Path.GetFileName(src));
                var tmp = dst + ".restoring";
                staged.Add((tmp, dst));
                File.Copy(src, tmp, true);
                if (!FilesEqual(src, tmp)) throw new IOException($"copy of {Path.GetFileName(src)} doesn't match the backup");
            }
        }
        catch
        {
            foreach (var (tmp, _) in staged) TryDeleteFile(tmp);
            throw;
        }
        foreach (var (tmp, dst) in staged)
        {
            File.Move(tmp, dst, true);
            FileDates.Copy(Path.Combine(backup.FolderPath, Path.GetFileName(dst)), dst);
        }
        if (backup.FolderDates is { } folderDates) folderDates.ApplyTo(target);
        log.Ok($"{backup.SaveFolderName}: restored from {backup.Id} ({backup.Reason.ToLowerInvariant()})");
        return before;
    }

    /// <summary>
    /// A backup of this save (from this saves folder) whose files match the save's byte for byte with the same
    /// modified dates. Checks sizes first, so a save that changed is ruled out without reading it.
    /// </summary>
    private BackupInfo? FindIdentical(SaveInfo save)
    {
        try
        {
            var dir = Path.Combine(root, save.FolderName);
            if (!Directory.Exists(dir)) return null;
            var current = Directory.GetFiles(save.FolderPath);
            foreach (var b in Directory.GetDirectories(dir).Where(d => !d.EndsWith(".partial", StringComparison.Ordinal)).OrderDescending())
            {
                var files = Directory.GetFiles(b).Where(f => Path.GetFileName(f) != InfoFile).ToList();
                if (files.Count != current.Length) continue;
                bool same = current.All(c =>
                {
                    var f = new FileInfo(Path.Combine(b, Path.GetFileName(c)));
                    var s = new FileInfo(c);
                    return f.Exists && f.Length == s.Length && f.LastWriteTimeUtc == s.LastWriteTimeUtc;
                });
                if (!same || !current.All(c => FilesEqual(c, Path.Combine(b, Path.GetFileName(c))))) continue;
                if (Read(b, save.FolderName) is { } info && !info.IsFromOtherFolder(save.FolderPath)) return info;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>True when the save folder holds exactly the backup's files, byte for byte (restoring would change nothing).</summary>
    public static bool IsSameAs(BackupInfo backup, string saveFolder)
    {
        try
        {
            if (!Directory.Exists(saveFolder)) return false;
            var mine = Directory.GetFiles(backup.FolderPath).Where(f => Path.GetFileName(f) != InfoFile).ToList();
            var theirs = Directory.GetFiles(saveFolder);
            if (mine.Count != theirs.Length) return false;
            return mine.All(f => File.Exists(Path.Combine(saveFolder, Path.GetFileName(f))) && FilesEqual(f, Path.Combine(saveFolder, Path.GetFileName(f))));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Deletes one backup. Only ever called on an explicit user request.</summary>
    public void Delete(BackupInfo backup)
    {
        Directory.Delete(backup.FolderPath, true);
        log.Info($"{backup.SaveFolderName}: deleted backup {backup.Id}");
    }

    internal static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { } }
    private static void TryDeleteDir(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (Exception) { } }

    private static BackupInfo? Read(string dir, string saveFolderName)
    {
        try
        {
            var json = JsonSerializer.Deserialize<BackupJson>(File.ReadAllText(Path.Combine(dir, InfoFile)));
            if (json is null) return null;
            SaveMetadata? meta = null; bool? sticky = null;
            try { meta = SaveMetadata.Load(Path.Combine(dir, SaveInfo.MetadataFile)); } catch { }
            try { sticky = ModdedFlag.ReadSticky(Path.Combine(dir, SaveInfo.SavFileName)); } catch { }
            long size = Directory.GetFiles(dir).Where(f => Path.GetFileName(f) != InfoFile).Sum(f => new FileInfo(f).Length);
            FileDates? folderDates = json.FolderCreatedUtc is { } c && json.FolderWrittenUtc is { } w ? new FileDates(c, w) : null;
            return new BackupInfo(dir, saveFolderName, json.CreatedUtc.ToLocalTime(), json.Reason, meta, sticky, size, json.Source ?? "")
                { FolderDates = folderDates, BatchId = json.BatchId };
        }
        catch { return null; }
    }

    internal static bool FilesEqual(string a, string b)
    {
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }
}
