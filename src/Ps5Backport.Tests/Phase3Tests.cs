using System.Text.Json;
using System.Text.Json.Serialization;
using Ps5Backport.Core;

namespace Ps5Backport.Tests;

/// <summary>
/// The sample games (see <see cref="SampleSet.SampleRoots"/>) with an "exact 4.00" export list made from the 3.20 dump,
/// so classification can be checked both as a lower bound and as exact.
/// </summary>
public sealed class AnalysisSet : IDisposable
{
    public string ExactDb { get; } = Path.Combine(Path.GetTempPath(), "ps5backport-analysis-" + Guid.NewGuid().ToString("N"));
    public List<string> Roots { get; }
    public static readonly string[] Targets = ["4", "4.03", "10"];

    public AnalysisSet()
    {
        if (Repo.RequirePrivate)
            foreach (string root in SampleSet.SampleRoots)
                Repo.Need(".original", root);
        Roots = SampleSet.SampleRoots.Select(r => Path.Combine(Repo.Root, ".original", r)).Where(Directory.Exists).ToList();
        string db320 = Path.Combine(Repo.Root, "firmware_db", "3.20.json");
        if (Roots.Count == 0 || !File.Exists(db320))
            return;
        Directory.CreateDirectory(ExactDb);
        File.Copy(db320, Path.Combine(ExactDb, "3.20.json"));
        var d4 = FirmwareDb.Load(db320);
        d4.Firmware = "4.00";
        FirmwareDb.Save(d4, Path.Combine(ExactDb, "4.00.json"));
    }

    public List<string> Need()
    {
        if (!Directory.Exists(ExactDb))
            Repo.Missing("sample games or firmware_db/3.20.json", privateFixture: true);
        return Roots;
    }

    /// <summary>"backports/PPSA01317/default" -> "PPSA01317_default" (golden file name).</summary>
    public static string Tag(string root) => Path.GetRelativePath(Path.Combine(Repo.Root, ".original", "backports"), root).Replace('\\', '_').Replace('/', '_');

    public void Dispose()
    {
        if (Directory.Exists(ExactDb))
            Directory.Delete(ExactDb, recursive: true);
    }
}

[Trait("Lane", "Private")]
public class AnalysisTests(AnalysisSet set) : IClassFixture<AnalysisSet>
{
    static string Rows(IEnumerable<ImportStatus> statuses) =>
        string.Concat(statuses.Select(s => $"{s.Nid} {s.Library} {s.GotVaddr} {s.Plt} {s.Status}\n"));

    /// <summary>The unwrapped, unencrypted modules of one sample game and the pooled provider exports.</summary>
    static List<(string Rel, SceElf Elf, HashSet<string> Provided)> Load(string root)
    {
        var loaded = GameScan.FindModules(root, cancel: TestContext.Current.CancellationToken)
            .Where(rel => Self.IsSelf(Self.FileKind(Path.Combine(root, rel))) && Self.EncryptedSegments(Path.Combine(root, rel)) == 0)
            .Select(rel => (rel, Elf: new SceElf(Self.Unwrap(File.ReadAllBytes(Path.Combine(root, rel)))))).ToList();
        return loaded.Select((x, i) => (x.rel, x.Elf,
            loaded.Where((_, j) => j != i).SelectMany(m => m.Elf.Exports()).ToHashSet(StringComparer.Ordinal))).ToList();
    }

