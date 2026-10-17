namespace Ps5Backport.Tests
{
    using System.Text;
    using Ps5Backport.Core;
    using Ps5Backport.App;
    using Ps5Backport.App.ViewModels;

    public sealed class AutomaticProTests
    {
        [Theory]
        [InlineData("NH6xARDOVv8", "opmode_normal")]
        [InlineData("tU5e3f9gSiU", "ret0")]
        [InlineData("yu17wG8L5FI", "ret0")]
        [InlineData("BfBDZGbti7A", "byte0")]
        public void AnalyzeAndCook_PreserveAvailableQueriesAndFallbackOnlyWhenMissing(string nid, string stub)
        {
            foreach (string availability in new[] { "missing", "native", "provider" })
            {
                string root = Path.Combine(Path.GetTempPath(), "ps5backport-auto-pro-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    string game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
                    string reference = Directory.CreateDirectory(Path.Combine(root, "reference")).FullName;
                    string set = Directory.CreateDirectory(Path.Combine(root, "set")).FullName;
                    string output = Path.Combine(root, "output");
                    byte[] module = SyntheticBinary.Elf(imports: true);
                    Encoding.ASCII.GetBytes(nid + "#A#A\0").CopyTo(module, 0x201);
                    SyntheticBinary.W64(module, 0x130, SceElf.DtJmpRel);
                    SyntheticBinary.W64(module, 0x140, SceElf.DtPltRelSz);
                    SyntheticBinary.W64(module, 0x480, 0x426);
                    Convert.FromHexString("FF255A0000006800000000").CopyTo(module, 0x420);
                    module[0x380] = Patching.Ret;
                    module.AsSpan(0x381, 32).Fill(Patching.Int3);
                    File.WriteAllBytes(Path.Combine(game, "eboot.bin"), module);
                    FirmwareDb.Save(new FirmwareExports("4", "test", false,
                        availability == "native" ? new() { ["synthetic"] = new() { nid } } : new()), Path.Combine(reference, "4.00.json"));
                    if (availability == "provider")
                    {
                        byte[] provider = SyntheticBinary.Elf(exports: true);
                        Encoding.ASCII.GetBytes(nid + "#A#A\0").CopyTo(provider, 0x201);
                        File.WriteAllBytes(Path.Combine(set, "query.sprx"), provider);
                    }
                    var options = new BackportOptions { Firmware = "4", ReferenceDir = reference, ExtraFakelib = set, Options = ["auto_pro"] };
                    var knowledge = new Knowledge(Paths.DataDir);
                    var analysis = Pipeline.Backport(game, output, options with { DryRun = true }, knowledge,
                        cancel: TestContext.Current.CancellationToken);
                    var cooked = Pipeline.Backport(game, output, options, knowledge,
                        cancel: TestContext.Current.CancellationToken, analysis: analysis.Analysis);
                    Assert.True(cooked.Published);
                    Assert.All(cooked.Modules, m => Assert.Empty(m.Error));
                    var entry = cooked.Modules.Single(m => m.Path == "eboot.bin");
                    Assert.Empty(entry.Fixed);
                    Assert.Equal(analysis.Modules.Single(m => m.Path == "eboot.bin").StubbedNow.Select(s => (s.Nid, s.Stub, s.At, s.Got)),
                        entry.StubbedNow.Select(s => (s.Nid, s.Stub, s.At, s.Got)));
                    if (availability == "missing")
                    {
                        Assert.Equal(stub, Assert.Single(entry.StubbedNow).Stub);
                        var patched = new SceElf(Self.Unwrap(File.ReadAllBytes(Path.Combine(output, "eboot.bin"))));
                        ulong address = patched.ReadU64(Assert.Single(patched.Imports()).GotVaddr);
                        Assert.Equal(knowledge.StubBytes(stub), patched.Data.AsSpan((int)patched.Off(address), knowledge.StubBytes(stub).Length).ToArray());
                    }
                    else
                    {
                        Assert.Empty(entry.StubbedNow);
                        Assert.False(File.Exists(Path.Combine(output, "eboot.bin")));
                    }
                }
                finally { Directory.Delete(root, true); }
            }
        }

        [Fact]
        public void AutomaticMode_KeepsFirmwareGuardAndRejectsForcedModeInEitherOrder()
        {
            var knowledge = new Knowledge(Paths.DataDir);
            var table = knowledge.FixTable(["auto_pro"]);
            Assert.True(table["3WVD91e12ZQ"].Due("4.00"));
            Assert.False(table["3WVD91e12ZQ"].Due("9.00"));
            Assert.Throws<ArgumentException>(() => knowledge.FixTable(["auto_pro", "force_pro"]));
            Assert.Throws<ArgumentException>(() => knowledge.FixTable(["force_pro", "auto_pro"]));
        }

        /// <summary>The option's texts describe GOT pre-fills that the loader overrides wherever the firmware exports the query
        /// (FINDINGS 6b): every auto_pro fix is a plain stub applied only when missing, and the first exporting firmwares named in
        /// the warning match firmware_db.</summary>
        [Fact]
        public void AutomaticMode_UsesLoaderOverridableGotPrefills()
        {
            var knowledge = new Knowledge(Repo.Need("data"));
            var option = knowledge.Options["auto_pro"];
            Assert.All(option.Fixes.Values, f => Assert.Equal((Fix.KindStub, Fix.WhenMissing), (f.Kind, f.When)));
            var databases = FirmwareDb.LoadAll(Repo.Need("firmware_db"));
            var firstExport = new Dictionary<string, string>
            {
                ["tU5e3f9gSiU"] = "7.00",   // sceKernelIsTrinityMode
                ["yu17wG8L5FI"] = "7.00",   // sceKernelHasTrinityMode
                ["BfBDZGbti7A"] = "9.00",   // sceAgcGetIsTrinityMode
                ["NH6xARDOVv8"] = "11.00",  // sceKernelGetOperationMode
            };
            Assert.Equal(firstExport.Keys.Order(StringComparer.Ordinal), option.Fixes.Keys.Order(StringComparer.Ordinal));
            foreach (var (nid, firmware) in firstExport)
                Assert.Equal(firmware, FirmwareDb.Introduced(nid, databases, knowledge)?.Firmware);
            Assert.Contains("from FW 7.00", option.Warning);
            Assert.Contains("from 9.00", option.Warning);
            Assert.Contains("from 11.00", option.Warning);
            Assert.DoesNotContain("even if this cook later runs", option.Warning);
        }

        /// <summary>A missing GetOperationMode is derived from the game's own sceKernelIsTrinityMode import (mode 2 | Pro), so a Pro on
        /// FW 7.00–10.60 reports mode 3 here as it does through IsTrinityMode; opmode_normal stays the fallback without that import.</summary>
        [Fact]
        public void OperationModeFallback_CallsIsTrinityModeThroughItsGot()
        {
            var fix = new Knowledge(Paths.DataDir).Options["auto_pro"].Fixes["NH6xARDOVv8"];
            Assert.Equal((Fix.KindStub, "opmode_normal", false), (fix.Kind, fix.Stub, fix.Proven));
            Assert.Equal(["tU5e3f9gSiU"], fix.Insns.Where(i => i.CallNid.Length > 0).Select(i => i.CallNid));
            Assert.Equal("c3", fix.Insns[^1].Hex);
        }

        /// <summary>Silksong's eboot imports both queries: its GetOperationMode GOT slot is pre-filled with the derived chain, which
        /// calls through IsTrinityMode's own GOT slot (itself pre-filled with ret0 for FW 4).</summary>
        [Fact]
        [Trait("Lane", "Private")]
        public void Cook_DerivesOperationModeFromIsTrinityMode()
        {
            string output = Samples.Temp("auto-pro-derived");
            try
            {
                var result = Samples.Backport(Samples.Backports("PPSA12544", "default"), output, "4", Repo.Need("firmware_db"), options: ["auto_pro"]);
                var stubs = result.Modules.Single(m => m.Path == "eboot.bin").StubbedNow;
                Assert.Equal("derived from sceKernelIsTrinityMode", stubs.Single(s => s.Nid == "NH6xARDOVv8").Stub);
                Assert.Equal("ret0", stubs.Single(s => s.Nid == "tU5e3f9gSiU").Stub);

                var elf = new SceElf(Self.Unwrap(File.ReadAllBytes(Path.Combine(output, "eboot.bin"))));
                var imports = elf.Imports().Where(i => i.Plt).ToDictionary(i => i.Nid);
                var fix = new Knowledge(Paths.DataDir).Options["auto_pro"].Fixes["NH6xARDOVv8"];
                var (executed, called, jumped) = Samples.Trace(elf, imports["NH6xARDOVv8"], fix, start: elf.ReadU64(imports["NH6xARDOVv8"].GotVaddr));
                Assert.Equal(fix.Insns.Select(i => i.CallGot ? Insn.CallGotToken : i.Hex), executed);
                Assert.Equal(imports["tU5e3f9gSiU"].GotVaddr, called);
                Assert.Null(jumped);
            }
            finally
            {
                if (Directory.Exists(output))
                    Directory.Delete(output, true);
            }
        }

        [Fact]
        public void Gui_ConsoleModesAreMutuallyExclusive()
        {
            var vm = new MainViewModel(new AppSettings());
            var automatic = vm.PatchOptions.Single(o => o.Key == "auto_pro");
            var forced = vm.PatchOptions.Single(o => o.Key == "force_pro");
            forced.IsEnabled = true;
            automatic.IsEnabled = true;
            Assert.False(forced.IsEnabled);
            forced.IsEnabled = true;
            Assert.False(automatic.IsEnabled);
        }
    }
}
