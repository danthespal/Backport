using System.Collections.Specialized;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Ps5Backport.App;
using Ps5Backport.App.ViewModels;
using Ps5Backport.App.Views;
using Ps5Backport.Core;

if (args[0] == "--check")
{
    await InteractionChecks.Run(args[1], args[2]);
    Console.WriteLine("Passed: " + args[1]);
    Environment.Exit(0);
}

using var session = HeadlessUnitTestSession.StartNew(typeof(ProfileApp));
var measurements = await session.Dispatch(async () =>
{
    using var vm = new MainViewModel();
    var window = new MainWindow { DataContext = vm, Width = 1280, Height = 820 };
    window.Show();
    var result = new GameResult("synthetic", "unused", "4");
    var module = new ModuleResult("eboot.bin", Self.KindElf, false);
    for (int i = 0; i < 10000; i++) module.Statuses.Add(new ImportStatus($"{i:D11}", "libSynthetic", 0, true, Imports.Missing));
    result.Modules.Add(module);
    typeof(MainViewModel).GetMethod("Show", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [result, new Knowledge()]);
    vm.SelectedTab = 2;
    await Task.Yield();
    int notifications = 0;
    vm.Imports.CollectionChanged += (_, _) => notifications++;
    var records = new List<object>();
    foreach (string query in new[] { "libSynthetic", "absent", "libSynthetic", "00000000001", "libSynthetic" })
    {
        notifications = 0;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        vm.ImportSearch = query;
        await Task.Yield();
        timer.Stop();
        records.Add(new { Query = query, Milliseconds = timer.Elapsed.TotalMilliseconds,
            AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated, Notifications = notifications, Rows = vm.Imports.Count });
    }
    window.Close();
    return records;
}, CancellationToken.None);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
File.WriteAllText(args[0], JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
Environment.Exit(0); // The process owns this isolated headless dispatcher; never leave it holding build outputs.

public class ProfileApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
