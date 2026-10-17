using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Ps5Backport.App.ViewModels;
using Ps5Backport.Core;
using System.Diagnostics;

namespace Ps5Backport.App.Views;

public partial class MainWindow : Window
{
    double _expandedLogHeight = 140;

    public MainWindow()
    {
        InitializeComponent();
        LogList.DoubleTapped += (_, _) => (DataContext as MainViewModel)?.JumpTo(LogList.SelectedItem as LogEntry);
        LogToggle.Click += (_, _) =>
        {
            if (LogToggle.IsChecked == true)
            {
                Body.RowDefinitions[3].Height = new GridLength(_expandedLogHeight);
                LogToggle.Content = "Activity log ▾";
            }
            else
            {
                _expandedLogHeight = Math.Max(80, Body.RowDefinitions[3].ActualHeight);
                Body.RowDefinitions[3].Height = new GridLength(44);
                LogToggle.Content = "Activity log ▸";
            }
        };

        // drop a game folder (or a file in it) anywhere on the Backport page; other pages handle their own drops
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (DataContext is MainViewModel { Page: AppPage.Backport })
                e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;
        });
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (DataContext is MainViewModel { Page: AppPage.Backport } vm
                && e.DataTransfer.TryGetFiles()?.FirstOrDefault()?.TryGetLocalPath() is { } path)
                vm.DropGame(path);
        });

        // the rail over the code graph page: the left edge or ≡ reveals it, leaving it or a click elsewhere hides it, Esc too
        RailEdge.PointerEntered += (_, _) => { if (DataContext is MainViewModel { RailDocked: false } vm) vm.RailRevealed = true; };
        Rail.PointerExited += (_, _) => { if (DataContext is MainViewModel { RailDocked: false } vm) vm.RailRevealed = false; };
        AddHandler(PointerPressedEvent, (_, _) =>
        {
            if (DataContext is MainViewModel { RailDocked: false, RailRevealed: true } vm && !Rail.IsPointerOver)
                vm.RailRevealed = false;
        }, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && DataContext is MainViewModel { RailRevealed: true } vm)
                vm.RailRevealed = false;
        }, RoutingStrategies.Tunnel);

        // Windows 11 Mica (Acrylic on Windows 10); the theme's solid background where neither is available
        TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None];
        Background = Brushes.Transparent;
        Opened += (_, _) =>
        {
            if (ActualTransparencyLevel == WindowTransparencyLevel.None)
                this[!BackgroundProperty] = this.GetResourceObservable("SystemControlBackgroundAltHighBrush").ToBinding();
        };
    }

    /// <summary>Installs the desktop close policy. This window owns the supplied sink until workers and settings finish.</summary>
    public void ConfigureShutdown(MainViewModel vm, AppSettings settings, string settingsPath, DiagnosticLog? diagnostics = null)
    {
        bool closing = false, readyToClose = false;
        Opened += (_, _) => diagnostics?.Write(new("desktop", "opened", "window",
            Message: FormattableString.Invariant($"Size={Width}x{Height}; scale={RenderScaling}; screens={Screens.All.Count}")));
        Closed += (_, _) => { vm.Dispose(); diagnostics?.Dispose(); };
        Closing += async (_, closingEvent) =>
        {
            if (!readyToClose)
            {
                closingEvent.Cancel = true;
                if (closing) return;
                closing = true;
                var timer = Stopwatch.StartNew();
                diagnostics?.Write(new("desktop", "closing", "shutdown"));
                try { await vm.CancelAndWaitAsync(); }
                catch (Exception e) { vm.Log.Error("A worker failed while closing", exception: e); }
                diagnostics?.Write(new("desktop", "workers_finished", "shutdown", DurationMilliseconds: timer.Elapsed.TotalMilliseconds));
                readyToClose = true;
                Avalonia.Threading.Dispatcher.UIThread.Post(Close);
                return;
            }
            vm.CaptureSettings();
            CaptureSettings(settings);
            try { settings.Save(settingsPath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { vm.Log.Error("Could not save settings while closing", exception: e); }
            diagnostics?.Write(new("desktop", "closed", "shutdown"));
        };
    }

    /// <summary>Restores the remembered size, position and log height.</summary>
    public void ApplySettings(AppSettings s)
    {
        if (s.WindowWidth is > 400 and var w && s.WindowHeight is > 300 and var h)
        {
            Width = w;
            Height = h;
        }
        if (s.WindowX is { } x && s.WindowY is { } y)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
        }
        if (s.Maximized)
            WindowState = WindowState.Maximized;
        _expandedLogHeight = Math.Clamp(s.LogHeight, 80, 600);
        Body.RowDefinitions[3].Height = new GridLength(LogToggle.IsChecked == true ? _expandedLogHeight : 44);
    }

    /// <summary>Copies the current size, position and log height into the settings (before saving).</summary>
    public void CaptureSettings(AppSettings s)
    {
        s.Maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            s.WindowWidth = Width;
            s.WindowHeight = Height;
            s.WindowX = Position.X;
            s.WindowY = Position.Y;
        }
        s.LogHeight = LogToggle.IsChecked == true ? Body.RowDefinitions[3].ActualHeight : _expandedLogHeight;
    }

    async Task<string[]> PickFiles(string title, string typeName, string[] patterns, bool multiple)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = multiple, FileTypeFilter = [new FilePickerFileType(typeName) { Patterns = patterns }],
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not MainViewModel vm)
            return;
        vm.PickFolder = async title =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        };
        vm.PickFile = async (title, typeName, pattern) => (await PickFiles(title, typeName, [pattern], false)).FirstOrDefault();
        vm.Store.PickFiles = PickFiles;
        vm.PickSaveFile = async (title, name, extension) =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title, SuggestedFileName = name, DefaultExtension = extension, ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType(extension.ToUpperInvariant()) { Patterns = [$"*.{extension}"] }],
            });
            return file?.TryGetLocalPath();
        };
        vm.SetClipboard = async text =>
        {
            if (Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text);
        };
        // keep the newest log line in view
        vm.Log.Visible.CollectionChanged += (_, _) =>
        {
            if (vm.Log.Visible.Count > 0)
                LogList.ScrollIntoView(vm.Log.Visible.Count - 1);
        };
    }
}
