namespace Ps5Backport.Tests
{
    using System.Text;
    using System.Text.Json.Nodes;
    using Ps5Backport.Core;

    /// <summary>The requirements probe: one verdict per optional patch from the game's own modules (FINDINGS 6n).</summary>
    public sealed class ProbeTests
    {
        const string Pad = "W2G-yoyMF5U";   // scePadSetVibrationMode, argument 1 = mode

        /// <summary>A module importing <paramref name="nid"/> through the PLT at 0x420, with call sites whose esi is a constant
        /// 4 (mov), 0 (xor), unknown (load from memory) and 5 (push/pop).</summary>
        static byte[] Module(string nid)
        {
            byte[] module = SyntheticBinary.Elf(imports: true);
            Encoding.ASCII.GetBytes(nid + "#A#A\0").CopyTo(module, 0x201);
            SyntheticBinary.W64(module, 0x130, SceElf.DtJmpRel);
            SyntheticBinary.W64(module, 0x140, SceElf.DtPltRelSz);
            SyntheticBinary.W64(module, 0x480, 0x426);
            Convert.FromHexString("FF255A0000006800000000").CopyTo(module, 0x420);
            module[0x380] = Patching.Ret;
            module.AsSpan(0x381, 0x1f).Fill(Patching.Int3);
            Call(0x3a0, "BE04000000");      // mov esi, 4
            Call(0x3b0, "31F6");            // xor esi, esi
            Call(0x3c0, "8B7510");          // mov esi, [rbp+0x10]
            Call(0x3d0, "6A055E");          // push 5; pop rsi
            return module;

            void Call(int at, string prefix)
            {
                byte[] bytes = Convert.FromHexString(prefix);
                bytes.CopyTo(module, at);
                int call = at + bytes.Length;
                module[call] = 0xE8;
                BitConverter.GetBytes(0x420 - (call + 5)).CopyTo(module, call + 1);
            }
        }

        [Fact]
        public void ArgumentConstants_ReadMovXorAndPushPop_AndReportUnknownLoads()
        {
            var elf = new SceElf(Module(Pad));
            var import = Assert.Single(elf.Imports());
            var sites = Imports.ArgumentConstants(elf, import, 1, TestContext.Current.CancellationToken);
            Assert.Equal([(0x3a5UL, (uint?)4), (0x3b2UL, 0u), (0x3c3UL, null), (0x3d3UL, 5u)], sites);
            Assert.Empty(Imports.ArgumentConstants(elf, import with { Plt = false }, 1, TestContext.Current.CancellationToken));
            Assert.Throws<ArgumentOutOfRangeException>(() => Imports.ArgumentConstants(elf, import, 6, TestContext.Current.CancellationToken));
        }

