using NAudio.Vorbis;
using Nmst.SaveCleaner.Core;
using Nmst.SaveCleaner.Core.Saves;

namespace Nmst.SaveCleaner.Tests;

public class AppTests
{
    [Theory]
    [InlineData("19:58:10, 10.12.2025", 2025, 12, 10)]
    [InlineData("07:43:33, 6.09.2026", 2026, 9, 6)]   // games writes single-digit days unpadded
    [InlineData("07:43:33, 6.9.2026", 2026, 9, 6)]
    public void Save_timestamps_parse_with_and_without_padding(string s, int y, int m, int d)
    {
        var t = SaveMetadata.ParseTimestamp(s);
        Assert.NotNull(t);
        Assert.Equal(new DateTime(y, m, d), t!.Value.Date);
    }

    [Fact]
    public void Every_real_save_has_an_in_game_timestamp()
    {
        if (!RealSaves.Available) return;
        var missing = SaveScanner.Scan(RealSaves.Folder).Where(s => s.Metadata is { Timestamp: null }).Select(s => s.FolderName).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void Scan_lists_only_folders_with_a_sav_dat()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(Path.Combine(t.Saves, "Empty"));
        var onlyShot = Directory.CreateDirectory(Path.Combine(t.Saves, "ScreenshotOnly")).FullName;
        File.WriteAllText(Path.Combine(onlyShot, SaveInfo.ScreenshotFile), "x");
        var broken = Directory.CreateDirectory(Path.Combine(t.Saves, "SavButNoMetadata")).FullName;
        File.WriteAllText(Path.Combine(broken, SaveInfo.SavFileName), "x");

        var found = SaveScanner.Scan(t.Saves);

        var s = Assert.Single(found);
        Assert.Equal("SavButNoMetadata", s.FolderName);
        Assert.NotNull(s.Error);
    }

    [Fact]
    public void Settings_round_trip_and_survive_garbage()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Root, "settings.json");
        Assert.False(AppSettings.Load(path).MusicMuted);
        Assert.Equal(0.25, AppSettings.Load(path).MusicVolume);

        new AppSettings { MusicMuted = true, MusicVolume = 0.55 }.Save(path);
        var back = AppSettings.Load(path);
        Assert.True(back.MusicMuted);
        Assert.Equal(0.55, back.MusicVolume);

        File.WriteAllText(path, "{ not json");
        Assert.False(AppSettings.Load(path).MusicMuted);
        File.WriteAllText(path, """{ "MusicVolume": 7 }""");
        Assert.Equal(1, AppSettings.Load(path).MusicVolume);
    }

    [Fact]
    public void Legacy_backups_move_to_the_new_folder_without_overwriting()
    {
        using var t = new TempDir();
        var legacy = Path.Combine(t.Root, "SaveBrowser");
        var data = Path.Combine(t.Root, "new");
        foreach (var id in new[] { "20261030-100000-000", "20261030-110000-000" })
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(legacy, "Backups", "ManualSave-1", id)).FullName, "backup.json"), id);
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(legacy, "Logs")).FullName, "SaveBrowser.log"), "log");
        // one backup already exists in the new place: it must not be replaced
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(data, "Backups", "ManualSave-1", "20261030-110000-000")).FullName, "backup.json"), "newer");

        var msg = AppPaths.MigrateLegacy(legacy, data);

        Assert.Contains("Moved 1 backup", msg);
        Assert.Equal("20261030-100000-000", File.ReadAllText(Path.Combine(data, "Backups", "ManualSave-1", "20261030-100000-000", "backup.json")));
        Assert.Equal("newer", File.ReadAllText(Path.Combine(data, "Backups", "ManualSave-1", "20261030-110000-000", "backup.json")));
        Assert.True(File.Exists(Path.Combine(data, "Logs", "old-SaveBrowser.log")));
        Assert.True(Directory.Exists(Path.Combine(legacy, "Backups", "ManualSave-1", "20261030-110000-000")));   // kept, not lost
        Assert.Null(AppPaths.MigrateLegacy(Path.Combine(t.Root, "missing"), data));
    }

    [Fact]
    public void Music_tracks_decode_and_match_their_manifest()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Music");
        var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "tracks.json"))).RootElement;
        Assert.Equal(5, manifest.GetArrayLength());
        foreach (var t in manifest.EnumerateArray())
        {
            using var r = new VorbisWaveReader(Path.Combine(dir, t.GetProperty("file").GetString()!));
            Assert.Equal(44100, r.WaveFormat.SampleRate);
            Assert.Equal(2, r.WaveFormat.Channels);
            Assert.InRange(r.TotalTime.TotalSeconds, t.GetProperty("duration").GetDouble() - 0.5, t.GetProperty("duration").GetDouble() + 0.5);
            Assert.True(t.GetProperty("outroStart").GetDouble() < r.TotalTime.TotalSeconds - 10);
        }
    }
}
