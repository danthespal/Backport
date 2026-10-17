using Iced.Intel;

namespace Ps5Backport.Core;

/// <summary>
/// How a function of one build was paired with one of another (strongest first): its export name, the same code (bytes
/// with addresses masked), the same strings and imports, the same instruction sequence, the same shape (instructions,
/// blocks, branches, calls), the same fingerprint among the callers/callees of a matched pair, the only unmatched
/// caller/callee left there, or its place between two matched functions in address order.
/// </summary>
public enum MatchHow { Name, SameCode, SameReferences, SameInstructions, SameShape, CallGraph, CallGraphOnly, AddressOrder }

/// <summary>A function of the old build paired with one of the new; Identical: the same code (it may have moved);
/// Similarity: 1 when identical, else how alike their instruction sequences are (0..1).</summary>
public sealed record FunctionMatch(FunctionRange Old, FunctionRange New, MatchHow How, bool Identical, double Similarity);

/// <summary>Two builds of one game compared function by function (BinDiff-style; FINDINGS 6o).</summary>
public sealed class VersionDiff
{
    public required IReadOnlyList<FunctionMatch> Matches { get; init; }
    /// <summary>Functions of the old build with no partner (removed, or changed beyond recognition).</summary>
    public required IReadOnlyList<FunctionRange> Removed { get; init; }
    /// <summary>Functions of the new build with no partner.</summary>
    public required IReadOnlyList<FunctionRange> Added { get; init; }
    Dictionary<ulong, FunctionMatch>? _byNew, _byOld;

    public FunctionMatch? OfNew(ulong start) => (_byNew ??= Matches.ToDictionary(m => m.New.Start)).GetValueOrDefault(start);
    public FunctionMatch? OfOld(ulong start) => (_byOld ??= Matches.ToDictionary(m => m.Old.Start)).GetValueOrDefault(start);
    public int Identical => Matches.Count(m => m.Identical);
    public int Changed => Matches.Count(m => !m.Identical);
}

public static partial class VersionMatch
{
    /// <summary>What matching uses of one function, from one decode of it.</summary>
    internal sealed class Print
    {
        public required FunctionRange Range;
        public ulong Code, Mnemonics, Refs;
        /// <summary>Hashes of the strings and imports it references, sorted.</summary>
        public ulong[] RefSet = [];
        public int Instructions, Blocks, Branches, Calls, RefCount;
        public string? Name;
        public int[] Callees = [];
        public List<int> Callers = [];
        /// <summary>Instructions, blocks, branches and calls packed into one key.</summary>
        public ulong Shape => ((ulong)(uint)Instructions << 32) ^ ((ulong)(ushort)Blocks << 16) ^ ((ulong)(byte)Branches << 8) ^ (byte)Calls;
    }

    const ulong FnvBasis = 14695981039346656037, FnvPrime = 1099511628211;
    static ulong Fnv(ulong h, ulong v)
    {
        for (int i = 0; i < 8; i++, v >>= 8)
            h = (h ^ (v & 0xff)) * FnvPrime;
        return h;
    }

    /// <summary>
    /// Pairs the functions of <paramref name="older"/> and <paramref name="newer"/>: unique fingerprints first (name, same
    /// code, same references, same instructions, same shape), then outwards along the call graph from every pair, then the
    /// gaps between pairs in address order, until nothing new is found. Functions left over are removed / added.
    /// </summary>
    public static VersionDiff Compare(CodeImage older, CodeImage newer, CancellationToken cancel = default)
    {
        var a = Prints(older, cancel);
        var b = Prints(newer, cancel);
        var m = new Matcher(a, b);
        m.Unique(MatchHow.Name, p => p.Name is { } n ? Fnv(FnvBasis, (ulong)n.GetHashCode()) ^ (ulong)n.Length : null, Enumerable.Range(0, a.Length), Enumerable.Range(0, b.Length));
        m.Unique(MatchHow.SameCode, p => p.Code, m.FreeA(), m.FreeB());
        m.Unique(MatchHow.SameReferences, p => p.RefCount >= 2 ? p.Refs : null, m.FreeA(), m.FreeB());
        m.Unique(MatchHow.SameInstructions, p => p.Instructions >= 8 ? p.Mnemonics : null, m.FreeA(), m.FreeB());
        m.Unique(MatchHow.SameShape, p => p.Blocks >= 5 ? p.Shape : null, m.FreeA(), m.FreeB());
        for (int round = 0; round < 8; round++)
        {
            cancel.ThrowIfCancellationRequested();
            int before = m.Count;
            m.Propagate();
            m.FillAddressGaps();
            if (m.Count == before)
                break;
        }
        return m.Result(older, newer);
    }

