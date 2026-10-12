using System.Buffers.Binary;
using System.Text;
using Ps5Backport.Core;

namespace Ps5Backport.Tests;

public sealed class SceDynamicTableTests
{
    const ulong SceStrtab = 0x61000035, SceStrsz = 0x61000037, SceSymtab = 0x61000039;
    const ulong SceRela = 0x6100002f, SceRelasz = 0x61000031, SceJmprel = 0x61000029, ScePltrelsz = 0x6100002d;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SceTables_MatchStandardTables_WithoutRewritingFileBytes(bool plt)
    {
        var standard = new SceElf(Image(false, plt));
        byte[] data = Image(true, plt), before = data.ToArray();
        var sce = new SceElf(data);
        Assert.Equal(standard.Symbols(), sce.Symbols());
        Assert.Equal(standard.Imports(), sce.Imports());
        Assert.Equal(standard.Relocs(plt ? RelocKind.JmpRel : RelocKind.Rela), sce.Relocs(plt ? RelocKind.JmpRel : RelocKind.Rela));
        Assert.True(standard.Exports().SetEquals(sce.Exports()));
        Assert.True(standard.ExportsByLibrary().SetEquals(sce.ExportsByLibrary()));
        Assert.Equal("libRequired", sce.ModuleName);
        Assert.Equal(0x498, sce.SymtabOffset(1));
        Assert.Equal(before, data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SceTables_RenameAndDefineSymbol_MatchStandardWrites(bool plt)
    {
        var standard = new SceElf(Image(false, plt));
        var sce = new SceElf(Image(true, plt));
        foreach (var elf in new[] { standard, sce })
        {
            Patching.RenameImport(elf, Assert.Single(elf.Imports()), "CCCCCCCCCCC");
            Patching.DefineSymbol(elf, 1, 0x1280);
            Assert.Equal("CCCCCCCCCCC", Assert.Single(elf.Imports()).Nid);
            Assert.True(Assert.Single(elf.Imports()).DefinedLocally);
        }
        Assert.Equal(standard.Data.AsSpan(0x400).ToArray(), sce.Data.AsSpan(0x400).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SceTables_StubImports_MatchStandardCodeAndTableWrites(bool plt)
    {
        var standard = new SceElf(Image(false, plt));
        var sce = new SceElf(Image(true, plt));
        foreach (var elf in new[] { standard, sce })
        {
            Assert.Single(Patching.StubImports(elf, ["AAAAAAAAAAA"], Knowledge.Default, defineSymbol: true));
            Assert.Equal(Imports.Stubbed, Assert.Single(Imports.Classify(elf, new HashSet<string>(), exact: true)).Status);
        }
        Assert.Equal(standard.Data.AsSpan(0x200).ToArray(), sce.Data.AsSpan(0x200).ToArray());
    }

    [Theory]
    [InlineData(SceStrtab, ulong.MaxValue)]
    [InlineData(SceStrtab, 0x301UL)]
    [InlineData(SceStrsz, 0x301UL)]
    [InlineData(SceStrsz, ulong.MaxValue)]
    [InlineData(SceSymtab, ulong.MaxValue)]
    [InlineData(SceSymtab, 0x2f0UL)]
    [InlineData(SceElf.DtSceSymtabSz, 73UL)]
    [InlineData(SceElf.DtSceSymtabSz, ulong.MaxValue)]
    [InlineData(SceRela, 0x2f0UL)]
    [InlineData(SceRelasz, 25UL)]
    [InlineData(SceRelasz, ulong.MaxValue)]
    [InlineData(SceJmprel, 0x2f0UL)]
    [InlineData(ScePltrelsz, 25UL)]
    public void SceTables_RejectOutOfSegmentOrTruncatedTables(ulong tag, ulong value)
    {
        var data = Image(true, tag is SceJmprel or ScePltrelsz);
        SetTag(data, tag, value);
        var elf = new SceElf(data);
        Assert.Throws<InvalidDataException>(() => elf.Imports());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SceTables_RequireOneDynlibDataSegment(bool duplicate)
    {
        byte[] data = Image(true, false);
        W32(data, 0xb0, duplicate ? 0x61000000U : 0U);
        if (duplicate) W32(data, 0x40, 0x61000000);
        Assert.Throws<InvalidDataException>(() => new SceElf(data).Symbols());
    }

    [Fact]
    public void SceTables_StringTerminatorMustFitDeclaredStringSize()
    {
        byte[] data = Image(true, false);
        SetTag(data, SceStrsz, 28); // Import-name terminator is at offset 28, outside this range.
        Assert.Throws<InvalidDataException>(() => new SceElf(data).Symbols());
    }

    [Fact]
    public void StandardTables_TakePrecedenceOverUnusedInvalidSceMetadata()
    {
        byte[] data = Image(false, false);
        W64(data, 0x190, SceStrtab); W64(data, 0x198, ulong.MaxValue);
        W64(data, 0x1a0, SceSymtab); W64(data, 0x1a8, ulong.MaxValue);
        Assert.Single(new SceElf(data).Imports());
    }

    [Theory]
    [InlineData(SceStrsz)]
    [InlineData(SceElf.DtSceSymtabSz)]
    [InlineData(SceRelasz)]
    public void SceTables_RequireMatchingSizeMetadata(ulong tag)
    {
        byte[] data = Image(true, false);
        for (int at = 0x100; at < 0x190; at += 16)
            if (BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(at)) == tag) W64(data, at, 0x6100ffff);
        Assert.Throws<InvalidDataException>(() => new SceElf(data).Imports());
    }

    [Theory]
    [InlineData(SceElf.DtSceSymEnt, 16UL)]
    [InlineData(SceElf.DtSceRelaEnt, 16UL)]
    [InlineData(SceElf.DtScePltRel, 17UL)]
    public void SceTables_RejectUnsupportedEntryFormats(ulong tag, ulong value)
    {
        byte[] data = Image(true, true);
        W64(data, 0x190, tag); W64(data, 0x198, value);
        Assert.Throws<InvalidDataException>(() => new SceElf(data).Imports());
    }

    [Fact]
    public void SceTables_AllowEmptyRelocationTableAtSegmentEnd()
    {
        byte[] data = Image(true, false);
        SetTag(data, SceRela, 0x300); SetTag(data, SceRelasz, 0);
        Assert.Empty(new SceElf(data).Relocs(RelocKind.Rela));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pipeline_CooksSceTables_WithEquivalentPayloadChanges(bool plt)
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ps5bp-sce-cook-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string db = Directory.CreateDirectory(Path.Combine(root, "db")).FullName;
            FirmwareDb.Save(new FirmwareExports("4", "synthetic", false, new()), Path.Combine(db, "4.00.json"));
            var payloads = new List<byte[]>();
            foreach (bool sce in new[] { false, true })
            {
                string game = Directory.CreateDirectory(Path.Combine(root, sce ? "sce" : "standard")).FullName;
                byte[] data = Image(sce, plt);
                data.AsSpan(0x40, 3 * 56).CopyTo(data.AsSpan(0x780));
                W64(data, 0x20, 0x780); W16(data, 0x38, 4);
                W32(data, 0x828, SceElf.PtSceModuleParam); W64(data, 0x830, 0x300);
                W64(data, 0x848, 0x18); W64(data, 0x850, 0x18);
                File.WriteAllBytes(Path.Combine(game, "a.sprx"), data);
                string output = Path.Combine(root, sce ? "sce-out" : "standard-out");
                var result = Pipeline.Backport(game, output, new BackportOptions
                {
                    Firmware = "4", ReferenceDir = db, KnownFixes = false,
                    StubNids = ["AAAAAAAAAAA"], DefineSymbol = true,
                }, Knowledge.Default, cancel: TestContext.Current.CancellationToken);
                Assert.True(result.Published);
                var module = Assert.Single(result.Modules);
                Assert.Empty(module.Error); Assert.True(module.Written); Assert.Single(module.StubbedNow);
                Assert.Equal(Imports.Stubbed, Assert.Single(module.Statuses).Status);
                payloads.Add(Self.Unwrap(File.ReadAllBytes(Path.Combine(output, "a.sprx"))).AsSpan(0x200, 0x500).ToArray());
            }
            Assert.Equal(payloads[0], payloads[1]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void SceTables_RejectSymbolWritesOutsideDeclaredTable(int index)
    {
        var elf = new SceElf(Image(true, false));
        byte[] before = elf.Data.ToArray();
        Assert.Throws<InvalidDataException>(() => Patching.DefineSymbol(elf, index, 0x1280));
        Assert.Equal(before, elf.Data);
    }

    static byte[] Image(bool sce, bool plt)
    {
        byte[] data = new byte[0x900];
        SceElf.Magic.CopyTo(data); data[4] = 2; data[5] = 1;
        W16(data, 0x10, SceElf.EtSceDynamic); W16(data, 0x12, 0x3e); W32(data, 0x14, 1);
        W64(data, 0x20, 0x40); W16(data, 0x34, 64); W16(data, 0x36, 56); W16(data, 0x38, 3);
        Header(0, SceElf.PtLoad, 0, 0x1000, sce ? 0x300UL : 0x900UL, SceElf.PfX);
        Header(1, SceElf.PtDynamic, 0x100, 0x1100, 0xd0, 0);
        Header(2, 0x61000000, 0x400, 0, 0x300, 0);
        byte[] strings = Encoding.ASCII.GetBytes("\0libRequired\0AAAAAAAAAAA#A#A\0BBBBBBBBBBB#A#A\0"); strings.CopyTo(data, 0x400);
        W32(data, 0x498, 13); data[0x49c] = SceElf.SttFunc;
        W32(data, 0x4b0, 29); data[0x4b4] = SceElf.SttFunc; W16(data, 0x4b6, 1); W64(data, 0x4b8, 0x1260);
        int relocation = plt ? 0x540 : 0x500;
        W64(data, relocation, 0x1240); W64(data, relocation + 8, (1UL << 32) | (plt ? 7UL : 6UL));
        data[0x220] = 0xff; data[0x221] = 0x25; data[0x226] = 0x68; W64(data, 0x240, 0x1226);
        data[0x27f] = 0xc3; data.AsSpan(0x280, 0x40).Fill(0xcc);
        int d = 0x100;
        Dynamic(sce ? SceStrtab : SceElf.DtStrtab, sce ? 0UL : 0x1400UL);
        Dynamic(sce ? SceStrsz : SceElf.DtStrSz, (ulong)strings.Length);
        Dynamic(sce ? SceSymtab : SceElf.DtSymtab, sce ? 0x80UL : 0x1480UL);
        Dynamic(SceElf.DtSceSymtabSz, 72);
        Dynamic(sce ? (plt ? SceJmprel : SceRela) : (plt ? SceElf.DtJmpRel : SceElf.DtRela), sce ? (ulong)(relocation - 0x400) : (ulong)(relocation + 0x1000));
        Dynamic(sce ? (plt ? ScePltrelsz : SceRelasz) : (plt ? SceElf.DtPltRelSz : SceElf.DtRelaSz), 24);
        Dynamic(SceElf.DtSceImportLib, 1); Dynamic(SceElf.DtSceExportLib, 1); Dynamic(SceElf.DtSceModuleName, 1);
        return data;
        void Dynamic(ulong tag, ulong value) { W64(data, d, tag); W64(data, d + 8, value); d += 16; }
        void Header(int index, uint type, ulong offset, ulong address, ulong size, uint flags) { int at = 0x40 + index * 56; W32(data, at, type); W32(data, at + 4, flags); W64(data, at + 8, offset); W64(data, at + 16, address); W64(data, at + 32, size); W64(data, at + 40, size); }
    }

    static void SetTag(byte[] data, ulong tag, ulong value)
    {
        for (int at = 0x100; at < 0x1d0; at += 16)
            if (BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(at)) == tag) { W64(data, at + 8, value); return; }
        throw new InvalidOperationException("Fixture tag missing");
    }
    static void W16(byte[] data, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at), value);
    static void W32(byte[] data, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), value);
    static void W64(byte[] data, int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(at), value);
}
