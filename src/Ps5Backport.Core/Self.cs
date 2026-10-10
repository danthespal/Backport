using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Ps5Backport.Core;

/// <summary>
/// SELF (fake-signed ELF) detection, unwrap and fake-sign (a port of make_fself.py's signer and
/// make_fself_batch.py's unpacker).
///
/// Unwrapping keeps the original ELF header from inside the SELF. With it kept, unwrap -> sign
/// reproduces GT7's shipped files byte for byte, except the ex-info digest (a SHA-256 of the
/// whole original ELF, which can't be recomputed when the original had data outside its segments).
/// </summary>
public static class Self
{
    public const string KindElf = "ELF";
    public const string KindPs4Self = "PS4-style fake SELF (make_fself.py)";
    public const string KindPs5Self = "PS5-style SELF";
    public const string KindUnknown = "unknown";

    static readonly byte[] Ps4Magic = [0x4F, 0x15, 0x3D, 0x1D];
    static readonly byte[] Ps5Magic = [0x54, 0x14, 0xF5, 0xEE];

    public const ulong DefaultPaid = 0x3100000000000002;
    public const ulong PtypeFake = 0x1;

    /// <summary>SELF segment-entry flag (PS5-SELF-Decrypter include/self.h).</summary>
    public const ulong SegmentEncrypted = 1 << 1;

    const ulong PropsSigned = 1 << 2, PropsCompressed = 1 << 3, PropsHasBlocks = 1 << 11, PropsHasDigests = 1 << 16;
    const int PropsBlockSizeShift = 12, PropsSegmentIndexShift = 20;
    const ulong PropsSegmentIndexMask = 0xFFFF;
    const int BlockSize = 0x4000, DigestSize = 0x20, SignatureSize = 0x100;
    const int ExInfoSize = 0x40, NpdrmBlockSize = 0x30, MetaBlockSize = 0x50, MetaFooterSize = 0x50;
    const uint PtSceRelro = 0x61000010, PtSceDynlibData = 0x61000000, PtSceComment = 0x6FFFFF00;

    // --- detection -----------------------------------------------------------
    public static string Kind(ReadOnlySpan<byte> head)
    {
        if (head.Length < 4)
            return KindUnknown;
        if (head[..4].SequenceEqual(SceElf.Magic))
            return KindElf;
        if (head[..4].SequenceEqual(Ps4Magic))
            return KindPs4Self;
        return head[..4].SequenceEqual(Ps5Magic) ? KindPs5Self : KindUnknown;
    }

    public static string FileKind(string path)
    {
        Span<byte> head = stackalloc byte[4];
        using var f = File.OpenRead(path);
        int n = f.ReadAtLeast(head, 4, throwOnEndOfStream: false);
        return Kind(head[..n]);
    }

    public static bool IsSelf(string kind) => kind is KindPs4Self or KindPs5Self;

    /// <summary>
    /// Number of SELF segment entries flagged as encrypted. Fake-signed files have none; a real
    /// (retail) SELF must be decrypted on a console first (PS5-SELF-Decrypter).
    /// </summary>
    public static int EncryptedSegments(ReadOnlySpan<byte> self)
    {
        if (self.Length < 0x20 || !IsSelf(Kind(self)))
            throw new InvalidDataException("invalid or truncated SELF header");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(self[0x18..]);
        if (count > (self.Length - 0x20) / 0x20)
            throw new InvalidDataException("SELF entry table extends past end of file");
        int encrypted = 0;
        for (int i = 0; i < count; i++)
            if ((BinaryPrimitives.ReadUInt64LittleEndian(self[(0x20 + i * 0x20)..]) & SegmentEncrypted) != 0)
                encrypted++;
        return encrypted;
    }

