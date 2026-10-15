// All regression tests and shared synthetic fixtures live here. Verification projects select
// their linked subset with BACKPORT_SYNTHETIC_ONLY or BACKPORT_PARSER_TESTS_ONLY.

#if !BACKPORT_SYNTHETIC_ONLY && !BACKPORT_PARSER_TESTS_ONLY
#region AmprIndexTests
namespace Ps5Backport.Tests
{
    using System.Buffers.Binary;
    using System.Text;
    using Ps5Backport.Core;

    /// <summary>AMPRIDX3 output follows the upstream format and represents the mounted game plus overlay view.</summary>
    public sealed class AmprIndexTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "ps5backport-ampr-index-" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void Build_MergesOverlayAndOmitsGeneratedOrPrivateFiles()
        {
            string game = Path.Combine(root, "game");
            string overlay = Path.Combine(root, "overlay");
            Directory.CreateDirectory(Path.Combine(game, "data"));
            Directory.CreateDirectory(overlay);
            File.WriteAllBytes(Path.Combine(game, "data", "base.bin"), [1, 2]);
            File.WriteAllBytes(Path.Combine(game, "same.bin"), [1]);
            File.WriteAllBytes(Path.Combine(game, "ampr_emu.index"), [0]);
            File.WriteAllBytes(Path.Combine(overlay, "same.bin"), [1, 2, 3, 4]);
            File.WriteAllText(Path.Combine(overlay, "backport_report.txt"), "private");
            File.WriteAllText(Path.Combine(overlay, "ampr_emu.log"), "diagnostic");

            AmprIndexResult result = AmprIndex.Build(game, overlay, cancel: TestContext.Current.CancellationToken);

            Assert.Equal(2, result.Entries);
            Assert.Equal(1, result.OverlayEntries);
            byte[] data = File.ReadAllBytes(result.Path);
            Assert.Equal("AMPRIDX3", Encoding.ASCII.GetString(data, 0, 8));
            Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)));
            Assert.Equal(24u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12)));
            Assert.Equal(2UL, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(16)));

            ulong pathBytes = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(24));
            int pathsOffset = checked(48 + 2 * 24);
            string paths = Encoding.UTF8.GetString(data, pathsOffset, checked((int)pathBytes));
            Assert.Contains("/app0/data/base.bin\0", paths);
            Assert.Contains("/app0/same.bin\0", paths);
            Assert.DoesNotContain("backport_report", paths);
            Assert.DoesNotContain("ampr_emu.index", paths);
            Assert.DoesNotContain("ampr_emu.log", paths);

            ulong firstSize = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(48 + 8));
            ulong secondSize = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(48 + 24 + 8));
            Assert.Equal(new ulong[] { 2, 4 }, new[] { firstSize, secondSize });
        }

        public void Dispose()
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
#endregion
#endif

#if !BACKPORT_SYNTHETIC_ONLY && !BACKPORT_PARSER_TESTS_ONLY
#region AppTests
namespace Ps5Backport.Tests
{
    using Ps5Backport.App;
    using Ps5Backport.App.ViewModels;

    /// <summary>Phase 6.6: remembered settings and how the main view model applies them.</summary>
    public class AppSettingsTests
    {
        static string TempFile() => Path.Combine(Path.GetTempPath(), "ps5backport-settings-" + Guid.NewGuid().ToString("N"), "settings.json");

        [Fact]
        public void Missing_GivesDefaults()
        {
            var s = AppSettings.Load(TempFile(), out string? problem);
            Assert.Null(problem);
            Assert.Equal(("4", true, 140.0), (s.Firmware, s.KnownFixes, s.LogHeight));
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
            Assert.Equal("No compatibility libraries for FW 4", vm.FakelibText);
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
    [Trait("Lane", "Private")]
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

        /// <summary>An unreadable choice (here a folder: UnauthorizedAccessException on Windows) is reported in the
        /// panel; an exception escaping an async command would end the app.</summary>
        [Fact]
        public async Task ChooseBpsPatch_UnreadableFile_IsReportedNotThrown()
        {
            string store = TempStore();
            try
            {
                var vm = Open(store);
                string folder = Directory.CreateDirectory(Path.Combine(store, "not-a-file.bps")).FullName;
                vm.Store.PickFiles = (_, _, _, _) => Task.FromResult(new[] { folder });
                await vm.Store.ChooseBpsPatchCommand.ExecuteAsync(null);
                Assert.StartsWith("Not a usable BPS patch:", vm.Store.BpsInfo);
            }
            finally { if (Directory.Exists(store)) Directory.Delete(store, true); }
        }

        /// <summary>A corrupt manifest.json (JsonException) is logged by Add and Apply BPS like any other store
        /// error; escaping a command would end the app.</summary>
        [Fact]
        public async Task CorruptManifest_AddAndApplyBps_LogErrors()
        {
            string[] files = Directory.GetFiles(Repo.Need("fakelibs", "4.03"), "*.sprx").Take(1).ToArray();
            string store = TempStore();
            try
            {
                string set = Directory.CreateDirectory(Path.Combine(store, "4.03")).FullName;
                File.Copy(files[0], Path.Combine(set, Path.GetFileName(files[0])));
                File.WriteAllText(Path.Combine(set, Ps5Backport.Core.FakelibStore.ManifestName), "{");
                var vm = Open(store);
                int errors = vm.Log.ErrorCount;
                vm.Store.PickFiles = (_, _, _, _) => Task.FromResult(files);
                vm.Store.AddFirmware = "4.03";
                await vm.Store.AddFilesCommand.ExecuteAsync(null);
                Assert.Equal(errors + 1, vm.Log.ErrorCount);
                vm.Store.BpsPatch = Repo.Need(".original", "reference", "BackPork", "patches", "7xx", "libSceAgc.bps");
                vm.Store.BpsInput = Repo.Need(".original", ".PS5OriginalLibs", "Firmware 10.01", "system", "common", "lib", "libSceAgc.sprx");
                vm.Store.ApplyBpsCommand.Execute(null);
                Assert.Equal(errors + 2, vm.Log.ErrorCount);
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
        public async Task Compare_650With700_ShowsTrinityModeAdded()
        {
            string db = Repo.Need("firmware_db", "7.00.json");
            var vm = new MainViewModel(new AppSettings { FirmwareDbDir = Path.GetDirectoryName(db) }) { Page = AppPage.FirmwareDb };
            await vm.PageLoad;
            Assert.Equal(Directory.GetFiles(Path.GetDirectoryName(db)!, "*.json").Length, vm.Database.Firmwares.Count);

            vm.Database.Selected = vm.Database.Firmwares.Single(f => f.Firmware == "6.50");
            Assert.Equal("7.00", vm.Database.CompareWith?.Firmware);         // the next newer firmware by default
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
    [Trait("Lane", "Private")]
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
            Repo.Need("firmware_db", "7.00.json");
            var vm = new MainViewModel(new AppSettings { FirmwareDbDir = Path.Combine(Repo.Root, "firmware_db") }) { Page = AppPage.Tools };
            await vm.PageLoad;
            string trinity = Ps5Backport.Core.Nid.Compute("sceKernelIsTrinityMode");
            vm.Tools.NidQuery = $"sceKernelIsTrinityMode La9uyZv4Kvw {trinity}#B#C";
            Assert.Equal(3, vm.Tools.NidResults.Count);
            var byName = vm.Tools.NidResults[0];
            Assert.Equal(trinity, byName.Nid);
            Assert.Contains("from FW 7.00", byName.Known);
            Assert.Contains("known fix: stub (proven)", byName.Known);
            Assert.Equal("La9uyZv4Kvw", vm.Tools.NidResults[1].Nid);              // a NID only the knowledge base knows
            Assert.Contains("known fix", vm.Tools.NidResults[1].Known);
            var symbol = vm.Tools.NidResults[2];
            Assert.Equal(("sceKernelIsTrinityMode", trinity), (symbol.Name, symbol.Nid));
            Assert.Contains("library 1, module 2", symbol.Known);
        }
    }

    /// <summary>Phase 6.1: dropping a game on the Backport page.</summary>
    [Trait("Lane", "Private")]
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
    [Trait("Lane", "Private")]
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
    [Trait("Lane", "Private")]
    public class OptionalPatchTests
    {
        /// <summary>The grouped panel is built from data/fixes.json: every option once, in its group, groups in
        /// option_groups order; search filters across groups and drops empty ones.</summary>
        [Fact]
        public void PatchGroups_ShowEveryOptionOnce_InDataOrder_AndFilter()
        {
            var k = new Core.Knowledge(Repo.Need("data"));
            var vm = new MainViewModel(new AppSettings());
            var groups = vm.VisiblePatchGroups;
            Assert.Equal(k.Options.Keys.Order(StringComparer.Ordinal), groups.SelectMany(g => g.Options).Select(o => o.Key).Order(StringComparer.Ordinal));
            Assert.Equal(k.OptionGroups.Where(name => k.Options.Values.Any(o => o.Group == name)), groups.Select(g => g.Name));
            Assert.All(groups, g => Assert.All(g.Options, o => Assert.Equal(g.Name, o.Group)));
            Assert.All(k.Options.Values, o => Assert.False(string.IsNullOrWhiteSpace(o.Group)));

            vm.PatchSearch = "speaker";
            Assert.All(vm.VisiblePatchGroups, g => Assert.NotEmpty(g.Options));
            Assert.Equal(vm.PatchOptions.Count(o => o.Name.Contains("speaker", StringComparison.OrdinalIgnoreCase)
                                                    || o.Description.Contains("speaker", StringComparison.OrdinalIgnoreCase)),
                         vm.VisiblePatchGroups.Sum(g => g.Options.Count));
            Assert.Contains(vm.VisiblePatchGroups, g => g.Options.Any(o => o.Key == "audio_speaker_compat"));
            vm.PatchSearch = "";

            var port = vm.PatchOptions.Single(o => o.Key == "audio_port_compat");
            port.SelectCommand.Execute(null);
            Assert.Same(port, vm.SelectedPatchOption);
            Assert.Same(port, Assert.Single(vm.PatchOptions, o => o.IsSelected));

            Assert.False(vm.IsBootNoticeEnabled);
            vm.PatchOptions.Single(o => o.Key == "boot_notice").IsEnabled = true;
            Assert.True(vm.IsBootNoticeEnabled);
        }

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
            // the probe ran: every row has a verdict; AC's single pad call passes "1 or 3", so nothing was pre-selected
            Assert.All(vm.PatchOptions, o => Assert.True(o.HasVerdict));
            Assert.Equal("applicable", vm.PatchOptions.Single(o => o.Key == "pad_vibration_compat").Verdict);
            Assert.Contains("probe:", vm.PatchOptionsSummary);
            Assert.False(vm.IsAnalysisStale);

            vm.LoadGame(game);
            Assert.All(vm.PatchOptions, o => Assert.False(o.IsEnabled));
            Assert.All(vm.PatchOptions, o => Assert.False(o.HasVerdict));
            Assert.DoesNotContain("probe:", vm.PatchOptionsSummary);
        }

        /// <summary>Analyze probes the game first and pre-selects only a "needed" option; the pre-selection is part of the
        /// analysis (not stale), appears in the result and the log, and not-applicable rows are dimmed.</summary>
        [Fact]
        public async Task Analyze_PreselectsNeededOptions_FromTheProbe()
        {
            string root = Path.Combine(Path.GetTempPath(), "ps5backport-gui-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
                string reference = Directory.CreateDirectory(Path.Combine(root, "reference")).FullName;
                const string pad = "W2G-yoyMF5U";
                byte[] module = SyntheticBinary.Elf(imports: true);
                System.Text.Encoding.ASCII.GetBytes(pad + "#A#A\0").CopyTo(module, 0x201);
                SyntheticBinary.W64(module, 0x130, Core.SceElf.DtJmpRel);
                SyntheticBinary.W64(module, 0x140, Core.SceElf.DtPltRelSz);
                SyntheticBinary.W64(module, 0x480, 0x426);
                Convert.FromHexString("FF255A0000006800000000").CopyTo(module, 0x420);
                module[0x380] = Core.Patching.Ret;
                module.AsSpan(0x381, 0x1f).Fill(Core.Patching.Int3);
                Convert.FromHexString("BE04000000E8").CopyTo(module, 0x3a0);                         // mov esi, 4; call PLT
                BitConverter.GetBytes(0x420 - (0x3a5 + 5)).CopyTo(module, 0x3a6);
                File.WriteAllBytes(Path.Combine(game, "eboot.bin"), module);
                Core.FirmwareDb.Save(new Core.FirmwareExports("4", "test", false, new() { ["libScePad"] = new() { pad } }), Path.Combine(reference, "4.00.json"));

                using var vm = new MainViewModel(new AppSettings { FirmwareDbDir = reference });
                vm.Game = new Core.GameInfo(game, "Synthetic game", "PPSA99999", "", "", "01.000.000", "13.60", "11.00", null, null);
                vm.SelectedFirmware = "4";
                vm.OutputFolder = Path.Combine(root, "out");
                Assert.All(vm.PatchOptions, o => Assert.False(o.HasVerdict));
                await vm.AnalyzeCommand.ExecuteAsync(null);
                Assert.True(vm.Result is not null, string.Join("\n", vm.Log.Visible.Select(e => e.ToString())));

                var padRow = vm.PatchOptions.Single(o => o.Key == "pad_vibration_compat");
                Assert.True(padRow.IsEnabled);
                Assert.Equal(("needed", "needed", true, 1.0), (padRow.Verdict, padRow.VerdictLabel, padRow.IsNeeded, padRow.RowOpacity));
                Assert.Contains("esi = 4 (above 3) at 1 of 1 call site(s), e.g. eboot.bin 0x3a5", padRow.Evidence);
                Assert.Equal(["pad_vibration_compat"], vm.PatchOptions.Where(o => o.IsEnabled).Select(o => o.Key));
                Assert.Equal(["pad_vibration_compat"], vm.Result!.Options);
                Assert.False(vm.IsAnalysisStale);
                var autoPro = vm.PatchOptions.Single(o => o.Key == "auto_pro");
                Assert.Equal(("not applicable", true, 0.55), (autoPro.VerdictLabel, autoPro.IsNotApplicable, autoPro.RowOpacity));
                Assert.Equal("console choice", vm.PatchOptions.Single(o => o.Key == "boot_notice").VerdictLabel);
                Assert.Contains("probe: 1 needed", vm.PatchOptionsSummary);
                Assert.Contains(vm.Log.Visible, e => e.Message.StartsWith("Probe: pre-selected Pad vibration mode compatibility"));
                Assert.Contains("probe needed", vm.ReportText);
            }
            finally { Directory.Delete(root, true); }
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
    [Trait("Lane", "Private")]
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
}
#endregion
#endif

#if !BACKPORT_SYNTHETIC_ONLY && !BACKPORT_PARSER_TESTS_ONLY
#region AuditTests
namespace Ps5Backport.Tests
{
    using System.Buffers.Binary;
    using Ps5Backport.App;
    using Ps5Backport.App.ViewModels;
    using Ps5Backport.Core;

    public class AuditTests
    {
        [Theory]
        [InlineData("11.00", "4", "Game SDK 11.00", 36.36363636363636)]
        [InlineData("4.03", "4.03", "Game SDK 4.03", 100)]
        [InlineData("", "4", "Game SDK unknown", 0)]
        public void GameVersionIndicator_UsesSdkInsteadOfDeclaredFirmware(string sdk, string target, string label, double percent)
        {
            using var vm = new MainViewModel(new AppSettings());
            vm.Game = new GameInfo("synthetic", "Synthetic game", "PPSA99999", "", "", "01.000.000", "13.60", sdk, null, null);
            vm.SelectedFirmware = target;
            Assert.Equal(label, vm.GameSdkText);
            Assert.Equal(percent, vm.GapPercent, 8);
        }

        static byte[] Elf()
        {
            var data = new byte[0x100];
            SceElf.Magic.CopyTo(data);
            data[4] = 2;
            data[5] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x10), SceElf.EtSceExec);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x12), 0x3E);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x14), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x20), 0x40);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x34), 0x40);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x36), 0x38);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x38), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x40), SceElf.PtLoad);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x44), SceElf.PfX);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x60), (ulong)data.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x68), (ulong)data.Length);
            return data;
        }

        static byte[] BpsBytes(byte[] body)
        {
            byte[] bytes = [.. "BPS1"u8, .. body, .. new byte[12]];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), Crc32.Compute(bytes.AsSpan(0, bytes.Length - 4)));
            return bytes;
        }

        [Theory]
        [InlineData(new byte[] { 0x80, 0x80, 0x81 })] // metadata crosses into the checksum
        [InlineData(new byte[] { 0x80, 0x81, 0x80, 0x81 })] // missing literal
        [InlineData(new byte[] { 0x80, 0x81, 0x80, 0x00 })] // unterminated action varint
        [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80 })] // action exceeds target
        [InlineData(new byte[] { 0x80, 0x81, 0x80 })] // actions do not fill target
        public void BpsParse_RejectsTruncatedOrInconsistentActions(byte[] body) =>
            Assert.Throws<InvalidDataException>(() => Bps.Parse(BpsBytes(body)));

        [Fact]
        public void BpsParse_RejectsVarintOverflow() =>
            Assert.Throws<InvalidDataException>(() => Bps.Parse(BpsBytes([.. Enumerable.Repeat((byte)0x7F, 11), 0x80])));

        [Fact]
        public void BpsApply_RefusesCopyFromUnwrittenTarget()
        {
            var patch = new Bps.Patch(0, 1, [], [new(Bps.Command.TargetCopy, 1, null, 0)], 0, Crc32.Compute(new byte[1]));
            Assert.Throws<InvalidDataException>(() => Bps.Apply(patch, []));
        }

        [Theory]
        [InlineData(Bps.Command.SourceRead, 2, 0)]
        [InlineData(Bps.Command.SourceCopy, 1, -1)]
        [InlineData(Bps.Command.TargetRead, 1, 0)]
        [InlineData((Bps.Command)99, 1, 0)]
        public void BpsApply_RejectsInvalidPublicActions(Bps.Command command, long length, long delta)
        {
            byte[] source = [1];
            var patch = new Bps.Patch(1, 2, [], [new(command, length, null, delta)], Crc32.Compute(source), 0);
            Assert.Throws<InvalidDataException>(() => Bps.Apply(patch, source));
        }

        [Theory]
        [InlineData(0x20, ulong.MaxValue)]
        [InlineData(0x48, ulong.MaxValue)]
        [InlineData(0x60, ulong.MaxValue)]
        public void Elf_RejectsOutOfBoundsOffsets(int offset, ulong value)
        {
            byte[] data = Elf();
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), value);
            Assert.Throws<InvalidDataException>(() => new SceElf(data));
        }

        [Fact]
        public void Elf_RejectsWrongEncoding()
        {
            byte[] data = Elf();
            data[5] = 2;
            Assert.Throws<InvalidDataException>(() => new SceElf(data));
        }

        [Fact]
        public void Elf_DynamicNameWithoutStrings_IsADataError()
        {
            byte[] data = Elf();
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x40), SceElf.PtDynamic);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x48), 0x80);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x60), 0x20);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x80), SceElf.DtSceModuleName);
            Assert.Throws<InvalidDataException>(() => new SceElf(data).ModuleName);
        }

        [Fact]
        public void ImportFirmwareDump_EmptyFolderCannotBecomeAnExactDatabase()
        {
            string root = TempRoot();
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "Firmware 4.00"));
                var (db, problem) = DataGenerators.ImportFirmwareDump(root, "4.00");
                Assert.Null(db);
                Assert.NotEmpty(problem);
            }
            finally { Directory.Delete(root, true); }
        }

        /// <summary>Imports write the repository firmware_db, not the copy bundled next to the executable.</summary>
        [Fact]
        public void Cli_FirmwareImportsDefaultToRepositoryDatabase()
        {
            Assert.Equal(Path.Combine(Repo.Root, "firmware_db"), Cli.ImportDb);
            Assert.NotEqual(Path.GetFullPath(Cli.ImportDb), Path.GetFullPath(Paths.FirmwareDbDir!));   // the bundled copy wins for reading
        }

        [Fact]
        public void Cli_EmptyFirmwareImportPreservesExistingDatabase()
        {
            string root = TempRoot();
            try
            {
                string input = Path.Combine(root, "input");
                Directory.CreateDirectory(input);
                string database = Path.Combine(root, "4.00.json");
                File.WriteAllText(database, "keep");
                using var output = new StringWriter();
                using var error = new StringWriter();
                Assert.Equal(1, Cli.Run(["fwdb", "import-dump", input, "--firmware", "4", "--source", "test", "--db", root], output, error));
                Assert.Equal("keep", File.ReadAllText(database));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void NullManifest_IsADataErrorRatherThanNullReference()
        {
            string root = TempRoot();
            try
            {
                File.WriteAllText(Path.Combine(root, FakelibStore.ManifestName), "null");
                Assert.Throws<InvalidDataException>(() => FakelibStore.LoadManifest(root));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void StorePage_CorruptManifestReportsErrorWithoutCrashing()
        {
            string root = TempRoot();
            try
            {
                string set = Path.Combine(root, "4");
                Directory.CreateDirectory(set);
                File.WriteAllText(Path.Combine(set, FakelibStore.ManifestName), "null");
                var vm = new MainViewModel(new AppSettings { FakelibStoreDir = root });
                vm.Page = AppPage.Store;
                Assert.True(vm.Log.ErrorCount > 0);
                Assert.Empty(vm.Store.Files);
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void SelfSign_RejectsTruncatedProgramHeaders()
        {
            byte[] data = Elf();
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x20), 0xFF);
            Assert.Throws<InvalidDataException>(() => Self.Sign(data));
        }

        [Fact]
        public void SelfUnwrap_RejectsOverflowingOutputSegment()
        {
            byte[] data = Self.Sign(Elf());
            int embedded = 0x20 + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x18)) * 0x20;
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(embedded + 0x48), ulong.MaxValue);
            Assert.Throws<InvalidDataException>(() => Self.Unwrap(data));
        }

        [Theory]
        [InlineData("999999999999999999999")]
        [InlineData("4.100")]
        public void Firmware_RejectsValuesThatCannotFitTheVersionFormat(string firmware) =>
            Assert.Throws<FormatException>(() => Firmware.Parse(firmware));

        [Fact]
        public void Sdk_RejectsShortParamRatherThanReadingAdjacentBytes()
        {
            byte[] data = Elf();
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x40), SceElf.PtSceProcParam);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x48), 0x80);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x60), 12);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x88), Sdk.ProcessParamMagic);
            Assert.Throws<InvalidDataException>(() => Sdk.Read(new SceElf(data)));
        }

        [Theory]
        [InlineData(new byte[] { }, new byte[] { })]
        [InlineData(new byte[] { 1 }, new byte[] { 2, 3 })]
        public void Patterns_RejectInvalidReplacementLengths(byte[] find, byte[] replace)
        {
            byte[] data = [1, 9];
            Assert.Throws<ArgumentException>(() => FixApplier.ReplaceAll(data, new FixPattern("bad", find, replace, []), cancel: TestContext.Current.CancellationToken));
            Assert.Equal(new byte[] { 1, 9 }, data);
        }

        [Fact]
        public void Tools_BusyOperationsCannotChangeTheFileList()
        {
            var vm = new MainViewModel();
            vm.Tools.Files.Add(new SelfFileRow("test", "ELF", "", "", "", "muted"));
            vm.Tools.Busy = true;
            vm.Tools.ClearFilesCommand.Execute(null);
            Assert.Single(vm.Tools.Files);
        }

        [Fact]
        public void WalkFiles_PreservesSortedDepthFirstOrder()
        {
            string root = TempRoot();
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "b"));
                Directory.CreateDirectory(Path.Combine(root, "a", "nested"));
                foreach (string rel in new[] { "z.elf", "c.elf", "b/z.elf", "a/z.elf", "a/nested/z.elf" })
                    File.WriteAllBytes(Path.Combine(root, rel), []);
                Assert.Equal(new[] { "c.elf", "z.elf", "a/z.elf", "a/nested/z.elf", "b/z.elf" },
                    GameScan.WalkFiles(root, cancel: TestContext.Current.CancellationToken).Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')));
                Assert.Equal(GameScan.WalkFiles(root, cancel: TestContext.Current.CancellationToken).Select(p => Path.GetRelativePath(root, p)), GameScan.FindModules(root, cancel: TestContext.Current.CancellationToken));
            }
            finally { Directory.Delete(root, true); }
        }

        [Theory]
        [InlineData("--firmware")]
        [InlineData("--ps5")]
        public void Cli_MissingOptionValue_ReturnsAnError(string option)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(1, Cli.Run(["sdk", "input", "output", option], output, error));
            Assert.Contains("needs a value", error.ToString());
        }

        [Fact]
        public void Workbench_BusyCannotReplaceGame()
        {
            var vm = new MainViewModel { Busy = true };
            vm.LoadGame(Path.GetTempPath());
            Assert.Null(vm.Game);
            Assert.False(vm.OpenGameCommand.CanExecute(null));
        }

        [Fact]
        public void Settings_ExplicitNullsDoNotCrashStartupOrRemember()
        {
            string root = TempRoot();
            try
            {
                string path = Path.Combine(root, "settings.json");
                File.WriteAllText(path, "{\"Firmware\":null,\"RecentGames\":null}");
                var settings = AppSettings.Load(path, out _);
                settings.Remember("game");
                var vm = new MainViewModel(settings);
                Assert.Equal("4", vm.SelectedFirmware);
                Assert.Single(settings.RecentGames);
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void AtomicWrite_DoesNotOverwriteAnUnrelatedTmpFile()
        {
            string root = TempRoot();
            try
            {
                string path = Path.Combine(root, "output");
                File.WriteAllText(path + ".tmp", "keep");
                Self.WriteAtomic(path, [1, 2]);
                Assert.Equal("keep", File.ReadAllText(path + ".tmp"));
                Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(path));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void Pipeline_RejectsCaseAndTrailingSeparatorAliasesBeforeWriting()
        {
            if (!OperatingSystem.IsWindows()) return;
            string root = TempRoot();
            try
            {
                string source = Path.Combine(root, "Game");
                Directory.CreateDirectory(source);
                var options = new BackportOptions { Firmware = "4", ReferenceDir = root };
                var k = Knowledge.Default;
                Assert.Throws<ArgumentException>(() => Pipeline.Backport(source, source.ToUpperInvariant(), options, k, cancel: TestContext.Current.CancellationToken));
                Assert.Throws<ArgumentException>(() => Pipeline.Backport(source + Path.DirectorySeparatorChar, Path.Combine(source, "out"), options, k, cancel: TestContext.Current.CancellationToken));
                Assert.False(Directory.Exists(Path.Combine(source, "out")));
                Assert.Throws<ArgumentException>(() => AmprIndex.Build(source, source, cancel: TestContext.Current.CancellationToken));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void Pipeline_PreCancelledRunWritesNothing()
        {
            string root = TempRoot();
            try
            {
                string output = Path.Combine(root, "output");
                string source = Path.Combine(root, "game");
                Directory.CreateDirectory(source);
                using var cancel = new CancellationTokenSource();
                cancel.Cancel();
                Assert.Throws<OperationCanceledException>(() => Pipeline.Backport(source, output,
                    new BackportOptions { Firmware = "4", ReferenceDir = root }, Knowledge.Default, cancel: cancel.Token));
                Assert.False(Directory.Exists(output));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void FirmwarePage_ClearSelectionClearsDetails()
        {
            var db = new FirmwareExports("4", "test", false, new() { ["lib"] = ["nid"] });
            var vm = new MainViewModel();
            vm.Database.ChangedOnly = false;
            vm.Database.Firmwares.Add(new FirmwareRow(db));
            vm.Database.Selected = vm.Database.Firmwares[0];
            Assert.Single(vm.Database.Modules);
            vm.Database.Selected = null;
            Assert.Empty(vm.Database.Modules);
            Assert.Null(vm.Database.SelectedModule);
        }

        [Fact]
        public void FirmwarePage_SearchesUnchangedFunctionNames()
        {
            string root = TempRoot();
            try
            {
                string names = Path.Combine(root, "names.csv");
                File.WriteAllText(names, "abcdefghijk sceExampleFunction\n");
                var vm = new MainViewModel(new AppSettings { AerolibPath = names });
                var db = new FirmwareExports("4", "test", false, new() { ["lib"] = ["abcdefghijk"] });
                vm.Database.Firmwares.Add(new FirmwareRow(db));
                vm.Database.Selected = vm.Database.Firmwares[0];
                vm.Database.Search = "Example";
                Assert.Single(vm.Database.Modules);
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void ModuleRow_PatternOnlyChangeIsVisible()
        {
            var module = new ModuleResult("eboot.bin", Self.KindElf, false);
            module.Patterns.Add(new PatternEntry { Name = "fix", Count = 1 });
            Assert.Equal("will change", new ModuleRow(module).State);
        }

        [Fact]
        public async Task FirmwareRefresh_DoesNotPublishResultsFromAnOldPath()
        {
            string root = TempRoot();
            try
            {
                string old = Path.Combine(root, "old"), current = Path.Combine(root, "new");
                Directory.CreateDirectory(old);
                Directory.CreateDirectory(current);
                FirmwareDb.Save(new FirmwareExports("4", "test", false, new()), Path.Combine(old, "4.00.json"));
                var settings = new AppSettings { FirmwareDbDir = old };
                var vm = new MainViewModel(settings);
                // Change the configured path synchronously as the old request starts.
                vm.Database.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(vm.Database.Busy) && vm.Database.Busy)
                        settings.FirmwareDbDir = current;
                };
                await vm.Database.Refresh();
                Assert.Empty(vm.Database.Firmwares);
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void Pipeline_ComposesMultiplePatternsForOneDataFile()
        {
            string root = TempRoot();
            try
            {
                string source = Path.Combine(root, "game"), output = Path.Combine(root, "overlay"), dataDir = Path.Combine(root, "data");
                Directory.CreateDirectory(source);
                Directory.CreateDirectory(dataDir);
                foreach (string file in Directory.EnumerateFiles(Paths.DataDir, "*.json"))
                    File.Copy(file, Path.Combine(dataDir, Path.GetFileName(file)));
                var fixes = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dataDir, "fixes.json")))!;
                fixes["patterns"] = System.Text.Json.Nodes.JsonNode.Parse("""
                    [{"name":"first","find":"0102","replace":"0304","files":["data.bin"]},
                     {"name":"second","find":"0506","replace":"0708","files":["data.bin"]}]
                    """);
                File.WriteAllText(Path.Combine(dataDir, "fixes.json"), fixes.ToJsonString());
                File.WriteAllBytes(Path.Combine(source, "data.bin"), [1, 2, 5, 6]);
                var db = new FirmwareExports("4", "test", false, new());
                FirmwareDb.Save(db, Path.Combine(root, "4.00.json"));
                var result = Pipeline.Backport(source, output,
                    new BackportOptions { Firmware = "4", ReferenceDir = root }, new Knowledge(dataDir), cancel: TestContext.Current.CancellationToken);
                Assert.Equal(new byte[] { 3, 4, 7, 8 }, File.ReadAllBytes(Path.Combine(output, "data.bin")));
                Assert.Equal(2, result.DataFiles.Count);
            }
            finally { Directory.Delete(root, true); }
        }

        [Theory]
        [InlineData("CAAAAA")]
        [InlineData("ZZZZZZZZZZ")]
        [InlineData("")]
        public void NidIds_RejectOverflowAndEmptyDigits(string digits) =>
            Assert.Throws<FormatException>(() => Nid.IdFromChars(digits));

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Elf_RejectsLoadSegmentsWhoseVirtualRangeWraps(bool memoryOnly)
        {
            byte[] data = SyntheticBinary.Elf();
            SyntheticBinary.W64(data, 0x50, ulong.MaxValue - (ulong)data.Length + 2);
            if (memoryOnly) SyntheticBinary.W64(data, 0x60, 0);
            Assert.Throws<InvalidDataException>(() => new SceElf(data));
        }

        [Fact]
        public void Elf_RejectsLoadSegmentWithFileSizeLargerThanMemorySize()
        {
            byte[] data = SyntheticBinary.Elf();
            SyntheticBinary.W64(data, 0x68, (ulong)data.Length - 1);
            Assert.Throws<InvalidDataException>(() => new SceElf(data));
        }

        [Fact]
        public void Elf_AcceptsLoadSegmentWithMemorySizeLargerThanFileSize()
        {
            byte[] data = SyntheticBinary.Elf();
            SyntheticBinary.W64(data, 0x68, (ulong)data.Length + 0x1000);
            var elf = new SceElf(data);
            Assert.Equal(data.Length - 1L, elf.Off((ulong)data.Length - 1));
            Assert.Throws<InvalidDataException>(() => elf.Off((ulong)data.Length));
        }

        [Fact]
        public void Elf_LoadSegmentCanEndAtTheLastVirtualAddress()
        {
            byte[] data = SyntheticBinary.Elf();
            SyntheticBinary.W64(data, 0x50, ulong.MaxValue - (ulong)data.Length + 1);
            var elf = new SceElf(data);
            Assert.Equal(ulong.MaxValue, elf.Vaddr(data.Length - 1));
            Assert.Equal(data.Length - 1L, elf.Off(ulong.MaxValue));
        }

        [Fact]
        public void NidSplit_RejectsAnInvalidNidAlphabet() =>
            Assert.Null(Nid.SplitSymbol("!!!!!!!!!!!#A#A"));

        [Fact]
        public void ParamPatch_LowersUppercaseHexPrefix()
        {
            string root = TempRoot();
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "sce_sys"));
                File.WriteAllText(Path.Combine(root, "sce_sys", "param.json"),
                    "{\"requiredSystemSoftwareVersion\":\"0X1360000000000000\"}");
                var info = GameScan.ReadParam(root, "4.03", patchParam: true);
                Assert.NotNull(info.PatchedText);
                using var doc = System.Text.Json.JsonDocument.Parse(info.PatchedText);
                Assert.Equal("0x0403000000000000", doc.RootElement.GetProperty("requiredSystemSoftwareVersion").GetString());
            }
            finally { Directory.Delete(root, true); }
        }

        [Theory]
        [InlineData("null")]
        [InlineData("{\"firmware\":null,\"modules\":{}}")]
        [InlineData("{\"firmware\":\"4\",\"modules\":null}")]
        [InlineData("{\"firmware\":\"4\",\"modules\":{\"lib\":[null]}}")]
        [InlineData("{\"firmware\":\"4\"}")]
        public async Task FirmwareDatabase_InvalidSchemaIsReportedWithoutCrashing(string json)
        {
            string root = TempRoot();
            try
            {
                string path = Path.Combine(root, "4.00.json");
                File.WriteAllText(path, json);
                Assert.Throws<InvalidDataException>(() => FirmwareDb.Load(path));
                using var vm = new MainViewModel(new AppSettings { FirmwareDbDir = root });
                await vm.Database.Refresh();
                Assert.False(vm.Database.Busy);
                Assert.Empty(vm.Database.Firmwares);
                Assert.True(vm.Log.ErrorCount > 0);
            }
            finally { Directory.Delete(root, true); }
        }

        [Theory]
        [InlineData("{}", "W:/Build/J00000002/x", "")]
        [InlineData("{\"J1\":\"4.00\"}", "W:/Build/J00000002/x", "J00000002 = newer than FW 4.00")]
        [InlineData("{\"J1\":\"4.00\"}", "W:/Build/J999999999999999999999999/x", "")]
        public void SonyBuild_HandlesEmptyCatalogAndAcceptedBuildIds(string builds, string path, string expected)
        {
            string root = TempRoot();
            try
            {
                foreach (string file in Directory.EnumerateFiles(Paths.DataDir, "*.json"))
                    File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
                File.WriteAllText(Path.Combine(root, "sony_builds.json"), "{\"builds\":" + builds + "}");
                Assert.Equal(expected, new Knowledge(root).SonyBuild(path));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void AtomicWrite_CreateOnlyPreservesAnExistingDestination()
        {
            string root = TempRoot();
            try
            {
                string path = Path.Combine(root, "output");
                File.WriteAllBytes(path, [7]);
                Assert.Throws<IOException>(() => Self.WriteAtomic(path, [1, 2], overwrite: false));
                Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(path));
                Assert.Equal(new[] { path }, Directory.GetFiles(root));
            }
            finally { Directory.Delete(root, true); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Pipeline_SelectedOptionUsesItsStubBodyAndReportName(bool dryRun)
        {
            string root = TempRoot();
            try
            {
                string game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
                string rules = Directory.CreateDirectory(Path.Combine(root, "rules")).FullName;
                string reference = Directory.CreateDirectory(Path.Combine(root, "reference")).FullName;
                string output = Path.Combine(root, "overlay");
                foreach (string file in Directory.EnumerateFiles(Paths.DataDir, "*.json"))
                    File.Copy(file, Path.Combine(rules, Path.GetFileName(file)));
                var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(rules, "fixes.json")))!;
                json["options"]!["audit_stub"] = System.Text.Json.Nodes.JsonNode.Parse("""
                    {"name":"Audit stub override","fixes":{"XXXXXXXXXXX":
                    {"kind":"stub","name":"synthetic","sources":[],"stub":"ret:0x12345678"}}}
                    """);
                File.WriteAllText(Path.Combine(rules, "fixes.json"), json.ToJsonString());
                byte[] module = SyntheticBinary.Elf(imports: true);
                SyntheticBinary.W64(module, 0x130, SceElf.DtJmpRel);
                SyntheticBinary.W64(module, 0x140, SceElf.DtPltRelSz);
                SyntheticBinary.W64(module, 0x480, 0x426);
                Convert.FromHexString("FF255A0000006800000000").CopyTo(module, 0x420);
                module[0x380] = Patching.Ret;
                module.AsSpan(0x381, 32).Fill(Patching.Int3);
                File.WriteAllBytes(Path.Combine(game, "eboot.bin"), module);
                FirmwareDb.Save(new FirmwareExports("4", "test", false, new()), Path.Combine(reference, "4.00.json"));
                var knowledge = new Knowledge(rules);
                var result = Pipeline.Backport(game, output,
                    new BackportOptions { Firmware = "4", ReferenceDir = reference, Options = ["audit_stub"], DryRun = dryRun },
                    knowledge, cancel: TestContext.Current.CancellationToken);
                Assert.Equal("ret:0x12345678", Assert.Single(Assert.Single(result.Modules).StubbedNow).Stub);
                if (!dryRun)
                {
                    Assert.True(result.Published);
                    Assert.Contains("audit_stub (Audit stub override)", File.ReadAllText(Path.Combine(output, Pipeline.ReportTxt)));
                    var elf = new SceElf(Self.Unwrap(File.ReadAllBytes(Path.Combine(output, "eboot.bin"))));
                    ulong stub = elf.ReadU64(Assert.Single(elf.Imports()).GotVaddr);
                    Assert.Equal(Convert.FromHexString("B878563412C3"), elf.Data.AsSpan((int)elf.Off(stub), 6).ToArray());
                }
            }
            finally { Directory.Delete(root, true); }
        }

        [Theory]
        [InlineData("{\"module\":42}")]
        [InlineData("{\"container\":{}}")]
        [InlineData("{\"size\":\"large\"}")]
        [InlineData("{\"size\":-1}")]
        public void StoreManifest_InvalidFieldTypesCannotCrashTheCli(string entry)
        {
            string root = TempRoot();
            try
            {
                string set = Directory.CreateDirectory(Path.Combine(root, "4")).FullName;
                File.WriteAllText(Path.Combine(set, FakelibStore.ManifestName), "{\"synthetic.sprx\":" + entry + "}");
                Assert.Throws<InvalidDataException>(() => FakelibStore.LoadManifest(set));
                using var output = new StringWriter();
                using var error = new StringWriter();
                Assert.Equal(1, Cli.Run(["fakelib", "list", "--store", root], output, error));
                Assert.Contains("error:", error.ToString());
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void CandidateGenerator_MissingSectionIsADataError()
        {
            string root = TempRoot();
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "core", "libs", "prx"));
                Directory.CreateDirectory(Path.Combine(root, "docs", "dev"));
                File.WriteAllText(Path.Combine(root, "docs", "dev", "TechnicalDebt.md"), "# Renamed sections\n");
                Assert.Throws<InvalidDataException>(() => DataGenerators.ScanCandidates(root));
            }
            finally { Directory.Delete(root, true); }
        }

        /// <summary>AnyPS5 2026-10 moved the bullets to docs/dev/debt/&lt;library&gt;.md; an empty top-level section must not wipe the catalog.</summary>
        [Fact]
        public void CandidateGenerator_ReadsPerLibraryDebtFiles()
        {
            string root = TempRoot();
            try
            {
                string library = Directory.CreateDirectory(Path.Combine(root, "core", "libs", "prx", "libSynthetic")).FullName;
                string debt = Directory.CreateDirectory(Path.Combine(root, "docs", "dev", "debt")).FullName;
                File.WriteAllText(Path.Combine(library, "Export.cpp"),
                    "int APS5_VABI sceTarget() { return 0; }\nint APS5_VABI sceOther() { return 1; }\n");
                File.WriteAllText(Path.Combine(root, "docs", "dev", "TechnicalDebt.md"),
                    "### Silent stubs\n\nEverywhere... except:\n\n### Unknown function info\n");
                Assert.Throws<InvalidDataException>(() => DataGenerators.ScanCandidates(root));

                File.WriteAllText(Path.Combine(debt, "libSynthetic.md"),
                    "### Silent stubs\n\n- [sceTarget](../../../core/libs/prx/libSynthetic/Export.cpp) (libSynthetic)\n\n" +
                    "### Unknown function info\n\n- [sceOther](../../../core/libs/prx/libSynthetic/Export.cpp)\n");
                var candidate = Assert.Single(DataGenerators.ScanCandidates(root));
                Assert.Equal("sceTarget", candidate.Name);
                Assert.Equal("AnyPS5 libSynthetic/Export.cpp:1", candidate.Source);
            }
            finally { Directory.Delete(root, true); }
        }

        /// <summary>Names and their NUL must fit inside the declared dynamic string table.</summary>
        [Theory]
        [Trait("Phase", "AnyPs5Phase2")]
        [InlineData(0, 1)]
        [InlineData(1, 1)]   // Symbol offset equals the declared table size.
        [InlineData(16, 1)]  // NUL is immediately outside the declared table.
        [InlineData(17, 17)]
        [InlineData(17, 18)]
        public void StringTableBoundary_RejectsInvalidSymbolNames(int stringSize, uint nameOffset)
        {
            byte[] data = SyntheticBinary.Elf();
            SyntheticBinary.W64(data, 0x160, 10); // DT_STRSZ, replacing the old DT_NULL.
            SyntheticBinary.W64(data, 0x168, (ulong)stringSize);
            SyntheticBinary.W64(data, 0x170, 0);  // Retain a terminated dynamic table.
            SyntheticBinary.W32(data, 0x298, nameOffset);
            Assert.Throws<InvalidDataException>(() => new SceElf(data).Symbols());
        }

        [Theory]
        [Trait("Phase", "AnyPs5Phase2")]
        [InlineData(17UL)] // NUL is the last byte inside the declared table.
        [InlineData(0x300UL)] // Table ends exactly at EOF.
        [InlineData(null)] // Retain support for inputs without DT_STRSZ.
        public void StringTableBoundary_AcceptsValidAndLegacyTables(ulong? stringSize)
        {
            byte[] data = SyntheticBinary.Elf();
            if (stringSize is { } size)
            {
                SyntheticBinary.W64(data, 0x160, 10);
                SyntheticBinary.W64(data, 0x168, size);
                SyntheticBinary.W64(data, 0x170, 0);
            }
            Assert.Equal(SyntheticBinary.Nid + "#A#A", new SceElf(data).Symbols()[1].Name);
        }

        [Theory]
        [Trait("Phase", "AnyPs5Phase2")]
        [InlineData(0x301UL)]
        [InlineData(ulong.MaxValue)]
        public void StringTableBoundary_RejectsDeclaredRangeOutsideFile(ulong stringSize)
        {
            byte[] data = SyntheticBinary.Elf();
            SyntheticBinary.W64(data, 0x160, 10);
            SyntheticBinary.W64(data, 0x168, stringSize);
            SyntheticBinary.W64(data, 0x170, 0);
            Assert.Throws<InvalidDataException>(() => new SceElf(data).Symbols());
        }

        [Theory]
        [Trait("Phase", "AnyPs5Phase2")]
        [InlineData(SceElf.DtSceModuleName, 16, false)]
        [InlineData(SceElf.DtSceModuleName, 17, true)]
        [InlineData(SceElf.DtSceModuleInfoPath, 16, false)]
        [InlineData(SceElf.DtSceModuleInfoPath, 17, true)]
        [InlineData(SceElf.DtSceImportLib, 16, false)]
        [InlineData(SceElf.DtSceImportLib, 17, true)]
        public void StringTableBoundary_AppliesToDynamicNames(ulong tag, int stringSize, bool valid)
        {
            byte[] data = SyntheticBinary.Elf();
            SyntheticBinary.W64(data, 0x150, tag);
            SyntheticBinary.W64(data, 0x158, 1);
            SyntheticBinary.W64(data, 0x160, 10);
            SyntheticBinary.W64(data, 0x168, (ulong)stringSize);
            SyntheticBinary.W64(data, 0x170, 0);
            var elf = new SceElf(data);
            string? ReadName() => tag == SceElf.DtSceModuleName ? elf.ModuleName
                : tag == SceElf.DtSceModuleInfoPath ? elf.BuildPath : elf.ImportLibraries()[0];
            if (valid)
                Assert.Equal(SyntheticBinary.Nid + "#A#A", ReadName());
            else
                Assert.Throws<InvalidDataException>(() => ReadName());
        }

        /// <summary>Explicit function labels restrict selection; library links preserve their existing scope.</summary>
        [Theory]
        [Trait("Phase", "AnyPs5Phase3")]
        [InlineData("sceTarget", "sceTarget", "")]
        [InlineData("`sceTarget`", "sceTarget", "")]
        [InlineData("sceMissing", "", "")]
        [InlineData("`sceMissing`", "", "")]
        [InlineData("sceTarget, sceOther", "sceOther,sceTarget", "")]
        [InlineData("`sceTarget`, sceMissing, sceTarget", "sceTarget", "")]
        [InlineData("sceTarget", "sceTarget", " - also mentions `sceOther`")]
        [InlineData("libSynthetic", "sceOther,sceTarget", "")]
        [InlineData("libSynthetic", "sceTarget", " - `sceTarget`")]
        [InlineData("libSynthetic", "sceOther,sceTarget", " - status `NOT_RUNNING`")]
        [InlineData("Descriptive label", "sceOther,sceTarget", "")]
        public void CandidateBulletScope_SelectsOnlyDocumentedFunctions(string label, string expectedNames, string prose)
        {
            string root = TempRoot();
            try
            {
                string library = Directory.CreateDirectory(Path.Combine(root, "core", "libs", "prx", "libSynthetic")).FullName;
                string docs = Directory.CreateDirectory(Path.Combine(root, "docs", "dev")).FullName;
                File.WriteAllText(Path.Combine(library, "Export.cpp"),
                    "int APS5_VABI sceTarget() { return 0; }\nint APS5_VABI sceOther() { return 1; }\n");
                string bullet = "- [" + label + "](../../core/libs/prx/libSynthetic/Export.cpp)" + prose + "\n";
                File.WriteAllText(Path.Combine(docs, "TechnicalDebt.md"),
                    "### Silent stubs\n" + bullet + bullet + "### Other\n- [sceOther](../../core/libs/prx/libSynthetic/Export.cpp)\n");
                var candidates = DataGenerators.ScanCandidates(root);
                Assert.Equal(expectedNames.Split(',', StringSplitOptions.RemoveEmptyEntries), candidates.Select(c => c.Name));
                Assert.All(candidates, c =>
                {
                    Assert.Equal(Nid.Compute(c.Name), c.Nid);
                    Assert.Equal(c.Name == "sceTarget" ? 0u : 1u, c.Value);
                });
                string output = Path.Combine(root, "candidates.json");
                Assert.Equal(candidates, DataGenerators.WriteCandidates(root, output));
                using var generated = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(output));
                var entries = generated.RootElement.GetProperty("candidates");
                Assert.Equal(candidates.Count, entries.EnumerateObject().Count());
                Assert.All(candidates, c =>
                {
                    var entry = entries.GetProperty(c.Nid);
                    Assert.Equal(c.Name, entry.GetProperty("name").GetString());
                    Assert.Equal($"0x{c.Value:x8}", entry.GetProperty("value").GetString());
                    Assert.Equal(c.Source, entry.GetProperty("source").GetString());
                });
            }
            finally { Directory.Delete(root, true); }
        }

        static string TempRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "ps5backport-audit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }
    }
}
#endregion
#endif

