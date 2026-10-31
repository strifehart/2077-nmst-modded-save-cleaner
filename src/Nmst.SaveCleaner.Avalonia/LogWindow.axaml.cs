using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using Nmst.SaveCleaner.ViewModels;

namespace Nmst.SaveCleaner.Avalonia;

public partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            vm.LogEntries.CollectionChanged += OnEntries;
            Closed += (_, _) => vm.LogEntries.CollectionChanged -= OnEntries;
            Dispatcher.UIThread.Post(() => Scroll.ScrollToEnd(), DispatcherPriority.Background);
        };
    }

    private void OnEntries(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() => Scroll.ScrollToEnd(), DispatcherPriority.Background);
}
