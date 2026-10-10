using System.Buffers.Binary;
using System.Text;
using Ps5Backport.Core;

namespace Ps5Backport.Tests;

/// <summary>Small format-correct fixtures generated from public format fields; no private binaries.</summary>
internal static class SyntheticBinary
{
    internal const string Nid = "XXXXXXXXXXX";

    internal static byte[] Elf(byte[]? payload = null, bool imports = false, bool exports = false)
    {
        var data = new byte[0x500 + (payload?.Length ?? 0)];
        SceElf.Magic.CopyTo(data); data[4] = 2; data[5] = 1;
        W16(data, 0x10, SceElf.EtSceExec); W16(data, 0x12, 0x3E); W32(data, 0x14, 1);
        W64(data, 0x20, 0x40); W16(data, 0x34, 0x40); W16(data, 0x36, 0x38); W16(data, 0x38, 3);
        Header(0, SceElf.PtLoad, 0, (ulong)data.Length, SceElf.PfX);
        Header(1, SceElf.PtDynamic, 0x100, 0x80, 0);
        Header(2, SceElf.PtSceModuleParam, 0x180, 0x18, 0);
        var tags = new (ulong, ulong)[]
        {
            (SceElf.DtStrtab, 0x200), (SceElf.DtSymtab, 0x280), (SceElf.DtSceSymtabSz, 72),
            (SceElf.DtRela, 0x300), (SceElf.DtRelaSz, imports ? 24UL : 0),
            (SceElf.DtSceExportLib, 0), (0, 0),
        };
        for (int i = 0; i < tags.Length; i++) { W64(data, 0x100 + i * 16, tags[i].Item1); W64(data, 0x108 + i * 16, tags[i].Item2); }
        Encoding.ASCII.GetBytes(Nid + "#A#A\0").CopyTo(data, 0x201);
        // Symbol 1 is an unresolved function import; symbol 2 defines the same NID in the module's own library.
        W32(data, 0x298, 1); data[0x29C] = SceElf.SttFunc;
        W32(data, 0x2B0, 1); data[0x2B4] = SceElf.SttFunc;
        W16(data, 0x2B6, exports ? (ushort)1 : (ushort)0); W64(data, 0x2B8, exports ? 0x400UL : 0);
        W64(data, 0x300, 0x480); W64(data, 0x308, (1UL << 32) | 1);
        payload?.CopyTo(data, 0x500);
        return data;

        void Header(int index, uint type, ulong offset, ulong size, uint flags)
        {
            int at = 0x40 + index * 0x38;
            W32(data, at, type); W32(data, at + 4, flags); W64(data, at + 8, offset);
            W64(data, at + 0x10, offset); W64(data, at + 0x20, size); W64(data, at + 0x28, size);
        }
    }

    internal static byte[] LiteralPatch(byte[] source, byte[] target)
    {
        var body = new List<byte>("BPS1"u8.ToArray());
        Varint(source.Length); Varint(target.Length); Varint(0);
        if (target.Length > 0) { Varint(((target.Length - 1L) << 2) | 1); body.AddRange(target); }
        body.AddRange(BitConverter.GetBytes(Crc32.Compute(source)));
        body.AddRange(BitConverter.GetBytes(Crc32.Compute(target)));
        body.AddRange(BitConverter.GetBytes(Crc32.Compute(body.ToArray())));
        return body.ToArray();

        void Varint(long value)
        {
            while (true)
            {
                byte next = (byte)(value & 127); value >>= 7;
                if (value == 0) { body.Add((byte)(next | 128)); return; }
                body.Add(next); value--;
            }
        }
    }

    internal static void W16(byte[] b, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), value);
    internal static void W32(byte[] b, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), value);
    internal static void W64(byte[] b, int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at), value);
}
