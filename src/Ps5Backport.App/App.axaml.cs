using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Ps5Backport.App.ViewModels;
using Ps5Backport.App.Views;
using Ps5Backport.Core;

namespace Ps5Backport.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string path = AppSettings.DefaultPath;
            var settings = AppSettings.Load(path, out string? problem);
            var diagnostics = new DiagnosticLog(DiagnosticLog.DefaultDirectory("desktop"));
            var vm = new MainViewModel(settings, diagnostics);
            if (problem is not null)
                vm.Log.Warning(problem);
            var window = new MainWindow { DataContext = vm };
            window.ApplySettings(settings);
            window.Opened += (_, _) => vm.RestoreLastGame();
            window.ConfigureShutdown(vm, settings, path, diagnostics);
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
