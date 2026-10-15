using System.Globalization;
using System.Text.Json;

namespace Ps5Backport.Core;

/// <summary>One adapter instruction: hex bytes, or "call the real function through its GOT slot".
/// <see cref="StateAt"/> &gt;= 0: the 4 bytes there are a rip-relative displacement to the fix's runtime state
/// block + <see cref="StateOffset"/>, written in fixes.json as "8905{state+0x4}".</summary>
public sealed record Insn(byte[] Bytes, bool CallGot, string Asm, int StateAt = -1, int StateOffset = 0)
{
    public const string CallGotToken = "call_got";
    public int Length => CallGot ? 6 : Bytes.Length;
    public string Hex => CallGot ? CallGotToken
        : StateAt < 0 ? Convert.ToHexStringLower(Bytes)
        : Convert.ToHexStringLower(Bytes.AsSpan(0, StateAt)) + $"{{state+0x{StateOffset:x}}}" + Convert.ToHexStringLower(Bytes.AsSpan(StateAt + 4));

    /// <summary>"4989d0  ; mov r8, rdx" -> bytes + asm note.</summary>
    public static Insn Parse(string entry)
    {
        int semi = entry.IndexOf(';');
        string hex = (semi < 0 ? entry : entry[..semi]).Trim();
        string asm = semi < 0 ? "" : entry[(semi + 1)..].Trim();
        if (hex == CallGotToken)
            return new Insn([], true, asm);
        var m = System.Text.RegularExpressions.Regex.Match(hex, @"^([0-9a-fA-F]*)\{state\+0x([0-9a-fA-F]+)\}([0-9a-fA-F]*)$");
        if (!m.Success)
            return new Insn(Convert.FromHexString(hex), false, asm);
        byte[] before = Convert.FromHexString(m.Groups[1].Value), after = Convert.FromHexString(m.Groups[3].Value);
        return new Insn([.. before, 0, 0, 0, 0, .. after], false, asm, before.Length,
                        int.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }
}

/// <summary>A known fix for one imported library function (data/fixes.json).
/// <see cref="Contiguous"/>: the adapter is placed in one piece (its instructions may branch inside it).
/// <see cref="State"/>: name of a zero-initialised runtime block of <see cref="StateSize"/> bytes, shared by
/// every fix of a module that names it.</summary>
public sealed record Fix(
    string Nid, string Kind, string Name, string Area, IReadOnlyList<string> Sources,
    string Stub, string NewNid, IReadOnlyList<Insn> Insns, string ExportNid,
    string When, bool PostCall, bool Proven, string Note, string Rationale, string Below = "", string NewLibrary = "",
    bool Contiguous = false, string State = "", int StateSize = 0)
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
/// <see cref="FakelibVariant"/>: files in the fakelib set's &lt;variant&gt;/ subfolder replace the same-named fakelibs.
/// <see cref="Group"/>: the area the option is listed under in the app (see <see cref="Knowledge.OptionGroups"/>).</summary>
public sealed record FixOption(string Key, string Name, string Description, string Warning,
                               IReadOnlyList<string> Sources, IReadOnlyDictionary<string, Fix> Fixes,
                               string FakelibVariant, IReadOnlyList<FixPattern> Patterns, string Group = FixOption.DefaultGroup,
                               OptionProbe? Probe = null)
{
    public const string DefaultGroup = "Other";
    /// <summary>How the requirements probe judges this option (data/fixes.json option "probe"; default: by imports and patterns).</summary>
    public OptionProbe Probe { get; init; } = Probe ?? OptionProbe.Default;
}

/// <summary>An option's probe rule. <see cref="Kind"/>: "" (imports/patterns), <see cref="KindConsole"/> (the tester's or console's
/// choice), <see cref="KindDetector"/> (engine recogniser; <see cref="Recognized"/> = "needed" or "applicable" when it matches) or
/// <see cref="KindArgument"/> (the constant the game passes in argument <see cref="Argument"/> of <see cref="Nid"/>; above
/// <see cref="Above"/> means needed).</summary>
public sealed record OptionProbe(string Kind = "", string Nid = "", int Argument = 0, long Above = 0, string Recognized = "")
{
    public const string KindConsole = "console", KindDetector = "detector", KindArgument = "argument", Needed = "needed", Applicable = "applicable";
    public static readonly OptionProbe Default = new();
}

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
    readonly Dictionary<string, string> _documentHashes = new(StringComparer.Ordinal);
    /// <summary>Hashes of the exact documents parsed by this instance, used to bind run evidence to loaded rules.</summary>
    public IReadOnlyDictionary<string, string> DocumentHashes => _documentHashes;
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
    /// <summary>Option groups in display order: data/fixes.json "option_groups", then any other group in option order.</summary>
    public IReadOnlyList<string> OptionGroups { get; }
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

