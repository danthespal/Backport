using Ps5Backport.App;
using Ps5Backport.App.ViewModels;

namespace Ps5Backport.Tests;

/// <summary>Phase 6.6: remembered settings and how the main view model applies them.</summary>
public class AppSettingsTests
{
    static string TempFile() => Path.Combine(Path.GetTempPath(), "ps5backport-settings-" + Guid.NewGuid().ToString("N"), "settings.json");

    [Fact]
    public void Missing_GivesDefaults()
    {
        var s = AppSettings.Load(TempFile(), out string? problem);
        Assert.Null(problem);
        Assert.Equal(("4", true, 200.0), (s.Firmware, s.KnownFixes, s.LogHeight));
    }

    [Fact]
    public void SaveLoad_RoundTrips()
    {
        string path = TempFile();
        try
        {
            var s = new AppSettings { Firmware = "4.03", CandidateFixes = true, WindowWidth = 1400, WindowHeight = 900, WindowX = 10, WindowY = 20, LogHeight = 250 };
            s.Remember(@"D:\games\PPSA01317");
            s.Save(path);
            var back = AppSettings.Load(path, out string? problem);
            Assert.Null(problem);
            Assert.Equal(("4.03", true, 1400.0, 900.0, 10, 20, 250.0), (back.Firmware, back.CandidateFixes, back.WindowWidth, back.WindowHeight, back.WindowX, back.WindowY, back.LogHeight));
            Assert.Equal(@"D:\games\PPSA01317", back.LastGame);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    /// <summary>Code-segment extension is on by default; a value saved under the old key (off by default) is ignored.</summary>
    [Fact]
    public void ExtendText_DefaultsOn_AndOldKeyIsIgnored()
    {
        Assert.True(new AppSettings().ExtendText);
        string path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, "{ \"ExtendText\": false }");
            Assert.True(AppSettings.Load(path, out _).ExtendText);
            new AppSettings { ExtendText = false }.Save(path);
            Assert.False(AppSettings.Load(path, out _).ExtendText);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void Corrupt_GivesDefaultsAndSaysWhy()
    {
        string path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, "{ not json");
            var s = AppSettings.Load(path, out string? problem);
            Assert.NotNull(problem);
            Assert.Equal("4", s.Firmware);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void Remember_KeepsTenNewestWithoutDuplicates()
    {
        var s = new AppSettings();
        for (int i = 0; i < 12; i++)
            s.Remember($"game{i}");
        s.Remember("GAME5");                        // same folder, different case: moves to the front
        Assert.Equal(AppSettings.MaxRecentGames, s.RecentGames.Count);
        Assert.Equal("GAME5", s.RecentGames[0]);
        Assert.Single(s.RecentGames, g => g.Equals("game5", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ViewModel_AppliesAndCapturesSettings()
    {
        var s = new AppSettings { Firmware = "4.03", CandidateFixes = true, KnownFixes = false, PatchParam = true };
        var vm = new MainViewModel(s);
        Assert.Equal(("4.03", true, false, true), (vm.SelectedFirmware, vm.CandidateFixes, vm.KnownFixes, vm.PatchParam));
        vm.SelectedFirmware = "5";
        vm.ExtendText = false;
        vm.CaptureSettings();
        Assert.Equal(("5", false), (s.Firmware, s.ExtendText));
    }

    [Fact]
    public void SettingsPage_PathOverrideAndReset()
    {
        string store = Path.Combine(Path.GetTempPath(), "ps5backport-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(store, "6.50"));
        try
        {
            var s = new AppSettings();
            var vm = new MainViewModel(s) { Page = AppPage.Settings };
            vm.SetPath(PathKind.FakelibStore, store);
            Assert.Equal(store, s.FakelibStoreDir);
            var row = Assert.Single(vm.PathSettings, p => p.Kind == PathKind.FakelibStore);
            Assert.Equal((store, true, "custom"), (row.Path, row.IsCustom, row.Source));
            Assert.Contains("6.50", vm.Firmwares);                  // the new store's set reaches the picker

            vm.ResetPathCommand.Execute(row);
            Assert.Null(s.FakelibStoreDir);
            Assert.False(vm.PathSettings.Single(p => p.Kind == PathKind.FakelibStore).IsCustom);
        }
        finally { Directory.Delete(store, true); }
    }

    [Fact]
    public void SettingsPage_MissingFolders_Warn()
    {
        string missing = Path.Combine(Path.GetTempPath(), "ps5backport-missing-" + Guid.NewGuid().ToString("N"));
        var vm = new MainViewModel(new AppSettings { FirmwareDbDir = missing, FakelibStoreDir = missing, AerolibPath = missing }) { Page = AppPage.Settings };
        Assert.Equal(3, vm.PathSettings.Count);
        Assert.All(vm.PathSettings, p => Assert.Equal(("warn", true), (p.Tone, p.IsCustom)));
        Assert.Equal("No fakelib set for FW 4", vm.FakelibText);
    }

    [Fact]
    public void SettingsPage_ForgetRecentGames()
    {
        var s = new AppSettings();
        s.Remember("game1");
        var vm = new MainViewModel(s);
        Assert.StartsWith("1 remembered", vm.RecentGamesText);
        vm.ClearRecentGamesCommand.Execute(null);
        Assert.Equal((0, null), (s.RecentGames.Count, s.LastGame));
        Assert.Equal("None remembered", vm.RecentGamesText);
    }

    [Fact]
    public void ViewModel_BadFirmwareInSettings_FallsBackTo4()
    {
        var vm = new MainViewModel(new AppSettings { Firmware = "nonsense" });
        Assert.Equal("4", vm.SelectedFirmware);
    }
}

/// <summary>Phase 7.1 / 7.3: the Fakelib store page.</summary>
public class StorePageTests
{
    static string TempStore() => Path.Combine(Path.GetTempPath(), "ps5backport-store-" + Guid.NewGuid().ToString("N"));

    static MainViewModel Open(string store) =>
        new(new AppSettings { FakelibStoreDir = store, FirmwareDbDir = Path.Combine(Repo.Root, "firmware_db") }) { Page = AppPage.Store };

    [Fact]
    public async Task AddFiles_VerifyAndTamper()
    {
        string[] files = Directory.GetFiles(Repo.Need("fakelibs", "4.03"), "*.sprx");
        string store = TempStore();
        try
        {
            var vm = Open(store);
            Assert.Empty(vm.Store.Sets);
            vm.Store.PickFiles = (_, _, _, _) => Task.FromResult(files);
            vm.Store.AddFirmware = "4.03";
            vm.Store.AddSource = "test copy";
            await vm.Store.AddFilesCommand.ExecuteAsync(null);

            var set = Assert.Single(vm.Store.Sets);
            Assert.Equal(("4.03", files.Length, 0), (set.Name, set.Libraries, set.Problems));
            Assert.Equal(set, vm.Store.SelectedSet);
            Assert.All(vm.Store.Files, f => Assert.Equal(("hash ok", "test copy"), (f.State, f.Source)));
            Assert.Contains("4.03", vm.Firmwares);
            // identical manifest entries to the real store, apart from the source note
            var ampr = vm.Store.Files.Single(f => f.Module == "libSceAmpr");
            Assert.Equal(("Aug 24 2026", "2.00"), (ampr.BuildDate, ampr.Sdk));

            File.AppendAllText(Path.Combine(store, "4.03", "libScePlayGo.sprx"), "x");
            vm.Store.Refresh();
            Assert.Equal(1, vm.Store.Sets.Single().Problems);
            Assert.Equal(("checksum differs from manifest", "warn"),
                         vm.Store.Files.Where(f => f.Module == "libScePlayGo").Select(f => (f.State, f.Tone)).Single());
        }
        finally { if (Directory.Exists(store)) Directory.Delete(store, true); }
    }

    [Fact]
    public async Task Check_403SetAgainst403_AllResolved()
    {
        Repo.Need("fakelibs", "4.03");
        var vm = Open(Path.Combine(Repo.Root, "fakelibs"));
        vm.Store.SelectedSet = vm.Store.Sets.Single(s => s.Name == "4.03");
        Assert.Equal("4.03", vm.Store.CheckFirmware);           // defaults to the set's own firmware
        await vm.Store.CheckCommand.ExecuteAsync(null);
        Assert.Contains("every import resolved", vm.Store.CheckSummary);
        Assert.All(vm.Store.Files, f => Assert.Equal("ok", f.CheckTone));
    }

    [Fact]
    public void ApplyBps_RebuildsTheStored7xxAgcByteForByte()
    {
        string patch = Repo.Need(".original", "reference", "BackPork", "patches", "7xx", "libSceAgc.bps");
        string input = Repo.Need(".original", ".PS5OriginalLibs", "Firmware 10.01", "system", "common", "lib", "libSceAgc.sprx");
        string stored = Repo.Need("fakelibs", "7", "libSceAgc.sprx");
        string store = TempStore();
        try
        {
            var vm = Open(store);
            vm.Store.BpsPatch = patch;
            vm.Store.BpsInput = input;
            vm.Store.AddFirmware = "7";
            vm.Store.ApplyBpsCommand.Execute(null);
            string built = Path.Combine(store, "7", "libSceAgc.sprx");
            Assert.Equal(File.ReadAllBytes(stored), File.ReadAllBytes(built));
            var row = Assert.Single(vm.Store.Files);
            Assert.Equal("BPS patch libSceAgc.bps applied to libSceAgc.sprx, fake-signed", row.Source);
            Assert.Equal(0, vm.Log.ErrorCount);
        }
        finally { if (Directory.Exists(store)) Directory.Delete(store, true); }
    }

    [Fact]
    public void ApplyBps_WrongLibrary_LogsErrorAndAddsNothing()
    {
        string patch = Repo.Need(".original", "reference", "BackPork", "patches", "7xx", "libSceAgc.bps");
        string wrong = Repo.Need("fakelibs", "4", "libScePlayGo.sprx");
        string store = TempStore();
        try
        {
            var vm = Open(store);
            vm.Store.BpsPatch = patch;
            vm.Store.BpsInput = wrong;
            vm.Store.AddFirmware = "7";
            vm.Store.ApplyBpsCommand.Execute(null);
            Assert.Equal(1, vm.Log.ErrorCount);
            Assert.Empty(vm.Store.Sets);
        }
        finally { if (Directory.Exists(store)) Directory.Delete(store, true); }
    }
}

/// <summary>Phase 7.2: the Firmware database page.</summary>
public class FirmwareDbPageTests
{
    [Fact]
    public async Task Compare_650With701_ShowsTrinityModeAdded()
    {
        string db = Repo.Need("firmware_db", "7.01.json");
        var vm = new MainViewModel(new AppSettings { FirmwareDbDir = Path.GetDirectoryName(db) }) { Page = AppPage.FirmwareDb };
        await vm.PageLoad;
        Assert.Equal(Directory.GetFiles(Path.GetDirectoryName(db)!, "*.json").Length, vm.Database.Firmwares.Count);

        vm.Database.Selected = vm.Database.Firmwares.Single(f => f.Firmware == "6.50");
        Assert.Equal("7.01", vm.Database.CompareWith?.Firmware);         // the next newer firmware by default
        string trinity = Ps5Backport.Core.Nid.Compute("sceKernelIsTrinityMode");
        vm.Database.Search = trinity;
        var row = Assert.Single(vm.Database.Modules);
        Assert.Contains(trinity, row.Added);
        Assert.Contains(vm.Database.Changes, c => c.Nid == trinity && c.Change == "added");

        // same list on both sides: nothing changed
        vm.Database.Search = "";
        vm.Database.CompareWith = vm.Database.Selected;
        Assert.Empty(vm.Database.Modules);
        Assert.Contains("+0 added, −0 removed", vm.Database.CompareSummary);
    }

    [Fact]
    public async Task Import_Genstub_WritesListAndSelectsIt()
    {
        string root = Path.Combine(Path.GetTempPath(), "ps5backport-fwdb-" + Guid.NewGuid().ToString("N"));
        string stubs = Path.Combine(root, "stubs"), db = Path.Combine(root, "db");
        Directory.CreateDirectory(stubs);
        try
        {
            File.WriteAllText(Path.Combine(stubs, "libkernel.c"), "void *f = sprx_dlsym(h, \"vNe1w4diLCs\");\n");
            var vm = new MainViewModel(new AppSettings { FirmwareDbDir = db });
            vm.PickFolder = _ => Task.FromResult<string?>(stubs);
            vm.Database.ImportFirmware = "9.60";
            vm.Database.ImportGenstub = true;
            vm.Database.ImportSource = "";
            await vm.Database.ImportCommand.ExecuteAsync(null);
            Assert.Equal(1, vm.Log.ErrorCount);                            // a source note is required
            vm.Database.ImportSource = "test stubs";
            await vm.Database.ImportCommand.ExecuteAsync(null);

            Assert.True(File.Exists(Path.Combine(db, "9.60.json")));
            var row = Assert.Single(vm.Database.Firmwares);
            Assert.Equal(("9.60", row), (row.Firmware, vm.Database.Selected));
            Assert.Equal(("test stubs", false, 1), (row.Db.Source, row.Db.DataSymbols, row.Db.Nids.Count));
        }
        finally { Directory.Delete(root, true); }
    }
}

/// <summary>Phase 7.4 / 7.5: the Tools page.</summary>
public class ToolsPageTests
{
    [Fact]
    public async Task SelfFiles_UnwrapThenSign_RoundTripsWithoutOverwriting()
    {
        string sample = Repo.Need("fakelibs", "4", "libScePlayGo.sprx");     // PS4-style fake SELF
        string dir = Path.Combine(Path.GetTempPath(), "ps5backport-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string self = Path.Combine(dir, "libScePlayGo.sprx");
            File.Copy(sample, self);
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "not a module");
            var vm = new MainViewModel(new AppSettings());
            await vm.Tools.Add([self, Path.Combine(dir, "notes.txt"), self]);   // duplicates are ignored
            Assert.Equal(["PS4 fake SELF", "not a SELF or ELF"], vm.Tools.Files.Select(f => f.Kind));
            Assert.Equal("libScePlayGo", vm.Tools.Files[0].Module);

            await vm.Tools.UnwrapCommand.ExecuteAsync(null);
            string elf = self + ".elf";
            Assert.Equal(Ps5Backport.Core.Self.Unwrap(File.ReadAllBytes(self)), File.ReadAllBytes(elf));
            Assert.Contains(vm.Tools.Files, f => f.Path == elf && f.IsElf);

            // signing libScePlayGo.sprx.elf would write libScePlayGo.sprx, which exists: refused
            await vm.Tools.SignCommand.ExecuteAsync(null);
            Assert.Equal(1, vm.Log.ErrorCount);

            vm.Tools.OutputFolder = Path.Combine(dir, "out");
            Directory.CreateDirectory(vm.Tools.OutputFolder);
            await vm.Tools.SignCommand.ExecuteAsync(null);
            string signed = Path.Combine(dir, "out", "libScePlayGo.sprx");
            Assert.Equal(Ps5Backport.Core.Self.Sign(File.ReadAllBytes(elf)), File.ReadAllBytes(signed));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task NidLookup_BothDirectionsWithKnowledge()
    {
        Repo.Need("firmware_db", "7.01.json");
        var vm = new MainViewModel(new AppSettings { FirmwareDbDir = Path.Combine(Repo.Root, "firmware_db") }) { Page = AppPage.Tools };
        await vm.PageLoad;
        string trinity = Ps5Backport.Core.Nid.Compute("sceKernelIsTrinityMode");
        vm.Tools.NidQuery = $"sceKernelIsTrinityMode La9uyZv4Kvw {trinity}#B#C";
        Assert.Equal(3, vm.Tools.NidResults.Count);
        var byName = vm.Tools.NidResults[0];
        Assert.Equal(trinity, byName.Nid);
        Assert.Contains("from FW 7.01", byName.Known);
        Assert.Contains("known fix: stub (proven)", byName.Known);
        Assert.Equal("La9uyZv4Kvw", vm.Tools.NidResults[1].Nid);              // a NID only the knowledge base knows
        Assert.Contains("known fix", vm.Tools.NidResults[1].Known);
        var symbol = vm.Tools.NidResults[2];
        Assert.Equal(("sceKernelIsTrinityMode", trinity), (symbol.Name, symbol.Nid));
        Assert.Contains("library 1, module 2", symbol.Known);
    }
}

/// <summary>Phase 6.1: dropping a game on the Backport page.</summary>
public class DropGameTests
{
    [Fact]
    public void DropFolderOrFileInIt_OpensTheGame()
    {
        string game = Repo.Need(".original", "backports", "PPSA01317", "decrypted");
        var vm = new MainViewModel(new AppSettings());
        vm.DropGame(Path.Combine(game, "eboot.bin"));
        Assert.Equal(("PPSA01317", Path.GetFullPath(game)), (vm.Game?.TitleId, Path.GetFullPath(vm.Game!.Root)));

        var other = new MainViewModel(new AppSettings());
        other.DropGame(game);
        Assert.Equal("PPSA01317", other.Game?.TitleId);
    }
}

/// <summary>Audit fix: a new firmware database folder reaches the store's check picker and the Firmware database page.</summary>
public class DatabaseFolderChangeTests
{
    [Fact]
    public async Task ChangingTheFolder_ReloadsBothPages()
    {
        string real = Repo.Need("firmware_db");
        string other = Path.Combine(Path.GetTempPath(), "ps5backport-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        try
        {
            File.Copy(Path.Combine(real, "4.03.json"), Path.Combine(other, "4.03.json"));
            var vm = new MainViewModel(new AppSettings { FirmwareDbDir = real, FakelibStoreDir = Path.Combine(Repo.Root, "fakelibs") });
            vm.Page = AppPage.FirmwareDb;
            await vm.PageLoad;
            Assert.True(vm.Database.Firmwares.Count > 1);
            vm.Page = AppPage.Store;
            Assert.True(vm.Store.CheckFirmwares.Count > 1);

            vm.Page = AppPage.Settings;                                   // the folder is changed on the Settings page
            vm.SetPath(PathKind.FirmwareDb, other);
            vm.Page = AppPage.Store;
            Assert.Equal(["4.03"], vm.Store.CheckFirmwares);
            vm.Page = AppPage.FirmwareDb;
            await vm.PageLoad;
            Assert.Equal("4.03", Assert.Single(vm.Database.Firmwares).Firmware);
        }
        finally { Directory.Delete(other, true); }
    }
}

/// <summary>Bug fix: Analyze and Cook stayed disabled after a game was opened (the buttons were never told to re-check).</summary>
public class RunButtonsTests
{
    [Fact]
    public void OpeningAGame_EnablesAnalyzeAndCook()
    {
        string game = Repo.Need(".original", "backports", "PPSA01317", "decrypted");
        var vm = new MainViewModel(new AppSettings());
        Assert.False(vm.AnalyzeCommand.CanExecute(null));
        int raised = 0;
        vm.AnalyzeCommand.CanExecuteChanged += (_, _) => raised++;
        vm.CookCommand.CanExecuteChanged += (_, _) => raised++;
        vm.LoadGame(game);
        Assert.Equal(2, raised);
        Assert.True(vm.AnalyzeCommand.CanExecute(null));
        Assert.True(vm.CookCommand.CanExecute(null));
    }
}

/// <summary>Optional patches on the Overview tab: listed from data/fixes.json, passed to the run, reset per game.</summary>
public class OptionalPatchTests
{
    [Fact]
    public async Task EnabledOption_ReachesTheRun_AndResetsForTheNextGame()
    {
        string game = Repo.Need(".original", "backports", "PPSA28183", "default");
        var vm = new MainViewModel(new AppSettings { FirmwareDbDir = Repo.Need("firmware_db"), FakelibStoreDir = Path.Combine(Repo.Root, "fakelibs") });
        Assert.Equal(new Core.Knowledge(Repo.Need("data")).Options.Keys, vm.PatchOptions.Select(o => o.Key));
        vm.LoadGame(game);
        vm.SelectedFirmware = "4.50";
        vm.PatchOptions.Single(o => o.Key == "force_pro").IsEnabled = true;
        await vm.AnalyzeCommand.ExecuteAsync(null);
        Assert.Equal(["force_pro"], vm.Result!.Options);
        Assert.Equal(0, vm.UnresolvedCount);
        Assert.Contains(vm.Warnings, w => w.StartsWith("option Force PS5 Pro mode:"));

        vm.LoadGame(game);
        Assert.All(vm.PatchOptions, o => Assert.False(o.IsEnabled));
    }

    [Fact]
    public async Task AmprDebugLogging_ReplacesTheAmprFakelib_AndResetsForTheNextGame()
    {
        string game = Repo.Need(".original", "backports", "PPSA28183", "default");
        var vm = new MainViewModel(new AppSettings { FirmwareDbDir = Repo.Need("firmware_db"), FakelibStoreDir = Path.Combine(Repo.Root, "fakelibs") });
        vm.LoadGame(game);
        vm.SelectedFirmware = "4.50";
        vm.AmprDebugLogging = true;
        await vm.AnalyzeCommand.ExecuteAsync(null);

        Assert.Contains(vm.Warnings, w => w.StartsWith("AMPR debug logging enabled:"));
        // the variant replaces the game's own fakelib/libSceAmpr.sprx (one module, from the store)
        Assert.True(vm.Result!.Modules.Single(m => m.Path == "fakelib/libSceAmpr.sprx").Added);

        vm.LoadGame(game);
        Assert.False(vm.AmprDebugLogging);
    }
}

/// <summary>A game importing the PS VR2 libraries gets the header badge and the warning; loading another game clears it.</summary>
public class Psvr2BadgeTests
{
    [Fact]
    public async Task VrGame_ShowsBadgeAndWarning_NonVrGameDoesNot()
    {
        var vm = new MainViewModel(new AppSettings { FirmwareDbDir = Repo.Need("firmware_db"), FakelibStoreDir = Path.Combine(Repo.Root, "fakelibs") });
        vm.LoadGame(Repo.Need(".original", "backports", "PPSA05666", "default"));
        vm.SelectedFirmware = "4";
        await vm.AnalyzeCommand.ExecuteAsync(null);
        Assert.True(vm.HasPsvr2);
        Assert.Contains("libSceHmd2 x22 (eboot.bin)", vm.Psvr2Text);
        Assert.Single(vm.Warnings, w => w.StartsWith("uses PS VR2 ("));

        vm.LoadGame(Repo.Need(".original", "backports", "PPSA36082", "default"));
        Assert.False(vm.HasPsvr2);
        await vm.AnalyzeCommand.ExecuteAsync(null);
        Assert.False(vm.HasPsvr2);
    }
}
