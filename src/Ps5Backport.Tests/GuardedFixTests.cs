namespace Ps5Backport.Tests
{
    using System.Buffers.Binary;
    using System.Text;
    using System.Text.Json.Nodes;
    using Ps5Backport.Core;

    /// <summary>Kind "guarded" (FINDINGS 6t): GOT pre-filled with the stub for consoles without the export, PLT through a guard
    /// around the native call (on_error: a negative result becomes the stub's value).</summary>
    public sealed class GuardedFixTests : IDisposable
    {
        internal const string Set3DLatency = "TViD1EZXkNI";
        readonly string _root = Path.Combine(Path.GetTempPath(), "ps5backport-guarded-" + Guid.NewGuid().ToString("N"));
        string DataDir => Path.Combine(_root, "data");

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }

        [Fact]
        public void Set3DLatency_IsGuardedOnErrorBelow1001()
        {
            var fix = new Knowledge(Paths.DataDir).Fixes[Set3DLatency];
            Assert.Equal((Fix.KindGuarded, Fix.GuardOnError, "ret0", Fix.WhenAlways, "10.01", false),
                         (fix.Kind, fix.Guard, fix.Stub, fix.When, fix.Below, fix.Proven));
        }

        [Fact]
        public void OnErrorGuard_CallsNativeAlignedAndReplacesNegativeResults()
        {
            var insns = FixApplier.OnErrorGuard(0x80268001);
            Assert.Equal(["4883ec08", "call_got", "4883c408", "b901802680", "85c0", "0f48c1", "c3"], insns.Select(i => i.Hex));
        }

        [Theory]
        [InlineData("""{"kind":"guarded","stub":"ret0","sources":[]}""", "unknown guard ''")]
        [InlineData("""{"kind":"guarded","guard":"if_import","stub":"ret0","sources":[]}""", "unknown guard 'if_import'")]
        [InlineData("""{"kind":"guarded","guard":"on_error","stub":"byte0","sources":[]}""", "value-only stub")]
        [InlineData("""{"kind":"guarded","guard":"on_error","stub":"missing","sources":[]}""", "invalid stub 'missing'")]
        [InlineData("""{"kind":"guarded","guard":"on_error","stub":"ret0","insns":["90"],"sources":[]}""", "insns are not allowed")]
        [InlineData("""{"kind":"stub","guard":"on_error","stub":"ret0","sources":[]}""", "guard is only valid")]
        public void InvalidGuardedRule_FailsAtLoad(string rule, string message)
        {
            WriteData(rule);
            var error = Assert.Throws<InvalidDataException>(() => new Knowledge(DataDir));
            Assert.Contains("test-nid", error.Message);
            Assert.Contains(message, error.Message);
        }

        [Fact]
        public void ValidGuardedRule_LoadsWithItsFallbackValue()
        {
            WriteData("""{"kind":"guarded","guard":"on_error","stub":"ret:0x5","sources":[]}""");
            var knowledge = new Knowledge(DataDir);
            Assert.Equal(Fix.GuardOnError, knowledge.Fixes["test-nid"].Guard);
            Assert.Equal(5u, knowledge.StubReturnValue("ret:0x5"));
            Assert.Equal(0u, knowledge.StubReturnValue("ret0"));
            Assert.Null(knowledge.StubReturnValue("byte0"));
        }

        /// <summary>FW 4 target: GOT pre-filled with ret0 and the PLT entry jumps to the guard, which calls through that GOT slot.
        /// FW 10.01 target (exports it, accepts latency 2): the rule is not due and the module stays unchanged.</summary>
        [Theory]
        [InlineData("4", true)]
        [InlineData("10.01", false)]
        public void Cook_GuardsSet3DLatencyOnlyBelow1001(string firmware, bool applied)
        {
            var (game, reference) = Set3DLatencyGame(_root, firmware, exported: !applied);
            string output = Path.Combine(_root, "output");

            var result = Pipeline.Backport(game, output, new BackportOptions { Firmware = firmware, ReferenceDir = reference },
                                           new Knowledge(Paths.DataDir), cancel: TestContext.Current.CancellationToken);
            var entry = Assert.Single(result.Modules);
            Assert.True(entry.Error.Length == 0, entry.Error);
            if (!applied)
            {
                Assert.Empty(entry.Fixed);
                Assert.False(File.Exists(Path.Combine(output, "eboot.bin")));
                return;
            }
            var fixedEntry = Assert.Single(entry.Fixed);
            Assert.Equal(Set3DLatency, fixedEntry.Nid);
            Assert.StartsWith("guarded: native, error -> ret0 via cave 0x", fixedEntry.How);
            Assert.False(fixedEntry.Proven);

            var patched = new SceElf(Self.Unwrap(File.ReadAllBytes(Path.Combine(output, "eboot.bin"))));
            ulong got = Assert.Single(patched.Imports()).GotVaddr;
            ulong prefill = patched.ReadU64(got);
            Assert.Equal("31c0c3", Convert.ToHexStringLower(patched.Data.AsSpan((int)patched.Off(prefill), 3)));

            ulong plt = patched.Vaddr(0x420);
            byte[] jmp = patched.Data.AsSpan(0x420, 5).ToArray();
            Assert.Equal(0xE9, jmp[0]);
            ulong cave = plt + 5 + (ulong)(long)BinaryPrimitives.ReadInt32LittleEndian(jmp.AsSpan(1));
            byte[] chain = patched.Data.AsSpan((int)patched.Off(cave), 25).ToArray();
            Assert.Equal("4883ec08ff15", Convert.ToHexStringLower(chain.AsSpan(0, 6)));
            Assert.Equal(got, cave + 10 + (ulong)(long)BinaryPrimitives.ReadInt32LittleEndian(chain.AsSpan(6)));
            Assert.Equal("4883c408b90000000085c00f48c1c3", Convert.ToHexStringLower(chain.AsSpan(10)));
        }

        /// <summary>A synthetic game whose eboot imports only Set3DLatency (or <paramref name="nid"/>; one PLT entry, a 64-byte
        /// int3 cave), and a firmware_db list for <paramref name="firmware"/> that exports it or not (plus <paramref name="alsoExported"/>).</summary>
        internal static (string Game, string Reference) Set3DLatencyGame(string root, string firmware, bool exported,
                                                                          string nid = Set3DLatency, string? alsoExported = null)
        {
            string game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
            string reference = Directory.CreateDirectory(Path.Combine(root, "reference")).FullName;
            byte[] module = SyntheticBinary.Elf(imports: true);
            Encoding.ASCII.GetBytes(nid + "#A#A\0").CopyTo(module, 0x201);
            SyntheticBinary.W64(module, 0x130, SceElf.DtJmpRel);
            SyntheticBinary.W64(module, 0x140, SceElf.DtPltRelSz);
            SyntheticBinary.W64(module, 0x480, 0x426);
            Convert.FromHexString("FF255A0000006800000000").CopyTo(module, 0x420);
            module[0x380] = Patching.Ret;
            module.AsSpan(0x381, 64).Fill(Patching.Int3);
            File.WriteAllBytes(Path.Combine(game, "eboot.bin"), module);
            var exports = exported ? new HashSet<string> { nid } : [];
            if (alsoExported is not null)
                exports.Add(alsoExported);
            FirmwareDb.Save(new FirmwareExports(firmware, "test", false, new() { ["libSceAudioOut"] = exports }),
                            Path.Combine(reference, Firmware.Name(firmware) + ".json"));
            return (game, reference);
        }

        void WriteData(string rule)
        {
            Directory.CreateDirectory(DataDir);
            var fixes = JsonNode.Parse("""{"sources":{},"umtx_op":"umtx","fixes":{}}""")!.AsObject();
            fixes["fixes"]!["test-nid"] = JsonNode.Parse(rule);
            Write("fixes", fixes.ToJsonString());
            Write("stubs", """{"stubs":{"ret0":{"hex":"31c0c3"},"byte0":{"hex":"c6070031c0c3"}}}""");
            Write("candidates", """{"candidates":{}}""");
            Write("left_unpatched", """{"left_unpatched":{}}""");
            Write("introduced", """{"introduced":{}}""");
            Write("known_builds", """{"builds":[]}""");
            Write("sony_builds", """{"builds":{"J00000001":"4.00"}}""");
        }

        void Write(string name, string json) => File.WriteAllText(Path.Combine(DataDir, name + ".json"), json);
    }
}