#if !BACKPORT_SYNTHETIC_ONLY && !BACKPORT_PARSER_TESTS_ONLY
#region DiagnosticTests
namespace Ps5Backport.Tests
{
    using System.Text.Json;
    using Ps5Backport.App.ViewModels;
    using Ps5Backport.Core;

    public sealed class DiagnosticTests : IDisposable
    {
        readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ps5backport-diagnostics-" + Guid.NewGuid().ToString("N"))).FullName;
        string Logs => Path.Combine(_root, "logs");

        [Fact]
        public void Rotation_IsBounded_AndRetainsNewestCompleteJsonRecords()
        {
            using (var log = new DiagnosticLog(Logs, 2048, 3))
            {
                for (int i = 0; i < 500; i++) log.Write(new("synthetic", "event", Message: "record " + i));
                Assert.Null(log.Failure);
            }
            string[] files = Directory.GetFiles(Logs, "*.jsonl");
            Assert.Equal(3, files.Length);
            Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, 2048));
            var entries = files.SelectMany(File.ReadAllLines).Select(s => JsonSerializer.Deserialize<DiagnosticEvent>(s)!).ToArray();
            Assert.DoesNotContain(entries, e => e.Message == "record 0");
            Assert.Contains(entries, e => e.Message == "record 499");
            Assert.Equal("record 499", JsonSerializer.Deserialize<DiagnosticEvent>(File.ReadAllLines(Path.Combine(Logs, "events.jsonl"))[^1])!.Message);
        }

        [Fact]
        public void CompetingWriter_FailsOpen_AndLeaseIsReleasedOnDispose()
        {
            using (var first = new DiagnosticLog(Logs))
            using (var second = new DiagnosticLog(Logs))
            {
                Assert.Null(first.Failure);
                Assert.NotNull(second.Failure);
                second.Write(new("synthetic", "ignored"));
                first.Write(new("synthetic", "kept"));
            }
            using var reopened = new DiagnosticLog(Logs);
            Assert.Null(reopened.Failure);
            Assert.Equal("kept", Read().Single().Event);
        }

        [Fact]
        public void DiskFailure_DoesNotFailTheBackport()
        {
            using var log = new DiagnosticLog(Logs);
            Directory.CreateDirectory(Path.Combine(Logs, "events.jsonl"));
            var result = Pipeline.Backport(Game(), Path.Combine(_root, "out"), Options(), Knowledge.Default, cancel: TestContext.Current.CancellationToken, diagnostics: log);
            Assert.Equal(OperationState.Completed, result.State);
            Assert.NotNull(log.Failure);
            Assert.False(Directory.Exists(Path.Combine(_root, "out")));
        }

        [Fact]
        public void StructuredEvents_CorrelateModulesStagesDurationAndExceptions()
        {
            using var log = new DiagnosticLog(Logs);
            string game = Game();
            File.WriteAllBytes(Path.Combine(game, "broken.sprx"), [1, 2, 3]);
            var result = Pipeline.Backport(game, Path.Combine(_root, "out"), Options(), Knowledge.Default, cancel: TestContext.Current.CancellationToken, diagnostics: log);
            Assert.Equal(OperationState.Failed, result.State);
            var events = Read();
            Assert.Single(events.Select(e => e.RunId).Distinct());
            Assert.All(events, e => Assert.NotNull(e.RunId));
            Assert.Contains(events, e => e.Event == "started" && e.Stage == "preflight");
            Assert.Contains(events, e => e.Event == "stage_finished" && e.Stage == "patching" && e.Module == "eboot.bin" && e.DurationMilliseconds >= 0);
            var failure = Assert.Single(events, e => e.Event == "module_failed");
            Assert.Equal(("loading", "broken.sprx", "data"), (failure.Stage, failure.Module, failure.ErrorCategory));
            Assert.Contains("InvalidDataException", failure.ExceptionType!);
            Assert.Contains("unknown file format", failure.ExceptionContext!);
            Assert.Equal("failed", events[^1].Event);
            Assert.True(events[^1].DurationMilliseconds >= 0);
        }

        [Fact]
        public async Task PreCancelledOperation_RecordsCancellationAndRunId()
        {
            using var log = new DiagnosticLog(Logs);
            using var operation = new BackportOperation(Game(), Path.Combine(_root, "out"), Options(), Knowledge.Default, diagnostics: log);
            operation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.ExecuteAsync(cancel: TestContext.Current.CancellationToken));
            var events = Read();
            Assert.Equal(operation.RunId, events[^1].RunId);
            Assert.Equal(("cancelled", "cancelled"), (events[^1].Event, events[^1].ErrorCategory));
        }

        [Fact]
        public void LocalUiRecords_BoundStringsAndEscapeNewlines_AndSurviveClear()
        {
            using var log = new DiagnosticLog(Logs);
            var vm = new LogViewModel(log);
            vm.Error("private\npath " + new string('x', 10000), exception: new IOException("test disk failure"));
            vm.Clear();
            Assert.Empty(vm.Visible);
            var entry = Assert.Single(Read());
            Assert.Contains("\n", entry.Message!);
            Assert.True(entry.Message!.Length <= 2048);
            Assert.Equal("io", entry.ErrorCategory);
            Assert.Contains("IOException", entry.ExceptionContext!);
            Assert.Single(File.ReadAllLines(Path.Combine(Logs, "events.jsonl")));
        }

        [Fact]
        public void UiHistory_IsBounded_CountsDescribeRetainedEntries_AndFiltersUseOneReset()
        {
            var log = new LogViewModel();
            for (int i = 0; i < 2400; i++) log.Add((LogLevel)(i % 3), "record " + i);
            Assert.Equal(LogViewModel.MaximumEntries, log.Visible.Count);
            Assert.Equal("record 400", log.Visible[0].Message);
            Assert.Equal("record 2399", log.Visible[^1].Message);
            Assert.Equal(667, log.WarningCount);
            Assert.Equal(667, log.ErrorCount);
            int resets = 0;
            log.Visible.CollectionChanged += (_, e) => { Assert.Equal(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, e.Action); resets++; };
            log.Filter = "errors";
            Assert.Equal(1, resets);
            Assert.Equal(667, log.Visible.Count);
            Assert.All(log.Visible, e => Assert.Equal(LogLevel.Error, e.Level));
            Assert.DoesNotContain("record 2\n", log.Text());
            log.Clear();
            Assert.Equal((0, 0), (log.ErrorCount, log.WarningCount));
        }

        BackportOptions Options()
        {
            string reference = Directory.CreateDirectory(Path.Combine(_root, "reference")).FullName;
            FirmwareDb.Save(new FirmwareExports("4", "synthetic", false, new()), Path.Combine(reference, "4.json"));
            return new() { Firmware = "4", ReferenceDir = reference, DryRun = true, KnownFixes = false };
        }
        string Game()
        {
            string game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
            File.WriteAllBytes(Path.Combine(game, "eboot.bin"), SyntheticBinary.Elf(imports: true));
            return game;
        }
        DiagnosticEvent[] Read() => File.ReadAllLines(Path.Combine(Logs, "events.jsonl")).Select(s => JsonSerializer.Deserialize<DiagnosticEvent>(s)!).ToArray();
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
#endregion
#endif

