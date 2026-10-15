namespace Ps5Backport.Tests
{
    using System.Text;
    using System.Text.Json.Nodes;
    using Ps5Backport.Core;

    /// <summary>A cook targets the lowest firmware but runs on every newer console: the report says which stubs the loader
    /// replaces with the native function from which firmware, and which resolved imports a later firmware no longer exports.</summary>
    public sealed class PortabilityReportTests
    {
        const string Cancel = "e6y3WMBYbiM";      // sceSaveDataCancel: default ret0 stub, exported from FW 6.00, no introduced.json fact
        const string Plain = "AbCdEfGhIjK";       // no rule, no fact

        /// <summary>A game folder with one eboot importing <paramref name="nid"/> through the PLT, with a cave for a stub.</summary>
        static string Game(string root, string nid)
        {
            string game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
            byte[] module = SyntheticBinary.Elf(imports: true);
            Encoding.ASCII.GetBytes(nid + "#A#A\0").CopyTo(module, 0x201);
            SyntheticBinary.W64(module, 0x130, SceElf.DtJmpRel);
            SyntheticBinary.W64(module, 0x140, SceElf.DtPltRelSz);
            SyntheticBinary.W64(module, 0x480, 0x426);
            Convert.FromHexString("FF255A0000006800000000").CopyTo(module, 0x420);
            module[0x380] = Patching.Ret;
            module.AsSpan(0x381, 64).Fill(Patching.Int3);
            File.WriteAllBytes(Path.Combine(game, "eboot.bin"), module);
            return game;
        }

        static string Reference(string root, params (string Firmware, string[] Nids)[] lists)
        {
            string reference = Directory.CreateDirectory(Path.Combine(root, "reference")).FullName;
            foreach (var (firmware, nids) in lists)
                FirmwareDb.Save(new FirmwareExports(firmware, "test", false, new() { ["synthetic"] = new(nids, StringComparer.Ordinal) }),
                                Path.Combine(reference, Firmware.Name(firmware) + ".json"));
            return reference;
        }

        [Theory]
        [InlineData(true, "6.00 / firmware_db 6.00")]
        [InlineData(false, "")]
        public void StubbedEntry_NamesTheFirmwareWhoseLoaderBindsTheNativeFunction(bool laterListExportsIt, string expected)
        {
            string root = Path.Combine(Path.GetTempPath(), "ps5backport-portability-stub-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string game = Game(root, Cancel);
                string reference = laterListExportsIt ? Reference(root, ("4", []), ("6", [Cancel])) : Reference(root, ("4", []));
                var knowledge = new Knowledge(Paths.DataDir);
                var result = Pipeline.Backport(game, Path.Combine(root, "output"), new BackportOptions { Firmware = "4", ReferenceDir = reference },
                                               knowledge, cancel: TestContext.Current.CancellationToken);
                Assert.True(result.Published);
                var module = Assert.Single(result.Modules);
                Assert.Empty(module.Error);
                var stub = Assert.Single(module.StubbedNow);
                Assert.Equal((Cancel, "ret0", expected), (stub.Nid, stub.Stub, stub.Introduced));
                Assert.Empty(module.LaterMissing);
                string text = Pipeline.FormatReport(result, knowledge);
                var json = Pipeline.ToJson(result);
                string introduced = json["modules"]![0]!["stubbed_now"]![0]!["introduced"]!.GetValue<string>();
                Assert.Equal(expected, introduced);
                if (laterListExportsIt)
                    Assert.Contains($"stubbed  {Cancel}  {Cancel}  ({stub.Stub} @ {stub.At}; {stub.Origin}; GOT pre-fill, native from FW 6.00 / firmware_db 6.00)", text);
                else
                {
                    Assert.Contains($"stubbed  {Cancel}  {Cancel}  ({stub.Stub} @ {stub.At}; {stub.Origin})", text);
                    Assert.DoesNotContain("native from", text);
                }
            }
            finally { Directory.Delete(root, true); }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ResolvedImport_MissingOnALaterFirmware_IsReported(bool droppedLater)
        {
            string root = Path.Combine(Path.GetTempPath(), "ps5backport-portability-later-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string game = Game(root, Plain);
                // exported on the 4.00 target and on 6.00; 5.00 drops it only in the positive case
                string reference = Reference(root, ("4", [Plain]), ("5", droppedLater ? [] : [Plain]), ("6", [Plain]));
                var knowledge = new Knowledge(Paths.DataDir);
                var result = Pipeline.Backport(game, Path.Combine(root, "output"), new BackportOptions { Firmware = "4", ReferenceDir = reference },
                                               knowledge, cancel: TestContext.Current.CancellationToken);
                Assert.True(result.Published);
                var module = Assert.Single(result.Modules);
                Assert.Empty(module.Error);
                var status = Assert.Single(module.Statuses);
                Assert.Equal(Imports.Reference, status.Status);
                Assert.Empty(module.StubbedNow);
                string text = Pipeline.FormatReport(result, knowledge);
                var later = (JsonArray)Pipeline.ToJson(result)["modules"]![0]!["later_missing"]!;
                if (droppedLater)
                {
                    var entry = Assert.Single(module.LaterMissing);
                    Assert.Equal((Plain, status.Library, "5.00"), (entry.Nid, entry.Library, entry.MissingOn));
                    Assert.Contains("NOTE: 1 import(s) resolve on FW 4 but are not exported by a later firmware", text);
                    Assert.Contains($"missing-later {Plain}  {Plain}  [{status.Library}]  exported on FW 4, not on FW 5.00", text);
                    Assert.Equal("5.00", Assert.Single(later)!["missing_on"]!.GetValue<string>());
                }
                else
                {
                    Assert.Empty(module.LaterMissing);
                    Assert.DoesNotContain("missing-later", text);
                    Assert.Empty(later);
                }
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
