using System.Security.Cryptography;
using System.Text.Json;
using Ps5Backport.Core;

namespace Ps5Backport.Tests;

/// <summary>
/// Phase 4: the patch engine, against golden results recorded when it matched ps5bp byte for byte. Scenarios:
///   all     every applicable known fix on every NOT_BACKPORT module (swap, redirect, adapters with
///           chained caves, post-call adapter, argument fixes, stubs with stub rules)
///   extend  the same on GT7's libc with all padding used up and the code-space fallback allowed
///   define  stubs with define_symbol on a fakelib, incl. a RELA (data) import and a ret:&lt;value&gt; rule
/// </summary>
[Trait("Lane", "Private")]
public class PatchTests
{
    static readonly string NotBackport = Path.Combine("work", "NOT_BACKPORT_elf");

    /// <summary>Uses up every int3 run after a ret (simulates a module with no padding left).</summary>
    static void FillPadding(SceElf elf)
    {
        var t = elf.TextSegment();
        var d = elf.Data;
        for (long i = t.Offset + 1; i < t.Offset + t.Filesz; i++)
            if (d[i] == Patching.Int3 && d[i - 1] is Patching.Ret or 0x90)
                d[i] = 0x90;
    }

    /// <summary>Applies every applicable known fix (non-stub first, then stubs).</summary>
    static (string Sha, string[][] Applied, string[][] Stubs, long Extended) ApplyAll(SceElf elf, Knowledge k)
    {
        var plt = elf.Imports().Where(i => i.Plt).Select(i => i.Nid).ToHashSet();
        var nids = k.FixList.Select(f => f.Nid).Where(n => plt.Contains(n) && FixApplier.Applicable(elf, n, k)).ToList();
        var applied = FixApplier.Apply(elf, nids.Where(n => k.Fixes[n].Kind != Fix.KindStub).ToList(), k);
        var stubs = Patching.StubImports(elf, nids.Where(n => k.Fixes[n].Kind == Fix.KindStub).ToList(), k, FixApplier.StubRules(k));
        return (Golden.Sha(elf.Data), applied.Select(a => new[] { a.Nid, a.How }).ToArray(),
                stubs.Select(s => new[] { s.Nid, s.GotVaddr.ToString(), s.Stub, s.StubVaddr.ToString() }).ToArray(), elf.TextExtended);
    }

    /// <summary>The patched bytes' hash, then one line per applied fix and per stub, then the bytes added to .text.</summary>
    static string Format((string Sha, string[][] Applied, string[][] Stubs, long Extended) r) =>
        $"sha256 {r.Sha}\n" + string.Concat(r.Applied.Select(a => $"fix {string.Join(" ", a)}\n")) +
        string.Concat(r.Stubs.Select(s => $"stub {string.Join(" ", s)}\n")) + $"extended {r.Extended}\n";

