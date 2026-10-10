using System.Diagnostics;
using System.Text.Json;
using Ps5Backport.Core;

/// <summary>Measures cancellation requested at the start of a synchronous module load.</summary>
static class CancellationExperiment
{
    internal static void Run(string source, string reference, string report)
    {
        var records = new List<double>();
        for (int i = 0; i < 3; i++)
        {
            using var cancel = new CancellationTokenSource();
            var progress = new CancelProgress(cancel);
            try
            {
                Pipeline.Backport(source, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "cancel-output"),
                    new BackportOptions { Firmware = "4", ReferenceDir = reference, DryRun = true, KnownFixes = false },
                    Knowledge.Default, progress, cancel.Token);
                throw new InvalidOperationException("Cancellation was not observed");
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                records.Add(progress.Elapsed.Elapsed.TotalMilliseconds);
            }
        }
        File.WriteAllText(report, JsonSerializer.Serialize(records));
    }

    sealed class CancelProgress(CancellationTokenSource cancel) : IProgress<BackportProgress>
    {
        internal Stopwatch Elapsed { get; } = new();
        public void Report(BackportProgress progress)
        {
            if (progress.Stage == "loading" && !cancel.IsCancellationRequested)
            {
                Elapsed.Start();
                cancel.Cancel();
            }
        }
    }
}
