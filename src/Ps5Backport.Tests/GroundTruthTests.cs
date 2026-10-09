using System.Buffers.Binary;
using System.Text.Json;
using Ps5Backport.Core;

namespace Ps5Backport.Tests;

/// <summary>
/// Checks against the testers' backports (GT7 FW 4, CONTROL Resonant FW 4.03, AC Black Flag) and other samples:
/// what we produce must equal what was console-tested, apart from documented differences.
/// </summary>
public static class Samples
{
    public static string Work(params string[] rel) => Repo.Need(["work", .. rel]);
    public static string Backports(params string[] rel) => Repo.Need([".original", "backports", .. rel]);
    public static string RefStubs => Repo.Need(".original", "reference", "PS5-3.20_Libs");
    public static Knowledge K => new(Repo.Need("data"));

    public static string Temp(string tag) =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ps5backport-{tag}-" + Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>(offset, length) of every run of differing bytes.</summary>
    public static List<(long Off, long Len)> DiffRuns(byte[] a, byte[] b)
    {
        var runs = new List<(long, long)>();
        for (long i = 0; i < a.Length;)
        {
            if (a[i] == b[i])
            {
                i++;
                continue;
            }
            long j = i;
            while (j < a.Length && a[j] != b[j])
                j++;
            runs.Add((i, j - i));
            i = j;
        }
        return runs;
    }

    public static GameResult Backport(string game, string output, string firmware, string reference, string? extraFakelib = null,
                                      string? store = null, IReadOnlyList<string>? options = null) =>
        Pipeline.Backport(game, output, new BackportOptions
        {
            Firmware = firmware, ReferenceDir = reference, ExtraFakelib = extraFakelib, FakelibStore = store, Options = options ?? [],
        }, K, cancel: TestContext.Current.CancellationToken);

    /// <summary>Follows a fix's cave chain from the PLT entry: jmp rel32 hops, then the instructions, until jmp [rip+GOT] or ret.</summary>
    public static (List<string> Executed, ulong? Called, ulong? Jumped) Trace(SceElf elf, Import imp, Fix fix)
    {
        ulong va = elf.ReadU64(imp.GotVaddr) - 6;
        var executed = new List<string>();
        ulong? called = null;
        for (int n = 0; n < 64; n++)
        {
            long off = elf.Off(va);
            if (elf.Data[off] == 0xE9)                                                    // jmp rel32 to the next piece
            {
                va = (ulong)((long)va + 5 + BinaryPrimitives.ReadInt32LittleEndian(elf.Data.AsSpan((int)off + 1)));
                continue;
            }
            if (elf.Data[off] == 0xFF && elf.Data[off + 1] == 0x25)                       // final jmp [rip+GOT]
                return (executed, called, (ulong)((long)va + 6 + BinaryPrimitives.ReadInt32LittleEndian(elf.Data.AsSpan((int)off + 2))));
            if (elf.Data[off] == 0xFF && elf.Data[off + 1] == 0x15)                       // call [rip+GOT]
            {
                called = (ulong)((long)va + 6 + BinaryPrimitives.ReadInt32LittleEndian(elf.Data.AsSpan((int)off + 2)));
                executed.Add(Insn.CallGotToken);
                va += 6;
                continue;
            }
            var insn = fix.Insns.First(i => !i.CallGot && elf.Data.AsSpan((int)off, i.Bytes.Length).SequenceEqual(i.Bytes));
            executed.Add(insn.Hex);
            if (insn.Hex == "c3")
                break;
            va += (ulong)insn.Bytes.Length;
        }
        return (executed, called, null);
    }
}

public class Gt7Tests
{
    [Fact]
    public void RightsModule_SignRoundTripIsExact()
    {
        string src = Samples.Backports("PPSA01317", "4xx+", "USA", "sce_sys", "about", "right.sprx");
        byte[] original = File.ReadAllBytes(src);
        Assert.Equal(original, Self.Sign(Self.Unwrap(original)));
    }

    [Fact]
    public void LogiWheel_SignRoundTrip_DiffersOnlyInTheDigest()
    {
        byte[] a = File.ReadAllBytes(Samples.Backports("PPSA01317", "4xx+", "USA", "logiWheel.prx"));
        byte[] b = Self.Sign(Self.Unwrap(a));
        Assert.Equal(a.Length, b.Length);
        int digest = Self.DigestOffset(a);
        Assert.All(Samples.DiffRuns(a, b), r => Assert.True(digest <= r.Off && r.Off + r.Len <= digest + 32));
    }

