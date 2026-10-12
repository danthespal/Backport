using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ps5Backport.Core;

/// <summary>Read-only fatal-signal diagnostics. Logged text offsets do not establish ELF coordinates or crash cause.</summary>
public static class CrashLog
{
    const int MaxBytes = 1024 * 1024, MaxLine = 4096, MaxEvents = 64, MaxAddresses = 128, MaxModules = 256;
    static readonly RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    static Match Match(string text, string pattern) => Regex.Match(text, pattern, Options);
    static string Hex(ulong value) => "0x" + value.ToString("x", CultureInfo.InvariantCulture);

    /// <summary>Reads a bounded log and optional cook report without modifying either; records their SHA256 identities.</summary>
    public static JsonObject AnalyzeFiles(string logPath, string? reportPath = null)
    {
        byte[] raw = ReadBounded(logPath, MaxBytes);
        JsonObject? report = null;
        byte[]? reportBytes = null;
        if (reportPath is not null)
        {
            reportBytes = ReadBounded(reportPath, 8 * MaxBytes);
            // Cook reports may carry a UTF-8 BOM.
            string text = Encoding.UTF8.GetString(reportBytes).TrimStart('\uFEFF');
            report = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("Cook report must be a JSON object.");
        }
        var result = Analyze(raw, report);
        result["logPath"] = Path.GetFullPath(logPath);
        if (reportPath is not null)
        {
            result["reportPath"] = Path.GetFullPath(reportPath);
            result["reportSha256"] = Convert.ToHexStringLower(SHA256.HashData(reportBytes!));
        }
        return result;
    }

