using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ps5Backport.Core;

/// <summary>A console that runs shared backports (data/testers.json). Handles only; no personal data.</summary>
public sealed record Tester(string Handle, string Model, string Firmware, string Loader, string Note);

/// <summary>One item a console run tested: "rule:&lt;NID&gt;", "option:&lt;key&gt;" or "fakelib:&lt;set&gt;[/&lt;variant&gt;]", the verdict
/// for that item and the item's fingerprint when the run was recorded ("" = not pinned, e.g. a run recorded from notes).</summary>
public sealed record ResultTest(string Item, string Verdict, string Fingerprint);

/// <summary>One console run of one build (data/console_results.json).</summary>
public sealed record ConsoleResult(
    string Date, string Tester, string Firmware, string Title, string Build, string Target, string EbootSha256,
    string Outcome, string Error, IReadOnlyList<ResultTest> Tests, string Log, string LogSha256, string Source, string Note);

/// <summary>Evidence for one item in one firmware band. Pinned runs tested the item's current form; <see cref="Old"/> counts runs
/// of an earlier form (fingerprint differs) or unpinned runs, which are history only.</summary>
public sealed record BandEvidence(int Pass, int Fail, int Unknown, int Old)
{
    public bool Any => Pass + Fail + Unknown + Old > 0;
}

/// <summary>An item a cook applied and the fingerprint of the form it applied. Experimental: a derived rule, an option or a
/// fakelib variant (what still needs console evidence).</summary>
public sealed record ConsoleItem(string Item, string Fingerprint, bool Experimental);

/// <summary>An item a cook applied, with its console evidence per band (<see cref="ConsoleResults.Bands"/> order).</summary>
public sealed class ConsoleEvidenceEntry
{
    public required string Item, Fingerprint, Summary;
    public required IReadOnlyList<BandEvidence> Bands;
    /// <summary>Bands where a run of the current form failed, with the latest such run's description.</summary>
    public List<string> Failures = [];
    /// <summary>The item is experimental (derived rule, option, fakelib variant) and passed on every band with no failure.</summary>
    public bool Candidate;
}

/// <summary>
/// Console results and the tester list: what ran where, and what that says about each rule, option and fakelib set.
/// Exact firmware is stored; <see cref="Bands"/> are only the view. Promotion stays a human decision: the engine reports
/// candidates, it never changes a rule's proven flag or an option's default.
/// </summary>
public sealed partial class ConsoleResults
{
    public const string TestersFile = "testers.json", ResultsFile = "console_results.json";
    public static readonly IReadOnlyList<string> Bands = ["4.xx", "5-7", "8-9", "10+"];
    public static readonly IReadOnlyList<string> Outcomes = ["plays", "boots", "hang", "crash", "load_error"];
    public static readonly IReadOnlyList<string> Verdicts = ["pass", "fail", "unknown"];
    public static readonly IReadOnlyList<string> Models = ["fat", "slim", "pro", "base"];

    static readonly string[] TesterKeys = ["model", "firmware", "loader", "note"];
    static readonly string[] ResultKeys = ["date", "tester", "firmware", "title", "build", "target", "eboot_sha256", "outcome", "error",
                                           "tests", "log", "log_sha256", "source", "note"];
    static readonly string[] TestKeys = ["item", "verdict", "fingerprint"];

    public string DataDir { get; }
    readonly Dictionary<string, Tester> _testers;
    public IReadOnlyDictionary<string, Tester> Testers => _testers;
    public List<ConsoleResult> Results { get; }

    ConsoleResults(string dataDir, Dictionary<string, Tester> testers, List<ConsoleResult> results)
    {
        DataDir = dataDir;
        _testers = testers;
        Results = results;
    }

    /// <summary>The repository's data/ when run inside the repository (results are written there), else the bundled data/.</summary>
    public static string DefaultDataDir => Paths.FindUp("Ps5Backport.slnx") is { } solution
        ? Path.Combine(Path.GetDirectoryName(solution)!, "data") : Paths.DataDir;

