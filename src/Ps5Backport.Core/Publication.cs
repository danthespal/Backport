using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ps5Backport.Core;

/// <summary>Completion records for a published overlay or library set. Hashes exclude the record itself.</summary>
public static class Publication
{
    public const string ManifestName = "backport_publication.json", EvidenceName = "backport_run.json";
    internal const string Prefix = ".ps5bp-";
    internal sealed record FileRecord(long Size, string Sha256);
    internal sealed record Manifest(int Version, string RunId, Dictionary<string, FileRecord> Files);

    /// <summary>Checks the recorded inventory, sizes and content hashes; later additions invalidate the record.</summary>
    public static void Verify(string directory)
    {
        Paths.RequireNoLinks(directory, tree: true);
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(Path.Combine(directory, ManifestName)))
            ?? throw new InvalidDataException("missing publication manifest");
        if (manifest.Version != 1 || !Guid.TryParseExact(manifest.RunId, "N", out _) || manifest.Files is null)
            throw new InvalidDataException("invalid publication manifest");
        var actual = Inventory(directory);
        if (actual.Count != manifest.Files.Count || manifest.Files.Any(p => !actual.TryGetValue(p.Key, out var value) || value != p.Value))
            throw new InvalidDataException($"published files differ from their manifest: {directory}");
    }

    internal static Dictionary<string, FileRecord> Inventory(string directory) =>
        GameScan.WalkFiles(directory).Where(f => Path.GetRelativePath(directory, f) != ManifestName)
            .Order(StringComparer.Ordinal).ToDictionary(f => Path.GetRelativePath(directory, f).Replace('\\', '/'),
                f => new FileRecord(new FileInfo(f).Length, FakelibStore.Sha256(f)), StringComparer.Ordinal);

    internal static void Seal(string directory, string id, Action<string>? checkpoint)
    {
        Paths.RequireNoLinks(directory, tree: true);
        foreach (string file in GameScan.WalkFiles(directory))
        {
            // Only transaction-owned copies are made writable; source and backup attributes stay untouched.
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            stream.Flush(flushToDisk: true);
            checkpoint?.Invoke("file-flushed");
        }
        WriteDurable(Path.Combine(directory, ManifestName), new Manifest(1, id, Inventory(directory)));
        Verify(directory);
    }

    internal static void WriteDurable<T>(string path, T value)
    {
        Paths.RequireNoLinks(path);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    /// <summary>Recovers an interrupted publication. Diagnostic folders are retained; a committed run's backup is discarded.</summary>
    public static void Recover(string destination)
    {
        using var writer = DirectoryTransaction.Acquire(destination);
        DirectoryTransaction.RecoverLocked(Path.GetFullPath(destination));
    }
}

/// <summary>Same-volume staged directory replacement, serialized by a stable exclusive lock file.</summary>
internal sealed class DirectoryTransaction : IDisposable
{
    internal sealed record Journal(int Version, string Id, bool HadDestination, bool Committed);
    readonly FileStream _writer;
    readonly Action<string>? _checkpoint;
    readonly Journal _journal;
    bool _finished;
    internal string Destination { get; }
    internal string Stage { get; }
    internal string Backup { get; }
    internal string Id => _journal.Id;

