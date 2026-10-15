using System.Buffers.Binary;
using System.Text;

namespace Ps5Backport.Core;

/// <summary>A program header. Size fields are mutable: the code-space fallback grows .text.</summary>
public sealed class Phdr(int index, uint type, uint flags, long offset, ulong vaddr, long filesz, long memsz)
{
    public int Index { get; } = index;
    public uint Type { get; } = type;
    public uint Flags { get; } = flags;
    public long Offset { get; } = offset;
    public ulong Vaddr { get; } = vaddr;
    public long Filesz { get; set; } = filesz;
    public long Memsz { get; set; } = memsz;
}

public sealed record Symbol(string Name, int Type, int Shndx, ulong Value)
{
    public string? Nid => Core.Nid.SplitSymbol(Name)?.Nid;
}

/// <summary>An imported symbol reached through a relocation.</summary>
/// <param name="Plt">True for JMPREL (PLT/GOT) imports, false for RELA (data/GOT) imports.</param>
/// <param name="DefinedLocally">Backport technique: the symbol was rewritten into a local definition.</param>
public sealed record Import(string Nid, string Library, int SymType, ulong GotVaddr, bool Plt, int SymIndex,
                            bool DefinedLocally);

public enum RelocKind { JmpRel, Rela }

public readonly record struct Reloc(ulong Offset, int SymIndex, uint Type, long Addend);

/// <summary>
/// PS5 (SCE) ELF model: segments, dynamic tables, imports and exports.
/// Standard DT_* tables use PT_LOAD virtual addresses; DT_SCE_* tables use offsets relative to
/// PT_SCE_DYNLIBDATA. Symbol names are "&lt;NID&gt;#&lt;library-id&gt;#&lt;module-id&gt;".
/// </summary>
public sealed class SceElf
{
    public const ushort EtSceExec = 0xFE10, EtSceDynamic = 0xFE18;
    public const uint PtLoad = 1, PtDynamic = 2, PtSceProcParam = 0x61000001, PtSceModuleParam = 0x61000002;
    public const uint PtSceVersion = 0x6FFFFF01;
    public const uint PtSceDynlibData = 0x61000000;
    public const uint PfX = 1, PfW = 2;
    public const uint PtGnuRelro = 0x6474E552;
    public const ulong DtStrtab = 5, DtSymtab = 6, DtRela = 7, DtRelaSz = 8, DtStrSz = 10, DtJmpRel = 23, DtPltRelSz = 2;
    public const ulong DtSceModuleInfoPath = 0x61000041, DtSceModuleName = 0x61000043;
    public const ulong DtSceExportLib = 0x61000047, DtSceImportLib = 0x61000049, DtSceSymtabSz = 0x6100003F;
    public const ulong DtSceJmpRel = 0x61000029, DtScePltRelSz = 0x6100002D, DtSceRela = 0x6100002F, DtSceRelaSz = 0x61000031;
    public const ulong DtSceStrtab = 0x61000035, DtSceStrSz = 0x61000037, DtSceSymtab = 0x61000039;
    public const ulong DtSceSymEnt = 0x6100003B, DtSceRelaEnt = 0x61000033, DtScePltRel = 0x6100002B;
    public const int SttObject = 1, SttFunc = 2;
    // not "\x7FELF"u8: a C# \x escape takes up to 4 hex digits ("\x7FE" is one character)
    public static ReadOnlySpan<byte> Magic => [0x7F, (byte)'E', (byte)'L', (byte)'F'];

    public byte[] Data { get; }
    public ushort Type { get; }
    public IReadOnlyList<Phdr> Phdrs { get; }
    /// <summary>Dynamic entries in file order, up to DT_NULL.</summary>
    public IReadOnlyList<(ulong Tag, ulong Value)> DynList { get; }
    /// <summary>First value of each dynamic tag.</summary>
    public IReadOnlyDictionary<ulong, ulong> Dyn { get; }

    /// <summary>Lets the cave search grow .text into the page gap when padding runs out (Phase 4).</summary>
    public bool AllowTextExtension { get; set; }
    /// <summary>Bytes added to .text by the code-space fallback.</summary>
    public long TextExtended { get; set; }
    /// <summary>Runtime state blocks of fixes (name -> vaddr, size), see <see cref="Patching.AllocateState"/>.</summary>
    public Dictionary<string, (ulong Vaddr, int Size)> StateBlocks { get; } = new(StringComparer.Ordinal);

