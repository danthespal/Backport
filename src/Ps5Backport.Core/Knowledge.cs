using System.Globalization;
using System.Text.Json;

namespace Ps5Backport.Core;

/// <summary>One adapter instruction: hex bytes, or "call the real function through its GOT slot".</summary>
public sealed record Insn(byte[] Bytes, bool CallGot, string Asm)
{
    public const string CallGotToken = "call_got";
    public int Length => CallGot ? 6 : Bytes.Length;
    public string Hex => CallGot ? CallGotToken : Convert.ToHexStringLower(Bytes);

    /// <summary>"4989d0  ; mov r8, rdx" -> bytes + asm note.</summary>
    public static Insn Parse(string entry)
    {
        int semi = entry.IndexOf(';');
        string hex = (semi < 0 ? entry : entry[..semi]).Trim();
        string asm = semi < 0 ? "" : entry[(semi + 1)..].Trim();
        return hex == CallGotToken ? new Insn([], true, asm) : new Insn(Convert.FromHexString(hex), false, asm);
    }
}

/// <summary>A known fix for one imported library function (data/fixes.json).</summary>
public sealed record Fix(
    string Nid, string Kind, string Name, string Area, IReadOnlyList<string> Sources,
    string Stub, string NewNid, IReadOnlyList<Insn> Insns, string ExportNid,
    string When, bool PostCall, bool Proven, string Note, string Rationale, string Below = "")
{
    public const string KindStub = "stub", KindSwap = "swap", KindAdapter = "adapter", KindRedirect = "redirect",
                        KindPltStub = "plt_stub", KindNone = "none";
    public const string WhenMissing = "missing", WhenAlways = "always";

    /// <summary>False when the fix is limited to targets below <see cref="Below"/> and <paramref name="firmware"/> isn't.</summary>
    public bool Due(string firmware) => Below.Length == 0 || Firmware.Parse(firmware).CompareTo(Firmware.Parse(Below)) < 0;
}

/// <summary>A byte replacement of an option: in modules only 4-aligned hits inside non-executable segments,
/// in <see cref="Files"/> (relative to the game root) every hit.</summary>
/// <see cref="Code"/>: search executable segments at any offset (the find bytes carry enough context to match one function).
public sealed record FixPattern(string Name, byte[] Find, byte[] Replace, IReadOnlyList<string> Files,
                                bool Code = false, IReadOnlyList<string>? Sources = null, string Note = "");

/// <summary>An opt-in group of fixes (data/fixes.json "options"), never applied by default.
/// <see cref="FakelibVariant"/>: files in the fakelib set's &lt;variant&gt;/ subfolder replace the same-named fakelibs.</summary>
public sealed record FixOption(string Key, string Name, string Description, string Warning,
                               IReadOnlyList<string> Sources, IReadOnlyDictionary<string, Fix> Fixes,
                               string FakelibVariant, IReadOnlyList<FixPattern> Patterns);

public sealed record StubBody(string Name, byte[] Bytes, string Asm, string Note);

/// <summary>Reference-derived stub candidate (AnyPS5 silent stub; not console-proven).</summary>
public sealed record Candidate(string Nid, string Name, uint Value, string Source);

public sealed record KnownBuild(string Module, string BuildDate, string State, string Note);

/// <summary>
/// The shared knowledge in data/*.json: fixes, stub bodies, candidates,
/// left-unpatched evidence, introduced-in facts and known homebrew builds.
/// </summary>
public sealed class Knowledge
{
    public string DataDir { get; }
    /// <summary>Source key ("GT7") -> full source name.</summary>
    public IReadOnlyDictionary<string, string> SourceNames { get; }
    /// <summary>_umtx_op (FreeBSD), the SyncOnAddress adapters' target.</summary>
    public string UmtxOp { get; }
    /// <summary>NID -> fix, in file order.</summary>
    public IReadOnlyDictionary<string, Fix> Fixes { get; }
    public IReadOnlyList<Fix> FixList { get; }
    /// <summary>Key -> opt-in fix group, in file order.</summary>
    public IReadOnlyDictionary<string, FixOption> Options { get; }
    /// <summary>Byte patterns applied with the known fixes (data/fixes.json "patterns").</summary>
    public IReadOnlyList<FixPattern> Patterns { get; }
    public IReadOnlyDictionary<string, StubBody> Stubs { get; }
    public IReadOnlyDictionary<string, Candidate> Candidates { get; }
    /// <summary>NID -> backports that shipped it unpatched (full source names).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> LeftUnpatched { get; }
    /// <summary>NID -> (first firmware known to export it, source).</summary>
    public IReadOnlyDictionary<string, (string Firmware, string Source)> Introduced { get; }
    public IReadOnlyList<KnownBuild> KnownBuilds { get; }
    /// <summary>Sony build id (J&lt;number&gt;) -> firmware it shipped with (data/sony_builds.json, builds + evidence).</summary>
    public IReadOnlyDictionary<string, string> SonyBuilds { get; }

