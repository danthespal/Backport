using Iced.Intel;

namespace Ps5Backport.Core;

/// <summary>What a call site does with the import's return value (eax).</summary>
public enum ResultUse { Unused, ErrorCheck, Used }

/// <summary>One call site of an import: <paramref name="Why"/> is the proof (unused, error-check) or the first instruction that
/// keeps the result (used).</summary>
public sealed record CallerResult(ulong Site, ResultUse Use, string Why);

public static partial class Imports
{
    /// <summary>Instructions one site's walk may decode before it gives up (Decker97's budget).</summary>
    const int ResultBudget = 96;

    /// <summary>
    /// {NID: what each call site does with the result}, for the PLT imports in <paramref name="statuses"/> (FINDINGS 6v, after
    /// Decker97's stub gate). Call sites are decoded calls to the PLT entry (<see cref="CodeToDecode"/>, as for
    /// <see cref="ArgumentConstants(SceElf, Import, int)"/>); calls through the GOT slot and data references are not covered.
    /// From the return address every path is followed (direct jumps, both sides of a jcc):
    ///   unused       on every path eax is overwritten (a full 32/64-bit write or 'xor eax, eax'), another call, int3, ud2 or hlt
    ///                comes first, without any instruction reading it
    ///   error-check  'test eax, eax' and je/jne/js/jns right after the call, and the success side (eax = 0) is unused
    ///   used         anything else: a read, a partial write, ret, an indirect branch, a tail call (jmp/jcc to the PLT entry),
    ///                undecodable bytes or more than <see cref="ResultBudget"/> instructions
    /// Only the return value is checked: whether a stub must also fill an output argument is not.
    /// </summary>
    public static Dictionary<string, List<CallerResult>> CallerResults(SceElf elf, IEnumerable<ImportStatus> statuses, CancellationToken cancel)
    {
        var plts = new Dictionary<ulong, string>();
        var result = new Dictionary<string, List<CallerResult>>(StringComparer.Ordinal);
        foreach (var s in statuses)
        {
            result[s.Nid] = [];
            if (s.Plt)
                plts[elf.ReadU64(s.GotVaddr) - 6] = s.Nid;
        }
        if (plts.Count == 0)
            return result;
        var code = elf.Phdrs.Where(p => p.Type == SceElf.PtLoad && (p.Flags & SceElf.PfX) != 0).ToList();
        var done = new HashSet<ulong>();
        foreach (var range in CodeToDecode(elf, plts.Keys.ToHashSet(), cancel))
        {
            cancel.ThrowIfCancellationRequested();
            foreach (var ins in CodeImage.Decode(elf, range))
            {
                if (!(ins.IsCallNear || ins.IsJmpShortOrNear || ins.IsJccShortOrNear) || !plts.TryGetValue(ins.NearBranchTarget, out string? nid)
                    || plts.ContainsKey(ins.IP) || !done.Add(ins.IP))   // windows of nearby sites can overlap
                    continue;
                result[nid].Add(ins.IsCallNear ? ClassifyResult(elf, code, ins) : new CallerResult(ins.IP, ResultUse.Used, "tail call: the caller gets the result"));
            }
        }
        foreach (var sites in result.Values)
            sites.Sort((x, y) => x.Site.CompareTo(y.Site));
        return result;
    }

    /// <summary>"used 1 (0x… reads the result: …), error-check 1, unused 2"; empty without call sites.</summary>
    public static string ResultSummary(IReadOnlyList<CallerResult> sites)
    {
        var parts = new List<string>();
        var used = sites.Where(s => s.Use == ResultUse.Used).ToList();
        if (used.Count > 0)
            parts.Add($"used {used.Count} ({used[0].Why})");
        int check = sites.Count(s => s.Use == ResultUse.ErrorCheck), unused = sites.Count(s => s.Use == ResultUse.Unused);
        if (check > 0)
            parts.Add($"error-check {check}");
        if (unused > 0)
            parts.Add($"unused {unused}");
        return string.Join(", ", parts);
    }

    static CallerResult ClassifyResult(SceElf elf, List<Phdr> code, in Instruction call)
    {
        var (unused, why) = ResultUnused(elf, code, call.NextIP);
        if (unused)
            return new CallerResult(call.IP, ResultUse.Unused, why);
        if (ErrorCheckOnly(elf, code, call.NextIP) is { } check)
            return new CallerResult(call.IP, check.Ok ? ResultUse.ErrorCheck : ResultUse.Used, check.Why);
        return new CallerResult(call.IP, ResultUse.Used, why);
    }

