namespace Ps5Backport.Tests
{
    using System.Text;
    using Ps5Backport.Core;

    /// <summary>
    /// FINDINGS 6v: what each call site does with an unresolved import's return value (unused, error-check only, used), after
    /// Decker97's stub gate. A synthetic module calls the import at <see cref="Site"/> through its PLT entry 0x420 (GOT 0x480).
    /// </summary>
    public sealed class CallerResultTests
    {
        const ulong Site = 0x510;
        const string Plain = "AbCdEfGhIjK";   // no rule, no fact

        /// <summary>16 int3 (the whole file is decoded from 0; any instruction running into them ends there) then 'call PLT'
        /// (or 'jmp PLT' with <paramref name="tail"/>) and <paramref name="after"/>.</summary>
        static byte[] Module(string after, bool tail = false, string nid = SyntheticBinary.Nid)
        {
            var payload = new List<byte>(Enumerable.Repeat(Patching.Int3, 16)) { tail ? (byte)0xE9 : (byte)0xE8 };
            payload.AddRange(BitConverter.GetBytes(0x420 - (int)(Site + 5)));
            payload.AddRange(Convert.FromHexString(after));
            byte[] module = SyntheticBinary.Elf(payload.ToArray(), imports: true);
            Encoding.ASCII.GetBytes(nid + "#A#A\0").CopyTo(module, 0x201);
            SyntheticBinary.W64(module, 0x130, SceElf.DtJmpRel);
            SyntheticBinary.W64(module, 0x140, SceElf.DtPltRelSz);
            SyntheticBinary.W64(module, 0x480, 0x426);
            Convert.FromHexString("FF255A0000006800000000").CopyTo(module, 0x420);
            return module;
        }

        static CallerResult Classify(string after, bool tail = false)
        {
            var elf = new SceElf(Module(after, tail));
            var status = new ImportStatus(SyntheticBinary.Nid, "synthetic", 0x480, true, Imports.Missing);
            return Assert.Single(Imports.CallerResults(elf, [status], TestContext.Current.CancellationToken)[SyntheticBinary.Nid]);
        }

        [Theory]
        [InlineData("b801000000c3", "result never read")]                       // mov eax, 1; ret
        [InlineData("85c97406b805000000c331c0c3", "result never read")]         // test ecx, ecx; je +6; mov eax, 5; ret; xor eax, eax; ret
        [InlineData("e800000000c3", "result never read")]                       // call (the callee's result replaces it); ret
        public void Unused_WhenEveryPathOverwritesEax(string after, string why)
            => Assert.Equal(new CallerResult(Site, ResultUse.Unused, why), Classify(after));

        [Theory]
        [InlineData("85c07506b801000000c3c3", "0x515: test eax, eax; jne, success (eax = 0) continues at 0x519")]   // jne err; mov eax, 1; ret; err: ret
        [InlineData("85c07401c3b801000000c3", "0x515: test eax, eax; je, success (eax = 0) continues at 0x51a")]    // je ok; ret; ok: mov eax, 1; ret
        public void ErrorCheck_WhenTheSuccessSideIgnoresEax(string after, string why)
            => Assert.Equal(new CallerResult(Site, ResultUse.ErrorCheck, why), Classify(after));

        [Theory]
        [InlineData("89c3c3", "0x515 reads the result: mov ebx,eax")]
        [InlineData("b001c3", "0x515 partial write keeps the result live: mov al,1")]
        [InlineData("c3", "0x515 returns with the result in eax")]
        [InlineData("85c07801c3c3", "success path 0x519: 0x519 returns with the result in eax")]   // js err; ret (eax = 0 returned)
        [InlineData("ffe1", "0x515 indirect branch")]                                              // jmp rcx
        public void Used_OtherwiseWithTheFirstReason(string after, string why)
            => Assert.Equal(new CallerResult(Site, ResultUse.Used, why), Classify(after));