    public static int EncryptedSegments(string path)
    {
        using var f = File.OpenRead(path);
        var head = new byte[0x20];
        f.ReadExactly(head);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(0x18));
        var all = new byte[0x20 + count * 0x20];
        head.CopyTo(all, 0);
        f.ReadExactly(all, 0x20, count * 0x20);
        return EncryptedSegments(all);
    }

    /// <summary>File offset of the 32-byte ELF digest in a make_fself SELF's ex-info.</summary>
    public static int DigestOffset(ReadOnlySpan<byte> self)
    {
        _ = EncryptedSegments(self);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(self[0x18..]);
        int ehdr = 0x20 + count * 0x20;
        if (self.Length - ehdr < 0x40)
            throw new InvalidDataException("embedded ELF header is truncated");
        int phnum = BinaryPrimitives.ReadUInt16LittleEndian(self[(ehdr + 0x38)..]);
        int exInfo = (ehdr + 0x40 + phnum * 0x38 + 15) & ~15;
        if (exInfo > self.Length - 0x40)
            throw new InvalidDataException("SELF digest extends past end of file");
        return exInfo + 0x20;
    }

    // --- unwrap --------------------------------------------------------------
    /// <summary>SELF -> ELF, keeping the original ELF header area (make_fself_batch unpack_fself_file).</summary>
    public static byte[] Unwrap(byte[] self) => Unwrap(self, Array.MaxLength);

    /// <summary>Unwraps with a caller-supplied allocation limit, checked before allocating the ELF output.</summary>
    public static byte[] Unwrap(byte[] self, int maximumOutputBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumOutputBytes);
        var s = self.AsSpan();
        long actual = self.Length;
        if (actual < 0x20)
            throw new InvalidDataException("truncated SELF header");
        if (Kind(s) is not (KindPs4Self or KindPs5Self))
            throw new InvalidDataException("invalid SELF magic");
        if (s[6] != 0x01)                                   // common header: magic, version, mode, endian, attributes
            throw new InvalidDataException($"unsupported SELF endian: 0x{s[6]:X2}");
        ulong declaredSize = U64(s, 0x10);
        int numEntries = U16(s, 0x18);
        long entriesEnd = 0x20 + numEntries * 0x20L;
        if (entriesEnd > actual)
            throw new InvalidDataException("SELF entry table extends past end of file");
        if (U16(s, 0x0C) > actual)
            throw new InvalidDataException("SELF header size extends past end of file");
        // a declared size beyond the file is tolerated: every entry is still checked to lie inside it
        // (re-signing tools cut a few bytes off: Kena's dump)

        var entries = new (ulong Props, long Offset, long Filesz)[numEntries];
        for (int i = 0; i < numEntries; i++)
        {
            int at = 0x20 + i * 0x20;
            ulong offset = U64(s, at + 8), filesz = U64(s, at + 0x10);
            if (offset > (ulong)actual || filesz > (ulong)actual - offset)
                throw new InvalidDataException($"SELF entry #{i} points outside the file");
            entries[i] = (U64(s, at), (long)offset, (long)filesz);
        }

        int elfOffset = (int)entriesEnd;
        var ehdr = ElfHeader.Parse(s[elfOffset..]);
        var phdrs = new (uint Type, long Offset, long Filesz)[ehdr.Phnum];
        for (int i = 0; i < ehdr.Phnum; i++)
        {
            long at = elfOffset + (long)ehdr.Phoff + i * (long)ehdr.Phentsize;
            if (at > actual || ehdr.Phentsize > actual - at)
                throw new InvalidDataException($"embedded ELF program header #{i} is truncated");
            ulong offset = U64(s, (int)at + 8), filesz = U64(s, (int)at + 0x20);
            if (offset > (ulong)Array.MaxLength || filesz > (ulong)Array.MaxLength - offset)
                throw new InvalidDataException($"embedded ELF segment #{i} exceeds the supported output size");
            phdrs[i] = (U32(s, (int)at), (long)offset, (long)filesz);
        }

        long headerSize = ehdr.HeaderAreaSize;
        if (headerSize > actual - elfOffset)
            throw new InvalidDataException("embedded ELF header area is truncated");
        long outputSize = headerSize;
        foreach (var p in phdrs)
            if (p.Filesz != 0)
                outputSize = Math.Max(outputSize, p.Offset + p.Filesz);
        if (outputSize > maximumOutputBytes)
            throw new InvalidDataException("SELF output exceeds the configured byte limit");
        var output = new byte[checked((int)outputSize)];
        var headerArea = s.Slice(elfOffset, (int)headerSize);
        headerArea.CopyTo(output);

        for (int i = 0; i < numEntries; i++)
        {
            var e = entries[i];
            if ((e.Props & PropsHasBlocks) == 0)
                continue;
            if ((e.Props & (SegmentEncrypted | PropsCompressed)) != 0)
                throw new InvalidDataException($"SELF entry #{i} is encrypted/compressed; decrypt the file first (PS5-SELF-Decrypter)");
            int segment = (int)((e.Props >> PropsSegmentIndexShift) & PropsSegmentIndexMask);
            if (segment >= phdrs.Length)
                throw new InvalidDataException($"SELF entry #{i} has invalid segment index {segment}");
            var p = phdrs[segment];
            if (e.Filesz != p.Filesz)
                throw new InvalidDataException($"SELF entry #{i} size 0x{e.Filesz:X} does not match ELF segment #{segment} size 0x{p.Filesz:X}");
            output = Place(output, p.Offset, s.Slice((int)e.Offset, (int)e.Filesz));
        }

        var versions = Enumerable.Range(0, phdrs.Length).Where(i => phdrs[i].Type == SceElf.PtSceVersion && phdrs[i].Filesz > 0).ToList();
        if (versions.Count > 1)
            throw new InvalidDataException("multiple PT_SCE_VERSION segments are not supported");
        if (versions.Count == 1)
        {
            var p = phdrs[versions[0]];
            long dataEnd = entries.Length == 0 ? entriesEnd : entries.Max(e => e.Offset + e.Filesz);
            long tail = actual - p.Filesz;
            // 1) name:-records at the end of the file, or around the declared size when a signer appended data after
            //    the block (Borderlands 4's backport: 0x1E8 bytes; the packer's alignment puts it up to 0xF before)
            long start = tail >= 0 && IsCompleteSceVersion(s.Slice((int)tail, (int)p.Filesz)) ? tail : -1;
            for (long at = Math.Max(0, (long)declaredSize - 0xF); start < 0 && at <= Math.Min((long)declaredSize + 0xF, tail); at++)
                if (IsCompleteSceVersion(s.Slice((int)at, (int)p.Filesz)))
                    start = at;
            ReadOnlySpan<byte> version =
                start >= 0 ? s.Slice((int)start, (int)p.Filesz)
                // 2) otherwise the bytes after the last segment, as they are: not every block holds records (Sony system
                //    modules: a build-path string; Unity's PS5Util.prx: opaque bytes; re-signing tools: zeros)
                : tail >= dataEnd ? s.Slice((int)tail, (int)p.Filesz)
                // 3) nothing after the last segment (a re-signing tool cut the block off: Kena's dump): zeros;
                //    the loader does not check this segment
                : new byte[p.Filesz];
            output = Place(output, p.Offset, version);
        }

        // the header area last: a PT_LOAD at file offset 0 must not overwrite it
        headerArea.CopyTo(output);
        return output;
    }

    static byte[] Place(byte[] output, long offset, ReadOnlySpan<byte> data)
    {
        long end = offset + data.Length;
        if (end > output.Length)
            Array.Resize(ref output, checked((int)end));
        data.CopyTo(output.AsSpan((int)offset));
        return output;
    }

    /// <summary>Trailing PT_SCE_VERSION records: 00 00, u16 size, 0x08, "name:", two 8-byte versions.</summary>
    static bool IsCompleteSceVersion(ReadOnlySpan<byte> records)
    {
        int offset = 0, count = 0;
        while (offset < records.Length)
        {
            if (records.Length - offset < 5 || records[offset] != 0 || records[offset + 1] != 0)
                return false;
            int payload = U16(records, offset + 2);
            int size = payload + 4;
            if (payload < 18 || size > records.Length - offset || records[offset + 4] != 8)
                return false;
            int nameSize = payload - 17;
            var name = records.Slice(offset + 5, nameSize);
            if (name.Length == 0 || name[^1] != (byte)':')
                return false;
            foreach (byte b in name)
                if (b < 0x20 || b > 0x7E)
                    return false;
            // the two 8-byte versions after the name are not compared: they differ in real eboots
            // (Ghost of Yotei: libScePsmlMfsr2), which upstream make_fself rejected as malformed
            offset += size;
            count++;
        }
        return count != 0;
    }

    // --- sign ----------------------------------------------------------------
    /// <summary>ELF -> fake-signed SELF with make_fself.py defaults (as used for GT7).</summary>
    public static byte[] Sign(byte[] elf, ulong paid = DefaultPaid, ulong ptype = PtypeFake, ulong appVersion = 0, ulong fwVersion = 0)
    {
        var e = elf.AsSpan();
        var ehdr = ElfHeader.Parse(e);

        // segments as make_fself reads them (reads past EOF come back short)
        var phdrs = new (uint Type, long Offset, long Filesz)[ehdr.Phnum];
        var segments = new ReadOnlyMemory<byte>[ehdr.Phnum];
        ReadOnlyMemory<byte> versionData = default;
        for (int i = 0; i < ehdr.Phnum; i++)
        {
            int at = checked((int)(ehdr.Phoff + (ulong)(i * ehdr.Phentsize)));
            ulong offsetValue = U64(e, at + 8), sizeValue = U64(e, at + 0x20);
            if (offsetValue > long.MaxValue || sizeValue > (ulong)Array.MaxLength)
                throw new InvalidDataException($"ELF segment #{i} has an unsupported offset or size");
            phdrs[i] = (U32(e, at), (long)offsetValue, (long)sizeValue);
            if (phdrs[i].Filesz > 0)
            {
                var clipped = Clip(e, phdrs[i].Offset, phdrs[i].Filesz);
                segments[i] = elf.AsMemory(checked((int)Math.Min(phdrs[i].Offset, elf.Length)), clipped.Length);
            }
            if (phdrs[i].Type == SceElf.PtSceVersion)
                versionData = segments[i];
        }
        bool Signed(uint type) => type is SceElf.PtLoad or PtSceRelro or PtSceDynlibData or PtSceComment;
        int[] signedSegments = Enumerable.Range(0, ehdr.Phnum).Where(i => Signed(phdrs[i].Type)).ToArray();
        int numEntries = signedSegments.Length * 2;

        int elfHeaderArea = Align16((int)ehdr.HeaderAreaSize);
        int headerSize = 0x20 + numEntries * 0x20 + elfHeaderArea + ExInfoSize + NpdrmBlockSize;
        int metaSize = numEntries * MetaBlockSize + MetaFooterSize + SignatureSize;
        if (headerSize > ushort.MaxValue || metaSize > ushort.MaxValue)
            throw new InvalidDataException("too many segments for a SELF header");

        // entries: a digest (meta) entry and a data entry per signed segment
        var entries = new (ulong Props, long Offset, long Size, ReadOnlyMemory<byte> Data)[numEntries];
        long offset = headerSize + metaSize;
        for (int k = 0; k < signedSegments.Length; k++)
        {
            int i = signedSegments[k];
            long blocks = (phdrs[i].Filesz + BlockSize - 1) / BlockSize;
            var digests = new byte[blocks * DigestSize];
            ulong metaProps = PropsSigned | PropsHasDigests | ((ulong)(2 * k + 1) << PropsSegmentIndexShift);
            entries[2 * k] = (metaProps, offset, digests.Length, digests);
            offset = Align16(offset + digests.Length);
            ulong dataProps = PropsSigned | PropsHasBlocks | (2UL << PropsBlockSizeShift) | ((ulong)i << PropsSegmentIndexShift);
            entries[2 * k + 1] = (dataProps, offset, phdrs[i].Filesz, segments[i]);
            offset = Align16(offset + phdrs[i].Filesz);
        }
        long fileSize = offset;

        var output = new byte[checked((int)fileSize + versionData.Length)];
        var o = output.AsSpan();
        // common + extended header
        Ps4Magic.CopyTo(o);
        o[4] = 0x00; o[5] = 0x01; o[6] = 0x01; o[7] = 0x12;                 // version, mode, endian, attributes
        BinaryPrimitives.WriteUInt32LittleEndian(o[0x08..], 0x101);           // key type
        BinaryPrimitives.WriteUInt16LittleEndian(o[0x0C..], (ushort)headerSize);
        BinaryPrimitives.WriteUInt16LittleEndian(o[0x0E..], (ushort)metaSize);
        BinaryPrimitives.WriteUInt64LittleEndian(o[0x10..], (ulong)fileSize);
        BinaryPrimitives.WriteUInt16LittleEndian(o[0x18..], (ushort)numEntries);
        BinaryPrimitives.WriteUInt16LittleEndian(o[0x1A..], 0x2 | (2 << 4));  // flags: 2 signed blocks
        for (int j = 0; j < numEntries; j++)
        {
            var entry = o[(0x20 + j * 0x20)..];
            BinaryPrimitives.WriteUInt64LittleEndian(entry, entries[j].Props);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], (ulong)entries[j].Offset);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[0x10..], (ulong)entries[j].Size);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[0x18..], (ulong)entries[j].Size);
        }

        // embedded ELF header (padding bytes 9..14 cleared, no section headers) and program headers
        int elfOffset = 0x20 + numEntries * 0x20;
        e[..0x40].CopyTo(o[elfOffset..]);
        o.Slice(elfOffset + 9, 6).Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(o[(elfOffset + 0x3C)..], 0);
        for (int i = 0; i < ehdr.Phnum; i++)
        {
            int at = (int)ehdr.Phoff + i * ehdr.Phentsize;
            e.Slice(at, 0x38).CopyTo(o[(elfOffset + at)..]);
        }

        // ex-info, NPDRM control block, meta blocks (zero), meta footer, signature (zero)
        int exInfo = elfOffset + elfHeaderArea;
        BinaryPrimitives.WriteUInt64LittleEndian(o[exInfo..], paid);
        BinaryPrimitives.WriteUInt64LittleEndian(o[(exInfo + 8)..], ptype);
        BinaryPrimitives.WriteUInt64LittleEndian(o[(exInfo + 0x10)..], appVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(o[(exInfo + 0x18)..], fwVersion);
        SHA256.HashData(e, o.Slice(exInfo + 0x20, DigestSize));
        BinaryPrimitives.WriteUInt16LittleEndian(o[(exInfo + ExInfoSize)..], 0x3);   // NPDRM control block type
        int footer = exInfo + ExInfoSize + NpdrmBlockSize + numEntries * MetaBlockSize;
        BinaryPrimitives.WriteUInt32LittleEndian(o[(footer + 0x30)..], 0x10000);

        foreach (var entry in entries)
            entry.Data.Span.CopyTo(o[(int)entry.Offset..]);
        versionData.Span.CopyTo(o[(int)fileSize..]);
        return output;
    }

    // --- file helpers ----------------------------------------------------------
    public static void UnwrapFile(string selfPath, string elfPath) => UnwrapFile(selfPath, elfPath, overwrite: true);

    /// <summary>Unwraps with an explicit destination replacement policy.</summary>
    public static void UnwrapFile(string selfPath, string elfPath, bool overwrite) => WriteAtomic(elfPath, Unwrap(File.ReadAllBytes(selfPath)), overwrite);

    public static void SignFile(string elfPath, string selfPath) => SignFile(elfPath, selfPath, overwrite: true);

    /// <summary>Signs with an explicit destination replacement policy.</summary>
    public static void SignFile(string elfPath, string selfPath, bool overwrite) => WriteAtomic(selfPath, Sign(File.ReadAllBytes(elfPath)), overwrite);

    /// <summary>Writes through a temp file so a failure never leaves a truncated output.</summary>
    public static void WriteAtomic(string path, byte[] data) => WriteAtomic(path, data, overwrite: true);

    /// <summary>Writes atomically; create-only publication refuses a destination created by another writer.</summary>
    public static void WriteAtomic(string path, byte[] data, bool overwrite)
    {
        string full = Path.GetFullPath(path);
        string tmp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(data);
            File.Move(tmp, full, overwrite);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }

    // --- helpers ---------------------------------------------------------------
    /// <summary>The 64-byte ELF header fields make_fself checks.</summary>
    readonly record struct ElfHeader(ulong Phoff, int Ehsize, int Phentsize, int Phnum)
    {
        public long HeaderAreaSize => Math.Max(Ehsize, (long)Phoff + Phentsize * (long)Phnum);

        public static ElfHeader Parse(ReadOnlySpan<byte> h)
        {
            if (h.Length < 0x40 || !h[..4].SequenceEqual(SceElf.Magic))
                throw new InvalidDataException("invalid ELF magic");
            if (h[4] != 2 || h[5] != 1)
                throw new InvalidDataException("unsupported ELF class or data encoding");
            if (U16(h, 0x12) != 0x3E || U32(h, 0x14) != 1)
                throw new InvalidDataException("unsupported ELF machine type or version");
            int shentsize = U16(h, 0x3A);
            if (U16(h, 0x36) != 0x38 || (shentsize > 0 && shentsize != 0x40))
                throw new InvalidDataException("unsupported ELF header entry size");
            if (U16(h, 0x10) is not (0x2 or 0xFE00 or 0xFE10 or 0xFE18))
                throw new InvalidDataException("unsupported ELF type");
            ulong phoff = U64(h, 0x20);
            int phnum = U16(h, 0x38), ehsize = U16(h, 0x34);
            if (ehsize < 0x40 || ehsize > h.Length || phoff > (ulong)h.Length || (ulong)phnum * 0x38 > (ulong)h.Length - phoff)
                throw new InvalidDataException("ELF header area is truncated or invalid");
            return new ElfHeader(phoff, ehsize, U16(h, 0x36), phnum);
        }
    }

    static ReadOnlySpan<byte> Clip(ReadOnlySpan<byte> data, long offset, long size)
    {
        if (offset >= data.Length)
            return [];
        return data.Slice((int)offset, (int)Math.Min(size, data.Length - offset));
    }

    static int Align16(int x) => (x + 15) & ~15;
    static long Align16(long x) => (x + 15) & ~15L;
    static ushort U16(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt16LittleEndian(s[at..]);
    static uint U32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt32LittleEndian(s[at..]);
    static ulong U64(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt64LittleEndian(s[at..]);
}
