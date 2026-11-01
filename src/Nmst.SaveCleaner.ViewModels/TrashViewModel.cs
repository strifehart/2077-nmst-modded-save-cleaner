using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nmst.SaveCleaner.Core.Localization;
using Nmst.SaveCleaner.Core.Saves;

namespace Nmst.SaveCleaner.ViewModels;

public sealed partial class TrashRowViewModel(TrashItem item, GameNames names, Action changed) : ObservableObject
{
    public TrashItem Item { get; } = item;
    public string Name => Item.SaveName;
    public string Line => (Item.Metadata is { } m
        ? $"{names.Text(m.LocationName) ?? "Unknown location"} · Lv {m.Level} · saved {Format.DateTime(m.Timestamp ?? Item.Deleted)}"
        : "Unreadable save") + $" · {Format.Size(Item.SizeBytes)}";
    public string DeletedLine => "Deleted " + Format.DateTime(Item.Deleted);
    public string Quest => Item.Metadata is { } m
        ? names.Text(m.TrackedQuest) ?? names.QuestFromEntry(m.TrackedQuestEntry) ?? "No tracked quest"
        : "";
    public string? ScreenshotPath => File.Exists(Path.Combine(Item.FolderPath, SaveInfo.ScreenshotFile)) ? Path.Combine(Item.FolderPath, SaveInfo.ScreenshotFile) : null;

    [ObservableProperty] private bool _isChecked;
    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>Deleted saves: restore them, or delete them for good.</summary>
public sealed partial class TrashViewModel : ObservableObject
{
    private readonly GameNames _names;
    private readonly Func<IReadOnlyList<TrashItem>, Task> _restore;
    private readonly Func<IReadOnlyList<TrashItem>, bool, Task> _deleteForever;   // (items, emptying the whole trash)
    private readonly Action _close;

    public TrashViewModel(IReadOnlyList<TrashItem> items, GameNames names,
        Func<IReadOnlyList<TrashItem>, Task> restore, Func<IReadOnlyList<TrashItem>, bool, Task> deleteForever, Action close)
    {
        _names = names;
        _restore = restore;
        _deleteForever = deleteForever;
        _close = close;
        Load(items);
    }

    public ObservableCollection<TrashRowViewModel> Rows { get; } = [];
    public bool IsEmpty => Rows.Count == 0;
    public bool HasRows => Rows.Count > 0;
    public string Summary => IsEmpty ? "The trash is empty." :
        $"{Rows.Count} save{(Rows.Count == 1 ? "" : "s")} · {Format.Size(Rows.Sum(r => r.Item.SizeBytes))}";
    private List<TrashItem> Ticked => Rows.Where(r => r.IsChecked).Select(r => r.Item).ToList();
    public string RestoreText => Ticked.Count > 1 ? $"Restore {Ticked.Count}" : "Restore";
    public string DeleteForeverText => Ticked.Count > 1 ? $"Delete {Ticked.Count} permanently" : "Delete permanently";

    public void Load(IReadOnlyList<TrashItem> items)
    {
        Rows.Clear();
        foreach (var i in items) Rows.Add(new TrashRowViewModel(i, _names, Changed));
        Resort();
        Changed();
    }

    // ---------- order: by deletion time (newest first) or by name (A–Z, numbers in natural order) ----------

    public enum TrashSort { Deleted, Name }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSortDeleted), nameof(IsSortName), nameof(DeletedSortLabel), nameof(NameSortLabel))]
    private TrashSort _sort = TrashSort.Deleted;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeletedSortLabel), nameof(NameSortLabel))]
    private bool _descending = true;

    public bool IsSortDeleted => Sort == TrashSort.Deleted;
    public bool IsSortName => Sort == TrashSort.Name;
    public string DeletedSortLabel => IsSortDeleted ? (Descending ? "Deleted ↓" : "Deleted ↑") : "Deleted";
    public string NameSortLabel => IsSortName ? (Descending ? "Name Z–A" : "Name A–Z") : "Name";

    /// <summary>Picks the order; picking the one already in use reverses it.</summary>
    [RelayCommand] private void SortByDeleted() => SortBy(TrashSort.Deleted, defaultDescending: true);
    [RelayCommand] private void SortByName() => SortBy(TrashSort.Name, defaultDescending: false);

    private void SortBy(TrashSort sort, bool defaultDescending)
    {
        if (Sort == sort) Descending = !Descending;
        else { Sort = sort; Descending = defaultDescending; }
        Resort();
    }

    // reorders the existing rows, so ticked saves stay ticked
    private void Resort()
    {
        IEnumerable<TrashRowViewModel> ordered = Sort == TrashSort.Name
            ? Rows.OrderBy(r => r.Name, NaturalComparer.Instance).ThenByDescending(r => r.Item.Deleted)
            : Rows.OrderBy(r => r.Item.Deleted).ThenBy(r => r.Name, NaturalComparer.Instance);
        var list = (Descending ? ordered.Reverse() : ordered).ToList();
        Rows.Clear();
        foreach (var r in list) Rows.Add(r);
    }

    private void Changed()
    {
        foreach (var n in new[] { nameof(IsEmpty), nameof(HasRows), nameof(Summary), nameof(RestoreText), nameof(DeleteForeverText) }) OnPropertyChanged(n);
        RestoreCommand.NotifyCanExecuteChanged();
        DeleteForeverCommand.NotifyCanExecuteChanged();
        EmptyCommand.NotifyCanExecuteChanged();
    }

    private bool AnyTicked() => Rows.Any(r => r.IsChecked);
    [RelayCommand(CanExecute = nameof(AnyTicked))] private Task Restore() => _restore(Ticked);
    [RelayCommand(CanExecute = nameof(AnyTicked))] private Task DeleteForever() => _deleteForever(Ticked, false);
    [RelayCommand(CanExecute = nameof(HasRows))] private Task Empty() => _deleteForever(Rows.Select(r => r.Item).ToList(), true);
    [RelayCommand] private void SelectAll() { foreach (var r in Rows) r.IsChecked = true; }
    [RelayCommand] private void Close() => _close();
}
