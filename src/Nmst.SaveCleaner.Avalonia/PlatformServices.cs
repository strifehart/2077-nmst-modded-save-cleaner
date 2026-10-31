using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Nmst.SaveCleaner.ViewModels;

namespace Nmst.SaveCleaner.Avalonia;

public sealed class PlatformServices(MainWindow owner) : IPlatformServices
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText) =>
        owner.ShowDialogAsync(title, message, confirmText, "Cancel");

    public Task AlertAsync(string title, string message) =>
        owner.ShowDialogAsync(title, message, "OK", null);

    public async Task<string?> PickFolderAsync(string title, string? startFolder)
    {
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (startFolder is not null && Directory.Exists(startFolder))
            options.SuggestedStartLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(startFolder);
        var picked = await owner.StorageProvider.OpenFolderPickerAsync(options);
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    public void OpenFolder(string path)
    {
        if (Directory.Exists(path)) _ = owner.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
    }

    public void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps) _ = owner.Launcher.LaunchUriAsync(uri);
    }

    public void CopyToClipboard(string text) => _ = owner.Clipboard?.SetTextAsync(text);

    public void Quit()
    {
        if (global::Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d) d.Shutdown();
    }
}
