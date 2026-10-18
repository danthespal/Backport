using System.Text;

namespace Ps5Backport.Core;

/// <summary>A bundled export candidate; Match describes library metadata, not runtime resolution.</summary>
public sealed record ImportProviderCandidate(string Module, string Library, string Match);

/// <summary>One distinct imported library/NID pair, independent of cooking classification.</summary>
public sealed record ImportProviderFinding(string Nid, string Library, IReadOnlyList<ImportProviderCandidate> Providers);

public static partial class Imports
{
    /// <summary>Reports bundled export candidates without changing status classification or patch selection.</summary>
    public static List<ImportProviderFinding> AuditProviders(string module, SceElf elf,
        IEnumerable<(string Module, SceElf Elf)> providers)
    {
        var comparer = PhysicalPaths.IgnoresCase(module) ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var exports = providers.Where(p => !comparer.Equals(p.Module, module) && !ReferenceEquals(p.Elf, elf))
            .SelectMany(p => p.Elf.ExportsByLibrary().Select(e => (p.Module, e.Library, e.Nid)))
            .Distinct().ToLookup(e => e.Nid, StringComparer.Ordinal);
        return elf.Imports().Where(i => !i.DefinedLocally).Select(i => (i.Nid, i.Library)).Distinct()
            .OrderBy(i => i.Library, StringComparer.Ordinal).ThenBy(i => i.Nid, StringComparer.Ordinal)
            .Select(i => new ImportProviderFinding(i.Nid, i.Library, exports[i.Nid]
                .OrderBy(e => e.Module, StringComparer.Ordinal).ThenBy(e => e.Library, StringComparer.Ordinal)
                .Select(e => new ImportProviderCandidate(e.Module, e.Library,
                    e.Library.Length == 0 || i.Library.StartsWith('#') ? "unknown-library"
                    : e.Library == i.Library ? "same-library" : "cross-library")).ToList())).ToList();
    }

    /// <summary>Formats the optional provider audit. Cross-library matches may be aliases or re-exports.</summary>
    public static string ProviderReport(IReadOnlyList<ImportProviderFinding> findings)
    {
        var text = new StringBuilder("provider audit (bundled export candidates; does not change cooking):\n");
        text.Append("  cross-library matches may be aliases/re-exports; no bundled provider does not mean absent from firmware.\n");
        foreach (var finding in findings)
        {
            text.Append($"  [{finding.Library}] {finding.Nid}:");
            if (finding.Providers.Count == 0)
                text.Append(" no bundled provider\n");
            else
            {
                text.Append('\n');
                foreach (var provider in finding.Providers)
                    text.Append($"    {provider.Match}: {provider.Module} [{(provider.Library.Length == 0 ? "unknown" : provider.Library)}]\n");
            }
        }
        return text.ToString().TrimEnd('\n');
    }
}
