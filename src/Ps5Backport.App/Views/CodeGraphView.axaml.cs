using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Ps5Backport.App.ViewModels;

namespace Ps5Backport.App.Views;

public partial class CodeGraphView : UserControl
{
    public CodeGraphView()
    {
        InitializeComponent();
        ZoomIn.Click += (_, _) => MainCanvas.ZoomAtCenter(1.25);
        SaveSvgItem.Click += async (_, _) =>
        {
            if (DataContext is CodeGraphViewModel vm)
                await vm.SaveSvg(MainCanvas.Palette());
        };
        SavePngItem.Click += async (_, _) =>
        {
            if (DataContext is CodeGraphViewModel vm)
                await vm.SavePng(SaveView);
        };
        ZoomOut.Click += (_, _) => MainCanvas.ZoomAtCenter(1 / 1.25);
        FitButton.Click += (_, _) => MainCanvas.Fit();
        MainCanvas.TargetActivated += Navigate;
        OriginalCanvas.TargetActivated += Navigate;
        MainCanvas.ModeToggleRequested += ToggleListing;
        OriginalCanvas.ModeToggleRequested += ToggleListing;
        MainCanvas.HistoryRequested += Step;
        OriginalCanvas.HistoryRequested += Step;
        // Ctrl+K (⌘K on macOS): the search box; F11: both side panels away or back
        foreach (var control in new Control[] { RailButton, ListingButton })
            ToolTip.SetTip(control, Hotkeys.Text((string)ToolTip.GetTip(control)!));
        SearchBox.PlaceholderText = Hotkeys.Text(SearchBox.PlaceholderText!);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.K && e.KeyModifiers == Hotkeys.Command)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.F11 && DataContext is CodeGraphViewModel vm)
            {
                vm.ToggleFocusCommand.Execute(null);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    void Step(bool forward)
    {
        if (DataContext is CodeGraphViewModel vm)
            (forward ? vm.ForwardCommand : vm.BackCommand).Execute(null);
    }

    void ToggleListing()
    {
        if (DataContext is CodeGraphViewModel vm)
            vm.Listing = !vm.Listing;
    }

    /// <summary>The main canvas's theme colours, for an SVG export.</summary>
    public SvgPalette Palette() => MainCanvas.Palette();

    /// <summary>Writes the graph area as it is on screen (both canvases side by side) to a PNG at twice its size.</summary>
    public bool SaveView(string path)
    {
        const double scale = 2;
        var size = GraphArea.Bounds.Size;
        if (size.Width < 1 || size.Height < 1)
            return false;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)(size.Width * scale), (int)(size.Height * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(GraphArea);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
        return true;
    }

    void Navigate(ulong target) => (DataContext as CodeGraphViewModel)?.Navigate(target);

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        MainCanvas.TargetActivated -= Navigate;
        OriginalCanvas.TargetActivated -= Navigate;
        MainCanvas.ModeToggleRequested -= ToggleListing;
        OriginalCanvas.ModeToggleRequested -= ToggleListing;
        MainCanvas.HistoryRequested -= Step;
        OriginalCanvas.HistoryRequested -= Step;
        base.OnUnloaded(e);
    }
}
