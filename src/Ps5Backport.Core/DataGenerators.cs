using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ps5Backport.Core;

/// <summary>
/// Regenerates the shared knowledge files from their sources (formerly tools/*.py):
/// firmware_db/ from firmware dumps, data/sony_builds.json from the same dumps, and
/// data/candidates.json from AnyPS5's "Silent stubs" list.
/// </summary>
public static class DataGenerators
{
    static readonly string[] ModuleExtensions = [".sprx", ".prx", ".elf"];

    // --- firmware_db/ from dumps (Firmware X.YY/**/common/lib) -----------------------------------

    /// <summary>
    /// One firmware's export list from &lt;dumps&gt;/Firmware &lt;fw&gt;: only common/lib folders (what games load;
    /// system_1/system_2 duplicates are merged). Encrypted or unreadable modules are skipped; the firmware is
    /// refused (null, reason) unless every module name found has a decrypted copy. Managed *.dll.sprx /
    /// *.exe.sprx assemblies are skipped: games don't import them.
    /// </summary>
    public static (FirmwareExports? Db, string Problem) ImportFirmwareDump(string dumps, string firmware)
    {
        string fwDir = Path.Combine(dumps, $"Firmware {firmware}");
        var modules = new Dictionary<string, HashSet<string>>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var decrypted = new HashSet<string>(StringComparer.Ordinal);
        var used = new List<string>();
        foreach (string dir in LibDirs(fwDir).Order(StringComparer.Ordinal))
        {
            int encrypted = 0;
            foreach (string file in Directory.EnumerateFiles(dir).Order(StringComparer.Ordinal))
            {
                string f = Path.GetFileName(file), lower = f.ToLowerInvariant();
                if (!ModuleExtensions.Any(lower.EndsWith) || lower.EndsWith(".dll.sprx") || lower.EndsWith(".exe.sprx"))
                    continue;
                names.Add(lower);
                string kind = Self.FileKind(file);
                SceElf elf;
                if (Self.IsSelf(kind))
                {
                    if (Self.EncryptedSegments(file) > 0)
                    {
                        encrypted++;
                        continue;
                    }
                    try { elf = new SceElf(Self.Unwrap(File.ReadAllBytes(file))); }
                    catch (InvalidDataException)
                    {
                        encrypted++;                 // unreadable SELF: treat like an encrypted one
                        continue;
                    }
                }
                else if (kind == Self.KindElf)
                    elf = SceElf.Load(file);
                else
                    continue;
                decrypted.Add(lower);
                string module = elf.ModuleName ?? Path.GetFileNameWithoutExtension(f);
                if (!modules.TryGetValue(module, out var set))
                    modules[module] = set = new HashSet<string>(StringComparer.Ordinal);
                set.UnionWith(elf.Exports());
            }
            used.Add(Path.GetRelativePath(fwDir, dir) + (encrypted > 0 ? $" ({encrypted} encrypted skipped)" : ""));
        }
        var missing = names.Except(decrypted).Order(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
            return (null, $"incomplete: {missing.Count} module(s) only encrypted, e.g. [{string.Join(", ", missing.Take(3).Select(m => $"'{m}'"))}]");
        if (modules.Count == 0)
            return (null, "no decrypted system modules found; no export list written");
        string source = $"own dump: Firmware {firmware} " + string.Join("; ", used).Replace(Path.DirectorySeparatorChar, '/');
        return (new FirmwareExports(firmware, source, true, modules), "");
    }

    /// <summary>The firmware folders under <paramref name="dumps"/> ("Firmware X.YY"), oldest first.</summary>
    public static List<string> DumpedFirmwares(string dumps) =>
        Directory.EnumerateDirectories(dumps).Select(Path.GetFileName)
                 .Where(d => d!.StartsWith("Firmware ", StringComparison.Ordinal)).Select(d => d!["Firmware ".Length..])
                 .OrderBy(FirmwareDb.Key).ToList();

    static IEnumerable<string> LibDirs(string fwDir)
    {
        foreach (string dir in Walk(fwDir))
        {
            string rel = Path.GetRelativePath(fwDir, dir).Replace(Path.DirectorySeparatorChar, '/');
            if ((rel.EndsWith("common/lib", StringComparison.Ordinal) || rel == "lib" || rel.EndsWith("system_3/common", StringComparison.Ordinal))
                && Directory.EnumerateFiles(dir).Any(f => ModuleExtensions.Any(f.ToLowerInvariant().EndsWith)))
                yield return dir;
        }
    }

    /// <summary>Top-down directory walk in file-system order (Python's os.walk).</summary>
    static IEnumerable<string> Walk(string dir)
    {
        var pending = new Stack<string>();
        pending.Push(dir);
        while (pending.TryPop(out string? current))
        {
            yield return current;
            foreach (string sub in Directory.EnumerateDirectories(current).Reverse())
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0)
                    pending.Push(sub);
        }
    }

    // --- data/sony_builds.json ---------------------------------------------------------------

    static readonly Regex BuildId = new(@"W:/Build/(J\d+)/", RegexOptions.Compiled);

