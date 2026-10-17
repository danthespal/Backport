namespace Ps5Backport.Tests
{
    using Ps5Backport.Core;

    /// <summary>
    /// FINDINGS 6v: the store's Agc/AgcDriver/Psml keep the console queries and libc++ mutexes as imports the loader can
    /// bind (st_shndx 0, PLT 'jmp [GOT]'), so the console's own function answers wherever the firmware exports it. Before
    /// 2026-10-17 they were local definitions, which forced "not Pro" and no-op mutexes on every firmware.
    /// </summary>
    [Trait("Lane", "Private")]
    public sealed class StoreImportFormTests
    {
        const string OperationMode = "NH6xARDOVv8";
        static readonly string[] Scoped =
        [
            "tU5e3f9gSiU",   // sceKernelIsTrinityMode
            "yu17wG8L5FI",   // sceKernelHasTrinityMode
            OperationMode,   // sceKernelGetOperationMode
            "TdAc0h4iomU", "XP6c8M4G+fc", "5qXct3c1skg", "4bp9gcNLwMI",   // libc++ _Mtx_init/destroy/lock/unlock
            "eSgYzXvTPQI", "ciYlptMdvhc",   // Agc: unnamed libkernel Pro-mode queries behind sceAgcGetRegisterDefaults* (from 9.00)
            "d7xu4QDJw8Q", "TUZlvaB9rlg",   // Psml: sceKernelGetAppInfo2, sceKernelTitleWorkaroundIsEnabled2 (MFSR2 override, from 6.00)
        ];

        /// <summary>
        /// A GOT slot holds either Sony's lazy PLT+6 or a pre-fill with the base answer for firmwares that lack the export.
        /// <paramref name="prefilled"/>: every slot is pre-filled (the former local definitions). The 9.60 Psml kept Sony's
        /// imports except GetOperationMode, whose PLT jumped straight to the stub.
        /// </summary>
        [Theory]
        [InlineData("4/libSceAgc.sprx", 3, true)]
        [InlineData("4/agc_mode2/libSceAgc.sprx", 3, true)]
        [InlineData("4.03/libSceAgc.sprx", 3, true)]
        [InlineData("4.03/agc_mode2/libSceAgc.sprx", 3, true)]
        [InlineData("9.60/libSceAgc.sprx", 3, true)]
        [InlineData("4/libSceAgcDriver.sprx", 1, true)]
        [InlineData("4/gpu_scratch/libSceAgcDriver.sprx", 1, true)]
        [InlineData("4.03/libSceAgcDriver.sprx", 1, true)]
        [InlineData("4.03/gpu_scratch/libSceAgcDriver.sprx", 1, true)]
        [InlineData("9.60/libSceAgcDriver.sprx", 1, true)]
        [InlineData("4/libScePsml.sprx", 8, true)]
        [InlineData("4/psml_kpn9/libScePsml.sprx", 8, true)]
        [InlineData("4.03/libScePsml.sprx", 6, true)]
        [InlineData("10/libScePsml.sprx", 8, true)]
        [InlineData("11/libScePsml.sprx", 8, true)]
        [InlineData("9.60/libScePsml.sprx", 8, false)]
        [InlineData("9.60/psml_kpn9/libScePsml.sprx", 8, false)]
        public void ConsoleQueries_AreImportsTheLoaderCanBind(string file, int count, bool prefilled)
        {
            var elf = new SceElf(Self.Unwrap(File.ReadAllBytes(Repo.Need(["fakelibs", .. file.Split('/')]))));
            var symbols = elf.Symbols();
            var slots = elf.Relocs(RelocKind.JmpRel).Where(r => symbols[r.SymIndex].Nid is { } nid && Scoped.Contains(nid)).ToList();
            Assert.Equal(count, slots.Count);
            var text = elf.TextSegment();
            foreach (var slot in slots)
            {
                var symbol = symbols[slot.SymIndex];
                Assert.Equal((0, 0ul), (symbol.Shndx, symbol.Value));   // an import, as in Sony's file
                Assert.Equal(7u, slot.Type);                            // R_X86_64_JUMP_SLOT
                ulong plt = PltThrough(elf, text, slot.Offset);
                ulong fill = elf.ReadU64(slot.Offset);
                if (fill == plt + 6)
                {
                    Assert.False(prefilled || symbol.Nid == OperationMode, $"{symbol.Name}: lazy GOT, expected a pre-fill");   // absent below 11.00
                    continue;
                }
                string body = Convert.ToHexStringLower(elf.Data.AsSpan(checked((int)elf.Off(fill)), 3));
                Assert.Equal(symbol.Nid == OperationMode ? "4885ff" : "31c0c3", body);   // test rdi, rdi (writes 0, 0) | xor eax, eax; ret
            }
        }

        /// <summary><see cref="Patching.RestoreImport"/> (CLI `fakelib restore-imports`) rebuilds each store file byte for byte from
        /// the original kept in work/fakelib-backups, unwrap → restore → sign.</summary>
        [Theory]
        [InlineData("4/libSceAgc.sprx")]
        [InlineData("4/agc_mode2/libSceAgc.sprx")]
        [InlineData("4.03/libSceAgc.sprx")]
        [InlineData("4.03/agc_mode2/libSceAgc.sprx")]
        [InlineData("9.60/libSceAgc.sprx")]
        [InlineData("4/libSceAgcDriver.sprx")]
        [InlineData("4/gpu_scratch/libSceAgcDriver.sprx")]
        [InlineData("4.03/libSceAgcDriver.sprx")]
        [InlineData("4.03/gpu_scratch/libSceAgcDriver.sprx")]
        [InlineData("9.60/libSceAgcDriver.sprx")]
        [InlineData("4/libScePsml.sprx")]
        [InlineData("4/psml_kpn9/libScePsml.sprx")]
        [InlineData("4.03/libScePsml.sprx")]
        [InlineData("9.60/libScePsml.sprx")]
        [InlineData("9.60/psml_kpn9/libScePsml.sprx")]
        public void RestoreImports_RebuildsTheStoreFile(string file)
        {
            var elf = new SceElf(Self.Unwrap(File.ReadAllBytes(Repo.Need(["work", "fakelib-backups", "native-imports-2026-10-17", .. file.Split('/')]))));
            var before = Imports.Classify(elf, new HashSet<string>()).ToDictionary(s => s.Nid, s => s.Status);
            var restored = Scoped.Where(before.ContainsKey).Select(nid => Patching.RestoreImport(elf, nid)).OfType<ImportRestore>().ToList();
            Assert.NotEmpty(restored);
            Assert.All(restored, r => Assert.Equal(r.WasDefined ? Imports.Forced : Imports.Patched, before[r.Nid]));   // 9.60 Psml: PLT 'jmp stub'
            Assert.Equal(File.ReadAllBytes(Repo.Need(["fakelibs", .. file.Split('/')])), Self.Sign(elf.Data));
            var after = Imports.Classify(elf, new HashSet<string>()).ToDictionary(s => s.Nid, s => s.Status);
            Assert.All(restored, r => Assert.Equal(Imports.Stubbed, after[r.Nid]));   // a GOT pre-fill the loader can override
        }

        /// <summary>A second run changes nothing; data imports and NIDs the module does not import are refused.</summary>
        [Fact]
        public void RestoreImport_IsIdempotentAndRefusesDataImports()
        {
            byte[] store = Self.Unwrap(File.ReadAllBytes(Repo.Need("fakelibs", "4", "libScePsml.sprx")));
            var elf = new SceElf((byte[])store.Clone());
            var imported = elf.Imports().Select(i => i.Nid).ToHashSet();
            Assert.All(Scoped.Where(imported.Contains), nid => Assert.Null(Patching.RestoreImport(elf, nid)));
            Assert.Equal(store, elf.Data);
            Assert.Contains("RELA", Assert.Throws<PatchException>(() => Patching.RestoreImport(elf, "9Qe7XFSQ4Lk")).Message);   // std::cout
            Assert.Contains("not imported", Assert.Throws<PatchException>(() => Patching.RestoreImport(elf, "yu17wG8L5FI")).Message);
        }

        /// <summary>The PLT entry that jumps through <paramref name="got"/> ('ff 25 rel32'); exactly one must exist.</summary>
        static ulong PltThrough(SceElf elf, Phdr text, ulong got)
        {
            var code = elf.Data.AsSpan(checked((int)text.Offset), checked((int)text.Filesz));
            var hits = new List<ulong>();
            for (int i = 0; i + 6 <= code.Length; i++)
                if (code[i] == 0xff && code[i + 1] == 0x25
                    && text.Vaddr + (ulong)i + 6 + (ulong)(long)BitConverter.ToInt32(code.Slice(i + 2, 4)) == got)
                    hits.Add(text.Vaddr + (ulong)i);
            return Assert.Single(hits);
        }
    }
}
