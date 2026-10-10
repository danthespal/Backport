using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Iced.Intel;
using Ps5Backport.Core;

namespace DecoderEvaluation;

internal static class Program
{
    static void Main(string[] args)
    {
        if (args.Length is < 1 or > 2 || args.Length == 2 && args[1] != "--private")
            throw new ArgumentException("Usage: DecoderEvaluation REPORT.json [--private] (run from repository root)");
        string root = Directory.GetCurrentDirectory();
        var cases = Checks.Run();
        var emitters = Checks.Emitters(root);
        var workloads = new List<object>();
        foreach (int mib in new[] { 1, 16 })
        {
            byte[] code = new byte[mib * 1024 * 1024];
            for (int at = 0; at < code.Length; at += 8)
            {
                Fixtures.Reference([0x48, 0x8B, 0x05], offset: at).CopyTo(code, at);
                code[at + 7] = 0x90;
            }
            workloads.Add(Measure($"synthetic-{mib}MiB", Fixtures.Elf(code), [Fixtures.Status()], corpus: false));
        }
        if (args.Length == 2)
            foreach (string path in new[] { "work/NOT_BACKPORT_elf/eboot.elf", "work/NOT_BACKPORT_elf/sce_module/libc.elf", "work/GT7_sce_module_elf/libc.elf" })
            {
                var elf = SceElf.Load(Path.Combine(root, path));
                var statuses = elf.Imports().Select(i => new ImportStatus(i.Nid, i.Library, i.GotVaddr, i.Plt, Imports.Missing)).ToArray();
                workloads.Add(Measure(path, elf, statuses, corpus: true));
            }
        string report = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        var assembly = typeof(Decoder).Assembly;
        var result = new { Utc = DateTimeOffset.UtcNow, Runtime = Environment.Version.ToString(), ProcessorCount = Environment.ProcessorCount,
            Decoder = assembly.GetName().FullName, DecoderDllBytes = new FileInfo(assembly.Location).Length,
            Cases = cases, Emitters = emitters, Workloads = workloads,
            Limits = "Linear decoding is not ground truth. Corpus differences need trusted boundaries. Static references do not prove reachability or ABI safety. First timing is workload-first, after synthetic checks; no process-cold claim." };
        File.WriteAllText(report, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Passed {cases.Count} reference cases and emitter checks. Local report: {report}");
    }

    static object Measure(string name, SceElf elf, ImportStatus[] statuses, bool corpus)
    {
        string hash = Convert.ToHexString(SHA256.HashData(elf.Data));
        var samples = new List<object>();
        Dictionary<string, int>? heuristic = null;
        Scan? linear = null;
        for (int n = 0; n < 4; n++)
        {
            // Alternate order to reduce a systematic cache/order advantage. Preparation is outside the timer.
            if (n % 2 == 0) { Time(false); Time(true); } else { Time(true); Time(false); }
            void Time(bool decode)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                long before = GC.GetAllocatedBytesForCurrentThread();
                var watch = Stopwatch.StartNew();
                if (decode) linear = ReferenceProbe.Decode(elf, statuses); else heuristic = Imports.CodeReferences(elf, statuses);
                watch.Stop();
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                samples.Add(new { Scanner = decode ? "Iced-linear" : "Core-heuristic", Iteration = n, Milliseconds = watch.Elapsed.TotalMilliseconds, AllocatedBytes = allocated });
            }
        }
        Checks.Require(heuristic is not null && linear is not null, "Missing measurement");
        if (!corpus) Checks.Require(heuristic!.SequenceEqual(linear!.Counts), "Synthetic valid-code comparison disagrees");
        var differences = heuristic!.Where(kv => kv.Value != linear!.Counts[kv.Key]).Select(kv => new { Nid = kv.Key, Heuristic = kv.Value, Linear = linear!.Counts[kv.Key] }).ToArray();
        object[] sites = [];
        if (corpus)
        {
            var old = ReferenceProbe.HeuristicSites(elf, statuses);
            var decoded = ReferenceProbe.Decode(elf, statuses, collectSites: true).Sites;
            var newAddresses = decoded.ToDictionary(s => s.Address);
            // Match prefixes within the same decoded reference before reporting an unmatched byte-pattern site.
            var unmatched = old.Where(s => !Enumerable.Range(0, 15).Any(back => s.Address >= (ulong)back &&
                newAddresses.TryGetValue(s.Address - (ulong)back, out var found) && found.Nid == s.Nid && found.Target == s.Target && back < found.Length)).Take(32).ToArray();
            var containing = new Dictionary<ulong, object>();
            foreach (var p in elf.Phdrs.Where(p => p.Type == SceElf.PtLoad && (p.Flags & SceElf.PfX) != 0 && unmatched.Any(s => s.Address >= p.Vaddr && s.Address < p.Vaddr + (ulong)p.Filesz)))
            {
                var decoder = Decoder.Create(64, new ByteArrayCodeReader(elf.Data, checked((int)p.Offset), checked((int)p.Filesz))); decoder.IP = p.Vaddr;
                while (decoder.IP < p.Vaddr + (ulong)p.Filesz)
                {
                    decoder.Decode(out var i);
                    foreach (var s in unmatched.Where(s => s.Address >= i.IP && s.Address < i.NextIP))
                        containing[s.Address] = new { Start = $"0x{i.IP:x}", Bytes = Convert.ToHexString(elf.Data.AsSpan(checked((int)elf.Off(i.IP)), i.Length)), Instruction = i.ToString() };
                }
            }
            sites = unmatched.Select(s => (object)new {
                s.Nid, Address = $"0x{s.Address:x}", Target = $"0x{s.Target:x}",
                Context = Convert.ToHexString(elf.Data.AsSpan(checked((int)elf.Off(s.Address)), Math.Min(16, elf.Data.Length - checked((int)elf.Off(s.Address))))),
                ContainingLinearInstruction = containing.GetValueOrDefault(s.Address),
                Caveat = "Code/data boundaries must be inspected before labeling an error; a linear instruction is not independent ground truth"
            }).ToArray();
            if (name.EndsWith("eboot.elf", StringComparison.Ordinal))
                foreach (var (nid, count) in new[] { ("2jiIxUmcsGo", 0), ("bIi4YUfSRys", 3), ("f3kPeoTZnIE", 1) })
                    Checks.Require(heuristic![nid] == count, "Existing corpus count drift: " + nid);
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var cancelWatch = Stopwatch.StartNew();
        try { ReferenceProbe.Decode(elf, statuses, cancel: cancelled.Token); throw new InvalidOperationException("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        cancelWatch.Stop();
        object? activeCancellation = null;
        if (name == "synthetic-16MiB")
        {
            using var active = new CancellationTokenSource();
            var activeWatch = Stopwatch.StartNew();
            long requested = 0;
            using var timer = new Timer(_ => { Interlocked.Exchange(ref requested, activeWatch.ElapsedTicks); active.Cancel(); }, null, 5, Timeout.Infinite);
            try { ReferenceProbe.Decode(elf, statuses, cancel: active.Token); throw new InvalidOperationException("Active cancellation was not observed"); }
            catch (OperationCanceledException) { }
            activeWatch.Stop();
            long requestTicks = Interlocked.Read(ref requested);
            Checks.Require(requestTicks > 0, "Missing active cancellation request");
            activeCancellation = new { RequestedAfterMilliseconds = requestTicks * 1000.0 / Stopwatch.Frequency,
                ObservedAfterRequestMilliseconds = (activeWatch.ElapsedTicks - requestTicks) * 1000.0 / Stopwatch.Frequency };
        }
        Checks.Require(hash == Convert.ToHexString(SHA256.HashData(elf.Data)), "Measured scan mutated input");
        return new { Name = name, Sha256 = hash, InputBytes = elf.Data.Length,
            ExecutableBytes = elf.Phdrs.Where(p => p.Type == SceElf.PtLoad && (p.Flags & SceElf.PfX) != 0).Sum(p => p.Filesz),
            Imports = statuses.Length, Instructions = linear!.Instructions, InvalidInstructions = linear.Invalid,
            HeuristicReferences = heuristic!.Values.Sum(v => (long)v), LinearReferences = linear.Counts.Values.Sum(v => (long)v),
            Differences = differences, InspectionSites = sites, PreCancelledMilliseconds = cancelWatch.Elapsed.TotalMilliseconds, ActiveCancellation = activeCancellation, Samples = samples };
    }
}
