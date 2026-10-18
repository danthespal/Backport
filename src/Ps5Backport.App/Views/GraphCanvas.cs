using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Ps5Backport.App.ViewModels;
using Ps5Backport.Core;

namespace Ps5Backport.App.Views;

/// <summary>
/// Draws a <see cref="GraphScene"/>: blocks, coloured edges (green true, red false, grey jump, cyan switch case, dashed loop) and change
/// highlights. Drag pans, the wheel zooms at the pointer (scrolls a listing; Ctrl+wheel zooms it), a double-click on a line with a
/// target raises <see cref="TargetActivated"/>, a click on a word highlights it everywhere and selects its block, Shift+click on
/// another block shows the paths to it (Esc clears both), Space raises <see cref="ModeToggleRequested"/>, Alt+Left/Right and the
/// mouse's back/forward buttons raise <see cref="HistoryRequested"/>. Canvases bound to the same <see cref="GraphViewport"/> pan,
/// zoom, highlight and show paths together. The leading canvas draws a minimap when the scene does not fit (click or drag in it
/// to move there; M hides or shows it).
/// Only blocks inside the view are drawn; below <see cref="TextScale"/> lines become bars.
/// </summary>
public sealed class GraphCanvas : Control
{
    public static readonly StyledProperty<GraphScene?> SceneProperty = AvaloniaProperty.Register<GraphCanvas, GraphScene?>(nameof(Scene));
    public static readonly StyledProperty<GraphViewport?> ViewportProperty = AvaloniaProperty.Register<GraphCanvas, GraphViewport?>(nameof(Viewport));
    /// <summary>This canvas positions the shared view when its scene changes (one per viewport).</summary>
    public static readonly StyledProperty<bool> LeadsProperty = AvaloniaProperty.Register<GraphCanvas, bool>(nameof(Leads));

    const double TextScale = 0.4, MinScale = 0.02, MaxScale = 3;

    public GraphScene? Scene { get => GetValue(SceneProperty); set => SetValue(SceneProperty, value); }
    public GraphViewport? Viewport { get => GetValue(ViewportProperty); set => SetValue(ViewportProperty, value); }
    public bool Leads { get => GetValue(LeadsProperty); set => SetValue(LeadsProperty, value); }

    public event Action<ulong>? TargetActivated;
    /// <summary>Space: switch between the graph and the listing.</summary>
    public event Action? ModeToggleRequested;
    /// <summary>Back (false) or Forward (true) through the shown functions.</summary>
    public event Action<bool>? HistoryRequested;

    /// <summary>Blocks on the shown path, for the scene and ends they were computed for.</summary>
    (GraphScene? Scene, ulong? From, ulong? To, HashSet<ulong>? Blocks) _path;

    readonly Dictionary<(SceneLine, int), FormattedText> _text = [];
    Point? _drag, _press;
    /// <summary>Where the minimap was last drawn and its scales (world to minimap); null when it was not drawn.</summary>
    (Rect Area, double ScaleX, double ScaleY)? _mini;
    bool _miniDrag;

    /// <summary>Draw the minimap (leading canvas only, when the scene does not fit); M toggles.</summary>
    public bool ShowMinimap { get; set; } = true;
    const double MiniMax = 180, MiniMin = 60, MiniMinHeight = 40, ListingMiniWidth = 48, MiniMargin = 12, LegendHeight = 40;
    bool _placePending;

