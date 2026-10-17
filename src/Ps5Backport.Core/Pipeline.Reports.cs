using System.Text;
using System.Text.Json.Nodes;

namespace Ps5Backport.Core;

public static partial class Pipeline
{
    // --- reports -----------------------------------------------------------------
    /// <summary>The text report.</summary>
    public static string FormatReport(GameResult r) => FormatReport(r, Knowledge.Default);

    /// <summary>Formats using the knowledge that supplied the run's options.</summary>
    public static string FormatReport(GameResult r, Knowledge knowledge)
    {
        var lines = new List<string>
        {
            $"Backport of {r.Source}",
            $"  title {r.TitleId} version {r.ContentVersion} -> FW {r.TargetFirmware}",
            $"  output: {r.Output}",
            $"  reference: {r.Reference}",
            $"  fakelibs: {(r.FakelibSource.Length > 0 ? r.FakelibSource : "none (no --fakelib and no store folder for this firmware)")}",
        };
        if (r.Options.Count > 0)
            lines.Add("  options: " + string.Join(", ", r.Options.Select(key => knowledge.Options.TryGetValue(key, out var option) ? $"{key} ({option.Name})" : key)));
        foreach (var p in r.Recommendations)
            lines.Add($"  probe {Verdict(p.Verdict),-14} {p.Key}  {p.Evidence}");
        foreach (string f in r.App0Files)
            lines.Add($"  added file {f} (from the fakelib set)");
        foreach (var d in r.DataFiles)
            lines.Add($"  data file {d.Path}: {d.Pattern} x{d.Count} ({(d.Written ? "written" : "no match")})");
        lines.AddRange(r.ParamWarnings.Select(w => $"  WARNING: {w}"));
        lines.Add("  refs = direct code references to an unresolved import (0: reachable only through data, e.g. a vtable, or not at all)");
        var derived = r.Modules.SelectMany(m => m.Fixed).Where(f => !f.Proven).Select(f => f.Name)
            .Union(r.Modules.SelectMany(m => m.StubbedNow).Where(s => s.Origin.Contains("derived")).Select(s => s.Name))
            .Order(StringComparer.Ordinal).ToList();
        if (derived.Count > 0)
            lines.Add($"  NOTE: {derived.Count} derived fix(es) applied, not yet console-tested: {string.Join(", ", derived)}");
        int laterMissing = r.Modules.Sum(m => m.LaterMissing.Count);
        if (laterMissing > 0)
            lines.Add($"  NOTE: {laterMissing} import(s) resolve on FW {r.TargetFirmware} but are not exported by a later firmware (missing-later lines): the same cook fails on that console if they are called");
        foreach (var note in (JsonArray)DeploymentReport(r)["notes"]!)
            lines.Add("  deployment: " + note!.GetValue<string>());
        foreach (var e in r.ConsoleEvidence)
        {
            lines.Add($"  console  {e.Item}  {e.Summary}");
            foreach (string failure in e.Failures)
                lines.Add($"  WARNING: console evidence: {e.Item} failed on {failure}");
            if (e.Candidate)
                lines.Add($"  NOTE: promotion candidate: {e.Item} passed on every firmware band with no failure (mark proven or make default: your decision)");
        }
        foreach (var m in r.Modules)
        {
            lines.Add("");
            lines.Add($"{m.Path}  [{m.Container}]  {(m.Error.Length > 0 ? "ERROR" : m.Written ? "written" : "unchanged")}");
            if (m.Error.Length > 0)
            {
                lines.Add($"  error: {m.Error}");
                continue;
            }
            lines.Add(m.SdkPatched ? $"  SDK {m.SdkBefore} -> {m.SdkAfter}" : $"  SDK {m.SdkBefore} (kept)");
            if (m.SonyBuild.Length > 0)
                lines.Add($"  Sony build {m.SonyBuild}");
            lines.Add("  imports: " + string.Join(", ", m.Counts.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}")));
            if (m.TextExtended != 0)
                lines.Add($"  NOTE: code segment extended by 0x{m.TextExtended:x} bytes for fix code (automatic when padding runs out; two backports grew Unity PS5Util.prx the same way; not console-confirmed)");
            foreach (var f in m.Fixed)
                lines.Add($"  fixed    {f.Nid}  {f.Name}  ({f.How}; {(f.Proven ? "proven" : "DERIVED, not console-tested")}; from {f.Source})");
            foreach (var s in m.StubbedNow)
                lines.Add($"  stubbed  {s.Nid}  {s.Name}  ({s.Stub} @ {s.At}; {s.Origin}{(s.Introduced.Length > 0 ? $"; GOT pre-fill, native from FW {s.Introduced}" : "")})");
            foreach (var p in m.Patterns)
                lines.Add($"  pattern  {p.Name}  x{p.Count}");
            foreach (var u in m.Unknown)
            {
                string hint = u.Candidate.Length > 0 ? $"   candidate: {u.Candidate} (--candidate-fixes)" : "";
                string intro = u.Introduced.Length > 0 ? $"   introduced: FW {u.Introduced}" : "";
                string left = u.LeftBy.Length > 0 ? $"   left unpatched by: {u.LeftBy}" : "";
                string use = u.Result.Length > 0 ? $"   result: {u.Result}" : "";
                lines.Add($"  {u.Status,-8} {u.Nid}  {u.Name}  [{u.Library}]  refs={u.CodeRefs}{use}{hint}{intro}{left}");
            }
            foreach (var l in m.LaterMissing)
                lines.Add($"  missing-later {l.Nid}  {l.Name}  [{l.Library}]  exported on FW {r.TargetFirmware}, not on FW {l.MissingOn}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>Report spelling of a probe verdict.</summary>
    public static string Verdict(OptionVerdict v) => v switch
    {
        OptionVerdict.Needed => "needed", OptionVerdict.Applicable => "applicable", OptionVerdict.NotApplicable => "not-applicable",
        OptionVerdict.ConsoleChoice => "console-choice", _ => v.ToString().ToLowerInvariant(),
    };

    /// <summary>The JSON report; "console_evidence" only when the run was given console results.</summary>
    public static JsonObject ToJson(GameResult r)
    {
        var json = ToJsonCore(r);
        json["console_items"] = new JsonArray(r.ConsoleItems.Select(c => (JsonNode)new JsonObject
        {
            ["item"] = c.Item, ["fingerprint"] = c.Fingerprint, ["experimental"] = c.Experimental,
        }).ToArray());
        if (r.ConsoleEvidence.Count > 0)
            json["console_evidence"] = new JsonArray(r.ConsoleEvidence.Select(e => (JsonNode)new JsonObject
            {
                ["item"] = e.Item, ["fingerprint"] = e.Fingerprint, ["summary"] = e.Summary,
                ["bands"] = new JsonObject(ConsoleResults.Bands.Select((band, i) => new KeyValuePair<string, JsonNode?>(band, new JsonObject
                {
                    ["pass"] = e.Bands[i].Pass, ["fail"] = e.Bands[i].Fail, ["unknown"] = e.Bands[i].Unknown, ["old"] = e.Bands[i].Old,
                }))),
                ["failures"] = new JsonArray(e.Failures.Select(f => (JsonNode)f).ToArray()), ["candidate"] = e.Candidate,
            }).ToArray());
        return json;
    }

    static JsonObject ToJsonCore(GameResult r) => new()
    {
        ["source"] = r.Source, ["output"] = r.Output, ["target_firmware"] = r.TargetFirmware,
        ["title_id"] = r.TitleId, ["content_version"] = r.ContentVersion,
        ["param_warnings"] = new JsonArray(r.ParamWarnings.Select(w => (JsonNode)w).ToArray()),
        ["fakelib_source"] = r.FakelibSource, ["reference"] = r.Reference,
        ["deployment"] = DeploymentReport(r),
        ["psvr2"] = new JsonArray(r.Psvr2.Select(p => (JsonNode)new JsonObject
        {
            ["module"] = p.Module, ["library"] = p.Library, ["imports"] = p.Imports,
        }).ToArray()),
        ["options"] = new JsonArray(r.Options.Select(key => (JsonNode)key).ToArray()),
        ["recommendations"] = new JsonArray(r.Recommendations.Select(p => (JsonNode)new JsonObject
        {
            ["key"] = p.Key, ["verdict"] = Verdict(p.Verdict), ["evidence"] = p.Evidence,
        }).ToArray()),
        ["app0_files"] = new JsonArray(r.App0Files.Select(f => (JsonNode)f).ToArray()),
        ["data_files"] = new JsonArray(r.DataFiles.Select(d => (JsonNode)new JsonObject
        {
            ["path"] = d.Path, ["pattern"] = d.Pattern, ["count"] = d.Count, ["written"] = d.Written,
        }).ToArray()),
        ["modules"] = new JsonArray(r.Modules.Select(m => (JsonNode)new JsonObject
        {
            ["path"] = m.Path, ["container"] = m.Container, ["added"] = m.Added,
            ["sdk_before"] = m.SdkBefore, ["sdk_after"] = m.SdkAfter, ["sdk_patched"] = m.SdkPatched, ["sony_build"] = m.SonyBuild,
            ["counts"] = new JsonObject(m.Counts.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))),
            ["unknown"] = new JsonArray(m.Unknown.Select(u => (JsonNode)new JsonObject
            {
                ["nid"] = u.Nid, ["name"] = u.Name, ["library"] = u.Library, ["candidate"] = u.Candidate, ["status"] = u.Status,
                ["code_refs"] = u.CodeRefs, ["result"] = u.Result, ["left_by"] = u.LeftBy, ["introduced"] = u.Introduced,
            }).ToArray()),
            ["stubbed_now"] = new JsonArray(m.StubbedNow.Select(s => (JsonNode)new JsonObject
            {
                ["nid"] = s.Nid, ["name"] = s.Name, ["stub"] = s.Stub, ["got"] = s.Got, ["at"] = s.At,
                ["known_fix"] = s.KnownFix, ["origin"] = s.Origin, ["introduced"] = s.Introduced,
            }).ToArray()),
            ["later_missing"] = new JsonArray(m.LaterMissing.Select(l => (JsonNode)new JsonObject
            {
                ["nid"] = l.Nid, ["name"] = l.Name, ["library"] = l.Library, ["missing_on"] = l.MissingOn,
            }).ToArray()),
            ["fixed"] = new JsonArray(m.Fixed.Select(f => (JsonNode)new JsonObject
            {
                ["nid"] = f.Nid, ["name"] = f.Name, ["how"] = f.How, ["source"] = f.Source, ["proven"] = f.Proven,
            }).ToArray()),
            ["patterns"] = new JsonArray(m.Patterns.Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["count"] = p.Count }).ToArray()),
            ["text_extended"] = m.TextExtended, ["written"] = m.Written, ["error"] = m.Error,
        }).ToArray()),
    };

    /// <summary>
    /// backport_report.json and backport_report.txt. Line endings are the platform's.
    /// </summary>
    public static void WriteReports(GameResult r, string output) => WriteReports(r, output, Knowledge.Default);

    /// <summary>Writes reports using the run's captured knowledge.</summary>
    public static void WriteReports(GameResult r, string output, Knowledge knowledge)
    {
        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(System.IO.Path.Combine(output, ReportJson), PyJson.Serialize(ToJson(r), 2).Replace("\n", Environment.NewLine), utf8);
        File.WriteAllText(System.IO.Path.Combine(output, ReportTxt), (FormatReport(r, knowledge) + "\n").Replace("\n", Environment.NewLine), utf8);
    }
}
