using System.Globalization;
using System.Text.Json;

namespace Nmst.SaveCleaner.Core.Saves;

public sealed record Attributes(int Body, int Reflexes, int TechnicalAbility, int Intelligence, int Cool);

/// <summary>The fields of metadata.9.json (Data.metadata) that the app uses.</summary>
public sealed record SaveMetadata
{
    public string Name { get; init; } = "";
    public DateTime? Timestamp { get; init; }
    public TimeSpan PlayTime { get; init; }
    public TimeSpan PlaythroughTime { get; init; }
    public string LifePath { get; init; } = "";
    public string BodyGender { get; init; } = "";
    public string BrainGender { get; init; } = "";
    public int Level { get; init; }
    public int StreetCred { get; init; }
    public Attributes Attributes { get; init; } = new(0, 0, 0, 0, 0);
    public string Difficulty { get; init; } = "";
    public string BuildPatch { get; init; } = "";
    public int GameVersion { get; init; }
    public int SaveVersion { get; init; }
    public string BuildId { get; init; } = "";
    public string InitialBuildId { get; init; } = "";
    public string PlaythroughId { get; init; } = "";
    public string LocationName { get; init; } = "";
    public string TrackedQuest { get; init; } = "";
    public string TrackedQuestEntry { get; init; } = "";
    public string MainQuest { get; init; } = "";
    public IReadOnlyList<string> FinishedQuests { get; init; } = [];
    public IReadOnlyList<string> AdditionalContent { get; init; } = [];
    public bool IsModded { get; init; }
    public bool IsPointOfNoReturn { get; init; }
    public bool IsEndGameSave { get; init; }
    public long FileSize { get; init; }
    public string Platform { get; init; } = "";

    public static SaveMetadata Load(string path) => Parse(File.ReadAllText(path));

    public static SaveMetadata Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Data", out var data) || !data.TryGetProperty("metadata", out var m))
            throw new InvalidDataException("metadata.9.json has no Data.metadata");

        return new SaveMetadata
        {
            Name = Str(m, "name"),
            Timestamp = ParseTimestamp(Str(m, "timestampString")),
            PlayTime = TimeSpan.FromSeconds(Num(m, "playTime")),
            PlaythroughTime = TimeSpan.FromSeconds(Num(m, "playthroughTime")),
            LifePath = Str(m, "lifePath"),
            BodyGender = Str(m, "bodyGender"),
            BrainGender = Str(m, "brainGender"),
            Level = (int)Num(m, "level"),
            StreetCred = (int)Num(m, "streetCred"),
            Attributes = new((int)Num(m, "strength"), (int)Num(m, "reflexes"), (int)Num(m, "technicalAbility"), (int)Num(m, "intelligence"), (int)Num(m, "cool")),
            Difficulty = Str(m, "difficulty"),
            BuildPatch = Str(m, "buildPatch"),
            GameVersion = (int)Num(m, "gameVersion"),
            SaveVersion = (int)Num(m, "saveVersion"),
            BuildId = Str(m, "buildID").Trim(),
            InitialBuildId = Str(m, "initialBuildID").Trim(),
            PlaythroughId = Str(m, "playthroughID"),
            LocationName = Str(m, "locationName"),
            TrackedQuest = Str(m, "trackedQuest"),
            TrackedQuestEntry = Str(m, "trackedQuestEntry"),
            MainQuest = Str(m, "mainQuest"),
            FinishedQuests = Str(m, "finishedQuests").Split(' ', StringSplitOptions.RemoveEmptyEntries),
            AdditionalContent = m.TryGetProperty("additionalContentIds", out var dlc) && dlc.ValueKind == JsonValueKind.Array
                ? dlc.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToArray() : [],
            IsModded = Bool(m, "isModded"),
            IsPointOfNoReturn = Bool(m, "isPointOfNoReturn"),
            IsEndGameSave = Bool(m, "isEndGameSave"),
            FileSize = (long)Num(m, "fileSize"),
            Platform = Str(m, "platform"),
        };
    }

    /// <summary>Patch label: buildPatch when present ("2.31"), otherwise derived from gameVersion (2310 → "2.31").</summary>
    public string Patch
    {
        get
        {
            if (BuildPatch.Length > 0) return BuildPatch;
            if (GameVersion <= 0) return "?";
            string minor = (GameVersion % 1000).ToString("D3", CultureInfo.InvariantCulture).TrimEnd('0');
            return $"{GameVersion / 1000}.{(minor.Length == 0 ? "0" : minor)}";
        }
    }

    public bool HasPhantomLiberty => AdditionalContent.Contains("EP1");

    // Saves write "19:58:10, 10.12.2025" (24h, day.month.year) and don't pad single-digit days: "07:43:33, 6.09.2026".
    private static readonly string[] TimestampFormats = ["HH:mm:ss, d.MM.yyyy", "HH:mm:ss, d.M.yyyy", "H:mm:ss, d.M.yyyy"];

    internal static DateTime? ParseTimestamp(string s) =>
        DateTime.TryParseExact(s, TimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : null;

    private static string Str(JsonElement m, string k) => m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static double Num(JsonElement m, string k) => m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    private static bool Bool(JsonElement m, string k) => m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
}