    public GraphCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var probe = new FormattedText("0000000000", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 12, Brushes.White);
        GraphScene.CharWidth = probe.WidthIncludingTrailingWhitespace / 10;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SceneProperty)
        {
            _text.Clear();
            if (Leads && Scene is not null)
                Place();
            InvalidateVisual();
        }
        else if (change.Property == ViewportProperty)
        {
            if (change.OldValue is GraphViewport old)
                old.Changed -= InvalidateVisual;
            if (change.NewValue is GraphViewport now)
                now.Changed += InvalidateVisual;
        }
        else if (change.Property == BoundsProperty && _placePending)
            Place();
        else if (change.Property == IsVisibleProperty && IsVisible)
            InvalidateVisual();
    }

    /// <summary>Starts a new scene's view: fits small graphs, otherwise shows its focus (the first change) at a readable size.</summary>
    void Place()
    {
        if (Scene is not { } scene || Viewport is not { } v)
            return;
        if (Bounds.Width < 1 || Bounds.Height < 1)
        {
            _placePending = true;
            return;
        }
        _placePending = false;
        double fit = Math.Min(Bounds.Width / scene.Size.Width, Bounds.Height / scene.Size.Height);
        if (fit >= 0.6)
        {
            Fit();
            return;
        }
        if (scene.IsListing)
        {
            // readable size, the left edge in view; the top of the listing, or its focus a quarter down
            bool top = scene.Blocks.Count == 0 || scene.Focus == scene.Blocks[0].Bounds.TopLeft;
            v.Set(1, 16 - GraphScene.Margin, top ? 16 - GraphScene.Margin : Bounds.Height / 4 - scene.Focus.Y);
            return;
        }
        double s = Math.Clamp(fit, 0.6, 1);
        v.Set(s, Bounds.Width * 0.15 - scene.Focus.X * s, Bounds.Height / 4 - scene.Focus.Y * s);
    }

    /// <summary>The whole graph in view.</summary>
    public void Fit()
    {
        if (Scene is not { } scene || Viewport is not { } v || Bounds.Width < 1)
            return;
        if (scene.IsListing)
        {
            // a listing fits its width, from the top
            double w = Math.Clamp(Bounds.Width / scene.Size.Width, MinScale, 1.2);
            v.Set(w, (Bounds.Width - scene.Size.Width * w) / 2, 0);
            return;
        }
        double s = Math.Clamp(Math.Min(Bounds.Width / scene.Size.Width, Bounds.Height / scene.Size.Height), MinScale, 1.2);
        v.Set(s, (Bounds.Width - scene.Size.Width * s) / 2, (Bounds.Height - scene.Size.Height * s) / 2);
    }

    public void ZoomAtCenter(double factor) => ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), factor);

    void ZoomAt(Point at, double factor)
    {
        if (Viewport is not { } v)
            return;
        double s = Math.Clamp(v.Scale * factor, MinScale, MaxScale);
        v.Set(s, at.X - (at.X - v.X) * s / v.Scale, at.Y - (at.Y - v.Y) * s / v.Scale);
    }

    // --- input ---------------------------------------------------------------------------------------
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsXButton1Pressed || properties.IsXButton2Pressed)
        {
            HistoryRequested?.Invoke(properties.IsXButton2Pressed);
            e.Handled = true;
            return;
        }
        if (!properties.IsLeftButtonPressed || Viewport is not { } v)
            return;
        if (_mini is { } mini && mini.Area.Inflate(4).Contains(e.GetPosition(this)))
        {
            _miniDrag = true;   // move the view there, and keep following the pointer while it is held
            CenterOn(e.GetPosition(this), mini, v);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (e.ClickCount == 2)
        {
            if (LineAt(e.GetPosition(this))?.Target is ulong target)
                TargetActivated?.Invoke(target);
            return;
        }
        var p = e.GetPosition(this);
        _drag = new Point(p.X - v.X, p.Y - v.Y);
        _press = p;
        e.Pointer.Capture(this);
        Focus();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_miniDrag && _mini is { } mini && Viewport is { } mv)
            CenterOn(e.GetPosition(this), mini, mv);
        else if (_drag is { } d && Viewport is { } v)
        {
            var p = e.GetPosition(this);
            v.Set(v.Scale, p.X - d.X, p.Y - d.Y);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);
        if (_press is { } start && Math.Abs(p.X - start.X) + Math.Abs(p.Y - start.Y) < 4 && Viewport is { } v)
        {
            // a click, not a drag: highlight the word (none: clear); select the block, or with Shift show the paths to it
            v.SetHighlight(WordAt(p));
            ulong? block = BlockAt(p)?.Start;
            if ((e.KeyModifiers & KeyModifiers.Shift) != 0 && v.PathFrom is ulong from && block is not null)
                v.SetPath(from, block);
            else
                v.SetPath(block, null);
        }
        _drag = _press = null;
        _miniDrag = false;
        e.Pointer.Capture(null);
    }

    /// <summary>Centres the view on the world point under <paramref name="p"/> in the minimap.</summary>
    void CenterOn(Point p, (Rect Area, double ScaleX, double ScaleY) mini, GraphViewport v)
    {
        double wx = Math.Clamp(p.X - mini.Area.X, 0, mini.Area.Width) / mini.ScaleX, wy = Math.Clamp(p.Y - mini.Area.Y, 0, mini.Area.Height) / mini.ScaleY;
        v.Set(v.Scale, Bounds.Width / 2 - wx * v.Scale, Bounds.Height / 2 - wy * v.Scale);
    }

    /// <summary>
    /// Where a minimap of a <paramref name="scene"/>-sized world goes in a <paramref name="bounds"/>-sized canvas, and its
    /// x and y scales. A graph's: at most 180 px each way, bottom left above the legend, stretched to at least 60 px wide and
    /// 40 px tall when the graph is very tall or wide. A <paramref name="listing"/>'s: a strip down the right edge, full height.
    /// Null when the canvas is too small for one.
    /// </summary>
    public static (Rect Area, double ScaleX, double ScaleY)? MinimapArea(Size scene, Size bounds, bool listing)
    {
        if (scene.Width <= 0 || scene.Height <= 0 || bounds.Width < 320 || bounds.Height < 260)
            return null;
        if (listing)
        {
            double height = bounds.Height - 2 * MiniMargin;
            return (new Rect(bounds.Width - MiniMargin - ListingMiniWidth, MiniMargin, ListingMiniWidth, height), ListingMiniWidth / scene.Width, height / scene.Height);
        }
        double s = Math.Min(MiniMax / scene.Width, MiniMax / scene.Height);
        double sx = Math.Max(s, MiniMin / scene.Width), sy = Math.Max(s, MiniMinHeight / scene.Height);
        double w = scene.Width * sx, h = scene.Height * sy;
        return (new Rect(MiniMargin, bounds.Height - LegendHeight - MiniMargin - h, w, h), sx, sy);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Scene is { IsListing: true } && Viewport is { } v && (e.KeyModifiers & Hotkeys.Command) == 0)
            v.Set(v.Scale, v.X, v.Y + e.Delta.Y * 3 * GraphScene.LineHeight * Math.Max(v.Scale, 0.5));
        else
            ZoomAt(e.GetPosition(this), Math.Pow(1.15, e.Delta.Y));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Space)
            ModeToggleRequested?.Invoke();
        else if (e.Key == Key.Escape)
        {
            Viewport?.SetHighlight(null);
            Viewport?.SetPath(null, null);
        }
        else if (e.Key is Key.Left or Key.Right && (e.KeyModifiers & KeyModifiers.Alt) != 0)
            HistoryRequested?.Invoke(e.Key == Key.Right);
        else if (e.Key == Key.M)
        {
            ShowMinimap = !ShowMinimap;
            InvalidateVisual();
        }
        else
            return;
        e.Handled = true;
    }

    SceneBlock? BlockAt(Point p)
    {
        if (Scene is not { } scene || Viewport is not { } v)
            return null;
        var w = new Point((p.X - v.X) / v.Scale, (p.Y - v.Y) / v.Scale);
        return scene.Blocks.FirstOrDefault(b => b.Bounds.Contains(w));
    }

    /// <summary>The blocks on the paths between the viewport's two blocks in this scene; null when no path is asked for.</summary>
    HashSet<ulong>? PathBlocks(GraphScene scene, GraphViewport v)
    {
        if (v.PathFrom is not ulong from || v.PathTo is not ulong to)
            return null;
        if (_path.Scene != scene || _path.From != from || _path.To != to)
            _path = (scene, from, to, scene.PathBlocks(from, to));
        return _path.Blocks;
    }

    /// <summary>The word under <paramref name="p"/> in a line's instruction text or a block header, or null.</summary>
    string? WordAt(Point p)
    {
        if (Scene is not { } scene || Viewport is not { } v || v.Scale < TextScale)
            return null;
        var w = new Point((p.X - v.X) / v.Scale, (p.Y - v.Y) / v.Scale);
        var block = scene.Blocks.FirstOrDefault(b => b.Bounds.Contains(w));
        if (block is null)
            return null;
        double x = w.X - block.Bounds.Left - GraphScene.Padding;
        if (w.Y < block.Bounds.Top + GraphScene.HeaderHeight)
            return Word(block.Header, (int)Math.Floor(x / GraphScene.CharWidth));
        return LineAt(p) is { } line ? Word(line.Text, (int)Math.Floor((x - scene.AddressChars * GraphScene.CharWidth) / GraphScene.CharWidth)) : null;
    }

    static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '$' or '.' or '?';

    /// <summary>The word of <paramref name="text"/> covering column <paramref name="col"/> (registers, mnemonics, numbers, names).</summary>
    public static string? Word(string text, int col)
    {
        if (col < 0 || col >= text.Length || !IsWordChar(text[col]))
            return null;
        int start = col, end = col;
        while (start > 0 && IsWordChar(text[start - 1]))
            start--;
        while (end + 1 < text.Length && IsWordChar(text[end + 1]))
            end++;
        return text[start..(end + 1)];
    }

    /// <summary>Start columns of <paramref name="word"/> in <paramref name="text"/> as a whole word (case ignored).</summary>
    public static IEnumerable<int> Occurrences(string text, string word)
    {
        for (int i = text.IndexOf(word, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(word, i + 1, StringComparison.OrdinalIgnoreCase))
            if ((i == 0 || !IsWordChar(text[i - 1])) && (i + word.Length == text.Length || !IsWordChar(text[i + word.Length])))
                yield return i;
    }

    SceneLine? LineAt(Point p)
    {
        if (Scene is not { } scene || Viewport is not { } v)
            return null;
        var w = new Point((p.X - v.X) / v.Scale, (p.Y - v.Y) / v.Scale);
        var block = scene.Blocks.FirstOrDefault(b => b.Bounds.Contains(w));
        if (block is null)
            return null;
        int i = (int)Math.Floor((w.Y - block.Bounds.Top - GraphScene.HeaderHeight) / GraphScene.LineHeight);
        return i >= 0 && i < block.Lines.Count ? block.Lines[i] : null;
    }

    /// <summary>The current theme's colours and mono font, for an SVG export that looks like this canvas.</summary>
    public SvgPalette Palette()
    {
        string C(string key) => Brush(key) is ISolidColorBrush s ? $"#{s.Color.R:x2}{s.Color.G:x2}{s.Color.B:x2}" : "#808080";
        return new SvgPalette(C("CanvasBrush"), C("CardBrush"), C("PanelBrush"), C("HairlineBrush"), C("InkBrush"), C("StatusMuted"),
                              C("CyanBrush"), C("MagentaBrush"), C("StatusWarn"), C("StatusError"), C("StatusOk"), Mono.FontFamily.Name);
    }

    // --- drawing -------------------------------------------------------------------------------------
    Typeface Mono => new(this.TryFindResource("MonoFont", out var f) && f is FontFamily family ? family : FontFamily.Default);

    IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var b) && b is IBrush brush ? brush : Brushes.Gray;

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Brush("CanvasBrush"), new Rect(Bounds.Size));
        if (Scene is not { } scene || Viewport is not { } v)
            return;
        var view = new Rect(-v.X / v.Scale, -v.Y / v.Scale, Bounds.Width / v.Scale, Bounds.Height / v.Scale);
        var transform = ctx.PushTransform(Matrix.CreateScale(v.Scale, v.Scale) * Matrix.CreateTranslation(v.X, v.Y));
        double thin = Math.Max(1.4, 1 / v.Scale);
        var path = PathBlocks(scene, v);

        var edgeBrush = new Dictionary<EdgeKind, IBrush>
        {
            [EdgeKind.True] = Brush("StatusOk"), [EdgeKind.False] = Brush("StatusError"), [EdgeKind.Jump] = Brush("StatusMuted"),
            [EdgeKind.Case] = Brush("CyanBrush"),
        };
        foreach (var e in scene.Edges)
        {
            if (!e.Bounds.Inflate(8).Intersects(view))
                continue;
            var brush = edgeBrush[e.Kind];
            bool onPath = path is not null && path.Contains(e.From) && path.Contains(e.To);
            if (path is not null && !onPath)
                brush = Tint(brush, 0.2);   // a path is shown: other edges fade
            var pen = new Pen(brush, onPath ? thin * 2.4 : thin, e.Back ? new DashStyle([4, 3], 0) : null);
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(e.Points[0], false);
                foreach (var p in e.Points.Skip(1))
                    g.LineTo(p);
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, pen, geometry);
            Arrow(ctx, brush, e.Points[^2], e.Points[^1], Math.Max(7, 5 / v.Scale));
        }

        bool text = v.Scale >= TextScale;
        var card = Brush("CardBrush");
        var header = Brush("PanelBrush");
        var hairline = Brush("HairlineBrush");
        var ink = Brush("InkBrush");
        var muted = Brush("StatusMuted");
        var cyan = Brush("CyanBrush");
        var magenta = Brush("MagentaBrush");
        var warn = Brush("StatusWarn");
        var error = Brush("StatusError");
        var ok = Brush("StatusOk");
        var mark = Tint(warn, 0.45);
        string? highlight = v.Highlight;
        double addressWidth = scene.AddressChars * GraphScene.CharWidth;
        foreach (var b in scene.Blocks)
        {
            if (!b.Bounds.Intersects(view))
                continue;
            var r = b.Bounds;
            ctx.FillRectangle(card, r);
            ctx.FillRectangle(header, new Rect(r.X, r.Y, r.Width, GraphScene.HeaderHeight - 4));
            var outline = b.Accent switch { BlockAccent.Patched => magenta, BlockAccent.Stub => warn, BlockAccent.Removed or BlockAccent.Crash => error, BlockAccent.Current => cyan, _ => hairline };
            ctx.DrawRectangle(new Pen(outline, b.Accent == BlockAccent.None ? thin * 0.8 : b.Accent == BlockAccent.Current ? thin * 2.4 : thin * 1.6), r);
            if (b.Start == v.PathFrom || b.Start == v.PathTo || path?.Contains(b.Start) == true)
                ctx.DrawRectangle(new Pen(cyan, thin * (b.Start == v.PathFrom || b.Start == v.PathTo ? 3 : 2)), r.Inflate(3));
            if (text)
            {
                if (highlight is not null)
                    foreach (int at in Occurrences(b.Header, highlight))
                        ctx.FillRectangle(mark, new Rect(r.X + GraphScene.Padding + at * GraphScene.CharWidth, r.Y + 2, highlight.Length * GraphScene.CharWidth, GraphScene.LineHeight));
                ctx.DrawText(Text(b.Header, cyan), new Point(r.X + GraphScene.Padding, r.Y + 3));
            }
            double y = r.Y + GraphScene.HeaderHeight;
            for (int i = 0; i < b.Lines.Count; i++, y += GraphScene.LineHeight)
            {
                var line = b.Lines[i];
                var row = new Rect(r.X + 2, y, r.Width - 4, GraphScene.LineHeight);
                if (!row.Intersects(view))
                    continue;
                IBrush? fill = line.Style switch
                {
                    LineStyle.Patched => Tint(magenta, 0.24), LineStyle.Stub => Tint(warn, 0.2),
                    LineStyle.Removed => Tint(error, 0.18), LineStyle.Crash => Tint(error, 0.4), LineStyle.Pattern => Tint(warn, 0.14), _ => null,
                };
                if (fill is not null)
                    ctx.FillRectangle(fill, row);
                var fore = line.Style switch { LineStyle.Call => cyan, LineStyle.Was => muted, _ => ink };
                double x = r.X + GraphScene.Padding;
                if (!text)
                {
                    // zoomed out: lines holding the highlighted word stand out
                    bool marked = highlight is not null && Occurrences(line.Text, highlight).Any();
                    ctx.FillRectangle(marked ? warn : Tint(fore, 0.35), new Rect(x + addressWidth, y + 4, line.Text.Length * GraphScene.CharWidth, GraphScene.LineHeight - 8));
                    continue;
                }
                if (highlight is not null)
                    foreach (int at in Occurrences(line.Text, highlight))
                        ctx.FillRectangle(mark, new Rect(x + addressWidth + at * GraphScene.CharWidth, y + 1, highlight.Length * GraphScene.CharWidth, GraphScene.LineHeight - 2));
                ctx.DrawText(Cached(line, 0, line.Address, muted), new Point(x, y));
                var body = Cached(line, 1, line.Text, fore);
                if (line.Style == LineStyle.Was)
                    body.SetTextDecorations(TextDecorations.Strikethrough);
                ctx.DrawText(body, new Point(x + addressWidth, y));
                double tagX = x + addressWidth + body.WidthIncludingTrailingWhitespace + 2 * GraphScene.CharWidth;
                for (int k = 0; k < (line.Tags?.Count ?? 0); k++)
                {
                    var tag = line.Tags![k];
                    var tone = tag.Kind switch
                    {
                        TagKind.Fakelib => cyan, TagKind.Redirect or TagKind.Unknown or TagKind.Pattern => warn, TagKind.Missing => error,
                        TagKind.Data => ok, _ => muted,
                    };
                    var t = Cached(line, 2 + k, tag.Text, tone);
                    ctx.DrawRectangle(new Pen(tone, 1), new Rect(tagX - 3, y + 1, t.Width + 6, GraphScene.LineHeight - 2));
                    ctx.DrawText(t, new Point(tagX, y));
                    tagX += t.Width + 3 * GraphScene.CharWidth;
                }
            }
            if (path is not null && !path.Contains(b.Start))
                ctx.FillRectangle(Tint(Brush("CanvasBrush"), 0.6), r.Inflate(1));   // off the path: faded
        }
        transform.Dispose();   // back to screen coordinates for the note and the minimap
        PathNote(ctx, scene, v, path);
        Minimap(ctx, scene, v, view, highlight);
    }

    /// <summary>
    /// The whole scene small, when it does not fit the canvas: blocks in their change colours (amber when they hold the
    /// highlighted word, cyan for a call graph's centre), the visible part outlined in cyan.
    /// </summary>
    void Minimap(DrawingContext ctx, GraphScene scene, GraphViewport v, Rect view, string? highlight)
    {
        _mini = null;
        bool fits = scene.Size.Width * v.Scale <= Bounds.Width && scene.Size.Height * v.Scale <= Bounds.Height;
        if (!Leads || !ShowMinimap || fits || MinimapArea(scene.Size, Bounds.Size, scene.IsListing) is not { } mini)
            return;
        _mini = mini;
        var (area, sx, sy) = mini;
        ctx.FillRectangle(Tint(Brush("CardBrush"), 0.92), area.Inflate(4));
        ctx.DrawRectangle(new Pen(Brush("HairlineBrush"), 1), area.Inflate(4));
        var muted = Tint(Brush("StatusMuted"), 0.55);
        var marked = Brush("StatusWarn");
        using (ctx.PushClip(area.Inflate(1)))
        {
            foreach (var b in scene.Blocks)
            {
                var r = new Rect(area.X + b.Bounds.X * sx, area.Y + b.Bounds.Y * sy, Math.Max(b.Bounds.Width * sx, 1.5), Math.Max(b.Bounds.Height * sy, 1));
                IBrush fill = highlight is not null && b.Lines.Any(l => Occurrences(l.Text, highlight).Any()) ? marked : b.Accent switch
                {
                    BlockAccent.Patched => Brush("MagentaBrush"), BlockAccent.Stub => Brush("StatusWarn"),
                    BlockAccent.Removed or BlockAccent.Crash => Brush("StatusError"), BlockAccent.Current => Brush("CyanBrush"), _ => muted,
                };
                ctx.FillRectangle(fill, r);
            }
            var seen = new Rect(area.X + view.X * sx, area.Y + view.Y * sy, view.Width * sx, Math.Max(view.Height * sy, 2)).Intersect(area.Inflate(1));
            ctx.DrawRectangle(new Pen(Brush("CyanBrush"), 1.5), seen);
        }
    }

    /// <summary>What the selection means, at the top right: the selected block, or the path between the two.</summary>
    void PathNote(DrawingContext ctx, GraphScene scene, GraphViewport v, HashSet<ulong>? path)
    {
        string Name(ulong start) => scene.Blocks.FirstOrDefault(b => b.Start == start)?.Header ?? $"0x{start:x}";
        if (v.PathFrom is not ulong from || scene.Blocks.All(b => b.Start != from))
            return;
        string note = v.PathTo is not ulong to ? $"{Name(from)} selected · Shift+click another block for the paths to it · Esc clears"
            : path!.Count == 0 ? $"no path from {Name(from)} to {Name(to)}"
            : $"paths {Name(from)} → {Name(to)}: {path.Count} {(path.Count == 1 ? "block" : "blocks")} · Esc clears";
        var t = new FormattedText(note, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, Brush("InkBrush"));
        var box = new Rect(Math.Max(8, Bounds.Width - t.Width - 24), 8, t.Width + 16, t.Height + 8);
        ctx.FillRectangle(Brush("CardBrush"), box);
        ctx.DrawRectangle(new Pen(Brush("CyanBrush"), 1), box);
        ctx.DrawText(t, new Point(box.X + 8, box.Y + 4));
    }

    FormattedText Text(string s, IBrush brush) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 12, brush);

    /// <summary>Line texts are cached per scene (part 0 address, 1 instruction, 2+ tags); headers are few and are not.</summary>
    FormattedText Cached(SceneLine line, int part, string s, IBrush brush)
    {
        if (!_text.TryGetValue((line, part), out var t))
            _text[(line, part)] = t = Text(s, brush);
        return t;
    }

    static IBrush Tint(IBrush brush, double opacity) =>
        brush is ISolidColorBrush s ? new SolidColorBrush(s.Color, opacity) : brush;

    static void Arrow(DrawingContext ctx, IBrush brush, Point from, Point tip, double size)
    {
        var d = tip - from;
        double len = Math.Sqrt(d.X * d.X + d.Y * d.Y);
        if (len < 0.001)
            return;
        var u = new Point(d.X / len, d.Y / len);
        var n = new Point(-u.Y, u.X);
        var back = new Point(tip.X - u.X * size, tip.Y - u.Y * size);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(tip, true);
            g.LineTo(new Point(back.X + n.X * size * 0.5, back.Y + n.Y * size * 0.5));
            g.LineTo(new Point(back.X - n.X * size * 0.5, back.Y - n.Y * size * 0.5));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(brush, null, geometry);
    }
}
