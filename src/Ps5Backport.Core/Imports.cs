using System.Buffers.Binary;
using System.Text;

namespace Ps5Backport.Core;

public sealed record ImportStatus(string Nid, string Library, ulong GotVaddr, bool Plt, string Status);

/// <summary>The export list imports are checked against, and whether it is exact for the target.</summary>
public sealed record Reference(HashSet<string> Nids, string Label, bool Exact);

/// <summary>
/// Classify a module's imports against what the target firmware provides.
///   reference  exported by the reference firmware (lower bound: present on the target)
///   provided   exported by a module shipped with the game (fakelib/, sce_module/, game prx)
///   stubbed    neutralised: GOT slot points at local code, stub code written into the PLT entry,
///              or the symbol rewritten into a local definition
///   patched    PLT entry jumps to a cave or local function (adapter, redirect, argument fix)
///   data       data import; reference lists contain functions only, so unchecked
///   missing    not exported by the target firmware (exact firmware_db): needs a fix
///   unknown    none of the above, with a lower-bound reference: possibly missing on the target
/// </summary>
public static class Imports
{
    public const string Reference = "reference", Provided = "provided", Stubbed = "stubbed", Patched = "patched";
    public const string Data = "data", Missing = "missing", Unknown = "unknown";

    /// <summary>Statuses a fix may target.</summary>
    public static readonly string[] Unresolved = [Unknown, Missing];
    public static bool IsUnresolved(string status) => status is Unknown or Missing;

    /// <summary>Report column order.</summary>
    public static readonly string[] AllStatuses = [Reference, Provided, Stubbed, Patched, Data, Missing, Unknown];

    const byte PushImm32 = 0x68;   // an untouched lazy-bind PLT stub starts with 'push <index>'
    const byte JmpRel32 = 0xE9;    // a redirected PLT entry starts with 'jmp rel32'

    /// <summary>
    /// The reference for a target firmware. <paramref name="path"/> is a firmware_db folder (per-firmware
    /// JSON) or a folder of genstub .c stubs (always a lower bound).
    /// </summary>
    public static Reference LoadReference(string path, string target)
        => LoadReference(path, target, FirmwareDb.LoadAll(path));

    /// <summary>Uses run-owned metadata rather than parsing the same databases a second time.</summary>
    internal static Reference LoadReference(string path, string target, IReadOnlyList<FirmwareExports> databases)
    {
        if (databases.Count > 0)
        {
            var (db, exact) = FirmwareDb.Select(databases, target);
            if (db is null)
                throw new ArgumentException($"firmware_db {path} has no firmware at or below {target}");
            return new Reference(db.Nids, $"firmware_db {db.Firmware} ({(exact ? "exact" : "lower bound")} for FW {target})", exact);
        }
        return new Reference(FirmwareDb.LoadGenstubExports(path), $"genstub stubs {path} (lower bound)", false);
    }