    static byte[] ReadBounded(string path, int limit)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > limit) throw new InvalidDataException($"Input exceeds {limit} byte limit: {path}");
        using var buffer = new MemoryStream();
        byte[] block = new byte[8192];
        int count;
        while ((count = stream.Read(block)) > 0)
        {
            if (buffer.Length + count > limit) throw new InvalidDataException("Input grew beyond byte limit.");
            buffer.Write(block, 0, count);
        }
        return buffer.ToArray();
    }

    /// <summary>Parses full fatal dumps, numbered excerpts and ShadowMount summaries. Report associations are unverified candidates.</summary>
    public static JsonObject Analyze(byte[] raw, JsonObject? report = null)
    {
        if (raw.Length > MaxBytes) throw new InvalidDataException("Log exceeds 1 MiB limit.");
        JsonArray reportModules = report?["modules"] as JsonArray ?? new JsonArray();
        if (report is not null && report["modules"] is not JsonArray)
            throw new InvalidDataException("Cook report needs a modules array.");
        if (reportModules.Count > 4096 || reportModules.Any(m => m is not JsonObject || m["path"] is not JsonValue v || !v.TryGetValue<string>(out var path) || string.IsNullOrEmpty(path)))
            throw new InvalidDataException("Invalid cook report module paths or module count.");
        string decoded = Encoding.UTF8.GetString(raw), text = decoded.Replace("\0", "", StringComparison.Ordinal);
        var events = new JsonArray(); var warnings = new JsonArray();
        JsonObject? current = null, pending = null;
        string section = "";
        void Warn(int line, string message) => warnings.Add(new JsonObject { ["line"] = line, ["warning"] = message });
        void Start(int line)
        {
            if (events.Count >= MaxEvents) throw new InvalidDataException("Too many fatal events.");
            current = new JsonObject { ["line"] = line, ["signal"] = null, ["pid"] = null, ["reason"] = "", ["addresses"] = new JsonArray(), ["modules"] = new JsonArray() };
            events.Add(current); pending = null; section = "";
        }
        void Add(string kind, string token, int line)
        {
            if (!Address(token, out ulong value)) { Warn(line, "Invalid address: " + token); return; }
            var addresses = (JsonArray)current!["addresses"]!;
            if (addresses.Count >= MaxAddresses) throw new InvalidDataException("Too many address records.");
            addresses.Add(new JsonObject { ["kind"] = kind, ["value"] = value, ["runtimeAddress"] = Hex(value), ["line"] = line });
        }
        using var reader = new StringReader(text);
        int number = 0;
        while (reader.ReadLine() is { } rawLine)
        {
            number++;
            if (rawLine.Length > MaxLine) throw new InvalidDataException("Log line exceeds 4096 character limit.");
            // Excerpts number original comment lines; retain physical input line numbers in output.
            string line = rawLine.Trim();
            var numbered = Match(line, @"^\d+:\s?(#.*)$");
            if (numbered.Success) line = numbered.Groups[1].Value;
            if (line == "# A user thread receives a fatal signal" || line.StartsWith("# exception: ", StringComparison.Ordinal))
            {
                Start(number);
                if (line.StartsWith("# exception: ", StringComparison.Ordinal)) current!["reason"] = line[13..];
                continue;
            }
            if (line.StartsWith("# signal: ", StringComparison.Ordinal))
            {
                if (current is null || current["signal"] is not null) Start(number);
                current!["signal"] = line[10..]; continue;
            }
            var summary = Match(line, @"\[MDBG\] game error: (\S+) pid=(\d+): (.*)");
            if (summary.Success)
            {
                Start(number); current!["title"] = summary.Groups[1].Value;
                if (long.TryParse(summary.Groups[2].Value, out long pid)) current["pid"] = pid;
                current["reason"] = summary.Groups[3].Value;
                foreach (Match item in Regex.Matches(summary.Groups[3].Value, @"(fault address|RIP|backtrace\[\d+\]):\s*(\S+)", Options))
                    Add(item.Groups[1].Value, item.Groups[2].Value.TrimEnd(';', ','), number);
                continue;
            }
            if (current is null) continue;
            if (line.StartsWith("# proc ID: ", StringComparison.Ordinal))
            {
                if (long.TryParse(line[11..], NumberStyles.None, CultureInfo.InvariantCulture, out long pid)) current["pid"] = pid;
                else Warn(number, "Invalid process ID.");
            }
            else if (line.StartsWith("# reason: ", StringComparison.Ordinal)) current["reason"] = line[10..];
            else if (line == "# fault address:" || line.StartsWith("# fault address: ", StringComparison.Ordinal)) Add("fault address", line[16..].Trim(), number);
            else if (line == "# rip:" || line.StartsWith("# rip: ", StringComparison.Ordinal)) Add("RIP", line[6..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "", number);
            else if (line == "# backtrace:") { section = "backtrace"; pending = null; }
            else if (line == "# dynamic libraries:") { section = "modules"; pending = null; }
            else if (section == "backtrace" && Match(line, @"^#\s+(?:0x)?[0-9a-fA-F]+$").Success) Add("backtrace", line[1..].Trim(), number);
            else if (section == "modules" && line.StartsWith("# /", StringComparison.Ordinal))
            {
                var modules = (JsonArray)current["modules"]!;
                if (modules.Count >= MaxModules) throw new InvalidDataException("Too many module records.");
                pending = new JsonObject { ["path"] = line[2..], ["line"] = number, ["ranges"] = new JsonArray(), ["fingerprint"] = "" };
                modules.Add(pending);
            }
            else if (section == "modules" && line.StartsWith("#  xotext:", StringComparison.Ordinal))
            {
                var range = Match(line, @"^#\s+xotext:\s+(\S+):(\S+)\s+nsegs:\s+\d+$");
                if (pending is null || !range.Success || !Address(range.Groups[1].Value, out ulong start) || !Address(range.Groups[2].Value, out ulong end) || start >= end)
                    Warn(number, "Invalid or unattached text range.");
                else
                {
                    var ranges = (JsonArray)pending["ranges"]!;
                    if (ranges.Count >= MaxModules) throw new InvalidDataException("Too many text ranges.");
                    ranges.Add(new JsonObject { ["start"] = start, ["end"] = end, ["line"] = number });
                }
            }
            else if (section == "modules" && line.StartsWith("#  fingerprint:", StringComparison.Ordinal) && pending is not null)
                pending["fingerprint"] = line[(line.IndexOf(':') + 1)..].Trim();
        }
        foreach (JsonObject ev in events.Cast<JsonObject>())
        foreach (JsonObject item in ((JsonArray)ev["addresses"]!).Cast<JsonObject>())
        {
            ulong value = item["value"]!.GetValue<ulong>();
            var matches = (from JsonObject module in (JsonArray)ev["modules"]!
                           from JsonObject range in (JsonArray)module["ranges"]!
                           where value != 0 && range["start"]!.GetValue<ulong>() <= value && value < range["end"]!.GetValue<ulong>()
                           select (module, range)).ToList();
            item["resolution"] = value == 0 ? "zero address; no module attribution" : matches.Count == 0 ? "no logged text range" : matches.Count > 1 ? "ambiguous overlapping text ranges" : "unique logged text range";
            if (matches.Count != 1) continue;
            var (matched, region) = matches[0];
            string path = matched["path"]!.GetValue<string>();
            item["runtimeModule"] = path;
            item["textRelativeOffset"] = Hex(value - region["start"]!.GetValue<ulong>());
            string Normalize(string p) => p.Replace('\\', '/');
            var exact = reportModules.Where(m => path.StartsWith("/app0/", StringComparison.Ordinal) && Normalize(m!["path"]!.GetValue<string>()) == path[6..]).ToList();
            var candidates = exact.Count > 0 ? exact : reportModules.Where(m => Normalize(m!["path"]!.GetValue<string>()).Split('/')[^1] == path.Split('/')[^1]).ToList();
            item["reportModuleCandidates"] = new JsonArray(candidates.Select(m => (JsonNode?)new JsonObject {
                ["path"] = m!["path"]!.DeepClone(), ["fixed"] = m["fixed"]?.DeepClone(),
                ["stubbed_now"] = m["stubbed_now"]?.DeepClone(), ["patterns"] = m["patterns"]?.DeepClone() }).ToArray());
        }
        return new JsonObject { ["schemaVersion"] = 1, ["logSha256"] = Convert.ToHexStringLower(SHA256.HashData(raw)),
            ["events"] = events, ["warnings"] = warnings, ["nulBytesIgnoredForParsing"] = raw.Count(b => b == 0),
            ["decodeReplacementCharacters"] = decoded.Count(c => c == '\uFFFD'),
            ["coordinateNote"] = "Offsets are relative to logged xotext start (half-open ranges); ELF virtual addresses and file offsets require matching binary/layout evidence.",
            ["reportIdentity"] = "unverified; path/filename association and opaque log fingerprints do not prove a loaded-binary SHA256 match",
            ["cause"] = "unknown; cook-report patch context does not establish crash cause" };
    }

    static bool Address(string token, out ulong value)
    {
        if (token.StartsWith("0x", StringComparison.Ordinal)) token = token[2..];
        value = 0;
        return token.Length is > 0 and <= 16 && ulong.TryParse(token, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }
}
