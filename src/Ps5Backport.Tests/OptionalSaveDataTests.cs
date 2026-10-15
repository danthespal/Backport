namespace Ps5Backport.Tests
{
    using System.Text;
    using Ps5Backport.Core;
    using Ps5Backport.App;
    using Ps5Backport.App.ViewModels;

    public sealed class OptionalSaveDataTests
    {
        [Theory]
        [InlineData("save_prepare_compat", "sDCBrmc61XU", false)]
        [InlineData("save_prepare_compat", "sDCBrmc61XU", true)]
        public void AnalyzeAndCook_OnlyAdaptSaveCallWhenSelected(string key, string nid, bool enabled)
        {
            string root = Path.Combine(Path.GetTempPath(), "ps5backport-optional-save-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
                string reference = Directory.CreateDirectory(Path.Combine(root, "reference")).FullName;
                string output = Path.Combine(root, "output");
                byte[] module = SyntheticBinary.Elf(imports: true);
                Encoding.ASCII.GetBytes(nid + "#A#A\0").CopyTo(module, 0x201);
                SyntheticBinary.W64(module, 0x130, SceElf.DtJmpRel);
                SyntheticBinary.W64(module, 0x140, SceElf.DtPltRelSz);
                SyntheticBinary.W64(module, 0x480, 0x426);
                Convert.FromHexString("FF255A0000006800000000").CopyTo(module, 0x420);
                module[0x380] = Patching.Ret;
                module.AsSpan(0x381, 64).Fill(Patching.Int3);
                File.WriteAllBytes(Path.Combine(game, "eboot.bin"), module);
                FirmwareDb.Save(new FirmwareExports("4", "test", false, new() { ["synthetic"] = new() { nid } }), Path.Combine(reference, "4.00.json"));
                var knowledge = new Knowledge(Paths.DataDir);
                Assert.False(knowledge.Fixes.ContainsKey(nid));
                var options = new BackportOptions { Firmware = "4", ReferenceDir = reference, Options = enabled ? [key] : [] };
                var analysis = Pipeline.Backport(game, output, options with { DryRun = true }, knowledge,
                    cancel: TestContext.Current.CancellationToken);
                var cooked = Pipeline.Backport(game, output, options, knowledge,
                    cancel: TestContext.Current.CancellationToken, analysis: analysis.Analysis);
                Assert.True(cooked.Published);
                var entry = Assert.Single(cooked.Modules);
                Assert.Empty(entry.Error);
                Assert.Equal(enabled ? 1 : 0, entry.Fixed.Count);
                Assert.Equal(Assert.Single(analysis.Modules).Fixed.Select(f => (f.Nid, f.How)), entry.Fixed.Select(f => (f.Nid, f.How)));
                if (enabled)
                {
                    Assert.Equal(nid, Assert.Single(entry.Fixed).Nid);
                    var patched = Self.Unwrap(File.ReadAllBytes(Path.Combine(output, "eboot.bin")));
                    Assert.NotEqual(module, patched);
                }
                else
                    Assert.False(File.Exists(Path.Combine(output, "eboot.bin")));
            }
            finally { Directory.Delete(root, true); }
        }

        /// <summary>sceSaveDataMount3 systemBlocks -1 -> 0 is a default rule: applied below FW 5.00 without any option, not at 5.00+.</summary>
        [Theory]
        [InlineData("4", true)]
        [InlineData("4.51", true)]
        [InlineData("5.02", false)]
        public void Mount3Adapter_IsDefaultBelowFirmware5(string firmware, bool expected)
        {
            const string nid = "ZP4e7rlzOUk";
            string root = Path.Combine(Path.GetTempPath(), "ps5backport-default-mount-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
                string reference = Directory.CreateDirectory(Path.Combine(root, "reference")).FullName;
                string output = Path.Combine(root, "output");
                byte[] module = SyntheticBinary.Elf(imports: true);
                Encoding.ASCII.GetBytes(nid + "#A#A\0").CopyTo(module, 0x201);
                SyntheticBinary.W64(module, 0x130, SceElf.DtJmpRel);
                SyntheticBinary.W64(module, 0x140, SceElf.DtPltRelSz);
                SyntheticBinary.W64(module, 0x480, 0x426);
                Convert.FromHexString("FF255A0000006800000000").CopyTo(module, 0x420);
                module[0x380] = Patching.Ret;
                module.AsSpan(0x381, 64).Fill(Patching.Int3);
                File.WriteAllBytes(Path.Combine(game, "eboot.bin"), module);
                FirmwareDb.Save(new FirmwareExports(firmware, "test", false, new() { ["synthetic"] = new() { nid } }),
                                Path.Combine(reference, Firmware.Name(firmware) + ".json"));
                var knowledge = new Knowledge(Paths.DataDir);
                var fix = knowledge.Fixes[nid];
                Assert.Equal((Fix.KindAdapter, Fix.WhenAlways, "5.00"), (fix.Kind, fix.When, fix.Below));
                Assert.DoesNotContain("save_mount_compat", knowledge.Options.Keys);
                var options = new BackportOptions { Firmware = firmware, ReferenceDir = reference };
                var cooked = Pipeline.Backport(game, output, options, knowledge, cancel: TestContext.Current.CancellationToken);
                Assert.True(cooked.Published);
                var entry = Assert.Single(cooked.Modules);
                Assert.Empty(entry.Error);
                if (expected)
                {
                    Assert.Equal(nid, Assert.Single(entry.Fixed).Nid);
                    Assert.NotEqual(module, Self.Unwrap(File.ReadAllBytes(Path.Combine(output, "eboot.bin"))));
                }
                else
                {
                    Assert.Empty(entry.Fixed);
                    Assert.False(File.Exists(Path.Combine(output, "eboot.bin")));
                }
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void Gui_PrepareToggleStartsDisabled_AndMountIsNoLongerAnOption()
        {
            var vm = new MainViewModel(new AppSettings());
            Assert.DoesNotContain(vm.PatchOptions, o => o.Key == "save_mount_compat");
            var prepare = vm.PatchOptions.Single(o => o.Key == "save_prepare_compat");
            Assert.False(prepare.IsEnabled);
            prepare.IsEnabled = true;
            Assert.True(prepare.IsEnabled);
        }
    }
}
