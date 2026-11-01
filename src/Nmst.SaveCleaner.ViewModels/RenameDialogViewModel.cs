using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nmst.SaveCleaner.Core.Saves;

namespace Nmst.SaveCleaner.ViewModels;

/// <summary>The rename / keep-this-autosave dialog: the name, checked as you type.</summary>
public sealed partial class RenameDialogViewModel : ObservableObject
{
    private readonly Func<string, string?> _validate;
    private readonly TaskCompletionSource<bool> _done = new();

    public RenameDialogViewModel(string title, string intro, string current, string original, string suggestion, Func<string, string?> validate)
    {
        Title = title;
        Intro = intro;
        Current = current;
        Original = original;
        _validate = validate;
        _name = suggestion;
        Check();
    }

    public string Title { get; }
    public string Heading => Title.ToUpperInvariant();
    public string Intro { get; }
    public string Current { get; }
    public string Original { get; }
    public string OriginalNote => Original.Length > 0
        ? $"Original name: {Original}"
        : "";
    public string CloudNote => "If this is your latest cloud save, the old name can show in the Load menu until you save again.";

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _isValid;
    public string Counter => $"{Name.Trim().Length} / {SaveRenamer.MaxLength}";

    partial void OnNameChanged(string value) => Check();

    private void Check()
    {
        Error = _validate(Name) ?? "";
        IsValid = Error.Length == 0;
        OnPropertyChanged(nameof(Counter));
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    public Task<bool> Result => _done.Task;
    [RelayCommand(CanExecute = nameof(IsValid))] private void Confirm() => _done.TrySetResult(true);
    [RelayCommand] private void Cancel() => _done.TrySetResult(false);
}
