namespace Ps5Backport.Core;

public static partial class Pipeline
{
    static void FinishRun(GameResult result, BackportOptions o, Reference reference, RunEvidence evidence,
        DirectoryTransaction? publication, string writeDir, string fingerprint, int moduleCount,
        IProgress<BackportProgress>? progress, CancellationToken cancel, Knowledge knowledge)
    {
        if (!o.DryRun)
        {
            WriteReports(result, writeDir, knowledge);
            if (result.Modules.Any(m => m.Error.Length > 0))
                result.DiagnosticDirectory = publication!.RetainDiagnostic();
            else
            {
                evidence!.Write(writeDir, o, result, reference);
                progress?.Report(new BackportProgress(0, moduleCount, "", "publishing"));
                evidence.Verify();
                publication!.Commit(cancel);
                result.Published = true;
                if (Directory.Exists(publication.Backup)) // the commit discards it; still here only if that failed
                    result.PreviousOutputBackup = publication.Backup;
            }
        }
        if (o.DryRun) evidence.Verify();
        result.State = result.Modules.Any(m => m.Error.Length > 0) ? OperationState.Failed : OperationState.Completed;
        if (o.DryRun && result.State == OperationState.Completed)
            result.Analysis = new AnalysisReceipt(fingerprint);
        progress?.Report(new BackportProgress(result.State == OperationState.Completed ? moduleCount : 0,
            moduleCount, "", result.State == OperationState.Completed ? "done" : "failed"));
    }
}
