using System.Buffers.Binary;
using Ps5Backport.Core;

namespace DecoderEvaluation;

internal static class Fixtures
{
    internal const ulong Start = 0x1000, Got = 0x10000000;
    internal const string Nid = "XXXXXXXXXXX";
    internal static ImportStatus Status(bool plt = false) => new(Nid, "synthetic", Got, plt, Imports.Missing);

    internal static SceElf Elf(byte[] code, ulong start = Start, ulong? lazyPlt = null)
    {
        byte[] data = new byte[0x200 + code.Length + 8];
        SceElf.Magic.CopyTo(data); data[4] = 2; data[5] = 1;
        W16(0x10, SceElf.EtSceExec); W16(0x12, 0x3E); W32(0x14, 1);
        W64(0x18, start); W64(0x20, 0x40); W16(0x34, 0x40); W16(0x36, 0x38); W16(0x38, 2);
        Header(0x40, SceElf.PfX, 0x200, start, (ulong)code.Length);
        Header(0x78, 0, (ulong)(0x200 + code.Length), Got, 8);
        code.CopyTo(data, 0x200);
        if (lazyPlt is { } value) W64(0x200 + code.Length, value);
        return new(data);
        void W16(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at), value);
        void W32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), value);
        void W64(int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(at), value);
        void Header(int at, uint flags, ulong offset, ulong address, ulong size)
        { W32(at, SceElf.PtLoad); W32(at + 4, flags); W64(at + 8, offset); W64(at + 16, address); W64(at + 32, size); W64(at + 40, size); }
    }

    internal static byte[] Reference(byte[] prefix, int offset = 0, ulong start = Start, ulong target = Got, byte[]? suffix = null)
    {
        var code = new byte[prefix.Length + 4 + (suffix?.Length ?? 0)];
        prefix.CopyTo(code, 0);
        ulong next = start + (ulong)(offset + prefix.Length + 4);
        BinaryPrimitives.WriteInt32LittleEndian(code.AsSpan(prefix.Length), unchecked((int)(target - next)));
        suffix?.CopyTo(code, prefix.Length + 4);
        return code;
    }
}