#if !BACKPORT_SYNTHETIC_ONLY && !BACKPORT_PARSER_TESTS_ONLY
#region DistributionTests
namespace Ps5Backport.Tests
{
    using System.Diagnostics;
    using System.Security.Cryptography;

    public class DistributionTests
    {
        [Theory]
        [InlineData("Ps5Backport.App")]
        [InlineData("Ps5Backport.Cli")]
        public void ExecutableBuildOutput_ContainsUnchangedRuntimeAssets(string project)
        {
    #if DEBUG
            const string configuration = "Debug";
    #else
            const string configuration = "Release";
    #endif
            string output = Path.Combine(Repo.Root, "src", project, "bin", configuration, "net10.0");
            foreach (string directory in new[] { "data", "firmware_db" })
                foreach (string original in Directory.EnumerateFiles(Path.Combine(Repo.Root, directory), "*.json", SearchOption.AllDirectories))
                {
                    string copy = Path.Combine(output, Path.GetRelativePath(Repo.Root, original));
                    Assert.True(File.Exists(copy), "Missing packaged asset: " + copy);
                    Assert.Equal(SHA256.HashData(File.ReadAllBytes(original)), SHA256.HashData(File.ReadAllBytes(copy)));
                }
            Assert.True(File.Exists(Path.Combine(output, "DISTRIBUTION.md")));
            string store = Path.Combine(Repo.Root, "fakelibs");
            if (Directory.Exists(store))
                foreach (string original in Directory.EnumerateFiles(store, "*", SearchOption.AllDirectories)
                             .Where(f => Path.GetFileName(f) == "manifest.json" || Path.GetExtension(f) is ".sprx" or ".prx" or ".psp")
                             .Where(f => !Path.GetRelativePath(store, f).Split(Path.DirectorySeparatorChar).Any(p => p.StartsWith(".ps5bp-", StringComparison.OrdinalIgnoreCase))))
                    Assert.Equal(SHA256.HashData(File.ReadAllBytes(original)),
                        SHA256.HashData(File.ReadAllBytes(Path.Combine(output, Path.GetRelativePath(Repo.Root, original)))));
            string symbols = Path.Combine(Repo.Root, "data", "aerolib.csv");
            Assert.Equal(SHA256.HashData(File.ReadAllBytes(symbols)), SHA256.HashData(File.ReadAllBytes(Path.Combine(output, "data", "aerolib.csv"))));
        }

        [Fact]
        public async Task CopiedCli_FindsRuntimeAssetsOutsideTheRepository()
        {
            string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ps5backport-distribution-" + Guid.NewGuid().ToString("N"))).FullName;
            try
            {
                string package = Directory.CreateDirectory(Path.Combine(root, "package")).FullName;
                string working = Directory.CreateDirectory(Path.Combine(root, "unrelated-working-directory")).FullName;
                string output = Path.GetDirectoryName(typeof(Cli).Assembly.Location)!;
                foreach (string name in new[] { "Ps5Backport.Cli.dll", "Ps5Backport.Core.dll", "Ps5Backport.Cli.deps.json", "Ps5Backport.Cli.runtimeconfig.json" })
                    File.Copy(Path.Combine(output, name), Path.Combine(package, name));
                foreach (string folder in new[] { "data", "firmware_db" })
                    foreach (string file in Directory.EnumerateFiles(Path.Combine(output, folder), "*.json", SearchOption.AllDirectories))
                    {
                        string copy = Path.Combine(package, Path.GetRelativePath(output, file));
                        Directory.CreateDirectory(Path.GetDirectoryName(copy)!); File.Copy(file, copy);
                    }
                await Run(["fwdb", "list"], "firmware");
                await Run(["fakelib", "list", "--store", Path.Combine(root, "absent-store")], "no fakelibs");
                string store = Directory.CreateDirectory(Path.Combine(package, "fakelibs", "4")).FullName;
                File.WriteAllText(Path.Combine(store, "manifest.json"), "{\"synthetic.sprx\":{\"source\":\"portable store\"}}");
                await Run(["fakelib", "list"], "portable store");
                File.WriteAllText(Path.Combine(package, "data", "aerolib.csv"), SyntheticBinary.Nid + " portable_symbol\n");
                string elf = Path.Combine(package, "synthetic.elf");
                File.WriteAllBytes(elf, SyntheticBinary.Elf(imports: true));
                await Run(["report", elf, "--all"], "portable_symbol");

                async Task Run(string[] arguments, string expected)
                {
                    var info = new ProcessStartInfo("dotnet") { WorkingDirectory = working, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    info.ArgumentList.Add(Path.Combine(package, "Ps5Backport.Cli.dll"));
                    foreach (string argument in arguments) info.ArgumentList.Add(argument);
                    using var process = Process.Start(info)!;
                    var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                    var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
                    try
                    {
                        await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                        Assert.True(process.ExitCode == 0, $"{await stdout}\n{await stderr}");
                        Assert.Contains(expected, await stdout, StringComparison.OrdinalIgnoreCase);
                    }
                    finally
                    {
                        if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(TestContext.Current.CancellationToken); }
                    }
                }
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }
}
#endregion
#endif

#if !BACKPORT_SYNTHETIC_ONLY && !BACKPORT_PARSER_TESTS_ONLY
#region FilePathCaseTests
namespace Ps5Backport.Tests
{
    using System.Text;
    using System.Text.Json;
    using Ps5Backport.Core;

    public sealed class FilePathCaseTests : IDisposable
    {
        const string Expected = "Media/Modules/Il2CppUserAssemblies.prx";
        readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ps5backport-file-path-" + Guid.NewGuid().ToString("N"))).FullName;

        [Theory]
        [InlineData("Il2cppUserAssemblies.prx", "/app0/Media/Modules/Il2CppUserAssemblies.prx", false, true)]
        [InlineData("Il2cppUserAssemblies.prx", "%s/Modules/Il2CppUserAssemblies.prx", false, true)]
        [InlineData("Il2cppUserAssemblies.prx", "%s/Modules/Il2CppUserAssemblies.prx", true, true)]
        [InlineData("Il2CppUserAssemblies.prx", "%s/Modules/Il2CppUserAssemblies.prx", false, false)]
        [InlineData("Il2cppUserAssemblies.prx", "no runtime module path", false, false)]
        [InlineData("Il2cppUserAssemblies.prx", "%s/Modules/Il2cppUserAssemblies.prx", false, false)]
        public void Cook_UsesRequestedCaseOnlyWhenTheGameReferencesIt(string filename, string requested, bool dryRun, bool corrected)
        {
            string game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
            string moduleFolder = Directory.CreateDirectory(Path.Combine(game, "Media", "Modules")).FullName;
            byte[] module = SyntheticBinary.Elf();
            File.WriteAllBytes(Path.Combine(moduleFolder, filename), module);
            File.WriteAllBytes(Path.Combine(game, "eboot.bin"), SyntheticBinary.Elf(
                Encoding.ASCII.GetBytes(requested + "\0")));
            string db = Directory.CreateDirectory(Path.Combine(_root, "reference")).FullName;
            FirmwareDb.Save(new FirmwareExports("4", "synthetic", false, new()), Path.Combine(db, "4.00.json"));
            string output = Path.Combine(_root, "out");
            var result = Pipeline.Backport(game, output,
                new BackportOptions { Firmware = "4", ReferenceDir = db, KnownFixes = false, DryRun = dryRun }, Knowledge.Default,
                cancel: TestContext.Current.CancellationToken);
            Assert.All(result.Modules, m => Assert.Empty(m.Error));
            var entry = Assert.Single(result.Modules, m => m.Path.StartsWith("Media/Modules/", StringComparison.Ordinal));
            Assert.Equal(corrected ? Expected : "Media/Modules/" + filename, entry.Path);
            Assert.Equal(corrected, result.ParamWarnings.Any(w => w.StartsWith("file path casing corrected:", StringComparison.Ordinal)));
            Assert.Equal(module, File.ReadAllBytes(Path.Combine(moduleFolder, filename))); // Source stays unchanged.
            Assert.Equal(dryRun, !Directory.Exists(output));
            if (!dryRun && corrected)
            {
                string published = Assert.Single(Directory.GetFiles(Path.Combine(output, "Media", "Modules")));
                Assert.Equal("Il2CppUserAssemblies.prx", Path.GetFileName(published));
                Assert.Equal(module, File.ReadAllBytes(published)); // Rename-only changes preserve the container and bytes.
                using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "backport_publication.json")));
                Assert.True(manifest.RootElement.GetProperty("Files").TryGetProperty(Expected, out _));
                Assert.False(manifest.RootElement.GetProperty("Files").TryGetProperty("Media/Modules/Il2cppUserAssemblies.prx", out _));
            }
        }

        [Theory]
        [InlineData("Plugins/audio.prx", "/app0/Plugins/Audio.prx", "Plugins/Audio.prx")]
        [InlineData("Engine/modules/render.sprx", "%s/Modules/Render.sprx", "Engine/Modules/Render.sprx")]
        [InlineData("Assets/config.dat", "Assets/Config.dat", "Assets/Config.dat")]
        [InlineData("Assets/config.dat", "./Assets/Config.dat", "Assets/Config.dat")]
        [InlineData("intro.bin", "/app0/Intro.bin", "Intro.bin")]
        public void ResolvesAnyEngineOrFileType(string source, string request, string expected)
        {
            var warnings = new List<string>();
            var result = FilePathCase.Resolve([source], [Encoding.ASCII.GetBytes(request + "\0")], warnings, TestContext.Current.CancellationToken);
            Assert.Equal(expected, result[source]);
            Assert.Single(warnings);
        }

        [Theory]
        [InlineData("/app0/Assets/Config.dat\0/app0/Assets/config.dat\0")]
        [InlineData("%s/Config.dat\0")]
        [InlineData("/app0/../Assets/Config.dat\0")]
        [InlineData("/other/Assets/Config.dat\0")]
        [InlineData("Config.dat\0")]
        [InlineData("/app0/Assets/Config.dat")]
        public void DoesNotGuessConflictingAmbiguousOrUnrelatedPaths(string requests)
        {
            var warnings = new List<string>();
            var result = FilePathCase.Resolve(["Assets/config.dat", "Other/config.dat"],
                [Encoding.ASCII.GetBytes(requests)], warnings, TestContext.Current.CancellationToken);
            Assert.Empty(result);
        }

        [Fact]
        public void DirectoryCaseAppliesToSiblingFiles()
        {
            var result = FilePathCase.Resolve(["assets/config.dat", "assets/other.dat"],
                [Encoding.ASCII.GetBytes("/app0/Assets/Config.dat\0")], [], TestContext.Current.CancellationToken);
            Assert.Equal("Assets/Config.dat", result["assets/config.dat"]);
            Assert.Equal("Assets/other.dat", result["assets/other.dat"]);
        }

        [Theory]
        [InlineData("assets/config.dat", "/app0/Assets/Config.dat\0/app0/assets/other.dat\0")]
        [InlineData("assets/sub/config.dat", "/app0/Assets/Sub/Config.dat\0/app0/assets/other.dat\0")]
        public void ConflictingDirectoryReferencesPreserveSourcePaths(string source, string requests)
        {
            var warnings = new List<string>();
            var result = FilePathCase.Resolve([source, "assets/other.dat"],
                [Encoding.ASCII.GetBytes(requests)], warnings, TestContext.Current.CancellationToken);
            Assert.Empty(result);
            Assert.Contains(warnings, w => w.StartsWith("conflicting directory casing:"));
        }

        [Fact]
        public void CookPublishesDataFilesWithRequestedCaseAndPreservesOtherSourceNames()
        {
            string game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
            string assets = Directory.CreateDirectory(Path.Combine(game, "assets")).FullName;
            byte[] contents = [1, 2, 3];
            File.WriteAllBytes(Path.Combine(assets, "config.dat"), contents);
            File.WriteAllBytes(Path.Combine(assets, "other.dat"), contents);
            File.WriteAllBytes(Path.Combine(game, "eboot.bin"), SyntheticBinary.Elf(
                Encoding.ASCII.GetBytes("/app0/Assets/Config.dat\0")));
            string db = Directory.CreateDirectory(Path.Combine(_root, "reference")).FullName;
            FirmwareDb.Save(new FirmwareExports("4", "synthetic", false, new()), Path.Combine(db, "4.00.json"));
            string output = Path.Combine(_root, "out");
            var result = Pipeline.Backport(game, output,
                new BackportOptions { Firmware = "4", ReferenceDir = db, KnownFixes = false }, Knowledge.Default,
                cancel: TestContext.Current.CancellationToken);
            Assert.True(result.Published);
            Assert.Equal("Assets", Path.GetFileName(Assert.Single(Directory.GetDirectories(output))));
            Assert.Equal(["Config.dat", "other.dat"], Directory.GetFiles(Path.Combine(output, "Assets"))
                .Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
            Assert.Equal(contents, File.ReadAllBytes(Path.Combine(output, "Assets", "Config.dat")));
            Assert.Equal("assets", Path.GetFileName(Assert.Single(Directory.GetDirectories(game))));
            Assert.Equal(contents, File.ReadAllBytes(Path.Combine(assets, "config.dat")));
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "backport_publication.json")));
            Assert.True(manifest.RootElement.GetProperty("Files").TryGetProperty("Assets/Config.dat", out _));
        }

        [Fact]
        public void PatchedMetadataUsesTheRequestedDirectoryCase()
        {
            string game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
            string metadata = Directory.CreateDirectory(Path.Combine(game, "sce_sys")).FullName;
            File.WriteAllText(Path.Combine(metadata, "param.json"), "{\"requiredSystemSoftwareVersion\":\"0x1360000000000000\"}");
            File.WriteAllBytes(Path.Combine(game, "eboot.bin"), SyntheticBinary.Elf(
                Encoding.ASCII.GetBytes("/app0/Sce_Sys/Param.json\0")));
            string db = Directory.CreateDirectory(Path.Combine(_root, "reference")).FullName;
            FirmwareDb.Save(new FirmwareExports("4", "synthetic", false, new()), Path.Combine(db, "4.00.json"));
            string output = Path.Combine(_root, "out");
            var result = Pipeline.Backport(game, output,
                new BackportOptions { Firmware = "4", ReferenceDir = db, KnownFixes = false, PatchParam = true }, Knowledge.Default,
                cancel: TestContext.Current.CancellationToken);
            Assert.True(result.Published);
            string folder = Assert.Single(Directory.GetDirectories(output));
            Assert.Equal("Sce_Sys", Path.GetFileName(folder));
            string file = Assert.Single(Directory.GetFiles(folder));
            Assert.Equal("Param.json", Path.GetFileName(file));
            Assert.Contains(Firmware.ParamVersion("4"), File.ReadAllText(file));
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
#endregion
#endif

#if !BACKPORT_SYNTHETIC_ONLY
#region GeneratedParserTests
namespace Ps5Backport.Tests
{
    using FsCheck.Xunit;
    using Ps5Backport.Core;

    /// <summary>Bounded generated properties; FsCheck integrates with the existing xUnit v3 runner.</summary>
    public sealed class GeneratedParserTests
    {
        [Property(MaxTest = 200, EndSize = 64, Replay = "12345,67891")]
        public void FirmwareNames_RoundTrip(byte major, byte minor)
        {
            int m = major % 100, n = minor % 100;
            Assert.Equal((m, n), Firmware.Parse(Firmware.Name($"{m}.{n}")));
        }

        [Property(MaxTest = 200, EndSize = 64, Replay = "54321,67891")]
        public void FirmwareSdk_UsesDecimalDigitsInBcd(byte major, byte minor)
        {
            int m = 1 + major % 11, n = minor % 100;
            uint bcd = (uint)((n / 10) * 16 + n % 10);
            var pair = Firmware.SdkFor($"{m}.{n}");
            Assert.Equal((Firmware.SdkPairs[m].Ps5 & 0xFF00FFFF) | (bcd << 16), pair.Ps5);
            Assert.Equal(Firmware.SdkPairs[m].Ps4, pair.Ps4);
        }

        [Property(MaxTest = 200, EndSize = 64, Replay = "45678,67891")]
        public void Firmware_RejectsUnsupportedRanges(ushort value)
        {
            int invalid = 100 + value;
            Assert.Throws<FormatException>(() => Firmware.Parse($"{invalid}.00"));
            Assert.Throws<FormatException>(() => Firmware.Parse($"1.{invalid}"));
            Assert.Throws<ArgumentException>(() => Firmware.SdkFor($"{12 + value % 88}.00"));
        }

        [Theory]
        [InlineData("+1")]
        [InlineData("-0")]
        [InlineData("4.+1")]
        [InlineData("4. 1")]
        [InlineData("４.０１")]
        [InlineData("2147483648.0")]
        public void Firmware_RejectsNonCanonicalTokens(string text) => Assert.Throws<FormatException>(() => Firmware.Parse(text));

        [Fact]
        public void Firmware_WhitespaceAndMissingMinorRemainCompatible()
        {
            Assert.Equal((4, 0), Firmware.Parse(" 4 "));
            Assert.Equal((4, 0), Firmware.Parse("4."));
            Assert.Equal("0x0403000000000000", Firmware.ParamVersion("4.03"));
        }

        [Property(MaxTest = 200, EndSize = 64, Replay = "23456,67891")]
        public void BpsLiteral_RoundTripsAndRejectsCorruption(byte[] source, byte[] target)
        {
            source = source.Take(64).ToArray(); target = target.Take(64).ToArray();
            byte[] bytes = SyntheticBinary.LiteralPatch(source, target);
            Assert.Equal(target, Bps.Apply(Bps.Parse(bytes), source));
            bytes[^1] ^= 1;
            Assert.Throws<InvalidDataException>(() => Bps.Parse(bytes));
        }

        [Property(MaxTest = 200, EndSize = 64, Replay = "34567,67891")]
        public void ElfSelf_RoundTripsAndRejectsTruncation(byte[] payload, byte truncate)
        {
            byte[] elf = SyntheticBinary.Elf(payload.Take(64).ToArray());
            byte[] self = Self.Sign(elf);
            Assert.Equal(elf, Self.Unwrap(self));
            Assert.Equal(Self.KindPs4Self, Self.Kind(self));
            Assert.Equal(0, Self.EncryptedSegments(self));
            Assert.Throws<InvalidDataException>(() => new SceElf(elf.Take(truncate % 64).ToArray()));
            Assert.Throws<InvalidDataException>(() => Self.Unwrap(self.Take(truncate % 32).ToArray()));
        }
    }
}
#endregion
#endif

#if !BACKPORT_SYNTHETIC_ONLY && !BACKPORT_PARSER_TESTS_ONLY
#region GroundTruthTests
namespace Ps5Backport.Tests
{
    using System.Buffers.Binary;
    using System.Text.Json;
    using Ps5Backport.Core;

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

    [Trait("Lane", "Private")]
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
                // GT7's call-site savedata patch is replaced by the generic sceSaveDataMount3 argument fix (a default rule below FW 5.00)
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
                    // the cave chain runs every instruction; call_got / the tail jmp go through the import's own GOT slot
                    var (executed, called, jumped) = Samples.Trace(elf, elf.Imports().First(i => i.Plt && i.GotVaddr == got[n]), fix);
                    Assert.Equal(fix.Insns.Select(i => i.Hex), executed);
                    Assert.Equal(got[n], fix.PostCall ? called : jumped);
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
            var table = k.FixTable([]);   // sceSaveDataMount3 is a default rule below FW 5.00
            FixApplier.Apply(elf, [nid], k, table);
            var (executed, _, jumped) = Samples.Trace(elf, imp, table[nid]);
            Assert.Equal(imp.GotVaddr, jumped);
            Assert.Equal(table[nid].Insns.Select(i => i.Hex), executed);
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
    [Trait("Lane", "Private")]
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
            var table = k.FixTable(["audio_speaker_compat"]);
            FixApplier.Apply(elf, [nid], k, table);
            var (executed, called, _) = Samples.Trace(elf, imp, table[nid]);
            Assert.Equal(imp.GotVaddr, called);
            Assert.Equal(table[nid].Insns.Select(i => i.Hex), executed);
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

    [Trait("Lane", "Private")]
    public class PatchEngineTests
    {
        [Fact]
        public void CodeReferences_Counts()
        {
            var elf = SceElf.Load(Samples.Work("NOT_BACKPORT_elf", "eboot.elf"));
            var reference = Imports.LoadReference(Repo.Need("firmware_db"), "4");
            var statuses = Imports.Classify(elf, reference.Nids, exact: reference.Exact).Where(s => s.Nid is "2jiIxUmcsGo" or "bIi4YUfSRys" or "f3kPeoTZnIE");
            var refs = Imports.CodeReferences(elf, statuses, cancel: TestContext.Current.CancellationToken);
            Assert.Equal(1, refs["2jiIxUmcsGo"]);   // sceTextToSpeech2Cancel: only a conditional tail call, je rel32 at 0x180bc02
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
            Assert.Equal(1, FixApplier.ReplaceAll(data, strap, TestContext.Current.CancellationToken, 0, data.Length, 4));   // only offset 8
            Assert.Equal(strap.Find, data[1..5]);
            Assert.Equal(strap.Replace, data[8..12]);

            var elf = SceElf.Load(Samples.Work("NOT_BACKPORT_elf", "eboot.elf"));
            var t = elf.TextSegment();
            byte[] code = elf.Data[(int)t.Offset..(int)(t.Offset + t.Filesz)];
            Assert.Equal(202, FixApplier.ApplyPattern(elf, strap, cancel: TestContext.Current.CancellationToken));                  // GT7's SDK BVH shaders
            Assert.Equal(code, elf.Data[(int)t.Offset..(int)(t.Offset + t.Filesz)]);
            Assert.Equal(0, FixApplier.ApplyPattern(elf, strap, cancel: TestContext.Current.CancellationToken));
            // the default patterns (Wwise, EA) leave GT7 alone
            Assert.All(k.Patterns, p => Assert.Equal(0, FixApplier.ApplyPattern(SceElf.Load(Samples.Work("NOT_BACKPORT_elf", "eboot.elf")), p, cancel: TestContext.Current.CancellationToken)));
        }

        /// <summary>CONTROL's backport already changed the Wwise speaker-setup site; the channel-switch site is still 7.1.4.</summary>
        [Fact]
        public void WwiseCodePatterns_OnControl()
        {
            var k = Samples.K;
            var elf = SceElf.Load(Samples.Work("CONTROL_elf", "eboot.elf"));
            var (setup, change, packed) = (k.Patterns[0], k.Patterns[1], k.Patterns[2]);
            Assert.True(setup.Code && change.Code && packed.Code);
            Assert.Equal(0, FixApplier.ApplyPattern(elf, setup, cancel: TestContext.Current.CancellationToken));
            Assert.Equal(1, FixApplier.ApplyPattern(elf, change, cancel: TestContext.Current.CancellationToken));
            Assert.Equal(0, FixApplier.ApplyPattern(elf, change, cancel: TestContext.Current.CancellationToken));
            Assert.Equal(1, FixApplier.ApplyPattern(elf, packed, cancel: TestContext.Current.CancellationToken));
        }
    }

    [Trait("Lane", "Private")]
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
                                              store: Path.Combine(Repo.Root, "fakelibs"), options: ["force_pro", "gpu_scratch", "save_prepare_compat"]);
                // gpu_scratch reproduces AC's own files: ShaderData.bin of 4xx+, the hooked AgcDriver of 10xx+
                Assert.Equal(File.ReadAllBytes(Path.Combine(ac, "4xx+", "ShaderData.bin")), File.ReadAllBytes(Path.Combine(output, "ShaderData.bin")));
                Assert.Equal(File.ReadAllBytes(Path.Combine(ac, "10xx+", "fakelib", "libSceAgcDriver.sprx")),
                             File.ReadAllBytes(Path.Combine(output, "fakelib", "libSceAgcDriver.sprx")));
                var mods = result.Modules.ToDictionary(m => m.Path);
                Assert.Equal(["force_pro", "gpu_scratch", "save_prepare_compat"], result.Options);
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

    [Trait("Lane", "Private")]
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

    }
}
#endregion
#endif

