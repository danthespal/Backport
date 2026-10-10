using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ps5Backport.Core;

namespace Ps5Backport.App.ViewModels;

/// <summary>One firmware in the database list.</summary>
public sealed record FirmwareRow(FirmwareExports Db)
{
    public string Firmware => Db.Firmware;
    public string Summary => $"{Db.Modules.Count} modules · {Db.Nids.Count:N0} exports";
    public string Kind => Db.DataSymbols ? "functions and data" : "functions only";
}

/// <summary>One module of the selected firmware, compared with another firmware.</summary>
public sealed record FirmwareModuleRow(string Module, int Exports, IReadOnlyList<string> Added, IReadOnlyList<string> Removed)
{
    public string ExportsText => Exports == 0 ? "" : Exports.ToString("N0");
    public string AddedText => Added.Count == 0 ? "" : $"+{Added.Count}";
    public string RemovedText => Removed.Count == 0 ? "" : $"−{Removed.Count}";
}

/// <summary>A function added or removed between the two firmwares (for the inspector).</summary>
public sealed record FirmwareNidRow(string Nid, string Name, string Change)
{
    public string Tone => Change == "added" ? "ok" : "warn";
    public string ChangeText => Change == "added" ? "+" : "−";
}

/// <summary>
/// The Firmware database page (APP_PLAN 7.2): the export lists in firmware_db/, what each firmware
/// adds or removes compared with another, and importing a new list from a dump or genstub folder.
/// </summary>
public sealed partial class FirmwareDbViewModel(MainViewModel main) : ObservableObject
{
    List<FirmwareModuleRow> _allModules = [];
    int _refreshVersion;

