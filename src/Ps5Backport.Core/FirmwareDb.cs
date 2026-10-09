using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ps5Backport.Core;

/// <summary>
/// One firmware's exported NIDs per system module (firmware_db/&lt;firmware&gt;.json). NID lists only,
/// no Sony code.
/// </summary>
public sealed class FirmwareExports(string firmware, string source, bool dataSymbols,
                                    Dictionary<string, HashSet<string>> modules, string path = "")
{
    public string Firmware { get; set; } = Core.Firmware.Name(firmware);
    public string Source { get; } = source;
    /// <summary>True when data symbols are included (dumps); genstub stubs list functions only.</summary>
    public bool DataSymbols { get; } = dataSymbols;
    public Dictionary<string, HashSet<string>> Modules { get; } = modules;
    public string Path { get; } = path;

    HashSet<string>? _nids;

    /// <summary>Every NID of every module.</summary>
    public HashSet<string> Nids => _nids ??= Modules.Values.SelectMany(n => n).ToHashSet(StringComparer.Ordinal);
}

/// <summary>
/// Per-firmware export database. Selection for a target firmware:
/// exact = a database for exactly that firmware (an import it lacks is *missing*);
/// lower bound = the highest older firmware (functions are rarely removed, so what it exports is
/// present on the target, but an import it lacks is only *unknown*).
/// </summary>
public static class FirmwareDb
{
    static readonly string[] ModuleExtensions = [".sprx", ".prx", ".elf"];
    /// <summary>Mono/.NET assemblies in some dumps' common/lib: games don't import them.</summary>
    static readonly string[] Managed = [".dll.sprx", ".exe.sprx"];

    static readonly Regex Dlsym = new(@"sprx_dlsym\([^,]+,\s*""([A-Za-z0-9+\-]{11})""", RegexOptions.Compiled);

    public static (int, int) Key(string firmware) => Core.Firmware.Parse(firmware);

    public static void Save(FirmwareExports db, string path)
    {
        var modules = new JsonObject();
        foreach (var (module, nids) in db.Modules.OrderBy(m => m.Key, StringComparer.Ordinal))
            modules[module] = new JsonArray(nids.Order(StringComparer.Ordinal).Select(n => (JsonNode)n).ToArray());
        var doc = new JsonObject
        {
            ["firmware"] = db.Firmware, ["source"] = db.Source, ["data_symbols"] = db.DataSymbols, ["modules"] = modules,
        };
        PyJson.WriteFile(path, doc, 1);
    }

    public static FirmwareExports Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var r = doc.RootElement;
        var modules = r.GetProperty("modules").EnumerateObject().ToDictionary(
            p => p.Name, p => p.Value.EnumerateArray().Select(n => n.GetString()!).ToHashSet(StringComparer.Ordinal));
        return new FirmwareExports(r.GetProperty("firmware").GetString()!,
                                   r.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "",
                                   r.TryGetProperty("data_symbols", out var d) && d.GetBoolean(), modules, path);
    }

    /// <summary>Every database in a folder, oldest firmware first.</summary>
    public static List<FirmwareExports> LoadAll(string? dbDir)
    {
        if (dbDir is null || !Directory.Exists(dbDir))
            return [];
        return Directory.EnumerateFiles(dbDir, "*.json").Select(Load).OrderBy(d => Key(d.Firmware)).ToList();
    }

    /// <summary>(database, exact) for a target firmware: the exact one, else the highest older one.</summary>
    public static (FirmwareExports? Db, bool Exact) Select(string dbDir, string target)
    {
        var key = Key(target);
        FirmwareExports? best = null;
        foreach (var db in LoadAll(dbDir))
        {
            var k = Key(db.Firmware);
            if (k == key)
                return (db, true);
            if (k.CompareTo(key) < 0)
                best = db;
        }
        return (best, false);
    }

    /// <summary>From genstub-generated &lt;module&gt;.c stubs (PS5-3.20_Libs, ps5-payload-sdk sce_stubs): functions only.</summary>
    public static FirmwareExports ImportGenstub(string stubDir, string firmware, string source)
    {
        var modules = new Dictionary<string, HashSet<string>>();
        foreach (string file in Directory.EnumerateFiles(stubDir).Order(StringComparer.Ordinal))
        {
            if (!file.EndsWith(".c", StringComparison.Ordinal))
                continue;
            var nids = Dlsym.Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            if (nids.Count > 0)
                modules[System.IO.Path.GetFileNameWithoutExtension(file)] = nids;
        }
        return new FirmwareExports(firmware, source, false, modules);
    }

    /// <summary>All NIDs from a folder of genstub .c stubs (always a lower bound).</summary>
    public static HashSet<string> LoadGenstubExports(string stubDir) => ImportGenstub(stubDir, "0", "").Nids;

    /// <summary>
    /// From decrypted system modules (fake-signed SELF or ELF), e.g. a /system/common/lib dump.
    /// Takes every module's own exports, data symbols included.
    /// </summary>
    public static FirmwareExports ImportDump(string dumpDir, string firmware, string source)
    {
        var modules = new Dictionary<string, HashSet<string>>();
        foreach (string file in Directory.EnumerateFiles(dumpDir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (!ModuleExtensions.Any(e => file.EndsWith(e, StringComparison.OrdinalIgnoreCase)) || Managed.Any(e => file.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                continue;
            string kind = Self.FileKind(file);
            byte[] data;
            if (Self.IsSelf(kind))
            {
                if (Self.EncryptedSegments(file) > 0)
                    throw new InvalidDataException($"{file}: encrypted; dump decrypted modules (PS5-SELF-Decrypter)");
                data = Self.Unwrap(File.ReadAllBytes(file));
            }
            else if (kind == Self.KindElf)
                data = File.ReadAllBytes(file);
            else
                continue;
            var elf = new SceElf(data);
            string module = elf.ModuleName ?? System.IO.Path.GetFileNameWithoutExtension(file);
            if (!modules.TryGetValue(module, out var set))
                modules[module] = set = new HashSet<string>(StringComparer.Ordinal);
            set.UnionWith(elf.Exports());
        }
        return new FirmwareExports(firmware, source, true, modules);
    }

    /// <summary>{module: NIDs} exported by <paramref name="newer"/> but not by <paramref name="older"/> (per module).</summary>
    public static Dictionary<string, HashSet<string>> Diff(FirmwareExports older, FirmwareExports newer)
    {
        var added = new Dictionary<string, HashSet<string>>();
        foreach (var (module, nids) in newer.Modules)
        {
            var extra = nids.Except(older.Modules.GetValueOrDefault(module) ?? [], StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            if (extra.Count > 0)
                added[module] = extra;
        }
        return added;
    }

    /// <summary>First firmware in <paramref name="dbs"/> (sorted) that exports the NID, else the known facts, else null.</summary>
    public static (string Firmware, string Source)? Introduced(string nid, IEnumerable<FirmwareExports> dbs, Knowledge knowledge)
    {
        foreach (var db in dbs)
            if (db.Nids.Contains(nid))
                return (db.Firmware, $"firmware_db {db.Firmware}");
        return knowledge.Introduced.TryGetValue(nid, out var fact) ? fact : null;
    }
}
