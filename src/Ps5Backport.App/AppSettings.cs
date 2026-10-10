using System.Text.Json;

namespace Ps5Backport.App;

/// <summary>
/// What the app remembers between runs: window geometry, log height, target firmware, options, the last
/// and recent games, and (for the Settings page) path overrides. Stored per user as JSON.
/// </summary>
public sealed class AppSettings
{
    public const int MaxRecentGames = 10;

    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public bool Maximized { get; set; }
    public double LogHeight { get; set; } = 200;

    public string Firmware { get; set; } = "4";
    public bool KnownFixes { get; set; } = true;
    public bool CandidateFixes { get; set; }
    public bool StubUnknown { get; set; }
    public bool DefineSymbol { get; set; }
    /// <summary>On by default (2026-10-09); a new JSON key so settings saved under the old off-by-default are not carried over.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("AutoExtendText")]
    public bool ExtendText { get; set; } = true;
    public bool PatchParam { get; set; }

    public string? LastGame { get; set; }
    public List<string> RecentGames { get; set; } = [];

    /// <summary>Overrides of the folders found next to the app (null = automatic).</summary>
    public string? FirmwareDbDir { get; set; }
    public string? FakelibStoreDir { get; set; }
    public string? AerolibPath { get; set; }

    /// <summary>%APPDATA%/PS5BackportStudio/settings.json (or the platform's equivalent).</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PS5BackportStudio", "settings.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The saved settings, or defaults when the file is missing or unreadable (<paramref name="problem"/> says why).</summary>
    public static AppSettings Load(string path, out string? problem)
    {
        problem = null;
        if (!File.Exists(path))
            return new AppSettings();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
            settings.Firmware ??= "4";
            settings.RecentGames ??= [];
            return settings;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            problem = $"Settings file {path} couldn't be read ({e.Message}); using defaults.";
            return new AppSettings();
        }
    }

    /// <summary>Writes through a temp file so a crash never leaves a half-written settings file.</summary>
    public void Save(string path)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Ps5Backport.Core.Self.WriteAtomic(path, System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, Json)));
    }

    /// <summary>Puts a game folder first in the recent list (no duplicates, at most <see cref="MaxRecentGames"/>).</summary>
    public void Remember(string gameFolder)
    {
        LastGame = gameFolder;
        RecentGames.RemoveAll(g => string.Equals(g, gameFolder, StringComparison.OrdinalIgnoreCase));
        RecentGames.Insert(0, gameFolder);
        if (RecentGames.Count > MaxRecentGames)
            RecentGames.RemoveRange(MaxRecentGames, RecentGames.Count - MaxRecentGames);
    }
}
