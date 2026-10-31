using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Nmst.SaveCleaner.Core;
using Nmst.SaveCleaner.Core.Saves;
using Nmst.SaveCleaner.ViewModels;

namespace Nmst.SaveCleaner.Avalonia;

public partial class App : Application
{
    /// <summary>"v0.1.0", from the project's &lt;Version&gt; (build metadata after '+' dropped).</summary>
    public static string VersionLabel { get; } = "v" + (typeof(App).Assembly
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?");

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string? migrated = null, migrateError = null;
            try { migrated = AppPaths.MigrateLegacy(); }
            catch (Exception e) { migrateError = e.Message; }

            var window = new MainWindow { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://NMSTSaveCleaner/Assets/Icon/app.ico"))) };
            var music = new MusicPlayer();
            var vm = new MainViewModel(new PlatformServices(window), SaveScanner.DefaultSavesFolder, AppPaths.Backups, AppPaths.LogFile, music, AppPaths.SettingsFile);
            if (migrated is not null) vm.Log.Info(migrated);
            if (migrateError is not null) vm.Log.Warn("Couldn't move backups from the old SaveBrowser folder: " + migrateError);
            window.DataContext = vm;
            desktop.MainWindow = window;

            // safety net: an unexpected error is logged and shown, instead of silently closing the app
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                e.Handled = true;
                vm.Log.Error($"Unexpected error: {e.Exception.GetType().Name}: {e.Exception.Message}");
                CrashLog.Write("handled UI exception", e.Exception);
                _ = window.ShowDialogAsync("Something went wrong",
                    $"{e.Exception.Message}\n\nNothing was changed by this error itself. Details are in the log; please include it if you report this.", "OK", null);
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                e.SetObserved();
                CrashLog.Write("unobserved task exception", e.Exception);
            };
            desktop.Exit += (_, _) => { vm.SaveSettingsNow(); music.Dispose(); };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
