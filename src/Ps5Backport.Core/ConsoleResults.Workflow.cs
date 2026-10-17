using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ps5Backport.Core;

/// <summary>One row of the console-results matrix: an item, its current fingerprint (null when the item no longer exists) and its
/// evidence per band.</summary>
public sealed record MatrixRow(string Item, string Label, bool Experimental, string? Fingerprint, IReadOnlyList<BandEvidence> Bands)
{
    /// <summary>Experimental and passed on every band with no failure: a promotion candidate.</summary>
    public bool Candidate => Experimental && Bands.All(b => b.Pass > 0 && b.Fail == 0);
    public bool Failed => Bands.Any(b => b.Fail > 0);
    public bool HasRuns => Bands.Any(b => b.Any);
    /// <summary>Has a run of its current form on some band.</summary>
    public bool Started => Bands.Any(Current);
    public static bool Current(BandEvidence e) => e.Pass + e.Fail + e.Unknown > 0;
}

/// <summary>A prefilled console result (<see cref="ConsoleResults.Draft"/>). <see cref="LogShowsCrash"/>: the log's first fatal event
/// set the outcome and the error.</summary>
public sealed record ResultDraft(ConsoleResult Result, bool LogShowsCrash);

/// <summary>What promoting an item changes: the derived fixes that become proven in data/fixes.json (inside option
/// <see cref="OptionKey"/> when set), and the part that stays a manual decision.</summary>
public sealed record PromotionPlan(string Item, string? OptionKey, IReadOnlyList<string> ProveNids, string Manual)
{
    public bool CanApply => ProveNids.Count > 0;
}

public sealed partial class ConsoleResults
{
    static readonly Regex KlogName = new(@"^PS5-([A-Za-z]+)-(\d+\.\d+)-", RegexOptions.CultureInvariant);
    static readonly Regex HandleSyntax = new("^[a-z0-9][a-z0-9_-]*$", RegexOptions.CultureInvariant);

    /// <summary>Adds a tester (saved with the next <see cref="Save"/>).</summary>
    public void AddTester(Tester tester)
    {
        if (!HandleSyntax.IsMatch(tester.Handle))
            throw new ArgumentException($"tester handle '{tester.Handle}': use a-z, 0-9, '-' and '_' (a handle, not a name)");
        if (_testers.ContainsKey(tester.Handle))
            throw new ArgumentException($"tester '{tester.Handle}' already exists");
        _testers[tester.Handle] = tester;
        try { Validate(); }
        catch (InvalidDataException e)
        {
            _testers.Remove(tester.Handle);
            throw new ArgumentException(e.Message, e);
        }
    }

