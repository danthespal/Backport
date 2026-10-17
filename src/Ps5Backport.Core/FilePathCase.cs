using System.Text;

namespace Ps5Backport.Core;

/// <summary>Resolves explicit game paths against the dump without assuming a title or engine.</summary>
internal static class FilePathCase
{
    internal static Dictionary<string, string> Resolve(IEnumerable<string> files, IEnumerable<byte[]> binaries,
        List<string> warnings, CancellationToken cancel)
    {
        var paths = files.ToArray();
        var full = paths.GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var suffixes = paths.SelectMany(p => p.Select((c, i) => (c, i)).Where(x => x.c == '/')
                .Select(x => (Suffix: p[(x.i + 1)..], Path: p)))
            .GroupBy(x => x.Suffix, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Path).ToArray(), StringComparer.OrdinalIgnoreCase);
        var requested = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (byte[] data in binaries)
        {
            int start = 0;
            for (int i = 0; i < data.Length; i++)
            {
                if ((i & 0xfffff) == 0) cancel.ThrowIfCancellationRequested();
                if (data[i] is >= 32 and <= 126) continue;
                int length = i - start;
                if (data[i] == 0 && length is >= 4 and <= 4096)
                {
                    string text = Encoding.ASCII.GetString(data, start, length);
                    bool formatted = text.StartsWith("%s/", StringComparison.Ordinal);
                    bool absolute = text.StartsWith("/app0/", StringComparison.Ordinal);
                    string relative = text.StartsWith("/app0/", StringComparison.Ordinal) ? text[6..]
                        : formatted ? text[3..] : text.StartsWith("./", StringComparison.Ordinal) ? text[2..] : text;
                    // Bare names and arbitrary format strings do not establish a file's location.
                    if ((relative.Contains('/') || formatted || absolute) && !relative.StartsWith('/') && !relative.Contains('%')
                        && !relative.Contains('\\') && !relative.Split('/').Any(p => p is "" or "." or ".."))
                    {
                        var index = formatted ? suffixes : full;
                        if (index.TryGetValue(relative, out var matches))
                        {
                            // Only files whose names differ just by case are ambiguous. A "%s/" suffix found in several
                            // folders (e.g. V Rising's Media/ and v3/Media/) asks for the same spelling in each of them.
                            if (matches.Distinct(StringComparer.OrdinalIgnoreCase).Count() != matches.Length)
                                warnings.Add($"ambiguous file path casing: {text}; source filenames preserved");
                            else
                                foreach (string source in matches)
                                {
                                    string expected = formatted ? source[..(source.Length - relative.Length)] + relative : relative;
                                    if (!requested.TryGetValue(source, out var spellings))
                                        requested[source] = spellings = new(StringComparer.Ordinal);
                                    spellings.Add(expected);
                                }
                        }
                    }
                }
                start = i + 1;
            }
        }
        var corrected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, spellings) in requested)
        {
            if (spellings.Count > 1)
                warnings.Add($"conflicting file path casing: {source} ({string.Join(", ", spellings.Order(StringComparer.Ordinal))}); source filename preserved");
            else if (spellings.Single() is { } expected && expected != source)
                corrected.Add(source, expected);
        }
        // A directory has only one spelling. Do not publish paths that disagree about it.
        var directories = requested.Values.SelectMany(s => s).SelectMany(p => p.Select((c, i) => (c, i))
                .Where(x => x.c == '/').Select(x => p[..x.i]))
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        var conflicts = directories.Where(g => g.Distinct(StringComparer.Ordinal).Count() > 1).ToArray();
        foreach (var group in conflicts)
        {
            warnings.Add($"conflicting directory casing: {group.Key}; source directory preserved");
            foreach (string source in corrected.Where(p => p.Value.StartsWith(group.Key + "/", StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToArray())
                corrected.Remove(source);
        }
        // Propagate an unambiguous directory correction to sibling files too.
        foreach (string source in paths)
        {
            string expected = corrected.GetValueOrDefault(source, source);
            foreach (var group in directories.Where(g => !conflicts.Any(c => g.Key.Equals(c.Key, StringComparison.OrdinalIgnoreCase)
                    || g.Key.StartsWith(c.Key + "/", StringComparison.OrdinalIgnoreCase))).OrderBy(g => g.Key.Length))
                if (expected.StartsWith(group.Key + "/", StringComparison.OrdinalIgnoreCase))
                    expected = group.First() + expected[group.Key.Length..];
            if (expected != source) corrected[source] = expected;
        }
        foreach (var (source, expected) in corrected.OrderBy(p => p.Key, StringComparer.Ordinal))
            warnings.Add($"file path casing corrected: {source} -> {expected} (referenced by the game)");
        return corrected;
    }
}
