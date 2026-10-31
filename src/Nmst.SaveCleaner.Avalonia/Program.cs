using Avalonia;

namespace Nmst.SaveCleaner.Avalonia;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // last resort: if the app does go down, leave a report in the log file
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write("crash", e.ExceptionObject as Exception);
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception e) { CrashLog.Write("crash at startup", e); throw; }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
