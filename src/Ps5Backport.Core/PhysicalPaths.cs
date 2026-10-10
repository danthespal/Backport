using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ps5Backport.Core;

/// <summary>Windows directory identity for overlap checks and lock keys, including short names and drive aliases.</summary>
internal static class PhysicalPaths
{
    internal static string Resolve(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!OperatingSystem.IsWindows())
            return full;
        var tail = new Stack<string>();
        string existing = full;
        while (!Directory.Exists(existing))
        {
            tail.Push(Path.GetFileName(existing));
            existing = Path.GetDirectoryName(existing) ?? throw new DirectoryNotFoundException(full);
        }
        // Zero desired access permits metadata queries. Backup semantics allows opening a directory.
        using var handle = CreateFileW(existing, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException($"cannot establish directory identity: {existing}", new Win32Exception(Marshal.GetLastWin32Error()));
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 2); // normalized NT volume path
        if (length == 0 || length >= buffer.Capacity)
            throw new IOException($"cannot establish directory identity: {existing}", new Win32Exception(Marshal.GetLastWin32Error()));
        string resolved = buffer.ToString().TrimEnd('\\');
        foreach (string component in tail)
            resolved += "\\" + component;
        return resolved;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
