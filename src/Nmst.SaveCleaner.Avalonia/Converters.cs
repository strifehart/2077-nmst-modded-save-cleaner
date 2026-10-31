using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Nmst.SaveCleaner.Core.Logging;
using Nmst.SaveCleaner.Core.Saves;

namespace Nmst.SaveCleaner.Avalonia;

/// <summary>ModdedState → the dot/badge colour.</summary>
public sealed class StateBrushConverter : IValueConverter
{
    public static readonly IBrush Tagged = new SolidColorBrush(Color.Parse("#ff3b6b"));
    public static readonly IBrush Sticky = new SolidColorBrush(Color.Parse("#ffb020"));
    public static readonly IBrush Clean = new SolidColorBrush(Color.Parse("#2cf5b0"));
    public static readonly IBrush Unknown = new SolidColorBrush(Color.Parse("#4a4258"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ModdedState.Tagged => Tagged,
        ModdedState.StickyOnly => Sticky,
        ModdedState.Clean => Clean,
        _ => Unknown,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Backup card summary: pink when the backup differs from the current save, muted when it's identical.</summary>
public sealed class DiffBrushConverter : IValueConverter
{
    private static readonly IBrush Differs = new SolidColorBrush(Color.Parse("#ffdaec"));
    private static readonly IBrush Same = new SolidColorBrush(Color.Parse("#6c6380"));
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? Differs : Same;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>true → 1, false → 0; paired with an opacity transition for fades.</summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? 1.0 : 0.0;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Log level → the faint tint behind its badge.</summary>
public sealed class LogLevelBadgeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => new SolidColorBrush(Color.Parse(value switch
    {
        LogLevel.Ok => "#1f2cf5b0",
        LogLevel.Done => "#2200e5ff",
        LogLevel.Warn => "#26ffb020",
        LogLevel.Error => "#33ff3b6b",
        _ => "#14ffffff",
    }));
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Log level → badge text ("OK", "DONE"…).</summary>
public sealed class LogLevelTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value?.ToString()?.ToUpperInvariant() ?? "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class LogLevelBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LogLevel.Ok => StateBrushConverter.Clean,
        LogLevel.Skip => new SolidColorBrush(Color.Parse("#6c6380")),
        LogLevel.Warn => StateBrushConverter.Sticky,
        LogLevel.Error => StateBrushConverter.Tagged,
        LogLevel.Done => new SolidColorBrush(Color.Parse("#00e5ff")),
        _ => new SolidColorBrush(Color.Parse("#9b90ad")),
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>File path → downscaled bitmap, with a small cache so flicking through saves stays smooth.</summary>
public sealed class PathToBitmapConverter : IValueConverter
{
    private static readonly Dictionary<string, Bitmap> Cache = [];
    private static readonly Queue<string> Order = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || !File.Exists(path)) return null;
        if (Cache.TryGetValue(path, out var hit)) return hit;
        try
        {
            using var s = File.OpenRead(path);
            var bmp = Bitmap.DecodeToWidth(s, 640);
            Cache[path] = bmp;
            Order.Enqueue(path);
            if (Order.Count > 24) Cache.Remove(Order.Dequeue());
            return bmp;
        }
        catch { return null; }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
