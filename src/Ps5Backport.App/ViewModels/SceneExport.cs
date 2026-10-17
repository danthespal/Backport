using System.Globalization;
using System.Security;
using System.Text;
using Avalonia;
using Ps5Backport.Core;

namespace Ps5Backport.App.ViewModels;

/// <summary>The theme colours an SVG export uses (CSS colours, e.g. "#1e1e2e"), and the mono font family.</summary>
public sealed record SvgPalette(string Canvas, string Card, string Header, string Hairline, string Ink, string Muted, string Cyan,
                                string Magenta, string Warn, string Error, string Ok, string Font);

/// <summary>Exports of a <see cref="GraphScene"/>: plain text (block by block) and SVG (the whole scene, as the canvas draws it).</summary>
public static class SceneExport
{
    /// <summary>
    /// The scene as text: <paramref name="title"/> lines as ; comments, then each block in address order with its lines
    /// (address, instruction, tags after ;) and the blocks it leads to.
    /// </summary>
    public static string ToText(this GraphScene scene, IEnumerable<string> title)
    {
        var sb = new StringBuilder();
        foreach (string t in title)
            sb.Append("; ").AppendLine(t);
        int pad = scene.Blocks.SelectMany(b => b.Lines).Select(l => l.Address.Length).DefaultIfEmpty(0).Max();
        var headers = scene.Blocks.ToDictionary(b => b.Start, b => b.Header);
        foreach (var b in scene.Blocks.OrderBy(b => b.Start))
        {
            sb.AppendLine().Append(b.Header).AppendLine(":");
            foreach (var l in b.Lines)
            {
                var line = new StringBuilder("    ").Append(l.Address.PadRight(pad)).Append("  ").Append(l.Text);
                if (l.Tags is { Count: > 0 } tags)
                    line.Append("    ; ").Append(string.Join(" · ", tags.Select(t => t.Text)));
                sb.AppendLine(line.ToString().TrimEnd());
            }
            if (scene.Next.TryGetValue(b.Start, out var next) && next.Count > 0)
                sb.Append("    ; → ").AppendLine(string.Join(", ", next.Select(n => headers.GetValueOrDefault(n, $"0x{n:x}"))));
        }
        return sb.ToString();
    }

