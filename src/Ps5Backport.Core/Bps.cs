namespace Ps5Backport.Core;

/// <summary>
/// BPS patches (beat format), as shipped by BackPork for 10.01 libraries -> 6xx/7xx.
///
///     "BPS1" varint(source_size) varint(target_size) varint(metadata_size) metadata
///     actions...  source_crc32 target_crc32 patch_crc32   (little-endian u32 each)
///
/// Actions: varint(((length - 1) &lt;&lt; 2) | command). <see cref="Edits"/> reads the literal writes
/// without the source file, so a patch can be inspected before having the library it applies to.
/// </summary>
public static class Bps
{
    public enum Command { SourceRead = 0, TargetRead = 1, SourceCopy = 2, TargetCopy = 3 }

    /// <param name="Payload">Literal bytes for TargetRead.</param>
    /// <param name="Delta">Cursor delta for SourceCopy / TargetCopy.</param>
    public sealed record Action(Command Command, long Length, byte[]? Payload, long Delta);

    public sealed record Patch(long SourceSize, long TargetSize, byte[] Metadata, IReadOnlyList<Action> Actions,
                               uint SourceCrc, uint TargetCrc);

    /// <param name="Kind">"write" (literal bytes), "source-copy" or "target-copy".</param>
    public sealed record Edit(long Offset, string Kind, long Length, byte[] Data, long FromOffset);

    static ReadOnlySpan<byte> Magic => "BPS1"u8;

    static long Varint(ReadOnlySpan<byte> data, ref int pos)
    {
        long value = 0, shift = 1;
        while (true)
        {
            byte x = data[pos++];
            value += (x & 0x7F) * shift;
            if ((x & 0x80) != 0)
                return value;
            shift <<= 7;
            value += shift;
        }
    }

    static long Signed(ReadOnlySpan<byte> data, ref int pos)
    {
        long v = Varint(data, ref pos);
        return (v & 1) != 0 ? -(v >> 1) : v >> 1;
    }

    public static Patch Parse(byte[] data)
    {
        var d = data.AsSpan();
        if (d.Length < 4 || !d[..4].SequenceEqual(Magic))
            throw new InvalidDataException("not a BPS patch");
        if (d.Length < 16)
            throw new InvalidDataException("BPS patch truncated");
        uint sourceCrc = BitConverter.ToUInt32(data, d.Length - 12);
        uint targetCrc = BitConverter.ToUInt32(data, d.Length - 8);
        uint patchCrc = BitConverter.ToUInt32(data, d.Length - 4);
        if (Crc32.Compute(d[..^4]) != patchCrc)
            throw new InvalidDataException("BPS patch checksum mismatch (corrupt file)");
        int pos = 4;
        long sourceSize = Varint(d, ref pos);
        long targetSize = Varint(d, ref pos);
        int metaSize = (int)Varint(d, ref pos);
        byte[] metadata = d.Slice(pos, metaSize).ToArray();
        pos += metaSize;
        var actions = new List<Action>();
        int end = d.Length - 12;
        while (pos < end)
        {
            long v = Varint(d, ref pos);
            var command = (Command)(v & 3);
            long length = (v >> 2) + 1;
            switch (command)
            {
                case Command.TargetRead:
                    actions.Add(new Action(command, length, d.Slice(pos, (int)length).ToArray(), 0));
                    pos += (int)length;
                    break;
                case Command.SourceCopy or Command.TargetCopy:
                    actions.Add(new Action(command, length, null, Signed(d, ref pos)));
                    break;
                default:
                    actions.Add(new Action(command, length, null, 0));
                    break;
            }
        }
        return new Patch(sourceSize, targetSize, metadata, actions, sourceCrc, targetCrc);
    }

    public static Patch Load(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>Every region of the output that is not an in-place source read, in output order.</summary>
    public static List<Edit> Edits(Patch patch)
    {
        var edits = new List<Edit>();
        long pos = 0, src = 0, tgt = 0;
        foreach (var a in patch.Actions)
        {
            switch (a.Command)
            {
                case Command.TargetRead:
                    edits.Add(new Edit(pos, "write", a.Length, a.Payload!, 0));
                    break;
                case Command.SourceCopy:
                    src += a.Delta;
                    if (src != pos)
                        edits.Add(new Edit(pos, "source-copy", a.Length, [], src));
                    src += a.Length;
                    break;
                case Command.TargetCopy:
                    tgt += a.Delta;
                    edits.Add(new Edit(pos, "target-copy", a.Length, [], tgt));
                    tgt += a.Length;
                    break;
            }
            pos += a.Length;
        }
        return edits;
    }

    /// <summary>Patched bytes. Checks the source and result CRC32 so a wrong input library is refused.</summary>
    public static byte[] Apply(Patch patch, byte[] source)
    {
        uint crc = Crc32.Compute(source);
        if (source.Length != patch.SourceSize || crc != patch.SourceCrc)
            throw new InvalidDataException(
                $"source does not match the patch (needs {patch.SourceSize} bytes, CRC32 {patch.SourceCrc:x8}; " +
                $"got {source.Length} bytes, CRC32 {crc:x8})");
        var output = new byte[patch.TargetSize];
        long n = 0, src = 0, tgt = 0;
        foreach (var a in patch.Actions)
        {
            if (n + a.Length > output.Length)
                throw new InvalidDataException("patch actions run past the target size (corrupt patch)");
            switch (a.Command)
            {
                case Command.SourceRead:
                    Array.Copy(source, n, output, n, a.Length);
                    break;
                case Command.TargetRead:
                    a.Payload!.CopyTo(output, n);
                    break;
                case Command.SourceCopy:
                    src += a.Delta;
                    Array.Copy(source, src, output, n, a.Length);
                    src += a.Length;
                    break;
                case Command.TargetCopy:
                    tgt += a.Delta;
                    for (long i = 0; i < a.Length; i++)      // byte by byte: the copy may overlap its own output
                        output[n + i] = output[tgt++];
                    break;
            }
            n += a.Length;
        }
        if (n != patch.TargetSize || Crc32.Compute(output) != patch.TargetCrc)
            throw new InvalidDataException("patched output does not match the patch target CRC32");
        return output;
    }
}

/// <summary>CRC-32 (IEEE 802.3, as zlib.crc32).</summary>
public static class Crc32
{
    static readonly uint[] Table = BuildTable();

    static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}
