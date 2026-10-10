using System.Security.Cryptography;
using System.Text.Json;

namespace Ps5Backport.Core;

/// <summary>One-shot operation state; terminal states never transition back to running.</summary>
public enum OperationState { Pending, Running, Completed, Failed, Cancelled }

/// <summary>Evidence from a successful full analysis. Cook checks it again before staging any output.</summary>
public sealed class AnalysisReceipt
{
    internal string Fingerprint { get; }
    internal AnalysisReceipt(string fingerprint) => Fingerprint = fingerprint;
}

/// <summary>A private, detached copy of the request and loaded rules, captured before background work starts.</summary>
internal sealed record RunSnapshot(string Source, string Output, BackportOptions Options, Knowledge Knowledge)
{
    internal static RunSnapshot Capture(string source, string output, BackportOptions options, Knowledge knowledge) => new(
        Path.GetFullPath(source), Path.GetFullPath(output), options with
        {
            ReferenceDir = Path.GetFullPath(options.ReferenceDir),
            ExtraFakelib = options.ExtraFakelib is null ? null : Path.GetFullPath(options.ExtraFakelib),
            FakelibStore = options.FakelibStore is null ? null : Path.GetFullPath(options.FakelibStore),
            Options = Array.AsReadOnly(options.Options.ToArray()), StubNids = Array.AsReadOnly(options.StubNids.ToArray()),
        }, knowledge.Snapshot());

    internal string Fingerprint(RunEvidence evidence, string fakelibSource) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            Source, Options = Options with { DryRun = false, Names = null }, Names = Options.Names?.Fingerprint,
            Knowledge, FakelibSource = fakelibSource, Inputs = evidence.Files.OrderBy(p => p.Key, StringComparer.Ordinal),
        }, new JsonSerializerOptions { IncludeFields = true })));
}

/// <summary>Owns a single backport, its cancellation source and its captured request. Await execution before disposal.</summary>
public sealed class BackportOperation : IDisposable
{
    readonly RunSnapshot _snapshot;
    readonly AnalysisReceipt? _analysis;
    readonly DiagnosticLog? _diagnostics;
    readonly CancellationTokenSource _cancel = new();
    int _state;
    /// <summary>Current state, safe to inspect from a progress callback.</summary>
    public OperationState State => (OperationState)Volatile.Read(ref _state);
    /// <summary>Local correlation identifier for this operation's diagnostic events.</summary>
    public string RunId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Captures paths, options and rules immediately; optional analysis must describe the same inputs/settings.</summary>
    public BackportOperation(string source, string output, BackportOptions options, Knowledge knowledge, AnalysisReceipt? analysis = null,
                             DiagnosticLog? diagnostics = null)
    {
        _snapshot = RunSnapshot.Capture(source, output, options, knowledge);
        _analysis = analysis;
        _diagnostics = diagnostics;
    }

    /// <summary>Requests cooperative cancellation; an already committed publication remains completed.</summary>
    public void Cancel() => _cancel.Cancel();

    /// <summary>Runs the shared engine once. Failure/cancellation sets a terminal state and preserves the exception.</summary>
    public async Task<GameResult> ExecuteAsync(IProgress<BackportProgress>? progress = null, CancellationToken cancel = default)
    {
        if (Interlocked.CompareExchange(ref _state, (int)OperationState.Running, (int)OperationState.Pending) != (int)OperationState.Pending)
            throw new InvalidOperationException("A backport operation can only run once");
        using var registration = cancel.Register(Cancel);
        try
        {
            // Enter the engine even for pre-cancellation so it records the terminal diagnostic state.
            var result = await Task.Run(() => Pipeline.Execute(_snapshot, progress, _cancel.Token, _analysis, _diagnostics, RunId)).ConfigureAwait(false);
            Volatile.Write(ref _state, (int)result.State);
            return result;
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
        {
            Volatile.Write(ref _state, (int)OperationState.Cancelled);
            throw;
        }
        catch
        {
            Volatile.Write(ref _state, (int)OperationState.Failed);
            throw;
        }
    }

    /// <summary>Releases the cancellation source after pending or terminal work; running work must be awaited first.</summary>
    public void Dispose()
    {
        if (State == OperationState.Running)
            throw new InvalidOperationException("Cancel and await the operation before disposing it");
        _cancel.Dispose();
    }
}