#if !BACKPORT_SYNTHETIC_ONLY && !BACKPORT_PARSER_TESTS_ONLY
#region HeadlessInteractionTests
namespace Ps5Backport.Tests
{
    using System.Diagnostics;

    public class HeadlessInteractionTests
    {
        [Theory]
        [InlineData("busy-cancel")]
        [InlineData("close-busy")]
        [InlineData("search-selection")]
        [InlineData("stale-analysis")]
        [InlineData("close-save-failure")]
        public async Task WindowBindingsAndInput_RunOnAnIsolatedHeadlessDispatcher(string scenario)
        {
            string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ps5backport-headless-" + Guid.NewGuid().ToString("N"))).FullName;
    #if DEBUG
            const string configuration = "Debug";
    #else
            const string configuration = "Release";
    #endif
            string worker = Path.Combine(Repo.Root, "verification", "UiProfile", "bin", configuration, "net10.0", "UiProfile.dll");
            var info = new ProcessStartInfo("dotnet") { WorkingDirectory = Repo.Root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { worker, "--check", scenario, root }) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            try
            {
                await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                Assert.True(process.ExitCode == 0, $"{scenario}: {await stdout}\n{await stderr}");
                Assert.Contains("Passed: " + scenario, await stdout);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(TestContext.Current.CancellationToken); }
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
#endregion
#endif

#if !BACKPORT_SYNTHETIC_ONLY && !BACKPORT_PARSER_TESTS_ONLY
#region ImportLibraryRenameTests
namespace Ps5Backport.Tests
{
    using System.Text;
    using Ps5Backport.Core;

    public sealed class ImportLibraryRenameTests
    {
        /// <summary>A module without an executable PT_LOAD is malformed input for patching: InvalidDataException,
        /// which the pipeline reports as a module error (InvalidOperationException would abort the whole run).</summary>
        [Fact]
        public void ModuleWithoutCode_IsADataError()
        {
            byte[] data = SyntheticBinary.Elf(imports: true);
            SyntheticBinary.W32(data, 0x40 + 4, 4);       // PT_LOAD #0: read-only, no execute flag
            var elf = new SceElf(data);
            Assert.Throws<InvalidDataException>(() => elf.TextSegment());
            Assert.Throws<InvalidDataException>(() => Patching.FindCave(elf, 8));
        }

        /// <summary>String-table suffix merging also works the other way: the import's name can be the tail of a
        /// longer name. Renaming in place would then corrupt that other symbol, so it must be refused.</summary>
        [Fact]
        public void NameThatIsTheTailOfAnotherName_IsRefusedWithoutChangingBytes()
        {
            byte[] data = SyntheticBinary.Elf(imports: true);
            data[0x200] = (byte)'Q';                      // "QXXXXXXXXXXX#A#A" at 0x200; the import's own name starts at 0x201
            SyntheticBinary.W32(data, 0x2B0, 0);          // symbol 2's name = the longer string
            var elf = new SceElf(data);
            Assert.Equal("Q" + SyntheticBinary.Nid + "#A#A", elf.Symbols()[2].Name);
            var original = data.ToArray();
            Assert.Contains("shared", Assert.Throws<PatchException>(() => Patching.RenameImport(elf, elf.Imports()[0], "04AjkP0jO9U")).Message);
            Assert.Equal(original, data);
        }

        [Fact]
        public void CrossLibraryRename_UsesTargetLibraryAndModuleIds()
        {
            var elf = Fixture();
            var source = elf.Imports().Single(i => i.Library == "libkernel_sync_on_address");
            Patching.RenameImport(elf, source, "04AjkP0jO9U", "libkernel");
            Assert.Equal("04AjkP0jO9U#A#G", elf.Symbols()[source.SymIndex].Name);
            Assert.Equal("libkernel", elf.Imports().Single(i => i.Nid == "04AjkP0jO9U").Library);
            Assert.Equal("YYYYYYYYYYY#A#G", elf.Symbols()[2].Name);
        }

        [Fact]
        public void NidOnlyRename_ReproducesTheWrongSyncLibrary()
        {
            var elf = Fixture();
            Patching.RenameImport(elf, elf.Imports()[0], "04AjkP0jO9U");
            Assert.Equal("04AjkP0jO9U#B#B", elf.Symbols()[1].Name);
            Assert.Equal("libkernel_sync_on_address", elf.Imports()[0].Library);
        }

        [Fact]
        public void MissingTargetLibrary_FailsWithoutChangingBytes()
        {
            var elf = Fixture();
            var original = elf.Data.ToArray();
            Assert.Throws<PatchException>(() => Patching.RenameImport(elf, elf.Imports()[0], "04AjkP0jO9U", "absent"));
            Assert.Equal(original, elf.Data);
        }

        [Fact]
        public void LongerTargetSuffix_FailsWithoutChangingBytes()
        {
            var elf = Fixture("#AA#G");
            var original = elf.Data.ToArray();
            Assert.Throws<PatchException>(() => Patching.RenameImport(elf, elf.Imports()[0], "04AjkP0jO9U", "libkernel"));
            Assert.Equal(original, elf.Data);
        }

        [Fact]
        public void AmbiguousTargetModule_FailsWithoutChangingBytes()
        {
            var elf = Fixture();
            var data = elf.Data;
            SyntheticBinary.W64(data, 0x128, 96); // DT_SCE_SYMTABSZ: add a third import.
            SyntheticBinary.W64(data, 0x148, 72);
            Encoding.ASCII.GetBytes("ZZZZZZZZZZZ#A#H\0").CopyTo(data, 0x270);
            SyntheticBinary.W32(data, 0x2C8, 0x70);
            data[0x2CC] = SceElf.SttFunc;
            SyntheticBinary.W64(data, 0x330, 0x490);
            SyntheticBinary.W64(data, 0x338, (3UL << 32) | 1);
            elf = new SceElf(data);
            var original = data.ToArray();
            Assert.Throws<PatchException>(() => Patching.RenameImport(elf, elf.Imports()[0], "04AjkP0jO9U", "libkernel"));
            Assert.Equal(original, data);
        }

        [Fact]
        public void SyncAdapters_ExplicitlyTargetLibkernel()
        {
            foreach (string nid in new[] { "B2n8aDorSH4", "PZQhiiLXRFs", "q2y-wDIVWZA", "Hc4CaR6JBL0" })
            {
                var fix = Knowledge.Default.Fixes[nid];
                Assert.Equal("04AjkP0jO9U", fix.NewNid);
                Assert.Equal("libkernel", fix.NewLibrary);
            }
        }

        /// <summary>The Wait adapters hand _umtx_op a real timespec: the u32 microsecond timeout is converted in the
        /// adapter's own stack frame instead of passing the u32 pointer through (FINDINGS §6d).</summary>
        [Fact]
        public void SyncWaitAdapters_ConvertMicrosecondTimeout()
        {
            foreach (string nid in new[] { "B2n8aDorSH4", "PZQhiiLXRFs", "Hc4CaR6JBL0" })
            {
                var fix = Knowledge.Default.Fixes[nid];
                Assert.True(fix.PostCall, nid);
                Assert.DoesNotContain(fix.Insns, i => !i.CallGot && (i.Bytes[0] is >= 0x70 and <= 0x7F ||
                                                                     i.Bytes[0] == 0x0F && i.Bytes[1] is >= 0x80 and <= 0x8F));   // branch-free: may be split across caves
                string[] hex = fix.Insns.Select(i => i.Hex).ToArray();
                Assert.Equal(["55", "4889e5", "4883ec10"], hex[..3]);       // frame + 16-byte timespec; rsp stays 16-aligned for the call
                Assert.Contains("41b940420f00", hex);                        // microseconds / 1000000 = tv_sec
                Assert.Contains("69d2e8030000", hex);                        // remainder * 1000 = tv_nsec
                Assert.Contains("4989e0", hex);                              // uaddr2 = &timespec
                Assert.Equal([Insn.CallGotToken, "c9", "c3"], hex[^3..]);    // call _umtx_op; leave; ret
            }
        }

        static SceElf Fixture(string targetSuffix = "#A#G")
        {
            var data = SyntheticBinary.Elf(imports: true);
            Array.Clear(data, 0x200, 0x80);
            Text(1, "XXXXXXXXXXX#B#B");
            Text(0x20, "YYYYYYYYYYY" + targetSuffix);
            Text(0x40, "libkernel_sync_on_address");
            Text(0x60, "libkernel");
            SyntheticBinary.W32(data, 0x2B0, 0x20);
            SyntheticBinary.W64(data, 0x148, 48); // DT_RELASZ: both symbols are imports.
            SyntheticBinary.W64(data, 0x150, SceElf.DtSceImportLib);
            SyntheticBinary.W64(data, 0x158, (1UL << 48) | 0x40);
            SyntheticBinary.W64(data, 0x160, SceElf.DtSceImportLib);
            SyntheticBinary.W64(data, 0x168, 0x60);
            SyntheticBinary.W64(data, 0x318, 0x488);
            SyntheticBinary.W64(data, 0x320, (2UL << 32) | 1);
            return new SceElf(data);

            void Text(int offset, string value) => Encoding.ASCII.GetBytes(value + "\0").CopyTo(data, 0x200 + offset);
        }
    }
}
#endregion
#endif

#if !BACKPORT_SYNTHETIC_ONLY && !BACKPORT_PARSER_TESTS_ONLY
#region OperationLifecycleTests
namespace Ps5Backport.Tests
{
    using System.Diagnostics;
    using Ps5Backport.App;
    using Ps5Backport.App.ViewModels;
    using Ps5Backport.Core;

    public sealed class OperationLifecycleTests : IDisposable
    {
        readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ps5backport-operation-" + Guid.NewGuid().ToString("N"))).FullName;
        string Game => Path.Combine(_root, "game");
        string Output => Path.Combine(_root, "out");
        string Reference => Path.Combine(_root, "reference");
        BackportOptions Options => new() { Firmware = "4", ReferenceDir = Reference, KnownFixes = false, DryRun = true };

        public OperationLifecycleTests()
        {
            Directory.CreateDirectory(Game);
            Directory.CreateDirectory(Reference);
            File.WriteAllBytes(Path.Combine(Game, "eboot.bin"), SyntheticBinary.Elf(imports: true));
            FirmwareDb.Save(new FirmwareExports("4", "synthetic", false, new()), Path.Combine(Reference, "4.00.json"));
        }

        [Fact]
        public async Task SuccessfulOperation_IsOneShot_AndAnalysisWritesNothing()
        {
            using var operation = new BackportOperation(Game, Output, Options, Knowledge.Default);
            Assert.Equal(OperationState.Pending, operation.State);
            var result = await operation.ExecuteAsync(cancel: TestContext.Current.CancellationToken);
            Assert.Equal(OperationState.Completed, operation.State);
            Assert.Equal(OperationState.Completed, result.State);
            Assert.NotNull(result.Analysis);
            Assert.False(result.Published);
            Assert.False(Directory.Exists(Output));
            Assert.DoesNotContain(Directory.GetFileSystemEntries(_root), p => Path.GetFileName(p).StartsWith(".ps5bp-"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => operation.ExecuteAsync(cancel: TestContext.Current.CancellationToken));
            Assert.Equal(OperationState.Completed, operation.State);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CancelledOperation_NeverReportsDoneOrPublishes(bool duringPatch)
        {
            using var operation = new BackportOperation(Game, Output, Options with { DryRun = false }, Knowledge.Default);
            var seen = new List<BackportProgress>();
            if (!duringPatch) operation.Cancel();
            var progress = new SyncProgress(p =>
            {
                seen.Add(p);
                if (p.Stage == "patching")
                {
                    Assert.Equal(OperationState.Running, operation.State);
                    Assert.Throws<InvalidOperationException>(operation.Dispose);
                    operation.Cancel();
                }
            });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.ExecuteAsync(progress, TestContext.Current.CancellationToken));
            Assert.Equal(OperationState.Cancelled, operation.State);
            Assert.DoesNotContain(seen, p => p.Stage == "done");
            Assert.False(Directory.Exists(Output));
            Assert.DoesNotContain(Directory.GetDirectories(_root), p => Path.GetFileName(p).Contains("stage"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Failures_HaveTerminalFailedState(bool malformedModule)
        {
            if (malformedModule) File.WriteAllBytes(Path.Combine(Game, "eboot.bin"), [1, 2, 3]);
            using var operation = new BackportOperation(Game, Output,
                malformedModule ? Options : Options with { Firmware = "invalid" }, Knowledge.Default);
            var seen = new List<BackportProgress>();
            if (malformedModule)
            {
                var result = await operation.ExecuteAsync(new SyncProgress(seen.Add), TestContext.Current.CancellationToken);
                Assert.Null(result.Analysis);
                Assert.Equal(OperationState.Failed, result.State);
                Assert.Equal("failed", seen.Last().Stage);
                Assert.NotEqual(seen.Last().Count, seen.Last().Index);
            }
            else await Assert.ThrowsAsync<FormatException>(() => operation.ExecuteAsync(cancel: TestContext.Current.CancellationToken));
            Assert.Equal(OperationState.Failed, operation.State);
            Assert.DoesNotContain(seen, p => p.Stage == "done");
        }

        [Fact]
        public async Task RequestCollections_AreCapturedBeforeBackgroundWork()
        {
            var selected = new List<string>();
            var stubs = new List<string>();
            using var operation = new BackportOperation(Game, Output, Options with { Options = selected, StubNids = stubs }, Knowledge.Default);
            selected.Add("not-an-option");
            stubs.Add(SyntheticBinary.Nid);
            var result = await operation.ExecuteAsync(cancel: TestContext.Current.CancellationToken);
            Assert.Empty(result.Options);
            Assert.Empty(result.Modules.Single().StubbedNow);
        }

        [Fact]
        public void RuleSnapshot_DeepCopiesNestedBytesAndCollections()
        {
            var knowledge = new Knowledge(Repo.Need("data"));
            var snapshot = RunSnapshot.Capture(Game, Output, Options, knowledge);
            var stub = knowledge.Stubs.Values.First(s => s.Bytes.Length > 0);
            byte before = snapshot.Knowledge.Stubs[stub.Name].Bytes[0];
            stub.Bytes[0] ^= 0xff;
            Assert.Equal(before, snapshot.Knowledge.Stubs[stub.Name].Bytes[0]);
            var fix = knowledge.FixList.First(f => f.Insns.Any(i => i.Bytes.Length > 0));
            var instruction = fix.Insns.First(i => i.Bytes.Length > 0);
            var captured = snapshot.Knowledge.Fixes[fix.Nid].Insns.First(i => i.Bytes.Length > 0).Bytes.ToArray();
            instruction.Bytes[0] ^= 0xff;
            Assert.Equal(captured, snapshot.Knowledge.Fixes[fix.Nid].Insns.First(i => i.Bytes.Length > 0).Bytes);
            ((Dictionary<string, FixOption>)knowledge.Options).Clear();
            Assert.NotEmpty(snapshot.Knowledge.Options);
        }

        [Theory]
        [InlineData("module")]
        [InlineData("module-added")]
        [InlineData("reference")]
        [InlineData("param")]
        [InlineData("settings")]
        [InlineData("rules")]
        [InlineData("ampr-warning")]
        [InlineData("library")]
        [InlineData("names")]
        [InlineData("introduced")]
        public void StaleAnalysis_IsRefusedBeforeCreatingOutput(string change)
        {
            var knowledge = new Knowledge(Repo.Need("data"));
            var options = Options;
            if (change == "library") options = options with { ExtraFakelib = Directory.CreateDirectory(Path.Combine(_root, "libraries")).FullName };
            var analyzed = Pipeline.Backport(Game, Output, options, knowledge, cancel: TestContext.Current.CancellationToken);
            var cook = options with { DryRun = false };
            switch (change)
            {
                case "module": File.WriteAllBytes(Path.Combine(Game, "eboot.bin"), SyntheticBinary.Elf(imports: true, exports: true)); break;
                case "module-added": File.WriteAllBytes(Path.Combine(Game, "new.sprx"), SyntheticBinary.Elf()); break;
                case "reference": FirmwareDb.Save(new FirmwareExports("4", "changed", false, new() { ["synthetic"] = new() { SyntheticBinary.Nid } }), Path.Combine(Reference, "4.00.json")); break;
                case "param": Directory.CreateDirectory(Path.Combine(Game, "sce_sys")); File.WriteAllText(Path.Combine(Game, "sce_sys", "param.json"), "{}"); break;
                case "settings": cook = cook with { ExtendText = false }; break;
                case "rules": knowledge.Stubs.Values.First(s => s.Bytes.Length > 0).Bytes[0] ^= 0xff; break;
                case "ampr-warning": File.WriteAllText(Path.Combine(Game, "ampr_emu.index"), "new index"); break;
                case "library": File.WriteAllBytes(Path.Combine(options.ExtraFakelib!, "new.sprx"), SyntheticBinary.Elf(exports: true)); break;
                case "names": File.WriteAllText(Path.Combine(_root, "names.csv"), SyntheticBinary.Nid + " renamed"); cook = cook with { Names = new NameDb(Path.Combine(_root, "names.csv")) }; break;
                case "introduced": ((Dictionary<string, (string Firmware, string Source)>)knowledge.Introduced)[knowledge.Introduced.Keys.First()] = ("99.00", "changed"); break;
            }
            var exception = Assert.Throws<InvalidOperationException>(() => Pipeline.Backport(Game, Output, cook, knowledge,
                cancel: TestContext.Current.CancellationToken, analysis: analyzed.Analysis));
            Assert.Contains("Analysis is stale", exception.Message);
            Assert.False(Directory.Exists(Output));
            Assert.DoesNotContain(Directory.GetFileSystemEntries(_root), p => Path.GetFileName(p).StartsWith(".ps5bp-"));
        }

        [Fact]
        public void CurrentAnalysis_AllowsFullCook_WithLegacyReportShape()
        {
            var analyzed = Pipeline.Backport(Game, Output, Options, Knowledge.Default, cancel: TestContext.Current.CancellationToken);
            var cooked = Pipeline.Backport(Game, Output, Options with { DryRun = false }, Knowledge.Default,
                cancel: TestContext.Current.CancellationToken, analysis: analyzed.Analysis);
            Assert.True(cooked.Published);
            Assert.Equal(analyzed.Modules.Single().Statuses, cooked.Modules.Single().Statuses);
            Assert.Equal(analyzed.Modules.Single().SdkAfter, cooked.Modules.Single().SdkAfter);
            Assert.Null(cooked.Analysis);
            Assert.DoesNotContain("state", Pipeline.ToJson(cooked).Select(p => p.Key), StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("analysis", Pipeline.ToJson(cooked).Select(p => p.Key), StringComparer.OrdinalIgnoreCase);
            Publication.Verify(Output);
        }

        [Fact]
        public async Task UiFailure_AndCloseNeverShowSuccessProgress()
        {
            var vm = new MainViewModel(new AppSettings { FirmwareDbDir = Reference, FakelibStoreDir = Path.Combine(_root, "absent-store") });
            vm.LoadGame(Game);
            vm.SelectedFirmware = "4";
            File.WriteAllBytes(Path.Combine(Game, "eboot.bin"), [1, 2, 3]);
            await vm.AnalyzeCommand.ExecuteAsync(null);
            Assert.Equal(OperationState.Failed, vm.OperationState);
            Assert.Equal(0, vm.Progress);
            Assert.StartsWith("Incomplete", vm.StatusText);
            await vm.CancelAndWaitAsync();
            Assert.False(vm.Busy);
            Assert.False(vm.AnalyzeCommand.CanExecute(null));
            vm.Dispose();
        }

        [Fact]
        public async Task ClosingWhileBusy_CancelsAndAwaitsTheRun()
        {
            var vm = new MainViewModel(new AppSettings { FirmwareDbDir = Reference, FakelibStoreDir = Path.Combine(_root, "absent-store") });
            vm.LoadGame(Game);
            var run = vm.AnalyzeCommand.ExecuteAsync(null);
            Assert.True(vm.Busy);
            await vm.CancelAndWaitAsync();
            await run;
            Assert.False(vm.Busy);
            Assert.Equal(OperationState.Cancelled, vm.OperationState);
            Assert.Equal(0, vm.Progress);
            Assert.Equal("Cancelled", vm.StatusText);
            Assert.False(vm.CookCommand.CanExecute(null));
            vm.Dispose();
        }

        [Fact]
        public async Task DelayedProgress_CannotOverwriteTerminalUiState()
        {
            var vm = new MainViewModel(new AppSettings { FirmwareDbDir = Reference, FakelibStoreDir = Path.Combine(_root, "absent-store") });
            vm.LoadGame(Game);
            var queue = new ProgressQueue();
            var previous = SynchronizationContext.Current;
            Task run;
            try
            {
                SynchronizationContext.SetSynchronizationContext(queue);
                run = vm.AnalyzeCommand.ExecuteAsync(null);
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            await run;
            Assert.Equal(OperationState.Completed, vm.OperationState);
            string terminal = vm.StatusText;
            Assert.True(queue.Drain() > 0);
            Assert.Equal(terminal, vm.StatusText);
            Assert.Equal(100, vm.Progress);
            vm.Dispose();
        }

        [Fact]
        public async Task UiCook_RefusesChangedSettingsAfterAnalysis()
        {
            var vm = new MainViewModel(new AppSettings { FirmwareDbDir = Reference, FakelibStoreDir = Path.Combine(_root, "absent-store") });
            vm.LoadGame(Game);
            await vm.AnalyzeCommand.ExecuteAsync(null);
            vm.ExtendText = !vm.ExtendText;
            vm.OutputFolder = Output;
            await vm.CookCommand.ExecuteAsync(null);
            Assert.Equal(OperationState.Failed, vm.OperationState);
            Assert.Equal(0, vm.Progress);
            Assert.False(Directory.Exists(Output));
            Assert.Contains("Analysis is stale", vm.Log.Text());
            vm.Dispose();
        }

        [Fact]
        public async Task Closing_AwaitsAlreadyOwnedPageWork()
        {
            var vm = new MainViewModel(new AppSettings { FirmwareDbDir = Reference });
            var picker = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.PickFolder = _ => picker.Task;
            vm.Database.ImportFirmware = "4";
            vm.Database.ImportSource = "synthetic";
            vm.Database.ImportGenstub = true;
            string empty = Directory.CreateDirectory(Path.Combine(_root, "empty-genstub")).FullName;
            var import = vm.Database.ImportCommand.ExecuteAsync(null);
            var close = vm.CancelAndWaitAsync();
            Assert.False(close.IsCompleted);
            picker.SetResult(empty);
            await close;
            await import;
            Assert.False(vm.Database.Busy);
            Assert.Contains("nothing written", vm.Log.Text());
            vm.Dispose();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task InnerScans_RespondToCancellationWithinBudget(bool codeReferences)
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            using var entered = new ManualResetEventSlim();
            using var proceed = new ManualResetEventSlim();
            byte[] data = new byte[128 * 1024 * 1024];
            var elf = new SceElf(SyntheticBinary.Elf(payload: data, imports: true));
            var statuses = Imports.Classify(elf, new HashSet<string>(), new HashSet<string>(), true);
            var task = Task.Run(() =>
            {
                entered.Set();
                proceed.Wait(TestContext.Current.CancellationToken);
                if (codeReferences) Imports.CodeReferences(elf, statuses, cancel.Token);
                else FixApplier.ReplaceAll(data, new FixPattern("dense", [0], [1], []), cancel.Token);
            }, TestContext.Current.CancellationToken);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            proceed.Set();
            await Task.Delay(10, TestContext.Current.CancellationToken);
            var timer = Stopwatch.StartNew();
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(4)]
        public void ScanWindows_PreserveBoundaryMatchesAndAlignment(int step)
        {
            byte[] data = new byte[2 * 1024 * 1024 + 10];
            byte[] find = [7, 8, 9, 10];
            foreach (int at in new[] { 1024 * 1024 - 2, 1024 * 1024 + 4 }) find.CopyTo(data, at);
            int count = FixApplier.ReplaceAll(data, new FixPattern("boundary", find, [1, 2, 3, 4], []), TestContext.Current.CancellationToken, step: step);
            Assert.Equal(step == 1 ? 2 : 1, count);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, data.AsSpan(1024 * 1024 + 4, 4).ToArray());
            Assert.Equal(step == 1 ? new byte[] { 1, 2, 3, 4 } : find, data.AsSpan(1024 * 1024 - 2, 4).ToArray());
        }

        sealed class SyncProgress(Action<BackportProgress> report) : IProgress<BackportProgress>
        {
            public void Report(BackportProgress value) => report(value);
        }

        sealed class ProgressQueue : SynchronizationContext
        {
            readonly List<Action> _pending = [];
            public override void Post(SendOrPostCallback callback, object? state)
            {
                if (state is BackportProgress)
                {
                    lock (_pending) _pending.Add(() => callback(state));
                }
                else ThreadPool.QueueUserWorkItem(_ => callback(state));
            }
            internal int Drain()
            {
                lock (_pending)
                {
                    foreach (var callback in _pending) callback();
                    return _pending.Count;
                }
            }
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
#endregion
#endif

#if !BACKPORT_SYNTHETIC_ONLY && !BACKPORT_PARSER_TESTS_ONLY
#region ParserBudgetTests
namespace Ps5Backport.Tests
{
    using System.Diagnostics;
    using Ps5Backport.Core;

    /// <summary>Local deterministic mutation corpus, isolated in a child process with finite resource budgets.</summary>
    public sealed class ParserBudgetTests
    {
        const int MaximumBytes = 64 * 1024, Cases = 512;

        [Theory]
        [InlineData("elf")]
        [InlineData("self")]
        [InlineData("bps")]
        public async Task MutatedReaders_StayWithinBudgets(string format)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.ArgumentList.Add(typeof(ParserBudgetTests).Assembly.Location);
            start.ArgumentList.Add("-method"); start.ArgumentList.Add("Ps5Backport.Tests.ParserBudgetTests.MutationWorker");
            start.Environment["PS5BP_FUZZ_FORMAT"] = format;
            start.Environment["DOTNET_GCHeapHardLimit"] = "10000000"; // hexadecimal: 256 MiB managed heap, runtime-enforced
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
            Assert.True(process.ExitCode == 0, $"{format} mutation worker failed:\n{await output}\n{await error}");
        }

        [Fact]
        public void MutationWorker()
        {
            string? format = Environment.GetEnvironmentVariable("PS5BP_FUZZ_FORMAT");
            if (format is null) return;
            Assert.Contains(format, new[] { "elf", "self", "bps" });
            byte[] seed = format switch
            {
                "elf" => SyntheticBinary.Elf(imports: true, exports: true),
                "self" => Self.Sign(SyntheticBinary.Elf()),
                _ => SyntheticBinary.LiteralPatch([1, 2, 3], [4, 5, 6]),
            };
            var random = new Random(305419896);
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < Cases; i++)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                byte[] data = seed.ToArray();
                int at = random.Next(data.Length);
                switch (i % 4)
                {
                    case 0: data[at] ^= (byte)(1 << random.Next(8)); break;
                    case 1: data = data.Take(at).ToArray(); break;
                    case 2:
                        at = Math.Min(at, data.Length - 8);
                        SyntheticBinary.W64(data, at, i % 3 == 0 ? ulong.MaxValue : (ulong)MaximumBytes + 1);
                        break;
                    default: data = [.. data, .. new byte[random.Next(64)]]; break;
                }
                if (format == "bps" && data.Length >= 16)
                    SyntheticBinary.W32(data, data.Length - 4, Crc32.Compute(data.AsSpan(0, data.Length - 4)));
                Assert.InRange(data.Length, 0, MaximumBytes);
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                try
                {
                    if (format == "bps")
                        _ = Bps.Apply(Bps.Parse(data), [1, 2, 3], MaximumBytes);
                    else if (format == "self")
                    {
                        _ = Self.EncryptedSegments(data); _ = Self.DigestOffset(data);
                        _ = new SceElf(Self.Unwrap(data, MaximumBytes));
                    }
                    else
                    {
                        var elf = new SceElf(data);
                        _ = elf.Symbols(); _ = elf.Imports(); _ = elf.Exports(); _ = elf.ModuleName; _ = elf.BuildPath;
                        _ = Sdk.Read(elf);
                        _ = Imports.Classify(elf, new HashSet<string>(), exact: true);
                    }
                }
                catch (InvalidDataException) { /* Expected rejection of a malformed or over-budget file. */ }
                catch (Exception e) { Assert.Fail($"{format} seed=305419896 case={i}: {e}"); }
                Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated <= 8 * 1024 * 1024, $"{format} case={i}: allocation budget");
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{format} case={i}: time budget");
            }
        }

        [Fact]
        public void OutputLimits_RejectBeforeLargeAllocation()
        {
            byte[] self = Self.Sign(SyntheticBinary.Elf());
            int elfAt = 0x20 + BitConverter.ToUInt16(self, 0x18) * 0x20;
            SyntheticBinary.W64(self, elfAt + 0x40 + 8, 1024 * 1024);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            Assert.Throws<InvalidDataException>(() => Self.Unwrap(self, MaximumBytes));
            var patch = new Bps.Patch(0, 1024 * 1024, [], [], 0, 0);
            Assert.Throws<InvalidDataException>(() => Bps.Apply(patch, [], MaximumBytes));
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < MaximumBytes * 2);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(24)]
        [InlineData(31)]
        public void TruncatedSelfMetadata_IsADataError(int size)
        {
            byte[] self = Self.Sign(SyntheticBinary.Elf()).Take(size).ToArray();
            Assert.Throws<InvalidDataException>(() => Self.EncryptedSegments(self));
            Assert.Throws<InvalidDataException>(() => Self.DigestOffset(self));
        }
    }
}
#endregion
#endif

#if !BACKPORT_SYNTHETIC_ONLY && !BACKPORT_PARSER_TESTS_ONLY
#region PerformanceRegressionTests
namespace Ps5Backport.Tests
{
    using System.Security.Cryptography;
    using Ps5Backport.Core;

    public sealed class PerformanceRegressionTests
    {
        [Fact]
        public void Signing_AllocatesOutputWithoutCopyingEachSegment_AndLeavesInputUnchanged()
        {
            byte[] input = SyntheticBinary.Elf(new byte[16 * 1024 * 1024], imports: true);
            byte[] before = SHA256.HashData(input);
            // Warm cryptographic initialization separately from the allocation budget.
            _ = Self.Sign(SyntheticBinary.Elf());
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            byte[] signed = Self.Sign(input);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Assert.InRange(bytes, signed.Length, signed.Length + 1024 * 1024L);
            Assert.Equal(before, SHA256.HashData(input));
            Assert.Equal(input, Self.Unwrap(signed));
        }

        [Theory]
        [InlineData("4", "4.00", true)]
        [InlineData("4.50", "4.00", false)]
        public void RunOwnedFirmwareMetadata_PreservesSelectionAndDoesNotReadAgain(string target, string selected, bool exact)
        {
            string root = Path.Combine(Path.GetTempPath(), "ps5backport-metadata-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string file = Path.Combine(root, "4.json");
                FirmwareDb.Save(new FirmwareExports("4", "synthetic", false, new() { ["lib"] = ["AAAAAAAAAAA"] }), file);
                var loaded = FirmwareDb.LoadAll(root);
                File.WriteAllText(file, "not JSON");
                var reference = Imports.LoadReference(root, target, loaded);
                Assert.Equal(exact, reference.Exact);
                Assert.Contains("firmware_db " + selected, reference.Label);
                Assert.Contains("AAAAAAAAAAA", reference.Nids);
                Assert.ThrowsAny<Exception>(() => Imports.LoadReference(root, target));
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }
}
#endregion
#endif

#if !BACKPORT_SYNTHETIC_ONLY && !BACKPORT_PARSER_TESTS_ONLY
#region Phase0Tests
namespace Ps5Backport.Tests
{
    using Ps5Backport.Core;

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
        public void Compute_MatchesGolden_ForAerolibNames()
        {
            string aerolib = Repo.Need("data", "aerolib.csv");
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
        public void NameDb_MatchesGolden()
        {
            string aerolib = Repo.Need("data", "aerolib.csv");
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

        [Theory]
        [InlineData("abc")]
        [InlineData("4.x")]
        [InlineData("")]
        [InlineData(".5")]
        public void Parse_Invalid(string value) => Assert.Throws<FormatException>(() => Firmware.Parse(value));

        [Fact]
        public void SdkFor_UnknownMajor() => Assert.Throws<ArgumentException>(() => Firmware.SdkFor("12"));
    }
}
#endregion
#endif

#if !BACKPORT_SYNTHETIC_ONLY && !BACKPORT_PARSER_TESTS_ONLY
#region Phase1Tests
namespace Ps5Backport.Tests
{
    using System.Security.Cryptography;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Ps5Backport.Core;

    /// <summary>
    /// Every sample module (GT7 USA backport, GT7 original, Control backport), loaded once: file kind, encrypted
    /// segments, unwrapped ELF, re-signed SELF and the ELF model.
    /// </summary>
    public sealed class SampleSet
    {
        public static readonly string[] SampleRoots =
            [Path.Combine("backports", "PPSA01317", "4xx+", "USA"), Path.Combine("backports", "PPSA01317", "default"), Path.Combine("backports", "PPSA34547", "4xx+")];

        public sealed record Sample(string Source, string Rel, string Kind, int Encrypted, byte[]? Elf);

        public IReadOnlyList<Sample> Samples { get; } = [];

        public SampleSet()
        {
            string original = Path.Combine(Repo.Root, ".original");
            if (Repo.RequirePrivate)
                foreach (string root in SampleRoots)
                    Repo.Need(".original", root);
            Samples = SampleRoots
                .Select(r => Path.Combine(original, r))
                .Where(Directory.Exists)
                .SelectMany(r => Directory.EnumerateFiles(r, "*", SearchOption.AllDirectories))
                .Where(f => Path.GetFileName(f).Equals("eboot.bin", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".prx", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".sprx", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
                .Select(f =>
                {
                    string kind = Self.FileKind(f);
                    int encrypted = Self.IsSelf(kind) ? Self.EncryptedSegments(f) : 0;
                    byte[]? elf = Self.IsSelf(kind) ? Self.Unwrap(File.ReadAllBytes(f)) : kind == Self.KindElf ? File.ReadAllBytes(f) : null;
                    return new Sample(f, Path.GetRelativePath(original, f).Replace('\\', '/'), kind, encrypted, elf);
                })
                .ToList();
        }

        public void Need()
        {
            foreach (string root in SampleRoots)
                Repo.Need(".original", root);
            if (Samples.Count == 0)
                Repo.Missing("sample modules in .original/", privateFixture: true);
        }

        /// <summary>The ELF model as text: type, module, build path, program headers, import libraries, imports, exports, text segment.</summary>
        public static string Model(SceElf e)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"{e.Type} {e.ModuleName} {e.BuildPath}\n");
            foreach (var p in e.Phdrs)
                sb.Append($"phdr {p.Index} {p.Type} {p.Flags} {p.Offset} {p.Vaddr} {p.Filesz} {p.Memsz}\n");
            foreach (var (id, name) in e.ImportLibraries())
                sb.Append($"lib {id} {name}\n");
            foreach (var x in e.Imports())
                sb.Append($"import {x.Nid} {x.Library} {x.SymType} {x.GotVaddr} {x.Plt} {x.SymIndex} {x.DefinedLocally}\n");
            sb.Append("exports " + string.Join(" ", e.Exports().Order(StringComparer.Ordinal)) + "\n");
            var t = e.TextSegment();
            sb.Append($"text {t.Index} {t.Offset} {t.Filesz}\n");
            return sb.ToString();
        }
    }

    [Trait("Lane", "Private")]
    public class SelfAndElfTests(SampleSet set) : IClassFixture<SampleSet>
    {
        /// <summary>Per module: kind, encrypted segments, SHA-256 of the unwrapped ELF, of the re-signed SELF and of the ELF model.</summary>
        [Fact]
        public void Kind_Unwrap_Sign_And_ElfModel_MatchGolden()
        {
            set.Need();
            var lines = set.Samples.Select(s => s.Elf is null
                ? $"{s.Rel} | {s.Kind} | {s.Encrypted}\n"
                : $"{s.Rel} | {s.Kind} | {s.Encrypted} | unwrap {Golden.Sha(s.Elf)} | sign {Golden.Sha(Self.Sign(s.Elf))} | model {Golden.Sha(SampleSet.Model(new SceElf(s.Elf)))}\n");
            Golden.Match("samples.txt", string.Concat(lines));
        }

        [Fact]
        public void UnwrapThenSign_ReproducesShippedPs4StyleSelfs_ExceptDigest()
        {
            set.Need();
            int checkedFiles = 0;
            foreach (var s in set.Samples.Where(s => s.Kind == Self.KindPs4Self))
            {
                byte[] original = File.ReadAllBytes(s.Source);
                byte[] again = Self.Sign(Self.Unwrap(original));
                int digest = Self.DigestOffset(original);
                Assert.Equal(original.Length, again.Length);
                original.AsSpan(digest, 32).Clear();
                again.AsSpan(digest, 32).Clear();
                Assert.True(original.AsSpan().SequenceEqual(again), $"round trip differs: {s.Source}");
                checkedFiles++;
            }
            Assert.True(checkedFiles > 0, "no PS4-style fake SELFs among the samples");
        }

        [Fact]
        public void EncryptedFlag_IsDetected_AndUnwrapRefuses()
        {
            set.Need();
            var sample = set.Samples.First(s => Self.IsSelf(s.Kind));
            byte[] self = File.ReadAllBytes(sample.Source);
            int count = BitConverter.ToUInt16(self, 0x18);
            for (int i = 0; i < count; i++)                      // forge: flag every data entry as encrypted
                if ((BitConverter.ToUInt64(self, 0x20 + i * 0x20) & (1 << 11)) != 0)
                    self[0x20 + i * 0x20] |= (byte)Self.SegmentEncrypted;
            Assert.True(Self.EncryptedSegments(self) > 0);
            Assert.Throws<InvalidDataException>(() => Self.Unwrap(self));
        }

        [Fact]
        public void NotAnElf_IsRefused()
        {
            Assert.Throws<InvalidDataException>(() => new SceElf(new byte[0x40]));
            Assert.Throws<InvalidDataException>(() => Self.Sign(new byte[0x40]));
            Assert.Throws<InvalidDataException>(() => Self.Unwrap(new byte[0x40]));
        }
    }

    public class BpsTests
    {
        static string BackPork => Repo.Need(".original", "reference", "BackPork", "patches");

        [Fact]
        [Trait("Lane", "Private")]
        public void Edits_MatchGolden_ForBackPorkPatches()
        {
            string root = BackPork;
            var files = Directory.EnumerateFiles(root, "*.bps", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
            Assert.NotEmpty(files);
            var sb = new System.Text.StringBuilder();
            foreach (string path in files)
            {
                var p = Bps.Load(path);
                sb.Append($"{Path.GetRelativePath(root, path).Replace('\\', '/')}:{p.SourceSize} {p.TargetSize} {p.SourceCrc} {p.TargetCrc} {p.Actions.Count}\n");
                foreach (var edit in Bps.Edits(p))
                    sb.Append($"  {edit.Offset} {edit.Kind} {edit.Length} {Convert.ToHexStringLower(edit.Data)} {edit.FromOffset}\n");
            }
            Golden.Match("backpork-bps.txt", sb.ToString());
        }

        // --- synthetic patches (same cases as tests/test_phase3.py) -------------------
        static byte[] Varint(long v)
        {
            var bytes = new List<byte>();
            while (true)
            {
                byte x = (byte)(v & 0x7F);
                v >>= 7;
                if (v == 0)
                {
                    bytes.Add((byte)(x | 0x80));
                    return [.. bytes];
                }
                bytes.Add(x);
                v--;
            }
        }

        static byte[] MakePatch(byte[] source, byte[] target, (Bps.Command C, int Length, object? Arg)[] actions)
        {
            var body = new List<byte>("BPS1"u8.ToArray());
            body.AddRange(Varint(source.Length));
            body.AddRange(Varint(target.Length));
            body.AddRange(Varint(0));
            foreach (var (c, length, arg) in actions)
            {
                body.AddRange(Varint(((long)(length - 1) << 2) | (long)c));
                if (c == Bps.Command.TargetRead)
                    body.AddRange((byte[])arg!);
                else if (c is Bps.Command.SourceCopy or Bps.Command.TargetCopy)
                {
                    long d = (long)(int)arg!;
                    body.AddRange(Varint((Math.Abs(d) << 1) | (d < 0 ? 1L : 0L)));
                }
            }
            body.AddRange(BitConverter.GetBytes(Crc32.Compute(source)));
            body.AddRange(BitConverter.GetBytes(Crc32.Compute(target)));
            body.AddRange(BitConverter.GetBytes(Crc32.Compute(body.ToArray())));
            return [.. body];
        }

        [Fact]
        public void AllFourActions_IncludingOverlappingTargetCopy()
        {
            byte[] source = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
            byte[] target = (byte[])source.Clone();
            byte[] stub = [0x31, 0xC0, 0xC3, 0x90];
            stub.CopyTo(target, 8);
            Array.Copy(source, 20, target, 12, 4);
            for (int i = 16; i < 24; i += 2)
            {
                target[i] = source[22];
                target[i + 1] = source[23];
            }
            var patch = Bps.Parse(MakePatch(source, target,
            [
                (Bps.Command.SourceRead, 8, null), (Bps.Command.TargetRead, 4, stub), (Bps.Command.SourceCopy, 4, 20),
                (Bps.Command.TargetCopy, 8, 14), (Bps.Command.SourceRead, 8, null),
            ]));
            Assert.Equal(target, Bps.Apply(patch, source));
            Assert.Equal(new (long, string)[] { (8, "write"), (12, "source-copy"), (16, "target-copy") },
                         Bps.Edits(patch).Select(e => (e.Offset, e.Kind)).ToArray());
        }

        [Fact]
        public void WrongSource_IsRefused()
        {
            byte[] source = new byte[16];
            byte[] target = (byte[])source.Clone();
            target[0] = 1;
            var patch = Bps.Parse(MakePatch(source, target, [(Bps.Command.TargetRead, 1, new byte[] { 1 }), (Bps.Command.SourceRead, 15, null)]));
            var e = Assert.Throws<InvalidDataException>(() => Bps.Apply(patch, Enumerable.Repeat((byte)2, 16).ToArray()));
            Assert.Contains("source does not match", e.Message);
        }

        [Fact]
        public void CorruptPatch_IsRefused()
        {
            byte[] data = MakePatch("ab"u8.ToArray(), "ab"u8.ToArray(), [(Bps.Command.SourceRead, 2, null)]);
            data[5] ^= 1;
            Assert.Contains("checksum", Assert.Throws<InvalidDataException>(() => Bps.Parse(data)).Message);
        }

        [Fact]
        public void Crc32_MatchesZlib()
        {
            Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));   // standard check value
            Assert.Equal(0u, Crc32.Compute([]));
        }
    }

    /// <summary>
    /// Unusual trailing PT_SCE_VERSION blocks: two different versions in one record (Ghost of Yotei), appended data
    /// (Borderlands 4), opaque bytes (God of War Sons of Sparta), and Kena's re-signed dump (zeroed, cut off,
    /// build-path string). Unwraps are checked against golden hashes recorded from ps5bp.
    /// </summary>
    [Trait("Lane", "Private")]
    public class SceVersionTests
    {
        static string Backports(params string[] rel) => Repo.Need([".original", "backports", .. rel]);

        static byte[] Version(SceElf e)
        {
            var p = e.Phdrs.First(p => p.Type == SceElf.PtSceVersion);
            return e.Data.AsSpan((int)p.Offset, (int)p.Filesz).ToArray();
        }

        [Theory]
        [InlineData("PPSA26344/default/eboot.bin")]                 // Yotei: records with differing versions
        [InlineData("PPSA01494/4xx+/prx/librenoircore.ps5.prx")]    // BL4: 0x1E8 bytes after the block
        [InlineData("PPSA28997/Media/Modules/PS5Util.prx")]         // GoW SoS: opaque block
        [InlineData("PPSA01746/default/prx/akdelay.prx")]           // Kena: zeroed block
        [InlineData("PPSA01746/default/sce_module/libc.prx")]       // Kena: cut off
        [InlineData("PPSA01746/default/sce_module/libSceFace.prx")]
        [InlineData("PPSA01746/4xx+/sce_module/libc.prx")]          // Kena: build-path string
        public void Unwrap_MatchesGolden_AndSignRoundTrips(string rel)
        {
            byte[] elf = Self.Unwrap(File.ReadAllBytes(Backports(rel.Split('/'))));
            Golden.Match(Path.Combine("unwrap", rel.Replace('/', '_') + ".sha256"), Golden.Sha(elf) + "\n");
            Assert.Equal(elf, Self.Unwrap(Self.Sign(elf)));
        }

        [Fact]
        public void AppendedData_VersionBlockIsFoundBeforeIt()
        {
            var elf = new SceElf(Self.Unwrap(File.ReadAllBytes(Backports("PPSA01494", "4xx+", "prx", "librenoircore.ps5.prx"))));
            Assert.Equal("libRenoirCore.PS5", elf.ModuleName);
            Assert.Equal(Convert.FromHexString("0000160008"), Version(elf)[..5]);   // first record: crti:
        }

        [Fact]
        public void OpaqueVersionBlock_IsTakenAsIs()
        {
            string path = Backports("PPSA28997", "Media", "Modules", "PS5Util.prx");
            var elf = new SceElf(Self.Unwrap(File.ReadAllBytes(path)));
            byte[] file = File.ReadAllBytes(path);
            byte[] version = Version(elf);
            Assert.Equal("PS5Util", elf.ModuleName);
            Assert.Equal(file[^version.Length..], version);
        }

        [Fact]
        public void Kena_ZeroedCutOffAndBuildPathBlocks()
        {
            Assert.All(Version(new SceElf(Self.Unwrap(File.ReadAllBytes(Backports("PPSA01746", "default", "prx", "akdelay.prx"))))), b => Assert.Equal(0, b));
            // the re-signed libc ends at its last segment, 8 bytes short of its declared size
            Assert.All(Version(new SceElf(Self.Unwrap(File.ReadAllBytes(Backports("PPSA01746", "default", "sce_module", "libc.prx"))))), b => Assert.Equal(0, b));
            Assert.Equal("Prospero_Release/libc.prx\0"u8.ToArray(),
                         Version(new SceElf(Self.Unwrap(File.ReadAllBytes(Backports("PPSA01746", "4xx+", "sce_module", "libc.prx"))))));
        }

        /// <summary>.esbak = the decrypted original; the re-signed SELF differs only in header bytes after the phdr table.</summary>
        [Fact]
        public void Kena_SameCodeAsTheDecryptedBackup()
        {
            var ours = new SceElf(Self.Unwrap(File.ReadAllBytes(Backports("PPSA01746", "default", "prx", "akdelay.prx"))));
            var backup = SceElf.Load(Backports("PPSA01746", "default", "prx", "akdelay.prx.esbak"));
            var t = backup.TextSegment();
            Assert.True(ours.Data.AsSpan((int)t.Offset, (int)t.Filesz).SequenceEqual(backup.Data.AsSpan((int)t.Offset, (int)t.Filesz)));
        }

        [Fact]
        public void KenaDamagedRightSprx_IsRefused()
        {
            string prx = Backports("PPSA01746", "default", "sce_sys", "about", "right.sprx");
            var e = Assert.Throws<InvalidDataException>(() => Self.Unwrap(File.ReadAllBytes(prx)));
            Assert.Contains("points outside the file", e.Message);
        }

        /// <summary>A Wwise plugin whose module-param segment is all zeros has no SDK fields: read (0, 0), write nothing.</summary>
        [Fact]
        public void ModuleWithoutSdkFields_ReadsZero_WritesNothing()
        {
            var elf = new SceElf(Self.Unwrap(File.ReadAllBytes(Backports("PPSA01746", "default", "prx", "akdelay.prx"))));
            Assert.Equal((0u, 0u), Sdk.Read(elf));
            Assert.False(Sdk.Write(elf, 0x04000031, 0x09040001));
            Assert.Equal((0u, 0u), Sdk.Read(elf));
        }
    }
}
#endregion
#endif

#if !BACKPORT_SYNTHETIC_ONLY && !BACKPORT_PARSER_TESTS_ONLY
#region Phase2Tests
namespace Ps5Backport.Tests
{
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using Ps5Backport.Core;

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
                          Str(raw, "when", Fix.WhenMissing), Str(raw, "note"), Str(raw, "rationale"), Str(raw, "below"), Str(raw, "new_library")],
                         new[] { f.Kind, f.Name, f.Area, f.Stub, f.NewNid, f.ExportNid, f.When, f.Note, f.Rationale, f.Below, f.NewLibrary });
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
            var mount3 = k.Fixes["ZP4e7rlzOUk"];                                                             // systemBlocks -1 -> 0: a default rule, SDK < 5.00 only
            Assert.True(mount3.Due("4") && mount3.Due("4.50"));
            Assert.False(mount3.Due("5") || mount3.Due("5.02") || mount3.Due("10.01"));
            Assert.True(k.Options["save_prepare_compat"].Fixes["sDCBrmc61XU"].Due("12.00"));                   // no limit
            var mfsr = k.Fixes["3WVD91e12ZQ"];                                   // scePsmlMfsrInit: skipped below 9.00
            Assert.True(mfsr.Kind == Fix.KindPltStub && mfsr.When == Fix.WhenAlways && mfsr.Due("4") && !mfsr.Due("9.00"));
            Assert.Equal(Convert.FromHexString("B81200818AC3"), k.StubBytes(mfsr.Stub));   // returns 0x8a810012 (MFSR not supported), as on a base console
            Assert.False(table.ContainsKey("3WVD91e12ZQ"));                      // force_pro lifts it (kind "none")
            Assert.Equal(Convert.FromHexString("C60701B801000000C3"), k.StubBytes(table["BfBDZGbti7A"].Stub));   // *out = 1 and return 1, as on a real Pro
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
            Assert.Equal(["sceHmd2ReprojectionSetAllowPositionalReprojection", "sceHttp2SetRequestNoContentLength",
                          "sceKernelSyncOnAddressWait", "sceKernelSyncOnAddressWait32", "sceKernelSyncOnAddressWait64",
                          "scePsmlMfsrInit", "sceUserServiceGetGamePresets", "sceVideoOutGetOutputStatusWithVrr"],
                         k.FixList.Where(f => !f.Proven).Select(f => f.Name).Order(StringComparer.Ordinal));
            Assert.True(k.Fixes["NH6xARDOVv8"].Proven);   // GetOperationMode stub: proven by both games
            Assert.True(k.Fixes["ZP4e7rlzOUk"].Proven);   // SaveDataMount3 adapter: PPSA32935 FW 4 cook saves and reloads on 13.60
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
        [Trait("Lane", "Private")]
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
        [Trait("Lane", "Private")]
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
            Assert.Equal("7.00", FirmwareDb.Introduced("tU5e3f9gSiU", dbs, k)!.Value.Firmware);   // sceKernelIsTrinityMode
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
                Assert.Equal("7.00", FirmwareDb.Introduced("tU5e3f9gSiU", [], k)!.Value.Firmware);   // known fact (data/introduced.json)
                Assert.Null(FirmwareDb.Introduced("XXXXXXXXXXX", all, k));
            }
            finally { Directory.Delete(dir, true); }
        }
    }

    [Trait("Lane", "Private")]
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
}
#endregion
#endif

#if !BACKPORT_SYNTHETIC_ONLY && !BACKPORT_PARSER_TESTS_ONLY
#region Phase3Tests
namespace Ps5Backport.Tests
{
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Ps5Backport.Core;

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
            var names = new NameDb(Repo.Need("data", "aerolib.csv"));
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
}
#endregion
#endif

#if !BACKPORT_SYNTHETIC_ONLY && !BACKPORT_PARSER_TESTS_ONLY
#region Phase4Tests
namespace Ps5Backport.Tests
{
    using System.Security.Cryptography;
    using System.Text.Json;
    using Ps5Backport.Core;

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
            Assert.True(applied >= 18, $"only {applied} fixes applied");   // the optional save-preparation adapter is excluded
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
}
#endregion
#endif

#if !BACKPORT_SYNTHETIC_ONLY && !BACKPORT_PARSER_TESTS_ONLY
#region Phase5Tests
namespace Ps5Backport.Tests
{
    using System.Text.Json;
    using Ps5Backport.Core;

    /// <summary>
    /// Phase 5: whole-game backports and the command line. Each configuration's output folder is checked against a
    /// golden record (every file's SHA-256 and the full text report), first recorded when the output was
    /// byte-identical to ps5bp's.
    /// </summary>
    [Trait("Lane", "Private")]
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
            string aerolib = Repo.Need("data", "aerolib.csv");
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
                string artifacts = result.Published ? output : result.DiagnosticDirectory;
                Golden.Match(Path.Combine("backport", name + ".txt"), OutputRecord(artifacts, (game, "<game>"), (output, "<out>")));
            }
            finally { Directory.Delete(root, true); }
        }

