using Avalonia;
using Microsoft.Msagl.Core.Geometry.Curves;
using Microsoft.Msagl.Core.Layout;
using Microsoft.Msagl.Layout.Layered;
using Microsoft.Msagl.Miscellaneous;
using Ps5Backport.Core;
using MsaglPoint = Microsoft.Msagl.Core.Geometry.Point;

namespace Ps5Backport.App.ViewModels;

/// <summary>Patched/Stub: backport code that differs from the original; Was: the original code under it (overlay);
/// Removed: original code the backport changed (side by side).</summary>
public enum LineStyle { Code, Call, Patched, Stub, Was, Removed, Crash, Pattern }

/// <summary>Block outline: which kind of change it holds; Current: the function a call graph is centred on.</summary>
public enum BlockAccent { None, Patched, Stub, Removed, Crash, Current }

/// <summary>Fakelib: the call goes to a fakelib library; Redirect: the backport sends it elsewhere; Rule: a data/fixes.json rule
/// covers it; System: an adapter's jump through the GOT, which still reaches the system function; Switch: a switch-table jump;
/// Missing/Unknown: the import does not resolve on the checked firmware (exact list / lower-bound list); Data: the string, pointer
/// or value a [rip+X] operand reaches.</summary>
public enum TagKind { Fakelib, Redirect, Rule, System, Switch, Missing, Unknown, Pattern, Data }

public sealed record LineTag(string Text, TagKind Kind);

public sealed record SceneLine(string Address, string Text, LineStyle Style, ulong? Target, IReadOnlyList<LineTag>? Tags = null);
/// <summary>A block or call-graph node before layout: <paramref name="Start"/> identifies it (a block's first address, a
/// function's start); <paramref name="Accent"/> overrides the outline the lines' changes would give.</summary>
public sealed record SceneNode(ulong Start, string Header, IReadOnlyList<SceneLine> Lines, BlockAccent? Accent = null);
/// <summary>A drawn block; <paramref name="Start"/>: its first instruction's address (blocks are matched by it across canvases).</summary>
public sealed record SceneBlock(Rect Bounds, string Header, IReadOnlyList<SceneLine> Lines, BlockAccent Accent, ulong Start);
/// <summary>A drawn edge from the block starting at <paramref name="From"/> to the one at <paramref name="To"/>.</summary>
public sealed record SceneEdge(IReadOnlyList<Point> Points, EdgeKind Kind, bool Back, ulong From, ulong To)
{
    public Rect Bounds { get; } = Points.Count == 0 ? default
        : new Rect(new Point(Points.Min(p => p.X), Points.Min(p => p.Y)), new Point(Points.Max(p => p.X), Points.Max(p => p.Y)));
}

/// <summary>A function graph laid out by MSAGL's layered (Sugiyama) layout, in world coordinates with y down; or, as a listing,
/// its blocks stacked in address order with no edges (IDA's text view); or a call graph of functions.</summary>
public sealed class GraphScene
{
    public const double LineHeight = 16, HeaderHeight = 22, Padding = 8, Margin = 40, MinBlockWidth = 140, ListingGap = 6;
    /// <summary>Width of one mono-font character at 12 px; the canvas measures it on the UI thread.</summary>
    public static double CharWidth { get; set; } = 7.2;

    public required IReadOnlyList<SceneBlock> Blocks { get; init; }
    public required IReadOnlyList<SceneEdge> Edges { get; init; }
    public required Size Size { get; init; }
    /// <summary>Where a new view starts: the requested line, else the first changed line, else the top of the first block.</summary>
    public required Point Focus { get; init; }
    /// <summary>Characters of the address column (the text column starts after it).</summary>
    public required int AddressChars { get; init; }
    /// <summary>A listing (blocks one under the other): the wheel scrolls instead of zooming.</summary>
    public bool IsListing { get; init; }
    /// <summary>{block start: starts of its successors}, also for a listing (which draws no edges).</summary>
    public required IReadOnlyDictionary<ulong, IReadOnlyList<ulong>> Next { get; init; }

    /// <summary>The blocks on some path from the block at <paramref name="from"/> to the one at <paramref name="to"/> (both
    /// included; empty when there is none): reachable from the first and reaching the second.</summary>
    public HashSet<ulong> PathBlocks(ulong from, ulong to)
    {
        var forward = Reach(from, b => Next.GetValueOrDefault(b, []));
        if (!forward.Contains(to))
            return [];
        var previous = Next.SelectMany(n => n.Value.Select(t => (From: n.Key, To: t))).ToLookup(e => e.To, e => e.From);
        var backward = Reach(to, b => previous[b]);
        forward.IntersectWith(backward);
        return forward;
    }

