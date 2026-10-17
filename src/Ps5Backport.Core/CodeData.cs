using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Iced.Intel;

namespace Ps5Backport.Core;

/// <summary>String: text at the address; Pointer: a relocated pointer (or a function's address); Value: the number stored there.</summary>
public enum DataKind { String, Pointer, Value }

/// <summary>
/// What a [rip+X] operand reads or takes the address of (code graph tool): <paramref name="Text"/> is ready to show
/// ("\"text\"", "→ sub_x", "= 5", "initially 0x10"); <paramref name="Target"/> is a function it points to.
/// </summary>
public sealed record DataReference(ulong Address, DataKind Kind, string Text, ulong? Target = null);

public sealed partial class CodeImage
{
    const int MaxString = 1024, MinString = 3, ShownString = 60;
    /// <summary>{address: (text, UTF-16)} or null when no string starts there; filled as asked.</summary>
    readonly ConcurrentDictionary<ulong, (string Text, bool Wide)?> _strings = new();
    Dictionary<ulong, Reloc>? _dataRelocs;
    List<Symbol>? _symbols;

    Dictionary<ulong, Reloc> DataRelocs => LazyInitializer.EnsureInitialized(ref _dataRelocs,
        () => Elf.Relocs(RelocKind.Rela).GroupBy(r => r.Offset).ToDictionary(g => g.Key, g => g.First()));

    bool InCode(ulong vaddr)
    {
        var text = Elf.TextSegment();
        return vaddr >= text.Vaddr && vaddr - text.Vaddr < (ulong)text.Memsz;
    }

    /// <summary>
    /// The NUL-terminated string at <paramref name="vaddr"/>: valid UTF-8, else mostly-ASCII UTF-16LE, of at least 3 printable
    /// characters (tabs and line breaks allowed); null otherwise. Data only; the result is cached.
    /// </summary>
    public (string Text, bool Wide)? StringAt(ulong vaddr) => InCode(vaddr) ? null : _strings.GetOrAdd(vaddr, ReadString);

    (string, bool)? ReadString(ulong vaddr)
    {
        long off;
        try { off = Elf.Off(vaddr); }
        catch (InvalidDataException) { return null; }   // bss or unmapped
        var span = Elf.Data.AsSpan((int)off, (int)Math.Min(2 * MaxString + 2, Elf.Data.Length - off));
        int nul = span[..Math.Min(span.Length, MaxString + 1)].IndexOf((byte)0);
        if (nul >= MinString && System.Text.Unicode.Utf8.IsValid(span[..nul]) && Printable(Encoding.UTF8.GetString(span[..nul])) is { } utf8)
            return (utf8, false);
        for (int i = 0; i + 1 < span.Length; i += 2)
        {
            if (span[i] != 0 || span[i + 1] != 0)
                continue;
            // binary tables decode as UTF-16 too: a wide string must be mostly ASCII (game text is; a table of halfwords is not)
            return i / 2 >= MinString && Printable(Encoding.Unicode.GetString(span[..i])) is { } wide && wide.Count(char.IsAscii) * 4 >= wide.Length * 3
                ? (wide, true) : null;
        }
        return null;
    }

    static string? Printable(string s) =>
        s.All(c => !char.IsControl(c) || c is '\t' or '\n' or '\r') && !s.Contains('�') && !s.Any(char.IsSurrogate) && s.Any(char.IsLetterOrDigit) ? s : null;

    /// <summary>"\"text\"" (L"…" for UTF-16), escaped and cut to 60 characters.</summary>
    public static string Quote((string Text, bool Wide) s)
    {
        var sb = new StringBuilder(s.Wide ? "L\"" : "\"");
        foreach (char c in s.Text.Length > ShownString ? s.Text[..ShownString] : s.Text)
            sb.Append(c switch { '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", '"' => "\\\"", '\\' => "\\\\", _ => c.ToString() });
        return sb.Append(s.Text.Length > ShownString ? "…\"" : "\"").ToString();
    }

