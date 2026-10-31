using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nmst.SaveCleaner.Core.Playthroughs;

namespace Nmst.SaveCleaner.ViewModels;

public sealed partial class PlaythroughGroupViewModel : ObservableObject
{
    private readonly Action _changed;

    public PlaythroughGroupViewModel(Playthrough p, IReadOnlyList<SaveItemViewModel> items, Action changed)
    {
        Playthrough = p;
        Items = items;
        _changed = changed;
        foreach (var i in items)
            i.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SaveItemViewModel.IsChecked)) OnPropertyChanged(nameof(CheckState)); };
    }

    public Playthrough Playthrough { get; }
    public IReadOnlyList<SaveItemViewModel> Items { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountLabel))]
    private int _visibleCount;

    [ObservableProperty] private bool _isExpanded;

    public string Title => Playthrough.IsUngrouped ? "Ungrouped" : Playthrough.LifePathLabel(Playthrough.LifePath);
    public string CountLabel => VisibleCount == Items.Count ? $"{Items.Count}" : $"{VisibleCount}/{Items.Count}";
    public string StatsLine => Playthrough.IsUngrouped ? "No playthrough ID"
        : $"{Format.Duration(Playthrough.MaxPlayTime)} · max Lv {Playthrough.MaxLevel}";
    public string LatestLine => "Latest " + Format.Date(Playthrough.Latest);
    public string? Id => Playthrough.Id;

    /// <summary>true = all saves ticked, false = none, null = some.</summary>
    public bool? CheckState
    {
        get
        {
            int n = Items.Count(i => i.IsChecked);
            return n == 0 ? false : n == Items.Count ? true : null;
        }
        set
        {
            bool target = CheckState != true;
            foreach (var i in Items) i.IsChecked = target;
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
        _changed();
    }
}