        [Fact]
        public void ProbeRules_AreLoadedFromData_AndInvalidKindsAreRejected()
        {
            var k = new Knowledge(Paths.DataDir);
            Assert.Equal(OptionProbe.KindArgument, k.Options["pad_vibration_compat"].Probe.Kind);
            Assert.Equal((Pad, 1, 3L), (k.Options["pad_vibration_compat"].Probe.Nid, k.Options["pad_vibration_compat"].Probe.Argument, k.Options["pad_vibration_compat"].Probe.Above));
            Assert.Equal((OptionProbe.KindDetector, OptionProbe.Needed), (k.Options["unity_sku_offline"].Probe.Kind, k.Options["unity_sku_offline"].Probe.Recognized));
            Assert.Equal((OptionProbe.KindDetector, OptionProbe.Applicable), (k.Options["ubisoft_offline_popups"].Probe.Kind, k.Options["ubisoft_offline_popups"].Probe.Recognized));
            foreach (string console in new[] { "auto_pro", "force_pro", "playgo_0_6", "playgo_0_6_log", "psml_kpn9", "boot_notice" })
                Assert.Equal(OptionProbe.KindConsole, k.Options[console].Probe.Kind);
            foreach (string plain in new[] { "save_prepare_compat", "audio_speaker_compat", "audio_port_compat", "audio_bed_71", "gpu_scratch", "np_error_dialogs" })
                Assert.Equal("", k.Options[plain].Probe.Kind);

            string data = Path.Combine(Path.GetTempPath(), "ps5backport-probe-data-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(data);
            try
            {
                foreach (string file in Directory.EnumerateFiles(Paths.DataDir, "*.json"))
                    File.Copy(file, Path.Combine(data, Path.GetFileName(file)));
                foreach (var (probe, expected) in new[]
                {
                    ("""{"kind":"bogus"}""", "unknown probe kind"),
                    ("""{"kind":"detector"}""", "recognized"),
                    ("""{"kind":"argument","nid":"XXXXXXXXXXX","argument":1}""", "one of the option's fixes"),
                    ("""{"kind":"argument","nid":"W2G-yoyMF5U","argument":6}""", "argument 0..5"),
                    ("""{"kind":"argument","nid":"W2G-yoyMF5U","argumnt":1}""", "unknown key 'argumnt'"),
                })
                {
                    string path = Path.Combine(data, "fixes.json");
                    var root = JsonNode.Parse(File.ReadAllText(path))!;
                    root["options"]!["pad_vibration_compat"]!["probe"] = JsonNode.Parse(probe);
                    File.WriteAllText(path, root.ToJsonString());
                    var error = Assert.Throws<InvalidDataException>(() => new Knowledge(data));
                    Assert.Contains(expected, error.Message);
                }
            }
            finally { Directory.Delete(data, true); }
        }

        [Fact]
        public void Probe_GivesEveryOptionAVerdict_AndTheCookRecordsTheSame()
        {
            string root = Path.Combine(Path.GetTempPath(), "ps5backport-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
                string reference = Directory.CreateDirectory(Path.Combine(root, "reference")).FullName;
                File.WriteAllBytes(Path.Combine(game, "eboot.bin"), Module(Pad));
                FirmwareDb.Save(new FirmwareExports("4", "test", false, new() { ["libScePad"] = new() { Pad } }), Path.Combine(reference, "4.00.json"));
                var knowledge = new Knowledge(Paths.DataDir);

                var probe = Pipeline.Probe(game, knowledge, TestContext.Current.CancellationToken);
                Assert.Equal(knowledge.Options.Keys.Order(StringComparer.Ordinal), probe.Select(p => p.Key).Order(StringComparer.Ordinal));
                var byKey = probe.ToDictionary(p => p.Key);
                Assert.Equal(OptionVerdict.Needed, byKey["pad_vibration_compat"].Verdict);
                Assert.Contains("esi = 4/5 (above 3) at 2 of 4 call site(s), e.g. eboot.bin 0x3a5", byKey["pad_vibration_compat"].Evidence);
                Assert.Equal(OptionVerdict.NotApplicable, byKey["auto_pro"].Verdict);              // console choice, but nothing imported
                Assert.Equal(OptionVerdict.NotApplicable, byKey["force_pro"].Verdict);
                Assert.Equal(OptionVerdict.ConsoleChoice, byKey["boot_notice"].Verdict);
                Assert.Equal(OptionVerdict.ConsoleChoice, byKey["playgo_0_6"].Verdict);
                Assert.Equal(OptionVerdict.NotApplicable, byKey["psml_kpn9"].Verdict);
                Assert.Equal(OptionVerdict.NotApplicable, byKey["unity_sku_offline"].Verdict);
                Assert.Equal(OptionVerdict.NotApplicable, byKey["ubisoft_offline_popups"].Verdict);
                Assert.Equal(OptionVerdict.NotApplicable, byKey["gpu_scratch"].Verdict);
                foreach (string key in new[] { "save_prepare_compat", "audio_speaker_compat", "audio_port_compat", "audio_bed_71", "np_error_dialogs" })
                    Assert.Equal(OptionVerdict.NotApplicable, byKey[key].Verdict);

                var options = new BackportOptions { Firmware = "4", ReferenceDir = reference, DryRun = true };
                var analysis = Pipeline.Backport(game, Path.Combine(root, "output"), options, knowledge, cancel: TestContext.Current.CancellationToken);
                Assert.Equal(probe, analysis.Recommendations);
                string text = Pipeline.FormatReport(analysis, knowledge);
                Assert.Contains("probe needed         pad_vibration_compat  scePadSetVibrationMode is called with esi = 4/5", text);
                Assert.Contains("probe console-choice boot_notice", text);
                var json = (JsonArray)Pipeline.ToJson(analysis)["recommendations"]!;
                Assert.Equal(knowledge.Options.Count, json.Count);
                Assert.Equal("needed", json.Single(n => n!["key"]!.GetValue<string>() == "pad_vibration_compat")!["verdict"]!.GetValue<string>());
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void Probe_ReportsApplicable_WhenAnImportedArgumentIsNotAConstant()
        {
            string root = Path.Combine(Path.GetTempPath(), "ps5backport-probe-unknown-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
                byte[] module = Module(Pad);
                // keep only the memory-load call site: overwrite the other three calls with int3 padding
                foreach (int at in new[] { 0x3a0, 0x3b0, 0x3d0 }) module.AsSpan(at, 10).Fill(Patching.Int3);
                File.WriteAllBytes(Path.Combine(game, "eboot.bin"), module);
                var verdict = Pipeline.Probe(game, new Knowledge(Paths.DataDir), TestContext.Current.CancellationToken).Single(p => p.Key == "pad_vibration_compat");
                Assert.Equal(OptionVerdict.Applicable, verdict.Verdict);
                Assert.Contains("esi is not a readable constant at 1 of 1 call site(s)", verdict.Evidence);
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
