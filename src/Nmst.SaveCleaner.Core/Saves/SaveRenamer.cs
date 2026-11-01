using System.Text.RegularExpressions;
using Nmst.SaveCleaner.Core.Backups;
using Nmst.SaveCleaner.Core.Logging;

namespace Nmst.SaveCleaner.Core.Saves;

/// <summary>
/// Renames a save by renaming its folder: that's the name the game's Load menu shows. Nothing inside the save
/// changes (metadata.9.json keeps the original name, which is how the app can always reset it), and the folder's own
/// dates and the saves folder's modified date are put back afterwards. The save's backups and the batch log follow
/// the new name.
/// </summary>
public static partial class SaveRenamer
{
    public const int MaxLength = 40;

    [GeneratedRegex(@"^(AutoSave|QuickSave|ManualSave|PointOfNoReturnSave|EndGameSave)-\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex GamePattern();

    private static readonly string[] Reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>
    /// Why <paramref name="newName"/> can't be used for <paramref name="save"/>, or null when it can. A name in the
    /// game's own pattern (AutoSave-3, ManualSave-2000…) is refused, since the game reuses or numbers those itself;
    /// the save's own original name is the one exception (resetting it).
    /// </summary>
    public static string? Validate(SaveInfo save, string newName)
    {
        var name = newName.Trim();
        if (name.Length == 0) return "Enter a name";
        if (name.Length > MaxLength) return $"Keep it to {MaxLength} characters";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            return "Names can't contain \\ / : * ? \" < > |";
        if (name.EndsWith('.')) return "Names can't end with a dot";
        if (Reserved.Contains(name.Split('.')[0], StringComparer.OrdinalIgnoreCase)) return $"\"{name}\" is reserved by Windows";
        if (name == save.FolderName) return "That's already its name";
        bool isOriginal = string.Equals(name, save.Metadata?.Name, StringComparison.OrdinalIgnoreCase);
        if (GamePattern().IsMatch(name) && !isOriginal) return "The game uses names like that itself; pick something else";
        var target = Path.Combine(Path.GetDirectoryName(save.FolderPath)!, name);
        bool caseOnly = string.Equals(name, save.FolderName, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (Directory.Exists(target) || File.Exists(target))) return "Another save already has that name";
        return null;
    }

    /// <summary>Renames the save; returns its new folder path. Throws when the name is invalid or the move fails.</summary>
    public static string Rename(SaveInfo save, string newName, BackupManager backups, BatchLog batches, ActivityLog log)
    {
        if (Validate(save, newName) is { } why) throw new ArgumentException(why);
        var name = newName.Trim();
        var savesFolder = Path.GetDirectoryName(save.FolderPath)!;
        var target = Path.Combine(savesFolder, name);

        var folderDates = FileDates.Of(save.FolderPath);
        var parentDates = FileDates.Of(savesFolder);
        if (string.Equals(name, save.FolderName, StringComparison.OrdinalIgnoreCase))
        {
            // a change of case only: Windows won't move a folder onto itself, so go through a temporary name
            var temp = Path.Combine(savesFolder, save.FolderName + ".renaming");
            MoveWithRetry(save.FolderPath, temp);
            MoveWithRetry(temp, target);
        }
        else MoveWithRetry(save.FolderPath, target);
        folderDates.ApplyTo(target);
        parentDates.ApplyTo(savesFolder);

        backups.MoveSave(save.FolderName, name);
        try { batches.RenameSave(savesFolder, save.FolderName, name); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Warn($"Couldn't update the undo record for the new name · {e.Message}"); }
        log.Ok($"{save.FolderName}: renamed to {name}");
        return target;
    }

    /// <summary>
    /// Windows refuses to rename a folder while any file in it is open, even for a moment (the app's own background
    /// check, antivirus, the search indexer). Retry briefly before giving up.
    /// </summary>
    private static void MoveWithRetry(string from, string to)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { Directory.Move(from, to); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 20 && Directory.Exists(from) && !Directory.Exists(to))
            {
                Thread.Sleep(100);
            }
        }
    }
}