    /// <summary>
    /// What the [rip+X] operand of <paramref name="ins"/> points at: for `lea`, a string or a function's address (or the
    /// relocated pointer in the first slot); for a read, the relocated pointer or the value stored in the file ("initially"
    /// in a writable segment, "=" in a read-only one). Null for imports' GOT slots, code, stores, bss, writable zeros and
    /// wide/packed reads.
    /// </summary>
    internal DataReference? DataAt(in Instruction ins)
    {
        ulong address = ins.IPRelativeMemoryAddress;
        bool lea = ins.Mnemonic == Mnemonic.Lea;
        if (ImportAt(address) is not null)
            return null;
        if (InCode(address))
            return lea && IsFunctionStart(address) ? new DataReference(address, DataKind.Pointer, $"&{FunctionName(address)}", address) : null;
        if (lea)
        {
            if (StringAt(address) is { } s)
                return new DataReference(address, DataKind.String, Quote(s));
            return PointerAt(address) is var (text, target) ? new DataReference(address, DataKind.Pointer, $"[0] → {text}", target) : null;
        }
        if (ins.Op0Kind == OpKind.Memory && ins.Mnemonic.ToString() is var m && (m.StartsWith("Mov", StringComparison.Ordinal) || m.StartsWith("Vmov", StringComparison.Ordinal)))
            return null;   // a store: what the file holds there says nothing about it
        int size = ins.MemorySize.GetSize();
        if (size == 8 && PointerAt(address) is var (pointer, to))
            return new DataReference(address, DataKind.Pointer, $"→ {pointer}", to);
        return ValueAt(address, ins.MemorySize) is { } value ? new DataReference(address, DataKind.Value, value) : null;
    }

    /// <summary>The relocated pointer in the slot at <paramref name="slot"/>: a function (Target), a string, an imported symbol or an address.</summary>
    (string Text, ulong? Target)? PointerAt(ulong slot)
    {
        if (!DataRelocs.TryGetValue(slot, out var r))
            return null;
        if (r.SymIndex != 0)
        {
            var symbols = LazyInitializer.EnsureInitialized(ref _symbols, Elf.Symbols);
            if (r.SymIndex >= symbols.Count)
                return null;
            string name = symbols[r.SymIndex].Name;
            return (Nid.SplitSymbol(name) is var (nid, _, _) ? _names.Name(nid) : name, null);
        }
        if (r.Type != RelocRelative)
            return null;
        ulong target = (ulong)r.Addend;
        if (IsFunctionStart(target))
            return (FunctionName(target), target);
        if (InCode(target))
            return (TargetName(target) ?? $"0x{target:x}", null);
        return StringAt(target) is { } s ? (Quote(s), null) : ($"0x{target:x}", null);
    }

    /// <summary>"= 5" / "initially 0x10" / "= 1.5" for a scalar integer or float read; null for bss and other sizes.</summary>
    string? ValueAt(ulong address, MemorySize size)
    {
        var segment = Elf.Phdrs.FirstOrDefault(p => p.Type == SceElf.PtLoad && p.Vaddr <= address && address - p.Vaddr < (ulong)p.Filesz);
        int bytes = size.GetSize();
        if (segment is null || bytes is not (1 or 2 or 4 or 8) || address + (ulong)bytes > segment.Vaddr + (ulong)segment.Filesz)
            return null;
        var data = Elf.Data.AsSpan((int)Elf.Off(address), bytes);
        string value = size switch
        {
            MemorySize.Float32 => BinaryPrimitives.ReadSingleLittleEndian(data).ToString("R", CultureInfo.InvariantCulture),
            MemorySize.Float64 => BinaryPrimitives.ReadDoubleLittleEndian(data).ToString("R", CultureInfo.InvariantCulture),
            MemorySize.UInt8 or MemorySize.Int8 or MemorySize.UInt16 or MemorySize.Int16 or MemorySize.UInt32 or MemorySize.Int32
                or MemorySize.UInt64 or MemorySize.Int64 => Number(bytes switch
                {
                    1 => data[0], 2 => BinaryPrimitives.ReadUInt16LittleEndian(data),
                    4 => BinaryPrimitives.ReadUInt32LittleEndian(data), _ => BinaryPrimitives.ReadUInt64LittleEndian(data),
                }),
            _ => "",
        };
        bool writable = (segment.Flags & SceElf.PfW) != 0;
        if (value.Length == 0 || writable && data.IndexOfAnyExcept((byte)0) < 0)
            return null;   // a writable zero is just an unset global
        return writable ? $"initially {value}" : $"= {value}";
    }

    static string Number(ulong v) => v <= 9999 ? v.ToString(CultureInfo.InvariantCulture) : $"0x{v:x}";
}
