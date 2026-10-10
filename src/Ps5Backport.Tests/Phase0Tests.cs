using Ps5Backport.Core;

namespace Ps5Backport.Tests;

/// <summary>Phase 0.4: NID, name DB and firmware primitives (golden results recorded from ps5bp).</summary>
public class NidTests
{
    [Theory]
    [InlineData("sceKernelIsTrinityMode", "tU5e3f9gSiU")]          // CONTROL README
    [InlineData("sceKernelSyncOnAddressWait8", "dFym5b41JLU")]      // REFERENCE_OVERVIEW Part 8
    [InlineData("sceKernelSyncOnAddressWait16", "XgVs4jRehcY")]
    [InlineData("sceKernelGetOperationMode", "NH6xARDOVv8")]
    [InlineData("sceSaveDataMount3", "ZP4e7rlzOUk")]
    public void Compute_KnownValues(string name, string nid) => Assert.Equal(nid, Nid.Compute(name));

    [Fact]
    [Trait("Lane", "Private")]
    public void Compute_MatchesGolden_ForAerolibNames()
    {
        string aerolib = Repo.Need(".original", "reference", "sce_symbols_new", "aerolib.csv");
        // every 7th name: a broad, deterministic sample (incl. C++ mangled names)
        string[] names = File.ReadLines(aerolib)
            .Select(l => l.Split(' ', 2))
            .Where(p => p.Length == 2)
            .Select(p => p[1].Trim())
            .Where((_, i) => i % 7 == 0)
            .ToArray();
        Golden.Match("aerolib-nids.txt", $"{names.Length} names, sha256 {Golden.Sha(string.Join("\n", names.Select(n => $"{Nid.Compute(n)} {n}")))}\n");
    }

    [Theory]
    [InlineData("tU5e3f9gSiU#s#BM", "tU5e3f9gSiU", 44, 76)]
    [InlineData("dFym5b41JLU#A#A", "dFym5b41JLU", 0, 0)]
    public void SplitSymbol_Values(string symbol, string nid, int library, int module)
    {
        Assert.Equal((nid, library, module), Nid.SplitSymbol(symbol));
    }

    [Theory]
    [InlineData("printf")]
    [InlineData("short#A#B")]
    [InlineData("tU5e3f9gSiU#s")]
    public void SplitSymbol_NonNid(string symbol) => Assert.Null(Nid.SplitSymbol(symbol));

    [Fact]
    [Trait("Lane", "Private")]
    public void NameDb_MatchesGolden()
    {
        string aerolib = Repo.Need(".original", "reference", "sce_symbols_new", "aerolib.csv");
        var db = new NameDb(aerolib);
        string[] probe = ["tU5e3f9gSiU", "GGeRJk1XdWc", "D-CzAxQL0XI", "XXXXXXXXXXX"];
        Golden.Match("namedb.txt", $"{db.Count}\n" + string.Join("", probe.Select(n => $"{n} {db.Name(n)}\n")));
    }
}

public class FirmwareTests
{
    static readonly string[] Versions = ["1", "4", "4.03", "4.3", " 7.61 ", "10", "10.01", "4.30"];

    [Fact]
    public void Firmware_MatchesGolden()
    {
        var actual = Versions.Select(v =>
        {
            var (major, minor) = Firmware.Parse(v);
            var (ps5, ps4) = Firmware.SdkFor(v);
            return $"'{v}' {major} {minor} {Firmware.Name(v)} 0x{ps5:x8} 0x{ps4:x8} {Firmware.ParamVersion(v)}\n";
        });
        Golden.Match("firmware.txt", string.Concat(actual));
    }

    [Fact]
    public void SdkFor_KnownBackports()
    {
        Assert.Equal((0x04030031u, 0x09040001u), Firmware.SdkFor("4.03"));   // CONTROL Resonant
        Assert.Equal((0x04000031u, 0x09040001u), Firmware.SdkFor("4"));      // GT7
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("4.x")]
    [InlineData("")]
    [InlineData(".5")]
    public void Parse_Invalid(string value) => Assert.Throws<FormatException>(() => Firmware.Parse(value));

    [Fact]
    public void SdkFor_UnknownMajor() => Assert.Throws<ArgumentException>(() => Firmware.SdkFor("12"));
}
