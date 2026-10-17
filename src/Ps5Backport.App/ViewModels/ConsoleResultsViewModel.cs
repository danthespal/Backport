using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ps5Backport.Core;

namespace Ps5Backport.App.ViewModels;

/// <summary>Evidence of one item in one firmware band: pass, fail, unknown and old (other form or unpinned) run counts.</summary>
public sealed record BandCell(int Pass, int Fail, int Unknown, int Old)
{
    public BandCell(BandEvidence e) : this(e.Pass, e.Fail, e.Unknown, e.Old) { }
    public bool HasPass => Pass > 0;
    public bool HasFail => Fail > 0;
    public bool HasUnknown => Unknown > 0;
    public bool HasOld => Old > 0;
    public bool IsEmpty => Pass + Fail + Unknown + Old == 0;
}

/// <summary>One item of the matrix (or of the Backport page's evidence card): its four band cells and state.</summary>
public sealed class ResultItemRow(string item, string label, bool experimental, IReadOnlyList<BandEvidence> bands, bool candidate, bool failed)
{
    public string Item => item;
    public string Label => label;
    public bool Experimental => experimental;
    public bool Candidate => candidate;
    public bool Failed => failed;
    public bool HasRuns => bands.Any(b => b.Any);
    public bool Started => bands.Any(MatrixRow.Current);
    public BandCell Band0 { get; } = new(bands[0]);
    public BandCell Band1 { get; } = new(bands[1]);
    public BandCell Band2 { get; } = new(bands[2]);
    public BandCell Band3 { get; } = new(bands[3]);
    public IReadOnlyList<BandEvidence> Bands => bands;
    public string Kind => item[..item.IndexOf(':')];
    public string State => candidate ? "★ candidate" : failed ? "failed" : "";
    public string StateTone => candidate ? "patched" : failed ? "error" : "muted";

    public ResultItemRow(MatrixRow row) : this(row.Item, row.Label, row.Experimental, row.Bands, row.Candidate, row.Failed) { }
    public ResultItemRow(ConsoleEvidenceEntry e, string label) : this(e.Item, label, true, e.Bands, e.Candidate, e.Failures.Count > 0) { }
}

/// <summary>One run of the selected item, for the inspector.</summary>
public sealed record RunView(string Band, string Header, string Detail, string Verdict, bool Current)
{
    public string VerdictTone => !Current ? "muted" : Verdict switch { "pass" => "ok", "fail" => "error", _ => "warn" };
    public string Pin => Current ? "" : "old form or unpinned · history only";
}

/// <summary>The legend's sample cells.</summary>
public static class ConsoleResultsLegend
{
    public static BandCell Pass { get; } = new(1, 0, 0, 0);
    public static BandCell Fail { get; } = new(0, 1, 0, 0);
    public static BandCell Unknown { get; } = new(0, 0, 1, 0);
    public static BandCell Old { get; } = new(0, 0, 0, 1);
}

/// <summary>A tester card under the matrix: console and what to send it next.</summary>
public sealed record TesterCard(string Handle, string Console, string SendNext);

/// <summary>One tested item of the result being added; the verdict follows the outcome until it is edited.</summary>
public sealed partial class VerdictRow(string item, string label, string fingerprint, string verdict) : ObservableObject
{
    public string Item => item;
    public string Label => label;
    public string Fingerprint => fingerprint;
    public static IReadOnlyList<string> Choices => ConsoleResults.Verdicts;
    bool _following = true;

    [ObservableProperty] public partial string Verdict { get; set; } = verdict;

    /// <summary>Takes the outcome's default verdict unless the user picked one.</summary>
    internal void Follow(string outcome)
    {
        if (!_following) return;
        Verdict = ConsoleResults.DefaultVerdict(outcome);
        _following = true;
    }

    partial void OnVerdictChanged(string value) => _following = false;
}

/// <summary>
/// The Console results page (layout A of work/mockups/console-results.html): items × firmware bands with filters, an inspector
/// with the runs of the selected item and its promotion, the tester strip, and the Add result flow. Reads and writes the same
/// data/console_results.json and data/testers.json as `ps5backport results` (<see cref="ConsoleResults.DefaultDataDir"/>, or the
/// Settings override).
/// </summary>
public sealed partial class ConsoleResultsViewModel(MainViewModel main) : ObservableObject
{
    LogViewModel Log => main.Log;
    ConsoleResults? _results;
    Knowledge? _knowledge;
    List<MatrixRow> _matrix = [];
    ConsoleResult? _draft;

