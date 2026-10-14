using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ps5Backport.Core;

namespace Ps5Backport.App.ViewModels;

public partial class MainViewModel
{
    int _presentationRevision;

    /// <summary>The displayed results were produced before a relevant UI setting changed.</summary>
    [ObservableProperty] public partial bool IsAnalysisStale { get; set; }

    /// <summary>Preserves the target of the displayed result when the firmware picker changes.</summary>
    public string ResultTargetText => Result is { } result ? $"Results for firmware {result.TargetFirmware}" : "";
    public bool HasModules => Modules.Count > 0;
    public bool HasImports => Imports.Count > 0;
    public string ImportTabText => $"Shown: {Imports.Count}";
    public string ModulesEmptyText => HasResult ? "No modules were found in this result." : "Analyze a game to review its modules and proposed changes.";
    public string ImportsEmptyText => !HasResult ? "Analyze a game to inspect its imports."
        : _allImports.Count == 0 ? "No imports were found in this result." : "No imports match your filters. Try another search or show all imports.";
    public IEnumerable<WarningRow> WarningDetails => Warnings.Select(w => new WarningRow(w));

    /// <summary>A published result remains a record of the last output, independent of the editable output path.</summary>
    public bool HasPublishedResult => Result?.Published == true;
    public string PublishedOutput => HasPublishedResult ? Result!.Output : "";
    public string CompletionText => HasPublishedResult
        ? $"{Result!.Modules.Count(m => m.Written)} module files written for firmware {Result.TargetFirmware}. " +
          (UnresolvedCount > 0 ? $"{UnresolvedCount} {(UnresolvedCount == 1 ? "import remains" : "imports remain")} unresolved; review before testing." : "Review the report before testing on the console.") : "";

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(SelectedFirmware) or nameof(KnownFixes) or nameof(CandidateFixes)
            or nameof(StubUnknown) or nameof(DefineSymbol) or nameof(ExtendText) or nameof(PatchParam) or nameof(AmprDebugLogging)
            or nameof(BootNoticeMessage))
            MarkAnalysisStale();
    }

    void MarkAnalysisStale()
    {
        _presentationRevision++;
        if (HasResult) IsAnalysisStale = true;
    }

    [RelayCommand]
    void ClearImportFilters()
    {
        ImportSearch = "";
        ImportFilter = "All";
        SelectedModule = null;
    }

    bool CanOpenPublishedOutput() => !_closing && HasPublishedResult && Directory.Exists(PublishedOutput);

    [RelayCommand(CanExecute = nameof(CanOpenPublishedOutput))]
    void OpenPublishedOutput()
    {
        if (CanOpenPublishedOutput())
        {
            using var process = Process.Start(new ProcessStartInfo(PublishedOutput) { UseShellExecute = true });
        }
    }

    bool CanBuildPublishedAmprIndex() => !Busy && CanOpenPublishedOutput() && Game is not null;

    /// <summary>Indexes the captured published output, even if the editable destination has since changed.</summary>
    [RelayCommand(CanExecute = nameof(CanBuildPublishedAmprIndex))]
    Task BuildPublishedAmprIndex() => CanBuildPublishedAmprIndex()
        ? _activeWork = BuildAmprIndexCore(PublishedOutput) : Task.CompletedTask;

    void NotifyResultPresentation()
    {
        OnPropertyChanged(nameof(HasModules));
        OnPropertyChanged(nameof(HasImports));
        OnPropertyChanged(nameof(ImportTabText));
        OnPropertyChanged(nameof(ModulesEmptyText));
        OnPropertyChanged(nameof(ImportsEmptyText));
        OnPropertyChanged(nameof(WarningDetails));
        OnPropertyChanged(nameof(ResultTargetText));
        OnPropertyChanged(nameof(HasPublishedResult));
        OnPropertyChanged(nameof(PublishedOutput));
        OnPropertyChanged(nameof(CompletionText));
        OpenPublishedOutputCommand.NotifyCanExecuteChanged();
        BuildPublishedAmprIndexCommand.NotifyCanExecuteChanged();
    }
}