        /// <summary>Every file of a backport output with its SHA-256 (reports with paths made portable), then the text report.</summary>
        public static string OutputRecord(string output, params (string Path, string Name)[] paths)
        {
            var sb = new System.Text.StringBuilder();
            foreach (string rel in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
                                            .Select(f => Path.GetRelativePath(output, f))
                                            .Where(f => f != Publication.ManifestName && f != Publication.EvidenceName).Order(StringComparer.Ordinal))
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

    [Trait("Lane", "Private")]
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
            string store = Repo.Need("fakelibs");
            Golden_("info", ["info", libc, self]);
            Golden_("report-libc-403-all", ["report", libc, "--firmware", "4.03", "--all"]);
            Golden_("report-eboot-4-provider", ["report", eboot, "--provider", libc, "--firmware", "4"]);
            Golden_("fwdb-list", ["fwdb", "list"]);
            Golden_("bps-info", ["bps", "info", .. Directory.EnumerateFiles(patches, "*.bps", SearchOption.AllDirectories).Order(StringComparer.Ordinal)]);
            Golden_("fakelib-list", ["fakelib", "list", "--store", store]);
            Golden_("fakelib-verify", ["fakelib", "verify", "--store", store]);
            Golden_("fakelib-list-403", ["fakelib", "list", "--firmware", "4.03", "--store", store]);
            Golden_("fakelib-check-403", ["fakelib", "check", "--firmware", "4.03", "--store", store]);
            Golden_("fakelib-check-4", ["fakelib", "check", "--firmware", "4", "--store", store]);
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
    [Trait("Lane", "Private")]
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

        [Theory]
        [InlineData(false, 19)]
        [InlineData(true, 21)]
        public void DryRun_WritesNothing_AndReportsOnlySelectedAdapters(bool optionalAdapters, int expectedFixes)
        {
            string game = Repo.Need(".original", "backports", "PPSA01317", "default");
            string output = Path.Combine(Path.GetTempPath(), "ps5backport-dry-" + Guid.NewGuid().ToString("N"));
            var k = new Knowledge(Repo.Need("data"));
            var o = new BackportOptions { Firmware = "4", ReferenceDir = Repo.Need("firmware_db"), FakelibStore = Path.Combine(Repo.Root, "fakelibs"), DryRun = true,
                Options = optionalAdapters ? ["save_prepare_compat", "audio_speaker_compat"] : [] };
            var dry = Pipeline.Backport(game, output, o, k, cancel: TestContext.Current.CancellationToken);
            Assert.False(Directory.Exists(output));
            Assert.All(dry.Modules, m => Assert.False(m.Written));
            Assert.Equal(expectedFixes, dry.Modules.Sum(m => m.Fixed.Count + m.StubbedNow.Count));
            Assert.Contains(dry.Modules, m => m.Fixed.Any(f => f.Nid == "ZP4e7rlzOUk"));   // sceSaveDataMount3: default below FW 5.00
            foreach (string nid in new[] { "sDCBrmc61XU", "DImz2Ft9E2g" })
                Assert.Equal(optionalAdapters, dry.Modules.Any(m => m.Fixed.Any(f => f.Nid == nid)));
            // with the exact 4.00 export list only one PSVR2 function is really missing (behind the sceHmd2Initialize check);
            // SetAllowPositionalReprojection is stubbed by its rule
            Assert.Equal(["sceHmd2ReprojectionSetParamWithBuffer"],
                         dry.Modules.SelectMany(m => m.Unknown).Select(u => new NameDb(Repo.Need("data", "aerolib.csv")).Name(u.Nid)).Order(StringComparer.Ordinal));
            Assert.All(dry.Modules.SelectMany(m => m.Unknown), u => Assert.Equal(Imports.Missing, u.Status));
            Assert.All(dry.Modules.Where(m => m.Error.Length == 0), m => Assert.NotEmpty(m.Statuses));
        }
    }

