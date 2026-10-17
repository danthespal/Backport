using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ps5Backport.Core;

namespace Ps5Backport.App.ViewModels;

/// <summary>One firmware set in the store list.</summary>
public sealed record StoreSetRow(string Name, string Folder, int Libraries, int Problems)
{
    public string Title => $"FW {Name}";
    public string Summary => $"{Libraries} {(Libraries == 1 ? "library" : "libraries")} · " +
                             (Problems == 0 ? "verified" : $"{Problems} problem{(Problems == 1 ? "" : "s")}");
    public string Tone => Problems == 0 ? "ok" : "warn";
}

/// <summary>One library of the selected set, with its manifest entry and check results.</summary>
public sealed partial class StoreFileRow(string name, JsonObject? entry, string sonyBuild = "") : ObservableObject
{
    public string Name => name;
    public string Module => Text("module", System.IO.Path.GetFileNameWithoutExtension(name));
    public string Container => Text("container").Replace(" (make_fself.py)", "").Replace("-style", "");
    /// <summary>PS5 SDK as a firmware version: "0x04000031" -> "4.00".</summary>
    public string Sdk => Text("ps5_sdk") is { Length: 10 } s && uint.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out uint v)
        ? $"{v >> 24:X}.{(v >> 16) & 0xFF:X2}" : Text("ps5_sdk");
    public string BuildDate => Text("build_date");
    public string BuildPath => Text("build_path");
    /// <summary>Firmware a Sony library comes from ("J03828141 = FW 12.70"), from its build id.</summary>
    public string SonyBuild => sonyBuild;
    public bool HasSonyBuild => sonyBuild.Length > 0;
    public string Source => Text("source");
    public string Size => entry?["size"] is { } size ? $"{size.GetValue<long>():N0} bytes" : "";
    public string Sha256 => Text("sha256");

    /// <summary>Verify result ("hash ok", "checksum differs from manifest", …) or a known-broken build warning.</summary>
    [ObservableProperty] public partial string State { get; set; } = "";
    [ObservableProperty] public partial string Tone { get; set; } = "muted";
    /// <summary>Result of the last "Check against firmware" for this library.</summary>
    [ObservableProperty] public partial string CheckText { get; set; } = "Not checked yet";
    [ObservableProperty] public partial string CheckTone { get; set; } = "muted";

    string Text(string key, string fallback = "") => entry?[key]?.GetValue<string>() ?? fallback;
}

/// <summary>
/// The Fakelib store page (APP_PLAN 7.1 + 7.3): sets per firmware, their libraries with provenance,
/// verify, check against a firmware's export list, add libraries, and build a library from a BPS patch.
/// </summary>
public sealed partial class StoreViewModel(MainViewModel main) : ObservableObject
{
    LogViewModel Log => main.Log;

