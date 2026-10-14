namespace Ps5Backport.Tests
{
    using System.Buffers.Binary;
    using System.Text;
    using Ps5Backport.Core;

    /// <summary>The opt-in "Hide PSN error dialogs" option (data/fixes.json np_error_dialogs, group Online).</summary>
    public sealed class OptionalOnlineTests
    {
        const string Option = "np_error_dialogs";

        /// <summary>sceErrorDialog Open, UpdateStatus, GetStatus, Close.</summary>
        static readonly string[] Nids = ["M2ZF-ClLhgY", "WWiGuh9XfgQ", "t2FvHRXzgqk", "ekXHb1kDBl0"];

        public static TheoryData<string, string, bool, bool> Cases()
        {
            var data = new TheoryData<string, string, bool, bool>();
            foreach (string nid in Nids)
            {
                data.Add(nid, "4", true, true);
                data.Add(nid, "4", false, false);
                data.Add(nid, "11", true, true);    // no firmware gate: the dialog is a game/PSN matter, not a firmware one
            }
            return data;
        }

        /// <summary>Each adapter applies only with the option, on any target; it is placed in one piece, addresses the
        /// shared one-byte state in the bss page tail and ends with the jump through its GOT slot.</summary>
        [Theory]
        [MemberData(nameof(Cases))]
        public void Cook_AppliesErrorDialogAdaptersOnlyWithOption(string nid, string firmware, bool option, bool expected)
        {
            string root = Path.Combine(Path.GetTempPath(), "ps5backport-npdialog-" + Guid.NewGuid().ToString("N"));
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
                var fix = knowledge.Options[Option].Fixes[nid];
                Assert.True(fix.Contiguous);
                Assert.False(fix.PostCall);
                Assert.Equal(Fix.WhenAlways, fix.When);
                Assert.Equal("", fix.Below);
                Assert.Equal("np_error_dialog", fix.State);
                var cooked = Pipeline.Backport(game, output, new BackportOptions { Firmware = firmware, ReferenceDir = reference,
                    Options = option ? [Option] : [] },
                    knowledge, cancel: TestContext.Current.CancellationToken);
                var entry = Assert.Single(cooked.Modules);
                Assert.Empty(entry.Error);
                Assert.Equal(expected, entry.Fixed.Any(f => f.Nid == nid));
                if (!expected)
                    return;
                var elf = new SceElf(module.ToArray());
                FixApplier.Apply(elf, [nid], knowledge, knowledge.FixTable([Option]));
                long plt = elf.Off(0x420);
                Assert.Equal(0xE9, elf.Data[plt]);
                ulong cave = (ulong)(0x425 + BinaryPrimitives.ReadInt32LittleEndian(elf.Data.AsSpan((int)plt + 1)));
                AssertAdapter(elf, fix, cave, got: 0x480, state: SyntheticBinary.BssVaddr + SyntheticBinary.BssSize);
            }
            finally { Directory.Delete(root, true); }
        }

        /// <summary>The adapter bytes at <paramref name="cave"/>, linked: state displacements and the tail jump.</summary>
        static void AssertAdapter(SceElf elf, Fix fix, ulong cave, ulong got, ulong state)
        {
            ulong va = cave;
            int Rel(ulong at) => BinaryPrimitives.ReadInt32LittleEndian(elf.Data.AsSpan((int)elf.Off(at)));
            foreach (var insn in fix.Insns)
            {
                long off = elf.Off(va);
                Assert.False(insn.CallGot);
                if (insn.StateAt >= 0)
                {
                    Assert.Equal(insn.Bytes[..insn.StateAt], elf.Data[(int)off..(int)(off + insn.StateAt)]);
                    Assert.Equal(insn.Bytes[(insn.StateAt + 4)..], elf.Data[(int)(off + insn.StateAt + 4)..(int)(off + insn.Length)]);
                    Assert.Equal(state + (ulong)insn.StateOffset, va + (ulong)insn.Length + (ulong)(long)Rel(va + (ulong)insn.StateAt));
                }
                else
                    Assert.Equal(insn.Bytes, elf.Data[(int)off..(int)(off + insn.Length)]);
                va += (ulong)insn.Length;
            }
            Assert.Equal([0xFF, 0x25], elf.Data[(int)elf.Off(va)..(int)(elf.Off(va) + 2)]);
            Assert.Equal(got, va + 6 + (ulong)(long)Rel(va + 2));
        }

        /// <summary>Every short branch inside the adapters lands on an instruction boundary or on the tail jump,
        /// and the open adapter tests the NP and libSceNet facilities and sets the mark the status adapters clear.</summary>
        [Fact]
        public void ErrorDialogAdapters_BranchesLandOnInstructionBoundaries()
        {
            var option = Samples.K.Options[Option];
            Assert.Equal("Online", option.Group);
            Assert.Contains("Online", Samples.K.OptionGroups);
            Assert.Equal(Nids.Order(StringComparer.Ordinal), option.Fixes.Keys.Order(StringComparer.Ordinal));
            foreach (var fix in option.Fixes.Values)
            {
                var boundaries = new HashSet<int>();
                int at = 0;
                foreach (var insn in fix.Insns)
                {
                    boundaries.Add(at);
                    at += insn.Length;
                }
                boundaries.Add(at);                             // the tail jump through the GOT slot
                at = 0;
                foreach (var insn in fix.Insns)
                {
                    if (insn.Length == 2 && insn.Bytes[0] is 0x74 or 0x75 or 0xEB)
                        Assert.Contains(at + 2 + (sbyte)insn.Bytes[1], boundaries);
                    at += insn.Length;
                }
                Assert.All(fix.Insns.Where(i => i.StateAt >= 0), i => Assert.Equal(0, i.StateOffset));
            }
            var open = option.Fixes["M2ZF-ClLhgY"];
            Assert.Contains(open.Insns, i => i.Hex == "81f955800000");
            Assert.Contains(open.Insns, i => i.Hex == "81f941800000");
            Assert.Contains(open.Insns, i => i.Hex == "c605{state+0x0}01");
            foreach (string nid in new[] { "WWiGuh9XfgQ", "t2FvHRXzgqk" })
            {
                var status = option.Fixes[nid];
                Assert.Equal("803d{state+0x0}00", status.Insns[0].Hex);
                Assert.Contains(status.Insns, i => i.Hex == "b803000000");
                Assert.Equal("c3", status.Insns[^1].Hex);
            }
            Assert.Equal("c605{state+0x0}00", Assert.Single(option.Fixes["ekXHb1kDBl0"].Insns).Hex);
        }
    }
}
