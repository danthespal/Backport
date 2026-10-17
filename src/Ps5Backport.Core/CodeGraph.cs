using Iced.Intel;

namespace Ps5Backport.Core;

/// <summary>A function's code: [Start, Start + Size).</summary>
public readonly record struct FunctionRange(ulong Start, ulong Size)
{
    public ulong End => Start + Size;
    public bool Contains(ulong vaddr) => vaddr >= Start && vaddr < End;
}

public enum LineKind { Code, Call }

/// <summary>True/False: the taken and fall-through sides of a conditional jump; Jump: an unconditional jump or fall-through;
/// Case: a switch-table jump to one of its cases.</summary>
public enum EdgeKind { True, False, Jump, Case }

/// <summary>A constant a call passes in argument register <paramref name="Index"/> (0 = rdi … 5 = r9).</summary>
public readonly record struct CallArgument(int Index, ulong Value)
{
    static readonly string[] Registers = ["rdi", "rsi", "rdx", "rcx", "r8", "r9"];
    public string Register => Registers[Index];
}

/// <summary>One instruction: Intel syntax with names for branch targets and GOT slots; Target = the branch or call target;
/// Reference = the address of a [rip+X] operand (e.g. the GOT slot of `call [rip+X]`); Cases = a switch jump's targets in
/// table order (entries repeat when cases share code); Arguments = constants a call passes in argument registers.</summary>
public sealed record GraphLine(ulong Address, int Length, string Text, LineKind Kind, ulong? Target, ulong? Reference = null,
                               IReadOnlyList<ulong>? Cases = null, IReadOnlyList<CallArgument>? Arguments = null, DataReference? Data = null)
{
    public ulong End => Address + (ulong)Length;
}

public sealed record GraphBlock(ulong Start, IReadOnlyList<GraphLine> Lines)
{
    public ulong End => Lines[^1].End;
}

/// <summary>An edge between two blocks (indexes into <see cref="FunctionGraph.Blocks"/>).</summary>
public readonly record struct GraphEdge(int From, int To, EdgeKind Kind);

/// <summary>A function's control-flow graph. Switch tables in clang's relative form get Case edges; other indirect jumps have none.
/// <see cref="Pieces"/>: the code ranges it spans (several for a stub chain joined by jumps); Range is the first.</summary>
public sealed record FunctionGraph(IReadOnlyList<FunctionRange> Pieces, IReadOnlyList<GraphBlock> Blocks, IReadOnlyList<GraphEdge> Edges)
{
    public FunctionRange Range => Pieces[0];
    public int Instructions => Blocks.Sum(b => b.Lines.Count);
    public bool Contains(ulong vaddr) => Contains(Pieces, vaddr);
    public static bool Contains(IReadOnlyList<FunctionRange> pieces, ulong vaddr) => pieces.Any(p => p.Contains(vaddr));
}

/// <summary>Who calls whom, from one pass over every function (calls and tail jumps; indirect calls only through GOT slots).</summary>
public sealed partial class CallIndex
{
    /// <summary>{function start: starts of the functions that call or tail-jump to it}.</summary>
    public required IReadOnlyDictionary<ulong, IReadOnlyList<ulong>> Callers { get; init; }
    /// <summary>{import NID: starts of the functions that call it}.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<ulong>> ImportCallers { get; init; }
    /// <summary>{string address (<see cref="CodeImage.StringAt"/>): starts of the functions that take its address with `lea`}.</summary>
    public IReadOnlyDictionary<ulong, IReadOnlyList<ulong>> StringUsers { get; init; } = new Dictionary<ulong, IReadOnlyList<ulong>>();
}