    /// <summary>These modules differ from the backport only in their SDK fields.</summary>
    [Theory]
    [InlineData("NOT_BACKPORT_elf/logiWheel.elf", "GT7_root_elf/logiWheel.elf")]
    [InlineData("NOT_BACKPORT_elf/sce_module/libScePfs.elf", "GT7_sce_module_elf/libScePfs.elf")]
    [InlineData("NOT_BACKPORT_elf/sce_sys/about/right.elf", "GT7_sce_sys_elf/right.elf")]
    public void Fw4SdkPatch_ReproducesTheBackport(string before, string after)
    {
        var elf = SceElf.Load(Samples.Work(before.Split('/')));
        Assert.Equal(Firmware.SdkFor("10"), Sdk.Read(elf));
        var (ps5, ps4) = Firmware.SdkFor("4");
        Sdk.Write(elf, ps5, ps4);
        Assert.Equal(File.ReadAllBytes(Samples.Work(after.Split('/'))), elf.Data);
    }

    /// <summary>The eboot stubs run the same bodies as the hand-made backport; nothing else differs but stub placement and GT7's savedata patch.</summary>
    [Fact]
    public void Eboot_MatchesTheBackportsBehaviour()
    {
        string[] stubbed = ["5tRaBjtdTzY", "T4ucGB8CsnM", "kP2L8t3j-aM", "LibwuIonIBw", "La9uyZv4Kvw", "NH6xARDOVv8"];
        const long SaveDataPatch = 0x83803E;   // GT7's own sceSaveDataMount3 call-site patch (we fix the argument at the import)
        var k = Samples.K;
        var ours = SceElf.Load(Samples.Work("NOT_BACKPORT_elf", "eboot.elf"));
        var theirs = SceElf.Load(Samples.Work("GT7_root_elf", "eboot.elf"));
        var (ps5, ps4) = Firmware.SdkFor("4");
        Sdk.Write(ours, ps5, ps4);
        var patches = Patching.StubImports(ours, stubbed, k, new Dictionary<string, string> { ["NH6xARDOVv8"] = "opmode_normal" });

        // (La9uyZv4Kvw: GT7 returned 0, the rule now returns the firmware's real mask 0xff)
        foreach (var p in patches.Where(p => p.Nid != "La9uyZv4Kvw"))
        {
            byte[] body = k.StubBytes(p.Stub);
            long target = theirs.Off(theirs.ReadU64(p.GotVaddr));
            Assert.Equal(body, theirs.Data.AsSpan((int)target, body.Length).ToArray());
        }
        var allowed = patches.Select(p => (ours.Off(p.GotVaddr), 8L))
            .Concat(patches.Select(p => (ours.Off(theirs.ReadU64(p.GotVaddr)), (long)k.StubBytes(p.Stub).Length)))
            .Concat(patches.Select(p => (ours.Off(p.StubVaddr), (long)k.StubBytes(p.Stub).Length)))
            .Append((SaveDataPatch, 4L)).ToList();
        foreach (var (off, len) in Samples.DiffRuns(ours.Data, theirs.Data))
            Assert.True(allowed.Any(a => a.Item1 <= off && off + len <= a.Item1 + a.Item2), $"unexpected diff at 0x{off:x}");
    }

    [Fact]
    public void DefineSymbol_MarksTheImportStubbed()
    {
        var elf = SceElf.Load(Samples.Work("NOT_BACKPORT_elf", "eboot.elf"));
        Patching.StubImports(elf, ["5tRaBjtdTzY"], Samples.K, defineSymbol: true);
        var reloaded = new SceElf(elf.Data);
        Assert.True(reloaded.Imports().First(i => i.Nid == "5tRaBjtdTzY").DefinedLocally);
        Assert.DoesNotContain("5tRaBjtdTzY", reloaded.Exports());
    }