    /// <summary>Firmware band of a console: 4.xx (below 5.00), 5-7, 8-9, 10+.</summary>
    public static string Band(string firmware) => Firmware.Parse(firmware).Major switch
    {
        < 5 => "4.xx", < 8 => "5-7", < 10 => "8-9", _ => "10+",
    };

    /// <summary>Loads both files from <paramref name="dataDir"/>; a missing file is an empty list.</summary>
    public static ConsoleResults Load(string dataDir)
    {
        string file = TestersFile;
        try
        {
            var testers = new Dictionary<string, Tester>(StringComparer.Ordinal);
            if (Read(dataDir, TestersFile, "testers") is JsonObject t)
                foreach (var (handle, node) in t)
                {
                    var x = Record(node, $"$.testers.{handle}", TesterKeys);
                    testers[handle] = new Tester(handle, Str(x, "model"), Str(x, "firmware"), Str(x, "loader"), Str(x, "note"));
                }
            file = ResultsFile;
            var results = new List<ConsoleResult>();
            if (Read(dataDir, ResultsFile, "results") is { } r)
            {
                if (r is not JsonArray list)
                    throw new InvalidDataException("$.results must be an array");
                for (int i = 0; i < list.Count; i++)
                {
                    var x = Record(list[i], $"$.results[{i}]", ResultKeys);
                    var tests = new List<ResultTest>();
                    if (x["tests"] is JsonArray testList)
                        for (int j = 0; j < testList.Count; j++)
                        {
                            var y = Record(testList[j], $"$.results[{i}].tests[{j}]", TestKeys);
                            tests.Add(new ResultTest(Str(y, "item"), Str(y, "verdict"), Str(y, "fingerprint")));
                        }
                    else if (x["tests"] is not null)
                        throw new InvalidDataException($"$.results[{i}].tests must be an array");
                    results.Add(new ConsoleResult(Str(x, "date"), Str(x, "tester"), Str(x, "firmware"), Str(x, "title"), Str(x, "build"),
                        Str(x, "target"), Str(x, "eboot_sha256"), Str(x, "outcome"), Str(x, "error"), tests, Str(x, "log"),
                        Str(x, "log_sha256"), Str(x, "source"), Str(x, "note")));
                }
            }
            var loaded = new ConsoleResults(dataDir, testers, results);
            loaded.Validate();
            return loaded;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or InvalidDataException or FormatException or ArgumentException)
        {
            throw new InvalidDataException($"Invalid console results in '{Path.Combine(dataDir, file)}': {e.Message}", e);
        }
    }

    static JsonNode? Read(string dataDir, string file, string member)
    {
        string path = Path.Combine(dataDir, file);
        if (!File.Exists(path))
            return null;
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("the document must be an object");
        foreach (var (key, _) in root)
            if (key != member && !key.StartsWith('_'))
                throw new InvalidDataException($"unknown key '{key}' at $ (allowed: {member}; keys starting with '_' are comments)");
        return root[member] switch
        {
            null => null,
            JsonObject o when member == "testers" => o,
            JsonArray a when member == "results" => a,
            _ => throw new InvalidDataException($"$.{member} has the wrong type"),
        };
    }

    static JsonObject Record(JsonNode? node, string at, string[] allowed)
    {
        if (node is not JsonObject record)
            throw new InvalidDataException($"{at} must be an object");
        foreach (var (key, _) in record)
            if (!key.StartsWith('_') && !allowed.Contains(key, StringComparer.Ordinal))
                throw new InvalidDataException($"unknown key '{key}' at {at} (allowed: {string.Join(", ", allowed)}; keys starting with '_' are comments)");
        return record;
    }

    static string Str(JsonObject x, string key) => x[key] is null ? "" : x[key]!.GetValue<string>();

