using System.Text.Json;
using Ps5Backport.App.ViewModels;
using Ps5Backport.Core;

namespace Ps5Backport.Tests;

public sealed class DiagnosticTests : IDisposable
{
    readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ps5backport-diagnostics-" + Guid.NewGuid().ToString("N"))).FullName;
    string Logs => Path.Combine(_root, "logs");

    [Fact]
    public void Rotation_IsBounded_AndRetainsNewestCompleteJsonRecords()
    {
        using (var log = new DiagnosticLog(Logs, 2048, 3))
        {
            for (int i = 0; i < 500; i++) log.Write(new("synthetic", "event", Message: "record " + i));
            Assert.Null(log.Failure);
        }
        string[] files = Directory.GetFiles(Logs, "*.jsonl");
        Assert.Equal(3, files.Length);
        Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, 2048));
        var entries = files.SelectMany(File.ReadAllLines).Select(s => JsonSerializer.Deserialize<DiagnosticEvent>(s)!).ToArray();
        Assert.DoesNotContain(entries, e => e.Message == "record 0");
        Assert.Contains(entries, e => e.Message == "record 499");
        Assert.Equal("record 499", JsonSerializer.Deserialize<DiagnosticEvent>(File.ReadAllLines(Path.Combine(Logs, "events.jsonl"))[^1])!.Message);
    }

    [Fact]
    public void CompetingWriter_FailsOpen_AndLeaseIsReleasedOnDispose()
    {
        using (var first = new DiagnosticLog(Logs))
        using (var second = new DiagnosticLog(Logs))
        {
            Assert.Null(first.Failure);
            Assert.NotNull(second.Failure);
            second.Write(new("synthetic", "ignored"));
            first.Write(new("synthetic", "kept"));
        }
        using var reopened = new DiagnosticLog(Logs);
        Assert.Null(reopened.Failure);
        Assert.Equal("kept", Read().Single().Event);
    }

    [Fact]
    public void DiskFailure_DoesNotFailTheBackport()
    {
        using var log = new DiagnosticLog(Logs);
        Directory.CreateDirectory(Path.Combine(Logs, "events.jsonl"));
        var result = Pipeline.Backport(Game(), Path.Combine(_root, "out"), Options(), Knowledge.Default, cancel: TestContext.Current.CancellationToken, diagnostics: log);
        Assert.Equal(OperationState.Completed, result.State);
        Assert.NotNull(log.Failure);
        Assert.False(Directory.Exists(Path.Combine(_root, "out")));
    }

    [Fact]
    public void StructuredEvents_CorrelateModulesStagesDurationAndExceptions()
    {
        using var log = new DiagnosticLog(Logs);
        string game = Game();
        File.WriteAllBytes(Path.Combine(game, "broken.sprx"), [1, 2, 3]);
        var result = Pipeline.Backport(game, Path.Combine(_root, "out"), Options(), Knowledge.Default, cancel: TestContext.Current.CancellationToken, diagnostics: log);
        Assert.Equal(OperationState.Failed, result.State);
        var events = Read();
        Assert.Single(events.Select(e => e.RunId).Distinct());
        Assert.All(events, e => Assert.NotNull(e.RunId));
        Assert.Contains(events, e => e.Event == "started" && e.Stage == "preflight");
        Assert.Contains(events, e => e.Event == "stage_finished" && e.Stage == "patching" && e.Module == "eboot.bin" && e.DurationMilliseconds >= 0);
        var failure = Assert.Single(events, e => e.Event == "module_failed");
        Assert.Equal(("loading", "broken.sprx", "data"), (failure.Stage, failure.Module, failure.ErrorCategory));
        Assert.Contains("InvalidDataException", failure.ExceptionType!);
        Assert.Contains("unknown file format", failure.ExceptionContext!);
        Assert.Equal("failed", events[^1].Event);
        Assert.True(events[^1].DurationMilliseconds >= 0);
    }

    [Fact]
    public async Task PreCancelledOperation_RecordsCancellationAndRunId()
    {
        using var log = new DiagnosticLog(Logs);
        using var operation = new BackportOperation(Game(), Path.Combine(_root, "out"), Options(), Knowledge.Default, diagnostics: log);
        operation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.ExecuteAsync(cancel: TestContext.Current.CancellationToken));
        var events = Read();
        Assert.Equal(operation.RunId, events[^1].RunId);
        Assert.Equal(("cancelled", "cancelled"), (events[^1].Event, events[^1].ErrorCategory));
    }

    [Fact]
    public void LocalUiRecords_BoundStringsAndEscapeNewlines_AndSurviveClear()
    {
        using var log = new DiagnosticLog(Logs);
        var vm = new LogViewModel(log);
        vm.Error("private\npath " + new string('x', 10000), exception: new IOException("test disk failure"));
        vm.Clear();
        Assert.Empty(vm.Visible);
        var entry = Assert.Single(Read());
        Assert.Contains("\n", entry.Message!);
        Assert.True(entry.Message!.Length <= 2048);
        Assert.Equal("io", entry.ErrorCategory);
        Assert.Contains("IOException", entry.ExceptionContext!);
        Assert.Single(File.ReadAllLines(Path.Combine(Logs, "events.jsonl")));
    }

    [Fact]
    public void UiHistory_IsBounded_CountsDescribeRetainedEntries_AndFiltersUseOneReset()
    {
        var log = new LogViewModel();
        for (int i = 0; i < 2400; i++) log.Add((LogLevel)(i % 3), "record " + i);
        Assert.Equal(LogViewModel.MaximumEntries, log.Visible.Count);
        Assert.Equal("record 400", log.Visible[0].Message);
        Assert.Equal("record 2399", log.Visible[^1].Message);
        Assert.Equal(667, log.WarningCount);
        Assert.Equal(667, log.ErrorCount);
        int resets = 0;
        log.Visible.CollectionChanged += (_, e) => { Assert.Equal(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, e.Action); resets++; };
        log.Filter = "errors";
        Assert.Equal(1, resets);
        Assert.Equal(667, log.Visible.Count);
        Assert.All(log.Visible, e => Assert.Equal(LogLevel.Error, e.Level));
        Assert.DoesNotContain("record 2\n", log.Text());
        log.Clear();
        Assert.Equal((0, 0), (log.ErrorCount, log.WarningCount));
    }

    BackportOptions Options()
    {
        string reference = Directory.CreateDirectory(Path.Combine(_root, "reference")).FullName;
        FirmwareDb.Save(new FirmwareExports("4", "synthetic", false, new()), Path.Combine(reference, "4.json"));
        return new() { Firmware = "4", ReferenceDir = reference, DryRun = true, KnownFixes = false };
    }
    string Game()
    {
        string game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
        File.WriteAllBytes(Path.Combine(game, "eboot.bin"), SyntheticBinary.Elf(imports: true));
        return game;
    }
    DiagnosticEvent[] Read() => File.ReadAllLines(Path.Combine(Logs, "events.jsonl")).Select(s => JsonSerializer.Deserialize<DiagnosticEvent>(s)!).ToArray();
    public void Dispose() => Directory.Delete(_root, recursive: true);
}
