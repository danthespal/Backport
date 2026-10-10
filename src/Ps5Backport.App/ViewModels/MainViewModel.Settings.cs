using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ps5Backport.Core;

namespace Ps5Backport.App.ViewModels;

public enum PathKind { FirmwareDb, FakelibStore, Aerolib }

/// <summary>The pages of the navigation rail.</summary>
public enum AppPage { Backport, Store, FirmwareDb, Tools, Settings }

/// <summary>One knowledge-folder row of the Settings page.</summary>
public sealed class PathSetting
{
    public required PathKind Kind { get; init; }
    public required string Title { get; init; }
    public required string Icon { get; init; }
    public required string Path { get; init; }
    public required bool IsCustom { get; init; }
    public required string Status { get; init; }
    /// <summary>Status key for coloring: ok / warn.</summary>
    public required string Tone { get; init; }
    public string Source => IsCustom ? "custom" : "automatic";
}

/// <summary>The Settings page (APP_PLAN 6.6): knowledge folders, defaults (the same values as the Options flyout), this app.</summary>
public partial class MainViewModel
{

    public ObservableCollection<PathSetting> PathSettings { get; } = [];

    public string RecentGamesText => Settings.RecentGames.Count == 0
        ? "None remembered"
        : $"{Settings.RecentGames.Count} remembered; the last one reopens at start";

    public string SettingsFile => AppSettings.DefaultPath;

    /// <summary>Set by the window: shows a file picker (title, file-type name, pattern) and returns the chosen path.</summary>
    public Func<string, string, string, Task<string?>>? PickFile { get; set; }

    void OpenSettings()
    {
        RefreshPaths();
        OnPropertyChanged(nameof(RecentGamesText));
    }

    void RefreshPaths()
    {
        PathSettings.Clear();
        string? db = FirmwareDbDir;
        int dbs = db is not null && Directory.Exists(db) ? Directory.EnumerateFiles(db, "*.json").Count() : 0;
        PathSettings.Add(new PathSetting
        {
            Kind = PathKind.FirmwareDb, Title = "Firmware database", Icon = "", Path = db ?? "not found",
            IsCustom = Settings.FirmwareDbDir is not null,
            Status = dbs > 0 ? $"{dbs} firmware export lists" : "no firmware lists; analysis falls back to the 3.20 reference",
            Tone = dbs > 0 ? "ok" : "warn",
        });
        string? store = StoreDir;
        var sets = store is not null && Directory.Exists(store)
            ? Directory.EnumerateDirectories(store).Select(System.IO.Path.GetFileName).Order(StringComparer.Ordinal).ToList() : [];
        PathSettings.Add(new PathSetting
        {
            Kind = PathKind.FakelibStore, Title = "Fakelib store", Icon = "", Path = store ?? "not found",
            IsCustom = Settings.FakelibStoreDir is not null,
            Status = sets.Count > 0 ? $"sets {string.Join(", ", sets)}" : "no fakelib sets",
            Tone = sets.Count > 0 ? "ok" : "warn",
        });
        string? aerolib = AerolibPath;
        bool found = aerolib is not null && File.Exists(aerolib);
        PathSettings.Add(new PathSetting
        {
            Kind = PathKind.Aerolib, Title = "Function names (aerolib.csv)", Icon = "", Path = aerolib ?? "not found",
            IsCustom = Settings.AerolibPath is not null,
            Status = found ? "found" : "not found; imports show as NIDs only",
            Tone = found ? "ok" : "warn",
        });
    }

    [RelayCommand]
    async Task ChangePath(PathSetting row)
    {
        string? path = row.Kind == PathKind.Aerolib
            ? PickFile is null ? null : await PickFile("Select aerolib.csv", "CSV", "*.csv")
            : PickFolder is null ? null : await PickFolder($"Select the {row.Title.ToLowerInvariant()} folder");
        if (path is not null)
            SetPath(row.Kind, path);
    }

    [RelayCommand]
    void ResetPath(PathSetting row) => SetPath(row.Kind, null);

    /// <summary>Sets (or with null, clears) a path override and reloads what depends on it.</summary>
    public void SetPath(PathKind kind, string? path)
    {
        switch (kind)
        {
            case PathKind.FirmwareDb: Settings.FirmwareDbDir = path; Database.Firmwares.Clear(); Database.Selected = null; break;
            case PathKind.FakelibStore: Settings.FakelibStoreDir = path; break;
            case PathKind.Aerolib: Settings.AerolibPath = path; _names = null; break;
        }
        AddStoreFirmwares();
        OnPropertyChanged(nameof(FakelibText));
        RefreshPaths();
        Log.Info($"Settings: firmware DB {FirmwareDbDir ?? "not found"}; fakelib store {StoreDir ?? "not found"}; aerolib {AerolibPath ?? "not found"}");
    }

    [RelayCommand]
    void ClearRecentGames()
    {
        Settings.RecentGames.Clear();
        Settings.LastGame = null;
        OnPropertyChanged(nameof(RecentGamesText));
    }

    [RelayCommand]
    void OpenSettingsFolder()
    {
        string folder = System.IO.Path.GetDirectoryName(SettingsFile)!;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }
}
