using Iced.Intel;

namespace Ps5Backport.Core;

/// <summary>Patched: inside an original function. Stub: code written into int3 padding or added text.
/// PltEntry: a rewritten PLT entry. Other: anything else in the code segment.</summary>
public enum ChangeKind { Patched, Stub, PltEntry, Other }

/// <summary>A run of changed bytes in the code segment. Function: the original function holding it (Patched);
/// Import: the import whose PLT entry it is (PltEntry).</summary>
public sealed record CodeChange(ulong Address, int Length, ChangeKind Kind, FunctionRange? Function, Import? Import)
{
    public ulong End => Address + (ulong)Length;
    public bool Overlaps(ulong start, ulong end) => Address < end && start < End;
}

/// <summary>An import the backport changed: its GOT slot or PLT entry now leads to <paramref name="Target"/>, and/or its
/// symbol was renamed to <paramref name="NewNid"/>. <paramref name="ViaPlt"/>: only the PLT entry was rewritten, so calls
/// through the PLT reach the target while `call/jmp [GOT]` still reaches the system function (an adapter's own tail).</summary>
public sealed record ImportRedirect(Import Import, ulong? Target, string? NewNid, bool ViaPlt = false);

/// <summary>
/// What a backport changed in an eboot, from a byte comparison with the original (code graph tool, FINDINGS 6o).
/// Only the code segment, GOT slots, PLT entries and import symbols are compared.
/// </summary>
public sealed class BackportDiff
{
    public required IReadOnlyList<CodeChange> Changes { get; init; }
    public required IReadOnlyList<ImportRedirect> Redirects { get; init; }
    /// <summary>Bytes the backport added to the end of the code segment.</summary>
    public long TextGrowth { get; init; }
    /// <summary>Stubs grouped into chains: pieces joined by jumps (Patching.PlaceChain splits long adapters across
    /// int3 caves). The first piece is where imports are redirected; a stub on its own is a chain of one.</summary>
    public required IReadOnlyList<IReadOnlyList<CodeChange>> StubChains { get; init; }

    public IEnumerable<CodeChange> In(FunctionRange f) => Changes.Where(c => c.Overlaps(f.Start, f.End));

    /// <summary>Imports redirected to code starting at <paramref name="target"/>.</summary>
    public IEnumerable<ImportRedirect> RedirectsTo(ulong target) => Redirects.Where(r => r.Target == target);

    /// <summary>What the backport did to this import (looked up by GOT slot), or null.</summary>
    public ImportRedirect? RedirectOf(Import import) => Redirects.FirstOrDefault(r => r.Import.GotVaddr == import.GotVaddr);

    /// <summary>The stub chain holding <paramref name="vaddr"/>, or null.</summary>
    public IReadOnlyList<CodeChange>? ChainAt(ulong vaddr) => StubChains.FirstOrDefault(c => c.Any(p => p.Overlaps(vaddr, vaddr + 1)));

    /// <summary>Changed runs closer than this are reported as one change.</summary>
    const int Join = 8;

    /// <summary>
    /// Compares a backport eboot with its original. Gives the backport image the original as name fallback and
    /// labels stubs "stub_XXXX". Throws when the two are different builds.
    /// </summary>
    /// <summary>
    /// Whether <paramref name="backport"/> can be a backport of <paramref name="original"/>: the same unwind table and code
    /// segment start, and a code segment at least as long. Backports never touch the unwind table, so a different one means
    /// a different build (discovered functions can differ: a stub in int3 padding changes what looks like a function start).
    /// </summary>
    public static bool SameBuild(CodeImage original, CodeImage backport)
    {
        var to = original.Elf.TextSegment();
        var tb = backport.Elf.TextSegment();
        return to.Vaddr == tb.Vaddr && tb.Filesz >= to.Filesz && original.Unwind.SequenceEqual(backport.Unwind);
    }

