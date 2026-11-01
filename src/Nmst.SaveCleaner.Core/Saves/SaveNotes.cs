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
/// folder. Saves are keyed by which game save they are plus their folder (<see cref="KeyOf"/>): a key survives
/// restores and cleaning, follows the app's own renames, gives each copy of a save its own star, and an auto/quick
/// slot the game overwrites becomes a different save. Reading never throws.
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
    /// Which game save this is: "playthroughID|in-game save time". Both stay the same through cleaning, restoring
    /// and renaming, so two folders with the same identity are copies of one save. Null without readable metadata.
    /// </summary>
    public static string? IdentityOf(SaveMetadata? m) => m?.Timestamp is { } t
        ? $"{m.PlaythroughId}|{t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}"
        : null;

    /// <summary>
    /// Identity plus folder name, so each copy of a save has its own star. The app moves the entry along when it
    /// renames a save (<see cref="Rekey"/>); a save the game writes anew in an auto/quick slot is a new identity.
    /// </summary>
    public static string KeyOf(SaveInfo save) => KeyFor(save.Metadata, save.FolderName);

    private static string KeyFor(SaveMetadata? m, string folder) => IdentityOf(m) is { } id ? $"{id}|{folder}" : "folder|" + folder;

    /// <summary>Moves a save's entry to its new folder name after a rename (or a restore under another name).</summary>
    public void Rekey(SaveMetadata? m, string oldFolder, string newFolder)
    {
        var from = KeyFor(m, oldFolder);
        if (!_saves.Remove(from, out var note)) return;
        var to = KeyFor(m, newFolder);
        if (_saves.TryGetValue(to, out var existing)) { existing.Starred |= note.Starred; existing.Renames.InsertRange(0, note.Renames); }
        else _saves[to] = note;
    }

    /// <summary>
    /// saves.json from before stars were per copy keyed entries by identity alone; give each such entry to every
    /// copy that has that identity now (so nothing that looked starred loses its star). True when anything changed.
    /// </summary>
    public bool MigrateLegacy(IEnumerable<SaveInfo> saves)
    {
        var legacy = _saves.Keys.Where(k => k.Count(c => c == '|') == 1 && !k.StartsWith("folder|", StringComparison.Ordinal)).ToList();
        if (legacy.Count == 0) return false;
        var byIdentity = saves.Where(s => IdentityOf(s.Metadata) is not null).ToLookup(s => IdentityOf(s.Metadata)!);
        foreach (var key in legacy)
        {
            var note = _saves[key];
            _saves.Remove(key);
            foreach (var s in byIdentity[key])
                _saves.TryAdd(KeyOf(s), new SaveNote { Starred = note.Starred, StarredUtc = note.StarredUtc, Renames = [.. note.Renames] });
        }
        return true;
    }

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
        File.WriteAllText(tmp, JsonSerializer.Serialize(new FileModel(2, _saves), Json));
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