/// <summary>
/// Read-only code view of one module for the code graph tool: functions from the unwind table (plus those discovered in
/// code it does not cover, <see cref="DiscoverFunctions"/>), names for imports
/// (through PLT entries and GOT slots), exports and the entry point, and a control-flow graph per function.
/// Nothing here writes to the module.
/// </summary>
public sealed partial class CodeImage
{
    public string Path { get; }
    public SceElf Elf { get; }
    /// <summary>Every function: the unwind table's and the discovered ones, sorted.</summary>
    public IReadOnlyList<FunctionRange> Functions { get; }
    /// <summary>The unwind table's functions only (what identifies a build).</summary>
    public IReadOnlyList<FunctionRange> Unwind { get; }
    /// <summary>Functions found outside the unwind table.</summary>
    public int DiscoveredCount => _discovered.Count;
    public IReadOnlyList<Import> Imports { get; }
    /// <summary>Libraries the module's folder ships as fakelibs: the file names in its fakelib/ folder (or the folder given to
    /// the constructor), without extension.</summary>
    public IReadOnlySet<string> FakelibLibraries { get; }
    /// <summary>Resolves names this image cannot, e.g. the original eboot for a backport whose PLT entries were rewritten.</summary>
    public CodeImage? Fallback { get; set; }

    readonly ulong[] _starts;
    readonly HashSet<ulong> _discovered;
    readonly string? _fakelibFolder;

