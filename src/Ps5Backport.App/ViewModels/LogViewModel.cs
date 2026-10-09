using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Ps5Backport.App.ViewModels;

public enum LogLevel { Info, Warning, Error }

/// <param name="Module">Module path the entry is about (click to jump to it), or null.</param>
public sealed record LogEntry(DateTime Time, LogLevel Level, string Message, string? Module = null)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public string LevelText => Level switch { LogLevel.Error => "error", LogLevel.Warning => "warn", _ => "info" };
    public override string ToString() => $"{TimeText}  {LevelText,-5}  {(Module is null ? "" : Module + "  ")}{Message}";
}

/// <summary>
/// The development log docked at the bottom: everything the app does, with errors and warnings counted
/// and filterable so problems are easy to see, copy and fix.
/// </summary>
public partial class LogViewModel : ObservableObject
{
    readonly List<LogEntry> _all = [];

    /// <summary>Entries matching the current filter (newest last).</summary>
    public ObservableCollection<LogEntry> Visible { get; } = [];

    [ObservableProperty] public partial int ErrorCount { get; set; }
    [ObservableProperty] public partial int WarningCount { get; set; }

    /// <summary>"all", "errors" or "warnings".</summary>
    [ObservableProperty] public partial string Filter { get; set; } = "all";

    public string ErrorsLabel => $"Errors {ErrorCount}";
    public string WarningsLabel => $"Warnings {WarningCount}";
    public bool ShowAll { get => Filter == "all"; set { if (value) Filter = "all"; } }
    public bool ShowErrors { get => Filter == "errors"; set { if (value) Filter = "errors"; } }
    public bool ShowWarnings { get => Filter == "warnings"; set { if (value) Filter = "warnings"; } }

    public void Info(string message, string? module = null) => Add(LogLevel.Info, message, module);
    public void Warning(string message, string? module = null) => Add(LogLevel.Warning, message, module);
    public void Error(string message, string? module = null) => Add(LogLevel.Error, message, module);

    public void Add(LogLevel level, string message, string? module = null)
    {
        var entry = new LogEntry(DateTime.Now, level, message, module);
        _all.Add(entry);
        if (level == LogLevel.Error)
            ErrorCount++;
        else if (level == LogLevel.Warning)
            WarningCount++;
        if (Matches(entry))
            Visible.Add(entry);
    }

    public void Clear()
    {
        _all.Clear();
        Visible.Clear();
        ErrorCount = WarningCount = 0;
    }

    /// <summary>The visible entries as plain text (for the clipboard).</summary>
    public string Text()
    {
        var sb = new StringBuilder();
        foreach (var e in Visible)
            sb.AppendLine(e.ToString());
        return sb.ToString();
    }

    bool Matches(LogEntry e) => Filter switch
    {
        "errors" => e.Level == LogLevel.Error,
        "warnings" => e.Level == LogLevel.Warning,
        _ => true,
    };

    partial void OnFilterChanged(string value)
    {
        Visible.Clear();
        foreach (var e in _all.Where(Matches))
            Visible.Add(e);
        OnPropertyChanged(nameof(ShowAll));
        OnPropertyChanged(nameof(ShowErrors));
        OnPropertyChanged(nameof(ShowWarnings));
    }

    partial void OnErrorCountChanged(int value) => OnPropertyChanged(nameof(ErrorsLabel));
    partial void OnWarningCountChanged(int value) => OnPropertyChanged(nameof(WarningsLabel));
}
