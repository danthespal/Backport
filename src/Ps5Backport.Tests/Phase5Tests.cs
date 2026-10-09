using System.Text.Json;
using Ps5Backport.Core;

namespace Ps5Backport.Tests;

/// <summary>
/// Phase 5: whole-game backports and the command line. Each configuration's output folder is checked against a
/// golden record (every file's SHA-256 and the full text report), first recorded when the output was
/// byte-identical to ps5bp's.
/// </summary>
public class BackportParityTests
{
    static string Temp(string tag) =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ps5backport-{tag}-" + Guid.NewGuid().ToString("N"))).FullName;

    public sealed record Config(string Name, string Game, string Firmware, bool Store = true, bool PatchParam = false,
                                bool Candidates = false, bool StubUnknown = false, bool DefineSymbol = false, bool ExtendText = false,
                                string[]? Options = null);

    public static TheoryData<string> Configs => ["notbackport-4", "notbackport-403-all-options", "control-403", "notbackport-4-no-store",
                                                 "ac-450-options"];

    static Config Get(string name) => name switch
    {
        "notbackport-4" => new(name, "backports/PPSA01317/default", "4"),
        "notbackport-403-all-options" => new(name, "backports/PPSA01317/default", "4.03", PatchParam: true, Candidates: true,
                                             StubUnknown: true, DefineSymbol: true, ExtendText: true),
        "control-403" => new(name, "backports/PPSA34547/4xx+", "4.03", PatchParam: true),
        "notbackport-4-no-store" => new(name, "backports/PPSA01317/default", "4", Store: false),
        "ac-450-options" => new(name, "backports/PPSA28183/default", "4.50", Options: ["force_pro", "gpu_scratch"]),
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [MemberData(nameof(Configs))]
    public void Backport_OutputFolder_MatchesGolden(string name)
    {
        var c = Get(name);
        string game = Repo.Need(new[] { ".original" }.Concat(c.Game.Split('/')).ToArray());
        string aerolib = Repo.Need(".original", "reference", "sce_symbols_new", "aerolib.csv");
        string db = Repo.Need("firmware_db"), store = Path.Combine(Repo.Root, "fakelibs");
        string root = Temp("bp");
        string output = Path.Combine(root, "out");
        try
        {
            var result = Pipeline.Backport(game, output, new BackportOptions
            {
                Firmware = c.Firmware, ReferenceDir = db, FakelibStore = c.Store ? store : null, PatchParam = c.PatchParam,
                CandidateFixes = c.Candidates, StubUnknown = c.StubUnknown, DefineSymbol = c.DefineSymbol, ExtendText = c.ExtendText,
                Options = c.Options ?? [], Names = new NameDb(aerolib),
            }, new Knowledge(Repo.Need("data")), cancel: TestContext.Current.CancellationToken);
            Assert.NotEmpty(result.Modules);
            Golden.Match(Path.Combine("backport", name + ".txt"), OutputRecord(output, (game, "<game>"), (output, "<out>")));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>Every file of a backport output with its SHA-256 (reports with paths made portable), then the text report.</summary>
    public static string OutputRecord(string output, params (string Path, string Name)[] paths)
    {
        var sb = new System.Text.StringBuilder();
        foreach (string rel in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
                                        .Select(f => Path.GetRelativePath(output, f)).Order(StringComparer.Ordinal))
        {
            byte[] data = File.ReadAllBytes(Path.Combine(output, rel));
            if (rel.StartsWith("backport_report", StringComparison.Ordinal))
                data = System.Text.Encoding.UTF8.GetBytes(Golden.Portable(System.Text.Encoding.UTF8.GetString(data).Replace("\r\n", "\n"), paths));
            sb.Append($"{rel.Replace('\\', '/')} {Golden.Sha(data)}\n");
        }
        string report = Path.Combine(output, "backport_report.txt");
        if (File.Exists(report))
            sb.Append("\n").Append(Golden.Portable(File.ReadAllText(report).Replace("\r\n", "\n"), paths));
        return sb.ToString();
    }

    [Fact]
    public void Backport_RefusesOutputInsideTheGame()
    {
        string game = Repo.Need(".original", "backports", "PPSA01317", "default");
        var o = new BackportOptions { Firmware = "4", ReferenceDir = Repo.Need("firmware_db") };
        Assert.Throws<ArgumentException>(() => Pipeline.Backport(game, Path.Combine(game, "out"), o, new Knowledge(Repo.Need("data")), cancel: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => Pipeline.Backport(game, game, o, new Knowledge(Repo.Need("data")), cancel: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Backport_ReportsProgress_AndHonoursCancellation()
    {
        string game = Repo.Need(".original", "backports", "PPSA01317", "default");
        string root = Temp("cancel");
        try
        {
            using var cts = new CancellationTokenSource();
            var seen = new List<BackportProgress>();
            var progress = new SyncProgress(p => { seen.Add(p); if (p.Stage == "patching") cts.Cancel(); });
            var o = new BackportOptions { Firmware = "4", ReferenceDir = Repo.Need("firmware_db"), FakelibStore = Path.Combine(Repo.Root, "fakelibs") };
            Assert.Throws<OperationCanceledException>(() => Pipeline.Backport(game, Path.Combine(root, "out"), o, new Knowledge(Repo.Need("data")), progress, cts.Token));
            Assert.Contains(seen, p => p.Stage == "loading");
            Assert.All(seen, p => Assert.True(p.Count > 0 && p.Index < p.Count));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>IProgress that reports synchronously (Progress&lt;T&gt; posts to the thread pool).</summary>
    sealed class SyncProgress(Action<BackportProgress> report) : IProgress<BackportProgress>
    {
        public void Report(BackportProgress value) => report(value);
    }
}

public class CliParityTests
{
    static (int Exit, string Out) Cs(params string[] args)
    {
        var o = new StringWriter();
        int exit = Cli.Run(args, o, new StringWriter());
        return (exit, o.ToString().Replace("\r\n", "\n"));
    }

    /// <summary>Runs a command and checks its exit code and output against golden/cli/&lt;name&gt;.</summary>
    static void Golden_(string name, string[] args, params (string Path, string Name)[] paths)
    {
        var cs = Cs(args);
        Golden.Match(Path.Combine("cli", name + ".txt"), $"exit {cs.Exit}\n" + Golden.Portable(cs.Out, paths));
    }

    [Fact]
    public void ReadOnlyCommands_MatchGolden()
    {
        string libc = Repo.Need("work", "NOT_BACKPORT_elf", "sce_module", "libc.elf");
        string eboot = Repo.Need("work", "NOT_BACKPORT_elf", "eboot.elf");
        string self = Repo.Need(".original", "backports", "PPSA01317", "4xx+", "USA", "logiWheel.prx");
        string patches = Repo.Need(".original", "reference", "BackPork", "patches");
        Golden_("info", ["info", libc, self]);
        Golden_("report-libc-403-all", ["report", libc, "--firmware", "4.03", "--all"]);
        Golden_("report-eboot-4-provider", ["report", eboot, "--provider", libc, "--firmware", "4"]);
        Golden_("fwdb-list", ["fwdb", "list"]);
        Golden_("bps-info", ["bps", "info", .. Directory.EnumerateFiles(patches, "*.bps", SearchOption.AllDirectories).Order(StringComparer.Ordinal)]);
        Golden_("fakelib-list", ["fakelib", "list"]);
        Golden_("fakelib-verify", ["fakelib", "verify"]);
        Golden_("fakelib-list-403", ["fakelib", "list", "--firmware", "4.03"]);
        Golden_("fakelib-check-403", ["fakelib", "check", "--firmware", "4.03"]);
        Golden_("fakelib-check-4", ["fakelib", "check", "--firmware", "4"]);
    }

    [Fact]
    public void FwdbDiff_MatchesGolden()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ps5backport-fwdb-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            File.Copy(Repo.Need("firmware_db", "3.20.json"), Path.Combine(root, "3.20.json"));
            var newer = FirmwareDb.Load(Path.Combine(root, "3.20.json"));
            newer.Firmware = "4.00";
            newer.Modules["libkernel"].Add("tU5e3f9gSiU");
            newer.Modules["libSceNew"] = ["GGeRJk1XdWc", "dkBx0YqFQ+Y"];
            FirmwareDb.Save(newer, Path.Combine(root, "4.00.json"));
            Golden_("fwdb-diff", ["fwdb", "diff", "3.20", "4", "--db", root], (root, "<db>"));
            Golden_("fwdb-list-temp", ["fwdb", "list", "--db", root], (root, "<db>"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void WritingCommands_MatchGolden()
    {
        string libc = Repo.Need("work", "NOT_BACKPORT_elf", "sce_module", "libc.elf");
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ps5backport-cliw-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var sb = new System.Text.StringBuilder();
            void Run(string[] args, string outFile)
            {
                File.Delete(outFile);
                var cs = Cs(args);
                sb.Append($"$ {Golden.Portable(string.Join(' ', args), (root, "<tmp>"))}\nexit {cs.Exit}\n{Golden.Portable(cs.Out, (root, "<tmp>"))}" +
                          $"{Path.GetFileName(outFile)} {Golden.Sha(File.ReadAllBytes(outFile))}\n\n");
            }
            string o = Path.Combine(root, "o.elf");
            Run(["sdk", libc, o, "--firmware", "4.03"], o);
            Run(["sdk", libc, o, "--ps5", "0x05000033", "--ps4", "0x09590001"], o);
            Run(["stub", libc, o, "--nid", "dFym5b41JLU", "--nid", "XgVs4jRehcY"], o);
            Run(["stub", libc, o, "--unknown", "--firmware", "4"], o);
            Run(["sign", libc, o], o);
            string elf = Path.Combine(root, "back.elf");
            Run(["unwrap", o, elf], elf);
            Golden.Match(Path.Combine("cli", "writing-commands.txt"), sb.ToString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Errors_ExitWithOne()
    {
        Assert.Equal(1, Cs("info", Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid())).Exit);
        Assert.Equal(2, Cs().Exit);
        Assert.Equal(2, Cs("nosuchcommand", "x").Exit);
    }
}

/// <summary>Phase 6 support in Core: game metadata for the header, and dry runs for Analyze.</summary>
public class GuiSupportTests
{
    [Theory]
    [InlineData("UP9000-PPSA01317_00-GRANTURISMO70000", "US")]
    [InlineData("EP5291-PPSA34547_00-0210080241603648", "Europe")]
    [InlineData("JP0001-PPSA00000_00-X", "Japan")]
    [InlineData("HP0001-PPSA00000_00-X", "Asia")]
    [InlineData("", "")]
    public void Region_FromContentId(string contentId, string region) => Assert.Equal(region, GameScan.Region(contentId));

    [Theory]
    [InlineData("0x1360000000000000", "13.60")]
    [InlineData("0x0403000000000000", "4.03")]
    [InlineData("0x1000000000000000", "10.00")]
    [InlineData("nonsense", "")]
    public void FirmwareOf_ParamVersion(string value, string firmware) => Assert.Equal(firmware, GameScan.FirmwareOf(value));

    [Fact]
    public void ReadInfo_Gt7()
    {
        var info = GameScan.ReadInfo(Repo.Need(".original", "backports", "PPSA01317", "default"));
        Assert.Equal(("PPSA01317", "US", "13.60", "13.00", "01.710.000"), (info.TitleId, info.Region, info.RequiredFirmware, info.SdkVersion, info.ContentVersion));
        Assert.StartsWith("Gran Turismo", info.TitleName);
        Assert.NotNull(info.IconPath);
    }

    [Fact]
    public void DryRun_WritesNothing_ButReportsTheSame()
    {
        string game = Repo.Need(".original", "backports", "PPSA01317", "default");
        string output = Path.Combine(Path.GetTempPath(), "ps5backport-dry-" + Guid.NewGuid().ToString("N"));
        var k = new Knowledge(Repo.Need("data"));
        var o = new BackportOptions { Firmware = "4", ReferenceDir = Repo.Need("firmware_db"), FakelibStore = Path.Combine(Repo.Root, "fakelibs"), DryRun = true };
        var dry = Pipeline.Backport(game, output, o, k, cancel: TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(output));
        Assert.All(dry.Modules, m => Assert.False(m.Written));
        Assert.Equal(21, dry.Modules.Sum(m => m.Fixed.Count + m.StubbedNow.Count));   // incl. the PS VR2 reprojection setter stub and scePsmlMfsrInit
        // with the exact 4.00 export list only one PSVR2 function is really missing (behind the sceHmd2Initialize check);
        // SetAllowPositionalReprojection is stubbed by its rule
        Assert.Equal(["sceHmd2ReprojectionSetParamWithBuffer"],
                     dry.Modules.SelectMany(m => m.Unknown).Select(u => new NameDb(Repo.Need(".original", "reference", "sce_symbols_new", "aerolib.csv")).Name(u.Nid)).Order(StringComparer.Ordinal));
        Assert.All(dry.Modules.SelectMany(m => m.Unknown), u => Assert.Equal(Imports.Missing, u.Status));
        Assert.All(dry.Modules.Where(m => m.Error.Length == 0), m => Assert.NotEmpty(m.Statuses));
    }
}

/// <summary>Battlefield 6 launches a second executable (SP/&lt;name&gt;_Ps5_retail.elf); it must be found like a module.</summary>
public class ExtraExecutableTests
{
    [Fact]
    public void ElfExecutable_IsFound()
    {
        string game = Repo.Need(".original", "backports", "PPSA19534", "default");
        var actual = GameScan.FindModules(game);
        Assert.Contains(Path.Combine("SP", "BattlefieldGame.Main_Ps5_retail.elf"), actual);
        Assert.Contains("eboot.bin", actual);
        Golden.Match(Path.Combine("modules", "PPSA19534_default.txt"), string.Concat(actual.Select(m => m.Replace('\\', '/') + "\n")));
    }
}

/// <summary>A decrypted dump (plain ELF modules): the output is always a fake SELF.</summary>
public class DecryptedInputTests
{
    [Fact]
    public void PlainElf_IsWrittenAsFakeSelf()
    {
        string module = Repo.Need(".original", "backports", "PPSA24003", "decrypted", "AkDelay.prx");
        string stubs = Repo.Need(".original", "reference", "PS5-3.20_Libs");
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ps5backport-decrypted-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string src = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
            File.Copy(module, Path.Combine(src, "AkDelay.prx"));
            var result = Pipeline.Backport(src, Path.Combine(root, "cs"), new BackportOptions { Firmware = "4", ReferenceDir = stubs },
                                           new Knowledge(Repo.Need("data")), cancel: TestContext.Current.CancellationToken);
            Assert.Equal((Self.KindElf, true), (result.Modules[0].Container, result.Modules[0].Written));
            Assert.Equal(Self.KindPs4Self, Self.FileKind(Path.Combine(root, "cs", "AkDelay.prx")));
            Assert.DoesNotContain(result.ParamWarnings, w => w.Contains("plain ELF"));
            Golden.Match(Path.Combine("backport", "decrypted-akdelay.sha256"), Golden.Sha(File.ReadAllBytes(Path.Combine(root, "cs", "AkDelay.prx"))) + "\n");
        }
        finally { Directory.Delete(root, true); }
    }
}

/// <summary>The game's own fakelib/ is checked against data/known_builds.json too (EA SPORTS FC 27 ships the Sep 6 ampr_emu).</summary>
public class GameFakelibBuildTests
{
    [Fact]
    public void KnownBrokenGameFakelib_IsFlagged()
    {
        string ampr = Repo.Need(".original", "backports", "PPSA34015", "default", "fakelib", "libSceAmpr.sprx");
        string stubs = Repo.Need(".original", "reference", "PS5-3.20_Libs");
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ps5backport-ampr-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string src = Path.Combine(root, "game");
            Directory.CreateDirectory(Path.Combine(src, "fakelib"));
            File.Copy(ampr, Path.Combine(src, "fakelib", "libSceAmpr.sprx"));
            var result = Pipeline.Backport(src, Path.Combine(root, "cs"), new BackportOptions { Firmware = "4", ReferenceDir = stubs },
                                           new Knowledge(Repo.Need("data")), cancel: TestContext.Current.CancellationToken);
            Golden.Match(Path.Combine("backport", "fc27-ampr-warnings.txt"), Golden.Portable(string.Concat(result.ParamWarnings.Select(w => w + "\n")), (root, "<tmp>")));
            Assert.Contains(result.ParamWarnings, w => w.StartsWith("fakelib/libSceAmpr.sprx (libSceAmpr built Sep  6 2026) is a known-broken build"));
        }
        finally { Directory.Delete(root, true); }
    }
}

/// <summary>A fakelib set's app0/ files (k9.psp, the PSSR model fakelibs/4's FW 13.60 libScePsml reads from /app0/k9.psp
/// on a PS5 Pro) are added only when the game uses that fakelib.</summary>
public class App0FileTests
{
    [Fact]
    public void App0File_NotAddedWithoutPsml()
    {
        string set = Repo.Need("fakelibs", "4");
        Repo.Need("fakelibs", "4", "app0", "k9.psp");
        string module = Repo.Need(".original", "backports", "PPSA24003", "decrypted", "AkDelay.prx");
        string stubs = Repo.Need(".original", "reference", "PS5-3.20_Libs");
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ps5backport-app0-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string src = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
            File.Copy(module, Path.Combine(src, "AkDelay.prx"));
            var result = Pipeline.Backport(src, Path.Combine(root, "cs"), new BackportOptions { Firmware = "4", ReferenceDir = stubs, ExtraFakelib = set },
                                           new Knowledge(Repo.Need("data")), cancel: TestContext.Current.CancellationToken);
            Assert.Empty(result.App0Files);
            Assert.False(File.Exists(Path.Combine(root, "cs", "k9.psp")));
        }
        finally { Directory.Delete(root, true); }
    }
}

/// <summary>sce_sys/about/right.sprx is not needed to start a game: an unreadable one is left out with a warning, not a module error.</summary>
public class RightsModuleTests
{
    [Fact]
    public void UnreadableRightsModule_IsAWarning()
    {
        string right = Repo.Need(".original", "backports", "PPSA01317", "4xx+", "USA", "sce_sys", "about", "right.sprx");
        string game = Path.Combine(Path.GetTempPath(), "ps5backport-rights-" + Guid.NewGuid().ToString("N"));
        string output = game + "-out";
        Directory.CreateDirectory(Path.Combine(game, "sce_sys", "about"));
        try
        {
            File.WriteAllBytes(Path.Combine(game, "sce_sys", "about", "right.sprx"), new byte[12512]);   // UFC 6's tested backport
            File.Copy(right, Path.Combine(game, "right.sprx"));                                         // one readable module
            var o = new BackportOptions { Firmware = "4", ReferenceDir = Repo.Need(".original", "reference", "PS5-3.20_Libs") };
            var r = Pipeline.Backport(game, output, o, new Knowledge(Repo.Need("data")), cancel: TestContext.Current.CancellationToken);
            Assert.DoesNotContain(r.Modules, m => m.Error.Length > 0);
            Assert.DoesNotContain(r.Modules, m => m.Path == "sce_sys/about/right.sprx");
            Assert.Contains(r.ParamWarnings, w => w.StartsWith("sce_sys/about/right.sprx could not be read"));
        }
        finally
        {
            Directory.Delete(game, true);
            if (Directory.Exists(output))
                Directory.Delete(output, true);
        }
    }
}
