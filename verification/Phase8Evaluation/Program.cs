using System.CommandLine;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ps5Backport.Core;

if (args.Length != 1) throw new ArgumentException("Usage: Phase8Evaluation REPORT.json (run from repository root)");
string report = Path.GetFullPath(args[0]); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
var root = new RootCommand("Phase 8 parse-only prototype");
var aero = new Option<string>("--aerolib") { Recursive = true };
root.Options.Add(aero);
var backport = new Command("backport");
var paths = new Argument<string[]>("paths") { Arity = new ArgumentArity(2, 2) };
var firmware = new Option<string>("--firmware");
var provider = new Option<string[]>("--provider") { AllowMultipleArgumentsPerToken = false };
var option = new Option<string[]>("--option") { AllowMultipleArgumentsPerToken = false };
var extend = new Option<bool>("--extend-text");
var noExtend = new Option<bool>("--no-extend-text");
backport.Arguments.Add(paths);
foreach (var opt in new Option[] { firmware, option, extend, noExtend }) backport.Options.Add(opt);
root.Subcommands.Add(backport);
var reportCommand = new Command("report");
reportCommand.Arguments.Add(new Argument<string[]>("files") { Arity = ArgumentArity.OneOrMore });
reportCommand.Options.Add(provider); root.Subcommands.Add(reportCommand);
var nid = new Command("nid");
var names = new Argument<string[]>("names") { Arity = ArgumentArity.OneOrMore };
nid.Arguments.Add(names); root.Subcommands.Add(nid);

