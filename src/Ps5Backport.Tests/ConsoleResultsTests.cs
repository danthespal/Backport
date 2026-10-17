namespace Ps5Backport.Tests
{
    using System.Text.Json.Nodes;
    using Ps5Backport.Core;

    /// <summary>Console results and the tester list (data/console_results.json, data/testers.json): validation, bands, pinned
    /// evidence, the cook report's evidence lines and `results add`.</summary>
    public sealed class ConsoleResultsTests : IDisposable
    {
        readonly string _root = Path.Combine(Path.GetTempPath(), "ps5backport-results-" + Guid.NewGuid().ToString("N"));
        string Data => Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }

        const string Testers = """
            {"testers":{"t450":{"model":"base","firmware":"4.50"},"t550":{"model":"slim","firmware":"5.50"},
                        "t860":{"model":"fat","firmware":"8.60"},"t1360":{"model":"pro","firmware":"13.60"}}}
            """;

        void Write(string testers, params JsonObject[] results)
        {
            File.WriteAllText(Path.Combine(Data, ConsoleResults.TestersFile), testers);
            File.WriteAllText(Path.Combine(Data, ConsoleResults.ResultsFile),
                new JsonObject { ["results"] = new JsonArray(results.Select(r => (JsonNode)r).ToArray()) }.ToJsonString());
        }

        static JsonObject Run(string tester, string firmware, string outcome, params (string Item, string Verdict, string Fingerprint)[] tests) => new()
        {
            ["date"] = "2026-10-16", ["tester"] = tester, ["firmware"] = firmware, ["title"] = "PPSA00001", ["outcome"] = outcome,
            ["tests"] = new JsonArray(tests.Select(t => (JsonNode)new JsonObject { ["item"] = t.Item, ["verdict"] = t.Verdict, ["fingerprint"] = t.Fingerprint }).ToArray()),
        };

        [Theory]
        [InlineData("4.00", "4.xx")]
        [InlineData("4.51", "4.xx")]
        [InlineData("5.02", "5-7")]
        [InlineData("7.61", "5-7")]
        [InlineData("8.00", "8-9")]
        [InlineData("9.60", "8-9")]
        [InlineData("10.01", "10+")]
        [InlineData("13.60", "10+")]
        public void Band_FollowsTheFourFirmwareBands(string firmware, string band) => Assert.Equal(band, ConsoleResults.Band(firmware));

        [Fact]
        public void MissingFiles_AreEmpty_AndTheRepositoryDataLoads()
        {
            var empty = ConsoleResults.Load(Data);
            Assert.Empty(empty.Testers);
            Assert.Empty(empty.Results);
            var repo = ConsoleResults.Load(Repo.Need("data"));
            Assert.All(repo.Results, r => Assert.True(repo.Testers.ContainsKey(r.Tester)));
        }

        [Theory]
        [InlineData("""{"date":"2026-10-16","tester":"t450","firmware":"4.50","title":"T","outcome":"plays","sorce":"x"}""", "unknown key 'sorce'")]
        [InlineData("""{"date":"2026-10-16","tester":"nobody","firmware":"4.50","title":"T","outcome":"plays"}""", "unknown tester 'nobody'")]
        [InlineData("""{"date":"2026-10-16","tester":"t450","firmware":"4.50","title":"T","outcome":"works"}""", "outcome 'works'")]
        [InlineData("""{"date":"16.10.2026","tester":"t450","firmware":"4.50","title":"T","outcome":"plays"}""", "yyyy-MM-dd")]
        [InlineData("""{"date":"2026-10-16","tester":"t450","firmware":"4.5x","title":"T","outcome":"plays"}""", "4.5x")]
        [InlineData("""{"date":"2026-10-16","tester":"t450","firmware":"4.50","title":"T","outcome":"plays","tests":[{"item":"opt:x","verdict":"pass"}]}""", "item 'opt:x'")]
        [InlineData("""{"date":"2026-10-16","tester":"t450","firmware":"4.50","title":"T","outcome":"plays","tests":[{"item":"option:x","verdict":"ok"}]}""", "verdict 'ok'")]
        [InlineData("""{"date":"2026-10-16","tester":"t450","firmware":"4.50","title":"T","outcome":"plays","tests":[{"item":"option:x","verdict":"pass","fingerprint":"XYZ"}]}""", "12 lowercase hex")]
        [InlineData("""{"date":"2026-10-16","tester":"t450","firmware":"4.50","title":"T","outcome":"plays","tests":[{"item":"option:x","verdict":"pass"},{"item":"option:x","verdict":"fail"}]}""", "listed twice")]
        public void InvalidResult_FailsAtLoadWithItsFile(string result, string message)
        {
            Write(Testers, JsonNode.Parse(result)!.AsObject());
            var error = Assert.Throws<InvalidDataException>(() => ConsoleResults.Load(Data));
            Assert.Contains(ConsoleResults.ResultsFile, error.Message);
            Assert.Contains(message, error.Message);
        }

        [Fact]
        public void InvalidTesterModel_FailsAtLoad()
        {
            Write("""{"testers":{"t":{"model":"ps5","firmware":"4.50"}}}""");
            Assert.Contains("model 'ps5'", Assert.Throws<InvalidDataException>(() => ConsoleResults.Load(Data)).Message);
        }

        /// <summary>Wording does not change a rule's fingerprint; anything that changes the patched bytes or when it applies does.</summary>
        [Fact]
        public void RuleFingerprint_IgnoresWording_ButNotBehaviour()
        {
            var k = new Knowledge(Paths.DataDir);
            var fix = k.Fixes[GuardedFixTests.Set3DLatency];
            string current = ConsoleResults.RuleFingerprint(fix, k);
            Assert.Matches("^[0-9a-f]{12}$", current);
            Assert.Equal(current, ConsoleResults.RuleFingerprint(fix with { Note = "reworded", Rationale = "", Proven = true, Sources = [] }, k));
            Assert.NotEqual(current, ConsoleResults.RuleFingerprint(fix with { Below = "9.00" }, k));
            Assert.NotEqual(current, ConsoleResults.RuleFingerprint(fix with { Kind = Fix.KindPltStub, Guard = "" }, k));
            Assert.NotEqual(current, ConsoleResults.RuleFingerprint(fix with { Stub = "ret:1" }, k));
        }

        /// <summary>Only runs of the current form count; an item with a pass on all four bands and no failure is a candidate,
        /// one current failure makes it a warning instead.</summary>
        [Fact]
        public void Evidence_CountsTheCurrentForm_AndFindsCandidatesAndFailures()
        {
            const string item = "option:demo", form = "0123456789ab", earlier = "ba9876543210";
            Write(Testers,
                Run("t450", "4.50", "plays", (item, "pass", form)),
                Run("t550", "5.50", "plays", (item, "pass", form)),
                Run("t860", "8.60", "plays", (item, "pass", form), ("fakelib:4", "pass", "")),
                Run("t1360", "13.60", "plays", (item, "pass", form)),
                Run("t1360", "13.60", "crash", (item, "fail", earlier)));
            var results = ConsoleResults.Load(Data);
            var bands = results.Evidence(item, form);
            Assert.Equal([new(1, 0, 0, 0), new(1, 0, 0, 0), new(1, 0, 0, 0), new(1, 0, 0, 1)], bands);
            Assert.Equal("4.xx 1 pass | 5-7 1 pass | 8-9 1 pass | 10+ 1 pass, 1 old", ConsoleResults.Summarize(bands));
            var entry = Assert.Single(results.ForCook([new ConsoleItem(item, form, true)]));
            Assert.True(entry.Candidate);
            Assert.Empty(entry.Failures);
            Assert.False(Assert.Single(results.ForCook([new ConsoleItem(item, form, false)])).Candidate);   // proven items are not candidates
            Assert.Empty(results.ForCook([new ConsoleItem("fakelib:7", "aaaaaaaaaaaa", false)]));            // no runs, not experimental

            results.Results.Add(new ConsoleResult("2026-10-17", "t450", "4.51", "PPSA00002", "", "", "", "crash", "0xa0d0c006", [new(item, "fail", form)], "", "", "", ""));
            entry = Assert.Single(results.ForCook([new ConsoleItem(item, form, true)]));
            Assert.False(entry.Candidate);
            Assert.Equal("4.xx: 2026-10-17 PPSA00002 on FW 4.51 (t450): 0xa0d0c006", Assert.Single(entry.Failures));
        }

        /// <summary>A cook always records what it applied (console_items); the evidence lines appear only when the caller passes
        /// results, so recorded goldens do not move with every new result.</summary>
        [Fact]
        public void Cook_RecordsItems_AndReportsEvidenceOnlyWhenGiven()
        {
            var (game, reference) = GuardedFixTests.Set3DLatencyGame(_root, "4", exported: false);
            var k = new Knowledge(Paths.DataDir);
            string form = ConsoleResults.RuleFingerprint(k.Fixes[GuardedFixTests.Set3DLatency], k);
            const string item = "rule:" + GuardedFixTests.Set3DLatency;
            Write(Testers,
                Run("t450", "4.50", "plays", (item, "pass", form)), Run("t550", "5.50", "plays", (item, "pass", form)),
                Run("t860", "8.60", "plays", (item, "pass", form)), Run("t1360", "13.60", "plays", (item, "pass", form)));
            var options = new BackportOptions { Firmware = "4", ReferenceDir = reference };

            string plain = Path.Combine(_root, "plain"), withResults = Path.Combine(_root, "evidence");
            Pipeline.Backport(game, plain, options, k, cancel: TestContext.Current.CancellationToken);
            Pipeline.Backport(game, withResults, options with { ConsoleResults = ConsoleResults.Load(Data) }, k, cancel: TestContext.Current.CancellationToken);

            var json = JsonNode.Parse(File.ReadAllText(Path.Combine(plain, Pipeline.ReportJson)))!.AsObject();
            Assert.Equal(new ConsoleItem(item, form, true), Assert.Single(ConsoleResults.ItemsFromReport(json)));
            Assert.Null(json["console_evidence"]);
            Assert.DoesNotContain("  console  ", File.ReadAllText(Path.Combine(plain, Pipeline.ReportTxt)));

            string text = File.ReadAllText(Path.Combine(withResults, Pipeline.ReportTxt));
            Assert.Contains($"  console  {item}  4.xx 1 pass | 5-7 1 pass | 8-9 1 pass | 10+ 1 pass", text);
            Assert.Contains($"  NOTE: promotion candidate: {item} passed on every firmware band", text);
            var evidence = JsonNode.Parse(File.ReadAllText(Path.Combine(withResults, Pipeline.ReportJson)))!["console_evidence"]!.AsArray();
            Assert.True(Assert.Single(evidence)!["candidate"]!.GetValue<bool>());
        }

        /// <summary>`results add` takes the console firmware from the klog name, the outcome and error from its fatal event, and
        /// the tested items with the forms the cook applied.</summary>
        [Fact]
        public void ResultsAdd_RecordsAKlogCrashAgainstTheCook()
        {
            var (game, reference) = GuardedFixTests.Set3DLatencyGame(_root, "4", exported: false);
            string cook = Path.Combine(_root, "PPSA00001_backport_4.00");
            var k = new Knowledge(Paths.DataDir);
            Pipeline.Backport(game, cook, new BackportOptions { Firmware = "4", ReferenceDir = reference }, k, cancel: TestContext.Current.CancellationToken);
            Write(Testers);
            string log = Path.Combine(_root, "PS5-FAT-8.60-20261016-011836-Demo.log");
            File.WriteAllText(log, "# A user thread receives a fatal signal\n# signal: 11 (SIGSEGV)\n# reason: page fault\n# rip: 00000000006dba52\n");

            var output = new StringWriter();
            int code = Cli.Run(["results", "add", "--data", Data, "--tester", "t860", "--build", cook, "--log", log, "--title", "PPSA00001",
                                "--test", "option:agc_mode2=fail", "--date", "2026-10-16"], output, new StringWriter());
            Assert.True(code == 0, output.ToString());

            var r = Assert.Single(ConsoleResults.Load(Data).Results);
            Assert.Equal(("t860", "8.60", "crash", "PPSA00001_backport_4.00", "4.00", "PS5-FAT-8.60-20261016-011836-Demo.log"),
                         (r.Tester, r.Firmware, r.Outcome, r.Build, r.Target, r.Log));
            Assert.StartsWith("11 (SIGSEGV) page fault RIP 0x6dba52", r.Error);
            Assert.Equal(64, r.EbootSha256.Length);
            string form = ConsoleResults.RuleFingerprint(k.Fixes[GuardedFixTests.Set3DLatency], k);
            Assert.Contains(new ResultTest("rule:" + GuardedFixTests.Set3DLatency, "unknown", form), r.Tests);
            Assert.Contains(new ResultTest("option:agc_mode2", "fail", ConsoleResults.OptionFingerprint(k.Options["agc_mode2"], k)), r.Tests);
        }
    }
}
