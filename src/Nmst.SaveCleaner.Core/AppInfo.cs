namespace Nmst.SaveCleaner.Core;

public static class AppInfo
{
    public const string Name = "NMST Modded Save Cleaner";
    public const string LongName = "No Modded Save Tag · Modded Save Cleaner";
    public const string Author = "strife//art";
    public const string SiteUrl = "https://strifehart.com/";
    public const string NexusProfileUrl = "https://www.nexusmods.com/profile/strifehart";
}

/// <summary>
/// Where the app keeps its own files: <c>%LOCALAPPDATA%\Cyberpunk 2077 - NMST Modded Save Cleaner\</c>
/// with Backups, Logs and settings.json.
/// </summary>
public static class AppPaths
{
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string DataRoot => Path.Combine(LocalAppData, "Cyberpunk 2077 - " + AppInfo.Name);
    public static string Backups => Path.Combine(DataRoot, "Backups");
    public static string LogFile => Path.Combine(DataRoot, "Logs", "ModdedSaveCleaner.log");
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");

    /// <summary>The folder used before the rename (development builds called the app "Save Browser").</summary>
    public static string LegacyRoot => Path.Combine(LocalAppData, "SaveBrowser");

    /// <summary>
    /// Moves backups and logs from the old SaveBrowser folder into <see cref="DataRoot"/> so earlier backups stay
    /// visible. Never overwrites: a backup that already exists in the new place is left in the old one.
    /// Returns a short description of what was moved, or null when there was nothing to do.
    /// </summary>
    public static string? MigrateLegacy() => MigrateLegacy(LegacyRoot, DataRoot);

    public static string? MigrateLegacy(string legacyRoot, string dataRoot)
    {
        if (!Directory.Exists(legacyRoot)) return null;
        int moved = 0, kept = 0;

        var oldBackups = Path.Combine(legacyRoot, "Backups");
        if (Directory.Exists(oldBackups))
        {
            foreach (var saveDir in Directory.GetDirectories(oldBackups))
            {
                var target = Path.Combine(dataRoot, "Backups", Path.GetFileName(saveDir));
                Directory.CreateDirectory(target);
                foreach (var b in Directory.GetDirectories(saveDir))
                {
                    var dst = Path.Combine(target, Path.GetFileName(b));
                    if (Directory.Exists(dst)) { kept++; continue; }
                    Directory.Move(b, dst);
                    moved++;
                }
            }
        }

        var oldLogs = Path.Combine(legacyRoot, "Logs");
        if (Directory.Exists(oldLogs))
        {
            var logDir = Directory.CreateDirectory(Path.Combine(dataRoot, "Logs")).FullName;
            foreach (var f in Directory.GetFiles(oldLogs))
            {
                var dst = Path.Combine(logDir, "old-" + Path.GetFileName(f));
                if (!File.Exists(dst)) File.Move(f, dst);
            }
        }

        DeleteEmptyDirs(legacyRoot);
        return moved == 0 && kept == 0 ? null
            : $"Moved {moved} backup{(moved == 1 ? "" : "s")} from the old SaveBrowser folder" + (kept > 0 ? $" ({kept} left there: already present)" : "");
    }

    private static void DeleteEmptyDirs(string dir)
    {
        foreach (var d in Directory.GetDirectories(dir)) DeleteEmptyDirs(d);
        if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir);
    }
}
