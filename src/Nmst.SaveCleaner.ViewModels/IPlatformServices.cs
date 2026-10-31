namespace Nmst.SaveCleaner.ViewModels;

/// <summary>The few things each UI framework does its own way. Everything else lives in the view models.</summary>
public interface IPlatformServices
{
    /// <summary>Shows a yes/no dialog and returns true for the confirm button.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText);

    /// <summary>Shows a message with a single OK button.</summary>
    Task AlertAsync(string title, string message);

    /// <summary>Lets the user pick a folder; null when cancelled.</summary>
    Task<string?> PickFolderAsync(string title, string? startFolder);

    /// <summary>Opens a folder in the system file manager.</summary>
    void OpenFolder(string path);

    /// <summary>Opens a web page in the default browser.</summary>
    void OpenUrl(string url);

    void CopyToClipboard(string text);

    void Quit();
}
