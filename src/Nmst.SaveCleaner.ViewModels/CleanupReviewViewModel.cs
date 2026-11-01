using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nmst.SaveCleaner.Core.Backups;

namespace Nmst.SaveCleaner.ViewModels;

/// <summary>One kind of backup the cleanup offers, ticked as a whole.</summary>
public sealed partial class CleanupGroupViewModel : ObservableObject
{
    public CleanupGroupViewModel(CleanupGroup group, IReadOnlyList<BackupInfo> backups, Action changed)
    {
        Backups = backups;
        _changed = changed;
        (Title, Description) = group switch
        {
            CleanupGroup.BeforeRestore => ("Before restore copies",
                "In-between states, usually a save the way the app cleaned it. Cleaning that save again recreates them."),
            CleanupGroup.SaveGone => ("Backups of saves that no longer exist",
                "The save folder was deleted, in game or by hand. These are the last copies of those saves."),
            CleanupGroup.OldVersion => ("Older saves from auto and quick slots",
                "The game has since written a different save into that slot; these are copies of the saves that were there before."),
            _ => ("Identical to the save as it is now", "The save itself is already this exact copy."),
        };
        Size = backups.Sum(b => b.SizeBytes);
        Items = backups.OrderBy(b => b.SaveFolderName, StringComparer.OrdinalIgnoreCase).ThenByDescending(b => b.Created)
            .Select(b => $"{b.SaveFolderName} · {b.Reason} · {Format.DateTime(b.Created)} · {Format.Size(b.SizeBytes)}").ToList();
    }

    private readonly Action _changed;
    public string Title { get; }
    public string Description { get; }
    public IReadOnlyList<BackupInfo> Backups { get; }
    public IReadOnlyList<string> Items { get; }
    public long Size { get; }
    public string CountText => $"{Backups.Count} backup{(Backups.Count == 1 ? "" : "s")} · {Format.Size(Size)}";
    public string ShowText => Backups.Count == 1 ? "Show it" : $"Show all {Backups.Count}";

    [ObservableProperty] private bool _isChecked = true;
    partial void OnIsCheckedChanged(bool value) => _changed();
}

/// <summary>
/// "Clean up backups": the backups that can go, grouped by why. Originals, manual backups and the ones Undo needs are
/// never listed.
/// </summary>
public sealed partial class CleanupReviewViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _done = new();

    public CleanupReviewViewModel(IReadOnlyList<CleanupCandidate> candidates, int kept)
    {
        Groups = candidates.GroupBy(c => c.Group).OrderBy(g => g.Key)
            .Select(g => new CleanupGroupViewModel(g.Key, g.Select(c => c.Backup).ToList(), Refresh)).ToList();
        Kept = kept;
    }

    public IReadOnlyList<CleanupGroupViewModel> Groups { get; }
    public int Kept { get; }
    public string KeptText => $"{Kept} backup{(Kept == 1 ? " is" : "s are")} kept and not listed: originals, manual backups and the ones Undo needs.";

    public IReadOnlyList<BackupInfo> Chosen => Groups.Where(g => g.IsChecked).SelectMany(g => g.Backups).ToList();
    public long ChosenSize => Groups.Where(g => g.IsChecked).Sum(g => g.Size);
    public string ConfirmText => Chosen.Count == 1 ? "Delete 1 backup" : $"Delete {Chosen.Count} backups";
    public string Summary => Chosen.Count == 0 ? "Nothing ticked" : $"Frees {Format.Size(ChosenSize)} · deleted backups can't be brought back";

    public Task<bool> Result => _done.Task;

    [RelayCommand(CanExecute = nameof(CanConfirm))] private void Confirm() => _done.TrySetResult(true);
    private bool CanConfirm() => Chosen.Count > 0;
    [RelayCommand] private void Cancel() => _done.TrySetResult(false);

    private void Refresh()
    {
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(Summary));
        ConfirmCommand.NotifyCanExecuteChanged();
    }
}
