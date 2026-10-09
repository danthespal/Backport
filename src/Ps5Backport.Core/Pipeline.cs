using System.Text;
using System.Text.Json.Nodes;

namespace Ps5Backport.Core;

/// <summary>Options of a whole-game backport (the flags of `ps5backport backport`).</summary>
public sealed record BackportOptions
{
    /// <summary>Target firmware, e.g. "4" or "4.03".</summary>
    public required string Firmware { get; init; }
    /// <summary>firmware_db folder or a genstub .c folder.</summary>
    public required string ReferenceDir { get; init; }
    /// <summary>Folder of extra fakelibs; overrides the store.</summary>
    public string? ExtraFakelib { get; init; }
    /// <summary>Per-firmware fakelib store, used when <see cref="ExtraFakelib"/> is not given (null = off).</summary>
    public string? FakelibStore { get; init; }
    public bool StubUnknown { get; init; }
    public IReadOnlyList<string> StubNids { get; init; } = [];
    public bool DefineSymbol { get; init; }
    public bool KnownFixes { get; init; } = true;
    public bool PatchParam { get; init; }
    public bool CandidateFixes { get; init; }
    /// <summary>Grow a code segment into the page gap when a module has no padding left for fix code (default on).</summary>
    public bool ExtendText { get; init; } = true;
    /// <summary>Opt-in fix groups (keys of data/fixes.json "options").</summary>
    public IReadOnlyList<string> Options { get; init; } = [];
    /// <summary>Replace libSceAmpr with the logging-enabled ampr_emu variant for a diagnostic run.</summary>
    public bool AmprDebugLogging { get; init; }
    /// <summary>Analyze only: run every step but write nothing (no modules, no param.json, no reports).</summary>
    public bool DryRun { get; init; }
    public NameDb? Names { get; init; }
}

public sealed class FixedEntry
{
    public required string Nid, Name, How, Source;
    public bool Proven;
}

public sealed class StubbedEntry
{
    public required string Nid, Name, Stub, Got, At, Origin;
    public bool KnownFix;
}

public sealed class UnresolvedEntry
{
    public required string Nid, Name, Library, Candidate, Status, LeftBy, Introduced;
    public int CodeRefs;
}

/// <summary>One module of a backport (field order = backport_report.json).</summary>
/// <summary>An option pattern applied to a module: name and number of replacements.</summary>
public sealed class PatternEntry
{
    public required string Name;
    public int Count;
}

/// <summary>A non-module file patched by an option pattern (e.g. ShaderData.bin).</summary>
public sealed class DataFileEntry
{
    public required string Path, Pattern;
    public int Count;
    public bool Written;
}

public sealed class ModuleResult(string path, string container, bool added)
{
    public string Path { get; } = path;
    public string Container { get; } = container;
    /// <summary>Comes from the fakelib store / --fakelib, not from the game folder.</summary>
    public bool Added { get; } = added;
    public string SdkBefore { get; set; } = "";
    public string SdkAfter { get; set; } = "";
    public bool SdkPatched { get; set; }
    /// <summary>Sony system library: build id and the firmware it shipped with ("J03828141 = FW 12.70").</summary>
    public string SonyBuild { get; set; } = "";
    /// <summary>Status -> count, in order of first appearance.</summary>
    public List<KeyValuePair<string, int>> Counts { get; } = [];
    public List<UnresolvedEntry> Unknown { get; } = [];
    public List<StubbedEntry> StubbedNow { get; } = [];
    public List<FixedEntry> Fixed { get; } = [];
    public List<PatternEntry> Patterns { get; } = [];
    public long TextExtended { get; set; }
    /// <summary>Every import's final status (for the GUI; not part of backport_report.json).</summary>
    public List<ImportStatus> Statuses { get; set; } = [];
    public bool Written { get; set; }
    public string Error { get; set; } = "";
}

