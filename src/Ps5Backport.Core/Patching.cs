using System.Buffers.Binary;
using System.Text;

namespace Ps5Backport.Core;

/// <summary>A patching step that can't be done on this module (no padding left, shared name, ...).</summary>
public sealed class PatchException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

public sealed record StubPatch(string Nid, ulong GotVaddr, string Stub, ulong StubVaddr);

/// <summary><see cref="Patching.RestoreImport"/>: the import's symbol, its stub (now the GOT pre-fill), GOT slot and PLT entry,
/// and what changed (local definition undone, PLT entry restored, GOT slot rewritten).</summary>
public sealed record ImportRestore(string Nid, string Symbol, ulong Stub, ulong GotVaddr, ulong Plt,
                                   bool WasDefined, bool PltRestored, bool GotRewritten);

/// <summary>
/// Patch primitives: int3 caves, stubs, PLT redirects, NID swaps, symbol
/// definitions, chained adapter code and the code-space fallback.
///
/// Stubs work because PLT imports are bound lazily: the GOT slot's initial value is the PLT lazy-bind
/// stub, and replacing it means the dynamic linker is never asked for the missing NID.
/// A PLT entry is: jmp [rip+GOT] (ff 25 rel32) ; push &lt;index&gt; ; jmp PLT0. The GOT slot's initial
/// value is the entry's lazy-bind part (entry + 6).
/// </summary>
public static class Patching
{
    public const byte Int3 = 0xCC, Ret = 0xC3;
    const byte PushImm32 = 0x68, JmpRel32 = 0xE9, Nop = 0x90;
    /// <summary>ExtendText makes one cave per block (61 usable bytes).</summary>
    public const int ExtensionBlock = 64;

    // --- caves -----------------------------------------------------------------
    /// <summary>
    /// Code-space fallback (not console-tested): grow the executable segment into the unused bytes up
    /// to the next segment (PS5 modules end .text mid-page; the next segment starts on a 16 KB page).
    /// The new bytes become `ret` + int3 padding, so the cave finders use them like any other padding.
    /// Only when the gap is unused (all zero). Returns the bytes added (0 when there is no gap).
    /// </summary>
    public static long ExtendText(SceElf elf)
    {
        var text = elf.TextSegment();
        long gap = ExtensionRoom(elf);
        if (gap == 0)
            return 0;
        long endOff = text.Offset + text.Filesz;
        // 64-byte blocks of ret + int3s: each block is its own cave (a cave starts after a ret, and code
        // placed in one ends with a jmp, so a single long run would serve only one fix)
        for (long i = 0; i < gap; i++)
            elf.Data[endOff + i] = i % ExtensionBlock == 0 ? Ret : Int3;
        long entry = (long)elf.U64(0x20) + text.Index * 0x38L;
        BinaryPrimitives.WriteInt64LittleEndian(elf.Data.AsSpan((int)entry + 0x20), text.Filesz + gap);
        BinaryPrimitives.WriteInt64LittleEndian(elf.Data.AsSpan((int)entry + 0x28), text.Memsz + gap);
        text.Filesz += gap;
        text.Memsz += gap;
        elf.TextExtended += gap;
        return gap;
    }

    /// <summary>Bytes <see cref="ExtendText"/> would add: the all-zero gap between the code segment and the next one
    /// (0 when there is none, it is under 16 bytes or not all zero, or the segment has bss). Writes nothing.</summary>
    public static long ExtensionRoom(SceElf elf)
    {
        var text = elf.TextSegment();
        if (text.Filesz != text.Memsz)
            return 0;
        long endOff = text.Offset + text.Filesz;
        ulong endVa = text.Vaddr + (ulong)text.Memsz;
        long nextOff = elf.Phdrs.Where(p => p.Filesz != 0 && p.Offset >= endOff).Select(p => p.Offset).DefaultIfEmpty(elf.Data.Length).Min();
        ulong nextVa = elf.Phdrs.Where(p => p.Type == SceElf.PtLoad && p.Vaddr >= endVa).Select(p => p.Vaddr).DefaultIfEmpty(endVa).Min();
        long gap = Math.Min(nextOff - endOff, (long)(nextVa - endVa));
        return gap < 16 || elf.Data.AsSpan((int)endOff, (int)gap).ContainsAnyExcept((byte)0) ? 0 : gap;
    }

    /// <summary>Every cave <see cref="FindCave"/> can pick, in address order: (vaddr of the usable bytes, usable bytes).
    /// For the code graph's cave map; writes nothing.</summary>
    public static List<(ulong Vaddr, long Usable)> Caves(SceElf elf) => [.. CaveRuns(elf, 1).Select(r => (elf.Vaddr(r.Offset), r.Usable))];