    public string DataDir => main.Settings.ConsoleResultsDir ?? ConsoleResults.DefaultDataDir;

    public ObservableCollection<ResultItemRow> Rows { get; } = [];
    public ObservableCollection<RunView> SelectedRuns { get; } = [];
    public ObservableCollection<TesterCard> TesterCards { get; } = [];

    // --- filters ---------------------------------------------------------------------------------------------------
    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial bool ExperimentalOnly { get; set; } = true;
    [ObservableProperty] public partial bool WithRunsOnly { get; set; }
    [ObservableProperty] public partial bool ShowRules { get; set; } = true;
    [ObservableProperty] public partial bool ShowOptions { get; set; } = true;
    [ObservableProperty] public partial bool ShowFakelibs { get; set; } = true;
    /// <summary>0 all, 1 candidates, 2 failed somewhere, 3 gaps (run elsewhere, missing on a band).</summary>
    [ObservableProperty] public partial int StateFilter { get; set; }

    partial void OnSearchChanged(string value) => Filter();
    partial void OnExperimentalOnlyChanged(bool value) => Filter();
    partial void OnWithRunsOnlyChanged(bool value) => Filter();
    partial void OnShowRulesChanged(bool value) => Filter();
    partial void OnShowOptionsChanged(bool value) => Filter();
    partial void OnShowFakelibsChanged(bool value) => Filter();
    partial void OnStateFilterChanged(int value) => Filter();

