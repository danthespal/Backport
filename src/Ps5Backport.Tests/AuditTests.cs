using System.Buffers.Binary;
using Ps5Backport.App;
using Ps5Backport.App.ViewModels;
using Ps5Backport.Core;

namespace Ps5Backport.Tests;

public class AuditTests
{
    static byte[] Elf()
    {
        var data = new byte[0x100];
        SceElf.Magic.CopyTo(data);
        data[4] = 2;
        data[5] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x10), SceElf.EtSceExec);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x12), 0x3E);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x14), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x20), 0x40);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x34), 0x40);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x36), 0x38);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x38), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x40), SceElf.PtLoad);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x44), SceElf.PfX);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x60), (ulong)data.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x68), (ulong)data.Length);
        return data;
    }

    static byte[] BpsBytes(byte[] body)
    {
        byte[] bytes = [.. "BPS1"u8, .. body, .. new byte[12]];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), Crc32.Compute(bytes.AsSpan(0, bytes.Length - 4)));
        return bytes;
    }

    [Theory]
    [InlineData(new byte[] { 0x80, 0x80, 0x81 })] // metadata crosses into the checksum
    [InlineData(new byte[] { 0x80, 0x81, 0x80, 0x81 })] // missing literal
    [InlineData(new byte[] { 0x80, 0x81, 0x80, 0x00 })] // unterminated action varint
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80 })] // action exceeds target
    [InlineData(new byte[] { 0x80, 0x81, 0x80 })] // actions do not fill target
    public void BpsParse_RejectsTruncatedOrInconsistentActions(byte[] body) =>
        Assert.Throws<InvalidDataException>(() => Bps.Parse(BpsBytes(body)));

    [Fact]
    public void BpsParse_RejectsVarintOverflow() =>
        Assert.Throws<InvalidDataException>(() => Bps.Parse(BpsBytes([.. Enumerable.Repeat((byte)0x7F, 11), 0x80])));

    [Fact]
    public void BpsApply_RefusesCopyFromUnwrittenTarget()
    {
        var patch = new Bps.Patch(0, 1, [], [new(Bps.Command.TargetCopy, 1, null, 0)], 0, Crc32.Compute(new byte[1]));
        Assert.Throws<InvalidDataException>(() => Bps.Apply(patch, []));
    }

    [Theory]
    [InlineData(Bps.Command.SourceRead, 2, 0)]
    [InlineData(Bps.Command.SourceCopy, 1, -1)]
    [InlineData(Bps.Command.TargetRead, 1, 0)]
    [InlineData((Bps.Command)99, 1, 0)]
    public void BpsApply_RejectsInvalidPublicActions(Bps.Command command, long length, long delta)
    {
        byte[] source = [1];
        var patch = new Bps.Patch(1, 2, [], [new(command, length, null, delta)], Crc32.Compute(source), 0);
        Assert.Throws<InvalidDataException>(() => Bps.Apply(patch, source));
    }

    [Theory]
    [InlineData(0x20, ulong.MaxValue)]
    [InlineData(0x48, ulong.MaxValue)]
    [InlineData(0x60, ulong.MaxValue)]
    public void Elf_RejectsOutOfBoundsOffsets(int offset, ulong value)
    {
        byte[] data = Elf();
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), value);
        Assert.Throws<InvalidDataException>(() => new SceElf(data));
    }

    [Fact]
    public void Elf_RejectsWrongEncoding()
    {
        byte[] data = Elf();
        data[5] = 2;
        Assert.Throws<InvalidDataException>(() => new SceElf(data));
    }

    [Fact]
    public void Elf_DynamicNameWithoutStrings_IsADataError()
    {
        byte[] data = Elf();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x40), SceElf.PtDynamic);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x48), 0x80);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x60), 0x20);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x80), SceElf.DtSceModuleName);
        Assert.Throws<InvalidDataException>(() => new SceElf(data).ModuleName);
    }

    [Fact]
    public void ImportFirmwareDump_EmptyFolderCannotBecomeAnExactDatabase()
    {
        string root = TempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Firmware 4.00"));
            var (db, problem) = DataGenerators.ImportFirmwareDump(root, "4.00");
            Assert.Null(db);
            Assert.NotEmpty(problem);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Cli_EmptyFirmwareImportPreservesExistingDatabase()
    {
        string root = TempRoot();
        try
        {
            string input = Path.Combine(root, "input");
            Directory.CreateDirectory(input);
            string database = Path.Combine(root, "4.00.json");
            File.WriteAllText(database, "keep");
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(1, Cli.Run(["fwdb", "import-dump", input, "--firmware", "4", "--source", "test", "--db", root], output, error));
            Assert.Equal("keep", File.ReadAllText(database));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void NullManifest_IsADataErrorRatherThanNullReference()
    {
        string root = TempRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, FakelibStore.ManifestName), "null");
            Assert.Throws<InvalidDataException>(() => FakelibStore.LoadManifest(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void StorePage_CorruptManifestReportsErrorWithoutCrashing()
    {
        string root = TempRoot();
        try
        {
            string set = Path.Combine(root, "4");
            Directory.CreateDirectory(set);
            File.WriteAllText(Path.Combine(set, FakelibStore.ManifestName), "null");
            var vm = new MainViewModel(new AppSettings { FakelibStoreDir = root });
            vm.Page = AppPage.Store;
            Assert.True(vm.Log.ErrorCount > 0);
            Assert.Empty(vm.Store.Files);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SelfSign_RejectsTruncatedProgramHeaders()
    {
        byte[] data = Elf();
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x20), 0xFF);
        Assert.Throws<InvalidDataException>(() => Self.Sign(data));
    }

    [Fact]
    public void SelfUnwrap_RejectsOverflowingOutputSegment()
    {
        byte[] data = Self.Sign(Elf());
        int embedded = 0x20 + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x18)) * 0x20;
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(embedded + 0x48), ulong.MaxValue);
        Assert.Throws<InvalidDataException>(() => Self.Unwrap(data));
    }

    [Theory]
    [InlineData("999999999999999999999")]
    [InlineData("4.100")]
    public void Firmware_RejectsValuesThatCannotFitTheVersionFormat(string firmware) =>
        Assert.Throws<FormatException>(() => Firmware.Parse(firmware));

    [Fact]
    public void Sdk_RejectsShortParamRatherThanReadingAdjacentBytes()
    {
        byte[] data = Elf();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x40), SceElf.PtSceProcParam);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x48), 0x80);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x60), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x88), Sdk.ProcessParamMagic);
        Assert.Throws<InvalidDataException>(() => Sdk.Read(new SceElf(data)));
    }

    [Theory]
    [InlineData(new byte[] { }, new byte[] { })]
    [InlineData(new byte[] { 1 }, new byte[] { 2, 3 })]
    public void Patterns_RejectInvalidReplacementLengths(byte[] find, byte[] replace)
    {
        byte[] data = [1, 9];
        Assert.Throws<ArgumentException>(() => FixApplier.ReplaceAll(data, new FixPattern("bad", find, replace, []), cancel: TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 1, 9 }, data);
    }

    [Fact]
    public void Tools_BusyOperationsCannotChangeTheFileList()
    {
        var vm = new MainViewModel();
        vm.Tools.Files.Add(new SelfFileRow("test", "ELF", "", "", "", "muted"));
        vm.Tools.Busy = true;
        vm.Tools.ClearFilesCommand.Execute(null);
        Assert.Single(vm.Tools.Files);
    }

    [Fact]
    public void WalkFiles_PreservesSortedDepthFirstOrder()
    {
        string root = TempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "b"));
            Directory.CreateDirectory(Path.Combine(root, "a", "nested"));
            foreach (string rel in new[] { "z.elf", "c.elf", "b/z.elf", "a/z.elf", "a/nested/z.elf" })
                File.WriteAllBytes(Path.Combine(root, rel), []);
            Assert.Equal(new[] { "c.elf", "z.elf", "a/z.elf", "a/nested/z.elf", "b/z.elf" },
                GameScan.WalkFiles(root, cancel: TestContext.Current.CancellationToken).Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')));
            Assert.Equal(GameScan.WalkFiles(root, cancel: TestContext.Current.CancellationToken).Select(p => Path.GetRelativePath(root, p)), GameScan.FindModules(root, cancel: TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("--firmware")]
    [InlineData("--ps5")]
    public void Cli_MissingOptionValue_ReturnsAnError(string option)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, Cli.Run(["sdk", "input", "output", option], output, error));
        Assert.Contains("needs a value", error.ToString());
    }

    [Fact]
    public void Workbench_BusyCannotReplaceGame()
    {
        var vm = new MainViewModel { Busy = true };
        vm.LoadGame(Path.GetTempPath());
        Assert.Null(vm.Game);
        Assert.False(vm.OpenGameCommand.CanExecute(null));
    }

    [Fact]
    public void Settings_ExplicitNullsDoNotCrashStartupOrRemember()
    {
        string root = TempRoot();
        try
        {
            string path = Path.Combine(root, "settings.json");
            File.WriteAllText(path, "{\"Firmware\":null,\"RecentGames\":null}");
            var settings = AppSettings.Load(path, out _);
            settings.Remember("game");
            var vm = new MainViewModel(settings);
            Assert.Equal("4", vm.SelectedFirmware);
            Assert.Single(settings.RecentGames);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AtomicWrite_DoesNotOverwriteAnUnrelatedTmpFile()
    {
        string root = TempRoot();
        try
        {
            string path = Path.Combine(root, "output");
            File.WriteAllText(path + ".tmp", "keep");
            Self.WriteAtomic(path, [1, 2]);
            Assert.Equal("keep", File.ReadAllText(path + ".tmp"));
            Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Pipeline_RejectsCaseAndTrailingSeparatorAliasesBeforeWriting()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = TempRoot();
        try
        {
            string source = Path.Combine(root, "Game");
            Directory.CreateDirectory(source);
            var options = new BackportOptions { Firmware = "4", ReferenceDir = root };
            var k = Knowledge.Default;
            Assert.Throws<ArgumentException>(() => Pipeline.Backport(source, source.ToUpperInvariant(), options, k, cancel: TestContext.Current.CancellationToken));
            Assert.Throws<ArgumentException>(() => Pipeline.Backport(source + Path.DirectorySeparatorChar, Path.Combine(source, "out"), options, k, cancel: TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(Path.Combine(source, "out")));
            Assert.Throws<ArgumentException>(() => AmprIndex.Build(source, source, cancel: TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Pipeline_PreCancelledRunWritesNothing()
    {
        string root = TempRoot();
        try
        {
            string output = Path.Combine(root, "output");
            string source = Path.Combine(root, "game");
            Directory.CreateDirectory(source);
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            Assert.Throws<OperationCanceledException>(() => Pipeline.Backport(source, output,
                new BackportOptions { Firmware = "4", ReferenceDir = root }, Knowledge.Default, cancel: cancel.Token));
            Assert.False(Directory.Exists(output));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FirmwarePage_ClearSelectionClearsDetails()
    {
        var db = new FirmwareExports("4", "test", false, new() { ["lib"] = ["nid"] });
        var vm = new MainViewModel();
        vm.Database.ChangedOnly = false;
        vm.Database.Firmwares.Add(new FirmwareRow(db));
        vm.Database.Selected = vm.Database.Firmwares[0];
        Assert.Single(vm.Database.Modules);
        vm.Database.Selected = null;
        Assert.Empty(vm.Database.Modules);
        Assert.Null(vm.Database.SelectedModule);
    }

    [Fact]
    public void FirmwarePage_SearchesUnchangedFunctionNames()
    {
        string root = TempRoot();
        try
        {
            string names = Path.Combine(root, "names.csv");
            File.WriteAllText(names, "abcdefghijk sceExampleFunction\n");
            var vm = new MainViewModel(new AppSettings { AerolibPath = names });
            var db = new FirmwareExports("4", "test", false, new() { ["lib"] = ["abcdefghijk"] });
            vm.Database.Firmwares.Add(new FirmwareRow(db));
            vm.Database.Selected = vm.Database.Firmwares[0];
            vm.Database.Search = "Example";
            Assert.Single(vm.Database.Modules);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ModuleRow_PatternOnlyChangeIsVisible()
    {
        var module = new ModuleResult("eboot.bin", Self.KindElf, false);
        module.Patterns.Add(new PatternEntry { Name = "fix", Count = 1 });
        Assert.Equal("will change", new ModuleRow(module).State);
    }

    [Fact]
    public async Task FirmwareRefresh_DoesNotPublishResultsFromAnOldPath()
    {
        string root = TempRoot();
        try
        {
            string old = Path.Combine(root, "old"), current = Path.Combine(root, "new");
            Directory.CreateDirectory(old);
            Directory.CreateDirectory(current);
            FirmwareDb.Save(new FirmwareExports("4", "test", false, new()), Path.Combine(old, "4.00.json"));
            var settings = new AppSettings { FirmwareDbDir = old };
            var vm = new MainViewModel(settings);
            // Change the configured path synchronously as the old request starts.
            vm.Database.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(vm.Database.Busy) && vm.Database.Busy)
                    settings.FirmwareDbDir = current;
            };
            await vm.Database.Refresh();
            Assert.Empty(vm.Database.Firmwares);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Pipeline_ComposesMultiplePatternsForOneDataFile()
    {
        string root = TempRoot();
        try
        {
            string source = Path.Combine(root, "game"), output = Path.Combine(root, "overlay"), dataDir = Path.Combine(root, "data");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(dataDir);
            foreach (string file in Directory.EnumerateFiles(Paths.DataDir, "*.json"))
                File.Copy(file, Path.Combine(dataDir, Path.GetFileName(file)));
            var fixes = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dataDir, "fixes.json")))!;
            fixes["patterns"] = System.Text.Json.Nodes.JsonNode.Parse("""
                [{"name":"first","find":"0102","replace":"0304","files":["data.bin"]},
                 {"name":"second","find":"0506","replace":"0708","files":["data.bin"]}]
                """);
            File.WriteAllText(Path.Combine(dataDir, "fixes.json"), fixes.ToJsonString());
            File.WriteAllBytes(Path.Combine(source, "data.bin"), [1, 2, 5, 6]);
            var db = new FirmwareExports("4", "test", false, new());
            FirmwareDb.Save(db, Path.Combine(root, "4.00.json"));
            var result = Pipeline.Backport(source, output,
                new BackportOptions { Firmware = "4", ReferenceDir = root }, new Knowledge(dataDir), cancel: TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { 3, 4, 7, 8 }, File.ReadAllBytes(Path.Combine(output, "data.bin")));
            Assert.Equal(2, result.DataFiles.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    static string TempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "ps5backport-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
