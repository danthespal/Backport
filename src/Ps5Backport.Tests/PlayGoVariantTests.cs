namespace Ps5Backport.Tests
{
    using Ps5Backport.App;
    using Ps5Backport.App.ViewModels;
    using Ps5Backport.Core;

    public sealed class PlayGoVariantTests : IDisposable
    {
        readonly string _root = Path.Combine(Path.GetTempPath(), "ps5backport-playgo-" + Guid.NewGuid().ToString("N"));
        readonly Knowledge _knowledge = new(Paths.DataDir);
        readonly string _game;
        readonly string _reference;
        readonly string _store;

        public PlayGoVariantTests()
        {
            _game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
            _reference = Directory.CreateDirectory(Path.Combine(_root, "reference")).FullName;
            _store = Path.Combine(_root, "store");
            File.WriteAllBytes(Path.Combine(_game, "eboot.bin"), SyntheticBinary.Elf());
            FirmwareDb.Save(new FirmwareExports("4", "test", false, new()), Path.Combine(_reference, "4.00.json"));
            Install("", 1);
            Install("playgo_0_6", 2);
            Install("playgo_0_6_log", 3);
        }

        void Install(string variant, byte marker)
        {
            string input = Path.Combine(_root, "input", variant.Length == 0 ? "base" : variant);
            Directory.CreateDirectory(input);
            byte[] bytes = SyntheticBinary.Elf();
            bytes[0x300] = marker;
            string file = Path.Combine(input, "libScePlayGo.sprx");
            File.WriteAllBytes(file, Self.Sign(bytes));
            FakelibStore.Add(_store, "4", [file], "synthetic test", variant);
        }

        BackportOptions Options(params string[] selected) => new()
        {
            Firmware = "4", ReferenceDir = _reference, FakelibStore = _store, Options = selected
        };

        [Theory]
        [InlineData("", 1)]
        [InlineData("playgo_0_6", 2)]
        [InlineData("playgo_0_6_log", 3)]
        public void AnalyzeAndCook_SelectOnlyTheRequestedVariant(string variant, byte marker)
        {
            string output = Path.Combine(_root, "output");
            var options = Options(variant.Length == 0 ? [] : [variant]);
            var analysis = Pipeline.Backport(_game, output, options with { DryRun = true }, _knowledge, cancel: TestContext.Current.CancellationToken);
            Assert.False(Directory.Exists(output));
            var cooked = Pipeline.Backport(_game, output, options, _knowledge, cancel: TestContext.Current.CancellationToken, analysis: analysis.Analysis);
            Assert.True(cooked.Published);
            Assert.All(cooked.Modules, m => Assert.Empty(m.Error));
            var library = new SceElf(Self.Unwrap(File.ReadAllBytes(Path.Combine(output, "fakelib/libScePlayGo.sprx"))));
            Assert.Equal(marker, library.Data[0x300]);
            Assert.False(File.Exists(Path.Combine(output, "fakelib/libScePlayGo_log.sprx")));
            Assert.False(File.Exists(Path.Combine(output, "playgo_stub.dat")));
            if (variant.Length > 0)
                Assert.Contains(cooked.ParamWarnings, w => w.Contains("console", StringComparison.OrdinalIgnoreCase));
            if (variant == "playgo_0_6_log")
                Assert.Contains(cooked.ParamWarnings, w => w.Contains("/app0/playlgo.log"));
        }

        [Fact]
        public void CustomSetWithoutVariant_WarnsAndKeepsItsLibrary()
        {
            string output = Path.Combine(_root, "output");
            var result = Pipeline.Backport(_game, output, Options("playgo_0_6") with
            {
                ExtraFakelib = Path.Combine(_root, "input/base")
            }, _knowledge, cancel: TestContext.Current.CancellationToken);
            Assert.True(result.Published);
            Assert.Contains(result.ParamWarnings, w => w.Contains("has no playgo_0_6/ variant"));
            Assert.Equal((byte)1, Self.Unwrap(File.ReadAllBytes(Path.Combine(output, "fakelib/libScePlayGo.sprx")))[0x300]);
        }

        [Fact]
        public void ExistingConfig_IsLeftIntact()
        {
            string config = Path.Combine(_game, "playgo_stub.dat");
            const string contents = "0,1,9\n3\nenglish_us\n";
            File.WriteAllText(config, contents);
            string output = Path.Combine(_root, "output");
            var result = Pipeline.Backport(_game, output, Options("playgo_0_6_log"), _knowledge,
                cancel: TestContext.Current.CancellationToken);
            Assert.True(result.Published);
            Assert.Equal(contents, File.ReadAllText(config));
            Assert.False(File.Exists(Path.Combine(output, "playgo_stub.dat")));
        }

        [Fact]
        public void ConflictingVariants_FailWithoutPublishing()
        {
            string output = Path.Combine(_root, "output");
            var error = Assert.Throws<ArgumentException>(() => Pipeline.Backport(_game, output,
                Options("playgo_0_6", "playgo_0_6_log"), _knowledge, cancel: TestContext.Current.CancellationToken));
            Assert.Contains("only one PlayGo", error.Message);
            Assert.False(Directory.Exists(output));
        }

        [Fact]
        public void ChangedVariant_FailsManifestVerification()
        {
            string file = Path.Combine(_store, "4/playgo_0_6/libScePlayGo.sprx");
            File.AppendAllText(file, "changed");
            Assert.Throws<ArgumentException>(() => Pipeline.Backport(_game, Path.Combine(_root, "output"),
                Options("playgo_0_6"), _knowledge, cancel: TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(Path.Combine(_root, "output")));
        }

        [Theory]
        [InlineData("playgo_0_6", "playgo_0_6_log")]
        [InlineData("playgo_0_6_log", "playgo_0_6")]
        public void GuiSelection_DisablesTheOtherVariant(string first, string second)
        {
            var vm = new MainViewModel(new AppSettings());
            vm.PatchOptions.Single(o => o.Key == first).IsEnabled = true;
            vm.PatchOptions.Single(o => o.Key == second).IsEnabled = true;
            Assert.False(vm.PatchOptions.Single(o => o.Key == first).IsEnabled);
            Assert.True(vm.PatchOptions.Single(o => o.Key == second).IsEnabled);
        }

        public void Dispose() => Directory.Delete(_root, true);
    }

    [Trait("Lane", "Private")]
    public sealed class PlayGoReleaseTests
    {
        [Theory]
        [InlineData("4", "playgo_0_6")]
        [InlineData("4", "playgo_0_6_log")]
        [InlineData("4.03", "playgo_0_6")]
        [InlineData("4.03", "playgo_0_6_log")]
        [InlineData("7", "playgo_0_6")]
        [InlineData("7", "playgo_0_6_log")]
        [InlineData("9.60", "playgo_0_6")]
        [InlineData("9.60", "playgo_0_6_log")]
        [InlineData("10", "playgo_0_6")]
        [InlineData("10", "playgo_0_6_log")]
        [InlineData("11", "playgo_0_6")]
        [InlineData("11", "playgo_0_6_log")]
        public void OfficialVariant_CooksWithoutChangingItsCode(string firmware, string variant)
        {
            string root = Path.Combine(Path.GetTempPath(), "ps5backport-playgo-release-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
                string store = Path.Combine(root, "store");
                string output = Path.Combine(root, "output");
                string input = Repo.Need("fakelibs", firmware, variant, "libScePlayGo.sprx");
                File.WriteAllBytes(Path.Combine(game, "eboot.bin"), SyntheticBinary.Elf());
                string baseModule = Path.Combine(root, "libScePlayGo.sprx");
                File.WriteAllBytes(baseModule, Self.Sign(SyntheticBinary.Elf()));
                FakelibStore.Add(store, firmware, [baseModule], "synthetic base");
                FakelibStore.Add(store, firmware, [input], "official test variant", variant);
                var options = new BackportOptions { Firmware = firmware, ReferenceDir = Repo.Need("firmware_db"),
                    FakelibStore = store, Options = [variant] };
                var knowledge = new Knowledge(Paths.DataDir);
                var analysis = Pipeline.Backport(game, output, options with { DryRun = true }, knowledge,
                    cancel: TestContext.Current.CancellationToken);
                var cook = Pipeline.Backport(game, output, options, knowledge,
                    cancel: TestContext.Current.CancellationToken, analysis: analysis.Analysis);
                Assert.True(cook.Published);
                var module = cook.Modules.Single(m => m.Path == "fakelib/libScePlayGo.sprx");
                Assert.Empty(module.Error);
                Assert.Empty(module.Unknown);
                Assert.Empty(module.Fixed);
                Assert.Empty(module.StubbedNow);
                Assert.Equal(Self.Unwrap(File.ReadAllBytes(input)),
                    Self.Unwrap(File.ReadAllBytes(Path.Combine(output, module.Path))));
                Assert.False(File.Exists(Path.Combine(output, "playgo_stub.dat")));
            }
            finally { Directory.Delete(root, true); }
        }

        [Theory]
        [InlineData("4")]
        [InlineData("4.03")]
        [InlineData("7")]
        [InlineData("9.60")]
        [InlineData("10")]
        [InlineData("11")]
        public void OfficialVariants_HaveMatchingExportsAndAvailableImports(string firmware)
        {
            string folder = Repo.Need("fakelibs", firmware);
            var normal = FakelibStore.Check(Path.Combine(folder, "playgo_0_6"),
                Imports.LoadReference(Repo.Need("firmware_db"), firmware)).Single();
            var logging = FakelibStore.Check(Path.Combine(folder, "playgo_0_6_log"),
                Imports.LoadReference(Repo.Need("firmware_db"), firmware)).Single();
            Assert.Empty(FakelibStore.Verify(Path.Combine(folder, "playgo_0_6")));
            Assert.Empty(FakelibStore.Verify(Path.Combine(folder, "playgo_0_6_log")));
            Assert.Equal(24, normal.Elf.Exports().Count);
            Assert.Equal(normal.Elf.Exports().Order(), logging.Elf.Exports().Order());
            Assert.All(normal.Statuses.Concat(logging.Statuses), s => Assert.False(Imports.IsUnresolved(s.Status)));
            Assert.Equal("f5fdb0ed6cc7413e6dcac79a62e0efd8cfbae04cca0bf932e7a741aed850d213",
                FakelibStore.Sha256(Path.Combine(folder, "playgo_0_6/libScePlayGo.sprx")));
            Assert.Equal("7595924889d67c33149dc152903d2f2492abbc3e846196d536c4f16a7b5560f6",
                FakelibStore.Sha256(Path.Combine(folder, "playgo_0_6_log/libScePlayGo.sprx")));
        }
    }
}
