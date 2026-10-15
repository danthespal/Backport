namespace Ps5Backport.Tests
{
    using System.Text;
    using Ps5Backport.Core;

    /// <summary>Opt-in <c>pad_vibration_compat</c>: scePadSetVibrationMode modes above 3 are clamped to 3 for targets below FW 8.00,
    /// where every console refuses modes 4 and 5 for a process stamped below SDK 8.00 (FINDINGS 6m).</summary>
    public sealed class OptionalInputTests
    {
        const string Nid = "W2G-yoyMF5U";   // scePadSetVibrationMode

        [Theory]
        [InlineData("4", true, true)]
        [InlineData("4", false, false)]
        [InlineData("7.61", true, true)]
        [InlineData("8", true, false)]
        public void Cook_ClampsVibrationModeOnlyWithOptionBelowFirmware8(string firmware, bool enabled, bool expected)
        {
            string root = Path.Combine(Path.GetTempPath(), "ps5backport-pad-vibration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
                string reference = Directory.CreateDirectory(Path.Combine(root, "reference")).FullName;
                string output = Path.Combine(root, "output");
                byte[] module = SyntheticBinary.Elf(imports: true);
                Encoding.ASCII.GetBytes(Nid + "#A#A\0").CopyTo(module, 0x201);
                SyntheticBinary.W64(module, 0x130, SceElf.DtJmpRel);
                SyntheticBinary.W64(module, 0x140, SceElf.DtPltRelSz);
                SyntheticBinary.W64(module, 0x480, 0x426);
                Convert.FromHexString("FF255A0000006800000000").CopyTo(module, 0x420);
                module[0x380] = Patching.Ret;
                module.AsSpan(0x381, 64).Fill(Patching.Int3);
                File.WriteAllBytes(Path.Combine(game, "eboot.bin"), module);
                // the function exists on every firmware (libScePad since 1.00), so the reference exports it
                FirmwareDb.Save(new FirmwareExports(firmware, "test", false, new() { ["libScePad"] = new() { Nid } }),
                                Path.Combine(reference, Firmware.Name(firmware) + ".json"));
                var knowledge = new Knowledge(Paths.DataDir);
                Assert.False(knowledge.Fixes.ContainsKey(Nid));
                var fix = knowledge.Options["pad_vibration_compat"].Fixes[Nid];
                Assert.Equal((Fix.KindAdapter, Fix.WhenAlways, "8.00", false), (fix.Kind, fix.When, fix.Below, fix.Proven));
                // cmp esi, 3 ; mov eax, 3 ; cmovg esi, eax: a signed clamp, so invalid negative modes still reach the native check
                Assert.Equal(["83fe03", "b803000000", "0f4ff0"], fix.Insns.Select(i => i.Hex));
                var options = new BackportOptions { Firmware = firmware, ReferenceDir = reference, Options = enabled ? ["pad_vibration_compat"] : [] };
                var analysis = Pipeline.Backport(game, output, options with { DryRun = true }, knowledge, cancel: TestContext.Current.CancellationToken);
                var cooked = Pipeline.Backport(game, output, options, knowledge, cancel: TestContext.Current.CancellationToken, analysis: analysis.Analysis);
                Assert.True(cooked.Published);
                var entry = Assert.Single(cooked.Modules);
                Assert.Empty(entry.Error);
                Assert.Equal(Assert.Single(analysis.Modules).Fixed.Select(f => (f.Nid, f.How)), entry.Fixed.Select(f => (f.Nid, f.How)));
                if (expected)
                {
                    var applied = Assert.Single(entry.Fixed);
                    Assert.Equal(Nid, applied.Nid);
                    Assert.StartsWith("adapter (argument fix) via cave", applied.How);
                    Assert.False(applied.Proven);
                    var patched = new SceElf(Self.Unwrap(File.ReadAllBytes(Path.Combine(output, "eboot.bin"))));
                    var import = Assert.Single(patched.Imports());
                    // the PLT entry now jumps to the cave, whose code is the three instructions followed by jmp [GOT]
                    long plt = patched.Off(patched.ReadU64(import.GotVaddr) - 6);
                    Assert.Equal(0xE9, patched.Data[plt]);
                    long cave = patched.Off((ulong)((long)patched.Vaddr(plt) + 5 + BitConverter.ToInt32(patched.Data, (int)plt + 1)));
                    Assert.Equal(Convert.FromHexString("83fe03b8030000000f4ff0ff25"), patched.Data.AsSpan((int)cave, 13).ToArray());
                }
                else
                {
                    Assert.Empty(entry.Fixed);
                    Assert.False(File.Exists(Path.Combine(output, "eboot.bin")));
                }
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