    /// <summary>Deep private copy: callers cannot change a running operation through collection/byte-array aliases.</summary>
    internal Knowledge Snapshot() => new(this);

    Knowledge(Knowledge original)
    {
        static Fix CopyFix(Fix f) => f with { Sources = f.Sources.ToArray(), Insns = f.Insns.Select(i => i with { Bytes = i.Bytes.ToArray() }).ToArray() };
        static FixPattern CopyPattern(FixPattern p) => p with { Find = p.Find.ToArray(), Replace = p.Replace.ToArray(), Files = p.Files.ToArray(), Sources = p.Sources?.ToArray() };
        DataDir = Path.GetFullPath(original.DataDir);
        foreach (var pair in original.DocumentHashes) _documentHashes.Add(pair.Key, pair.Value);
        SourceNames = new Dictionary<string, string>(original.SourceNames);
        UmtxOp = original.UmtxOp;
        FixList = original.FixList.Select(CopyFix).ToArray();
        Fixes = original.Fixes.ToDictionary(p => p.Key, p => CopyFix(p.Value), StringComparer.Ordinal);
        OptionGroups = original.OptionGroups.ToArray();
        Options = original.Options.ToDictionary(p => p.Key, p => p.Value with
        {
            Sources = p.Value.Sources.ToArray(),
            Fixes = p.Value.Fixes.ToDictionary(f => f.Key, f => CopyFix(f.Value), StringComparer.Ordinal),
            Patterns = p.Value.Patterns.Select(CopyPattern).ToArray(),
        }, StringComparer.Ordinal);
        Patterns = original.Patterns.Select(CopyPattern).ToArray();
        Stubs = original.Stubs.ToDictionary(p => p.Key, p => p.Value with { Bytes = p.Value.Bytes.ToArray() }, StringComparer.Ordinal);
        Candidates = new Dictionary<string, Candidate>(original.Candidates);
        LeftUnpatched = original.LeftUnpatched.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value.ToArray(), StringComparer.Ordinal);
        Introduced = new Dictionary<string, (string Firmware, string Source)>(original.Introduced);
        KnownBuilds = original.KnownBuilds.ToArray();
        SonyBuilds = new Dictionary<string, string>(original.SonyBuilds);
    }

    public Knowledge(string? dataDir = null)
    {
        DataDir = dataDir ?? Paths.DataDir;
        string file = "fixes";
        try
        {
            using var fixes = Open(file);
            var root = fixes.RootElement;
            var sources = root.GetProperty("sources").EnumerateObject().ToDictionary(p => p.Name, p => StringValue(p.Value));
            SourceNames = sources;
            UmtxOp = StringValue(root.GetProperty("umtx_op"));
            string Source(string key) => sources.GetValueOrDefault(key, key);
            Fix ReadFix(JsonProperty p)
            {
                var f = p.Value;
                return new Fix(
                    p.Name, Str(f, "kind"), Str(f, "name"), Str(f, "area"),
                    f.GetProperty("sources").EnumerateArray().Select(s => Source(StringValue(s))).ToList(),
                    Str(f, "stub"), Str(f, "new_nid"),
                    f.TryGetProperty("insns", out var insns) ? insns.EnumerateArray().Select(i => Insn.Parse(StringValue(i))).ToList() : [],
                    Str(f, "export_nid"), Str(f, "when", Fix.WhenMissing), Bool(f, "post_call", false), Bool(f, "proven", true),
                    Str(f, "note"), Str(f, "rationale"), Str(f, "below"), Str(f, "new_library"),
                    Bool(f, "contiguous", false), Str(f, "state"), f.TryGetProperty("state_size", out var size) ? size.GetInt32() : 0);
            }
            var list = root.GetProperty("fixes").EnumerateObject().Select(ReadFix).ToList();
            FixList = list;
            Fixes = list.ToDictionary(f => f.Nid, StringComparer.Ordinal);
            var options = new Dictionary<string, FixOption>(StringComparer.Ordinal);
            if (root.TryGetProperty("options", out var opts))
                foreach (var p in opts.EnumerateObject())
                    options[p.Name] = new FixOption(
                        p.Name, Str(p.Value, "name"), Str(p.Value, "description"), Str(p.Value, "warning"),
                        p.Value.TryGetProperty("sources", out var os) ? os.EnumerateArray().Select(s => Source(StringValue(s))).ToList() : [],
                        p.Value.GetProperty("fixes").EnumerateObject().Select(ReadFix).ToDictionary(f => f.Nid, StringComparer.Ordinal),
                        Str(p.Value, "fakelib_variant"),
                        p.Value.TryGetProperty("patterns", out var pats) ? pats.EnumerateArray().Select(ReadPattern).ToList() : [],
                        Str(p.Value, "group", FixOption.DefaultGroup),
                        p.Value.TryGetProperty("probe", out var probe) ? ReadProbe(probe) : null);
            Options = options;

            static OptionProbe ReadProbe(JsonElement x) => new(
                Str(x, "kind"), Str(x, "nid"), x.TryGetProperty("argument", out var a) ? a.GetInt32() : 0,
                x.TryGetProperty("above", out var b) ? b.GetInt64() : 0, Str(x, "recognized"));
            var declared = root.TryGetProperty("option_groups", out var og) ? og.EnumerateArray().Select(StringValue).ToList() : [];
            OptionGroups = declared.Concat(options.Values.Select(o => o.Group)).Distinct(StringComparer.Ordinal).ToList();
            Patterns = root.TryGetProperty("patterns", out var defaults) ? defaults.EnumerateArray().Select(ReadPattern).ToList() : [];

            FixPattern ReadPattern(JsonElement x) => new(
                Str(x, "name"), Convert.FromHexString(Str(x, "find")), Convert.FromHexString(Str(x, "replace")),
                x.TryGetProperty("files", out var fs) ? fs.EnumerateArray().Select(StringValue).ToList() : [],
                Bool(x, "code", false),
                x.TryGetProperty("sources", out var ss) ? ss.EnumerateArray().Select(s => Source(StringValue(s))).ToList() : [],
                Str(x, "note"));

            file = "stubs";
            using (var stubs = Open(file))
                Stubs = stubs.RootElement.GetProperty("stubs").EnumerateObject().ToDictionary(
                    p => p.Name, p => new StubBody(p.Name, Convert.FromHexString(Str(p.Value, "hex")), Str(p.Value, "asm"), Str(p.Value, "note")));

            file = "candidates";
            using (var candidates = Open(file))
                Candidates = candidates.RootElement.GetProperty("candidates").EnumerateObject().ToDictionary(
                    p => p.Name, p => new Candidate(p.Name, Str(p.Value, "name"), CandidateValue(Str(p.Value, "value")),
                                                    Str(p.Value, "source")), StringComparer.Ordinal);

            file = "left_unpatched";
            using (var left = Open(file))
                LeftUnpatched = left.RootElement.GetProperty("left_unpatched").EnumerateObject().ToDictionary(
                    p => p.Name, p => (IReadOnlyList<string>)p.Value.EnumerateArray().Select(s => Source(StringValue(s))).ToList(),
                    StringComparer.Ordinal);

            file = "introduced";
            using (var introduced = Open(file))
                Introduced = introduced.RootElement.GetProperty("introduced").EnumerateObject().ToDictionary(
                    p => p.Name, p => (Str(p.Value, "firmware"), Str(p.Value, "source")), StringComparer.Ordinal);

            file = "known_builds";
            using (var builds = Open(file))
                KnownBuilds = builds.RootElement.GetProperty("builds").EnumerateArray()
                    .Select(b => new KnownBuild(Str(b, "module"), Str(b, "build_date"), Str(b, "state"), Str(b, "note"))).ToList();

            file = "sony_builds";
            using (var sony = Open(file))
            {
                var map = sony.RootElement.GetProperty("builds").EnumerateObject().ToDictionary(p => p.Name, p => StringValue(p.Value), StringComparer.Ordinal);
                if (sony.RootElement.TryGetProperty("evidence", out var evidence))
                    foreach (var p in evidence.EnumerateObject())
                        map[p.Name] = Str(p.Value, "firmware");
                SonyBuilds = map;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidDataException($"Invalid knowledge data in '{Path.Combine(DataDir, file + ".json")}': {e.Message}", e);
        }
        Validate();
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
        if (SonyBuilds.Count == 0 || !long.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out long n))
            return "";
        var known = SonyBuilds.Keys.Select(k => long.Parse(k[1..], CultureInfo.InvariantCulture)).ToList();
        long? lower = known.Where(k => k < n).Select(k => (long?)k).Max(), higher = known.Where(k => k > n).Select(k => (long?)k).Min();
        string Fw(long k) => SonyBuilds.First(p => long.Parse(p.Key.AsSpan(1), CultureInfo.InvariantCulture) == k).Value;
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
        var selected = options.ToList();
        if (selected.Contains("auto_pro", StringComparer.Ordinal) && selected.Contains("force_pro", StringComparer.Ordinal))
            throw new ArgumentException("Automatic Pro mode cannot be combined with Force PS5 Pro mode. Select one console mode.");
        if (selected.Contains("audio_port_compat", StringComparer.Ordinal) && selected.Contains("audio_bed_71", StringComparer.Ordinal))
            throw new ArgumentException("Audio 7.1 bed cannot be combined with Audio port compatibility. Select one audio bed fix.");
        var table = new Dictionary<string, Fix>(Fixes, StringComparer.Ordinal);
        foreach (string key in selected)
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

    void Validate()
    {
        string context = "fixes.json";
        try
        {
            if (string.IsNullOrWhiteSpace(UmtxOp))
                throw new ArgumentException("umtx_op must not be empty");
            foreach (var fix in FixList)
                ValidateFix(fix, allowNone: false);
            foreach (var pattern in Patterns)
                ValidatePattern(pattern);
            foreach (var option in Options.Values)
            {
                context = $"fixes.json option '{option.Key}'";
                if (string.IsNullOrWhiteSpace(option.Key))
                    throw new ArgumentException("option key must not be empty");
                if (string.IsNullOrWhiteSpace(option.Group))
                    throw new ArgumentException("option group must not be empty");
                if (option.FakelibVariant.Length > 0)
                    _ = FakelibStore.VariantFolder(DataDir, option.FakelibVariant);
                ValidateProbe(option);
                foreach (var fix in option.Fixes.Values)
                    ValidateFix(fix, allowNone: true);
                foreach (var pattern in option.Patterns)
                    ValidatePattern(pattern);
            }
            context = "fixes.json";
            foreach (var group in FixList.Concat(Options.Values.SelectMany(o => o.Fixes.Values))
                                         .Where(f => f.State.Length > 0).GroupBy(f => f.State, StringComparer.Ordinal))
                if (group.Select(f => f.StateSize).Distinct().Count() > 1)
                    throw new ArgumentException($"state '{group.Key}': every fix must declare the same state_size");
            context = "stubs.json";
            foreach (var stub in Stubs.Values)
                if (string.IsNullOrWhiteSpace(stub.Name) || stub.Bytes.Length == 0)
                    throw new ArgumentException($"stub '{stub.Name}' must have a name and nonempty bytes");
            context = "introduced.json";
            foreach (var (nid, entry) in Introduced)
            {
                context = $"introduced.json NID '{nid}'";
                _ = Firmware.Parse(entry.Firmware);
            }
            context = "sony_builds.json";
            foreach (string key in SonyBuilds.Keys)
                if (!key.StartsWith('J') || !long.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    throw new ArgumentException($"invalid build id '{key}'");
        }
        catch (Exception e) when (e is ArgumentException or FormatException or OverflowException or KeyNotFoundException)
        {
            throw new InvalidDataException($"Invalid knowledge data in '{Path.Combine(DataDir, context)}': {e.Message}", e);
        }
    }

    void ValidateFix(Fix fix, bool allowNone)
    {
        if (string.IsNullOrWhiteSpace(fix.Nid))
            throw new ArgumentException("fix NID must not be empty");
        if (fix.When is not (Fix.WhenMissing or Fix.WhenAlways))
            throw new ArgumentException($"fix '{fix.Nid}': unknown condition '{fix.When}'");
        if (fix.Below.Length > 0)
        {
            try { _ = Firmware.Parse(fix.Below); }
            catch (FormatException e) { throw new ArgumentException($"fix '{fix.Nid}': invalid below firmware '{fix.Below}'", e); }
        }
        switch (fix.Kind)
        {
            case Fix.KindStub or Fix.KindPltStub:
                try { _ = StubBytes(fix.Stub); }
                catch (Exception e) when (e is KeyNotFoundException or FormatException or OverflowException)
                { throw new ArgumentException($"fix '{fix.Nid}': invalid stub '{fix.Stub}'", e); }
                break;
            case Fix.KindSwap:
                if (string.IsNullOrWhiteSpace(fix.NewNid))
                    throw new ArgumentException($"fix '{fix.Nid}': swap needs new_nid");
                break;
            case Fix.KindRedirect:
                if (string.IsNullOrWhiteSpace(fix.ExportNid))
                    throw new ArgumentException($"fix '{fix.Nid}': redirect needs export_nid");
                break;
            case Fix.KindAdapter:
                if (fix.Insns.Count == 0 || fix.Insns.Any(i => i.Length == 0))
                    throw new ArgumentException($"fix '{fix.Nid}': adapter needs nonempty instructions");
                if ((fix.State.Length > 0) != (fix.StateSize > 0) || fix.StateSize < 0)
                    throw new ArgumentException($"fix '{fix.Nid}': state and state_size go together (state_size > 0)");
                if (fix.Insns.Any(i => i.StateAt >= 0 && (fix.State.Length == 0 || i.StateOffset >= fix.StateSize)))
                    throw new ArgumentException($"fix '{fix.Nid}': {{state+N}} needs a state block larger than N");
                break;
            case Fix.KindNone when allowNone:
                break;
            default:
                throw new ArgumentException($"fix '{fix.Nid}': unknown or disallowed kind '{fix.Kind}'");
        }
    }

    static void ValidateProbe(FixOption option)
    {
        var probe = option.Probe;
        switch (probe.Kind)
        {
            case "" or OptionProbe.KindConsole:
                break;
            case OptionProbe.KindDetector:
                if (probe.Recognized is not (OptionProbe.Needed or OptionProbe.Applicable))
                    throw new ArgumentException("probe 'detector' needs recognized = needed or applicable");
                break;
            case OptionProbe.KindArgument:
                if (!option.Fixes.TryGetValue(probe.Nid, out var fix) || fix.Kind == Fix.KindNone)
                    throw new ArgumentException($"probe 'argument' needs nid to be one of the option's fixes (got '{probe.Nid}')");
                if (probe.Argument is < 0 or > 5)
                    throw new ArgumentException("probe 'argument' needs argument 0..5 (rdi, rsi, rdx, rcx, r8, r9)");
                break;
            default:
                throw new ArgumentException($"unknown probe kind '{probe.Kind}'");
        }
    }

    void ValidatePattern(FixPattern pattern)
    {
        if (pattern.Find.Length == 0 || pattern.Find.Length != pattern.Replace.Length)
            throw new ArgumentException($"pattern '{pattern.Name}': find/replace must have equal, nonzero lengths");
        foreach (string relative in pattern.Files)
        {
            try { _ = Paths.ResolveRelativeFile(DataDir, relative); }
            catch (ArgumentException e) { throw new ArgumentException($"pattern '{pattern.Name}': {e.Message}", e); }
        }
    }

    /// <summary>Keys each record may carry. A misspelt key would otherwise be ignored and silently change a rule
    /// (<c>belo</c> makes it unconditional, <c>proovn</c> marks it proven, <c>contiguos</c> splits a branching adapter), so
    /// unknown keys are errors. Keys starting with '_' are comments and allowed everywhere.</summary>
    static readonly IReadOnlyDictionary<string, string[]> RootKeys = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["fixes"] = ["sources", "umtx_op", "fixes", "option_groups", "options", "patterns"],
        ["stubs"] = ["stubs"],
        ["candidates"] = ["candidates"],
        ["left_unpatched"] = ["left_unpatched"],
        ["introduced"] = ["introduced"],
        ["known_builds"] = ["builds"],
        ["sony_builds"] = ["builds", "evidence"],
    };
    static readonly string[] FixKeys = ["kind", "name", "area", "sources", "stub", "new_nid", "new_library", "insns", "export_nid", "when",
                                        "post_call", "proven", "note", "rationale", "below", "contiguous", "state", "state_size"];
    static readonly string[] OptionKeys = ["name", "group", "description", "warning", "sources", "fixes", "fakelib_variant", "patterns", "probe"];
    static readonly string[] ProbeKeys = ["kind", "nid", "argument", "above", "recognized"];
    static readonly string[] PatternKeys = ["name", "area", "sources", "find", "replace", "files", "code", "note", "rationale"];
    static readonly string[] StubKeys = ["hex", "asm", "note"];
    static readonly string[] CandidateKeys = ["name", "value", "source"];
    static readonly string[] IntroducedKeys = ["firmware", "source"];
    static readonly string[] BuildKeys = ["module", "build_date", "state", "note"];
    static readonly string[] EvidenceKeys = ["firmware", "source"];

    JsonDocument Open(string name)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(DataDir, name + ".json"));
        _documentHashes[name + ".json"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var doc = JsonDocument.Parse(bytes);
        try
        {
            ValidateKeys(doc.RootElement, "$");
            ValidateShape(doc.RootElement, name);
            return doc;
        }
        catch { doc.Dispose(); throw; }

        void ValidateShape(JsonElement root, string file)
        {
            if (root.ValueKind != JsonValueKind.Object || !RootKeys.TryGetValue(file, out var rootKeys))
                return;                                             // type errors are reported by the typed readers
            Known(root, "$", rootKeys);
            switch (file)
            {
                case "fixes":
                    Records(root, "fixes", FixKeys);
                    if (root.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object)
                        foreach (var option in options.EnumerateObject())
                        {
                            if (option.Value.ValueKind != JsonValueKind.Object) continue;
                            string at = $"$.options.{option.Name}";
                            Known(option.Value, at, OptionKeys);
                            Records(option.Value, "fixes", FixKeys, at);
                            Items(option.Value, "patterns", PatternKeys, at);
                            if (option.Value.TryGetProperty("probe", out var probe) && probe.ValueKind == JsonValueKind.Object)
                                Known(probe, at + ".probe", ProbeKeys);
                        }
                    Items(root, "patterns", PatternKeys);
                    break;
                case "stubs": Records(root, "stubs", StubKeys); break;
                case "candidates": Records(root, "candidates", CandidateKeys); break;
                case "introduced": Records(root, "introduced", IntroducedKeys); break;
                case "known_builds": Items(root, "builds", BuildKeys); break;
                case "sony_builds": Records(root, "evidence", EvidenceKeys); break;
            }
        }

        // every object value of parent.<member>: a record with fixed keys (the member's own keys are free: NIDs, option keys, ...)
        void Records(JsonElement parent, string member, string[] allowed, string at = "$")
        {
            if (parent.TryGetProperty(member, out var map) && map.ValueKind == JsonValueKind.Object)
                foreach (var record in map.EnumerateObject())
                    if (record.Value.ValueKind == JsonValueKind.Object)
                        Known(record.Value, $"{at}.{member}.{record.Name}", allowed);
        }

        void Items(JsonElement parent, string member, string[] allowed, string at = "$")
        {
            if (parent.TryGetProperty(member, out var list) && list.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                        Known(item, $"{at}.{member}[{index}]", allowed);
                    index++;
                }
            }
        }

        void Known(JsonElement record, string at, string[] allowed)
        {
            foreach (var property in record.EnumerateObject())
                if (!property.Name.StartsWith('_') && !allowed.Contains(property.Name, StringComparer.Ordinal))
                    throw new InvalidDataException($"{name}.json: unknown key '{property.Name}' at {at} (allowed: {string.Join(", ", allowed)}; keys starting with '_' are comments)");
        }

        void ValidateKeys(JsonElement node, string path)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in node.EnumerateObject())
                {
                    if (!keys.Add(property.Name))
                        throw new InvalidDataException($"{name}.json: duplicate key '{property.Name}' at {path}");
                    ValidateKeys(property.Value, path + "." + property.Name);
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (var item in node.EnumerateArray())
                    ValidateKeys(item, $"{path}[{index++}]");
            }
        }
    }

    static uint CandidateValue(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? uint.Parse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
        : throw new FormatException("candidate value must be a hexadecimal string starting with 0x");

    static string StringValue(JsonElement e) => e.GetString() ?? throw new InvalidOperationException("expected a string, not null");

    static string Str(JsonElement e, string name, string fallback = "") =>
        e.TryGetProperty(name, out var v) ? StringValue(v) : fallback;

    static bool Bool(JsonElement e, string name, bool fallback) =>
        e.TryGetProperty(name, out var v) ? v.GetBoolean() : fallback;
}
