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

    /// <summary>
    /// The "; asm" comment of every adapter instruction and every stub body documents hand-encoded bytes. This decodes the
    /// bytes with Iced and compares mnemonic and operands with the comment structurally: registers, memory base/index/scale/
    /// displacement, immediates under 8/16/32/64-bit masks, branch targets relative to the block start, {state+N} placeholders.
    /// Size words and "ptr" in the comment are not checked (the bytes decide), notes after two spaces or " (" are ignored, and a
    /// label operand (".ret") checks the mnemonic only. Any disagreement fails with the full list.
    /// </summary>
    internal static object Comments(string root)
    {
        var knowledge = new Knowledge(Path.Combine(root, "data"));
        var mismatches = new List<string>();
        int checkedInstructions = 0, withoutComment = 0;
        var owned = knowledge.Fixes.Values.Select(f => ("fixes", f))
            .Concat(knowledge.Options.SelectMany(o => o.Value.Fixes.Values.Select(f => ($"options.{o.Key}", f))));
        foreach (var (owner, fix) in owned)
        {
            ulong ip = 0;
            foreach (var insn in fix.Insns)
            {
                if (insn.CallGot) { ip += 6; continue; }
                var decoder = Decoder.Create(64, new ByteArrayCodeReader(insn.Bytes)); decoder.IP = ip;
                decoder.Decode(out var instruction);
                if (instruction.Code == Code.INVALID || instruction.NextIP != ip + (ulong)insn.Bytes.Length)
                    mismatches.Add($"{owner}.{fix.Nid}: bytes {insn.Hex} do not decode to exactly one instruction");
                else if (insn.Asm.Length == 0)
                    withoutComment++;
                else
                    Compare($"{owner}.{fix.Nid} @ {ip:x}", instruction, insn.Asm, insn.StateAt >= 0 ? insn.StateOffset : -1);
                ip += (ulong)insn.Bytes.Length;
            }
        }
        foreach (var stub in knowledge.Stubs.Values)
        {
            var pieces = stub.Asm.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0)
                .Select(p => p.StartsWith('.') && p.Contains(':') ? p[(p.IndexOf(':') + 1)..].Trim() : p).ToList();
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(stub.Bytes)); decoder.IP = 0;
            var decoded = new List<Instruction>();
            while (decoder.IP < (ulong)stub.Bytes.Length) { decoder.Decode(out var i); decoded.Add(i); }
            if (decoded.Count != pieces.Count)
                mismatches.Add($"stubs.{stub.Name}: {decoded.Count} instructions decoded, {pieces.Count} in the comment");
            else
                for (int n = 0; n < decoded.Count; n++) Compare($"stubs.{stub.Name} #{n}", decoded[n], pieces[n], -1);
        }
        Require(mismatches.Count == 0, "Comment/bytes mismatches:\n  " + string.Join("\n  ", mismatches));
        return new { Instructions = checkedInstructions, WithoutComment = withoutComment };

        void Compare(string where, in Instruction i, string asm, int stateOffset)
        {
            checkedInstructions++;
            string text = asm.Trim();
            int cut = new[] { text.IndexOf("  ", StringComparison.Ordinal), text.IndexOf(" (", StringComparison.Ordinal) }
                .Where(c => c >= 0).DefaultIfEmpty(-1).Min();
            if (cut >= 0) text = text[..cut].Trim();
            string[] head = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            string mnemonic = head[0];
            string rest = head.Length > 1 ? head[1] : "";
            if (mnemonic is "rep" or "repe" or "repne")
            {
                string[] next = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                mnemonic += " " + next[0];
                rest = next.Length > 1 ? next[1] : "";
            }
            string decodedMnemonic = (i.HasRepPrefix ? "rep " : i.HasRepnePrefix ? "repne " : "") + i.Mnemonic.ToString().ToLowerInvariant();
            if (mnemonic != decodedMnemonic) { mismatches.Add($"{where}: mnemonic '{mnemonic}' vs decoded '{decodedMnemonic}' ({text})"); return; }
            var operands = rest.Length == 0 ? new List<string>() : rest.Split(',').Select(o => o.Trim()).ToList();
            if (operands.Any(o => o.StartsWith('.'))) return;                     // label: mnemonic only
            var kinds = new List<OpKind>();
            for (int n = 0; n < i.OpCount; n++) kinds.Add(i.GetOpKind(n));
            if (operands.Count == 0 && kinds.All(k => k is OpKind.Register or OpKind.MemoryESRDI
                    or OpKind.MemoryESEDI or OpKind.MemorySegRSI or OpKind.MemorySegESI or OpKind.MemorySegRDI or OpKind.MemorySegEDI))
                return;                                                           // string instruction: implicit operands only
            if (operands.Count != i.OpCount) { mismatches.Add($"{where}: {operands.Count} operand(s) in the comment, {i.OpCount} decoded ({text})"); return; }
            for (int n = 0; n < i.OpCount; n++)
            {
                string want = CanonText(operands[n], stateOffset), got = CanonDecoded(i, n, stateOffset);
                if (want.StartsWith("imm:") && got.StartsWith("imm:"))
                {
                    ulong a = Convert.ToUInt64(want[4..], 16), b = Convert.ToUInt64(got[4..], 16);
                    if (new ulong[] { 0xFF, 0xFFFF, 0xFFFFFFFF, ulong.MaxValue }.Any(mask => ((a ^ b) & mask) == 0)) continue;
                }
                else if (want == got) continue;
                mismatches.Add($"{where}: operand {n} '{operands[n]}' -> {want}, decoded {got} ({text})");
            }
        }

        static string Reg(Register r) => System.Text.RegularExpressions.Regex.Replace(r.ToString().ToLowerInvariant(), @"^(r\d+)l$", "$1b");

        static string CanonDecoded(in Instruction i, int n, int stateOffset)
        {
            switch (i.GetOpKind(n))
            {
                case OpKind.Register: return Reg(i.GetOpRegister(n));
                case OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64: return $"imm:{i.NearBranchTarget:x}";
                case OpKind.Memory:
                    if (i.IsIPRelativeMemoryOperand)
                        return stateOffset >= 0 ? $"[rip+state+0x{stateOffset:x}]" : $"[rip->0x{i.IPRelativeMemoryAddress:x}]";
                    var parts = new List<string>();
                    if (i.MemoryBase != Register.None) parts.Add(Reg(i.MemoryBase));
                    if (i.MemoryIndex != Register.None) parts.Add($"{Reg(i.MemoryIndex)}*{i.MemoryIndexScale}");
                    long disp = (long)i.MemoryDisplacement64;
                    if (disp != 0 || parts.Count == 0) parts.Add(disp < 0 ? $"-0x{-disp:x}" : $"0x{disp:x}");
                    return "[" + string.Join("+", parts).Replace("+-", "-") + "]";
                default: return $"imm:{i.GetImmediate(n):x}";
            }
        }

        static string CanonText(string operand, int stateOffset)
        {
            string s = System.Text.RegularExpressions.Regex.Replace(operand.ToLowerInvariant(), @"\s+", "").Replace("ptr", "");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"^(byte|word|dword|qword|xmmword|ymmword)", "");
            if (s.StartsWith('['))
            {
                string inner = s[1..^1];
                if (inner.Contains("state"))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(inner, @"state\+0x([0-9a-f]+)");
                    return $"[rip+state+0x{(m.Success ? Convert.ToInt64(m.Groups[1].Value, 16) : 0):x}]";
                }
                string? baseReg = null, index = null;
                long disp = 0;
                foreach (System.Text.RegularExpressions.Match t in System.Text.RegularExpressions.Regex.Matches(inner, @"([+-]?)([a-z0-9*]+)"))
                {
                    string sign = t.Groups[1].Value, tok = t.Groups[2].Value;
                    if (tok.Contains('*')) index = tok;
                    else if (System.Text.RegularExpressions.Regex.IsMatch(tok, @"^(0x[0-9a-f]+|\d+)$"))
                        disp += (sign == "-" ? -1 : 1) * (tok.StartsWith("0x") ? Convert.ToInt64(tok[2..], 16) : long.Parse(tok));
                    else if (baseReg is null) baseReg = tok;
                    else index = tok + "*1";
                }
                var parts = new List<string>();
                if (baseReg is not null) parts.Add(baseReg);
                if (index is not null) parts.Add(index);
                if (disp != 0 || parts.Count == 0) parts.Add(disp < 0 ? $"-0x{-disp:x}" : $"0x{disp:x}");
                return "[" + string.Join("+", parts).Replace("+-", "-") + "]";
            }
            if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^(r[abcd]x|r[sb]p|r[sd]i|r(8|9|1[0-5])[dwb]?|e[abcd]x|e[sb]p|e[sd]i|[abcd]x|[sb]p|[sd]i|[abcd][lh]|[sd]il|[sb]pl|xmm\d+|ymm\d+)$"))
                return s;
            bool invert = s.StartsWith('~');
            if (invert) s = s[1..];
            bool negative = s.StartsWith('-');
            if (negative) s = s[1..];
            if (!System.Text.RegularExpressions.Regex.IsMatch(s, @"^(0x[0-9a-f]+|\d+)$")) return "?" + operand;
            long value = s.StartsWith("0x") ? Convert.ToInt64(s[2..], 16) : long.Parse(s);
            if (negative) value = -value;
            if (invert) value = ~value;
            return $"imm:{(ulong)value:x}";
        }
    }
}
