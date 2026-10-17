using System.Buffers.Binary;

namespace Ps5Backport.Core;

public static partial class Imports
{
    /// <summary>Argument registers of the SysV calling convention, 32-bit forms, by argument index.</summary>
    static readonly string[] ArgumentRegisters = ["edi", "esi", "edx", "ecx", "r8d", "r9d"];

    /// <summary>
    /// Call sites of an import (call, jmp or jcc to its PLT entry) with the constant the game passes in argument
    /// <paramref name="argument"/> (0 = rdi … 5 = r9), read from decoded instructions: walking back through the site's basic
    /// block to the previous call, the closest write to the register decides, and only <c>mov r32/r64, imm</c>,
    /// <c>xor r, r</c> (0) and <c>push imm; pop r</c> are constants (<see cref="CodeImage.Arguments"/>, shared with the code
    /// graph). Null when the value is not a constant there (loaded from memory, computed, or set in another block).
    /// The functions to decode are those holding a byte-pattern hit (call/jmp/jcc rel32 to the PLT entry) according to the
    /// unwind table; a module without one is decoded segment by segment. Replaced the plain byte scan on 15 October 2026,
    /// which also read immediates inside other instructions' bytes (FINDINGS 6o). Still evidence, not proof: registers are
    /// read within a block, not along every path.
    /// </summary>
    public static List<(ulong Site, uint? Constant)> ArgumentConstants(SceElf elf, Import import, int argument)
        => ArgumentConstants(elf, import, argument, default);

    public static List<(ulong Site, uint? Constant)> ArgumentConstants(SceElf elf, Import import, int argument, CancellationToken cancel)
    {
        if (argument is < 0 or > 5)
            throw new ArgumentOutOfRangeException(nameof(argument), "SysV passes six integer arguments in registers");
        if (!import.Plt)
            return [];
        ulong plt = elf.ReadU64(import.GotVaddr) - 6;
        var result = new List<(ulong, uint?)>();
        foreach (var range in CodeToDecode(elf, new HashSet<ulong> { plt }, cancel))
        {
            cancel.ThrowIfCancellationRequested();
            var insns = CodeImage.Decode(elf, range);
            var leaders = CodeImage.Leaders(elf, insns, [range], out _);
            int blockStart = 0;
            for (int k = 0; k < insns.Count; k++)
            {
                var ins = insns[k];
                if (leaders.Contains(ins.IP))
                    blockStart = k;
                if (!(ins.IsCallNear || ins.IsJmpShortOrNear || ins.IsJccShortOrNear) || ins.NearBranchTarget != plt || ins.IP == plt)
                    continue;
                var constant = CodeImage.Arguments(insns, blockStart, k)?.Where(a => a.Index == argument).Select(a => (uint?)(uint)a.Value).FirstOrDefault();
                result.Add((ins.IP, constant));
            }
        }
        // windows of nearby sites can overlap
        return [.. result.DistinctBy(r => r.Item1).OrderBy(r => r.Item1)];
    }

    /// <summary>
    /// The code ranges to decode for calls to the PLT entries <paramref name="plts"/>: the unwind-table functions holding a
    /// byte-pattern hit (call/jmp/jcc rel32 to one; a superset of the real sites), the gap between two such functions for a hit
    /// in code without an FDE (Borderlands 4, Beast of Reincarnation and PPSA34008 call the pad import from such code), or,
    /// without an unwind table, the executable segments with a hit.
    /// </summary>
    static List<FunctionRange> CodeToDecode(SceElf elf, IReadOnlySet<ulong> plts, CancellationToken cancel)
    {
        var hits = new List<ulong>();
        var segments = new List<FunctionRange>();
        var executable = new List<FunctionRange>();
        foreach (var p in elf.Phdrs)
        {
            if (p.Type != SceElf.PtLoad || (p.Flags & SceElf.PfX) == 0)
                continue;
            var seg = elf.Data.AsSpan(checked((int)p.Offset), checked((int)p.Filesz));
            executable.Add(new FunctionRange(p.Vaddr, (ulong)p.Filesz));
            int before = hits.Count;
            for (int i = 0; i + 5 <= seg.Length; i++)
            {
                if ((i & 65535) == 0) cancel.ThrowIfCancellationRequested();
                byte b = seg[i];
                int length;
                if (b is 0xE8 or 0xE9) length = 5;
                else if (b == 0x0F && i + 6 <= seg.Length && seg[i + 1] is >= 0x80 and <= 0x8F) length = 6;
                else continue;
                long target = (long)p.Vaddr + i + length + BinaryPrimitives.ReadInt32LittleEndian(seg[(i + length - 4)..]);
                if (plts.Contains((ulong)target) && !plts.Contains(p.Vaddr + (ulong)i))
                    hits.Add(p.Vaddr + (ulong)i);
            }
            if (hits.Count > before)
                segments.Add(new FunctionRange(p.Vaddr, (ulong)p.Filesz));
        }
        if (hits.Count == 0)
            return [];
        List<FunctionRange> functions;
        try
        {
            functions = CodeImage.UnwindFunctions(elf);
        }
        catch (InvalidDataException)
        {
            return segments;
        }
        var starts = functions.Select(f => f.Start).ToArray();
        var ranges = new SortedSet<FunctionRange>(Comparer<FunctionRange>.Create((x, y) => x.Start.CompareTo(y.Start)));
        foreach (ulong hit in hits)
        {
            int i = Array.BinarySearch(starts, hit);
            if (i < 0)
                i = ~i - 1;
            if (i >= 0 && functions[i].Contains(hit))
                ranges.Add(functions[i]);
            else if (executable.FirstOrDefault(e => e.Contains(hit)) is { Size: > 0 } segment)
                ranges.Add(Window(elf, hit, Math.Max(segment.Start, i >= 0 ? functions[i].End : segment.Start)));
        }
        return [.. ranges];
    }

    /// <summary>How far before a site in code without an FDE decoding may start (such gaps can be most of the code segment).</summary>
    const int WindowBack = 0x2000;

    /// <summary>
    /// A window ending just after the site <paramref name="hit"/>, starting after the last int3 padding within
    /// <see cref="WindowBack"/> bytes before it (where a function most likely starts; else at the window's edge, x86 decoding
    /// resynchronises within a few instructions), never before <paramref name="floor"/> (the previous function's end).
    /// </summary>
    static FunctionRange Window(SceElf elf, ulong hit, ulong floor)
    {
        ulong start = Math.Max(floor, hit > WindowBack ? hit - WindowBack : 0);
        long off = elf.Off(start);
        var bytes = elf.Data.AsSpan((int)off, (int)(hit - start));
        int padding = bytes.LastIndexOf(Patching.Int3);
        if (padding >= 0)
            start += (ulong)padding + 1;
        return new FunctionRange(start, hit + 16 - start);
    }

    public static string ArgumentRegister(int argument) => ArgumentRegisters[argument];
}