    /// <summary>(stub file offset, usable bytes) for int3 runs after a `ret`, keeping one guard int3 on each side.</summary>
    static IEnumerable<(long Offset, long Usable)> CaveRuns(SceElf elf, long minUsable)
    {
        var text = elf.TextSegment();
        byte[] data = elf.Data;
        long end = text.Offset + text.Filesz;
        long i = text.Offset + 1;
        while (i < end)
        {
            if (data[i] != Int3 || data[i - 1] != Ret)
            {
                i++;
                continue;
            }
            long j = i;
            while (j < end && data[j] == Int3)
                j++;
            if (j - i - 2 >= minUsable)
                yield return (i + 1, j - i - 2);
            i = j;
        }
    }

    /// <summary>
    /// First run of int3 padding directly after a `ret` that fits <paramref name="size"/> bytes with one
    /// guard int3 on each side; the stub's file offset, or null. With AllowTextExtension the code segment
    /// is extended once when nothing fits.
    /// </summary>
    public static long? FindCave(SceElf elf, long size)
    {
        long? off = FirstCave(elf, size);
        if (off is null && elf.AllowTextExtension && ExtendText(elf) > 0)
            off = FirstCave(elf, size);
        return off;
    }

    static long? FirstCave(SceElf elf, long size) =>
        CaveRuns(elf, size).Select(r => (long?)r.Offset).FirstOrDefault();

