namespace Ps5Backport.Core;

public static partial class Pipeline
{
    sealed record Discovery(string? Extra, List<(string Rel, string Src, bool Added)> Modules, Dictionary<string, string[]> Inventories, List<FixPattern> Patterns);

    static Discovery DiscoverModules(string source, IReadOnlyList<string> sourceModules, BackportOptions o, Knowledge knowledge, GameResult result)
    {
        if (result.Options.Contains("playgo_0_6") && result.Options.Contains("playgo_0_6_log"))
            throw new ArgumentException("Select only one PlayGo 0.6 variant (normal or logging).");
        // fakelibs: an explicit folder wins; otherwise the per-firmware store (fakelibs/<FW>/)
        string? extra = o.ExtraFakelib;
        if (extra is null && o.FakelibStore is not null)
        {
            extra = FakelibStore.Find(o.FakelibStore, o.Firmware);
            if (extra is not null)
            {
                var problems = FakelibStore.Verify(extra);
                if (problems.Count > 0)
                    throw new ArgumentException($"fakelib store {extra} failed verification: " +
                                                string.Join("; ", problems.Select(p => $"{p.Name}: {p.Problem}")));
                result.ParamWarnings.AddRange(FakelibStore.BuildWarnings(extra, knowledge));
            }
        }
        if (extra is not null && !Directory.Exists(extra))
            throw new DirectoryNotFoundException($"fakelib folder not found: {System.IO.Path.GetFullPath(extra)}");
        result.FakelibSource = extra is null ? "" : System.IO.Path.GetFullPath(extra);

        // (relative output path, source file, added) for every module to process
        var modules = sourceModules.Select(rel => (Rel: rel, Src: System.IO.Path.Combine(source, rel), Added: false)).ToList();
        var libraryInventories = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (extra is not null)
        {
            libraryInventories[extra] = Directory.GetFiles(extra).Order(StringComparer.Ordinal).ToArray();
            foreach (string file in libraryInventories[extra])
            {
                string name = System.IO.Path.GetFileName(file);
                if (!name.EndsWith(".prx", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".sprx", StringComparison.OrdinalIgnoreCase))
                    continue;
                string rel = System.IO.Path.Combine("fakelib", name);
                // an extra fakelib replaces the game's file of the same name
                modules.RemoveAll(m => string.Equals(m.Rel, rel, StringComparison.OrdinalIgnoreCase));
                modules.Add((rel, file, true));
            }
        }
        // options' fakelib variants (fakelibs/<FW>/<variant>/) replace same-named fakelibs
        foreach (string key in result.Options)
        {
            var option = knowledge.Options[key];
            if (option.FakelibVariant.Length == 0)
                continue;
            string? folder = extra is null ? null : FakelibStore.VariantFolder(extra, option.FakelibVariant);
            if (folder is null || !Directory.Exists(folder))
            {
                result.ParamWarnings.Add($"option {option.Name}: the fakelib set has no {option.FakelibVariant}/ variant; fakelibs unchanged");
                continue;
            }
            var problems = FakelibStore.Verify(folder);
            if (problems.Count > 0)
                throw new ArgumentException($"fakelib variant {folder} failed verification: " +
                                            string.Join("; ", problems.Select(p => $"{p.Name}: {p.Problem}")));
            libraryInventories[folder] = Directory.GetFiles(folder).Order(StringComparer.Ordinal).ToArray();
            foreach (string file in libraryInventories[folder])
            {
                string name = System.IO.Path.GetFileName(file);
                if (!name.EndsWith(".prx", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".sprx", StringComparison.OrdinalIgnoreCase))
                    continue;
                string rel = System.IO.Path.Combine("fakelib", name);
                modules.RemoveAll(m => string.Equals(m.Rel, rel, StringComparison.OrdinalIgnoreCase));
                modules.Add((rel, file, true));
                result.ParamWarnings.Add($"option {option.Name}: {rel.Replace(System.IO.Path.DirectorySeparatorChar, '/')} from {System.IO.Path.GetFullPath(folder)}");
            }
        }
        if (o.AmprDebugLogging)
        {
            string? folder = extra is null ? null : FakelibStore.VariantFolder(extra, "ampr_debug");
            string? file = folder is null ? null : Path.Combine(folder, "libSceAmpr.sprx");
            if (file is null || !File.Exists(file))
                throw new ArgumentException("AMPR debug logging needs an ampr_debug/libSceAmpr.sprx variant in the selected fakelib set");
            var problems = FakelibStore.Verify(folder!);
            if (problems.Count > 0)
                throw new ArgumentException($"AMPR debug fakelib variant {folder} failed verification: " +
                                            string.Join("; ", problems.Select(p => $"{p.Name}: {p.Problem}")));
            string rel = System.IO.Path.Combine("fakelib", "libSceAmpr.sprx");
            libraryInventories.TryAdd(folder!, Directory.GetFiles(folder!).Order(StringComparer.Ordinal).ToArray());
            modules.RemoveAll(m => string.Equals(m.Rel, rel, StringComparison.OrdinalIgnoreCase));
            modules.Add((rel, file, true));
            result.ParamWarnings.Add("AMPR debug logging enabled: fakelib/libSceAmpr.sprx writes /app0/ampr_emu.log; collect it after the test and do not use this build for release.");
        }
        var patterns = (o.KnownFixes ? knowledge.Patterns : []).Concat(result.Options.SelectMany(key => knowledge.Options[key].Patterns)).ToList();
        return new Discovery(extra, modules, libraryInventories, patterns);
    }
}
