using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ps5Backport.Core;

namespace Ps5Backport.Tests;

/// <summary>Staged publication, batch rollback, provenance, aliases and actual process-interruption recovery.</summary>
public sealed class PublicationTests : IDisposable
{
    readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ps5backport-publication-" + Guid.NewGuid().ToString("N"))).FullName;
    string Output => Path.Combine(_root, "out");

    void Previous()
    {
        Directory.CreateDirectory(Output);
        File.WriteAllText(Path.Combine(Output, "old.bin"), "previous");
    }

    [Theory]
    [InlineData("file-flushed")]
    [InlineData("prepared")]
    [InlineData("backup-moved")]
    [InlineData("stage-moved")]
    public void PublicationFault_RestoresPriorResult(string failAt)
    {
        Previous();
        Assert.Throws<IOException>(() =>
        {
            using var transaction = new DirectoryTransaction(Output, stage =>
            {
                if (stage == failAt) throw new IOException("injected disk-full/write failure", unchecked((int)0x80070070));
            });
            File.WriteAllText(Path.Combine(transaction.Stage, "new.bin"), "replacement");
            transaction.Commit(TestContext.Current.CancellationToken);
        });
        Assert.Equal("previous", File.ReadAllText(Path.Combine(Output, "old.bin")));
        Assert.False(File.Exists(Path.Combine(Output, "new.bin")));
        Assert.Empty(Directory.GetDirectories(_root, "*.stage"));
        Assert.Empty(Directory.GetFiles(_root, "*.journal.json"));
    }

    [Fact]
    public void CancellationBeforeCommit_PreservesDestination()
    {
        Previous();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        using (var transaction = new DirectoryTransaction(Output))
        {
            File.WriteAllText(Path.Combine(transaction.Stage, "new.bin"), "replacement");
            Assert.Throws<OperationCanceledException>(() => transaction.Commit(cancel.Token));
        }
        Assert.Equal("previous", File.ReadAllText(Path.Combine(Output, "old.bin")));
    }

    [Fact]
    public void LockedStagedFile_AbortsPublicationAndPreservesDestination()
    {
        Previous();
        using (var transaction = new DirectoryTransaction(Output))
        {
            string file = Path.Combine(transaction.Stage, "new.bin");
            File.WriteAllText(file, "replacement");
            using (var reader = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.Throws<IOException>(() => transaction.Commit(TestContext.Current.CancellationToken));
        }
        Assert.Equal("previous", File.ReadAllText(Path.Combine(Output, "old.bin")));
        Assert.Empty(Directory.GetDirectories(_root, "*.stage"));
    }

    [Fact]
    public void InvalidJournal_IsRetainedForManualInspection()
    {
        Previous();
        using (var child = Worker("stage-created"))
        {
            Assert.True(child.WaitForExit(30000));
            Assert.Equal(37, child.ExitCode);
        }
        string journal = Assert.Single(Directory.GetFiles(_root, "*.journal.json"));
        File.WriteAllText(journal, "{\"Version\":1,\"Id\":\"../foreign\"}");
        Assert.Throws<InvalidDataException>(() => Publication.Recover(Output));
        Assert.Equal("previous", File.ReadAllText(Path.Combine(Output, "old.bin")));
        Assert.True(File.Exists(journal));
        Assert.Single(Directory.GetDirectories(_root, "*.stage"));
    }

    [Fact]
    public void TwoWriters_CannotAcquireTheSameDestination()
    {
        Previous();
        using var transaction = new DirectoryTransaction(Output);
        Assert.Throws<IOException>(() => new DirectoryTransaction(Output));
        using var child = Worker("blocked");
        Assert.True(child.WaitForExit(30000));
        Assert.Equal(39, child.ExitCode);
    }

    [Fact]
    public void ReusedDestination_ContainsOnlyNewArtifactsAndRetainsBackup()
    {
        Previous();
        string backup;
        using (var transaction = new DirectoryTransaction(Output))
        {
            backup = transaction.Backup;
            File.WriteAllText(Path.Combine(transaction.Stage, "new.bin"), "replacement");
            transaction.Commit(TestContext.Current.CancellationToken);
        }
        Assert.False(File.Exists(Path.Combine(Output, "old.bin")));
        Assert.Equal("previous", File.ReadAllText(Path.Combine(backup, "old.bin")));
        Publication.Verify(Output);
        File.WriteAllText(Path.Combine(Output, "new.bin"), "tampered");
        Assert.Throws<InvalidDataException>(() => Publication.Verify(Output));
    }

    [Fact]
    public void TamperingAfterPreparation_IsRefusedBeforeRename()
    {
        Previous();
        DirectoryTransaction? transaction = null;
        using (transaction = new DirectoryTransaction(Output, stage =>
        {
            if (stage == "prepared") File.WriteAllText(Path.Combine(transaction!.Stage, "new.bin"), "tampered");
        }))
        {
            File.WriteAllText(Path.Combine(transaction.Stage, "new.bin"), "replacement");
            Assert.Throws<InvalidDataException>(() => transaction.Commit(TestContext.Current.CancellationToken));
        }
        Assert.Equal("previous", File.ReadAllText(Path.Combine(Output, "old.bin")));
    }

    [Theory]
    [InlineData("stage-created", true)]
    [InlineData("backup-moved", true)]
    [InlineData("stage-moved", true)]
    [InlineData("committed", true)]
    [InlineData("stage-created", false)]
    [InlineData("stage-moved", false)]
    [InlineData("committed", false)]
    public void AbruptProcessExit_IsRecovered(string exitAt, bool existing)
    {
        if (existing) Previous();
        using (var child = Worker(exitAt))
        {
            Assert.True(child.WaitForExit(30000));
            Assert.Equal(37, child.ExitCode);
        }
        Publication.Recover(Output);
        Publication.Recover(Output); // idempotence, including a recovery completed by another process
        if (exitAt == "committed")
        {
            Assert.Equal("replacement", File.ReadAllText(Path.Combine(Output, "new.bin")));
            Publication.Verify(Output);
        }
        else if (existing)
            Assert.Equal("previous", File.ReadAllText(Path.Combine(Output, "old.bin")));
        else
            Assert.False(Directory.Exists(Output));
        Assert.Empty(Directory.GetDirectories(_root, "*.stage"));
        Assert.Empty(Directory.GetFiles(_root, "*.journal.json"));
    }

    Process Worker(string exitAt)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(PublicationTests).Assembly.Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add("Ps5Backport.Tests.PublicationTests.InterruptionWorker");
        start.Environment["PS5BP_TRANSACTION_TEST_ROOT"] = _root;
        start.Environment["PS5BP_TRANSACTION_TEST_EXIT"] = exitAt;
        return Process.Start(start)!;
    }

