using System.Text;

namespace Ps5Backport.Core;

/// <summary>Details of an AMPRIDX3 file built for a game and its overlay.</summary>
public sealed record AmprIndexResult(string Path, int Entries, int OverlayEntries);

/// <summary>Builds ampr_emu's AMPRIDX3 path index for the final /app0 view: game files plus an overlay.</summary>
public static class AmprIndex
{
    sealed record Entry(string Path, byte[] Key, ulong Size, long Mtime, bool Overlay);

    static readonly HashSet<string> ExcludedRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "ampr_emu.index", "ampr_emu.index.tmp", "ampr_emu.index.bak", "ampr_commands.bin",
        "ampr_emu.log", "apr_emu.log", "backport_report.txt", "backport_report.json",
    };

    /// <summary>
    /// Creates <c>ampr_emu.index</c> in <paramref name="overlayRoot"/>. The index models the source game
    /// with files of the overlay taking precedence, exactly as the mounted /app0 tree will appear to AMPR.
    /// Generated indexes, logs and private backport reports are excluded.
    /// </summary>
    public static AmprIndexResult Build(string sourceRoot, string overlayRoot)
    {
        sourceRoot = System.IO.Path.GetFullPath(sourceRoot);
        overlayRoot = System.IO.Path.GetFullPath(overlayRoot);
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"Game folder not found: {sourceRoot}");
        if (!Directory.Exists(overlayRoot))
            throw new DirectoryNotFoundException($"Backport overlay not found: {overlayRoot}. Cook the backport first.");

        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        AddTree(sourceRoot, overlay: false, entries);
        AddTree(overlayRoot, overlay: true, entries);
        if (entries.Count == 0)
            throw new InvalidDataException("The game and overlay contain no files to index");

        List<Entry> rows = entries.Values.OrderBy(e => e.Key, ByteArrayComparer.Instance).ToList();
        string output = System.IO.Path.Combine(overlayRoot, "ampr_emu.index");
        Write(rows, output);
        return new AmprIndexResult(output, rows.Count, rows.Count(e => e.Overlay));
    }

    static void AddTree(string root, bool overlay, Dictionary<string, Entry> entries)
    {
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.IndexOf('/') < 0 && ExcludedRootFiles.Contains(relative))
                continue;
            string path = "/app0/" + relative;
            byte[] key = Key(path);
            string dictionaryKey = Convert.ToHexString(key);
            var info = new FileInfo(file);
            var entry = new Entry(path, key, checked((ulong)info.Length),
                                  Math.Max(0, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds()), overlay);
            if (entries.TryGetValue(dictionaryKey, out Entry? existing))
            {
                if (overlay && !existing.Overlay)
                {
                    entries[dictionaryKey] = entry;
                    continue;
                }
                throw new InvalidDataException($"Case-insensitive /app0 path collision: {existing.Path} <-> {path}");
            }
            entries.Add(dictionaryKey, entry);
        }
    }

    static void Write(List<Entry> rows, string output)
    {
        uint slots = HashSlotCount(rows.Count);
        var blob = new MemoryStream();
        var records = new MemoryStream();
        using (var recordWriter = new BinaryWriter(records, Encoding.UTF8, leaveOpen: true))
        {
            foreach (Entry row in rows)
            {
                byte[] path = Encoding.UTF8.GetBytes(row.Path);
                recordWriter.Write(checked((uint)blob.Length));
                recordWriter.Write((uint)path.Length);
                recordWriter.Write(row.Size);
                recordWriter.Write(row.Mtime);
                blob.Write(path);
                blob.WriteByte(0);
            }
        }

        ulong hashOffset = Align16(checked(48UL + (ulong)records.Length + (ulong)blob.Length));
        var table = new (ulong Hash, uint Index, uint Flags)[checked((int)slots)];
        uint mask = slots - 1;
        for (uint i = 0; i < (uint)rows.Count; i++)
        {
            ulong hash = Hash(rows[(int)i].Key);
            uint pos = (uint)hash & mask;
            bool duplicate = false;
            while (table[(int)pos].Index != 0)
            {
                if (table[(int)pos].Hash == hash)
                {
                    table[(int)pos].Flags |= 1;
                    duplicate = true;
                }
                pos = (pos + 1) & mask;
            }
            table[(int)pos] = (hash, i + 1, duplicate ? 1u : 0u);
        }

        string temp = output + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
        {
            writer.Write(Encoding.ASCII.GetBytes("AMPRIDX3"));
            writer.Write(3u);
            writer.Write(24u);
            writer.Write((ulong)rows.Count);
            writer.Write((ulong)blob.Length);
            writer.Write(hashOffset);
            writer.Write(16u);
            writer.Write(slots);
            writer.Write(records.ToArray());
            writer.Write(blob.ToArray());
            while ((ulong)stream.Position < hashOffset)
                writer.Write((byte)0);
            foreach (var slot in table)
            {
                writer.Write(slot.Hash);
                writer.Write(slot.Index);
                writer.Write(slot.Flags);
            }
        }
        File.Move(temp, output, overwrite: true);
    }

    static byte[] Key(string path)
    {
        byte[] key = Encoding.UTF8.GetBytes(path.Replace('\\', '/'));
        for (int i = 0; i < key.Length; i++)
            if (key[i] is >= (byte)'A' and <= (byte)'Z')
                key[i] += 0x20;
        return key;
    }

    static ulong Hash(byte[] key)
    {
        ulong hash = 1469598103934665603UL;
        foreach (byte b in key)
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return hash == 0 ? 1 : hash;
    }

    static uint HashSlotCount(int entries)
    {
        if (entries <= 0 || entries > int.MaxValue / 2)
            throw new InvalidDataException("Invalid AMPR index entry count");
        uint slots = 2;
        uint target = checked((uint)entries * 2);
        while (slots < target)
            slots <<= 1;
        return slots;
    }

    static ulong Align16(ulong value) => (value + 15UL) & ~15UL;

    sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();

        public int Compare(byte[]? left, byte[]? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            int length = Math.Min(left.Length, right.Length);
            for (int i = 0; i < length; i++)
                if (left[i] != right[i]) return left[i].CompareTo(right[i]);
            return left.Length.CompareTo(right.Length);
        }
    }
}