    public static BackportDiff Compare(CodeImage original, CodeImage backport)
    {
        var o = original.Elf;
        var b = backport.Elf;
        var to = o.TextSegment();
        var tb = b.TextSegment();
        // backports never touch the unwind table, so a different one means a different build (discovered functions can
        // differ: a stub in int3 padding changes what looks like a function start)
        if (!SameBuild(original, backport))
            throw new InvalidDataException("the two files are different builds: compare a backport with the eboot it was made from");
        backport.Fallback = original;

        // PLT entries of the original, to recognise rewritten ones
        var plt = new Dictionary<ulong, Import>();
        foreach (var imp in o.Imports().Where(i => i.Plt))
            if (Entry(o, imp) is ulong entry)
                plt.TryAdd(entry, imp);

        var runs = new List<(ulong Start, ulong End)>();
        void Add(ulong start, ulong end)
        {
            // join close runs of one function (or of one padding gap), never across a function between two caves
            var owner = original.FunctionAt(start);
            if (runs.Count > 0 && start - runs[^1].End < Join
                && Enumerable.Range(0, (int)(start - runs[^1].End) + 1).All(k => original.FunctionAt(runs[^1].End - 1 + (ulong)k) == owner))
                runs[^1] = (runs[^1].Start, end);
            else
                runs.Add((start, end));
        }
        var oldText = o.Data.AsSpan((int)to.Offset, (int)to.Filesz);
        var newText = b.Data.AsSpan((int)tb.Offset, (int)to.Filesz);
        for (long i = 0; i < to.Filesz;)
        {
            i += oldText[(int)i..].CommonPrefixLength(newText[(int)i..]);
            if (i >= to.Filesz)
                break;
            long j = i;
            while (j < to.Filesz && o.Data[to.Offset + j] != b.Data[tb.Offset + j])
                j++;
            Add(to.Vaddr + (ulong)i, to.Vaddr + (ulong)j);
            i = j;
        }
        // added text: 64-byte blocks of ret + int3s (Patching.ExtendText) unless code was written there
        for (long i = to.Filesz; i < tb.Filesz;)
        {
            if (b.Data[tb.Offset + i] == Filler(i - to.Filesz))
            {
                i++;
                continue;
            }
            long j = i;
            while (j < tb.Filesz && b.Data[tb.Offset + j] != Filler(j - to.Filesz))
                j++;
            Add(to.Vaddr + (ulong)i, to.Vaddr + (ulong)j);
            i = j;
        }

        var changes = new List<CodeChange>();
        foreach (var (start, end) in runs)
        {
            var pltHit = plt.FirstOrDefault(p => p.Key < end && start < p.Key + 16);
            if (pltHit.Value is { } pltImport)
                changes.Add(new CodeChange(start, (int)(end - start), ChangeKind.PltEntry, null, pltImport));
            else if (original.FunctionAt(start) is { } f)
                changes.Add(new CodeChange(start, (int)(end - start), ChangeKind.Patched, f, null));
            else if (end > to.Vaddr + (ulong)to.Filesz || o.Data.AsSpan((int)o.Off(start), (int)(end - start)).IndexOfAnyExcept(Patching.Int3) < 0)
                changes.Add(new CodeChange(start, (int)(end - start), ChangeKind.Stub, null, null));
            else
                changes.Add(new CodeChange(start, (int)(end - start), ChangeKind.Other, null, null));
        }

        var redirects = new List<ImportRedirect>();
        var backportImports = b.Imports().GroupBy(i => i.GotVaddr).ToDictionary(g => g.Key, g => g.First());
        foreach (var imp in o.Imports().GroupBy(i => i.GotVaddr).Select(g => g.First()))
        {
            string? renamed = backportImports.TryGetValue(imp.GotVaddr, out var now) && now.Nid != imp.Nid ? now.Nid : null;
            ulong? target = null;
            bool viaPlt = false;
            if (Slot(o, imp.GotVaddr) is ulong before && Slot(b, imp.GotVaddr) is ulong after && before != after)
                target = after;
            else if (changes.Any(c => c.Kind == ChangeKind.PltEntry && c.Import == imp)
                     && Entry(o, imp) is ulong entry && b.Data[b.Off(entry)] == 0xE9)
                (target, viaPlt) = (entry + 5 + (ulong)(long)(int)b.U32(b.Off(entry) + 1), true);
            if (renamed is not null || target is not null)
                redirects.Add(new ImportRedirect(imp, target, renamed, viaPlt));
        }
        var chains = Chains(b, changes.Where(c => c.Kind == ChangeKind.Stub).ToList());
        foreach (var chain in chains)
            backport.AddLabel(chain[0].Address, $"stub_{chain[0].Address:x}");
        return new BackportDiff { Changes = changes, Redirects = redirects, TextGrowth = tb.Filesz - to.Filesz, StubChains = chains };

        static byte Filler(long i) => i % Patching.ExtensionBlock == 0 ? Patching.Ret : Patching.Int3;
    }

    /// <summary>Links each stub to the stubs its jumps land in; a chain starts at a stub nothing jumps to.</summary>
    static List<IReadOnlyList<CodeChange>> Chains(SceElf elf, List<CodeChange> stubs)
    {
        CodeChange? StubAt(ulong vaddr) => stubs.FirstOrDefault(s => s.Overlaps(vaddr, vaddr + 1));
        var next = new Dictionary<CodeChange, List<CodeChange>>();
        var linked = new HashSet<CodeChange>();
        foreach (var stub in stubs)
        {
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(elf.Data, (int)elf.Off(stub.Address), stub.Length), stub.Address);
            next[stub] = [];
            while (decoder.IP < stub.End)
            {
                var ins = decoder.Decode();
                if ((ins.IsJmpShortOrNear || ins.IsJccShortOrNear) && StubAt(ins.NearBranchTarget) is { } to && to != stub && linked.Add(to))
                    next[stub].Add(to);
            }
        }
        var chains = new List<IReadOnlyList<CodeChange>>();
        foreach (var head in stubs.Where(s => !linked.Contains(s)))
        {
            var chain = new List<CodeChange>();
            var todo = new Queue<CodeChange>([head]);
            while (todo.TryDequeue(out var piece))
            {
                chain.Add(piece);
                foreach (var n in next[piece])
                    todo.Enqueue(n);
            }
            chains.Add(chain);
        }
        foreach (var stub in stubs.Where(s => !chains.Any(c => c.Contains(s))))   // pieces of a jump cycle
            chains.Add([stub]);
        return chains;
    }

    static ulong? Slot(SceElf elf, ulong got)
    {
        try { return elf.ReadU64(got); }
        catch (InvalidDataException) { return null; }
    }

    static ulong? Entry(SceElf elf, Import imp)
    {
        if (Slot(elf, imp.GotVaddr) is not ulong slot || slot < 6)
            return null;
        try
        {
            long off = elf.Off(slot - 6);
            return elf.Data[off] == 0xFF && elf.Data[off + 1] == 0x25 ? slot - 6 : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
