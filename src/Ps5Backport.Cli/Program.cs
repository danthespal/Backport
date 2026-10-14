using System.Globalization;
using Ps5Backport.Core;

// ps5backport: the command line of PS5 Backport Studio.
using var diagnostics = new DiagnosticLog(DiagnosticLog.DefaultDirectory("cli"));
int exitCode = Cli.Run(args, Console.Out, Console.Error, diagnostics);
if (diagnostics.Failure is { } diagnosticFailure) Console.Error.WriteLine($"warning: local diagnostics unavailable: {diagnosticFailure}");
return exitCode;

public static class Cli
{
    const string Usage = """
        usage: ps5backport [--aerolib FILE] <command> [args]
          info FILE...                             file kind; for ELFs type, module, SDK, import/export counts
          unwrap SELF ELF                          SELF -> ELF (keeps the original ELF header)
          sign ELF SELF                            ELF -> fake-signed SELF (make_fself defaults)
          sdk ELF OUT (--firmware FW | --ps5 V --ps4 V)
          nid NAME...                              NID of each symbol name
          crash-log LOG [--report JSON] [--json]    local fatal-signal analysis; inputs are read-only
          report ELF... [--reference DIR] [--firmware FW] [--provider ELF]... [--all] [--provider-audit]
          stub ELF OUT [--nid NID]... [--unknown] [--firmware FW] [--define-symbol] [--reference DIR] [--provider ELF]...
          backport GAME OUT --firmware FW [--patch-param] [--fakelib DIR] [--fakelib-store DIR] [--no-fakelib-store]
                   [--stub-nid NID]... [--stub-unknown] [--define-symbol] [--no-known-fixes] [--candidate-fixes]
                   [--option KEY]... [--boot-notice TEXT] [--boot-notice-delay SECONDS] [--no-extend-text] [--reference DIR]
          fwdb import-genstub|import-dump DIR --firmware FW --source TEXT [--db DIR]
          fwdb list [--db DIR]  |  fwdb diff OLD NEW [--db DIR]
          fwdb import-firmwares [FW...] [--dumps DIR] [--db DIR]   every (or the given) 'Firmware X.YY' dump folder
          data sony-builds [--dumps DIR]           regenerate data/sony_builds.json from the firmware dumps
          data candidates [--anyps5 DIR]           regenerate data/candidates.json from AnyPS5's "Silent stubs"
          bps info PATCH...  |  bps apply PATCH INPUT OUTPUT [--sign]
          fakelib add FILE... --firmware FW [--source TEXT] [--variant NAME] [--store DIR]
          fakelib list|verify [--firmware FW] [--store DIR]  |  fakelib check --firmware FW [--store DIR] [--reference DIR]
        """;

    static readonly HashSet<string> Flags =
    [
        "--all", "--unknown", "--define-symbol", "--patch-param", "--no-fakelib-store", "--stub-unknown",
        "--no-known-fixes", "--candidate-fixes", "--extend-text", "--no-extend-text", "--sign", "--provider-audit", "--json",
    ];

    /// <summary>Positionals plus options (`--name value`, `--name=value`, repeatable; flags take no value).</summary>
    sealed class Args
    {
        public List<string> Positional { get; } = [];
        readonly Dictionary<string, List<string>> _options = [];

        public Args(IEnumerable<string> tokens)
        {
            var queue = new Queue<string>(tokens);
            while (queue.Count > 0)
            {
                string t = queue.Dequeue();
                if (!t.StartsWith("--", StringComparison.Ordinal) || t == "--")
                {
                    Positional.Add(t);
                    continue;
                }
                int eq = t.IndexOf('=');
                string name = eq < 0 ? t : t[..eq];
                string value = eq >= 0 ? t[(eq + 1)..]
                             : Flags.Contains(name) ? "true"
                             : queue.Count > 0 ? queue.Dequeue()
                             : throw new ArgumentException($"{name} needs a value");
                if (!_options.TryGetValue(name, out var list))
                    _options[name] = list = [];
                list.Add(value);
            }
        }

        public string? Get(string name) => _options.TryGetValue(name, out var v) ? v[^1] : null;
        public string Get(string name, string fallback) => Get(name) ?? fallback;
        public bool Has(string name) => _options.ContainsKey(name);
        public List<string> All(string name) => _options.GetValueOrDefault(name) ?? [];
    }