/// <summary>A whole-game backport (field order = backport_report.json).</summary>
/// <summary>PS VR2 imports of one game module from one library (distinct NIDs).</summary>
public sealed record Psvr2Entry(string Module, string Library, int Imports);

public sealed class GameResult(string source, string output, string targetFirmware)
{
    public string Source { get; } = source;
    public string Output { get; } = output;
    public string TargetFirmware { get; } = targetFirmware;
    public string TitleId { get; set; } = "";
    public string ContentVersion { get; set; } = "";
    public List<string> ParamWarnings { get; } = [];
    public string FakelibSource { get; set; } = "";
    public string Reference { get; set; } = "";
    /// <summary>PS VR2 library imports of the game's own modules.</summary>
    public List<Psvr2Entry> Psvr2 { get; } = [];
    /// <summary>Selected opt-in fix groups.</summary>
    public List<string> Options { get; } = [];
    public List<DataFileEntry> DataFiles { get; } = [];
    /// <summary>Data files added from the fakelib set's app0/ (e.g. k9.psp for an app0-patched libScePsml).</summary>
    public List<string> App0Files { get; } = [];
    public List<ModuleResult> Modules { get; } = [];
}

/// <summary>Progress of a backport: module index (0-based), module count, the module and what is happening.</summary>
public readonly record struct BackportProgress(int Index, int Count, string Module, string Stage);

/// <summary>
/// Whole-game backport. The source game folder is never modified; the output
/// folder gets only the files that changed or were added, in the same relative layout, which works as a
/// ShadowMountPlus overlay (&lt;scan_path&gt;/backports/&lt;TITLE_ID&gt;/), plus backport_report.txt/.json.
/// </summary>
public static class Pipeline
{
    public const string ReportJson = "backport_report.json", ReportTxt = "backport_report.txt";

