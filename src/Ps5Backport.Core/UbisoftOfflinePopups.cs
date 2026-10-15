using System.Buffers.Binary;

namespace Ps5Backport.Core;

/// <summary>Opt-in SilentError classification for recognized Ubisoft offline warning definitions.</summary>
internal static class UbisoftOfflinePopups
{
    internal const string Option = "ubisoft_offline_popups";
    internal const string Name = "Ubisoft offline popups: existing silent callbacks";

    // Match the compiler layout and error IDs, allowing relocation and static-field addresses to move.
    const string Definitions = "48 8b 3d ?? ?? ?? ?? e8 ?? ?? ?? ?? c7 40 10 a8 11 00 00 c7 40 1c ?? 00 00 00 48 c7 40 14 00 00 00 00 bf ?? ?? ?? ?? 48 8b 0d ?? ?? ?? ?? 48 8b 91 b8 00 00 00 48 89 82 ?? ?? ?? ?? 48 03 b9 b8 00 00 00 e8 ?? ?? ?? ?? 48 8b 3d ?? ?? ?? ?? e8 ?? ?? ?? ?? c7 40 10 a9 11 00 00 c7 40 1c ?? 00 00 00 48 c7 40 14 00 00 00 00 bf ?? ?? ?? ?? 48 8b 0d ?? ?? ?? ?? 48 8b 91 b8 00 00 00 48 89 82 ?? ?? ?? ?? 48 03 b9 b8 00 00 00 e8 ?? ?? ?? ??";
    const string HandlerPrefix = "48 8b 3d ?? ?? ?? ?? 4c 8d 7d 10 83 bf e4 00 00 00 00 75 05 e8 ?? ?? ?? ?? 4c 89 ff e8 ?? ?? ?? ?? 48 85 c0 0f 84 ?? ?? ?? ?? 83 78 1c 02";
    const string Standalone = HandlerPrefix + " 75 1f 48 85 db 74 ?? 48 8b 7b 40 48 8b 43 18 48 8b 73 28 48 83 c4 20 5b 41 5c 41 5e 41 5f 5d ff e0";
    const string Popup = HandlerPrefix + " 75 1d 31 c0 48 85 db 0f 84 ?? ?? ?? ?? 48 8b 73 28 48 8b 7b 40 ff 53 18 31 c0 e9 ?? ?? ?? ??";

    /// <summary>True when the module has the recognised definitions and handlers; changes nothing. Also true after
    /// <see cref="Apply"/> (the layout is still there; only the two severities differ).</summary>
    internal static bool Recognize(SceElf elf, CancellationToken cancel = default) => Locate(elf, cancel) is not null;

    /// <summary>Changes only warnings 4520/4521 after validating both existing silent callback handlers.</summary>
    internal static int Apply(SceElf elf, CancellationToken cancel = default)
    {
        if (Locate(elf, cancel) is not var (at, first, second)) return 0;
        cancel.ThrowIfCancellationRequested();
        // The callback branches are left intact; neither authentication nor save outcomes are changed.
        int count = (first == 0 ? 1 : 0) + (second == 0 ? 1 : 0);
        elf.Data[at + 22] = 2;
        elf.Data[at + 94] = 2;
        return count;
    }

    /// <summary>(definitions offset, first severity, second severity) of the recognised layout, or null.</summary>
    static (long At, byte First, byte Second)? Locate(SceElf elf, CancellationToken cancel)
    {
        var definitions = Find(elf, Definitions, cancel);
        if (definitions.Count != 1) return null;
        long at = definitions[0];
        long definitionClass = Relative(elf, at, 7, 3);
        long errorList = Relative(elf, at + 39, 7, 3);
        long allocation = Relative(elf, at + 7, 5, 1);
        long barrier = Relative(elf, at + 67, 5, 1);
        if (!Readable(elf, definitionClass) || !Readable(elf, errorList) || definitionClass == errorList ||
            definitionClass != Relative(elf, at + 72, 7, 3) || errorList != Relative(elf, at + 111, 7, 3) ||
            allocation != Relative(elf, at + 79, 5, 1) || barrier != Relative(elf, at + 139, 5, 1) ||
            !Executable(elf, allocation) || !Executable(elf, barrier)) return null;
        int firstField = Int32(elf, at + 35), secondField = Int32(elf, at + 107);
        if (firstField < 0 || firstField % 8 != 0 || secondField != (long)firstField + 8 ||
            firstField != Int32(elf, at + 56) || secondField != Int32(elf, at + 128)) return null;
        byte first = elf.Data[at + 22], second = elf.Data[at + 94];
        if (first is not (0 or 2) || second is not (0 or 2)) return null;
        var standalone = Find(elf, Standalone, cancel);
        var popup = Find(elf, Popup, cancel);
        if (standalone.Count != 1 || popup.Count != 1) return null;
        long handler = standalone[0], other = popup[0];
        long errorIdClass = Relative(elf, handler, 7, 3);
        long initialize = Relative(elf, handler + 20, 5, 1);
        long lookup = Relative(elf, handler + 28, 5, 1);
        if (!Readable(elf, errorIdClass) || errorIdClass == definitionClass || errorIdClass == errorList ||
            errorIdClass != Relative(elf, other, 7, 3) || initialize != Relative(elf, other + 20, 5, 1) ||
            lookup != Relative(elf, other + 28, 5, 1) || !Executable(elf, initialize) || !Executable(elf, lookup)) return null;
        return (at, first, second);
    }

    static int Int32(SceElf elf, long at) => BinaryPrimitives.ReadInt32LittleEndian(elf.Data.AsSpan((int)at, 4));
    static long Relative(SceElf elf, long at, int length, int displacement)
        => checked((long)elf.Vaddr(at) + length + Int32(elf, at + displacement));

    static bool Readable(SceElf elf, long address) => address >= 0 && elf.Phdrs.Any(p =>
        p.Type == SceElf.PtLoad && (p.Flags & 5) == 4 && (ulong)address >= p.Vaddr && (ulong)address + 8 <= p.Vaddr + (ulong)p.Filesz);

    static bool Executable(SceElf elf, long address) => address >= 0 && elf.Phdrs.Any(p =>
        p.Type == SceElf.PtLoad && (p.Flags & SceElf.PfX) != 0 && (ulong)address >= p.Vaddr && (ulong)address < p.Vaddr + (ulong)p.Filesz);

    static List<long> Find(SceElf elf, string signature, CancellationToken cancel)
    {
        byte?[] pattern = signature.Split(' ').Select(s => s == "??" ? (byte?)null : Convert.ToByte(s, 16)).ToArray();
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
                bool match = true;
                for (int i = 0; i < pattern.Length; i++)
                    if (pattern[i] is { } value && elf.Data[at + i] != value) { match = false; break; }
                if (match) hits.Add(at);
                at++;
            }
        }
        return hits;
    }
}