    public Knowledge(string? dataDir = null)
    {
        DataDir = dataDir ?? Paths.DataDir;

        using var fixes = Open("fixes");
        var root = fixes.RootElement;
        var sources = root.GetProperty("sources").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        SourceNames = sources;
        UmtxOp = root.GetProperty("umtx_op").GetString()!;
        string Source(string key) => sources.GetValueOrDefault(key, key);
        Fix ReadFix(JsonProperty p)
        {
            var f = p.Value;
            return new Fix(
                p.Name, Str(f, "kind"), Str(f, "name"), Str(f, "area"),
                f.GetProperty("sources").EnumerateArray().Select(s => Source(s.GetString()!)).ToList(),
                Str(f, "stub"), Str(f, "new_nid"),
                f.TryGetProperty("insns", out var insns) ? insns.EnumerateArray().Select(i => Insn.Parse(i.GetString()!)).ToList() : [],
                Str(f, "export_nid"), Str(f, "when", Fix.WhenMissing), Bool(f, "post_call", false), Bool(f, "proven", true),
                Str(f, "note"), Str(f, "rationale"), Str(f, "below"));
        }
        var list = root.GetProperty("fixes").EnumerateObject().Select(ReadFix).ToList();
        FixList = list;
        Fixes = list.ToDictionary(f => f.Nid, StringComparer.Ordinal);
        var options = new Dictionary<string, FixOption>(StringComparer.Ordinal);
        if (root.TryGetProperty("options", out var opts))
            foreach (var p in opts.EnumerateObject())
                options[p.Name] = new FixOption(
                    p.Name, Str(p.Value, "name"), Str(p.Value, "description"), Str(p.Value, "warning"),
                    p.Value.TryGetProperty("sources", out var os) ? os.EnumerateArray().Select(s => Source(s.GetString()!)).ToList() : [],
                    p.Value.GetProperty("fixes").EnumerateObject().Select(ReadFix).ToDictionary(f => f.Nid, StringComparer.Ordinal),
                    Str(p.Value, "fakelib_variant"),
                    p.Value.TryGetProperty("patterns", out var pats) ? pats.EnumerateArray().Select(ReadPattern).ToList() : []);
        Options = options;
        Patterns = root.TryGetProperty("patterns", out var defaults) ? defaults.EnumerateArray().Select(ReadPattern).ToList() : [];

        FixPattern ReadPattern(JsonElement x) => new(
            Str(x, "name"), Convert.FromHexString(Str(x, "find")), Convert.FromHexString(Str(x, "replace")),
            x.TryGetProperty("files", out var fs) ? fs.EnumerateArray().Select(f => f.GetString()!).ToList() : [],
            Bool(x, "code", false),
            x.TryGetProperty("sources", out var ss) ? ss.EnumerateArray().Select(s => Source(s.GetString()!)).ToList() : [],
            Str(x, "note"));

        using (var stubs = Open("stubs"))
            Stubs = stubs.RootElement.GetProperty("stubs").EnumerateObject().ToDictionary(
                p => p.Name, p => new StubBody(p.Name, Convert.FromHexString(Str(p.Value, "hex")), Str(p.Value, "asm"), Str(p.Value, "note")));

        using (var candidates = Open("candidates"))
            Candidates = candidates.RootElement.GetProperty("candidates").EnumerateObject().ToDictionary(
                p => p.Name, p => new Candidate(p.Name, Str(p.Value, "name"),
                                                uint.Parse(Str(p.Value, "value").AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                                                Str(p.Value, "source")), StringComparer.Ordinal);

        using (var left = Open("left_unpatched"))
            LeftUnpatched = left.RootElement.GetProperty("left_unpatched").EnumerateObject().ToDictionary(
                p => p.Name, p => (IReadOnlyList<string>)p.Value.EnumerateArray().Select(s => Source(s.GetString()!)).ToList(),
                StringComparer.Ordinal);

        using (var introduced = Open("introduced"))
            Introduced = introduced.RootElement.GetProperty("introduced").EnumerateObject().ToDictionary(
                p => p.Name, p => (Str(p.Value, "firmware"), Str(p.Value, "source")), StringComparer.Ordinal);

        using (var builds = Open("known_builds"))
            KnownBuilds = builds.RootElement.GetProperty("builds").EnumerateArray()
                .Select(b => new KnownBuild(Str(b, "module"), Str(b, "build_date"), Str(b, "state"), Str(b, "note"))).ToList();

        using (var sony = Open("sony_builds"))
        {
            var map = sony.RootElement.GetProperty("builds").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
            if (sony.RootElement.TryGetProperty("evidence", out var evidence))
                foreach (var p in evidence.EnumerateObject())
                    map[p.Name] = Str(p.Value, "firmware");
            SonyBuilds = map;
        }
    }

    /// <summary>"J03828141 = FW 12.70" for a Sony system library's build path (W:/Build/J&lt;number&gt;/...), else "".
    /// Unknown ids are placed between the known ones (ids grow with time).</summary>
    public string SonyBuild(string? buildPath)
    {
        var m = System.Text.RegularExpressions.Regex.Match(buildPath ?? "", @"W:/Build/(J\d+)/");
        if (!m.Success)
            return "";
        string id = m.Groups[1].Value;
        if (SonyBuilds.TryGetValue(id, out var fw))
            return $"{id} = FW {fw}";
        long n = long.Parse(id[1..], CultureInfo.InvariantCulture);
        var known = SonyBuilds.Keys.Select(k => long.Parse(k[1..], CultureInfo.InvariantCulture)).ToList();
        long? lower = known.Where(k => k < n).Select(k => (long?)k).Max(), higher = known.Where(k => k > n).Select(k => (long?)k).Min();
        string Fw(long k) => SonyBuilds[$"J{k:D8}"];
        return lower is null ? $"{id} = older than FW {Fw(higher!.Value)}"
             : higher is null ? $"{id} = newer than FW {Fw(lower.Value)}"
             : $"{id} = between FW {Fw(lower.Value)} and {Fw(higher.Value)}";
    }

    static Knowledge? _default;

    /// <summary>The knowledge in the default data/ folder, loaded once.</summary>
    public static Knowledge Default => _default ??= new Knowledge();

    /// <summary>The fix table for one backport: <see cref="Fixes"/> with the selected options' fixes on top.</summary>
    public IReadOnlyDictionary<string, Fix> FixTable(IEnumerable<string> options)
    {
        var table = new Dictionary<string, Fix>(Fixes, StringComparer.Ordinal);
        foreach (string key in options)
        {
            if (!Options.TryGetValue(key, out var option))
                throw new ArgumentException($"unknown option '{key}' (known: {string.Join(", ", Options.Keys)})");
            foreach (var (nid, fix) in option.Fixes)
            {
                if (fix.Kind == Fix.KindNone)   // the option lifts the default fix
                    table.Remove(nid);
                else
                    table[nid] = fix;
            }
        }
        return table;
    }

    /// <summary>Stub body for a fix's stub name or "ret:&lt;value&gt;" (mov eax, value; ret — ret0 for 0).</summary>
    public byte[] StubBytes(string stub)
    {
        if (stub.StartsWith("ret:", StringComparison.Ordinal))
        {
            string text = stub[4..].Trim();                 // Python int(x, 0) & 0xFFFFFFFF
            bool negative = text.StartsWith('-');
            string digits = negative ? text[1..] : text;
            long value = digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? long.Parse(digits.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : long.Parse(digits, CultureInfo.InvariantCulture);
            uint v = (uint)(negative ? -value : value);
            return v == 0 ? Stubs["ret0"].Bytes : [0xB8, (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24), 0xC3];
        }
        return Stubs.TryGetValue(stub, out var body) ? body.Bytes : throw new KeyNotFoundException($"unknown stub '{stub}'");
    }

    /// <summary>(state, note) for a homebrew module build, or null when not known.</summary>
    public KnownBuild? Build(string? module, string? buildDate) =>
        KnownBuilds.FirstOrDefault(b => b.Module == module && b.BuildDate == buildDate);

    JsonDocument Open(string name) => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(DataDir, name + ".json")));

    static string Str(JsonElement e, string name, string fallback = "") =>
        e.TryGetProperty(name, out var v) ? v.GetString() ?? fallback : fallback;

    static bool Bool(JsonElement e, string name, bool fallback) =>
        e.TryGetProperty(name, out var v) ? v.GetBoolean() : fallback;
}