    string FakelibFolder => _fakelibFolder ?? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)) ?? ".", "fakelib");
    readonly NameDb _names;
    readonly Dictionary<ulong, Import> _byGot = [], _byPlt = [];
    readonly Dictionary<ulong, string> _labels = [];
    /// <summary>{NID: address} of this module's own exported functions.</summary>
    readonly Dictionary<string, ulong> _exports = new(StringComparer.Ordinal);

    /// <param name="fakelibFolder">Use this folder as the module's fakelib/ (a cook preview: the store set the run would add).</param>
    public CodeImage(string path, SceElf elf, NameDb names, string? fakelibFolder = null)
    {
        _fakelibFolder = fakelibFolder;
        Path = path;
        Elf = elf;
        _names = names;
        Imports = elf.Imports();
        foreach (var imp in Imports)
        {
            _byGot.TryAdd(imp.GotVaddr, imp);
            if (imp.Plt && PltEntry(imp) is ulong entry)
                _byPlt.TryAdd(entry, imp);
        }
        var unwind = UnwindFunctions(elf);
        Unwind = unwind;
        var discovered = DiscoverFunctions(elf, unwind, _byPlt.Keys);
        _discovered = discovered.Select(f => f.Start).ToHashSet();
        Functions = discovered.Count == 0 ? unwind : [.. unwind.Concat(discovered).OrderBy(f => f.Start)];
        _starts = Functions.Select(f => f.Start).ToArray();
        var own = elf.DynList.Where(d => d.Tag == SceElf.DtSceExportLib).Select(d => (int)(d.Value >> 48)).ToHashSet();
        foreach (var s in elf.Symbols())
            if (s.Type == SceElf.SttFunc && s.Value != 0 && s.Shndx != 0 && Nid.SplitSymbol(s.Name) is var (nid, library, _) && own.Contains(library))
            {
                _labels.TryAdd(s.Value, names.Name(nid));
                _exports.TryAdd(nid, s.Value);
            }
        _labels.TryAdd(elf.U64(0x18), "entry");
        string fakelib = FakelibFolder;
        FakelibLibraries = (Directory.Exists(fakelib)
                ? Directory.EnumerateFiles(fakelib).Where(f => f.EndsWith(".sprx", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".prx", StringComparison.OrdinalIgnoreCase))
                    .Select(System.IO.Path.GetFileNameWithoutExtension).OfType<string>()
                    // libSceSaveData.native.sprx serves the library imports call libSceSaveData_native
                    .SelectMany(name => new[] { name, name.Replace('.', '_') })
                : []).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Loads an ELF or a fake-signed SELF (unwrapped in memory). Encrypted SELFs are refused.</summary>
    public static CodeImage Load(string path, NameDb names)
    {
        byte[] data = File.ReadAllBytes(path);
        string kind = Self.Kind(data);
        if (Self.IsSelf(kind))
        {
            if (Self.EncryptedSegments(data) > 0)
                throw new InvalidDataException("the SELF is encrypted: decrypt it first (PS5-SELF-Decrypter)");
            data = Self.Unwrap(data);
        }
        else if (kind != Self.KindElf)
            throw new InvalidDataException("not a SELF or ELF file");
        return new CodeImage(path, new SceElf(data), names);
    }

    /// <summary>The PLT entry of a JMPREL import: its GOT slot initially points just past an untouched `jmp [rip+GOT]`.</summary>
    ulong? PltEntry(Import imp)
    {
        try
        {
            ulong entry = Elf.ReadU64(imp.GotVaddr) - 6;
            long off = Elf.Off(entry);
            return Elf.Data[off] == 0xFF && Elf.Data[off + 1] == 0x25 && entry + 6 + (ulong)(long)(int)Elf.U32(off + 2) == imp.GotVaddr
                ? entry : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    // --- lookups -----------------------------------------------------------------------------------
    /// <summary>The function holding <paramref name="vaddr"/>.</summary>
    public FunctionRange? FunctionAt(ulong vaddr)
    {
        int i = Array.BinarySearch(_starts, vaddr);
        if (i < 0)
            i = ~i - 1;
        return i >= 0 && Functions[i].Contains(vaddr) ? Functions[i] : null;
    }

    public bool IsFunctionStart(ulong vaddr) => Array.BinarySearch(_starts, vaddr) >= 0;

    /// <summary>Addresses of the untouched PLT entries.</summary>
    internal IReadOnlyCollection<ulong> PltEntries => _byPlt.Keys;

    /// <summary>The function at <paramref name="start"/> was found outside the unwind table.</summary>
    public bool IsDiscovered(ulong start) => _discovered.Contains(start);

    /// <summary>The import whose PLT entry or GOT slot is <paramref name="vaddr"/> (here or in <see cref="Fallback"/>).</summary>
    public Import? ImportAt(ulong vaddr) =>
        _byPlt.GetValueOrDefault(vaddr) ?? _byGot.GetValueOrDefault(vaddr) ?? Fallback?.ImportAt(vaddr);

    public string ImportName(Import imp) => _names.Name(imp.Nid);

    /// <summary>Where this module's exported function <paramref name="nid"/> starts, or null.</summary>
    public ulong? ExportAddress(string nid) => _exports.TryGetValue(nid, out ulong at) ? at : null;

    /// <summary>Extra names (e.g. stubs found by a comparison); the first name given to an address wins.</summary>
    public void AddLabel(ulong vaddr, string name) => _labels.TryAdd(vaddr, name);

    /// <summary>Export name, "entry", a label, or sub_XXXX.</summary>
    public string FunctionName(ulong start) => _labels.GetValueOrDefault(start) ?? Fallback?._labels.GetValueOrDefault(start) ?? $"sub_{start:x}";

    /// <summary>Name of a branch or call target outside <paramref name="inside"/>: import, label, function; null when unknown.</summary>
    public string? TargetName(ulong target, IReadOnlyList<FunctionRange>? inside = null)
    {
        if (inside is not null && FunctionGraph.Contains(inside, target))
            return $"loc_{target:x}";
        if (ImportAt(target) is { } imp)
            return ImportName(imp);
        if (_labels.TryGetValue(target, out string? label) || Fallback?._labels.TryGetValue(target, out label) == true)
            return label;
        return IsFunctionStart(target) ? $"sub_{target:x}" : null;
    }

    // --- graphs ------------------------------------------------------------------------------------
    /// <summary>Decodes <paramref name="f"/> linearly and splits it into basic blocks.</summary>
    public FunctionGraph Graph(FunctionRange f) => Graph([f]);

    /// <summary>One graph over several pieces of code (a stub chain): jumps between the pieces become edges.</summary>
    public FunctionGraph Graph(IReadOnlyList<FunctionRange> pieces)
    {
        var insns = pieces.SelectMany(p => Decode(Elf, p)).ToList();
        var formatter = new IntelFormatter(new Resolver(this, pieces));
        formatter.Options.SpaceAfterOperandSeparator = true;
        var output = new StringOutput();
        bool Inside(ulong vaddr) => FunctionGraph.Contains(pieces, vaddr);
        var leaders = Leaders(Elf, insns, pieces, out var switches);

        bool IsTailJump(in Instruction ins) => ins.IsJmpShortOrNear && !Inside(ins.NearBranchTarget)
                                               || ins.FlowControl == FlowControl.IndirectBranch && ins.IsIPRelativeMemoryOperand;
        var blocks = new List<GraphBlock>();
        var lastOf = new List<Instruction>();
        var lines = new List<GraphLine>();
        int blockStart = 0;
        for (int k = 0; k < insns.Count; k++)
        {
            var ins = insns[k];
            if (leaders.Contains(ins.IP) && lines.Count > 0)
            {
                blocks.Add(new GraphBlock(lines[0].Address, lines));
                lastOf.Add(insns[k - 1]);
                lines = [];
            }
            if (leaders.Contains(ins.IP))
                blockStart = k;
            formatter.Format(ins, output);
            bool call = ins.FlowControl is FlowControl.Call or FlowControl.IndirectCall;
            ulong? target = ins.IsCallNear || ins.IsJmpShortOrNear || ins.IsJccShortOrNear ? ins.NearBranchTarget : null;
            ulong? reference = ins.IsIPRelativeMemoryOperand ? ins.IPRelativeMemoryAddress : null;
            var data = reference is not null && ins.FlowControl == FlowControl.Next ? DataAt(ins) : null;
            lines.Add(new GraphLine(ins.IP, ins.Length, output.ToStringAndReset(), call ? LineKind.Call : LineKind.Code, target, reference,
                                    switches.GetValueOrDefault(ins.IP), call || IsTailJump(ins) ? Arguments(insns, blockStart, k) : null, data));
        }
        if (lines.Count > 0)
        {
            blocks.Add(new GraphBlock(lines[0].Address, lines));
            lastOf.Add(insns[^1]);
        }

        var index = blocks.Select((b, i) => (b.Start, i)).ToDictionary(p => p.Start, p => p.i);
        var edges = new List<GraphEdge>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var last = lastOf[i];
            bool next = i + 1 < blocks.Count && blocks[i + 1].Start == last.NextIP;
            switch (last.FlowControl)
            {
                case FlowControl.ConditionalBranch:
                    if (index.TryGetValue(last.NearBranchTarget, out int t))
                        edges.Add(new GraphEdge(i, t, EdgeKind.True));
                    if (next)
                        edges.Add(new GraphEdge(i, i + 1, EdgeKind.False));
                    break;
                case FlowControl.UnconditionalBranch:
                    if (last.IsJmpShortOrNear && index.TryGetValue(last.NearBranchTarget, out int j))
                        edges.Add(new GraphEdge(i, j, EdgeKind.Jump));
                    break;
                case FlowControl.IndirectBranch when switches.TryGetValue(last.IP, out var cases):
                    foreach (ulong c in cases.Distinct())
                        if (index.TryGetValue(c, out int k))
                            edges.Add(new GraphEdge(i, k, EdgeKind.Case));
                    break;
                case FlowControl.Return or FlowControl.IndirectBranch or FlowControl.Interrupt or FlowControl.Exception:
                    break;
                default:
                    if (next)
                        edges.Add(new GraphEdge(i, i + 1, EdgeKind.Jump));
                    break;
            }
        }
        return new FunctionGraph(pieces, blocks, edges);
    }

    /// <summary>
    /// Block starts of decoded code (<paramref name="insns"/> over <paramref name="pieces"/>): each piece start, each branch
    /// target inside the pieces, each switch case, and the instruction after each block-ending one. Also returns the
    /// switch tables ({jmp address: case targets}).
    /// </summary>
    internal static SortedSet<ulong> Leaders(SceElf elf, List<Instruction> insns, IReadOnlyList<FunctionRange> pieces,
                                             out Dictionary<ulong, IReadOnlyList<ulong>> switches)
    {
        bool Inside(ulong vaddr) => FunctionGraph.Contains(pieces, vaddr);
        switches = SwitchTables(elf, insns, Inside);
        var leaders = new SortedSet<ulong>(pieces.Select(p => p.Start).Concat(switches.Values.SelectMany(t => t)));
        foreach (var ins in insns)
        {
            if (EndsBlock(ins))
                leaders.Add(ins.NextIP);
            if ((ins.IsJccShortOrNear || ins.IsJmpShortOrNear) && Inside(ins.NearBranchTarget))
                leaders.Add(ins.NearBranchTarget);
        }
        return leaders;
    }

    static readonly Register[] ArgumentRegisters = [Register.RDI, Register.RSI, Register.RDX, Register.RCX, Register.R8, Register.R9];

    /// <summary>
    /// Constants in the argument registers at the call (or tail jump) <paramref name="call"/>: walking back through its block to
    /// the previous call (which clobbers them), the closest write to each register decides. `mov r32/r64, imm`, `xor r, r` (0)
    /// and `push imm; pop r` are constants; any other write (Iced's register info, implicit writes included) is not. Shared by
    /// the code graph and the requirements probe (<see cref="Core.Imports.ArgumentConstants(SceElf, Import, int)"/>).
    /// </summary>
    internal static List<CallArgument>? Arguments(List<Instruction> insns, int blockStart, int call)
    {
        var decided = new bool[ArgumentRegisters.Length];
        List<CallArgument>? result = null;
        var info = new InstructionInfoFactory();
        for (int j = call - 1; j >= blockStart && decided.Contains(false); j--)
        {
            var ins = insns[j];
            if (ins.FlowControl is FlowControl.Call or FlowControl.IndirectCall)
                break;
            foreach (var used in info.GetInfo(ins).GetUsedRegisters())
            {
                if (used.Access is not (OpAccess.Write or OpAccess.ReadWrite or OpAccess.CondWrite or OpAccess.ReadCondWrite))
                    continue;
                int a = Array.IndexOf(ArgumentRegisters, used.Register.GetFullRegister());
                if (a < 0 || decided[a])
                    continue;
                decided[a] = true;
                if (Constant(insns, j, used.Register) is ulong value)
                    (result ??= []).Add(new CallArgument(a, value));
            }
        }
        result?.Sort((x, y) => x.Index.CompareTo(y.Index));
        return result;
    }

    /// <summary>The constant instruction <paramref name="j"/> writes to <paramref name="register"/>, or null.</summary>
    static ulong? Constant(List<Instruction> insns, int j, Register register)
    {
        var ins = insns[j];
        // Iced reports a 32-bit write as the full register (it zero-extends); the operand tells the width
        if (ins.Op0Kind != OpKind.Register || ins.Op0Register.GetFullRegister() != register.GetFullRegister() || ins.Op0Register.GetSize() < 4)
            return null;
        if (ins.Mnemonic == Mnemonic.Mov && ins.Op1Kind is OpKind.Immediate32 or OpKind.Immediate64 or OpKind.Immediate32to64)
            return ins.Op0Register.GetSize() == 4 ? (uint)ins.GetImmediate(1) : ins.GetImmediate(1);
        if (ins.Mnemonic == Mnemonic.Xor && ins.Op1Kind == OpKind.Register && ins.Op1Register == ins.Op0Register)
            return 0;
        if (ins.Mnemonic == Mnemonic.Pop && j > 0 && insns[j - 1] is { Mnemonic: Mnemonic.Push } push
            && push.Op0Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64)
            return push.GetImmediate(0);
        return null;
    }

    /// <summary>Most cases a switch table may have (larger counts are not a bound).</summary>
    const int MaxCases = 4096;

    /// <summary>
    /// clang's relative jump tables (FINDINGS 6o): `lea base, [rip+table]` (possibly hoisted to the function start),
    /// `movsxd r, dword [base+idx*4]`, `add r, base`, `jmp r`. The case count comes from the `cmp x, n` + `ja`/`jae` guard
    /// just before the load, and then every entry must land inside the function; without a guard, entries are read until
    /// one does not. Registers are followed backwards in address order, not along the control flow, so this is a
    /// recogniser, not a proof. {jmp address: case targets in table order}.
    /// </summary>
    static Dictionary<ulong, IReadOnlyList<ulong>> SwitchTables(SceElf elf, List<Instruction> insns, Func<ulong, bool> inside)
    {
        var tables = new Dictionary<ulong, IReadOnlyList<ulong>>();
        for (int k = 0; k < insns.Count; k++)
        {
            var jmp = insns[k];
            if (jmp.Mnemonic != Mnemonic.Jmp || jmp.Op0Kind != OpKind.Register)
                continue;
            int add = LastWrite(insns, k, jmp.Op0Register);
            if (add < 0 || insns[add].Mnemonic != Mnemonic.Add || insns[add].Op1Kind != OpKind.Register)
                continue;
            // add entry, base or add base, entry: the operand loaded by movsxd [base+idx*4] is the entry
            var (x, y) = (insns[add].Op0Register, insns[add].Op1Register);
            var (load, b) = (LoadFrom(add, x, y), y);
            if (load < 0)
                (load, b) = (LoadFrom(add, y, x), x);
            if (load < 0)
                continue;
            int lea = LastWrite(insns, load, b);
            if (lea < 0 || insns[lea].Mnemonic != Mnemonic.Lea || !insns[lea].IsIPRelativeMemoryOperand)
                continue;
            ulong table = insns[lea].IPRelativeMemoryAddress;
            int? bound = Bound(insns, load);
            var targets = new List<ulong>();
            bool valid = true;
            for (int e = 0; e < (bound ?? MaxCases); e++)
            {
                ulong? target = Entry(table, e);
                if (target is ulong t && inside(t))
                    targets.Add(t);
                else
                {
                    valid = bound is null;   // a guarded table must be valid throughout
                    break;
                }
            }
            if (valid && targets.Count > 0)
                tables[jmp.IP] = targets;
        }
        return tables;

        int LoadFrom(int add, Register entry, Register b)
        {
            int load = LastWrite(insns, add, entry);
            return load >= 0 && insns[load] is { Mnemonic: Mnemonic.Movsxd, Op1Kind: OpKind.Memory, MemoryIndexScale: 4, MemoryDisplacement64: 0 } m
                   && m.MemoryBase.GetFullRegister() == b.GetFullRegister() ? load : -1;
        }

        ulong? Entry(ulong table, int e)
        {
            try { return table + (ulong)(long)(int)elf.U32(elf.Off(table + (ulong)e * 4)); }
            catch (InvalidDataException) { return null; }
        }
    }

    /// <summary>Index of the last instruction before <paramref name="before"/> that writes <paramref name="register"/> (any width), or -1.</summary>
    static int LastWrite(List<Instruction> insns, int before, Register register)
    {
        var full = register.GetFullRegister();
        for (int j = before - 1; j >= 0; j--)
        {
            var ins = insns[j];
            if (ins.Op0Kind == OpKind.Register && ins.Op0Register.GetFullRegister() == full
                && ins.Mnemonic is not (Mnemonic.Cmp or Mnemonic.Test or Mnemonic.Push or Mnemonic.Bt))
                return j;
        }
        return -1;
    }

    /// <summary>Cases from the `cmp x, n` + `ja` (n + 1) or `jae` (n) guard within a few instructions before <paramref name="load"/>.</summary>
    static int? Bound(List<Instruction> insns, int load)
    {
        for (int j = load - 1; j > 0 && j >= load - 24; j--)
        {
            if (insns[j].Mnemonic is not (Mnemonic.Ja or Mnemonic.Jae))
                continue;
            var cmp = insns[j - 1];
            if (cmp.Mnemonic != Mnemonic.Cmp || cmp.Op1Kind is not (OpKind.Immediate8 or OpKind.Immediate8to32 or OpKind.Immediate8to64
                                                                  or OpKind.Immediate32 or OpKind.Immediate32to64))
                return null;
            ulong n = cmp.GetImmediate(1);
            ulong count = insns[j].Mnemonic == Mnemonic.Ja ? n + 1 : n;
            return count is > 0 and <= MaxCases ? (int)count : null;
        }
        return null;
    }

    static bool EndsBlock(in Instruction ins) => ins.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch
        or FlowControl.Return or FlowControl.IndirectBranch or FlowControl.Interrupt or FlowControl.Exception;

    /// <summary>Linear decoding of <paramref name="f"/> (clipped to the file).</summary>
    internal static List<Instruction> Decode(SceElf elf, FunctionRange f)
    {
        long off = elf.Off(f.Start);
        int length = (int)Math.Min(f.Size, (ulong)(elf.Data.Length - off));
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(elf.Data, (int)off, length), f.Start);
        var insns = new List<Instruction>();
        ulong end = f.Start + (ulong)length;
        while (decoder.IP < end)
            insns.Add(decoder.Decode());
        return insns;
    }

    /// <summary>One pass over every function: callers of each function and of each import, users of each string.</summary>
    public CallIndex ScanCalls(CancellationToken cancel)
    {
        var callers = new Dictionary<ulong, HashSet<ulong>>();
        var importCallers = new Dictionary<string, HashSet<ulong>>(StringComparer.Ordinal);
        var stringUsers = new Dictionary<ulong, HashSet<ulong>>();
        foreach (var f in Functions)
        {
            cancel.ThrowIfCancellationRequested();
            long off = Elf.Off(f.Start);
            int length = (int)Math.Min(f.Size, (ulong)(Elf.Data.Length - off));
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(Elf.Data, (int)off, length), f.Start);
            ulong end = f.Start + (ulong)length;
            while (decoder.IP < end)
            {
                decoder.Decode(out var ins);
                ulong target;
                if (ins.Mnemonic == Mnemonic.Lea && ins.IsIPRelativeMemoryOperand)
                {
                    if (StringAt(ins.IPRelativeMemoryAddress) is not null)
                        Add(stringUsers, ins.IPRelativeMemoryAddress, f.Start);
                    continue;
                }
                if (ins.IsCallNear || (ins.IsJmpShortOrNear && !f.Contains(ins.NearBranchTarget)))
                    target = ins.NearBranchTarget;
                else if (ins.FlowControl is FlowControl.IndirectCall or FlowControl.IndirectBranch && ins.IsIPRelativeMemoryOperand)
                    target = ins.IPRelativeMemoryAddress;
                else
                    continue;
                if (ImportAt(target) is { } imp)
                    Add(importCallers, imp.Nid, f.Start);
                else if (target != f.Start && IsFunctionStart(target))
                    Add(callers, target, f.Start);
            }
        }
        return new CallIndex
        {
            Callers = callers.ToDictionary(p => p.Key, p => (IReadOnlyList<ulong>)[.. p.Value.Order()]),
            ImportCallers = importCallers.ToDictionary(p => p.Key, p => (IReadOnlyList<ulong>)[.. p.Value.Order()], StringComparer.Ordinal),
            StringUsers = stringUsers.ToDictionary(p => p.Key, p => (IReadOnlyList<ulong>)[.. p.Value.Order()]),
        };

        static void Add<TKey>(Dictionary<TKey, HashSet<ulong>> map, TKey key, ulong caller) where TKey : notnull
        {
            if (!map.TryGetValue(key, out var set))
                map[key] = set = [];
            set.Add(caller);
        }
    }

    /// <summary>Names branch targets and GOT slots in the formatted text; other numbers stay hex.</summary>
    sealed class Resolver(CodeImage image, IReadOnlyList<FunctionRange> pieces) : ISymbolResolver
    {
        public bool TryGetSymbol(in Instruction instruction, int operand, int instructionOperand, ulong address, int addressSize, out SymbolResult symbol)
        {
            string? name = instructionOperand < 0 ? null : instruction.GetOpKind(instructionOperand) switch
            {
                OpKind.NearBranch64 or OpKind.NearBranch32 or OpKind.NearBranch16 => image.TargetName(address, pieces),
                OpKind.Memory when instruction.IsIPRelativeMemoryOperand && image.ImportAt(address) is { } imp => image.ImportName(imp) + "@got",
                _ => null,
            };
            symbol = name is null ? default : new SymbolResult(address, name);
            return name is not null;
        }
    }

    // --- unwind table ------------------------------------------------------------------------------
    /// <summary>
    /// Function ranges from PT_GNU_EH_FRAME: the .eh_frame_hdr search table gives each FDE's start and the FDE gives its
    /// pc_range (FINDINGS 6o). clang-built eboots have one FDE per function, so no symbols are needed.
    /// </summary>
    public static List<FunctionRange> UnwindFunctions(SceElf elf)
    {
        var hdr = elf.Phdrs.FirstOrDefault(p => p.Type == SceElf.PtGnuEhFrame)
            ?? throw new InvalidDataException("no PT_GNU_EH_FRAME segment: function boundaries are unknown");
        long h = hdr.Offset;
        byte[] d = elf.Data;
        // version 1; eh_frame_ptr any 4-byte encoding; fde_count udata4; table datarel|sdata4
        if (hdr.Filesz < 12 || d[h] != 1 || (d[h + 1] & 0x0F) is not (0x03 or 0x0B) || d[h + 2] != 0x03 || d[h + 3] != 0x3B)
            throw new InvalidDataException("unsupported .eh_frame_hdr encodings");
        long count = elf.U32(h + 8);
        if (12 + count * 8 > hdr.Filesz)
            throw new InvalidDataException(".eh_frame_hdr search table is truncated");
        var cieEncodings = new Dictionary<long, byte>();
        var functions = new List<FunctionRange>((int)count);
        for (long i = 0; i < count; i++)
        {
            long e = h + 12 + i * 8;
            ulong start = hdr.Vaddr + (ulong)(long)(int)elf.U32(e);
            long fde = elf.Off(hdr.Vaddr + (ulong)(long)(int)elf.U32(e + 4));
            if (elf.U32(fde) == 0xFFFFFFFF)
                throw new InvalidDataException("64-bit DWARF FDEs are not supported");
            long cie = fde + 4 - elf.U32(fde + 4);
            if (!cieEncodings.TryGetValue(cie, out byte encoding))
                cieEncodings[cie] = encoding = FdeEncoding(d, cie);
            ulong size = (encoding & 0x0F) switch
            {
                0x03 or 0x0B => elf.U32(fde + 12),
                0x00 or 0x04 or 0x0C => elf.U64(fde + 16),
                _ => throw new InvalidDataException($"unsupported FDE pointer encoding 0x{encoding:x2}"),
            };
            if (size > 0)
                functions.Add(new FunctionRange(start, size));
        }
        functions.Sort((a, b) => a.Start.CompareTo(b.Start));
        return functions;
    }

    /// <summary>The FDE pointer encoding of a CIE ('R' in a "z..." augmentation; absptr without one).</summary>
    static byte FdeEncoding(byte[] d, long cie)
    {
        long p = cie + 8;
        byte version = d[p++];
        long aug = p;
        while (d[p] != 0)
            p++;
        string augmentation = System.Text.Encoding.ASCII.GetString(d, (int)aug, (int)(p - aug));
        p++;
        Leb(d, ref p);                  // code alignment
        Leb(d, ref p);                  // data alignment
        if (version == 1)
            p++;                        // return register
        else
            Leb(d, ref p);
        if (!augmentation.StartsWith('z'))
            return 0x00;
        Leb(d, ref p);                  // augmentation data length
        foreach (char c in augmentation[1..])
        {
            switch (c)
            {
                case 'R':
                    return d[p];
                case 'L':
                    p++;
                    break;
                case 'P':
                    byte personality = d[p++];
                    p += (personality & 0x0F) is 0x02 or 0x0A ? 2 : (personality & 0x0F) is 0x03 or 0x0B ? 4 : 8;
                    break;
            }
        }
        return 0x00;
    }

    /// <summary>Skips one LEB128 value (signedness does not matter for skipping).</summary>
    static void Leb(byte[] d, ref long p)
    {
        while ((d[p++] & 0x80) != 0) { }
    }
}
