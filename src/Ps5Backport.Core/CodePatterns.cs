namespace Ps5Backport.Core;

/// <summary>Where a rule's byte pattern or an engine recogniser matches a module: Source is "default rule" or "option KEY";
/// Code: in an executable segment (else in data, e.g. shader bytecode).</summary>
public sealed record DetectorMatch(string Name, string Source, ulong Vaddr, int Length, bool Code)
{
    public ulong End => Vaddr + (ulong)Length;
}

/// <summary>
/// Every place the rule data's byte patterns (data/fixes.json, default and per option) and the engine's recognisers (Unity
/// unset-SKU, Ubisoft offline popups) match a module, found without writing: the same searches the cook and the
/// requirements probe use (README "Code graph page").
/// </summary>
public static class Detectors
{
    public static List<DetectorMatch> Find(SceElf elf, Knowledge knowledge, CancellationToken cancel = default)
    {
        var result = new List<DetectorMatch>();
        void Add(IEnumerable<FixPattern> patterns, string source)
        {
            foreach (var p in patterns)
                foreach (long offset in FixApplier.FindPattern(elf, p, cancel))
                    result.Add(new DetectorMatch(p.Name, source, elf.Vaddr(offset), p.Find.Length, p.Code));
        }
        Add(knowledge.Patterns, "default rule");
        foreach (var option in knowledge.Options.Values)
            Add(option.Patterns, $"option {option.Key}");
        if (UnitySkuFallback.Locate(elf, cancel) is var (entry, _))
            result.Add(new DetectorMatch(UnitySkuFallback.Name, $"option {UnitySkuFallback.Option}", elf.Vaddr(entry), 6, true));
        if (UbisoftOfflinePopups.Locate(elf, cancel) is var (at, _, _))
            result.Add(new DetectorMatch(UbisoftOfflinePopups.Name, $"option {UbisoftOfflinePopups.Option}", elf.Vaddr(at),
                                         UbisoftOfflinePopups.DefinitionsLength, true));
        return result;
    }
}
