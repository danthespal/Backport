using Ps5Backport.App;
using Ps5Backport.App.ViewModels;
using Ps5Backport.Core;

namespace Ps5Backport.Tests;

/// <summary>The Console results page (ConsoleResultsViewModel) and the Backport page's evidence card.</summary>
public sealed class GuiConsoleResultsTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ps5backport-gui-results-" + Guid.NewGuid().ToString("N"));
    const string Item = "rule:" + GuardedFixTests.Set3DLatency;

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    MainViewModel Open(string results)
    {
        string data = ConsoleResultsWorkflowTests.DataCopy(_root, ConsoleResultsWorkflowTests.FourTesters, results);
        var vm = new MainViewModel(new AppSettings { ConsoleResultsDir = data, FakelibStoreDir = Path.Combine(_root, "absent"), KnownFixes = true });
        vm.Page = AppPage.Results;
        return vm;
    }

    static string Pinned(params string[] testers)
    {
        var k = new Knowledge(Paths.DataDir);
        return ConsoleResultsWorkflowTests.Passes(Item, ConsoleResults.RuleFingerprint(k.Fixes[GuardedFixTests.Set3DLatency], k), testers);
    }

    [Fact]
    public void Page_ShowsTheMatrix_FiltersAndSelectsTheCandidate()
    {
        using var vm = Open(Pinned("t450", "t550", "t860", "t1360"));
        var page = vm.Results;
        Assert.Equal("1 promotion candidate", page.CandidatesText);
        Assert.Equal(Item, page.SelectedRow?.Item);                    // a candidate is selected first
        Assert.True(page.IsCandidate);
        Assert.Equal(4, page.SelectedRuns.Count);
        Assert.All(page.SelectedRuns, r => Assert.True(r.Current));
        Assert.Equal(new BandCell(1, 0, 0, 0), page.SelectedRow!.Band2);
        Assert.Equal(4, page.TesterCards.Count);

        page.StateFilter = 1;                                           // candidates
        Assert.Equal([Item], page.Rows.Select(r => r.Item));
        page.StateFilter = 0;
        page.ShowRules = false;
        Assert.DoesNotContain(page.Rows, r => r.Kind == "rule");
        page.ShowRules = true;
        page.Search = "Set3DLatency";
        Assert.Equal([Item], page.Rows.Select(r => r.Item));
    }

    /// <summary>Promote shows the change, writes it only on Apply, and the item then stops being experimental.</summary>
    [Fact]
    public void Promote_EditsTheRuleDataOnlyOnApply()
    {
        using var vm = Open(Pinned("t450", "t550", "t860", "t1360"));
        var page = vm.Results;
        string fixes = Path.Combine(page.DataDir, "fixes.json"), before = File.ReadAllText(fixes);
        page.OpenPromoteCommand.Execute(null);
        Assert.True(page.IsPromoteOpen);
        Assert.True(page.CanApplyPromote);
        Assert.Contains($"fixes.{GuardedFixTests.Set3DLatency}", page.PromoteChange);
        page.ClosePromoteCommand.Execute(null);
        Assert.Equal(before, File.ReadAllText(fixes));

        page.OpenPromoteCommand.Execute(null);
        page.ApplyPromoteCommand.Execute(null);
        Assert.False(page.IsPromoteOpen);
        Assert.True(new Knowledge(page.DataDir).Fixes[GuardedFixTests.Set3DLatency].Proven);
        Assert.DoesNotContain(page.Rows, r => r.Item == Item);         // proven: no longer experimental
    }

    /// <summary>Add result: the cook folder's items arrive pinned, the outcome drives untouched verdicts, Save writes the file.</summary>
    [Fact]
    public void AddResult_PrefillsFromTheCook_AndSaves()
    {
        var (game, reference) = GuardedFixTests.Set3DLatencyGame(_root, "4", exported: false);
        string cook = Path.Combine(_root, "PPSA00001_backport_4.00");
        Pipeline.Backport(game, cook, new BackportOptions { Firmware = "4", ReferenceDir = reference }, new Knowledge(Paths.DataDir),
                          cancel: TestContext.Current.CancellationToken);
        using var vm = Open("""{"results":[]}""");
        var page = vm.Results;
        page.StartAddCommand.Execute(null);
        page.AddTester = "t860";
        page.SetBuild(cook);
        Assert.Equal("", page.AddProblem);
        Assert.Equal("", page.AddTitle);                                // the synthetic cook has no param.json
        page.AddTitle = "PPSA00001";
        var row = Assert.Single(page.AddTests);
        Assert.Equal((Item, "sceAudioOut2Set3DLatency", "pass"), (row.Item, row.Label, row.Verdict));
        page.AddOutcome = "crash";
        Assert.Equal("unknown", row.Verdict);                           // follows the outcome
        row.Verdict = "fail";
        page.AddOutcome = "hang";
        Assert.Equal("fail", row.Verdict);                              // an edited verdict stays
        page.AddNote = "freeze at the title screen";
        page.SaveResultCommand.Execute(null);

        Assert.False(page.IsAdding);
        var saved = Assert.Single(ConsoleResults.Load(page.DataDir).Results);
        Assert.Equal(("t860", "8.60", "hang", "PPSA00001", "freeze at the title screen"), (saved.Tester, saved.Firmware, saved.Outcome, saved.Title, saved.Note));
        Assert.Equal("fail", Assert.Single(saved.Tests).Verdict);
        Assert.Equal("1 failure", page.FailuresText);
    }

    [Fact]
    public void AddResult_NewTester_IsSavedAndSelected()
    {
        using var vm = Open("""{"results":[]}""");
        var page = vm.Results;
        page.StartAddCommand.Execute(null);
        page.NewHandle = "tester-503";
        page.NewModel = "slim";
        page.NewFirmware = "5.02";
        page.AddNewTesterCommand.Execute(null);
        Assert.Equal("tester-503", page.AddTester);
        Assert.Contains("tester-503", ConsoleResults.Load(page.DataDir).Testers.Keys);
        page.NewHandle = "Bad Name";
        page.NewFirmware = "5.02";
        page.AddNewTesterCommand.Execute(null);
        Assert.Contains("handle", page.AddProblem);
    }

    /// <summary>Studio cooks pass the results in: the Overview's evidence card lists the applied item and its candidate note.</summary>
    [Fact]
    public async Task BackportPage_ShowsEvidenceOfTheAppliedItems()
    {
        var (game, reference) = GuardedFixTests.Set3DLatencyGame(_root, "4", exported: false);
        string data = ConsoleResultsWorkflowTests.DataCopy(_root, ConsoleResultsWorkflowTests.FourTesters, Pinned("t450", "t550", "t860", "t1360"));
        using var vm = new MainViewModel(new AppSettings { ConsoleResultsDir = data, FirmwareDbDir = reference, FakelibStoreDir = Path.Combine(_root, "absent") });
        vm.LoadGame(game);
        vm.OutputFolder = Path.Combine(_root, "output");
        await vm.AnalyzeCommand.ExecuteAsync(null);
        Assert.Equal(OperationState.Completed, vm.OperationState);
        Assert.True(vm.HasConsoleEvidence);
        var row = Assert.Single(vm.ConsoleEvidence, r => r.Item == Item);
        Assert.True(row.Candidate);
        Assert.Contains(vm.ConsoleEvidenceNotes, n => n.StartsWith($"★ {Item}", StringComparison.Ordinal));
        vm.OpenConsoleResultsCommand.Execute(null);
        Assert.Equal(AppPage.Results, vm.Page);
    }
}
