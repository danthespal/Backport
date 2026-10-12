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
