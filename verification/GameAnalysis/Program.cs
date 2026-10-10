using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iced.Intel;
using Ps5Backport.Core;

namespace GameAnalysis;

internal static class Program
{
    static int Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        try
        {
            if (args is ["--self-test"]) { Checks.Run(); return 0; }
            if (args.Length != 2) throw new ArgumentException("Usage: GameAnalysis PROFILE.json OUTPUT_DIRECTORY | --self-test");
            Run(Profile.Load(args[0]), args[1], cancellation.Token);
            return 0;
        }
        catch (Exception e) when (e is ArgumentException or IOException or JsonException or FormatException
            or OverflowException or OperationCanceledException or PatchException or KeyNotFoundException)
        {
            Console.Error.WriteLine(e.Message);
            return e is OperationCanceledException ? 130 : 1;
        }
    }

    internal static void Run(Profile profile, string output, CancellationToken cancel)
    {
        profile.Validate();
        output = Path.GetFullPath(output);
        foreach (string input in new[] { profile.GameDirectory, profile.ReferenceDirectory, profile.Aerolib,
            profile.DataDirectory, profile.FakelibDirectory }.OfType<string>()) Paths.RequireSeparateTrees(input, output);
        Paths.RequireNoLinks(profile.GameDirectory, tree: true);
        if (Directory.Exists(output) || File.Exists(output)) throw new ArgumentException("Output already exists; choose a new directory.");
        var databases = FirmwareDb.LoadAll(profile.ReferenceDirectory);
        var references = profile.Firmwares.ToDictionary(f => f, f => Imports.LoadReference(profile.ReferenceDirectory, f));
        var names = profile.Aerolib is null ? new NameDb() : new NameDb(profile.Aerolib);
        var knowledge = profile.DataDirectory is null ? null : new Knowledge(profile.DataDirectory);
        // Validate optional production inputs before writing any report files.
        foreach (string option in profile.Options)
            if (knowledge?.Options.ContainsKey(option) != true) throw new ArgumentException($"Unknown option: {option}");
        if (profile.FakelibDirectory is { } provider)
        {
            Paths.RequireNoLinks(provider, tree: true);
            if (!Directory.Exists(provider)) throw new DirectoryNotFoundException(provider);
            if (File.Exists(Path.Combine(provider, FakelibStore.ManifestName)) && FakelibStore.Verify(provider).Count > 0)
                throw new InvalidDataException("Fakelib manifest verification failed.");
        }
        var metadataFiles = Directory.GetFiles(profile.ReferenceDirectory, "*.json").ToList();
        if (profile.Aerolib is { } aerolib) metadataFiles.Add(aerolib);
        if (profile.DataDirectory is { } data) metadataFiles.AddRange(Directory.GetFiles(data, "*.json"));
        if (profile.FakelibDirectory is { } libraries) metadataFiles.AddRange(Directory.GetFiles(libraries, "*", SearchOption.AllDirectories));
        var metadataHashes = metadataFiles.Distinct(StringComparer.Ordinal).ToDictionary(p => p, FakelibStore.Sha256);
        Directory.CreateDirectory(output);
        var rows = new List<object>();
        var comparisons = new List<object>();
        var dryRuns = new List<object>();
        var inventories = new Dictionary<string, string[]>();
        var sourceHashes = new Dictionary<string, string>();
        var summary = new StringBuilder("# Game sample analysis\n\n");
        summary.AppendLine("Static observations, not console compatibility certification. Data imports are unchecked. Export providers are flattened by NID; module loading and ABI are not verified. Reference counts and candidate branch sites are byte heuristics, not proof of reachability. Disassembly windows require independently known instruction boundaries.\n");
        summary.AppendLine("| Variant/module | Firmware reference | Unresolved function imports |\n| --- | --- | ---: |");
        WriteJson("run.json", new { schemaVersion = 1, state = "running", startedUtc = DateTimeOffset.UtcNow, profile });
        try
        {
            using var sites = File.CreateText(Path.Combine(output, "ghidra-sites.txt"));
            using var disassembly = File.CreateText(Path.Combine(output, "disassembly.txt"));
            foreach (string variant in profile.Variants)
            {
                cancel.ThrowIfCancellationRequested();
                Console.WriteLine($"Analyzing {variant}...");
                string source = Path.Combine(profile.GameDirectory, variant);
                var modules = new Dictionary<string, SceElf>(StringComparer.Ordinal);
                inventories[variant] = GameScan.FindModules(source, cancel).Select(p => p.Replace('\\', '/')).ToArray();
                foreach (string relative in inventories[variant])
                {
                    cancel.ThrowIfCancellationRequested();
                    string path = Paths.ResolveRelativeFile(source, relative);
                    byte[] bytes = File.ReadAllBytes(path);
                    sourceHashes[path] = Hash(bytes);
                    string kind = Self.Kind(bytes);
                    var elf = new SceElf(Self.IsSelf(kind) ? Self.Unwrap(bytes) : bytes);
                    modules.Add(relative, elf);
                    string dest = Paths.ResolveRelativeFile(Path.Combine(output, "elf", variant), relative + ".elf");
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.WriteAllBytes(dest, elf.Data);
                }
                if (modules.Count == 0) throw new InvalidDataException($"Variant {variant} contains no modules.");
                var exported = modules.Values.SelectMany(e => e.Exports()).ToHashSet(StringComparer.Ordinal);
                foreach (var (relative, elf) in modules)
                {
                    cancel.ThrowIfCancellationRequested();
                    var sdk = Sdk.Read(elf);
                    var imports = Imports.Classify(elf, new HashSet<string>(), exported, true);
                    var refs = Imports.CodeReferences(elf, imports, cancel);
                    var targets = new HashSet<string>(profile.ImportNids, StringComparer.Ordinal);
                    var results = new List<object>();
                    foreach (var (firmware, reference) in references)
                    {
                        var statuses = Imports.Classify(elf, reference.Nids, exported, reference.Exact);
                        var unresolved = statuses.Where(s => Imports.IsUnresolved(s.Status)).ToArray();
                        targets.UnionWith(unresolved.Select(s => s.Nid));
                        results.Add(new { firmware, reference.Label, reference.Exact,
                            counts = statuses.GroupBy(s => s.Status).ToDictionary(g => g.Key, g => g.Count()),
                            unresolved = unresolved.Select(s => new { s.Nid, name = Name(s.Nid), s.Library,
                                got = $"0x{s.GotVaddr:x}", plt = s.Plt, codeRefs = refs[s.Nid],
                                firstObservedExport = databases.FirstOrDefault(d => d.Nids.Contains(s.Nid))?.Firmware }).ToArray() });
                        summary.AppendLine($"| {variant}/{relative} | {reference.Label} | {unresolved.Length} |");
                    }
                    rows.Add(new { variant, path = relative, sourceSha256 = sourceHashes[Paths.ResolveRelativeFile(source, relative)],
                        elfSha256 = Hash(elf.Data), size = elf.Data.Length, sdk = $"0x{sdk.Ps5:x8}/0x{sdk.Ps4:x8}", elf.BuildPath, results });
                    WriteSites(sites, variant, relative, elf, imports.Where(s => targets.Contains(s.Nid)), Name, cancel);
                    foreach (var window in profile.Windows.Where(w => w.Module.Replace('\\', '/') == relative))
                        WriteWindow(disassembly, variant, relative, elf, window);
                }
            }
            foreach (string variant in profile.Variants.Where(v => v != profile.Baseline))
                foreach (string relative in inventories[profile.Baseline].Union(inventories[variant]).Order(StringComparer.Ordinal))
                {
                    cancel.ThrowIfCancellationRequested();
                    bool beforeExists = inventories[profile.Baseline].Contains(relative), afterExists = inventories[variant].Contains(relative);
                    if (!beforeExists || !afterExists)
                    {
                        comparisons.Add(new { baseline = profile.Baseline, variant, path = relative, state = beforeExists ? "removed" : "added" });
                        continue;
                    }
                    var before = SceElf.Load(Path.Combine(output, "elf", profile.Baseline, relative + ".elf"));
                    var after = SceElf.Load(Path.Combine(output, "elf", variant, relative + ".elf"));
                    comparisons.Add(Compare(profile.Baseline, variant, relative, before, after, cancel));
                }
            foreach (string firmware in profile.StudioFirmwares)
                {
                    Console.WriteLine($"Studio dry run for {firmware}...");
                    var result = Pipeline.Backport(Path.Combine(profile.GameDirectory, profile.Baseline), Path.Combine(output, "dryrun-" + firmware),
                        new BackportOptions { Firmware = firmware, ReferenceDir = profile.ReferenceDirectory, ExtraFakelib = profile.FakelibDirectory,
                            DryRun = true, PatchParam = true, Options = profile.Options, Names = names }, knowledge!, cancel: cancel);
                    File.WriteAllText(Path.Combine(output, "studio-" + firmware + ".txt"), Pipeline.FormatReport(result));
                    WriteJson("studio-" + firmware + ".json", Pipeline.ToJson(result));
                    dryRuns.Add(new { firmware, state = result.State.ToString(), unresolved = result.Modules.Sum(m => m.Unknown.Count) });
                    if (result.State != OperationState.Completed) throw new InvalidDataException($"Studio analysis failed for {firmware}.");
                }
            foreach (var (path, hash) in sourceHashes)
            {
                cancel.ThrowIfCancellationRequested();
                if (FakelibStore.Sha256(path) != hash) throw new IOException($"Source changed during analysis: {path}");
            }
            foreach (var (path, hash) in metadataHashes)
            {
                cancel.ThrowIfCancellationRequested();
                if (FakelibStore.Sha256(path) != hash) throw new IOException($"Metadata/provider changed during analysis: {path}");
            }
            foreach (var (variant, inventory) in inventories)
                if (!inventory.SequenceEqual(GameScan.FindModules(Path.Combine(profile.GameDirectory, variant), cancel).Select(p => p.Replace('\\', '/'))))
                    throw new IOException($"Module inventory changed during analysis: {variant}");
            WriteJson("analysis.json", new { schemaVersion = 1, rows, comparisons, dryRuns });
            File.WriteAllText(Path.Combine(output, "summary.md"), summary.ToString());
            WriteJson("run.json", new { schemaVersion = 1, state = "completed", finishedUtc = DateTimeOffset.UtcNow, profile,
                moduleCount = rows.Count, sourceHashes, metadataHashes });
            Console.WriteLine($"Completed: {rows.Count} modules. Reports: {output}");
        }
        catch (Exception e)
        {
            WriteJson("run.json", new { schemaVersion = 1, state = e is OperationCanceledException ? "cancelled" : "failed", error = e.Message, profile });
            throw;
        }
        string Name(string nid) => names.Name(nid) is var name && name != nid ? name : knowledge?.Fixes.GetValueOrDefault(nid)?.Name ?? nid;
        void WriteJson(string name, object value) => File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(value, Profile.Json));
    }

    internal static object Compare(string baseline, string variant, string path, SceElf before, SceElf after, CancellationToken cancel)
    {
        var ranges = new List<object>();
        int shared = Math.Min(before.Data.Length, after.Data.Length);
        for (int i = 0; i < shared; i++)
        {
            if ((i & 65535) == 0) cancel.ThrowIfCancellationRequested();
            if (before.Data[i] == after.Data[i]) continue;
            int start = i;
            while (i + 1 < shared && before.Data[i + 1] != after.Data[i + 1]) i++;
            ranges.Add(Range(start, i - start + 1));
        }
        if (before.Data.Length != after.Data.Length) ranges.Add(Range(shared, Math.Abs(before.Data.Length - after.Data.Length)));
        return new { baseline, variant, path, state = "compared", beforeSize = before.Data.Length, afterSize = after.Data.Length, ranges };
        object Range(int offset, int length)
        {
            string? Address(SceElf elf) => elf.Phdrs.FirstOrDefault(p => p.Type == SceElf.PtLoad && offset >= p.Offset && offset < p.Offset + p.Filesz)
                is { } segment ? $"0x{segment.Vaddr + (ulong)(offset - segment.Offset):x}" : null;
            string Bytes(byte[] data) => offset >= data.Length ? "" : Convert.ToHexStringLower(data.AsSpan(offset, Math.Min(32, Math.Min(length, data.Length - offset))));
            return new { offset = $"0x{offset:x}", length, beforeVaddr = Address(before), afterVaddr = Address(after),
                before = Bytes(before.Data), after = Bytes(after.Data), previewTruncated = length > 32 };
        }
    }

    static void WriteWindow(TextWriter output, string variant, string path, SceElf elf, Window window)
    {
        ulong start = window.Vaddr;
        long offset = elf.Off(start);
        _ = elf.Off(checked(start + (ulong)window.Length - 1));
        if (!elf.Phdrs.Any(p => p.Type == SceElf.PtLoad && p.Offset <= offset && offset + window.Length <= p.Offset + p.Filesz))
            throw new InvalidDataException("Disassembly window crosses file-backed segments.");
        var decoder = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(elf.Data.AsSpan((int)offset, window.Length).ToArray()));
        decoder.IP = start;
        var formatter = new IntelFormatter();
        output.WriteLine($"{variant} {path} 0x{start:x} (manual boundary; invalid/truncated instructions can appear)");
        while (decoder.IP < start + (ulong)window.Length)
        {
            decoder.Decode(out var instruction);
            var formatted = new TextOutput();
            formatter.Format(instruction, formatted);
            output.WriteLine($"{instruction.IP:x}: {formatted}");
        }
    }

    static void WriteSites(TextWriter output, string variant, string path, SceElf elf, IEnumerable<ImportStatus> imports,
        Func<string, string> name, CancellationToken cancel)
    {
        var targets = new Dictionary<ulong, List<string>>();
        foreach (var import in imports.Where(i => i.Plt))
        {
            ulong entry = elf.ReadU64(import.GotVaddr) - 6;
            // Local stubs do not retain the lazy-PLT layout from which this address is derived.
            if (import.Status is Imports.Stubbed or Imports.Patched) continue;
            output.WriteLine($"{variant}/{path}: {import.Nid} {name(import.Nid)} PLT=0x{entry:x} GOT=0x{import.GotVaddr:x}");
            if (!targets.TryGetValue(entry, out var nids)) targets[entry] = nids = [];
            nids.Add(import.Nid);
        }
        if (targets.Count == 0) return;
        foreach (var segment in elf.Phdrs.Where(p => p.Type == SceElf.PtLoad && (p.Flags & SceElf.PfX) != 0))
            for (int i = (int)segment.Offset; i + 5 <= segment.Offset + segment.Filesz; i++)
            {
                if ((i & 65535) == 0) cancel.ThrowIfCancellationRequested();
                if (elf.Data[i] is not (0xe8 or 0xe9)) continue;
                ulong ip = segment.Vaddr + (ulong)(i - segment.Offset);
                long displacement = BinaryPrimitives.ReadInt32LittleEndian(elf.Data.AsSpan(i + 1));
                if (targets.TryGetValue(unchecked(ip + 5 + (ulong)displacement), out var nids))
                    output.WriteLine($"  candidate direct branch at 0x{ip:x} -> {string.Join(", ", nids)}");
            }
    }

    static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
    sealed class TextOutput : FormatterOutput
    {
        readonly StringBuilder text = new();
        public override void Write(string value, FormatterTextKind kind) => text.Append(value);
        public override string ToString() => text.ToString();
    }
}
