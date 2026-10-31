using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Nmst.SaveCleaner.Core.Saves;

/// <summary>
/// Finds the Cyberpunk 2077 saves folder. Windows: the real "Saved Games" location (it can be moved, e.g. to another
/// drive or OneDrive), which Steam, GOG and Epic all use. Linux/Steam Deck: inside the game's Proton prefix in any
/// Steam library, including SD cards and Flatpak Steam.
/// </summary>
public static partial class SavesLocator
{
    private static readonly string SubPath = Path.Combine("CD Projekt Red", "Cyberpunk 2077");
    private const string SteamAppId = "1091500";
    private static readonly Guid SavedGamesFolderId = new("4C5C32FF-BB9D-43b0-B5B4-2D72E54EAAA4");   // FOLDERID_SavedGames

    /// <summary>The first candidate that holds saves; otherwise the first candidate (so the app can say it's empty).</summary>
    public static string Detect()
    {
        var all = Candidates().ToList();
        if (all.Count == 0)   // e.g. Linux without Steam: point at the usual place so the app can say "no saves found"
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games", SubPath);
        return all.FirstOrDefault(HasSaves) ?? all[0];
    }

    public static IEnumerable<string> Candidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            if (KnownSavedGames() is { } saved) yield return Path.Combine(saved, SubPath);
            yield return Path.Combine(home, "Saved Games", SubPath);
        }
        else
        {
            foreach (var c in ProtonCandidates(home)) yield return c;
        }
    }

    /// <summary>Where Proton keeps the game's saves, for every Steam library found under <paramref name="home"/>.</summary>
    public static IEnumerable<string> ProtonCandidates(string home)
    {
        string[] steamRoots =
        [
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".steam", "root"),
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam"),
        ];
        var libraries = new List<string>();
        foreach (var root in steamRoots.Where(Directory.Exists))
        {
            libraries.Add(root);
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            try { if (File.Exists(vdf)) libraries.AddRange(LibraryPaths(File.ReadAllText(vdf))); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lib in libraries)
        {
            string full;
            try { full = Path.GetFullPath(lib); } catch (Exception) { continue; }   // a garbled vdf entry
            if (!seen.Add(full)) continue;
            yield return Path.Combine(full, "steamapps", "compatdata", SteamAppId, "pfx", "drive_c", "users", "steamuser", "Saved Games", SubPath);
        }
    }

    /// <summary>The "path" entries of Steam's libraryfolders.vdf (one per library: internal drive, SD card…).</summary>
    public static IEnumerable<string> LibraryPaths(string vdf) =>
        VdfPath().Matches(vdf).Select(m => m.Groups[1].Value.Replace(@"\\", @"\"));

    public static bool HasSaves(string folder)
    {
        try { return Directory.Exists(folder) && Directory.EnumerateDirectories(folder).Any(d => File.Exists(Path.Combine(d, SaveInfo.SavFileName))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string? KnownSavedGames()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            if (SHGetKnownFolderPath(SavedGamesFolderId, 0, IntPtr.Zero, out var p) != 0) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    [GeneratedRegex(@"""path""\s+""([^""]+)""")]
    private static partial Regex VdfPath();
}