    // --- summary ---------------------------------------------------------------------------------------------------
    [ObservableProperty] public partial string Problem { get; set; } = "";
    public bool HasProblem => Problem.Length > 0;
    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));
    public string TestersText => $"{_results?.Testers.Count ?? 0} testers";
    public string RunsText => $"{_results?.Results.Count ?? 0} runs";
    public int CandidateCount => _matrix.Count(r => r.Candidate);
    public string CandidatesText => $"{CandidateCount} promotion candidate{(CandidateCount == 1 ? "" : "s")}";
    public int FailureCount => _matrix.Count(r => r.Failed);
    public string FailuresText => $"{FailureCount} failure{(FailureCount == 1 ? "" : "s")}";
    public string NeverRunText => $"{_matrix.Count(r => r.Experimental && !r.Started)} of {_matrix.Count(r => r.Experimental)} experimental items never run in their current form";
    public string DataText => $"{System.IO.Path.Combine(DataDir, ConsoleResults.ResultsFile)}";

    // --- inspector -------------------------------------------------------------------------------------------------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedTitle), nameof(SelectedLabel), nameof(SelectedPin), nameof(IsCandidate),
                              nameof(FailureText), nameof(HasFailure), nameof(PromotionText))]
    public partial ResultItemRow? SelectedRow { get; set; }

    public bool HasSelection => SelectedRow is not null;
    public bool HasNoRuns => SelectedRuns.Count == 0;
    public string SelectedTitle => SelectedRow?.Item ?? "";
    public string SelectedLabel => SelectedRow?.Label ?? "";
    public string SelectedPin => SelectedRow is { } row && _matrix.FirstOrDefault(m => m.Item == row.Item) is { } m
        ? m.Fingerprint is { } fp ? $"fingerprint {fp} (current form)" : "no longer in the rule data or the store" : "";
    public bool IsCandidate => SelectedRow?.Candidate == true;
    public string FailureText => SelectedRow is { } row && _results is not null
        ? string.Join("\n", ConsoleResults.Bands.Select((band, i) => row.Bands[i].Fail > 0 ? $"failed on {band}" : null).OfType<string>()) : "";
    public bool HasFailure => FailureText.Length > 0;
    /// <summary>What Promote… would do for the selected item.</summary>
    public string PromotionText => SelectedRow is { } row && _knowledge is { } k ? ConsoleResults.Plan(row.Item, k) switch
    {
        { CanApply: true } p => $"Promote marks {string.Join(", ", p.ProveNids.Select(n => Name(k, n)))} proven in data/fixes.json.",
        var p => p.Manual,
    } : "";

    partial void OnSelectedRowChanged(ResultItemRow? value)
    {
        SelectedRuns.Clear();
        if (value is null || _results is null)
            return;
        string? fingerprint = _matrix.FirstOrDefault(m => m.Item == value.Item)?.Fingerprint;
        foreach (var (run, test, current) in _results.Runs(value.Item, fingerprint))
            SelectedRuns.Add(new RunView(ConsoleResults.Band(run.Firmware),
                $"{run.Date} · {run.Tester} · FW {run.Firmware} · {run.Title}{(run.Build.Length > 0 ? " " + run.Build : "")}",
                run.Outcome + (run.Error.Length > 0 ? ": " + run.Error : "") + (run.Note.Length > 0 ? " · " + run.Note : ""),
                test.Verdict, current));
        OnPropertyChanged(nameof(HasNoRuns));
    }

    static string Name(Knowledge k, string nid) => k.Fixes.TryGetValue(nid, out var f) ? f.Name
        : k.Options.Values.SelectMany(o => o.Fixes.Values).FirstOrDefault(f => f.Nid == nid)?.Name ?? nid;

    // --- loading ---------------------------------------------------------------------------------------------------

    /// <summary>Reloads both files and the rule data they are pinned against (keeps the selection).</summary>
    public void Refresh()
    {
        string? keep = SelectedRow?.Item;
        try
        {
            _results = ConsoleResults.Load(DataDir);
            _knowledge = File.Exists(System.IO.Path.Combine(DataDir, "fixes.json")) ? new Knowledge(DataDir) : main.KnowledgeData;
            _matrix = _results.Matrix(_knowledge, main.StoreDir);
            Problem = "";
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _results = null;
            _matrix = [];
            Problem = e.Message;
            Log.Error($"Console results couldn't be read: {e.Message}", exception: e);
        }
        TesterCards.Clear();
        if (_results is not null)
            foreach (var (tester, band, items) in _results.SendNext(_matrix))
                TesterCards.Add(new TesterCard(tester.Handle, $"{tester.Model} · FW {tester.Firmware} · {band}",
                    items.Count == 0 ? "send next: nothing" : "send next: " + string.Join(", ", items)));
        TesterHandles.Clear();
        foreach (string handle in _results?.Testers.Keys.Order(StringComparer.Ordinal) ?? Enumerable.Empty<string>())
            TesterHandles.Add(handle);
        foreach (string name in new[] { nameof(TestersText), nameof(RunsText), nameof(CandidatesText), nameof(CandidateCount), nameof(FailuresText),
                                        nameof(FailureCount), nameof(NeverRunText), nameof(DataText) })
            OnPropertyChanged(name);
        Filter();
        SelectedRow = Rows.FirstOrDefault(r => r.Item == keep) ?? Rows.FirstOrDefault(r => r.Candidate) ?? Rows.FirstOrDefault();
    }

    /// <summary>The results a Studio cook reports evidence from (null, with a log warning, when the files are invalid).</summary>
    internal ConsoleResults? ForCook()
    {
        try { return ConsoleResults.Load(DataDir); }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Console results not used for this run: {e.Message}");
            return null;
        }
    }

    void Filter()
    {
        string? keep = SelectedRow?.Item;
        Rows.Clear();
        var bands = ConsoleResults.Bands.Count;
        foreach (var m in _matrix)
        {
            string kind = m.Item[..m.Item.IndexOf(':')];
            if ((kind == "rule" && !ShowRules) || (kind == "option" && !ShowOptions) || (kind == "fakelib" && !ShowFakelibs))
                continue;
            if ((ExperimentalOnly && !m.Experimental) || (WithRunsOnly && !m.HasRuns))
                continue;
            bool gap = m.Started && Enumerable.Range(0, bands).Any(i => !MatrixRow.Current(m.Bands[i]));
            if ((StateFilter == 1 && !m.Candidate) || (StateFilter == 2 && !m.Failed) || (StateFilter == 3 && !gap))
                continue;
            if (Search.Length > 0 && !m.Item.Contains(Search, StringComparison.OrdinalIgnoreCase) && !m.Label.Contains(Search, StringComparison.OrdinalIgnoreCase)
                && !(_results?.Results.Any(r => r.Title.Contains(Search, StringComparison.OrdinalIgnoreCase) && r.Tests.Any(t => t.Item == m.Item)) ?? false))
                continue;
            Rows.Add(new ResultItemRow(m));
        }
        SelectedRow = Rows.FirstOrDefault(r => r.Item == keep);
    }

    [RelayCommand]
    void Reload()
    {
        Refresh();
        Log.Info($"Console results: {RunsText}, {TestersText}, {CandidatesText}, {FailuresText} ({DataText})");
    }

    // --- promotion -------------------------------------------------------------------------------------------------
    [ObservableProperty] public partial bool IsPromoteOpen { get; set; }
    [ObservableProperty] public partial string PromoteTitle { get; set; } = "";
    [ObservableProperty] public partial string PromoteChange { get; set; } = "";
    [ObservableProperty] public partial string PromoteManual { get; set; } = "";
    [ObservableProperty] public partial bool CanApplyPromote { get; set; }
    PromotionPlan? _plan;

    [RelayCommand]
    void OpenPromote()
    {
        if (SelectedRow is not { } row || _knowledge is not { } k)
            return;
        _plan = ConsoleResults.Plan(row.Item, k);
        PromoteTitle = $"Promote {row.Item}";
        PromoteChange = _plan.CanApply
            ? $"data/fixes.json ({System.IO.Path.Combine(DataDir, "fixes.json")}): remove \"proven\": false from\n" +
              string.Join("\n", _plan.ProveNids.Select(n => $"  {(_plan.OptionKey is { } key ? $"options.{key}.fixes." : "fixes.")}{n}  {Name(k, n)}")) +
              "\nNothing else in the file changes; the edit is checked by reparsing and reloading the rule data."
            : "No data/fixes.json change for this item.";
        PromoteManual = _plan.Manual;
        CanApplyPromote = _plan.CanApply;
        IsPromoteOpen = true;
    }

    [RelayCommand]
    void ClosePromote() => IsPromoteOpen = false;

    [RelayCommand]
    void ApplyPromote()
    {
        if (_plan is not { CanApply: true } plan)
            return;
        try
        {
            var changed = ConsoleResults.ApplyPromotion(DataDir, plan);
            Log.Info($"Promoted {plan.Item}: {string.Join(", ", changed)} now proven in {System.IO.Path.Combine(DataDir, "fixes.json")}" +
                     (DataDir != main.KnowledgeData.DataDir ? " (cooks use it after the next build copies data/)" : ""));
            IsPromoteOpen = false;
            Refresh();
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or KeyNotFoundException or UnauthorizedAccessException)
        {
            Log.Error($"Promotion of {plan.Item} failed, data/fixes.json unchanged: {e.Message}", exception: e);
            PromoteManual = $"Not changed: {e.Message}";
        }
    }

    // --- add result ------------------------------------------------------------------------------------------------
    public ObservableCollection<string> TesterHandles { get; } = [];
    public static IReadOnlyList<string> Outcomes => ConsoleResults.Outcomes;
    public static IReadOnlyList<string> Models => ConsoleResults.Models;
    public ObservableCollection<VerdictRow> AddTests { get; } = [];

    [ObservableProperty] public partial bool IsAdding { get; set; }
    [ObservableProperty] public partial string? AddTester { get; set; }
    [ObservableProperty] public partial string AddBuild { get; set; } = "";
    [ObservableProperty] public partial string AddLog { get; set; } = "";
    [ObservableProperty] public partial string AddTitle { get; set; } = "";
    [ObservableProperty] public partial string AddOutcome { get; set; } = "plays";
    [ObservableProperty] public partial string AddError { get; set; } = "";
    [ObservableProperty] public partial string AddDate { get; set; } = "";
    [ObservableProperty] public partial string AddNote { get; set; } = "";
    [ObservableProperty] public partial string AddBuildInfo { get; set; } = "";
    [ObservableProperty] public partial string AddLogInfo { get; set; } = "";
    [ObservableProperty] public partial string AddProblem { get; set; } = "";
    // new tester
    [ObservableProperty] public partial string NewHandle { get; set; } = "";
    [ObservableProperty] public partial string NewModel { get; set; } = "base";
    [ObservableProperty] public partial string NewFirmware { get; set; } = "";
    [ObservableProperty] public partial string NewLoader { get; set; } = "";

    partial void OnAddOutcomeChanged(string value)
    {
        foreach (var row in AddTests)
            row.Follow(value);
    }

    partial void OnAddTesterChanged(string? value) => Prefill();

    [RelayCommand]
    void StartAdd()
    {
        if (_results is null)
            Refresh();
        AddTester ??= TesterHandles.FirstOrDefault();
        AddDate = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        AddProblem = "";
        IsAdding = true;
    }

    [RelayCommand]
    void CancelAdd()
    {
        IsAdding = false;
        _draft = null;
        AddTests.Clear();
        AddBuild = AddLog = AddTitle = AddError = AddNote = AddBuildInfo = AddLogInfo = AddProblem = "";
        AddOutcome = "plays";
    }

    [RelayCommand]
    async Task ChooseBuild()
    {
        if (main.PickFolder is not null && await main.PickFolder("Select the cooked backport folder (contains backport_report.json)") is { } folder)
            SetBuild(folder);
    }

    [RelayCommand]
    async Task ChooseLog()
    {
        if (main.PickFile is not null && await main.PickFile("Select the tester's kernel log", "Kernel log", "*.log") is { } file)
            SetLog(file);
    }

    public void SetBuild(string folder)
    {
        AddBuild = folder;
        Prefill();
    }

    public void SetLog(string file)
    {
        AddLog = file;
        Prefill();
    }

    /// <summary>Fills the draft from the tester, cook folder and log (<see cref="ConsoleResults.Draft"/>, the same as `results add`).</summary>
    void Prefill()
    {
        if (!IsAdding || _results is null || _knowledge is null || AddTester is null)
            return;
        AddProblem = "";
        try
        {
            var (draft, crash) = _results.Draft(AddTester, AddBuild.Length > 0 ? AddBuild : null, AddLog.Length > 0 ? AddLog : null,
                _knowledge, main.StoreDir, date: AddDate);
            _draft = draft;
            if (draft.Title.Length > 0)
                AddTitle = draft.Title;
            if (crash)
            {
                AddOutcome = draft.Outcome;
                AddError = draft.Error;
            }
            AddBuildInfo = AddBuild.Length == 0 ? "" : $"title {draft.Title} · target {(draft.Target.Length > 0 ? draft.Target : "?")} · eboot " +
                (draft.EbootSha256.Length > 0 ? draft.EbootSha256[..8] + "…" : "none") + $" · {draft.Tests.Count} applied items (console_items)";
            AddLogInfo = AddLog.Length == 0 ? "" : $"firmware {draft.Firmware} ({ConsoleResults.Band(draft.Firmware)}) · " +
                (crash ? $"fatal event: {draft.Error}" : "no fatal event found");
            AddTests.Clear();
            foreach (var t in draft.Tests)
            {
                var row = new VerdictRow(t.Item, ConsoleResults.Label(t.Item, _knowledge), t.Fingerprint, ConsoleResults.DefaultVerdict(AddOutcome));
                row.Follow(AddOutcome);
                AddTests.Add(row);
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or IOException or FormatException or UnauthorizedAccessException
                                     or System.Text.Json.JsonException)
        {
            _draft = null;
            AddProblem = e.Message;
        }
    }

    [RelayCommand]
    void SaveResult()
    {
        if (_results is null || AddTester is null)
            return;
        if (_draft is null)
            Prefill();
        if (_draft is not { } draft)
        {
            AddProblem = AddProblem.Length > 0 ? AddProblem : "Choose a cook folder, or enter the title id";
            return;
        }
        var entry = draft with
        {
            Tester = AddTester, Title = AddTitle.Trim(), Outcome = AddOutcome, Error = AddError.Trim(), Date = AddDate.Trim(), Note = AddNote.Trim(),
            Tests = [.. AddTests.Select(t => new ResultTest(t.Item, t.Verdict, t.Fingerprint))],
        };
        _results.Results.Add(entry);
        try { _results.Save(); }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _results.Results.Remove(entry);
            AddProblem = e.Message;
            return;
        }
        Log.Info($"Console result recorded: {entry.Date} {entry.Tester} FW {entry.Firmware} {entry.Title} {entry.Outcome} ({entry.Tests.Count} items) in {DataText}");
        CancelAdd();
        Refresh();
    }

    [RelayCommand]
    void AddNewTester()
    {
        if (_results is null)
            return;
        try
        {
            _results.AddTester(new Tester(NewHandle.Trim(), NewModel, Firmware.Name(NewFirmware.Trim()), NewLoader.Trim(), ""));
            _results.Save();
            string handle = NewHandle.Trim();
            NewHandle = NewFirmware = NewLoader = "";
            Refresh();
            AddTester = handle;
            Log.Info($"Tester {handle} added to {System.IO.Path.Combine(DataDir, ConsoleResults.TestersFile)}");
        }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            AddProblem = e.Message;
        }
    }
}