    public ObservableCollection<StoreSetRow> Sets { get; } = [];
    public ObservableCollection<StoreFileRow> Files { get; } = [];
    /// <summary>Firmwares to check against: every firmware database.</summary>
    public ObservableCollection<string> CheckFirmwares { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSet), nameof(SetTitle))]
    public partial StoreSetRow? SelectedSet { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFile))]
    public partial StoreFileRow? SelectedFile { get; set; }

    [ObservableProperty] public partial string? CheckFirmware { get; set; }
    [ObservableProperty] public partial string CheckSummary { get; set; } = "";
    [ObservableProperty] public partial bool Busy { get; set; }

    public bool HasSet => SelectedSet is not null;
    public bool HasFile => SelectedFile is not null;
    public string SetTitle => SelectedSet is { } s ? $"Fakelib set {s.Title}" : "";
    public string StoreText => main.StoreDir is { } store ? $"{Sets.Count} sets in {store}" : "No fakelib store; set one in Settings";

    // inputs of the Add and BPS flyouts
    [ObservableProperty] public partial string AddFirmware { get; set; } = "";
    [ObservableProperty] public partial string AddSource { get; set; } = "";
    [ObservableProperty] public partial string BpsPatch { get; set; } = "";
    [ObservableProperty] public partial string BpsInput { get; set; } = "";
    [ObservableProperty] public partial string BpsInfo { get; set; } = "";
    [ObservableProperty] public partial bool BpsSign { get; set; } = true;

    /// <summary>Set by the window: shows a file picker (title, file-type name, patterns, allow several) and returns the chosen paths.</summary>
    public Func<string, string, string[], bool, Task<string[]>>? PickFiles { get; set; }

    /// <summary>Reloads the set list (keeps the selection when the set still exists).</summary>
    public void Refresh()
    {
        try { RefreshSets(); }
        catch (Exception e) when (IsStoreError(e))
        {
            Sets.Clear();
            SelectedSet = null;
            Files.Clear();
            SelectedFile = null;
            OnPropertyChanged(nameof(StoreText));
            Log.Error($"Fakelib store couldn't be read: {e.Message}", exception: e);
        }
    }

    void RefreshSets()
    {
        string? keep = SelectedSet?.Name;
        Sets.Clear();
        if (main.StoreDir is { } store && Directory.Exists(store))
            foreach (var (name, manifest) in FakelibStore.List(store))
            {
                string folder = System.IO.Path.Combine(store, name);
                int libraries = Directory.EnumerateFiles(folder).Count(f => f.EndsWith(".sprx", StringComparison.OrdinalIgnoreCase)
                                                                             || f.EndsWith(".prx", StringComparison.OrdinalIgnoreCase));
                int problems = FakelibStore.Verify(folder).Count + FakelibStore.BuildWarnings(folder, main.KnowledgeData).Count;
                Sets.Add(new StoreSetRow(name, folder, libraries, problems));
            }
        // firmware_db/<fw>.json: the file names are enough (loading the lists would parse ~16 MB on the UI thread)
        string? keepCheck = CheckFirmware;
        CheckFirmwares.Clear();
        if (main.FirmwareDbDir is { } dbDir && Directory.Exists(dbDir))
            foreach (string fw in Directory.EnumerateFiles(dbDir, "*.json").Select(System.IO.Path.GetFileNameWithoutExtension).OfType<string>()
                                           .Where(IsFirmware).OrderBy(FirmwareDb.Key))
                CheckFirmwares.Add(fw);
        CheckFirmware = keepCheck is not null && CheckFirmwares.Contains(keepCheck) ? keepCheck : null;
        OnPropertyChanged(nameof(StoreText));
        SelectedSet = Sets.FirstOrDefault(s => s.Name == keep) ?? Sets.FirstOrDefault();
    }

    partial void OnSelectedSetChanged(StoreSetRow? value)
    {
        Files.Clear();
        SelectedFile = null;
        CheckSummary = "";
        if (value is null)
            return;
        try { LoadSet(value); }
        catch (Exception e) when (IsStoreError(e))
        {
            Files.Clear();
            Log.Error($"Fakelib set {value.Name} couldn't be read: {e.Message}", exception: e);
        }
    }

    static bool IsStoreError(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException
        or System.Text.Json.JsonException or InvalidOperationException or FormatException;

    void LoadSet(StoreSetRow value)
    {
        var manifest = FakelibStore.LoadManifest(value.Folder);
        var problems = FakelibStore.Verify(value.Folder).ToDictionary(p => p.Name, p => p.Problem);
        var broken = FakelibStore.BuildWarnings(value.Folder, main.KnowledgeData);
        var names = manifest.Select(kv => kv.Key).Union(problems.Keys).Order(StringComparer.Ordinal);
        foreach (string name in names)
        {
            var entry = manifest[name]?.AsObject();
            var row = new StoreFileRow(name, entry, main.KnowledgeData.SonyBuild(entry?["build_path"]?.GetValue<string>()));
            string? warning = broken.FirstOrDefault(w => w.StartsWith(name + " ", StringComparison.Ordinal));
            (row.State, row.Tone) = problems.TryGetValue(name, out string? problem) ? (problem, "warn")
                                  : warning is not null ? ("known-broken build", "error")
                                  : ("hash ok", "ok");
            Files.Add(row);
        }
        SelectedFile = Files.FirstOrDefault();
        AddFirmware = value.Name;
        // check against the set's own firmware by default (or the nearest database below it)
        CheckFirmware = CheckFirmwares.LastOrDefault(f => AtOrBelow(f, value.Name)) ?? CheckFirmwares.FirstOrDefault();
    }

    [RelayCommand]
    Task Check() => main.IsClosing ? Task.CompletedTask : main.OwnPageWork(CheckCore());

    async Task CheckCore()
    {
        if (SelectedSet is not { } set || CheckFirmware is not { } fw || main.FirmwareDbDir is not { } db)
            return;
        Busy = true;
        try
        {
            // the reference loads every firmware list (~16 MB of JSON): off the UI thread, like the check itself
            var (reference, results) = await Task.Run(() =>
            {
                var loaded = Imports.LoadReference(db, fw);
                return (loaded, FakelibStore.Check(set.Folder, loaded));
            });
            if (SelectedSet?.Folder != set.Folder || CheckFirmware != fw || main.FirmwareDbDir != db)
                return;
            int unresolved = 0;
            foreach (var (name, _, statuses) in results)
            {
                var missing = statuses.Where(s => Imports.IsUnresolved(s.Status)).ToList();
                unresolved += missing.Count;
                if (Files.FirstOrDefault(f => f.Name == name) is not { } row)
                    continue;
                row.CheckText = missing.Count == 0 ? $"all imports resolved on FW {fw}"
                    : $"{missing.Count} unresolved on FW {fw}: " + string.Join(", ", missing.Select(s => main.FunctionName(s.Nid)));
                row.CheckTone = missing.Count == 0 ? "ok" : "warn";
            }
            CheckSummary = $"{reference.Label}: {(unresolved == 0 ? "every import resolved" : $"{unresolved} unresolved import(s); a missing function only fails when called")}";
            Log.Info($"Checked fakelib set {set.Name} against {reference.Label}: {unresolved} unresolved import(s)");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or FormatException or ArgumentException)
        {
            Log.Error($"Check of fakelib set {set.Name} failed: {e.Message}", exception: e);
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    async Task AddFiles()
    {
        if (main.StoreDir is not { } store)
        {
            Log.Error("No fakelib store; set one in Settings");
            return;
        }
        if (!ValidFirmware(AddFirmware))
            return;
        if (PickFiles is null || await PickFiles("Select fakelib libraries", "PS5 libraries", ["*.sprx", "*.prx"], true) is not { Length: > 0 } files)
            return;
        if (main.IsClosing) return;
        try
        {
            var (folder, added) = FakelibStore.Add(store, AddFirmware, files, AddSource.Trim());
            Log.Info($"Added {string.Join(", ", added.Select(a => a.Name))} to {folder}");
            Select(System.IO.Path.GetFileName(folder));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException
                                     or System.Text.Json.JsonException)
        {
            Log.Error($"Adding fakelibs failed: {e.Message}", exception: e);
        }
    }

    [RelayCommand]
    async Task ChooseBpsPatch()
    {
        if (PickFiles is null || await PickFiles("Select a BPS patch", "BPS patch", ["*.bps"], false) is not [var path])
            return;
        BpsPatch = path;
        try
        {
            var patch = Bps.Load(path);
            BpsInfo = $"Expects a 0x{patch.SourceSize:x}-byte library with CRC32 {patch.SourceCrc:x8}; " +
                      $"{Bps.Edits(patch).Count} edits; output 0x{patch.TargetSize:x} bytes";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or PatchException or IndexOutOfRangeException or UnauthorizedAccessException)
        {
            BpsInfo = $"Not a usable BPS patch: {e.Message}";
        }
    }

    [RelayCommand]
    async Task ChooseBpsInput()
    {
        if (PickFiles is not null && await PickFiles("Select the library the patch applies to", "PS5 libraries", ["*.sprx", "*.prx", "*.elf"], false) is [var path])
            BpsInput = path;
    }

    /// <summary>Applies the BPS patch to the input library (unwrapped first if it is a SELF), optionally fake-signs it, and adds it to the set.</summary>
    [RelayCommand]
    void ApplyBps()
    {
        if (main.StoreDir is not { } store || !ValidFirmware(AddFirmware))
            return;
        if (!File.Exists(BpsPatch) || !File.Exists(BpsInput))
        {
            Log.Error("Choose a BPS patch and the library it applies to");
            return;
        }
        // scratch folder inside the store (the .ps5bp- prefix hides it from set listings), never the Windows temp folder
        string temp = System.IO.Path.Combine(store, ".ps5bp-bps-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte[] source = File.ReadAllBytes(BpsInput);
            if (Self.IsSelf(Self.Kind(source)))            // the patches expect the decrypted ELF
                source = Self.Unwrap(source);
            byte[] patched = Bps.Apply(Bps.Load(BpsPatch), source);
            string name = System.IO.Path.GetFileName(BpsInput);
            if (name.EndsWith(".elf", StringComparison.OrdinalIgnoreCase))
                name = System.IO.Path.ChangeExtension(name, ".sprx");
            Directory.CreateDirectory(temp);
            string output = System.IO.Path.Combine(temp, name);
            Self.WriteAtomic(output, BpsSign ? Self.Sign(patched) : patched);
            string note = AddSource.Trim().Length > 0 ? AddSource.Trim()
                : $"BPS patch {System.IO.Path.GetFileName(BpsPatch)} applied to {name}" + (BpsSign ? ", fake-signed" : "");
            var (folder, _) = FakelibStore.Add(store, AddFirmware, [output], note);
            Log.Info($"Patched {BpsInput} with {System.IO.Path.GetFileName(BpsPatch)}{(BpsSign ? " (fake-signed)" : "")} -> {System.IO.Path.Combine(folder, name)}");
            Select(System.IO.Path.GetFileName(folder));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or PatchException or ArgumentException or UnauthorizedAccessException
                                     or System.Text.Json.JsonException)
        {
            Log.Error($"BPS patch failed: {e.Message}", exception: e);
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, true);
        }
    }

    [RelayCommand]
    void OpenFolder()
    {
        if (SelectedSet is { } set && Directory.Exists(set.Folder))
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(set.Folder) { UseShellExecute = true });
        }
    }

    void Select(string name)
    {
        Refresh();
        SelectedSet = Sets.FirstOrDefault(s => s.Name == name) ?? SelectedSet;
        main.AddStoreFirmwares();
    }

    bool ValidFirmware(string firmware)
    {
        try
        {
            Firmware.Parse(firmware);
            return true;
        }
        catch (FormatException)
        {
            Log.Error($"Enter the set's firmware (e.g. 4.03 or 7), not '{firmware}'");
            return false;
        }
    }

    static bool IsFirmware(string name)
    {
        try { return Firmware.Name(name) == name; }
        catch (FormatException) { return false; }
    }

    /// <summary>firmware &lt;= set (false when the set folder isn't named after a firmware).</summary>
    static bool AtOrBelow(string firmware, string set)
    {
        try { return Firmware.Parse(firmware).CompareTo(Firmware.Parse(set)) <= 0; }
        catch (FormatException) { return false; }
    }
}
