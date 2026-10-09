namespace Ps5Backport.Core;

/// <summary>
/// Locations of the shared knowledge and stores. They live at the repository root;
/// a published app carries <c>data/</c> next to the executable. Searched upwards from the app folder.
/// </summary>
public static class Paths
{
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
