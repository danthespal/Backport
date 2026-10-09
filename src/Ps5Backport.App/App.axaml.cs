using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Ps5Backport.App.ViewModels;
using Ps5Backport.App.Views;

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
            var vm = new MainViewModel(settings);
            if (problem is not null)
                vm.Log.Warning(problem);
            var window = new MainWindow { DataContext = vm };
            window.ApplySettings(settings);
            window.Opened += (_, _) => vm.RestoreLastGame();
            window.Closing += (_, _) =>
            {
                vm.CaptureSettings();
                window.CaptureSettings(settings);
                try { settings.Save(path); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* nothing to report to once closing */ }
            };
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
