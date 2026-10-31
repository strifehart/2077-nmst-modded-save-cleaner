// Usage: DragProbe <saves folder>. Drags the save list's scrollbar thumb at several window sizes and reports
// whether the list scrolled. Reads saves only; backups go to a temp folder.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nmst.SaveCleaner.Avalonia;
using Nmst.SaveCleaner.ViewModels;

var saves = args[0];
var tmp = Directory.CreateTempSubdirectory("dragprobe-").FullName;
AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());
try
{
    var w = new MainWindow { Width = 1440, Height = 900 };
    var vm = new MainViewModel(new PlatformServices(w), saves, tmp, null);
    w.DataContext = vm;
    w.Show();
    w.HideIntro();
    vm.DisclaimerTicked = true;
    var t = vm.AcceptDisclaimerCommand.ExecuteAsync(null);
    while (!t.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
    if (Environment.GetEnvironmentVariable("PROBE_COLLAPSED") == "1") vm.CollapseAllCommand.Execute(null); else vm.ExpandAllCommand.Execute(null);

    foreach (var (width, height) in new[] { (1440, 900), (1920, 1040), (2560, 1400) })
    {
        w.Width = width; w.Height = height;
        Pump();
        var sv = w.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.GetVisualDescendants().OfType<ItemsControl>().Any(i => i.ItemsSource == vm.Rows));
        sv.Offset = new Vector(0, 0); Pump();
        if (args.Length > 1) { var until = DateTime.Now.AddSeconds(double.Parse(args[1])); while (DateTime.Now < until) Pump(); }   // idle like a real user before grabbing
        var thumb = sv.GetVisualDescendants().OfType<Thumb>().First(th => th.FindAncestorOfType<ScrollBar>() is { Orientation: Orientation.Vertical });
        var tl = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), w)!.Value;
        var hit = w.InputHitTest(tl);
        int rowsBefore = vm.Rows.Count;
        w.MouseMove(tl); Pump();
        w.MouseDown(tl, MouseButton.Left); Pump();
        for (int i = 1; i <= 10; i++) { w.MouseMove(tl + new Vector(0, i * 10), RawInputModifiers.LeftMouseButton); Pump(); }
        w.MouseUp(tl + new Vector(0, 100), MouseButton.Left); Pump();
        Console.WriteLine($"{width}x{height}: thumb {thumb.Bounds.Width:0}x{thumb.Bounds.Height:0} at {tl.X:0},{tl.Y:0} · hit-test finds {Describe(hit)} · scrolled to {sv.Offset.Y:0} of {sv.ScrollBarMaximum.Y:0} · rows {rowsBefore} -> {vm.Rows.Count}");
    }
}
finally { Directory.Delete(tmp, true); }

static string Describe(object? o) => o switch
{
    null => "nothing",
    Visual v => v.GetType().Name + (v.FindAncestorOfType<Thumb>() is not null || v is Thumb ? " (inside the thumb)" : $" (parent {v.GetVisualParent()?.GetType().Name})"),
    _ => o.GetType().Name,
};

static void Pump()
{
    for (int i = 0; i < 6; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Thread.Sleep(10); }
}
