using System.Text.Json;
using System.Text.Json.Nodes;
using Ps5Backport.Core;

namespace Ps5Backport.Tests;

/// <summary>Phase 2: shared knowledge (data/*.json), firmware DB and fakelib store.</summary>
public class KnowledgeTests
{
    static Knowledge K => new(Repo.Need("data"));

    static JsonElement Raw(string name) => JsonDocument.Parse(File.ReadAllBytes(Repo.Need("data", name + ".json"))).RootElement;

    static string Str(JsonElement e, string name, string fallback = "") => e.TryGetProperty(name, out var v) ? v.GetString()! : fallback;

    static bool Bool(JsonElement e, string name, bool fallback) => e.TryGetProperty(name, out var v) ? v.GetBoolean() : fallback;

    /// <summary>A fix as loaded equals its data/fixes.json entry (sources resolved through the "sources" table).</summary>
    static void SameFix(JsonElement raw, Fix f, IReadOnlyDictionary<string, string> sources)
    {
        Assert.Equal([Str(raw, "kind"), Str(raw, "name"), Str(raw, "area"), Str(raw, "stub"), Str(raw, "new_nid"), Str(raw, "export_nid"),
                      Str(raw, "when", Fix.WhenMissing), Str(raw, "note"), Str(raw, "rationale"), Str(raw, "below")],
                     new[] { f.Kind, f.Name, f.Area, f.Stub, f.NewNid, f.ExportNid, f.When, f.Note, f.Rationale, f.Below });
        Assert.Equal(raw.GetProperty("sources").EnumerateArray().Select(s => sources.GetValueOrDefault(s.GetString()!, s.GetString()!)), f.Sources);
        var insns = raw.TryGetProperty("insns", out var i) ? i.EnumerateArray().Select(x => x.GetString()!.Split(';')[0].Trim()).ToList() : [];
        Assert.Equal(insns, f.Insns.Select(x => x.Hex));
        Assert.Equal((Bool(raw, "post_call", false), Bool(raw, "proven", true)), (f.PostCall, f.Proven));
    }

    [Fact]
    public void Fixes_AreLoadedAsWritten()
    {
        var k = K;
        var entries = Raw("fixes").GetProperty("fixes").EnumerateObject().ToList();
        Assert.Equal(entries.Select(p => p.Name), k.FixList.Select(f => f.Nid));                 // same order
        foreach (var p in entries)
            SameFix(p.Value, k.Fixes[p.Name], k.SourceNames);
    }

    [Fact]
    public void Options_AreLoadedAsWritten()
    {
        var k = K;
        var options = Raw("fixes").GetProperty("options").EnumerateObject().ToList();
        Assert.Equal(options.Select(p => p.Name), k.Options.Keys);
        foreach (var p in options)
        {
            var o = k.Options[p.Name];
            Assert.Equal([Str(p.Value, "name"), Str(p.Value, "description"), Str(p.Value, "warning"), Str(p.Value, "fakelib_variant")],
                         new[] { o.Name, o.Description, o.Warning, o.FakelibVariant });
            var fixes = p.Value.GetProperty("fixes").EnumerateObject().ToList();
            Assert.Equal(fixes.Select(f => f.Name), o.Fixes.Keys);
            foreach (var f in fixes)
                SameFix(f.Value, o.Fixes[f.Name], k.SourceNames);
            var patterns = p.Value.TryGetProperty("patterns", out var ps) ? ps.EnumerateArray().ToList() : [];
            Assert.Equal(patterns.Select(x => $"{Str(x, "name")}|{Str(x, "find")}|{Str(x, "replace")}"),
                         o.Patterns.Select(x => $"{x.Name}|{Convert.ToHexStringLower(x.Find)}|{Convert.ToHexStringLower(x.Replace)}"));
        }
    }

    [Fact]
    public void SonyBuilds_ToFirmware()
    {
        var k = K;
        Assert.Equal("J03828141 = FW 12.70", k.SonyBuild("W:/Build/J03828141/sys/build/x/libSceAgc.prx"));
        Assert.Equal("J02065949 = FW 5.02/5.10", k.SonyBuild("W:/Build/J02065949/x"));      // same libraries
        Assert.Equal("J03950001 = FW 13.60", k.SonyBuild("W:/Build/J03950001/x"));          // evidence
        Assert.Equal("J03900000 = between FW 12.70 and 13.60", k.SonyBuild("W:/Build/J03900000/x"));
        Assert.Equal("J09999999 = newer than FW 13.60", k.SonyBuild("W:/Build/J09999999/x"));
        Assert.Equal("", k.SonyBuild("C:/dev/ps5/ampr_emu/out/libSceAmpr.prx"));
        Assert.Equal("", k.SonyBuild(""));
    }

