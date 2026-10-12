using System.Buffers.Binary;
using System.Text;
using Ps5Backport.Core;

namespace Ps5Backport.Tests;

public sealed class ProviderAuditTests
{
    [Theory]
    [InlineData("none", "", Imports.Missing)]
    [InlineData("libRequired", "same-library", Imports.Provided)]
    [InlineData("libUnrelated", "cross-library", Imports.Provided)]
    [InlineData("self", "", Imports.Missing)]
    public void Audit_DistinguishesLibraries_WithoutChangingClassification(string provider, string match, string status)
    {
        var elf = new SceElf(Image("libRequired", imports: true, exports: provider == "self"));
        var providers = new List<(string Module, SceElf Elf)>();
        if (provider != "none")
            providers.Add(provider == "self" ? ("a.sprx", elf) : ("b.sprx", new SceElf(Image(provider, exports: true))));
        byte[] before = elf.Data.ToArray();
        var provided = providers.Where(p => p.Module != "a.sprx").SelectMany(p => p.Elf.Exports()).ToHashSet();
        var original = Imports.Classify(elf, new HashSet<string>(), provided, exact: true);
        var finding = Assert.Single(Imports.AuditProviders("a.sprx", elf, providers));
        Assert.Equal("libRequired", finding.Library);
        if (match.Length == 0) Assert.Empty(finding.Providers);
        else Assert.Equal(match, Assert.Single(finding.Providers).Match);
        Assert.Equal(status, Assert.Single(original).Status);
        Assert.Equal(original, Imports.Classify(elf, new HashSet<string>(), provided, exact: true));
        Assert.Equal(before, elf.Data);
    }

    [Fact]
    public void Audit_KeepsSameNidUnderDifferentLibraries_AndAllProviderCandidates()
    {
        var elf = new SceElf(Image("libRequired", imports: true, dual: true));
        var provider = new SceElf(Image("libRequired", exports: true));
        var other = new SceElf(Image("libUnrelated", exports: true));
        var findings = Imports.AuditProviders("a.sprx", elf, [("b.sprx", provider), ("c.sprx", other), ("b.sprx", provider)]);
        Assert.Equal(2, findings.Count);
        Assert.All(findings, f =>
        {
            Assert.Equal(2, f.Providers.Count);
            Assert.Single(f.Providers, p => p.Match == "same-library");
            Assert.Single(f.Providers, p => p.Match == "cross-library");
        });
        string report = Imports.ProviderReport(findings);
        Assert.Contains("[libRequired] AAAAAAAAAAA", report);
        Assert.Contains("[libUnrelated] AAAAAAAAAAA", report);
        Assert.Single(Imports.Classify(elf, new HashSet<string>(), exact: true));
    }

    [Fact]
    public void Audit_ExcludesSelfEvenWhenLoadedAsAnotherObject()
    {
        var elf = new SceElf(Image("libRequired", imports: true, exports: true));
        Assert.Empty(Assert.Single(Imports.AuditProviders("a.sprx", elf,
            [("a.sprx", new SceElf(elf.Data.ToArray()))])).Providers);
    }

    [Fact]
    public void Audit_DoesNotInventAnAliasRuleForLibc()
    {
        var elf = new SceElf(Image("libSceLibcInternal", imports: true));
        var finding = Assert.Single(Imports.AuditProviders("a.sprx", elf, [("libc.prx", new SceElf(Image("libc", exports: true)))]));
        Assert.Equal("cross-library", Assert.Single(finding.Providers).Match);
        Assert.Contains("may be aliases/re-exports", Imports.ProviderReport([finding]));
    }

    [Fact]
    public void Audit_MissingLibraryMetadata_IsNotASameLibraryMatch()
    {
        var elf = new SceElf(Image("libRequired", imports: true));
        var finding = Assert.Single(Imports.AuditProviders("a.sprx", elf, [("b.sprx", new SceElf(Image("", exports: true)))]));
        Assert.Equal("unknown-library", Assert.Single(finding.Providers).Match);
    }