    /// <summary>
    /// A result for one console run, prefilled the way `results add` and Studio's Add result do: the console firmware from
    /// <paramref name="firmware"/>, else the klog name (PS5-&lt;MODEL&gt;-&lt;FW&gt;-…), else the tester's; outcome and error from the
    /// log's first fatal event unless given; the tested items with the forms the cook in <paramref name="build"/> recorded
    /// (pass for plays/boots, else unknown); <paramref name="tests"/> ("ITEM" or "ITEM=verdict") add or override items. The
    /// outcome stays empty when neither a value nor a fatal event gives one, the title when neither a value nor the cook report
    /// gives one. Nothing is added or saved.
    /// </summary>
    public ResultDraft Draft(string tester, string? build, string? log, Knowledge knowledge, string? store,
                             string? outcome = null, string? error = null, string? firmware = null, string? title = null, string? date = null,
                             IEnumerable<string>? tests = null, string source = "", string note = "")
    {
        if (!Testers.TryGetValue(tester, out var who))
            throw new ArgumentException($"unknown tester '{tester}' (known: {string.Join(", ", Testers.Keys)})");
        string? reportPath = build is null ? null : Path.Combine(build, Pipeline.ReportJson);
        var report = reportPath is null ? null
            : JsonNode.Parse(File.ReadAllText(reportPath).TrimStart('﻿')) as JsonObject ?? throw new InvalidDataException($"{reportPath}: not a JSON object");
        string console = Firmware.Name(firmware
            ?? (log is not null && KlogName.Match(Path.GetFileName(log)) is { Success: true } m ? m.Groups[2].Value : who.Firmware));
        string result = outcome ?? "", problem = error ?? "", logSha = "";
        bool crash = false;
        if (log is not null)
        {
            var analysis = CrashLog.AnalyzeFiles(log, reportPath);
            logSha = analysis["logSha256"]!.GetValue<string>();
            if (analysis["events"] is JsonArray { Count: > 0 } events && events[0] is JsonObject first)
            {
                crash = true;
                string rip = (first["addresses"] as JsonArray)?.FirstOrDefault(x => x?["kind"]?.GetValue<string>() == "RIP")
                    ?["runtimeAddress"]?.GetValue<string>() is { } address ? $" RIP {address}" : "";
                if (problem.Length == 0)
                    problem = $"{first["signal"]?.GetValue<string>() ?? "fatal"} {first["reason"]?.GetValue<string>()?.Trim()}".Trim() + rip;
                if (result.Length == 0)
                    result = "crash";
            }
        }
        string verdict = DefaultVerdict(result);
        var cooked = report is null ? [] : ItemsFromReport(report);
        var list = cooked.Select(i => new ResultTest(i.Item, verdict, i.Fingerprint)).ToList();
        foreach (string spec in tests ?? [])
        {
            int eq = spec.IndexOf('=');
            string item = eq < 0 ? spec : spec[..eq], given = eq < 0 ? verdict : spec[(eq + 1)..];
            // the cooked form when the build applied the item, else the item's current form
            string fingerprint = cooked.FirstOrDefault(c => c.Item == item)?.Fingerprint ?? Fingerprint(item, knowledge, store)
                ?? throw new ArgumentException($"test {item}: no such rule, option or fakelib folder");
            int at = list.FindIndex(t => t.Item == item);
            if (at >= 0) list[at] = new ResultTest(item, given, fingerprint);
            else list.Add(new ResultTest(item, given, fingerprint));
        }
        string id = title ?? report?["title_id"]?.GetValue<string>() ?? "";   // may stay empty: saving requires it
        string target = report?["target_firmware"]?.GetValue<string>() is { Length: > 0 } fw ? Firmware.Name(fw) : "";
        string eboot = build is not null && File.Exists(Path.Combine(build, "eboot.bin")) ? FakelibStore.Sha256(Path.Combine(build, "eboot.bin")) : "";
        return new ResultDraft(new ConsoleResult(date ?? DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), tester, console, id,
            build is null ? "" : Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(build))), target, eboot, result, problem, list,
            log is null ? "" : Path.GetFileName(log), logSha, source, note), crash);
    }

    /// <summary>The verdict a tested item gets from the run's outcome: pass when the game plays or boots, else unknown.</summary>
    public static string DefaultVerdict(string outcome) => outcome is "plays" or "boots" ? "pass" : "unknown";

    /// <summary>Every experimental item (derived rules, options, store fakelib variants), every store set and every item with
    /// runs, sorted by item.</summary>
    public List<MatrixRow> Matrix(Knowledge knowledge, string? store)
    {
        var experimental = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in knowledge.FixList.Where(f => !f.Proven))
            experimental.Add($"rule:{f.Nid}");
        foreach (string key in knowledge.Options.Keys)
            experimental.Add($"option:{key}");
        var items = new SortedSet<string>(experimental, StringComparer.Ordinal);
        if (store is not null && Directory.Exists(store))
            foreach (string set in Directory.EnumerateDirectories(store).Where(d => File.Exists(Path.Combine(d, FakelibStore.ManifestName))))
            {
                string name = Path.GetFileName(set);
                items.Add($"fakelib:{name}");
                foreach (string variant in Directory.EnumerateDirectories(set).Where(d => File.Exists(Path.Combine(d, FakelibStore.ManifestName))))
                {
                    items.Add($"fakelib:{name}/{Path.GetFileName(variant)}");
                    experimental.Add($"fakelib:{name}/{Path.GetFileName(variant)}");
                }
            }
        foreach (var t in Results.SelectMany(r => r.Tests))
            items.Add(t.Item);
        return [.. items.Select(item =>
        {
            string? fingerprint = Fingerprint(item, knowledge, store);
            return new MatrixRow(item, Label(item, knowledge), experimental.Contains(item), fingerprint, Evidence(item, fingerprint ?? "removed"));
        })];
    }

    /// <summary>The rule's function name, the option's name, or the fakelib set/variant ("" when the item no longer exists).</summary>
    public static string Label(string item, Knowledge knowledge) =>
        item.StartsWith("rule:", StringComparison.Ordinal) ? knowledge.Fixes.GetValueOrDefault(item[5..])?.Name
            ?? knowledge.Options.Values.SelectMany(o => o.Fixes.Values).FirstOrDefault(f => f.Nid == item[5..])?.Name ?? ""
        : item.StartsWith("option:", StringComparison.Ordinal) ? knowledge.Options.GetValueOrDefault(item[7..])?.Name ?? ""
        : item.StartsWith("fakelib:", StringComparison.Ordinal) ? (item.Contains('/') ? $"fakelib set {item[8..item.IndexOf('/')]}, variant {item[(item.IndexOf('/') + 1)..]}" : $"fakelib set {item[8..]}")
        : "";

    /// <summary>Per tester: experimental items run in their current form on some band but not yet on the tester's.</summary>
    public List<(Tester Tester, string Band, List<string> Items)> SendNext(IReadOnlyList<MatrixRow> matrix)
    {
        var started = matrix.Where(r => r.Experimental && r.Started).ToList();
        var bands = Bands.ToList();
        return [.. Testers.Values.OrderBy(t => Firmware.Parse(t.Firmware)).Select(t =>
        {
            string band = Band(t.Firmware);
            int index = bands.IndexOf(band);
            return (t, band, started.Where(r => !MatrixRow.Current(r.Bands[index])).Select(r => r.Item).ToList());
        })];
    }

    /// <summary>Runs that tested <paramref name="item"/>, newest first; Current = the run tested the form with
    /// <paramref name="fingerprint"/>.</summary>
    public List<(ConsoleResult Run, ResultTest Test, bool Current)> Runs(string item, string? fingerprint) =>
        [.. Results.SelectMany(r => r.Tests.Where(t => t.Item == item).Select(t => (r, t, fingerprint is not null && t.Fingerprint == fingerprint)))
                   .OrderByDescending(x => x.r.Date, StringComparer.Ordinal)];

    // --- promotion -------------------------------------------------------------------------------------------------

    /// <summary>What promoting <paramref name="item"/> would change. Only marking derived fixes proven is a data edit; making an
    /// option or a fakelib variant the default restructures rules or store files, so that part stays a written-out manual step.</summary>
    public static PromotionPlan Plan(string item, Knowledge knowledge)
    {
        if (item.StartsWith("rule:", StringComparison.Ordinal))
            return knowledge.Fixes.TryGetValue(item[5..], out var fix)
                ? new PromotionPlan(item, null, fix.Proven ? [] : [fix.Nid], fix.Proven ? "The rule is already proven." : "")
                : new PromotionPlan(item, null, [], "The rule no longer exists in data/fixes.json.");
        if (item.StartsWith("option:", StringComparison.Ordinal))
        {
            if (!knowledge.Options.TryGetValue(item[7..], out var option))
                return new PromotionPlan(item, null, [], "The option no longer exists in data/fixes.json.");
            string variant = option.FakelibVariant.Length > 0
                ? $" Its fakelib variant '{option.FakelibVariant}' becomes a store default separately (promote fakelib:<set>/{option.FakelibVariant})." : "";
            return new PromotionPlan(item, option.Key, [.. option.Fixes.Values.Where(f => !f.Proven && f.Kind != Fix.KindNone).Select(f => f.Nid)],
                "The option stays opt-in. Making it a default is a separate rule change: move its fixes into \"fixes\" and its patterns into " +
                "\"patterns\", delete the option and update its tests, as save_mount_compat was on 2026-10-14." + variant);
        }
        if (item.StartsWith("fakelib:", StringComparison.Ordinal) && item.Contains('/'))
        {
            string set = item[8..item.IndexOf('/')], variant = item[(item.IndexOf('/') + 1)..];
            return new PromotionPlan(item, null, [],
                $"A fakelib variant has no proven flag. To make it set {set}'s default: copy fakelibs/{set}/{variant}/*.sprx over the set's files, " +
                $"run `ps5backport fakelib verify`, keep the replaced files in work/fakelib-backups/, and decide whether its option stays.");
        }
        return new PromotionPlan(item, null, [], "A fakelib set has no proven flag; its console evidence is informational.");
    }

    /// <summary>Applies <paramref name="plan"/> to data/fixes.json in <paramref name="dataDir"/>: removes each listed fix's
    /// <c>"proven": false</c> (true is the default) and nothing else, keeping the file's formatting. The edit is checked twice:
    /// the reparsed document must equal the original with only those members removed, and the rule data must still load.
    /// Returns the changed NIDs.</summary>
    public static IReadOnlyList<string> ApplyPromotion(string dataDir, PromotionPlan plan)
    {
        if (!plan.CanApply)
            throw new InvalidOperationException($"{plan.Item}: nothing to change in data/fixes.json");
        string path = Path.Combine(dataDir, "fixes.json");
        byte[] original = File.ReadAllBytes(path);
        bool bom = original is [0xEF, 0xBB, 0xBF, ..];
        string text = new UTF8Encoding(false).GetString(original, bom ? 3 : 0, original.Length - (bom ? 3 : 0));
        string edited = MarkProven(text, plan.OptionKey, plan.ProveNids);
        File.WriteAllBytes(path, [.. bom ? new byte[] { 0xEF, 0xBB, 0xBF } : [], .. new UTF8Encoding(false).GetBytes(edited)]);
        try { _ = new Knowledge(dataDir); }
        catch (InvalidDataException)
        {
            File.WriteAllBytes(path, original);
            throw;
        }
        return plan.ProveNids;
    }

    /// <summary>The fixes.json text with <c>"proven": false</c> removed from each fix in <paramref name="nids"/> (default fixes, or
    /// option <paramref name="optionKey"/>'s fixes). Throws when a fix is missing, not marked false, or the result differs in
    /// anything else.</summary>
    public static string MarkProven(string text, string? optionKey, IEnumerable<string> nids)
    {
        var cuts = new List<(int Start, int End)>();
        foreach (string nid in nids)
        {
            var (open, _) = JsonText.Find(text, optionKey is null ? ["fixes", nid] : ["options", optionKey, "fixes", nid]);
            var members = JsonText.Members(text, open);
            int i = members.FindIndex(m => m.Key == "proven");
            if (i < 0 || text[members[i].ValueStart..members[i].ValueEnd] != "false")
                throw new InvalidOperationException($"fix {nid}: not marked \"proven\": false");
            // with a following member: cut up to it (it takes this member's place and indentation); as the last member: cut from
            // the end of the previous value, which removes the separating comma
            cuts.Add(i + 1 < members.Count ? (members[i].KeyStart, members[i + 1].KeyStart)
                   : i > 0 ? (members[i - 1].ValueEnd, members[i].ValueEnd) : (members[i].KeyStart, members[i].ValueEnd));
        }
        var builder = new StringBuilder(text);
        foreach (var (start, end) in cuts.OrderByDescending(c => c.Start))
            builder.Remove(start, end - start);
        string edited = builder.ToString();

        var expected = JsonNode.Parse(text)!;
        foreach (string nid in nids)
        {
            var record = (optionKey is null ? expected["fixes"]![nid] : expected["options"]![optionKey]!["fixes"]![nid])!.AsObject();
            record.Remove("proven");
        }
        if (!JsonNode.DeepEquals(expected, JsonNode.Parse(edited)))
            throw new InvalidOperationException("the edit changed more than the proven flags; data/fixes.json left unchanged");
        return edited;
    }

    /// <summary>Spans of members in JSON text, for edits that keep the hand formatting.</summary>
    static class JsonText
    {
        static int Ws(string s, int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            return i;
        }

        static int StringEnd(string s, int i)
        {
            for (i++; s[i] != '"'; i++)
                if (s[i] == '\\') i++;
            return i + 1;
        }

        static int ValueEnd(string s, int i)
        {
            i = Ws(s, i);
            if (s[i] == '"')
                return StringEnd(s, i);
            if (s[i] is '{' or '[')
                for (int depth = 0; ; i++)
                {
                    if (s[i] == '"') { i = StringEnd(s, i) - 1; continue; }
                    if (s[i] is '{' or '[') depth++;
                    else if (s[i] is '}' or ']' && --depth == 0) return i + 1;
                }
            while (i < s.Length && ",}] \t\r\n".IndexOf(s[i]) < 0) i++;
            return i;
        }

        /// <summary>Members of the object whose '{' is at <paramref name="open"/>.</summary>
        public static List<(int KeyStart, string Key, int ValueStart, int ValueEnd)> Members(string s, int open)
        {
            var members = new List<(int, string, int, int)>();
            int i = Ws(s, open + 1);
            while (s[i] != '}')
            {
                int keyEnd = StringEnd(s, i);
                string key = JsonNode.Parse(s[i..keyEnd])!.GetValue<string>();
                int valueStart = Ws(s, Ws(s, keyEnd) + 1);   // past ':'
                int valueEnd = ValueEnd(s, valueStart);
                members.Add((i, key, valueStart, valueEnd));
                i = Ws(s, valueEnd);
                if (s[i] == ',') i = Ws(s, i + 1);
            }
            return members;
        }

        /// <summary>The value span of the object member at <paramref name="path"/> (each step an object key).</summary>
        public static (int Start, int End) Find(string s, string[] path)
        {
            int start = Ws(s, 0), end = ValueEnd(s, start);
            foreach (string key in path)
            {
                if (s[start] != '{')
                    throw new KeyNotFoundException($"'{key}': parent is not an object");
                var member = Members(s, start).FirstOrDefault(m => m.Key == key);
                if (member.Key is null)
                    throw new KeyNotFoundException($"'{string.Join("/", path)}' not found in data/fixes.json");
                (start, end) = (member.ValueStart, member.ValueEnd);
            }
            return (start, end);
        }
    }
}