    [Fact]
    public void DefaultPatterns_AreLoadedAsWritten()
    {
        var raw = Raw("fixes").GetProperty("patterns").EnumerateArray().ToList();
        Assert.Equal(raw.Select(x => $"{Str(x, "name")}|{Str(x, "find")}|{Str(x, "replace")}|{Bool(x, "code", false)}|{Str(x, "note")}"),
                     K.Patterns.Select(p => $"{p.Name}|{Convert.ToHexStringLower(p.Find)}|{Convert.ToHexStringLower(p.Replace)}|{p.Code}|{p.Note}"));
    }

    [Fact]
    public void FixTable_OptionOverridesDefault_AndBelowLimitsTargets()
    {
        var k = K;
        var table = k.FixTable(["force_pro"]);
        Assert.Equal(Fix.KindPltStub, table["NH6xARDOVv8"].Kind);           // sceKernelGetOperationMode -> mode 3
        Assert.Equal(Fix.KindStub, k.Fixes["NH6xARDOVv8"].Kind);            // default untouched
        Assert.Throws<ArgumentException>(() => k.FixTable(["no_such_option"]));
        var mount3 = k.Fixes["ZP4e7rlzOUk"];                                 // systemBlocks -1 -> 0: SDK < 5.00 only
        Assert.True(mount3.Due("4") && mount3.Due("4.50"));
        Assert.False(mount3.Due("5") || mount3.Due("5.02") || mount3.Due("10.01"));
        Assert.True(k.Fixes["sDCBrmc61XU"].Due("12.00"));                   // no limit
        var mfsr = k.Fixes["3WVD91e12ZQ"];                                   // scePsmlMfsrInit: skipped below 9.00
        Assert.True(mfsr.Kind == Fix.KindPltStub && mfsr.When == Fix.WhenAlways && mfsr.Due("4") && !mfsr.Due("9.00"));
        Assert.False(table.ContainsKey("3WVD91e12ZQ"));                      // force_pro lifts it (kind "none")
    }