    public SceElf(byte[] data)
    {
        Data = data;
        if (data.Length < 0x40 || !data.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("not an ELF file (unwrap SELF files first)");
        if (data[4] != 2 || data[5] != 1 || U16(0x12) != 0x3E || U32(0x14) != 1 || U16(0x36) != 0x38)
            throw new InvalidDataException("unsupported ELF class, encoding, machine, version or program header size");
        Type = U16(0x10);
        ulong phoffValue = U64(0x20);
        int phnum = U16(0x38);
        if (phoffValue > (ulong)data.Length || (ulong)phnum * 0x38 > (ulong)data.Length - phoffValue)
            throw new InvalidDataException("ELF program header table extends past end of file");
        long phoff = (long)phoffValue;
        var phdrs = new List<Phdr>(phnum);
        for (int i = 0; i < phnum; i++)
        {
            long at = phoff + i * 0x38L;
            ulong offset = U64(at + 8), filesz = U64(at + 0x20), memsz = U64(at + 0x28);
            if (offset > (ulong)data.Length || filesz > (ulong)data.Length - offset || memsz > long.MaxValue)
                throw new InvalidDataException($"ELF segment #{i} points outside the file or has an invalid size");
            uint type = U32(at);
            ulong vaddr = U64(at + 0x10);
            if (type == PtLoad && ((filesz > 0 && filesz - 1 > ulong.MaxValue - vaddr) ||
                                   (memsz > 0 && memsz - 1 > ulong.MaxValue - vaddr)))
                throw new InvalidDataException($"ELF load segment #{i} has an overflowing virtual address range");
            // The loader maps only memsz bytes, so file bytes past it would be "backed" for Off() but never loaded.
            if (type == PtLoad && filesz > memsz)
                throw new InvalidDataException($"ELF load segment #{i} has a file size larger than its memory size");
            phdrs.Add(new Phdr(i, type, U32(at + 4), (long)offset, vaddr, (long)filesz, (long)memsz));
        }
        Phdrs = phdrs;

        var dynList = new List<(ulong, ulong)>();
        var dyn = phdrs.FirstOrDefault(p => p.Type == PtDynamic);
        if (dyn is not null)
        {
            if (dyn.Filesz % 16 != 0)
                throw new InvalidDataException("ELF dynamic table has a truncated entry");
            for (long i = 0; i < dyn.Filesz; i += 16)
            {
                ulong tag = U64(dyn.Offset + i);
                if (tag == 0)
                    break;
                dynList.Add((tag, U64(dyn.Offset + i + 8)));
            }
        }
        DynList = dynList;
        var first = new Dictionary<ulong, ulong>();
        foreach (var (tag, value) in dynList)
            first.TryAdd(tag, value);
        Dyn = first;
    }

    public static SceElf Load(string path) => new(File.ReadAllBytes(path));

    public void Save(string path) => File.WriteAllBytes(path, Data);

    // --- primitive reads -----------------------------------------------------
    public ushort U16(long offset) => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(checked((int)offset)));
    public uint U32(long offset) => BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(checked((int)offset)));
    public ulong U64(long offset) => BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan(checked((int)offset)));

    // --- address helpers -----------------------------------------------------
    /// <summary>File offset of a virtual address inside a PT_LOAD's file-backed part.</summary>
    public long Off(ulong vaddr)
    {
        foreach (var p in Phdrs)
            if (p.Type == PtLoad && p.Vaddr <= vaddr && vaddr - p.Vaddr < (ulong)p.Filesz)
                return p.Offset + (long)(vaddr - p.Vaddr);
        throw new InvalidDataException($"vaddr 0x{vaddr:x} is not backed by any PT_LOAD");
    }

    /// <summary>Virtual address of a file offset inside a PT_LOAD.</summary>
    public ulong Vaddr(long offset)
    {
        foreach (var p in Phdrs)
            if (p.Type == PtLoad && p.Offset <= offset && offset < p.Offset + p.Filesz)
                return p.Vaddr + (ulong)(offset - p.Offset);
        throw new InvalidDataException($"file offset 0x{offset:x} is not inside any PT_LOAD");
    }

    /// <summary>NUL-terminated string; non-ASCII bytes become U+FFFD (as Python's 'replace').</summary>
    public string CStr(long offset)
    {
        if (offset < 0 || offset >= Data.Length)
            throw new InvalidDataException($"string offset 0x{offset:x} is outside the ELF");
        return CStr(offset, Data.Length - (int)offset);
    }

    string CStr(long offset, int length)
    {
        int start = checked((int)offset);
        int count = Data.AsSpan(start, length).IndexOf((byte)0);
        if (count < 0)
            throw new InvalidDataException($"unterminated string at 0x{offset:x}");
        int end = start + count;
        var sb = new StringBuilder(end - start);
        for (int i = start; i < end; i++)
            sb.Append(Data[i] < 0x80 ? (char)Data[i] : '�');
        return sb.ToString();
    }

    public ulong ReadU64(ulong vaddr) => U64(Off(vaddr));

    public void WriteU64(ulong vaddr, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(Data.AsSpan(checked((int)Off(vaddr))), value);

    // --- dynamic information -------------------------------------------------
    // Prefer existing standard tables. SCE-only tables need one unambiguous, file-backed base.
    (long Offset, long Available, bool Sce) TableLocation(ulong standardTag, ulong sceTag)
    {
        if (Dyn.TryGetValue(standardTag, out ulong address))
        {
            long offset = Off(address);
            return (offset, Data.Length - offset, false);
        }
        if (!Dyn.TryGetValue(sceTag, out ulong relative))
            throw new InvalidDataException("ELF dynamic table is missing");
        var segments = Phdrs.Where(p => p.Type == PtSceDynlibData).ToList();
        if (segments.Count != 1)
            throw new InvalidDataException("SCE dynamic tables require exactly one PT_SCE_DYNLIBDATA segment");
        var segment = segments[0];
        if (relative > (ulong)segment.Filesz)
            throw new InvalidDataException("SCE dynamic table offset is outside PT_SCE_DYNLIBDATA");
        return (segment.Offset + (long)relative, segment.Filesz - (long)relative, true);
    }

    ulong TableSize(bool sce, ulong standardTag, ulong sceTag, ulong fallback = 0)
    {
        if (!sce)
            return Dyn.GetValueOrDefault(standardTag, fallback);
        if (!Dyn.TryGetValue(sceTag, out ulong size))
            throw new InvalidDataException("SCE dynamic table size is missing");
        return size;
    }

    internal long StringTableOffset => StringTable().Offset;

    (long Offset, int Size) StringTable()
    {
        if (!Dyn.ContainsKey(DtStrtab) && !Dyn.ContainsKey(DtSceStrtab))
            throw new InvalidDataException("ELF dynamic string table is missing");
        var (offset, available, sce) = TableLocation(DtStrtab, DtSceStrtab);
        ulong size = TableSize(sce, DtStrSz, DtSceStrSz, (ulong)available);
        if (size > (ulong)available)
            throw new InvalidDataException("ELF dynamic string table extends past end of file");
        return (offset, (int)size);
    }

    string TableString(long strings, int size, uint nameOffset)
    {
        if (nameOffset >= (ulong)size)
            throw new InvalidDataException($"string offset 0x{nameOffset:x} is outside the ELF dynamic string table");
        return CStr(strings + nameOffset, size - (int)nameOffset);
    }

    string DynStr(ulong value)
    {
        var (strings, size) = StringTable();
        return TableString(strings, size, (uint)(value & 0xFFFFFFFF));
    }

    public string? ModuleName => DynList.Where(d => d.Tag == DtSceModuleName).Select(d => DynStr(d.Value)).FirstOrDefault();

    public string? BuildPath => DynList.Where(d => d.Tag == DtSceModuleInfoPath).Select(d => DynStr(d.Value)).FirstOrDefault();

    /// <summary>{library id: library name} from the import-library dynamic tags.</summary>
    public Dictionary<int, string> ImportLibraries()
    {
        var libs = new Dictionary<int, string>();
        foreach (var (tag, value) in DynList)
            if (tag == DtSceImportLib)
                libs[(int)(value >> 48)] = DynStr(value);
        return libs;
    }

    public long SymtabOffset(int index)
    {
        var (offset, count) = SymbolTable();
        if (index < 0 || index >= count)
            throw new InvalidDataException("ELF symbol index is outside the dynamic symbol table");
        return offset + index * 24L;
    }

    (long Offset, int Count) SymbolTable()
    {
        var (offset, available, sce) = TableLocation(DtSymtab, DtSceSymtab);
        ulong size = TableSize(sce, DtSceSymtabSz, DtSceSymtabSz);
        if (sce && Dyn.TryGetValue(DtSceSymEnt, out ulong entrySize) && entrySize != 24)
            throw new InvalidDataException("unsupported SCE symbol entry size");
        if (size % 24 != 0 || size > (ulong)available)
            throw new InvalidDataException("ELF symbol table is truncated");
        return (offset, (int)(size / 24));
    }

    public List<Symbol> Symbols()
    {
        if ((!Dyn.ContainsKey(DtSymtab) && !Dyn.ContainsKey(DtSceSymtab))
            || (!Dyn.ContainsKey(DtStrtab) && !Dyn.ContainsKey(DtSceStrtab)))   // e.g. a module without a dynamic symbol table
            return [];
        var (baseOff, count) = SymbolTable();
        var (strings, stringSize) = StringTable();
        var symbols = new List<Symbol>(count);
        for (int i = 0; i < count; i++)
        {
            long at = baseOff + i * 24L;
            symbols.Add(new Symbol(TableString(strings, stringSize, U32(at)), Data[at + 4] & 0xF, U16(at + 6), U64(at + 8)));
        }
        return symbols;
    }

    public List<Reloc> Relocs(RelocKind kind)
    {
        var (vaTag, sizeTag) = kind == RelocKind.JmpRel ? (DtJmpRel, DtPltRelSz) : (DtRela, DtRelaSz);
        var (sceTag, sceSizeTag) = kind == RelocKind.JmpRel ? (DtSceJmpRel, DtScePltRelSz) : (DtSceRela, DtSceRelaSz);
        var relocs = new List<Reloc>();
        if (!Dyn.ContainsKey(vaTag) && !Dyn.ContainsKey(sceTag))
            return relocs;
        var (baseOff, available, sce) = TableLocation(vaTag, sceTag);
        ulong size = TableSize(sce, sizeTag, sceSizeTag);
        if (sce && Dyn.TryGetValue(DtSceRelaEnt, out ulong entrySize) && entrySize != 24)
            throw new InvalidDataException("unsupported SCE relocation entry size");
        if (sce && kind == RelocKind.JmpRel && Dyn.TryGetValue(DtScePltRel, out ulong format) && format != DtRela)
            throw new InvalidDataException("unsupported SCE PLT relocation format");
        if (size % 24 != 0 || size > (ulong)available)
            throw new InvalidDataException("ELF relocation table is truncated");
        for (ulong i = 0; i < size / 24; i++)
        {
            long at = baseOff + (long)i * 24;
            ulong info = U64(at + 8);
            relocs.Add(new Reloc(U64(at), (int)(info >> 32), (uint)(info & 0xFFFFFFFF), (long)U64(at + 16)));
        }
        return relocs;
    }

    /// <summary>Every NID-named import reached by a JMPREL (first) or RELA relocation.</summary>
    public List<Import> Imports()
    {
        var symbols = Symbols();
        var libraries = ImportLibraries();
        var imports = new List<Import>();
        foreach (var kind in new[] { RelocKind.JmpRel, RelocKind.Rela })
        {
            foreach (var r in Relocs(kind))
            {
                if (r.SymIndex == 0)
                    continue;
                if (r.SymIndex < 0 || r.SymIndex >= symbols.Count)
                    throw new InvalidDataException($"ELF relocation has invalid symbol index {r.SymIndex}");
                var symbol = symbols[r.SymIndex];
                if (Core.Nid.SplitSymbol(symbol.Name) is not var (nid, library, _))
                    continue;
                imports.Add(new Import(nid, libraries.GetValueOrDefault(library, $"#{library}"), symbol.Type,
                                       r.Offset, kind == RelocKind.JmpRel, r.SymIndex, symbol.Shndx != 0));
            }
        }
        return imports;
    }

    /// <summary>
    /// NIDs this module exports: defined symbols of its own export libraries.
    /// (Imports rewritten into local definitions keep their imported library id.)
    /// </summary>
    public HashSet<string> Exports()
    {
        var own = DynList.Where(d => d.Tag == DtSceExportLib).Select(d => (int)(d.Value >> 48)).ToHashSet();
        var exports = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in Symbols())
            if (s.Value != 0 && s.Shndx != 0 && Core.Nid.SplitSymbol(s.Name) is var (nid, library, _) && own.Contains(library))
                exports.Add(nid);
        return exports;
    }

    /// <summary>Export NIDs with their declared library names, for reporting only. Empty names mean unavailable metadata.</summary>
    public HashSet<(string Nid, string Library)> ExportsByLibrary()
    {
        var libraries = DynList.Where(d => d.Tag == DtSceExportLib)
            .GroupBy(d => (int)(d.Value >> 48)).ToDictionary(g => g.Key, g => DynStr(g.First().Value));
        var exports = new HashSet<(string Nid, string Library)>();
        foreach (var symbol in Symbols())
            if (symbol.Value != 0 && symbol.Shndx != 0 && Core.Nid.SplitSymbol(symbol.Name) is { } id
                && libraries.TryGetValue(id.Library, out string? library))
                exports.Add((id.Nid, library));
        return exports;
    }

    // --- code helpers --------------------------------------------------------
    public Phdr TextSegment() => Phdrs.FirstOrDefault(p => p.Type == PtLoad && (p.Flags & PfX) != 0)
        ?? throw new InvalidDataException("ELF has no executable PT_LOAD segment");
}
