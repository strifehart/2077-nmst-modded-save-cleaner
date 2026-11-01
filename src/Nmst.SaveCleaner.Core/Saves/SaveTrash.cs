using System.Globalization;
using System.Text.Json;
using Nmst.SaveCleaner.Core.Logging;

namespace Nmst.SaveCleaner.Core.Saves;

/// <summary>A save in the trash, with where it came from.</summary>
public sealed record TrashItem(string FolderPath, string SaveName, string SavesFolder, DateTime Deleted, SaveMetadata? Metadata, long SizeBytes, FileDates? FolderDates)
{
    public string Id => Path.GetFileName(FolderPath);
}

/// <summary>
/// Deleted saves, moved out of the saves folder (so the game no longer sees them) into the app's own Trash folder:
/// <c>&lt;root&gt;\&lt;save name&gt;\&lt;yyyyMMdd-HHmmss-fff&gt;\</c> with the save's files plus trash.json. Restoring
/// moves them back with their dates. Nothing is ever removed from the trash except on an explicit request.
/// </summary>
public sealed class SaveTrash(string root, ActivityLog log)
{
    private const string InfoFile = "trash.json";
    private const string IdFormat = "yyyyMMdd-HHmmss-fff";

    private sealed record TrashJson(string SaveName, string SavesFolder, DateTime DeletedUtc, DateTime? FolderCreatedUtc, DateTime? FolderWrittenUtc);

    public string Root => root;

    /// <summary>Moves the save into the trash; its files keep their dates, the folder's and the saves folder's are recorded/kept.</summary>
    public TrashItem Delete(SaveInfo save)
    {
        var savesFolder = Path.GetDirectoryName(save.FolderPath)!;
        var parentDates = FileDates.Of(savesFolder);
        var folderDates = FileDates.Of(save.FolderPath);
        var deleted = DateTime.Now;
        var dir = Path.Combine(root, save.FolderName, deleted.ToString(IdFormat, CultureInfo.InvariantCulture));
        while (Directory.Exists(dir)) { deleted = deleted.AddMilliseconds(1); dir = Path.Combine(root, save.FolderName, deleted.ToString(IdFormat, CultureInfo.InvariantCulture)); }
        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);

        Move(save.FolderPath, dir);
        var info = new TrashJson(save.FolderName, savesFolder, deleted.ToUniversalTime(), folderDates.CreatedUtc, folderDates.WrittenUtc);
        File.WriteAllText(Path.Combine(dir, InfoFile), JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
        parentDates.ApplyTo(savesFolder);
        log.Ok($"{save.FolderName}: moved to the trash");
        return Read(dir)!;
    }

    /// <summary>
    /// Puts a save back into <paramref name="savesFolder"/>. If its name has been taken since (typically an auto/quick
    /// slot the game reused), it comes back as "&lt;name&gt; restored" instead of overwriting anything. Returns the
    /// folder it was restored to.
    /// </summary>
    public string Restore(TrashItem item, string savesFolder)
    {
        var name = FreeName(savesFolder, item.SaveName);
        var target = Path.Combine(savesFolder, name);
        var parentDates = FileDates.Of(savesFolder);
        File.Delete(Path.Combine(item.FolderPath, InfoFile));
        Move(item.FolderPath, target);
        item.FolderDates?.ApplyTo(target);
        parentDates.ApplyTo(savesFolder);
        TryDeleteEmpty(Path.GetDirectoryName(item.FolderPath)!);
        log.Ok(name == item.SaveName ? $"{name}: restored from the trash" : $"{item.SaveName}: restored from the trash as {name} (the name was taken)");
        return target;
    }

    /// <summary>Deletes a trashed save for good. Only ever on an explicit request.</summary>
    public void DeleteForever(TrashItem item)
    {
        Directory.Delete(item.FolderPath, true);
        TryDeleteEmpty(Path.GetDirectoryName(item.FolderPath)!);
        log.Info($"{item.SaveName}: permanently deleted from the trash");
    }

    /// <summary>Trashed saves, newest first. Never throws.</summary>
    public IReadOnlyList<TrashItem> List()
    {
        try
        {
            if (!Directory.Exists(root)) return [];
            return Directory.GetDirectories(root).SelectMany(Directory.GetDirectories)
                .Select(Read).OfType<TrashItem>().OrderByDescending(t => t.Deleted).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warn("Couldn't read the trash · " + e.Message);
            return [];
        }
    }

    private static TrashItem? Read(string dir)
    {
        try
        {
            var json = JsonSerializer.Deserialize<TrashJson>(File.ReadAllText(Path.Combine(dir, InfoFile)));
            if (json is null) return null;
            SaveMetadata? meta = null;
            try { meta = SaveMetadata.Load(Path.Combine(dir, SaveInfo.MetadataFile)); } catch { }
            long size = Directory.GetFiles(dir).Where(f => Path.GetFileName(f) != InfoFile).Sum(f => new FileInfo(f).Length);
            FileDates? dates = json.FolderCreatedUtc is { } c && json.FolderWrittenUtc is { } w ? new FileDates(c, w) : null;
            return new TrashItem(dir, json.SaveName, json.SavesFolder, json.DeletedUtc.ToLocalTime(), meta, size, dates);
        }
        catch { return null; }
    }

    private static string FreeName(string savesFolder, string name)
    {
        if (!Directory.Exists(Path.Combine(savesFolder, name))) return name;
        for (int n = 1; ; n++)
        {
            var candidate = n == 1 ? $"{name} restored" : $"{name} restored {n}";
            if (!Directory.Exists(Path.Combine(savesFolder, candidate))) return candidate;
        }
    }

    /// <summary>
    /// Moves a folder, keeping its files' dates. A move across drives (saves on one drive, the app's data on another)
    /// is done as copy, verify, then delete. Retries briefly while something has a file in it open.
    /// </summary>
    private static void Move(string from, string to)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (string.Equals(Path.GetPathRoot(Path.GetFullPath(from)), Path.GetPathRoot(Path.GetFullPath(to)), StringComparison.OrdinalIgnoreCase))
                    Directory.Move(from, to);
                else CopyThenDelete(from, to);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 20 && Directory.Exists(from) && !Directory.Exists(to))
            {
                Thread.Sleep(100);
            }
        }
    }

    private static void CopyThenDelete(string from, string to)
    {
        var partial = to + ".partial";
        Directory.CreateDirectory(partial);
        try
        {
            foreach (var f in Directory.GetFiles(from))
            {
                var dst = Path.Combine(partial, Path.GetFileName(f));
                File.Copy(f, dst);
                if (!Backups.BackupManager.FilesEqual(f, dst)) throw new IOException($"copy of {Path.GetFileName(f)} doesn't match");
                FileDates.Copy(f, dst);
            }
            Directory.Move(partial, to);
        }
        catch
        {
            try { Directory.Delete(partial, true); } catch { }
            throw;
        }
        Directory.Delete(from, true);
    }

    private static void TryDeleteEmpty(string dir)
    {
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
