using System.Diagnostics;
using System.Text.Json;

namespace Ps5Backport.Tests;

/// <summary>
/// The Python engine (ps5bp) is the reference implementation: parity tests run it on the
/// same input and compare. Tests skip (not fail) when Python, ps5bp or sample files are missing.
/// </summary>
public static class Oracle
{
    /// <summary>Repository root: the first parent folder that contains ps5bp/.</summary>
    public static string Root { get; } = FindRoot();

    static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "ps5bp")))
                return dir.FullName;
        throw new InvalidOperationException("repository root (folder with ps5bp/) not found");
    }

    /// <summary>Absolute path under the repo root; skips the test when it doesn't exist.</summary>
    public static string Need(params string[] relative)
    {
        string path = Path.Combine([Root, .. relative]);
        if (!File.Exists(path) && !Directory.Exists(path))
            Assert.Skip($"sample file missing: {Path.Combine(relative)}");
        return path;
    }

    static readonly Lazy<string?> Python = new(FindPython);

    static string? FindPython()
    {
        foreach (string exe in new[] { "python", "python3", "py" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe, "-c \"import sys; print(sys.version_info[0])\"")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                });
                if (p is null)
                    continue;
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode == 0 && output.Trim() == "3")
                    return exe;
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return null;
    }

    /// <summary>
    /// Runs a Python snippet with the repo root as working directory (so `import ps5bp` works)
    /// and returns its stdout. The snippet must print its result.
    /// </summary>
    public static string Run(string code)
    {
        string exe = Python.Value ?? throw SkipException("Python 3 not found");
        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Root,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("-");                      // read the script from stdin (no quoting issues)
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        using var p = Process.Start(info)!;
        p.StandardInput.Write(code);
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"ps5bp oracle failed:\n{stderr}");
        return stdout.Result;
    }

    /// <summary>Runs a snippet that prints one JSON document and deserialises it.</summary>
    public static T Json<T>(string code) => JsonSerializer.Deserialize<T>(Run(code))!;

    static Exception SkipException(string reason)
    {
        Assert.Skip(reason);
        return new UnreachableException();
    }
}