    [Fact]
    public void AllKnownFixes_OnEveryNotBackportModule_MatchGolden()
    {
        string root = Repo.Need(NotBackport);
        var files = Directory.EnumerateFiles(root, "*.elf", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        var k = new Knowledge(Repo.Need("data"));
        var sb = new System.Text.StringBuilder();
        int applied = 0;
        foreach (string file in files)
        {
            var result = ApplyAll(SceElf.Load(file), k);
            sb.Append($"== {Path.GetRelativePath(root, file).Replace('\\', '/')}\n{Format(result)}");
            applied += result.Applied.Length + result.Stubs.Length;
        }
        Golden.Match(Path.Combine("patch", "all-known-fixes.txt"), sb.ToString());
        Assert.True(applied >= 19, $"only {applied} fixes applied");   // eboot 3 + 6, libc 6 + 4
    }

    [Fact]
    public void CodeSpaceFallback_WhenPaddingIsUsedUp_MatchesGolden()
    {
        string libc = Repo.Need("work", "NOT_BACKPORT_elf", "sce_module", "libc.elf");
        var k = new Knowledge(Repo.Need("data"));
        var elf = SceElf.Load(libc);
        FillPadding(elf);
        var e = Assert.Throws<PatchException>(() => ApplyAll(elf, k));
        Assert.Contains("no int3 padding", e.Message);

        elf = SceElf.Load(libc);
        FillPadding(elf);
        elf.AllowTextExtension = true;
        var result = ApplyAll(elf, k);
        Assert.Equal(0x812, result.Extended);              // one cave per 64-byte block: every libc fix fits
        Assert.Equal((6, 4), (result.Applied.Length, result.Stubs.Length));   // swap, redirect, 4 adapters; 4 stubs
        Golden.Match(Path.Combine("patch", "libc-extended.txt"), Format(result));
    }

    [Fact]
    public void DefineSymbolStubs_IncludingDataImport_MatchGolden()
    {
        string agc = Repo.Need("work", "GT7_fakelib_elf", "libSceAgc.elf");
        var elf = SceElf.Load(agc);
        var free = elf.Imports().Where(i => !i.DefinedLocally).ToList();
        var nids = free.Where(i => i.Plt).Select(i => i.Nid).Distinct().Take(2)
                       .Concat(free.Where(i => !i.Plt).Select(i => i.Nid).Take(1)).ToList();
        Assert.Equal(3, nids.Count);
        var rules = new Dictionary<string, string> { [nids[0]] = "ret:0x8002002d" };
        var k = new Knowledge(Repo.Need("data"));
        var stubs = Patching.StubImports(elf, nids, k, rules, defineSymbol: true);
        Golden.Match(Path.Combine("patch", "define-symbol.txt"),
                     Format((Golden.Sha(elf.Data), [], stubs.Select(s => new[] { s.Nid, s.GotVaddr.ToString(), s.Stub, s.StubVaddr.ToString() }).ToArray(), 0)));
        Assert.Contains("RELA", Assert.Throws<PatchException>(() => Patching.StubImports(SceElf.Load(agc), [nids[2]], k)).Message);
        // the symbols now read as stubbed (defined locally)
        Assert.All(new SceElf(elf.Data).Imports().Where(i => nids.Contains(i.Nid)), i => Assert.True(i.DefinedLocally));
    }

    [Fact]
    public void Errors_AreReported()
    {
        string libc = Repo.Need("work", "NOT_BACKPORT_elf", "sce_module", "libc.elf");
        var k = new Knowledge(Repo.Need("data"));
        var elf = SceElf.Load(libc);
        Assert.Contains("not imported", Assert.Throws<PatchException>(() => Patching.StubImports(elf, ["XXXXXXXXXXX"], k)).Message);
        var imp = elf.Imports().First(i => i.Plt);
        Assert.Contains("invalid NID", Assert.Throws<PatchException>(() => Patching.RenameImport(elf, imp, "short")).Message);
        Patching.RedirectPlt(elf, imp, Patching.PltEntry(elf, imp) + 0x10);
        Assert.Contains("not an untouched jmp", Assert.Throws<PatchException>(() => Patching.PltEntry(elf, imp)).Message);
    }

    [Fact]
    public void PatchedModules_ClassifyAsPatchedOrStubbed()
    {
        string libc = Repo.Need("work", "NOT_BACKPORT_elf", "sce_module", "libc.elf");
        var k = new Knowledge(Repo.Need("data"));
        var elf = SceElf.Load(libc);
        ApplyAll(elf, k);
        var statuses = Imports.Classify(elf, new HashSet<string>()).ToDictionary(s => s.Nid, s => s.Status);
        Assert.Equal(Imports.Patched, statuses[k.UmtxOp]);        // SyncOnAddress adapters: renamed to _umtx_op, PLT jmp to cave
        Assert.Equal(Imports.Patched, statuses["qBS714-Jr3g"]);   // redirect to local __cxa_atexit
        Assert.Equal(Imports.Stubbed, statuses["dFym5b41JLU"]);   // Wait8 stub (GOT -> stub)
    }
}