    static HashSet<ulong> Reach(ulong start, Func<ulong, IEnumerable<ulong>> next)
    {
        var seen = new HashSet<ulong> { start };
        var work = new Stack<ulong>([start]);
        while (work.TryPop(out ulong b))
            foreach (ulong n in next(b))
                if (seen.Add(n))
                    work.Push(n);
        return seen;
    }

    static Dictionary<ulong, IReadOnlyList<ulong>> Successors(IReadOnlyList<SceneNode> nodes, IEnumerable<(int From, int To)> edges) =>
        edges.GroupBy(e => nodes[e.From].Start)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ulong>)[.. g.Select(e => nodes[e.To].Start).Distinct()]);

    /// <summary>The blocks of a function graph as nodes: the function name, then loc_ headers.</summary>
    static List<SceneNode> Nodes(FunctionGraph graph, string name, Func<GraphBlock, IReadOnlyList<SceneLine>> lines) =>
        [.. graph.Blocks.Select((b, i) => new SceneNode(b.Start, i == 0 ? name : $"loc_{b.Start:x}", lines(b)))];

    /// <param name="lines">The lines of one block (styles, "was" lines and tags are the caller's choice).</param>
    /// <param name="focusAt">The line to start the view at (e.g. the call the Imports tab asked about), when it is in the graph.</param>
    public static GraphScene Build(FunctionGraph graph, string name, Func<GraphBlock, IReadOnlyList<SceneLine>> lines, ulong? focusAt = null) =>
        Layout(Nodes(graph, name, lines), [.. graph.Edges.Select(e => (e.From, e.To, e.Kind))], blocks => FocusPoint(blocks, focusAt));

    /// <summary>A call graph (callers above, callees below; edges from caller to callee); the view starts at <paramref name="center"/>.</summary>
    public static GraphScene Calls(IReadOnlyList<SceneNode> nodes, IReadOnlyList<(int From, int To)> edges, ulong center) =>
        Layout(nodes, [.. edges.Select(e => (e.From, e.To, EdgeKind.Jump))],
               blocks => blocks.FirstOrDefault(b => b.Start == center)?.Bounds.TopLeft ?? new Point(Margin, Margin));

    /// <summary>MSAGL's layered layout of <paramref name="nodes"/>; <paramref name="focus"/> picks where a new view starts.</summary>
    static GraphScene Layout(IReadOnlyList<SceneNode> nodes, IReadOnlyList<(int From, int To, EdgeKind Kind)> edgeList, Func<List<SceneBlock>, Point> focus)
    {
        int addressChars = AddressColumn([.. nodes.Select(n => n.Lines)]);
        var g = new GeometryGraph();
        var msaglNodes = new List<Node>();
        for (int i = 0; i < nodes.Count; i++)
        {
            var size = BlockSize(nodes[i], addressChars);
            var node = new Node(CurveFactory.CreateRectangle(size.Width, size.Height, new MsaglPoint()), i);
            msaglNodes.Add(node);
            g.Nodes.Add(node);
        }
        var msaglEdges = edgeList.Select(e => new Edge(msaglNodes[e.From], msaglNodes[e.To])).ToList();
        foreach (var e in msaglEdges)
            g.Edges.Add(e);
        var settings = new SugiyamaLayoutSettings { NodeSeparation = 30, LayerSeparation = 48 };
        settings.EdgeRoutingSettings.EdgeRoutingMode = Microsoft.Msagl.Core.Routing.EdgeRoutingMode.SugiyamaSplines;
        LayoutHelpers.CalculateLayout(g, settings, null);

        var box = g.BoundingBox;
        Point World(MsaglPoint p) => new(p.X - box.Left + Margin, box.Top - p.Y + Margin);   // MSAGL's y points up
        var blocks = new List<SceneBlock>();
        for (int i = 0; i < msaglNodes.Count; i++)
        {
            var b = msaglNodes[i].BoundingBox;
            blocks.Add(Block(nodes[i], new Rect(World(new MsaglPoint(b.Left, b.Top)), World(new MsaglPoint(b.Right, b.Bottom)))));
        }
        var edges = new List<SceneEdge>();
        for (int i = 0; i < edgeList.Count; i++)
        {
            var e = edgeList[i];
            var points = Sample(msaglEdges[i].Curve).Select(World).ToList();
            if (points.Count < 2)
                points = [blocks[e.From].Bounds.Center, blocks[e.To].Bounds.Center];
            edges.Add(new SceneEdge(points, e.Kind, blocks[e.To].Bounds.Top <= blocks[e.From].Bounds.Top, nodes[e.From].Start, nodes[e.To].Start));
        }
        return new GraphScene
        {
            Blocks = blocks, Edges = edges, Focus = focus(blocks), AddressChars = addressChars,
            Next = Successors(nodes, edgeList.Select(e => (e.From, e.To))),
            Size = new Size(box.Width + 2 * Margin, box.Height + 2 * Margin),
        };
    }

    /// <summary>The same blocks as a listing: one under the other in address order, all as wide as the widest.</summary>
    public static GraphScene Listing(FunctionGraph graph, string name, Func<GraphBlock, IReadOnlyList<SceneLine>> lines, ulong? focusAt = null)
    {
        var nodes = Nodes(graph, name, lines);
        int addressChars = AddressColumn([.. nodes.Select(n => n.Lines)]);
        var sizes = nodes.Select(n => BlockSize(n, addressChars)).ToList();
        double width = sizes.Select(s => s.Width).DefaultIfEmpty(MinBlockWidth).Max();
        var blocks = new List<SceneBlock>();
        double y = Margin;
        foreach (int i in Enumerable.Range(0, nodes.Count).OrderBy(i => nodes[i].Start))
        {
            blocks.Add(Block(nodes[i], new Rect(Margin, y, width, sizes[i].Height)));
            y += sizes[i].Height + ListingGap;
        }
        return new GraphScene
        {
            Blocks = blocks, Edges = [], Focus = FocusPoint(blocks, focusAt), AddressChars = addressChars, IsListing = true,
            Next = Successors(nodes, graph.Edges.Select(e => (e.From, e.To))),
            Size = new Size(width + 2 * Margin, y - ListingGap + Margin),
        };
    }

    static int AddressColumn(List<IReadOnlyList<SceneLine>> blockLines) =>
        blockLines.SelectMany(l => l).Select(l => l.Address.Length).DefaultIfEmpty(6).Max() + 2;

    static Size BlockSize(SceneNode node, int addressChars)
    {
        int chars = node.Lines.Select(l => l.Text.Length + (l.Tags?.Sum(t => t.Text.Length + 3) ?? 0)).DefaultIfEmpty(0).Max();
        double width = Math.Max((addressChars + chars) * CharWidth, node.Header.Length * CharWidth);
        return new Size(Math.Max(MinBlockWidth, 2 * Padding + width), HeaderHeight + node.Lines.Count * LineHeight + Padding);
    }

    /// <summary><paramref name="node"/> at <paramref name="bounds"/>, outlined by its own accent or else by its changes.</summary>
    static SceneBlock Block(SceneNode node, Rect bounds)
    {
        var accent = node.Accent ?? node.Lines.Select(l => l.Style).Aggregate(BlockAccent.None, (a, s) => s switch
        {
            LineStyle.Patched => BlockAccent.Patched,
            LineStyle.Stub or LineStyle.Pattern when a == BlockAccent.None => BlockAccent.Stub,
            LineStyle.Removed when a == BlockAccent.None => BlockAccent.Removed,
            LineStyle.Crash => BlockAccent.Crash,
            _ => a,
        });
        return new SceneBlock(bounds, node.Header, node.Lines, accent, node.Start);
    }

    /// <summary>The requested line, else the first block's crash line or first change, else the top of the first block.</summary>
    static Point FocusPoint(List<SceneBlock> blocks, ulong? focusAt)
    {
        // the first block with a line index() picks: that line
        Point? First(Func<List<SceneLine>, int> index)
        {
            foreach (var b in blocks)
                if (index([.. b.Lines]) is var k and >= 0)
                    return new Point(b.Bounds.Left, b.Bounds.Top + HeaderHeight + k * LineHeight);
            return null;
        }
        string? requested = focusAt is ulong at ? $"{at:x}" : null;
        return (requested is null ? null : First(l => l.FindIndex(x => x.Address == requested)))
            ?? First(l => l.FindIndex(x => x.Style is LineStyle.Crash) is var k and >= 0 ? k
                : l.FindIndex(x => x.Style is LineStyle.Patched or LineStyle.Stub or LineStyle.Removed or LineStyle.Pattern))
            ?? (blocks.Count == 0 ? new Point(Margin, Margin) : blocks[0].Bounds.TopLeft);
    }

    static IEnumerable<MsaglPoint> Sample(ICurve? curve)
    {
        if (curve is null)
            yield break;
        IEnumerable<ICurve> segments = curve is Curve c ? c.Segments : [curve];
        bool first = true;
        foreach (var s in segments)
        {
            int steps = s is LineSegment ? 1 : 12;
            for (int k = first ? 0 : 1; k <= steps; k++)
                yield return s[s.ParStart + (s.ParEnd - s.ParStart) * k / steps];
            first = false;
        }
    }
}