string[][] cases = [
    ["backport", "game", "out", "--firmware", "4"],
    ["backport", "game", "out", "--firmware=4"],
    ["backport", "game", "out", "--firmware", "3", "--firmware", "4"],
    ["backport", "game", "out", "--option", "a", "--option=b"],
    ["report", "file", "--provider", "a", "--provider", "b"],
    ["--aerolib", "names.csv", "backport", "game", "out"],
    ["backport", "game", "out", "--aerolib=names.csv"],
    ["backport", "game", "out", "--extend-text", "--no-extend-text"],
    ["backport", "game", "out", "--extend-text=false"],
    ["backport", "game", "out", "--firmware"],
    ["backport", "game", "out", "--firmware", "--no-extend-text"],
    ["backport", "game", "out", "--typo", "value"],
    ["backport", "--", "game", "out"],
    ["nid", "--", "--name"],
    ["--help"], ["nid", "-h"], ["nid", "hello"], []
];
var results = new List<object>();
bool[] candidateAccepts = [true, true, false, true, true, true, true, true, true, false, true, false, true, true, true, true, true, false];
int caseIndex = 0;
// Reflection inspects the existing private parser without exposing a new production API or copying its implementation.
Type legacyType = typeof(Cli).GetNestedType("Args", BindingFlags.NonPublic)!;
foreach (string[] tokens in cases)
{
    object? legacy = null;
    string? legacyError = null;
    try { legacy = Activator.CreateInstance(legacyType, new object[] { tokens }); }
    catch (TargetInvocationException e) { legacyError = e.InnerException!.Message; }
    object? SnapshotLegacy() => legacy is null ? null : new {
        Positionals = (List<string>)legacyType.GetProperty("Positional")!.GetValue(legacy)!,
        Firmware = legacyType.GetMethod("Get", [typeof(string)])!.Invoke(legacy, ["--firmware"]),
        Aerolib = legacyType.GetMethod("Get", [typeof(string)])!.Invoke(legacy, ["--aerolib"]),
        Options = legacyType.GetMethod("All")!.Invoke(legacy, ["--option"]),
        Providers = legacyType.GetMethod("All")!.Invoke(legacy, ["--provider"]),
        Extend = legacyType.GetMethod("Has")!.Invoke(legacy, ["--extend-text"]),
        NoExtend = legacyType.GetMethod("Has")!.Invoke(legacy, ["--no-extend-text"])
    };
    var parsed = root.Parse(tokens);
    if ((parsed.Errors.Count == 0) != candidateAccepts[caseIndex] || (legacyError is not null) != (caseIndex is 9 or 13 or 14))
        throw new InvalidOperationException("Specified parser observation drift: " + string.Join(' ', tokens));
    caseIndex++;
    // Only safe, read-only NID/usage commands are executed. Backport tokens are parsed, never invoked.
    object? invocation = null;
    if (tokens.Length == 0 || tokens[0] is "nid" or "--help")
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        int exit = Cli.Run(tokens, output, error);
        invocation = new { Exit = exit, Out = output.ToString(), Error = error.ToString() };
    }
    results.Add(new { Tokens = tokens, LegacyError = legacyError, Legacy = SnapshotLegacy(),
        Candidate = new { Command = parsed.CommandResult.Command.Name, Action = parsed.Action?.GetType().Name, Errors = parsed.Errors.Select(e => e.Message).ToArray(),
            Aerolib = parsed.Errors.Count == 0 ? parsed.GetValue(aero) : null,
            Firmware = parsed.Errors.Count == 0 ? parsed.GetValue(firmware) : null,
            Options = parsed.Errors.Count == 0 ? parsed.GetValue(option) : null, Providers = parsed.Errors.Count == 0 ? parsed.GetValue(provider) : null,
            Extend = parsed.Errors.Count == 0 ? (bool?)parsed.GetValue(extend) : null,
            NoExtend = parsed.Errors.Count == 0 ? (bool?)parsed.GetValue(noExtend) : null }, LegacyInvocation = invocation });
}
var files = Directory.GetFiles("firmware_db", "*.json").Order(StringComparer.Ordinal).ToArray();
var hashes = files.ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
var samples = new List<object>();
List<FirmwareExports> dbs = [];
for (int n = 0; n < 4; n++)
{
    long before = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
    dbs = FirmwareDb.LoadAll("firmware_db");
    long allNids = dbs.Sum(d => (long)d.Nids.Count);
    watch.Stop(); samples.Add(new { Iteration = n, Milliseconds = watch.Elapsed.TotalMilliseconds,
        AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before, TotalFirmwareNids = allNids });
}
var known = dbs.SelectMany(d => d.Nids).Distinct(StringComparer.Ordinal).Take(1000).ToArray();
long hits = 0; var queries = Stopwatch.StartNew();
foreach (string value in known) foreach (var db in dbs) if (db.Nids.Contains(value)) hits++;
queries.Stop();
if (hashes.Any(kv => kv.Value != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(kv.Key)))) ) throw new InvalidOperationException("Catalog changed during evaluation");
var knowledge = new Knowledge("data");
var semanticChecks = new List<object>();
foreach (string label in new[] { "misspelled-proven", "unresolved-stub", "empty-adapter" })
{
    string scratch = Path.Combine(Path.GetDirectoryName(report)!, "synthetic-knowledge", label);
    Directory.CreateDirectory(scratch);
    foreach (string file in knowledge.DocumentHashes.Keys) File.Copy(Path.Combine("data", file), Path.Combine(scratch, file), overwrite: true);
    var document = JsonNode.Parse(File.ReadAllText(Path.Combine(scratch, "fixes.json")))!;
    string key = knowledge.FixList.First(f => f.Kind == Fix.KindStub).Nid;
    var fix = document["fixes"]![key]!.AsObject();
    fix.Remove("proven");
    if (label == "misspelled-proven") fix["proovn"] = false;
    if (label == "unresolved-stub") fix["stub"] = "does-not-exist";
    if (label == "empty-adapter") { fix["kind"] = "adapter"; fix["insns"] = new JsonArray(); }
    File.WriteAllText(Path.Combine(scratch, "fixes.json"), document.ToJsonString());
    bool accepts = false; bool? proven = null; string? message = null;
    try { var loaded = new Knowledge(scratch); accepts = true; proven = loaded.Fixes[key].Proven; }
    catch (InvalidDataException e) { message = e.Message; }
    if (accepts != (label == "misspelled-proven") || accepts && proven != true) throw new InvalidOperationException("Semantic observation drift: " + label);
    semanticChecks.Add(new { Case = label, CoreAccepts = accepts, Proven = proven, Error = message });
}
if (knowledge.DocumentHashes.Any(kv => kv.Value != Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine("data", kv.Key))))))
    throw new InvalidOperationException("Rule data changed during evaluation");
var record = new { Utc = DateTimeOffset.UtcNow, Runtime = Environment.Version.ToString(), CliCases = results,
    Catalog = new { Files = files.Length, Bytes = files.Sum(p => new FileInfo(p).Length), Samples = samples,
        Queries = known.Length * dbs.Count, QueryMilliseconds = queries.Elapsed.TotalMilliseconds, Hits = hits, Hashes = hashes },
    Knowledge = new { Files = knowledge.DocumentHashes.Count, Bytes = knowledge.DocumentHashes.Keys.Sum(p => new FileInfo(Path.Combine("data", p)).Length),
        Fixes = knowledge.Fixes.Count, Options = knowledge.Options.Count, Stubs = knowledge.Stubs.Count },
    CandidateDllBytes = new FileInfo(typeof(RootCommand).Assembly.Location).Length, SemanticChecks = semanticChecks,
    Limit = "Partial parse-only prototype, not a migrated CLI. Catalog is warm local JSON/.NET baseline; no filesystem concurrency or database migration guarantee." };
File.WriteAllText(report, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Recorded {cases.Length} CLI compatibility cases and read-only catalog measurements: {report}");
