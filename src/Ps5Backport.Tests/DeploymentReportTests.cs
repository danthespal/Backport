namespace Ps5Backport.Tests
{
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using Ps5Backport.Core;

    public sealed class DeploymentReportTests
    {
        static GameResult Result(params string[] modules)
        {
            var result = new GameResult("missing-source", "missing-output", "4") { TitleId = "PPSA01317" };
            foreach (string path in modules) result.Modules.Add(new ModuleResult(path, "ELF", false));
            return result;
        }

        static JsonObject Deployment(GameResult result) => (JsonObject)Pipeline.ToJson(result)["deployment"]!;
        static string[] Values(JsonObject report, string key) => ((JsonArray)report[key]!).Select(n => n!.GetValue<string>()).ToArray();

        [Theory]
        [InlineData("libSceAgc.sprx", true, true, false)]
        [InlineData("libSceAgcDriver.sprx", true, true, false)]
        [InlineData("libScePsml.sprx", true, true, false)]
        [InlineData("libSceAppContent.sprx", false, false, false)]
        [InlineData("libSceGameUpdate.sprx", false, false, false)]
        [InlineData("libSceNpEntitlementAccess.sprx", false, false, false)]
        [InlineData("libSceAmpr.sprx", false, true, false)]
        [InlineData("libScePlayGo.sprx", false, true, false)]
        [InlineData("libkernel.sprx", false, true, true)]
        [InlineData("custom.sprx", true, true, false)]
        [InlineData("LIBSCEPLAYGO.SPRX", true, true, false)]
        public void AssessedPolicy_DistinguishesModesAndPackages(string name, bool full, bool package, bool kernel)
        {
            string path = "fakelib/" + name;
            var report = Deployment(Result(path));
            Assert.Equal(full ? [path] : [], Values(report, "full_mode_required_for"));
            Assert.Equal(package ? [path] : [], Values(report, "package_emulators_exclude"));
            Assert.Equal(kernel ? [path] : [], Values(report, "excluded_even_in_full"));
        }

        [Fact]
        public void Inventory_IncludesUnchangedFakelib2_ButNotSystemOrNestedModules()
        {
            var result = Result("fakelib2\\libSceAgc.sprx", "sce_module/libScePsml.sprx", "fakelib/nested/custom.sprx", "eboot.bin");
            var report = Deployment(result);
            Assert.Equal(["fakelib2/libSceAgc.sprx"], Values(report, "fakelib_modules"));
            Assert.False(result.Modules[0].Written);
            Assert.Equal(["fakelib2/libSceAgc.sprx"], Values(report, "full_mode_required_for"));
            Assert.Contains(Values(report, "notes"), n => n.Contains("fakelib2 prevents global composition but remains filtered"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EmptyOrEmulatorSet_DoesNotRequireFull(bool emulators)
        {
            var report = Deployment(emulators ? Result("fakelib/libSceAmpr.sprx", "fakelib/libScePlayGo.sprx") : Result("eboot.bin"));
            Assert.Empty(Values(report, "full_mode_required_for"));
            Assert.DoesNotContain(Values(report, "notes"), n => n.StartsWith("Select explicit per-title Full"));
            if (!emulators) Assert.DoesNotContain(Values(report, "notes"), n => n.Contains("global_fakelib_exclude="));
        }

        [Fact]
        public void FailedModule_IsUnassessed_AndConsoleSettingsRemainUnknown()
        {
            var result = Result("fakelib/libSceAgc.sprx");
            result.Modules[0].Error = "invalid ELF";
            var report = Deployment(result);
            Assert.Equal(["fakelib/libSceAgc.sprx"], Values(report, "unassessed_modules"));
            foreach (string key in new[] { "console_loader", "console_settings", "launch_format" })
                Assert.Equal("unknown", report[key]!.GetValue<string>());
            Assert.Contains("planned", report["inventory_scope"]!.GetValue<string>());
            Assert.Contains(Values(report, "notes"), n => n.Contains("module errors"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void LoggingVariant_RequiresEvidenceThatItWasSelected(bool applied)
        {
            var result = Result("fakelib/libScePlayGo.sprx");
            result.Options.Add("playgo_0_6_log");
            result.ParamWarnings.Add("option PlayGo logging: writes /app0/playlgo.log");
            result.ParamWarnings.Add(applied ? "option PlayGo logging: fakelib/libScePlayGo.sprx from store/playgo_0_6_log"
                : "option PlayGo logging: the fakelib set has no playgo_0_6_log/ variant; fakelibs unchanged");
            Assert.Equal(applied ? ["/app0/playlgo.log"] : [], Values(Deployment(result), "diagnostic_log_destinations"));
        }

        [Fact]
        public void AddedDataAndAmprLog_AreNotInventedInputDependencies()
        {
            var result = Result("fakelib/libSceAmpr.sprx", "fakelib/libScePlayGo.sprx");
            result.App0Files.Add("k9.psp");
            result.ParamWarnings.Add("AMPR debug logging enabled: fakelib/libSceAmpr.sprx writes /app0/ampr_emu.log");
            var report = Deployment(result);
            Assert.Equal(["k9.psp"], Values(report, "added_app0_data"));
            Assert.Equal(["/app0/ampr_emu.log"], Values(report, "diagnostic_log_destinations"));
            Assert.Contains(Values(report, "notes"), n => n.Contains("playgo_stub.dat") && n.Contains("optional"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnalyzeAndCook_ReusePublicationEvidence_AndKeepGameBytes(bool dryRun)
        {
            string root = Path.Combine(Path.GetTempPath(), "ps5backport-deployment-" + Guid.NewGuid().ToString("N"));
            try
            {
                string game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
                string reference = Directory.CreateDirectory(Path.Combine(root, "reference")).FullName;
                string output = Path.Combine(root, "out");
                string fakelib = Directory.CreateDirectory(Path.Combine(game, "fakelib2")).FullName;
                byte[] bytes = SyntheticBinary.Elf();
                File.WriteAllBytes(Path.Combine(game, "eboot.bin"), bytes);
                File.WriteAllBytes(Path.Combine(fakelib, "libSceAgc.sprx"), bytes);
                FirmwareDb.Save(new FirmwareExports("4", "synthetic", false, new()), Path.Combine(reference, "4.00.json"));
                var result = Pipeline.Backport(game, output, new BackportOptions { Firmware = "4", ReferenceDir = reference, DryRun = dryRun },
                    new Knowledge(Paths.DataDir), cancel: TestContext.Current.CancellationToken);
                Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(game, "eboot.bin")));
                Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(fakelib, "libSceAgc.sprx")));
                Assert.All(result.Modules, m => Assert.Empty(m.Error));
                var deployment = Deployment(result);
                Assert.Equal(["fakelib2/libSceAgc.sprx"], Values(deployment, "full_mode_required_for"));
                Assert.Equal(Publication.ManifestName, deployment["hash_manifest"]!.GetValue<string>());
                Assert.Contains(Values(deployment, "notes"), n => n.Contains("Analysis has no final output hashes"));
                if (dryRun) Assert.False(Directory.Exists(output));
                else
                {
                    Assert.True(result.Published);
                    Assert.Equal(Pipeline.ToJson(result).ToJsonString(), JsonNode.Parse(File.ReadAllText(Path.Combine(output, Pipeline.ReportJson)))!.ToJsonString());
                    using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, Publication.ManifestName)));
                    Assert.True(manifest.RootElement.GetProperty("Files").TryGetProperty(Pipeline.ReportJson, out _));
                    Assert.Empty(Directory.GetFiles(output, "*deployment*"));
                    Assert.False(File.Exists(Path.Combine(output, "fakelib2/libSceAgc.sprx"))); // unchanged: supplied by the original
                }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
