using System.Buffers.Binary;

namespace Ps5Backport.Core;

public static partial class Imports
{
    /// <summary>Argument registers of the SysV calling convention, 32-bit forms, by argument index.</summary>
    static readonly string[] ArgumentRegisters = ["edi", "esi", "edx", "ecx", "r8d", "r9d"];

    /// <summary>
    /// Call sites of an import (call/jmp/jcc rel32 to its PLT entry) with the constant the game passes in argument
    /// <paramref name="argument"/> (0 = rdi … 5 = r9), when the last write to that register before the call is a readable
    /// constant: <c>mov r32, imm32</c>, <c>xor r32, r32</c> (0) or <c>push imm8; pop r64</c>. Null when the value is not a
    /// constant there (loaded from memory, computed, or written further away than <see cref="LookBack"/> bytes).
    /// A byte-pattern scan without disassembly: a constant found inside another instruction's bytes is a possible false
    /// positive, so callers should present the result as evidence, not proof. Sites inside the PLT itself are skipped.
    /// </summary>
    public static List<(ulong Site, uint? Constant)> ArgumentConstants(SceElf elf, Import import, int argument)
        => ArgumentConstants(elf, import, argument, default);

    public const int LookBack = 24;

    public static List<(ulong Site, uint? Constant)> ArgumentConstants(SceElf elf, Import import, int argument, CancellationToken cancel)
    {
        if (argument is < 0 or > 5)
            throw new ArgumentOutOfRangeException(nameof(argument), "SysV passes six integer arguments in registers");
        if (!import.Plt)
            return [];
        ulong plt = elf.ReadU64(import.GotVaddr) - 6;
        var result = new List<(ulong, uint?)>();
        foreach (var p in elf.Phdrs)
        {
            if (p.Type != SceElf.PtLoad || (p.Flags & SceElf.PfX) == 0)
                continue;
            var seg = elf.Data.AsSpan(checked((int)p.Offset), checked((int)p.Filesz));
            for (int i = 0; i + 5 <= seg.Length; i++)
            {
                if ((i & 65535) == 0) cancel.ThrowIfCancellationRequested();
                byte b = seg[i];
                int length;
                if (b is 0xE8 or 0xE9) length = 5;
                else if (b == 0x0F && i + 6 <= seg.Length && seg[i + 1] is >= 0x80 and <= 0x8F) length = 6;
                else continue;
                long target = (long)p.Vaddr + i + length + BinaryPrimitives.ReadInt32LittleEndian(seg[(i + length - 4)..]);
                if ((ulong)target != plt || p.Vaddr + (ulong)i == plt)
                    continue;
                result.Add((p.Vaddr + (ulong)i, ConstantBefore(seg, i, argument)));
            }
        }
        return result;
    }

    /// <summary>The constant written to the argument register by the closest recognised writer in the bytes before the call.</summary>
    static uint? ConstantBefore(ReadOnlySpan<byte> seg, int call, int argument)
    {
        // encodings of the writers for edi, esi, edx, ecx, r8d, r9d
        int reg = argument switch { 0 => 7, 1 => 6, 2 => 2, 3 => 1, 4 => 0, 5 => 1, _ => throw new ArgumentOutOfRangeException(nameof(argument)) };
        bool extended = argument >= 4;
        int start = Math.Max(0, call - LookBack);
        for (int at = call - 1; at >= start; at--)
        {
            // a closer write that is not a readable constant ends the search: mov/lea/movzx/movsxd/cmovcc into the register
            // (reg field), mov r/m32, r32 or arithmetic with the register as destination (rm field, mod = 11)
            int op = at, prefixLen = 0;
            if (0x40 <= seg[op] && seg[op] <= 0x4F && op + 1 < call) { op++; prefixLen = 1; }
            bool rexR = prefixLen == 1 && (seg[at] & 0x04) != 0, rexB = prefixLen == 1 && (seg[at] & 0x01) != 0;
            if (op + 1 < call)
            {
                byte opcode = seg[op], modrm = seg[op + 1];
                int regField = (modrm >> 3) & 7, rmField = modrm & 7, mod = modrm >> 6;
                bool regIsTarget = regField == reg && rexR == extended, rmIsTarget = mod == 3 && rmField == reg && rexB == extended;
                if (opcode == 0xC7 && rmIsTarget && op + 6 <= call)
                    return BinaryPrimitives.ReadUInt32LittleEndian(seg[(op + 2)..]);      // mov r32, imm32 (C7 /0 form)
                if ((opcode is 0x8B or 0x8D or 0x63) && regIsTarget) return null;
                if (opcode == 0x0F && op + 2 < call && (seg[op + 1] is 0xB6 or 0xB7 || (seg[op + 1] & 0xF0) == 0x40)
                    && ((seg[op + 2] >> 3) & 7) == reg && rexR == extended) return null;
                if ((opcode is 0x89 or 0x81 or 0x83 or 0x01 or 0x09 or 0x11 or 0x19 or 0x21 or 0x29 or 0x31 or 0x39 or 0x03 or 0x0B or 0x13 or 0x1B or 0x23 or 0x2B or 0x33) && rmIsTarget
                    && !(opcode == 0x31 && regField == reg))                                 // xor reg, reg is the constant 0 below
                    return null;
            }
            // mov r32, imm32: [41] B8+r imm32, ending anywhere before the call
            if (!extended && seg[at] == 0xB8 + reg && at + 5 <= call)
                return BinaryPrimitives.ReadUInt32LittleEndian(seg[(at + 1)..]);
            if (extended && seg[at] == 0x41 && at + 6 <= call && seg[at + 1] == 0xB8 + reg)
                return BinaryPrimitives.ReadUInt32LittleEndian(seg[(at + 2)..]);
            // xor r32, r32: [45] 31 (C0 | reg<<3 | reg)
            if (!extended && seg[at] == 0x31 && at + 2 <= call && seg[at + 1] == (0xC0 | reg << 3 | reg))
                return 0;
            if (extended && seg[at] == 0x45 && at + 3 <= call && seg[at + 1] == 0x31 && seg[at + 2] == (0xC0 | reg << 3 | reg))
                return 0;
            // push imm8; pop r64: 6A ib [41] 58+r
            if (!extended && seg[at] == 0x6A && at + 3 <= call && seg[at + 2] == 0x58 + reg)
                return (uint)(sbyte)seg[at + 1];
            if (extended && seg[at] == 0x6A && at + 4 <= call && seg[at + 2] == 0x41 && seg[at + 3] == 0x58 + reg)
                return (uint)(sbyte)seg[at + 1];
        }
        return null;
    }

    public static string ArgumentRegister(int argument) => ArgumentRegisters[argument];
}
