using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Ps5Backport.Core;

/// <summary>NIDs: computation and symbol-name parsing.</summary>
public static class Nid
{
    static readonly byte[] Suffix = Convert.FromHexString("518D64A635DED8C1E6B039B1C3E55230");
    public const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+-";

    /// <summary>11-character NID of a symbol name (SHA-1 with suffix, first 8 bytes reversed, custom base64).</summary>
    public static string Compute(string name)
    {
        byte[] input = [.. Encoding.UTF8.GetBytes(name), .. Suffix];
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(SHA1.HashData(input));
        Span<byte> be = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(be, value);
        return Convert.ToBase64String(be).TrimEnd('=').Replace('/', '-');
    }

    /// <summary>Library/module ids in symbol names are base-64 digits: 's' = 44, 'BM' = 76.</summary>
    public static int IdFromChars(string chars)
    {
        int n = 0;
        foreach (char c in chars)
        {
            int digit = Alphabet.IndexOf(c);
            if (digit < 0)
                throw new FormatException($"'{c}' is not a NID alphabet character");
            n = n * 64 + digit;
        }
        return n;
    }

    /// <summary>'&lt;NID&gt;#&lt;lib&gt;#&lt;mod&gt;' -> (nid, lib id, module id), or null for a non-NID name.</summary>
    public static (string Nid, int Library, int Module)? SplitSymbol(string symbol)
    {
        string[] parts = symbol.Split('#');
        if (parts.Length != 3 || parts[0].Length != 11)
            return null;
        return (parts[0], IdFromChars(parts[1]), IdFromChars(parts[2]));
    }
}

/// <summary>NID -> name lookup from aerolib.csv ('&lt;NID&gt; &lt;name&gt;' per line).</summary>
public sealed class NameDb
{
    readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);

    public NameDb() { }

    public NameDb(string path)
    {
        foreach (string line in File.ReadLines(path))
        {
            string trimmed = line.TrimStart();
            int space = trimmed.IndexOfAny([' ', '\t']);
            if (space <= 0)
                continue;
            string name = trimmed[(space + 1)..].Trim();
            if (name.Length > 0)
                _names[trimmed[..space]] = name;
        }
    }

    public int Count => _names.Count;

    /// <summary>The function name, or the NID itself when unknown.</summary>
    public string Name(string nid) => _names.GetValueOrDefault(nid, nid);
}