    /// <summary>Battlefield 6 launches a second executable (SP/&lt;name&gt;_Ps5_retail.elf); it must be found like a module.</summary>
    [Trait("Lane", "Private")]
    public class ExtraExecutableTests
    {
        [Fact]
        public void ElfExecutable_IsFound()
        {
            string game = Repo.Need(".original", "backports", "PPSA19534", "default");
            var actual = GameScan.FindModules(game, cancel: TestContext.Current.CancellationToken);
            Assert.Contains(Path.Combine("SP", "BattlefieldGame.Main_Ps5_retail.elf"), actual);
            Assert.Contains("eboot.bin", actual);
            Golden.Match(Path.Combine("modules", "PPSA19534_default.txt"), string.Concat(actual.Select(m => m.Replace('\\', '/') + "\n")));
        }
    }

    /// <summary>A decrypted dump (plain ELF modules): the output is always a fake SELF.</summary>
    [Trait("Lane", "Private")]
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
    [Trait("Lane", "Private")]
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
    [Trait("Lane", "Private")]
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
    [Trait("Lane", "Private")]
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
}
#endregion
#endif

#if !BACKPORT_SYNTHETIC_ONLY && !BACKPORT_PARSER_TESTS_ONLY
#region Ppsa03671Tests
namespace Ps5Backport.Tests
{
    using Ps5Backport.Core;

    /// <summary>Supplied backports provide static evidence; these checks do not certify console execution.</summary>
    [Trait("Lane", "Private")]
    public sealed class Ppsa03671Tests
    {
        [Fact]
        public void SuppliedBackport_RedirectsBothLibraryAndModuleForUmtx()
        {
            var baseline = Load("PPSA03671", "default", "eboot.bin");
            var backport = Load("PPSA03671", "4xx+", "eboot.bin");
            var oldImport = Assert.Single(baseline.Imports(), i => i.Nid == "86f0wFtxn0k");
            var newImport = Assert.Single(backport.Imports(), i => i.Nid == "04AjkP0jO9U");
            Assert.Equal("libc", oldImport.Library);
            Assert.Equal("libkernel", newImport.Library);
            Assert.Equal("86f0wFtxn0k#u#s", baseline.Symbols()[oldImport.SymIndex].Name);
            Assert.Equal("04AjkP0jO9U#v#G", backport.Symbols()[newImport.SymIndex].Name);
            Assert.Contains("86f0wFtxn0k", Load("PPSA03671", "default", "sce_module/libc.prx").Exports());
        }

        [Fact]
        public void UnitySyncAdapters_ProduceImportsInLibkernel()
        {
            var elf = Load("PPSA12544", "default", "Media/Modules/PS5Util.prx");
            elf.AllowTextExtension = true; // Match Studio's default cooking settings.
            string[] nids = ["Hc4CaR6JBL0", "q2y-wDIVWZA"];
            var originals = elf.Imports().Where(i => nids.Contains(i.Nid)).ToArray();
            Assert.Equal(2, originals.Length);
            Assert.All(originals, i => Assert.Equal("libkernel_sync_on_address", i.Library));
            var applied = FixApplier.Apply(elf, nids, Samples.K);
            Assert.Equal(2, applied.Count);
            var reloaded = new SceElf(elf.Data);
            foreach (var original in originals)
            {
                var renamed = Assert.Single(reloaded.Imports(), i => i.SymIndex == original.SymIndex);
                Assert.Equal("04AjkP0jO9U", renamed.Nid);
                Assert.Equal("libkernel", renamed.Library);
                var fix = Samples.K.Fixes[original.Nid];
                var (_, called, jumped) = Samples.Trace(reloaded, original, fix);
                Assert.Equal(original.GotVaddr, fix.PostCall ? called : jumped);      // Wait: call [GOT] (post-call, timespec); Wake: jmp [GOT]
            }
        }

        static SceElf Load(string title, string variant, string relative)
        {
            byte[] data = File.ReadAllBytes(Samples.Backports(title, variant, relative));
            return new SceElf(Self.IsSelf(Self.Kind(data)) ? Self.Unwrap(data) : data);
        }
    }
}
#endregion
#endif

#if !BACKPORT_SYNTHETIC_ONLY && !BACKPORT_PARSER_TESTS_ONLY
#region Ppsa34008Tests
namespace Ps5Backport.Tests
{
    using Ps5Backport.Core;

    /// <summary>Static compatibility evidence from PPSA34008; these checks do not certify console execution.</summary>
    [Trait("Lane", "Private")]
    public class Ppsa34008Tests
    {
        static string Sample(string variant, string relative) =>
            Repo.Need(".original", "backports", "PPSA34008", variant, relative);

        static SceElf Load(string variant, string relative)
        {
            byte[] data = File.ReadAllBytes(Sample(variant, relative));
            return new SceElf(Self.IsSelf(Self.Kind(data)) ? Self.Unwrap(data) : data);
        }

        [Theory]
        [InlineData("4.03", 5, 8)]
        [InlineData("10.01", 0, 4)]
        public void SuppliedBackport_ExposesDifferentKernelRequirements(string firmware, int ebootMissing, int libcMissing)
        {
            string folder = Sample("10xx+", "fakelib");
            var modules = Directory.GetFiles(folder).Select(p => Load("10xx+", Path.GetRelativePath(Sample("10xx+", ""), p))).ToArray();
            var providers = GameScan.FindModules(Sample("10xx+", ""), TestContext.Current.CancellationToken)
                .SelectMany(p => Load("10xx+", p).Exports()).ToHashSet();
            var reference = Imports.LoadReference(Repo.Need("firmware_db"), firmware);
            Assert.True(reference.Exact);
            List<ImportStatus> Missing(SceElf elf) => Imports.Classify(elf, reference.Nids, providers, true)
                .Where(s => Imports.IsUnresolved(s.Status)).ToList();
            Assert.Equal(ebootMissing, Missing(Load("10xx+", "eboot.bin")).Count);
            Assert.Equal(libcMissing, Missing(Load("10xx+", "sce_module/libc.prx")).Count);
            foreach (var elf in modules) Assert.Empty(Missing(elf));
        }

        [Fact]
        public void ExistingShaderOption_ReproducesEverySuppliedShaderReplacement()
        {
            var before = Load("default", "eboot.bin");
            var after = Load("10xx+", "eboot.bin");
            var pattern = new Knowledge(Repo.Need("data")).Options["gpu_scratch"].Patterns.Single();
            var patched = new SceElf((byte[])before.Data.Clone());
            Assert.Equal(164, FixApplier.ApplyPattern(patched, pattern, TestContext.Current.CancellationToken));
            int changed = 0;
            for (int i = 0; i < before.Data.Length; i++)
            {
                if (before.Data[i] == patched.Data[i]) continue;
                Assert.Equal(patched.Data[i], after.Data[i]);
                changed++;
            }
            Assert.Equal(328, changed);
            Assert.Equal(0, FixApplier.ApplyPattern(after, pattern, TestContext.Current.CancellationToken));
        }

        [Theory]
        [InlineData("4.03", "32KQRUK13kI")]
        [InlineData("10.01", "")]
        public void StudioAnalysis_LeavesOnlyTheUnprovenCoredumpImport(string firmware, string remainingNid)
        {
            string output = Path.Combine(Repo.Root, "work", "ppsa34008-test-" + Guid.NewGuid().ToString("N"));
            var result = Pipeline.Backport(Sample("default", ""), output, new BackportOptions
            {
                Firmware = firmware, ReferenceDir = Repo.Need("firmware_db"),
                ExtraFakelib = Sample("10xx+", "fakelib"), DryRun = true, PatchParam = true, Options = ["gpu_scratch"]
            }, new Knowledge(Repo.Need("data")), cancel: TestContext.Current.CancellationToken);
            Assert.Equal(OperationState.Completed, result.State);
            Assert.All(result.Modules, m => Assert.Empty(m.Error));
            Assert.Equal(remainingNid.Length == 0 ? [] : new[] { remainingNid }, result.Modules.SelectMany(m => m.Unknown).Select(u => u.Nid).ToArray());
            Assert.False(result.Published);
            Assert.False(Directory.Exists(output));
        }
    }
}
#endregion
#endif

#if !BACKPORT_SYNTHETIC_ONLY && !BACKPORT_PARSER_TESTS_ONLY
#region PreflightTests
namespace Ps5Backport.Tests
{
    using System.Text.Json.Nodes;
    using Ps5Backport.Core;

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

        /// <summary>A misspelt key used to be ignored (a misspelt "below" made a rule unconditional, "proven" defaulted to true).
        /// Every record type reports an unknown key with its file, path and the allowed keys.</summary>
        [Theory]
        [InlineData("fixes", "$.fixes.test-nid", "belo", "fixes", "test-nid")]
        [InlineData("fixes", "$.fixes.test-nid", "proovn", "fixes", "test-nid")]
        [InlineData("fixes", "$", "fixe")]
        [InlineData("fixes", "$.options.off", "warnng", "options", "off")]
        [InlineData("fixes", "$.options.off.fixes.test-nid", "contiguos", "options", "off", "fixes", "test-nid")]
        [InlineData("fixes", "$.patterns[0]", "fnd", "patterns", "0")]
        [InlineData("stubs", "$.stubs.ret0", "hx", "stubs", "ret0")]
        [InlineData("candidates", "$.candidates.candidate", "valu", "candidates", "candidate")]
        [InlineData("introduced", "$.introduced.test-nid", "firmwar", "introduced", "test-nid")]
        [InlineData("known_builds", "$.builds[0]", "stat", "builds", "0")]
        [InlineData("sony_builds", "$.evidence.J00000002", "firmwar", "evidence", "J00000002")]
        public void UnknownKey_FailsAtLoadWithPathAndAllowedKeys(string file, string expectedPath, string key, params string[] path)
        {
            Edit("fixes", root =>
            {
                root["options"] = JsonNode.Parse("""{"off":{"fixes":{"test-nid":{"kind":"none","sources":[]}},"fakelib_variant":"test_variant"}}""");
                root["patterns"] = JsonNode.Parse("""[{"name":"p","find":"90","replace":"cc","files":["x.bin"]}]""");
            });
            Edit("known_builds", root => root["builds"] = JsonNode.Parse("""[{"module":"libSceAmpr","build_date":"Aug 24 2026","state":"ok"}]"""));
            Edit("sony_builds", root => root["evidence"] = JsonNode.Parse("""{"J00000002":{"firmware":"13.60","source":"test"}}"""));
            Edit(file, root =>
            {
                JsonNode node = root;
                foreach (string step in path)
                    node = node is JsonArray array ? array[int.Parse(step)]! : node[step]!;
                node[key] = "x";
            });
            var error = Assert.Throws<InvalidDataException>(() => new Knowledge(DataDir));
            Assert.Contains(file + ".json", error.Message);
            Assert.Contains($"unknown key '{key}' at {expectedPath}", error.Message);
            Assert.Contains("allowed:", error.Message);
        }

        [Fact]
        public void CommentKeys_StartingWithUnderscore_AreAllowedEverywhere()
        {
            Edit("fixes", root =>
            {
                root["_about"] = "root comment";
                root["fixes"]!["test-nid"]!["_why"] = "record comment";
                root["options"] = JsonNode.Parse("""{"off":{"_todo":"x","fixes":{"test-nid":{"kind":"none","sources":[],"_note":"y"}}}}""");
            });
            Edit("stubs", root => root["stubs"]!["ret0"]!["_seen"] = "GT7");
            var knowledge = new Knowledge(DataDir);
            Assert.Single(knowledge.Fixes);
            Assert.Empty(knowledge.FixTable(["off"]));
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
}
#endregion
#endif

#if !BACKPORT_SYNTHETIC_ONLY && !BACKPORT_PARSER_TESTS_ONLY
#region ProviderOwnershipTests
namespace Ps5Backport.Tests
{
    using Ps5Backport.Core;

    public sealed class ProviderOwnershipTests : IDisposable
    {
        readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ps5backport-providers-" + Guid.NewGuid().ToString("N"))).FullName;

        [Theory]
        [InlineData(false, Imports.Missing)]
        [InlineData(true, Imports.Provided)]
        public void DuplicateExport_IsProvidedOnlyWhenAnotherModuleOwnsIt(bool anotherProvider, string expected)
        {
            string game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
            File.WriteAllBytes(Path.Combine(game, "a.sprx"), SyntheticBinary.Elf(imports: true, exports: true));
            if (anotherProvider)
                File.WriteAllBytes(Path.Combine(game, "b.sprx"), SyntheticBinary.Elf(exports: true));
            var reference = new Reference(new HashSet<string>(), "synthetic exact", true);
            string db = Directory.CreateDirectory(Path.Combine(_root, "reference")).FullName;
            FirmwareDb.Save(new FirmwareExports("4", "synthetic", false, new()), Path.Combine(db, "4.00.json"));
            var result = Pipeline.Backport(game, Path.Combine(_root, "out"),
                new BackportOptions { Firmware = "4", ReferenceDir = db, KnownFixes = false, DryRun = true }, Knowledge.Default,
                cancel: TestContext.Current.CancellationToken);
            Assert.All(result.Modules, m => Assert.Empty(m.Error));
            Assert.Equal(expected, Assert.Single(result.Modules.Single(m => m.Path == "a.sprx").Statuses).Status);
            var libraryStatus = Assert.Single(FakelibStore.Check(game, reference).Single(m => m.Name == "a.sprx").Statuses);
            Assert.Equal(expected, libraryStatus.Status);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
#endregion
#endif

#if !BACKPORT_SYNTHETIC_ONLY && !BACKPORT_PARSER_TESTS_ONLY
#region PublicationTests
namespace Ps5Backport.Tests
{
    using System.Buffers.Binary;
    using System.Diagnostics;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using Ps5Backport.Core;

    /// <summary>Staged publication, batch rollback, provenance, aliases and actual process-interruption recovery.</summary>
    public sealed class PublicationTests : IDisposable
    {
        // The interruption child process uses the parent's root and exits without Dispose, so it creates none.
        readonly string _root = Environment.GetEnvironmentVariable("PS5BP_TRANSACTION_TEST_ROOT") is null
            ? Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ps5backport-publication-" + Guid.NewGuid().ToString("N"))).FullName
            : "";
        string Output => Path.Combine(_root, "out");

        void Previous()
        {
            Directory.CreateDirectory(Output);
            File.WriteAllText(Path.Combine(Output, "old.bin"), "previous");
        }

        [Theory]
        [InlineData("file-flushed")]
        [InlineData("prepared")]
        [InlineData("backup-moved")]
        [InlineData("stage-moved")]
        public void PublicationFault_RestoresPriorResult(string failAt)
        {
            Previous();
            Assert.Throws<IOException>(() =>
            {
                using var transaction = new DirectoryTransaction(Output, stage =>
                {
                    if (stage == failAt) throw new IOException("injected disk-full/write failure", unchecked((int)0x80070070));
                });
                File.WriteAllText(Path.Combine(transaction.Stage, "new.bin"), "replacement");
                transaction.Commit(TestContext.Current.CancellationToken);
            });
            Assert.Equal("previous", File.ReadAllText(Path.Combine(Output, "old.bin")));
            Assert.False(File.Exists(Path.Combine(Output, "new.bin")));
            Assert.Empty(Directory.GetDirectories(_root, "*.stage"));
            Assert.Empty(Directory.GetFiles(_root, "*.journal.json"));
        }

        [Fact]
        public void CancellationBeforeCommit_PreservesDestination()
        {
            Previous();
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            using (var transaction = new DirectoryTransaction(Output))
            {
                File.WriteAllText(Path.Combine(transaction.Stage, "new.bin"), "replacement");
                Assert.Throws<OperationCanceledException>(() => transaction.Commit(cancel.Token));
            }
            Assert.Equal("previous", File.ReadAllText(Path.Combine(Output, "old.bin")));
        }

        [Fact]
        public void LockedStagedFile_AbortsPublicationAndPreservesDestination()
        {
            Previous();
            using (var transaction = new DirectoryTransaction(Output))
            {
                string file = Path.Combine(transaction.Stage, "new.bin");
                File.WriteAllText(file, "replacement");
                using (var reader = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Assert.Throws<IOException>(() => transaction.Commit(TestContext.Current.CancellationToken));
            }
            Assert.Equal("previous", File.ReadAllText(Path.Combine(Output, "old.bin")));
            Assert.Empty(Directory.GetDirectories(_root, "*.stage"));
        }

        [Fact]
        public void InvalidJournal_IsRetainedForManualInspection()
        {
            Previous();
            using (var child = Worker("stage-created"))
            {
                Assert.True(child.WaitForExit(30000));
                Assert.Equal(37, child.ExitCode);
            }
            string journal = Assert.Single(Directory.GetFiles(_root, "*.journal.json"));
            File.WriteAllText(journal, "{\"Version\":1,\"Id\":\"../foreign\"}");
            Assert.Throws<InvalidDataException>(() => Publication.Recover(Output));
            Assert.Equal("previous", File.ReadAllText(Path.Combine(Output, "old.bin")));
            Assert.True(File.Exists(journal));
            Assert.Single(Directory.GetDirectories(_root, "*.stage"));
        }

        [Fact]
        public void TwoWriters_CannotAcquireTheSameDestination()
        {
            Previous();
            using var transaction = new DirectoryTransaction(Output);
            Assert.Throws<IOException>(() => new DirectoryTransaction(Output));
            using var child = Worker("blocked");
            Assert.True(child.WaitForExit(30000));
            Assert.Equal(39, child.ExitCode);
        }

        [Fact]
        public void ReusedDestination_ContainsOnlyNewArtifactsAndDiscardsBackup()
        {
            Previous();
            string backup;
            using (var transaction = new DirectoryTransaction(Output))
            {
                backup = transaction.Backup;
                File.WriteAllText(Path.Combine(transaction.Stage, "new.bin"), "replacement");
                transaction.Commit(TestContext.Current.CancellationToken);
            }
            Assert.False(File.Exists(Path.Combine(Output, "old.bin")));
            Assert.False(Directory.Exists(backup));
            Assert.Empty(Directory.GetDirectories(_root, "*.backup"));
            Publication.Verify(Output);
            File.WriteAllText(Path.Combine(Output, "new.bin"), "tampered");
            Assert.Throws<InvalidDataException>(() => Publication.Verify(Output));
        }

        [Fact]
        public void TamperingAfterPreparation_IsRefusedBeforeRename()
        {
            Previous();
            DirectoryTransaction? transaction = null;
            using (transaction = new DirectoryTransaction(Output, stage =>
            {
                if (stage == "prepared") File.WriteAllText(Path.Combine(transaction!.Stage, "new.bin"), "tampered");
            }))
            {
                File.WriteAllText(Path.Combine(transaction.Stage, "new.bin"), "replacement");
                Assert.Throws<InvalidDataException>(() => transaction.Commit(TestContext.Current.CancellationToken));
            }
            Assert.Equal("previous", File.ReadAllText(Path.Combine(Output, "old.bin")));
        }

        [Theory]
        [InlineData("stage-created", true)]
        [InlineData("backup-moved", true)]
        [InlineData("stage-moved", true)]
        [InlineData("committed", true)]
        [InlineData("stage-created", false)]
        [InlineData("stage-moved", false)]
        [InlineData("committed", false)]
        public void AbruptProcessExit_IsRecovered(string exitAt, bool existing)
        {
            if (existing) Previous();
            using (var child = Worker(exitAt))
            {
                Assert.True(child.WaitForExit(30000));
                Assert.Equal(37, child.ExitCode);
            }
            Publication.Recover(Output);
            Publication.Recover(Output); // idempotence, including a recovery completed by another process
            if (exitAt == "committed")
            {
                Assert.Equal("replacement", File.ReadAllText(Path.Combine(Output, "new.bin")));
                Publication.Verify(Output);
                Assert.Empty(Directory.GetDirectories(_root, "*.backup"));
            }
            else if (existing)
                Assert.Equal("previous", File.ReadAllText(Path.Combine(Output, "old.bin")));
            else
                Assert.False(Directory.Exists(Output));
            Assert.Empty(Directory.GetDirectories(_root, "*.stage"));
            Assert.Empty(Directory.GetFiles(_root, "*.journal.json"));
        }

        Process Worker(string exitAt)
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(typeof(PublicationTests).Assembly.Location);
            start.ArgumentList.Add("-method");
            start.ArgumentList.Add("Ps5Backport.Tests.PublicationTests.InterruptionWorker");
            start.Environment["PS5BP_TRANSACTION_TEST_ROOT"] = _root;
            start.Environment["PS5BP_TRANSACTION_TEST_EXIT"] = exitAt;
            return Process.Start(start)!;
        }

        [Fact]
        public void InterruptionWorker()
        {
            string? root = Environment.GetEnvironmentVariable("PS5BP_TRANSACTION_TEST_ROOT");
            if (root is null) return; // this case is only active in the isolated child process
            string absolute = Path.GetFullPath(root);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), absolute, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("ps5backport-publication-", Path.GetFileName(absolute));
            string? exitAt = Environment.GetEnvironmentVariable("PS5BP_TRANSACTION_TEST_EXIT");
            if (exitAt == "blocked")
            {
                try { using var writer = new DirectoryTransaction(Path.Combine(absolute, "out")); }
                catch (IOException) { Environment.Exit(39); }
                Environment.Exit(40);
            }
            using var transaction = new DirectoryTransaction(Path.Combine(absolute, "out"), stage =>
            {
                if (stage == exitAt) Environment.Exit(37); // deliberately bypass Dispose/finally: actual interrupted process
            });
            File.WriteAllText(Path.Combine(transaction.Stage, "new.bin"), "replacement");
            transaction.Commit(TestContext.Current.CancellationToken);
            Environment.Exit(40);
        }

        (string Game, string Reference, Knowledge Knowledge) RunFixture()
        {
            string game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
            string reference = Directory.CreateDirectory(Path.Combine(_root, "reference")).FullName;
            string data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
            foreach (string file in Directory.GetFiles(Paths.DataDir, "*.json"))
                File.Copy(file, Path.Combine(data, Path.GetFileName(file)));
            string fixesPath = Path.Combine(data, "fixes.json");
            var fixes = JsonNode.Parse(File.ReadAllText(fixesPath))!;
            fixes["patterns"] = JsonNode.Parse("""[{"name":"test","find":"0102","replace":"0304","files":["data.bin"]}]""");
            File.WriteAllText(fixesPath, fixes.ToJsonString());
            File.WriteAllBytes(Path.Combine(game, "data.bin"), [1, 2]);
            FirmwareDb.Save(new FirmwareExports("4", "synthetic", false, new()), Path.Combine(reference, "4.00.json"));
            return (game, reference, new Knowledge(data));
        }

        [Fact]
        public void Pipeline_PublishesVerifiedEvidenceAndReplacesStaleOutput()
        {
            Previous();
            var fixture = RunFixture();
            var options = new BackportOptions { Firmware = "4", ReferenceDir = fixture.Reference, ExtendText = false };
            var result = Pipeline.Backport(fixture.Game, Output, options, fixture.Knowledge, cancel: TestContext.Current.CancellationToken);
            Assert.True(result.Published);
            Assert.False(File.Exists(Path.Combine(Output, "old.bin")));
            Assert.Empty(result.PreviousOutputBackup);
            Assert.Empty(Directory.GetDirectories(_root, "*.backup"));
            Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(Path.Combine(fixture.Game, "data.bin")));
            Publication.Verify(Output);
            using var evidence = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Output, Publication.EvidenceName)));
            var inputs = evidence.RootElement.GetProperty("Inputs");
            Assert.Equal(fixture.Knowledge.DocumentHashes["fixes.json"], inputs.GetProperty("knowledge:fixes.json").GetProperty("Sha256").GetString());
            Assert.True(evidence.RootElement.GetProperty("ReferenceExact").GetBoolean());
            Assert.False(evidence.RootElement.GetProperty("Options").GetProperty("ExtendText").GetBoolean());
            Assert.DoesNotContain(_root, evidence.RootElement.GetRawText());
            Assert.Contains(typeof(Pipeline).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
                evidence.RootElement.GetProperty("Engine").GetString()!);
            AmprIndex.Build(fixture.Game, Output, cancel: TestContext.Current.CancellationToken);
            Publication.Verify(Output);
            File.WriteAllBytes(Path.Combine(fixture.Game, "new.elf"), Elf());
            Assert.Throws<IOException>(() => AmprIndex.Build(fixture.Game, Output, cancel: TestContext.Current.CancellationToken));
            File.Delete(Path.Combine(fixture.Game, "new.elf"));
            File.WriteAllBytes(Path.Combine(fixture.Game, "data.bin"), [9, 9]);
            Assert.Throws<IOException>(() => AmprIndex.Build(fixture.Game, Output, cancel: TestContext.Current.CancellationToken));
            Publication.Verify(Output);
        }