    /// <summary>
    /// Sony build id -> firmware(s), from the dumps' common/lib modules. An id belongs to the firmware whose dump
    /// holds most of its files; firmwares with at least half as many (5.02 and 5.10 ship the same libraries)
    /// share it, and strays (a copied file or two) don't count.
    /// </summary>
    public static SortedDictionary<string, string> ScanSonyBuilds(string dumps)
    {
        var counts = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (string fwDir in Directory.EnumerateDirectories(dumps, "Firmware *").Order(StringComparer.Ordinal))
        {
            string fw = Path.GetFileName(fwDir)["Firmware ".Length..];
            foreach (string dir in Walk(fwDir).Where(d => d.Replace(Path.DirectorySeparatorChar, '/').EndsWith("/common/lib", StringComparison.OrdinalIgnoreCase)))
                foreach (string path in Directory.EnumerateFiles(dir, "*.sprx"))
                {
                    if (path.EndsWith(".dll.sprx", StringComparison.Ordinal) || path.EndsWith(".exe.sprx", StringComparison.Ordinal))
                        continue;
                    var m = BuildId.Match(Encoding.Latin1.GetString(File.ReadAllBytes(path)));
                    if (!m.Success)
                        continue;
                    if (!counts.TryGetValue(m.Groups[1].Value, out var c))
                        counts[m.Groups[1].Value] = c = new Dictionary<string, int>(StringComparer.Ordinal);
                    c[fw] = c.GetValueOrDefault(fw) + 1;
                }
        }
        var output = new SortedDictionary<string, string>(Comparer<string>.Create((a, b) => long.Parse(a[1..]).CompareTo(long.Parse(b[1..]))));
        foreach (var (id, c) in counts)
        {
            int top = c.Values.Max();
            output[id] = string.Join("/", c.Where(kv => kv.Value * 2 >= top).Select(kv => kv.Key).OrderBy(FirmwareDb.Key));
        }
        return output;
    }

