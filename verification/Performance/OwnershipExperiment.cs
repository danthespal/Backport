using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Ps5Backport.Core;

/// <summary>A loading-only experiment; it deliberately does not implement or alter pipeline transformations.</summary>
static class OwnershipExperiment
{
    internal static void Run(string source, string output)
    {
        var modules = GameScan.FindModules(source).Select(p => Path.Combine(source, p)).ToArray();
        var records = new List<object>();
        foreach (bool retain in new[] { true, false })
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long allocated = GC.GetTotalAllocatedBytes(true);
            var timer = Stopwatch.StartNew();
            var exports = new HashSet<string>(StringComparer.Ordinal);
            var hashes = new Dictionary<string, string>();
            var retained = new List<SceElf>();
            long totalDecoded = 0, largestDecoded = 0;
            foreach (string file in modules)
            {
                var (elf, hash) = Read(file);
                hashes[file] = hash;
                exports.UnionWith(elf.Exports());
                totalDecoded += elf.Data.Length;
                largestDecoded = Math.Max(largestDecoded, elf.Data.Length);
                if (retain) retained.Add(elf);
            }
            if (!retain)
                foreach (string file in modules)
                {
                    var (elf, hash) = Read(file);
                    if (hash != hashes[file]) throw new IOException("Input changed between experimental passes");
                    _ = elf.Imports();
                }
            else
                foreach (var elf in retained) _ = elf.Imports();
            timer.Stop();
            records.Add(new { Strategy = retain ? "retain decoded modules" : "two passes", timer.Elapsed.TotalMilliseconds,
                AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, TotalDecodedBytes = totalDecoded,
                LargestDecodedBytes = largestDecoded, Exports = exports.Order(StringComparer.Ordinal).ToArray() });
            GC.KeepAlive(retained);
        }
        File.WriteAllText(output, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
    }

    static (SceElf Elf, string Hash) Read(string file)
    {
        byte[] data = File.ReadAllBytes(file);
        string hash = Convert.ToHexStringLower(SHA256.HashData(data));
        return (new SceElf(Self.IsSelf(Self.Kind(data)) ? Self.Unwrap(data) : data), hash);
    }
}
