using System.Diagnostics;
using Ps5Backport.App;
using Ps5Backport.App.ViewModels;
using Ps5Backport.Core;

namespace Ps5Backport.Tests;

public sealed class OperationLifecycleTests : IDisposable
{
    readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ps5backport-operation-" + Guid.NewGuid().ToString("N"))).FullName;
    string Game => Path.Combine(_root, "game");
    string Output => Path.Combine(_root, "out");
    string Reference => Path.Combine(_root, "reference");
    BackportOptions Options => new() { Firmware = "4", ReferenceDir = Reference, KnownFixes = false, DryRun = true };

    public OperationLifecycleTests()
    {
        Directory.CreateDirectory(Game);
        Directory.CreateDirectory(Reference);
        File.WriteAllBytes(Path.Combine(Game, "eboot.bin"), SyntheticBinary.Elf(imports: true));
        FirmwareDb.Save(new FirmwareExports("4", "synthetic", false, new()), Path.Combine(Reference, "4.00.json"));
    }

    [Fact]
    public async Task SuccessfulOperation_IsOneShot_AndAnalysisWritesNothing()
    {
        using var operation = new BackportOperation(Game, Output, Options, Knowledge.Default);
        Assert.Equal(OperationState.Pending, operation.State);
        var result = await operation.ExecuteAsync(cancel: TestContext.Current.CancellationToken);
        Assert.Equal(OperationState.Completed, operation.State);
        Assert.Equal(OperationState.Completed, result.State);
        Assert.NotNull(result.Analysis);
        Assert.False(result.Published);
        Assert.False(Directory.Exists(Output));
        Assert.DoesNotContain(Directory.GetFileSystemEntries(_root), p => Path.GetFileName(p).StartsWith(".ps5bp-"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => operation.ExecuteAsync(cancel: TestContext.Current.CancellationToken));
        Assert.Equal(OperationState.Completed, operation.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledOperation_NeverReportsDoneOrPublishes(bool duringPatch)
    {
        using var operation = new BackportOperation(Game, Output, Options with { DryRun = false }, Knowledge.Default);
        var seen = new List<BackportProgress>();
        if (!duringPatch) operation.Cancel();
        var progress = new SyncProgress(p =>
        {
            seen.Add(p);
            if (p.Stage == "patching")
            {
                Assert.Equal(OperationState.Running, operation.State);
                Assert.Throws<InvalidOperationException>(operation.Dispose);
                operation.Cancel();
            }
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.ExecuteAsync(progress, TestContext.Current.CancellationToken));
        Assert.Equal(OperationState.Cancelled, operation.State);
        Assert.DoesNotContain(seen, p => p.Stage == "done");
        Assert.False(Directory.Exists(Output));
        Assert.DoesNotContain(Directory.GetDirectories(_root), p => Path.GetFileName(p).Contains("stage"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failures_HaveTerminalFailedState(bool malformedModule)
    {
        if (malformedModule) File.WriteAllBytes(Path.Combine(Game, "eboot.bin"), [1, 2, 3]);
        using var operation = new BackportOperation(Game, Output,
            malformedModule ? Options : Options with { Firmware = "invalid" }, Knowledge.Default);
        var seen = new List<BackportProgress>();
        if (malformedModule)
        {
            var result = await operation.ExecuteAsync(new SyncProgress(seen.Add), TestContext.Current.CancellationToken);
            Assert.Null(result.Analysis);
            Assert.Equal(OperationState.Failed, result.State);
            Assert.Equal("failed", seen.Last().Stage);
            Assert.NotEqual(seen.Last().Count, seen.Last().Index);
        }
        else await Assert.ThrowsAsync<FormatException>(() => operation.ExecuteAsync(cancel: TestContext.Current.CancellationToken));
        Assert.Equal(OperationState.Failed, operation.State);
        Assert.DoesNotContain(seen, p => p.Stage == "done");
    }

    [Fact]
    public async Task RequestCollections_AreCapturedBeforeBackgroundWork()
    {
        var selected = new List<string>();
        var stubs = new List<string>();
        using var operation = new BackportOperation(Game, Output, Options with { Options = selected, StubNids = stubs }, Knowledge.Default);
        selected.Add("not-an-option");
        stubs.Add(SyntheticBinary.Nid);
        var result = await operation.ExecuteAsync(cancel: TestContext.Current.CancellationToken);
        Assert.Empty(result.Options);
        Assert.Empty(result.Modules.Single().StubbedNow);
    }

    [Fact]
    public void RuleSnapshot_DeepCopiesNestedBytesAndCollections()
    {
        var knowledge = new Knowledge(Repo.Need("data"));
        var snapshot = RunSnapshot.Capture(Game, Output, Options, knowledge);
        var stub = knowledge.Stubs.Values.First(s => s.Bytes.Length > 0);
        byte before = snapshot.Knowledge.Stubs[stub.Name].Bytes[0];
        stub.Bytes[0] ^= 0xff;
        Assert.Equal(before, snapshot.Knowledge.Stubs[stub.Name].Bytes[0]);
        var fix = knowledge.FixList.First(f => f.Insns.Any(i => i.Bytes.Length > 0));
        var instruction = fix.Insns.First(i => i.Bytes.Length > 0);
        var captured = snapshot.Knowledge.Fixes[fix.Nid].Insns.First(i => i.Bytes.Length > 0).Bytes.ToArray();
        instruction.Bytes[0] ^= 0xff;
        Assert.Equal(captured, snapshot.Knowledge.Fixes[fix.Nid].Insns.First(i => i.Bytes.Length > 0).Bytes);
        ((Dictionary<string, FixOption>)knowledge.Options).Clear();
        Assert.NotEmpty(snapshot.Knowledge.Options);
    }

    [Theory]
    [InlineData("module")]
    [InlineData("module-added")]
    [InlineData("reference")]
    [InlineData("param")]
    [InlineData("settings")]
    [InlineData("rules")]
    [InlineData("ampr-warning")]
    [InlineData("library")]
    [InlineData("names")]
    [InlineData("introduced")]
    public void StaleAnalysis_IsRefusedBeforeCreatingOutput(string change)
    {
        var knowledge = new Knowledge(Repo.Need("data"));
        var options = Options;
        if (change == "library") options = options with { ExtraFakelib = Directory.CreateDirectory(Path.Combine(_root, "libraries")).FullName };
        var analyzed = Pipeline.Backport(Game, Output, options, knowledge, cancel: TestContext.Current.CancellationToken);
        var cook = options with { DryRun = false };
        switch (change)
        {
            case "module": File.WriteAllBytes(Path.Combine(Game, "eboot.bin"), SyntheticBinary.Elf(imports: true, exports: true)); break;
            case "module-added": File.WriteAllBytes(Path.Combine(Game, "new.sprx"), SyntheticBinary.Elf()); break;
            case "reference": FirmwareDb.Save(new FirmwareExports("4", "changed", false, new() { ["synthetic"] = new() { SyntheticBinary.Nid } }), Path.Combine(Reference, "4.00.json")); break;
            case "param": Directory.CreateDirectory(Path.Combine(Game, "sce_sys")); File.WriteAllText(Path.Combine(Game, "sce_sys", "param.json"), "{}"); break;
            case "settings": cook = cook with { ExtendText = false }; break;
            case "rules": knowledge.Stubs.Values.First(s => s.Bytes.Length > 0).Bytes[0] ^= 0xff; break;
            case "ampr-warning": File.WriteAllText(Path.Combine(Game, "ampr_emu.index"), "new index"); break;
            case "library": File.WriteAllBytes(Path.Combine(options.ExtraFakelib!, "new.sprx"), SyntheticBinary.Elf(exports: true)); break;
            case "names": File.WriteAllText(Path.Combine(_root, "names.csv"), SyntheticBinary.Nid + " renamed"); cook = cook with { Names = new NameDb(Path.Combine(_root, "names.csv")) }; break;
            case "introduced": ((Dictionary<string, (string Firmware, string Source)>)knowledge.Introduced)[knowledge.Introduced.Keys.First()] = ("99.00", "changed"); break;
        }
        var exception = Assert.Throws<InvalidOperationException>(() => Pipeline.Backport(Game, Output, cook, knowledge,
            cancel: TestContext.Current.CancellationToken, analysis: analyzed.Analysis));
        Assert.Contains("Analysis is stale", exception.Message);
        Assert.False(Directory.Exists(Output));
        Assert.DoesNotContain(Directory.GetFileSystemEntries(_root), p => Path.GetFileName(p).StartsWith(".ps5bp-"));
    }

    [Fact]
    public void CurrentAnalysis_AllowsFullCook_WithLegacyReportShape()
    {
        var analyzed = Pipeline.Backport(Game, Output, Options, Knowledge.Default, cancel: TestContext.Current.CancellationToken);
        var cooked = Pipeline.Backport(Game, Output, Options with { DryRun = false }, Knowledge.Default,
            cancel: TestContext.Current.CancellationToken, analysis: analyzed.Analysis);
        Assert.True(cooked.Published);
        Assert.Equal(analyzed.Modules.Single().Statuses, cooked.Modules.Single().Statuses);
        Assert.Equal(analyzed.Modules.Single().SdkAfter, cooked.Modules.Single().SdkAfter);
        Assert.Null(cooked.Analysis);
        Assert.DoesNotContain("state", Pipeline.ToJson(cooked).Select(p => p.Key), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("analysis", Pipeline.ToJson(cooked).Select(p => p.Key), StringComparer.OrdinalIgnoreCase);
        Publication.Verify(Output);
    }

    [Fact]
    public async Task UiFailure_AndCloseNeverShowSuccessProgress()
    {
        var vm = new MainViewModel(new AppSettings { FirmwareDbDir = Reference, FakelibStoreDir = Path.Combine(_root, "absent-store") });
        vm.LoadGame(Game);
        vm.SelectedFirmware = "4";
        File.WriteAllBytes(Path.Combine(Game, "eboot.bin"), [1, 2, 3]);
        await vm.AnalyzeCommand.ExecuteAsync(null);
        Assert.Equal(OperationState.Failed, vm.OperationState);
        Assert.Equal(0, vm.Progress);
        Assert.StartsWith("Incomplete", vm.StatusText);
        await vm.CancelAndWaitAsync();
        Assert.False(vm.Busy);
        Assert.False(vm.AnalyzeCommand.CanExecute(null));
        vm.Dispose();
    }

    [Fact]
    public async Task ClosingWhileBusy_CancelsAndAwaitsTheRun()
    {
        var vm = new MainViewModel(new AppSettings { FirmwareDbDir = Reference, FakelibStoreDir = Path.Combine(_root, "absent-store") });
        vm.LoadGame(Game);
        var run = vm.AnalyzeCommand.ExecuteAsync(null);
        Assert.True(vm.Busy);
        await vm.CancelAndWaitAsync();
        await run;
        Assert.False(vm.Busy);
        Assert.Equal(OperationState.Cancelled, vm.OperationState);
        Assert.Equal(0, vm.Progress);
        Assert.Equal("Cancelled", vm.StatusText);
        Assert.False(vm.CookCommand.CanExecute(null));
        vm.Dispose();
    }

    [Fact]
    public async Task DelayedProgress_CannotOverwriteTerminalUiState()
    {
        var vm = new MainViewModel(new AppSettings { FirmwareDbDir = Reference, FakelibStoreDir = Path.Combine(_root, "absent-store") });
        vm.LoadGame(Game);
        var queue = new ProgressQueue();
        var previous = SynchronizationContext.Current;
        Task run;
        try
        {
            SynchronizationContext.SetSynchronizationContext(queue);
            run = vm.AnalyzeCommand.ExecuteAsync(null);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        await run;
        Assert.Equal(OperationState.Completed, vm.OperationState);
        string terminal = vm.StatusText;
        Assert.True(queue.Drain() > 0);
        Assert.Equal(terminal, vm.StatusText);
        Assert.Equal(100, vm.Progress);
        vm.Dispose();
    }

    [Fact]
    public async Task UiCook_RefusesChangedSettingsAfterAnalysis()
    {
        var vm = new MainViewModel(new AppSettings { FirmwareDbDir = Reference, FakelibStoreDir = Path.Combine(_root, "absent-store") });
        vm.LoadGame(Game);
        await vm.AnalyzeCommand.ExecuteAsync(null);
        vm.ExtendText = !vm.ExtendText;
        vm.OutputFolder = Output;
        await vm.CookCommand.ExecuteAsync(null);
        Assert.Equal(OperationState.Failed, vm.OperationState);
        Assert.Equal(0, vm.Progress);
        Assert.False(Directory.Exists(Output));
        Assert.Contains("Analysis is stale", vm.Log.Text());
        vm.Dispose();
    }

    [Fact]
    public async Task Closing_AwaitsAlreadyOwnedPageWork()
    {
        var vm = new MainViewModel(new AppSettings { FirmwareDbDir = Reference });
        var picker = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PickFolder = _ => picker.Task;
        vm.Database.ImportFirmware = "4";
        vm.Database.ImportSource = "synthetic";
        vm.Database.ImportGenstub = true;
        string empty = Directory.CreateDirectory(Path.Combine(_root, "empty-genstub")).FullName;
        var import = vm.Database.ImportCommand.ExecuteAsync(null);
        var close = vm.CancelAndWaitAsync();
        Assert.False(close.IsCompleted);
        picker.SetResult(empty);
        await close;
        await import;
        Assert.False(vm.Database.Busy);
        Assert.Contains("nothing written", vm.Log.Text());
        vm.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InnerScans_RespondToCancellationWithinBudget(bool codeReferences)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var entered = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        byte[] data = new byte[128 * 1024 * 1024];
        var elf = new SceElf(SyntheticBinary.Elf(payload: data, imports: true));
        var statuses = Imports.Classify(elf, new HashSet<string>(), new HashSet<string>(), true);
        var task = Task.Run(() =>
        {
            entered.Set();
            proceed.Wait(TestContext.Current.CancellationToken);
            if (codeReferences) Imports.CodeReferences(elf, statuses, cancel.Token);
            else FixApplier.ReplaceAll(data, new FixPattern("dense", [0], [1], []), cancel.Token);
        }, TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        proceed.Set();
        await Task.Delay(10, TestContext.Current.CancellationToken);
        var timer = Stopwatch.StartNew();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void ScanWindows_PreserveBoundaryMatchesAndAlignment(int step)
    {
        byte[] data = new byte[2 * 1024 * 1024 + 10];
        byte[] find = [7, 8, 9, 10];
        foreach (int at in new[] { 1024 * 1024 - 2, 1024 * 1024 + 4 }) find.CopyTo(data, at);
        int count = FixApplier.ReplaceAll(data, new FixPattern("boundary", find, [1, 2, 3, 4], []), TestContext.Current.CancellationToken, step: step);
        Assert.Equal(step == 1 ? 2 : 1, count);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, data.AsSpan(1024 * 1024 + 4, 4).ToArray());
        Assert.Equal(step == 1 ? new byte[] { 1, 2, 3, 4 } : find, data.AsSpan(1024 * 1024 - 2, 4).ToArray());
    }

    sealed class SyncProgress(Action<BackportProgress> report) : IProgress<BackportProgress>
    {
        public void Report(BackportProgress value) => report(value);
    }

    sealed class ProgressQueue : SynchronizationContext
    {
        readonly List<Action> _pending = [];
        public override void Post(SendOrPostCallback callback, object? state)
        {
            if (state is BackportProgress)
            {
                lock (_pending) _pending.Add(() => callback(state));
            }
            else ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
        internal int Drain()
        {
            lock (_pending)
            {
                foreach (var callback in _pending) callback();
                return _pending.Count;
            }
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
