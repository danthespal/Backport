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
        // ps5backportstudio --snapshot OUT.png [--game DIR] [--option KEY]... [--analyze] [--expand] [--height PX] [--dark] [--tab N] [--page settings|store|firmwaredb|tools|codegraph|results] [--results-select ITEM] [--results-add COOK[;KLOG]] [--results-promote] [--files F1;F2] [--nid TEXT]
        // [--code-graph EBOOT[;BACKPORT|cook]|current] [--side-by-side] [--function HEX] [--check-firmware FW|Off] [--filter all|changed|fakelib|missing] [--crash-log LOG]
        // [--show-in-code-graph] (the --select-import's call sites) [--open-in-imports NID] (from the code graph) [--listing] [--highlight WORD] [--path FROM[;TO]] [--search TEXT] [--calls] [--export-text|--export-svg|--export-png PATH] [--no-minimap] [--follow NID]: render the main window
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

    /// <summary>Runs the UI thread's jobs until <paramref name="done"/> holds (at most a minute).</summary>
    static void PumpUntil(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddMinutes(1);
        while (!done() && DateTime.UtcNow < until)
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
        int select = Array.IndexOf(args, "--results-select");
        if (select >= 0 && select + 1 < args.Length)
            vm.Results.SelectedRow = vm.Results.Rows.FirstOrDefault(r => r.Item == args[select + 1]) ?? vm.Results.SelectedRow;
        if (args.Contains("--results-promote"))
            vm.Results.OpenPromoteCommand.Execute(null);
        int add = Array.IndexOf(args, "--results-add");
        if (add >= 0 && add + 1 < args.Length)
        {
            vm.Results.StartAddCommand.Execute(null);
            string[] parts = args[add + 1].Split(';');
            vm.Results.SetBuild(parts[0]);
            if (parts.Length > 1)
                vm.Results.SetLog(parts[1]);
        }
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
        int graph = Array.IndexOf(args, "--code-graph");
        if (args.Contains("--show-in-code-graph"))
        {
            Pump(vm.ShowInCodeGraphCommand.ExecuteAsync(null));
            PumpUntil(() => vm.CodeGraph.GraphMessage.Length == 0 || vm.CodeGraph.GraphMessage.StartsWith("No graph", StringComparison.Ordinal));
        }
        else if (graph >= 0 && graph + 1 < args.Length)
        {
            var cg = vm.CodeGraph;
            string[] paths = args[graph + 1].Split(';');
            if (paths[0] == "current")   // the --game, compared with its last backport or Studio's cook
                Pump(cg.CurrentGameCommand.ExecuteAsync(null));
            else
            {
                cg.EbootPath = paths[0];
                cg.ComparePreview = paths.Length > 1 && paths[1] == "cook";   // EBOOT;cook = Studio's cook preview
                cg.ComparePath = paths.Length > 1 && !cg.ComparePreview ? paths[1] : "";   // a backport, or another build of the game
                Pump(cg.Reload());
            }
            int check = Array.IndexOf(args, "--check-firmware");
            if (check >= 0 && check + 1 < args.Length)
            {
                cg.CheckFirmware = args[check + 1];
                PumpUntil(() => !cg.Busy);
                Thread.Sleep(500);   // the check runs in the background; let it land
                Dispatcher.UIThread.RunJobs();
            }
            int crash = Array.IndexOf(args, "--crash-log");
            if (crash >= 0 && crash + 1 < args.Length)
            {
                cg.CrashPath = args[crash + 1];
                Pump(cg.LoadCrash());
            }
            int filter = Array.IndexOf(args, "--filter");
            if (filter >= 0 && filter + 1 < args.Length)
                cg.Filter = Enum.Parse<FunctionFilter>(args[filter + 1], ignoreCase: true);
            int search = Array.IndexOf(args, "--search");
            if (search >= 0 && search + 1 < args.Length)
                cg.Search = args[search + 1];
            cg.SideBySide = args.Contains("--side-by-side");
            cg.Listing = args.Contains("--listing");
            if (args.Contains("--no-minimap"))
                foreach (var canvas in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<GraphCanvas>())
                    canvas.ShowMinimap = false;
            if (args.Contains("--calls"))
                cg.View = CodeView.Calls;
            int highlight = Array.IndexOf(args, "--highlight");
            if (highlight >= 0 && highlight + 1 < args.Length)
                cg.Viewport.SetHighlight(args[highlight + 1]);
            int path = Array.IndexOf(args, "--path");   // FROM[;TO]: block starts, hex
            if (path >= 0 && path + 1 < args.Length)
            {
                var ends = args[path + 1].Split(';').Select(a => Convert.ToUInt64(a.Replace("0x", ""), 16)).ToArray();
                cg.Viewport.SetPath(ends[0], ends.Length > 1 ? ends[1] : null);
            }
            int function = Array.IndexOf(args, "--function");
            if (function >= 0 && function + 1 < args.Length)
                cg.Navigate(Convert.ToUInt64(args[function + 1].Replace("0x", ""), 16));
            PumpUntil(() => cg.GraphMessage.Length == 0 || cg.GraphMessage.StartsWith("No graph", StringComparison.Ordinal));
            int follow = Array.IndexOf(args, "--follow");   // NID: open the module that exports it
            if (follow >= 0 && follow + 1 < args.Length)
            {
                Pump(cg.FollowImport(args[follow + 1]));
                PumpUntil(() => cg.GraphMessage.Length == 0 || cg.GraphMessage.StartsWith("No graph", StringComparison.Ordinal));
            }
            int text = Array.IndexOf(args, "--export-text");
            if (text >= 0 && text + 1 < args.Length)
                File.WriteAllText(args[text + 1], cg.ExportText() ?? "");
            var view = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<CodeGraphView>().First();
            int svg = Array.IndexOf(args, "--export-svg");
            if (svg >= 0 && svg + 1 < args.Length)
                File.WriteAllText(args[svg + 1], cg.ExportSvg(view.Palette()) ?? "");
            int png = Array.IndexOf(args, "--export-png");
            if (png >= 0 && png + 1 < args.Length)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                view.SaveView(args[png + 1]);
            }
            int imports = Array.IndexOf(args, "--open-in-imports");
            if (imports >= 0 && imports + 1 < args.Length)
                cg.OpenInImportsCommand.Execute(new LinkRow("", "", null, Nid: args[imports + 1]));
        }
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