    /// <summary>The whole scene as SVG at 1:1, drawn like <c>GraphCanvas</c> (edges, blocks, change fills, tags).</summary>
    public static string ToSvg(this GraphScene scene, SvgPalette p, string title)
    {
        string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        string X(string s) => SecurityElement.Escape(s);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(scene.Size.Width)}\" height=\"{F(scene.Size.Height)}\" viewBox=\"0 0 {F(scene.Size.Width)} {F(scene.Size.Height)}\" font-family=\"{X(p.Font)}, monospace\" font-size=\"12\">\n");
        sb.Append("<title>").Append(X(title)).Append("</title>\n");
        sb.Append(CultureInfo.InvariantCulture, $"<rect width=\"100%\" height=\"100%\" fill=\"{p.Canvas}\"/>\n");

        foreach (var e in scene.Edges)
        {
            string colour = e.Kind switch { EdgeKind.True => p.Ok, EdgeKind.False => p.Error, EdgeKind.Case => p.Cyan, _ => p.Muted };
            string points = string.Join(" ", e.Points.Select(pt => $"{F(pt.X)},{F(pt.Y)}"));
            sb.Append(CultureInfo.InvariantCulture, $"<polyline points=\"{points}\" fill=\"none\" stroke=\"{colour}\" stroke-width=\"1.4\"{(e.Back ? " stroke-dasharray=\"4 3\"" : "")}/>\n");
            if (e.Points.Count >= 2)
                sb.Append(CultureInfo.InvariantCulture, $"<polygon points=\"{Arrow(e.Points[^2], e.Points[^1], 7, F)}\" fill=\"{colour}\"/>\n");
        }

        double addressWidth = scene.AddressChars * GraphScene.CharWidth;
        foreach (var b in scene.Blocks)
        {
            var r = b.Bounds;
            string outline = b.Accent switch
            {
                BlockAccent.Patched => p.Magenta, BlockAccent.Stub => p.Warn, BlockAccent.Removed or BlockAccent.Crash => p.Error,
                BlockAccent.Current => p.Cyan, _ => p.Hairline,
            };
            double stroke = b.Accent switch { BlockAccent.None => 1.1, BlockAccent.Current => 3.4, _ => 2.2 };
            sb.Append(CultureInfo.InvariantCulture, $"<g><rect x=\"{F(r.X)}\" y=\"{F(r.Y)}\" width=\"{F(r.Width)}\" height=\"{F(r.Height)}\" fill=\"{p.Card}\" stroke=\"{outline}\" stroke-width=\"{F(stroke)}\"/>\n");
            sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"{F(r.X)}\" y=\"{F(r.Y)}\" width=\"{F(r.Width)}\" height=\"{F(GraphScene.HeaderHeight - 4)}\" fill=\"{p.Header}\"/>\n");
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{F(r.X + GraphScene.Padding)}\" y=\"{F(r.Y + 15)}\" fill=\"{p.Cyan}\">{X(b.Header)}</text>\n");
            double y = r.Y + GraphScene.HeaderHeight;
            foreach (var line in b.Lines)
            {
                (string Colour, double Opacity)? fill = line.Style switch
                {
                    LineStyle.Patched => (p.Magenta, 0.24), LineStyle.Stub => (p.Warn, 0.2), LineStyle.Removed => (p.Error, 0.18),
                    LineStyle.Crash => (p.Error, 0.4), LineStyle.Pattern => (p.Warn, 0.14), _ => null,
                };
                if (fill is var (colour, opacity))
                    sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"{F(r.X + 2)}\" y=\"{F(y)}\" width=\"{F(r.Width - 4)}\" height=\"{F(GraphScene.LineHeight)}\" fill=\"{colour}\" fill-opacity=\"{F(opacity)}\"/>\n");
                double x = r.X + GraphScene.Padding, baseline = y + 12.5;
                string fore = line.Style switch { LineStyle.Call => p.Cyan, LineStyle.Was => p.Muted, _ => p.Ink };
                if (line.Address.Length > 0)
                    sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{F(x)}\" y=\"{F(baseline)}\" fill=\"{p.Muted}\">{X(line.Address)}</text>");
                sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{F(x + addressWidth)}\" y=\"{F(baseline)}\" fill=\"{fore}\" xml:space=\"preserve\"{(line.Style == LineStyle.Was ? " text-decoration=\"line-through\"" : "")}>{X(line.Text)}</text>\n");
                double tagX = x + addressWidth + (line.Text.Length + 2) * GraphScene.CharWidth;
                foreach (var tag in line.Tags ?? [])
                {
                    string tone = tag.Kind switch
                    {
                        TagKind.Fakelib => p.Cyan, TagKind.Redirect or TagKind.Unknown or TagKind.Pattern => p.Warn, TagKind.Missing => p.Error,
                        TagKind.Data => p.Ok, _ => p.Muted,
                    };
                    double w = tag.Text.Length * GraphScene.CharWidth;
                    sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"{F(tagX - 3)}\" y=\"{F(y + 1)}\" width=\"{F(w + 6)}\" height=\"{F(GraphScene.LineHeight - 2)}\" fill=\"none\" stroke=\"{tone}\"/>");
                    sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{F(tagX)}\" y=\"{F(baseline)}\" fill=\"{tone}\" xml:space=\"preserve\">{X(tag.Text)}</text>\n");
                    tagX += w + 3 * GraphScene.CharWidth;
                }
                y += GraphScene.LineHeight;
            }
            sb.Append("</g>\n");
        }
        return sb.Append("</svg>\n").ToString();
    }

    static string Arrow(Point from, Point tip, double size, Func<double, string> f)
    {
        var d = tip - from;
        double len = Math.Sqrt(d.X * d.X + d.Y * d.Y);
        if (len < 0.001)
            return $"{f(tip.X)},{f(tip.Y)}";
        var u = new Point(d.X / len, d.Y / len);
        var n = new Point(-u.Y, u.X);
        var back = new Point(tip.X - u.X * size, tip.Y - u.Y * size);
        return $"{f(tip.X)},{f(tip.Y)} {f(back.X + n.X * size * 0.5)},{f(back.Y + n.Y * size * 0.5)} {f(back.X - n.X * size * 0.5)},{f(back.Y - n.Y * size * 0.5)}";
    }
}
