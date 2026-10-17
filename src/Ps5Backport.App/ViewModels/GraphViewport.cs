using CommunityToolkit.Mvvm.ComponentModel;

namespace Ps5Backport.App.ViewModels;

/// <summary>Pan and zoom shared by the graph canvases (side by side they move together): screen = world * Scale + (X, Y).</summary>
public sealed partial class GraphViewport : ObservableObject
{
    public double Scale { get; private set; } = 1;
    public double X { get; private set; }
    public double Y { get; private set; }
    public string ZoomText => $"{Math.Round(Scale * 100)}%";

    /// <summary>The word clicked in a line (IDA-style): every occurrence is highlighted in the canvases sharing this viewport.</summary>
    public string? Highlight { get; private set; }

    public event Action? Changed;

    /// <summary>The selected block (its start), and the block a path is shown to (Shift+click); shared by both canvases.</summary>
    public ulong? PathFrom { get; private set; }
    public ulong? PathTo { get; private set; }

    public void SetPath(ulong? from, ulong? to)
    {
        if (from == PathFrom && to == PathTo)
            return;
        (PathFrom, PathTo) = (from, to);
        Changed?.Invoke();
    }

    public void SetHighlight(string? word)
    {
        if (word == Highlight)
            return;
        Highlight = word;
        Changed?.Invoke();
    }

    public void Set(double scale, double x, double y)
    {
        (Scale, X, Y) = (scale, x, y);
        OnPropertyChanged(nameof(ZoomText));
        Changed?.Invoke();
    }
}
