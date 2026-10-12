using System.Text;
using System.Text.Json.Nodes;
using Ps5Backport.Core;

namespace Ps5Backport.Tests;

public sealed class CrashLogTests
{
    const string Header = "# A user thread receives a fatal signal\n# signal: 11 (SIGSEGV)\n";
    static string Map(string path = "/app0/eboot.bin", string start = "400000", string end = "500000") => $"# dynamic libraries:\n# {path}\n#  xotext: {start}:{end} nsegs: 4\n";
    static JsonObject Parse(string body, JsonObject? report = null) => CrashLog.Analyze(Encoding.UTF8.GetBytes(Header + body), report);
    static JsonObject First(JsonObject result) => result["events"]![0]!["addresses"]![0]!.AsObject();

    [Theory]
    [InlineData("400000", "unique logged text range", "0x0")]
    [InlineData("400010", "unique logged text range", "0x10")]
    [InlineData("500000", "no logged text range", null)]
    [InlineData("3fffff", "no logged text range", null)]
    [InlineData("0", "zero address; no module attribution", null)]
    public void Range_IsHalfOpen_AndZeroIsUnattributed(string address, string resolution, string? offset)
    {
        var item = First(Parse("# rip: " + address + "  eflags: 00010282\n" + Map()));
        Assert.Equal(resolution, item["resolution"]!.GetValue<string>());
        Assert.Equal(offset, item["textRelativeOffset"]?.GetValue<string>());
    }

    [Theory]
    [InlineData("10000000000000000")]
    [InlineData("-1")]
    [InlineData("0xZZ")]
    [InlineData("400010junk")]
    [InlineData("")]
    public void InvalidAddresses_WarnWithoutAttribution(string value)
    {
        var result = Parse("# rip: " + value + "\n" + Map());
        Assert.Empty(result["events"]![0]!["addresses"]!.AsArray());
        Assert.Single(result["warnings"]!.AsArray());
    }

    [Fact]
    public void UInt64Maximum_ParsesWithoutOverflow() => Assert.Equal(ulong.MaxValue, First(Parse("# rip: ffffffffffffffff"))["value"]!.GetValue<ulong>());

    [Theory]
    [InlineData("500000", "400000")]
    [InlineData("400000", "400000")]
    [InlineData("ZZ", "500000")]
    public void InvalidRange_WarnsAndStaysUnmapped(string start, string end)
    {
        var result = Parse("# rip: 400010\n" + Map(start: start, end: end));
        Assert.Single(result["warnings"]!.AsArray());
        Assert.Equal("no logged text range", First(result)["resolution"]!.GetValue<string>());
    }

    [Fact]
    public void Overlap_RemainsAmbiguous()
    {
        var item = First(Parse("# rip: 400010\n" + Map() + Map("/app0/other.prx")));
        Assert.Equal("ambiguous overlapping text ranges", item["resolution"]!.GetValue<string>());
        Assert.Null(item["runtimeModule"]);
    }

    [Fact]
    public void Maps_DoNotLeakIntoNextEvent()
    {
        var result = Parse("# rip: 400010\n" + Map() + Header + "# rip: 400010\n");
        Assert.Equal(2, result["events"]!.AsArray().Count);
        Assert.Equal("no logged text range", result["events"]![1]!["addresses"]![0]!["resolution"]!.GetValue<string>());
    }

    [Fact]
    public void Interleaving_NumberedLines_AndNuls_AreParsingOnly()
    {
        byte[] raw = Encoding.UTF8.GetBytes("10: # A user thread receives a fatal signal\n11: # fault address: 40\00010\n12: # dynamic libraries:\n13: # /app0/eboot.bin\n<118>other process\n14: #  xotext: 400000:500000 nsegs: 4\n15: #  fingerprint: opaque\n");
        byte[] before = raw.ToArray();
        var result = CrashLog.Analyze(raw);
        Assert.Equal(1, result["nulBytesIgnoredForParsing"]!.GetValue<int>());
        Assert.Equal("fault address", First(result)["kind"]!.GetValue<string>());
        Assert.Equal(2, First(result)["line"]!.GetValue<int>());
        Assert.Equal("0x10", First(result)["textRelativeOffset"]!.GetValue<string>());
        Assert.Equal("opaque", result["events"]![0]!["modules"]![0]!["fingerprint"]!.GetValue<string>());
        Assert.Equal(before, raw);
    }

    [Fact]
    public void Truncation_AndUnrelatedHex_DoNotInventMapsOrFrames()
    {
        var result = Parse("# rip: 400010\n# backtrace:\nrandom 1234\n# dynamic libraries:\n# /app0/eboot.bin");
        Assert.Single(result["events"]![0]!["addresses"]!.AsArray());
        Assert.Equal("no logged text range", First(result)["resolution"]!.GetValue<string>());
        Assert.Empty(CrashLog.Analyze(Encoding.UTF8.GetBytes("# rip: 400010\n"))["events"]!.AsArray());
    }

    [Fact]
    public void Summary_KeepsPidAndAddresses_WithoutInventingMap()
    {
        var result = CrashLog.Analyze(Encoding.UTF8.GetBytes("[MDBG] game error: PPSA00001 pid=7: failure; fault address: 0x0; RIP: 0x0; backtrace[0]: 0x400010\n"));
        Assert.Equal(7, result["events"]![0]!["pid"]!.GetValue<long>());
        Assert.Equal(3, result["events"]![0]!["addresses"]!.AsArray().Count);
        Assert.Equal("no logged text range", result["events"]![0]!["addresses"]![2]!["resolution"]!.GetValue<string>());
    }

