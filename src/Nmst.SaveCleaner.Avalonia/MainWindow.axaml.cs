using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Nmst.SaveCleaner.ViewModels;

namespace Nmst.SaveCleaner.Avalonia;

public partial class MainWindow : Window
{
    // intro timeline in seconds: things fade in one after another while the glow pulses pink ↔ cyan, then it all fades out
    private const double IntroFadeOutStart = 2.15, IntroEnd = 2.65, PulsePeriod = 1.2;
    private static readonly Color Pink = Color.Parse("#eb107e"), SitePink = Color.Parse("#e60073"), Cyan = Color.Parse("#00e5ff");

    private LogWindow? _log;
    private DispatcherTimer? _introTimer;
    private DateTime _introStart;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            vm.PropertyChanged += OnVmChanged;
            vm.LogEntries.CollectionChanged += OnLogEntriesChanged;
        };
        Opened += (_, _) => { StartIntro(); UpdateWindowChrome(); };
        Closing += (_, _) => _log?.Close();
        Intro.PointerPressed += (_, _) => SkipIntro();
        KeyDown += OnKeyDown;
        // a click anywhere outside the music player (or its mini player) closes it; tunnel so it's seen before the target handles it
        AddHandler(PointerPressedEvent, OnAnyPointerPressed, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty || e.Property == IsExtendedIntoWindowDecorationsProperty || e.Property == OffScreenMarginProperty)
                UpdateWindowChrome();
        };
    }

    private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainViewModel { IsMusicPanelOpen: true } vm || e.Source is not global::Avalonia.Visual v) return;
        if (IsInside(v, MusicPanel) || IsInside(v, MiniPlayer)) return;
        vm.IsMusicPanelOpen = false;
    }

    private static bool IsInside(global::Avalonia.Visual v, global::Avalonia.Visual container)
    {
        for (var p = v; p is not null; p = global::Avalonia.VisualTree.VisualExtensions.GetVisualParent(p))
            if (ReferenceEquals(p, container)) return true;
        return false;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Intro.IsVisible) { SkipIntro(); e.Handled = true; return; }
        if (e.Key == Key.Escape && DataContext is MainViewModel { IsMusicPanelOpen: true } open && !DialogLayer.IsVisible)
        {
            open.IsMusicPanelOpen = false;
            e.Handled = true;
            return;
        }
        if (!DialogLayer.IsVisible) return;
        if (e.Key == Key.Escape && DialogCancel.IsVisible) { CloseDialog(false); e.Handled = true; }
        else if (e.Key is Key.Enter or Key.Escape) { CloseDialog(e.Key == Key.Enter); e.Handled = true; }
    }

    // ---------- custom title bar ----------

    /// <summary>Shows the window buttons even without a real window (off-screen snapshots).</summary>
    public static bool ForceCustomChrome { get; set; }

    public void UpdateChromeForSnapshot() => UpdateWindowChrome();

    private void UpdateWindowChrome()
    {
        // our own buttons only where the system title bar is really gone; elsewhere the platform's own stay
        WindowButtons.IsVisible = ForceCustomChrome || IsExtendedIntoWindowDecorations;
        bool max = WindowState == WindowState.Maximized;
        MaxGlyph.IsVisible = !max;
        RestoreGlyph.IsVisible = max;
        // a maximised borderless window extends past the screen edge by the frame width; pull the content back in
        Root.Margin = max ? OffScreenMargin : default;
    }

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) ToggleMaximise();
        else BeginMoveDrag(e);
    }

    private void ToggleMaximise() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnMinimiseClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximiseClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e) => ToggleMaximise();
    private void OnCloseClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e) => Close();

    // ---------- in-window dialogs ----------

    private TaskCompletionSource<bool>? _dialog;

    /// <summary>Shows a confirm (cancelText set) or alert (cancelText null) card over the page; true = confirmed.</summary>
    public Task<bool> ShowDialogAsync(string title, string message, string confirmText, string? cancelText)
    {
        _dialog?.TrySetResult(false);
        _dialog = new TaskCompletionSource<bool>();
        DialogTitle.Text = title.ToUpperInvariant();
        DialogMessage.Text = message;
        DialogConfirm.Content = confirmText;
        DialogCancel.Content = cancelText;
        DialogCancel.IsVisible = cancelText is not null;
        DialogLayer.IsVisible = true;
        AppButtons.IsHitTestVisible = false;   // nothing else in the top bar while a question is open
        Dispatcher.UIThread.Post(() => DialogConfirm.Focus(), DispatcherPriority.Input);
        return _dialog.Task;
    }

    private void CloseDialog(bool result)
    {
        DialogLayer.IsVisible = false;
        AppButtons.IsHitTestVisible = true;
        var d = _dialog;
        _dialog = null;
        d?.TrySetResult(result);
    }

    private void OnDialogConfirm(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e) => CloseDialog(true);
    private void OnDialogCancel(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e) => CloseDialog(false);

    private void StartIntro()
    {
        (DataContext as MainViewModel)?.StartMusic();
        _introStart = DateTime.Now;
        _introTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            double t = (DateTime.Now - _introStart).TotalSeconds;
            RenderIntroAt(t);
            if (t >= IntroEnd) { _introTimer?.Stop(); Intro.IsVisible = false; }
        });
        _introTimer.Start();
    }

    /// <summary>Jumps to the fade-out.</summary>
    public void SkipIntro()
    {
        double t = (DateTime.Now - _introStart).TotalSeconds;
        if (t < IntroFadeOutStart) _introStart = DateTime.Now - TimeSpan.FromSeconds(IntroFadeOutStart);
    }

    /// <summary>Draws the intro as it looks <paramref name="t"/> seconds in (also used for off-screen snapshots).</summary>
    public void RenderIntroAt(double t)
    {
        static double Ramp(double t, double from, double to) => Math.Clamp((t - from) / (to - from), 0, 1);
        IntroName.Opacity = Ramp(t, 0.15, 0.6);
        IntroSub.Opacity = Ramp(t, 0.45, 0.85);
        IntroBy.Opacity = Ramp(t, 0.75, 1.15);
        Intro.Opacity = 1 - Ramp(t, IntroFadeOutStart, IntroEnd);

        double mix = (1 - Math.Cos(2 * Math.PI * t / PulsePeriod)) / 2;   // 0 = pink, 1 = cyan
        var name = new SolidColorBrush(Lerp(Pink, Cyan, mix));
        var by = new SolidColorBrush(Lerp(SitePink, Cyan, mix));
        IntroNameGlowXL.Foreground = name;
        IntroNameGlowWide.Foreground = name;
        IntroNameGlow.Foreground = name;
        IntroByGlowXL.Foreground = by;
        IntroByGlowWide.Foreground = by;
        IntroByGlow.Foreground = by;
        IntroSub.Effect = new DropShadowEffect { BlurRadius = 10, OffsetX = 0, OffsetY = 0, Color = Lerp(Pink, Cyan, mix) };
    }

    // position bar in the music flyout: click to jump to that point of the track
    private void OnSeekBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control bar || bar.Bounds.Width <= 0 || DataContext is not MainViewModel vm) return;
        vm.SeekTrack(e.GetPosition(bar).X / bar.Bounds.Width);
    }

    public void HideIntro()
    {
        _introTimer?.Stop();
        Intro.IsVisible = false;
    }

    private static Color Lerp(Color a, Color b, double k) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));

    // the log is its own window so it can sit on another monitor while a batch runs
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel vm) return;
        if (e.PropertyName == nameof(MainViewModel.IsLogOpen))
        {
            LogDrawer.Height = vm.IsLogOpen ? _logHeight : 0;
            if (vm.IsLogOpen) ScrollLogToEnd();
        }
        else if (e.PropertyName == nameof(MainViewModel.IsLogPoppedOut))
        {
            if (vm.IsLogPoppedOut)
            {
                if (_log is null)
                {
                    _log = new LogWindow { DataContext = vm, Icon = Icon };
                    _log.Closed += (_, _) => { _log = null; vm.IsLogPoppedOut = false; };
                    _log.Show(this);
                }
                else _log.Activate();
            }
            else _log?.Close();
        }
    }

    // ---------- docked log ----------

    private double _logHeight = 260;
    private double? _resizeStartY, _resizeStartHeight;

    private void ScrollLogToEnd() => Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd(), DispatcherPriority.Background);

    private void OnLogEntriesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (LogDrawer.Height > 0) ScrollLogToEnd();
    }

    // drag the drawer's top edge; the slide transition is switched off while dragging so it follows the mouse
    private void OnLogResizePressed(object? sender, PointerPressedEventArgs e)
    {
        _resizeStartY = e.GetPosition(this).Y;
        _resizeStartHeight = LogDrawer.Bounds.Height;
        LogDrawer.Transitions = null;
        e.Pointer.Capture(LogResizeHandle);
    }

    private void OnLogResizeMoved(object? sender, PointerEventArgs e)
    {
        if (_resizeStartY is null || _resizeStartHeight is null) return;
        double max = Math.Max(140, Bounds.Height * 0.7);
        _logHeight = Math.Clamp(_resizeStartHeight.Value + (_resizeStartY.Value - e.GetPosition(this).Y), 120, max);
        LogDrawer.Height = _logHeight;
    }

    private void OnLogResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        _resizeStartY = _resizeStartHeight = null;
        e.Pointer.Capture(null);
        LogDrawer.Transitions = [new global::Avalonia.Animation.DoubleTransition
        {
            Property = HeightProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = new global::Avalonia.Animation.Easings.CubicEaseOut(),
        }];
    }
}
