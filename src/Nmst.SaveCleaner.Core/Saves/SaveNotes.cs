using System.Globalization;
using System.Text.Json;

namespace Nmst.SaveCleaner.Core.Saves;

/// <summary>One rename the app did: folder name before and after.</summary>
public sealed record RenameEntry(string From, string To, DateTime AtUtc);

/// <summary>What the app remembers about one save, apart from the save itself.</summary>
public sealed class SaveNote
{
    public bool Starred { get; set; }
    public DateTime? StarredUtc { get; set; }
    public List<RenameEntry> Renames { get; set; } = [];
}

/// <summary>
/// Stars and rename history, in <c>saves.json</c> in the app's own folder. Nothing is written into the save or its
/// folder. Saves are keyed by who they are (<see cref="KeyOf"/>), not by folder name, so a key survives renames,
/// restores and cleaning, and an auto/quick slot the game overwrites becomes a different save. Reading never throws.
/// </summary>
public sealed class SaveNotes
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed record FileModel(int Version, Dictionary<string, SaveNote> Saves);

    private readonly string _path;
    private readonly Dictionary<string, SaveNote> _saves;

    public SaveNotes(string path)
    {
        _path = path;
        _saves = Load(path);
    }

    /// <summary>
    /// "playthroughID|in-game save time": both stay the same through cleaning, restoring and renaming. A save
    /// without readable metadata falls back to its folder name.
    /// </summary>
    public static string KeyOf(SaveInfo save) => save.Metadata is { } m && m.Timestamp is { } t
        ? $"{m.PlaythroughId}|{t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}"
        : "folder|" + save.FolderName;

    public bool IsStarred(SaveInfo save) => _saves.TryGetValue(KeyOf(save), out var n) && n.Starred;

    public IReadOnlyList<RenameEntry> RenamesOf(SaveInfo save) => _saves.TryGetValue(KeyOf(save), out var n) ? n.Renames : [];

    public void SetStarred(SaveInfo save, bool starred)
    {
        var n = Get(save);
        n.Starred = starred;
        n.StarredUtc = starred ? DateTime.UtcNow : null;
        Tidy(save);
    }

    public void AddRename(SaveInfo save, string from, string to) => Get(save).Renames.Add(new RenameEntry(from, to, DateTime.UtcNow));

    /// <summary>Writes the file (atomically). Throws on I/O errors so the caller can say the change wasn't kept.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new FileModel(1, _saves), Json));
        File.Move(tmp, _path, true);
    }

    private SaveNote Get(SaveInfo save)
    {
        var key = KeyOf(save);
        if (!_saves.TryGetValue(key, out var n)) _saves[key] = n = new SaveNote();
        return n;
    }

    // an unstarred save with no renames doesn't need an entry
    private void Tidy(SaveInfo save)
    {
        var key = KeyOf(save);
        if (_saves.TryGetValue(key, out var n) && !n.Starred && n.Renames.Count == 0) _saves.Remove(key);
    }

    private static Dictionary<string, SaveNote> Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path)) is { Saves: { } s }) return s;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { }
        return [];
    }
}
