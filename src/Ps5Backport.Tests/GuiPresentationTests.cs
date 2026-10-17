using Ps5Backport.App;
using Ps5Backport.App.ViewModels;
using Ps5Backport.Core;

namespace Ps5Backport.Tests;

public sealed class GuiPresentationTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ps5backport-presentation-" + Guid.NewGuid().ToString("N"));
    readonly MainViewModel _vm;
    readonly string _game;

    public GuiPresentationTests()
    {
        _game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
        File.WriteAllBytes(Path.Combine(_game, "eboot.bin"), SyntheticBinary.Elf(imports: true));
        string reference = Directory.CreateDirectory(Path.Combine(_root, "reference")).FullName;
        FirmwareDb.Save(new FirmwareExports("4", "synthetic", false, new()), Path.Combine(reference, "4.json"));
        _vm = new MainViewModel(new AppSettings { FirmwareDbDir = reference, FakelibStoreDir = Path.Combine(_root, "absent"), KnownFixes = false });
        _vm.LoadGame(_game);
        _vm.OutputFolder = Path.Combine(_root, "output");
    }

    public void Dispose()
    {
        _vm.Dispose();
        Directory.Delete(_root, true);
    }

    [Theory]
    [InlineData("firmware")]
    [InlineData("known")]
    [InlineData("candidate")]
    [InlineData("unknown")]
    [InlineData("symbols")]
    [InlineData("extend")]
    [InlineData("param")]
    [InlineData("logging")]
    [InlineData("optional")]
    [InlineData("paths")]
    [InlineData("notice")]
    public async Task RelevantChanges_MarkDisplayedResultsStale(string change)
    {
        await _vm.AnalyzeCommand.ExecuteAsync(null);
        Assert.Equal(OperationState.Completed, _vm.OperationState);
        Assert.False(_vm.IsAnalysisStale);
        var displayed = _vm.Result;
        switch (change)
        {
            case "firmware": _vm.SelectedFirmware = "5"; break;
            case "known": _vm.KnownFixes = !_vm.KnownFixes; break;
            case "candidate": _vm.CandidateFixes = !_vm.CandidateFixes; break;
            case "unknown": _vm.StubUnknown = !_vm.StubUnknown; break;
            case "symbols": _vm.DefineSymbol = !_vm.DefineSymbol; break;
            case "extend": _vm.ExtendText = !_vm.ExtendText; break;
            case "param": _vm.PatchParam = !_vm.PatchParam; break;
            case "logging": _vm.AmprDebugLogging = !_vm.AmprDebugLogging; break;
            case "optional": _vm.PatchOptions[0].IsEnabled = true; break;
            case "paths": _vm.SetPath(PathKind.Aerolib, Path.Combine(_root, "names.csv")); break;
            case "notice": _vm.BootNoticeMessage = "changed after analysis"; break;   // part of the cook fingerprint
        }
        Assert.True(_vm.IsAnalysisStale);
        Assert.Same(displayed, _vm.Result);
        Assert.Equal("Results for firmware 4", _vm.ResultTargetText);
    }

    [Fact]
    public async Task ReanalysisAndNewGame_ClearStaleState()
    {
        await _vm.AnalyzeCommand.ExecuteAsync(null);
        _vm.KnownFixes = true;
        Assert.True(_vm.IsAnalysisStale);
        await _vm.AnalyzeCommand.ExecuteAsync(null);
        Assert.Equal(OperationState.Completed, _vm.OperationState);
        Assert.False(_vm.IsAnalysisStale);
        _vm.KnownFixes = false;
        _vm.LoadGame(_game);
        Assert.False(_vm.HasResult);
        Assert.False(_vm.IsAnalysisStale);
        Assert.False(_vm.HasPublishedResult);
        Assert.Empty(_vm.WarningDetails);
    }

    [Fact]
    public async Task SettingsChangedDuringAnalysis_DoNotMakeOldResultsAppearCurrent()
    {
        var run = _vm.AnalyzeCommand.ExecuteAsync(null);
        Assert.True(_vm.Busy);
        _vm.KnownFixes = true;
        await run;
        Assert.Equal(OperationState.Completed, _vm.OperationState);
        Assert.True(_vm.IsAnalysisStale);
    }

    [Fact]
    public async Task ClearingFilters_RestoresRowsAndResetsEmptyState()
    {
        Assert.False(_vm.HasModules);
        Assert.StartsWith("Analyze a game", _vm.ImportsEmptyText);
        await _vm.AnalyzeCommand.ExecuteAsync(null);
        Assert.True(_vm.HasModules);
        Assert.True(_vm.HasImports);
        _vm.SelectedModule = _vm.Modules.Single();
        _vm.ImportSearch = "does-not-match";
        Assert.False(_vm.HasImports);
        Assert.StartsWith("No imports match", _vm.ImportsEmptyText);
        _vm.ClearImportFiltersCommand.Execute(null);
        Assert.Equal("", _vm.ImportSearch);
        Assert.Equal("All", _vm.ImportFilter);
        Assert.Null(_vm.SelectedModule);
        Assert.Single(_vm.Imports);
        Assert.True(_vm.HasImports);
    }

    [Fact]
    public async Task CompletionActions_UsePublishedOutputAfterDestinationIsEdited()
    {
        string published = _vm.OutputFolder;
        await _vm.CookCommand.ExecuteAsync(null);
        Assert.True(_vm.HasPublishedResult);
        Assert.Contains("1 import remains unresolved", _vm.CompletionText);
        _vm.OutputFolder = Path.Combine(_root, "edited-destination");
        Assert.Equal(published, _vm.PublishedOutput);
        Assert.True(_vm.OpenPublishedOutputCommand.CanExecute(null));
        Assert.True(_vm.BuildPublishedAmprIndexCommand.CanExecute(null));
        await _vm.BuildPublishedAmprIndexCommand.ExecuteAsync(null);
        Assert.Equal(OperationState.Completed, _vm.OperationState);
        Assert.True(File.Exists(Path.Combine(published, "ampr_emu.index")));
        Assert.False(Directory.Exists(_vm.OutputFolder));
        // ampr_emu treats the index as the complete /app0 list (FINDINGS 6b2): the build warns, every time
        Assert.Contains(_vm.Log.Visible, e => e.Level == LogLevel.Warning && e.Message == MainViewModel.AmprIndexWarning);
        Assert.DoesNotContain("Include ampr_emu.index in the release", _vm.AmprIndexText);
        Assert.Contains("only if", _vm.AmprIndexText);
    }

    [Fact]
    public void WarningSummaries_PreserveOriginalDetails_AndUnknownWarnings()
    {
        const string technical = "param.json sdkVersion=0x1300000000000000 is above the target FW 4; left unchanged (--patch-param lowers it)";
        var warning = new WarningRow(technical);
        Assert.Equal(technical, warning.Detail);
        Assert.Contains("left unchanged", warning.Summary);
        Assert.DoesNotContain("0x", warning.Summary);
        const string unknown = "A new diagnostic which has no summary mapping.";
        Assert.Equal(unknown, new WarningRow(unknown).Summary);
    }

    [Fact]
    public void DataAndUnknownStatuses_RemainUncheckedOrUnresolved()
    {
        ImportRow Row(string status) => new() { Module = "eboot.bin", Nid = "nid", Name = "name", Library = "library", Status = status };
        var data = Row(Imports.Data);
        Assert.False(data.IsResolved);
        Assert.Contains("unchecked", data.StatusDisplay);
        var unknown = Row(Imports.Unknown);
        Assert.True(unknown.IsUnresolved);
        Assert.Contains("uncertain", unknown.StatusDescription);
    }

    [Fact]
    public void CodeGraphHighlight_PicksAndMatchesWholeWords()
    {
        const string text = "mov rbx, qword ptr [rbx+rbx*8+38h]";
        Assert.Equal("rbx", Ps5Backport.App.Views.GraphCanvas.Word(text, 5));
        Assert.Equal("mov", Ps5Backport.App.Views.GraphCanvas.Word(text, 0));
        Assert.Equal("38h", Ps5Backport.App.Views.GraphCanvas.Word(text, 31));
        Assert.Null(Ps5Backport.App.Views.GraphCanvas.Word(text, 7));    // the comma
        Assert.Null(Ps5Backport.App.Views.GraphCanvas.Word(text, -1));
        Assert.Equal([4, 20, 24], Ps5Backport.App.Views.GraphCanvas.Occurrences(text, "RBX"));
        Assert.Empty(Ps5Backport.App.Views.GraphCanvas.Occurrences("mov rbx2, ebx", "rbx"));   // whole words only
        Assert.Equal([5], Ps5Backport.App.Views.GraphCanvas.Occurrences("call sub_120810", "sub_120810"));
    }

    [Fact]
    public void CodeGraphListing_StacksBlocksInAddressOrder()
    {
        GraphLine Line(ulong at, string text) => new(at, 1, text, LineKind.Code, null);
        // blocks deliberately out of address order (as a stub chain's pieces can be); one edge
        var graph = new FunctionGraph([new FunctionRange(0x100, 0x20)],
            [new GraphBlock(0x100, [Line(0x100, "jmp loc_110")]), new GraphBlock(0x120, [Line(0x120, "ret")]), new GraphBlock(0x110, [Line(0x110, "nop"), Line(0x111, "jmp loc_120")])],
            [new GraphEdge(0, 2, EdgeKind.Jump), new GraphEdge(2, 1, EdgeKind.Jump)]);
        IReadOnlyList<SceneLine> Lines(GraphBlock b) => [.. b.Lines.Select(l => new SceneLine($"{l.Address:x}", l.Text, LineStyle.Code, null))];
        var listing = GraphScene.Listing(graph, "sub_100", Lines, focusAt: 0x111);
        Assert.True(listing.IsListing);
        Assert.Empty(listing.Edges);
        Assert.Equal(["sub_100", "loc_110", "loc_120"], listing.Blocks.Select(b => b.Header));
        Assert.All(listing.Blocks.Zip(listing.Blocks.Skip(1)), p => Assert.True(p.Second.Bounds.Top > p.First.Bounds.Bottom));
        Assert.Single(listing.Blocks.Select(b => b.Bounds.Width).Distinct());
        var loc110 = listing.Blocks[1].Bounds;
        Assert.Equal(new Avalonia.Point(loc110.Left, loc110.Top + GraphScene.HeaderHeight + GraphScene.LineHeight), listing.Focus);   // its second line
    }

    [Fact]
    public void CodeGraphPath_KeepsBlocksBetweenTheTwo()
    {
        GraphLine Line(ulong at) => new(at, 1, "nop", LineKind.Code, null);
        // 100 -> 110 -> 120 -> 110 (loop), 100 -> 130 (dead end)
        var graph = new FunctionGraph([new FunctionRange(0x100, 0x40)],
            [new GraphBlock(0x100, [Line(0x100)]), new GraphBlock(0x110, [Line(0x110)]), new GraphBlock(0x120, [Line(0x120)]), new GraphBlock(0x130, [Line(0x130)])],
            [new GraphEdge(0, 1, EdgeKind.True), new GraphEdge(1, 2, EdgeKind.Jump), new GraphEdge(2, 1, EdgeKind.Jump), new GraphEdge(0, 3, EdgeKind.False)]);
        var scene = GraphScene.Listing(graph, "sub_100", b => [.. b.Lines.Select(l => new SceneLine($"{l.Address:x}", l.Text, LineStyle.Code, null))]);
        Assert.Equal([0x100UL, 0x110, 0x120], scene.PathBlocks(0x100, 0x120).Order().ToArray());
        Assert.Equal([0x110UL, 0x120], scene.PathBlocks(0x120, 0x110).Order().ToArray());   // back along the loop
        Assert.Empty(scene.PathBlocks(0x130, 0x100));
        Assert.Equal([0x130UL], scene.PathBlocks(0x130, 0x130));
    }

    [Fact]
    public void CodeGraphMinimap_FitsTheCanvas()
    {
        var canvas = new Avalonia.Size(600, 500);
        // a wide graph: 180 px wide, height by the same scale, bottom left above the legend
        var (area, sx, sy) = Ps5Backport.App.Views.GraphCanvas.MinimapArea(new Avalonia.Size(3600, 1800), canvas, listing: false)!.Value;
        Assert.Equal((180d, 90d, 0.05, 0.05), (area.Width, area.Height, sx, sy));
        Assert.Equal(12, area.X);
        Assert.True(area.Bottom <= canvas.Height - 40);
        // a very tall graph (like sub_113580): 180 px tall, stretched to 60 px wide
        (area, sx, sy) = Ps5Backport.App.Views.GraphCanvas.MinimapArea(new Avalonia.Size(1000, 36000), canvas, listing: false)!.Value;
        Assert.Equal((60d, 180d), (area.Width, area.Height));
        Assert.True(sx > sy);
        // a listing: a strip down the right edge, full height
        (area, _, sy) = Ps5Backport.App.Views.GraphCanvas.MinimapArea(new Avalonia.Size(900, 60000), canvas, listing: true)!.Value;
        Assert.Equal((48d, 476d, 540d), (area.Width, area.Height, area.X));
        Assert.Equal(476d / 60000, sy, 1e-9);
        Assert.Null(Ps5Backport.App.Views.GraphCanvas.MinimapArea(new Avalonia.Size(900, 900), new Avalonia.Size(300, 200), listing: false));
    }

    [Fact]
    public void CodeGraphExport_WritesTextAndSvg()
    {
        GraphLine Line(ulong at, string text) => new(at, 1, text, LineKind.Code, null);
        var graph = new FunctionGraph([new FunctionRange(0x100, 0x20)],
            [new GraphBlock(0x100, [Line(0x100, "lea rsi, [4895C2Fh]"), Line(0x101, "jne loc_110")]), new GraphBlock(0x110, [Line(0x110, "ret")])],
            [new GraphEdge(0, 1, EdgeKind.True)]);
        IReadOnlyList<SceneLine> Lines(GraphBlock b) => [.. b.Lines.Select(l => new SceneLine($"{l.Address:x}", l.Text,
            l.Address == 0x110 ? LineStyle.Patched : LineStyle.Code, null,
            l.Address == 0x100 ? [new LineTag("\"a<b & c\"", TagKind.Data)] : null))];
        var scene = GraphScene.Build(graph, "sub_100", Lines);

        string text = scene.ToText(["sub_100 · 0x100", "eboot.bin"]);
        Assert.StartsWith("; sub_100 · 0x100\n; eboot.bin\n", text.ReplaceLineEndings("\n"));
        Assert.Contains("    100  lea rsi, [4895C2Fh]    ; \"a<b & c\"", text);
        Assert.Contains("    ; → loc_110", text);
        Assert.True(text.IndexOf("sub_100:", StringComparison.Ordinal) < text.IndexOf("loc_110:", StringComparison.Ordinal));

        var palette = new SvgPalette("#000000", "#111111", "#222222", "#333333", "#eeeeee", "#888888", "#00ffff", "#ff00ff", "#ffaa00", "#ff4444", "#44ff44", "Cascadia Mono");
        var svg = System.Xml.Linq.XDocument.Parse(scene.ToSvg(palette, "sub_100 <test>"));
        System.Xml.Linq.XNamespace ns = "http://www.w3.org/2000/svg";
        Assert.Equal("sub_100 <test>", svg.Root!.Element(ns + "title")!.Value);
        Assert.Equal(2, svg.Root.Elements(ns + "g").Count());
        Assert.Single(svg.Root.Elements(ns + "polyline"), e => (string?)e.Attribute("stroke") == "#44ff44");   // the true edge
        Assert.Contains(svg.Descendants(ns + "text"), t => t.Value == "\"a<b & c\"");
        Assert.Contains(svg.Descendants(ns + "rect"), r => (string?)r.Attribute("fill") == "#ff00ff");          // the patched line
    }

    [Fact]
    public async Task CodeGraphVersions_MatchAnotherBuild()
    {
        var graph = _vm.CodeGraph;
        graph.EbootPath = Samples.Backports("PPSA23566", "decrypted", "eboot.bin");   // v1.004
        graph.ComparePath = Samples.Backports("PPSA23566", "dump", "eboot.bin");      // v1.005: another build, not a backport
        await graph.Reload();
        Assert.True(graph.HasImage, graph.Status);
        Assert.True(graph.IsVersions);
        Assert.True(graph.IsCompare);
        Assert.Equal("698 changed · 466 new · 295 removed", graph.VersionChip);
        Assert.Equal(("OLD BUILD", "NEW BUILD"), (graph.OriginalLabel, graph.ShownLabel));
        Assert.Equal(295, graph.OldOnlyRows.Count);
        graph.Filter = FunctionFilter.Changed;
        Assert.Equal(698 + 466, graph.Rows.Count);
        Assert.Contains(graph.Rows, r => r.Range.Start == 0x1843740 && r.Chip == "modified");
        graph.Navigate(0x1843740);
        for (int i = 0; i < 400 && graph.FunctionTitle != "sub_1843740"; i++)
            await Task.Delay(25, TestContext.Current.CancellationToken);   // the graph and inspector are built in the background
        Assert.StartsWith("Changed since the old build (8", graph.MatchText);
        Assert.Contains("sub_183e7a0 at 0x183e7a0, matched by the same strings and imports", graph.MatchText);
    }

    [Fact]
    public async Task CodeGraphFollow_OpensTheFakelibAndReturns()
    {
        var graph = _vm.CodeGraph;
        string eboot = Samples.Backports("PPSA01317", "4xx+", "USA", "eboot.bin");
        graph.EbootPath = eboot;
        await graph.Reload();
        Assert.True(graph.HasImage, graph.Status);
        Assert.False(graph.CanGoBack);   // a fresh load: nothing to go back to
        graph.Navigate(0x1636e10);       // calls sceAgcInit_0090

        await graph.FollowImport("23LRUSvYu1M");
        Assert.EndsWith(Path.Combine("fakelib", "libSceAgc.sprx"), graph.EbootPath);
        Assert.True(graph.InModule);
        Assert.Equal("eboot.bin › libSceAgc.sprx", graph.ModuleTrail);
        Assert.Equal("Back to eboot.bin", graph.ReturnText);
        Assert.Equal(0x9990UL, graph.Recent[0].Target);
        Assert.True(graph.CanGoBack);    // Back at the start of the module returns

        await graph.ReturnToModuleCommand.ExecuteAsync(null);
        Assert.False(graph.InModule);
        Assert.Equal(eboot, graph.EbootPath);
        Assert.True(graph.HasImage);
        Assert.Equal(0x1636e10UL, graph.Recent[0].Target);   // the function it was on

        // choosing an eboot again starts a new trail
        await graph.FollowImport("23LRUSvYu1M");
        graph.EbootPath = eboot;
        await graph.Reload();
        Assert.False(graph.InModule);
    }

    [Fact]
    public async Task CodeGraphHistory_BackForwardAndRecent()
    {
        var graph = _vm.CodeGraph;
        graph.EbootPath = Samples.Work("NOT_BACKPORT_elf", "eboot.elf");
        await graph.Reload();
        Assert.True(graph.HasImage, graph.Status);
        ulong[] functions = [0x113580, 0x120810, 0x2b530];
        foreach (ulong f in functions)
            graph.Navigate(f);
        Assert.Equal([.. functions.Reverse()], graph.Recent.Take(3).Select(r => r.Target!.Value));
        graph.BackCommand.Execute(null);
        graph.BackCommand.Execute(null);
        Assert.Equal(0x113580UL, graph.Recent[0].Target);
        Assert.True(graph.CanGoForward);
        graph.ForwardCommand.Execute(null);
        Assert.Equal(0x120810UL, graph.Recent[0].Target);
        Assert.True(graph.CanGoForward);
        graph.Navigate(0x118400);   // a new path drops what was ahead
        Assert.False(graph.CanGoForward);
        Assert.Equal(4, graph.Recent.Count(r => r.Target is 0x113580 or 0x120810 or 0x2b530 or 0x118400));
    }

    [Fact]
    public async Task CodeGraphLinks_OpenTheGameAndItsImports()
    {
        string eboot = Path.Combine(_game, "eboot.bin");
        await _vm.AnalyzeCommand.ExecuteAsync(null);
        var import = _vm.Imports.First();
        _vm.SelectedImport = import;
        Assert.True(_vm.ShowInCodeGraphCommand.CanExecute(null));

        // Imports tab -> Code graph: the game's eboot, compared with Studio's cook while no backport is written
        await _vm.ShowInCodeGraphCommand.ExecuteAsync(null);
        var graph = _vm.CodeGraph;
        Assert.Equal(AppPage.CodeGraph, _vm.Page);
        Assert.Equal(eboot, graph.EbootPath);
        Assert.True(graph.ComparePreview);
        Assert.True(graph.CanOpenImports);   // the synthetic eboot has no unwind table, so no graph (Status says why)

        // Code graph -> Imports tab: filters cleared, the import selected
        _vm.ImportFilter = "Unresolved";
        _vm.ImportSearch = "does-not-match";
        graph.OpenInImportsCommand.Execute(new LinkRow(import.Name, "", null, Nid: import.Nid));
        Assert.Equal(AppPage.Backport, _vm.Page);
        Assert.Equal(2, _vm.SelectedTab);
        Assert.Equal("All", _vm.ImportFilter);
        Assert.Equal(import.Nid, _vm.SelectedImport?.Nid);

        // a backport in the output folder that changed eboot.bin: Current game compares with it
        Directory.CreateDirectory(_vm.OutputFolder);
        File.Copy(eboot, Path.Combine(_vm.OutputFolder, "eboot.bin"));
        await graph.CurrentGameCommand.ExecuteAsync(null);
        Assert.False(graph.ComparePreview);
        Assert.Equal(Path.Combine(_vm.OutputFolder, "eboot.bin"), graph.ComparePath);

        // another eboot shown: imports cannot open in this game's analysis
        graph.EbootPath = Path.Combine(_root, "other", "eboot.bin");
        Assert.False(graph.CanOpenImports);
    }
}
