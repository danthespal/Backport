namespace Ps5Backport.Tests
{
    using System.Text;
    using Ps5Backport.Core;

    public sealed class OptionalAudioTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnalyzeAndCook_OnlyAdaptSpeakerInfoWhenSelected(bool enabled)
        {
            const string nid = "DImz2Ft9E2g";
            string root = Path.Combine(Path.GetTempPath(), "ps5backport-optional-audio-" + Guid.NewGuid().ToString("N"));
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
                Assert.True(knowledge.Fixes.ContainsKey("TViD1EZXkNI"));
                var options = new BackportOptions { Firmware = "4", ReferenceDir = reference,
                    Options = enabled ? ["audio_speaker_compat"] : [] };
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
                    var patched = new SceElf(Self.Unwrap(File.ReadAllBytes(Path.Combine(output, "eboot.bin"))));
                    Assert.Equal(0x426UL, patched.ReadU64(Assert.Single(patched.Imports()).GotVaddr));
                    Assert.NotEqual(module, patched.Data);
                }
                else
                    Assert.False(File.Exists(Path.Combine(output, "eboot.bin")));
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
