namespace Nmst.SaveCleaner.Core.Saves;

/// <summary>
/// A file's or folder's created and modified dates (UTC). Writes go through temp files and copies, which would
/// otherwise stamp everything with "now"; capturing these first and applying them afterwards keeps a save looking
/// exactly as it did in Explorer and to sync tools. Best effort: a platform that can't set a date doesn't fail the
/// operation.
/// </summary>
public readonly record struct FileDates(DateTime CreatedUtc, DateTime WrittenUtc)
{
    public static FileDates Of(string path) => Directory.Exists(path)
        ? new(Directory.GetCreationTimeUtc(path), Directory.GetLastWriteTimeUtc(path))
        : new(File.GetCreationTimeUtc(path), File.GetLastWriteTimeUtc(path));

    public void ApplyTo(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.SetCreationTimeUtc(path, CreatedUtc);
                Directory.SetLastWriteTimeUtc(path, WrittenUtc);
            }
            else
            {
                File.SetCreationTimeUtc(path, CreatedUtc);
                File.SetLastWriteTimeUtc(path, WrittenUtc);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
    }

    /// <summary>Copies the dates of <paramref name="from"/> onto <paramref name="to"/>.</summary>
    public static void Copy(string from, string to) => Of(from).ApplyTo(to);
}