    void Validate()
    {
        foreach (var t in Testers.Values)
        {
            if (!Models.Contains(t.Model))
                throw new InvalidDataException($"tester '{t.Handle}': model '{t.Model}' (use {string.Join(", ", Models)})");
            _ = Firmware.Parse(t.Firmware);
        }
        for (int i = 0; i < Results.Count; i++)
        {
            var r = Results[i];
            string at = $"result {i + 1} ({r.Title} {r.Date})";
            if (!DateTime.TryParseExact(r.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new InvalidDataException($"{at}: date must be yyyy-MM-dd");
            if (!Testers.ContainsKey(r.Tester))
                throw new InvalidDataException($"{at}: unknown tester '{r.Tester}' (add it to {TestersFile})");
            _ = Firmware.Parse(r.Firmware);
            if (r.Target.Length > 0)
                _ = Firmware.Parse(r.Target);
            if (r.Title.Length == 0)
                throw new InvalidDataException($"{at}: title is required");
            if (!Outcomes.Contains(r.Outcome))
                throw new InvalidDataException($"{at}: outcome '{r.Outcome}' (use {string.Join(", ", Outcomes)})");
            foreach (var t in r.Tests)
            {
                if (!ItemSyntax.IsMatch(t.Item))
                    throw new InvalidDataException($"{at}: item '{t.Item}' (use rule:<NID>, option:<key> or fakelib:<set>[/<variant>])");
                if (!Verdicts.Contains(t.Verdict))
                    throw new InvalidDataException($"{at}: verdict '{t.Verdict}' for {t.Item} (use {string.Join(", ", Verdicts)})");
                if (t.Fingerprint.Length > 0 && !Hex12.IsMatch(t.Fingerprint))
                    throw new InvalidDataException($"{at}: fingerprint of {t.Item} must be 12 lowercase hex digits");
            }
            if (r.Tests.Select(t => t.Item).Distinct(StringComparer.Ordinal).Count() != r.Tests.Count)
                throw new InvalidDataException($"{at}: an item is listed twice");
        }
    }

    static readonly Regex ItemSyntax = new(@"^(rule:[A-Za-z0-9+\-]{11}|option:[a-z0-9_]+|fakelib:[0-9.]+(/[a-z0-9_]+)?)$", RegexOptions.CultureInvariant);
    static readonly Regex Hex12 = new("^[0-9a-f]{12}$", RegexOptions.CultureInvariant);

    /// <summary>Writes both files (stable formatting, LF), so a result added through the CLI is a reviewable diff.</summary>
    public void Save()
    {
        Validate();
        var testers = new JsonObject();
        foreach (var t in Testers.Values.OrderBy(t => t.Handle, StringComparer.Ordinal))
            testers[t.Handle] = new JsonObject { ["model"] = t.Model, ["firmware"] = t.Firmware, ["loader"] = t.Loader, ["note"] = t.Note };
        PyJson.WriteFile(Path.Combine(DataDir, TestersFile), new JsonObject { ["testers"] = testers }, 2);
        var results = new JsonArray(Results.Select(r => (JsonNode)ToJson(r)).ToArray());
        PyJson.WriteFile(Path.Combine(DataDir, ResultsFile), new JsonObject { ["results"] = results }, 2);
    }

    static JsonObject ToJson(ConsoleResult r)
    {
        var x = new JsonObject { ["date"] = r.Date, ["tester"] = r.Tester, ["firmware"] = r.Firmware, ["title"] = r.Title };
        void Opt(string key, string value) { if (value.Length > 0) x[key] = value; }
        Opt("build", r.Build); Opt("target", r.Target); Opt("eboot_sha256", r.EbootSha256);
        x["outcome"] = r.Outcome;
        Opt("error", r.Error);
        x["tests"] = new JsonArray(r.Tests.Select(t => (JsonNode)new JsonObject
        {
            ["item"] = t.Item, ["verdict"] = t.Verdict, ["fingerprint"] = t.Fingerprint,
        }).ToArray());
        Opt("log", r.Log); Opt("log_sha256", r.LogSha256); Opt("source", r.Source); Opt("note", r.Note);
        return x;
    }

    // --- fingerprints: what defines an item's behaviour, not its wording ------------------------------------------

    static string Hash(string canonical) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..12];

    static string Canonical(Fix f, Knowledge k)
    {
        string stub = f.Stub.Length == 0 ? "" : TryStubBytes(k, f.Stub);
        return string.Join('\u001f', f.Kind, stub, f.NewNid, f.NewLibrary, string.Join(',', f.Insns.Select(i => i.Hex)), f.ExportNid,
            f.When, f.Below, f.PostCall, f.Contiguous, f.State, f.StateSize, f.Guard);
    }

    static string TryStubBytes(Knowledge k, string stub)
    {
        try { return Convert.ToHexStringLower(k.StubBytes(stub)); }
        catch (KeyNotFoundException) { return "?" + stub; }
    }

    /// <summary>Kind, stub bytes, instructions, conditions: a changed note keeps the evidence, a changed body makes it old.</summary>
    public static string RuleFingerprint(Fix fix, Knowledge knowledge) => Hash(Canonical(fix, knowledge));

    /// <summary>The option's fixes, patterns and fakelib variant name (the variant's files are the fakelib item's fingerprint).</summary>
    public static string OptionFingerprint(FixOption option, Knowledge knowledge) => Hash(string.Join('\u001e',
        option.Fixes.Values.OrderBy(f => f.Nid, StringComparer.Ordinal).Select(f => f.Nid + "=" + Canonical(f, knowledge))
            .Concat(option.Patterns.Select(p => $"{Convert.ToHexStringLower(p.Find)}>{Convert.ToHexStringLower(p.Replace)}|{p.Code}|{string.Join(',', p.Files)}"))
            .Append("variant=" + option.FakelibVariant)));

    /// <summary>The folder's manifest (file names and SHA-256), without its subfolders.</summary>
    public static string FakelibFingerprint(string folder) => Hash(string.Join('\n',
        FakelibStore.LoadManifest(folder).OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => $"{e.Key}={e.Value?["sha256"]?.GetValue<string>()}")));