    [Fact]
    public void Cli_AuditIsOptIn_AndReadsFilesWithoutModifyingThem()
    {
        string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ps5bp-provider-audit-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string importer = Path.Combine(folder, "a.sprx"), provider = Path.Combine(folder, "b.sprx");
            byte[] original = Image("libRequired", imports: true), exported = Image("libUnrelated", exports: true);
            File.WriteAllBytes(importer, original); File.WriteAllBytes(provider, exported);
            string db = Directory.CreateDirectory(Path.Combine(folder, "db")).FullName;
            FirmwareDb.Save(new FirmwareExports("4", "synthetic", false, new()), Path.Combine(db, "4.00.json"));
            string[] arguments = ["report", importer, "--provider", provider, "--reference", db, "--all"];
            var normal = new StringWriter(); var audit = new StringWriter(); var error = new StringWriter();
            Assert.Equal(0, Cli.Run(arguments, normal, error));
            Assert.Equal(0, Cli.Run([.. arguments, "--provider-audit"], audit, error));
            Assert.Equal("", error.ToString());
            Assert.DoesNotContain("provider audit", normal.ToString());
            Assert.StartsWith(normal.ToString().TrimEnd(), audit.ToString());
            Assert.Contains("cross-library:", audit.ToString());
            Assert.Contains(provider, audit.ToString());
            Assert.Equal(original, File.ReadAllBytes(importer)); Assert.Equal(exported, File.ReadAllBytes(provider));
            Assert.Equal(3, Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Count());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    static byte[] Image(string library, bool imports = false, bool exports = false, bool dual = false)
    {
        byte[] data = new byte[0x800];
        SceElf.Magic.CopyTo(data); data[4] = 2; data[5] = 1; data[6] = 1;
        W16(0x10, SceElf.EtSceDynamic); W16(0x12, 0x3e); W32(0x14, 1);
        W64(0x20, 0x40); W16(0x34, 64); W16(0x36, 56); W16(0x38, 3);
        Header(0, SceElf.PtLoad, 0, 0x1000, 0x800); Header(1, SceElf.PtDynamic, 0x200, 0x1200, 10 * 16);
        Header(2, SceElf.PtSceModuleParam, 0x180, 0x1180, 0x18);
        byte[] strings = Encoding.ASCII.GetBytes("\0" + library + "\0AAAAAAAAAAA#A#A\0" + (dual ? "libUnrelated\0AAAAAAAAAAA#B#A\0" : "")); strings.CopyTo(data, 0x400);
        W32(0x518, (uint)(library.Length + 2)); data[0x51c] = 2;
        W32(0x530, (uint)(library.Length + 2)); data[0x534] = 2;
        W16(0x536, exports ? (ushort)1 : (ushort)0); W64(0x538, exports ? 0x1750UL : 0);
        W64(0x600, 0x1700); W64(0x608, (1UL << 32) | 6);
        if (dual) { W32(0x530, (uint)(library.Length + 31)); W64(0x618, 0x1708); W64(0x620, (2UL << 32) | 6); }
        int d = 0x200;
        Dynamic(SceElf.DtStrtab, 0x1400); Dynamic(SceElf.DtStrSz, (ulong)strings.Length); Dynamic(SceElf.DtSymtab, 0x1500);
        Dynamic(SceElf.DtSceSymtabSz, 72); Dynamic(SceElf.DtRela, 0x1600); Dynamic(SceElf.DtRelaSz, imports ? (dual ? 48UL : 24UL) : 0);
        Dynamic(SceElf.DtSceImportLib, 1); if (exports) Dynamic(SceElf.DtSceExportLib, 1); Dynamic(SceElf.DtSceModuleName, 1);
        if (dual) Dynamic(SceElf.DtSceImportLib, (1UL << 48) | (uint)(library.Length + 18));
        return data;
        void Dynamic(ulong tag, ulong value) { W64(d, tag); W64(d + 8, value); d += 16; }
        void Header(int index, uint type, ulong offset, ulong address, ulong size) { int at = 0x40 + index * 56; W32(at, type); W32(at + 4, 4); W64(at + 8, offset); W64(at + 16, address); W64(at + 32, size); W64(at + 40, size); }
        void W16(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at), value);
        void W32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), value);
        void W64(int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(at), value);
    }
}