    [Fact]
    public void WholeGame_Fw4_AgainstThe320Stubs()
    {
        string game = Samples.Backports("PPSA01317", "default");
        string fakelib = Samples.Backports("PPSA01317", "4xx+", "USA", "fakelib");
        string output = Samples.Temp("gt7");
        try
        {
            // no --stub-nid: GT7's eboot stubs and libc fixes come from data/fixes.json
            var result = Samples.Backport(game, output, "4", Samples.RefStubs, extraFakelib: fakelib);
            var mods = result.Modules.ToDictionary(m => m.Path);
            Assert.DoesNotContain(result.Modules, m => m.Error.Length > 0);
            Assert.Equal("PPSA01317", result.TitleId);
            Assert.NotEmpty(result.ParamWarnings);
            var eboot = mods["eboot.bin"];
            // 6 GT7 stubs + the PS VR2 reprojection setter (uv0Ae+jCeWY, FW 6.50; GT7 itself left it unpatched)
            Assert.Equal(7, eboot.StubbedNow.Count);
            Assert.Equal(29, eboot.Counts.First(c => c.Key == "unknown").Value);
            Assert.All(eboot.StubbedNow, s => Assert.True(s.KnownFix));
            // GT7's call-site savedata patch is replaced by the generic sceSaveDataMount3 argument fix
            Assert.Contains(eboot.Fixed, f => f.Nid == "ZP4e7rlzOUk");
            // libc: all six GT7 fixes, plus four stubs learned from CONTROL; only sceKernelDebugWriteCppExceptionInfo remains unknown
            var libc = mods["sce_module/libc.prx"];
            Assert.Equal(["4h6F1LLbTiw", "B2n8aDorSH4", "Hc4CaR6JBL0", "PZQhiiLXRFs", "q2y-wDIVWZA", "qBS714-Jr3g"], libc.Fixed.Select(f => f.Nid).Order(StringComparer.Ordinal));
            Assert.Equal(["OQ-dzhlnM28", "XgVs4jRehcY", "dFym5b41JLU", "tB59hFLH3SA"], libc.StubbedNow.Select(s => s.Nid).Order(StringComparer.Ordinal));
            Assert.Equal(["cfwBSQyr5Ys"], libc.Unknown.Select(u => u.Nid));
            // modules that only need the SDK patch match the hand-made GT7 backport exactly
            foreach (string rel in new[] { "libSieRdNNabla.prx", "logiWheel.prx", "sce_module/libSceNpCppWebApi.prx", "sce_module/libScePfs.prx", "sce_sys/about/right.sprx" })
                Assert.Equal(Self.Unwrap(File.ReadAllBytes(Samples.Backports(["PPSA01317", "4xx+", "USA", .. rel.Split('/')]))),
                             Self.Unwrap(File.ReadAllBytes(Path.Combine(output, rel))));
            // unchanged extra fakelibs are copied verbatim
            foreach (string file in Directory.EnumerateFiles(fakelib))
                Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(output, "fakelib", Path.GetFileName(file))));
            Assert.True(File.Exists(Path.Combine(output, Pipeline.ReportJson)));
        }
        finally { Directory.Delete(output, true); }
    }

    /// <summary>NID swaps give the same string table as GT7's backported libc; adapters jump through their own GOT slot.</summary>
    [Fact]
    public void LibcFixes_MatchGt7()
    {
        var k = Samples.K;
        var elf = SceElf.Load(Samples.Work("NOT_BACKPORT_elf", "sce_module", "libc.elf"));
        var gt7 = SceElf.Load(Samples.Work("GT7_sce_module_elf", "libc.elf"));
        string[] nids = ["4h6F1LLbTiw", "B2n8aDorSH4", "PZQhiiLXRFs", "q2y-wDIVWZA", "Hc4CaR6JBL0", "qBS714-Jr3g"];
        var got = elf.Imports().Where(i => i.Plt).GroupBy(i => i.Nid).ToDictionary(g => g.Key, g => g.First().GotVaddr);
        var plt = nids.ToDictionary(n => n, n => elf.ReadU64(got[n]) - 6);
        FixApplier.Apply(elf, nids, k);

        long strtab = elf.Off(elf.Dyn[SceElf.DtStrtab]);
        Assert.True(elf.Data.AsSpan((int)strtab, 0x40000).SequenceEqual(gt7.Data.AsSpan((int)strtab, 0x40000)));
        foreach (string n in nids)
        {
            long entry = elf.Off(plt[n]);
            var fix = k.Fixes[n];
            if (fix.Kind == Fix.KindSwap)                                    // rename only; PLT untouched
            {
                Assert.Equal([0xFF, 0x25], elf.Data[(int)entry..(int)(entry + 2)]);
                continue;
            }
            Assert.Equal(0xE9, elf.Data[entry]);                             // PLT entry now jmp rel32
            ulong target = (ulong)((long)plt[n] + 5 + BinaryPrimitives.ReadInt32LittleEndian(elf.Data.AsSpan((int)entry + 1)));
            if (fix.Kind == Fix.KindAdapter)
            {
                long code = elf.Off(target);
                byte[] body = fix.Insns.SelectMany(i => i.Bytes).ToArray();
                Assert.Equal(body, elf.Data[(int)code..(int)(code + body.Length)]);
                Assert.Equal([0xFF, 0x25], elf.Data[(int)(code + body.Length)..(int)(code + body.Length + 2)]);
                ulong jmp = target + (ulong)body.Length;
                Assert.Equal(got[n], (ulong)((long)jmp + 6 + BinaryPrimitives.ReadInt32LittleEndian(elf.Data.AsSpan((int)(code + body.Length + 2)))));
                Assert.Equal(plt[n] + 6, elf.ReadU64(got[n]));               // lazy binding left intact
            }
            else if (fix.Kind == Fix.KindRedirect)
                Assert.Equal(0x113E0ul, target);                             // __cxa_atexit, as in GT7
        }
        var statuses = Imports.Classify(elf, FirmwareDb.LoadGenstubExports(Samples.RefStubs));
        Assert.Equal(["OQ-dzhlnM28", "XgVs4jRehcY", "cfwBSQyr5Ys", "dFym5b41JLU", "tB59hFLH3SA"],
                     statuses.Where(s => s.Status == Imports.Unknown).Select(s => s.Nid).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FakelibAgc_Classification()
    {
        string folder = Samples.Work("GT7_fakelib_elf");
        var reference = FirmwareDb.LoadGenstubExports(Samples.RefStubs);
        // all fakelibs: libSceAmpr re-exports libkernel qSleeVC6lwI
        var provided = Imports.LoadProviderExports(Directory.EnumerateFiles(folder).Where(f => Path.GetFileName(f) != "libSceAgc.elf"));
        var by = Imports.Classify(SceElf.Load(Path.Combine(folder, "libSceAgc.elf")), reference, provided)
                        .GroupBy(s => s.Status).ToDictionary(g => g.Key, g => g.Select(s => s.Nid).ToHashSet());
        Assert.Equal(9, by[Imports.Stubbed].Count);          // 8 GOT stubs + qSleeVC6lwI (symbol-only local definition)
        Assert.Contains("tU5e3f9gSiU", by[Imports.Stubbed]);   // sceKernelIsTrinityMode
        Assert.Contains("qSleeVC6lwI", by[Imports.Stubbed]);
        Assert.Equal(["GGeRJk1XdWc", "dkBx0YqFQ+Y"], by[Imports.Unknown].Order(StringComparer.Ordinal));   // left lazy-bound in GT7
    }

    /// <summary>Argument fixes in eboots are split across &lt;=16-byte int3 caves; following the jumps runs the whole fix.</summary>
    [Fact]
    public void Mount3FixChain_ExecutesTheFix()
    {
        var k = Samples.K;
        var elf = SceElf.Load(Samples.Work("NOT_BACKPORT_elf", "eboot.elf"));
        const string nid = "ZP4e7rlzOUk";   // sceSaveDataMount3
        var imp = elf.Imports().First(i => i.Nid == nid && i.Plt);
        FixApplier.Apply(elf, [nid], k);
        var (executed, _, jumped) = Samples.Trace(elf, imp, k.Fixes[nid]);
        Assert.Equal(imp.GotVaddr, jumped);
        Assert.Equal(k.Fixes[nid].Insns.Select(i => i.Hex), executed);
    }

    /// <summary>GT7 imports sceTextToSpeech2Initialize (missing on 3.20); AnyPS5: GT7 skips narration when it fails.</summary>
    [Fact]
    public void Tts2InitializeCandidate_ReturnsAnError()
    {
        var k = Samples.K;
        const string nid = "UOjiprYwVNw";
        var elf = SceElf.Load(Samples.Work("NOT_BACKPORT_elf", "eboot.elf"));
        var p = Patching.StubImports(elf, [nid], k, new Dictionary<string, string> { [nid] = $"ret:0x{k.Candidates[nid].Value:x}" })[0];
        long stub = elf.Off(elf.ReadU64(p.GotVaddr));
        Assert.Equal(Convert.FromHexString("b82d000280c3"), elf.Data[(int)stub..(int)(stub + 6)]);   // mov eax, 0x8002002D; ret
    }

    /// <summary>With an exact 4.00 list, known fixes still apply to "missing" imports.</summary>
    [Fact]
    public void Backport_WithAnExactList_ReportsTheReferenceAndFixesMissingImports()
    {
        string db = Samples.Temp("exactdb"), output = Samples.Temp("exact");
        try
        {
            var stubs = FirmwareDb.ImportGenstub(Samples.RefStubs, "3.20", "genstub test");
            FirmwareDb.Save(stubs, Path.Combine(db, "3.20.json"));
            stubs.Firmware = "4.00";
            FirmwareDb.Save(stubs, Path.Combine(db, "4.00.json"));
            var result = Samples.Backport(Samples.Backports("PPSA01317", "default"), output, "4", db);
            Assert.Contains("firmware_db 4.00 (exact for FW 4)", result.Reference);
            var eboot = result.Modules.First(m => m.Path == "eboot.bin");
            Assert.Equal(7, eboot.StubbedNow.Count);   // GT7's 6 + the PS VR2 reprojection setter (FW 6.50)
            Assert.DoesNotContain(eboot.Counts, c => c.Key == Imports.Unknown);
            Assert.Contains(eboot.Counts, c => c.Key == Imports.Missing);
        }
        finally
        {
            Directory.Delete(db, true);
            Directory.Delete(output, true);
        }
    }
}

/// <summary>CONTROL Resonant 1.300 FW 4.03 backport: its README documents every change.</summary>
public class ControlTests
{
    static readonly Dictionary<string, string[]> ReadmeStubs = new()
    {
        ["eboot.elf"] = ["NH6xARDOVv8", "GTfJ64qFCPw", "TViD1EZXkNI", "y7JD1gfwyfA", "e6y3WMBYbiM", "eb-gvTYQcoY", "MCJ8SkzsQxY", "tU5e3f9gSiU"],
        ["sce_module/libc.elf"] = ["tB59hFLH3SA", "OQ-dzhlnM28", "qBS714-Jr3g", "dFym5b41JLU", "XgVs4jRehcY", "B2n8aDorSH4", "PZQhiiLXRFs", "q2y-wDIVWZA", "Hc4CaR6JBL0"],
        ["nakama-sdk.elf"] = ["WYszaHaIkWs", "ksn0O9Iilb0", "-0wUiGX74GQ", "BlZ8niq3nHU"],
    };

    /// <summary>CONTROL writes stub code into the PLT entry itself; every documented stub must be seen.</summary>
    [Fact]
    public void Classifier_FindsEveryReadmeStub()
    {
        var reference = FirmwareDb.LoadGenstubExports(Samples.RefStubs);
        foreach (var (rel, expected) in ReadmeStubs)
        {
            var statuses = Imports.Classify(SceElf.Load(Samples.Work(["CONTROL_elf", .. rel.Split('/')])), reference);
            Assert.Equal(expected.Order(StringComparer.Ordinal), statuses.Where(s => s.Status == Imports.Stubbed).Select(s => s.Nid).Distinct().Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void FixTable_CoversEveryReadmeStub()
    {
        var k = Samples.K;
        Assert.All(ReadmeStubs.Values.SelectMany(n => n), n => Assert.True(k.Fixes.ContainsKey(n), n));
    }

    [Fact]
    public void Fw403_Encodings()
    {
        Assert.Equal((0x04030031u, 0x09040001u), Firmware.SdkFor("4.03"));
        Assert.Equal("0x0403000000000000", Firmware.ParamVersion("4.03"));
        Assert.Equal(Firmware.SdkFor("4.03"), Sdk.Read(SceElf.Load(Samples.Work("CONTROL_elf", "eboot.elf"))));
    }

    /// <summary>The audio fix calls the real GetSpeakerInfo through its own GOT slot, then masks the result.</summary>
    [Fact]
    public void SpeakerInfo_PostCallFix()
    {
        var k = Samples.K;
        var elf = SceElf.Load(Samples.Work("CONTROL_elf", "eboot.elf"));
        const string nid = "DImz2Ft9E2g";   // sceAudioOut2GetSpeakerInfo
        var imp = elf.Imports().First(i => i.Nid == nid && i.Plt);
        FixApplier.Apply(elf, [nid], k);
        var (executed, called, _) = Samples.Trace(elf, imp, k.Fixes[nid]);
        Assert.Equal(imp.GotVaddr, called);
        Assert.Equal(k.Fixes[nid].Insns.Select(i => i.Hex), executed);
    }

    /// <summary>The imports GT7's and CONTROL's tested backports left unresolved are exactly data/left_unpatched.json's.</summary>
    [Fact]
    public void LeftUnpatched_MatchesTheConsoleTestedBackports()
    {
        var k = Samples.K;
        HashSet<string> Unresolved(IEnumerable<string> paths, string firmware)
        {
            var elfs = paths.Select(SceElf.Load).ToList();
            var reference = Imports.LoadReference(Samples.RefStubs, firmware);   // the list was written against the 3.20 genstub list
            var provided = elfs.SelectMany(e => e.Exports()).ToHashSet(StringComparer.Ordinal);
            return elfs.SelectMany(e => Imports.Classify(e, reference.Nids, provided.Except(e.Exports()).ToHashSet(), reference.Exact))
                       .Where(s => Imports.IsUnresolved(s.Status)).Select(s => s.Nid).ToHashSet();
        }
        var gt7 = Unresolved(new[] { "GT7_root_elf", "GT7_sce_module_elf", "GT7_fakelib_elf" }
                                 .SelectMany(d => Directory.EnumerateFiles(Samples.Work(d), "*.elf")), "4");
        var control = Unresolved(Directory.EnumerateFiles(Samples.Work("CONTROL_elf"), "*.elf", SearchOption.AllDirectories), "4.03");
        Assert.Equal(k.LeftUnpatched.Where(kv => kv.Value.Contains(k.SourceNames["GT7"])).Select(kv => kv.Key).Order(), gt7.Order());
        Assert.Equal(k.LeftUnpatched.Where(kv => kv.Value.Contains(k.SourceNames["CONTROL"])).Select(kv => kv.Key).Order(), control.Order());
    }
}

public class PatchEngineTests
{
    [Fact]
    public void CodeReferences_Counts()
    {
        var elf = SceElf.Load(Samples.Work("NOT_BACKPORT_elf", "eboot.elf"));
        var reference = Imports.LoadReference(Repo.Need("firmware_db"), "4");
        var statuses = Imports.Classify(elf, reference.Nids, exact: reference.Exact).Where(s => s.Nid is "2jiIxUmcsGo" or "bIi4YUfSRys" or "f3kPeoTZnIE");
        var refs = Imports.CodeReferences(elf, statuses);
        Assert.Equal(0, refs["2jiIxUmcsGo"]);   // sceTextToSpeech2Cancel: never referenced
        Assert.Equal(3, refs["bIi4YUfSRys"]);   // sceHmd2GetDeviceInformation
        Assert.Equal(1, refs["f3kPeoTZnIE"]);   // sceHmd2Open
    }

    static void FillPadding(SceElf elf)
    {
        var t = elf.TextSegment();
        for (long i = t.Offset + 1; i < t.Offset + t.Filesz; i++)
            if (elf.Data[i] == Patching.Int3 && elf.Data[i - 1] is Patching.Ret or 0x90)
                elf.Data[i] = 0x90;
    }

    [Fact]
    public void ExtendText_IntoThePageGap_Once()
    {
        var elf = SceElf.Load(Samples.Work("GT7_sce_module_elf", "libc.elf"));
        var text = elf.TextSegment();
        long oldEnd = text.Offset + text.Filesz;
        Assert.Equal(0x812, Patching.ExtendText(elf));            // 0x1137ee -> next segment at 0x114000
        Assert.Equal(Patching.Ret, elf.Data[oldEnd]);
        var again = new SceElf(elf.Data);
        Assert.Equal((0x114000L, 0x114000L), (again.TextSegment().Filesz, again.TextSegment().Memsz));
        Assert.Equal(0, Patching.ExtendText(elf));                // only once
    }

    [Fact]
    public void NoPadding_NeedsTheExtensionFlag_AndStillSignsExactly()
    {
        var k = Samples.K;
        var elf = SceElf.Load(Samples.Work("GT7_sce_module_elf", "libc.elf"));
        FillPadding(elf);
        string nid = elf.Imports().First(i => i.Plt).Nid;
        Assert.Contains("no int3 padding", Assert.Throws<PatchException>(() => Patching.StubImports(elf, [nid], k)).Message);
        elf.AllowTextExtension = true;
        var p = Assert.Single(Patching.StubImports(elf, [nid], k));
        Assert.True(p.StubVaddr >= 0x1137ee);                    // the stub sits in the extended bytes
        Assert.Equal(0x812, elf.TextExtended);
        Assert.Equal(elf.Data, Self.Unwrap(Self.Sign(elf.Data)));
    }

    [Fact]
    public void PageExactSegment_HasNoGap() => Assert.Equal(0, Patching.ExtendText(SceElf.Load(Samples.Work("GT7_fakelib_elf", "libScePsml.elf"))));

    [Fact]
    public void PatchParam_OnlyVersionsChange_AndLineEndingsAreKept()
    {
        string root = Samples.Backports("PPSA01317", "default");
        string original = File.ReadAllText(Path.Combine(root, "sce_sys", "param.json"));
        var info = GameScan.ReadParam(root, "4", patchParam: true);
        Assert.Contains("\r\n", original);                        // the samples are CRLF
        Assert.Equal(original.Replace("\"0x1360000000000000\"", "\"0x0400000000000000\"").Replace("\"0x1300000000000000\"", "\"0x0400000000000000\""),
                     info.PatchedText);
        Assert.Contains("lowered to 0x0400000000000000", info.Warnings[0]);
    }

    /// <summary>A plt_stub points the PLT entry at a cave and leaves the GOT slot alone.</summary>
    [Fact]
    public void PltStub_JumpsToTheStub_AndKeepsTheGot()
    {
        var k = Samples.K;
        var elf = SceElf.Load(Samples.Work("NOT_BACKPORT_elf", "eboot.elf"));
        var table = k.FixTable(["force_pro"]);
        const string nid = "NH6xARDOVv8";   // GT7's eboot imports no IsTrinityMode (its fakelibs do)
        var imp = elf.Imports().First(i => i.Nid == nid && i.Plt);
        ulong gotBefore = elf.ReadU64(imp.GotVaddr);
        ulong plt = Patching.PltEntry(elf, imp);
        FixApplier.Apply(elf, [nid], k, table);
        long off = elf.Off(plt);
        Assert.Equal(0xE9, elf.Data[off]);
        long target = elf.Off((ulong)((long)plt + 5 + BinaryPrimitives.ReadInt32LittleEndian(elf.Data.AsSpan((int)off + 1))));
        byte[] body = k.StubBytes(table[nid].Stub);
        Assert.Equal(body, elf.Data[(int)target..(int)(target + body.Length)]);
        Assert.Equal(gotBefore, elf.ReadU64(imp.GotVaddr));
    }

    [Fact]
    public void Patterns_ReplaceAllRespectsStep_AndModulePatternsSkipCode()
    {
        var k = Samples.K;
        var strap = k.Options["gpu_scratch"].Patterns[0];   // s_trap 1 -> s_nop
        byte[] data = [0, .. strap.Find, 0, 0, 0, .. strap.Find];
        Assert.Equal(1, FixApplier.ReplaceAll(data, strap, 0, data.Length, 4));   // only offset 8
        Assert.Equal(strap.Find, data[1..5]);
        Assert.Equal(strap.Replace, data[8..12]);

        var elf = SceElf.Load(Samples.Work("NOT_BACKPORT_elf", "eboot.elf"));
        var t = elf.TextSegment();
        byte[] code = elf.Data[(int)t.Offset..(int)(t.Offset + t.Filesz)];
        Assert.Equal(202, FixApplier.ApplyPattern(elf, strap));                  // GT7's SDK BVH shaders
        Assert.Equal(code, elf.Data[(int)t.Offset..(int)(t.Offset + t.Filesz)]);
        Assert.Equal(0, FixApplier.ApplyPattern(elf, strap));
        // the default patterns (Wwise, EA) leave GT7 alone
        Assert.All(k.Patterns, p => Assert.Equal(0, FixApplier.ApplyPattern(SceElf.Load(Samples.Work("NOT_BACKPORT_elf", "eboot.elf")), p)));
    }

    /// <summary>CONTROL's backport already changed the Wwise speaker-setup site; the channel-switch site is still 7.1.4.</summary>
    [Fact]
    public void WwiseCodePatterns_OnControl()
    {
        var k = Samples.K;
        var elf = SceElf.Load(Samples.Work("CONTROL_elf", "eboot.elf"));
        var (setup, change, packed) = (k.Patterns[0], k.Patterns[1], k.Patterns[2]);
        Assert.True(setup.Code && change.Code && packed.Code);
        Assert.Equal(0, FixApplier.ApplyPattern(elf, setup));
        Assert.Equal(1, FixApplier.ApplyPattern(elf, change));
        Assert.Equal(0, FixApplier.ApplyPattern(elf, change));
        Assert.Equal(1, FixApplier.ApplyPattern(elf, packed));
    }
}

public class WholeGameTests
{
    /// <summary>AC Black Flag Resynced at FW 4.50 with both options reproduces AC's own files and leaves nothing unresolved.</summary>
    [Fact]
    public void AcFw450_WithOptions()
    {
        string ac = Samples.Backports("PPSA28183");
        string output = Samples.Temp("ac");
        try
        {
            var result = Samples.Backport(Path.Combine(ac, "default"), output, "4.50", Repo.Need("firmware_db"),
                                          store: Path.Combine(Repo.Root, "fakelibs"), options: ["force_pro", "gpu_scratch"]);
            // gpu_scratch reproduces AC's own files: ShaderData.bin of 4xx+, the hooked AgcDriver of 10xx+
            Assert.Equal(File.ReadAllBytes(Path.Combine(ac, "4xx+", "ShaderData.bin")), File.ReadAllBytes(Path.Combine(output, "ShaderData.bin")));
            Assert.Equal(File.ReadAllBytes(Path.Combine(ac, "10xx+", "fakelib", "libSceAgcDriver.sprx")),
                         File.ReadAllBytes(Path.Combine(output, "fakelib", "libSceAgcDriver.sprx")));
            var mods = result.Modules.ToDictionary(m => m.Path);
            Assert.Equal(["force_pro", "gpu_scratch"], result.Options);
            var data = Assert.Single(result.DataFiles);
            Assert.Equal(("ShaderData.bin", "s_trap 1 -> s_nop", 168, true), (data.Path, data.Pattern, data.Count, data.Written));
            var pattern = Assert.Single(mods["eboot.bin"].Patterns);
            Assert.Equal(("s_trap 1 -> s_nop", 164), (pattern.Name, pattern.Count));
            var (eboot, aegir) = (mods["eboot.bin"], mods["libaegir_f.prx"]);
            Assert.Empty(eboot.Unknown);
            Assert.Empty(aegir.Unknown);
            var fixedHow = eboot.Fixed.ToDictionary(f => f.Nid, f => f.How);
            Assert.Contains("ZP4e7rlzOUk", fixedHow.Keys);
            foreach (string nid in new[] { "NH6xARDOVv8", "tU5e3f9gSiU", "BfBDZGbti7A" })
                Assert.StartsWith("PLT -> ", fixedHow[nid]);
            Assert.Subset(eboot.StubbedNow.Select(s => s.Nid).ToHashSet(), new HashSet<string> { "w7Ipp9Xl7hg", "IXmfUaze9So" });
            Assert.Equal(["-0wUiGX74GQ", "D1IAo-CNswA", "jSHMX1oSE+E", "v2hFBiyS13w"], aegir.StubbedNow.Select(s => s.Nid).Order(StringComparer.Ordinal));
        }
        finally { Directory.Delete(output, true); }
    }

    static JsonElement Report(string game)
    {
        string output = Samples.Temp("vr");
        try
        {
            Samples.Backport(game, output, "4", Repo.Need("firmware_db"));
            return JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, Pipeline.ReportJson))).RootElement.Clone();
        }
        finally { Directory.Delete(output, true); }
    }

    [Fact]
    public void Psvr2Game_IsFlagged()
    {
        var report = Report(Samples.Backports("PPSA05666", "default"));   // Horizon Call of the Mountain: VR only
        Assert.Equal(["eboot.bin libSceHmd2", "eboot.bin libSceHmd2ReprojectionPositional", "eboot.bin libSceVrTracker2"],
                     report.GetProperty("psvr2").EnumerateArray().Select(p => $"{p.GetProperty("module").GetString()} {p.GetProperty("library").GetString()}").Order(StringComparer.Ordinal));
        Assert.Single(report.GetProperty("param_warnings").EnumerateArray(), w => w.GetString()!.Contains("uses PS VR2"));
    }

    [Fact]
    public void OtherGame_IsNotFlaggedAsPsvr2()
    {
        var report = Report(Samples.Backports("PPSA36082", "default"));   // Hades II
        Assert.Equal(0, report.GetProperty("psvr2").GetArrayLength());
        Assert.DoesNotContain(report.GetProperty("param_warnings").EnumerateArray(), w => w.GetString()!.Contains("PS VR2"));
    }

    [Fact]
    public void App0File_AddedForAPssrGame()
    {
        string set = Repo.Need("fakelibs", "4");
        Repo.Need("fakelibs", "4", "app0", "k9.psp");
        string output = Samples.Temp("app0");
        try
        {
            var result = Samples.Backport(Samples.Backports("PPSA01317", "decrypted"), output, "4", Samples.RefStubs, extraFakelib: set);
            Assert.Equal(["k9.psp"], result.App0Files);
            Assert.Equal(4135904, new FileInfo(Path.Combine(output, "k9.psp")).Length);
        }
        finally { Directory.Delete(output, true); }
    }

    [Fact]
    public void Backport_UsesTheStoreWithoutAFakelibOption()
    {
        string fakelib = Samples.Backports("PPSA01317", "4xx+", "USA", "fakelib");
        string store = Samples.Temp("store"), output = Samples.Temp("storeout");
        try
        {
            var files = Directory.EnumerateFiles(fakelib).Order(StringComparer.Ordinal).ToList();
            FakelibStore.Add(store, "4", files, "GT7 test");
            Assert.Equal(Path.Combine(store, "4"), FakelibStore.Find(store, "4.50"));   // no 4.50 folder -> major
            var result = Samples.Backport(Samples.Backports("PPSA01317", "default"), output, "4.50", Samples.RefStubs, store: store);
            Assert.Equal(Path.Combine(store, "4"), result.FakelibSource);
            Assert.All(files, f => Assert.True(File.Exists(Path.Combine(output, "fakelib", Path.GetFileName(f))), f));
        }
        finally
        {
            Directory.Delete(store, true);
            Directory.Delete(output, true);
        }
    }

    [Fact]
    public void EncryptedSelf_StopsWithADecryptHint_AndStaleAmprIndexWarns()
    {
        string right = Samples.Backports("PPSA01317", "4xx+", "USA", "sce_sys", "about", "right.sprx");
        string game = Samples.Temp("enc"), output = Samples.Temp("encout");
        try
        {
            byte[] self = File.ReadAllBytes(right);
            Assert.Equal(0, Self.EncryptedSegments(self));                   // a plain fake SELF is not encrypted
            self[0x20] |= (byte)Self.SegmentEncrypted;                        // flag the first segment entry
            File.WriteAllBytes(Path.Combine(game, "right.sprx"), self);
            Assert.Equal(1, Self.EncryptedSegments(self));
            File.WriteAllBytes(Path.Combine(game, "ampr_emu.index"), []);
            var result = Samples.Backport(game, output, "4", Samples.RefStubs);
            Assert.Contains("decrypt the game on a console first", result.Modules[0].Error);
            Assert.Contains(result.ParamWarnings, w => w.Contains("ampr_emu.index") && w.Contains("delete it"));
        }
        finally
        {
            Directory.Delete(game, true);
            Directory.Delete(output, true);
        }
    }
}

public class BackPorkPatchTests
{
    static string BackPork(params string[] rel) => Repo.Need([".original", "reference", "BackPork", "patches", .. rel]);

    /// <summary>10.01 libSceNpAuth: the patch rewrites only the SDK bytes, ending with the target's PS5 SDK.</summary>
    [Fact]
    public void NpAuthPatches_AreSdkPatches()
    {
        foreach (var (fw, folder) in new[] { ("6", "6xx"), ("7", "7xx") })
        {
            var (ps5, ps4) = Firmware.SdkFor(fw);
            byte[] pair = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(pair, ps4);
            BinaryPrimitives.WriteUInt32LittleEndian(pair.AsSpan(4), ps5);
            foreach (string name in new[] { "libSceNpAuth.bps", "libSceNpAuthAuthorizedAppDialog.bps" })
            {
                var edit = Assert.Single(Bps.Edits(Bps.Load(BackPork(folder, name))));
                Assert.Equal("write", edit.Kind);
                Assert.True(pair.AsSpan().EndsWith(edit.Data), $"{fw} {name} {Convert.ToHexStringLower(edit.Data)}");
            }
        }
    }

    [Fact]
    public void AllPatches_AreInPlace()
    {
        foreach (string folder in new[] { "6xx", "7xx" })
            foreach (string file in Directory.EnumerateFiles(BackPork(folder), "*.bps"))
            {
                var p = Bps.Load(file);
                Assert.Equal(p.SourceSize, p.TargetSize);
            }
    }
}