    public static GameResult Backport(string source, string output, BackportOptions o, Knowledge knowledge,
                                      IProgress<BackportProgress>? progress = null, CancellationToken cancel = default)
    {
        var (targetPs5, targetPs4) = Firmware.SdkFor(o.Firmware);
        string src = System.IO.Path.GetFullPath(source), outDir = System.IO.Path.GetFullPath(output);
        if (outDir == src || outDir.StartsWith(src + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("output folder must not be the game folder or inside it");
        var result = new GameResult(src, outDir, o.Firmware);
        var fixes = knowledge.FixTable(o.Options);
        if (!o.DryRun)
            Directory.CreateDirectory(output);
        result.Options.AddRange(o.Options.Distinct(StringComparer.Ordinal));
        foreach (string key in result.Options)
            result.ParamWarnings.Add($"option {knowledge.Options[key].Name}: {knowledge.Options[key].Warning}");

        var param = GameScan.ReadParam(source, o.Firmware, o.PatchParam);
        result.TitleId = param.TitleId;
        result.ContentVersion = param.ContentVersion;
        result.ParamWarnings.AddRange(param.Warnings);
        if (param.PatchedText is not null && !o.DryRun)
        {
            string dest = System.IO.Path.Combine(output, "sce_sys", "param.json");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
            File.WriteAllText(dest, param.PatchedText, new UTF8Encoding(false));
        }
        if (GameScan.AmprIndexWarning(source) is { } ampr)
            result.ParamWarnings.Add(ampr);

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
        result.FakelibSource = extra is null ? "" : System.IO.Path.GetFullPath(extra);

        // (relative output path, source file, added) for every module to process
        var modules = GameScan.FindModules(source).Select(rel => (Rel: rel, Src: System.IO.Path.Combine(source, rel), Added: false)).ToList();
        if (extra is not null)
        {
            foreach (string file in Directory.EnumerateFiles(extra).Order(StringComparer.Ordinal))
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
            foreach (string file in Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal))
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
            modules.RemoveAll(m => string.Equals(m.Rel, rel, StringComparison.OrdinalIgnoreCase));
            modules.Add((rel, file, true));
            result.ParamWarnings.Add("AMPR debug logging enabled: fakelib/libSceAmpr.sprx writes /app0/ampr_emu.log; collect it after the test and do not use this build for release.");
        }
        var patterns = (o.KnownFixes ? knowledge.Patterns : []).Concat(result.Options.SelectMany(key => knowledge.Options[key].Patterns)).ToList();
        if (GameScan.DumpFakelibWarning(modules.Select(m => m.Rel)) is { } dumpFakelibs)
            result.ParamWarnings.Add(dumpFakelibs);

        var reference = Imports.LoadReference(o.ReferenceDir, o.Firmware);
        result.Reference = reference.Label;
        // newer firmware databases tell when a missing function appeared
        var target = FirmwareDb.Key(o.Firmware);
        var newerDbs = Directory.Exists(o.ReferenceDir)
            ? FirmwareDb.LoadAll(o.ReferenceDir).Where(d => FirmwareDb.Key(d.Firmware).CompareTo(target) > 0).ToList()
            : [];

        // 1) unwrap everything, so every module's exports can serve as providers
        var loaded = new List<(ModuleResult Mod, string Src, SceElf Elf)>();
        for (int i = 0; i < modules.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var (rel, file, added) = modules[i];
            var mod = new ModuleResult(rel.Replace(System.IO.Path.DirectorySeparatorChar, '/'), Self.FileKind(file), added);
            result.Modules.Add(mod);
            progress?.Report(new BackportProgress(i, modules.Count, mod.Path, "loading"));
            try
            {
                byte[] data;
                if (mod.Container == Self.KindElf)
                    data = File.ReadAllBytes(file);
                else if (Self.IsSelf(mod.Container))
                {
                    int encrypted = Self.EncryptedSegments(file);
                    if (encrypted > 0)
                        throw new InvalidDataException($"{encrypted} encrypted SELF segment(s): this is a retail-encrypted file; " +
                                                       "decrypt the game on a console first (PS5-SELF-Decrypter) and use that dump");
                    data = Self.Unwrap(File.ReadAllBytes(file));
                }
                else
                    throw new InvalidDataException("unknown file format");
                loaded.Add((mod, file, new SceElf(data) { AllowTextExtension = o.ExtendText }));
            }
            catch (Exception e) when (IsModuleError(e))
            {
                if (mod.Path.Equals(RightsModule, StringComparison.OrdinalIgnoreCase))
                {
                    // not needed to run: a tested backport shipped it zeroed and others leave it out; the overlay keeps
                    // the dump's own, so an unreadable one is a warning, not a module error
                    result.Modules.Remove(mod);
                    result.ParamWarnings.Add($"{mod.Path} could not be read ({e.Message}); left out of the backport, the dump's " +
                                             "own copy stays in use (the game does not need it to start)");
                }
                else
                    mod.Error = e.Message;
            }
        }

        var provided = loaded.SelectMany(l => l.Elf.Exports()).ToHashSet(StringComparer.Ordinal);

        CheckPsvr2(loaded.Select(l => (l.Mod, l.Elf)), result);

        // the game's own fakelibs (not replaced by the store or --fakelib): known-broken homebrew builds
        foreach (var (mod, _, elf) in loaded)
            if (!mod.Added && mod.Path.StartsWith("fakelib/", StringComparison.OrdinalIgnoreCase)
                && FakelibStore.BuildDateOf(elf) is { } date && knowledge.Build(elf.ModuleName, date) is { State: "broken" } build)
                result.ParamWarnings.Add($"{mod.Path} ({elf.ModuleName} built {date}) is a known-broken build: {build.Note}");

        // 2) patch each module
        for (int i = 0; i < loaded.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var (mod, file, elf) = loaded[i];
            progress?.Report(new BackportProgress(result.Modules.IndexOf(mod), modules.Count, mod.Path, "patching"));
            try
            {
                PatchModule(mod, elf, file, outDir, o, knowledge, fixes, patterns, reference, provided, newerDbs, targetPs5, targetPs4);
            }
            catch (Exception e) when (IsModuleError(e))
            {
                mod.Error = e.Message;
            }
        }

        // option patterns in data files (e.g. ShaderData.bin): every hit
        foreach (var pattern in patterns)
            foreach (string rel in pattern.Files)
            {
                string path = System.IO.Path.Combine(source, rel);
                if (!File.Exists(path))
                    continue;
                byte[] data = File.ReadAllBytes(path);
                int count = FixApplier.ReplaceAll(data, pattern);
                if (count > 0 && !o.DryRun)
                {
                    string dest = System.IO.Path.Combine(output, rel);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
                    File.WriteAllBytes(dest, data);
                }
                result.DataFiles.Add(new DataFileEntry { Path = rel.Replace('\\', '/'), Pattern = pattern.Name, Count = count, Written = count > 0 && !o.DryRun });
            }

        // the fakelib set's app0/ folder: data files its libraries read from the game folder (k9.psp, the PSSR
        // model an app0-patched libScePsml loads on a PS5 Pro). Added only when a fakelib the game uses (one that
        // exports a function a game module imports) names the file as "/app0/<file>"; the game's own file wins.
        string? app0 = extra is null ? null : System.IO.Path.Combine(extra, "app0");
        if (app0 is not null && Directory.Exists(app0))
        {
            var gameImports = loaded.Where(l => !l.Mod.Added).SelectMany(l => l.Elf.Imports()).Select(i => i.Nid).ToHashSet(StringComparer.Ordinal);
            var used = loaded.Where(l => l.Mod.Added && l.Elf.Exports().Overlaps(gameImports)).Select(l => l.Elf).ToList();
            foreach (string file in GameScan.WalkFiles(app0))
            {
                string rel = System.IO.Path.GetRelativePath(app0, file).Replace('\\', '/');
                byte[] wanted = Encoding.ASCII.GetBytes("/app0/" + rel);
                if (File.Exists(System.IO.Path.Combine(source, rel)) || !used.Any(e => e.Data.AsSpan().IndexOf(wanted) >= 0))
                    continue;
                if (!o.DryRun)
                {
                    string dest = System.IO.Path.Combine(output, rel);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest, overwrite: true);
                    File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(file));
                }
                result.App0Files.Add(rel);
            }
        }

