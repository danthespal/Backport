using System.Buffers.Binary;

namespace Ps5Backport.Core;

/// <summary>Opt-in fallback for a recognized Unity/Esut unset-SKU failure and its recoverable consumer.</summary>
internal static class UnitySkuFallback
{
    internal const string Option = "unity_sku_offline";
    internal const string Name = "Unity unset SKU: existing offline fallback";

    // Only relocations are wildcarded. Registers, field offsets, enum values and control flow must match.
    const string Dispatch = "49 8b 46 48 48 85 c0 0f 84 ?? ?? ?? ?? 8b 40 30 83 f8 01 74 ?? 83 f8 03 0f 85 ?? ?? ?? ??";
    const string Failure = "48 8b 3d ?? ?? ?? ?? e8 ?? ?? ?? ?? 48 8b 3d ?? ?? ?? ?? 83 bf e4 00 00 00 00 75 0c e8 ?? ?? ?? ?? 48 8b 3d ?? ?? ?? ?? 48 8b 87 b8 00 00 00 48 8b 70 20 eb ??";
    const string Consumer = "48 8b 3d ?? ?? ?? ?? 4c 8b 79 18 83 bf e4 00 00 00 00 75 0c e8 ?? ?? ?? ?? 48 8b 3d ?? ?? ?? ?? 48 8b 87 b8 00 00 00 48 8b 40 70 48 85 c0 0f 84 ?? ?? ?? ?? 8b 70 10 4c 89 ff 31 d2 e8 ?? ?? ?? ?? 84 c0 74 31 48 8b 7b 78 48 85 ff 0f 84 ?? ?? ?? ?? 49 8b 46 20 48 8b 80 c0 00 00 00 48 8b b0 f0 00 00 00 e8 ?? ?? ?? ?? 84 c0 75 65 83 bb 80 00 00 00 00 74 5c";

    /// <summary>Returns one only for a unique linked producer/consumer; never changes a SKU or success result.</summary>
    internal static int Apply(SceElf elf, CancellationToken cancel = default)
    {
        var dispatches = Find(elf, Dispatch, cancel);
        if (dispatches.Count != 1) return 0;
        long dispatch = dispatches[0];
        long failureVa = Relative(elf, dispatch + 24, 6, 2);
        long? failureOffset = ExecutableOffset(elf, failureVa, 53);
        if (failureOffset is not { } failure || !Matches(elf.Data, failure, Parse(Failure))) return 0;
        // The NotSet/default branch and the SKU dispatch belong to the same function, in this layout.
        if (failure <= dispatch + 30 || failure - dispatch > 1024) return 0;
        long errorClass = Relative(elf, failure + 12, 7, 3);
        if (errorClass != Relative(elf, failure + 33, 7, 3) || !ReadableData(elf, errorClass)) return 0;
        var consumers = Find(elf, Consumer, cancel).Where(c =>
            Relative(elf, c, 7, 3) == errorClass && Relative(elf, c + 25, 7, 3) == errorClass &&
            Relative(elf, c + 20, 5, 1) == Relative(elf, failure + 28, 5, 1)).ToList();
        if (consumers.Count != 1) return 0;
        long consumer = consumers[0];
        // Both 'has offline mode' and TitleScreen=0 branches must reach the same existing continuation.
        long continuation = checked((long)elf.Vaddr(consumer + 109) + (sbyte)elf.Data[consumer + 108]);
        if (continuation != checked((long)elf.Vaddr(consumer + 118) + (sbyte)elf.Data[consumer + 117]) ||
            ExecutableOffset(elf, continuation, 1) is null) return 0;
        long entry = failure + 47;
        long resumeVa = checked((long)elf.Vaddr(entry + 6) + (sbyte)elf.Data[entry + 5]);
        long? resumeOffset = ExecutableOffset(elf, resumeVa, 12);
        if (resumeOffset is not { } resume || !Matches(elf.Data, resume,
            Parse("48 8b 0d ?? ?? ?? ?? 48 89 df 31 d2"))) return 0;
        cancel.ThrowIfCancellationRequested();
        byte[] body = Convert.FromHexString("488B7020498B5648837A30007504488B7070E900000000");
        long cave = Patching.FindCave(elf, body.Length) ?? throw new PatchException("Matched Unity unset-SKU fallback, but no code padding is available");
        long caveVa = checked((long)elf.Vaddr(cave));
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(body.Length - 4), checked((int)(resumeVa - caveVa - body.Length)));
        byte[] redirect = [0xe9, 0, 0, 0, 0, 0x90];
        BinaryPrimitives.WriteInt32LittleEndian(redirect.AsSpan(1), checked((int)(caveVa - (long)elf.Vaddr(entry) - 5)));
        body.CopyTo(elf.Data, cave);
        redirect.CopyTo(elf.Data, entry);
        return 1;
    }

    static byte?[] Parse(string pattern) => pattern.Split(' ').Select(s => s == "??" ? (byte?)null : Convert.ToByte(s, 16)).ToArray();

    static bool Matches(byte[] data, long at, byte?[] pattern)
    {
        if (at < 0 || at > data.Length - pattern.Length) return false;
        for (int i = 0; i < pattern.Length; i++)
            if (pattern[i] is { } value && data[at + i] != value) return false;
        return true;
    }

    static List<long> Find(SceElf elf, string signature, CancellationToken cancel)
    {
        byte?[] pattern = Parse(signature);
        byte[] anchor = pattern.TakeWhile(b => b.HasValue).Select(b => b!.Value).ToArray();
        var hits = new List<long>();
        foreach (var segment in elf.Phdrs.Where(p => p.Type == SceElf.PtLoad && (p.Flags & SceElf.PfX) != 0))
        {
            long end = segment.Offset + segment.Filesz - pattern.Length + 1;
            for (long at = segment.Offset; at < end;)
            {
                cancel.ThrowIfCancellationRequested();
                int count = (int)Math.Min(65536, end - at);
                int found = elf.Data.AsSpan((int)at, count + anchor.Length - 1).IndexOf(anchor);
                if (found < 0) { at += count; continue; }
                at += found;
                if (Matches(elf.Data, at, pattern)) hits.Add(at);
                at++;
            }
        }
        return hits;
    }

    static long Relative(SceElf elf, long instruction, int length, int displacement)
        => checked((long)elf.Vaddr(instruction) + length + BinaryPrimitives.ReadInt32LittleEndian(elf.Data.AsSpan((int)instruction + displacement, 4)));

    static long? ExecutableOffset(SceElf elf, long address, int length)
    {
        if (address < 0) return null;
        var segment = elf.Phdrs.FirstOrDefault(p => p.Type == SceElf.PtLoad && (p.Flags & SceElf.PfX) != 0 &&
            (ulong)address >= p.Vaddr && (ulong)address + (ulong)length <= p.Vaddr + (ulong)p.Filesz);
        return segment is null ? null : segment.Offset + (address - (long)segment.Vaddr);
    }

    static bool ReadableData(SceElf elf, long address) => address >= 0 && elf.Phdrs.Any(p =>
        p.Type == SceElf.PtLoad && (p.Flags & 5) == 4 && (ulong)address >= p.Vaddr && (ulong)address + 8 <= p.Vaddr + (ulong)p.Filesz);
}