    [Fact]
    public void LegacyHeader_AndSignalOnlyEvents_AreRecognized()
    {
        var legacy = CrashLog.Analyze(Encoding.UTF8.GetBytes("# exception: access violation\n# rip: 400010\n"));
        Assert.Equal("access violation", legacy["events"]![0]!["reason"]!.GetValue<string>());
        var signals = CrashLog.Analyze(Encoding.UTF8.GetBytes("# signal: 11 (SIGSEGV)\n# rip: 400010\n# signal: 11 (SIGSEGV)\n# rip: 500010\n"));
        Assert.Equal(2, signals["events"]!.AsArray().Count);
    }

    [Fact]
    public void InvalidUtf8_IsVisible_AndBytesStayUnchanged()
    {
        byte[] raw = [.. Encoding.UTF8.GetBytes(Header), 0xff];
        byte[] before = raw.ToArray();
        Assert.Equal(1, CrashLog.Analyze(raw)["decodeReplacementCharacters"]!.GetValue<int>());
        Assert.Equal(before, raw);
    }

    [Fact]
    public void Report_UsesExactPathBeforeBasename_AndNeverInfersCause()
    {
        var report = JsonNode.Parse("""{"modules":[{"path":"eboot.bin","stubbed_now":[{"at":"0x10"}]},{"path":"other/eboot.bin"}]}""")!.AsObject();
        string before = report.ToJsonString();
        var result = Parse("# rip: 400010\n" + Map(), report);
        var candidates = First(result)["reportModuleCandidates"]!.AsArray();
        Assert.Single(candidates);
        Assert.Equal("0x10", candidates[0]!["stubbed_now"]![0]!["at"]!.GetValue<string>());
        Assert.StartsWith("unverified", result["reportIdentity"]!.GetValue<string>());
        Assert.StartsWith("unknown", result["cause"]!.GetValue<string>());
        Assert.Contains("ELF", result["coordinateNote"]!.GetValue<string>());
        Assert.Equal(before, report.ToJsonString());
    }

    [Fact]
    public void DuplicateBasenames_RemainCandidates()
    {
        var report = JsonNode.Parse("""{"modules":[{"path":"fakelib/custom.sprx"},{"path":"other/custom.sprx"}]}""")!.AsObject();
        Assert.Equal(2, First(Parse("# rip: 400010\n" + Map("/sandbox/common/custom.sprx"), report))["reportModuleCandidates"]!.AsArray().Count);
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("line")]
    [InlineData("events")]
    [InlineData("addresses")]
    [InlineData("modules")]
    [InlineData("ranges")]
    public void ResourceBounds_RejectExcess(string kind)
    {
        string body = kind switch {
            "bytes" => new string('x', 1024 * 1024 + 1), "line" => new string('x', 4097),
            "events" => string.Concat(Enumerable.Repeat(Header, 64)),
            "addresses" => "# backtrace:\n" + string.Concat(Enumerable.Repeat("# 400010\n", 129)),
            "modules" => "# dynamic libraries:\n" + string.Concat(Enumerable.Repeat("# /app0/a.prx\n", 257)),
            _ => Map() + string.Concat(Enumerable.Repeat("#  xotext: 400000:500000 nsegs: 4\n", 256)) };
        Assert.Throws<InvalidDataException>(() => Parse(body));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"modules\":[{}]}")]
    [InlineData("{\"modules\":[{\"path\":7}]}")]
    [InlineData("{\"modules\":[{\"path\":null}]}")]
    [InlineData("{\"modules\":[{\"path\":\"\"}]}")]
    public void InvalidReport_IsRejected(string json) => Assert.Throws<InvalidDataException>(() => Parse("", JsonNode.Parse(json)!.AsObject()));

    [Fact]
    public void Cli_ReadsOnly_AndSupportsJsonAndText()
    {
        string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ps5bp-crash-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string log = Path.Combine(folder, "crash.log"), report = Path.Combine(folder, "report.json");
            byte[] original = Encoding.UTF8.GetBytes(Header + "# rip: 400010\n" + Map());
            File.WriteAllBytes(log, original); File.WriteAllText(report, "{\"modules\":[{\"path\":\"eboot.bin\"}]}");
            byte[] reportBefore = File.ReadAllBytes(report);
            var output = new StringWriter(); var error = new StringWriter();
            Assert.Equal(0, Cli.Run(["crash-log", log, "--report", report, "--json"], output, error));
            Assert.Equal("0x10", First(JsonNode.Parse(output.ToString())!.AsObject())["textRelativeOffset"]!.GetValue<string>());
            output.GetStringBuilder().Clear();
            Assert.Equal(0, Cli.Run(["crash-log", log, "--report", report], output, error));
            Assert.Contains("unverified report candidate: eboot.bin", output.ToString());
            Assert.Contains("unknown", output.ToString());
            Assert.Equal("", error.ToString());
            Assert.Equal(original, File.ReadAllBytes(log)); Assert.Equal(reportBefore, File.ReadAllBytes(report));
            Assert.Equal(2, Directory.GetFiles(folder).Length);
            File.WriteAllText(report, "[]");
            Assert.Equal(1, Cli.Run(["crash-log", log, "--report", report], new StringWriter(), error));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