        [Fact]
        public void LowerBoundReference_IsRecordedExplicitly()
        {
            var fixture = RunFixture();
            File.Delete(Path.Combine(fixture.Reference, "4.00.json"));
            FirmwareDb.Save(new FirmwareExports("3", "synthetic", false, new()), Path.Combine(fixture.Reference, "3.00.json"));
            var result = Pipeline.Backport(fixture.Game, Output,
                new BackportOptions { Firmware = "4", ReferenceDir = fixture.Reference }, fixture.Knowledge,
                cancel: TestContext.Current.CancellationToken);
            Assert.True(result.Published);
            using var evidence = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Output, Publication.EvidenceName)));
            Assert.False(evidence.RootElement.GetProperty("ReferenceExact").GetBoolean());
            Assert.Contains("lower bound", result.Reference);
            Publication.Verify(Output);
        }

        [Fact]
        public void ModuleErrors_RetainDiagnosticsWithoutPublishing()
        {
            Previous();
            var fixture = RunFixture();
            File.WriteAllBytes(Path.Combine(fixture.Game, "eboot.bin"), [1, 2]);
            var result = Pipeline.Backport(fixture.Game, Output, new BackportOptions { Firmware = "4", ReferenceDir = fixture.Reference }, fixture.Knowledge,
                cancel: TestContext.Current.CancellationToken);
            Assert.False(result.Published);
            Assert.NotEmpty(result.DiagnosticDirectory);
            Assert.True(File.Exists(Path.Combine(result.DiagnosticDirectory, Pipeline.ReportJson)));
            Assert.False(File.Exists(Path.Combine(result.DiagnosticDirectory, Publication.ManifestName)));
            Assert.Equal("previous", File.ReadAllText(Path.Combine(Output, "old.bin")));
        }

        [Fact]
        [Trait("Phase", "AnyPs5Phase4")]
        public void InvalidDynamicStrings_PreserveOutputAndAllowSubsequentCook()
        {
            Previous();
            var fixture = RunFixture();
            var options = new BackportOptions { Firmware = "4", ReferenceDir = fixture.Reference, ExtendText = false };
            byte[] data = SyntheticBinary.Elf();
            SyntheticBinary.W64(data, 0x160, 10); // DT_STRSZ excludes the symbol's terminating NUL.
            SyntheticBinary.W64(data, 0x168, 16);
            SyntheticBinary.W64(data, 0x170, 0);
            string module = Path.Combine(fixture.Game, "eboot.bin");
            File.WriteAllBytes(module, data);
            Assert.Throws<InvalidDataException>(() => Pipeline.Backport(fixture.Game, Output, options, fixture.Knowledge,
                cancel: TestContext.Current.CancellationToken));
            Assert.Equal(data, File.ReadAllBytes(module));
            Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(Path.Combine(fixture.Game, "data.bin")));
            Assert.Equal("previous", File.ReadAllText(Path.Combine(Output, "old.bin")));
            Assert.Single(Directory.EnumerateFiles(Output, "*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetDirectories(_root, "*.stage"));
            Assert.Empty(Directory.GetFiles(_root, "*.journal.json"));

            SyntheticBinary.W64(data, 0x168, 17);
            File.WriteAllBytes(module, data);
            var result = Pipeline.Backport(fixture.Game, Output, options, fixture.Knowledge,
                cancel: TestContext.Current.CancellationToken);
            Assert.True(result.Published);
            Assert.Equal(new byte[] { 3, 4 }, File.ReadAllBytes(Path.Combine(Output, "data.bin")));
            Assert.Equal(data, File.ReadAllBytes(module));
            Assert.Empty(result.PreviousOutputBackup);
            Assert.Empty(Directory.GetDirectories(_root, "*.backup"));
            Publication.Verify(Output);
        }

        [Theory]
        [InlineData("cancel")]
        [InlineData("new-module")]
        [InlineData("new-reference")]
        [InlineData("write-failure")]
        [InlineData("new-library")]
        [InlineData("new-app0")]
        public void FailureBeforePublication_PreservesOutput(string fault)
        {
            Previous();
            var fixture = RunFixture();
            string? extra = fault is "new-library" or "new-app0" ? Directory.CreateDirectory(Path.Combine(_root, "extra")).FullName : null;
            using var cancel = new CancellationTokenSource();
            var progress = new SyncProgress(p =>
            {
                if (p.Stage != "publishing") return;
                switch (fault)
                {
                    case "cancel": cancel.Cancel(); break;
                    case "new-module": File.WriteAllBytes(Path.Combine(fixture.Game, "new.elf"), Elf()); break;
                    case "new-reference": File.WriteAllText(Path.Combine(fixture.Reference, "5.00.json"), "{}"); break;
                    case "write-failure": throw new UnauthorizedAccessException("injected write failure");
                    case "new-library": File.WriteAllBytes(Path.Combine(extra!, "new.sprx"), Elf()); break;
                    case "new-app0":
                        Directory.CreateDirectory(Path.Combine(extra!, "app0"));
                        File.WriteAllText(Path.Combine(extra!, "app0", "new.bin"), "changed");
                        break;
                }
            });
            Assert.NotNull(Record.Exception(() => Pipeline.Backport(fixture.Game, Output,
                new BackportOptions { Firmware = "4", ReferenceDir = fixture.Reference, ExtraFakelib = extra }, fixture.Knowledge, progress, cancel.Token)));
            Assert.Equal("previous", File.ReadAllText(Path.Combine(Output, "old.bin")));
            Assert.Empty(Directory.GetDirectories(_root, "*.stage"));
        }

        [Fact]
        public void ChangedLoadedKnowledge_IsRejected()
        {
            Previous();
            var fixture = RunFixture();
            File.AppendAllText(Path.Combine(fixture.Knowledge.DataDir, "fixes.json"), " ");
            Assert.Throws<IOException>(() => Pipeline.Backport(fixture.Game, Output,
                new BackportOptions { Firmware = "4", ReferenceDir = fixture.Reference }, fixture.Knowledge, cancel: TestContext.Current.CancellationToken));
            Assert.Equal("previous", File.ReadAllText(Path.Combine(Output, "old.bin")));
        }

        [Fact]
        public void BatchImportFailure_PreservesLibrariesAndManifest()
        {
            string store = Path.Combine(_root, "store"), first = Path.Combine(_root, "a.sprx"), second = Path.Combine(_root, "b.sprx");
            File.WriteAllBytes(first, Elf());
            FakelibStore.Add(store, "4", [first]);
            string folder = Path.Combine(store, "4");
            byte[] manifest = File.ReadAllBytes(Path.Combine(folder, "manifest.json"));
            byte[] library = File.ReadAllBytes(Path.Combine(folder, "a.sprx"));
            byte[] update = Elf(); update[^1] = 7;
            File.WriteAllBytes(first, update);
            File.WriteAllBytes(second, [0]);
            Assert.Throws<InvalidDataException>(() => FakelibStore.Add(store, "4", [first, second]));
            Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(folder, "manifest.json")));
            Assert.Equal(library, File.ReadAllBytes(Path.Combine(folder, "a.sprx")));
            Assert.False(File.Exists(Path.Combine(folder, "b.sprx")));
            Publication.Verify(folder);
            Assert.Throws<IOException>(() => FakelibStore.AddBatch(store, "4", [first], "", "", stage =>
            {
                if (stage == "file-added") throw new IOException("injected copy failure");
            }));
            Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(folder, "manifest.json")));
            Assert.Single(FakelibStore.List(store));
            FakelibStore.AddBatch(store, "4", [first], "", "", stage =>
            {
                if (stage == "file-added")
                    Assert.Throws<IOException>(() => FakelibStore.Add(store, "5", [first]));
            });
            Assert.False(Directory.Exists(Path.Combine(store, "5")));
            FakelibStore.Add(store, "4", [first]);
            Assert.Equal(update, File.ReadAllBytes(Path.Combine(folder, "a.sprx")));
            Publication.Verify(folder);
        }

        [Fact]
        public void VariantImport_PublishesParentAndVariantTogether()
        {
            string store = Path.Combine(_root, "store"), input = Path.Combine(_root, "a.sprx");
            File.WriteAllBytes(input, Elf());
            FakelibStore.Add(store, "4", [input]);
            FakelibStore.Add(store, "4", [input], variant: "test");
            string folder = Path.Combine(store, "4"), variant = Path.Combine(folder, "test");
            Publication.Verify(folder);
            Publication.Verify(variant);
            byte[] original = File.ReadAllBytes(Path.Combine(variant, "a.sprx"));
            byte[] updated = Elf(); updated[^1] = 9;
            File.WriteAllBytes(input, updated);
            Assert.Throws<IOException>(() => FakelibStore.AddBatch(store, "4", [input], "", "test", stage =>
            {
                if (stage == "stage-moved") throw new IOException("injected variant publication failure");
            }));
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(variant, "a.sprx")));
            Publication.Verify(folder);
            Publication.Verify(variant);
            FakelibStore.Add(store, "4", [input], variant: "test");
            Assert.Equal(updated, File.ReadAllBytes(Path.Combine(variant, "a.sprx")));
            Publication.Verify(folder);
            Publication.Verify(variant);
        }

        [Fact]
        public void WindowsAliasesAndJunctions_AreRefused()
        {
            if (!OperatingSystem.IsWindows()) return;
            Previous();
            Assert.Throws<ArgumentException>(() => Paths.RequireSeparateTrees(Output, "\\\\?\\" + Output));
            var shortName = new StringBuilder(32768);
            Assert.NotEqual(0u, GetShortPathNameW(Output, shortName, (uint)shortName.Capacity));
            Assert.Throws<ArgumentException>(() => Paths.RequireSeparateTrees(Output, shortName.ToString()));
            Assert.Throws<ArgumentException>(() => Paths.RequireNotContaining("\\\\?\\" + Output, Path.Combine(Output, "data")));
            RunCmd("mklink", "/J", "link", "out");
            try { Assert.Throws<IOException>(() => new DirectoryTransaction(Path.Combine(_root, "link", "nested"))); }
            finally { Directory.Delete(Path.Combine(_root, "link")); }
            RunCmd("mklink", "/J", "out\\linked", "..\\out");
            try { Assert.Throws<IOException>(() => new DirectoryTransaction(Output)); }
            finally { Directory.Delete(Path.Combine(Output, "linked")); }
            Assert.Equal("previous", File.ReadAllText(Path.Combine(Output, "old.bin")));
        }

        [Fact]
        public void WindowsDriveAlias_UsesTheSameWriterLock()
        {
            if (!OperatingSystem.IsWindows()) return;
            Previous();
            var used = Directory.GetLogicalDrives().Select(d => char.ToUpperInvariant(d[0])).ToHashSet();
            char letter = Enumerable.Range('D', 'Z' - 'D' + 1).Select(i => (char)i).Last(c => !used.Contains(c));
            string drive = letter + ":";
            Subst(drive, _root);
            try
            {
                string alias = drive + "\\out";
                Assert.Throws<ArgumentException>(() => Paths.RequireSeparateTrees(Output, alias));
                using var transaction = new DirectoryTransaction(Output);
                Assert.Throws<IOException>(() => new DirectoryTransaction(alias));
            }
            finally { Subst(drive, "/d"); }
        }

        static void Subst(string drive, string target)
        {
            var start = new ProcessStartInfo("subst.exe") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(drive);
            start.ArgumentList.Add(target);
            using var process = Process.Start(start)!;
            Assert.True(process.WaitForExit(30000));
            Assert.Equal(0, process.ExitCode);
        }

        [Fact]
        public void ReplacingDirectoryWithHardLinkedChild_DoesNotModifySource()
        {
            if (!OperatingSystem.IsWindows()) return;
            Previous();
            File.WriteAllText(Path.Combine(_root, "source.bin"), "source");
            RunCmd("mklink", "/H", "out\\linked.bin", "source.bin");
            using (var transaction = new DirectoryTransaction(Output))
            {
                transaction.CopyExisting(TestContext.Current.CancellationToken);
                File.WriteAllText(Path.Combine(transaction.Stage, "linked.bin"), "replacement");
                transaction.Commit(TestContext.Current.CancellationToken);
            }
            Assert.Equal("source", File.ReadAllText(Path.Combine(_root, "source.bin")));
            Assert.Equal("replacement", File.ReadAllText(Path.Combine(Output, "linked.bin")));
        }

        void RunCmd(params string[] arguments)
        {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _root };
            start.ArgumentList.Add("/c");
            foreach (string argument in arguments) start.ArgumentList.Add(argument); // fixed relative names, no interpolated shell paths
            using var process = Process.Start(start)!;
            Assert.True(process.WaitForExit(30000));
            Assert.Equal(0, process.ExitCode);
        }

        static byte[] Elf()
        {
            var data = new byte[0x200];
            SceElf.Magic.CopyTo(data); data[4] = 2; data[5] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x10), SceElf.EtSceExec);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x12), 0x3E);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x14), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x20), 0x40);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x34), 0x40);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x36), 0x38);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x38), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x40), SceElf.PtLoad);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x44), SceElf.PfX);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x60), (ulong)data.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x68), (ulong)data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x78), SceElf.PtSceModuleParam);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x80), 0x180);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x98), 0x18);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0xA0), 0x18);
            return data;
        }

        sealed class SyncProgress(Action<BackportProgress> action) : IProgress<BackportProgress>
        {
            public void Report(BackportProgress value) => action(value);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetShortPathNameW(string path, StringBuilder shortPath, uint length);

        public void Dispose()
        {
            if (_root.Length > 0)
                Directory.Delete(_root, recursive: true);
        }
    }
}
#endregion
#endif

#if !BACKPORT_SYNTHETIC_ONLY && !BACKPORT_PARSER_TESTS_ONLY
#region Repo
namespace Ps5Backport.Tests
{
    using System.Security.Cryptography;
    using System.Text;

    /// <summary>Tracked test data is mandatory; optional private fixtures skip unless the private lane is required.</summary>
    public static class Repo
    {
        /// <summary>Repository root: the first parent folder that contains Ps5Backport.slnx.</summary>
        public static string Root { get; } = FindRoot();

        static string FindRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Ps5Backport.slnx")))
                    return dir.FullName;
            throw new InvalidOperationException("repository root (folder with Ps5Backport.slnx) not found");
        }

        /// <summary>Absolute path under the repo root; missing tracked data fails, optional private data may skip.</summary>
        public static string Need(params string[] relative)
        {
            string path = Path.Combine([Root, .. relative]);
            if (!File.Exists(path) && !Directory.Exists(path))
                Missing(Path.Combine(relative), relative[0].Replace('\\', '/').Split('/')[0] is ".original" or "work" or "fakelibs");
            return path;
        }

        public static bool RequirePrivate => Environment.GetEnvironmentVariable("PS5BP_REQUIRE_PRIVATE") == "1";

        public static void Missing(string relative, bool privateFixture)
        {
            string message = $"required {(privateFixture ? "private fixture" : "tracked test data")} missing: {relative}";
            if (!privateFixture || RequirePrivate)
                Assert.Fail(message);
            Assert.Skip(message);
        }
    }

    /// <summary>
    /// Recorded expected results (src/Ps5Backport.Tests/golden/). They were first recorded when the C# engine
    /// matched the former Python engine byte for byte, so they carry that proof. After an intended change
    /// (a new fix rule, a new report line), record again with the environment variable PS5BP_RECORD=1 and
    /// review the diff of golden/ before committing it.
    /// </summary>
    public static class Golden
    {
        static string Dir => Path.Combine(Repo.Root, "src", "Ps5Backport.Tests", "golden");

        public static bool Recording => Environment.GetEnvironmentVariable("PS5BP_RECORD") == "1";

        /// <summary>Compares <paramref name="actual"/> with golden/<paramref name="name"/> (LF line endings), or records it.</summary>
        public static void Match(string name, string actual)
        {
            actual = actual.Replace("\r\n", "\n");
            string path = Path.Combine(Dir, name);
            if (Recording)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, actual, new UTF8Encoding(false));
                return;
            }
            if (!File.Exists(path))
                Assert.Fail($"golden/{name} is missing: run the tests once with PS5BP_RECORD=1");
            Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n"), actual);
        }

        public static string Sha(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

        public static string Sha(string text) => Sha(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n")));

        /// <summary>Replaces absolute paths (the repo, temp folders) with placeholders so golden files are portable.</summary>
        public static string Portable(string text, params (string Path, string Name)[] paths)
        {
            foreach (var (path, name) in paths.Append((Repo.Root, "<repo>")).OrderByDescending(p => p.Item1.Length))
                text = text.Replace(path.Replace("\\", "\\\\"), name)   // as escaped in JSON
                           .Replace(path, name).Replace(path.Replace('\\', '/'), name);
            return text;
        }
    }
}
#endregion
#endif

#region SyntheticBinary
namespace Ps5Backport.Tests
{
    using System.Buffers.Binary;
    using System.Text;
    using Ps5Backport.Core;

    /// <summary>Small format-correct fixtures generated from public format fields; no private binaries.</summary>
    internal static class SyntheticBinary
    {
        internal const string Nid = "XXXXXXXXXXX";
        internal const ulong BssVaddr = 0x10000, BssSize = 0x100;

        /// <summary><paramref name="writable"/>: adds a bss-only read-write PT_LOAD at vaddr
        /// <see cref="BssVaddr"/> (memsz <see cref="BssSize"/>); the program headers then move to the file's end.</summary>
        internal static byte[] Elf(byte[]? payload = null, bool imports = false, bool exports = false, bool writable = false)
        {
            int body = 0x500 + (payload?.Length ?? 0);
            var data = new byte[body + (writable ? 4 * 0x38 : 0)];
            int phoff = writable ? body : 0x40;
            SceElf.Magic.CopyTo(data); data[4] = 2; data[5] = 1;
            W16(data, 0x10, SceElf.EtSceExec); W16(data, 0x12, 0x3E); W32(data, 0x14, 1);
            W64(data, 0x20, (ulong)phoff); W16(data, 0x34, 0x40); W16(data, 0x36, 0x38); W16(data, 0x38, writable ? (ushort)4 : (ushort)3);
            Header(0, SceElf.PtLoad, 0, (ulong)data.Length, SceElf.PfX);
            Header(1, SceElf.PtDynamic, 0x100, 0x80, 0);
            Header(2, SceElf.PtSceModuleParam, 0x180, 0x18, 0);
            if (writable)
            {
                Header(3, SceElf.PtLoad, (ulong)body, 0, SceElf.PfW | 4);
                W64(data, phoff + 3 * 0x38 + 0x10, BssVaddr); W64(data, phoff + 3 * 0x38 + 0x18, BssVaddr);
                W64(data, phoff + 3 * 0x38 + 0x28, BssSize);
            }
            var tags = new (ulong, ulong)[]
            {
                (SceElf.DtStrtab, 0x200), (SceElf.DtSymtab, 0x280), (SceElf.DtSceSymtabSz, 72),
                (SceElf.DtRela, 0x300), (SceElf.DtRelaSz, imports ? 24UL : 0),
                (SceElf.DtSceExportLib, 0), (0, 0),
            };
            for (int i = 0; i < tags.Length; i++) { W64(data, 0x100 + i * 16, tags[i].Item1); W64(data, 0x108 + i * 16, tags[i].Item2); }
            Encoding.ASCII.GetBytes(Nid + "#A#A\0").CopyTo(data, 0x201);
            // Symbol 1 is an unresolved function import; symbol 2 defines the same NID in the module's own library.
            W32(data, 0x298, 1); data[0x29C] = SceElf.SttFunc;
            W32(data, 0x2B0, 1); data[0x2B4] = SceElf.SttFunc;
            W16(data, 0x2B6, exports ? (ushort)1 : (ushort)0); W64(data, 0x2B8, exports ? 0x400UL : 0);
            W64(data, 0x300, 0x480); W64(data, 0x308, (1UL << 32) | 1);
            payload?.CopyTo(data, 0x500);
            return data;

            void Header(int index, uint type, ulong offset, ulong size, uint flags)
            {
                int at = phoff + index * 0x38;
                W32(data, at, type); W32(data, at + 4, flags); W64(data, at + 8, offset);
                W64(data, at + 0x10, offset); W64(data, at + 0x20, size); W64(data, at + 0x28, size);
            }
        }

        internal static byte[] LiteralPatch(byte[] source, byte[] target)
        {
            var body = new List<byte>("BPS1"u8.ToArray());
            Varint(source.Length); Varint(target.Length); Varint(0);
            if (target.Length > 0) { Varint(((target.Length - 1L) << 2) | 1); body.AddRange(target); }
            body.AddRange(BitConverter.GetBytes(Crc32.Compute(source)));
            body.AddRange(BitConverter.GetBytes(Crc32.Compute(target)));
            body.AddRange(BitConverter.GetBytes(Crc32.Compute(body.ToArray())));
            return body.ToArray();

            void Varint(long value)
            {
                while (true)
                {
                    byte next = (byte)(value & 127); value >>= 7;
                    if (value == 0) { body.Add((byte)(next | 128)); return; }
                    body.Add(next); value--;
                }
            }
        }

        internal static void W16(byte[] b, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), value);
        internal static void W32(byte[] b, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), value);
        internal static void W64(byte[] b, int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at), value);
    }
}
#endregion

#if !BACKPORT_SYNTHETIC_ONLY && !BACKPORT_PARSER_TESTS_ONLY
#region UbisoftOfflinePopupsTests
namespace Ps5Backport.Tests
{
    using System.Buffers.Binary;
    using Ps5Backport.Core;

    public sealed class UbisoftOfflinePopupsTests
    {
        /// <summary>The probe's recogniser agrees with Apply and changes nothing; the layout stays recognisable after the patch.</summary>
        [Fact]
        public void Recognize_MatchesApplyWithoutChangingBytes()
        {
            var elf = Fixture();
            byte[] before = elf.Data.ToArray();
            Assert.True(UbisoftOfflinePopups.Recognize(elf, TestContext.Current.CancellationToken));
            Assert.Equal(before, elf.Data);
            Assert.Equal(2, UbisoftOfflinePopups.Apply(elf, TestContext.Current.CancellationToken));
            Assert.True(UbisoftOfflinePopups.Recognize(elf, TestContext.Current.CancellationToken));
            Assert.False(UbisoftOfflinePopups.Recognize(new SceElf(SyntheticBinary.Elf()), TestContext.Current.CancellationToken));
        }

        [Fact]
        public void RelocatedLibrary_ChangesOnlyTwoSeverities_AndIsIdempotent()
        {
            var elf = Fixture();
            byte[] before = elf.Data.ToArray();
            Assert.Equal(2, UbisoftOfflinePopups.Apply(elf, TestContext.Current.CancellationToken));
            Assert.Equal(new[] { 0x616, 0x65e }, Enumerable.Range(0, before.Length).Where(i => before[i] != elf.Data[i]).ToArray());
            Assert.Equal(2, elf.Data[0x616]); Assert.Equal(2, elf.Data[0x65e]);
            byte[] patched = elf.Data.ToArray();
            Assert.Equal(0, UbisoftOfflinePopups.Apply(elf, TestContext.Current.CancellationToken));
            Assert.Equal(patched, elf.Data);
        }

        [Fact]
        public void OneAlreadySilentWarning_ChangesOnlyRemainingWarning()
        {
            var elf = Fixture(); elf.Data[0x616] = 2;
            byte[] before = elf.Data.ToArray();
            Assert.Equal(1, UbisoftOfflinePopups.Apply(elf, TestContext.Current.CancellationToken));
            Assert.Equal(new[] { 0x65e }, Enumerable.Range(0, before.Length).Where(i => before[i] != elf.Data[i]).ToArray());
        }

