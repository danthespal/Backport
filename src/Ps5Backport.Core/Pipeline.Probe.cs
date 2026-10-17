namespace Ps5Backport.Core;

/// <summary>What the analysed game says about an optional patch.</summary>
public enum OptionVerdict
{
    /// <summary>Static evidence that the game fails or misbehaves without it on the target.</summary>
    Needed,
    /// <summary>The option would change something in this game; whether the game needs it is a runtime question.</summary>
    Applicable,
    /// <summary>Nothing in this game for the option to change.</summary>
    NotApplicable,
    /// <summary>Depends on the console, the library set or the tester's intent, not on the game.</summary>
    ConsoleChoice,
}

public sealed record OptionRecommendation(string Key, OptionVerdict Verdict, string Evidence);

/// <summary>
/// The requirements probe: one verdict per optional patch from the game's own modules, before anything is patched.
/// Rules come from the option's data (<see cref="OptionProbe"/>) and shape: a <c>console</c> probe is the tester's
/// choice; a <c>detector</c> probe asks the engine recogniser; an <c>argument</c> probe reads the constant a game passes
/// to one function; otherwise an option with fixes is applicable when the game imports one of them, an option with
/// patterns when a pattern matches. Static evidence only: it does not establish console compatibility.
/// </summary>
public static partial class Pipeline
{
    /// <summary>Game-side recognisers of the engine-specific options, by option key.</summary>
    static readonly Dictionary<string, Func<SceElf, CancellationToken, bool>> Detectors = new(StringComparer.Ordinal)
    {
        [UnitySkuFallback.Option] = UnitySkuFallback.Recognize,
        [UbisoftOfflinePopups.Option] = UbisoftOfflinePopups.Recognize,
    };

    /// <summary>Probe a game folder on its own (no reference, no fakelibs): loads the game's modules and runs the probe.</summary>
    public static List<OptionRecommendation> Probe(string source, Knowledge knowledge, CancellationToken cancel = default)
    {
        string src = Path.GetFullPath(source);
        if (!Directory.Exists(src))
            throw new DirectoryNotFoundException($"source game folder not found: {src}");
        var modules = new List<(string Path, SceElf Elf)>();
        foreach (string rel in GameScan.FindModules(src, cancel))
        {
            cancel.ThrowIfCancellationRequested();
            string file = Path.Combine(src, rel);
            string kind = Self.FileKind(file);
            byte[] data;
            if (kind == Self.KindElf) data = File.ReadAllBytes(file);
            else if (Self.IsSelf(kind) && Self.EncryptedSegments(file) == 0) data = Self.Unwrap(File.ReadAllBytes(file));
            else continue;                                              // encrypted or unknown: the cook reports it
            try { modules.Add((rel.Replace(Path.DirectorySeparatorChar, '/'), new SceElf(data))); }
            catch (InvalidDataException) { }                            // malformed modules are module errors of the cook
        }
        return ProbeModules(modules, src, knowledge, cancel);
    }