    /// <summary>
    /// Per sample game: its modules, and per module the import statuses (lower bound FW 4.10, exact FW 4), the code
    /// references and the text report (hashes; the readable report is in the golden file).
    /// </summary>
    [Fact]
    public void ModulesClassifyReferencesAndReports_MatchGolden()
    {
        var lower = Imports.LoadReference(Path.Combine(Repo.Root, "firmware_db"), "4.10");
        var exact = Imports.LoadReference(set.ExactDb, "4");
        var names = new NameDb(Repo.Need(".original", "reference", "sce_symbols_new", "aerolib.csv"));
        int modules = 0;
        foreach (string root in set.Need())
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("modules: " + string.Join(", ", GameScan.FindModules(root, cancel: TestContext.Current.CancellationToken).Select(m => m.Replace('\\', '/'))) + "\n");
            foreach (var (rel, elf, provided) in Load(root))
            {
                var statuses = Imports.Classify(elf, lower.Nids, provided, lower.Exact);
                var refs = Imports.CodeReferences(elf, statuses, cancel: TestContext.Current.CancellationToken).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}");
                sb.Append($"\n== {rel.Replace('\\', '/')}\n");
                sb.Append($"lower {Golden.Sha(Rows(statuses))}\n");
                sb.Append($"exact {Golden.Sha(Rows(Imports.Classify(elf, exact.Nids, provided, exact.Exact)))}\n");
                sb.Append($"refs {string.Join(" ", refs)}\n");
                sb.Append(Imports.Report(rel, elf, statuses, names));
                modules++;
            }
            Golden.Match(Path.Combine("analysis", AnalysisSet.Tag(root) + ".txt"), sb.ToString());
        }
        Assert.True(modules >= 25, $"only {modules} modules analysed");
    }

    [Fact]
    public void LoadReference_Labels()
    {
        set.Need();
        var lower = Imports.LoadReference(Path.Combine(Repo.Root, "firmware_db"), "4.10");
        var exact = Imports.LoadReference(set.ExactDb, "4");
        var stubs = Imports.LoadReference(Repo.Need(".original", "reference", "PS5-3.20_Libs"), "4");
        Golden.Match("reference-labels.txt", Golden.Portable($"{lower.Label}\n{exact.Label}\n{stubs.Label}\n", (set.ExactDb, "<exact-db>")));
        Assert.False(lower.Exact);                         // 4.10: no dump, the 4.03 list is a lower bound
        Assert.True(exact.Exact);
        Assert.False(stubs.Exact);
        Assert.EndsWith("(lower bound)", stubs.Label);
    }

    /// <summary>The same list once as a lower bound, once as exact: every "unknown" becomes "missing".</summary>
    [Fact]
    public void Unknown_BecomesMissing_WithAnExactList()
    {
        var elf = SceElf.Load(Repo.Need("work", "NOT_BACKPORT_elf", "eboot.elf"));
        string db = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ps5backport-genstubdb-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            // the 3.20 genstub list (it lacks sceTextToSpeech2Initialize), once as 3.20 and once as an "exact 4.00"
            var stubs = FirmwareDb.ImportGenstub(Repo.Need(".original", "reference", "PS5-3.20_Libs"), "3.20", "genstub test");
            FirmwareDb.Save(stubs, Path.Combine(db, "3.20.json"));
            stubs.Firmware = "4.00";
            FirmwareDb.Save(stubs, Path.Combine(db, "4.00.json"));
            AssertUnknownBecomesMissing(elf, Imports.LoadReference(db, "4.03"), Imports.LoadReference(db, "4"));
        }
        finally { Directory.Delete(db, true); }
    }

    static void AssertUnknownBecomesMissing(SceElf elf, Reference lower, Reference exact)
    {
        Assert.Contains("lower bound", lower.Label);
        Assert.Contains("exact", exact.Label);
        var l = Imports.Classify(elf, lower.Nids, exact: false).ToDictionary(s => s.Nid, s => s.Status);
        var x = Imports.Classify(elf, exact.Nids, exact: true).ToDictionary(s => s.Nid, s => s.Status);
        Assert.Equal(Imports.Unknown, l["UOjiprYwVNw"]);   // sceTextToSpeech2Initialize
        Assert.Equal(Imports.Missing, x["UOjiprYwVNw"]);
        Assert.Equal(l.Where(kv => kv.Value == Imports.Unknown).Select(kv => kv.Key).Order(), x.Where(kv => kv.Value == Imports.Missing).Select(kv => kv.Key).Order());
    }

    [Fact]
    public void Param_And_AmprWarning_MatchGolden()
    {
        foreach (string root in set.Need())
        {
            var sb = new System.Text.StringBuilder();
            foreach (string fw in AnalysisSet.Targets)
                foreach (bool patch in new[] { false, true })
                {
                    var info = GameScan.ReadParam(root, fw, patch);
                    sb.Append($"== {fw} patch={patch}: {info.TitleId} {info.ContentVersion}\n");
                    foreach (string w in info.Warnings)
                        sb.Append($"warning: {w}\n");
                    if (info.PatchedText is not null)
                        sb.Append($"patched sha256 {Golden.Sha(System.Text.Encoding.UTF8.GetBytes(info.PatchedText))}\n");
                }
            sb.Append($"ampr: {GameScan.AmprIndexWarning(root)}\n");
            Golden.Match(Path.Combine("param", AnalysisSet.Tag(root) + ".txt"), Golden.Portable(sb.ToString()));
        }
    }

    [Fact]
    public void StoreCheck_MatchesGolden()
    {
        set.Need();
        foreach (string fw in new[] { "4", "4.03" })
        {
            string folder = Repo.Need("fakelibs", fw);
            var reference = Imports.LoadReference(Path.Combine(Repo.Root, "firmware_db"), fw);
            var sb = new System.Text.StringBuilder();
            foreach (var (name, _, statuses) in FakelibStore.Check(folder, reference))
                sb.Append($"== {name}\n{Rows(statuses)}");
            Golden.Match(Path.Combine("store-check", fw + ".txt"), sb.ToString());
        }
    }

    [Fact]
    public void ConsoleTestedSets_AreCompleteOnExact403()
    {
        var exact = Imports.LoadReference(Repo.Need("firmware_db"), "4.03");
        Assert.True(exact.Exact);
        Assert.All(FakelibStore.Check(Repo.Need("fakelibs", "4.03"), exact),
                   c => Assert.DoesNotContain(c.Statuses, s => Imports.IsUnresolved(s.Status)));
    }

    [Fact]
    public void ConsoleTestedSets_LeaveTheKnownFourUnresolved_AgainstThe320Stubs()
    {
        var reference = Imports.LoadReference(Repo.Need(".original", "reference", "PS5-3.20_Libs"), "4.03");
        var found = FakelibStore.Check(Repo.Need("fakelibs", "4.03"), reference)
            .ToDictionary(c => c.Name, c => c.Statuses.Where(s => Imports.IsUnresolved(s.Status)).Select(s => s.Nid).ToHashSet());
        Assert.Equal(["GGeRJk1XdWc", "dkBx0YqFQ+Y"], found["libSceAgc.sprx"].Order(StringComparer.Ordinal));
        Assert.Equal(["LzoM-wVLJDE"], found["libSceAgcDriver.sprx"]);
        Assert.Equal(["GGeRJk1XdWc", "Ygv0S+Hi+hA", "dkBx0YqFQ+Y"], found["libScePsml.sprx"].Order(StringComparer.Ordinal));
        Assert.Empty(found["libSceAmpr.sprx"]);
    }
}

/// <summary>A dump needs ampr_emu and the PlayGo stub in fakelib/ (game's own or from the set).</summary>
public class DumpFakelibWarningTests
{
    [Fact]
    public void BothPresent_NoWarning() =>
        Assert.Null(GameScan.DumpFakelibWarning([@"fakelib\libSceAmpr.sprx", "fakelib/LIBSCEPLAYGO.SPRX"]));

    [Fact]
    public void Missing_OnesAreNamed()
    {
        string? warning = GameScan.DumpFakelibWarning(["eboot.bin", @"fakelib\libSceAmpr.sprx"]);
        Assert.Equal("no fakelib/libScePlayGo.sprx in the game or the fakelib set: a dump needs them under ShadowMountPlus, " +
                     "so the game would depend on the console's own (global fakelib or auto_update_ampr)", warning);
    }
}