    /// <summary>Writes data/sony_builds.json; its "evidence" section (firmwares without a decrypted dump) is kept.</summary>
    public static int WriteSonyBuilds(string dumps, string outPath)
    {
        JsonNode evidence = new JsonObject();
        if (File.Exists(outPath) && JsonNode.Parse(File.ReadAllText(outPath))?["evidence"] is { } old)
            evidence = old.DeepClone();
        var builds = ScanSonyBuilds(dumps);
        var doc = new JsonObject
        {
            ["_about"] = "Sony build id (the J<number> in W:/Build/J<number>/ inside a system library) -> firmware it shipped " +
                         "with. \"builds\" is generated by `ps5backport data sony-builds` from the decrypted firmware dumps (three ids " +
                         "per firmware); \"evidence\" covers firmwares without a decrypted dump. Ids grow with time, so an unknown " +
                         "id is placed between the known ones.",
            ["builds"] = new JsonObject(builds.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["evidence"] = evidence,
        };
        PyJson.WriteFile(outPath, doc, 2);
        return builds.Count;
    }

    // --- data/candidates.json (AnyPS5 "Silent stubs") ----------------------------------------

    static readonly Regex Func = new(
        @"(?:APS5_EXPORT\(""([A-Za-z0-9+\-]{11})"",\s*\w+\);\s*)?[\w:<>\s\*]+?APS5_VABI\s+(\w+)\s*\(([^)]*)\)\s*(?:noexcept\s*)?(?:try\s*)?\{",
        RegexOptions.Compiled);

    static readonly Regex Constant = new(
        @"(?:constexpr[^=;]*?\b|#define\s+|^\s*)([A-Z][A-Z0-9_]{2,})\s*=?\s*(?:static_cast<[^>]+>\()?\(?\s*(-?0x[0-9A-Fa-f]+|-?\d+)[uUlL]*\)?",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public sealed record Candidate(string Nid, string Name, uint Value, string Source);

    /// <summary>
    /// Functions AnyPS5 implements as a bare <c>return &lt;constant&gt;;</c> and lists under "Silent stubs" in
    /// docs/dev/TechnicalDebt.md (games are known to survive that result), sorted by name.
    /// </summary>
    public static List<Candidate> ScanCandidates(string anyps5)
    {
        string prx = Path.Combine(anyps5, "core", "libs", "prx");
        var consts = LoadConstants(Path.Combine(anyps5, "core", "libs"));
        var found = new List<Candidate>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (rel, named) in SilentStubBullets(anyps5))
        {
            string path = Path.Combine(prx, rel);
            if (!File.Exists(path))
                continue;
            string text = ReadSource(path);
            var matches = Func.Matches(text);
            var restrict = named.Intersect(matches.Select(m => m.Groups[2].Value)).ToHashSet(StringComparer.Ordinal);
            foreach (Match m in matches)
            {
                string name = m.Groups[2].Value;
                if (restrict.Count > 0 && !restrict.Contains(name))
                    continue;
                if (ConstantReturn(BodyOf(text, m.Index + m.Length - 1), consts) is not { } value)
                    continue;
                string nid = m.Groups[1].Success ? m.Groups[1].Value : Nid.Compute(name);
                int line = text.AsSpan(0, m.Index).Count('\n') + 1;
                var c = new Candidate(nid, name, (uint)(value & 0xFFFFFFFF), $"AnyPS5 {rel}:{line}");
                if (index.TryGetValue(nid, out int i))
                    found[i] = c;                   // a later definition wins, the first position stays
                else
                {
                    index[nid] = found.Count;
                    found.Add(c);
                }
            }
        }
        return found.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Writes data/candidates.json.</summary>
    public static List<Candidate> WriteCandidates(string anyps5, string outPath)
    {
        var found = ScanCandidates(anyps5);
        var doc = new JsonObject
        {
            ["_about"] = "GENERATED by `ps5backport data candidates` from AnyPS5 docs/dev/TechnicalDebt.md \"Silent stubs\". " +
                         "Reference-derived stub candidates (not console-proven): functions AnyPS5 implements as " +
                         "`return <constant>;` and documents as safe no-ops. Applied only with --candidate-fixes, and " +
                         "only to imports missing on the target firmware. Regenerate instead of editing by hand.",
            ["candidates"] = new JsonObject(found.Select(c => KeyValuePair.Create(c.Nid, (JsonNode?)new JsonObject
            {
                ["name"] = c.Name, ["value"] = $"0x{c.Value:x8}", ["source"] = c.Source,
            }))),
        };
        PyJson.WriteFile(outPath, doc, 2);
        return found;
    }

    /// <summary>A source file as Python reads it: UTF-8 with replacement characters, universal newlines.</summary>
    static string ReadSource(string path) =>
        File.ReadAllText(path, new UTF8Encoding(false, false)).Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>NAME -> value from constexpr/#define/enum definitions (the first one found wins).</summary>
    static Dictionary<string, BigInteger> LoadConstants(string libs)
    {
        var consts = new Dictionary<string, BigInteger>(StringComparer.Ordinal);
        foreach (string dir in Walk(libs))
            foreach (string file in Directory.EnumerateFiles(dir))
            {
                if (!(file.EndsWith(".hpp", StringComparison.Ordinal) || file.EndsWith(".cpp", StringComparison.Ordinal) || file.EndsWith(".h", StringComparison.Ordinal)))
                    continue;
                foreach (Match m in Constant.Matches(ReadSource(file)))
                    consts.TryAdd(m.Groups[1].Value, ParseInt(m.Groups[2].Value));
            }
        return consts;
    }

    static BigInteger ParseInt(string s)
    {
        bool negative = s.StartsWith('-');
        string digits = negative ? s[1..] : s;
        var v = digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? BigInteger.Parse("0" + digits[2..], System.Globalization.NumberStyles.HexNumber)
            : BigInteger.Parse(digits);
        return negative ? -v : v;
    }

    static string BodyOf(string text, int brace)
    {
        int depth = 0;
        for (int i = brace; i < text.Length; i++)
        {
            if (text[i] == '{')
                depth++;
            else if (text[i] == '}' && --depth == 0)
                return text[(brace + 1)..i];
        }
        return "";
    }

    /// <summary>The returned constant when the body is only <c>(void)x;</c> statements plus <c>return C;</c>.</summary>
    static BigInteger? ConstantReturn(string body, Dictionary<string, BigInteger> consts)
    {
        body = Regex.Replace(body, @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);
        var stmts = body.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0)
                        .Where(s => !Regex.IsMatch(s, @"^\(void\)\s*\w+$")).ToList();
        if (stmts.Count != 1 || !stmts[0].StartsWith("return", StringComparison.Ordinal))
            return null;
        string expr = stmts[0]["return".Length..].Trim();
        expr = Regex.Replace(expr, @"^static_cast<[^>]+>\((.*)\)$", "$1").Trim();
        if (Regex.IsMatch(expr, @"^-?(0x[0-9A-Fa-f]+|\d+)[uUlL]*$"))
            return ParseInt(expr.TrimEnd('u', 'U', 'l', 'L'));
        return consts.TryGetValue(expr, out var v) ? v : null;
    }

    /// <summary>[(linked file, named functions)] from the "Silent stubs" section.</summary>
    static List<(string Rel, HashSet<string> Named)> SilentStubBullets(string anyps5)
    {
        string text = File.ReadAllText(Path.Combine(anyps5, "docs", "dev", "TechnicalDebt.md")).Replace("\r\n", "\n");
        string section = text.Split("### Silent stubs", 2)[1].Split("\n### ", 2)[0];
        var output = new List<(string, HashSet<string>)>();
        foreach (string line in section.Split('\n'))
        {
            if (!line.StartsWith("- [", StringComparison.Ordinal))
                continue;
            var link = Regex.Match(line, @"\]\(\.\./\.\./core/libs/prx/([^)]+)\)");
            if (link.Success)
                output.Add((link.Groups[1].Value, Regex.Matches(line, @"`(\w+)`").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal)));
        }
        return output;
    }
}