    /// <summary>Current fingerprint of an item, or null when it no longer exists.</summary>
    public static string? Fingerprint(string item, Knowledge knowledge, string? store)
    {
        if (item.StartsWith("rule:", StringComparison.Ordinal))
            return knowledge.Fixes.TryGetValue(item[5..], out var fix) ? RuleFingerprint(fix, knowledge) : null;
        if (item.StartsWith("option:", StringComparison.Ordinal))
            return knowledge.Options.TryGetValue(item[7..], out var option) ? OptionFingerprint(option, knowledge) : null;
        if (item.StartsWith("fakelib:", StringComparison.Ordinal) && store is not null)
        {
            string folder = Path.Combine(store, item[8..].Replace('/', Path.DirectorySeparatorChar));
            return Directory.Exists(folder) ? FakelibFingerprint(folder) : null;
        }
        return null;
    }

    /// <summary>Items a cook applied, with the fingerprint of the form it applied: known-fix rules (the run's fix table, options
    /// included), selected options, and the store fakelib set plus the variants its options selected.</summary>
    public static List<ConsoleItem> Items(GameResult r, Knowledge knowledge, string? store) => Items(
        r.Modules.SelectMany(m => m.Fixed.Select(f => f.Nid).Concat(m.StubbedNow.Where(s => s.KnownFix).Select(s => s.Nid))),
        r.Options, r.FakelibSource, knowledge, store);