    public ObservableCollection<FirmwareRow> Firmwares { get; } = [];
    public ObservableCollection<FirmwareModuleRow> Modules { get; } = [];
    public ObservableCollection<FirmwareNidRow> Changes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFirmware), nameof(Title), nameof(Details))]
    public partial FirmwareRow? Selected { get; set; }

    [ObservableProperty] public partial FirmwareRow? CompareWith { get; set; }
    [ObservableProperty] public partial FirmwareModuleRow? SelectedModule { get; set; }
    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial bool ChangedOnly { get; set; } = true;
    [ObservableProperty] public partial string CompareSummary { get; set; } = "";
    [ObservableProperty] public partial string ChangesTitle { get; set; } = "";
    [ObservableProperty] public partial bool Busy { get; set; }

    public bool HasFirmware => Selected is not null;
    public string Title => Selected is { } s ? $"Firmware {s.Firmware}" : "";
    public string Details => Selected is { } s ? $"{s.Db.Source} · {s.Kind}" : "";
    public string DbText => main.FirmwareDbDir is { } db ? $"{Firmwares.Count} firmwares in {db}" : "No firmware database; set one in Settings";

    // inputs of the Import flyout
    [ObservableProperty] public partial string ImportFirmware { get; set; } = "";
    [ObservableProperty] public partial string ImportSource { get; set; } = "";
    [ObservableProperty] public partial bool ImportGenstub { get; set; }

    /// <summary>Loads every export list (off the UI thread: 43 lists are about 16 MB of JSON).</summary>
    public Task Refresh() => main.IsClosing ? Task.CompletedTask : main.OwnPageWork(RefreshCore());

    async Task RefreshCore()
    {
        string? keep = Selected?.Firmware;
        string? directory = main.FirmwareDbDir;
        int version = ++_refreshVersion;
        Busy = true;
        try
        {
            var dbs = await Task.Run(() => FirmwareDb.LoadAll(directory));
            if (version != _refreshVersion || directory != main.FirmwareDbDir)
                return;
            Firmwares.Clear();
            foreach (var db in dbs)
                Firmwares.Add(new FirmwareRow(db));
            OnPropertyChanged(nameof(DbText));
            Selected = Firmwares.FirstOrDefault(f => f.Firmware == keep) ?? Firmwares.FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or FormatException)
        {
            main.Log.Error($"Firmware database couldn't be read: {e.Message}", exception: e);
        }
        finally { if (version == _refreshVersion) Busy = false; }
    }

    partial void OnSelectedChanged(FirmwareRow? value)
    {
        if (value is null)
        {
            CompareWith = null;
            Compare();
            return;
        }
        ImportFirmware = value.Firmware;
        // compare with the next newer firmware by default (the previous one for the newest)
        int i = Firmwares.IndexOf(value);
        var next = i + 1 < Firmwares.Count ? Firmwares[i + 1] : i > 0 ? Firmwares[i - 1] : null;
        if (CompareWith == next)
            Compare();
        else
            CompareWith = next;
    }

    partial void OnCompareWithChanged(FirmwareRow? value) => Compare();
    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnChangedOnlyChanged(bool value) => ApplyFilter();

    partial void OnSelectedModuleChanged(FirmwareModuleRow? value)
    {
        Changes.Clear();
        ChangesTitle = "";
        if (value is null || CompareWith is not { } other)
            return;
        foreach (string nid in value.Added)
            Changes.Add(new FirmwareNidRow(nid, main.FunctionName(nid), "added"));
        foreach (string nid in value.Removed)
            Changes.Add(new FirmwareNidRow(nid, main.FunctionName(nid), "removed"));
        ChangesTitle = Changes.Count == 0 ? $"{value.Module}: no change in {other.Firmware}"
            : $"{value.Module} in {other.Firmware}: {value.Added.Count} added, {value.Removed.Count} removed";
    }

    /// <summary>Per module: exports on the selected firmware, and what the compared firmware adds and removes.</summary>
    void Compare()
    {
        _allModules = [];
        if (Selected is not { } a)
        {
            CompareSummary = "";
            ApplyFilter();
            return;
        }
        var b = CompareWith?.Db;
        var added = b is null ? new Dictionary<string, HashSet<string>>() : FirmwareDb.Diff(a.Db, b);
        var removed = b is null ? new Dictionary<string, HashSet<string>>() : FirmwareDb.Diff(b, a.Db);
        var modules = a.Db.Modules.Keys.Union(b?.Modules.Keys ?? Enumerable.Empty<string>()).Order(StringComparer.Ordinal);
        foreach (string module in modules)
            _allModules.Add(new FirmwareModuleRow(module, a.Db.Modules.GetValueOrDefault(module)?.Count ?? 0,
                                                  Sorted(added.GetValueOrDefault(module)), Sorted(removed.GetValueOrDefault(module))));
        CompareSummary = b is null ? "" :
            $"{b.Firmware} compared with {a.Firmware}: +{added.Values.Sum(v => v.Count):N0} added, −{removed.Values.Sum(v => v.Count):N0} removed " +
            $"in {_allModules.Count(m => m.Added.Count + m.Removed.Count > 0)} modules";
        ApplyFilter();
    }

    List<string> Sorted(HashSet<string>? nids) =>
        nids is null ? [] : nids.OrderBy(main.FunctionName, StringComparer.Ordinal).ToList();

    void ApplyFilter()
    {
        string? keep = SelectedModule?.Module;
        IEnumerable<FirmwareModuleRow> rows = _allModules;
        if (ChangedOnly && CompareWith is not null)
            rows = rows.Where(m => m.Added.Count + m.Removed.Count > 0);
        if (Search.Trim() is { Length: > 0 } q)
        {
            // module name, or a NID / function name the module exports or changes
            var nids = Selected?.Db.Modules ?? new Dictionary<string, HashSet<string>>();
            rows = rows.Where(m => m.Module.Contains(q, StringComparison.OrdinalIgnoreCase)
                                   || (nids.GetValueOrDefault(m.Module)?.Any(n => n == q || main.FunctionName(n).Contains(q, StringComparison.OrdinalIgnoreCase)) ?? false)
                                   || m.Added.Concat(m.Removed).Any(n => n == q || main.FunctionName(n).Contains(q, StringComparison.OrdinalIgnoreCase)));
        }
        Modules.Clear();
        foreach (var row in rows)
            Modules.Add(row);
        SelectedModule = Modules.FirstOrDefault(m => m.Module == keep) ?? Modules.FirstOrDefault();
    }

    /// <summary>Imports a decrypted /system/common/lib dump (or a genstub folder) as firmware_db/&lt;fw&gt;.json.</summary>
    [RelayCommand]
    Task Import() => main.IsClosing ? Task.CompletedTask : main.OwnPageWork(ImportCore());

    async Task ImportCore()
    {
        if (main.FirmwareDbDir is not { } db)
        {
            main.Log.Error("No firmware database folder; set one in Settings");
            return;
        }
        string firmware;
        try { firmware = Firmware.Name(ImportFirmware); }
        catch (FormatException)
        {
            main.Log.Error($"Enter the firmware of the dump (e.g. 4.03), not '{ImportFirmware}'");
            return;
        }
        if (ImportSource.Trim().Length == 0)
        {
            main.Log.Error("Say where the dump comes from (the source note is stored in the list)");
            return;
        }
        string what = ImportGenstub ? "genstub folder (*.c stubs)" : "decrypted system/common/lib dump";
        if (main.PickFolder is null || await main.PickFolder($"Select the {what} for FW {firmware}") is not { } folder)
            return;
        string path = System.IO.Path.Combine(db, $"{firmware}.json");
        if (File.Exists(path))
            main.Log.Warning($"Replacing the existing list for {firmware}");
        Busy = true;
        try
        {
            string source = ImportSource.Trim();
            bool genstub = ImportGenstub;
            var exports = await Task.Run(() => genstub ? FirmwareDb.ImportGenstub(folder, firmware, source) : FirmwareDb.ImportDump(folder, firmware, source));
            if (exports.Modules.Count == 0)
            {
                main.Log.Error($"No {(genstub ? "genstub stubs" : "decrypted modules")} found in {folder}; nothing written");
                return;
            }
            Directory.CreateDirectory(db);
            FirmwareDb.Save(exports, path);
            main.Log.Info($"Imported {exports.Firmware}: {exports.Modules.Count} modules, {exports.Nids.Count:N0} exports -> {path}");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            main.Log.Error($"Import failed: {e.Message}", exception: e);
            return;
        }
        finally { Busy = false; }
        await Refresh();
        Selected = Firmwares.FirstOrDefault(f => f.Firmware == firmware) ?? Selected;
    }
}
