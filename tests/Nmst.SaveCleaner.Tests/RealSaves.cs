using Nmst.SaveCleaner.Core.Saves;

namespace Nmst.SaveCleaner.Tests;

/// <summary>
/// Tests run against real saves, read-only. Anything that writes works on a copy in a temp folder.
/// Folder: SAVEBROWSER_TEST_SAVES, else the default CP2077 saves folder. Tests return early when it's missing.
/// </summary>
internal static class RealSaves
{
    public static string Folder => Environment.GetEnvironmentVariable("SAVEBROWSER_TEST_SAVES") ?? SaveScanner.DefaultSavesFolder;
    public static bool Available => Directory.Exists(Folder);

    public static string? Path(string name)
    {
        var p = System.IO.Path.Combine(Folder, name);
        return File.Exists(System.IO.Path.Combine(p, SaveInfo.SavFileName)) ? p : null;
    }

    /// <summary>First save matching the predicate (scans metadata + sticky flag).</summary>
    public static string? Find(Func<SaveMetadata, bool?, bool> pred)
    {
        if (!Available) return null;
        foreach (var s in SaveScanner.Scan(Folder))
        {
            if (s.Metadata is null) continue;
            bool? sticky;
            try { sticky = ModdedFlag.ReadSticky(s.SavPath); } catch { continue; }
            if (pred(s.Metadata, sticky)) return s.FolderPath;
        }
        return null;
    }
}

internal sealed class TempDir : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("savebrowser-test-").FullName;
    public string Saves => Directory.CreateDirectory(System.IO.Path.Combine(Root, "saves")).FullName;
    public string Backups => System.IO.Path.Combine(Root, "backups");

    public string CopySave(string source)
    {
        var dst = System.IO.Path.Combine(Saves, System.IO.Path.GetFileName(source));
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(source)) File.Copy(f, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(f)));
        return dst;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { }
    }
}