    /// <summary>
    /// One place for <paramref name="size"/> bytes of code: an int3 run (as <see cref="FindCave"/>), else
    /// consecutive untouched 64-byte blocks of the text extension joined into one run (their `ret`s are
    /// overwritten), extending .text once when allowed. File offset, or null.
    /// </summary>
    internal static long? FindContiguousCave(SceElf elf, long size)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (FirstCave(elf, size) is long off)
                return off;
            var text = elf.TextSegment();
            long end = text.Offset + text.Filesz, start = end - elf.TextExtended;
            long runStart = -1;
            for (long b = start; elf.TextExtended > 0 && b < end; b += ExtensionBlock)
            {
                long blockEnd = Math.Min(b + ExtensionBlock, end);
                bool pristine = elf.Data[b] == Ret && !elf.Data.AsSpan((int)b + 1, (int)(blockEnd - b - 1)).ContainsAnyExcept(Int3);
                if (!pristine)
                {
                    runStart = -1;
                    continue;
                }
                if (runStart < 0)
                    runStart = b;
                if (blockEnd - 1 - (runStart + 2) >= size)      // keep the first ret and one guard int3 on each side
                    return runStart + 2;
            }
            if (!(attempt == 0 && elf.AllowTextExtension && ExtendText(elf) > 0))
                break;
        }
        return null;
    }

    // --- runtime state -------------------------------------------------------------
    /// <summary>PS5 page size: segments are mapped in 16 KB pages.</summary>
    public const long PageSize = 0x4000;

    /// <summary>
    /// Zero-initialised read-write memory for a fix's runtime state (not console-tested): the unused tail of
    /// the last page of a writable segment, past its memsz. The loader maps whole pages and zero-fills the
    /// bss part, so those bytes exist at runtime; no header changes. Skipped: pages covered by PT_GNU_RELRO
    /// (read-only after relocation) or shared with another mapped (nonzero-flags) PT_LOAD. A flags-0 PT_LOAD
    /// that starts inside that page (PS5 eboots keep their dynamic tables in one) cannot be mapped there
    /// without unmapping the bss below it, so it does not count. One block per name and module.
    /// </summary>
    public static ulong AllocateState(SceElf elf, string name, int size)
    {
        if (elf.StateBlocks.TryGetValue(name, out var existing))
            return existing.Vaddr;
        foreach (var seg in elf.Phdrs.Where(p => p.Type == SceElf.PtLoad && (p.Flags & SceElf.PfW) != 0 && p.Memsz > 0)
                                     .OrderByDescending(p => p.Vaddr))
        {
            ulong end = seg.Vaddr + (ulong)seg.Memsz;
            ulong pageEnd = (end + PageSize - 1) & ~(ulong)(PageSize - 1);
            ulong at = elf.StateBlocks.Values.Where(b => b.Vaddr >= end && b.Vaddr < pageEnd)
                          .Select(b => b.Vaddr + (ulong)b.Size).DefaultIfEmpty(end).Max();
            at = (at + 15) & ~15UL;
            if (at + (ulong)size > pageEnd)
                continue;
            bool clash = elf.Phdrs.Any(p => p != seg && p.Memsz > 0 && p.Vaddr < pageEnd && p.Vaddr + (ulong)p.Memsz > end &&
                                            (p.Type == SceElf.PtGnuRelro || (p.Type == SceElf.PtLoad && p.Flags != 0)));
            if (clash)
                continue;
            elf.StateBlocks[name] = (at, size);
            return at;
        }
        throw new PatchException($"no writable page tail left for {size} bytes of runtime state '{name}'");
    }

    // --- PLT-level primitives ----------------------------------------------------
    /// <summary>Virtual address of an import's PLT entry (checked to be an untouched jmp through its GOT slot).</summary>
    public static ulong PltEntry(SceElf elf, Import imp)
    {
        if (!imp.Plt)
            throw new PatchException($"{imp.Nid} has no PLT entry (RELA import)");
        ulong entry = elf.ReadU64(imp.GotVaddr) - 6;
        long off = elf.Off(entry);
        if (elf.Data[off] != 0xFF || elf.Data[off + 1] != 0x25 ||
            entry + 6 + (ulong)(long)BinaryPrimitives.ReadInt32LittleEndian(elf.Data.AsSpan((int)off + 2)) != imp.GotVaddr)
            throw new PatchException($"{imp.Nid}: PLT entry at 0x{entry:x} is not an untouched jmp through GOT 0x{imp.GotVaddr:x}");
        return entry;
    }

    static byte[] Rel32(ulong sourceNext, ulong target)
    {
        long disp = (long)(target - sourceNext);
        if (disp < int.MinValue || disp > int.MaxValue)
            throw new PatchException($"jump target 0x{target:x} out of rel32 range");
        return BitConverter.GetBytes((int)disp);
    }

    /// <summary>`jmp qword [rip+disp]` placed at <paramref name="at"/>, jumping through <paramref name="got"/>.</summary>
    public static byte[] JmpViaGot(ulong at, ulong got) => [0xFF, 0x25, .. Rel32(at + 6, got)];

    /// <summary>
    /// Overwrite the PLT entry's first instruction with `jmp target; nop` (6 bytes). Every call through
    /// this import then goes to <paramref name="target"/>; the lazy-bind part and the GOT slot stay intact.
    /// </summary>
    public static ulong RedirectPlt(SceElf elf, Import imp, ulong target)
    {
        ulong entry = PltEntry(elf, imp);
        long off = elf.Off(entry);
        byte[] code = [0xE9, .. Rel32(entry + 5, target), 0x90];
        code.CopyTo(elf.Data, off);
        return entry;
    }

    /// <summary>
    /// Write branch-free instructions, split across as many int3 caves as needed (eboots only have
    /// &lt;=16-byte padding); pieces are joined with `jmp rel32`. With <paramref name="tailJmp"/> the chain
    /// ends with `jmp [GOT]` (pre-call adapter); without, the instructions must contain call_got and end
    /// with `ret` (post-call adapter). Returns the vaddr of the first piece. <paramref name="contiguous"/>:
    /// one piece only, so the instructions may branch inside it. <paramref name="stateVaddr"/>: target of the
    /// instructions' {state+N} displacements. <paramref name="callGots"/>: GOT slots of the imports named by call_got:NID.
    /// </summary>
    public static ulong PlaceChain(SceElf elf, IReadOnlyList<Insn> insns, ulong gotVaddr, bool tailJmp = true,
                                   bool contiguous = false, ulong stateVaddr = 0, IReadOnlyDictionary<string, ulong>? callGots = null)
    {
        int tail = tailJmp ? 6 : 0;
        var pieces = new List<(long Offset, List<Insn> Insns, bool IsLast)>();
        int k = 0;
        if (contiguous)
        {
            long need = insns.Sum(i => (long)i.Length) + tail;
            long off = FindContiguousCave(elf, need)
                ?? throw new PatchException($"no padding or code-space left for a {need}-byte adapter placed in one piece");
            pieces.Add((off, insns.ToList(), true));
            elf.Data.AsSpan((int)off, (int)need).Fill(0x90);                     // reserve
        }
        while (!contiguous)
        {
            long lastNeed = insns.Skip(k).Sum(i => (long)i.Length) + tail;
            long minNeed = k < insns.Count ? insns[k].Length + 5 : tail;
            // prefer one cave for all remaining code; split only when no cave is big enough
            var found = CaveRuns(elf, lastNeed).Cast<(long, long)?>().FirstOrDefault()
                        ?? CaveRuns(elf, Math.Min(lastNeed, minNeed)).Cast<(long, long)?>().FirstOrDefault();
            if (found is null && elf.AllowTextExtension && ExtendText(elf) > 0)
                continue;
            if (found is not var (off, usable))
                throw new PatchException($"no int3 padding left for a {Math.Min(lastNeed, minNeed)}-byte code piece");
            if (lastNeed <= usable)
            {
                pieces.Add((off, insns.Skip(k).ToList(), true));
                elf.Data.AsSpan((int)off, (int)lastNeed).Fill(0x90);        // reserve
                break;
            }
            int n = k;
            long size = 0;
            while (n < insns.Count && size + insns[n].Length + 5 <= usable)
                size += insns[n++].Length;
            pieces.Add((off, insns.Skip(k).Take(n - k).ToList(), false));
            elf.Data.AsSpan((int)off, (int)(size + 5)).Fill(0x90);              // reserve
            k = n;
        }
        for (int idx = 0; idx < pieces.Count; idx++)
        {
            var (off, piece, isLast) = pieces[idx];
            ulong va = elf.Vaddr(off);
            var body = new List<byte>();
            foreach (var insn in piece)
            {
                if (insn.CallGot)
                {
                    ulong target = insn.CallNid.Length == 0 ? gotVaddr
                        : callGots?.GetValueOrDefault(insn.CallNid) is { } other and not 0 ? other
                        : throw new PatchException($"call_got:{insn.CallNid}: the module does not import it through the PLT");
                    body.AddRange([0xFF, 0x15, .. Rel32(va + (ulong)body.Count + 6, target)]);
                }
                else if (insn.StateAt >= 0)
                {
                    if (stateVaddr == 0)
                        throw new PatchException("instruction refers to a runtime state block, but the fix has none");
                    byte[] bytes = insn.Bytes.ToArray();
                    Rel32(va + (ulong)body.Count + (ulong)bytes.Length, stateVaddr + (ulong)insn.StateOffset).CopyTo(bytes, insn.StateAt);
                    body.AddRange(bytes);
                }
                else
                    body.AddRange(insn.Bytes);
            }
            if (!isLast)
                body.AddRange([0xE9, .. Rel32(va + (ulong)body.Count + 5, elf.Vaddr(pieces[idx + 1].Offset))]);
            else if (tailJmp)
                body.AddRange(JmpViaGot(va + (ulong)body.Count, gotVaddr));
            body.ToArray().CopyTo(elf.Data, off);
        }
        return elf.Vaddr(pieces[0].Offset);
    }

    /// <summary>
    /// Swap an import's NID in .dynstr, optionally using an existing target library/module suffix. Imports are never looked up in this
    /// module's hash table, so no hash rebuild is needed.
    /// </summary>
    public static void RenameImport(SceElf elf, Import imp, string newNid, string newLibrary = "")
    {
        if (newNid.Length != 11)
            throw new PatchException($"invalid NID '{newNid}'");
        long symOff = elf.SymtabOffset(imp.SymIndex);
        uint nameOff = elf.U32(symOff);
        long start = elf.StringTableOffset + nameOff;
        string full = elf.CStr(start);
        // refuse when another symbol's name shares these bytes (string-table suffix merging, either way round:
        // the other name starts inside this one, or this name is the tail of a longer one)
        var names = elf.Symbols();
        for (int i = 0; i < names.Count; i++)
        {
            uint other = elf.U32(elf.SymtabOffset(i));
            if (i != imp.SymIndex && other < nameOff + full.Length && nameOff < other + (uint)names[i].Name.Length)
                throw new PatchException($"{imp.Nid}: symbol name is shared with symbol #{i}");
        }
        if (!full.StartsWith(imp.Nid, StringComparison.Ordinal))
            throw new PatchException($"{imp.Nid}: unexpected symbol name '{full}'");
        string replacement = newNid + full[11..];
        if (newLibrary.Length > 0 && newLibrary != imp.Library)
        {
            var symbols = elf.Symbols();
            var suffixes = elf.Imports().Where(i => i.Library == newLibrary && !i.DefinedLocally)
                .Select(i => symbols[i.SymIndex].Name[11..]).Distinct(StringComparer.Ordinal).ToArray();
            if (suffixes.Length != 1)
                throw new PatchException($"{imp.Nid}: target library '{newLibrary}' needs one existing import library/module suffix (found {suffixes.Length})");
            replacement = newNid + suffixes[0];
            if (replacement.Length > full.Length)
                throw new PatchException($"{imp.Nid}: target library/module suffix does not fit the symbol name");
        }
        // Clear leftover bytes when the replacement suffix is shorter, including the terminator.
        elf.Data.AsSpan((int)start, full.Length + 1).Clear();
        Encoding.ASCII.GetBytes(replacement).CopyTo(elf.Data, start);
    }

    /// <summary>Rewrite a symbol into a local definition at <paramref name="vaddr"/> (st_shndx=1, as GT7's fakelibs).</summary>
    public static void DefineSymbol(SceElf elf, int symIndex, ulong vaddr)
    {
        long off = elf.SymtabOffset(symIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(elf.Data.AsSpan((int)off + 6), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(elf.Data.AsSpan((int)off + 8), vaddr);
    }

    /// <summary>
    /// Undo a forced stub so the loader can bind the native function again (FINDINGS 6v): a symbol rewritten into a local
    /// definition becomes an import (st_shndx 0, value 0, as Sony ships it), a PLT entry rewritten into 'jmp &lt;stub&gt;'
    /// becomes 'jmp [rip+GOT]', and the GOT slot is pre-filled with that stub, which keeps answering on firmwares that lack
    /// the export. Only a function import with exactly one JUMP_SLOT, no RELA reference, one PLT entry and a recognised
    /// GOT value (the stub or the lazy PLT+6) is accepted. Returns null when the import is already in that form.
    /// </summary>
    public static ImportRestore? RestoreImport(SceElf elf, string nid)
    {
        var imports = elf.Imports().Where(i => i.Nid == nid).ToList();
        if (imports.Count == 0)
            throw new PatchException($"{nid}: not imported by this module");
        if (imports.Any(i => !i.Plt))
            throw new PatchException($"{nid}: also referenced by RELA relocations (data import)");
        if (imports.Count != 1)
            throw new PatchException($"{nid}: {imports.Count} JUMP_SLOT relocations");
        var imp = imports[0];
        int slot = elf.Relocs(RelocKind.JmpRel).FindIndex(r => r.SymIndex == imp.SymIndex);
        var reloc = elf.Relocs(RelocKind.JmpRel)[slot];
        if (reloc.Type != 7)
            throw new PatchException($"{nid}: JMPREL type {reloc.Type} is not R_X86_64_JUMP_SLOT");

        // the PLT entry: 6-byte jmp, 'push <slot>', 'jmp PLT0'
        byte[] push = [PushImm32, (byte)slot, (byte)(slot >> 8), (byte)(slot >> 16), (byte)(slot >> 24)];
        var entries = new List<ulong>();
        foreach (var p in elf.Phdrs.Where(p => p.Type == SceElf.PtLoad && (p.Flags & SceElf.PfX) != 0))
        {
            var code = elf.Data.AsSpan(checked((int)p.Offset), checked((int)p.Filesz));
            for (int at = code.IndexOf(push); at >= 6; )
            {
                if (at + 5 < code.Length && code[at + 5] == JmpRel32)
                    entries.Add(p.Vaddr + (ulong)at - 6);
                int next = code[(at + 1)..].IndexOf(push);
                at = next < 0 ? -1 : at + 1 + next;
            }
        }
        if (entries.Count != 1)
            throw new PatchException($"{nid}: {entries.Count} PLT entries for JUMP_SLOT {slot}");
        ulong plt = entries[0];
        long pltOff = elf.Off(plt);
        byte[] viaGot = [0xFF, 0x25, .. BitConverter.GetBytes(checked((int)((long)imp.GotVaddr - (long)(plt + 6))))];

        ulong? stub = imp.DefinedLocally ? elf.Symbols()[imp.SymIndex].Value : null;
        bool pltRestored = !elf.Data.AsSpan((int)pltOff, 6).SequenceEqual(viaGot);
        if (pltRestored)
        {
            ulong? target = elf.Data[pltOff] == JmpRel32 && elf.Data[pltOff + 5] == Nop
                ? plt + 5 + (ulong)(long)BinaryPrimitives.ReadInt32LittleEndian(elf.Data.AsSpan((int)pltOff + 1)) : null;
            bool executable = target is { } t && elf.Phdrs.Any(p => p.Type == SceElf.PtLoad && (p.Flags & SceElf.PfX) != 0
                                                                      && t >= p.Vaddr && t < p.Vaddr + (ulong)p.Filesz);
            stub ??= executable ? target : null;
            if (target is null || target != stub)
                throw new PatchException($"{nid}: PLT 0x{plt:x} is {Convert.ToHexStringLower(elf.Data.AsSpan((int)pltOff, 6))}, neither 'jmp [GOT]' nor 'jmp <stub>'");
        }
        if (stub is null)
            return null;   // already an import whose PLT jumps through the GOT

        ulong prefill = elf.ReadU64(imp.GotVaddr);
        if (prefill != stub && prefill != plt + 6)
            throw new PatchException($"{nid}: GOT 0x{imp.GotVaddr:x} holds 0x{prefill:x}, neither the stub nor the lazy PLT+6");
        if (pltRestored)
            viaGot.CopyTo(elf.Data, pltOff);
        elf.WriteU64(imp.GotVaddr, stub.Value);
        if (imp.DefinedLocally)
        {
            long sym = elf.SymtabOffset(imp.SymIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(elf.Data.AsSpan((int)sym + 6), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(elf.Data.AsSpan((int)sym + 8), 0);
        }
        return new ImportRestore(nid, elf.Symbols()[imp.SymIndex].Name, stub.Value, imp.GotVaddr, plt, imp.DefinedLocally, pltRestored, prefill != stub);
    }

    /// <summary>
    /// Point the given imports at stubs: always the GOT slot of PLT imports, and with
    /// <paramref name="defineSymbol"/> also the import's symbol (needed for RELA imports). One stub body
    /// is shared by every import that uses it. <paramref name="rules"/>: NID -> stub name or "ret:&lt;value&gt;"
    /// (default ret0).
    /// </summary>
    public static List<StubPatch> StubImports(SceElf elf, IReadOnlyList<string> nids, Knowledge knowledge,
                                              IReadOnlyDictionary<string, string>? rules = null, bool defineSymbol = false)
    {
        var byNid = new Dictionary<string, List<Import>>(StringComparer.Ordinal);
        var wanted = nids.ToHashSet(StringComparer.Ordinal);
        foreach (var imp in elf.Imports())
        {
            if (!wanted.Contains(imp.Nid))
                continue;
            if (!imp.Plt && !defineSymbol)
                throw new PatchException($"{imp.Nid} is a RELA (non-PLT) import; it needs define_symbol=True");
            if (!byNid.TryGetValue(imp.Nid, out var list))
                byNid[imp.Nid] = list = [];
            list.Add(imp);
        }
        var missing = wanted.Where(n => !byNid.ContainsKey(n)).Order(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
            throw new PatchException($"not imported by this module: {string.Join(", ", missing)}");

        var placed = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var patches = new List<StubPatch>();
        foreach (string nid in nids)
        {
            string stub = rules?.GetValueOrDefault(nid) ?? "ret0";
            if (!placed.TryGetValue(stub, out ulong at))
            {
                byte[] body = knowledge.StubBytes(stub);
                long off = FindCave(elf, body.Length)
                    ?? throw new PatchException($"no int3 padding large enough for stub '{stub}' ({body.Length} bytes) needed by import {nid}");
                body.CopyTo(elf.Data, off);
                placed[stub] = at = elf.Vaddr(off);
            }
            foreach (var imp in byNid[nid])
            {
                if (imp.Plt)
                    elf.WriteU64(imp.GotVaddr, at);
                if (defineSymbol)
                    DefineSymbol(elf, imp.SymIndex, at);
                patches.Add(new StubPatch(nid, imp.GotVaddr, stub, at));
            }
        }
        return patches;
    }
}

/// <summary>Applying the known fixes of <see cref="Knowledge.Fixes"/> .</summary>
public static class FixApplier
{
    /// <summary>Replace <see cref="FixPattern.Find"/> in data[start..end) at offsets that are a multiple of
    /// <paramref name="step"/> from <paramref name="start"/>. Returns the number of replacements.</summary>
    public static int ReplaceAll(byte[] data, FixPattern pattern, long start = 0, long end = -1, int step = 1)
        => ReplaceAll(data, pattern, default, start, end, step);

    /// <summary>Checks cancellation between bounded scan windows; match order and alignment are unchanged.</summary>
    public static int ReplaceAll(byte[] data, FixPattern pattern, CancellationToken cancel, long start = 0, long end = -1, int step = 1)
    {
        cancel.ThrowIfCancellationRequested();
        end = end < 0 ? data.Length : end;
        if (pattern.Find.Length == 0 || pattern.Replace.Length != pattern.Find.Length)
            throw new ArgumentException("patterns must find a nonempty sequence and replace it with the same number of bytes");
        if (start < 0 || end < start || end > data.Length || step <= 0)
            throw new ArgumentOutOfRangeException(nameof(start), "invalid pattern range or step");
        int count = 0;
        long i = start;
        long checkpoint = start;
        while (i <= end - pattern.Find.Length)
        {
            if (i >= checkpoint)
            {
                cancel.ThrowIfCancellationRequested();
                checkpoint = i + 65536;
            }
            long candidates = Math.Min(1024 * 1024, end - pattern.Find.Length - i + 1);
            int at = data.AsSpan((int)i, checked((int)(candidates + pattern.Find.Length - 1))).IndexOf(pattern.Find);
            if (at < 0)
            {
                i += candidates;
                continue;
            }
            i += at;
            if ((i - start) % step == 0)
            {
                pattern.Replace.CopyTo(data, i);
                count++;
            }
            i++;
        }
        return count;
    }

    /// <summary>Matches of <see cref="FixPattern.Find"/> in data[start..end) at offsets that are a multiple of
    /// <paramref name="step"/> from <paramref name="start"/>, without writing (the probe's count).</summary>
    public static int CountMatches(byte[] data, FixPattern pattern, CancellationToken cancel, long start = 0, long end = -1, int step = 1)
        => FindMatches(data, pattern, cancel, start, end, step).Count;

    /// <summary>Offsets of the matches <see cref="CountMatches"/> counts.</summary>
    public static List<long> FindMatches(byte[] data, FixPattern pattern, CancellationToken cancel, long start = 0, long end = -1, int step = 1)
    {
        cancel.ThrowIfCancellationRequested();
        end = end < 0 ? data.Length : end;
        if (pattern.Find.Length == 0)
            throw new ArgumentException("patterns must find a nonempty sequence");
        if (start < 0 || end < start || end > data.Length || step <= 0)
            throw new ArgumentOutOfRangeException(nameof(start), "invalid pattern range or step");
        var found = new List<long>();
        long i = start, checkpoint = start;
        while (i <= end - pattern.Find.Length)
        {
            if (i >= checkpoint) { cancel.ThrowIfCancellationRequested(); checkpoint = i + 65536; }
            long candidates = Math.Min(1024 * 1024, end - pattern.Find.Length - i + 1);
            int at = data.AsSpan((int)i, checked((int)(candidates + pattern.Find.Length - 1))).IndexOf(pattern.Find);
            if (at < 0) { i += candidates; continue; }
            i += at;
            if ((i - start) % step == 0) found.Add(i);
            i++;
        }
        return found;
    }

    /// <summary>What <see cref="ApplyPattern(SceElf, FixPattern, CancellationToken)"/> would replace, counted without writing.</summary>
    public static int CountPattern(SceElf elf, FixPattern pattern, CancellationToken cancel) => FindPattern(elf, pattern, cancel).Count;

    /// <summary>File offsets of what <see cref="ApplyPattern(SceElf, FixPattern, CancellationToken)"/> would replace.</summary>
    public static List<long> FindPattern(SceElf elf, FixPattern pattern, CancellationToken cancel) => pattern.Code
        ? [.. elf.Phdrs.Where(p => p.Type == SceElf.PtLoad && p.Filesz > 0 && (p.Flags & SceElf.PfX) != 0)
                       .SelectMany(p => FindMatches(elf.Data, pattern, cancel, p.Offset, p.Offset + p.Filesz))]
        : [.. elf.Phdrs.Where(p => p.Type == SceElf.PtLoad && p.Filesz > 0 && (p.Flags & SceElf.PfX) == 0)
                       .SelectMany(p => FindMatches(elf.Data, pattern, cancel, p.Offset, p.Offset + p.Filesz, 4))];

    /// <summary>Pattern in a module: only 4-aligned hits inside non-executable loaded segments (shader binaries
    /// live in data; code is left alone).</summary>
    public static int ApplyPattern(SceElf elf, FixPattern pattern) => ApplyPattern(elf, pattern, default);

    /// <summary>Segment pattern replacement with cooperative scan cancellation.</summary>
    public static int ApplyPattern(SceElf elf, FixPattern pattern, CancellationToken cancel) => pattern.Code
        ? elf.Phdrs.Where(p => p.Type == SceElf.PtLoad && p.Filesz > 0 && (p.Flags & SceElf.PfX) != 0)
                   .Sum(p => ReplaceAll(elf.Data, pattern, cancel, p.Offset, p.Offset + p.Filesz))
        : elf.Phdrs.Where(p => p.Type == SceElf.PtLoad && p.Filesz > 0 && (p.Flags & SceElf.PfX) == 0)
                   .Sum(p => ReplaceAll(elf.Data, pattern, cancel, p.Offset, p.Offset + p.Filesz, 4));

    /// <summary>NID -> stub for every stub fix (the rules StubImports uses for known fixes).</summary>
    public static Dictionary<string, string> StubRules(Knowledge knowledge) =>
        StubRules(knowledge.Fixes);

    /// <summary>
    /// A stub whose <see cref="Fix.Insns"/> call other imports (call_got:NID): placed as a branch-free chain (ret-terminated)
    /// when the module imports every one of them through the PLT, else null (the plain <see cref="Fix.Stub"/> applies).
    /// The caller pre-fills the import's GOT slot with the returned address, so the loader still binds the native function
    /// wherever the firmware exports it.
    /// </summary>
    public static ulong? DerivedStub(SceElf elf, Fix fix)
    {
        if (fix.Kind != Fix.KindStub || fix.Insns.Count == 0)
            return null;
        var gots = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var imp in elf.Imports().Where(i => i.Plt))
            gots.TryAdd(imp.Nid, imp.GotVaddr);
        if (fix.Insns.Any(i => i.CallNid.Length > 0 && !gots.ContainsKey(i.CallNid)))
            return null;
        return Patching.PlaceChain(elf, fix.Insns, 0, tailJmp: false, callGots: gots);
    }

    /// <summary>Stub rules from the run's fix table, including selected option overrides.</summary>
    public static Dictionary<string, string> StubRules(IReadOnlyDictionary<string, Fix> fixes) =>
        fixes.Values.Where(f => f.Kind == Fix.KindStub).ToDictionary(f => f.Nid, f => f.Stub, StringComparer.Ordinal);

    /// <summary>Address of <paramref name="nid"/> if this module exports it (own export library), else null.</summary>
    public static ulong? LocalExport(SceElf elf, string nid)
    {
        var own = elf.DynList.Where(d => d.Tag == SceElf.DtSceExportLib).Select(d => (int)(d.Value >> 48)).ToHashSet();
        foreach (var s in elf.Symbols())
            if (Nid.SplitSymbol(s.Name) is var (n, library, _) && n == nid && own.Contains(library) && s.Value != 0 && s.Shndx != 0)
                return s.Value;
        return null;
    }

    /// <summary>False when a fix can't apply to this module (redirect target not exported here).
    /// <paramref name="fixes"/>: the fix table (default <see cref="Knowledge.Fixes"/>).</summary>
    public static bool Applicable(SceElf elf, string nid, Knowledge knowledge, IReadOnlyDictionary<string, Fix>? fixes = null)
    {
        if (!(fixes ?? knowledge.Fixes).TryGetValue(nid, out var fix))
            return false;
        return fix.Kind != Fix.KindRedirect || LocalExport(elf, fix.ExportNid) is not null;
    }

    /// <summary>
    /// Apply the non-stub fixes for <paramref name="nids"/> (stubs are batched by the caller through
    /// <see cref="Patching.StubImports"/> with <see cref="StubRules"/>). Returns (nid, description) per fix.
    /// </summary>
    public static List<(string Nid, string How)> Apply(SceElf elf, IReadOnlyList<string> nids, Knowledge knowledge,
                                                       IReadOnlyDictionary<string, Fix>? fixes = null)
    {
        fixes ??= knowledge.Fixes;
        var byNid = new Dictionary<string, Import>(StringComparer.Ordinal);
        var wanted = nids.ToHashSet(StringComparer.Ordinal);
        foreach (var imp in elf.Imports())
            if (wanted.Contains(imp.Nid) && imp.Plt)
                byNid.TryAdd(imp.Nid, imp);
        var applied = new List<(string, string)>();
        foreach (string nid in nids)
        {
            var fix = fixes[nid];
            try
            {
                if (ApplyOne(elf, fix, byNid.GetValueOrDefault(nid), knowledge) is { } how)
                    applied.Add((nid, how));
            }
            catch (Exception e) when (e is PatchException or InvalidDataException)
            {
                throw new PatchException($"fix for {fix.Name} ({nid}): {e.Message}", e);
            }
        }
        return applied;
    }

    /// <summary>Post-call chain of the <see cref="Fix.GuardOnError"/> guard: call the native function through its GOT slot
    /// (stack kept 16-byte aligned) and return <paramref name="fallback"/> instead of a negative 32-bit result. Branch-free,
    /// so <see cref="Patching.PlaceChain"/> may split it across caves.</summary>
    public static IReadOnlyList<Insn> OnErrorGuard(uint fallback) =>
    [
        new([0x48, 0x83, 0xEC, 0x08], false, "sub rsp, 8"),
        new([], true, "call [GOT]"),
        new([0x48, 0x83, 0xC4, 0x08], false, "add rsp, 8"),
        new([0xB9, (byte)fallback, (byte)(fallback >> 8), (byte)(fallback >> 16), (byte)(fallback >> 24)], false, $"mov ecx, 0x{fallback:x}"),
        new([0x85, 0xC0], false, "test eax, eax"),
        new([0x0F, 0x48, 0xC1], false, "cmovs eax, ecx"),
        new([Patching.Ret], false, "ret"),
    ];

    static string? ApplyOne(SceElf elf, Fix fix, Import? imp, Knowledge knowledge)
    {
        if (fix.Kind == Fix.KindStub)
            return null;
        if (imp is null)
            throw new PatchException($"{fix.Nid}: not a PLT import of this module");
        string renamedTo = fix.NewNid + (fix.NewLibrary.Length > 0 && fix.NewLibrary != imp.Library ? $" ({fix.NewLibrary})" : "");
        switch (fix.Kind)
        {
            case Fix.KindSwap:
                Patching.RenameImport(elf, imp, fix.NewNid, fix.NewLibrary);
                return $"swap -> {renamedTo}";
            case Fix.KindAdapter:
                ulong state = fix.State.Length > 0 ? Patching.AllocateState(elf, fix.State, fix.StateSize) : 0;
                ulong cave = Patching.PlaceChain(elf, fix.Insns, imp.GotVaddr, tailJmp: !fix.PostCall, fix.Contiguous, state);
                if (fix.NewNid.Length > 0)
                    Patching.RenameImport(elf, imp, fix.NewNid, fix.NewLibrary);
                Patching.RedirectPlt(elf, imp, cave);
                return $"adapter{(fix.NewNid.Length > 0 ? $" -> {renamedTo}" : " (argument fix)")} via cave 0x{cave:x}" +
                       (state != 0 ? $", state '{fix.State}' at 0x{state:x}" : "");
            case Fix.KindRedirect:
                ulong target = LocalExport(elf, fix.ExportNid)
                    ?? throw new PatchException($"{fix.Nid}: this module does not export {fix.ExportNid}");
                Patching.RedirectPlt(elf, imp, target);
                return $"redirect -> local export {fix.ExportNid} @ 0x{target:x}";
            case Fix.KindGuarded:
                // the GOT pre-fill serves consoles whose firmware lacks the export (the loader leaves it alone there);
                // elsewhere the loader binds the native function, and the guard turns its errors into the stub's value
                uint fallback = knowledge.StubReturnValue(fix.Stub)
                    ?? throw new PatchException($"{fix.Nid}: guard '{fix.Guard}' needs a value-only stub, not '{fix.Stub}'");
                ulong guard = Patching.PlaceChain(elf, OnErrorGuard(fallback), imp.GotVaddr, tailJmp: false);
                Patching.RedirectPlt(elf, imp, guard);   // before the pre-fill: the PLT entry is found through the GOT's lazy-bind value
                var prefill = Patching.StubImports(elf, [fix.Nid], knowledge, new Dictionary<string, string> { [fix.Nid] = fix.Stub });
                return $"guarded: native, error -> {fix.Stub} via cave 0x{guard:x}; GOT pre-fill {fix.Stub} @ 0x{prefill[0].StubVaddr:x}";
            case Fix.KindPltStub:
                byte[] body = knowledge.StubBytes(fix.Stub);
                long off = Patching.FindCave(elf, body.Length)
                    ?? throw new PatchException($"no int3 padding large enough for stub '{fix.Stub}' ({body.Length} bytes)");
                body.CopyTo(elf.Data, off);
                Patching.RedirectPlt(elf, imp, elf.Vaddr(off));
                return $"PLT -> {fix.Stub} @ 0x{elf.Vaddr(off):x}";
            default:
                throw new PatchException($"{fix.Nid}: unknown fix kind '{fix.Kind}'");
        }
    }
}
