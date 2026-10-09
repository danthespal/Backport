using System.Security.Cryptography;
using System.Text;

namespace Ps5Backport.Tests;

/// <summary>The repository and its sample files. Tests skip (not fail) when a sample file is missing.</summary>
public static class Repo
{
    /// <summary>Repository root: the first parent folder that contains Ps5Backport.slnx.</summary>
    public static string Root { get; } = FindRoot();

    static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ps5Backport.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("repository root (folder with Ps5Backport.slnx) not found");
    }

    /// <summary>Absolute path under the repo root; skips the test when it doesn't exist.</summary>
    public static string Need(params string[] relative)
    {
        string path = Path.Combine([Root, .. relative]);
        if (!File.Exists(path) && !Directory.Exists(path))
            Assert.Skip($"sample file missing: {Path.Combine(relative)}");
        return path;
    }
}

/// <summary>
/// Recorded expected results (src/Ps5Backport.Tests/golden/). They were first recorded when the C# engine
/// matched the former Python engine byte for byte, so they carry that proof. After an intended change
/// (a new fix rule, a new report line), record again with the environment variable PS5BP_RECORD=1 and
/// review the diff of golden/ before committing it.
/// </summary>
public static class Golden
{
    static string Dir => Path.Combine(Repo.Root, "src", "Ps5Backport.Tests", "golden");

    public static bool Recording => Environment.GetEnvironmentVariable("PS5BP_RECORD") == "1";

    /// <summary>Compares <paramref name="actual"/> with golden/<paramref name="name"/> (LF line endings), or records it.</summary>
    public static void Match(string name, string actual)
    {
        actual = actual.Replace("\r\n", "\n");
        string path = Path.Combine(Dir, name);
        if (Recording)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual, new UTF8Encoding(false));
            return;
        }
        if (!File.Exists(path))
            Assert.Fail($"golden/{name} is missing: run the tests once with PS5BP_RECORD=1");
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n"), actual);
    }

    public static string Sha(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string Sha(string text) => Sha(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n")));

    /// <summary>Replaces absolute paths (the repo, temp folders) with placeholders so golden files are portable.</summary>
    public static string Portable(string text, params (string Path, string Name)[] paths)
    {
        foreach (var (path, name) in paths.Append((Repo.Root, "<repo>")).OrderByDescending(p => p.Item1.Length))
            text = text.Replace(path.Replace("\\", "\\\\"), name)   // as escaped in JSON
                       .Replace(path, name).Replace(path.Replace('\\', '/'), name);
        return text;
    }
}