        [Theory]
        [InlineData("wrong-error")]
        [InlineData("missing-partner")]
        [InlineData("wrong-severity")]
        [InlineData("ambiguous-definitions")]
        [InlineData("missing-handler")]
        [InlineData("ambiguous-handler")]
        [InlineData("broken-callback")]
        [InlineData("different-lookup")]
        [InlineData("non-code-lookup")]
        [InlineData("different-error-id-class")]
        [InlineData("different-definition-class")]
        [InlineData("different-error-list")]
        [InlineData("different-allocator")]
        [InlineData("different-barrier")]
        [InlineData("wrong-static-field")]
        [InlineData("code-class-slot")]
        public void UnsupportedOrAmbiguousLayout_LeavesEveryByteUnchanged(string change)
        {
            var elf = Fixture();
            switch (change)
            {
                case "wrong-error": elf.Data[0x60f]++; break;
                case "missing-partner": elf.Data[0x648] = 0x90; break;
                case "wrong-severity": elf.Data[0x65e] = 1; break;
                case "ambiguous-definitions": elf.Data.AsSpan(0x600, 144).CopyTo(elf.Data.AsSpan(0xe00)); break;
                case "missing-handler": elf.Data[0x900] = 0x90; break;
                case "ambiguous-handler": elf.Data.AsSpan(0x900, 81).CopyTo(elf.Data.AsSpan(0xe00)); break;
                case "broken-callback": elf.Data[0x94e] = 0xd0; break;
                case "different-lookup": Rip(elf.Data, 0xc1c, 5, 1, 0x2400); break;
                case "non-code-lookup": Rip(elf.Data, 0x91c, 5, 1, 0x4000); Rip(elf.Data, 0xc1c, 5, 1, 0x4000); break;
                case "different-error-id-class": Rip(elf.Data, 0xc00, 7, 3, 0x4018); break;
                case "different-definition-class": Rip(elf.Data, 0x648, 7, 3, 0x4018); break;
                case "different-error-list": Rip(elf.Data, 0x66f, 7, 3, 0x4018); break;
                case "different-allocator": Rip(elf.Data, 0x64f, 5, 1, 0x2400); break;
                case "different-barrier": Rip(elf.Data, 0x68b, 5, 1, 0x2400); break;
                case "wrong-static-field": elf.Data[0x680]++; break;
                case "code-class-slot": Rip(elf.Data, 0x600, 7, 3, 0x500); Rip(elf.Data, 0x648, 7, 3, 0x500); break;
            }
            byte[] before = elf.Data.ToArray();
            Assert.Equal(0, UbisoftOfflinePopups.Apply(elf, TestContext.Current.CancellationToken));
            Assert.Equal(before, elf.Data);
        }

        [Fact]
        public void DataOnlyLookalikesAndCancellation_DoNotChangeBytes()
        {
            var elf = Fixture();
            elf.Data.AsSpan(0x600, 144).CopyTo(elf.Data.AsSpan(0x4200));
            elf.Data[0x600] = 0x90;
            byte[] before = elf.Data.ToArray();
            Assert.Equal(0, UbisoftOfflinePopups.Apply(elf, TestContext.Current.CancellationToken));
            Assert.Equal(before, elf.Data);
            elf = Fixture(); before = elf.Data.ToArray();
            Assert.Throws<OperationCanceledException>(() => UbisoftOfflinePopups.Apply(elf, new CancellationToken(true)));
            Assert.Equal(before, elf.Data);
        }

        [Theory]
        [InlineData(false, true, false)]
        [InlineData(true, false, false)]
        [InlineData(true, true, true)]
        public void Pipeline_RequiresOptionAndKnownFixes_AndReportsTwoWarnings(bool selected, bool known, bool applied)
        {
            string root = Path.Combine(Path.GetTempPath(), "ps5backport-popups-" + Guid.NewGuid().ToString("N"));
            try
            {
                string game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
                string db = Directory.CreateDirectory(Path.Combine(root, "db")).FullName;
                File.WriteAllBytes(Path.Combine(game, "eboot.bin"), Fixture().Data);
                FirmwareDb.Save(new FirmwareExports("4", "synthetic", false, new()), Path.Combine(db, "4.00.json"));
                var result = Pipeline.Backport(game, Path.Combine(root, "out"), new BackportOptions
                {
                    Firmware = "4", ReferenceDir = db, KnownFixes = known, DryRun = true,
                    Options = selected ? [UbisoftOfflinePopups.Option] : [],
                }, Knowledge.Default, cancel: TestContext.Current.CancellationToken);
                var module = Assert.Single(result.Modules);
                Assert.Empty(module.Error);
                Assert.Equal(applied, module.Patterns.Any(p => p.Name == UbisoftOfflinePopups.Name));
                if (applied) Assert.Equal(2, Assert.Single(module.Patterns, p => p.Name == UbisoftOfflinePopups.Name).Count);
                Assert.False(Directory.Exists(Path.Combine(root, "out")));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        static SceElf Fixture()
        {
            byte[] data = SyntheticBinary.Elf(); Array.Resize(ref data, 0x5000);
            SyntheticBinary.W64(data, 0x60, 0x3000); SyntheticBinary.W64(data, 0x68, 0x3000);
            data.AsSpan(0x40, 3 * 0x38).CopyTo(data.AsSpan(0x1000));
            SyntheticBinary.W64(data, 0x20, 0x1000); SyntheticBinary.W16(data, 0x38, 4);
            const int header = 0x10a8;
            SyntheticBinary.W32(data, header, SceElf.PtLoad); SyntheticBinary.W32(data, header + 4, 4);
            SyntheticBinary.W64(data, header + 8, 0x4000); SyntheticBinary.W64(data, header + 16, 0x4000);
            SyntheticBinary.W64(data, header + 32, 0x1000); SyntheticBinary.W64(data, header + 40, 0x1000);
            // Real compiler bytes from the analyzed library, relocated to a synthetic module.
            Convert.FromHexString("488b3da5743907e8480748ffc74010a8110000c7401c0000000048c7401400000000bf88000000488b0d36ef3907488b91b8000000488982880000004803b9b8000000e8cc4007ff488b3d5d743907e8000748ffc74010a9110000c7401c0000000048c7401400000000bf90000000488b0deeee3907488b91b8000000488982900000004803b9b8000000e8844007ff").CopyTo(data, 0x600);
            Convert.FromHexString("488b3d3c2d3f074c8d7d1083bfe4000000007505e8fa9650ff4c89ffe8727d03004885c00f84d800000083781c02751f4885db7479488b7b40488b4318488b73284883c4205b415c415e415f5dffe0").CopyTo(data, 0x900);
            Convert.FromHexString("488b3d11283f074c8d7d1083bfe4000000007505e8cf9150ff4c89ffe8477803004885c00f84c202000083781c02751d31c04885db0f8495020000488b7328488b7b40ff531831c0e983020000").CopyTo(data, 0xc00);
            foreach (int a in new[] { 0x600, 0x648 }) Rip(data, a, 7, 3, 0x4000);
            foreach (int a in new[] { 0x627, 0x66f }) Rip(data, a, 7, 3, 0x4008);
            foreach (int a in new[] { 0x607, 0x64f }) Rip(data, a, 5, 1, 0x2100);
            foreach (int a in new[] { 0x643, 0x68b }) Rip(data, a, 5, 1, 0x2300);
            foreach (int a in new[] { 0x900, 0xc00 })
            {
                Rip(data, a, 7, 3, 0x4010); Rip(data, a + 20, 5, 1, 0x2000); Rip(data, a + 28, 5, 1, 0x2200);
            }
            return new SceElf(data);
        }

        static void Rip(byte[] data, int instruction, int length, int displacement, int target)
            => BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(instruction + displacement), target - instruction - length);
    }
}
#endregion
#endif

#if !BACKPORT_SYNTHETIC_ONLY && !BACKPORT_PARSER_TESTS_ONLY
#region UnitySkuFallbackTests
namespace Ps5Backport.Tests
{
    using System.Buffers.Binary;
    using Ps5Backport.Core;

    public sealed class UnitySkuFallbackTests
    {
        /// <summary>The probe's recogniser agrees with Apply and changes nothing; the redirect removes the recognised layout.</summary>
        [Fact]
        public void Recognize_MatchesApplyWithoutChangingBytes()
        {
            var elf = Fixture();
            byte[] before = elf.Data.ToArray();
            Assert.True(UnitySkuFallback.Recognize(elf, TestContext.Current.CancellationToken));
            Assert.Equal(before, elf.Data);
            Assert.Equal(1, UnitySkuFallback.Apply(elf, TestContext.Current.CancellationToken));
            Assert.False(UnitySkuFallback.Recognize(elf, TestContext.Current.CancellationToken));
            Assert.False(UnitySkuFallback.Recognize(new SceElf(SyntheticBinary.Elf()), TestContext.Current.CancellationToken));
        }

        [Fact]
        public void LinkedRelocatedCode_UsesGuardedFallback_AndIsIdempotent()
        {
            var elf = Fixture();
            byte[] before = elf.Data.ToArray();
            Assert.Equal(1, UnitySkuFallback.Apply(elf, TestContext.Current.CancellationToken));
            const int entry = 0x6ef;
            Assert.Equal(0xe9, elf.Data[entry]);
            int cave = entry + 5 + BinaryPrimitives.ReadInt32LittleEndian(elf.Data.AsSpan(entry + 1));
            // Same console-tested body, with the resume displacement independently resolved.
            Assert.Equal(Convert.FromHexString("488B7020498B5648837A30007504488B7070E9"), elf.Data.AsSpan(cave, 19).ToArray());
            Assert.Equal(0x740, cave + 23 + BinaryPrimitives.ReadInt32LittleEndian(elf.Data.AsSpan(cave + 19)));
            Assert.All(Enumerable.Range(0, before.Length).Where(i => before[i] != elf.Data[i]),
                i => Assert.True(i >= entry && i < entry + 6 || i >= cave && i < cave + 23));
            byte[] patched = elf.Data.ToArray();
            Assert.Equal(0, UnitySkuFallback.Apply(elf, TestContext.Current.CancellationToken));
            Assert.Equal(patched, elf.Data);
        }

        [Theory]
        [InlineData("missing-consumer")]
        [InlineData("different-error-class")]
        [InlineData("ambiguous-dispatch")]
        [InlineData("different-sku-enum")]
        [InlineData("different-continuation")]
        [InlineData("executable-class-slot")]
        public void IncompleteOrAmbiguousRecognition_LeavesEveryByteUnchanged(string change)
        {
            var elf = Fixture();
            switch (change)
            {
                case "missing-consumer": elf.Data[0x900] = 0x90; break;
                case "different-error-class": Rip(elf.Data, 0x900, 7, 3, 0x4100); break;
                case "ambiguous-dispatch": elf.Data.AsSpan(0x600, 30).CopyTo(elf.Data.AsSpan(0xc00)); break;
                case "different-sku-enum": elf.Data[0x617] = 2; break;
                case "different-continuation": elf.Data[0x975]++; break;
                case "executable-class-slot": Rip(elf.Data, 0x6cc, 7, 3, 0x500); Rip(elf.Data, 0x6e1, 7, 3, 0x500); break;
            }
            byte[] before = elf.Data.ToArray();
            Assert.Equal(0, UnitySkuFallback.Apply(elf, TestContext.Current.CancellationToken));
            Assert.Equal(before, elf.Data);
        }

        [Fact]
        public void CancellationAndMissingPadding_DoNotPartiallyPatch()
        {
            var elf = Fixture();
            byte[] before = elf.Data.ToArray();
            Assert.Throws<OperationCanceledException>(() => UnitySkuFallback.Apply(elf, new CancellationToken(true)));
            Assert.Equal(before, elf.Data);
            elf.Data.AsSpan(0xb00, 81).Fill(0x90);
            before = elf.Data.ToArray();
            Assert.Throws<PatchException>(() => UnitySkuFallback.Apply(elf, TestContext.Current.CancellationToken));
            Assert.Equal(before, elf.Data);
        }

        [Theory]
        [InlineData(false, true, false)]
        [InlineData(true, false, false)]
        [InlineData(true, true, true)]
        public void Pipeline_AppliesOnlySelectedKnownFix_AndReportsIt(bool selected, bool known, bool applied)
        {
            string root = Path.Combine(Path.GetTempPath(), "ps5backport-sku-" + Guid.NewGuid().ToString("N"));
            try
            {
                string game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
                string db = Directory.CreateDirectory(Path.Combine(root, "db")).FullName;
                File.WriteAllBytes(Path.Combine(game, "eboot.bin"), Fixture().Data);
                FirmwareDb.Save(new FirmwareExports("4", "synthetic", false, new()), Path.Combine(db, "4.00.json"));
                var result = Pipeline.Backport(game, Path.Combine(root, "out"), new BackportOptions
                {
                    Firmware = "4", ReferenceDir = db, KnownFixes = known, DryRun = true,
                    Options = selected ? [UnitySkuFallback.Option] : [],
                }, Knowledge.Default, cancel: TestContext.Current.CancellationToken);
                var module = Assert.Single(result.Modules);
                Assert.Empty(module.Error);
                Assert.Equal(applied, module.Patterns.Any(p => p.Name == UnitySkuFallback.Name));
                Assert.False(Directory.Exists(Path.Combine(root, "out")));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        static SceElf Fixture()
        {
            byte[] data = SyntheticBinary.Elf();
            Array.Resize(ref data, 0x5000);
            SyntheticBinary.W64(data, 0x60, 0x3000); SyntheticBinary.W64(data, 0x68, 0x3000);
            // Relocate the header table to add readable type slots without losing the module SDK parameter.
            data.AsSpan(0x40, 3 * 0x38).CopyTo(data.AsSpan(0x1000));
            SyntheticBinary.W64(data, 0x20, 0x1000); SyntheticBinary.W16(data, 0x38, 4);
            const int header = 0x10a8;
            SyntheticBinary.W32(data, header, SceElf.PtLoad); SyntheticBinary.W32(data, header + 4, 4);
            SyntheticBinary.W64(data, header + 8, 0x4000); SyntheticBinary.W64(data, header + 16, 0x4000);
            SyntheticBinary.W64(data, header + 32, 0x1000); SyntheticBinary.W64(data, header + 40, 0x1000);
            Convert.FromHexString("498b46484885c00f84710100008b403083f801747383f8030f85a2000000").CopyTo(data, 0x600);
            Rip(data, 0x618, 6, 2, 0x6c0);
            Convert.FromHexString("488b3d8e284507e82914ffff488b3d621e3e0783bfe400000000750ce814884fff488b3d4d1e3e07488b87b8000000488b7020eb51").CopyTo(data, 0x6c0);
            Rip(data, 0x6cc, 7, 3, 0x4000); Rip(data, 0x6e1, 7, 3, 0x4000);
            Rip(data, 0x6dc, 5, 1, 0x2000);
            data[0x6f4] = 0x4b;
            Convert.FromHexString("488b0df06e3f074889df31d2").CopyTo(data, 0x740);
            Convert.FromHexString("488b3d9c9d98044c8b791883bfe400000000750ce84a07aafc488b3d839d9804488b87b8000000488b40704885c00f84c20000008b70104c89ff31d2e8123e5bfd84c07431488b7b784885ff0f84a4000000498b4620488b80c0000000488bb0f0000000e88ad52e0084c0756583bb8000000000745c").CopyTo(data, 0x900);
            Rip(data, 0x900, 7, 3, 0x4000); Rip(data, 0x919, 7, 3, 0x4000);
            Rip(data, 0x914, 5, 1, 0x2000);
            data[0xb00] = 0xc3; data.AsSpan(0xb01, 80).Fill(0xcc);
            return new SceElf(data);
        }

        static void Rip(byte[] data, int instruction, int length, int displacement, int target)
            => BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(instruction + displacement), target - instruction - length);
    }
}
#endregion
#endif

#if !BACKPORT_SYNTHETIC_ONLY && !BACKPORT_PARSER_TESTS_ONLY
#region BootNoticeTests
namespace Ps5Backport.Tests
{
    using System.Buffers.Binary;
    using System.Text;
    using Ps5Backport.Core;

    public sealed class BootNoticeTests
    {
        [Fact]
        public void RedirectsEntry_IntoCave_WithEncodedMessage_AndIsIdempotent()
        {
            var elf = Fixture();
            byte[] before = elf.Data.ToArray();
            const string msg = "Backport";
            Assert.Equal(1, BootNotice.Apply(elf, msg, 8, TestContext.Current.CancellationToken));

            const int entry = 0x500;
            Assert.Equal(0xE9, elf.Data[entry]);                       // e_entry redirect (6 bytes)
            int cave = entry + 5 + BinaryPrimitives.ReadInt32LittleEndian(elf.Data.AsSpan(entry + 1));

            // Cave replicates the overwritten _start bytes, then saves caller-saved registers.
            Assert.Equal(new byte[] { 0x55, 0x48, 0x89, 0xE5, 0x41, 0x57 }, elf.Data.AsSpan(cave, 6).ToArray());
            Assert.Equal(new byte[] { 0x57, 0x56, 0x52, 0x51, 0x50 }, elf.Data.AsSpan(cave + 6, 5).ToArray());
            // The message and the device path are materialised as immediates (X-only text).
            Assert.True(Contains(elf.Data, cave, "Backport"u8.ToArray()));
            Assert.True(Contains(elf.Data, cave, "/dev/not"u8.ToArray()));
            Assert.True(Contains(elf.Data, cave, "ificatio"u8.ToArray()));
            // Every call routes through the imported libkernel PLT entries (open/write/close/usleep).
            Assert.Equal(new ulong[] { 0x420, 0x430, 0x440, 0x450 },
                CallTargets(elf.Data, cave, cave + 0x200).OrderBy(t => t).ToArray());

            // Idempotent: the entry no longer starts with the _start prologue.
            byte[] patched = elf.Data.ToArray();
            Assert.Equal(0, BootNotice.Apply(elf, msg, 8, TestContext.Current.CancellationToken));
            Assert.Equal(patched, elf.Data);
        }

        /// <summary>
        /// _start is entered like a normal function (rsp = 8 mod 16: the game's own _start pushes 5 registers before
        /// its calls), so the cave's 11 pushes plus its stack allocation must leave rsp 16-aligned at every call.
        /// </summary>
        [Fact]
        public void Cave_KeepsStackAlignedAtCalls()
        {
            var elf = Fixture();
            Assert.Equal(1, BootNotice.Apply(elf, "Hi", 8, TestContext.Current.CancellationToken));
            int cave = 0x500 + 5 + BinaryPrimitives.ReadInt32LittleEndian(elf.Data.AsSpan(0x501));
            int sub = cave;
            while (!(elf.Data[sub] == 0x48 && elf.Data[sub + 1] == 0x81 && elf.Data[sub + 2] == 0xEC))
                sub++;
            int pushes = 2 + 5 + 4;                       // rbp, r15 (replicated prologue); rdi..rax; r8..r11
            Assert.Equal(6 + 5 + 8, sub - cave);          // the pushes are exactly the bytes before "sub rsp"
            int alloc = BinaryPrimitives.ReadInt32LittleEndian(elf.Data.AsSpan(sub + 3));
            Assert.Equal(8, (pushes * 8 + alloc) % 16);   // rsp at each call = entry (8 mod 16) - this = 0 mod 16
        }

        [Fact]
        public void LongMessage_IsCutAtACharacterBoundary()
        {
            var elf = Fixture();
            string message = new string('a', 255) + "é";   // the 2-byte é straddles the 256-byte limit
            Assert.Equal(1, BootNotice.Apply(elf, message, 0, TestContext.Current.CancellationToken));
            int cave = 0x500 + 5 + BinaryPrimitives.ReadInt32LittleEndian(elf.Data.AsSpan(0x501));
            byte[] sevenA = [.. Enumerable.Repeat((byte)'a', 7)];
            Assert.False(Contains(elf.Data, cave, [.. sevenA, 0xC3]));   // no orphaned UTF-8 lead byte
            Assert.True(Contains(elf.Data, cave, [.. sevenA, 0x00]));    // the last word ends after 255 'a's
        }

        /// <summary>usleep takes a 32-bit microsecond count: 4294 s is the largest delay; more must not wrap.</summary>
        [Fact]
        public void Delay_BeyondUsleepRange_IsRejected()
        {
            Assert.Equal(1, BootNotice.Apply(Fixture(), "Hi", 4294, TestContext.Current.CancellationToken));
            Assert.Throws<ArgumentOutOfRangeException>(() => BootNotice.Apply(Fixture(), "Hi", 4295, TestContext.Current.CancellationToken));
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.NotEqual(0, Cli.Run(["backport", "in", "out", "--firmware", "4", "--boot-notice-delay", "5000"], output, error));
            Assert.Contains("--boot-notice-delay", error.ToString());
        }

        [Fact]
        public void Delay_ControlsUsleep_Call()
        {
            Assert.Equal(4, ApplyAndCount(8));   // usleep + open + write + close
            Assert.Equal(3, ApplyAndCount(0));   // open + write + close

            static int ApplyAndCount(int delay)
            {
                var elf = Fixture();
                Assert.Equal(1, BootNotice.Apply(elf, "Hi", delay, TestContext.Current.CancellationToken));
                int cave = 0x500 + 5 + BinaryPrimitives.ReadInt32LittleEndian(elf.Data.AsSpan(0x501));
                return CallTargets(elf.Data, cave, cave + 0x200).Length;
            }
        }

        [Fact]
        public void UnrecognizedEntry_LeavesEveryByteUnchanged()
        {
            // e_entry that does not begin with "push rbp" (0x55).
            var elf = Fixture();
            elf.Data[0x500] = 0x90;
            byte[] before = elf.Data.ToArray();
            Assert.Equal(0, BootNotice.Apply(elf, "x", 8, TestContext.Current.CancellationToken));
            Assert.Equal(before, elf.Data);

            // "push rbp" but missing the mov r14d,[rdi]; mov rbx,rsi signature.
            elf = Fixture();
            elf.Data.AsSpan(0x50A, 6).Fill(0x90);
            before = elf.Data.ToArray();
            Assert.Equal(0, BootNotice.Apply(elf, "x", 8, TestContext.Current.CancellationToken));
            Assert.Equal(before, elf.Data);
        }

        [Theory]
        [InlineData(false, true, false)]
        [InlineData(true, false, false)]
        [InlineData(true, true, true)]
        public void Pipeline_RequiresOptionAndKnownFixes_AndReportsIt(bool selected, bool known, bool applied)
        {
            string root = Path.Combine(Path.GetTempPath(), "ps5backport-notice-" + Guid.NewGuid().ToString("N"));
            try
            {
                string game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
                string db = Directory.CreateDirectory(Path.Combine(root, "db")).FullName;
                File.WriteAllBytes(Path.Combine(game, "eboot.bin"), Fixture().Data);
                FirmwareDb.Save(new FirmwareExports("4", "synthetic", false, new()), Path.Combine(db, "4.00.json"));
                var result = Pipeline.Backport(game, Path.Combine(root, "out"), new BackportOptions
                {
                    Firmware = "4", ReferenceDir = db, KnownFixes = known, DryRun = true,
                    Options = selected ? [BootNotice.Option] : [],
                    BootNoticeMessage = "hi", BootNoticeDelaySeconds = 0,
                }, Knowledge.Default, cancel: TestContext.Current.CancellationToken);
                var module = Assert.Single(result.Modules);
                Assert.Empty(module.Error);
                Assert.Equal(applied, module.Patterns.Any(p => p.Name == BootNotice.Name));
                Assert.False(Directory.Exists(Path.Combine(root, "out")));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        static ulong[] CallTargets(byte[] data, int start, int end)
        {
            var targets = new List<ulong>();
            for (int i = start; i + 5 <= end; i++)
                if (data[i] == 0xE8)
                    targets.Add((ulong)(i + 5) + (ulong)(long)BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(i + 1)));
            return targets.ToArray();
        }

        static bool Contains(byte[] haystack, int start, byte[] needle)
        {
            for (int i = start; i + needle.Length <= haystack.Length; i++)
                if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
            return false;
        }

        static SceElf Fixture()
        {
            // A minimal SCE ELF whose single executable segment holds a real SDK _start prologue and
            // imports sceKernelOpen/Write/Close/Usleep through PLT entries, plus an int3 run for the cave.
            string[] nids = ["1G3lF1Gg1k8", "4wSze92BhLI", "UK2Tl2DWUns", "1jfXLRVzisc"];
            var payload = new byte[0x1200];
            byte[] prologue = [0x55, 0x48, 0x89, 0xE5, 0x41, 0x57, 0x41, 0x56, 0x53, 0x50, 0x44, 0x8B, 0x37, 0x48, 0x89, 0xF3];
            prologue.CopyTo(payload, 0);
            payload[0x100] = 0xC3;                            // ret before the int3 run
            for (int i = 0x101; i < payload.Length; i++) payload[i] = 0xCC;

            byte[] d = SyntheticBinary.Elf(payload, imports: true);
            SyntheticBinary.W64(d, 0x18, 0x500);              // e_entry -> the _start prologue

            // Four PLT (JMPREL) imports: expand the symbol table to 5 entries and the relocation table to 4.
            SyntheticBinary.W64(d, 0x128, 120);               // DtSceSymtabSz = 5 syms
            SyntheticBinary.W64(d, 0x130, SceElf.DtJmpRel);   // DtRela -> DtJmpRel
            SyntheticBinary.W64(d, 0x140, SceElf.DtPltRelSz); // DtRelaSz -> DtPltRelSz
            SyntheticBinary.W64(d, 0x148, 96);                // DtPltRelSz = 4 relocs
            for (int i = 0; i < nids.Length; i++)
            {
                Encoding.ASCII.GetBytes(nids[i] + "#A#A\0").CopyTo(d, 0x201 + i * 16);
                int symAt = 0x298 + i * 24;                   // symbol i + 1
                SyntheticBinary.W32(d, symAt, (uint)(1 + i * 16));
                d[symAt + 4] = SceElf.SttFunc;
                int relAt = 0x300 + i * 24;                   // JMPREL relocation i
                ulong got = 0x480 + (ulong)i * 8;
                SyntheticBinary.W64(d, relAt, got);
                SyntheticBinary.W64(d, relAt + 8, ((ulong)(i + 1) << 32) | 1);
                ulong plt = 0x420 + (ulong)i * 16;            // PLT entry i
                SyntheticBinary.W64(d, (int)got, plt + 6);    // GOT slot -> PLT+6 (lazy-bind)
                d[(int)plt] = 0xFF; d[(int)plt + 1] = 0x25;   // jmp [rip+rel32] -> GOT
                SyntheticBinary.W32(d, (int)plt + 2, (uint)(got - (plt + 6)));
            }
            d[0x380] = Patching.Ret;                          // PLT0 stub (lazy-bind target)
            d.AsSpan(0x381, 32).Fill(Patching.Int3);

            return new SceElf(d);
        }
    }
}
#endregion
#endif