    [Fact]
    public void OtherTables_AreLoadedAsWritten()
    {
        var k = K;
        var fixes = Raw("fixes");
        Assert.Equal(fixes.GetProperty("umtx_op").GetString(), k.UmtxOp);
        Assert.Equal(fixes.GetProperty("sources").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!), k.SourceNames);
        Assert.Equal(Raw("stubs").GetProperty("stubs").EnumerateObject().ToDictionary(p => p.Name, p => Str(p.Value, "hex").ToLowerInvariant()),
                     k.Stubs.ToDictionary(kv => kv.Key, kv => Convert.ToHexStringLower(kv.Value.Bytes)));
        Assert.Equal(["b82d000280c3", Convert.ToHexStringLower(k.Stubs["ret0"].Bytes), "b8ffffffffc3", "b810000000c3"],
                     new[] { "ret:0x8002002d", "ret:0", "ret:-1", "ret:16" }.Select(s => Convert.ToHexStringLower(k.StubBytes(s))));
        var candidates = Raw("candidates").GetProperty("candidates").EnumerateObject().ToList();
        foreach (var p in candidates)
            Assert.Equal((Str(p.Value, "name"), Convert.ToUInt32(Str(p.Value, "value"), 16), Str(p.Value, "source")),
                         (k.Candidates[p.Name].Name, k.Candidates[p.Name].Value, k.Candidates[p.Name].Source));
        Assert.Equal(candidates.Count, k.Candidates.Count);
        Assert.Equal(Raw("left_unpatched").GetProperty("left_unpatched").EnumerateObject().ToDictionary(
                         p => p.Name, p => string.Join("|", p.Value.EnumerateArray().Select(s => k.SourceNames.GetValueOrDefault(s.GetString()!, s.GetString()!)))),
                     k.LeftUnpatched.ToDictionary(kv => kv.Key, kv => string.Join("|", kv.Value)));
        Assert.Equal(Raw("introduced").GetProperty("introduced").EnumerateObject().ToDictionary(p => p.Name, p => Str(p.Value, "firmware")),
                     k.Introduced.ToDictionary(kv => kv.Key, kv => kv.Value.Firmware));
        Assert.Equal(Raw("known_builds").GetProperty("builds").GetArrayLength(), k.KnownBuilds.Count);
    }

    /// <summary>PyJson writes exactly what Python's json.dumps(obj, indent=n) wrote (recorded), so data files don't churn.</summary>
    [Fact]
    public void PyJson_MatchesGolden()
    {
        const string sample = """{"a": [], "b": {}, "c": [1, true, false, null, "x"], "d": {"q\"uote": "back\\slash \n tab\t é € \u0001 \u007f", "n": -5}, "e": 18446744073709551615}""";
        var node = JsonNode.Parse(sample);
        Golden.Match("pyjson.txt", PyJson.Serialize(node, 1) + "\n---\n" + PyJson.Serialize(node, 2) + "\n");
    }

    /// <summary>Fixes not yet console-tested are flagged as derived (the report lists them).</summary>
    [Fact]
    public void DerivedFixes_AreFlagged()
    {
        var k = K;
        Assert.Equal(["libSceVideoOutVrrStatus gWT7X8H0bYs", "sceAudioOut2GetSpeakerInfo", "sceHmd2ReprojectionSetAllowPositionalReprojection",
                      "sceHttp2SetRequestNoContentLength", "sceKernelSyncOnAddressWait", "scePsmlMfsrInit", "sceSaveDataMount3",
                      "sceSaveDataPrepare", "sceUserServiceGetGamePresets"],
                     k.FixList.Where(f => !f.Proven).Select(f => f.Name).Order(StringComparer.Ordinal));
        Assert.True(k.Fixes["NH6xARDOVv8"].Proven);   // GetOperationMode stub: proven by both games
    }

    /// <summary>The AnyPS5 candidates' NIDs are the hashes of their names.</summary>
    [Fact]
    public void Candidates_NidsMatchNames()
    {
        var k = K;
        Assert.All(k.Candidates, kv => Assert.Equal(kv.Key, Nid.Compute(kv.Value.Name)));
        Assert.Equal(0x8002002Du, k.Candidates["UOjiprYwVNw"].Value);   // sceTextToSpeech2Initialize: GT7 skips narration
    }
}

