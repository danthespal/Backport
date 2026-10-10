using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ps5Backport.Core;

/// <summary>
/// Per-firmware fakelib store: fakelibs/&lt;firmware&gt;/*.sprx|prx + manifest.json.
/// Lookup for a target firmware tries the exact version folder first ('4.03'), then the major ('4').
/// The manifest records, per file, its container, module, SDK pair, build path, build date
/// (homebrew), SHA-256, size and where it came from. Entries are JsonObjects so unknown fields survive.
/// </summary>
public static class FakelibStore
{
    public const string ManifestName = "manifest.json";
    static readonly string[] ModuleExtensions = [".sprx", ".prx"];

    /// <summary>Compiler __DATE__ ("Aug 24 2026", day space-padded), the only build id homebrew ampr_emu carries.</summary>
    static readonly Regex BuildDate = new(@"(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) [ 123]\d 20\d\d", RegexOptions.Compiled);

    static bool IsModule(string name) => ModuleExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Folder name for a firmware: '4' -> "4", '4.03' -> "4.03".</summary>
    public static string FolderName(string firmware)
    {
        var (major, minor) = Firmware.Parse(firmware);
        return minor != 0 ? $"{major}.{minor:D2}" : $"{major}";
    }

    /// <summary>Candidate folders for a target firmware, most specific first.</summary>
    public static List<string> FirmwareDirs(string store, string firmware)
    {
        var (major, minor) = Firmware.Parse(firmware);
        string[] names = minor != 0 ? [$"{major}.{minor:D2}", $"{major}"] : [$"{major}"];
        return names.Select(n => Path.Combine(store, n)).ToList();
    }

    /// <summary>The store folder used for a firmware, or null.</summary>
    public static string? Find(string store, string firmware) =>
        FirmwareDirs(store, firmware).FirstOrDefault(d =>
            Directory.Exists(d) && Directory.EnumerateFiles(d).Any(f => IsModule(Path.GetFileName(f))));

    public static string Sha256(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }

    /// <summary>Container, module, SDK pair, build path and (homebrew) build date of a module (SELF or ELF).</summary>
    public static JsonObject Describe(string path)
    {
        string kind = Self.FileKind(path);
        byte[] data = Self.IsSelf(kind) ? Self.Unwrap(File.ReadAllBytes(path))
                    : kind == Self.KindElf ? File.ReadAllBytes(path)
                    : throw new InvalidDataException($"{path}: not a SELF or ELF module");
        var elf = new SceElf(data);
        var (ps5, ps4) = Sdk.Read(elf);
        var info = new JsonObject
        {
            ["container"] = kind, ["module"] = elf.ModuleName, ["ps5_sdk"] = $"0x{ps5:x8}", ["ps4_sdk"] = $"0x{ps4:x8}",
            ["build_path"] = elf.BuildPath,
        };
        if (BuildDateOf(elf) is { } date)
            info["build_date"] = date;
        return info;
    }

    /// <summary>The compiler __DATE__ in a homebrew module ("Aug 24 2026"), the only build id ampr_emu carries.</summary>
    public static string? BuildDateOf(SceElf elf) => BuildDate.Match(Encoding.Latin1.GetString(elf.Data)) is { Success: true } m ? m.Value : null;

