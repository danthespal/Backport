using System.Text.Json.Nodes;
using Ps5Backport.Core;

namespace Ps5Backport.Tests;

/// <summary>Synthetic rule validation and preflight regressions; no private binary fixtures required.</summary>
public sealed class PreflightTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ps5backport-preflight-" + Guid.NewGuid().ToString("N"));
    string DataDir => Path.Combine(_root, "data");

    public PreflightTests()
    {
        Directory.CreateDirectory(DataDir);
        Write("fixes", """
            {"sources":{"T":"Test evidence"},"umtx_op":"umtx",
             "fixes":{"test-nid":{"kind":"stub","stub":"ret0","sources":["T","free text"]}}}
            """);
        Write("stubs", """{"stubs":{"ret0":{"hex":"31c0c3"}}}""");
        Write("candidates", """{"candidates":{"candidate":{"value":"0x1"}}}""");
        Write("left_unpatched", """{"left_unpatched":{}}""");
        Write("introduced", """{"introduced":{"test-nid":{"firmware":"4.00","source":"test"}}}""");
        Write("known_builds", """{"builds":[]}""");
        Write("sony_builds", """{"builds":{"J00000001":"4.00"}}""");
    }

    void Write(string name, string json) => File.WriteAllText(Path.Combine(DataDir, name + ".json"), json);

    void Edit(string name, Action<JsonObject> edit)
    {
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(DataDir, name + ".json")))!.AsObject();
        edit(root);
        Write(name, root.ToJsonString());
    }

    [Fact]
    public void ValidRules_PreserveEvidenceAndOptionOverrides()
    {
        Edit("fixes", root => root["options"] = JsonNode.Parse("""
            {"off":{"fixes":{"test-nid":{"kind":"none","sources":[]}},"fakelib_variant":"test_variant"}}
            """));
        var knowledge = new Knowledge(DataDir);
        Assert.Equal(new[] { "Test evidence", "free text" }, knowledge.Fixes["test-nid"].Sources);
        Assert.Empty(knowledge.FixTable(["off"]));
        Assert.Single(knowledge.Fixes);
    }

    [Theory]
    [InlineData("kind", "typo")]
    [InlineData("kind", "none")]
    [InlineData("when", "sometimes")]
    [InlineData("below", "4.100")]
    [InlineData("stub", "missing")]
    [InlineData("stub", "ret:nope")]
    [InlineData("stub", "ret:9223372036854775808")]
    [InlineData("kind", "adapter")]
    [InlineData("kind", "swap")]
    [InlineData("kind", "redirect")]
    public void InvalidFix_FailsAtLoadWithFileAndRuleContext(string field, string value)
    {
        Edit("fixes", root => root["fixes"]!["test-nid"]![field] = value);
        var error = Assert.Throws<InvalidDataException>(() => new Knowledge(DataDir));
        Assert.Contains("fixes.json", error.Message);
        Assert.Contains("test-nid", error.Message);
    }

    [Theory]
    [InlineData("fixes", "{}")]
    [InlineData("stubs", "null")]
    [InlineData("candidates", "[]")]
    [InlineData("left_unpatched", "{\"left_unpatched\":{\"nid\":[null]}}")]
    [InlineData("introduced", "{\"introduced\":{\"nid\":{\"firmware\":null}}}")]
    [InlineData("known_builds", "{\"builds\":{}}")]
    [InlineData("sony_builds", "{\"builds\":{\"J1\":null}}")]
    [InlineData("fixes", "{")]
    [InlineData("stubs", "{\"stubs\":{},\"stubs\":{}}")]
    [InlineData("stubs", "{\"stubs\":{\"ret0\":{\"hex\":\"c3\",\"hex\":\"90\"}}}")]
    public void MalformedDocument_ReportsResponsibleFile(string file, string json)
    {
        Write(file, json);
        var error = Assert.Throws<InvalidDataException>(() => new Knowledge(DataDir));
        Assert.Contains(file + ".json", error.Message);
    }

    [Theory]
    [InlineData("01", "")]
    [InlineData("", "")]
    [InlineData("0102", "03")]
    [InlineData("xyz", "000")]
    public void InvalidPattern_IsRejectedBeforeUse(string find, string replace)
    {
        Edit("fixes", root => root["patterns"] = new JsonArray(new JsonObject
        {
            ["name"] = "bad-pattern", ["find"] = find, ["replace"] = replace,
        }));
        var error = Assert.Throws<InvalidDataException>(() => new Knowledge(DataDir));
        Assert.Contains("fixes.json", error.Message);
    }

    [Theory]
    [InlineData("../outside.bin")]
    [InlineData("folder/../../outside.bin")]
    [InlineData("..\\outside.bin")]
    [InlineData("/outside.bin")]
    [InlineData("\\outside.bin")]
    [InlineData("C:\\outside.bin")]
    [InlineData("C:outside.bin")]
    [InlineData("\\\\server\\share\\outside.bin")]
    [InlineData("file.bin:stream")]
    [InlineData("folder./outside.bin")]
    [InlineData("folder /outside.bin")]
    [InlineData("folder//outside.bin")]
    [InlineData("CON.txt")]
    [InlineData("LPT1.bin")]
    [InlineData("")]
    public void UnsafePatternPath_IsRejectedAndLeavesFilesUntouched(string relative)
    {
        string outside = Path.Combine(_root, "outside.bin");
        File.WriteAllBytes(outside, [1, 2]);
        Edit("fixes", root => root["patterns"] = new JsonArray(new JsonObject
        {
            ["name"] = "unsafe", ["find"] = "0102", ["replace"] = "0304",
            ["files"] = new JsonArray(JsonValue.Create(relative)),
        }));
        var error = Assert.Throws<InvalidDataException>(() => new Knowledge(DataDir));
        Assert.Contains("unsafe", error.Message);
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(outside));
        Assert.Throws<ArgumentException>(() => Paths.ResolveRelativeFile(_root, relative));
    }

    [Theory]
    [InlineData("value", "1234")]
    [InlineData("value", "0x100000000")]
    [InlineData("value", "0x")]
    public void InvalidCandidateValue_IsRejected(string field, string value)
    {
        Edit("candidates", root => root["candidates"]!["candidate"]![field] = value);
        Assert.Contains("candidates.json", Assert.Throws<InvalidDataException>(() => new Knowledge(DataDir)).Message);
    }

    [Fact]
    public void EmptyStubAndUnsafeVariant_AreRejected()
    {
        Edit("stubs", root => root["stubs"]!["ret0"]!["hex"] = "");
        Assert.Contains("stubs.json", Assert.Throws<InvalidDataException>(() => new Knowledge(DataDir)).Message);
        Edit("stubs", root => root["stubs"]!["ret0"]!["hex"] = "c3");
        Edit("fixes", root => root["options"] = JsonNode.Parse("""
            {"bad":{"fixes":{},"fakelib_variant":"../escape"}}
            """));
        Assert.Contains("option 'bad'", Assert.Throws<InvalidDataException>(() => new Knowledge(DataDir)).Message);
    }

    [Theory]
    [InlineData("missing-source", false)]
    [InlineData("missing-source", true)]
    [InlineData("missing-extra", false)]
    [InlineData("missing-extra", true)]
    [InlineData("invalid-param", false)]
    [InlineData("invalid-param", true)]
    [InlineData("invalid-reference", false)]
    [InlineData("invalid-reference", true)]
    [InlineData("unavailable-firmware", false)]
    [InlineData("unavailable-firmware", true)]
    [InlineData("missing-debug-variant", false)]
    [InlineData("missing-debug-variant", true)]
    [InlineData("invalid-store", false)]
    [InlineData("invalid-store", true)]
    public void FailedPreflight_CreatesNothingAndPreservesExistingOutput(string problem, bool existingOutput)
    {
        string source = Path.Combine(_root, "game"), output = Path.Combine(_root, "output"), reference = Path.Combine(_root, "db");
        Directory.CreateDirectory(Path.Combine(source, "sce_sys"));
        Directory.CreateDirectory(reference);
        string param = """{"titleId":"TEST","requiredSystemSoftwareVersion":"0x0800000000000000"}""";
        File.WriteAllText(Path.Combine(source, "sce_sys", "param.json"), param);
        FirmwareDb.Save(new FirmwareExports("4", "test", false, new()), Path.Combine(reference, "4.00.json"));
        string sentinel = Path.Combine(output, "sce_sys", "param.json");
        if (existingOutput)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
            File.WriteAllText(sentinel, "keep-me");
        }
        var options = new BackportOptions { Firmware = "4", ReferenceDir = reference, PatchParam = true };
        switch (problem)
        {
            case "missing-source": source = Path.Combine(_root, "missing"); break;
            case "missing-extra": options = options with { ExtraFakelib = Path.Combine(_root, "missing") }; break;
            case "invalid-param": File.WriteAllText(Path.Combine(source, "sce_sys", "param.json"), "{"); break;
            case "invalid-reference": File.WriteAllText(Path.Combine(reference, "4.00.json"), "{"); break;
            case "unavailable-firmware": options = options with { Firmware = "3" }; break;
            case "missing-debug-variant": options = options with { AmprDebugLogging = true }; break;
            case "invalid-store":
                string store = Path.Combine(_root, "store");
                Directory.CreateDirectory(Path.Combine(store, "4"));
                File.WriteAllBytes(Path.Combine(store, "4", "test.sprx"), [0]);
                File.WriteAllText(Path.Combine(store, "4", "manifest.json"), "null");
                options = options with { FakelibStore = store };
                break;
        }
        Assert.NotNull(Record.Exception(() => Pipeline.Backport(source, output, options, new Knowledge(DataDir), cancel: TestContext.Current.CancellationToken)));
        if (existingOutput)
        {
            Assert.Equal("keep-me", File.ReadAllText(sentinel));
            Assert.Single(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories));
        }
        else
            Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void MixedSeparators_ComposePatternsAndPreserveSource()
    {
        string source = Path.Combine(_root, "game"), output = Path.Combine(_root, "output"), reference = Path.Combine(_root, "db");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        Directory.CreateDirectory(reference);
        FirmwareDb.Save(new FirmwareExports("4", "test", false, new()), Path.Combine(reference, "4.00.json"));
        File.WriteAllBytes(Path.Combine(source, "nested", "data.bin"), [1, 2, 5, 6]);
        Edit("fixes", root => root["patterns"] = JsonNode.Parse("""
            [{"name":"first","find":"0102","replace":"0304","files":["nested/data.bin"]},
             {"name":"second","find":"0506","replace":"0708","files":["nested\\data.bin"]}]
            """));
        Pipeline.Backport(source, output, new BackportOptions { Firmware = "4", ReferenceDir = reference },
            new Knowledge(DataDir), cancel: TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 3, 4, 7, 8 }, File.ReadAllBytes(Path.Combine(output, "nested", "data.bin")));
        Assert.Equal(new byte[] { 1, 2, 5, 6 }, File.ReadAllBytes(Path.Combine(source, "nested", "data.bin")));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