public class FirmwareDbTests
{
    [Fact]
    public void LoadSave_RoundTripsByteIdentical()
    {
        string path = Repo.Need("firmware_db", "3.20.json");
        var db = FirmwareDb.Load(path);
        Assert.Equal("3.20", db.Firmware);
        Assert.True(db.DataSymbols);                         // from a real 3.20 dump (tools/import_firmware_dumps.py)
        Assert.True(db.Nids.Count > 31275);
        string tmp = Path.GetTempFileName();
        try
        {
            FirmwareDb.Save(db, tmp);
            Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(tmp));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void ImportGenstub_MatchesGolden_AndIsASubsetOfThe320Dump()
    {
        string stubs = Repo.Need(".original", "reference", "PS5-3.20_Libs");
        string cs = Path.GetTempFileName();
        try
        {
            var db = FirmwareDb.ImportGenstub(stubs, "3.20", "genstub");
            Assert.Equal(31275, db.Nids.Count);
            FirmwareDb.Save(db, cs);
            Golden.Match("genstub-320.sha256", Golden.Sha(File.ReadAllBytes(cs)) + "\n");
            // every NID of a game-facing module is in the real 3.20 dump; only mscorlib.dll's (a managed assembly, skipped) are not
            var dump = FirmwareDb.Load(Repo.Need("firmware_db", "3.20.json"));
            Assert.Equal(["mscorlib.dll"], db.Modules.Where(m => !m.Value.IsSubsetOf(dump.Nids)).Select(m => m.Key));
        }
        finally { File.Delete(cs); }
    }

    [Fact]
    public void ImportDump_MatchesGolden_AndDiffs()
    {
        string folder = Repo.Need(".original", "backports", "PPSA01317", "4xx+", "USA", "fakelib");
        string cs = Path.GetTempFileName();
        try
        {
            var db = FirmwareDb.ImportDump(folder, "4.00", "test");
            Assert.True(db.DataSymbols);
            Assert.True(db.Modules["libSceAgc"].Count > 200);
            FirmwareDb.Save(db, cs);
            Golden.Match("import-dump-gt7-fakelib.sha256", Golden.Sha(File.ReadAllBytes(cs)) + "\n");
            var diff = FirmwareDb.Diff(new FirmwareExports("3.20", "empty", false, []), db);
            Assert.Equal(db.Modules.Keys.Order(), diff.Keys.Order());
        }
        finally { File.Delete(cs); }
    }

    /// <summary>The dumped firmwares answer what exists where.</summary>
    [Fact]
    public void DumpedFirmwares_AnswerWhatExistsWhere()
    {
        Repo.Need("firmware_db", "4.00.json");
        var dbs = FirmwareDb.LoadAll(Repo.Need("firmware_db"));
        var k = new Knowledge(Repo.Need("data"));
        var fw400 = dbs.First(d => d.Firmware == "4.00");
        Assert.Contains("UOjiprYwVNw", fw400.Nids);                       // sceTextToSpeech2Initialize exists on 4.00
        Assert.Contains("D-CzAxQL0XI", fw400.Nids);                       // UserServicePlatformPrivacyWs1, called by GT7 at startup
        Assert.DoesNotContain("B2n8aDorSH4", fw400.Nids);                 // sceKernelSyncOnAddressWait32 does not
        Assert.Equal("9.00", FirmwareDb.Introduced("B2n8aDorSH4", dbs, k)!.Value.Firmware);
        Assert.Equal("7.01", FirmwareDb.Introduced("tU5e3f9gSiU", dbs, k)!.Value.Firmware);   // sceKernelIsTrinityMode
        Assert.Equal(("4.03", false), (FirmwareDb.Select(Repo.Need("firmware_db"), "4.10").Db!.Firmware, FirmwareDb.Select(Repo.Need("firmware_db"), "4.10").Exact));
    }

    [Fact]
    public void Select_ExactAndLowerBound_DiffAndIntroduced()
    {
        string db320 = Repo.Need("firmware_db", "3.20.json");
        string dir = Path.Combine(Path.GetTempPath(), "ps5backport-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(db320, Path.Combine(dir, "3.20.json"));
            var db4 = FirmwareDb.Load(db320);
            db4.Firmware = "4.00";                                   // pretend we had an exact 4.00 list
            db4.Modules["libkernel"].Add("tU5e3f9gSiU");
            FirmwareDb.Save(db4, Path.Combine(dir, "4.00.json"));

            Assert.Equal(("3.20", true), (FirmwareDb.Select(dir, "3.20").Db!.Firmware, FirmwareDb.Select(dir, "3.20").Exact));
            Assert.Equal(("3.20", false), (FirmwareDb.Select(dir, "3.50").Db!.Firmware, FirmwareDb.Select(dir, "3.50").Exact));
            Assert.Equal(("4.00", true), (FirmwareDb.Select(dir, "4").Db!.Firmware, FirmwareDb.Select(dir, "4").Exact));
            Assert.Equal(("4.00", false), (FirmwareDb.Select(dir, "4.03").Db!.Firmware, FirmwareDb.Select(dir, "4.03").Exact));
            Assert.Null(FirmwareDb.Select(dir, "2.50").Db);

            var all = FirmwareDb.LoadAll(dir);
            Assert.Equal(["3.20", "4.00"], all.Select(d => d.Firmware));
            var added = FirmwareDb.Diff(all[0], all[1]);
            Assert.Equal(["libkernel"], added.Keys);
            Assert.Equal(["tU5e3f9gSiU"], added["libkernel"]);

            var k = new Knowledge(Repo.Need("data"));
            Assert.Equal(("4.00", "firmware_db 4.00"), FirmwareDb.Introduced("tU5e3f9gSiU", all, k));
            Assert.Equal("7.01", FirmwareDb.Introduced("tU5e3f9gSiU", [], k)!.Value.Firmware);   // known fact (data/introduced.json)
            Assert.Null(FirmwareDb.Introduced("XXXXXXXXXXX", all, k));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class FakelibStoreTests
{
    static string Temp() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ps5backport-store-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void Describe_MatchesGolden()
    {
        string folder = Repo.Need("fakelibs", "4.03");
        var files = Directory.EnumerateFiles(folder, "*.sprx").Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(files);
        Golden.Match("fakelibs-403-describe.txt", string.Concat(files.Select(f => $"{Path.GetFileName(f)}: {PyJson.Serialize(FakelibStore.Describe(f), 2)}\n")));
        Assert.Equal("Aug 24 2026", FakelibStore.Describe(Path.Combine(folder, "libSceAmpr.sprx"))["build_date"]!.GetValue<string>());
        Assert.Equal("ok", new Knowledge(Repo.Need("data")).Build("libSceAmpr", "Aug 24 2026")!.State);
    }

    [Fact]
    public void Add_WritesTheGoldenManifest_AndVerifies()
    {
        string folder = Repo.Need("fakelibs", "4.03");
        var files = Directory.EnumerateFiles(folder, "*.sprx").Order(StringComparer.Ordinal).ToArray();
        string cs = Temp();
        try
        {
            var (dest, added) = FakelibStore.Add(cs, "4.03", files, "test source");
            Assert.Equal(Path.Combine(cs, "4.03"), dest);
            Assert.Equal(files.Length, added.Count);
            Golden.Match("fakelibs-403-manifest.json", File.ReadAllText(Path.Combine(cs, "4.03", FakelibStore.ManifestName)));
            var entry = FakelibStore.LoadManifest(dest)["libSceAgc.sprx"]!.AsObject();
            Assert.Equal(("libSceAgc", "0x04000031", "test source", 64),
                         (entry["module"]!.GetValue<string>(), entry["ps5_sdk"]!.GetValue<string>(), entry["source"]!.GetValue<string>(), entry["sha256"]!.GetValue<string>().Length));

            Assert.Empty(FakelibStore.Verify(dest));
            Assert.Equal(dest, FakelibStore.Find(cs, "4.03"));
            Assert.Null(FakelibStore.Find(cs, "4"));              // no major folder, no 4.00
            Assert.Equal(["4.03"], FakelibStore.List(cs).Keys);

            // tamper: changed, missing and unlisted files
            File.AppendAllText(Path.Combine(dest, "libScePlayGo.sprx"), "x");
            File.Delete(Path.Combine(dest, "libScePsml.sprx"));
            File.WriteAllText(Path.Combine(dest, "extra.prx"), "x");
            Assert.Equal([("libScePlayGo.sprx", "checksum differs from manifest"), ("libScePsml.sprx", "missing"), ("extra.prx", "not in manifest")],
                         FakelibStore.Verify(dest));
        }
        finally { Directory.Delete(cs, true); }
    }

    [Fact]
    public void Find_PrefersExactThenMajor()
    {
        string store = Temp();
        try
        {
            Directory.CreateDirectory(Path.Combine(store, "4"));
            File.WriteAllText(Path.Combine(store, "4", "a.sprx"), "x");
            Assert.Equal(Path.Combine(store, "4"), FakelibStore.Find(store, "4.03"));
            Directory.CreateDirectory(Path.Combine(store, "4.03"));
            Assert.Equal(Path.Combine(store, "4"), FakelibStore.Find(store, "4.03"));   // empty exact folder is skipped
            File.WriteAllText(Path.Combine(store, "4.03", "b.prx"), "x");
            Assert.Equal(Path.Combine(store, "4.03"), FakelibStore.Find(store, "4.03"));
            Assert.Null(FakelibStore.Find(store, "5"));
        }
        finally { Directory.Delete(store, true); }
    }

    [Fact]
    public void KnownBrokenBuild_Warns()
    {
        string folder = Temp();
        try
        {
            File.WriteAllText(Path.Combine(folder, FakelibStore.ManifestName),
                """{"libSceAmpr.sprx": {"module": "libSceAmpr", "build_date": "Sep  6 2026"}, "libScePlayGo.sprx": {"module": "libScePlayGo"}}""");
            var warning = Assert.Single(FakelibStore.BuildWarnings(folder, new Knowledge(Repo.Need("data"))));
            Assert.Contains("known-broken", warning);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void SdkWrite_ChangesAndReportsIt()
    {
        string path = Repo.Need("fakelibs", "4", "libSceAgc.sprx");
        var elf = new SceElf(Self.Unwrap(File.ReadAllBytes(path)));
        Assert.Equal((0x04000031u, 0x09040001u), Sdk.Read(elf));
        Assert.False(Sdk.Write(elf, 0x04000031, 0x09040001));
        Assert.True(Sdk.Write(elf, 0x04030031, 0x09040001));
        Assert.Equal((0x04030031u, 0x09040001u), Sdk.Read(elf));
    }
}
