using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ps5Backport.Core;

/// <summary>Guards and fingerprints relevant inputs; private paths are kept in memory, not in the evidence sidecar.</summary>
internal sealed class RunEvidence(CancellationToken cancel) : IDisposable
{
    readonly List<(string Path, string Label, FileStream? Guard)> _inputs = [];
    readonly List<(string Root, string Pattern, string[] Files)> _sets = [];
    readonly List<(string Root, string[] Files)> _moduleSets = [];
    readonly List<(string Root, string[] Files)> _trees = [];
    internal Dictionary<string, Publication.FileRecord?> Files { get; } = new(StringComparer.Ordinal);

    internal void Capture(string path, string label, string? expectedHash = null)
    {
        cancel.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        Paths.RequireNoLinks(path);
        FileStream? guard = null;
        try
        {
            Publication.FileRecord? fingerprint = null;
            if (File.Exists(path))
            {
                guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                fingerprint = new Publication.FileRecord(guard.Length, Convert.ToHexStringLower(SHA256.HashData(guard)));
            }
            if (expectedHash is not null && fingerprint?.Sha256 != expectedHash)
                throw new IOException($"loaded knowledge changed on disk; reload it before cooking: {label}");
            Files.Add(label, fingerprint);
            _inputs.Add((path, label, guard));
        }
        catch { guard?.Dispose(); throw; }
    }

    internal void CaptureSet(string root, string category, string pattern, string[]? expected = null)
    {
        Paths.RequireNoLinks(root, tree: true);
        var files = SetFiles(root, pattern);
        if (expected is not null && !files.SequenceEqual(expected.Where(f => !Path.GetFileName(f)
            .StartsWith(Publication.Prefix, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)))
            throw new IOException($"selected library inventory changed during preflight: {root}");
        _sets.Add((root, pattern, files));
        foreach (string file in files)
            Capture(file, category + ":" + Path.GetRelativePath(root, file).Replace('\\', '/'));
    }

    static string[] SetFiles(string root, string pattern) => (Directory.Exists(root) ? Directory.GetFiles(root, pattern) : [])
        .Where(f => !Path.GetFileName(f).StartsWith(Publication.Prefix, StringComparison.OrdinalIgnoreCase))
        .Order(StringComparer.Ordinal).ToArray();

    internal void CaptureTree(string root, string category)
    {
        Paths.RequireNoLinks(root, tree: true);
        string[] files = Directory.Exists(root) ? GameScan.WalkFiles(root, cancel).Order(StringComparer.Ordinal).ToArray() : [];
        _trees.Add((root, files));
        foreach (string file in files)
            Capture(file, category + ":" + Path.GetRelativePath(root, file).Replace('\\', '/'));
    }

    internal void CaptureModules(string root, IReadOnlyList<string> modules)
    {
        Paths.RequireNoLinks(root, tree: true);
        _moduleSets.Add((root, modules.ToArray()));
        foreach (string relative in modules)
            Capture(Path.Combine(root, relative), "game:" + relative.Replace('\\', '/'));
    }

    internal void Verify()
    {
        foreach (var (root, expected) in _trees)
        {
            cancel.ThrowIfCancellationRequested();
            Paths.RequireNoLinks(root, tree: true);
            string[] files = Directory.Exists(root) ? GameScan.WalkFiles(root, cancel).Order(StringComparer.Ordinal).ToArray() : [];
            if (!files.SequenceEqual(expected))
                throw new IOException($"input tree changed during the run: {root}");
        }
        foreach (var (root, pattern, expected) in _sets)
        {
            cancel.ThrowIfCancellationRequested();
            Paths.RequireNoLinks(root, tree: true);
            if (!SetFiles(root, pattern).SequenceEqual(expected))
                throw new IOException($"input inventory changed during the run: {root}");
        }
        foreach (var (root, expected) in _moduleSets)
        {
            Paths.RequireNoLinks(root, tree: true);
            if (!GameScan.FindModules(root, cancel).SequenceEqual(expected))
                throw new IOException($"source module inventory changed during the run: {root}");
        }
        foreach (var (path, label, _) in _inputs)
        {
            cancel.ThrowIfCancellationRequested();
            Paths.RequireNoLinks(path);
            var actual = File.Exists(path) ? new Publication.FileRecord(new FileInfo(path).Length, FakelibStore.Sha256(path)) : null;
            if (actual != Files[label])
                throw new IOException($"input changed during the run: {label}");
        }
    }

    internal void Write(string directory, BackportOptions options, GameResult result, Reference reference)
    {
        Verify();
        var engine = typeof(Pipeline).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(Pipeline).Assembly.GetName().Version?.ToString();
        Publication.WriteDurable(Path.Combine(directory, Publication.EvidenceName), new
        {
            Version = 1, Engine = engine, Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            result.TitleId, result.ContentVersion, TargetFirmware = options.Firmware,
            ReferenceExact = reference.Exact, ReferenceNidsSha256 = Convert.ToHexStringLower(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(string.Join('\n', reference.Nids.Order(StringComparer.Ordinal))))),
            Options = new
            {
                options.KnownFixes, options.CandidateFixes, options.StubUnknown, options.DefineSymbol, options.ExtendText,
                options.PatchParam, options.AmprDebugLogging, Selected = options.Options, options.StubNids,
            },
            NamesSha256 = options.Names?.Fingerprint,
            SourceModules = _moduleSets.SelectMany(s => s.Files).Select(f => f.Replace('\\', '/')).ToArray(), Inputs = Files,
        });
    }

    internal static void VerifySource(string directory, string source)
    {
        string path = Path.Combine(directory, Publication.EvidenceName);
        if (!File.Exists(path))
            return; // legacy overlays have no provenance to check
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        if (document.RootElement.TryGetProperty("SourceModules", out var moduleList)
            && !GameScan.FindModules(source).Select(f => f.Replace('\\', '/')).SequenceEqual(moduleList.Deserialize<string[]>() ?? []))
            throw new IOException("overlay belongs to a different source module inventory");
        if (!document.RootElement.TryGetProperty("Inputs", out var inputs))
            return;
        foreach (var entry in inputs.EnumerateObject().Where(e => e.Name.StartsWith("game:", StringComparison.Ordinal)))
        {
            string file = Paths.ResolveRelativeFile(source, entry.Name[5..]);
            var expected = entry.Value.Deserialize<Publication.FileRecord>();
            var actual = File.Exists(file) ? new Publication.FileRecord(new FileInfo(file).Length, FakelibStore.Sha256(file)) : null;
            if (actual != expected)
                throw new IOException($"overlay belongs to different source inputs: {entry.Name}");
        }
    }

    public void Dispose()
    {
        foreach (var (_, _, guard) in _inputs)
            guard?.Dispose();
    }
}
