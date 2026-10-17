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
        CodeGraph = new CodeGraphViewModel(this);
        Results = new ConsoleResultsViewModel(this);
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
            PatchOptions.Add(new OptionRow(option, row => SelectedPatchOption = row));
          foreach (var option in PatchOptions)
              option.PropertyChanged += (_, e) =>
              {
                  if (e.PropertyName == nameof(OptionRow.IsEnabled))
                  {
                      if (option.IsEnabled && option.Key is "auto_pro" or "force_pro")
                          foreach (var other in PatchOptions.Where(o => o.Key == (option.Key == "auto_pro" ? "force_pro" : "auto_pro")))
                              other.IsEnabled = false;
                      if (option.IsEnabled && option.Key is "playgo_0_6" or "playgo_0_6_log")
                          foreach (var other in PatchOptions.Where(o => o.Key == (option.Key == "playgo_0_6" ? "playgo_0_6_log" : "playgo_0_6")))
                              other.IsEnabled = false;
                      OnPropertyChanged(nameof(PatchOptionsSummary));
                      if (option.Key == BootNoticeOption)
                          OnPropertyChanged(nameof(IsBootNoticeEnabled));
                      if (!_applyingProbe) MarkAnalysisStale();
                  }
              };
          SelectedPatchOption = PatchOptions.FirstOrDefault();
        Log.Info($"PS5 Backport Studio ready. Firmware DB: {FirmwareDbDir ?? "not found"}; fakelib store: {StoreDir ?? "not found"}");
        ReportDiagnosticFailure();
    }

    public AppSettings Settings { get; }
    public StoreViewModel Store { get; }
    public FirmwareDbViewModel Database { get; }
    public ToolsViewModel Tools { get; }
    public CodeGraphViewModel CodeGraph { get; }
    public ConsoleResultsViewModel Results { get; }
    /// <summary>Console evidence of the displayed run's applied items (GameResult.ConsoleEvidence), with its failures and
    /// promotion candidates.</summary>
    public ObservableCollection<ResultItemRow> ConsoleEvidence { get; } = [];
    public ObservableCollection<string> ConsoleEvidenceNotes { get; } = [];
    public bool HasConsoleEvidence => ConsoleEvidence.Count > 0;

    [RelayCommand]
    void OpenConsoleResults() => Page = AppPage.Results;

    /// <summary>Loading of the current page, when it loads in the background (the firmware database).</summary>
    public Task PageLoad { get; private set; } = Task.CompletedTask;

    /// <summary>The page shown next to the navigation rail.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGameBackground), nameof(RailDocked), nameof(RailVisible), nameof(ContentMargin))]
    public partial AppPage Page { get; set; }

    /// <summary>The navigation rail sits beside the page on every page but the code graph, which wants the width: there it
    /// hides and slides over the page on demand (the ≡ button, the left edge, Ctrl+B) until a page is picked or it is left.</summary>
    public bool RailDocked => Page != AppPage.CodeGraph;
    /// <summary>The rail is shown over the code graph page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RailVisible))]
    public partial bool RailRevealed { get; set; }
    public bool RailVisible => RailDocked || RailRevealed;
    /// <summary>Room for the docked rail.</summary>
    public Avalonia.Thickness ContentMargin => RailDocked ? new Avalonia.Thickness(64, 0, 0, 0) : default;

    [RelayCommand]
    void ToggleRail() => RailRevealed = !RailDocked && !RailRevealed;

    partial void OnPageChanged(AppPage value)
    {
        RailRevealed = false;
        if (value == AppPage.Settings)
            OpenSettings();
        else if (value == AppPage.Store)
            Store.Refresh();
        else if (value == AppPage.FirmwareDb && Database.Firmwares.Count == 0)
            PageLoad = Database.Refresh();
        else if (value == AppPage.Tools)
            PageLoad = Tools.Open();
        else if (value == AppPage.Results)
            Results.Refresh();
    }

    /// <summary>Folders and files the app uses: the Settings override, else the ones found next to the app.</summary>
    public string? FirmwareDbDir => Settings.FirmwareDbDir ?? Paths.FirmwareDbDir;
    public string? StoreDir => Settings.FakelibStoreDir ?? Paths.FakelibStoreDir;
    public string? AerolibPath => Settings.AerolibPath
        ?? Paths.AerolibPath;

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
    /// <summary>Set by the window: a save dialog (title, suggested file name, extension); the chosen path or null.</summary>
    public Func<string, string, string, Task<string?>>? PickSaveFile { get; set; }

    // --- game ------------------------------------------------------------------------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGame), nameof(GameTitle), nameof(GameLine), nameof(GameSdkText), nameof(GapPercent))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(CookCommand), nameof(BuildAmprIndexCommand))]
    public partial GameInfo? Game { get; set; }

    partial void OnGameChanged(GameInfo? value) => CodeGraph.MainChanged();

    [ObservableProperty] public partial Bitmap? Cover { get; set; }

    partial void OnCoverChanged(Bitmap? oldValue, Bitmap? newValue) => oldValue?.Dispose();

    /// <summary>The selected game's sce_sys/pic0.png artwork.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGameBackground))]
    public partial Bitmap? GameBackground { get; set; }

    /// <summary>Shows artwork only on the game workbench.</summary>
    public bool HasGameBackground => Page == AppPage.Backport && GameBackground is not null;

    partial void OnGameBackgroundChanged(Bitmap? oldValue, Bitmap? newValue) => oldValue?.Dispose();

    /// <summary>Releases game images and cancels any work when the window closes.</summary>
    public void Dispose()
    {
        _closing = true;
        Cancel();
        Cover = null;
        GameBackground = null;
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
    public string GameSdkText => Game is null ? "" : Game.SdkVersion.Length > 0 ? $"Game SDK {Game.SdkVersion}" : "Game SDK unknown";

    // --- target and options ----------------------------------------------------------------
    public ObservableCollection<string> Firmwares { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetText), nameof(GapPercent), nameof(FakelibText))]
    public partial string SelectedFirmware { get; set; }

    public string TargetText => TryName(SelectedFirmware) is { } fw ? $"Target FW {fw}" : "Target firmware?";

    /// <summary>The target firmware compared with the declared game SDK (0-100, not a compatibility verdict).</summary>
    public double GapPercent
    {
        get
        {
            if (Game is not { SdkVersion.Length: > 0 } g || TryName(SelectedFirmware) is not { } target)
                return 0;
            double sdk = Version(g.SdkVersion), have = Version(target);
            return sdk <= 0 ? 0 : Math.Clamp(have / sdk * 100, 0, 100);
        }
    }

    /// <summary>Adds the store's sets (e.g. "4.03") to the firmware picker.</summary>
    internal void AddStoreFirmwares()
    {
        if (StoreDir is { } store && Directory.Exists(store))
            foreach (string folder in Directory.EnumerateDirectories(store).Select(System.IO.Path.GetFileName).OfType<string>()
                                               .Where(k => !k.StartsWith(Publication.Prefix, StringComparison.OrdinalIgnoreCase))   // transaction/scratch folders
                                               .Order(StringComparer.Ordinal).Where(k => !Firmwares.Contains(k)))
                Firmwares.Add(folder);
    }

    public string FakelibText
    {
        get
        {
            if (StoreDir is not { } store || TryName(SelectedFirmware) is null)
                return "No compatibility library folder";
            return FakelibStore.Find(store, SelectedFirmware) is { } folder
                ? $"Compatibility libraries: FW {System.IO.Path.GetFileName(folder)}"
                : $"No compatibility libraries for FW {SelectedFirmware}";
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
      public string PatchOptionsSummary => $"Optional patches · {PatchOptions.Count} available · {PatchOptions.Count(o => o.IsEnabled)} enabled"
          + (PatchOptions.Any(o => o.HasVerdict) ? $" · probe: {ProbeSummary}" : "");
      /// <summary>"1 needed, 3 applicable, 5 console choices, 6 not applicable" for the current verdicts.</summary>
      public string ProbeSummary => string.Join(", ", new[]
      {
          (PatchOptions.Count(o => o.IsNeeded), "needed"), (PatchOptions.Count(o => o.IsApplicable), "applicable"),
          (PatchOptions.Count(o => o.IsConsoleChoice), "console choice(s)"), (PatchOptions.Count(o => o.IsNotApplicable), "not applicable"),
      }.Select(c => $"{c.Item1} {c.Item2}"));
      [ObservableProperty] public partial OptionRow? SelectedPatchOption { get; set; }
      [ObservableProperty]
      [NotifyPropertyChangedFor(nameof(VisiblePatchOptions), nameof(VisiblePatchGroups))]
      public partial string PatchSearch { get; set; } = "";
      /// <summary>The visible patches by area, in data/fixes.json "option_groups" order; empty areas are left out.</summary>
      public IReadOnlyList<PatchGroup> VisiblePatchGroups => VisiblePatchOptions
          .GroupBy(o => o.Group, StringComparer.Ordinal)
          .OrderBy(g => GroupRank(g.Key))
          .Select(g => new PatchGroup(g.Key, g.ToList())).ToList();
      int GroupRank(string group)
      {
          int rank = Knowledge.OptionGroups.ToList().IndexOf(group);
          return rank < 0 ? int.MaxValue : rank;
      }
      partial void OnSelectedPatchOptionChanged(OptionRow? oldValue, OptionRow? newValue)
      {
          if (oldValue is not null) oldValue.IsSelected = false;
          if (newValue is not null) newValue.IsSelected = true;
      }
      const string BootNoticeOption = "boot_notice";
      /// <summary>The boot notification message box is shown only while that patch is enabled.</summary>
      public bool IsBootNoticeEnabled => PatchOptions.Any(o => o.Key == BootNoticeOption && o.IsEnabled);
      public IEnumerable<OptionRow> VisiblePatchOptions => PatchOptions.Where(o =>
          o.Name.Contains(PatchSearch.Trim(), StringComparison.OrdinalIgnoreCase) ||
          o.Description.Contains(PatchSearch.Trim(), StringComparison.OrdinalIgnoreCase));
      partial void OnPatchSearchChanged(string value)
      {
          if (SelectedPatchOption is null || !VisiblePatchOptions.Contains(SelectedPatchOption))
              SelectedPatchOption = VisiblePatchOptions.FirstOrDefault();
      }

    /// <summary>Message text for the "Boot notification" patch; empty = default. Applied only when that patch is enabled.</summary>
    [ObservableProperty] public partial string BootNoticeMessage { get; set; } = "";

    // --- run state ---------------------------------------------------------------------------
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(CookCommand), nameof(BuildAmprIndexCommand), nameof(OpenGameCommand), nameof(BuildPublishedAmprIndexCommand))]
    public partial bool Busy { get; set; }

    [ObservableProperty] public partial string StatusText { get; set; } = "Open a game folder to start";
    [ObservableProperty] public partial string AmprIndexText { get; set; } = "Build after creating the backport if the release needs an AMPR index.";

    /// <summary>ampr_emu loads a valid ampr_emu.index as the complete /app0 file list (FINDINGS 6b2).</summary>
    public const string AmprIndexWarning =
        "Warning: ampr_emu treats ampr_emu.index as the complete list of /app0 files: a file missing from it is " +
        "\"not found\" on the console. Build it from exactly the files the console will have (this game folder plus " +
        "the overlay) and rebuild it after any change. Without an index, ampr_emu scans all of /app0 at the game's first " +
        "file access and the game waits on its splash screen meanwhile; on a large game this takes minutes.";

    /// <summary>Bound by the AMPR index sections of the window.</summary>
    public string AmprIndexWarningText => AmprIndexWarning;
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
    [NotifyCanExecuteChangedFor(nameof(ShowInCodeGraphCommand))]
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
            GameBackground = null;
            try { GameBackground = info.BackgroundPath is { } background ? new Bitmap(background) : null; }
            catch (Exception e) { Log.Warning($"Could not load the background image: {e.Message}"); }
            OutputFolder = DefaultOutput(info);
            foreach (var option in PatchOptions)
            {
                option.IsEnabled = false;
                option.Verdict = "";
                option.Evidence = "";
            }
            OnPropertyChanged(nameof(PatchOptionsSummary));
              PatchSearch = "";
            AmprDebugLogging = false;
            AmprIndexText = "Build after creating the backport if the release needs an AMPR index.";
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

    /// <summary>Cancels the requirements probe that runs before the analysis operation exists.</summary>
    CancellationTokenSource? _probeCancel;

    [RelayCommand]
    void Cancel()
    {
        _probeCancel?.Cancel();
        _operation?.Cancel();
        _cancel?.Cancel();
    }

    [RelayCommand]
    void OpenOutput()
    {
        if (Directory.Exists(OutputFolder))
        {
            using var process = Process.Start(new ProcessStartInfo(OutputFolder) { UseShellExecute = true });
        }
        else
            Log.Warning($"Output folder doesn't exist yet: {OutputFolder}");
    }

    bool CanBuildAmprIndex() => !Busy && !_closing && Game is not null && Directory.Exists(OutputFolder);

    /// <summary>Builds the AMPRIDX3 file map for the game plus its cooked overlay.</summary>
    [RelayCommand(CanExecute = nameof(CanBuildAmprIndex))]
    Task BuildAmprIndex() => CanBuildAmprIndex() ? _activeWork = BuildAmprIndexCore(OutputFolder) : Task.CompletedTask;

    async Task BuildAmprIndexCore(string output)
    {
        if (Busy || _closing || Game is not { } game || !Directory.Exists(output))
            return;
        Busy = true;
        OperationState = OperationState.Running;
        Progress = 0;
        _cancel = new CancellationTokenSource();
        StatusText = "Building AMPR index…";
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
            AmprIndexText = $"AMPR index ready: {index.Entries:N0} files ({index.OverlayEntries:N0} from the overlay). " +
                            "Include ampr_emu.index only if the console's /app0 holds exactly these files.";
            StatusText = "AMPR index built";
            Log.Info($"Built {index.Path}: {index.Entries:N0} /app0 entries, {index.OverlayEntries:N0} overlay entries");
            Log.Warning(AmprIndexWarning);
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

    bool CanShowInCodeGraph() => SelectedImport is { IsEboot: true } && Game is not null;

    /// <summary>Imports tab: opens the Code graph page on the functions of this game's eboot that call the selected import.</summary>
    [RelayCommand(CanExecute = nameof(CanShowInCodeGraph))]
    Task ShowInCodeGraph()
    {
        if (SelectedImport is not { IsEboot: true } row)
            return Task.CompletedTask;
        Page = AppPage.CodeGraph;
        return CodeGraph.ShowCallers(row.Nid);
    }

    /// <summary>Code graph: shows the eboot's import <paramref name="nid"/> in the Imports tab (filters cleared, found by name).</summary>
    internal void ShowImport(string nid)
    {
        if (_allImports.FirstOrDefault(r => r.Nid == nid && r.IsEboot) is not { } row)
        {
            Log.Warning($"{Names.Name(nid)} is not among {GameScan.MainExecutable}'s imports in the Backport page's analysis");
            return;
        }
        ImportFilter = "All";
        SelectedModule = null;
        ImportSearch = row.Name;
        SelectedImport = row;
        Page = AppPage.Backport;
        SelectedTab = ImportsTab;
    }

    const int ImportsTab = 2;

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
        int presentationRevision = _presentationRevision;
        IsAnalysisStale = HasResult;
        string verb = dryRun ? "Analyzing" : "Creating backport";
        StatusText = $"{verb}…";
        try
        {
            Log.Info($"{verb} {game.TitleName} for FW {Firmware.Name(SelectedFirmware)}{(dryRun ? " (dry run, nothing is written)" : $" -> {OutputFolder}")}");
            // progress reports are posted to the caller's synchronization context: capture it before the first await
            BackportOperation? started = null;
            var progress = new Progress<BackportProgress>(p =>
            {
                if (_closing || started is null || !ReferenceEquals(_operation, started) || started.State != OperationState.Running
                    || OperationState != OperationState.Running) return;
                Progress = p.Count == 0 ? 0 : Math.Min(99, 100.0 * p.Index / p.Count);
                if (p.Module.Length > 0) StatusText = $"{verb}: {p.Stage} {p.Module}";
            });
            if (dryRun)
            {
                // the requirements probe runs first, so a pre-ticked "needed" option is part of the analysis that follows
                // (the pre-tick itself does not count as a settings change made during the run); Cancel stops it too
                StatusText = "Probing optional patches…";
                var knowledgeForProbe = Knowledge;
                using var probeCancel = new CancellationTokenSource();
                _probeCancel = probeCancel;
                List<OptionRecommendation> recommendations;
                try { recommendations = await Task.Run(() => Pipeline.Probe(game.Root, knowledgeForProbe, probeCancel.Token)); }
                finally { _probeCancel = null; }
                probeCancel.Token.ThrowIfCancellationRequested();
                if (_closing) throw new OperationCanceledException();
                ApplyProbe(recommendations, preselectNeeded: true);
            }
            var options = RunOptions(dryRun);
            var knowledge = Knowledge;
            string output = OutputFolder; // a dry run writes nothing there
            using var operation = new BackportOperation(game.Root, output, options, knowledge, dryRun ? null : _analysis, _diagnostics);
            Log.RunId = operation.RunId;
            _operation = operation;
            started = operation;
            if (dryRun) _analysis = null;
            var result = await operation.ExecuteAsync(progress);
            OperationState = result.State;
            Progress = result.State == OperationState.Completed ? 100 : 0;
            if (dryRun) _analysis = result.Analysis;
            Show(result, knowledge);
            IsAnalysisStale = presentationRevision != _presentationRevision;
            int written = result.Modules.Count(m => m.Written);
            StatusText = dryRun ? result.State == OperationState.Completed
                ? $"Analyzed: {FixedCount} fixes planned, {UnresolvedCount} unresolved" : "Incomplete: analysis contains module errors"
                : result.Published ? $"Backport created: {written} files written" : "Incomplete: output not published";
            if (!dryRun && result.Published)
                AmprIndexText = "Backport created. Build the AMPR index from the same game folder you copy to the console; without it, ampr_emu scans /app0 at first start.";
            if (result.State == OperationState.Failed)
                Log.Error(dryRun ? "Analysis contains module errors; resolve them before cooking"
                    : $"Output left unchanged; incomplete-run reports: {result.DiagnosticDirectory}");
            else
                Log.Info(dryRun ? $"Analysis done: {ModuleCount} modules, {FixedCount} fixes, {UnresolvedCount} unresolved imports"
                                : $"Done: {written} files and the reports written to {output}");
            if (result.PreviousOutputBackup.Length > 0)
                Log.Warning($"Previous output could not be deleted (a file may be open); remove it manually: {result.PreviousOutputBackup}");
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

    /// <summary>Shows the probe's verdicts on the option rows; with <paramref name="preselectNeeded"/> ticks the "needed" ones
    /// (never unticks anything) and logs what it did.</summary>
    bool _applyingProbe;

    void ApplyProbe(IReadOnlyList<OptionRecommendation> recommendations, bool preselectNeeded)
    {
        var byKey = recommendations.ToDictionary(p => p.Key, StringComparer.Ordinal);
        var preselected = new List<OptionRow>();
        _applyingProbe = true;
        try
        {
            foreach (var option in PatchOptions)
            {
                if (!byKey.TryGetValue(option.Key, out var verdict)) continue;
                option.Verdict = Pipeline.Verdict(verdict.Verdict);
                option.Evidence = verdict.Evidence;
                if (preselectNeeded && verdict.Verdict == OptionVerdict.Needed && !option.IsEnabled)
                {
                    option.IsEnabled = true;
                    preselected.Add(option);
                }
            }
        }
        finally { _applyingProbe = false; }
        OnPropertyChanged(nameof(PatchOptionsSummary));
        if (preselectNeeded)
        {
            Log.Info($"Probe: {ProbeSummary} (static evidence from the game's modules, not console compatibility)");
            foreach (var option in preselected)
                Log.Info($"Probe: pre-selected {option.Name} — {option.Evidence}");
        }
    }

    void Show(GameResult r, Knowledge k)
    {
        ClearResults();
        Result = r;
        ReferenceText = r.Reference;
        ApplyProbe(r.Recommendations, preselectNeeded: false);
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
            if (m.LaterMissing.Count > 0)
                Log.Warning($"{m.LaterMissing.Count} import(s) resolve on FW {r.TargetFirmware} but are not exported by a later firmware: " +
                            string.Join(", ", m.LaterMissing.Select(l => $"{l.Name} (not on FW {l.MissingOn})")), m.Path);
            var unresolved = m.Unknown.ToDictionary(u => u.Nid);
            var applied = m.Fixed.ToDictionary(f => f.Nid, f => f.How);
            var nativeFrom = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var s in m.StubbedNow)
            {
                applied.TryAdd(s.Nid, $"{s.Stub} @ {s.At} ({s.Origin})");
                if (s.Introduced.Length > 0)
                    nativeFrom.TryAdd(s.Nid, s.Introduced);
            }
            foreach (var s in m.Statuses)
            {
                unresolved.TryGetValue(s.Nid, out var u);
                _allImports.Add(new ImportRow
                {
                    Module = m.Path, Nid = s.Nid, Name = Names.Name(s.Nid), Library = s.Library, Status = s.Status, Plt = s.Plt,
                    Refs = u?.CodeRefs, Result = u?.Result ?? "", Candidate = u?.Candidate ?? "", LeftByCount = k.LeftUnpatched.GetValueOrDefault(s.Nid)?.Count ?? 0, Introduced = u?.Introduced ?? nativeFrom.GetValueOrDefault(s.Nid, ""),
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
        foreach (var e in r.ConsoleEvidence)
        {
            ConsoleEvidence.Add(new ResultItemRow(e, ConsoleResults.Label(e.Item, k)));
            foreach (string failure in e.Failures)
                ConsoleEvidenceNotes.Add($"⚠ {e.Item} failed on {failure}");
            if (e.Candidate)
                ConsoleEvidenceNotes.Add($"★ {e.Item} passed on every firmware band with no failure: promotion candidate (your decision, on the Console results page)");
        }
        OnPropertyChanged(nameof(HasConsoleEvidence));
        ReportText = Pipeline.FormatReport(r, k);
        ApplyImportFilter();
        NotifyResultPresentation();
    }

    void ClearResults()
    {
        Result = null;
        Modules.Clear();
        Imports.Clear();
        Warnings.Clear();
        OnPropertyChanged(nameof(HasWarnings));
        ConsoleEvidence.Clear();
        ConsoleEvidenceNotes.Clear();
        OnPropertyChanged(nameof(HasConsoleEvidence));
        _allImports = [];
        ReportText = "";
        ModuleCount = FixedCount = UnresolvedCount = DerivedCount = 0;
        SelectedImport = null;
        SelectedModule = null;
        IsAnalysisStale = false;
        ImportCountText = "";
        NotifyResultPresentation();
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
        OnPropertyChanged(nameof(HasImports));
        OnPropertyChanged(nameof(ImportTabText));
        OnPropertyChanged(nameof(ImportsEmptyText));
    }

    // --- helpers -------------------------------------------------------------------------------
    Knowledge Knowledge => _knowledge ??= new Knowledge();
    internal Knowledge KnowledgeData => Knowledge;
    internal string FunctionName(string nid) => Names.Name(nid);
    internal NameDb NameData => Names;

    /// <summary>The run options of the Backport page (target firmware, store, fixes, enabled options); the code graph's cook
    /// preview uses the same.</summary>
    internal BackportOptions RunOptions(bool dryRun) => new()
    {
        Firmware = SelectedFirmware,
        ReferenceDir = FirmwareDbDir ?? Paths.FindUp(System.IO.Path.Combine(".original", "reference", "PS5-3.20_Libs")) ?? "firmware_db",
        FakelibStore = StoreDir,
        KnownFixes = KnownFixes, CandidateFixes = CandidateFixes, StubUnknown = StubUnknown,
        DefineSymbol = DefineSymbol, ExtendText = ExtendText, PatchParam = PatchParam,
        AmprDebugLogging = AmprDebugLogging,
        Options = PatchOptions.Where(o => o.IsEnabled).Select(o => o.Key).ToList(),
        BootNoticeMessage = BootNoticeMessage,
        Names = Names, DryRun = dryRun,
        ConsoleResults = Results.ForCook(),
    };

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
