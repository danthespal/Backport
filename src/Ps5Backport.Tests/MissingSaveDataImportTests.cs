namespace Ps5Backport.Tests
{
    using Ps5Backport.Core;

    /// <summary>
    /// FINDINGS 6v: functions FW 4.xx lacks that games import. sceSaveDataDirNameSearch2 becomes sceSaveDataDirNameSearch (the same
    /// client code), sceSaveDataConvert reports "no PS4 save" (SAVE_DATA_ERROR_NOT_FOUND) and sceCoredumpWriteUserString returns 0.
    /// </summary>
    public sealed class MissingSaveDataImportTests : IDisposable
    {
        const string DirNameSearch2 = "PHnuI4LhuRk", DirNameSearch = "dyIhnXq-0SM", Convert_ = "2mfSRGdshtk", CoredumpUserString = "32KQRUK13kI";
        readonly string _root = Path.Combine(Path.GetTempPath(), "ps5backport-missing-savedata-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }

        [Fact]
        public void Rules_AreDerivedAndUseTheNativeAnswers()
        {
            var fixes = new Knowledge(Paths.DataDir).Fixes;
            Assert.Equal((Fix.KindSwap, DirNameSearch, "", Fix.WhenMissing, false),
                         (fixes[DirNameSearch2].Kind, fixes[DirNameSearch2].NewNid, fixes[DirNameSearch2].NewLibrary, fixes[DirNameSearch2].When, fixes[DirNameSearch2].Proven));
            Assert.Equal((Fix.KindStub, "ret:0x809f0008", Fix.WhenMissing, false),
                         (fixes[Convert_].Kind, fixes[Convert_].Stub, fixes[Convert_].When, fixes[Convert_].Proven));
            Assert.Equal((Fix.KindStub, "ret0", Fix.WhenMissing, false),
                         (fixes[CoredumpUserString].Kind, fixes[CoredumpUserString].Stub, fixes[CoredumpUserString].When, fixes[CoredumpUserString].Proven));
        }

        /// <summary>FW 4.00 lacks all three and exports the swap target under the importing library; each is native from the
        /// firmware the rules name.</summary>
        [Fact]
        public void FirmwareDb_BacksTheRules()
        {
            var knowledge = new Knowledge(Repo.Need("data"));
            var databases = FirmwareDb.LoadAll(Repo.Need("firmware_db"));
            var fw400 = databases.Single(d => d.Firmware == "4.00");
            Assert.DoesNotContain(DirNameSearch2, fw400.Nids);
            Assert.DoesNotContain(Convert_, fw400.Nids);
            Assert.DoesNotContain(CoredumpUserString, fw400.Nids);
            Assert.Contains(DirNameSearch, fw400.Modules["libSceSaveData_native"]);
            Assert.Equal("5.02", FirmwareDb.Introduced(DirNameSearch2, databases, knowledge)?.Firmware);
            Assert.Equal("8.00", FirmwareDb.Introduced(Convert_, databases, knowledge)?.Firmware);
            Assert.Equal("8.00", FirmwareDb.Introduced(CoredumpUserString, databases, knowledge)?.Firmware);
        }

        /// <summary>FW 4 target: the import is renamed in place (same library suffix, PLT entry untouched); a target that exports
        /// DirNameSearch2 leaves the module unchanged.</summary>
        [Theory]
        [InlineData("4", true)]
        [InlineData("5.02", false)]
        public void Cook_SwapsDirNameSearch2(string firmware, bool applied)
        {
            var (game, reference) = GuardedFixTests.Set3DLatencyGame(_root, firmware, exported: !applied, DirNameSearch2, alsoExported: DirNameSearch);
            // the synthetic module's second symbol shares the import's name string, which RenameImport refuses: give it its own
            string eboot = Path.Combine(game, "eboot.bin");
            byte[] module = File.ReadAllBytes(eboot);
            System.Text.Encoding.ASCII.GetBytes("unrelatedNi#A#A\0").CopyTo(module, 0x211);
            SyntheticBinary.W32(module, 0x2B0, 0x11);
            File.WriteAllBytes(eboot, module);
            var entry = Cook(game, reference, firmware, out string output);
            if (!applied)
            {
                Assert.Empty(entry.Fixed);
                Assert.False(File.Exists(Path.Combine(output, "eboot.bin")));
                return;
            }
            var fixedEntry = Assert.Single(entry.Fixed);
            Assert.Equal((DirNameSearch2, "swap -> " + DirNameSearch, false), (fixedEntry.Nid, fixedEntry.How, fixedEntry.Proven));
            var patched = new SceElf(Self.Unwrap(File.ReadAllBytes(Path.Combine(output, "eboot.bin"))));
            var import = Assert.Single(patched.Imports());
            Assert.Equal(DirNameSearch, import.Nid);
            Assert.Equal(DirNameSearch + "#A#A", patched.CStr(0x201));
            Assert.Equal("ff255a000000", System.Convert.ToHexStringLower(patched.Data.AsSpan(0x420, 6)));
            Assert.DoesNotContain(entry.Statuses, s => Imports.IsUnresolved(s.Status));
        }

        /// <summary>FW 4 target: the GOT slot is pre-filled with the stub (the loader binds the native function from 8.00).</summary>
        [Theory]
        [InlineData(Convert_, "ret:0x809f0008", "b808009f80c3")]
        [InlineData(CoredumpUserString, "ret0", "31c0c3")]
        public void Cook_PrefillsTheStub(string nid, string stub, string body)
        {
            var (game, reference) = GuardedFixTests.Set3DLatencyGame(_root, "4", exported: false, nid);
            var entry = Cook(game, reference, "4", out string output);
            var stubbed = Assert.Single(entry.StubbedNow);
            Assert.Equal((nid, stub, true), (stubbed.Nid, stubbed.Stub, stubbed.KnownFix));
            var patched = new SceElf(Self.Unwrap(File.ReadAllBytes(Path.Combine(output, "eboot.bin"))));
            ulong prefill = patched.ReadU64(Assert.Single(patched.Imports()).GotVaddr);
            Assert.Equal(body, System.Convert.ToHexStringLower(patched.Data.AsSpan((int)patched.Off(prefill), body.Length / 2)));
        }

        ModuleResult Cook(string game, string reference, string firmware, out string output)
        {
            output = Path.Combine(_root, "output");
            var result = Pipeline.Backport(game, output, new BackportOptions { Firmware = firmware, ReferenceDir = reference },
                                           new Knowledge(Paths.DataDir), cancel: TestContext.Current.CancellationToken);
            var entry = Assert.Single(result.Modules);
            Assert.True(entry.Error.Length == 0, entry.Error);
            return entry;
        }
    }
}
