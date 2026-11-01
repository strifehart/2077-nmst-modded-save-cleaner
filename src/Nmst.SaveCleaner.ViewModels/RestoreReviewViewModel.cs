using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nmst.SaveCleaner.Core.Backups;

namespace Nmst.SaveCleaner.ViewModels;

/// <summary>One restore point in a row's dropdown; Backup null means "don't restore this save".</summary>
public sealed record RestoreOption(string Label, BackupInfo? Backup)
{
    public override string ToString() => Label;
}

/// <summary>A save in the review: what it will be restored to, changeable per save.</summary>
public sealed partial class RestoreRowViewModel : ObservableObject
{
    public RestoreRowViewModel(string name, IReadOnlyList<RestoreOption> options, RestoreOption selected, string note, Action changed)
    {
        Name = name;
        Options = options;
        _selected = selected;
        Note = note;
        _changed = changed;
    }

    private readonly Action _changed;
    public string Name { get; }
    public IReadOnlyList<RestoreOption> Options { get; }
    /// <summary>Why this row needs a look ("newer backup exists", "changed since the batch"), or empty.</summary>
    public string Note { get; }
    public bool HasNote => Note.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIncluded), nameof(NameOpacity))]
    private RestoreOption _selected;

    public bool IsIncluded => Selected.Backup is not null;
    /// <summary>Saves left out are dimmed, not hidden.</summary>
    public double NameOpacity => IsIncluded ? 1 : 0.55;
    partial void OnSelectedChanged(RestoreOption value) => _changed();
}

/// <summary>A save left out of the restore, and why.</summary>
public sealed record RestoreSkip(string Name, string Reason);

/// <summary>
/// The review shown before a bulk restore or an undo. Only saves with a real choice are listed up front; saves with a
/// single matching backup sit in a collapsed "ready" group, and skipped saves say why.
/// </summary>
public sealed partial class RestoreReviewViewModel : ObservableObject
{
    private readonly Func<RestoreRule, IReadOnlyList<RestorePlanEntry>>? _replan;
    private readonly Func<RestorePlanEntry, IReadOnlyList<RestoreOption>> _options;
    private readonly TaskCompletionSource<bool> _done = new();

    /// <param name="replan">Re-plans for another rule (bulk restore); null for an undo, which has no rule.</param>
    public RestoreReviewViewModel(string title, string intro, IReadOnlyList<RestorePlanEntry> plan,
        Func<RestorePlanEntry, IReadOnlyList<RestoreOption>> options, Func<RestoreRule, IReadOnlyList<RestorePlanEntry>>? replan,
        RestoreRule rule = RestoreRule.UndoCleaning)
    {
        Title = title;
        Intro = intro;
        _options = options;
        _replan = replan;
        _rule = rule;
        Load(plan);
    }

    public string Title { get; }
    public string Heading => Title.ToUpperInvariant();
    public string Intro { get; }
    public bool HasRule => _replan is not null;

    public IReadOnlyList<RuleChoice> Rules { get; } =
    [
        new(RestoreRule.UndoCleaning, "Undo cleaning (latest Before clean)"),
        new(RestoreRule.LatestManual, "Latest manual backup"),
        new(RestoreRule.Newest, "Newest backup of any kind"),
    ];

    [ObservableProperty] private RestoreRule _rule;
    public RuleChoice SelectedRule
    {
        get => Rules.First(r => r.Rule == Rule);
        set { if (value is not null && value.Rule != Rule) { Rule = value.Rule; if (_replan is not null) Load(_replan(Rule)); } }
    }

    public ObservableCollection<RestoreRowViewModel> NeedsLook { get; } = [];
    public ObservableCollection<RestoreRowViewModel> Ready { get; } = [];
    public ObservableCollection<RestoreSkip> Skipped { get; } = [];

    public bool HasNeedsLook => NeedsLook.Count > 0;
    public bool HasReady => Ready.Count > 0;
    public bool HasSkipped => Skipped.Count > 0;
    public string ReadyHeader => $"{Ready.Count} save{(Ready.Count == 1 ? "" : "s")} with a single matching backup";
    public string SkippedHeader => $"{Skipped.Count} skipped";

    public int RestoreCount => NeedsLook.Concat(Ready).Count(r => r.IsIncluded);
    public string ConfirmText => RestoreCount == 1 ? "Restore 1 save" : $"Restore {RestoreCount} saves";
    public string Summary => $"{RestoreCount} to restore · {NeedsLook.Concat(Ready).Count(r => !r.IsIncluded) + Skipped.Count} left as they are";

    /// <summary>The rows the user kept, with the backup each one gets.</summary>
    public IReadOnlyList<(string Name, BackupInfo Backup)> Chosen =>
        NeedsLook.Concat(Ready).Where(r => r.IsIncluded).Select(r => (r.Name, r.Selected.Backup!)).ToList();

    public Task<bool> Result => _done.Task;

    [RelayCommand(CanExecute = nameof(CanConfirm))] private void Confirm() => _done.TrySetResult(true);
    private bool CanConfirm() => RestoreCount > 0;
    [RelayCommand] private void Cancel() => _done.TrySetResult(false);

    private void Load(IReadOnlyList<RestorePlanEntry> plan)
    {
        NeedsLook.Clear(); Ready.Clear(); Skipped.Clear();
        foreach (var e in plan.OrderBy(e => e.SaveFolderName, StringComparer.OrdinalIgnoreCase))
        {
            // nothing matching the rule: listed with the reason (another rule may pick it up), never restored silently
            if (e.Chosen is null) { Skipped.Add(new(e.SaveFolderName, e.SkipReason ?? "nothing to restore")); continue; }
            var options = _options(e);
            var pickable = options.Count(o => o.Backup is not null);

            // a save the game wrote to since the batch is offered but not ticked: undoing would lose that progress
            var selected = e.ChangedSinceBatch
                ? options.First(o => o.Backup is null)
                : options.First(o => o.Backup?.Id == e.Chosen.Id);
            string note = e.ChangedSinceBatch ? "changed since: the game saved over it"
                : e.NewerExists ? "a newer backup exists"
                : "";
            var row = new RestoreRowViewModel(e.SaveFolderName, options, selected, note, Refresh);
            if (pickable > 1 || note.Length > 0) NeedsLook.Add(row); else Ready.Add(row);
        }
        Refresh();
    }

    private void Refresh()
    {
        foreach (var n in new[] { nameof(HasNeedsLook), nameof(HasReady), nameof(HasSkipped), nameof(ReadyHeader), nameof(SkippedHeader),
                     nameof(RestoreCount), nameof(ConfirmText), nameof(Summary), nameof(SelectedRule) })
            OnPropertyChanged(n);
        ConfirmCommand.NotifyCanExecuteChanged();
    }
}

public sealed record RuleChoice(RestoreRule Rule, string Label)
{
    public override string ToString() => Label;
}
