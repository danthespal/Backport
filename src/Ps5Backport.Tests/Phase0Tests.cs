using Ps5Backport.Core;

namespace Ps5Backport.Tests;

/// <summary>Phase 0.4: NID, name DB and firmware primitives, checked against ps5bp.</summary>
public class NidTests
{
    [Theory]
    [InlineData("sceKernelIsTrinityMode", "tU5e3f9gSiU")]          // CONTROL README
    [InlineData("sceKernelSyncOnAddressWait8", "dFym5b41JLU")]      // REFERENCE_OVERVIEW Part 8
    [InlineData("sceKernelSyncOnAddressWait16", "XgVs4jRehcY")]
    public void Compute_KnownValues(string name, string nid) => Assert.Equal(nid, Nid.Compute(name));

    [Fact]
    public void Compute_MatchesPython_ForAerolibNames()
    {
        string aerolib = Oracle.Need(".original", "reference", "sce_symbols_new", "aerolib.csv");
        // every 7th name: a broad, deterministic sample (incl. C++ mangled names)
        string[] names = File.ReadLines(aerolib)
            .Select(l => l.Split(' ', 2))
            .Where(p => p.Length == 2)
            .Select(p => p[1].Trim())
            .Where((_, i) => i % 7 == 0)
            .ToArray();
        string list = System.Text.Json.JsonSerializer.Serialize(names);
        var expected = Oracle.Json<string[]>($$"""
            import json
            from ps5bp.nid import compute_nid
            names = json.loads({{System.Text.Json.JsonSerializer.Serialize(list)}})
            print(json.dumps([compute_nid(n) for n in names]))
            """);
        Assert.Equal(expected, names.Select(Nid.Compute).ToArray());
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
    public void NameDb_MatchesPython()
    {
        string aerolib = Oracle.Need(".original", "reference", "sce_symbols_new", "aerolib.csv");
        var db = new NameDb(aerolib);
        var expected = Oracle.Json<Dictionary<string, object>>("""
            import json
            from ps5bp.nid import NameDb
            db = NameDb('.original/reference/sce_symbols_new/aerolib.csv')
            probe = ['tU5e3f9gSiU', 'GGeRJk1XdWc', 'D-CzAxQL0XI', 'XXXXXXXXXXX']
            print(json.dumps({'count': len(db.names), 'names': [db.name(n) for n in probe]}))
            """);
        Assert.Equal(((System.Text.Json.JsonElement)expected["count"]).GetInt32(), db.Count);
        string[] names = ((System.Text.Json.JsonElement)expected["names"]).EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(names, new[] { "tU5e3f9gSiU", "GGeRJk1XdWc", "D-CzAxQL0XI", "XXXXXXXXXXX" }.Select(db.Name).ToArray());
    }
}

public class FirmwareTests
{
    static readonly string[] Versions = ["1", "4", "4.03", "4.3", " 7.61 ", "10", "10.01", "4.30"];

    [Fact]
    public void Firmware_MatchesPython()
    {
        string list = System.Text.Json.JsonSerializer.Serialize(Versions);
        var expected = Oracle.Json<string[][]>($$"""
            import json
            from ps5bp import sdk, fwdb
            out = []
            for v in json.loads({{System.Text.Json.JsonSerializer.Serialize(list)}}):
                ps5, ps4 = sdk.sdk_for_firmware(v)
                major, minor = sdk.parse_firmware(v)
                out.append([str(major), str(minor), fwdb.fw_name(v), f'{ps5:#010x}', f'{ps4:#010x}', sdk.param_version(v)])
            print(json.dumps(out))
            """);
        var actual = Versions.Select(v =>
        {
            var (major, minor) = Firmware.Parse(v);
            var (ps5, ps4) = Firmware.SdkFor(v);
            return new[] { major.ToString(), minor.ToString(), Firmware.Name(v), $"0x{ps5:x8}", $"0x{ps4:x8}", Firmware.ParamVersion(v) };
        }).ToArray();
        Assert.Equal(expected, actual);
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
    public void SdkFor_UnknownMajor() => Assert.Throws<ArgumentException>(() => Firmware.SdkFor("11"));
}