        // a dry run writes nothing: there, "unchanged" means no SDK change, fix, stub or pattern
        bool Changed(ModuleResult m) => o.DryRun ? m.SdkPatched || m.Fixed.Count + m.StubbedNow.Count + m.Patterns.Count > 0 : m.Written;
        var plain = result.Modules.Where(m => m.Container == Self.KindElf && !Changed(m) && m.Error.Length == 0).Select(m => m.Path).ToList();
        if (plain.Count > 0)
            result.ParamWarnings.Add($"plain ELF module(s) left unchanged and not in the output: {string.Join(", ", plain)}; " +
                                     "most tested backports ship fake SELFs (one plain-ELF backport ran on a PS5 Pro at FW 9.60), so sign them " +
                                     "(ps5backport sign) if the game does not start");
        if (!o.DryRun)
            WriteReports(result, output);
        progress?.Report(new BackportProgress(modules.Count, modules.Count, "", "done"));
        return result;
    }

    static bool IsModuleError(Exception e) => e is PatchException or InvalidDataException or IOException or ArgumentException;

    static void PatchModule(ModuleResult mod, SceElf elf, string file, string output, BackportOptions o, Knowledge k,
                            IReadOnlyDictionary<string, Fix> fixes, List<FixPattern> patterns, Reference reference, HashSet<string> allProvided, List<FirmwareExports> newerDbs,
                            uint targetPs5, uint targetPs4)
    {
        bool changed = false;
        var (ps5, ps4) = Sdk.Read(elf);
        mod.SdkBefore = $"0x{ps5:x8}/0x{ps4:x8}";
        if (ps5 > targetPs5)
        {
            changed |= Sdk.Write(elf, targetPs5, targetPs4);
            mod.SdkPatched = true;
        }
        (ps5, ps4) = Sdk.Read(elf);
        mod.SdkAfter = $"0x{ps5:x8}/0x{ps4:x8}";
        mod.SonyBuild = k.SonyBuild(elf.BuildPath);

        var provided = allProvided.Except(elf.Exports()).ToHashSet(StringComparer.Ordinal);
        var statuses = Imports.Classify(elf, reference.Nids, provided, reference.Exact);
        var imported = statuses.Select(s => s.Nid).ToHashSet(StringComparer.Ordinal);
        var unknown = statuses.Where(s => Imports.IsUnresolved(s.Status)).Select(s => s.Nid).ToList();
        var unknownPlt = statuses.Where(s => Imports.IsUnresolved(s.Status) && s.Plt).Select(s => s.Nid).ToHashSet(StringComparer.Ordinal);

        // 1) known fixes (Fixes + selected options): for missing imports, plus argument fixes ('always') for
        //    functions that exist on old firmware but reject new values
        var untouched = statuses.Where(s => s.Plt && s.Status is Imports.Reference or Imports.Provided or Imports.Unknown or Imports.Missing)
                                .Select(s => s.Nid).ToHashSet(StringComparer.Ordinal);
        var known = statuses.Select(s => s.Nid).Where(n =>
            o.KnownFixes && fixes.TryGetValue(n, out var f) && FixApplier.Applicable(elf, n, k, fixes) && f.Due(o.Firmware)
            && ((f.When == Fix.WhenMissing && unknownPlt.Contains(n)) || (f.When == Fix.WhenAlways && untouched.Contains(n)))).ToList();
        foreach (var (nid, how) in FixApplier.Apply(elf, known.Where(n => fixes[n].Kind != Fix.KindStub).ToList(), k, fixes))
        {
            var f = fixes[nid];
            mod.Fixed.Add(new FixedEntry { Nid = nid, Name = f.Name, How = how, Source = string.Join(", ", f.Sources), Proven = f.Proven });
            changed = true;
        }

        // 2) stubs: known stub fixes + requested NIDs + (optionally) every other unknown import
        var toStub = known.Where(n => fixes[n].Kind == Fix.KindStub).ToList();
        if (o.CandidateFixes)   // reference-derived (AnyPS5 silent stubs), opt-in
            toStub.AddRange(unknown.Where(n => unknownPlt.Contains(n) && k.Candidates.ContainsKey(n) && !fixes.ContainsKey(n)));
        toStub.AddRange(o.StubNids.Where(imported.Contains));
        if (o.StubUnknown)
            toStub.AddRange(unknown.Where(n => (unknownPlt.Contains(n) || o.DefineSymbol)
                                               && !(fixes.TryGetValue(n, out var f) && f.Kind != Fix.KindStub && known.Contains(n))));
        toStub = toStub.Distinct(StringComparer.Ordinal)
                       .Where(n => !statuses.Any(s => s.Nid == n && s.Status == Imports.Stubbed)).ToList();
        if (toStub.Count > 0)
        {
            var rules = FixApplier.StubRules(k);
            foreach (string n in toStub)
                if (k.Candidates.TryGetValue(n, out var c) && !k.Fixes.ContainsKey(n))
                    rules[n] = $"ret:0x{c.Value:x}";
            foreach (var p in Patching.StubImports(elf, toStub, k, rules, o.DefineSymbol))
            {
                string origin = k.Fixes.TryGetValue(p.Nid, out var f) ? (f.Proven ? "known fix, proven" : "known fix, derived")
                              : k.Candidates.ContainsKey(p.Nid) ? "candidate (AnyPS5), derived" : "requested";
                mod.StubbedNow.Add(new StubbedEntry
                {
                    Nid = p.Nid, Name = Name(o.Names, p.Nid), Stub = p.Stub, Got = $"0x{p.GotVaddr:x}", At = $"0x{p.StubVaddr:x}",
                    KnownFix = k.Fixes.ContainsKey(p.Nid), Origin = origin,
                });
            }
            changed = true;
        }
        foreach (var pattern in patterns)
        {
            int count = FixApplier.ApplyPattern(elf, pattern);
            if (count > 0)
            {
                mod.Patterns.Add(new PatternEntry { Name = pattern.Name, Count = count });
                changed = true;
            }
        }
        if (changed)
            statuses = Imports.Classify(elf, reference.Nids, provided, reference.Exact);

        foreach (var s in statuses)
        {
            int at = mod.Counts.FindIndex(kv => kv.Key == s.Status);
            if (at < 0)
                mod.Counts.Add(new(s.Status, 1));
            else
                mod.Counts[at] = new(s.Status, mod.Counts[at].Value + 1);
        }
        var unresolved = statuses.Where(s => Imports.IsUnresolved(s.Status)).ToList();
        var refs = unresolved.Count > 0 ? Imports.CodeReferences(elf, unresolved) : [];
        foreach (var s in unresolved)
        {
            var intro = FirmwareDb.Introduced(s.Nid, newerDbs, k);
            mod.Unknown.Add(new UnresolvedEntry
            {
                Nid = s.Nid, Name = Name(o.Names, s.Nid), Library = s.Library,
                Candidate = k.Candidates.TryGetValue(s.Nid, out var c) ? $"return 0x{c.Value:x8} ({c.Source})" : "",
                Status = s.Status, CodeRefs = refs[s.Nid],
                LeftBy = string.Join(", ", k.LeftUnpatched.GetValueOrDefault(s.Nid) ?? []),
                Introduced = intro is { } x ? $"{x.Firmware} / {x.Source}" : "",
            });
        }

        mod.TextExtended = elf.TextExtended;
        mod.Statuses = statuses;
        if (o.DryRun)
            return;
        string dest = System.IO.Path.Combine(output, mod.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (changed)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, Self.Sign(elf.Data));   // always a fake-signed SELF: the console loads SELFs, also for a decrypted (plain ELF) dump
            mod.Written = true;
        }
        else if (mod.Added)   // unchanged extra fakelib: copy verbatim, keep its container
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
            File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(file));
            mod.Written = true;
        }
    }

    static string Name(NameDb? names, string nid) => names?.Name(nid) ?? nid;

    // --- reports -----------------------------------------------------------------
    /// <summary>The text report.</summary>
    public static string FormatReport(GameResult r)
    {
        var lines = new List<string>
        {
            $"Backport of {r.Source}",
            $"  title {r.TitleId} version {r.ContentVersion} -> FW {r.TargetFirmware}",
            $"  output: {r.Output}",
            $"  reference: {r.Reference}",
            $"  fakelibs: {(r.FakelibSource.Length > 0 ? r.FakelibSource : "none (no --fakelib and no store folder for this firmware)")}",
        };
        if (r.Options.Count > 0)
            lines.Add("  options: " + string.Join(", ", r.Options.Select(key => $"{key} ({Knowledge.Default.Options[key].Name})")));
        foreach (string f in r.App0Files)
            lines.Add($"  added file {f} (from the fakelib set)");
        foreach (var d in r.DataFiles)
            lines.Add($"  data file {d.Path}: {d.Pattern} x{d.Count} ({(d.Written ? "written" : "no match")})");
        lines.AddRange(r.ParamWarnings.Select(w => $"  WARNING: {w}"));
        lines.Add("  refs = direct code references to an unresolved import (0: reachable only through data, e.g. a vtable, or not at all)");
        var derived = r.Modules.SelectMany(m => m.Fixed).Where(f => !f.Proven).Select(f => f.Name)
            .Union(r.Modules.SelectMany(m => m.StubbedNow).Where(s => s.Origin.Contains("derived")).Select(s => s.Name))
            .Order(StringComparer.Ordinal).ToList();
        if (derived.Count > 0)
            lines.Add($"  NOTE: {derived.Count} derived fix(es) applied, not yet console-tested: {string.Join(", ", derived)}");
        foreach (var m in r.Modules)
        {
            lines.Add("");
            lines.Add($"{m.Path}  [{m.Container}]  {(m.Error.Length > 0 ? "ERROR" : m.Written ? "written" : "unchanged")}");
            if (m.Error.Length > 0)
            {
                lines.Add($"  error: {m.Error}");
                continue;
            }
            lines.Add(m.SdkPatched ? $"  SDK {m.SdkBefore} -> {m.SdkAfter}" : $"  SDK {m.SdkBefore} (kept)");
            if (m.SonyBuild.Length > 0)
                lines.Add($"  Sony build {m.SonyBuild}");
            lines.Add("  imports: " + string.Join(", ", m.Counts.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}")));
            if (m.TextExtended != 0)
                lines.Add($"  NOTE: code segment extended by 0x{m.TextExtended:x} bytes for fix code (automatic when padding runs out; two backports grew Unity PS5Util.prx the same way; not console-confirmed)");
            foreach (var f in m.Fixed)
                lines.Add($"  fixed    {f.Nid}  {f.Name}  ({f.How}; {(f.Proven ? "proven" : "DERIVED, not console-tested")}; from {f.Source})");
            foreach (var s in m.StubbedNow)
                lines.Add($"  stubbed  {s.Nid}  {s.Name}  ({s.Stub} @ {s.At}; {s.Origin})");
            foreach (var p in m.Patterns)
                lines.Add($"  pattern  {p.Name}  x{p.Count}");
            foreach (var u in m.Unknown)
            {
                string hint = u.Candidate.Length > 0 ? $"   candidate: {u.Candidate} (--candidate-fixes)" : "";
                string intro = u.Introduced.Length > 0 ? $"   introduced: FW {u.Introduced}" : "";
                string left = u.LeftBy.Length > 0 ? $"   left unpatched by: {u.LeftBy}" : "";
                lines.Add($"  {u.Status,-8} {u.Nid}  {u.Name}  [{u.Library}]  refs={u.CodeRefs}{hint}{intro}{left}");
            }
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// sce_sys/about/right.sprx (libSceGameRight): lowered like any module when its SDK is above the target (GT7's is 10.00;
    /// its tested backport lowered it), but an unreadable copy is only a warning (UFC 6 ran with an all-zero one).
    /// </summary>
    const string RightsModule = "sce_sys/about/right.sprx";

    /// <summary>PS VR2 system libraries (headset, reprojection, tracking): only games that use the headset import them.</summary>
    public static readonly string[] Psvr2Libraries = ["libSceHmd2", "libSceVrTracker2"];

    /// <summary>Records the game's own PS VR2 imports (per module and library, distinct NIDs) and warns once.</summary>
    static void CheckPsvr2(IEnumerable<(ModuleResult Mod, SceElf Elf)> loaded, GameResult result)
    {
        foreach (var (mod, elf) in loaded)
        {
            if (mod.Added)
                continue;
            var libs = elf.Imports().Where(i => Psvr2Libraries.Any(p => i.Library.StartsWith(p, StringComparison.Ordinal)))
                .GroupBy(i => i.Library, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal);
            foreach (var g in libs)
                result.Psvr2.Add(new Psvr2Entry(mod.Path, g.Key, g.Select(i => i.Nid).Distinct(StringComparer.Ordinal).Count()));
        }
        if (result.Psvr2.Count > 0)
            result.ParamWarnings.Add($"uses PS VR2 ({string.Join(", ", result.Psvr2.Select(p => $"{p.Module} {p.Library} x{p.Imports}"))}): " +
                "a VR-only game cannot run below the firmware that supports the headset; " +
                "a game with an optional VR mode usually runs without it. Check before backporting to an old firmware");
    }

    /// <summary>The JSON report.</summary>
    public static JsonObject ToJson(GameResult r) => new()
    {
        ["source"] = r.Source, ["output"] = r.Output, ["target_firmware"] = r.TargetFirmware,
        ["title_id"] = r.TitleId, ["content_version"] = r.ContentVersion,
        ["param_warnings"] = new JsonArray(r.ParamWarnings.Select(w => (JsonNode)w).ToArray()),
        ["fakelib_source"] = r.FakelibSource, ["reference"] = r.Reference,
        ["psvr2"] = new JsonArray(r.Psvr2.Select(p => (JsonNode)new JsonObject
        {
            ["module"] = p.Module, ["library"] = p.Library, ["imports"] = p.Imports,
        }).ToArray()),
        ["options"] = new JsonArray(r.Options.Select(key => (JsonNode)key).ToArray()),
        ["app0_files"] = new JsonArray(r.App0Files.Select(f => (JsonNode)f).ToArray()),
        ["data_files"] = new JsonArray(r.DataFiles.Select(d => (JsonNode)new JsonObject
        {
            ["path"] = d.Path, ["pattern"] = d.Pattern, ["count"] = d.Count, ["written"] = d.Written,
        }).ToArray()),
        ["modules"] = new JsonArray(r.Modules.Select(m => (JsonNode)new JsonObject
        {
            ["path"] = m.Path, ["container"] = m.Container, ["added"] = m.Added,
            ["sdk_before"] = m.SdkBefore, ["sdk_after"] = m.SdkAfter, ["sdk_patched"] = m.SdkPatched, ["sony_build"] = m.SonyBuild,
            ["counts"] = new JsonObject(m.Counts.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))),
            ["unknown"] = new JsonArray(m.Unknown.Select(u => (JsonNode)new JsonObject
            {
                ["nid"] = u.Nid, ["name"] = u.Name, ["library"] = u.Library, ["candidate"] = u.Candidate, ["status"] = u.Status,
                ["code_refs"] = u.CodeRefs, ["left_by"] = u.LeftBy, ["introduced"] = u.Introduced,
            }).ToArray()),
            ["stubbed_now"] = new JsonArray(m.StubbedNow.Select(s => (JsonNode)new JsonObject
            {
                ["nid"] = s.Nid, ["name"] = s.Name, ["stub"] = s.Stub, ["got"] = s.Got, ["at"] = s.At,
                ["known_fix"] = s.KnownFix, ["origin"] = s.Origin,
            }).ToArray()),
            ["fixed"] = new JsonArray(m.Fixed.Select(f => (JsonNode)new JsonObject
            {
                ["nid"] = f.Nid, ["name"] = f.Name, ["how"] = f.How, ["source"] = f.Source, ["proven"] = f.Proven,
            }).ToArray()),
            ["patterns"] = new JsonArray(m.Patterns.Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["count"] = p.Count }).ToArray()),
            ["text_extended"] = m.TextExtended, ["written"] = m.Written, ["error"] = m.Error,
        }).ToArray()),
    };

    /// <summary>
    /// backport_report.json and backport_report.txt. Line endings are the platform's.
    /// </summary>
    public static void WriteReports(GameResult r, string output)
    {
        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(System.IO.Path.Combine(output, ReportJson), PyJson.Serialize(ToJson(r), 2).Replace("\n", Environment.NewLine), utf8);
        File.WriteAllText(System.IO.Path.Combine(output, ReportTxt), (FormatReport(r) + "\n").Replace("\n", Environment.NewLine), utf8);
    }
}
