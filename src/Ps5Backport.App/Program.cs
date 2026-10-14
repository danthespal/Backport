using Avalonia;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Ps5Backport.App.ViewModels;
using Ps5Backport.App.Views;

namespace Ps5Backport.App;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // ps5backportstudio --snapshot OUT.png [--game DIR] [--option KEY]... [--analyze] [--expand] [--height PX] [--dark] [--tab N] [--page settings|store|firmwaredb|tools] [--files F1;F2] [--nid TEXT]: render the main window
        // without a display (headless Skia) and save it. Used to review the UI and for docs.
        int at = Array.IndexOf(args, "--snapshot");
        if (at >= 0 && at + 1 < args.Length)
            return Snapshot(args[at + 1], args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    /// <summary>Runs the UI thread's jobs until the task is done (progress and results post back to it).</summary>
    static void Pump(Task task)
    {
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    static int Snapshot(string output, string[] args)
    {
        AppBuilder.Configure<App>()
            .UseSkia().UseHarfBuzz()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        if (args.Contains("--dark"))
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        else
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;

        using var vm = new MainViewModel();
        int game = Array.IndexOf(args, "--game");
        if (game >= 0 && game + 1 < args.Length)
        {
            vm.LoadGame(args[game + 1]);
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--option" && vm.PatchOptions.FirstOrDefault(o => o.Key == args[i + 1]) is { } option)
                    option.IsEnabled = true;
            if (args.Contains("--analyze"))
            {
                Pump(vm.AnalyzeCommand.ExecuteAsync(null));
            }
        }
        int import = Array.IndexOf(args, "--select-import");
        if (import >= 0 && import + 1 < args.Length)
            vm.SelectedImport = vm.Imports.FirstOrDefault(i => i.Name == args[import + 1] || i.Nid == args[import + 1]);
        int tab = Array.IndexOf(args, "--tab");
        if (tab >= 0 && tab + 1 < args.Length)
            vm.SelectedTab = int.Parse(args[tab + 1]);
        int page = Array.IndexOf(args, "--page");
        if (page >= 0 && page + 1 < args.Length)
            vm.Page = Enum.Parse<AppPage>(args[page + 1], ignoreCase: true);
        Pump(vm.PageLoad);
        int files = Array.IndexOf(args, "--files");
        if (files >= 0 && files + 1 < args.Length)
            Pump(vm.Tools.Add(args[files + 1].Split(';')));
        int nid = Array.IndexOf(args, "--nid");
        if (nid >= 0 && nid + 1 < args.Length)
            vm.Tools.NidQuery = args[nid + 1];

        int height = Array.IndexOf(args, "--height");
        var window = new MainWindow { DataContext = vm, Width = 1280,
            Height = height >= 0 && height + 1 < args.Length ? int.Parse(args[height + 1]) : 820 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        if (args.Contains("--expand"))
        {
            foreach (var expander in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.Expander>())
                expander.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
        }
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(output, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        window.Close();
        return frame is null ? 1 : 0;
    }
}
