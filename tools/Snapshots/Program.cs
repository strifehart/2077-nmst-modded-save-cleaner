// Usage: Snapshots <saves folder> <output folder>
// Reads saves only. Backups made for the screenshots go into a temp folder that's deleted afterwards.
using Avalonia;
using Avalonia.Headless;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Nmst.SaveCleaner.Avalonia;
using Nmst.SaveCleaner.Core.Backups;
using Nmst.SaveCleaner.Core.Logging;
using Nmst.SaveCleaner.Core.Saves;
using Nmst.SaveCleaner.ViewModels;

var saves = args.Length > 0 ? args[0] : SaveScanner.DefaultSavesFolder;
var outDir = Directory.CreateDirectory(args.Length > 1 ? args[1] : "snapshots").FullName;
var backupRoot = Directory.CreateTempSubdirectory("savebrowser-snap-").FullName;

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();
SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());

try
{
    MainWindow.ForceCustomChrome = true;
    var w = new MainWindow { Width = 1440, Height = 900 };
    var vm = new MainViewModel(new PlatformServices(w), saves, backupRoot, null, new StillPlayer());
    w.DataContext = vm;
    w.Show();
    w.UpdateChromeForSnapshot();
    vm.StartMusic();
    w.RenderIntroAt(0.5); Shot("0a_intro_pink");
    w.RenderIntroAt(1.75); Shot("0b_intro_cyan");
    w.HideIntro();
    Shot("1_disclaimer");

    vm.DisclaimerTicked = true;
    Run(vm.AcceptDisclaimerCommand.ExecuteAsync(null));
    WaitFor(() => !vm.StatusLine.Contains("checking") && vm.StatusLine.Length > 0, 60);

    // two backups of the focused save so the backup cards show (reads the save, writes only to the temp folder)
    if (vm.Focused is { } f)
    {
        var bm = new BackupManager(backupRoot, new ActivityLog(null));
        bm.Create(f.Info, "Before clean");
        Thread.Sleep(20);
        bm.Create(f.Info, "Manual");
        // a backup taken from a different save, filed under this one, so the card shows its differences
        var other = vm.Rows.OfType<SaveItemViewModel>().First(s => s != f && s.IsReadable);
        var ob = bm.Create(other.Info, "Before restore");
        Directory.Move(ob.FolderPath, Path.Combine(backupRoot, f.Name, Path.GetFileName(ob.FolderPath)));
        vm.FocusSaveCommand.Execute(vm.Rows.OfType<SaveItemViewModel>().Skip(1).First());
        vm.FocusSaveCommand.Execute(f);
    }
    Shot("2_main");

    vm.SearchText = "Chippin";
    Shot("3_search");
    vm.SearchText = "";

    vm.ExpandAllCommand.Execute(null);
    vm.SelectTaggedCommand.Execute(null);
    vm.IsBusy = true; vm.BusyText = "Cleaning 168/373 · ManualSave-1101"; vm.Progress = 0.45;
    Shot("4_batch_progress");
    vm.IsBusy = false;

    vm.SelectNoneCommand.Execute(null);
    Pump();
    Run(vm.BackupCheckedCommand.ExecuteAsync(null));   // writes only to the temp backup folder; shows a result message
    Shot("5_result");

    vm.Log.Warn("Cyberpunk 2077 is running; nothing was changed");
    vm.Log.Error("ManualSave-999: example error to show the badge");
    vm.IsLogOpen = true;
    Pump(); Thread.Sleep(400);
    Shot("5b_log_drawer");
    vm.IsLogOpen = false;
    Pump(); Thread.Sleep(400);

    vm.IsMusicPanelOpen = true;
    Pump(); Thread.Sleep(400); Pump();
    Shot("6_music_panel");
    // click inside the player: stays open; click on the save list: closes; Esc: closes
    w.MouseDown(new Point(1200, 700), Avalonia.Input.MouseButton.Left); w.MouseUp(new Point(1200, 700), Avalonia.Input.MouseButton.Left); Pump();
    Console.WriteLine("inside click keeps it open: " + vm.IsMusicPanelOpen);
    w.MouseDown(new Point(150, 400), Avalonia.Input.MouseButton.Left); w.MouseUp(new Point(150, 400), Avalonia.Input.MouseButton.Left); Pump();
    Console.WriteLine("outside click closes it: " + !vm.IsMusicPanelOpen);
    vm.IsMusicPanelOpen = true; Pump();
    w.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null); Pump();
    Console.WriteLine("Esc closes it: " + !vm.IsMusicPanelOpen);
    vm.IsMusicPanelOpen = false;
    Pump(); Thread.Sleep(400);

    var footer = w.GetLogicalDescendants().OfType<Border>().First(b => ToolTip.GetTip(b) is ItemsControl);
    ToolTip.SetIsOpen(footer, true);
    Shot("8_footer_tooltip");
    ToolTip.SetIsOpen(footer, false);

    w.Width = 1200; w.Height = 720;
    Shot("7_min_width");

    var empty = Directory.CreateDirectory(Path.Combine(backupRoot, "no-saves-here")).FullName;
    vm.SavesFolder = empty;
    Run(vm.ReloadCommand.ExecuteAsync(null));
    Shot("9_no_saves");

    _ = w.ShowDialogAsync("Clean modded tag", "Clean 3 saves? Each one is backed up first (about 5,0 MB of backups).", "Clean", "Cancel");
    Shot("10_dialog");

    Console.WriteLine("snapshots in " + outDir);

    void Shot(string name)
    {
        Pump();
        var frame = w.CaptureRenderedFrame();
        frame?.Save(Path.Combine(outDir, name + ".png"));
    }
}
finally
{
    Directory.Delete(backupRoot, true);
}

static void Pump()
{
    for (int i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Thread.Sleep(15); }
}

static void Run(Task t)
{
    while (!t.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
    t.GetAwaiter().GetResult();
}

static void WaitFor(Func<bool> done, int seconds)
{
    var end = DateTime.Now.AddSeconds(seconds);
    while (!done() && DateTime.Now < end) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(25); }
}

/// <summary>Silent stand-in so the music flyout renders with a track 41 s into Chrome Highway.</summary>
sealed class StillPlayer : IMusicPlayer
{
    public bool IsAvailable => true;
    public string NowPlaying => "Chrome Highway";
    public event Action? NowPlayingChanged { add { value?.Invoke(); } remove { } }
    public double Volume { set { } }
    public double Position => 41;
    public double Duration => 83;
    public bool IsPaused => false;
    public void Play() { }
    public void Stop() { }
    public void Next() { }
    public void Previous() { }
    public void TogglePause() { }
    public void Seek(double seconds) { }
    public void Dispose() { }
}
