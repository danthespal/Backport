using System.Security.Cryptography;
using Iced.Intel;
using Ps5Backport.Core;

namespace DecoderEvaluation;

internal sealed record CaseResult(string Name, int Expected, int Heuristic, int Linear, int? Annotated, long Invalid);

internal static class Checks
{
    internal static List<CaseResult> Run()
    {
        var results = new List<CaseResult>();
        foreach (byte[] prefix in new byte[][] { [0xE8], [0xE9], [0xFF, 0x15], [0xFF, 0x25], [0x48, 0x8B, 0x05], [0x4C, 0x8D, 0x0D], [0xF3, 0xE8] })
            Check("reference-" + Convert.ToHexString(prefix), Fixtures.Reference(prefix), 1, 1, 1);
        const ulong high = 0x100001000;
        Check("address-size-EIP", Fixtures.Reference([0x67, 0xFF, 0x15], start: high), 1, 0, 1, start: high);
        Check("FS-base-unresolved", Fixtures.Reference([0x64, 0x48, 0x8B, 0x05]), 0, 1, 0);
        Check("call-looking-imm64", [0x48, 0xB8, .. Fixtures.Reference([0xE8], offset: 2), 0, 0, 0], 0, 1, 0);
        Check("embedded-data-after-return", [0xC3, .. Fixtures.Reference([0xE8], offset: 1)], 0, 1, 1, ranges: [new(0, 0, 1)]);
        Check("jump-over-data-alignment", [0xEB, 0x01, 0xB8, .. Fixtures.Reference([0xE8], offset: 3)], 1, 1, 0,
            ranges: [new(0, 0, 2), new(0, 3, 5)]);
        Check("invalid-lock-call", Fixtures.Reference([0xF0, 0xFF, 0x15]), 0, 1, 0);
        Check("truncated-call", [0xE8, 0, 0], 0, 0, 0);
        Check("register-indirect-unresolved", [0xFF, 0xD0], 0, 0, 0);
        Check("PLT-self", Fixtures.Reference([0xFF, 0x25]), 0, 0, 0, plt: Fixtures.Start + 6);
        Check("prefixed-PLT-self", Fixtures.Reference([0x48, 0xFF, 0x25]), 0, 1, 0, plt: Fixtures.Start + 6);
        Check("short-jump-to-PLT", [0xEB, 0x0E, .. Enumerable.Repeat((byte)0x90, 14), .. Fixtures.Reference([0xFF, 0x25], offset: 16)],
            1, 0, 1, plt: Fixtures.Start + 22);
        var random = new Random(7007);
        for (int n = 0; n < 512; n++)
        {
            int padding = random.Next(0, 65);
            bool immediate = n % 2 == 0;
            byte[] body = immediate
                ? [0x48, 0xB8, .. Fixtures.Reference([0xE8], offset: padding + 2), 0, 0, 0]
                : Fixtures.Reference([0x48, 0x8B, 0x05], offset: padding);
            Check($"generated-{n}", [.. Enumerable.Repeat((byte)0x90, padding), .. body], immediate ? 0 : 1, 1, immediate ? 0 : 1);
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { ReferenceProbe.Decode(Fixtures.Elf([0x90]), [Fixtures.Status()], cancel: cancelled.Token); throw new InvalidOperationException("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        return results;

        void Check(string name, byte[] code, int truth, int heuristic, int linear, ulong start = Fixtures.Start,
                   ulong? plt = null, IReadOnlyList<CodeRange>? ranges = null)
        {
            var elf = Fixtures.Elf(code, start, plt);
            var statuses = new[] { Fixtures.Status(plt.HasValue) };
            byte[] hash = SHA256.HashData(elf.Data);
            int actual = Imports.CodeReferences(elf, statuses)[Fixtures.Nid];
            var decoded = ReferenceProbe.Decode(elf, statuses);
            int? annotated = ranges is null ? null : ReferenceProbe.Decode(elf, statuses, knownCode: ranges).Counts[Fixtures.Nid];
            Require(actual == heuristic && decoded.Counts[Fixtures.Nid] == linear && (annotated is null || annotated == truth),
                $"{name}: expected heuristic/linear {heuristic}/{linear}, got {actual}/{decoded.Counts[Fixtures.Nid]}, annotated {annotated}");
            Require(hash.SequenceEqual(SHA256.HashData(elf.Data)), "Analysis mutated input");
            results.Add(new(name, truth, actual, decoded.Counts[Fixtures.Nid], annotated, decoded.Invalid));
        }
    }

    internal static object Emitters(string root)
    {
        var knowledge = new Knowledge(Path.Combine(root, "data"));
        int stubCount = 0, blocks = 0;
        foreach (var stub in knowledge.Stubs)
        {
            var instructions = Decode(stub.Value.Bytes, Fixtures.Start);
            Require(instructions[^1].Mnemonic == Mnemonic.Ret, "Stub must end at RET: " + stub.Key);
            stubCount++;
        }
        foreach (var (name, value) in new[] { ("ret:-1", uint.MaxValue), ("ret:0x12345678", 0x12345678u) })
        {
            var instructions = Decode(knowledge.StubBytes(name), Fixtures.Start);
            Require(instructions.Count == 2 && instructions[0].Op0Register == Register.EAX && instructions[0].Immediate32 == value
                && instructions[1].Mnemonic == Mnemonic.Ret, "Return-value encoding: " + name);
        }
        foreach (var fix in knowledge.Fixes.Values.Concat(knowledge.Options.Values.SelectMany(o => o.Fixes.Values)))
            foreach (var block in fix.Insns.Where(i => !i.CallGot)) { Decode(block.Bytes, Fixtures.Start); blocks++; }
        foreach (ulong target in new ulong[] { Fixtures.Got, 0x800, Fixtures.Start + 6 + int.MaxValue })
        {
            var instruction = Decode(Patching.JmpViaGot(Fixtures.Start, target), Fixtures.Start).Single();
            Require(instruction.Mnemonic == Mnemonic.Jmp && instruction.IPRelativeMemoryAddress == target, "GOT jump target");
        }
        var plt = Fixtures.Elf(Fixtures.Reference([0xFF, 0x25]), lazyPlt: Fixtures.Start + 6);
        var import = new Import(Fixtures.Nid, "synthetic", SceElf.SttFunc, Fixtures.Got, true, 1, false);
        Patching.RedirectPlt(plt, import, Fixtures.Start + 0x100);
        var redirected = Decode(plt.Data.AsSpan(0x200, 6).ToArray(), Fixtures.Start);
        Require(redirected[0].NearBranchTarget == Fixtures.Start + 0x100 && redirected[1].Mnemonic == Mnemonic.Nop
            && plt.ReadU64(Fixtures.Got) == Fixtures.Start + 6, "PLT redirect/GOT preservation");
        int chainInstructions = 0;
        foreach (bool tail in new[] { true, false })
        {
            byte[] caves = [.. Enumerable.Range(0, 5).SelectMany(_ => new byte[] { 0xC3 }.Concat(Enumerable.Repeat((byte)0xCC, 16)))];
            var elf = Fixtures.Elf(caves);
            var insns = Enumerable.Repeat(Insn.Parse("4989d0 ; mov r8,rdx"), 5).ToList();
            insns.Add(Insn.Parse("call_got"));
            if (!tail) insns.Add(Insn.Parse("c3"));
            ulong address = Patching.PlaceChain(elf, insns, Fixtures.Got, tail);
            var visited = new HashSet<ulong>();
            int moves = 0, calls = 0, jumps = 0;
            while (visited.Add(address) && visited.Count < 100)
            {
                var decoder = Decoder.Create(64, new ByteArrayCodeReader(elf.Data, checked((int)elf.Off(address)), 15));
                decoder.IP = address; decoder.Decode(out var i);
                Require(i.Code != Code.INVALID, "Invalid chain instruction"); chainInstructions++;
                if (i.Mnemonic == Mnemonic.Mov) { Require(i.Op0Register == Register.R8 && i.Op1Register == Register.RDX, "Adapter registers"); moves++; }
                if (i.Mnemonic == Mnemonic.Call) { Require(i.IPRelativeMemoryAddress == Fixtures.Got, "Adapter call target"); calls++; }
                if (i.Mnemonic == Mnemonic.Ret) { Require(!tail, "Unexpected adapter return"); break; }
                if (i.Mnemonic == Mnemonic.Jmp)
                {
                    if (i.Op0Kind == OpKind.Memory) { Require(tail && i.IPRelativeMemoryAddress == Fixtures.Got, "Adapter tail target"); break; }
                    address = i.NearBranchTarget; jumps++; continue;
                }
                address = i.NextIP;
            }
            Require(moves == 5 && calls == 1 && jumps > 0 && visited.Count < 100, "Incomplete chained adapter");
            for (int n = 0; n < 5; n++) Require(elf.Data[0x200 + n * 17 + 1] == 0xCC && elf.Data[0x200 + n * 17 + 16] == 0xCC, "Cave guards");
        }
        return new { NamedStubs = stubCount, AdapterBlocks = blocks, ReturnConstants = 2, GotTargets = 3, PltRedirects = 1, ChainedAdapters = 2, ChainInstructions = chainInstructions };
    }

    static List<Instruction> Decode(byte[] bytes, ulong address)
    {
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes)); decoder.IP = address;
        var result = new List<Instruction>();
        while (decoder.IP < address + (ulong)bytes.Length)
        {
            decoder.Decode(out var i);
            Require(i.Code != Code.INVALID && i.NextIP <= address + (ulong)bytes.Length, "Invalid/truncated emitted block"); result.Add(i);
        }
        Require(result.Count > 0, "Empty emitted block");
        return result;
    }
    internal static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
