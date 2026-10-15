using System.Text;

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
    /// <summary>Message text for the boot_notice option (baked into the e_entry cave); empty = default.</summary>
    public string? BootNoticeMessage { get; init; }
    /// <summary>Delay before the boot_notice toast, in seconds (0 = immediate).</summary>
    public int BootNoticeDelaySeconds { get; init; } = 8;
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
    /// <summary>First later firmware that exports the function ("6.00 / firmware_db 6.00"). A stub is a GOT pre-fill: on a console
    /// from that firmware on, the loader binds the native function over it. Empty when no later list or known fact exports it.</summary>
    public string Introduced = "";
}

/// <summary>An import that the target firmware exports but a later firmware list does not: the same cook fails on that console
/// if the function is called (the loader binds unresolved imports to a trap).</summary>
public sealed class LaterMissingEntry
{
    public required string Nid, Name, Library, MissingOn;
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
    public string Path { get; internal set; } = path;
    internal bool PathCaseCorrected { get; set; }
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
    /// <summary>Imports that resolve on the target but are missing from a later firmware list (firmware_db above the target).</summary>
    public List<LaterMissingEntry> LaterMissing { get; } = [];
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
    /// <summary>Terminal engine state, excluded from legacy reports.</summary>
    public OperationState State { get; internal set; } = OperationState.Running;
    /// <summary>Successful dry-run evidence, excluded from legacy reports; null for failed analysis and cooking.</summary>
    public AnalysisReceipt? Analysis { get; internal set; }
    /// <summary>True only after a complete cook has been published. Unresolved imports can still remain.</summary>
    public bool Published { get; internal set; }
    /// <summary>Incomplete-run diagnostics, kept outside the active output; empty for successful runs and analysis.</summary>
    public string DiagnosticDirectory { get; internal set; } = "";
    /// <summary>Previous output left behind because it could not be deleted after successful replacement; normally empty.</summary>
    public string PreviousOutputBackup { get; internal set; } = "";
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
    /// <summary>The requirements probe: one verdict per optional patch from the game's own modules (<see cref="Pipeline.Probe"/>).</summary>
    public List<OptionRecommendation> Recommendations { get; } = [];
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
public static partial class Pipeline
{
    public const string ReportJson = "backport_report.json", ReportTxt = "backport_report.txt";

    public static GameResult Backport(string source, string output, BackportOptions o, Knowledge knowledge,
                                      IProgress<BackportProgress>? progress = null, CancellationToken cancel = default,
                                      AnalysisReceipt? analysis = null, DiagnosticLog? diagnostics = null) =>
        Execute(RunSnapshot.Capture(source, output, o, knowledge), progress, cancel, analysis, diagnostics);

    internal static GameResult Execute(RunSnapshot snapshot, IProgress<BackportProgress>? progress,
                                       CancellationToken cancel, AnalysisReceipt? analysis, DiagnosticLog? diagnostics = null, string? runId = null)
    {
        var trace = new DiagnosticRun(diagnostics, snapshot.Options.DryRun ? "analyze" : "cook", runId ?? Guid.NewGuid().ToString("N"), progress);
        try
        {
            var result = ExecuteCore(snapshot, trace, cancel, analysis, trace);
            trace.Finish(result.State);
            return result;
        }
        catch (OperationCanceledException e) when (cancel.IsCancellationRequested) { trace.Finish(OperationState.Cancelled, e); throw; }
        catch (Exception e) { trace.Finish(OperationState.Failed, e); throw; }
    }

