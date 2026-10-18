namespace Ps5Backport.Tests
{
    using System.Text;
    using System.Text.Json.Nodes;
    using Ps5Backport.Core;

    /// <summary>The code graph tool's analysis on GT7: unwind-table functions, basic blocks, calls and the backport diff (FINDINGS 6o).</summary>
    public sealed class CodeGraphTests
    {
        const ulong SaveData = 0x833f60;   // calls sceSaveDataMount3; the backport patches one of its argument fields
        const string Mount3 = "ZP4e7rlzOUk";

        static NameDb Names => new(Repo.Need("data", "aerolib.csv"));
        static CodeImage Original() => CodeImage.Load(Samples.Work("NOT_BACKPORT_elf", "eboot.elf"), Names);

        [Trait("Lane", "Private")]
        [Fact]
        public void UnwindFunctions_ComeFromTheEhFrameHeader()
        {
            var image = Original();
            Assert.Equal(156935, image.Functions.Count);
            Assert.All(image.Functions.Zip(image.Functions.Skip(1)), p => Assert.True(p.First.End <= p.Second.Start));
            Assert.Equal(new FunctionRange(SaveData, 0x1c3), image.FunctionAt(SaveData + 0xda));
            Assert.Null(image.FunctionAt(0x3d2));   // int3 padding between functions
        }

        [Trait("Lane", "Private")]
        [Fact]
        public void DiscoverFunctions_RecoversGt7sUnwindFunctions_WithoutTheUnwindTable()
        {
            // GT7's unwind table is complete, so discovery does not run; run it as if the table were missing and compare
            var image = Original();
            Assert.Equal(0, image.DiscoveredCount);
            var found = CodeImage.DiscoverFunctions(image.Elf, [], image.PltEntries);
            var truth = image.Unwind.Select(f => f.Start).ToHashSet();
            int right = found.Count(f => truth.Contains(f.Start));
            Assert.True(right >= 0.97 * found.Count, $"precision {right}/{found.Count}");   // measured 98.1 %
            Assert.True(right >= 0.98 * truth.Count, $"recall {right}/{truth.Count}");      // measured 98.5 %
            Assert.DoesNotContain(found, f => image.ImportAt(f.Start) is not null);         // the PLT is never a function
        }

        [Trait("Lane", "Private")]
        [Fact]
        public void Graph_SplitsBlocks_NamesTargets_AndLabelsEdges()
        {
            var image = Original();
            var g = image.Graph(image.FunctionAt(SaveData)!.Value);
            Assert.Equal((15, 117), (g.Blocks.Count, g.Instructions));
            Assert.Equal("0->9:True 0->1:False 1->3:True 1->2:False 2->3:Jump 3->11:True 3->4:False 4->7:True 4->5:False 5->7:True 5->6:False " +
                         "6->7:Jump 7->14:True 7->8:False 9->12:True 9->10:False 10->1:Jump 11->7:Jump 12->14:True 12->13:False",
                         string.Join(" ", g.Edges.Select(e => $"{e.From}->{e.To}:{e.Kind}")));
            var lines = g.Blocks.SelectMany(b => b.Lines).ToDictionary(l => l.Address);
            Assert.Equal("js loc_8340cc", lines[0x833f93].Text);
            Assert.Equal(("call sceSaveDataMount3", LineKind.Call), (lines[0x834050].Text, lines[0x834050].Kind));
            Assert.Equal("call __stack_chk_fail", lines[0x83411c].Text);
            Assert.Equal("jmp sub_833a00", lines[0x834117].Text);   // tail call: no edge, the target is named
            Assert.Equal(0x833a00UL, lines[0x834117].Target);
            Assert.Equal("mov qword ptr [rdi+18h], 0FFFFFFFFFFFFFFFFh", lines[0x83403a].Text);
        }

        [Trait("Lane", "Private")]
        [Fact]
        public void Graph_DecodesClangSwitchTables()
        {
            var image = Original();
            GraphLine Jump(ulong at) => image.Graph(image.FunctionAt(at)!.Value).Blocks.SelectMany(b => b.Lines).Single(l => l.Address == at);

            // cmp al, 4Bh; ja → 76 entries, all landing on two blocks
            var g = image.Graph(image.FunctionAt(0x82260)!.Value);
            Assert.Equal(76, Assert.Single(g.Blocks.SelectMany(b => b.Lines), l => l.Cases is not null).Cases!.Count);
            Assert.Equal([0x82279UL, 0x82280], g.Edges.Where(e => e.Kind == EdgeKind.Case).Select(e => g.Blocks[e.To].Start).Order().ToArray());

            Assert.Equal(4, Jump(0x160c).Cases!.Count);           // cmp r15d, 3; ja; lea rax, [table]; movsxd rcx, [rax+r15*4]; add rcx, rax
            Assert.NotNull(Jump(0x709baa).Cases);                 // add rax, rcx with the table base as the destination
            Assert.Null(Jump(0x2be).Cases);                       // jmp [rax+8]: a virtual call, not a switch
        }

        [Trait("Lane", "Private")]
        [Fact]
        public void Graph_ReadsConstantArguments_StoppingAtTheBlockAndThePreviousCall()
        {
            var image = Original();
            GraphLine At(CodeImage i, ulong a) => i.Graph(i.FunctionAt(a)!.Value).Blocks.SelectMany(b => b.Lines).Single(l => l.Address == a);

            Assert.Equal([new CallArgument(0, 1)], At(image, 0x1ff559).Arguments!);   // mov edi, 1; call exit
            // mov edi, eax; call exit: the old byte scan read 213 from an earlier instruction's bytes; the decoded block, now also
            // used by the requirements probe, has no constant
            Assert.Null(At(image, 0x17e989a).Arguments);
            Assert.Contains((0x17e989aUL, (uint?)null), Imports.ArgumentConstants(image.Elf, image.Imports.First(i => i.Nid == "uMei1W9uyNo"), 1, TestContext.Current.CancellationToken));

            // Borderlands 4 calls the pad import from code without an FDE (most of its code has none): the probe still finds the site,
            // and the code graph lists the function by discovery
            var bl4 = CodeImage.Load(Samples.Backports("PPSA01494", "default", "eboot.bin"), Names);
            Assert.DoesNotContain(bl4.Unwind, f => f.Contains(0x251864e));
            Assert.Equal(0x2518630UL, bl4.FunctionAt(0x251864e)!.Value.Start);
            Assert.True(bl4.IsDiscovered(0x2518630));
            Assert.True(bl4.DiscoveredCount > 10 * bl4.Unwind.Count);
            var pad = bl4.Imports.First(i => i.Nid == "W2G-yoyMF5U" && i.Plt);
            Assert.Equal([(0x251864eUL, (uint?)null)], Imports.ArgumentConstants(bl4.Elf, pad, 1, TestContext.Current.CancellationToken));

            // UFC 6 passes vibration mode 4 (FINDINGS 6m); AC Black Flag's tail jump passes a computed mode
            var ufc = CodeImage.Load(Samples.Backports("PPSA23566", "decrypted", "eboot.bin"), Names);
            Assert.All(new ulong[] { 0x303dceb, 0x303dd27, 0x303dd63, 0x303dd9f }, a => Assert.Contains(new CallArgument(1, 4), At(ufc, a).Arguments!));
            var ac = CodeImage.Load(Samples.Backports("PPSA28183", "default", "eboot.bin"), Names);
            var tail = At(ac, 0x943f487);
            Assert.StartsWith("jmp scePadSetVibrationMode", tail.Text);
            Assert.DoesNotContain(tail.Arguments ?? [], a => a.Index == 1);
        }

        [Trait("Lane", "Private")]
        [Fact]
        public void CrashMap_MapsLoggedAddressesOntoTheEboot()
        {
            var image = Original();   // GT7: code segment at 0, 0x3f0e72c bytes; loaded at 0x400000 like PPSA38510 (FINDINGS §2)
            static JsonObject Log(string text) => CrashLog.Analyze(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n")));
            string body = "# A user thread receives a fatal signal\n# signal: 11 (SIGSEGV)\n# fault address: 0000000000000018\n" +
                          "# rip: 0000000000c3403a  eflags: 00010282\n# backtrace:\n#  0000000000c34055\n#  0000000000000000\n# dynamic libraries:\n";

            var ev = Assert.Single(CrashMap.Map(Log(body + "# /app0/eboot.bin\n#  xotext: 400000:4310000 nsegs: 4\n"), image));
            Assert.Contains("matches this file's code size", ev.Evidence);
            Assert.Equal([("fault address", (ulong?)null), ("RIP", 0x83403aUL), ("backtrace", 0x834055UL), ("backtrace", null)],
                         ev.Frames.Select(f => (f.Kind, f.Vaddr)));
            Assert.Contains("null pointer", ev.Frames[^1].Note);
            Assert.True(ev.Frames[2].IsReturnAddress);

            // a smaller logged text range is another build
            Assert.Contains("differs", Assert.Single(CrashMap.Map(Log(body + "# /app0/eboot.bin\n#  xotext: 400000:4000000 nsegs: 4\n"), image)).Evidence);

            // ShadowMount one-line summary: no module map, the usual eboot base is assumed and said so
            var summary = Assert.Single(CrashMap.Map(Log("[MDBG] game error: PPSA01317 pid=77: SIGSEGV RIP: 0xc3403a; backtrace[0]: 0x1102d7f\n"), image));
            Assert.Contains("assumed", summary.Evidence);
            Assert.Equal([0x83403aUL, 0xd02d7f], summary.Frames.Select(f => f.Vaddr!.Value));
        }

        [Trait("Lane", "Private")]
        [Fact]
        public void Detectors_ListRulePatternMatches_InCodeAndData()
        {
            // Control (original): the three default Wwise 7.1.4 -> 7.1 patterns in code, gpu_scratch's s_trap in data
            var control = CodeImage.Load(Samples.Backports("PPSA34547", "default", "eboot.bin"), Names);
            var matches = Detectors.Find(control.Elf, Samples.K, TestContext.Current.CancellationToken);
            Assert.Equal([0x3ca31ecUL, 0x3ca34b3, 0x3d8a3a7], matches.Where(m => m.Code).Select(m => m.Vaddr).Order().ToArray());
            Assert.All(matches.Where(m => m.Code), m => Assert.StartsWith("Wwise 7.1.4 -> 7.1", m.Name));
            Assert.All(matches.Where(m => m.Code), m => Assert.NotNull(control.FunctionAt(m.Vaddr)));
            Assert.Equal(164, matches.Count(m => !m.Code && m.Source == "option gpu_scratch"));
            // the counts the probe and the cook report are the locator's
            foreach (var p in Samples.K.Patterns)
                Assert.Equal(FixApplier.CountPattern(control.Elf, p, TestContext.Current.CancellationToken), matches.Count(m => m.Name == p.Name));
        }

        [Trait("Lane", "Private")]
        [Fact]
        public void Caves_AreTheEnginesCaves_AndTheExtensionRoomIsWhatExtendTextAdds()
        {
            var image = CodeImage.Load(Samples.Backports("PPSA01317", "default", "eboot.bin"), Names);
            var caves = Patching.Caves(image.Elf);
            Assert.Equal((38736, 257911L, 13L), (caves.Count, caves.Sum(c => c.Usable), caves.Max(c => c.Usable)));   // 16-byte alignment fill
            Assert.All(caves.Take(500), c =>
            {
                long off = image.Elf.Off(c.Vaddr);
                Assert.Equal((Patching.Ret, Patching.Int3, Patching.Int3), (image.Elf.Data[off - 2], image.Elf.Data[off - 1], image.Elf.Data[off + c.Usable]));
            });
            // the first cave that fits is the one FindCave picks
            Assert.Equal(image.Elf.Off(caves.First(c => c.Usable >= 12).Vaddr), Patching.FindCave(image.Elf, 12));
            long room = Patching.ExtensionRoom(image.Elf);
            Assert.Equal(6356, room);
            Assert.Equal(room, Patching.ExtendText(image.Elf));
            Assert.Equal(0, Patching.ExtensionRoom(image.Elf));   // used up
        }

        [Trait("Lane", "Private")]
        [Fact]
        public void ScanCalls_FindsCallersOfFunctionsAndImports()
        {
            var image = Original();
            var index = image.ScanCalls(TestContext.Current.CancellationToken);
            Assert.Equal([0x834130UL, 0x834370, 0x2ed4010], index.Callers[SaveData]);
            Assert.Contains(SaveData, index.ImportCallers[Mount3]);
            // strings whose address code takes with lea, and who uses them (FINDINGS 6o)
            Assert.Equal(38934, index.StringUsers.Count);
            Assert.Equal(13, index.StringUsers.Keys.Count(k => image.StringAt(k)!.Value.Wide));
            Assert.Equal([0x27450UL], index.StringUsers[0x4895c2f].Where(f => f == 0x27450));
            Assert.Equal("(no file)", image.StringAt(0x4895c2f)?.Text);

            // the call graph around it: callers above (level -1, then -2), callees and imports below
            var hood = index.Neighbourhood(image, SaveData);
            var center = hood.Nodes[0];
            Assert.Equal((SaveData, 0), (center.Function, center.Level));
            var callers = hood.Edges.Where(e => e.To == 0).Select(e => hood.Nodes[e.From]).ToList();
            Assert.Equal([0x834130UL, 0x834370, 0x2ed4010], callers.Select(n => n.Function!.Value).Order().ToArray());
            Assert.All(callers, n => Assert.Equal(-1, n.Level));
            var below = hood.Edges.Where(e => e.From == 0).Select(e => hood.Nodes[e.To]).ToList();
            Assert.Contains(below, n => n.Import?.Nid == Mount3);
            Assert.Equal(index.Callees[SaveData].Count(c => c != SaveData), below.Count(n => n.Function is not null));
            Assert.Contains(hood.Nodes, n => n.Level == -2);   // the callers' callers
            Assert.All(hood.Nodes.GroupBy(n => n.Key), g => Assert.Single(g));
            // a hub: at most FirstLevel callers, the rest in one "more" node
            ulong hub = index.Callers.MaxBy(p => p.Value.Count).Key;
            var hubHood = index.Neighbourhood(image, hub, depth: 1);
            var more = Assert.Single(hubHood.Nodes, n => n.More > 0 && n.Level < 0);
            Assert.Equal(index.Callers[hub].Count(c => c != hub) - CallIndex.FirstLevel, more.More);
        }

        [Trait("Lane", "Private")]
        [Fact]
        public void Graph_TagsStringsPointersAndConstants()
        {
            var image = Original();
            DataReference? At(ulong function, ulong line) =>
                image.Graph(image.FunctionAt(function)!.Value).Blocks.SelectMany(b => b.Lines).Single(l => l.Address == line).Data;
            Assert.Equal(new DataReference(0x4895c2f, DataKind.String, "\"(no file)\""), At(0x27450, 0x274ac));
            Assert.Equal(new DataReference(0x551f168, DataKind.Pointer, "[0] → sub_15bd6a0", 0x15bd6a0), At(0x27450, 0x27496));   // a relocated table
            Assert.Equal(DataKind.Pointer, At(0x113580, 0x1137ca)?.Kind);       // lea of a function: &sub_1a387c0
            Assert.Equal(0x1a387c0UL, At(0x113580, 0x1137ca)?.Target);
            Assert.Equal("= 0.5", At(0x113580, 0x1140f4)?.Text);                 // vmovss from read-only data
            Assert.Equal("initially 1920", At(0x113580, 0x115332)?.Text);        // a writable global's initial value
            Assert.Null(At(0x113580, 0x1135d6));                                  // a writable zero is not shown
            Assert.Equal("L\"FSDeviceWheelCSW\"", CodeImage.Quote(("FSDeviceWheelCSW", true)));
            Assert.Equal("\"a\\nb\"", CodeImage.Quote(("a\nb", false)));
        }

        [Trait("Lane", "Private")]
        [Fact]
        public void Versions_MatchUfc6Builds()
        {
            // UFC 6 v1.004 (decrypted) and v1.005 (dump): two builds of one game, 471k functions each (FINDINGS 6o)
            var older = CodeImage.Load(Samples.Backports("PPSA23566", "decrypted", "eboot.bin"), Names);
            var newer = CodeImage.Load(Samples.Backports("PPSA23566", "dump", "eboot.bin"), Names);
            Assert.False(BackportDiff.SameBuild(older, newer));
            var diff = VersionMatch.Compare(older, newer, TestContext.Current.CancellationToken);
            Assert.Equal((471078, 470380, 698, 295, 466), (diff.Matches.Count, diff.Identical, diff.Changed, diff.Removed.Count, diff.Added.Count));
            Assert.Equal(older.Functions.Count, diff.Matches.Count + diff.Removed.Count);
            Assert.Equal(newer.Functions.Count, diff.Matches.Count + diff.Added.Count);
            var m = diff.OfNew(0x1843740)!;
            Assert.Equal((0x183e7a0UL, MatchHow.SameReferences, false), (m.Old.Start, m.How, m.Identical));
            Assert.InRange(m.Similarity, 0.85, 0.95);
            Assert.Same(m, diff.OfOld(0x183e7a0));
            var (removed, added) = VersionMatch.LineDiff(older.Graph(m.Old), newer.Graph(m.New));
            Assert.Equal((6, 16), (removed.Count, added.Count));
            // most identical functions moved: a match is by code, not by address
            Assert.True(diff.Matches.Count(x => x.Identical && x.Old.Start != x.New.Start) > 400_000);
        }

        [Trait("Lane", "Private")]
        [Fact]
        public void Versions_SameBuild_PairsEveryFunctionWithItself()
        {
            var original = Original();
            var backport = CodeImage.Load(Samples.Backports("PPSA01317", "4xx+", "USA", "eboot.bin"), Names);
            Assert.True(BackportDiff.SameBuild(original, backport));
            var diff = VersionMatch.Compare(original, backport, TestContext.Current.CancellationToken);
            Assert.Equal(original.Functions.Count, diff.Matches.Count);
            Assert.All(diff.Matches, x => Assert.Equal(x.Old.Start, x.New.Start));
            Assert.Equal([SaveData], diff.Matches.Where(x => !x.Identical).Select(x => x.New.Start));   // the patched one
        }

        [Fact]
        public void LineDiff_IgnoresMovedAddresses()
        {
            GraphLine L(ulong at, string text) => new(at, 1, text, LineKind.Code, null);
            FunctionGraph G(params GraphLine[] lines) => new([new FunctionRange(lines[0].Address, (ulong)lines.Length)], [new GraphBlock(lines[0].Address, lines)], []);
            var older = G(L(0x100, "push rbp"), L(0x101, "call sub_1a2b30"), L(0x102, "lea rsi, [4895C2Fh]"), L(0x103, "mov eax, 1"), L(0x104, "ret"));
            var newer = G(L(0x200, "push rbp"), L(0x201, "call sub_1a9f70"), L(0x202, "lea rsi, [489A100h]"), L(0x203, "mov eax, 2"), L(0x204, "nop"), L(0x205, "ret"));
            var (removed, added) = VersionMatch.LineDiff(older, newer);
            Assert.Equal([0x103UL], removed);
            Assert.Equal([0x203UL, 0x204], added.Order().ToArray());
        }

        [Trait("Lane", "Private")]
        [Fact]
        public void Follow_FindsTheShippedModuleAndItsExport()
        {
            // GT7's published backport: sceAgcInit_0090 comes from fakelib/libSceAgc.sprx, where it starts at 0x9990
            var backport = CodeImage.Load(Samples.Backports("PPSA01317", "4xx+", "USA", "eboot.bin"), Names);
            const string agcInit = "23LRUSvYu1M";
            string? file = backport.ProviderFile(agcInit);
            Assert.Equal(Samples.Backports("PPSA01317", "4xx+", "USA", "fakelib", "libSceAgc.sprx"), file);
            Assert.Equal("fakelib/libSceAgc.sprx", backport.ProviderExports()[agcInit]);
            Assert.Null(backport.ProviderFile(Mount3));   // a system library function: nothing shipped exports it
            var agc = CodeImage.Load(file!, Names);
            Assert.Equal(0x9990UL, agc.ExportAddress(agcInit));
            Assert.True(agc.IsFunctionStart(0x9990));
            Assert.Null(agc.ExportAddress(Mount3));
            // inside a fakelib, the other fakelibs are its neighbours: AgcDriver calls can be followed on
            Assert.Contains(agc.Imports, i => i.Library == "libSceAgcDriver" && agc.ProviderFile(i.Nid)?.EndsWith("libSceAgcDriver.sprx") == true);

            // the 7.xx set ships libSceSaveData.native.sprx for the library imports call libSceSaveData_native
            var withSaveData = new CodeImage(backport.Path, backport.Elf, Names, Repo.Need("fakelibs", "7"));
            Assert.Contains("libSceSaveData_native", withSaveData.FakelibLibraries);
        }

        [Trait("Lane", "Private")]
        [Fact]
        public void Compare_FindsThePatch_TheStubs_AndTheRedirectedImports()
        {
            // the published backport is a fake-signed SELF next to its fakelib/ folder
            var original = Original();
            var backport = CodeImage.Load(Samples.Backports("PPSA01317", "4xx+", "USA", "eboot.bin"), Names);
            Assert.Equal(["libSceAgc", "libSceAgcDriver", "libSceAmpr", "libScePlayGo", "libScePsml"], backport.FakelibLibraries.Order());

            var diff = BackportDiff.Compare(original, backport);
            Assert.Equal(0, diff.TextGrowth);
            Assert.Equal([(0x3d2UL, 3, ChangeKind.Stub), (0x573UL, 12, ChangeKind.Stub), (0x83403eUL, 4, ChangeKind.Patched)],
                         diff.Changes.Select(c => (c.Address, c.Length, c.Kind)));
            Assert.Equal(SaveData, diff.Changes[2].Function!.Value.Start);
            Assert.Equal(["sceKernelGetAvailableCpumask -> stub_3d2", "sceKernelGetOperationMode -> stub_573",
                          "sceVideoOutAddVrrActiveStatusEvent -> stub_3d2", "sceVideoOutAddVrrStatusFlagsPrivilege -> stub_3d2",
                          "sceVideoOutVrrPegToFixedRate -> stub_3d2", "sceVideoOutVrrUnpegFromFixedRate -> stub_3d2"],
                         diff.Redirects.Select(r => $"{backport.ImportName(r.Import)} -> {backport.TargetName(r.Target!.Value)}").Order(StringComparer.Ordinal));

            Assert.All(diff.Redirects, r => Assert.False(r.ViaPlt));   // this backport rewrote GOT slots, not PLT entries
            Assert.Equal(0x3d2UL, diff.RedirectOf(original.Imports.First(i => i.Nid == "La9uyZv4Kvw"))!.Target);
            Assert.Equal(2, diff.StubChains.Count);
            Assert.All(diff.StubChains, c => Assert.Single(c));

            var stub = backport.Graph(new FunctionRange(0x573, 12));
            Assert.Equal(["mov dword ptr [rdi], 2", "and dword ptr [rsi], 0", "xor eax, eax", "ret"], stub.Blocks.SelectMany(b => b.Lines).Select(l => l.Text));
            Assert.Equal("mov qword ptr [rdi+18h], 0", backport.Graph(diff.Changes[2].Function!.Value).Blocks.SelectMany(b => b.Lines).Single(l => l.Address == 0x83403a).Text);
        }

        [Trait("Lane", "Private")]
        [Fact]
        public void Compare_OwnCook_JoinsAdapterChains_AndKeepsTheGotTailOnTheSystemFunction()
        {
            // Studio's own FW 4 cook: the sceSaveDataMount3 adapter is split across int3 caves and reached through its PLT entry
            string root = Samples.Temp("codegraph");
            try
            {
                string game = Samples.Backports("PPSA01317", "default");
                Assert.True(Samples.Backport(game, Path.Combine(root, "out"), "4", Repo.Need("firmware_db"), store: Repo.Need("fakelibs")).Published);
                var original = CodeImage.Load(Path.Combine(game, "eboot.bin"), Names);
                var backport = CodeImage.Load(Path.Combine(root, "out", "eboot.bin"), Names);
                var diff = BackportDiff.Compare(original, backport);
                Assert.DoesNotContain(diff.Changes, c => c.Kind == ChangeKind.Other);   // caves are not merged across the small functions between them

                var mount = diff.Redirects.Single(r => r.Import.Nid == Mount3);
                Assert.True(mount.ViaPlt);
                var chain = diff.ChainAt(mount.Target!.Value)!;
                Assert.Equal(mount.Target, chain[0].Address);
                Assert.True(chain.Count > 1);
                var graph = backport.Graph([.. chain.Select(c => new FunctionRange(c.Address, (ulong)c.Length))]);
                Assert.Equal(chain.Count - 1, graph.Edges.Count);   // one jump between consecutive pieces
                var tail = graph.Blocks[^1].Lines[^1];
                Assert.Equal("jmp qword ptr [sceSaveDataMount3@got]", tail.Text);
                Assert.Equal(mount.Import.GotVaddr, tail.Reference);
                Assert.Equal($"stub_{chain[0].Address:x}", backport.FunctionName(chain[0].Address));

                var stubs = diff.StubChains.Where(c => c.Count == 1)
                    .ToDictionary(c => c[0].Address, c => Samples.K.StubName(backport.Elf.Data.AsSpan((int)backport.Elf.Off(c[0].Address), c[0].Length)));
                Assert.Equal("opmode_normal", stubs[diff.Redirects.Single(r => r.Import.Nid == "NH6xARDOVv8").Target!.Value]);
                Assert.Equal("ret:0xff", stubs[diff.Redirects.Single(r => r.Import.Nid == "La9uyZv4Kvw").Target!.Value]);

                // the import check agrees with the cook's own report: one import left unpatched (missing on FW 4.00)
                var databases = FirmwareDb.LoadAll(Repo.Need("firmware_db"));
                var before = original.CheckImports(databases, "4", Samples.K);
                var after = backport.CheckImports(databases, "4", Samples.K);
                Assert.Equal(32, before.Unresolved.Count());
                Assert.Equal("11.00", before.Introduced["NH6xARDOVv8"]);   // sceKernelGetOperationMode
                var left = Assert.Single(after.Unresolved);
                Assert.Equal(("QxBJF-G2v5k", Imports.Missing), (left.Nid, left.Status));   // sceHmd2ReprojectionSetParamWithBuffer
                Assert.Equal("5.02", after.Introduced[left.Nid]);
                Assert.Contains("missing  QxBJF-G2v5k", File.ReadAllText(Path.Combine(root, "out", "backport_report.txt")));
                Assert.Equal("fakelib/libSceAgc.sprx", after.Provided.Values.First(m => m.StartsWith("fakelib/libSceAgc", StringComparison.Ordinal) && !m.Contains("Driver")));

                // the cook preview (dry run kept in memory) is the same eboot, with the store's fakelib set, and writes nothing
                var (preview, run) = CookPreview.Run(Path.Combine(game, "eboot.bin"), new BackportOptions
                {
                    Firmware = "4", ReferenceDir = Repo.Need("firmware_db"), FakelibStore = Repo.Need("fakelibs"),
                }, Samples.K, Names, TestContext.Current.CancellationToken);
                Assert.True(preview.Elf.Data.AsSpan().SequenceEqual(backport.Elf.Data));
                Assert.Equal(backport.FakelibLibraries.Order(), preview.FakelibLibraries.Order());
                Assert.Equal("QxBJF-G2v5k", Assert.Single(preview.CheckImports(databases, "4", Samples.K).Unresolved).Nid);
                Assert.True(run.Modules.Where(m => m.Path != "eboot.bin").All(m => m.PatchedData is null));
                Assert.False(Directory.Exists(run.Output));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void StubName_KnowsStubBodiesAndReturnValues()
        {
            var k = Samples.K;
            Assert.Equal("ret0", k.StubName(k.Stubs["ret0"].Bytes));
            Assert.Equal("ret:0x8a810012", k.StubName(Convert.FromHexString("B81200818AC3")));
            Assert.Null(k.StubName(Convert.FromHexString("90C3")));
        }

        [Trait("Lane", "Private")]
        [Fact]
        public void Compare_RefusesADifferentBuild()
        {
            var control = CodeImage.Load(Samples.Work("CONTROL_elf", "eboot.elf"), Names);
            var e = Assert.Throws<InvalidDataException>(() => BackportDiff.Compare(Original(), control));
            Assert.Contains("different builds", e.Message);
        }
    }
}
