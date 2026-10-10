using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Ps5Backport.Core;
using Ps5Backport.Tests;

if (args.Length == 0) throw new ArgumentException("Use prepare ROOT or run REPO ROOT WORKLOAD analyze|cook ITERATIONS REPORT.json");
if (args[0] == "ownership") { OwnershipExperiment.Run(args[1], args[2]); return; }
if (args[0] == "cancel") { CancellationExperiment.Run(args[1], args[2], args[3]); return; }
if (args[0] == "inspect")
{
    foreach (string file in GameScan.FindModules(args[1]))
    {
        byte[] data = File.ReadAllBytes(Path.Combine(args[1], file));
        if (Self.IsSelf(Self.Kind(data))) data = Self.Unwrap(data);
        long allocated = GC.GetTotalAllocatedBytes(true);
        var symbols = new SceElf(data).Symbols();
        Console.WriteLine($"{file}: {data.Length / 1048576.0:F1} decoded MiB; {symbols.Count} symbols, {(GC.GetTotalAllocatedBytes(true) - allocated) / 1048576.0:F1} metadata MiB");
    }
    return;
}
if (args[0] == "prepare")
{
    foreach (var (name, modules, mib) in new[] { ("small", 4, 2), ("large", 12, 16) })
    {
        string folder = Directory.CreateDirectory(Path.Combine(args[1], name)).FullName;
        for (int i = 0; i < modules; i++)
        {
            var elf = new SceElf(SyntheticBinary.Elf(new byte[mib * 1024 * 1024], imports: true, exports: i == 1));
            SyntheticBinary.W64(elf.Data, 0x180, 0x18);
            SyntheticBinary.W32(elf.Data, 0x188, Sdk.ModuleParamMagic);
            Sdk.Write(elf, 0x09000031, 0x09000000);
            File.WriteAllBytes(Path.Combine(folder, i == 0 ? "eboot.bin" : $"module{i:D2}.sprx"), elf.Data);
        }
    }
    string db = Directory.CreateDirectory(Path.Combine(args[1], "reference")).FullName;
    FirmwareDb.Save(new FirmwareExports("4", "public synthetic", false, new()), Path.Combine(db, "4.00.json"));
    return;
}
string repo = Path.GetFullPath(args[1]), root = Path.GetFullPath(args[2]), workload = args[3];
bool dry = args[4] == "analyze";
int iterations = int.Parse(args[5]);
string source = workload switch
{
    "private-small" => Path.Combine(repo, ".original/backports/PPSA01317/default"),
    "private-large" => Path.Combine(repo, ".original/backports/PPSA28183/default"),
    _ => Path.Combine(root, workload),
};
bool privateData = workload.StartsWith("private-", StringComparison.Ordinal);
var knowledge = new Knowledge(Path.Combine(repo, "data"));
var options = new BackportOptions
{
    Firmware = privateData && workload == "private-large" ? "4.50" : "4",
    ReferenceDir = privateData ? Path.Combine(repo, "firmware_db") : Path.Combine(root, "reference"),
    FakelibStore = privateData ? Path.Combine(repo, "fakelibs") : null,
    Options = workload == "private-large" ? ["force_pro", "gpu_scratch"] : [],
    DryRun = dry,
};
var inputs = GameScan.FindModules(source).Select(p => new { Path = p.Replace('\\', '/'), Sha256 = HashFile(Path.Combine(source, p)) }).ToArray();
var records = new List<object>();
using var counters = new RuntimeCounters();
for (int iteration = 0; iteration < iterations; iteration++)
{
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    string output = Path.Combine(root, "outputs", Path.GetFileNameWithoutExtension(args[6]), workload + iteration);
    long allocated = GC.GetTotalAllocatedBytes(true);
    int[] collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
    using var memory = new MemorySampler();
    var progress = new PhaseProgress();
    var watch = Stopwatch.StartNew();
    var result = Pipeline.Backport(source, output, options, knowledge, progress);
    watch.Stop();
    long allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated;
    int[] gcCounts = Enumerable.Range(0, 3).Select(g => GC.CollectionCount(g) - collections[g]).ToArray();
    memory.Sample();
    memory.Dispose();
    var phases = progress.Finish(watch.Elapsed.TotalMilliseconds);
    if (result.State != OperationState.Completed) throw new InvalidOperationException("Measured operation failed");
    var json = Pipeline.ToJson(result);
    json["source"] = "<game>"; json["output"] = "<out>";
    string report = json.ToJsonString();
    string textReport = Pipeline.FormatReport(result).Replace(source, "<game>").Replace(output, "<out>");
    var outputFiles = !dry ? Directory.GetFiles(output, "*", SearchOption.AllDirectories)
        .Where(p => !p.EndsWith(Publication.ManifestName) && !p.EndsWith(Publication.EvidenceName) && !Path.GetFileName(p).StartsWith("backport_report"))
        .Order(StringComparer.Ordinal).Select(p => new { Path = Path.GetRelativePath(output, p).Replace('\\', '/'), Sha256 = HashFile(p) }).ToArray() : [];
    records.Add(new
    {
        Iteration = iteration, Temperature = iteration == 0 ? "process-cold (OS cache uncontrolled)" : "process-warm",
        Milliseconds = watch.Elapsed.TotalMilliseconds, AllocatedBytes = allocatedBytes,
        Collections = gcCounts,
        memory.PeakWorkingSet, memory.PeakPrivateBytes, memory.PeakManagedBytes, memory.PeakLohAfterGc,
        Phases = phases, ReportSha256 = Hash(Encoding.UTF8.GetBytes(report)),
        TextReportSha256 = Hash(Encoding.UTF8.GetBytes(textReport)), OutputFiles = outputFiles,
    });
}
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[6]))!);
File.WriteAllText(args[6], JsonSerializer.Serialize(new
{
    Workload = workload, Mode = dry ? "analyze" : "cook", Inputs = inputs,
    Environment = new { Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription, ProcessorCount = System.Environment.ProcessorCount,
        Engine = typeof(Pipeline).Assembly.GetName().Version?.ToString() }, Records = records, RuntimeCounters = counters.Snapshot(),
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{workload}/{args[4]}: {args[6]}");
static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }

sealed class PhaseProgress : IProgress<BackportProgress>
{
    readonly Stopwatch _watch = Stopwatch.StartNew();
    readonly List<(string Phase, double Start, long Allocated)> _starts = [("preflight", 0, GC.GetTotalAllocatedBytes(true))];
    public void Report(BackportProgress p)
    {
        _starts.Add(($"{p.Stage}:{p.Module}", _watch.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(true)));
    }
    internal object[] Finish(double end) => _starts.Select((s, i) => (object)new
    { s.Phase, Milliseconds = Math.Max(0, (i + 1 < _starts.Count ? _starts[i + 1].Start : end) - s.Start),
        AllocatedBytes = (i + 1 < _starts.Count ? _starts[i + 1].Allocated : GC.GetTotalAllocatedBytes(true)) - s.Allocated }).ToArray();
}

sealed class MemorySampler : IDisposable
{
    readonly Timer _timer;
    readonly object _gate = new();
    internal long PeakWorkingSet, PeakPrivateBytes, PeakManagedBytes, PeakLohAfterGc;
    internal MemorySampler() { Sample(); _timer = new Timer(_ => Sample(), null, 5, 5); }
    internal void Sample()
    {
        lock (_gate)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This sampler uses Windows process counters");
            var memory = new ProcessMemory { Size = (uint)Marshal.SizeOf<ProcessMemory>() };
            if (!GetProcessMemoryInfo(new IntPtr(-1), ref memory, memory.Size)) throw new InvalidOperationException("Cannot sample process memory");
            PeakWorkingSet = Math.Max(PeakWorkingSet, checked((long)memory.WorkingSet));
            PeakPrivateBytes = Math.Max(PeakPrivateBytes, checked((long)memory.PrivateUsage));
            PeakManagedBytes = Math.Max(PeakManagedBytes, GC.GetTotalMemory(false));
            var info = GC.GetGCMemoryInfo();
            if (info.GenerationInfo.Length > 3) PeakLohAfterGc = Math.Max(PeakLohAfterGc, info.GenerationInfo[3].SizeAfterBytes);
        }
    }
    public void Dispose() => _timer.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessMemory
    {
        internal uint Size, PageFaultCount;
        internal nuint PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool, PeakNonPagedPool, NonPagedPool, PageFileUsage, PeakPageFileUsage, PrivateUsage;
    }
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemory memory, uint size);
}

sealed class RuntimeCounters : EventListener
{
    internal List<Dictionary<string, object?>> Values { get; } = [];
    internal object Snapshot() { lock (Values) return Values.ToArray(); }
    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "System.Runtime") EnableEvents(source, EventLevel.Informational, EventKeywords.All,
            new Dictionary<string, string?> { ["EventCounterIntervalSec"] = "1" });
    }
    protected override void OnEventWritten(EventWrittenEventArgs data)
    {
        if (data.EventName == "EventCounters" && data.Payload?[0] is IDictionary<string, object> values)
            lock (Values) Values.Add(values.ToDictionary(p => p.Key, p => (object?)p.Value));
    }
}
