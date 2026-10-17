using Iced.Intel;

namespace Ps5Backport.Core;

public sealed partial class CodeImage
{
    /// <summary>Discovery runs when the code outside the unwind table is at least this many bytes (not counting padding)…</summary>
    const long DiscoverAtBytes = 1 << 20;
    /// <summary>…or this share of the code segments.</summary>
    const double DiscoverAtShare = 0.01;
    const int RelocRelative = 8;   // R_X86_64_RELATIVE

    /// <summary>
    /// Functions in code the unwind table does not cover (FINDINGS 6o: about 80 % of the code in Borderlands 4, Beast of
    /// Reincarnation and PPSA34008). A start is a call target (linear sweep of the code segments), a code pointer in a
    /// RELATIVE relocation (vtables), 16-byte-aligned code right after int3 padding, an export, or the first code of a gap;
    /// a function runs to the next start, without its trailing padding. On GT7, whose unwind table is complete, these
    /// signals find 98.5 % of the real starts at 97.5 % precision. Empty when the uncovered code is small, so fully covered
    /// modules keep exactly their unwind functions. The PLT is never a function.
    /// </summary>
    internal static List<FunctionRange> DiscoverFunctions(SceElf elf, List<FunctionRange> unwind, IReadOnlyCollection<ulong> pltEntries)
    {
        var segments = elf.Phdrs.Where(p => p.Type == SceElf.PtLoad && (p.Flags & SceElf.PfX) != 0 && p.Filesz > 0).ToList();
        ulong pltStart = pltEntries.Count > 0 ? pltEntries.Min() : 0, pltEnd = pltEntries.Count > 0 ? pltEntries.Max() + 16 : 0;

        // uncovered code: segment parts outside every unwind range and outside the PLT
        var gaps = new List<FunctionRange>();
        long uncovered = 0, total = 0;
        foreach (var seg in segments)
        {
            ulong cursor = seg.Vaddr, end = seg.Vaddr + (ulong)seg.Filesz;
            total += seg.Filesz;
            foreach (var f in unwind.Where(f => f.End > seg.Vaddr && f.Start < end).Append(new FunctionRange(end, 0)))
            {
                foreach (var gap in Without(new FunctionRange(cursor, f.Start > cursor ? f.Start - cursor : 0), pltStart, pltEnd))
                {
                    gaps.Add(gap);
                    uncovered += CodeBytes(elf, gap);
                }
                cursor = Math.Max(cursor, f.End);
            }
        }
        if (uncovered < DiscoverAtBytes && uncovered < total * DiscoverAtShare)
            return [];

        bool InGap(ulong a) => FindGap(gaps, a) >= 0;
        var starts = new HashSet<ulong>();
        foreach (var seg in segments)
        {
            byte[] data = elf.Data;
            int off = checked((int)seg.Offset), length = checked((int)seg.Filesz);
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(data, off, length), seg.Vaddr);
            ulong end = seg.Vaddr + (ulong)length;
            while (decoder.IP < end)
            {
                decoder.Decode(out var ins);
                if (ins.IsCallNear && InGap(ins.NearBranchTarget))
                    starts.Add(ins.NearBranchTarget);
            }
            for (int i = 1; i < length; i++)
            {
                ulong at = seg.Vaddr + (ulong)i;
                if (data[off + i] != Patching.Int3 && data[off + i - 1] == Patching.Int3 && (at & 15) == 0 && InGap(at))
                    starts.Add(at);
            }
        }
        foreach (var r in elf.Relocs(RelocKind.Rela))
            if (r.Type == RelocRelative && InGap((ulong)r.Addend))
                starts.Add((ulong)r.Addend);
        foreach (var s in elf.Symbols())
            if (s.Type == SceElf.SttFunc && s.Value != 0 && s.Shndx != 0 && InGap(s.Value))
                starts.Add(s.Value);
        foreach (var gap in gaps)
            if (FirstCode(elf, gap) is ulong first)
                starts.Add(first);

        // each start runs to the next start or the end of its gap, without trailing padding
        var sorted = starts.Order().ToArray();
        var functions = new List<FunctionRange>(sorted.Length);
        for (int i = 0; i < sorted.Length; i++)
        {
            var gap = gaps[FindGap(gaps, sorted[i])];
            ulong end = i + 1 < sorted.Length && sorted[i + 1] < gap.End ? sorted[i + 1] : gap.End;
            long off = elf.Off(sorted[i]);
            int size = (int)(end - sorted[i]);
            while (size > 0 && elf.Data[off + size - 1] is Patching.Int3 or 0)
                size--;
            if (size > 0)
                functions.Add(new FunctionRange(sorted[i], (ulong)size));
        }
        return functions;
    }

    /// <summary><paramref name="range"/> without [start, end).</summary>
    static IEnumerable<FunctionRange> Without(FunctionRange range, ulong start, ulong end)
    {
        if (range.Size == 0)
            yield break;
        if (end <= range.Start || start >= range.End)
        {
            yield return range;
            yield break;
        }
        if (start > range.Start)
            yield return new FunctionRange(range.Start, start - range.Start);
        if (end < range.End)
            yield return new FunctionRange(end, range.End - end);
    }

    static int FindGap(List<FunctionRange> gaps, ulong vaddr)
    {
        int lo = 0, hi = gaps.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (vaddr < gaps[mid].Start) hi = mid - 1;
            else if (vaddr >= gaps[mid].End) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    static long CodeBytes(SceElf elf, FunctionRange r)
    {
        var bytes = elf.Data.AsSpan((int)elf.Off(r.Start), (int)r.Size);
        return bytes.Length - bytes.Count(Patching.Int3) - bytes.Count((byte)0);
    }

    static ulong? FirstCode(SceElf elf, FunctionRange r)
    {
        var bytes = elf.Data.AsSpan((int)elf.Off(r.Start), (int)r.Size);
        int i = bytes.IndexOfAnyExcept(Patching.Int3, (byte)0);
        return i < 0 ? null : r.Start + (ulong)i;
    }
}
