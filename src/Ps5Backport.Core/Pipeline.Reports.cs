using System.Text;
using System.Text.Json.Nodes;

namespace Ps5Backport.Core;

public static partial class Pipeline
{
    // --- reports -----------------------------------------------------------------
    /// <summary>The text report.</summary>
    public static string FormatReport(GameResult r)
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
            lines.Add("  options: " + string.Join(", ", r.Options.Select(key => $"{key} ({Knowledge.Default.Options[key].Name})")));
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
                lines.Add($"  stubbed  {s.Nid}  {s.Name}  ({s.Stub} @ {s.At}; {s.Origin})");
            foreach (var p in m.Patterns)
                lines.Add($"  pattern  {p.Name}  x{p.Count}");
            foreach (var u in m.Unknown)
            {
                string hint = u.Candidate.Length > 0 ? $"   candidate: {u.Candidate} (--candidate-fixes)" : "";
                string intro = u.Introduced.Length > 0 ? $"   introduced: FW {u.Introduced}" : "";
                string left = u.LeftBy.Length > 0 ? $"   left unpatched by: {u.LeftBy}" : "";
                lines.Add($"  {u.Status,-8} {u.Nid}  {u.Name}  [{u.Library}]  refs={u.CodeRefs}{hint}{intro}{left}");
            }
        }
        return string.Join("\n", lines);
    }

    /// <summary>The JSON report.</summary>
    public static JsonObject ToJson(GameResult r) => new()
    {
        ["source"] = r.Source, ["output"] = r.Output, ["target_firmware"] = r.TargetFirmware,
        ["title_id"] = r.TitleId, ["content_version"] = r.ContentVersion,
        ["param_warnings"] = new JsonArray(r.ParamWarnings.Select(w => (JsonNode)w).ToArray()),
        ["fakelib_source"] = r.FakelibSource, ["reference"] = r.Reference,
        ["psvr2"] = new JsonArray(r.Psvr2.Select(p => (JsonNode)new JsonObject
        {
            ["module"] = p.Module, ["library"] = p.Library, ["imports"] = p.Imports,
        }).ToArray()),
        ["options"] = new JsonArray(r.Options.Select(key => (JsonNode)key).ToArray()),
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
                ["code_refs"] = u.CodeRefs, ["left_by"] = u.LeftBy, ["introduced"] = u.Introduced,
            }).ToArray()),
            ["stubbed_now"] = new JsonArray(m.StubbedNow.Select(s => (JsonNode)new JsonObject
            {
                ["nid"] = s.Nid, ["name"] = s.Name, ["stub"] = s.Stub, ["got"] = s.Got, ["at"] = s.At,
                ["known_fix"] = s.KnownFix, ["origin"] = s.Origin,
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
    public static void WriteReports(GameResult r, string output)
    {
        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(System.IO.Path.Combine(output, ReportJson), PyJson.Serialize(ToJson(r), 2).Replace("\n", Environment.NewLine), utf8);
        File.WriteAllText(System.IO.Path.Combine(output, ReportTxt), (FormatReport(r) + "\n").Replace("\n", Environment.NewLine), utf8);
    }
}