    /// <summary>Fingerprints of every function of <paramref name="image"/>, in its function order.</summary>
    internal static Print[] Prints(CodeImage image, CancellationToken cancel)
    {
        var functions = image.Functions;
        var prints = new Print[functions.Count];
        var index = new Dictionary<ulong, int>(functions.Count);
        for (int i = 0; i < functions.Count; i++)
            index[functions[i].Start] = i;
        var data = image.Elf.Data;
        Parallel.For(0, functions.Count, new ParallelOptions { CancellationToken = cancel }, i =>
        {
            var f = functions[i];
            var p = new Print { Range = f };
            string name = image.FunctionName(f.Start);
            p.Name = name.StartsWith("sub_", StringComparison.Ordinal) || name == "entry" ? null : name;
            long off = image.Elf.Off(f.Start);
            int length = (int)Math.Min(f.Size, (ulong)(data.Length - off));
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(data, (int)off, length), f.Start);
            ulong end = f.Start + (ulong)length, code = FnvBasis, mnemonics = FnvBasis;
            var refs = new List<ulong>();
            var callees = new List<int>();
            var leaders = new HashSet<ulong>();
            Span<byte> bytes = stackalloc byte[16];
            while (decoder.IP < end)
            {
                decoder.Decode(out var ins);
                var offsets = decoder.GetConstantOffsets(ins);
                data.AsSpan((int)(off + (long)(ins.IP - f.Start)), ins.Length).CopyTo(bytes);
                bool branch = ins.IsCallNear || ins.IsJmpShortOrNear || ins.IsJccShortOrNear;
                // addresses move between builds: mask rip-relative displacements and branch targets
                if (ins.IsIPRelativeMemoryOperand && offsets.HasDisplacement)
                    bytes.Slice(offsets.DisplacementOffset, offsets.DisplacementSize).Clear();
                if (branch && offsets.HasImmediate)
                    bytes.Slice(offsets.ImmediateOffset, offsets.ImmediateSize).Clear();
                foreach (byte x in bytes[..ins.Length])
                    code = (code ^ x) * FnvPrime;
                mnemonics = Fnv(mnemonics, (ulong)ins.Mnemonic);
                p.Instructions++;
                if (ins.IsJccShortOrNear)
                {
                    p.Branches++;
                    leaders.Add(ins.NearBranchTarget);
                    leaders.Add(ins.NextIP);
                }
                else if (ins.IsJmpShortOrNear && f.Contains(ins.NearBranchTarget))
                    leaders.Add(ins.NearBranchTarget);
                ulong? target = ins.IsCallNear || ins.IsJmpShortOrNear && !f.Contains(ins.NearBranchTarget) ? ins.NearBranchTarget
                    : ins.FlowControl is FlowControl.IndirectCall or FlowControl.IndirectBranch && ins.IsIPRelativeMemoryOperand ? ins.IPRelativeMemoryAddress : null;
                if (ins.IsCallNear || ins.FlowControl == FlowControl.IndirectCall)
                    p.Calls++;
                if (target is ulong t)
                {
                    if (image.ImportAt(t) is { } imp)
                        refs.Add(Fnv(FnvBasis, (ulong)imp.Nid.GetHashCode()) | 1);
                    else if (index.TryGetValue(t, out int callee) && callee != i)
                        callees.Add(callee);
                }
                else if (ins.Mnemonic == Mnemonic.Lea && ins.IsIPRelativeMemoryOperand && image.StringAt(ins.IPRelativeMemoryAddress) is { } s)
                    refs.Add(Fnv(FnvBasis, (ulong)s.Text.GetHashCode()) & ~1UL);
            }
            leaders.RemoveWhere(l => !f.Contains(l) || l == f.Start);
            p.Blocks = leaders.Count + 1;
            p.Code = Fnv(code, (ulong)length);
            p.Mnemonics = mnemonics;
            var distinct = refs.Distinct().Order().ToArray();
            p.RefCount = distinct.Length;
            p.RefSet = distinct;
            p.Refs = distinct.Aggregate(FnvBasis, Fnv);
            p.Callees = [.. callees.Distinct()];
            prints[i] = p;
        });
        for (int i = 0; i < prints.Length; i++)
            foreach (int c in prints[i].Callees)
                prints[c].Callers.Add(i);
        return prints;
    }

    sealed class Matcher(Print[] a, Print[] b)
    {
        readonly int[] _ab = Enumerable.Repeat(-1, a.Length).ToArray(), _ba = Enumerable.Repeat(-1, b.Length).ToArray();
        readonly MatchHow[] _how = new MatchHow[a.Length];
        readonly Queue<int> _fresh = new();
        public int Count { get; private set; }

        public IEnumerable<int> FreeA() => Enumerable.Range(0, a.Length).Where(i => _ab[i] < 0);
        public IEnumerable<int> FreeB() => Enumerable.Range(0, b.Length).Where(j => _ba[j] < 0);

        void Pair(int i, int j, MatchHow how)
        {
            _ab[i] = j;
            _ba[j] = i;
            _how[i] = how;
            Count++;
            _fresh.Enqueue(i);
        }

        /// <summary>Pairs the candidates whose key (non-null) occurs exactly once on each side.</summary>
        public int Unique(MatchHow how, Func<Print, ulong?> key, IEnumerable<int> left, IEnumerable<int> right)
        {
            var groups = new Dictionary<ulong, (int A, int B, int CountA, int CountB)>();
            foreach (int i in left)
                if (_ab[i] < 0 && key(a[i]) is ulong k)
                    groups[k] = groups.TryGetValue(k, out var g) ? g with { CountA = g.CountA + 1 } : (i, -1, 1, 0);
            foreach (int j in right)
                if (_ba[j] < 0 && key(b[j]) is ulong k && groups.TryGetValue(k, out var g))
                    groups[k] = g with { B = j, CountB = g.CountB + 1 };
            int added = 0;
            foreach (var g in groups.Values)
                if (g.CountA == 1 && g.CountB == 1)
                {
                    Pair(g.A, g.B, how);
                    added++;
                }
            return added;
        }

        /// <summary>From each new pair, the unmatched callees and callers on both sides: unique fingerprints there, then a
        /// lone pair of similar size.</summary>
        public void Propagate()
        {
            if (_fresh.Count == 0)   // after a gap fill: start again from every pair
                for (int i = 0; i < a.Length; i++)
                    if (_ab[i] >= 0)
                        _fresh.Enqueue(i);
            while (_fresh.TryDequeue(out int i))
            {
                int j = _ab[i];
                Around(a[i].Callees, b[j].Callees);
                Around(a[i].Callers, b[j].Callers);
            }
        }

        void Around(IReadOnlyList<int> left, IReadOnlyList<int> right)
        {
            var l = left.Where(x => _ab[x] < 0).ToList();
            var r = right.Where(x => _ba[x] < 0).ToList();
            if (l.Count == 0 || r.Count == 0)
                return;
            Unique(MatchHow.CallGraph, p => p.Code, l, r);
            Unique(MatchHow.CallGraph, p => p.RefCount > 0 ? p.Refs : null, l, r);
            Unique(MatchHow.CallGraph, p => p.Mnemonics, l, r);
            Unique(MatchHow.CallGraph, p => p.Shape, l, r);
            l.RemoveAll(x => _ab[x] >= 0);
            r.RemoveAll(x => _ba[x] >= 0);
            if (l.Count == 1 && r.Count == 1 && Close(a[l[0]], b[r[0]]))
                Pair(l[0], r[0], MatchHow.CallGraphOnly);
        }

        /// <summary>Alike enough for a pairing by position alone: similar size, and when either references strings or
        /// imports, at least 30 % of them shared.</summary>
        static bool Close(Print x, Print y)
        {
            if (Math.Max(x.Instructions, y.Instructions) > 2 * Math.Min(x.Instructions, y.Instructions) + 2)
                return false;
            if (x.RefCount == 0 && y.RefCount == 0)
                return true;
            int shared = x.RefSet.Intersect(y.RefSet).Count();
            return shared * 10 >= 3 * (x.RefCount + y.RefCount - shared);
        }

        /// <summary>
        /// The gaps between anchors, the longest chain of pairs in address order on both sides (a few pairs out of order
        /// must not hide the thousands around them), are filled by <see cref="Gap"/>.
        /// </summary>
        public void FillAddressGaps()
        {
            int lastA = -1, lastB = -1;
            foreach (int i in InOrder().Append(a.Length))
            {
                int nextB = i < a.Length ? _ab[i] : b.Length;
                var left = Enumerable.Range(lastA + 1, i - lastA - 1).Where(x => _ab[x] < 0).ToList();
                var right = Enumerable.Range(lastB + 1, nextB - lastB - 1).Where(y => _ba[y] < 0).ToList();
                if (left.Count > 0 && right.Count > 0)
                    Gap(left, right);
                (lastA, lastB) = (i, nextB);
            }
        }

        /// <summary>The longest increasing subsequence of the pairs' new positions, in old order (patience sorting).</summary>
        List<int> InOrder()
        {
            var tails = new List<int>();
            var previous = new int[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                if (_ab[i] < 0)
                    continue;
                int lo = 0, hi = tails.Count;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (_ab[tails[mid]] < _ab[i]) lo = mid + 1; else hi = mid;
                }
                previous[i] = lo > 0 ? tails[lo - 1] : -1;
                if (lo == tails.Count) tails.Add(i); else tails[lo] = i;
            }
            var chain = new List<int>();
            for (int i = tails.Count > 0 ? tails[^1] : -1; i >= 0; i = previous[i])
                chain.Add(i);
            chain.Reverse();
            return chain;
        }

        /// <summary>
        /// One gap between anchors: fingerprints unique within it; then copies of the same code paired in order (as many as
        /// the side with fewer has); then, if as many functions are left on each side, all of them in order.
        /// </summary>
        void Gap(List<int> left, List<int> right)
        {
            Unique(MatchHow.AddressOrder, p => p.Code, left, right);
            Unique(MatchHow.AddressOrder, p => p.RefCount > 0 ? p.Refs : null, left, right);
            Unique(MatchHow.AddressOrder, p => p.Instructions >= 4 ? p.Mnemonics : null, left, right);
            left.RemoveAll(x => _ab[x] >= 0);
            right.RemoveAll(y => _ba[y] >= 0);
            var copies = right.GroupBy(y => b[y].Code).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var g in left.GroupBy(x => a[x].Code).ToList())
                if (copies.TryGetValue(g.Key, out var same))
                    foreach (var (x, y) in g.Zip(same))
                        Pair(x, y, MatchHow.AddressOrder);
            left.RemoveAll(x => _ab[x] >= 0);
            right.RemoveAll(y => _ba[y] >= 0);
            if (left.Count > 0 && left.Count == right.Count && left.Zip(right).All(p => Close(a[p.First], b[p.Second])))
                foreach (var (x, y) in left.Zip(right))
                    Pair(x, y, MatchHow.AddressOrder);
        }

        public VersionDiff Result(CodeImage older, CodeImage newer)
        {
            var matches = new List<FunctionMatch>(Count);
            for (int i = 0; i < a.Length; i++)
            {
                if (_ab[i] < 0)
                    continue;
                var (x, y) = (a[i], b[_ab[i]]);
                bool identical = x.Code == y.Code;
                matches.Add(new FunctionMatch(x.Range, y.Range, _how[i], identical, identical ? 1 : Similarity(older, x.Range, newer, y.Range)));
            }
            return new VersionDiff
            {
                Matches = matches,
                Removed = [.. FreeA().Select(i => a[i].Range)],
                Added = [.. FreeB().Select(j => b[j].Range)],
            };
        }
    }

    /// <summary>An instruction's text without what moves between builds: code and data addresses, sub_/loc_ names.</summary>
    static string Normalise(string text) => Addresses().Replace(text, "#");

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(?:sub|loc)_[0-9a-f]+\b|\b[0-9A-F]{6,}h\b|\b0x[0-9a-f]{6,}\b")]
    private static partial System.Text.RegularExpressions.Regex Addresses();

    /// <summary>
    /// A line-level diff of a matched pair (BinDiff's instruction view): the old instructions with no counterpart in the new
    /// function and the new ones with none in the old, from a longest common subsequence of their texts with addresses
    /// masked. Very large pairs (over 16 M cells after the common prefix and suffix) mark their whole middle as changed.
    /// </summary>
    public static (HashSet<ulong> Removed, HashSet<ulong> Added) LineDiff(FunctionGraph older, FunctionGraph newer)
    {
        var x = older.Blocks.SelectMany(b => b.Lines).OrderBy(l => l.Address).ToList();
        var y = newer.Blocks.SelectMany(b => b.Lines).OrderBy(l => l.Address).ToList();
        var nx = x.Select(l => Normalise(l.Text)).ToArray();
        var ny = y.Select(l => Normalise(l.Text)).ToArray();
        int start = 0;
        while (start < nx.Length && start < ny.Length && nx[start] == ny[start])
            start++;
        int ex = nx.Length, ey = ny.Length;
        while (ex > start && ey > start && nx[ex - 1] == ny[ey - 1])
            (ex, ey) = (ex - 1, ey - 1);
        int n = ex - start, m = ey - start;
        var keptX = new bool[n];
        var keptY = new bool[m];
        if ((long)n * m <= 16_000_000 && n > 0 && m > 0)
        {
            var dp = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    dp[i, j] = nx[start + i] == ny[start + j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            for (int i = 0, j = 0; i < n && j < m;)
            {
                if (nx[start + i] == ny[start + j]) { keptX[i++] = true; keptY[j++] = true; }
                else if (dp[i + 1, j] >= dp[i, j + 1]) i++;
                else j++;
            }
        }
        return ([.. Enumerable.Range(0, n).Where(i => !keptX[i]).Select(i => x[start + i].Address)],
                [.. Enumerable.Range(0, m).Where(j => !keptY[j]).Select(j => y[start + j].Address)]);
    }

    /// <summary>How alike two functions' instruction sequences are: the Jaccard index of their mnemonic 3-grams.</summary>
    internal static double Similarity(CodeImage older, FunctionRange x, CodeImage newer, FunctionRange y)
    {
        static HashSet<ulong> Grams(CodeImage image, FunctionRange f)
        {
            var m = CodeImage.Decode(image.Elf, f).Select(i => (ulong)i.Mnemonic).ToList();
            var grams = new HashSet<ulong>();
            for (int k = 0; k + 2 < m.Count; k++)
                grams.Add(m[k] << 32 | m[k + 1] << 16 | m[k + 2]);
            if (m.Count < 3)
                grams.UnionWith(m);
            return grams;
        }
        var gx = Grams(older, x);
        var gy = Grams(newer, y);
        int union = gx.Union(gy).Count();
        return union == 0 ? 1 : (double)gx.Intersect(gy).Count() / union;
    }
}
