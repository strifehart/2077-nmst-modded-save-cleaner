using Nmst.SaveCleaner.Core;

namespace Nmst.SaveCleaner.Avalonia;

/// <summary>Appends exception details to the app's log file. Never throws.</summary>
internal static class CrashLog
{
    public static void Write(string what, Exception? e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.LogFile)!);
            File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:HH:mm:ss} ERROR {what} ({AppInfo.Name} {App.VersionLabel}): {e}{Environment.NewLine}");
        }
        catch (Exception) { /* nothing left to report to */ }
    }
}
