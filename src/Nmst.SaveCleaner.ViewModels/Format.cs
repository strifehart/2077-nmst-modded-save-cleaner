using System.Globalization;
using Nmst.SaveCleaner.Core.Saves;

namespace Nmst.SaveCleaner.ViewModels;

public static class Format
{
    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m";

    /// <summary>Seconds → "1:23".</summary>
    public static string Clock(double seconds)
    {
        int s = (int)Math.Max(0, seconds);
        return $"{s / 60}:{s % 60:00}";
    }

    public static string Date(DateTime d) => d.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    public static string DateTime(DateTime d) => d.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };

    public static string State(ModdedState s) => s switch
    {
        ModdedState.Tagged => "Modded",
        ModdedState.StickyOnly => "Sticky flag",
        ModdedState.Clean => "Clean",
        _ => "Checking…",
    };

    public static string StateDetail(bool metadataModded, bool? sticky) =>
        $"isModded {(metadataModded ? "true" : "false")} · ModdingSystem {(sticky is null ? "none" : sticky.Value ? "01 (sticky)" : "00")}";
}
