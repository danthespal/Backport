using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ps5Backport.Core;

namespace Ps5Backport.App.ViewModels;

/// <summary>
/// The Backport workbench: game header, target firmware and options, Analyze (dry run) and Cook
/// (writes the overlay), the Overview / Modules / Imports / Report tabs, the inspector and the log.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    Knowledge? _knowledge;
    NameDb? _names;
    CancellationTokenSource? _cancel;
    BackportOperation? _operation;
    AnalysisReceipt? _analysis;
    Task _activeWork = Task.CompletedTask;
    readonly HashSet<Task> _pageWork = [];
    bool _closing;
    readonly DiagnosticLog? _diagnostics;
    bool _diagnosticFailureReported;
    readonly BatchCollection<ImportRow> _imports = new();
    internal bool IsClosing => _closing;

    /// <summary>Retains page workers so shutdown can await operations that already own files.</summary>
    internal async Task OwnPageWork(Task work)
    {
        lock (_pageWork) _pageWork.Add(work);
        try { await work; }
        finally { lock (_pageWork) _pageWork.Remove(work); }
    }
    List<ImportRow> _allImports = [];

    /// <param name="settings">Remembered firmware, options, games and path overrides (defaults when null).</param>
    public MainViewModel(AppSettings? settings = null) : this(settings, null) { }

    /// <summary>The caller owns the optional local diagnostic sink and disposes it after all workers finish.</summary>
    public MainViewModel(AppSettings? settings, DiagnosticLog? diagnostics)
    {
        _diagnostics = diagnostics;
        Log = new LogViewModel(diagnostics);
        Settings = settings ?? new AppSettings();
        Store = new StoreViewModel(this);
        Database = new FirmwareDbViewModel(this);
        Tools = new ToolsViewModel(this);
        foreach (int major in Firmware.SdkPairs.Keys.Order())
            Firmwares.Add(major.ToString());
        AddStoreFirmwares();
        SelectedFirmware = TryName(Settings.Firmware) is not null ? Settings.Firmware : "4";
        if (!Firmwares.Contains(SelectedFirmware))
            Firmwares.Add(SelectedFirmware);
        KnownFixes = Settings.KnownFixes;
        CandidateFixes = Settings.CandidateFixes;
        StubUnknown = Settings.StubUnknown;
        DefineSymbol = Settings.DefineSymbol;
        ExtendText = Settings.ExtendText;
        PatchParam = Settings.PatchParam;
        foreach (var option in Knowledge.Options.Values)
            PatchOptions.Add(new OptionRow(option));
        Log.Info($"PS5 Backport Studio ready. Firmware DB: {FirmwareDbDir ?? "not found"}; fakelib store: {StoreDir ?? "not found"}");
        ReportDiagnosticFailure();
    }

    public AppSettings Settings { get; }
    public StoreViewModel Store { get; }
    public FirmwareDbViewModel Database { get; }
    public ToolsViewModel Tools { get; }

    /// <summary>Loading of the current page, when it loads in the background (the firmware database).</summary>
    public Task PageLoad { get; private set; } = Task.CompletedTask;

    /// <summary>The page shown next to the navigation rail.</summary>
    [ObservableProperty] public partial AppPage Page { get; set; }

    partial void OnPageChanged(AppPage value)
    {
        if (value == AppPage.Settings)
            OpenSettings();
        else if (value == AppPage.Store)
            Store.Refresh();
        else if (value == AppPage.FirmwareDb && Database.Firmwares.Count == 0)
            PageLoad = Database.Refresh();
        else if (value == AppPage.Tools)
            PageLoad = Tools.Open();
    }

    /// <summary>Folders and files the app uses: the Settings override, else the ones found next to the app.</summary>
    public string? FirmwareDbDir => Settings.FirmwareDbDir ?? Paths.FirmwareDbDir;
    public string? StoreDir => Settings.FakelibStoreDir ?? Paths.FakelibStoreDir;
    public string? AerolibPath => Settings.AerolibPath
        ?? Paths.FindUp(System.IO.Path.Combine(".original", "reference", "sce_symbols_new", "aerolib.csv"));

    /// <summary>Copies the current firmware and options into <see cref="Settings"/> (before saving).</summary>
    public void CaptureSettings()
    {
        Settings.Firmware = SelectedFirmware;
        Settings.KnownFixes = KnownFixes;
        Settings.CandidateFixes = CandidateFixes;
        Settings.StubUnknown = StubUnknown;
        Settings.DefineSymbol = DefineSymbol;
        Settings.ExtendText = ExtendText;
        Settings.PatchParam = PatchParam;
    }

    /// <summary>Reopens the last game, if it still exists.</summary>
    public void RestoreLastGame()
    {
        if (Settings.LastGame is { } last && Directory.Exists(last))
            LoadGame(last);
    }

    public LogViewModel Log { get; }

    void ReportDiagnosticFailure()
    {
        if (!_diagnosticFailureReported && _diagnostics?.Failure is { } failure)
        {
            _diagnosticFailureReported = true;
            Log.Warning($"Local diagnostics unavailable: {failure}");
        }
    }

    /// <summary>Set by the window: shows a folder picker and returns the chosen path.</summary>
    public Func<string, Task<string?>>? PickFolder { get; set; }
    /// <summary>Set by the window: puts text on the clipboard.</summary>
    public Func<string, Task>? SetClipboard { get; set; }

    // --- game ------------------------------------------------------------------------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGame), nameof(GameTitle), nameof(GameLine), nameof(RequiredText), nameof(GapPercent))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(CookCommand), nameof(BuildAmprIndexCommand))]
    public partial GameInfo? Game { get; set; }

    [ObservableProperty] public partial Bitmap? Cover { get; set; }

    partial void OnCoverChanged(Bitmap? oldValue, Bitmap? newValue) => oldValue?.Dispose();

    /// <summary>Releases the cover and cancels any work when the window closes.</summary>
    public void Dispose()
    {
        _closing = true;
        Cancel();
        Cover = null;
    }

    /// <summary>Close policy: stop new work, request cancellation, then wait for staging/commit cleanup.</summary>
    public async Task CancelAndWaitAsync()
    {
        _closing = true;
        Cancel();
        await _activeWork;
        Task[] pages;
        lock (_pageWork) pages = _pageWork.ToArray();
        await Task.WhenAll(pages);
    }

    public bool HasGame => Game is not null;
    public string GameTitle => Game?.TitleName ?? "Open or drop a game folder";
    public string GameLine => Game is null ? "A decrypted dump: eboot.bin, sce_module/, sce_sys/param.json" :
        string.Join("  ·  ", new[] { Game.TitleId, Game.Region, Game.ContentVersion.Length > 0 ? $"v{Game.ContentVersion}" : "",
                                     Game.SdkVersion.Length > 0 ? $"SDK {Game.SdkVersion}" : "" }.Where(s => s.Length > 0));
    public string RequiredText => Game is { RequiredFirmware.Length: > 0 } g ? $"Game requires FW {g.RequiredFirmware}" : "";

    // --- target and options ----------------------------------------------------------------
    public ObservableCollection<string> Firmwares { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetText), nameof(GapPercent), nameof(FakelibText))]
    public partial string SelectedFirmware { get; set; }

    public string TargetText => TryName(SelectedFirmware) is { } fw ? $"Target FW {fw}" : "Target firmware?";

    /// <summary>How far the target firmware is from what the game requires (0-100, for the gap bar).</summary>
    public double GapPercent
    {
        get
        {
            if (Game is not { RequiredFirmware.Length: > 0 } g || TryName(SelectedFirmware) is not { } target)
                return 0;
            double required = Version(g.RequiredFirmware), have = Version(target);
            return required <= 0 ? 0 : Math.Clamp(have / required * 100, 0, 100);
        }
    }

    /// <summary>Adds the store's sets (e.g. "4.03") to the firmware picker.</summary>
    internal void AddStoreFirmwares()
    {
        if (StoreDir is { } store && Directory.Exists(store))
            foreach (string folder in Directory.EnumerateDirectories(store).Select(System.IO.Path.GetFileName).OfType<string>()
                                               .Order(StringComparer.Ordinal).Where(k => !Firmwares.Contains(k)))
                Firmwares.Add(folder);
    }

    public string FakelibText
    {
        get
        {
            if (StoreDir is not { } store || TryName(SelectedFirmware) is null)
                return "No fakelib store";
            return FakelibStore.Find(store, SelectedFirmware) is { } folder
                ? $"Fakelibs: fakelibs/{System.IO.Path.GetFileName(folder)}"
                : $"No fakelib set for FW {SelectedFirmware}";
        }
    }

    [ObservableProperty] public partial bool KnownFixes { get; set; } = true;
    [ObservableProperty] public partial bool CandidateFixes { get; set; }
    [ObservableProperty] public partial bool StubUnknown { get; set; }
    [ObservableProperty] public partial bool DefineSymbol { get; set; }
    [ObservableProperty] public partial bool ExtendText { get; set; }
    [ObservableProperty] public partial bool PatchParam { get; set; }
    /// <summary>Uses the logging-enabled ampr_emu fakelib for one diagnostic run; never persisted.</summary>
    [ObservableProperty] public partial bool AmprDebugLogging { get; set; }
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildAmprIndexCommand))]
    public partial string OutputFolder { get; set; } = "";
    /// <summary>Optional patches (opt-in fix groups); reset when another game is opened.</summary>
    public ObservableCollection<OptionRow> PatchOptions { get; } = [];

    // --- run state ---------------------------------------------------------------------------
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(CookCommand), nameof(BuildAmprIndexCommand), nameof(OpenGameCommand))]
    public partial bool Busy { get; set; }

    [ObservableProperty] public partial string StatusText { get; set; } = "Open a game folder to start";
    [ObservableProperty] public partial string AmprIndexText { get; set; } = "Build after cooking if the release should include an AMPR index.";
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial OperationState OperationState { get; set; } = OperationState.Pending;
    [ObservableProperty] public partial int SelectedTab { get; set; }

    // --- results -----------------------------------------------------------------------------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(HasPsvr2), nameof(Psvr2Text))]
    public partial GameResult? Result { get; set; }

    public bool HasResult => Result is not null;
    /// <summary>The analysed game imports PS VR2 libraries (header badge).</summary>
    public bool HasPsvr2 => Result is { Psvr2.Count: > 0 };
    public string Psvr2Text => HasPsvr2
        ? "Uses PS VR2: " + string.Join(", ", Result!.Psvr2.Select(p => $"{p.Library} x{p.Imports} ({p.Module})")) +
          ". A VR-only game cannot run below the firmware that supports the headset; a game with an optional VR mode usually runs without it."
        : "";
    public ObservableCollection<ModuleRow> Modules { get; } = [];
    public ObservableCollection<ImportRow> Imports => _imports;
    public ObservableCollection<string> Warnings { get; } = [];
    public bool HasWarnings => Warnings.Count > 0;
    [ObservableProperty] public partial string ReportText { get; set; } = "";
    [ObservableProperty] public partial int ModuleCount { get; set; }
    [ObservableProperty] public partial int FixedCount { get; set; }
    [ObservableProperty] public partial int UnresolvedCount { get; set; }
    [ObservableProperty] public partial int DerivedCount { get; set; }
    [ObservableProperty] public partial string ReferenceText { get; set; } = "";

    [ObservableProperty] public partial ModuleRow? SelectedModule { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedImport))]
    public partial ImportRow? SelectedImport { get; set; }
    public bool HasSelectedImport => SelectedImport is not null;

    public string[] ImportFilters { get; } = ["Unresolved", "Changed", "All"];
    [ObservableProperty] public partial string ImportFilter { get; set; } = "Unresolved";
    [ObservableProperty] public partial string ImportSearch { get; set; } = "";
    [ObservableProperty] public partial string ImportCountText { get; set; } = "";

    partial void OnImportFilterChanged(string value) => ApplyImportFilter();
    partial void OnImportSearchChanged(string value) => ApplyImportFilter();
    partial void OnSelectedModuleChanged(ModuleRow? value) => ApplyImportFilter();
    partial void OnSelectedFirmwareChanged(string value)
    {
        if (Game is not null)
            OutputFolder = DefaultOutput(Game);
    }

    // --- commands -----------------------------------------------------------------------------
    bool CanOpenGame() => !Busy && !_closing;

    [RelayCommand(CanExecute = nameof(CanOpenGame))]
    async Task OpenGame()
    {
        if (PickFolder is null || await PickFolder("Select a decrypted game folder (contains eboot.bin)") is not { } folder)
            return;
        LoadGame(folder);
    }

    /// <summary>A folder (or a file in it, e.g. eboot.bin) dropped on the Backport page: open that game.</summary>
    public void DropGame(string path)
    {
        if (Busy)
        {
            Log.Warning("Wait for the current run to finish (or cancel it) before opening another game");
            return;
        }
        string folder = Directory.Exists(path) ? path : System.IO.Path.GetDirectoryName(path) ?? path;
        LoadGame(folder);
    }

    public void LoadGame(string folder)
    {
        if (Busy || _closing)
        {
            Log.Warning("Wait for the current run to finish (or cancel it) before opening another game");
            return;
        }
        try
        {
            var info = GameScan.ReadInfo(folder);
            Game = info;
            _analysis = null;
            Cover = null;
            try { Cover = info.IconPath is { } icon ? new Bitmap(icon) : null; }
            catch (Exception e) { Log.Warning($"Could not load the cover image: {e.Message}"); }
            OutputFolder = DefaultOutput(info);
            foreach (var option in PatchOptions)
                option.IsEnabled = false;
            AmprDebugLogging = false;
            AmprIndexText = "Build after cooking if the release should include an AMPR index.";
            ClearResults();
            StatusText = "Ready to analyze";
            Settings.Remember(folder);
            Log.Info($"Opened {info.TitleName} ({info.TitleId}, {info.Region}) from {folder}");
            if (!File.Exists(System.IO.Path.Combine(folder, GameScan.MainExecutable)))
                Log.Warning("No eboot.bin in this folder; is it the game root?");
        }
        catch (Exception e)
        {
            Log.Error($"Could not open {folder}: {e.Message}", exception: e);
        }
    }

    bool CanRun() => !Busy && !_closing && Game is not null;

    [RelayCommand(CanExecute = nameof(CanRun))]
    Task Analyze() => Run(dryRun: true);

    [RelayCommand(CanExecute = nameof(CanRun))]
    Task Cook() => Run(dryRun: false);

    [RelayCommand]
    void Cancel()
    {
        _operation?.Cancel();
        _cancel?.Cancel();
    }

    [RelayCommand]
    void OpenOutput()
    {
        if (Directory.Exists(OutputFolder))
            Process.Start(new ProcessStartInfo(OutputFolder) { UseShellExecute = true });
        else
            Log.Warning($"Output folder doesn't exist yet: {OutputFolder}");
    }

    bool CanBuildAmprIndex() => !Busy && !_closing && Game is not null && Directory.Exists(OutputFolder);

    /// <summary>Builds the AMPRIDX3 file map for the game plus its cooked overlay.</summary>
    [RelayCommand(CanExecute = nameof(CanBuildAmprIndex))]
    Task BuildAmprIndex() => CanBuildAmprIndex() ? _activeWork = BuildAmprIndexCore() : Task.CompletedTask;

    async Task BuildAmprIndexCore()
    {
        if (!CanBuildAmprIndex() || Game is not { } game)
            return;
        Busy = true;
        OperationState = OperationState.Running;
        Progress = 0;
        _cancel = new CancellationTokenSource();
        StatusText = "Building AMPR index…";
        string output = OutputFolder;
        string runId = Guid.NewGuid().ToString("N");
        var timer = Stopwatch.StartNew();
        Log.RunId = runId;
        _diagnostics?.Write(new("ampr-index", "started", "index", runId));
        try
        {
            var token = _cancel.Token;
            AmprIndexResult index = await Task.Run(() => AmprIndex.Build(game.Root, output, token), token);
            OperationState = OperationState.Completed;
            Progress = 100;
            AmprIndexText = $"AMPR index ready: {index.Entries:N0} files ({index.OverlayEntries:N0} from the overlay). Include ampr_emu.index in the release.";
            StatusText = "AMPR index built";
            Log.Info($"Built {index.Path}: {index.Entries:N0} /app0 entries, {index.OverlayEntries:N0} overlay entries");
        }
        catch (OperationCanceledException)
        {
            OperationState = OperationState.Cancelled;
            StatusText = "Cancelled";
            Log.Warning("AMPR index cancelled");
        }
        catch (Exception e)
        {
            OperationState = OperationState.Failed;
            StatusText = "AMPR index failed";
            Log.Error($"Could not build AMPR index: {e.Message}", exception: e);
        }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
            _diagnostics?.Write(new("ampr-index", OperationState.ToString().ToLowerInvariant(), "index", runId,
                DurationMilliseconds: timer.Elapsed.TotalMilliseconds));
            Log.RunId = null;
            ReportDiagnosticFailure();
            Busy = false;
        }
    }

    [RelayCommand]
    async Task CopyLog()
    {
        if (SetClipboard is not null)
            await SetClipboard(Log.Text());
    }

    [RelayCommand]
    void ClearLog() => Log.Clear();

    [RelayCommand]
    void ClearModuleFilter() => SelectedModule = null;

    /// <summary>Log entry clicked: show its module in the Modules tab.</summary>
    public void JumpTo(LogEntry? entry)
    {
        if (entry?.Module is not { } path || Modules.FirstOrDefault(m => m.Path == path) is not { } row)
            return;
        SelectedModule = row;
        SelectedTab = 1;
    }

    Task Run(bool dryRun) => Busy || _closing ? Task.CompletedTask : _activeWork = RunCore(dryRun);

    async Task RunCore(bool dryRun)
    {
        if (Busy || _closing) return;
        if (Game is not { } game || TryName(SelectedFirmware) is null)
        {
            Log.Error($"Pick a valid target firmware (e.g. 4 or 4.03), not '{SelectedFirmware}'");
            return;
        }
        Busy = true;
        Progress = 0;
        OperationState = OperationState.Running;
        string verb = dryRun ? "Analyzing" : "Cooking";
        StatusText = $"{verb}…";
        try
        {
            Log.Info($"{verb} {game.TitleName} for FW {Firmware.Name(SelectedFirmware)}{(dryRun ? " (dry run, nothing is written)" : $" -> {OutputFolder}")}");
            var options = new BackportOptions
            {
                Firmware = SelectedFirmware,
                ReferenceDir = FirmwareDbDir ?? Paths.FindUp(System.IO.Path.Combine(".original", "reference", "PS5-3.20_Libs")) ?? "firmware_db",
                FakelibStore = StoreDir,
                KnownFixes = KnownFixes, CandidateFixes = CandidateFixes, StubUnknown = StubUnknown,
                DefineSymbol = DefineSymbol, ExtendText = ExtendText, PatchParam = PatchParam,
                AmprDebugLogging = AmprDebugLogging,
                Options = PatchOptions.Where(o => o.IsEnabled).Select(o => o.Key).ToList(),
                Names = Names, DryRun = dryRun,
            };
            var knowledge = Knowledge;
            string output = dryRun ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ps5backport-analyze") : OutputFolder;
            using var operation = new BackportOperation(game.Root, output, options, knowledge, dryRun ? null : _analysis, _diagnostics);
            Log.RunId = operation.RunId;
            _operation = operation;
            if (dryRun) _analysis = null;
            var progress = new Progress<BackportProgress>(p =>
            {
                if (_closing || !ReferenceEquals(_operation, operation) || operation.State != OperationState.Running
                    || OperationState != OperationState.Running) return;
                Progress = p.Count == 0 ? 0 : Math.Min(99, 100.0 * p.Index / p.Count);
                if (p.Module.Length > 0) StatusText = $"{verb}: {p.Stage} {p.Module}";
            });
            var result = await operation.ExecuteAsync(progress);
            OperationState = result.State;
            Progress = result.State == OperationState.Completed ? 100 : 0;
            if (dryRun) _analysis = result.Analysis;
            Show(result, knowledge);
            int written = result.Modules.Count(m => m.Written);
            StatusText = dryRun ? result.State == OperationState.Completed
                ? $"Analyzed: {FixedCount} fixes planned, {UnresolvedCount} unresolved" : "Incomplete: analysis contains module errors"
                : result.Published ? $"Cooked: {written} files written" : "Incomplete: output not published";
            if (!dryRun && result.Published)
                AmprIndexText = "Overlay cooked. Build the AMPR index before packaging the release.";
            if (result.State == OperationState.Failed)
                Log.Error(dryRun ? "Analysis contains module errors; resolve them before cooking"
                    : $"Output left unchanged; incomplete-run reports: {result.DiagnosticDirectory}");
            else
                Log.Info(dryRun ? $"Analysis done: {ModuleCount} modules, {FixedCount} fixes, {UnresolvedCount} unresolved imports"
                                : $"Done: {written} files and the reports written to {output}");
            if (result.PreviousOutputBackup.Length > 0)
                Log.Info($"Previous output retained at {result.PreviousOutputBackup}");
        }
        catch (OperationCanceledException)
        {
            OperationState = OperationState.Cancelled;
            Progress = 0;
            StatusText = "Cancelled";
            Log.Warning("Cancelled");
        }
        catch (Exception e)
        {
            OperationState = OperationState.Failed;
            Progress = 0;
            StatusText = "Failed";
            Log.Error(e.Message, exception: e);
        }
        finally
        {
            _operation = null;
            Log.RunId = null;
            ReportDiagnosticFailure();
            Busy = false;
        }
    }

    void Show(GameResult r, Knowledge k)
    {
        ClearResults();
        Result = r;
        ReferenceText = r.Reference;
        var fixes = k.FixTable(r.Options);
        foreach (string w in r.ParamWarnings)
        {
            Warnings.Add(w);
            Log.Warning(w);
        }
        foreach (var m in r.Modules)
        {
            Modules.Add(new ModuleRow(m));
            if (m.Error.Length > 0)
                Log.Error(m.Error, m.Path);
            if (m.TextExtended != 0)
                Log.Warning($"code segment extended by 0x{m.TextExtended:x} bytes (not console-tested)", m.Path);
            var unresolved = m.Unknown.ToDictionary(u => u.Nid);
            var applied = m.Fixed.ToDictionary(f => f.Nid, f => f.How);
            foreach (var s in m.StubbedNow)
                applied.TryAdd(s.Nid, $"{s.Stub} @ {s.At} ({s.Origin})");
            foreach (var s in m.Statuses)
            {
                unresolved.TryGetValue(s.Nid, out var u);
                _allImports.Add(new ImportRow
                {
                    Module = m.Path, Nid = s.Nid, Name = Names.Name(s.Nid), Library = s.Library, Status = s.Status, Plt = s.Plt,
                    Refs = u?.CodeRefs, Candidate = u?.Candidate ?? "", LeftByCount = k.LeftUnpatched.GetValueOrDefault(s.Nid)?.Count ?? 0, Introduced = u?.Introduced ?? "",
                    Applied = applied.GetValueOrDefault(s.Nid, ""), Fix = fixes.GetValueOrDefault(s.Nid),
                });
            }
        }
        ModuleCount = r.Modules.Count;
        FixedCount = r.Modules.Sum(m => m.Fixed.Count + m.StubbedNow.Count);
        UnresolvedCount = r.Modules.Sum(m => m.Unknown.Count);
        var derived = r.Modules.SelectMany(m => m.Fixed).Where(f => !f.Proven).Select(f => f.Name)
            .Union(r.Modules.SelectMany(m => m.StubbedNow).Where(s => s.Origin.Contains("derived")).Select(s => s.Name)).ToList();
        DerivedCount = derived.Count;
        if (derived.Count > 0)
            Log.Warning($"{derived.Count} fix(es) not yet console-tested: {string.Join(", ", derived)}");
        OnPropertyChanged(nameof(HasWarnings));
        ReportText = Pipeline.FormatReport(r);
        ApplyImportFilter();
    }

    void ClearResults()
    {
        Result = null;
        Modules.Clear();
        Imports.Clear();
        Warnings.Clear();
        OnPropertyChanged(nameof(HasWarnings));
        _allImports = [];
        ReportText = "";
        ModuleCount = FixedCount = UnresolvedCount = DerivedCount = 0;
        SelectedImport = null;
        SelectedModule = null;
    }

    void ApplyImportFilter()
    {
        IEnumerable<ImportRow> rows = _allImports;
        if (SelectedModule is { } module)
            rows = rows.Where(r => r.Module == module.Path);
        rows = ImportFilter switch
        {
            "Unresolved" => rows.Where(r => Core.Imports.IsUnresolved(r.Status)),
            "Changed" => rows.Where(r => r.Applied.Length > 0),
            _ => rows,
        };
        if (ImportSearch.Trim() is { Length: > 0 } q)
            rows = rows.Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Nid.Contains(q, StringComparison.Ordinal)
                                   || r.Library.Contains(q, StringComparison.OrdinalIgnoreCase));
        var selection = SelectedImport;
        _imports.ReplaceWith(rows);
        SelectedImport = selection is not null && Imports.Contains(selection) ? selection : null;
        ImportCountText = $"{Imports.Count} of {_allImports.Count} imports" + (SelectedModule is null ? "" : $" in {SelectedModule.Path}");
    }

    // --- helpers -------------------------------------------------------------------------------
    Knowledge Knowledge => _knowledge ??= new Knowledge();
    internal Knowledge KnowledgeData => Knowledge;
    internal string FunctionName(string nid) => Names.Name(nid);

    NameDb Names => _names ??= AerolibPath is { } aerolib && File.Exists(aerolib)
        ? new NameDb(aerolib) : new NameDb();

    string DefaultOutput(GameInfo g)
    {
        string parent = System.IO.Path.GetDirectoryName(System.IO.Path.TrimEndingDirectorySeparator(g.Root)) ?? g.Root;
        string id = g.TitleId.Length > 0 ? g.TitleId : System.IO.Path.GetFileName(g.Root);
        return System.IO.Path.Combine(parent, $"{id}_backport_{TryName(SelectedFirmware) ?? SelectedFirmware}");
    }

    static string? TryName(string? firmware)
    {
        try { return firmware is null ? null : Firmware.Name(firmware); }
        catch (FormatException) { return null; }
    }

    static double Version(string name) => Firmware.Parse(name) is var (major, minor) ? major + minor / 100.0 : 0;
}