    /// <summary>file name -> entry, in file order (empty when there is no manifest).</summary>
    public static JsonObject LoadManifest(string folder)
    {
        string path = Path.Combine(folder, ManifestName);
        if (!File.Exists(path))
            return [];
        if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject manifest || manifest.Any(e => e.Value is not JsonObject))
            throw new InvalidDataException($"{path}: manifest must be an object of file entries");
        foreach (string name in manifest.Select(e => e.Key))
        {
            _ = Paths.ResolveRelativeFile(folder, name);
            if (name != Path.GetFileName(name) || !IsModule(name))
                throw new InvalidDataException($"{path}: expected a module filename, got '{name}'");
        }
        return manifest;
    }

    static void SaveManifest(string folder, JsonObject manifest)
    {
        var sorted = new JsonObject();
        foreach (var (name, entry) in manifest.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList())
        {
            manifest.Remove(name);
            sorted[name] = entry;
        }
        PyJson.WriteFile(Path.Combine(folder, ManifestName), sorted, 2);
    }

    /// <summary>fakelibs/&lt;FW&gt;/&lt;variant&gt;/: opt-in replacements for some of the set's files (an option's fakelib_variant).</summary>
    public static string VariantFolder(string folder, string variant) =>
        variant.Length > 0 && variant.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
            ? Path.Combine(folder, variant)
            : throw new ArgumentException($"invalid variant name '{variant}' (use a-z, 0-9, _)");

    /// <summary>
    /// Copies modules into fakelibs/&lt;firmware&gt;/ (or its &lt;variant&gt;/ subfolder) and records them in
    /// the manifest. Returns the folder and the (name, entry) pairs added.
    /// </summary>
    public static (string Folder, List<(string Name, JsonObject Entry)> Added) Add(
        string store, string firmware, IEnumerable<string> files, string source = "", string variant = "")
        => AddBatch(store, firmware, files, source, variant);

    internal static (string Folder, List<(string Name, JsonObject Entry)> Added) AddBatch(
        string store, string firmware, IEnumerable<string> files, string source, string variant, Action<string>? checkpoint = null)
    {
        string setFolder = Path.Combine(store, FolderName(firmware));
        string folder = setFolder;
        if (variant.Length > 0)
            folder = VariantFolder(folder, variant);
        var inputs = files.ToArray();
        var names = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string path in inputs)
        {
            Paths.RequireNoLinks(path);
            string name = Path.GetFileName(path);
            _ = Paths.ResolveRelativeFile(folder, name);
            if (!IsModule(name) || !names.Add(name))
                throw new ArgumentException($"{name}: require distinct .sprx or .prx filenames");
        }
        using var storeWriter = DirectoryTransaction.Acquire(store);
        using var transaction = new DirectoryTransaction(setFolder, checkpoint);
        transaction.CopyExisting();
        string stagedFolder = variant.Length == 0 ? transaction.Stage : VariantFolder(transaction.Stage, variant);
        Directory.CreateDirectory(stagedFolder);
        var manifest = LoadManifest(folder);
        var added = new List<(string, JsonObject)>();
        foreach (string path in inputs)
        {
            string name = Path.GetFileName(path);
            string dest = Path.Combine(stagedFolder, name);
            using (var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                File.Copy(path, dest, overwrite: true);
                File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(path));   // like shutil.copy2
            }
            File.SetAttributes(dest, File.GetAttributes(dest) & ~FileAttributes.ReadOnly);
            var entry = Describe(dest); // metadata belongs to the exact staged bytes, not a separately reread input
            entry["sha256"] = Sha256(dest);
            entry["size"] = new FileInfo(dest).Length;
            entry["source"] = source;
            manifest[name] = entry;
            added.Add((name, entry));
            checkpoint?.Invoke("file-added");
        }
        SaveManifest(stagedFolder, manifest);
        var problems = Verify(stagedFolder);
        if (problems.Count > 0)
            throw new InvalidDataException("staged library set failed verification: " + string.Join("; ", problems.Select(p => $"{p.Name}: {p.Problem}")));
        if (variant.Length > 0)
            Publication.Seal(stagedFolder, transaction.Id, checkpoint);
        transaction.Commit();
        return (folder, added);
    }

    /// <summary>(file, problem) for files missing, changed, or not in the manifest.</summary>
    public static List<(string Name, string Problem)> Verify(string folder)
    {
        var manifest = LoadManifest(folder);
        var problems = new List<(string, string)>();
        var present = Directory.EnumerateFiles(folder).Select(f => Path.GetFileName(f)).Where(IsModule).ToHashSet();
        foreach (var (name, entry) in manifest)
        {
            string path = Path.Combine(folder, name);
            if (!File.Exists(path))
                problems.Add((name, "missing"));
            else if (Sha256(path) != entry?["sha256"]?.GetValue<string>())
                problems.Add((name, "checksum differs from manifest"));
        }
        foreach (string name in present.Where(n => !manifest.ContainsKey(n)).Order(StringComparer.Ordinal))
            problems.Add((name, "not in manifest"));
        return problems;
    }

    /// <summary>Warnings for fakelibs whose (module, build date) is known not to work.</summary>
    public static List<string> BuildWarnings(string folder, Knowledge knowledge)
    {
        var warnings = new List<string>();
        foreach (var (name, entry) in LoadManifest(folder))
        {
            string? module = entry?["module"]?.GetValue<string>();
            string? date = entry?["build_date"]?.GetValue<string>();
            if (knowledge.Build(module, date) is { State: "broken" } build)
                warnings.Add($"{name} ({module} built {date}) is a known-broken build: {build.Note}");
        }
        return warnings;
    }

    /// <summary>
    /// Import status of every fakelib in a store folder against a target firmware's export list.
    /// The folder's other fakelibs count as providers, as they are loaded together.
    /// </summary>
    public static List<(string Name, SceElf Elf, List<ImportStatus> Statuses)> Check(string folder, Reference reference)
    {
        var modules = Directory.EnumerateFiles(folder).Where(f => IsModule(Path.GetFileName(f))).Order(StringComparer.Ordinal)
            .Select(f => (Name: Path.GetFileName(f), Elf: new SceElf(Self.FileKind(f) == Self.KindElf ? File.ReadAllBytes(f) : Self.Unwrap(File.ReadAllBytes(f)))))
            .ToList();
        var exports = modules.Select(m => m.Elf.Exports()).ToList();
        var result = new List<(string, SceElf, List<ImportStatus>)>();
        for (int i = 0; i < modules.Count; i++)
        {
            var provided = exports.Where((_, j) => j != i).SelectMany(e => e).ToHashSet(StringComparer.Ordinal);
            result.Add((modules[i].Name, modules[i].Elf, Imports.Classify(modules[i].Elf, reference.Nids, provided, reference.Exact)));
        }
        return result;
    }

    /// <summary>{folder name: manifest} for every firmware folder in the store, sorted by name.</summary>
    public static SortedDictionary<string, JsonObject> List(string store)
    {
        var result = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        if (!Directory.Exists(store))
            return result;
        foreach (string dir in Directory.EnumerateDirectories(store))
            if (!Path.GetFileName(dir).StartsWith(Publication.Prefix, StringComparison.OrdinalIgnoreCase))
                result[Path.GetFileName(dir)] = LoadManifest(dir);
        return result;
    }
}
