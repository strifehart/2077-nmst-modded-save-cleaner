namespace Nmst.SaveCleaner.Core.Saves;

public static class SaveScanner
{
    /// <summary>The detected saves folder (see <see cref="SavesLocator"/>).</summary>
    public static string DefaultSavesFolder => SavesLocator.Detect();

    /// <summary>
    /// Lists save folders that contain a sav.dat; the game ignores the others too (empty folders it leaves behind
    /// after deleting a save, or ones holding only a screenshot/metadata). A folder with a sav.dat but unreadable
    /// metadata is still returned, with <see cref="SaveInfo.Error"/> set.
    /// </summary>
    public static IReadOnlyList<SaveInfo> Scan(string savesFolder)
    {
        var result = new List<SaveInfo>();
        foreach (var dir in Directory.EnumerateDirectories(savesFolder))
        {
            if (!File.Exists(Path.Combine(dir, SaveInfo.SavFileName))) continue;
            result.Add(Read(dir, new DirectoryInfo(dir).GetFiles()));
        }
        return result;
    }

    public static SaveInfo Read(string dir) => Read(dir, new DirectoryInfo(dir).GetFiles());

    private static SaveInfo Read(string dir, FileInfo[] files)
    {
        long size = files.Sum(f => f.Length);
        var lastWrite = files.Max(f => f.LastWriteTime);
        var metaPath = Path.Combine(dir, SaveInfo.MetadataFile);
        if (!File.Exists(metaPath)) return new SaveInfo(dir, null, "No metadata.9.json", size, lastWrite);
        if (!File.Exists(Path.Combine(dir, SaveInfo.SavFileName))) return new SaveInfo(dir, null, "No sav.dat", size, lastWrite);
        try { return new SaveInfo(dir, SaveMetadata.Load(metaPath), null, size, lastWrite); }
        catch (Exception e) { return new SaveInfo(dir, null, "Unreadable metadata: " + e.Message, size, lastWrite); }
    }
}