    static string RepoPath(params string[] parts) => Paths.FindUp(Path.Combine(parts)) ?? Path.Combine(parts);
    static string DefaultAerolib => Paths.AerolibPath ?? RepoPath("data", "aerolib.csv");
    static string DefaultReference => Paths.FirmwareDbDir ?? RepoPath(".original", "reference", "PS5-3.20_Libs");
    static string DefaultStore => Paths.FakelibStoreDir ?? "fakelibs";
    static string DefaultDb => Paths.FirmwareDbDir ?? "firmware_db";
    /// <summary>Where fwdb imports write: the repository firmware_db (not the bundled copy) when run inside the repository.</summary>
    public static string ImportDb => Paths.FindUp("Ps5Backport.slnx") is { } solution
        ? Path.Combine(Path.GetDirectoryName(solution)!, "firmware_db") : DefaultDb;
    static string DefaultDumps => RepoPath(".original", ".PS5OriginalLibs");

    static NameDb Names(Args a)
    {
        string path = a.Get("--aerolib", DefaultAerolib);
        return File.Exists(path) ? new NameDb(path) : new NameDb();
    }

    static string Py(string? s) => s ?? "None";

    public static int Run(string[] argv, TextWriter output, TextWriter error, DiagnosticLog? diagnostics = null)
    {
        try
        {
            var a = new Args(argv);
            if (a.Positional.Count == 0)
            {
                error.Write(Usage);
                return 2;
            }
            string command = a.Positional[0];
            var p = a.Positional.Skip(1).ToList();
            switch (command)
            {
                case "info" when p.Count > 0: Info(p, output); return 0;
                case "unwrap" when p.Count == 2:
                    Self.UnwrapFile(p[0], p[1]);
                    output.WriteLine($"unwrapped {p[0]} -> {p[1]}");
                    return 0;
                case "sign" when p.Count == 2:
                    Self.SignFile(p[0], p[1]);
                    output.WriteLine($"signed {p[0]} -> {p[1]}");
                    return 0;
                case "sdk" when p.Count == 2: return SdkCommand(a, p, output);
                case "nid" when p.Count > 0:
                    foreach (string name in p)
                        output.WriteLine($"{Nid.Compute(name)}  {name}");
                    return 0;
                case "report" when p.Count > 0: return Report(a, p, output);
                case "crash-log" when p.Count == 1: return CrashLogCommand(a, p[0], output);
                case "stub" when p.Count == 2: return Stub(a, p, output);
                case "backport" when p.Count == 2: return Backport(a, p, output, diagnostics);
                case "fwdb" when p.Count > 0: return FwdbCommand(a, p, output);
                case "bps" when p.Count > 0: return BpsCommand(a, p, output);
                case "fakelib" when p.Count > 0: return FakelibCommand(a, p, output);
                case "data" when p.Count == 1: return DataCommand(a, p[0], output);
                default:
                    error.Write(Usage);
                    return 2;
            }
        }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidDataException or IOException
                                    or PatchException or KeyNotFoundException or UnauthorizedAccessException or OverflowException
                                    or System.Text.Json.JsonException)
        {
            diagnostics?.Write(new("cli", "failed", "command", Message: e.Message, ErrorCategory: DiagnosticLog.Category(e),
                ExceptionType: e.GetType().FullName, ExceptionContext: e.ToString()));
            error.WriteLine($"error: {e.Message}");
            return 1;
        }
    }

    static int CrashLogCommand(Args a, string path, TextWriter output)
    {
        var result = CrashLog.AnalyzeFiles(path, a.Get("--report"));
        if (a.Has("--json")) output.WriteLine(result.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
        {
            output.WriteLine($"log: {result["logPath"]} (SHA256 {result["logSha256"]})");
            if (result["reportPath"] is not null) output.WriteLine($"report: {result["reportPath"]} (SHA256 {result["reportSha256"]}; identity unverified)");
            output.WriteLine($"parsing: {result["nulBytesIgnoredForParsing"]} NUL bytes ignored; {result["decodeReplacementCharacters"]} replacement characters");
            foreach (var ev in result["events"]!.AsArray())
            {
                output.WriteLine($"fatal event at line {ev!["line"]}: pid={ev["pid"]}, signal={ev["signal"]}, reason={ev["reason"]}");
                foreach (var item in ev["addresses"]!.AsArray())
                {
                    output.WriteLine($"  {item!["kind"]} {item["runtimeAddress"]}: {item["resolution"]}" +
                        (item["runtimeModule"] is null ? "" : $"; {item["runtimeModule"]} + {item["textRelativeOffset"]}"));
                    if (item["reportModuleCandidates"] is { } candidates)
                        foreach (var candidate in candidates.AsArray()) output.WriteLine($"    unverified report candidate: {candidate!["path"]}");
                }
            }
            if (result["events"]!.AsArray().Count == 0) output.WriteLine("No recognized fatal events.");
            foreach (var warning in result["warnings"]!.AsArray()) output.WriteLine($"warning at line {warning!["line"]}: {warning["warning"]}");
            output.WriteLine(result["coordinateNote"]);
            output.WriteLine(result["reportIdentity"]);
            output.WriteLine(result["cause"]);
        }
        return 0;
    }

    static void Info(List<string> files, TextWriter o)
    {
        foreach (string path in files)
        {
            string kind = Self.FileKind(path);
            o.WriteLine($"{path}: {kind}");
            if (kind != Self.KindElf)
                continue;
            var elf = SceElf.Load(path);
            var (ps5, ps4) = Sdk.Read(elf);
            o.WriteLine($"   type=0x{elf.Type:x} module={Py(elf.ModuleName)} PS5 SDK=0x{ps5:x8} PS4 SDK=0x{ps4:x8}");
            o.WriteLine($"   build: {Py(elf.BuildPath)}");
            o.WriteLine($"   {elf.Imports().Count} imports, {elf.Exports().Count} exports");
        }
    }

    /// <summary>--boot-notice-delay in seconds: sceKernelUsleep takes a 32-bit microsecond count (at most 4294 s).</summary>
    static int BootNoticeDelay(string s)
    {
        uint seconds = ParseInt0(s);
        if (seconds > uint.MaxValue / 1000000)
            throw new ArgumentException($"--boot-notice-delay must be 0 to {uint.MaxValue / 1000000} seconds");
        return (int)seconds;
    }

    static uint ParseInt0(string s)
    {
        s = s.Trim();
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : uint.Parse(s, CultureInfo.InvariantCulture);
    }

    static int SdkCommand(Args a, List<string> p, TextWriter o)
    {
        var elf = SceElf.Load(p[0]);
        var old = Sdk.Read(elf);
        (uint Ps5, uint Ps4) sdk;
        if (a.Get("--firmware") is { } fw)
            sdk = Firmware.SdkFor(fw);
        else if (a.Get("--ps5") is { } ps5 && a.Get("--ps4") is { } ps4)
            sdk = (ParseInt0(ps5), ParseInt0(ps4));
        else
            throw new ArgumentException("sdk needs --firmware, or --ps5 with --ps4");
        Sdk.Write(elf, sdk.Ps5, sdk.Ps4);
        elf.Save(p[1]);
        o.WriteLine($"PS5 SDK 0x{old.Ps5:x8} -> 0x{sdk.Ps5:x8}, PS4 SDK 0x{old.Ps4:x8} -> 0x{sdk.Ps4:x8}: {p[1]}");
        return 0;
    }

    static int Report(Args a, List<string> files, TextWriter o)
    {
        var names = Names(a);
        var reference = Imports.LoadReference(a.Get("--reference", DefaultReference), a.Get("--firmware", "4"));
        o.WriteLine($"reference: {reference.Label}");
        o.WriteLine();
        var provided = Imports.LoadProviderExports(a.All("--provider"));
        var auditProviders = a.Has("--provider-audit")
            ? a.All("--provider").Select(path => (Module: Path.GetFullPath(path), Elf: SceElf.Load(path))).ToList()
            : [];
        string[] show = a.Has("--all")
            ? [Imports.Missing, Imports.Unknown, Imports.Stubbed, Imports.Patched, Imports.Provided, Imports.Data]
            : [Imports.Missing, Imports.Unknown, Imports.Stubbed, Imports.Patched];
        foreach (string path in files)
        {
            var elf = SceElf.Load(path);
            o.WriteLine(Imports.Report(Path.GetFileName(path), elf, Imports.Classify(elf, reference.Nids, provided, reference.Exact), names, show));
            if (a.Has("--provider-audit"))
                o.WriteLine(Imports.ProviderReport(Imports.AuditProviders(Path.GetFullPath(path), elf, auditProviders)));
            o.WriteLine();
        }
        return 0;
    }

    static int Stub(Args a, List<string> p, TextWriter o)
    {
        var names = Names(a);
        var k = Knowledge.Default;
        var elf = SceElf.Load(p[0]);
        var nids = a.All("--nid").ToList();
        if (a.Has("--unknown"))
        {
            var reference = Imports.LoadReference(a.Get("--reference", DefaultReference), a.Get("--firmware", "4"));
            var provided = Imports.LoadProviderExports(a.All("--provider"));
            nids.AddRange(Imports.Classify(elf, reference.Nids, provided, reference.Exact)
                .Where(s => Imports.IsUnresolved(s.Status) && s.Plt && !nids.Contains(s.Nid)).Select(s => s.Nid));
        }
        foreach (var s in Patching.StubImports(elf, nids, k, FixApplier.StubRules(k), a.Has("--define-symbol")))
            o.WriteLine($"   GOT 0x{s.GotVaddr:x} -> {s.Stub} @ 0x{s.StubVaddr:x}   {names.Name(s.Nid)}");
        elf.Save(p[1]);
        o.WriteLine($"{nids.Count} imports stubbed: {p[1]}");
        return 0;
    }

    static int Backport(Args a, List<string> p, TextWriter o, DiagnosticLog? diagnostics)
    {
        string fw = a.Get("--firmware") ?? throw new ArgumentException("backport needs --firmware");
        var options = new BackportOptions
        {
            Firmware = fw,
            ReferenceDir = a.Get("--reference", DefaultReference),
            ExtraFakelib = a.Get("--fakelib"),
            FakelibStore = a.Has("--no-fakelib-store") ? null : a.Get("--fakelib-store", DefaultStore),
            StubUnknown = a.Has("--stub-unknown"),
            StubNids = a.All("--stub-nid"),
            DefineSymbol = a.Has("--define-symbol"),
            KnownFixes = !a.Has("--no-known-fixes"),
            PatchParam = a.Has("--patch-param"),
            CandidateFixes = a.Has("--candidate-fixes"),
            ExtendText = !a.Has("--no-extend-text"),   // --extend-text: accepted, now the default
            Options = a.All("--option"),
            BootNoticeMessage = a.Get("--boot-notice"),
            BootNoticeDelaySeconds = BootNoticeDelay(a.Get("--boot-notice-delay", "8")),
            Names = Names(a),
        };
        var result = Pipeline.Backport(p[0], p[1], options, Knowledge.Default, diagnostics: diagnostics);
        o.WriteLine(Pipeline.FormatReport(result));
        int written = result.Modules.Count(m => m.Written), errors = result.Modules.Count(m => m.Error.Length > 0);
        o.WriteLine();
        if (result.Published)
            o.WriteLine($"{written} files written, {errors} errors; report: {Path.Combine(p[1], Pipeline.ReportTxt)}");
        else
            o.WriteLine($"Output not published: {errors} module errors; diagnostics: {result.DiagnosticDirectory}");
        return errors > 0 ? 1 : 0;
    }

    static int FwdbCommand(Args a, List<string> p, TextWriter o)
    {
        string action = p[0];
        string db = a.Get("--db", action.StartsWith("import-", StringComparison.Ordinal) ? ImportDb : DefaultDb);
        if (action == "import-firmwares")
        {
            string dumps = a.Get("--dumps", DefaultDumps);
            Directory.CreateDirectory(db);
            foreach (string fw in p.Count > 1 ? p.Skip(1) : DataGenerators.DumpedFirmwares(dumps))
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var (exports, problem) = DataGenerators.ImportFirmwareDump(dumps, fw);
                if (exports is null)
                {
                    o.WriteLine($"{fw}: SKIPPED, {problem}");
                    continue;
                }
                string path = Path.Combine(db, $"{exports.Firmware}.json");
                FirmwareDb.Save(exports, path);
                o.WriteLine($"{fw}: {exports.Modules.Count} modules, {exports.Nids.Count} NIDs, {new FileInfo(path).Length / 1024} KB, " +
                            $"{watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s");
            }
            return 0;
        }
        if (action is "import-genstub" or "import-dump")
        {
            if (p.Count < 2 || a.Get("--firmware") is not { } fw || a.Get("--source") is not { } source)
                throw new ArgumentException($"fwdb {action} needs a folder, --firmware and --source");
            var exports = action == "import-genstub" ? FirmwareDb.ImportGenstub(p[1], fw, source) : FirmwareDb.ImportDump(p[1], fw, source);
            if (exports.Modules.Count == 0)
                throw new InvalidDataException("no modules found; existing firmware database left unchanged");
            Directory.CreateDirectory(db);
            string path = Path.Combine(db, $"{exports.Firmware}.json");
            FirmwareDb.Save(exports, path);
            o.WriteLine($"{exports.Firmware}: {exports.Modules.Count} modules, {exports.Nids.Count} NIDs " +
                        $"({(exports.DataSymbols ? "functions and data" : "functions only")}) -> {path}");
            return 0;
        }
        var dbs = FirmwareDb.LoadAll(db);
        if (action == "list")
        {
            foreach (var d in dbs)
                o.WriteLine($"{d.Firmware,-6} {d.Modules.Count,4} modules {d.Nids.Count,6} NIDs  " +
                            $"{(d.DataSymbols ? "functions+data" : "functions only")}  {d.Source}");
            return 0;
        }
        if (action != "diff" || p.Count != 3)
            throw new ArgumentException("fwdb: import-genstub | import-dump | import-firmwares | list | diff OLD NEW");
        var byFw = dbs.ToDictionary(d => d.Firmware);
        string oldFw = Firmware.Name(p[1]), newFw = Firmware.Name(p[2]);
        if (!byFw.ContainsKey(oldFw) || !byFw.ContainsKey(newFw))
            throw new ArgumentException($"fwdb diff needs both firmwares in {db} (have: {string.Join(", ", byFw.Keys)})");
        var names = Names(a);
        var added = FirmwareDb.Diff(byFw[oldFw], byFw[newFw]);
        foreach (var (module, nids) in added.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            o.WriteLine($"{module}: +{nids.Count}");
            foreach (string nid in nids.OrderBy(names.Name, StringComparer.Ordinal))
                o.WriteLine($"   {nid}  {names.Name(nid)}");
        }
        o.WriteLine($"{added.Values.Sum(v => v.Count)} NIDs exported by {newFw} but not by {oldFw}");
        return 0;
    }

    /// <summary>A file in the repository's data/, not the copy bundled beside the executable.</summary>
    static string RepoDataFile(string command, string name) => Paths.FindUp("Ps5Backport.slnx") is { } solution
        ? Path.Combine(Path.GetDirectoryName(solution)!, "data", name)
        : throw new DirectoryNotFoundException($"data {command} must run inside the repository (Ps5Backport.slnx not found above the app folder)");

    static int DataCommand(Args a, string what, TextWriter o)
    {
        switch (what)
        {
            case "sony-builds":
            {
                string path = RepoDataFile(what, "sony_builds.json");
                o.WriteLine($"{DataGenerators.WriteSonyBuilds(a.Get("--dumps", DefaultDumps), path)} build ids -> {path}");
                return 0;
            }
            case "candidates":
            {
                string path = RepoDataFile(what, "candidates.json");
                var found = DataGenerators.WriteCandidates(a.Get("--anyps5", RepoPath(".original", "reference", "AnyPS5")), path);
                o.WriteLine($"{found.Count} candidates written to {path}");
                foreach (var c in found)
                    o.WriteLine($"  {c.Nid} {c.Name,-48} -> 0x{c.Value:x8}  ({c.Source})");
                return 0;
            }
            default:
                throw new ArgumentException("data: sony-builds | candidates");
        }
    }

    static int BpsCommand(Args a, List<string> p, TextWriter o)
    {
        if (p[0] == "info")
        {
            foreach (string path in p.Skip(1))
            {
                var patch = Bps.Load(path);
                o.WriteLine($"== {path}: 0x{patch.SourceSize:x} -> 0x{patch.TargetSize:x} bytes, source CRC32 {patch.SourceCrc:x8}, {patch.Actions.Count} actions");
                foreach (var e in Bps.Edits(patch))
                {
                    string what = e.Kind == "write" ? Convert.ToHexStringLower(e.Data) : $"from 0x{e.FromOffset:x}";
                    o.WriteLine($"   0x{e.Offset:x7}  {e.Kind,-11} {e.Length,4}  {what}");
                }
            }
            return 0;
        }
        if (p[0] != "apply" || p.Count != 4)
            throw new ArgumentException("bps apply needs PATCH INPUT OUTPUT");
        string patchPath = p[1], input = p[2], dest = p[3];
        byte[] source = File.ReadAllBytes(input);
        if (Self.IsSelf(Self.Kind(source)))           // the patches expect the decrypted ELF
            source = Self.Unwrap(source);
        byte[] patched = Bps.Apply(Bps.Load(patchPath), source);
        Self.WriteAtomic(dest, a.Has("--sign") ? Self.Sign(patched) : patched);
        o.WriteLine($"patched {input} -> {dest}" + (a.Has("--sign") ? " (fake-signed)" : ""));
        return 0;
    }

    static int FakelibCommand(Args a, List<string> p, TextWriter o)
    {
        string store = a.Get("--store", DefaultStore);
        string? fw = a.Get("--firmware");
        var k = Knowledge.Default;
        switch (p[0])
        {
            case "add":
            {
                if (fw is null || p.Count < 2)
                    throw new ArgumentException("fakelib add needs --firmware and at least one file");
                var (folder, added) = FakelibStore.Add(store, fw, p.Skip(1), a.Get("--source", ""), a.Get("--variant", ""));
                foreach (var (name, e) in added)
                {
                    string container = e["container"]!.GetValue<string>();
                    o.WriteLine($"   {name,-34} {container[..Math.Min(14, container.Length)],-14} SDK {e["ps5_sdk"]}/{e["ps4_sdk"]}  {e["sha256"]!.GetValue<string>()[..12]}");
                }
                o.WriteLine($"{added.Count} files added to {folder}");
                return 0;
            }
            case "check":
                return FakelibCheck(a, store, fw, o, k);
            case "list" or "verify":
                break;
            default:
                throw new ArgumentException("fakelib: add | list | verify | check");
        }
        var map = FakelibStore.List(store);
        if (fw is not null)
        {
            map = new SortedDictionary<string, System.Text.Json.Nodes.JsonObject>(StringComparer.Ordinal);
            if (FakelibStore.Find(store, fw) is { } folder)
                map[Path.GetFileName(folder)] = FakelibStore.LoadManifest(folder);
        }
        if (map.Count == 0)
        {
            o.WriteLine($"no fakelibs in {store}" + (fw is not null ? $" for firmware {fw}" : ""));
            return 0;
        }
        int bad = 0;
        foreach (var (folderName, manifest) in map)
        {
            string folder = Path.Combine(store, folderName);
            o.WriteLine($"== {folderName}  ({folder})");
            foreach (var (name, e) in manifest)
            {
                string Str(string key) => e?[key]?.GetValue<string>() ?? "";
                string container = Str("container");
                string built = Str("build_date").Length > 0 ? $"built {Str("build_date")}  " : "";
                string sony = k.SonyBuild(Str("build_path")) is { Length: > 0 } s ? $"Sony {s}  " : "";
                o.WriteLine($"   {name,-34} {container[..Math.Min(14, container.Length)],-14} SDK {PyOpt(e, "ps5_sdk")}/{PyOpt(e, "ps4_sdk")}  {built}{sony}{Str("source")}");
            }
            foreach (string w in FakelibStore.BuildWarnings(folder, k))
                o.WriteLine($"   WARNING: {w}");
            if (p[0] == "verify")
            {
                var problems = FakelibStore.Verify(folder);
                bad += problems.Count;
                o.WriteLine("   verify: " + (problems.Count == 0 ? "OK" : string.Join("; ", problems.Select(x => $"{x.Name}: {x.Problem}"))));
            }
        }
        return bad > 0 ? 1 : 0;
    }

    static string PyOpt(System.Text.Json.Nodes.JsonNode? e, string key) => e?[key]?.GetValue<string>() ?? "None";

    /// <summary>Imports of the stored fakelib set that the target firmware may not export.</summary>
    static int FakelibCheck(Args a, string store, string? fw, TextWriter o, Knowledge k)
    {
        if (fw is null)
            throw new ArgumentException("fakelib check needs --firmware");
        string folder = FakelibStore.Find(store, fw) ?? throw new ArgumentException($"no fakelib folder in {store} for firmware {fw}");
        var names = Names(a);
        var reference = Imports.LoadReference(a.Get("--reference", DefaultReference), fw);
        o.WriteLine($"fakelibs: {folder}");
        o.WriteLine($"reference: {reference.Label}");
        int unresolved = 0;
        foreach (var (name, elf, statuses) in FakelibStore.Check(folder, reference))
        {
            o.WriteLine();
            o.WriteLine(Imports.Report(name, elf, statuses, names, [Imports.Missing, Imports.Unknown]));
            foreach (var s in statuses.Where(s => Imports.IsUnresolved(s.Status)))
            {
                unresolved++;
                if (k.Fixes.TryGetValue(s.Nid, out var f))
                    o.WriteLine($"      -> known fix for {names.Name(s.Nid)}: {f.Kind} (applied by backport)");
            }
        }
        o.WriteLine();
        o.WriteLine($"{unresolved} unresolved import(s); a missing function only fails when called (lazy PLT binding)");
        return 0;
    }
}