    static string Control(string destination)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        string parent = Path.GetDirectoryName(full) ?? throw new ArgumentException("cannot publish a filesystem root");
        if (Path.GetFileName(full).StartsWith(Publication.Prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("reserved transaction directory name");
        string physical = PhysicalPaths.Resolve(full);
        string key = OperatingSystem.IsWindows() ? physical.ToUpperInvariant() : physical;
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(parent, Publication.Prefix + hash);
    }

    internal static FileStream Acquire(string destination)
    {
        Paths.RequireNoLinks(destination, tree: true);
        string control = Control(destination);
        if (File.Exists(destination))
            throw new IOException($"destination is a file: {destination}");
        Directory.CreateDirectory(Path.GetDirectoryName(control)!);
        Paths.RequireNoLinks(control + ".lock");
        return new FileStream(control + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal DirectoryTransaction(string destination, Action<string>? checkpoint = null)
    {
        Destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        _checkpoint = checkpoint;
        _writer = Acquire(Destination);
        try
        {
            RecoverLocked(Destination);
            _journal = new Journal(1, Guid.NewGuid().ToString("N"), Directory.Exists(Destination), false);
            Stage = Control(Destination) + "-" + Id + ".stage";
            Backup = Control(Destination) + "-" + Id + ".backup";
            Publication.WriteDurable(Control(Destination) + ".journal.json", _journal);
            Directory.CreateDirectory(Stage);
            _checkpoint?.Invoke("stage-created");
        }
        catch { _writer.Dispose(); throw; }
    }

    /// <summary>Copies ordinary files/directories, refusing link entries. Used to update existing store sets/indexes.</summary>
    internal void CopyExisting(CancellationToken cancel = default)
    {
        if (!Directory.Exists(Destination))
            return;
        Paths.RequireNoLinks(Destination, tree: true);
        foreach (string file in GameScan.WalkFiles(Destination, cancel))
        {
            string relative = Path.GetRelativePath(Destination, file);
            if (relative == Publication.ManifestName || relative.Split(Path.DirectorySeparatorChar)
                .Any(p => p.StartsWith(Publication.Prefix, StringComparison.OrdinalIgnoreCase)))
                continue;
            string target = Paths.ResolveRelativeFile(Stage, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
            _checkpoint?.Invoke("file-copied");
        }
    }

    internal void Commit(CancellationToken cancel = default)
    {
        Publication.Seal(Stage, Id, _checkpoint);
        _checkpoint?.Invoke("prepared");
        cancel.ThrowIfCancellationRequested();
        Paths.RequireNoLinks(Destination, tree: true);
        Paths.RequireNoLinks(Stage, tree: true);
        Publication.Verify(Stage);
        // Cancellation ends here: these renames and the committed journal form the recovery critical section.
        if (_journal.HadDestination)
            Directory.Move(Destination, Backup);
        _checkpoint?.Invoke("backup-moved");
        Directory.Move(Stage, Destination);
        _checkpoint?.Invoke("stage-moved");
        Publication.WriteDurable(Control(Destination) + ".journal.json", _journal with { Committed = true });
        _finished = true;
        _checkpoint?.Invoke("committed");
        try
        {
            DiscardBackup(Backup);
            File.Delete(Control(Destination) + ".journal.json");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { /* Publication is committed; cleanup/recovery retries under the next writer's lock. */ }
    }

    /// <summary>Retains incomplete artifacts with no completion marker and leaves the destination untouched.</summary>
    internal string RetainDiagnostic()
    {
        File.Delete(Path.Combine(Stage, Publication.ManifestName));
        string diagnostic = Stage[..^6] + ".incomplete";
        Directory.Move(Stage, diagnostic);
        File.Delete(Control(Destination) + ".journal.json");
        _finished = true;
        return diagnostic;
    }

    internal static void RecoverLocked(string destination)
    {
        string control = Control(destination), journalPath = control + ".journal.json";
        Paths.RequireNoLinks(journalPath);
        if (!File.Exists(journalPath))
            return;
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(journalPath))
            ?? throw new InvalidDataException("invalid publication journal; manual inspection required");
        if (journal.Version != 1 || !Guid.TryParseExact(journal.Id, "N", out _))
            throw new InvalidDataException("invalid publication journal; manual inspection required");
        string stage = control + "-" + journal.Id + ".stage", backup = control + "-" + journal.Id + ".backup";
        Paths.RequireNoLinks(destination, tree: true);
        Paths.RequireNoLinks(stage, tree: true);
        Paths.RequireNoLinks(backup, tree: true);
        if (journal.Committed)
        {
            Publication.Verify(destination);
            DiscardBackup(backup);
        }
        else if (Directory.Exists(backup))
        {
            if (Directory.Exists(destination))
                DeletePublished(destination, journal.Id);
            Directory.Move(backup, destination);
        }
        else if (!journal.HadDestination && !Directory.Exists(stage) && Directory.Exists(destination))
            DeletePublished(destination, journal.Id);
        if (Directory.Exists(stage))
            Directory.Delete(stage, recursive: true); // exact journal-derived sibling, validated above; never a supplied path
        File.Delete(journalPath);
    }

    /// <summary>Best-effort removal of the replaced destination once the new one is committed; a failure leaves it in place.</summary>
    static void DiscardBackup(string backup)
    {
        try
        {
            if (!Directory.Exists(backup))
                return;
            Paths.RequireNoLinks(backup, tree: true);
            Directory.Delete(backup, recursive: true); // exact journal-derived sibling, never a supplied path
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static void DeletePublished(string destination, string id)
    {
        var manifest = JsonSerializer.Deserialize<Publication.Manifest>(File.ReadAllBytes(Path.Combine(destination, Publication.ManifestName)));
        if (manifest?.RunId != id)
            throw new IOException("destination changed during interrupted publication; backup retained for manual recovery");
        Paths.RequireNoLinks(destination, tree: true);
        Directory.Delete(destination, recursive: true); // only the uncommitted directory bearing this transaction's identity
    }

    public void Dispose()
    {
        try { if (!_finished) RecoverLocked(Destination); }
        finally { _writer.Dispose(); }
    }
}
