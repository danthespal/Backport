using System.Buffers.Binary;
using Iced.Intel;
using Ps5Backport.Core;

namespace DecoderEvaluation;

internal sealed record Site(string Nid, ulong Address, ulong Target, int Length);
internal sealed record CodeRange(int Segment, int Start, int Length);
internal sealed record Scan(Dictionary<string, int> Counts, long Instructions, long Invalid, List<Site> Sites);

/// <summary>Evaluation only: linear decoding requires trusted starts/ranges, which executable segments do not provide.</summary>
internal static class ReferenceProbe
{
    internal static Scan Decode(SceElf elf, IReadOnlyList<ImportStatus> statuses, bool collectSites = false,
                                IReadOnlyList<CodeRange>? knownCode = null, CancellationToken cancel = default)
    {
        var (targets, plt, counts) = Targets(elf, statuses);
        var sites = new List<Site>();
        long instructions = 0, invalid = 0;
        foreach (var p in elf.Phdrs.Where(p => p.Type == SceElf.PtLoad && (p.Flags & SceElf.PfX) != 0))
        {
            var ranges = knownCode?.Where(r => r.Segment == p.Index)
                ?? [new CodeRange(p.Index, 0, checked((int)p.Filesz))];
            foreach (var range in ranges)
            {
                if (range.Start < 0 || range.Length < 0 || range.Start + (long)range.Length > p.Filesz)
                    throw new ArgumentException("Code range exceeds segment");
                var reader = new ByteArrayCodeReader(elf.Data, checked((int)p.Offset + range.Start), range.Length);
                var decoder = Decoder.Create(64, reader);
                decoder.IP = p.Vaddr + (ulong)range.Start;
                ulong end = decoder.IP + (ulong)range.Length, nextCheck = decoder.IP;
                while (decoder.IP < end)
                {
                    if (decoder.IP >= nextCheck) { cancel.ThrowIfCancellationRequested(); nextCheck = decoder.IP + 65536; }
                    decoder.Decode(out var instruction);
                    instructions++;
                    if (instruction.Code == Code.INVALID) { invalid++; continue; }
                    if (plt.Contains(instruction.IP) || Target(instruction) is not { } target || !targets.TryGetValue(target, out string? nid)) continue;
                    counts[nid]++;
                    if (collectSites) sites.Add(new(nid, instruction.IP, target, instruction.Length));
                }
            }
        }
        cancel.ThrowIfCancellationRequested();
        return new(counts, instructions, invalid, sites);
    }

    static ulong? Target(in Instruction i)
    {
        if (i.Mnemonic is Mnemonic.Call or Mnemonic.Jmp && i.Op0Kind == OpKind.NearBranch64)
            return i.NearBranchTarget;
        if (!i.IsIPRelativeMemoryOperand) return null;
        bool load = i.Mnemonic is Mnemonic.Mov or Mnemonic.Lea && i.Op0Kind == OpKind.Register && i.Op1Kind == OpKind.Memory;
        bool branch = i.Mnemonic is Mnemonic.Call or Mnemonic.Jmp && i.Op0Kind == OpKind.Memory;
        // LEA ignores segment bases; loads/indirect branches through FS/GS require runtime state.
        if (!load && !branch || i.Mnemonic != Mnemonic.Lea && i.MemorySegment is Register.FS or Register.GS) return null;
        return i.IPRelativeMemoryAddress;
    }

    static (Dictionary<ulong, string> Targets, HashSet<ulong> Plt, Dictionary<string, int> Counts) Targets(
        SceElf elf, IEnumerable<ImportStatus> statuses)
    {
        var targets = new Dictionary<ulong, string>();
        var plt = new HashSet<ulong>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in statuses)
        {
            targets[s.GotVaddr] = s.Nid;
            if (s.Plt) { ulong entry = elf.ReadU64(s.GotVaddr) - 6; targets[entry] = s.Nid; plt.Add(entry); }
            counts[s.Nid] = 0;
        }
        return (targets, plt, counts);
    }

    /// <summary>Site-producing mirror of the production byte heuristic; callers cross-check its counts against Core.</summary>
    internal static List<Site> HeuristicSites(SceElf elf, IReadOnlyList<ImportStatus> statuses)
    {
        var (targets, plt, _) = Targets(elf, statuses);
        var sites = new List<Site>();
        foreach (var p in elf.Phdrs.Where(p => p.Type == SceElf.PtLoad && (p.Flags & SceElf.PfX) != 0))
        {
            var bytes = elf.Data.AsSpan(checked((int)p.Offset), checked((int)p.Filesz));
            for (int offset = 0; offset < bytes.Length; offset++)
            {
                byte b = bytes[offset];
                int length = b is 0xE8 or 0xE9 ? 1 : offset + 1 < bytes.Length &&
                    ((b == 0xFF && bytes[offset + 1] is 0x15 or 0x25) || (b is 0x8B or 0x8D && (bytes[offset + 1] & 0xC7) == 0x05)) ? 2 : 0;
                int displacement = offset + length;
                if (length == 0 || displacement + 4 > bytes.Length) continue;
                ulong address = p.Vaddr + (ulong)offset;
                ulong target = p.Vaddr + (ulong)(displacement + 4) + (ulong)(long)BinaryPrimitives.ReadInt32LittleEndian(bytes[displacement..]);
                if (!plt.Contains(address) && targets.TryGetValue(target, out string? nid)) sites.Add(new(nid, address, target, length + 4));
            }
        }
        var expected = Imports.CodeReferences(elf, statuses);
        if (expected.Any(kv => kv.Value != sites.Count(s => s.Nid == kv.Key))) throw new InvalidOperationException("Heuristic mirror drifted from Core");
        return sites;
    }
}