    /// <summary>The probe over already loaded game modules (the fakelibs are not part of the game's requirements).</summary>
    internal static List<OptionRecommendation> ProbeModules(IReadOnlyList<(string Path, SceElf Elf)> modules, string source,
                                                            Knowledge knowledge, CancellationToken cancel)
    {
        var imports = modules.Select(m => (m.Path, Imports: m.Elf.Imports())).ToList();
        bool psml = imports.Any(m => m.Imports.Any(i => i.Library.StartsWith("libScePsml", StringComparison.Ordinal)))
                    || modules.Any(m => DynamicPsmlEvidence(m.Elf) is not null);
        var result = new List<OptionRecommendation>();
        foreach (var option in knowledge.Options.Values)
        {
            cancel.ThrowIfCancellationRequested();
            var nids = option.Fixes.Values.Where(f => f.Kind != Fix.KindNone).Select(f => f.Nid).ToHashSet(StringComparer.Ordinal);
            var imported = imports.SelectMany(m => m.Imports.Where(i => nids.Contains(i.Nid)).Select(i => (m.Path, i.Nid)))
                                  .Distinct().ToList();
            string Names(IEnumerable<string> set) => string.Join(", ", set.Select(n => option.Fixes[n].Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
            int patternHits = option.Patterns.Count == 0 ? 0 : PatternHits(option.Patterns, modules, source, cancel);
            string importsText = imported.Count == 0 ? $"none of its {nids.Count} function(s) is imported"
                : $"imports {imported.Select(i => i.Nid).Distinct().Count()} of its {nids.Count} function(s): {Names(imported.Select(i => i.Nid))}";
            switch (option.Probe.Kind)
            {
                case OptionProbe.KindConsole:
                    if (nids.Count > 0 && imported.Count == 0)
                        result.Add(new(option.Key, OptionVerdict.NotApplicable, importsText));
                    else if (option.Key == BootNotice.Option && BootNotice.Assess(modules) is var (host, evidence))
                        result.Add(new(option.Key, host is not null ? OptionVerdict.ConsoleChoice : OptionVerdict.NotApplicable, evidence));
                    else if (option.Key.StartsWith("psml", StringComparison.Ordinal))
                        result.Add(new(option.Key, psml ? OptionVerdict.ConsoleChoice : OptionVerdict.NotApplicable,
                            psml ? "the game uses libScePsml (PS5 Pro only)" : "the game does not use libScePsml"));
                    else
                        result.Add(new(option.Key, OptionVerdict.ConsoleChoice, nids.Count > 0 ? importsText : "depends on the console or the tester's intent, not on the game"));
                    break;
                case OptionProbe.KindDetector:
                    if (!Detectors.TryGetValue(option.Key, out var recognize))
                        throw new InvalidDataException($"option '{option.Key}' declares a detector probe, but the engine has no recogniser for it");
                    var recognized = modules.Where(m => recognize(m.Elf, cancel)).Select(m => m.Path).ToList();
                    result.Add(recognized.Count == 0
                        ? new(option.Key, OptionVerdict.NotApplicable, "no module has the recognised code layout")
                        : new(option.Key, option.Probe.Recognized == OptionProbe.Needed ? OptionVerdict.Needed : OptionVerdict.Applicable,
                              $"recognised code layout in {string.Join(", ", recognized)}"));
                    break;
                case OptionProbe.KindArgument:
                    if (imported.Count == 0) { result.Add(new(option.Key, OptionVerdict.NotApplicable, importsText)); break; }
                    var sites = new List<(string Module, ulong Site, uint? Constant)>();
                    foreach (var (path, elf) in modules)
                        foreach (var import in elf.Imports().Where(i => i.Nid == option.Probe.Nid && i.Plt))
                            sites.AddRange(Imports.ArgumentConstants(elf, import, option.Probe.Argument, cancel).Select(s => (path, s.Site, s.Constant)));
                    string register = Imports.ArgumentRegister(option.Probe.Argument);
                    var above = sites.Where(s => s.Constant is { } c && (int)c > option.Probe.Above).ToList();
                    int unknown = sites.Count(s => s.Constant is null);
                    if (above.Count > 0)
                        result.Add(new(option.Key, OptionVerdict.Needed,
                            $"{option.Fixes[option.Probe.Nid].Name} is called with {register} = {string.Join("/", above.Select(s => s.Constant!.Value).Distinct().Order())} " +
                            $"(above {option.Probe.Above}) at {above.Count} of {sites.Count} call site(s), e.g. {above[0].Module} 0x{above[0].Site:x}; decoded call-site evidence"));
                    else if (unknown > 0)
                        result.Add(new(option.Key, OptionVerdict.Applicable,
                            $"{option.Fixes[option.Probe.Nid].Name} is imported; {register} is not a readable constant at {unknown} of {sites.Count} call site(s)"));
                    else
                        result.Add(new(option.Key, OptionVerdict.NotApplicable,
                            $"{option.Fixes[option.Probe.Nid].Name} is called with {register} = {string.Join("/", sites.Select(s => s.Constant!.Value).Distinct().Order())} only (not above {option.Probe.Above}) at {sites.Count} call site(s)"));
                    break;
                default:
                    if (nids.Count == 0 && option.Patterns.Count > 0)
                        result.Add(new(option.Key, patternHits > 0 ? OptionVerdict.Applicable : OptionVerdict.NotApplicable,
                            patternHits > 0 ? $"its byte pattern(s) match {patternHits} time(s)" : "its byte pattern(s) do not match"));
                    else if (nids.Count > 0 && imported.Count == 0)
                        result.Add(new(option.Key, OptionVerdict.NotApplicable, importsText));
                    else if (nids.Count > 0 && option.Patterns.Count > 0)
                        result.Add(new(option.Key, patternHits > 0 ? OptionVerdict.Applicable : OptionVerdict.NotApplicable,
                            patternHits > 0 ? $"{importsText}; its engine pattern(s) match {patternHits} time(s)" : $"{importsText}, but its engine pattern(s) do not match"));
                    else if (nids.Count > 0)
                        result.Add(new(option.Key, OptionVerdict.Applicable, importsText));
                    else
                        result.Add(new(option.Key, OptionVerdict.ConsoleChoice, "depends on the console or the tester's intent, not on the game"));
                    break;
            }
        }
        return result;
    }

    /// <summary>Matches of an option's patterns in the game modules (as the cook would apply them) and in its data files.</summary>
    static int PatternHits(IReadOnlyList<FixPattern> patterns, IReadOnlyList<(string Path, SceElf Elf)> modules, string source, CancellationToken cancel)
    {
        int hits = 0;
        foreach (var pattern in patterns)
        {
            foreach (var (_, elf) in modules)
                hits += FixApplier.CountPattern(elf, pattern, cancel);
            foreach (string relative in pattern.Files)
            {
                string path = Paths.ResolveRelativeFile(source, relative);
                if (File.Exists(path))
                    hits += FixApplier.CountMatches(File.ReadAllBytes(path), pattern, cancel);
            }
        }
        return hits;
    }
}