    static GameResult ExecuteCore(RunSnapshot snapshot, IProgress<BackportProgress>? progress,
                                 CancellationToken cancel, AnalysisReceipt? analysis, DiagnosticRun trace)
    {
        var (source, output, o, knowledge) = snapshot;
        cancel.ThrowIfCancellationRequested();
        var (targetPs5, targetPs4) = Firmware.SdkFor(o.Firmware);
        string src = System.IO.Path.GetFullPath(source), outDir = System.IO.Path.GetFullPath(output);
        Paths.ValidateOverlay(src, outDir);
        if (!Directory.Exists(src))
            throw new DirectoryNotFoundException($"source game folder not found: {src}");
        if (!o.DryRun)
        {
            Paths.RequireSeparateTrees(src, outDir);
            Paths.RequireNotContaining(outDir, knowledge.DataDir);
            Paths.RequireNotContaining(outDir, o.ReferenceDir);
        }
        using var evidence = new RunEvidence(cancel);
        var sourceModules = GameScan.FindModules(source, cancel);
        evidence.CaptureModules(src, sourceModules);
        evidence.Capture(Path.Combine(src, "sce_sys", "param.json"), "game:sce_sys/param.json");
        foreach (var (name, hash) in knowledge.DocumentHashes)
            evidence.Capture(Path.Combine(knowledge.DataDir, name), "knowledge:" + name, hash);
        evidence.Capture(Path.Combine(src, "ampr_emu.index"), "game:ampr_emu.index");
        evidence.Capture(Path.Combine(src, "ampr_emu.index.tmp"), "game:ampr_emu.index.tmp");
        evidence.CaptureSet(o.ReferenceDir, "reference", "*.json");
        evidence.CaptureSet(o.ReferenceDir, "reference", "*.c");
        var result = new GameResult(src, outDir, o.Firmware);
        var fixes = knowledge.FixTable(o.Options);
        result.Options.AddRange(o.Options.Distinct(StringComparer.Ordinal));
        foreach (string key in result.Options)
            result.ParamWarnings.Add($"option {knowledge.Options[key].Name}: {knowledge.Options[key].Warning}");

        var param = GameScan.ReadParam(source, o.Firmware, o.PatchParam);
        result.TitleId = param.TitleId;
        result.ContentVersion = param.ContentVersion;
        result.ParamWarnings.AddRange(param.Warnings);
        if (GameScan.AmprIndexWarning(source) is { } ampr)
            result.ParamWarnings.Add(ampr);

        var (extra, modules, libraryInventories, patterns) = DiscoverModules(source, sourceModules, o, knowledge, result);
        if (extra is not null)
        {
            if (!o.DryRun) Paths.RequireSeparateTrees(extra, outDir);
            Paths.RequireNoLinks(extra, tree: true);
            var folders = modules.Where(m => m.Added).Select(m => Path.GetDirectoryName(m.Src)!).Append(extra).Distinct(StringComparer.Ordinal);
            foreach (string folder in folders)
            {
                evidence.Capture(Path.Combine(folder, FakelibStore.ManifestName), "store:" + Path.GetRelativePath(extra, folder).Replace('\\', '/') + "/manifest.json");
                if (File.Exists(Path.Combine(folder, FakelibStore.ManifestName)) && FakelibStore.Verify(folder).Count > 0)
                    throw new InvalidDataException($"selected library set changed during preflight: {folder}");
            }
            foreach (var module in modules.Where(m => m.Added))
                evidence.Capture(module.Src, "library:" + module.Rel.Replace('\\', '/'));
            foreach (var (folder, files) in libraryInventories)
                evidence.CaptureSet(folder, "store-files:" + Path.GetRelativePath(extra, folder).Replace('\\', '/'), "*", files);
            string app0Folder = Path.Combine(extra, "app0");
            evidence.CaptureTree(app0Folder, "app0");
            foreach (string label in evidence.Files.Keys.Where(k => k.StartsWith("app0:", StringComparison.Ordinal)).ToArray())
            {
                string relative = label[5..];
                if (!evidence.Files.ContainsKey("game:" + relative))
                    evidence.Capture(Paths.ResolveRelativeFile(source, relative), "game:" + relative);
            }
        }
        foreach (string relative in patterns.SelectMany(p => p.Files).Select(f => f.Replace('\\', '/')).Distinct(StringComparer.Ordinal))
            if (!evidence.Files.ContainsKey("game:" + relative))
                evidence.Capture(Paths.ResolveRelativeFile(source, relative), "game:" + relative);
        if (GameScan.DumpFakelibWarning(modules.Select(m => m.Rel)) is { } dumpFakelibs)
            result.ParamWarnings.Add(dumpFakelibs);

        var firmwareDbs = FirmwareDb.LoadAll(o.ReferenceDir);
        var reference = Imports.LoadReference(o.ReferenceDir, o.Firmware, firmwareDbs);
        result.Reference = reference.Label;
        // newer firmware databases tell when a missing function appeared
        var target = FirmwareDb.Key(o.Firmware);
        var newerDbs = firmwareDbs.Where(d => FirmwareDb.Key(d.Firmware).CompareTo(target) > 0).ToList();

        var (loaded, provided) = LoadProviders(modules, result, o, knowledge, progress, cancel, trace);
        // Capture dependency evidence before patches can rewrite imports or string data.
        var gameImports = loaded.Where(l => !l.Mod.Added).SelectMany(l => l.Elf.Imports()).Select(i => i.Nid).ToHashSet(StringComparer.Ordinal);
        bool directPsml = loaded.Any(l => l.Mod.Added &&
            string.Equals(Path.GetFileNameWithoutExtension(l.Mod.Path), "libScePsml", StringComparison.Ordinal) && l.Elf.Exports().Overlaps(gameImports));
        var dynamicPsml = loaded.Where(l => !l.Mod.Added && !directPsml)
            .Select(l => (l.Mod.Path, Evidence: DynamicPsmlEvidence(l.Elf))).Where(l => l.Evidence is not null).ToList();
        foreach (var (path, evidenceText) in dynamicPsml)
            result.ParamWarnings.Add($"possible dynamic Psml dependency in {path}: {evidenceText}; including the selected Psml library's app0 data when available (static evidence does not prove runtime use)");
        // the requirements probe reads the game's own modules before anything is patched
        result.Recommendations.AddRange(ProbeModules(loaded.Where(l => !l.Mod.Added).Select(l => (l.Mod.Path, l.Elf)).ToList(), src, knowledge, cancel));
        var sourceFiles = evidence.CaptureInventory(src);
        var pathCase = FilePathCase.Resolve(sourceFiles.Select(f => Path.GetRelativePath(src, f).Replace('\\', '/')),
            loaded.Where(m => !m.Mod.Added).Select(m => m.Elf.Data), result.ParamWarnings, cancel);
        var outputPaths = sourceFiles.Select(f => Path.GetRelativePath(src, f).Replace('\\', '/'))
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => pathCase.GetValueOrDefault(g.Key, g.Key), StringComparer.OrdinalIgnoreCase);
        foreach (var (mod, file, _) in loaded)
            if (!mod.Added && pathCase.TryGetValue(Path.GetRelativePath(src, file).Replace('\\', '/'), out string? expected))
            {
                mod.Path = expected;
                mod.PathCaseCorrected = true;
            }
        foreach (string relative in pathCase.Keys)
            if (!evidence.Files.ContainsKey("game:" + relative))
                evidence.Capture(Paths.ResolveRelativeFile(src, relative), "game:" + relative);
        string fingerprint = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
            snapshot.Fingerprint(evidence, result.FakelibSource) + string.Join('\n', sourceFiles.Select(f => Path.GetRelativePath(src, f))))));
        if (analysis is not null && analysis.Fingerprint != fingerprint)
            throw new InvalidOperationException("Analysis is stale: inputs or settings changed. Analyze again before cooking.");

        // Preflight above is read-only: invalid metadata, selected stores or references leave output untouched.
        cancel.ThrowIfCancellationRequested();
        using var publication = o.DryRun ? null : new DirectoryTransaction(outDir);
        string writeDir = publication?.Stage ?? outDir;
        if (!o.DryRun)
        {
            if (param.PatchedText is not null)
            {
                string dest = Paths.ResolveRelativeFile(writeDir, outputPaths.GetValueOrDefault("sce_sys/param.json", "sce_sys/param.json"));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
                File.WriteAllText(dest, param.PatchedText, new UTF8Encoding(false));
            }
        }

        var patchContext = new PatchContext(o, knowledge, fixes, patterns, reference, provided, newerDbs, targetPs5, targetPs4, cancel);

        // 2) patch each module
        for (int i = 0; i < loaded.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var (mod, file, elf) = loaded[i];
            progress?.Report(new BackportProgress(result.Modules.IndexOf(mod), modules.Count, mod.Path, "patching"));
            try
            {
                PatchModule(mod, elf, file, writeDir, patchContext);
            }
            catch (Exception e) when (IsModuleError(e))
            {
                mod.Error = e.Message;
                trace.ModuleError("patching", mod.Path, e);
            }
        }

        // option patterns in data files (e.g. ShaderData.bin): every hit
        foreach (var group in patterns.SelectMany(p => p.Files.Select(rel => (Rel: rel.Replace('\\', '/'), Pattern: p)))
                                      .GroupBy(p => p.Rel, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
            {
                cancel.ThrowIfCancellationRequested();
                string rel = group.Key;
                string path = Paths.ResolveRelativeFile(source, rel);
                if (!File.Exists(path))
                    continue;
                byte[] data = File.ReadAllBytes(path);
                bool changed = false;
                foreach (var (_, pattern) in group)
                {
                    int count = FixApplier.ReplaceAll(data, pattern, cancel);
                    changed |= count > 0;
                    result.DataFiles.Add(new DataFileEntry { Path = outputPaths.GetValueOrDefault(rel, rel), Pattern = pattern.Name, Count = count, Written = count > 0 && !o.DryRun });
                }
                if (changed && !o.DryRun)
                {
                    string dest = Paths.ResolveRelativeFile(writeDir, outputPaths.GetValueOrDefault(rel, rel));
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
                    File.WriteAllBytes(dest, data);
                }
            }

        // Rename-only data files are part of the overlay too; keep any already patched contents.
        var modulePaths = modules.Select(m => m.Rel.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, expected) in pathCase.Where(p => !modulePaths.Contains(p.Key)))
        {
            cancel.ThrowIfCancellationRequested();
            if (!o.DryRun)
            {
                string dest = Paths.ResolveRelativeFile(writeDir, expected);
                if (!File.Exists(dest))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(Paths.ResolveRelativeFile(src, relative), dest);
                }
            }
            result.DataFiles.Add(new DataFileEntry { Path = expected, Pattern = "file path casing", Count = 1, Written = !o.DryRun });
        }

        // the fakelib set's app0/ folder: data files its libraries read from the game folder (k9.psp, the PSSR
        // model an app0-patched libScePsml loads on a PS5 Pro). Direct imports or explicit dynamic Psml
        // evidence select the library; it must name the file as "/app0/<file>". The game's own file wins.
        string? app0 = extra is null ? null : System.IO.Path.Combine(extra, "app0");
        if (app0 is not null && Directory.Exists(app0))
        {
            var used = loaded.Where(l => l.Mod.Added && (l.Elf.Exports().Overlaps(gameImports) ||
                dynamicPsml.Count > 0 && string.Equals(Path.GetFileNameWithoutExtension(l.Mod.Path), "libScePsml", StringComparison.Ordinal)))
                .Select(l => l.Elf).ToList();
            foreach (string file in GameScan.WalkFiles(app0, cancel))
            {
                cancel.ThrowIfCancellationRequested();
                string rel = System.IO.Path.GetRelativePath(app0, file).Replace('\\', '/');
                byte[] wanted = Encoding.ASCII.GetBytes("/app0/" + rel);
                if (File.Exists(System.IO.Path.Combine(source, rel)) || !used.Any(e => e.Data.AsSpan().IndexOf(wanted) >= 0))
                    continue;
                if (!o.DryRun)
                {
                    string dest = System.IO.Path.Combine(writeDir, rel);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest, overwrite: true);
                    File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(file));
                }
                result.App0Files.Add(rel);
            }
        }

        // a dry run writes nothing: there, "unchanged" means no SDK change, fix, stub or pattern
        bool Changed(ModuleResult m) => o.DryRun ? m.PathCaseCorrected || m.SdkPatched || m.Fixed.Count + m.StubbedNow.Count + m.Patterns.Count > 0 : m.Written;
        var plain = result.Modules.Where(m => m.Container == Self.KindElf && !Changed(m) && m.Error.Length == 0).Select(m => m.Path).ToList();
        if (plain.Count > 0)
            result.ParamWarnings.Add($"plain ELF module(s) left unchanged and not in the output: {string.Join(", ", plain)}; " +
                                     "most tested backports ship fake SELFs (one plain-ELF backport ran on a PS5 Pro at FW 9.60), so sign them " +
                                     "(ps5backport sign) if the game does not start");
        cancel.ThrowIfCancellationRequested();
        FinishRun(result, o, reference, evidence, publication, writeDir, fingerprint, modules.Count, progress, cancel, knowledge);
        return result;
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

}
