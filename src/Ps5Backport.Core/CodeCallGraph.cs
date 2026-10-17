namespace Ps5Backport.Core;

/// <summary>
/// A node of a call neighbourhood: a function of the eboot (<paramref name="Function"/>), an import it calls
/// (<paramref name="Import"/>), or "<paramref name="More"/> more" callers/callees left out. <paramref name="Level"/>: negative
/// for callers (-1 direct), 0 for the centre, positive for callees. <paramref name="Key"/> is unique within the neighbourhood.
/// </summary>
public sealed record CallNode(ulong Key, int Level, ulong? Function = null, Import? Import = null, int More = 0);

/// <summary>The functions around one: who calls it, what it calls; edges go from caller to callee (indexes into Nodes).</summary>
public sealed record CallNeighbourhood(IReadOnlyList<CallNode> Nodes, IReadOnlyList<(int From, int To)> Edges);

public sealed partial class CallIndex
{
    /// <summary>At most this many callers (callees, imports) per node at the first level, and at the second.</summary>
    public const int FirstLevel = 10, SecondLevel = 5;

    Dictionary<ulong, IReadOnlyList<ulong>>? _callees;
    Dictionary<ulong, IReadOnlyList<string>>? _importsOf;

    /// <summary>{function start: the functions it calls or tail-jumps to}, the reverse of <see cref="Callers"/>.</summary>
    public IReadOnlyDictionary<ulong, IReadOnlyList<ulong>> Callees => LazyInitializer.EnsureInitialized(ref _callees, () => Invert(Callers));

    /// <summary>{function start: NIDs of the imports it calls}, the reverse of <see cref="ImportCallers"/>.</summary>
    public IReadOnlyDictionary<ulong, IReadOnlyList<string>> ImportsOf => LazyInitializer.EnsureInitialized(ref _importsOf, () => Invert(ImportCallers));

    static Dictionary<ulong, IReadOnlyList<T>> Invert<T>(IReadOnlyDictionary<T, IReadOnlyList<ulong>> map) where T : notnull =>
        map.SelectMany(p => p.Value.Select(caller => (Caller: caller, p.Key)))
            .GroupBy(e => e.Caller)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<T>)[.. g.Select(e => e.Key).Distinct().OrderBy(k => k)]);

    /// <summary>
    /// The call neighbourhood of <paramref name="center"/> up to <paramref name="depth"/> levels each way: callers above,
    /// callees and the imports it calls below (imports only for the centre). Each node lists at most
    /// <see cref="FirstLevel"/> (then <see cref="SecondLevel"/>) callers, callees and imports; the rest become one "more" node.
    /// A function already in the neighbourhood is linked again, not repeated.
    /// </summary>
    public CallNeighbourhood Neighbourhood(CodeImage image, ulong center, int depth = 2)
    {
        var nodes = new List<CallNode>();
        var index = new Dictionary<ulong, int>();
        var edges = new HashSet<(int, int)>();
        var imports = image.Imports.DistinctBy(i => i.Nid).ToDictionary(i => i.Nid, StringComparer.Ordinal);

        (int Index, bool New) Add(CallNode node)
        {
            if (index.TryGetValue(node.Key, out int i))
                return (i, false);
            index[node.Key] = nodes.Count;
            nodes.Add(node);
            return (nodes.Count - 1, true);
        }
        int More(int count, int level) => Add(new CallNode(ulong.MaxValue - (ulong)nodes.Count, level, More: count)).Index;

        void Walk(int at, ulong f, int level, bool up)
        {
            int limit = Math.Abs(level) == 1 ? FirstLevel : SecondLevel;
            var next = (up ? Callers : Callees).GetValueOrDefault(f, []).Where(x => x != f).ToList();
            foreach (ulong x in next.Take(limit))
            {
                var (i, added) = Add(new CallNode(x, level, Function: x));
                edges.Add(up ? (i, at) : (at, i));
                if (added && Math.Abs(level) < depth)
                    Walk(i, x, level + (up ? -1 : 1), up);
            }
            if (next.Count > limit)
            {
                int m = More(next.Count - limit, level);
                edges.Add(up ? (m, at) : (at, m));
            }
        }

        int c = Add(new CallNode(center, 0, Function: center)).Index;
        Walk(c, center, -1, up: true);
        Walk(c, center, 1, up: false);
        var called = ImportsOf.GetValueOrDefault(center, []).Select(n => imports.GetValueOrDefault(n)).OfType<Import>().ToList();
        foreach (var imp in called.Take(FirstLevel))
            edges.Add((c, Add(new CallNode(imp.GotVaddr, 1, Import: imp)).Index));
        if (called.Count > FirstLevel)
            edges.Add((c, More(called.Count - FirstLevel, 1)));
        return new CallNeighbourhood(nodes, [.. edges.Order()]);
    }
}