    static List<ConsoleItem> Items(IEnumerable<string> ruleNids, IReadOnlyList<string> options, string fakelibSource,
                                   Knowledge knowledge, string? store)
    {
        var items = new List<ConsoleItem>();
        var table = knowledge.FixTable(options);
        foreach (string nid in ruleNids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            if (table.TryGetValue(nid, out var fix))
                items.Add(new($"rule:{nid}", RuleFingerprint(fix, knowledge), !fix.Proven));
        foreach (string key in options)
            if (knowledge.Options.TryGetValue(key, out var option))
                items.Add(new($"option:{key}", OptionFingerprint(option, knowledge), true));
        if (store is not null && fakelibSource.Length > 0 && StoreRelative(store, fakelibSource) is { } set)
        {
            items.Add(new($"fakelib:{set}", FakelibFingerprint(fakelibSource), false));
            foreach (string variant in options.Select(k => knowledge.Options.GetValueOrDefault(k)?.FakelibVariant ?? "")
                                              .Where(v => v.Length > 0).Distinct(StringComparer.Ordinal))
            {
                string folder = Path.Combine(fakelibSource, variant);
                if (Directory.Exists(folder))
                    items.Add(new($"fakelib:{set}/{variant}", FakelibFingerprint(folder), true));
            }
        }
        return items;
    }

    /// <summary>The items a cook recorded in its backport_report.json ("console_items"): the forms it actually applied.</summary>
    public static List<ConsoleItem> ItemsFromReport(JsonObject report) =>
        report["console_items"] is JsonArray list
            ? [.. list.Select(x => new ConsoleItem(x!["item"]!.GetValue<string>(), x["fingerprint"]!.GetValue<string>(), x["experimental"]!.GetValue<bool>()))]
            : throw new InvalidDataException("the cook report has no console_items (cooked before console results existed); list the items with --test");

    static string? StoreRelative(string store, string folder)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(store), Path.GetFullPath(folder));
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) || relative.Contains(Path.DirectorySeparatorChar)
            ? null : relative;
    }

    // --- evidence -----------------------------------------------------------------------------------------------

    /// <summary>Per-band evidence for an item in its current form.</summary>
    public IReadOnlyList<BandEvidence> Evidence(string item, string fingerprint) => Bands.Select(band =>
    {
        int pass = 0, fail = 0, unknown = 0, old = 0;
        foreach (var r in Results.Where(r => Band(r.Firmware) == band))
            foreach (var t in r.Tests.Where(t => t.Item == item))
            {
                if (t.Fingerprint != fingerprint) old++;
                else if (t.Verdict == "pass") pass++;
                else if (t.Verdict == "fail") fail++;
                else unknown++;
            }
        return new BandEvidence(pass, fail, unknown, old);
    }).ToList();

    /// <summary>"4.xx 1 pass | 5-7 - | 8-9 1 pass, 1 old | 10+ 2 pass" (ASCII: reports are also printed to Windows consoles).</summary>
    public static string Summarize(IReadOnlyList<BandEvidence> bands) => string.Join(" | ", Bands.Select((band, i) =>
    {
        var e = bands[i];
        var parts = new List<string>();
        if (e.Pass > 0) parts.Add($"{e.Pass} pass");
        if (e.Fail > 0) parts.Add($"{e.Fail} fail");
        if (e.Unknown > 0) parts.Add($"{e.Unknown} unknown");
        if (e.Old > 0) parts.Add($"{e.Old} old");
        return $"{band} {(parts.Count == 0 ? "-" : string.Join(", ", parts))}";
    }));

    /// <summary>Evidence entries for a cook: every applied item that has results, plus experimental items without any.</summary>
    public List<ConsoleEvidenceEntry> ForCook(IEnumerable<ConsoleItem> applied)
    {
        var entries = new List<ConsoleEvidenceEntry>();
        foreach (var (item, fingerprint, experimental) in applied)
        {
            var bands = Evidence(item, fingerprint);
            if (!experimental && !bands.Any(b => b.Any))
                continue;
            var entry = new ConsoleEvidenceEntry { Item = item, Fingerprint = fingerprint, Summary = Summarize(bands), Bands = bands };
            for (int i = 0; i < Bands.Count; i++)
                if (bands[i].Fail > 0 && LatestFailure(item, fingerprint, Bands[i]) is { } failed)
                    entry.Failures.Add($"{Bands[i]}: {failed}");
            entry.Candidate = experimental && bands.All(b => b.Pass > 0 && b.Fail == 0);
            entries.Add(entry);
        }
        return entries;
    }

    string? LatestFailure(string item, string fingerprint, string band) => Results
        .Where(r => Band(r.Firmware) == band && r.Tests.Any(t => t.Item == item && t.Fingerprint == fingerprint && t.Verdict == "fail"))
        .OrderBy(r => r.Date, StringComparer.Ordinal).Select(r =>
            $"{r.Date} {r.Title} on FW {r.Firmware} ({r.Tester}){(r.Error.Length > 0 ? ": " + r.Error : "")}").LastOrDefault();
}