    /// <summary>NIDs exported by unwrapped ELF modules shipped with the game.</summary>
    public static HashSet<string> LoadProviderExports(IEnumerable<string> paths)
    {
        var nids = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in paths)
            nids.UnionWith(SceElf.Load(path).Exports());
        return nids;
    }

    /// <summary>
    /// One status per imported NID (a NID can have many relocations; the PLT one is preferred).
    /// With exact (the reference is the target firmware's own export list) an import that is neither
    /// there nor provided is 'missing'; with a lower-bound reference it is only 'unknown'.
    /// </summary>
    public static List<ImportStatus> Classify(SceElf elf, IReadOnlySet<string> reference,
                                              IReadOnlySet<string>? provided = null, bool exact = false)
    {
        // first import per NID in order of appearance, replaced by the first PLT one (keeps the position)
        var order = new List<string>();
        var unique = new Dictionary<string, Import>(StringComparer.Ordinal);
        foreach (var imp in elf.Imports())
        {
            if (!unique.TryGetValue(imp.Nid, out var seen))
            {
                order.Add(imp.Nid);
                unique[imp.Nid] = imp;
            }
            else if (imp.Plt && !seen.Plt)
                unique[imp.Nid] = imp;
        }

        var result = new List<ImportStatus>(order.Count);
        foreach (string nid in order)
        {
            var imp = unique[nid];
            string status;
            if (imp.DefinedLocally)
                status = Stubbed;
            else if (imp.SymType == SceElf.SttObject)
                status = Data;
            else if (imp.Plt && elf.Data[elf.Off(elf.ReadU64(imp.GotVaddr))] != PushImm32)
                status = Stubbed;    // GOT slot points at local code (GT7 eboot / fakelibs)
            else if (imp.Plt && elf.Data[elf.Off(elf.ReadU64(imp.GotVaddr) - 6)] == JmpRel32)
                status = Patched;    // PLT entry jumps to a cave or local function
            else if (imp.Plt && !IsJmpViaGot(elf, elf.ReadU64(imp.GotVaddr) - 6))
                status = Stubbed;    // stub code written into the PLT entry itself (CONTROL Resonant)
            else if (reference.Contains(nid))
                status = Reference;
            else if (provided?.Contains(nid) == true)
                status = Provided;
            else
                status = exact ? Missing : Unknown;
            result.Add(new ImportStatus(nid, imp.Library, imp.GotVaddr, imp.Plt, status));
        }
        return result;
    }

    /// <summary>An untouched PLT entry starts with 'jmp [rip+GOT]' (ff 25).</summary>
    static bool IsJmpViaGot(SceElf elf, ulong vaddr)
    {
        long off = elf.Off(vaddr);
        return elf.Data[off] == 0xFF && elf.Data[off + 1] == 0x25;
    }

    /// <summary>
    /// {NID: number of instructions in executable segments that call, jump to or load the import's
    /// PLT entry or GOT slot}: call/jmp rel32, call/jmp [rip+disp32], mov/lea reg, [rip+disp32].
    /// A byte-pattern scan (no disassembly): chance matches, prefixes and unsupported encodings can
    /// produce false positives or missed references. Zero does not prove an import is unused; data
    /// pointers and indirect control flow are not resolved. Positive counts do not prove reachability.
    /// </summary>
    public static Dictionary<string, int> CodeReferences(SceElf elf, IEnumerable<ImportStatus> statuses)
        => CodeReferences(elf, statuses, default);

    /// <summary>The same heuristic reference scan, checking cancellation every 64 KiB of code.</summary>
    public static Dictionary<string, int> CodeReferences(SceElf elf, IEnumerable<ImportStatus> statuses, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var targets = new Dictionary<ulong, string>();
        var pltEntries = new HashSet<ulong>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in statuses)
        {
            targets[s.GotVaddr] = s.Nid;
            if (s.Plt)
            {
                ulong entry = elf.ReadU64(s.GotVaddr) - 6;
                targets[entry] = s.Nid;
                pltEntries.Add(entry);
            }
            counts[s.Nid] = 0;
        }
        foreach (var p in elf.Phdrs)
        {
            if (p.Type != SceElf.PtLoad || (p.Flags & SceElf.PfX) == 0)
                continue;
            var seg = elf.Data.AsSpan(checked((int)p.Offset), checked((int)p.Filesz));
            for (int i = 0; i < seg.Length; i++)
            {
                if ((i & 65535) == 0) cancel.ThrowIfCancellationRequested();
                byte b = seg[i];
                int opcodeLength;
                if (b is 0xE8 or 0xE9)
                    opcodeLength = 1;
                else if (i + 1 < seg.Length && ((b == 0xFF && seg[i + 1] is 0x15 or 0x25) ||
                                                (b is 0x8B or 0x8D && (seg[i + 1] & 0xC7) == 0x05)))
                    opcodeLength = 2;
                else
                    continue;
                int dispAt = i + opcodeLength;
                if (dispAt + 4 > seg.Length)
                    continue;
                ulong target = p.Vaddr + (ulong)(dispAt + 4) + (ulong)(long)BinaryPrimitives.ReadInt32LittleEndian(seg[dispAt..]);
                if (targets.TryGetValue(target, out string? nid) && !pltEntries.Contains(p.Vaddr + (ulong)i))
                    counts[nid]++;
            }
        }
        return counts;
    }

    /// <summary>The per-module text report.</summary>
    public static string Report(string name, SceElf elf, IReadOnlyList<ImportStatus> statuses, NameDb names,
                                IReadOnlyCollection<string>? show = null)
    {
        show ??= [Missing, Unknown, Stubbed, Patched];
        var sb = new StringBuilder();
        sb.Append($"== {name}  module={Py(elf.ModuleName)}\n   build: {Py(elf.BuildPath)}\n   ");
        sb.Append(string.Join(", ", AllStatuses.Select(k => $"{k}={statuses.Count(s => s.Status == k)}")));
        var byLibrary = statuses.Where(s => show.Contains(s.Status)).GroupBy(s => s.Library)
                                .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var library in byLibrary)
        {
            sb.Append($"\n   {library.Key}:");
            foreach (var s in library.OrderBy(s => s.Status, StringComparer.Ordinal).ThenBy(s => names.Name(s.Nid), StringComparer.Ordinal))
                sb.Append($"\n      {s.Status,-8} {s.Nid}  {names.Name(s.Nid)}  (GOT 0x{s.GotVaddr:x})");
        }
        return sb.ToString();
    }

    /// <summary>Python's str() of an optional string (None for null).</summary>
    static string Py(string? s) => s ?? "None";
}
