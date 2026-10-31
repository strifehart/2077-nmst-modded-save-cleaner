using System.Text.Json;

namespace Nmst.SaveCleaner.Core;

/// <summary>
/// Preferences kept between launches. The disclaimer acceptance is deliberately not stored here: it's asked for
/// on every launch.
/// </summary>
public sealed class AppSettings
{
    public bool MusicMuted { get; set; }

    /// <summary>0..1. Starts modest so the music never blasts on first launch.</summary>
    public const double DefaultMusicVolume = 0.25;
    public double MusicVolume { get; set; } = DefaultMusicVolume;

    /// <summary>A saves folder picked with "Change folder"; null means the default Saved Games location.</summary>
    public string? SavesFolder { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path)) return Sanitise(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new());
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* settings are a convenience, never fatal */ }
    }

    private static AppSettings Sanitise(AppSettings s)
    {
        s.MusicVolume = double.IsFinite(s.MusicVolume) ? Math.Clamp(s.MusicVolume, 0, 1) : DefaultMusicVolume;
        return s;
    }
}
