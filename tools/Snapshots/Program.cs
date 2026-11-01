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
var tempRoot = Directory.CreateTempSubdirectory("savebrowser-snap-").FullName;
var backupRoot = Directory.CreateDirectory(Path.Combine(tempRoot, "Backups")).FullName;   // saves.json goes next to it, inside tempRoot

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
        var bc = bm.Create(f.Info, "Before clean");
        File.AppendAllText(Path.Combine(bc.FolderPath, SaveInfo.MetadataFile), " ");   // differ, so the next one isn't a reused duplicate
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

    // restore review: three saves get a "Before clean" backup (nudged so it differs from the save), the first also a
    // newer manual one, the fourth only a manual one: a row to check, ready saves and a skipped one. Temp folder only.
    var picks = vm.Rows.OfType<SaveItemViewModel>().Where(s => s.IsReadable).Take(4).ToList();
    var bm2 = new BackupManager(backupRoot, new ActivityLog(null));
    var touched = new List<BatchSave>();
    foreach (var p in picks.Take(3))
    {
        var b = bm2.Create(p.Info, "Before clean", "snap-batch");
        File.AppendAllText(Path.Combine(b.FolderPath, SaveInfo.MetadataFile), " ");
        touched.Add(BatchLog.Snapshot(p.Info.FolderPath, b.Id));
        Thread.Sleep(5);
    }
    bm2.Create(picks[0].Info, "Manual");
    bm2.Create(picks[3].Info, "Manual");
    var pickNames = picks.Select(p => p.Name).ToHashSet();
    Run(vm.ReloadCommand.ExecuteAsync(null));   // so the list's backup counts include the ones just made
    WaitFor(() => !vm.StatusLine.Contains("checking"), 60);
    vm.ExpandAllCommand.Execute(null);
    vm.SelectNoneCommand.Execute(null);
    foreach (var p in vm.Rows.OfType<SaveItemViewModel>().Where(s => pickNames.Contains(s.Name))) p.IsChecked = true;
    Pump();
    var review = vm.RestoreCheckedCommand.ExecuteAsync(null);
    WaitFor(() => vm.Review is not null, 30);
    Shot("11_restore_review");
    vm.Review?.CancelCommand.Execute(null);
    Run(review);

    // undo: the same three as a recorded clean batch, the second "saved over by the game" since
    touched[1] = touched[1] with { SavLength = touched[1].SavLength + 1 };
    new BatchLog(Path.Combine(backupRoot, "batches.json")).Add(new BatchRecord("snap-batch", BatchKind.Clean, DateTime.UtcNow, vm.SavesFolder, touched));
    Run(vm.ReloadCommand.ExecuteAsync(null));
    WaitFor(() => !vm.StatusLine.Contains("checking"), 60);
    var undo = vm.UndoLastBatchCommand.ExecuteAsync(null);
    WaitFor(() => vm.Review is not null, 30);
    Shot("12_undo_review");
    vm.Review?.CancelCommand.Execute(null);
    Run(undo);

    // clean up backups: a Before restore copy that differs (offered as "before restore") and one identical to the save
    var nudged = bm2.Create(picks[2].Info, "Before restore");
    File.AppendAllText(Path.Combine(nudged.FolderPath, SaveInfo.MetadataFile), "  ");
    bm2.Create(picks[3].Info, "Before restore");
    var cleanup = vm.CleanUpBackupsCommand.ExecuteAsync(null);
    WaitFor(() => vm.Cleanup is not null, 30);
    Shot("13_cleanup_review");
    vm.Cleanup?.CancelCommand.Execute(null);
    Run(cleanup);

    var backupsButton = w.FindControl<Button>("BackupsButton")!;
    Run(vm.UpdateBackupsSummaryAsync());
    backupsButton.Flyout!.ShowAt(backupsButton);
    Pump(); Thread.Sleep(300);
    Shot("14_backups_menu");
    // click "Clean up backups…" the way a user does (button click, not the command directly): the review must open
    var menuLink = ((Control)((Flyout)backupsButton.Flyout).Content!).GetLogicalDescendants().OfType<Button>()
        .First(b => (b.Content as string)?.StartsWith("Clean up") == true);
    var peer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(menuLink);
    ((Avalonia.Automation.Provider.IInvokeProvider)peer).Invoke();
    WaitFor(() => vm.Cleanup is not null, 10);
    Console.WriteLine("menu click opens the cleanup review: " + (vm.Cleanup is not null));
    Console.WriteLine("menu closed after the click: " + !backupsButton.Flyout.IsOpen);
    vm.Cleanup?.CancelCommand.Execute(null);
    Pump();

    // stars (written to the temp saves.json only), the Starred filter, and the rename dialog (cancelled: no save is renamed)
    vm.SelectNoneCommand.Execute(null);
    Pump();
    var starRows = vm.Rows.OfType<SaveItemViewModel>().Take(3).ToList();
    vm.ToggleStarCommand.Execute(starRows[0]);
    vm.ToggleStarCommand.Execute(starRows[2]);
    vm.FocusSaveCommand.Execute(starRows[0]);
    Shot("15_stars");
    vm.ShowStarredOnly = true;
    Shot("16_starred_filter");
    vm.ShowStarredOnly = false;
    var rename = vm.RenameFocusedCommand.ExecuteAsync(null);
    WaitFor(() => vm.RenameDialog is not null, 10);
    vm.RenameDialog!.Name = "Before Konpeki";
    Shot("17_rename_dialog");
    vm.RenameDialog.Name = "AutoSave-3";
    Shot("17b_rename_refused");
    vm.RenameDialog.CancelCommand.Execute(null);
    Run(rename);

    // deleted saves: two temp copies of real saves are trashed (the real ones are never touched), then the view opens
    var fakeSaves = Directory.CreateDirectory(Path.Combine(tempRoot, "fake-saves")).FullName;
    var fakeTrash = new SaveTrash(Path.Combine(tempRoot, "Trash"), new ActivityLog(null));
    foreach (var s in starRows.Take(2))
    {
        var copy = Directory.CreateDirectory(Path.Combine(fakeSaves, s.Name)).FullName;
        foreach (var file in Directory.GetFiles(s.Info.FolderPath)) File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        fakeTrash.Delete(SaveScanner.Read(copy));
    }
    Run(vm.OpenTrashCommand.ExecuteAsync(null));
    vm.TrashView!.Rows[0].IsChecked = true;
    Shot("18_deleted_saves");
    vm.TrashView.CloseCommand.Execute(null);
    Pump();

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
    Directory.Delete(tempRoot, true);
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
