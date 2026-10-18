using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ps5Backport.Core;

/// <summary>Directory identity for overlap checks and lock keys: Windows short names and drive aliases, Unix symlinks.</summary>
internal static class PhysicalPaths
{
    internal static string Resolve(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var tail = new Stack<string>();
        string existing = full;
        while (!Directory.Exists(existing))
        {
            tail.Push(Path.GetFileName(existing));
            existing = Path.GetDirectoryName(existing) ?? throw new DirectoryNotFoundException(full);
        }
        string resolved = (OperatingSystem.IsWindows() ? FinalPath(existing) : RealPath(existing)).TrimEnd(Path.DirectorySeparatorChar);
        foreach (string component in tail)
            resolved += Path.DirectorySeparatorChar + component;
        return resolved.Length == 0 ? "/" : resolved;
    }

    /// <summary>Whether names on the volume holding <paramref name="path"/> match regardless of case: always on Windows,
    /// asked per volume on macOS (APFS, HFS+ and exFAT default to case-insensitive), never on other systems.</summary>
    internal static bool IgnoresCase(string path)
    {
        if (OperatingSystem.IsWindows())
            return true;
        if (!OperatingSystem.IsMacOS())
            return false;
        string existing = Path.GetFullPath(path);
        while (!Directory.Exists(existing) && !File.Exists(existing))
            existing = Path.GetDirectoryName(existing) ?? "/";
        // _PC_CASE_SENSITIVE is 1 on a case-sensitive volume. An error counts as case-insensitive, the stricter answer for every caller.
        return pathconf(existing, 11) != 1;
    }

    /// <summary>A link in an ancestor the user cannot write to was made by the system (macOS /var, /tmp), not the user.</summary>
    internal static bool UserCanWrite(string directory) => OperatingSystem.IsWindows() || access(directory, 2) == 0;

    static string FinalPath(string existing)
    {
        // Zero desired access permits metadata queries. Backup semantics allows opening a directory.
        using var handle = CreateFileW(existing, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException($"cannot establish directory identity: {existing}", new Win32Exception(Marshal.GetLastWin32Error()));
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 2); // normalized NT volume path
        if (length == 0 || length >= buffer.Capacity)
            throw new IOException($"cannot establish directory identity: {existing}", new Win32Exception(Marshal.GetLastWin32Error()));
        return buffer.ToString();
    }

    static string RealPath(string existing)
    {
        IntPtr resolved = realpath(existing, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
            throw new IOException($"cannot establish directory identity: {existing}", new Win32Exception(Marshal.GetLastWin32Error()));
        try { return Marshal.PtrToStringUTF8(resolved)!; }
        finally { free(resolved); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    [DllImport("libc", SetLastError = true)]
    static extern IntPtr realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);

    [DllImport("libc")]
    static extern void free(IntPtr pointer);

    [DllImport("libc", SetLastError = true)]
    static extern nint pathconf([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int name);

    [DllImport("libc", SetLastError = true)]
    static extern int access([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);
}
