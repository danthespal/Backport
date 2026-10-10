using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Ps5Backport.App;
using Ps5Backport.App.ViewModels;
using Ps5Backport.App.Views;
using Ps5Backport.Core;
using Ps5Backport.Tests;

/// <summary>Actual control/binding/input checks in an isolated dispatcher process, without another test framework.</summary>
static class InteractionChecks
{
    internal static async Task Run(string scenario, string root)
    {
        string game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        string reference = Directory.CreateDirectory(Path.Combine(root, "reference")).FullName;
        int size = scenario is "busy-cancel" or "close-busy" ? 64 * 1024 * 1024 : 1024;
        File.WriteAllBytes(Path.Combine(game, "eboot.bin"), SyntheticBinary.Elf(new byte[size], imports: true));
        FirmwareDb.Save(new FirmwareExports("4", "synthetic", false, new()), Path.Combine(reference, "4.json"));
        string names = Path.Combine(root, "names.csv");
        File.WriteAllText(names, SyntheticBinary.Nid + " ExampleFunction\n");
        // The isolated worker exits after dispatch; renderer teardown can block in headless mode.
        var session = HeadlessUnitTestSession.StartNew(typeof(ProfileApp));
        await session.Dispatch(async () =>
        {
            using var diagnostics = new DiagnosticLog(Path.Combine(root, "logs"));
            var settings = new AppSettings { FirmwareDbDir = reference, FakelibStoreDir = Path.Combine(root, "absent"), AerolibPath = names, KnownFixes = false };
            using var vm = new MainViewModel(settings, diagnostics);
            vm.LoadGame(game);
            vm.OutputFolder = Path.Combine(root, "out");
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 820 };
            string settingsPath = Path.Combine(root, "settings.json");
            window.ConfigureShutdown(vm, settings, settingsPath, diagnostics);
            window.Show();
            window.UpdateLayout();
            var analyze = window.FindControl<Button>("AnalyzeButton")!;
            var cook = window.FindControl<Button>("CookButton")!;
            var cancel = window.FindControl<Button>("CancelButton")!;
            Check(analyze.IsEffectivelyEnabled && cook.IsEffectivelyEnabled, "Commands should be available after opening a game");

            if (scenario is "busy-cancel" or "close-busy")
            {
                var task = vm.AnalyzeCommand.ExecuteAsync(null);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                window.UpdateLayout();
                Check(vm.Busy && !analyze.IsEffectivelyEnabled && !cook.IsEffectivelyEnabled && cancel.IsVisible, $"Busy bindings did not update: busy={vm.Busy}, analyze={analyze.IsEffectivelyEnabled}, cook={cook.IsEffectivelyEnabled}, cancel={cancel.IsVisible}, state={vm.OperationState}, log={vm.Log.Text()}");
                Check(!vm.OpenGameCommand.CanExecute(null), "Open should be disabled while busy");
                if (scenario == "busy-cancel")
                {
                    cancel.Focus();
                    window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                    window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                    await task;
                    Check(vm.OperationState == OperationState.Cancelled && vm.Progress == 0, "Cancel button did not cancel the operation");
                    Check(analyze.IsEffectivelyEnabled && cook.IsEffectivelyEnabled && !cancel.IsVisible, "Terminal bindings did not recover");
                }
                else
                {
                    window.Close();
                    await task;
                    await Closed(window);
                    Check(vm.OperationState == OperationState.Cancelled && !vm.Busy, "Close did not cancel/await cleanup");
                    Check(File.Exists(settingsPath), "Close did not save settings");
                    Check(!vm.CookCommand.CanExecute(null), "Closing admitted new work");
                }
            }
            else
            {
                await vm.AnalyzeCommand.ExecuteAsync(null);
                Check(vm.OperationState == OperationState.Completed, "Synthetic analysis failed");
                if (scenario == "search-selection")
                {
                    vm.SelectedTab = 2;
                    window.UpdateLayout();
                    var search = window.FindControl<TextBox>("ImportSearchBox")!;
                    var table = window.FindControl<TableView>("ImportsTable")!;
                    vm.ImportFilter = "All";
                    var selected = vm.Imports.Single();
                    table.SelectedItem = selected;
                    Check(ReferenceEquals(vm.SelectedImport, selected), "Table selection did not reach the view model");
                    search.Focus();
                    window.KeyTextInput("  examplefunction  ");
                    await Task.Yield();
                    Check(vm.Imports.Count == 1, "Case-insensitive name/trimmed search changed");
                    Check(ReferenceEquals(vm.SelectedImport, selected) && ReferenceEquals(table.SelectedItem, selected), "Batch replacement lost selection");
                    search.SelectAll();
                    window.KeyTextInput("absent");
                    await Task.Yield();
                    Check(vm.Imports.Count == 0 && vm.SelectedImport is null && !vm.HasSelectedImport, "Hidden selection left a stale inspector");
                    search.SelectAll();
                    window.KeyTextInput("#0");
                    await Task.Yield();
                    Check(vm.Imports.Count == 1, "Library search changed");
                    search.SelectAll();
                    window.KeyTextInput("xxxxxxxxxxx");
                    await Task.Yield();
                    Check(vm.Imports.Count == 0, "NID search must remain case sensitive");
                    search.SelectAll();
                    window.KeyTextInput(SyntheticBinary.Nid);
                    await Task.Yield();
                    Check(vm.Imports.Count == 1, "Exact NID search changed");
                    Check(Avalonia.Automation.AutomationProperties.GetName(search) == "Search imports", "Search lacks an accessible name");
                }
                else if (scenario == "stale-analysis")
                {
                    vm.ExtendText = !vm.ExtendText;
                    await vm.CookCommand.ExecuteAsync(null);
                    Check(vm.OperationState == OperationState.Failed && vm.Progress == 0, "Stale analysis displayed success");
                    Check(!Directory.Exists(vm.OutputFolder), "Stale cook acquired output");
                    Check(vm.Log.Text().Contains("Analysis is stale", StringComparison.Ordinal), "Stale analysis was not explained");
                }
                else if (scenario == "close-save-failure")
                {
                    Directory.CreateDirectory(settingsPath);
                    window.Close();
                    await Closed(window);
                    var events = File.ReadAllLines(Path.Combine(root, "logs", "events.jsonl")).Select(s => JsonSerializer.Deserialize<DiagnosticEvent>(s)!);
                    Check(events.Any(e => e.Message == "Could not save settings while closing" && e.ExceptionContext is not null), "Closing lost the settings exception");
                }
                else throw new ArgumentException("Unknown interaction scenario: " + scenario);
            }
            if (window.IsVisible) { window.Close(); await Closed(window); }
            Check(!Directory.GetDirectories(root).Any(p => Path.GetFileName(p).StartsWith(".ps5bp-", StringComparison.Ordinal)), "Staging escaped cleanup");
            return true;
        }, CancellationToken.None);
    }

    static async Task Closed(Window window)
    {
        for (int i = 0; window.IsVisible && i < 100; i++) await Task.Delay(10);
        Check(!window.IsVisible, "Window did not finish closing");
    }
    static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
