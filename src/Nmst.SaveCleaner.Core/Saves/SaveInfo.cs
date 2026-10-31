namespace Nmst.SaveCleaner.Core.Saves;

public enum SaveKind { Manual, Auto, Quick, PointOfNoReturn, EndGame, Other }

/// <summary>Combined modded state of a save: the metadata flag plus the sticky ModdingSystem byte in sav.dat.</summary>
public enum ModdedState
{
    /// <summary>The sticky flag hasn't been read yet (or couldn't be).</summary>
    Unknown,
    Clean,
    /// <summary>isModded is true in metadata.9.json.</summary>
    Tagged,
    /// <summary>isModded is false but the sav.dat flag is set, so the next re-save tags it again.</summary>
    StickyOnly,
}

/// <summary>One save folder as found on disk. Metadata is null when metadata.9.json is missing or unreadable.</summary>
public sealed record SaveInfo(
    string FolderPath,
    SaveMetadata? Metadata,
    string? Error,
    long SizeBytes,
    DateTime LastWrite)
{
    public const string MetadataFile = "metadata.9.json";
    public const string SavFileName = "sav.dat";
    public const string ScreenshotFile = "screenshot.png";

    public string FolderName => Path.GetFileName(FolderPath);
    public string SavPath => Path.Combine(FolderPath, SavFileName);
    public string MetadataPath => Path.Combine(FolderPath, MetadataFile);
    public string? ScreenshotPath => File.Exists(Path.Combine(FolderPath, ScreenshotFile)) ? Path.Combine(FolderPath, ScreenshotFile) : null;

    public SaveKind Kind => KindOf(FolderName);
    public DateTime Timestamp => Metadata?.Timestamp ?? LastWrite;
    public string? PlaythroughId => string.IsNullOrEmpty(Metadata?.PlaythroughId) ? null : Metadata!.PlaythroughId;

    public static SaveKind KindOf(string folderName) =>
        folderName.StartsWith("ManualSave", StringComparison.OrdinalIgnoreCase) ? SaveKind.Manual
        : folderName.StartsWith("AutoSave", StringComparison.OrdinalIgnoreCase) ? SaveKind.Auto
        : folderName.StartsWith("QuickSave", StringComparison.OrdinalIgnoreCase) ? SaveKind.Quick
        : folderName.StartsWith("PointOfNoReturn", StringComparison.OrdinalIgnoreCase) ? SaveKind.PointOfNoReturn
        : folderName.StartsWith("EndGame", StringComparison.OrdinalIgnoreCase) ? SaveKind.EndGame
        : SaveKind.Other;

    public static ModdedState Combine(bool metadataModded, bool? sticky) =>
        metadataModded ? ModdedState.Tagged
        : sticky is null ? ModdedState.Unknown
        : sticky.Value ? ModdedState.StickyOnly : ModdedState.Clean;
}
