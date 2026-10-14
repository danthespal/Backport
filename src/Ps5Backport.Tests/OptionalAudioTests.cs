namespace Ps5Backport.Tests
{
    using System.Buffers.Binary;
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

        public static TheoryData<string, string, bool, bool> DownmixCases()
        {
            var data = new TheoryData<string, string, bool, bool>();
            foreach (string nid in DownmixNids)
            {
                data.Add(nid, "4", true, true);
                data.Add(nid, "4", false, false);
                data.Add(nid, "11", true, false);
            }
            return data;
        }

        /// <summary>sceAudioOut2 ContextCreate, PortCreate, PortSetAttributes, ContextAdvance, PortDestroy.</summary>
        static readonly string[] DownmixNids = ["0x6o1VVAYSY", "JK2wamZPzwM", "8XTArSPyWHk", "PE2zHMqLSHs", "cd+Rtw+D1x8"];

        /// <summary>The 12->8 downmix rules: only with the option, only below 11.00; each adapter is placed in one
        /// piece, calls/jumps through its GOT slot and addresses the shared state in the bss page tail.</summary>
        [Theory]
        [MemberData(nameof(DownmixCases))]
        public void Cook_AppliesAudioDownmixOnlyWithOptionBelowFirmware11(string nid, string firmware, bool option, bool expected)
        {
            string root = Path.Combine(Path.GetTempPath(), "ps5backport-downmix-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
                string reference = Directory.CreateDirectory(Path.Combine(root, "reference")).FullName;
                string output = Path.Combine(root, "output");
                byte[] padding = [Patching.Ret, .. Enumerable.Repeat(Patching.Int3, 0x1ff)];
                byte[] module = SyntheticBinary.Elf(padding, imports: true, writable: true);
                Encoding.ASCII.GetBytes(nid + "#A#A\0").CopyTo(module, 0x201);
                SyntheticBinary.W64(module, 0x130, SceElf.DtJmpRel);
                SyntheticBinary.W64(module, 0x140, SceElf.DtPltRelSz);
                SyntheticBinary.W64(module, 0x480, 0x426);
                Convert.FromHexString("FF255A0000006800000000").CopyTo(module, 0x420);
                File.WriteAllBytes(Path.Combine(game, "eboot.bin"), module);
                FirmwareDb.Save(new FirmwareExports(firmware, "test", false, new() { ["synthetic"] = new() { nid } }),
                    Path.Combine(reference, firmware + ".00.json"));
                var knowledge = new Knowledge(Paths.DataDir);
                Assert.False(knowledge.Fixes.ContainsKey(nid));
                var fix = knowledge.Options["audio_port_compat"].Fixes[nid];
                Assert.True(fix.Contiguous);
                Assert.Equal("audio_out2_downmix", fix.State);
                var cooked = Pipeline.Backport(game, output, new BackportOptions { Firmware = firmware, ReferenceDir = reference,
                    Options = option ? ["audio_port_compat"] : [] },
                    knowledge, cancel: TestContext.Current.CancellationToken);
                var entry = Assert.Single(cooked.Modules);
                Assert.Empty(entry.Error);
                Assert.Equal(expected, entry.Fixed.Any(f => f.Nid == nid));
                if (!expected)
                    return;
                var elf = new SceElf(module.ToArray());
                FixApplier.Apply(elf, [nid], knowledge, knowledge.FixTable(["audio_port_compat"]));
                long plt = elf.Off(0x420);
                Assert.Equal(0xE9, elf.Data[plt]);
                ulong cave = (ulong)(0x425 + BinaryPrimitives.ReadInt32LittleEndian(elf.Data.AsSpan((int)plt + 1)));
                AssertAdapter(elf, fix, cave, got: 0x480, state: SyntheticBinary.BssVaddr + SyntheticBinary.BssSize);
            }
            finally { Directory.Delete(root, true); }
        }

        /// <summary>The adapter bytes at <paramref name="cave"/>, linked: state displacements, call_got and the tail jump.</summary>
        static void AssertAdapter(SceElf elf, Fix fix, ulong cave, ulong got, ulong state)
        {
            ulong va = cave;
            int Rel(ulong at) => BinaryPrimitives.ReadInt32LittleEndian(elf.Data.AsSpan((int)elf.Off(at)));
            foreach (var insn in fix.Insns)
            {
                long off = elf.Off(va);
                if (insn.CallGot)
                {
                    Assert.Equal([0xFF, 0x15], elf.Data[(int)off..(int)(off + 2)]);
                    Assert.Equal(got, va + 6 + (ulong)(long)Rel(va + 2));
                }
                else if (insn.StateAt >= 0)
                {
                    Assert.Equal(insn.Bytes[..insn.StateAt], elf.Data[(int)off..(int)(off + insn.StateAt)]);
                    Assert.Equal(insn.Bytes[(insn.StateAt + 4)..], elf.Data[(int)(off + insn.StateAt + 4)..(int)(off + insn.Length)]);
                    Assert.Equal(state + (ulong)insn.StateOffset, va + (ulong)insn.Length + (ulong)(long)Rel(va + (ulong)insn.StateAt));
                }
                else
                    Assert.Equal(insn.Bytes, elf.Data[(int)off..(int)(off + insn.Length)]);
                va += (ulong)insn.Length;
            }
            if (!fix.PostCall)
            {
                Assert.Equal([0xFF, 0x25], elf.Data[(int)elf.Off(va)..(int)(elf.Off(va) + 2)]);
                Assert.Equal(got, va + 6 + (ulong)(long)Rel(va + 2));
            }
        }

        /// <summary>The 7.1 bed option: both engine patterns apply with the option on any target (no firmware gate),
        /// the PortGetState cap only below 11.00; the cap is one piece, calls through its GOT slot and returns.</summary>
        [Theory]
        [InlineData("4", true, true)]
        [InlineData("4", false, false)]
        [InlineData("11", true, false)]
        public void Cook_AppliesSevenOneBedOnlyWithOption(string firmware, bool option, bool expectCap)
        {
            const string nid = "gatEUKG+Ea4";
            string root = Path.Combine(Path.GetTempPath(), "ps5backport-bed71-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
                string reference = Directory.CreateDirectory(Path.Combine(root, "reference")).FullName;
                string output = Path.Combine(root, "output");
                var knowledge = new Knowledge(Paths.DataDir);
                var bed = knowledge.Options["audio_bed_71"];
                Assert.Equal(2, bed.Patterns.Count);
                Assert.All(bed.Patterns, p => Assert.True(p.Code));
                byte[] payload = [Patching.Ret, .. Enumerable.Repeat(Patching.Int3, 0x1ff), .. bed.Patterns[0].Find, .. bed.Patterns[1].Find];
                byte[] module = SyntheticBinary.Elf(payload, imports: true);
                Encoding.ASCII.GetBytes(nid + "#A#A\0").CopyTo(module, 0x201);
                SyntheticBinary.W64(module, 0x130, SceElf.DtJmpRel);
                SyntheticBinary.W64(module, 0x140, SceElf.DtPltRelSz);
                SyntheticBinary.W64(module, 0x480, 0x426);
                Convert.FromHexString("FF255A0000006800000000").CopyTo(module, 0x420);
                File.WriteAllBytes(Path.Combine(game, "eboot.bin"), module);
                FirmwareDb.Save(new FirmwareExports(firmware, "test", false, new() { ["synthetic"] = new() { nid } }),
                    Path.Combine(reference, firmware + ".00.json"));
                Assert.False(knowledge.Fixes.ContainsKey(nid));
                var fix = bed.Fixes[nid];
                Assert.True(fix.Contiguous);
                Assert.True(fix.PostCall);
                Assert.Equal("", fix.State);
                var cooked = Pipeline.Backport(game, output, new BackportOptions { Firmware = firmware, ReferenceDir = reference,
                    Options = option ? ["audio_bed_71"] : [] },
                    knowledge, cancel: TestContext.Current.CancellationToken);
                var entry = Assert.Single(cooked.Modules);
                Assert.Empty(entry.Error);
                Assert.Equal(option ? bed.Patterns.Select(p => (p.Name, 1)) : Enumerable.Empty<(string, int)>(),
                             entry.Patterns.Select(p => (p.Name, p.Count)));
                Assert.Equal(expectCap, entry.Fixed.Any(f => f.Nid == nid));
                if (!option)
                    return;
                var patched = new SceElf(Self.Unwrap(File.ReadAllBytes(Path.Combine(output, "eboot.bin"))));
                int at = 0x500 + 0x200;
                Assert.Equal(bed.Patterns[0].Replace, patched.Data[at..(at + bed.Patterns[0].Replace.Length)]);
                at += bed.Patterns[0].Replace.Length;
                Assert.Equal(bed.Patterns[1].Replace, patched.Data[at..(at + bed.Patterns[1].Replace.Length)]);
                if (!expectCap)
                    return;
                var elf = new SceElf(module.ToArray());
                FixApplier.Apply(elf, [nid], knowledge, knowledge.FixTable(["audio_bed_71"]));
                long plt = elf.Off(0x420);
                Assert.Equal(0xE9, elf.Data[plt]);
                ulong cave = (ulong)(0x425 + BinaryPrimitives.ReadInt32LittleEndian(elf.Data.AsSpan((int)plt + 1)));
                AssertAdapter(elf, fix, cave, got: 0x480, state: 0);
            }
            finally { Directory.Delete(root, true); }
        }

        /// <summary>Each 7.1 bed pattern matches Ghost of Yotei's eboot exactly once: the snd_SynthInit permitted-modes
        /// constant (file VA 0x30407c) and the probe's port-format constant (0x34d4f7).</summary>
        [Fact]
        public void SevenOneBed_PatternsMatchYoteiOnce()
        {
            var elf = new SceElf(File.ReadAllBytes(Repo.Need(".original", "backports", "PPSA26344", "decrypted", "eboot.bin")));
            var patterns = Samples.K.Options["audio_bed_71"].Patterns;
            foreach (var pattern in patterns)
                Assert.Equal(1, FixApplier.ApplyPattern(elf, pattern, TestContext.Current.CancellationToken));
            Assert.Equal(Convert.FromHexString("c745a0ef3f0000"), elf.Data[(int)elf.Off(0x30407c)..(int)(elf.Off(0x30407c) + 7)]);
            Assert.Equal(Convert.FromHexString("48b80008000080bb0000"), elf.Data[(int)elf.Off(0x34d4f7)..(int)(elf.Off(0x34d4f7) + 10)]);
            foreach (var pattern in patterns)
                Assert.Equal(0, FixApplier.ApplyPattern(elf, pattern, TestContext.Current.CancellationToken));
        }

        [Fact]
        public void FixTable_RejectsBothAudioBedOptions()
        {
            var k = Samples.K;
            Assert.NotNull(k.FixTable(["audio_bed_71"]));
            Assert.NotNull(k.FixTable(["audio_port_compat"]));
            var error = Assert.Throws<ArgumentException>(() => k.FixTable(["audio_port_compat", "audio_bed_71"]));
            Assert.Contains("cannot be combined", error.Message);
        }

        [Fact]
        public void Insn_StatePlaceholder_RoundTrips()
        {
            var insn = Insn.Parse("0fa32d{state+0x4}  ; bt dword ptr [rip + state+0x4], ebp");
            Assert.Equal(3, insn.StateAt);
            Assert.Equal(4, insn.StateOffset);
            Assert.Equal(7, insn.Length);
            Assert.Equal("0fa32d{state+0x4}", insn.Hex);
            Assert.Equal(-1, Insn.Parse("c3").StateAt);
        }

        /// <summary>
        /// The downmix state fits the 0x58-byte page tail of Battlefield 6 and PPSA34008 (EA eboots), where the
        /// 16-slot 0x90-byte state failed and aborted the whole cook. 8 slots: Yotei peaks at 3 bed ports + the probe.
        /// </summary>
        [Theory]
        [InlineData("PPSA19534", "default", "eboot.bin")]
        [InlineData("PPSA19534", "default", "SP/BattlefieldGame.Main_Ps5_retail.elf")]
        [InlineData("PPSA34008", "default", "eboot.bin")]
        public void AudioDownmixState_FitsEaPageTail(string title, string variant, string module)
        {
            var fix = Samples.K.Options["audio_port_compat"].Fixes["JK2wamZPzwM"];
            Assert.Equal(8 + 8 * 8, fix.StateSize);
            string path = Samples.Backports(title, variant, module);
            byte[] raw = File.ReadAllBytes(path);
            var elf = new SceElf(Self.IsSelf(Self.FileKind(path)) ? Self.Unwrap(raw) : raw);
            Patching.AllocateState(elf, fix.State, fix.StateSize);
        }

        /// <summary>State lives past memsz in the last page of a writable segment, one block per name, aligned.</summary>
        [Fact]
        public void AllocateState_UsesWritablePageTail()
        {
            var elf = new SceElf(SyntheticBinary.Elf(writable: true));
            ulong first = Patching.AllocateState(elf, "a", 0x90);
            Assert.Equal(SyntheticBinary.BssVaddr + SyntheticBinary.BssSize, first);
            Assert.Equal(first, Patching.AllocateState(elf, "a", 0x90));
            Assert.Equal(first + 0x90, Patching.AllocateState(elf, "b", 8));
            Assert.Throws<PatchException>(() => Patching.AllocateState(elf, "c", (int)Patching.PageSize));
        }

        [Fact]
        public void AllocateState_RefusesWithoutWritableSpace()
        {
            Assert.Throws<PatchException>(() => Patching.AllocateState(new SceElf(SyntheticBinary.Elf()), "a", 8));
            // a mapped segment in the same page blocks it; a flags-0 one (never mapped there) does not
            foreach (var (flags, allowed) in new[] { (4u, false), (0u, true) })
            {
                byte[] data = SyntheticBinary.Elf(writable: true);
                int ph = (int)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x20)) + 2 * 0x38;   // reuse the module-param header
                SyntheticBinary.W32(data, ph, SceElf.PtLoad); SyntheticBinary.W32(data, ph + 4, flags);
                SyntheticBinary.W64(data, ph + 0x10, SyntheticBinary.BssVaddr + 0x200);
                SyntheticBinary.W64(data, ph + 0x20, 0); SyntheticBinary.W64(data, ph + 0x28, 0x100);
                var elf = new SceElf(data);
                if (allowed)
                    Assert.Equal(SyntheticBinary.BssVaddr + SyntheticBinary.BssSize, Patching.AllocateState(elf, "a", 8));
                else
                    Assert.Throws<PatchException>(() => Patching.AllocateState(elf, "a", 8));
            }
        }

        /// <summary>A contiguous adapter larger than any cave goes into joined blocks of the code-space extension.</summary>
        [Fact]
        public void PlaceChain_Contiguous_JoinsExtensionBlocks()
        {
            byte[] data = SyntheticBinary.Elf(new byte[0x400], imports: true, writable: true);
            int ph = (int)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x20));
            SyntheticBinary.W64(data, ph + 0x20, 0x500); SyntheticBinary.W64(data, ph + 0x28, 0x500);   // .text ends at 0x500,
            SyntheticBinary.W64(data, ph + 3 * 0x38 + 0x10, 0x900);                                      // 0x400 zero bytes before the bss
            var elf = new SceElf(data) { AllowTextExtension = true };
            ulong state = Patching.AllocateState(elf, "s", 8);
            var insns = Enumerable.Range(0, 150).Select(_ => new Insn([0x90], false, "nop")).Append(Insn.Parse("8905{state+0x4}")).ToList();
            ulong at = Patching.PlaceChain(elf, insns, 0x480, tailJmp: true, contiguous: true, stateVaddr: state);
            Assert.True(elf.TextExtended > 0);
            Assert.True(at >= 0x500);
            long off = elf.Off(at);
            Assert.All(elf.Data[(int)off..(int)(off + 150)], b => Assert.Equal(0x90, b));
            int disp = BinaryPrimitives.ReadInt32LittleEndian(elf.Data.AsSpan((int)off + 152));
            Assert.Equal(state + 4, at + 156 + (ulong)(long)disp);
            Assert.Equal([0xFF, 0x25], elf.Data[(int)(off + 156)..(int)(off + 158)]);
            Assert.Throws<PatchException>(() =>
                Patching.PlaceChain(elf, Enumerable.Range(0, 0x500).Select(_ => new Insn([0x90], false, "")).ToList(), 0x480, contiguous: true));
        }
    }
}
