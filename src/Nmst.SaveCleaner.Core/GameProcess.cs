using System.Diagnostics;

namespace Nmst.SaveCleaner.Core;

public static class GameProcess
{
    /// <summary>The game caches its save list and may write saves at any time, so the app refuses to write while it runs.</summary>
    public static bool IsRunning()
    {
        var procs = Process.GetProcessesByName("Cyberpunk2077");
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }
}
