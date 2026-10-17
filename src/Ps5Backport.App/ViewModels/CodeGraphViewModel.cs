using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ps5Backport.Core;

namespace Ps5Backport.App.ViewModels;

public enum FunctionFilter { All, Changed, Fakelib, Missing, Patterns }

/// <summary>Graph: the function's blocks and edges; Listing: its blocks in address order; Calls: the functions around it.</summary>
public enum CodeView { Graph, Listing, Calls }

/// <summary>A row of the function list. Chip: patched, stub, plt, changed, missing, fakelib, entry or empty.</summary>
public sealed record FunctionRow(FunctionRange Range, string Name, string Detail, string Chip)
{
    public bool HasChip => Chip.Length > 0;
    public bool IsChange => Chip is "patched" or "stub" or "plt" or "changed" or "modified" or "new";
}

/// <summary>One change in the selected function: the original and backport code and bytes.</summary>
public sealed record ChangeRow(string Title, string Kind, string Chip, string Before, string BeforeBytes, string After, string AfterBytes, string Note)
{
    public bool HasNote => Note.Length > 0;
}

/// <summary>One bar of the cave histogram: caves with exactly <paramref name="Size"/> usable bytes.</summary>
public sealed record CaveBar(int Size, int Count, double Width)
{
    public string Label => $"{Size,2} B";
    public string CountText => Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A string or pointer the function uses: Target opens the function it points to; Search lists the functions using
/// that string.</summary>
public sealed record DataRow(string Text, string Detail, ulong? Target, string Search)
{
    public bool CanOpen => Target is not null || Search.Length > 0;
}

/// <summary>A module the view left to follow a call into another (a fakelib or game module): what Back restores.</summary>
sealed record ModuleFrame(string Eboot, string Compare, bool Preview, string PreviewFirmware, CodeImage View, CodeImage? Original,
                          BackportDiff? Diff, VersionDiff? Versions, CallIndex? Calls, FunctionRange? Current, FunctionRange[] History);

/// <summary>One line of the crash section: an event header (no target) or a mapped address.</summary>
public sealed record CrashRow(string Title, string Detail, ulong? Target, bool Header = false)
{
    public bool CanOpen => Target is not null;
}

/// <summary>A call, caller or redirected import; Target opens that function; Rule: what data/fixes.json does with it;
/// Status: "missing" or "unknown" on the checked firmware; Nid: the import (as the original eboot names it) for the Imports tab.</summary>
public sealed record LinkRow(string Name, string Detail, ulong? Target, bool Fakelib = false, string Rule = "", string Status = "",
                             string Arguments = "", string Nid = "")
{
    public bool CanOpen => Target is not null;
    public bool HasNid => Nid.Length > 0;
    public bool HasRule => Rule.Length > 0;
    public bool HasStatus => Status.Length > 0;
    public bool HasArguments => Arguments.Length > 0;
}

/// <summary>
/// The Code graph page: one eboot's functions as IDA/Ghidra-style graphs, optionally compared with a backport of it
/// (patched instructions, stubs in int3 padding, redirected imports, calls into fakelibs). Read-only: no file is written.
/// </summary>
public sealed partial class CodeGraphViewModel(MainViewModel main) : ObservableObject
{
    CodeImage? _view, _original;
    BackportDiff? _diff;
    /// <summary>When the compared file is another build (not a backport of this one): the function matching (VersionMatch).</summary>
    VersionDiff? _versions;
    CallIndex? _calls;
    Dictionary<ulong, SortedSet<string>> _fakelibCalls = [];
    /// <summary>{first piece: all pieces} of each stub chain of the comparison.</summary>
    Dictionary<ulong, IReadOnlyList<FunctionRange>> _chains = [];
    List<FirmwareExports>? _databases;
    List<CrashEvent> _crash = [];
    /// <summary>{end of a `ret`: usable bytes of the cave right after it} in the shown eboot.</summary>
    Dictionary<ulong, long> _caveAfter = [];
    /// <summary>Where data/fixes.json patterns and the recognisers match (the original when comparing).</summary>
    List<DetectorMatch> _matches = [];
    /// <summary>Crash marks: {instruction address: label} for faulting instructions, {end address: label} for the calls a
    /// backtrace return address follows.</summary>
    Dictionary<ulong, string> _crashAt = [], _crashReturn = [];
    ImportCheck? _check;
    /// <summary>{function start: names of the unresolved imports it calls} on the checked firmware.</summary>
    Dictionary<ulong, SortedSet<string>> _missingCalls = [];
    int _checkVersion;
    List<FunctionRow> _all = [];
    /// <summary>The function list's row of each start (call-graph nodes show its detail and chip).</summary>
    Dictionary<ulong, FunctionRow> _rowByStart = [];
    FunctionRange? _current;
    readonly Stack<FunctionRange> _history = new(), _forward = new();
    const int RecentLimit = 20;
    int _loadVersion, _graphVersion;
    bool _navigating;
    /// <summary>The import the Imports tab asked about: graphs open at its first call. Cleared when the search changes.</summary>
    string? _focusNid;
    /// <summary>The modules left by following calls (the eboot at the bottom); Back at the start of a module's history returns.</summary>
    readonly Stack<ModuleFrame> _modules = new();
    /// <summary>Set for the reload that returns to a frame (it reuses the frame's images and call scan), or that follows a call.</summary>
    ModuleFrame? _restore;
    bool _following;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EbootText), nameof(EbootCrumb), nameof(CanOpenImports))]
    public partial string EbootPath { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompareText), nameof(CompareCrumb), nameof(HasComparePath))]
    public partial string ComparePath { get; set; } = "";

    /// <summary>Compare with the backport Studio would make (a dry run with the Backport page's settings) instead of a file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompareText), nameof(CompareCrumb), nameof(HasComparePath))]
    public partial bool ComparePreview { get; set; }
    string _previewFirmware = "";

    // --- the compact layout: command-bar crumbs and the side panels ---------------------------------------
    /// <summary>The last three path segments ("PPSA01317/default/eboot.bin") for the command bar; the full path is the tooltip.</summary>
    static string Crumb(string path, string empty) =>
        path.Length == 0 ? empty : string.Join('/', path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).TakeLast(3));
    public string EbootCrumb => Crumb(EbootPath, "No eboot");
    public string CompareCrumb => ComparePreview ? $"Studio's cook · FW {(_previewFirmware.Length > 0 ? _previewFirmware : main.SelectedFirmware)}" : Crumb(ComparePath, "none");
    public string CrashCrumb => Crumb(CrashPath, "none");

    /// <summary>The function list and the inspector can be put away for the graph (‹ / › on each, F11 for both).</summary>
    [ObservableProperty] public partial bool ListVisible { get; set; } = true;
    [ObservableProperty] public partial bool InspectorVisible { get; set; } = true;

    [RelayCommand] void ToggleList() => ListVisible = !ListVisible;
    [RelayCommand] void ToggleInspector() => InspectorVisible = !InspectorVisible;
    /// <summary>F11: both panels away, or both back.</summary>
    [RelayCommand]
    void ToggleFocus()
    {
        bool show = !(ListVisible || InspectorVisible);
        ListVisible = InspectorVisible = show;
    }

    [ObservableProperty] public partial bool Busy { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial string StatusTone { get; set; } = "muted";
    [ObservableProperty] public partial string SummaryText { get; set; } = "";
    [ObservableProperty] public partial string PatchedText { get; set; } = "";
    /// <summary>The command bar's chips: the counts alone; the long texts are their tooltips.</summary>
    [ObservableProperty] public partial string FunctionsChip { get; set; } = "";
    [ObservableProperty] public partial string PatchedChip { get; set; } = "";
    [ObservableProperty] public partial string StubChip { get; set; } = "";
    [ObservableProperty] public partial string RedirectChip { get; set; } = "";
    [ObservableProperty] public partial string FakelibChip { get; set; } = "";
    [ObservableProperty] public partial string MissingChip { get; set; } = "";
    [ObservableProperty] public partial string PatternChip { get; set; } = "";
    [ObservableProperty] public partial string StubText { get; set; } = "";
    [ObservableProperty] public partial string RedirectText { get; set; } = "";
    [ObservableProperty] public partial string FakelibText { get; set; } = "";
    [ObservableProperty] public partial string OtherText { get; set; } = "";
    [ObservableProperty] public partial string MissingText { get; set; } = "";

    /// <summary>Firmwares of firmware_db, plus "Off"; the import check runs against <see cref="CheckFirmware"/>.</summary>
    [ObservableProperty] public partial IReadOnlyList<string> CheckFirmwares { get; set; } = [Off];
    [ObservableProperty] public partial string CheckFirmware { get; set; } = Off;
    public const string Off = "Off";

    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterAll), nameof(FilterChanged), nameof(FilterFakelib), nameof(FilterMissing), nameof(FilterPatterns))]
    public partial FunctionFilter Filter { get; set; } = FunctionFilter.All;
    [ObservableProperty] public partial IReadOnlyList<FunctionRow> Rows { get; set; } = [];
    [ObservableProperty] public partial FunctionRow? SelectedRow { get; set; }
    [ObservableProperty] public partial string ListFooter { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Overlay))]
    public partial bool SideBySide { get; set; }

    /// <summary>How the function is shown; Space in the canvas switches between the graph and the listing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GraphView), nameof(Listing), nameof(CallsView), nameof(ShowLegend), nameof(ShowCallsLegend), nameof(ShowOriginal))]
    public partial CodeView View { get; set; }

    [ObservableProperty] public partial GraphScene? Scene { get; set; }
    [ObservableProperty] public partial GraphScene? OriginalScene { get; set; }
    [ObservableProperty] public partial string GraphMessage { get; set; } = "Choose an eboot to see its functions as graphs.";

    [ObservableProperty] public partial string FunctionTitle { get; set; } = "";
    [ObservableProperty] public partial string FunctionInfo { get; set; } = "";
    [ObservableProperty] public partial string CallersNote { get; set; } = "";
    public ObservableCollection<ChangeRow> Changes { get; } = [];
    public ObservableCollection<LinkRow> Redirected { get; } = [];
    public ObservableCollection<LinkRow> Calls { get; } = [];
    public ObservableCollection<LinkRow> Callers { get; } = [];
    /// <summary>Strings and pointers the shown function reaches through [rip+X] (values stay on the lines).</summary>
    public ObservableCollection<DataRow> DataRefs { get; } = [];
    public bool HasDataRefs => DataRefs.Count > 0;

    public GraphViewport Viewport { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CrashText), nameof(CrashCrumb), nameof(HasCrashPath))]
    public partial string CrashPath { get; set; } = "";
    public string CrashText => CrashPath.Length == 0 ? "Optional: a tester's crash log" : CrashPath;
    public bool HasCrashPath => CrashPath.Length > 0;
    public ObservableCollection<CrashRow> CrashRows { get; } = [];
    /// <summary>Pattern and recogniser matches (code ones open their function; data hits are grouped).</summary>
    public ObservableCollection<CrashRow> PatternRows { get; } = [];
    public bool HasPatterns => PatternRows.Count > 0;

    // --- cave map ----------------------------------------------------------------------------------------
    public ObservableCollection<CaveBar> CaveBars { get; } = [];
    [ObservableProperty] public partial string CaveText { get; set; } = "";
    [ObservableProperty] public partial string CaveRoomText { get; set; } = "";
    [ObservableProperty] public partial string CaveUsedText { get; set; } = "";
    public bool HasCaves => CaveText.Length > 0;
    public bool HasCaveUse => CaveUsedText.Length > 0;
    [ObservableProperty] public partial string PatternText { get; set; } = "";
    public bool HasCrash => CrashRows.Count > 0;

    public string EbootText => EbootPath.Length == 0 ? "No eboot chosen" : EbootPath;
    public string CompareText => ComparePreview ? $"Studio's cook for FW {(_previewFirmware.Length > 0 ? _previewFirmware : main.SelectedFirmware)} (dry run, nothing written)"
        : ComparePath.Length == 0 ? "Optional: the backport made from it, Studio's own cook, or another version of the game" : ComparePath;
    public bool HasComparePath => ComparePath.Length > 0 || ComparePreview;
    public bool IsCompare => _diff is not null || _versions is not null;
    public bool IsVersions => _versions is not null;
    /// <summary>The canvases' labels: original / backport, or old / new build.</summary>
    public string OriginalLabel => IsVersions ? "OLD BUILD" : "ORIGINAL";
    public string ShownLabel => IsVersions ? "NEW BUILD" : "BACKPORT";
    /// <summary>Version mode: "698 changed · 466 new · 295 removed" (chip) and the full counts (tooltip).</summary>
    [ObservableProperty] public partial string VersionChip { get; set; } = "";
    [ObservableProperty] public partial string VersionText { get; set; } = "";
    /// <summary>Version mode: what the shown function matched in the old build.</summary>
    [ObservableProperty] public partial string MatchText { get; set; } = "";
    /// <summary>Version mode: functions of the old build with no partner (first 500).</summary>
    public ObservableCollection<CrashRow> OldOnlyRows { get; } = [];
    public bool HasOldOnly => OldOnlyRows.Count > 0;
    public bool HasImage => _view is not null;
    public bool HasScene => Scene is not null;
    public bool ShowLegend => HasScene && View == CodeView.Graph;
    public bool ShowCallsLegend => HasScene && View == CodeView.Calls;
    public bool GraphView
    {
        get => View == CodeView.Graph;
        set { if (value) View = CodeView.Graph; }
    }
    public bool Listing
    {
        get => View == CodeView.Listing;
        set { if (value) View = CodeView.Listing; else if (View == CodeView.Listing) View = CodeView.Graph; }
    }
    public bool CallsView
    {
        get => View == CodeView.Calls;
        set { if (value) View = CodeView.Calls; }
    }
    public bool ShowOriginal => SideBySide && IsCompare && View != CodeView.Calls;
    public bool HasFunction => _current is not null;
    public bool HasChanges => Changes.Count > 0;
    public bool HasRedirected => Redirected.Count > 0;
    public bool HasCalls => Calls.Count > 0;
    public bool CanGoBack => _history.Count > 0 || _modules.Count > 0;
    /// <summary>The view is inside a module reached by following a call.</summary>
    public bool InModule => _modules.Count > 0;
    /// <summary>"eboot.bin › libSceAgc.sprx › libSceAgcDriver.sprx".</summary>
    public string ModuleTrail => string.Join(" › ", _modules.Reverse().Select(m => Path.GetFileName(m.Eboot)).Append(Path.GetFileName(EbootPath)));
    public string ReturnText => _modules.TryPeek(out var frame) ? $"Back to {Path.GetFileName(frame.Eboot)}" : "";
    public bool CanGoForward => _forward.Count > 0;
    /// <summary>Functions shown since the eboot was loaded, the latest first (at most <see cref="RecentLimit"/>).</summary>
    public ObservableCollection<LinkRow> Recent { get; } = [];
    /// <summary>Picked in the History list: opens that function (then the list resets).</summary>
    [ObservableProperty] public partial LinkRow? HistoryPick { get; set; }
    /// <summary>A game is open on the Backport page.</summary>
    public bool HasCurrentGame => main.Game is not null;
    /// <summary>The shown eboot is the Backport page's game and that page has an analysis: imports can open there.</summary>
    public bool CanOpenImports => main.HasResult && IsCurrentGame;
    bool IsCurrentGame => main.Game is { } game && EbootPath.Length > 0 &&
        string.Equals(Path.GetFullPath(Path.Combine(game.Root, GameScan.MainExecutable)), Path.GetFullPath(EbootPath), StringComparison.OrdinalIgnoreCase);

    /// <summary>The Backport page's game or analysis changed.</summary>
    internal void MainChanged()
    {
        OnPropertyChanged(nameof(HasCurrentGame));
        OnPropertyChanged(nameof(CanOpenImports));
    }

    public bool Overlay
    {
        get => !SideBySide;
        set => SideBySide = !value;
    }
    public bool FilterAll { get => Filter == FunctionFilter.All; set { if (value) Filter = FunctionFilter.All; } }
    public bool FilterChanged { get => Filter == FunctionFilter.Changed; set { if (value) Filter = FunctionFilter.Changed; } }
    public bool FilterFakelib { get => Filter == FunctionFilter.Fakelib; set { if (value) Filter = FunctionFilter.Fakelib; } }
    public bool FilterMissing { get => Filter == FunctionFilter.Missing; set { if (value) Filter = FunctionFilter.Missing; } }
    public bool FilterPatterns { get => Filter == FunctionFilter.Patterns; set { if (value) Filter = FunctionFilter.Patterns; } }
    public string MissingFilterText => CheckFirmware == Off ? "Missing" : $"Missing on {CheckFirmware}";

    // --- files ---------------------------------------------------------------------------------------
    static readonly string[] Patterns = ["*.bin", "*.elf", "*.self"];

    [RelayCommand]
    async Task ChooseEboot()
    {
        if (main.Store.PickFiles is { } pick && await pick("Select an eboot", "Eboot (ELF or fake-signed SELF)", Patterns, false) is [var path])
        {
            EbootPath = path;
            await Reload();
        }
    }

    /// <summary>
    /// The Backport page's game: its eboot, compared with its last backport (the eboot.bin in the published output, else in
    /// the output folder) or, when there is none (not cooked yet, or the cook left eboot.bin unchanged), with Studio's cook.
    /// False when nothing could be shown.
    /// </summary>
    [RelayCommand]
    Task CurrentGame() => LoadCurrentGame();

    async Task<bool> LoadCurrentGame()
    {
        if (main.Game is not { } game)
            return false;
        string eboot = Path.Combine(game.Root, GameScan.MainExecutable);
        if (!File.Exists(eboot))
        {
            main.Log.Warning($"Code graph: no {GameScan.MainExecutable} in {game.Root}");
            return false;
        }
        string output = main.HasPublishedResult ? main.PublishedOutput : main.OutputFolder;
        string cooked = Path.Combine(output, GameScan.MainExecutable);
        bool hasBackport = File.Exists(cooked);
        EbootPath = eboot;
        ComparePath = hasBackport ? cooked : "";
        ComparePreview = !hasBackport;
        // the output is an overlay: a backport that left eboot.bin unchanged has none
        main.Log.Info($"Code graph: {game.TitleName}, compared with " + (hasBackport ? $"its backport {cooked}"
            : Directory.Exists(output) ? $"Studio's cook (the backport in {output} did not change {GameScan.MainExecutable})" : "Studio's cook (no backport written yet)"));
        await Reload();
        return _view is not null;
    }

    /// <summary>
    /// From the Imports tab: the functions of the current game's eboot that call <paramref name="nid"/>, the first one opened.
    /// Loads the game first when another eboot (or none) is shown.
    /// </summary>
    public async Task ShowCallers(string nid)
    {
        if ((!IsCurrentGame || _view is null) && !await LoadCurrentGame())
            return;
        Filter = FunctionFilter.All;
        _focusNid = nid;
        Search = main.FunctionName(nid);
        if (_calls is null)
            return;   // the list fills when the call scan ends
        if (Rows.FirstOrDefault() is { } first)
            SelectedRow = first;
        else
            main.Log.Info($"Code graph: no function of {Path.GetFileName(EbootPath)} calls {Search} directly (it may be called through a pointer)");
    }

    // --- following calls into other modules -------------------------------------------------------------
    /// <summary>
    /// Opens the shipped module that exports <paramref name="nid"/> (a fakelib, or a module next to the eboot or in
    /// sce_module/; <see cref="CodeImage.ProviderFile"/>) at that export. Back at the start returns, as it was.
    /// </summary>
    public async Task FollowImport(string nid)
    {
        if (_view is not { } view)
            return;
        string name = main.FunctionName(nid);
        if (await Task.Run(() => view.ProviderFile(nid)) is not { } file)
        {
            main.Log.Info($"Code graph: {name} is a system library function; no module shipped with this eboot exports it");
            return;
        }
        _modules.Push(new ModuleFrame(EbootPath, ComparePath, ComparePreview, _previewFirmware, view, _original, _diff, _versions, _calls, _current, [.. _history]));
        _following = true;
        ComparePath = "";
        ComparePreview = false;
        EbootPath = file;
        await Reload();
        if (_view is { } module && module.Path == file && module.ExportAddress(nid) is ulong at)
        {
            Navigate(at);
            main.Log.Info($"Code graph: followed {name} into {Path.GetFileName(file)} (0x{at:x})");
        }
        else if (_view?.Path == file)
            main.Log.Warning($"Code graph: {Path.GetFileName(file)} lists {name} as an export but no function starts there");
    }

    /// <summary>The GOT slot of the import <paramref name="l"/> calls when a shipped module exports it (double-click follows it).</summary>
    ulong? Followable(GraphLine l) => CalledImport(l) is var (imp, _) && _view!.ProviderFile(imp.Nid) is not null ? imp.GotVaddr : null;

    /// <summary>Back to the module the last call was followed from, with its function, history and comparison.</summary>
    [RelayCommand]
    async Task ReturnToModule()
    {
        if (!_modules.TryPop(out var frame))
            return;
        _restore = frame;
        ComparePath = frame.Compare;
        ComparePreview = frame.Preview;
        EbootPath = frame.Eboot;
        await Reload();
    }

    void NotifyModules()
    {
        OnPropertyChanged(nameof(InModule));
        OnPropertyChanged(nameof(ModuleTrail));
        OnPropertyChanged(nameof(ReturnText));
        OnPropertyChanged(nameof(CanGoBack));
    }

    // --- export --------------------------------------------------------------------------------------
    /// <summary>"GT7_eboot sub_27450 (calls).svg"-style names for the save dialog.</summary>
    string ExportName(string extension) =>
        $"{Path.GetFileNameWithoutExtension(EbootPath)} {FunctionTitle}{(View == CodeView.Calls ? " (calls)" : "")}.{extension}";

    /// <summary>What an export is of: the function, the eboot, the comparison and the view.</summary>
    IEnumerable<string> ExportTitle()
    {
        yield return $"{FunctionTitle} · {FunctionInfo}";
        yield return $"{Path.GetFileName(EbootPath)}: {EbootPath}";
        if (IsCompare)
            yield return $"compared with {CompareText}" + (SideBySide ? " (the backport side)" : " (overlay: \"was\" lines are the original)");
        yield return View switch { CodeView.Listing => "listing", CodeView.Calls => "call graph: callers, then callees", _ => "graph" } + " · PS5 Backport Studio";
    }

    /// <summary>The shown scene as text (blocks in address order), or null when nothing is shown.</summary>
    public string? ExportText() => Scene?.ToText(ExportTitle());

    /// <summary>The shown scene as SVG in the given theme colours, or null when nothing is shown.</summary>
    public string? ExportSvg(SvgPalette palette) => Scene?.ToSvg(palette, string.Join(" — ", ExportTitle().Take(2)));

    [RelayCommand]
    async Task CopyText()
    {
        if (ExportText() is not { } text || main.SetClipboard is null)
            return;
        await main.SetClipboard(text);
        main.Log.Info($"Code graph: copied {FunctionTitle} as text ({text.Count(c => c == '\n'):N0} lines)");
    }

    /// <summary>Asks where to save and writes the whole scene as SVG.</summary>
    public async Task SaveSvg(SvgPalette palette)
    {
        if (ExportSvg(palette) is not { } svg || main.PickSaveFile is null || await main.PickSaveFile("Save the graph as SVG", ExportName("svg"), "svg") is not { } path)
            return;
        try
        {
            await File.WriteAllTextAsync(path, svg);
            main.Log.Info($"Code graph: saved {FunctionTitle} as {path}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            main.Log.Error($"Code graph: could not save {path}: {e.Message}", exception: e);
        }
    }

    /// <summary>Asks where to save and lets the view write what is on screen as PNG.</summary>
    public async Task SavePng(Func<string, bool> render)
    {
        if (Scene is null || main.PickSaveFile is null || await main.PickSaveFile("Save the view as PNG", ExportName("png"), "png") is not { } path)
            return;
        try
        {
            if (render(path))
                main.Log.Info($"Code graph: saved the view of {FunctionTitle} as {path}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            main.Log.Error($"Code graph: could not save {path}: {e.Message}", exception: e);
        }
    }

    [RelayCommand]
    void OpenInImports(LinkRow? link)
    {
        if (link is { HasNid: true } && CanOpenImports)
            main.ShowImport(link.Nid);
    }

    [RelayCommand]
    async Task ChooseCompare()
    {
        if (main.Store.PickFiles is { } pick && await pick("Select the backport eboot to compare", "Eboot (ELF or fake-signed SELF)", Patterns, false) is [var path])
        {
            ComparePath = path;
            ComparePreview = false;
            if (EbootPath.Length > 0)
                await Reload();
        }
    }

    [RelayCommand]
    async Task ChooseCrashLog()
    {
        if (main.Store.PickFiles is { } pick && await pick("Select a crash log", "Crash log", ["*.txt", "*.log", "*"], false) is [var path])
        {
            CrashPath = path;
            await LoadCrash();
        }
    }

    [RelayCommand]
    void ClearCrash()
    {
        CrashPath = "";
        _crash = [];
        _crashAt = [];
        _crashReturn = [];
        CrashRows.Clear();
        OnPropertyChanged(nameof(HasCrash));
        BuildRows();
        if (_current is { } f)
            Show(f, remember: false);
    }

    /// <summary>Reads the crash log (CrashLog.AnalyzeFiles) and maps its addresses onto the shown eboot; opens the first one.</summary>
    public async Task LoadCrash()
    {
        if (_view is not { } view || CrashPath.Length == 0)
            return;
        string path = CrashPath;
        try
        {
            var events = await Task.Run(() => CrashMap.Map(CrashLog.AnalyzeFiles(path), view));
            if (view != _view || path != CrashPath)
                return;
            _crash = events;
            _crashAt = [];
            _crashReturn = [];
            CrashRows.Clear();
            foreach (var ev in events)
            {
                CrashRows.Add(new CrashRow(ev.Signal.Length > 0 ? ev.Signal : "fatal event", (ev.Reason.Length > 0 ? ev.Reason + " · " : "") + ev.Evidence, null, true));
                foreach (var frame in ev.Frames)
                {
                    string label = frame.IsReturnAddress ? $"#{frame.Index}" : frame.Kind;
                    string where = frame.Vaddr is ulong v ? $"{FunctionAndOffset(v)} (0x{v:x})" : frame.Note;
                    CrashRows.Add(new CrashRow($"{label}  0x{frame.Runtime:x}", frame.Vaddr is null ? where : $"{where} · {frame.Note}", frame.Vaddr));
                    if (frame.Vaddr is not ulong at)
                        continue;
                    if (frame.IsReturnAddress)
                        _crashReturn.TryAdd(at, $"backtrace #{frame.Index}: returns here");
                    else
                        _crashAt.TryAdd(at, frame.Kind);
                }
            }
            OnPropertyChanged(nameof(HasCrash));
            BuildRows();
            main.Log.Info($"Code graph: crash log {Path.GetFileName(path)}: {events.Count} event(s), {events.Sum(e => e.Frames.Count(f => f.Vaddr is not null))} address(es) in {Path.GetFileName(view.Path)}");
            var first = events.SelectMany(e => e.Frames).FirstOrDefault(f => f.Vaddr is not null);
            if (first?.Vaddr is ulong target)
                Navigate(target);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Status = $"Crash log: {e.Message}";
            StatusTone = "error";
            main.Log.Error($"Code graph: crash log {path}: {e.Message}");
        }
    }

    /// <summary>
    /// The cave map: the int3 runs after a `ret` that the engine's cave search can use (Patching.Caves), by size, the room
    /// "Extend code segment" would add, and, when comparing, the caves the backport filled.
    /// </summary>
    void SetCaves(List<(ulong Vaddr, long Usable)> caves, List<(ulong Vaddr, long Usable)>? originalCaves, long room)
    {
        _caveAfter = caves.ToDictionary(c => c.Vaddr - 1, c => c.Usable);   // ret, guard int3, usable bytes
        CaveBars.Clear();
        if (caves.Count == 0)
        {
            CaveText = "No caves: no int3 padding after a ret in the code segment.";
        }
        else
        {
            long largest = caves.Max(c => c.Usable);
            CaveText = $"{caves.Count:N0} caves after a ret, {caves.Sum(c => c.Usable):N0} bytes usable, at most {largest} bytes each. " +
                       "Longer code is split into jump-joined pieces; code that must stay in one piece (contiguous adapters, the boot notice) needs the extension.";
            int max = caves.GroupBy(c => c.Usable).Max(g => g.Count());
            foreach (var g in caves.GroupBy(c => c.Usable).OrderBy(g => g.Key))
                CaveBars.Add(new CaveBar((int)g.Key, g.Count(), 150.0 * g.Count() / max));
        }
        long blocks = room / Patching.ExtensionBlock;
        CaveRoomText = room == 0 ? "Extend code segment: no room (the next segment follows the code directly, or the gap is not empty)."
            : $"Extend code segment: {room:N0} bytes of room = {blocks:N0} blocks of {Patching.ExtensionBlock - 3} usable bytes.";
        CaveUsedText = "";
        if (originalCaves is not null && _diff is { } diff)
        {
            var stubs = diff.Changes.Where(c => c.Kind == ChangeKind.Stub).ToList();
            var used = originalCaves.Where(c => stubs.Any(s => s.Overlaps(c.Vaddr, c.Vaddr + (ulong)c.Usable))).ToList();
            CaveUsedText = $"This backport filled {used.Count:N0} of {originalCaves.Count:N0} caves ({stubs.Sum(s => s.Length):N0} bytes of stub code)" +
                           (diff.TextGrowth > 0 ? $" and grew the code segment by {diff.TextGrowth:N0} bytes." : ".");
        }
        OnPropertyChanged(nameof(HasCaves));
        OnPropertyChanged(nameof(HasCaveUse));
    }

    /// <summary>Shows pattern/recogniser matches: one row per code match, data hits grouped per pattern.</summary>
    void SetMatches(List<DetectorMatch> matches)
    {
        _matches = matches;
        PatternRows.Clear();
        foreach (var m in matches.Where(m => m.Code))
            PatternRows.Add(new CrashRow(m.Name, $"{m.Source} · 0x{m.Vaddr:x} in {FunctionAndOffset(m.Vaddr)}", m.Vaddr));
        foreach (var g in matches.Where(m => !m.Code).GroupBy(m => (m.Name, m.Source)))
            PatternRows.Add(new CrashRow(g.Key.Name, $"{g.Key.Source} · {g.Count()} {Plural(g.Count(), "hit", "hits")} in data (first 0x{g.First().Vaddr:x}), not code", null));
        int code = matches.Count(m => m.Code);
        PatternText = matches.Count == 0 ? "" : $"{matches.Count} rule pattern {Plural(matches.Count, "match", "matches")} ({code} in code)";
        PatternChip = matches.Count == 0 ? "" : code > 0 ? $"{code} {Plural(code, "pattern", "patterns")} in code" : $"{matches.Count} pattern {Plural(matches.Count, "hit", "hits")} in data";
        OnPropertyChanged(nameof(HasPatterns));
        BuildRows();
        if (_current is { } f)
            Show(f, remember: false);
    }

    string FunctionAndOffset(ulong vaddr) =>
        _view!.FunctionAt(vaddr) is { } f ? $"{_view.FunctionName(f.Start)}+0x{vaddr - f.Start:x}" : "no function";

    [RelayCommand]
    void OpenCrash(CrashRow? row)
    {
        if (row?.Target is ulong target)
            Navigate(target);
    }

    /// <summary>Compares the eboot with the backport Studio would make from its game folder with the Backport page's settings.</summary>
    [RelayCommand]
    async Task PreviewCook()
    {
        ComparePath = "";
        ComparePreview = true;
        if (EbootPath.Length > 0)
            await Reload();
    }

    [RelayCommand]
    async Task ClearCompare()
    {
        ComparePath = "";
        ComparePreview = false;
        if (EbootPath.Length > 0)
            await Reload();
    }

    /// <summary>Loads the eboot (and the backport), compares them and lists the functions; calls are scanned afterwards.</summary>
    public Task Reload() => main.IsClosing ? Task.CompletedTask : main.OwnPageWork(ReloadCore());

    async Task ReloadCore()
    {
        int version = ++_loadVersion;
        var restore = _restore;
        _restore = null;
        if (!_following && restore is null)
            _modules.Clear();   // another eboot or comparison was chosen: the trail starts again
        _following = false;
        NotifyModules();
        Busy = true;
        Status = restore is not null ? "Returning…" : ComparePreview ? "Loading and cooking a preview (dry run, nothing is written)…" : "Loading…";
        StatusTone = "muted";
        try
        {
            string eboot = EbootPath, compare = ComparePath;
            var preview = ComparePreview ? main.RunOptions(dryRun: true) : null;
            var knowledge = main.KnowledgeData;
            GameResult? run = null;
            var names = main.NameData;
            string? dbDir = main.FirmwareDbDir;
            var databases = _databases;
            var (view, original, diff, versions) = await Task.Run(() =>
            {
                databases ??= FirmwareDb.LoadAll(dbDir);
                if (restore is not null)
                    return (restore.View, restore.Original, restore.Diff, restore.Versions);
                var first = CodeImage.Load(eboot, names);
                if (compare.Length == 0 && preview is null)
                {
                    first.ProviderExports();   // read the shipped modules now: following a call asks them on the UI thread
                    return (first, (CodeImage?)null, (BackportDiff?)null, (VersionDiff?)null);
                }
                CodeImage backport;
                if (preview is not null)
                    (backport, run) = CookPreview.Run(eboot, preview, knowledge, names);
                else
                    backport = CodeImage.Load(compare, names);
                backport.ProviderExports();
                // a backport of this eboot: byte comparison; another build of the game: function matching (BinDiff-style)
                return BackportDiff.SameBuild(first, backport)
                    ? (backport, first, BackportDiff.Compare(first, backport), (VersionDiff?)null)
                    : (backport, first, null, VersionMatch.Compare(first, backport));
            });
            if (version != _loadVersion)
                return;
            if (_databases is null)
            {
                var loaded = databases ?? [];   // set inside Task.Run
                _databases = loaded;
                CheckFirmwares = [Off, .. loaded.Select(d => d.Firmware)];
                // start with the Backport page's target firmware
                CheckFirmware = loaded.LastOrDefault(d => FirmwareDb.Key(d.Firmware).CompareTo(FirmwareDb.Key(main.SelectedFirmware)) <= 0)?.Firmware ?? Off;
            }
            (_view, _original, _diff, _calls, _check) = (view, original, diff, null, null);
            _versions = versions;
            OnPropertyChanged(nameof(IsVersions));
            OnPropertyChanged(nameof(OriginalLabel));
            OnPropertyChanged(nameof(ShownLabel));
            OldOnlyRows.Clear();
            foreach (var gone in versions?.Removed.Take(500) ?? [])
                OldOnlyRows.Add(new CrashRow(original!.FunctionName(gone.Start), $"0x{gone.Start:x} · {gone.Size:N0} bytes", null));
            OnPropertyChanged(nameof(HasOldOnly));
            _matches = [];
            PatternRows.Clear();
            PatternText = PatternChip = "";
            OnPropertyChanged(nameof(HasPatterns));
            if (restore is not null)
            {
                _previewFirmware = restore.PreviewFirmware;
                OnPropertyChanged(nameof(CompareText));
                OnPropertyChanged(nameof(CompareCrumb));
            }
            if (run is not null)
            {
                _previewFirmware = run.TargetFirmware;
                OnPropertyChanged(nameof(CompareText));
                OnPropertyChanged(nameof(CompareCrumb));
                foreach (string warning in run.ParamWarnings)
                    main.Log.Warning($"Code graph cook preview: {warning}");
            }
            _missingCalls = [];
            _fakelibCalls = [];
            _chains = diff?.StubChains.ToDictionary(c => c[0].Address,
                c => (IReadOnlyList<FunctionRange>)[.. c.Select(p => new FunctionRange(p.Address, (ulong)p.Length))]) ?? [];
            _history.Clear();
            _forward.Clear();
            _current = null;   // a new module: Back must not lead to the previous one's function
            Recent.Clear();
            Summarise();
            BuildRows();
            Status = "";
            if (restore?.Current is { } back)
            {
                var row = _all.FirstOrDefault(r => r.Range == back);
                _navigating = true;
                SelectedRow = row is not null && Rows.Contains(row) ? row : null;
                _navigating = false;
                Show(back, remember: false);
                foreach (var h in restore.History.Reverse())
                    _history.Push(h);
            }
            else
                SelectFirst();
            OnPropertyChanged(nameof(IsCompare));
            OnPropertyChanged(nameof(HasImage));
            OnPropertyChanged(nameof(ShowOriginal));
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
            await RunImportCheck();
            if (version != _loadVersion)
                return;
            var scanned = diff is not null && original is not null ? original : view;   // a backport may be patched already
            var knowledgeForPatterns = main.KnowledgeData;
            var (matches, caves, originalCaves, room) = await Task.Run(() => (Detectors.Find(scanned.Elf, knowledgeForPatterns),
                Patching.Caves(view.Elf), original is null ? null : Patching.Caves(original.Elf), Patching.ExtensionRoom(view.Elf)));
            if (version != _loadVersion)
                return;
            SetCaves(caves, originalCaves, room);
            SetMatches(matches);
            if (CrashPath.Length > 0 && _modules.Count == 0)   // the crash log maps onto the eboot, not a followed module
                await LoadCrash();
            main.Log.Info($"Code graph: {view.Functions.Count:N0} functions in {Path.GetFileName(view.Path)}" +
                          (diff is null ? "" : $"; {diff.Changes.Count} changed code runs, {diff.Redirects.Count} redirected imports") +
                          (versions is null ? "" : $"; another build: {VersionText}"));

            var calls = restore?.Calls ?? await Task.Run(() => view.ScanCalls(CancellationToken.None));
            if (version != _loadVersion)
                return;
            _calls = calls;
            _fakelibCalls = FakelibCalls(view, calls);
            _missingCalls = MissingCalls();
            Summarise();
            BuildRows();
            if (_current is { } f)
            {
                FillCallers(f);
                if (View == CodeView.Calls)
                    Show(f, remember: false);   // the call graph was waiting for the scan
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)   // a malformed file must not take the app down
        {
            if (version != _loadVersion)
                return;
            (_view, _original, _diff, _calls, _check) = (null, null, null, null, null);
            _versions = null;
            VersionChip = VersionText = "";
            _missingCalls = [];
            _all = [];
            Rows = [];
            Scene = OriginalScene = null;
            ClearInspector();
            SummaryText = PatchedText = StubText = RedirectText = FakelibText = OtherText = MissingText = "";
            FunctionsChip = PatchedChip = StubChip = RedirectChip = FakelibChip = MissingChip = PatternChip = "";
            Status = e.Message;
            StatusTone = "error";
            GraphMessage = "Nothing to show.";
            OnPropertyChanged(nameof(HasImage));
            OnPropertyChanged(nameof(IsCompare));
            main.Log.Error($"Code graph: {e.Message}");
        }
        finally
        {
            if (version == _loadVersion)
                Busy = false;
        }
    }

    partial void OnCheckFirmwareChanged(string value)
    {
        OnPropertyChanged(nameof(MissingFilterText));
        if (_view is not null)
            _ = RunImportCheck();
    }

    /// <summary>Classifies the viewed eboot's imports for <see cref="CheckFirmware"/> (off: no statuses), then refreshes.</summary>
    async Task RunImportCheck()
    {
        int version = ++_checkVersion;
        var view = _view;
        var databases = _databases;
        string firmware = CheckFirmware;
        ImportCheck? check = null;
        if (view is not null && databases is { Count: > 0 } && firmware != Off)
        {
            try
            {
                var knowledge = main.KnowledgeData;
                check = await Task.Run(() => view.CheckImports(databases, firmware, knowledge));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                main.Log.Warning($"Code graph: import check for FW {firmware}: {e.Message}");
            }
        }
        if (version != _checkVersion || view != _view)
            return;
        _check = check;
        _missingCalls = MissingCalls();
        Summarise();
        BuildRows();
        if (_current is { } f)
            Show(f, remember: false);
    }

    /// <summary>{function start: unresolved imports it calls} from the call scan and the import check.</summary>
    Dictionary<ulong, SortedSet<string>> MissingCalls()
    {
        var result = new Dictionary<ulong, SortedSet<string>>();
        if (_check is null || _calls is null || _view is null)
            return result;
        foreach (var s in _check.Unresolved)
            foreach (ulong caller in _calls.ImportCallers.GetValueOrDefault(s.Nid, []))
            {
                if (!result.TryGetValue(caller, out var names))
                    result[caller] = names = new SortedSet<string>(StringComparer.Ordinal);
                names.Add(main.FunctionName(s.Nid));
            }
        return result;
    }

    /// <summary>"missing on FW 4.00 (from 11.00)" for an unresolved import, or null.</summary>
    string? StatusText(Import imp)
    {
        if (_check?.Statuses.GetValueOrDefault(imp.Nid) is not { } s || !Imports.IsUnresolved(s.Status))
            return null;
        string from = _check.Introduced.TryGetValue(imp.Nid, out string? first) ? $" (from {first})" : "";
        return s.Status == Imports.Missing ? $"missing on {_check.Firmware}{from}" : $"not in the {_check.Firmware} list{from}";
    }

    /// <summary>{function start: fakelib libraries it calls} for the libraries in the backport's fakelib/ folder.</summary>
    static Dictionary<ulong, SortedSet<string>> FakelibCalls(CodeImage image, CallIndex calls)
    {
        var result = new Dictionary<ulong, SortedSet<string>>();
        foreach (var imp in image.Imports.Where(i => image.FakelibLibraries.Contains(i.Library)).DistinctBy(i => i.Nid))
            foreach (ulong caller in calls.ImportCallers.GetValueOrDefault(imp.Nid, []))
            {
                if (!result.TryGetValue(caller, out var libs))
                    result[caller] = libs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                libs.Add(imp.Library);
            }
        return result;
    }

    void Summarise()
    {
        if (_view is not { } view)
            return;
        SummaryText = view.DiscoveredCount == 0 ? $"{view.Functions.Count:N0} functions (from unwind info)"
            : $"{view.Functions.Count:N0} functions: {view.Unwind.Count:N0} from unwind info + {view.DiscoveredCount:N0} found in code it does not cover";
        int patched = _diff?.Changes.Count(c => c.Kind == ChangeKind.Patched) ?? 0;
        int stubs = _diff?.StubChains.Count ?? 0;
        int other = _diff?.Changes.Count(c => c.Kind is ChangeKind.PltEntry or ChangeKind.Other) ?? 0;
        PatchedText = patched == 0 ? "" : $"{patched} patched {Plural(patched, "run", "runs")} in functions";
        int split = _diff?.StubChains.Count(c => c.Count > 1) ?? 0;
        StubText = stubs == 0 ? "" : $"{stubs} {Plural(stubs, "stub", "stubs")} in int3 padding" + (split == 0 ? "" : $" ({split} split across caves)");
        RedirectText = _diff is { Redirects.Count: > 0 } d ? $"{d.Redirects.Count} redirected {Plural(d.Redirects.Count, "import", "imports")}" : "";
        OtherText = (other == 0 ? "" : $"{other} other code {Plural(other, "change", "changes")}") +
                    (_diff is { TextGrowth: > 0 } g ? $" · code segment grew by {g.TextGrowth:N0} bytes" : "");
        int unresolved = _check?.Unresolved.Count() ?? 0;
        MissingText = _check is null ? "" : unresolved == 0 ? $"every import resolves on FW {_check.Firmware}"
            : $"{unresolved} {Plural(unresolved, "import", "imports")} unresolved on FW {_check.Firmware}" +
              (_calls is null ? "" : $", called by {_missingCalls.Count:N0} functions");
        FakelibText = view.FakelibLibraries.Count == 0 ? ""
            : $"{view.FakelibLibraries.Count} fakelib {Plural(view.FakelibLibraries.Count, "library", "libraries")}" +
              (_calls is null ? " (scanning calls…)" : $", called by {_fakelibCalls.Count:N0} functions");
        FunctionsChip = $"{view.Functions.Count:N0} functions";
        PatchedChip = patched == 0 ? "" : $"{patched} patched";
        StubChip = stubs == 0 ? "" : $"{stubs} {Plural(stubs, "stub", "stubs")}";
        RedirectChip = _diff is { Redirects.Count: > 0 } r ? $"{r.Redirects.Count} redirected" : "";
        FakelibChip = view.FakelibLibraries.Count == 0 ? "" : $"{view.FakelibLibraries.Count} fakelib";
        MissingChip = _check is null ? "" : $"{unresolved} missing on {_check.Firmware}";
        if (_versions is { } vs)
        {
            VersionChip = $"{vs.Changed:N0} changed · {vs.Added.Count:N0} new · {vs.Removed.Count:N0} removed";
            VersionText = $"{vs.Matches.Count:N0} functions paired with {Path.GetFileName(_original!.Path)}'s: {vs.Identical:N0} identical (moved or not), " +
                          $"{vs.Changed:N0} changed; {vs.Added.Count:N0} only in this build, {vs.Removed.Count:N0} only in the old one";
        }
        else
            VersionChip = VersionText = "";
    }

    /// <summary>How a version match was made, for the inspector; weak ways say so.</summary>
    static string HowText(MatchHow how) => how switch
    {
        MatchHow.Name => "the same export name",
        MatchHow.SameCode => "the same code",
        MatchHow.SameReferences => "the same strings and imports",
        MatchHow.SameInstructions => "the same instruction sequence",
        MatchHow.SameShape => "the same shape (instructions, blocks, branches, calls)",
        MatchHow.CallGraph => "its place in the call graph (a unique fingerprint among a matched function's callers or callees)",
        MatchHow.CallGraphOnly => "its place in the call graph (the only unmatched caller or callee left; weak, check it)",
        _ => "its place in address order between matched functions",
    };

    static string HowShort(MatchHow how) => how switch
    {
        MatchHow.Name => "name", MatchHow.SameCode => "code", MatchHow.SameReferences => "strings/imports",
        MatchHow.SameInstructions => "instructions", MatchHow.SameShape => "shape", MatchHow.CallGraph => "call graph",
        MatchHow.CallGraphOnly => "call graph, weak", _ => "address order",
    };

    static string Plural(int n, string one, string many) => n == 1 ? one : many;

    // --- function list -------------------------------------------------------------------------------
    void BuildRows()
    {
        if (_view is not { } view)
            return;
        var rows = new List<FunctionRow>();
        var patched = new HashSet<ulong>();
        var patternFunctions = _matches.Where(m => m.Code).Select(m => view.FunctionAt(m.Vaddr)).OfType<FunctionRange>().Select(f => f.Start).ToHashSet();
        foreach (var frame in _crash.SelectMany(e => e.Frames))
            if (frame.Vaddr is ulong at && view.FunctionAt(at) is { } crashed && patched.Add(crashed.Start))
                rows.Add(new FunctionRow(crashed, view.FunctionName(crashed.Start),
                    (frame.IsReturnAddress ? $"backtrace #{frame.Index}" : frame.Kind) + $" at +0x{at - crashed.Start:x}", "crash"));
        if (_diff is { } diff)
        {
            foreach (var g in diff.Changes.Where(c => c.Kind == ChangeKind.Patched).GroupBy(c => c.Function!.Value))
            {
                if (!patched.Add(g.Key.Start))
                    continue;   // already listed for the crash
                rows.Add(new FunctionRow(g.Key, view.FunctionName(g.Key.Start), $"{g.Count()} {Plural(g.Count(), "change", "changes")} · " + CallsText(g.Key.Start), "patched"));
            }
            foreach (var chain in diff.StubChains)
                rows.Add(new FunctionRow(new FunctionRange(chain[0].Address, (ulong)chain[0].Length), view.FunctionName(chain[0].Address), StubDetail(chain), "stub"));
            foreach (var c in diff.Changes.Where(c => c.Kind is ChangeKind.PltEntry or ChangeKind.Other))
            {
                var range = new FunctionRange(c.Address, (ulong)c.Length);
                rows.Add(c.Kind == ChangeKind.PltEntry
                    ? new FunctionRow(range, $"plt_{view.ImportName(c.Import!)}", "rewritten PLT entry → " + (diff.RedirectOf(c.Import!)?.Target is ulong t ? Describe(t) : "?"), "plt")
                    : new FunctionRow(range, $"code_{c.Address:x}", "changed bytes outside functions", "changed"));
            }
        }
        if (_versions is { } versions)
        {
            foreach (var m in versions.Matches.Where(m => !m.Identical).OrderBy(m => m.Similarity))
                if (patched.Add(m.New.Start))
                    rows.Add(new FunctionRow(m.New, view.FunctionName(m.New.Start), $"{m.Similarity:P0} alike · {HowShort(m.How)}", "modified"));
            foreach (var f in versions.Added)
                if (patched.Add(f.Start))
                    rows.Add(new FunctionRow(f, view.FunctionName(f.Start), $"{f.Size:N0} bytes · no partner in the old build", "new"));
        }
        foreach (var f in view.Functions)
        {
            if (patched.Contains(f.Start))
                continue;
            string name = view.FunctionName(f.Start);
            if (patternFunctions.Contains(f.Start))
                rows.Add(new FunctionRow(f, name, string.Join(", ", _matches.Where(m => m.Code && f.Contains(m.Vaddr)).Select(m => m.Name).Distinct()), "pattern"));
            else if (_missingCalls.TryGetValue(f.Start, out var missing))
                rows.Add(new FunctionRow(f, name, "calls " + string.Join(", ", missing), "missing"));
            else if (_fakelibCalls.TryGetValue(f.Start, out var libs))
                rows.Add(new FunctionRow(f, name, "calls " + string.Join(", ", libs), "fakelib"));
            else
                rows.Add(new FunctionRow(f, name, $"{f.Size:N0} bytes" + (view.IsDiscovered(f.Start) ? " · no unwind entry (found)" : ""), name == "entry" ? "entry" : ""));
        }
        _all = rows;
        _rowByStart = rows.GroupBy(r => r.Range.Start).ToDictionary(g => g.Key, g => g.First());
        ApplyFilter();
    }

    string CallsText(ulong start) => _fakelibCalls.TryGetValue(start, out var libs) ? "calls " + string.Join(", ", libs) : $"{_view!.FunctionAt(start)?.Size:N0} bytes";

    /// <summary>Known stub name or piece count, then the imports redirected to it.</summary>
    string StubDetail(IReadOnlyList<CodeChange> chain)
    {
        var names = _diff!.RedirectsTo(chain[0].Address).Select(r => _view!.ImportName(r.Import)).ToList();
        string what = chain.Count > 1 ? $"{chain.Count} pieces" : KnownStub(chain[0].Address) ?? "";
        string to = names.Count == 0 ? "no import redirected here" : "← " + string.Join(", ", names);
        return what.Length == 0 ? to : $"{what} · {to}";
    }

    /// <summary>The data/stubs.json name of a one-piece stub starting at <paramref name="vaddr"/>.</summary>
    string? KnownStub(ulong vaddr) =>
        _diff?.StubChains.FirstOrDefault(c => c[0].Address == vaddr) is [var only]
            ? main.KnowledgeData.StubName(_view!.Elf.Data.AsSpan((int)_view.Elf.Off(only.Address), only.Length)) : null;

    /// <summary>A redirect destination: its name, with the known stub it is.</summary>
    string Describe(ulong target)
    {
        string name = _view!.TargetName(target) ?? $"0x{target:x}";
        return KnownStub(target) is { } stub ? $"{name} ({stub})" : name;
    }

    // --- fix rules (data/fixes.json) -----------------------------------------------------------------
    /// <summary>The default rule for <paramref name="nid"/>, else the first option that has one, with where it comes from.</summary>
    (Fix Fix, string Source)? RuleFor(string nid)
    {
        var k = main.KnowledgeData;
        if (k.Fixes.TryGetValue(nid, out var fix) && fix.Kind != Fix.KindNone)
            return (fix, "default rule");
        foreach (var o in k.Options.Values)
            if (o.Fixes.TryGetValue(nid, out var f) && f.Kind != Fix.KindNone)
                return (f, $"option {o.Key}");
        return null;
    }

    string RuleShort(Fix f) => f.Kind is Fix.KindStub or Fix.KindPltStub ? $"{f.Kind} {f.Stub}"
        : f.Kind == Fix.KindGuarded ? $"{f.Kind} {f.Guard} {f.Stub}"
        : f.NewNid.Length > 0 ? $"{f.Kind} → {main.FunctionName(f.NewNid)}" : f.Kind;

    string RuleText((Fix Fix, string Source) rule)
    {
        var f = rule.Fix;
        string limits = (f.Below.Length > 0 ? $", below FW {f.Below}" : "") + (f.When == Fix.WhenAlways ? ", always" : "") + (f.Proven ? "" : ", not console-tested");
        return $"{rule.Source}: {RuleShort(f)}{limits}" + (f.Note.Length > 0 ? $" — {f.Note}" : "");
    }

    /// <summary>The import a call or jump reaches: through its PLT entry (Target) or its GOT slot (Reference); ViaGot tells which.</summary>
    (Import Import, bool ViaGot)? CalledImport(GraphLine l)
    {
        if (l.Target is ulong t && _view!.ImportAt(t) is { } viaPlt)
            return (viaPlt, false);
        if ((l.Kind == LineKind.Call || l.Text.StartsWith("jmp", StringComparison.Ordinal)) && l.Reference is ulong r && _view!.ImportAt(r) is { } viaGot)
            return (viaGot, true);
        return null;
    }

    partial void OnSearchChanged(string value)
    {
        if (_focusNid is not null && value != main.FunctionName(_focusNid))
            _focusNid = null;
        ApplyFilter();
    }
    partial void OnFilterChanged(FunctionFilter value) => ApplyFilter();

    /// <summary>Filter, then search: a hex address finds the function holding it; text matches names and the imports a function calls.</summary>
    void ApplyFilter()
    {
        IEnumerable<FunctionRow> rows = Filter switch
        {
            FunctionFilter.Changed => _all.Where(r => r.IsChange),
            FunctionFilter.Fakelib => _all.Where(r => r.Chip == "fakelib" || _fakelibCalls.ContainsKey(r.Range.Start)),
            FunctionFilter.Missing => _all.Where(r => _missingCalls.ContainsKey(r.Range.Start)),
            FunctionFilter.Patterns => _all.Where(r => _matches.Any(m => m.Code && r.Range.Contains(m.Vaddr))),
            _ => _all,
        };
        string q = Search.Trim();
        if (q.Length > 0)
        {
            string hex = q.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? q[2..] : q;
            var callers = ImportCallers(q, out bool exact);
            if (!exact)
                callers.UnionWith(StringUsers(q));
            if (ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out ulong address) && hex.Length >= 3)
                rows = rows.Where(r => r.Range.Contains(address) || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || callers.Contains(r.Range.Start));
            else
                rows = rows.Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || callers.Contains(r.Range.Start));
        }
        var list = rows.ToList();
        var selected = SelectedRow;
        _navigating = true;
        Rows = list;
        SelectedRow = selected is not null && list.Contains(selected) ? selected : null;
        _navigating = false;
        ListFooter = $"{list.Count:N0} of {_all.Count:N0}" + (q.Length > 0 && _calls is null ? " · import and string search after the call scan" : "") +
                     (Filter is FunctionFilter.Fakelib or FunctionFilter.Missing && _calls is null ? " · scanning calls…" : "") +
                     (Filter == FunctionFilter.Missing && _check is null ? " · choose a firmware to check against" : "");
    }

    /// <summary>Callers of the imports whose name or NID is <paramref name="query"/> (<paramref name="exact"/>); with no exact
    /// match, of those whose name contains it.</summary>
    HashSet<ulong> ImportCallers(string query, out bool exact)
    {
        exact = false;
        if (_calls is null || _view is null || query.Length < 3)
            return [];
        var imports = _view.Imports.DistinctBy(i => i.Nid).ToList();
        var named = imports.Where(i => i.Nid == query || _view.ImportName(i).Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        exact = named.Count > 0;
        var result = new HashSet<ulong>();
        foreach (var imp in exact ? named : imports.Where(i => _view.ImportName(i).Contains(query, StringComparison.OrdinalIgnoreCase)))
            result.UnionWith(_calls.ImportCallers.GetValueOrDefault(imp.Nid, []));
        return result;
    }

    /// <summary>The functions that take the address of a string containing <paramref name="query"/> (case ignored).</summary>
    HashSet<ulong> StringUsers(string query)
    {
        var result = new HashSet<ulong>();
        if (_calls is null || _view is null || query.Length < 3)
            return result;
        foreach (var (at, users) in _calls.StringUsers)
            if (_view.StringAt(at)?.Text.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                result.UnionWith(users);
        return result;
    }

    [RelayCommand]
    void OpenData(DataRow? row)
    {
        if (row?.Target is ulong target)
            Navigate(target);
        else if (row is { Search.Length: > 0 })
        {
            Filter = FunctionFilter.All;
            Search = row.Search;   // the functions using this string
        }
    }

    void SelectFirst()
    {
        var first = _all.FirstOrDefault(r => r.IsChange) ?? _all.FirstOrDefault(r => r.Chip == "entry") ?? _all.FirstOrDefault();
        if (first is not null && !Rows.Contains(first))
            Search = "";
        SelectedRow = first;
    }

    partial void OnSelectedRowChanged(FunctionRow? value)
    {
        if (value is not null && !_navigating)
            Show(value.Range, remember: true);
    }

    partial void OnViewChanged(CodeView value)
    {
        if (_current is { } f)
            Show(f, remember: false);
    }

    partial void OnSideBySideChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowOriginal));
        if (_current is { } f)
            Show(f, remember: false);
    }

    // --- navigation ----------------------------------------------------------------------------------
    /// <summary>Opens the function (or stub) holding <paramref name="target"/>; Back returns.</summary>
    public void Navigate(ulong target)
    {
        if (_view?.ImportAt(target) is { } imp)
        {
            _ = FollowImport(imp.Nid);
            return;
        }
        var row = _all.FirstOrDefault(r => r.Range.Start == target) ?? _all.FirstOrDefault(r => r.Range.Contains(target))
                  ?? (_diff?.ChainAt(target) is { } chain ? _all.FirstOrDefault(r => r.Range.Start == chain[0].Address) : null);
        if (row is null)
            return;
        _navigating = true;
        SelectedRow = Rows.Contains(row) ? row : null;
        _navigating = false;
        Show(row.Range, remember: true);
    }

    [RelayCommand]
    void Open(LinkRow? link)
    {
        if (link?.Target is ulong target)
            Navigate(target);
    }

    [RelayCommand]
    void Back()
    {
        if (_history.Count == 0 && _modules.Count > 0)
            _ = ReturnToModule();
        else
            Step(_history, _forward);
    }

    [RelayCommand]
    void Forward() => Step(_forward, _history);

    /// <summary>Back or Forward: the current function goes onto the other stack.</summary>
    void Step(Stack<FunctionRange> from, Stack<FunctionRange> to)
    {
        if (!from.TryPop(out var f))
            return;
        if (_current is { } current)
            to.Push(current);
        var row = _all.FirstOrDefault(r => r.Range == f);
        _navigating = true;
        SelectedRow = row is not null && Rows.Contains(row) ? row : null;
        _navigating = false;
        Show(f, remember: false);
    }

    partial void OnHistoryPickChanged(LinkRow? value)
    {
        if (value?.Target is not ulong target)
            return;
        Navigate(target);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => HistoryPick = null);   // the same entry can be picked again
    }

    /// <summary>Moves <paramref name="f"/> to the top of the History list.</summary>
    void AddRecent(FunctionRange f)
    {
        if (Recent.FirstOrDefault(r => r.Target == f.Start) is { } old)
            Recent.Remove(old);
        Recent.Insert(0, new LinkRow(_view!.FunctionName(f.Start), $"0x{f.Start:x}", f.Start));
        while (Recent.Count > RecentLimit)
            Recent.RemoveAt(Recent.Count - 1);
    }

    // --- graph ---------------------------------------------------------------------------------------
    IReadOnlyList<FunctionRange> Pieces(FunctionRange f) => _chains.GetValueOrDefault(f.Start) ?? [f];

    async void Show(FunctionRange f, bool remember)
    {
        if (_view is not { } view)
            return;
        if (remember && _current is { } previous && previous != f)
        {
            _history.Push(previous);
            _forward.Clear();   // a new path: what was ahead is dropped, as in a browser
        }
        if (_current != f)
            AddRecent(f);
        _current = f;
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(HasFunction));
        int version = ++_graphVersion;
        GraphMessage = "Building the graph…";
        var original = _original;
        var diff = _diff;
        var versions = _versions;
        bool side = SideBySide && (diff is not null || versions is not null);
        string? focusNid = _focusNid;
        Func<FunctionGraph, string, Func<GraphBlock, IReadOnlyList<SceneLine>>, ulong?, GraphScene> build = Listing ? GraphScene.Listing : GraphScene.Build;
        bool callGraph = View == CodeView.Calls;
        var callIndex = _calls;
        try
        {
            var (graph, originalGraph, scene, originalScene) = await Task.Run(() =>
            {
                var pieces = Pieces(f);
                var g = view.Graph(pieces);
                // the original's same addresses (a backport), or the matched function of the old build (another version)
                var match = versions?.OfNew(f.Start);
                IReadOnlyList<FunctionRange> oldPieces = match is not null ? [match.Old] : pieces;
                var og = versions is not null ? match is null ? null : original!.Graph(match.Old)
                    : original is not null && pieces.All(p => InCode(original, p)) ? original.Graph(pieces) : null;
                var (removedLines, addedLines) = versions is not null && og is not null ? VersionMatch.LineDiff(og, g) : ([], []);
                string name = view.FunctionName(f.Start);
                string oldName = match is not null ? original!.FunctionName(match.Old.Start) : name;
                ulong? focus = focusNid is null ? null
                    : g.Blocks.SelectMany(b => b.Lines).FirstOrDefault(l => CalledImport(l) is var (i, _) && (i.Nid == focusNid || diff?.RedirectOf(i)?.Import.Nid == focusNid))?.Address;
                if (callGraph)   // no call graph until the call scan has run
                    return (g, og, callIndex is null ? null : CallScene(f, callIndex), (GraphScene?)null);
                var s = build(g, name, b => Lines(b, pieces, og, overlay: !side, addedLines), focus);
                var os = side && og is not null ? build(og, oldName, b => OriginalLines(b, oldPieces, removedLines), versions is null ? focus : null) : null;
                return (g, og, s, os);
            });
            if (version != _graphVersion)
                return;
            Scene = scene;
            OriginalScene = originalScene;
            GraphMessage = scene is null ? "Scanning calls… the call graph appears when the scan ends." : "";
            OnPropertyChanged(nameof(HasScene));
            OnPropertyChanged(nameof(ShowLegend));
            OnPropertyChanged(nameof(ShowCallsLegend));
            FillInspector(f, graph, originalGraph);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (version != _graphVersion)
                return;
            Scene = OriginalScene = null;
            GraphMessage = $"No graph: {e.Message}";
            OnPropertyChanged(nameof(HasScene));
            main.Log.Warning($"Code graph: 0x{f.Start:x}: {e.Message}");
        }
    }

    /// <summary>
    /// The call graph around <paramref name="f"/> (CallIndex.Neighbourhood): each function node shows its address and its row's
    /// detail and chip, each import its library, redirect, fakelib and firmware status; double-click opens (centres) a node.
    /// </summary>
    GraphScene CallScene(FunctionRange f, CallIndex index)
    {
        var view = _view!;
        var hood = index.Neighbourhood(view, f.Start);
        var nodes = hood.Nodes.Select(n => CallSceneNode(n, f.Start)).ToList();
        return GraphScene.Calls(nodes, hood.Edges, f.Start);
    }

    SceneNode CallSceneNode(CallNode n, ulong center)
    {
        var view = _view!;
        if (n.More > 0)
            return new SceneNode(n.Key, $"+ {n.More:N0} more {(n.Level < 0 ? Plural(n.More, "caller", "callers") : Plural(n.More, "callee", "callees"))}", []);
        if (n.Import is { } imp)
        {
            var tags = new List<LineTag>();
            ulong? target = null;
            if (_diff?.RedirectOf(imp) is { Target: ulong dest })
            {
                tags.Add(new LineTag($"→ {Describe(dest)}", TagKind.Redirect));
                target = dest;
            }
            if (view.FakelibLibraries.Contains(imp.Library))
                tags.Add(new LineTag("fakelib", TagKind.Fakelib));
            if (target is null && view.ProviderFile(imp.Nid) is not null)
                target = imp.GotVaddr;   // double-click follows it into the module that exports it
            if (StatusText(imp) is { } status)
                tags.Add(new LineTag(status, _check!.Statuses[imp.Nid].Status == Imports.Missing ? TagKind.Missing : TagKind.Unknown));
            return new SceneNode(n.Key, view.ImportName(imp), [new SceneLine("", imp.Library, LineStyle.Call, target, tags)]);
        }
        ulong start = n.Function!.Value;
        var row = _rowByStart.GetValueOrDefault(start);
        string chip = row?.Chip ?? "";
        IReadOnlyList<LineTag>? chipTag = chip is "" or "entry" ? null : [new LineTag(chip, chip switch
        {
            "missing" or "crash" => TagKind.Missing, "fakelib" => TagKind.Fakelib, "pattern" => TagKind.Pattern, _ => TagKind.Redirect,
        })];
        var accent = start == center ? BlockAccent.Current : chip switch
        {
            "patched" => BlockAccent.Patched, "stub" => BlockAccent.Stub, "crash" => BlockAccent.Crash, _ => BlockAccent.None,
        };
        string detail = $"0x{start:x}" + (row is null ? "" : $" · {row.Detail}");
        return new SceneNode(start, view.FunctionName(start), [new SceneLine("", detail, LineStyle.Code, start == center ? null : start, chipTag)], accent);
    }

    static bool InCode(CodeImage image, FunctionRange f)
    {
        var text = image.Elf.TextSegment();
        return f.Start >= text.Vaddr && f.End <= text.Vaddr + (ulong)text.Filesz;
    }

    /// <summary>Backport (or single eboot) lines: changes highlighted; in overlay mode each change is followed by the original code.</summary>
    /// <param name="versionAdded">Another version: the lines with no counterpart in the matched old function (VersionMatch.LineDiff).</param>
    IReadOnlyList<SceneLine> Lines(GraphBlock block, IReadOnlyList<FunctionRange> pieces, FunctionGraph? original, bool overlay,
                                   IReadOnlySet<ulong>? versionAdded = null)
    {
        var lines = new List<SceneLine>();
        foreach (var l in block.Lines)
        {
            var change = _diff?.Changes.FirstOrDefault(c => c.Overlaps(l.Address, l.End));
            var style = change is null ? versionAdded?.Contains(l.Address) == true ? LineStyle.Patched : l.Kind == LineKind.Call ? LineStyle.Call : LineStyle.Code
                : change.Kind == ChangeKind.Stub ? LineStyle.Stub : LineStyle.Patched;
            var (tags, redirect) = Tags(l, backport: true);
            var all = WithData(WithSwitch(tags, l), l);
            if (l.Text == "ret" && _caveAfter.TryGetValue(l.End, out long free))
                all = [.. all ?? [], new LineTag($"cave after: {free} bytes", TagKind.Rule)];
            foreach (var m in _matches.Where(m => m.Code && m.Vaddr < l.End && l.Address < m.End))
            {
                if (style is LineStyle.Code or LineStyle.Call)
                    style = LineStyle.Pattern;
                if (m.Vaddr >= l.Address)   // tag the first line of the match only
                    all = [.. all ?? [], new LineTag($"{m.Name} · {m.Source}", TagKind.Pattern)];
            }
            if (_crashAt.TryGetValue(l.Address, out string? crashed) || _crashReturn.TryGetValue(l.End, out crashed))
            {
                style = LineStyle.Crash;
                all = [.. all ?? [], new LineTag(crashed, TagKind.Missing)];
            }
            lines.Add(new SceneLine($"{l.Address:x}", l.Text, style, redirect ?? Outside(l, pieces) ?? l.Data?.Target ?? Followable(l), all));
            if (change is not null && overlay)
                lines.Add(new SceneLine("was", OriginalText(original, l.Address, l.End), LineStyle.Was, null));
        }
        return lines;
    }

    /// <summary>
    /// The original's lines (side by side): what the backport changed, or, for another version, what has no counterpart in
    /// the new function. An old build's addresses are not the shown eboot's: its targets open their partners in the new build,
    /// and its import calls are not named again here (its own text already names them).
    /// </summary>
    IReadOnlyList<SceneLine> OriginalLines(GraphBlock block, IReadOnlyList<FunctionRange> pieces, IReadOnlySet<ulong> versionRemoved)
    {
        if (_versions is { } versions)
            return [.. block.Lines.Select(l => new SceneLine($"{l.Address:x}", l.Text,
                versionRemoved.Contains(l.Address) ? LineStyle.Removed : l.Kind == LineKind.Call ? LineStyle.Call : LineStyle.Code,
                (Outside(l, pieces) ?? l.Data?.Target) is ulong t ? versions.OfOld(t)?.New.Start : null, WithData(WithSwitch(null, l), l)))];
        return [.. block.Lines.Select(l => new SceneLine($"{l.Address:x}", l.Text,
            _diff!.Changes.Any(c => c.Overlaps(l.Address, l.End)) ? LineStyle.Removed : l.Kind == LineKind.Call ? LineStyle.Call : LineStyle.Code,
            Outside(l, pieces) ?? l.Data?.Target, WithData(WithSwitch(Tags(l, backport: false).Tags, l), l)))];
    }

    /// <summary>Adds the string, pointer or value a [rip+X] operand reaches.</summary>
    static IReadOnlyList<LineTag>? WithData(IReadOnlyList<LineTag>? tags, GraphLine l) =>
        l.Data is { } data ? [.. tags ?? [], new LineTag(data.Text, TagKind.Data)] : tags;

    /// <summary>Adds "switch · N cases" to a switch-table jump (distinct targets when cases share code).</summary>
    static IReadOnlyList<LineTag>? WithSwitch(IReadOnlyList<LineTag>? tags, GraphLine l)
    {
        if (l.Cases is not { } cases)
            return tags;
        int targets = cases.Distinct().Count();
        var tag = new LineTag($"switch · {cases.Count} cases" + (targets < cases.Count ? $" → {targets} blocks" : ""), TagKind.Switch);
        return [.. tags ?? [], tag];
    }

    /// <summary>
    /// Tags of a call to an import: where the backport sends it (that destination opens on double-click), the rule that
    /// covers it when nothing was redirected, "fakelib" for a fakelib library, and "system function" for an adapter's own
    /// `jmp [GOT]`, which still reaches the system function. <paramref name="backport"/> false: the original's lines.
    /// </summary>
    (IReadOnlyList<LineTag>? Tags, ulong? Redirect) Tags(GraphLine l, bool backport)
    {
        if (CalledImport(l) is not var (imp, viaGot))
            return (null, null);
        var tags = new List<LineTag>();
        ulong? redirect = null;
        var r = backport ? _diff?.RedirectOf(imp) : null;
        if (r is { Target: ulong dest } && !(r.ViaPlt && viaGot))
        {
            tags.Add(new LineTag($"→ {Describe(dest)}", TagKind.Redirect));
            redirect = dest;
        }
        else if (r is { ViaPlt: true } && viaGot)
            tags.Add(new LineTag("→ system function", TagKind.System));
        if (r?.NewNid is { } renamed)
            tags.Add(new LineTag($"renamed → {main.FunctionName(renamed)}", TagKind.Redirect));
        if (r is null && RuleFor(imp.Nid) is { } rule)
            tags.Add(new LineTag($"rule: {RuleShort(rule.Fix)}", TagKind.Rule));
        if (_view!.FakelibLibraries.Contains(imp.Library))
            tags.Add(new LineTag("fakelib", TagKind.Fakelib));
        if (backport && StatusText(imp) is { } status)
            tags.Add(new LineTag(status, _check!.Statuses[imp.Nid].Status == Imports.Missing ? TagKind.Missing : TagKind.Unknown));
        if (l.Arguments is { Count: > 0 } arguments)
            tags.Add(new LineTag(ArgumentsText(arguments), TagKind.Rule));
        if (ProbeVerdict(imp, l) is var (verdict, needed))
            tags.Add(new LineTag(verdict, needed ? TagKind.Redirect : TagKind.Rule));
        return (tags.Count == 0 ? null : tags, redirect);
    }

    /// <summary>"rdi=1, rsi=0x8000": small values in decimal, others in hex.</summary>
    static string ArgumentsText(IEnumerable<CallArgument> arguments) =>
        string.Join(", ", arguments.Select(a => $"{a.Register}={(a.Value <= 9999 ? a.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"0x{a.Value:x}")}"));

    /// <summary>
    /// What an option's argument probe (data/fixes.json "probe" kind argument, e.g. pad_vibration_compat) concludes from this
    /// call: needed when the constant is above its limit. Null when no option watches this import.
    /// </summary>
    (string Text, bool Needed)? ProbeVerdict(Import imp, GraphLine l)
    {
        foreach (var o in main.KnowledgeData.Options.Values)
        {
            var p = o.Probe;
            if (p.Kind != OptionProbe.KindArgument || p.Nid != imp.Nid)
                continue;
            string register = new CallArgument(p.Argument, 0).Register;
            if (l.Arguments?.FirstOrDefault(a => a.Index == p.Argument) is { } arg && l.Arguments.Any(a => a.Index == p.Argument))
                return (long)(uint)arg.Value > p.Above
                    ? ($"{o.Key}: needed ({register}={arg.Value} > {p.Above})", true)
                    : ($"{o.Key}: not needed here ({register}={arg.Value} ≤ {p.Above})", false);
            return ($"{o.Key}: {register} is not a constant here", false);
        }
        return null;
    }

    /// <summary>Only targets outside the function open something on double-click.</summary>
    static ulong? Outside(GraphLine l, IReadOnlyList<FunctionRange> pieces) => l.Target is ulong t && !FunctionGraph.Contains(pieces, t) ? t : null;

    /// <summary>The original instructions over [start, end): runs of int3 collapse to "int3 ×N".</summary>
    static string OriginalText(FunctionGraph? original, ulong start, ulong end)
    {
        if (original is null)
            return "(new code)";
        var texts = original.Blocks.SelectMany(b => b.Lines).Where(l => l.Address < end && start < l.End).Select(l => l.Text).ToList();
        if (texts.Count == 0)
            return "(new code)";
        return texts.All(t => t == "int3") ? texts.Count == 1 ? "int3" : $"int3 ×{texts.Count}" : string.Join(" · ", texts);
    }

    // --- inspector -----------------------------------------------------------------------------------
    void ClearInspector()
    {
        _current = null;
        FunctionTitle = FunctionInfo = CallersNote = MatchText = "";
        Changes.Clear();
        Redirected.Clear();
        Calls.Clear();
        Callers.Clear();
        DataRefs.Clear();
        OnPropertyChanged(nameof(HasCalls));
        OnPropertyChanged(nameof(HasDataRefs));
        OnPropertyChanged(nameof(HasFunction));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(HasRedirected));
    }

    void FillInspector(FunctionRange f, FunctionGraph graph, FunctionGraph? original)
    {
        var view = _view!;
        var pieces = graph.Pieces;
        ulong size = pieces.Aggregate(0UL, (a, p) => a + p.Size);
        FunctionTitle = view.FunctionName(f.Start);
        FunctionInfo = $"0x{f.Start:x} · {size:N0} bytes" + (pieces.Count > 1 ? $" in {pieces.Count} pieces" : "") +
                       (view.IsDiscovered(f.Start) ? " · no unwind entry: found from calls, pointers or padding, its end is the next start" : "") +
                       $" · {graph.Instructions:N0} {Plural(graph.Instructions, "instruction", "instructions")} · {graph.Blocks.Count:N0} {Plural(graph.Blocks.Count, "block", "blocks")}";
        MatchText = _versions is not { } versions ? ""
            : versions.OfNew(f.Start) is { } m
                ? (m.Identical ? "Unchanged since the old build: " : $"Changed since the old build ({m.Similarity:P0} alike): ") +
                  $"{_original!.FunctionName(m.Old.Start)} at 0x{m.Old.Start:x}{(m.Identical && m.Old.Start != f.Start ? " (moved)" : "")}, matched by {HowText(m.How)}"
                : "New in this build: no function of the old build matches it.";
        Changes.Clear();
        Redirected.Clear();
        if (_diff is { } diff)
        {
            var lines = graph.Blocks.SelectMany(b => b.Lines).ToList();
            foreach (var c in pieces.SelectMany(diff.In).Distinct())
            {
                // shown by whole instructions: from the first to the last backport instruction the change touches
                var touched = lines.Where(l => c.Overlaps(l.Address, l.End)).ToList();
                ulong start = touched.Count > 0 ? Math.Min(touched[0].Address, c.Address) : c.Address;
                ulong end = touched.Count > 0 ? Math.Max(touched[^1].End, c.End) : c.End;
                string after = string.Join(" · ", touched.Select(l => l.Text));
                string note = c.Kind switch
                {
                    ChangeKind.Patched when lines.FirstOrDefault(l => l.Address >= c.End && l.Kind == LineKind.Call) is { } call
                        => $"Next call after it: {call.Text} at 0x{call.Address:x}",
                    ChangeKind.PltEntry => $"PLT entry of {view.ImportName(c.Import!)}",
                    ChangeKind.Stub when KnownStub(c.Address) is { } stub => $"Known stub {stub}" +
                        (main.KnowledgeData.Stubs.TryGetValue(stub, out var body) && body.Note.Length > 0 ? $": {body.Note}"
                         : stub.StartsWith("ret:", StringComparison.Ordinal) ? ": returns that value" : ""),
                    _ => "",
                };
                var (kind, chip) = c.Kind switch
                {
                    ChangeKind.Patched => ("patched in place", "patched"),
                    ChangeKind.Stub => ("stub in int3 padding", "stub"),
                    ChangeKind.PltEntry => ("PLT entry", "plt"),
                    _ => ("changed", "changed"),
                };
                var span = new FunctionRange(start, end - start);
                string at = start >= f.Start && start < f.End ? $"0x{start:x} (+0x{start - f.Start:x})" : $"0x{start:x}";
                Changes.Add(new ChangeRow(at, kind, chip,
                    OriginalText(original, start, end), _original is { } o && InCode(o, span) ? Hex(o, span) : "",
                    after, Hex(view, span), note));
                foreach (var r in diff.RedirectsTo(c.Address))
                    Redirected.Add(new LinkRow(view.ImportName(r.Import), r.Import.Library, null));
            }
        }
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(HasRedirected));

        Calls.Clear();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in graph.Blocks.SelectMany(b => b.Lines))
        {
            if (l.Kind != LineKind.Call && !l.Text.StartsWith("jmp", StringComparison.Ordinal))
                continue;
            if (CalledImport(l) is var (imp, viaGot))
            {
                if (!seen.Add($"{imp.Nid}/{viaGot}"))
                    continue;
                var (_, redirect) = Tags(l, backport: true);
                var r = _diff?.RedirectOf(imp);
                string? provider = redirect is null ? view.ProviderFile(imp.Nid) : null;
                string detail = imp.Library + (redirect is ulong dest ? $" → {Describe(dest)}" : r is { ViaPlt: true } && viaGot ? " → system function" : "") +
                                (provider is null ? "" : $" · opens {Path.GetFileName(provider)}");
                string? status = StatusText(imp);
                // the constants of every call to this import in the function, one line per call that passes some
                var sites = graph.Blocks.SelectMany(b => b.Lines).Where(x => x.Kind == LineKind.Call && CalledImport(x) is var (i, _) && i.Nid == imp.Nid).ToList();
                var argLines = sites.Select(x => (x.Address, Text: x.Arguments is { Count: > 0 } a ? ArgumentsText(a) : "", Probe: ProbeVerdict(imp, x)?.Text))
                    .Where(x => x.Text.Length > 0 || x.Probe is not null)
                    .Select(x => $"0x{x.Address:x}: " + string.Join(" · ", new[] { x.Text, x.Probe ?? "" }.Where(t => t.Length > 0))).ToList();
                string arguments = argLines.Count == 0 ? "" : string.Join("\n", argLines.Take(4)) + (argLines.Count > 4 ? $"\n… {argLines.Count - 4} more calls" : "");
                Calls.Add(new LinkRow(view.ImportName(imp), detail, redirect ?? (provider is null ? null : imp.GotVaddr), view.FakelibLibraries.Contains(imp.Library),
                                      RuleFor(imp.Nid) is { } rule ? RuleText(rule) : "", status ?? "", arguments, r?.Import.Nid ?? imp.Nid));
            }
            else if (Outside(l, pieces) is ulong t && seen.Add($"{t:x}") && view.TargetName(t) is { } name)
                Calls.Add(new LinkRow(name, "this eboot", view.IsFunctionStart(t) || _all.Any(row => row.Range.Start == t) ? t : null));
        }
        OnPropertyChanged(nameof(HasCalls));

        DataRefs.Clear();
        foreach (var l in graph.Blocks.SelectMany(b => b.Lines).Where(l => l.Data is { Kind: DataKind.String or DataKind.Pointer }).DistinctBy(l => l.Data!.Address).Take(200))
        {
            var data = l.Data!;
            string search = data.Kind == DataKind.String && view.StringAt(data.Address) is { } s ? s.Text : "";
            DataRefs.Add(new DataRow(data.Text, $"0x{l.Address:x}", data.Target, search));
        }
        OnPropertyChanged(nameof(HasDataRefs));
        FillCallers(f);
    }

    void FillCallers(FunctionRange f)
    {
        Callers.Clear();
        if (_calls is null)
        {
            CallersNote = "Scanning calls…";
            return;
        }
        var callers = _calls.Callers.GetValueOrDefault(f.Start, []);
        foreach (ulong c in callers.Take(200))
            Callers.Add(new LinkRow(_view!.FunctionName(c), $"0x{c:x}", c));
        CallersNote = callers.Count == 0 ? "No direct callers (it may be called through a pointer)."
            : callers.Count > 200 ? $"First 200 of {callers.Count:N0}" : "";
    }

    static string Hex(CodeImage image, FunctionRange span)
    {
        int n = (int)Math.Min(span.Size, 32);
        string hex = Convert.ToHexString(image.Elf.Data, (int)image.Elf.Off(span.Start), n);
        return string.Join(" ", Enumerable.Range(0, n).Select(i => hex.Substring(i * 2, 2))) + (span.Size > (ulong)n ? " …" : "");
    }
}
