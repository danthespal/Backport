using System.Security.Cryptography;
using System.Text.Json;
using Ps5Backport.Core;
using Ps5Backport.Tests;

namespace GameAnalysis;

/// <summary>Public, generated fixtures exercise complete reports and source/output isolation.</summary>
internal static class Checks
{
    internal static void Run()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "game-analysis-checks-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string game = Path.Combine(root, "game"), reference = Path.Combine(root, "reference");
            Directory.CreateDirectory(Path.Combine(game, "before"));
            Directory.CreateDirectory(Path.Combine(game, "after"));
            Directory.CreateDirectory(reference);
            File.WriteAllBytes(Path.Combine(game, "before/eboot.bin"), SyntheticBinary.Elf([0x90, 0xc3], imports: true));
            File.WriteAllBytes(Path.Combine(game, "after/eboot.bin"), SyntheticBinary.Elf([0xc3, 0x90, 0x90, 0xc3], imports: true));
            File.WriteAllBytes(Path.Combine(game, "before/removed.prx"), SyntheticBinary.Elf());
            File.WriteAllBytes(Path.Combine(game, "after/added.prx"), SyntheticBinary.Elf());
            FirmwareDb.Save(new FirmwareExports("4.03", "synthetic", true, []), Path.Combine(reference, "4.03.json"));
            FirmwareDb.Save(new FirmwareExports("10.01", "synthetic", true,
                new() { ["synthetic"] = [SyntheticBinary.Nid] }), Path.Combine(reference, "10.01.json"));
            string profileFile = Path.Combine(root, "profile.json");
            File.WriteAllText(profileFile, """
                {"gameDirectory":"game","referenceDirectory":"reference","baseline":"before",
                 "variants":["before","after"],"firmwares":["4.03","10.01","10.40"],
                 "windows":[{"module":"eboot.bin","address":"0x500","length":2}]}
                """);
            var profile = Profile.Load(profileFile);
            var original = Directory.GetFiles(game, "*", SearchOption.AllDirectories).ToDictionary(p => p,
                p => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))));
            string output = Path.Combine(root, "output");
            Program.Run(profile, output, default);
            using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "analysis.json")));
            Require(report.RootElement.GetProperty("schemaVersion").GetInt32() == 1, "Report schema");
            var rows = report.RootElement.GetProperty("rows").EnumerateArray().ToArray();
            Require(rows.Length == 4, "Module inventory");
            var targets = rows.First(r => r.GetProperty("path").GetString() == "eboot.bin").GetProperty("results").EnumerateArray().ToArray();
            Require(targets[0].GetProperty("unresolved").GetArrayLength() == 1, "Exact absent import");
            Require(targets[1].GetProperty("unresolved").GetArrayLength() == 0, "Exact present import");
            Require(!targets[2].GetProperty("exact").GetBoolean(), "Lower-bound reference label");
            var comparisons = report.RootElement.GetProperty("comparisons").EnumerateArray().ToArray();
            Require(comparisons.Any(c => c.GetProperty("state").GetString() == "removed"), "Removed module");
            Require(comparisons.Any(c => c.GetProperty("state").GetString() == "added"), "Added module");
            var eboot = comparisons.Single(c => c.GetProperty("path").GetString() == "eboot.bin");
            Require(eboot.GetProperty("afterSize").GetInt32() - eboot.GetProperty("beforeSize").GetInt32() == 2, "Size change");
            Require(eboot.GetProperty("ranges").EnumerateArray().Any(r => r.GetProperty("before").GetString() == "" && r.GetProperty("length").GetInt32() == 2), "Appended bytes recorded");
            Require(File.ReadAllText(Path.Combine(output, "disassembly.txt")).Contains("500: nop"), "Bounded disassembly");
            Require(File.ReadAllText(Path.Combine(output, "run.json")).Contains("completed"), "Completion marker");
            foreach (var (path, hash) in original) Require(FakelibStore.Sha256(path) == hash, "Source preservation");
            Reject(() => Program.Run(profile, Path.Combine(game, "output"), default), "Output in source");
            Reject(() => Program.Run(profile, output, default), "Existing output");
            profile.Variants = ["before", "../after"];
            Reject(profile.Validate, "Variant traversal");
            profile.Variants = ["before", "before"];
            Reject(profile.Validate, "Duplicate variants");
            profile = Profile.Load(profileFile);
            profile.Windows[0].Length = 5000;
            Reject(profile.Validate, "Oversized window");
            profile = Profile.Load(profileFile);
            profile.Firmwares = ["12.00"];
            profile.StudioFirmwares = ["12.00"];
            string unsupported = Path.Combine(root, "unsupported-target");
            Reject(() => Program.Run(profile, unsupported, default), "Unsupported Studio SDK before output");
            Require(!Directory.Exists(unsupported), "Unsupported target leaves no output");
            profile = Profile.Load(profileFile);
            profile.Windows[0].Address = "0xffffffffffffffff";
            string failed = Path.Combine(root, "failed");
            try { Program.Run(profile, failed, default); throw new Exception("Expected invalid address rejection."); }
            catch (InvalidDataException) { Require(File.ReadAllText(Path.Combine(failed, "run.json")).Contains("failed"), "Failure marker"); }
            Console.WriteLine("Self-tests passed: matrices, lower bounds, additions/removals, append deltas, disassembly, source preservation, path rejection and failed-run labeling.");
        }
        finally { Directory.Delete(root, true); }
    }

    static void Require(bool value, string check) { if (!value) throw new InvalidDataException("Self-test failed: " + check); }
    static void Reject(Action action, string check)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidDataException("Self-test failed to reject: " + check);
    }
}
