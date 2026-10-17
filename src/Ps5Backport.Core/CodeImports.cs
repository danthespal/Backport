namespace Ps5Backport.Core;

/// <summary>
/// The engine's import statuses (<see cref="Imports.Classify"/>) for one module against a target firmware, as the code
/// graph shows them. Provided: {NID: the shipped module that exports it}. Introduced: {NID: first firmware that has it}
/// for imports that are missing or unknown on the target.
/// </summary>
public sealed record ImportCheck(string Firmware, string Reference, bool Exact, IReadOnlyDictionary<string, ImportStatus> Statuses,
                                 IReadOnlyDictionary<string, string> Provided, IReadOnlyDictionary<string, string> Introduced)
{
    /// <summary>Imports a game on this firmware cannot resolve: missing, or unknown with a lower-bound reference.</summary>
    public IEnumerable<ImportStatus> Unresolved => Statuses.Values.Where(s => Imports.IsUnresolved(s.Status));
}

public sealed partial class CodeImage
{
    IReadOnlyDictionary<string, string>? _providers;
    readonly Dictionary<string, string> _providerFiles = new(StringComparer.Ordinal);
    readonly Lock _providerLock = new();

    /// <summary>
    /// {NID: module file} for every module shipped next to this one (the folder's *.prx/*.sprx, sce_module/ and fakelib/ or the
    /// constructor's fakelib folder, unwrapped in memory). Encrypted or unreadable modules are skipped.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProviderExports()
    {
        lock (_providerLock)
            return _providers ??= ReadProviders();
    }

    /// <summary>The full path of the shipped module that exports <paramref name="nid"/> (see <see cref="ProviderExports"/>), or
    /// null: where the code graph follows a call into a fakelib or a game module.</summary>
    public string? ProviderFile(string nid)
    {
        ProviderExports();
        lock (_providerLock)
            return _providerFiles.GetValueOrDefault(nid);
    }

    Dictionary<string, string> ReadProviders()
    {
        string root = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)) ?? ".";
        var providers = new Dictionary<string, string>(StringComparer.Ordinal);
        var folders = new[] { (Dir: root, Label: ""), (Dir: System.IO.Path.Combine(root, "sce_module"), Label: "sce_module/"), (Dir: FakelibFolder, Label: "fakelib/") };
        var files = folders.Where(f => Directory.Exists(f.Dir))
            .SelectMany(f => Directory.EnumerateFiles(f.Dir).Select(file => (File: file, Name: f.Label + System.IO.Path.GetFileName(file))))
            .Where(f => f.File.EndsWith(".prx", StringComparison.OrdinalIgnoreCase) || f.File.EndsWith(".sprx", StringComparison.OrdinalIgnoreCase));
        foreach (var (file, name) in files)
        {
            try
            {
                byte[] data = File.ReadAllBytes(file);
                if (Self.IsSelf(Self.Kind(data)))
                {
                    if (Self.EncryptedSegments(data) > 0)
                        continue;
                    data = Self.Unwrap(data);
                }
                foreach (string nid in new SceElf(data).Exports())
                    if (providers.TryAdd(nid, name))
                        _providerFiles[nid] = file;
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // malformed, locked or unreadable: skipped, as documented
            }
        }
        return providers;
    }

    /// <summary>
    /// This module's imports classified for <paramref name="firmware"/> against <paramref name="databases"/> (firmware_db,
    /// sorted), counting the modules shipped next to it as providers, the same way a backport run does.
    /// </summary>
    public ImportCheck CheckImports(IReadOnlyList<FirmwareExports> databases, string firmware, Knowledge knowledge)
    {
        var reference = Core.Imports.LoadReference("firmware_db", firmware, databases);
        var providers = ProviderExports();
        var provided = providers.Keys.ToHashSet(StringComparer.Ordinal);
        var statuses = Core.Imports.Classify(Elf, reference.Nids, provided, reference.Exact).ToDictionary(s => s.Nid, StringComparer.Ordinal);
        var target = FirmwareDb.Key(firmware);
        var newer = databases.Where(d => FirmwareDb.Key(d.Firmware).CompareTo(target) > 0).ToList();
        var introduced = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in statuses.Values.Where(s => Core.Imports.IsUnresolved(s.Status)))
            if (FirmwareDb.Introduced(s.Nid, newer, knowledge) is { } first)
                introduced[s.Nid] = first.Firmware;
        return new ImportCheck(Firmware.Name(firmware), reference.Label, reference.Exact, statuses,
                               providers, introduced);
    }
}
