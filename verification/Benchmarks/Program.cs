using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Ps5Backport.Core;

BenchmarkSwitcher.FromAssembly(typeof(FirmwareMetadataBenchmarks).Assembly).Run(args);

/// <summary>Focused benchmark for the duplicate firmware parsing confirmed by whole-run allocation measurements.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class FirmwareMetadataBenchmarks
{
    string _directory = "";
    [GlobalSetup]
    public void Setup() => _directory = Paths.FindUp("firmware_db") ?? throw new DirectoryNotFoundException("firmware_db is required");

    [Benchmark(Baseline = true)]
    public int DuplicateLoads()
    {
        var reference = Imports.LoadReference(_directory, "4");
        var newer = FirmwareDb.LoadAll(_directory).Where(d => FirmwareDb.Key(d.Firmware).CompareTo(FirmwareDb.Key("4")) > 0).ToList();
        return reference.Nids.Count + newer.Count;
    }

    [Benchmark]
    public int SingleRunLoad()
    {
        var databases = FirmwareDb.LoadAll(_directory);
        var reference = Imports.LoadReference(_directory, "4", databases);
        var newer = databases.Where(d => FirmwareDb.Key(d.Firmware).CompareTo(FirmwareDb.Key("4")) > 0).ToList();
        return reference.Nids.Count + newer.Count;
    }
}
