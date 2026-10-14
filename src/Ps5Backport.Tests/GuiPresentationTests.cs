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
}