    /// <summary>Whether no path from <paramref name="start"/> reads eax before overwriting it, or the first reason it may.</summary>
    static (bool Unused, string Why) ResultUnused(SceElf elf, List<Phdr> code, ulong start)
    {
        var info = new InstructionInfoFactory();
        var todo = new Stack<ulong>([start]);
        var seen = new HashSet<ulong>();
        int n = 0;
        while (todo.Count > 0)
        {
            ulong pc = todo.Pop();
            while (seen.Add(pc))
            {
                if (DecodeAt(elf, code, pc) is not { } ins)
                    return (false, $"0x{pc:x}: undecodable");
                if (++n > ResultBudget)
                    return (false, $"more than {ResultBudget} instructions without a proof");
                if (ins.Mnemonic is Mnemonic.Xor or Mnemonic.Sub && ins.Op0Kind == OpKind.Register && ins.Op1Kind == OpKind.Register
                    && ins.Op0Register == ins.Op1Register && ins.Op0Register is Register.EAX or Register.RAX)
                    break;   // the result is zeroed: this path is done
                bool fullWrite = false, partialWrite = false;
                foreach (var r in info.GetInfo(ins).GetUsedRegisters())
                {
                    if (r.Register.GetFullRegister() != Register.RAX)
                        continue;
                    if (r.Access is OpAccess.Read or OpAccess.ReadWrite or OpAccess.CondRead or OpAccess.ReadCondWrite)
                        return (false, $"0x{ins.IP:x} reads the result: {ins}");
                    if (r.Access is OpAccess.Write)
                    {
                        if (r.Register.GetSize() >= 4)   // a 32-bit write zero-extends
                            fullWrite = true;
                        else
                            partialWrite = true;
                    }
                    else if (r.Access is OpAccess.CondWrite)
                        partialWrite = true;
                }
                if (ins.FlowControl is FlowControl.Call or FlowControl.IndirectCall or FlowControl.Interrupt || ins.Mnemonic is Mnemonic.Ud2 or Mnemonic.Hlt)
                    break;   // the callee's result replaces it / the path stops
                if (fullWrite)
                    break;
                if (partialWrite)
                    return (false, $"0x{ins.IP:x} partial write keeps the result live: {ins}");
                switch (ins.FlowControl)
                {
                    case FlowControl.Return:
                        return (false, $"0x{ins.IP:x} returns with the result in eax");
                    case FlowControl.UnconditionalBranch when ins.IsJmpShortOrNear:
                        pc = ins.NearBranchTarget;
                        continue;
                    case FlowControl.ConditionalBranch when ins.Op0Kind is OpKind.NearBranch64:   // jcc, loop, jrcxz
                        todo.Push(ins.NearBranchTarget);
                        break;
                    case FlowControl.UnconditionalBranch or FlowControl.IndirectBranch or FlowControl.ConditionalBranch:
                        return (false, $"0x{ins.IP:x} indirect branch");
                }
                pc = ins.NextIP;
            }
        }
        return (true, "result never read");
    }

    /// <summary>Null unless <paramref name="at"/> is 'test eax, eax' and a direct je/jne/js/jns; then whether the success side
    /// (eax = 0) is unused.</summary>
    static (bool Ok, string Why)? ErrorCheckOnly(SceElf elf, List<Phdr> code, ulong at)
    {
        if (DecodeAt(elf, code, at) is not { Mnemonic: Mnemonic.Test, Op0Kind: OpKind.Register, Op1Kind: OpKind.Register } test
            || test.Op0Register != test.Op1Register || test.Op0Register is not (Register.EAX or Register.RAX)
            || DecodeAt(elf, code, test.NextIP) is not { IsJccShortOrNear: true } branch)
            return null;
        bool? fallsThrough = branch.ConditionCode switch
        {
            ConditionCode.ne or ConditionCode.s => true,    // taken on error
            ConditionCode.e or ConditionCode.ns => false,   // taken on success
            _ => null,
        };
        if (fallsThrough is not { } fall)
            return null;
        ulong success = fall ? branch.NextIP : branch.NearBranchTarget;
        var (unused, why) = ResultUnused(elf, code, success);
        return unused ? (true, $"0x{test.IP:x}: test {Lower(test.Op0Register)}, {Lower(test.Op0Register)}; {branch.Mnemonic.ToString().ToLowerInvariant()}, success (eax = 0) continues at 0x{success:x}")
                      : (false, $"success path 0x{success:x}: {why}");
    }

    static string Lower(Register r) => r.ToString().ToLowerInvariant();

    /// <summary>The instruction at <paramref name="va"/> in an executable segment, or null (outside code or invalid bytes).</summary>
    static Instruction? DecodeAt(SceElf elf, List<Phdr> code, ulong va)
    {
        var seg = code.FirstOrDefault(p => va >= p.Vaddr && va < p.Vaddr + (ulong)p.Filesz);
        if (seg is null)
            return null;
        int off = checked((int)(seg.Offset + (long)(va - seg.Vaddr)));
        int length = (int)Math.Min(15, seg.Vaddr + (ulong)seg.Filesz - va);
        var ins = Decoder.Create(64, new ByteArrayCodeReader(elf.Data, off, length), va).Decode();
        return ins.IsInvalid ? null : ins;
    }
}