        [Fact]
        public void TailCall_IsUsed()
            => Assert.Equal(new CallerResult(Site, ResultUse.Used, "tail call: the caller gets the result"), Classify("", tail: true));

        [Fact]
        public void Summary_ListsUsedFirstWithItsReason()
        {
            Assert.Equal("", Imports.ResultSummary([]));
            Assert.Equal("used 1 (0x1 reads the result: x), error-check 1, unused 2", Imports.ResultSummary(
            [
                new(1, ResultUse.Unused, ""), new(2, ResultUse.Used, "0x1 reads the result: x"), new(3, ResultUse.ErrorCheck, ""),
                new(4, ResultUse.Unused, ""),
            ]));
        }

        /// <summary>The backport report shows the summary after refs= for an unresolved import (text and JSON).</summary>
        [Fact]
        public void Report_ShowsTheResultUse()
        {
            string root = Samples.Temp("caller-result");
            try
            {
                string game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
                File.WriteAllBytes(Path.Combine(game, "eboot.bin"), Module("b801000000c3", nid: Plain));
                string reference = Directory.CreateDirectory(Path.Combine(root, "reference")).FullName;
                FirmwareDb.Save(new FirmwareExports("4", "test", false, new() { ["synthetic"] = new([], StringComparer.Ordinal) }),
                                Path.Combine(reference, Firmware.Name("4") + ".json"));
                var knowledge = new Knowledge(Paths.DataDir);
                var result = Pipeline.Backport(game, Path.Combine(root, "output"), new BackportOptions { Firmware = "4", ReferenceDir = reference, DryRun = true },
                                               knowledge, cancel: TestContext.Current.CancellationToken);
                var unresolved = Assert.Single(Assert.Single(result.Modules).Unknown);
                Assert.Equal((Plain, 1, "unused 1"), (unresolved.Nid, unresolved.CodeRefs, unresolved.Result));
                Assert.Contains($"refs=1   result: unused 1", Pipeline.FormatReport(result, knowledge));
                Assert.Equal("unused 1", Pipeline.ToJson(result)["modules"]![0]!["unknown"]![0]!["result"]!.GetValue<string>());
            }
            finally { Directory.Delete(root, true); }
        }
    }

    /// <summary>Call sites checked by hand in FINDINGS 6k/6v: the classifier agrees, and is conservative where a wrapper returns
    /// with the result still in eax (CONTROL's second Cancel call; its only caller 0x374dc48 ignores it).</summary>
    [Trait("Lane", "Private")]
    public sealed class CallerResultSampleTests
    {
        [Theory]
        [InlineData("PPSA34547", "e6y3WMBYbiM", "0x374da7d Unused result never read|0x374fdee Used 0x374fe04 returns with the result in eax")]
        [InlineData("PPSA03671", "e6y3WMBYbiM", "0x3f91159 Unused result never read")]
        [InlineData("PPSA03671", "2mfSRGdshtk", "0x3f90b2b Used 0x3f90b30 reads the result: cmp eax,809F0008h")]
        [InlineData("PPSA03671", "8EA5OMIL1lQ", "0x3f90d58 ErrorCheck 0x3f90d5d: test eax, eax; js, success (eax = 0) continues at 0x3f90d61")]
        [InlineData("PPSA29343", "32KQRUK13kI", "0x168a6 Unused result never read")]
        [InlineData("PPSA34015", "32KQRUK13kI", "0x4f86 Unused result never read")]
        public void KnownSites(string game, string nid, string expected)
        {
            var elf = new SceElf(Self.Unwrap(File.ReadAllBytes(Samples.Backports(game, "default", "eboot.bin"))));
            var status = Imports.Classify(elf, new HashSet<string>()).Single(s => s.Nid == nid);
            var sites = Imports.CallerResults(elf, [status], TestContext.Current.CancellationToken)[nid];
            Assert.Equal(expected, string.Join("|", sites.Select(s => $"0x{s.Site:x} {s.Use} {s.Why}")));
        }
    }
}