    [Fact]
    public void InterruptionWorker()
    {
        string? root = Environment.GetEnvironmentVariable("PS5BP_TRANSACTION_TEST_ROOT");
        if (root is null) return; // this case is only active in the isolated child process
        string absolute = Path.GetFullPath(root);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), absolute, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("ps5backport-publication-", Path.GetFileName(absolute));
        string? exitAt = Environment.GetEnvironmentVariable("PS5BP_TRANSACTION_TEST_EXIT");
        if (exitAt == "blocked")
        {
            try { using var writer = new DirectoryTransaction(Path.Combine(absolute, "out")); }
            catch (IOException) { Environment.Exit(39); }
            Environment.Exit(40);
        }
        using var transaction = new DirectoryTransaction(Path.Combine(absolute, "out"), stage =>
        {
            if (stage == exitAt) Environment.Exit(37); // deliberately bypass Dispose/finally: actual interrupted process
        });
        File.WriteAllText(Path.Combine(transaction.Stage, "new.bin"), "replacement");
        transaction.Commit(TestContext.Current.CancellationToken);
        Environment.Exit(40);
    }

    (string Game, string Reference, Knowledge Knowledge) RunFixture()
    {
        string game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
        string reference = Directory.CreateDirectory(Path.Combine(_root, "reference")).FullName;
        string data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        foreach (string file in Directory.GetFiles(Paths.DataDir, "*.json"))
            File.Copy(file, Path.Combine(data, Path.GetFileName(file)));
        string fixesPath = Path.Combine(data, "fixes.json");
        var fixes = JsonNode.Parse(File.ReadAllText(fixesPath))!;
        fixes["patterns"] = JsonNode.Parse("""[{"name":"test","find":"0102","replace":"0304","files":["data.bin"]}]""");
        File.WriteAllText(fixesPath, fixes.ToJsonString());
        File.WriteAllBytes(Path.Combine(game, "data.bin"), [1, 2]);
        FirmwareDb.Save(new FirmwareExports("4", "synthetic", false, new()), Path.Combine(reference, "4.00.json"));
        return (game, reference, new Knowledge(data));
    }

    [Fact]
    public void Pipeline_PublishesVerifiedEvidenceAndReplacesStaleOutput()
    {
        Previous();
        var fixture = RunFixture();
        var options = new BackportOptions { Firmware = "4", ReferenceDir = fixture.Reference, ExtendText = false };
        var result = Pipeline.Backport(fixture.Game, Output, options, fixture.Knowledge, cancel: TestContext.Current.CancellationToken);
        Assert.True(result.Published);
        Assert.False(File.Exists(Path.Combine(Output, "old.bin")));
        Assert.Equal("previous", File.ReadAllText(Path.Combine(result.PreviousOutputBackup, "old.bin")));
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(Path.Combine(fixture.Game, "data.bin")));
        Publication.Verify(Output);
        using var evidence = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Output, Publication.EvidenceName)));
        var inputs = evidence.RootElement.GetProperty("Inputs");
        Assert.Equal(fixture.Knowledge.DocumentHashes["fixes.json"], inputs.GetProperty("knowledge:fixes.json").GetProperty("Sha256").GetString());
        Assert.True(evidence.RootElement.GetProperty("ReferenceExact").GetBoolean());
        Assert.False(evidence.RootElement.GetProperty("Options").GetProperty("ExtendText").GetBoolean());
        Assert.DoesNotContain(_root, evidence.RootElement.GetRawText());
        Assert.Contains(typeof(Pipeline).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
            evidence.RootElement.GetProperty("Engine").GetString()!);
        AmprIndex.Build(fixture.Game, Output, cancel: TestContext.Current.CancellationToken);
        Publication.Verify(Output);
        File.WriteAllBytes(Path.Combine(fixture.Game, "new.elf"), Elf());
        Assert.Throws<IOException>(() => AmprIndex.Build(fixture.Game, Output, cancel: TestContext.Current.CancellationToken));
        File.Delete(Path.Combine(fixture.Game, "new.elf"));
        File.WriteAllBytes(Path.Combine(fixture.Game, "data.bin"), [9, 9]);
        Assert.Throws<IOException>(() => AmprIndex.Build(fixture.Game, Output, cancel: TestContext.Current.CancellationToken));
        Publication.Verify(Output);
    }

    [Fact]
    public void LowerBoundReference_IsRecordedExplicitly()
    {
        var fixture = RunFixture();
        File.Delete(Path.Combine(fixture.Reference, "4.00.json"));
        FirmwareDb.Save(new FirmwareExports("3", "synthetic", false, new()), Path.Combine(fixture.Reference, "3.00.json"));
        var result = Pipeline.Backport(fixture.Game, Output,
            new BackportOptions { Firmware = "4", ReferenceDir = fixture.Reference }, fixture.Knowledge,
            cancel: TestContext.Current.CancellationToken);
        Assert.True(result.Published);
        using var evidence = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Output, Publication.EvidenceName)));
        Assert.False(evidence.RootElement.GetProperty("ReferenceExact").GetBoolean());
        Assert.Contains("lower bound", result.Reference);
        Publication.Verify(Output);
    }

    [Fact]
    public void ModuleErrors_RetainDiagnosticsWithoutPublishing()
    {
        Previous();
        var fixture = RunFixture();
        File.WriteAllBytes(Path.Combine(fixture.Game, "eboot.bin"), [1, 2]);
        var result = Pipeline.Backport(fixture.Game, Output, new BackportOptions { Firmware = "4", ReferenceDir = fixture.Reference }, fixture.Knowledge,
            cancel: TestContext.Current.CancellationToken);
        Assert.False(result.Published);
        Assert.NotEmpty(result.DiagnosticDirectory);
        Assert.True(File.Exists(Path.Combine(result.DiagnosticDirectory, Pipeline.ReportJson)));
        Assert.False(File.Exists(Path.Combine(result.DiagnosticDirectory, Publication.ManifestName)));
        Assert.Equal("previous", File.ReadAllText(Path.Combine(Output, "old.bin")));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("new-module")]
    [InlineData("new-reference")]
    [InlineData("write-failure")]
    [InlineData("new-library")]
    [InlineData("new-app0")]
    public void FailureBeforePublication_PreservesOutput(string fault)
    {
        Previous();
        var fixture = RunFixture();
        string? extra = fault is "new-library" or "new-app0" ? Directory.CreateDirectory(Path.Combine(_root, "extra")).FullName : null;
        using var cancel = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p.Stage != "publishing") return;
            switch (fault)
            {
                case "cancel": cancel.Cancel(); break;
                case "new-module": File.WriteAllBytes(Path.Combine(fixture.Game, "new.elf"), Elf()); break;
                case "new-reference": File.WriteAllText(Path.Combine(fixture.Reference, "5.00.json"), "{}"); break;
                case "write-failure": throw new UnauthorizedAccessException("injected write failure");
                case "new-library": File.WriteAllBytes(Path.Combine(extra!, "new.sprx"), Elf()); break;
                case "new-app0":
                    Directory.CreateDirectory(Path.Combine(extra!, "app0"));
                    File.WriteAllText(Path.Combine(extra!, "app0", "new.bin"), "changed");
                    break;
            }
        });
        Assert.NotNull(Record.Exception(() => Pipeline.Backport(fixture.Game, Output,
            new BackportOptions { Firmware = "4", ReferenceDir = fixture.Reference, ExtraFakelib = extra }, fixture.Knowledge, progress, cancel.Token)));
        Assert.Equal("previous", File.ReadAllText(Path.Combine(Output, "old.bin")));
        Assert.Empty(Directory.GetDirectories(_root, "*.stage"));
    }

    [Fact]
    public void ChangedLoadedKnowledge_IsRejected()
    {
        Previous();
        var fixture = RunFixture();
        File.AppendAllText(Path.Combine(fixture.Knowledge.DataDir, "fixes.json"), " ");
        Assert.Throws<IOException>(() => Pipeline.Backport(fixture.Game, Output,
            new BackportOptions { Firmware = "4", ReferenceDir = fixture.Reference }, fixture.Knowledge, cancel: TestContext.Current.CancellationToken));
        Assert.Equal("previous", File.ReadAllText(Path.Combine(Output, "old.bin")));
    }

    [Fact]
    public void BatchImportFailure_PreservesLibrariesAndManifest()
    {
        string store = Path.Combine(_root, "store"), first = Path.Combine(_root, "a.sprx"), second = Path.Combine(_root, "b.sprx");
        File.WriteAllBytes(first, Elf());
        FakelibStore.Add(store, "4", [first]);
        string folder = Path.Combine(store, "4");
        byte[] manifest = File.ReadAllBytes(Path.Combine(folder, "manifest.json"));
        byte[] library = File.ReadAllBytes(Path.Combine(folder, "a.sprx"));
        byte[] update = Elf(); update[^1] = 7;
        File.WriteAllBytes(first, update);
        File.WriteAllBytes(second, [0]);
        Assert.Throws<InvalidDataException>(() => FakelibStore.Add(store, "4", [first, second]));
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(folder, "manifest.json")));
        Assert.Equal(library, File.ReadAllBytes(Path.Combine(folder, "a.sprx")));
        Assert.False(File.Exists(Path.Combine(folder, "b.sprx")));
        Publication.Verify(folder);
        Assert.Throws<IOException>(() => FakelibStore.AddBatch(store, "4", [first], "", "", stage =>
        {
            if (stage == "file-added") throw new IOException("injected copy failure");
        }));
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(folder, "manifest.json")));
        Assert.Single(FakelibStore.List(store));
        FakelibStore.AddBatch(store, "4", [first], "", "", stage =>
        {
            if (stage == "file-added")
                Assert.Throws<IOException>(() => FakelibStore.Add(store, "5", [first]));
        });
        Assert.False(Directory.Exists(Path.Combine(store, "5")));
        FakelibStore.Add(store, "4", [first]);
        Assert.Equal(update, File.ReadAllBytes(Path.Combine(folder, "a.sprx")));
        Publication.Verify(folder);
    }

    [Fact]
    public void VariantImport_PublishesParentAndVariantTogether()
    {
        string store = Path.Combine(_root, "store"), input = Path.Combine(_root, "a.sprx");
        File.WriteAllBytes(input, Elf());
        FakelibStore.Add(store, "4", [input]);
        FakelibStore.Add(store, "4", [input], variant: "test");
        string folder = Path.Combine(store, "4"), variant = Path.Combine(folder, "test");
        Publication.Verify(folder);
        Publication.Verify(variant);
        byte[] original = File.ReadAllBytes(Path.Combine(variant, "a.sprx"));
        byte[] updated = Elf(); updated[^1] = 9;
        File.WriteAllBytes(input, updated);
        Assert.Throws<IOException>(() => FakelibStore.AddBatch(store, "4", [input], "", "test", stage =>
        {
            if (stage == "stage-moved") throw new IOException("injected variant publication failure");
        }));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(variant, "a.sprx")));
        Publication.Verify(folder);
        Publication.Verify(variant);
        FakelibStore.Add(store, "4", [input], variant: "test");
        Assert.Equal(updated, File.ReadAllBytes(Path.Combine(variant, "a.sprx")));
        Publication.Verify(folder);
        Publication.Verify(variant);
    }

    [Fact]
    public void WindowsAliasesAndJunctions_AreRefused()
    {
        if (!OperatingSystem.IsWindows()) return;
        Previous();
        Assert.Throws<ArgumentException>(() => Paths.RequireSeparateTrees(Output, "\\\\?\\" + Output));
        var shortName = new StringBuilder(32768);
        Assert.NotEqual(0u, GetShortPathNameW(Output, shortName, (uint)shortName.Capacity));
        Assert.Throws<ArgumentException>(() => Paths.RequireSeparateTrees(Output, shortName.ToString()));
        Assert.Throws<ArgumentException>(() => Paths.RequireNotContaining("\\\\?\\" + Output, Path.Combine(Output, "data")));
        RunCmd("mklink", "/J", "link", "out");
        try { Assert.Throws<IOException>(() => new DirectoryTransaction(Path.Combine(_root, "link", "nested"))); }
        finally { Directory.Delete(Path.Combine(_root, "link")); }
        RunCmd("mklink", "/J", "out\\linked", "..\\out");
        try { Assert.Throws<IOException>(() => new DirectoryTransaction(Output)); }
        finally { Directory.Delete(Path.Combine(Output, "linked")); }
        Assert.Equal("previous", File.ReadAllText(Path.Combine(Output, "old.bin")));
    }

    [Fact]
    public void WindowsDriveAlias_UsesTheSameWriterLock()
    {
        if (!OperatingSystem.IsWindows()) return;
        Previous();
        var used = Directory.GetLogicalDrives().Select(d => char.ToUpperInvariant(d[0])).ToHashSet();
        char letter = Enumerable.Range('D', 'Z' - 'D' + 1).Select(i => (char)i).Last(c => !used.Contains(c));
        string drive = letter + ":";
        Subst(drive, _root);
        try
        {
            string alias = drive + "\\out";
            Assert.Throws<ArgumentException>(() => Paths.RequireSeparateTrees(Output, alias));
            using var transaction = new DirectoryTransaction(Output);
            Assert.Throws<IOException>(() => new DirectoryTransaction(alias));
        }
        finally { Subst(drive, "/d"); }
    }

    static void Subst(string drive, string target)
    {
        var start = new ProcessStartInfo("subst.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(drive);
        start.ArgumentList.Add(target);
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(30000));
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void ReplacingDirectoryWithHardLinkedChild_DoesNotModifySource()
    {
        if (!OperatingSystem.IsWindows()) return;
        Previous();
        File.WriteAllText(Path.Combine(_root, "source.bin"), "source");
        RunCmd("mklink", "/H", "out\\linked.bin", "source.bin");
        using (var transaction = new DirectoryTransaction(Output))
        {
            transaction.CopyExisting(TestContext.Current.CancellationToken);
            File.WriteAllText(Path.Combine(transaction.Stage, "linked.bin"), "replacement");
            transaction.Commit(TestContext.Current.CancellationToken);
        }
        Assert.Equal("source", File.ReadAllText(Path.Combine(_root, "source.bin")));
        Assert.Equal("replacement", File.ReadAllText(Path.Combine(Output, "linked.bin")));
    }

    void RunCmd(params string[] arguments)
    {
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _root };
        start.ArgumentList.Add("/c");
        foreach (string argument in arguments) start.ArgumentList.Add(argument); // fixed relative names, no interpolated shell paths
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(30000));
        Assert.Equal(0, process.ExitCode);
    }

    static byte[] Elf()
    {
        var data = new byte[0x200];
        SceElf.Magic.CopyTo(data); data[4] = 2; data[5] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x10), SceElf.EtSceExec);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x12), 0x3E);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x14), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x20), 0x40);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x34), 0x40);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x36), 0x38);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x38), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x40), SceElf.PtLoad);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x44), SceElf.PfX);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x60), (ulong)data.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x68), (ulong)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x78), SceElf.PtSceModuleParam);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x80), 0x180);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x98), 0x18);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0xA0), 0x18);
        return data;
    }

    sealed class SyncProgress(Action<BackportProgress> action) : IProgress<BackportProgress>
    {
        public void Report(BackportProgress value) => action(value);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetShortPathNameW(string path, StringBuilder shortPath, uint length);

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
