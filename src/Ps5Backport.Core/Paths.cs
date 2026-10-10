namespace Ps5Backport.Core;

/// <summary>
/// Locations of the shared knowledge and stores. They live at the repository root;
/// a published app carries <c>data/</c> next to the executable. Searched upwards from the app folder.
/// </summary>
public static class Paths
{
    /// <summary>Rejects output paths that would write into the source game tree.</summary>
    public static void ValidateOverlay(string source, string output)
    {
        string src = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        string dest = Path.TrimEndingDirectorySeparator(Path.GetFullPath(output));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string prefix = Path.EndsInDirectorySeparator(src) ? src : src + Path.DirectorySeparatorChar;
        if (string.Equals(src, dest, comparison) || dest.StartsWith(prefix, comparison))
            throw new ArgumentException("output folder must not be the game folder or inside it");
    }

    /// <summary>Resolves a portable relative file path under a root, rejecting traversal and ambiguous components.</summary>
    public static string ResolveRelativeFile(string root, string relative)
    {
        string normalized = relative.Replace('\\', '/');
        var parts = normalized.Split('/');
        foreach (string part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.Any(c => c < 32 || "<>:\"|?*".Contains(c)))
                throw new ArgumentException($"unsafe relative file path '{relative}'");
            string stem = part.Split('.')[0];
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || (stem.Length == 4 && stem[3] is >= '1' and <= '9'
                    && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))))
                throw new ArgumentException($"unsafe relative file path '{relative}'");
        }
        return Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
    }

    /// <summary>Rejects links/junctions in existing path components and, optionally, every tree entry.</summary>
    public static void RequireNoLinks(string path, bool tree = false)
    {
        string full = Path.GetFullPath(path);
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"filesystem links are not supported for publication: {current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        if (!tree || !Directory.Exists(full))
            return;
        var pending = new Stack<string>();
        pending.Push(full);
        while (pending.TryPop(out string? directory))
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"filesystem links are not supported for publication: {entry}");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
            }
    }

    /// <summary>Rejects overlapping trees in either direction before a directory is replaced.</summary>
    public static void RequireSeparateTrees(string first, string second)
    {
        RequireNoLinks(first);
        RequireNoLinks(second);
        string a = PhysicalPaths.Resolve(first), b = PhysicalPaths.Resolve(second);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        char separator = Path.DirectorySeparatorChar;
        if (string.Equals(a, b, comparison) || a.StartsWith(b.TrimEnd(separator) + separator, comparison)
            || b.StartsWith(a.TrimEnd(separator) + separator, comparison))
            throw new ArgumentException($"publication and input trees must not overlap: {first}, {second}");
    }

    /// <summary>Rejects replacing an input directory or its ancestor, including physical path aliases.</summary>
    public static void RequireNotContaining(string destination, string input)
    {
        RequireNoLinks(destination);
        RequireNoLinks(input);
        string a = PhysicalPaths.Resolve(destination), b = PhysicalPaths.Resolve(input);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(a, b, comparison) || b.StartsWith(a.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException($"publication must not replace an input directory or its ancestor: {destination}, {input}");
    }

    /// <summary>The first folder at or above the app folder that contains <paramref name="relative"/>, or null.</summary>
    public static string? FindUp(string relative, string? start = null)
    {
        for (var dir = new DirectoryInfo(start ?? AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (Directory.Exists(candidate) || File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>data/ (fixes, stubs, candidates, ...).</summary>
    public static string DataDir => FindUp(Path.Combine("data", "fixes.json")) is { } f
        ? Path.GetDirectoryName(f)!
        : throw new DirectoryNotFoundException("data/fixes.json not found above the app folder");

    /// <summary>firmware_db/ (per-firmware export lists), or null when absent.</summary>
    public static string? FirmwareDbDir => FindUp("firmware_db");

    /// <summary>fakelibs/ (per-firmware fakelib store), or null when absent.</summary>
    public static string? FakelibStoreDir => FindUp("fakelibs");
}
