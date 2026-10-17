namespace Ps5Backport.Tests
{
    using System.Text.Json.Nodes;
    using Ps5Backport.Core;

    /// <summary>Promotion edits, the matrix, send-next lists and testers (ConsoleResults.Workflow.cs).</summary>
    public sealed class ConsoleResultsWorkflowTests : IDisposable
    {
        readonly string _root = Path.Combine(Path.GetTempPath(), "ps5backport-workflow-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }

        /// <summary>The repository's rule data plus the given console results, in a scratch data folder.</summary>
        internal static string DataCopy(string root, string testers, string results)
        {
            string data = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
            foreach (string file in Directory.EnumerateFiles(Paths.DataDir, "*.json"))
                if (Path.GetFileName(file) is not (ConsoleResults.TestersFile or ConsoleResults.ResultsFile))
                    File.Copy(file, Path.Combine(data, Path.GetFileName(file)));
            File.WriteAllText(Path.Combine(data, ConsoleResults.TestersFile), testers);
            File.WriteAllText(Path.Combine(data, ConsoleResults.ResultsFile), results);
            return data;
        }

        internal const string FourTesters = """
            {"testers":{"t450":{"model":"base","firmware":"4.50"},"t550":{"model":"slim","firmware":"5.50"},
                        "t860":{"model":"fat","firmware":"8.60"},"t1360":{"model":"base","firmware":"13.60"}}}
            """;

        /// <summary>Passing runs of <paramref name="item"/> (pinned to <paramref name="fingerprint"/>) on the given testers.</summary>
        internal static string Passes(string item, string fingerprint, params string[] testers) => new JsonObject
        {
            ["results"] = new JsonArray(testers.Select(t => (JsonNode)new JsonObject
            {
                ["date"] = "2026-10-17", ["tester"] = t,
                ["firmware"] = t switch { "t450" => "4.50", "t550" => "5.50", "t860" => "8.60", _ => "13.60" },
                ["title"] = "PPSA00001", ["outcome"] = "plays",
                ["tests"] = new JsonArray(new JsonObject { ["item"] = item, ["verdict"] = "pass", ["fingerprint"] = fingerprint }),
            }).ToArray()),
        }.ToJsonString();

        [Fact]
        public void MarkProven_RemovesOnlyTheFlag_KeepingTheFormatting()
        {
            const string text = "{\r\n  \"fixes\": {\r\n    \"AAAAAAAAAAA\": {\r\n      \"kind\": \"stub\",\r\n      \"proven\": false,\r\n      \"note\": \"a, b\"\r\n    },\r\n" +
                                "    \"BBBBBBBBBBB\": {\r\n      \"kind\": \"stub\",\r\n      \"proven\": false\r\n    }\r\n  },\r\n" +
                                "  \"options\": {\"o\": {\"fixes\": {\"CCCCCCCCCCC\": {\"kind\": \"adapter\", \"proven\": false, \"when\": \"always\"}}}}\r\n}\r\n";
            Assert.Equal(text.Replace("      \"proven\": false,\r\n", ""), ConsoleResults.MarkProven(text, null, ["AAAAAAAAAAA"]));
            Assert.Equal(text.Replace(",\r\n      \"proven\": false\r\n", "\r\n"), ConsoleResults.MarkProven(text, null, ["BBBBBBBBBBB"]));
            Assert.Equal(text.Replace("\"proven\": false, ", ""), ConsoleResults.MarkProven(text, "o", ["CCCCCCCCCCC"]));
            Assert.Equal(text.Replace("      \"proven\": false,\r\n", "").Replace(",\r\n      \"proven\": false\r\n", "\r\n"),
                         ConsoleResults.MarkProven(text, null, ["BBBBBBBBBBB", "AAAAAAAAAAA"]));
            Assert.Throws<InvalidOperationException>(() => ConsoleResults.MarkProven(ConsoleResults.MarkProven(text, null, ["AAAAAAAAAAA"]), null, ["AAAAAAAAAAA"]));
            Assert.Throws<KeyNotFoundException>(() => ConsoleResults.MarkProven(text, null, ["CCCCCCCCCCC"]));   // only inside the option
        }

        /// <summary>On the real fixes.json: one line goes, the rule data still loads, and the rule is proven.</summary>
        [Fact]
        public void ApplyPromotion_OnTheRealRuleData_ChangesOneLine()
        {
            string data = DataCopy(_root, FourTesters, """{"results":[]}""");
            string before = File.ReadAllText(Path.Combine(data, "fixes.json"));
            var plan = ConsoleResults.Plan("rule:" + GuardedFixTests.Set3DLatency, new Knowledge(data));
            Assert.Equal([GuardedFixTests.Set3DLatency], plan.ProveNids);
            Assert.Equal([GuardedFixTests.Set3DLatency], ConsoleResults.ApplyPromotion(data, plan));
            string after = File.ReadAllText(Path.Combine(data, "fixes.json"));
            Assert.Equal(before.Split('\n').Length - 1, after.Split('\n').Length);
            Assert.True(new Knowledge(data).Fixes[GuardedFixTests.Set3DLatency].Proven);
            Assert.False(ConsoleResults.Plan("rule:" + GuardedFixTests.Set3DLatency, new Knowledge(data)).CanApply);
        }

        [Fact]
        public void Plan_KeepsDefaultsAndStoreChangesManual()
        {
            var k = new Knowledge(Paths.DataDir);
            var option = ConsoleResults.Plan("option:auto_pro", k);
            Assert.Equal("auto_pro", option.OptionKey);
            Assert.Contains("BfBDZGbti7A", option.ProveNids);
            Assert.Contains("stays opt-in", option.Manual);
            var variant = ConsoleResults.Plan("fakelib:4/agc_mode2", k);
            Assert.False(variant.CanApply);
            Assert.Contains("fakelibs/4/agc_mode2", variant.Manual);
            Assert.False(ConsoleResults.Plan("rule:ZP4e7rlzOUk", k).CanApply);   // already proven
        }

        /// <summary>A pinned pass on every band makes a candidate; send-next lists only items run elsewhere but not on the tester's band.</summary>
        [Fact]
        public void Matrix_FindsCandidates_AndSendNextListsOnlyGaps()
        {
            var k = new Knowledge(Paths.DataDir);
            const string item = "rule:" + GuardedFixTests.Set3DLatency;
            string fp = ConsoleResults.RuleFingerprint(k.Fixes[GuardedFixTests.Set3DLatency], k);
            string data = DataCopy(_root, FourTesters, Passes(item, fp, "t450", "t860", "t1360"));
            var results = ConsoleResults.Load(data);
            var matrix = results.Matrix(k, null);
            var row = matrix.Single(r => r.Item == item);
            Assert.Equal("sceAudioOut2Set3DLatency", row.Label);
            Assert.False(row.Candidate);   // 5-7 missing
            var next = results.SendNext(matrix).ToDictionary(x => x.Tester.Handle, x => x.Items);
            Assert.Equal([item], next["t550"]);
            Assert.Empty(next["t450"]);

            File.WriteAllText(Path.Combine(data, ConsoleResults.ResultsFile), Passes(item, fp, "t450", "t550", "t860", "t1360"));
            Assert.True(ConsoleResults.Load(data).Matrix(k, null).Single(r => r.Item == item).Candidate);
        }

        [Fact]
        public void AddTester_ValidatesHandleModelAndFirmware()
        {
            string data = DataCopy(_root, FourTesters, """{"results":[]}""");
            var results = ConsoleResults.Load(data);
            results.AddTester(new Tester("tester-503", "slim", "5.02", "", ""));
            Assert.Throws<ArgumentException>(() => results.AddTester(new Tester("Tester 1", "slim", "5.02", "", "")));
            Assert.Throws<ArgumentException>(() => results.AddTester(new Tester("t450", "slim", "5.02", "", "")));
            Assert.Throws<ArgumentException>(() => results.AddTester(new Tester("x", "ps5", "5.02", "", "")));
            results.Save();
            Assert.Equal("5.02", ConsoleResults.Load(data).Testers["tester-503"].Firmware);
        }
    }
}
