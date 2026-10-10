using System.Diagnostics;
using System.Text.Json;

namespace Ps5Backport.Core;

/// <summary>One local diagnostic event; messages and exception details can contain private file paths.</summary>
public sealed record DiagnosticEvent(string Operation, string Event, string Stage = "", string? RunId = null,
    string? Module = null, double? DurationMilliseconds = null, string? Message = null,
    string? ErrorCategory = null, string? ExceptionType = null, string? ExceptionContext = null)
{
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>A bounded, single-writer JSON-lines log. Logging failure never fails an operation.</summary>
public sealed class DiagnosticLog : IDisposable
{
    public const int MaximumFileBytes = 1024 * 1024;
    public const int RetainedFiles = 4;
    readonly object _gate = new();
    readonly string _directory;
    readonly int _maximumBytes, _retainedFiles;
    FileStream? _lease;
    bool _disposed;
    string? _failure;
    public string? Failure => Volatile.Read(ref _failure);
    public string DirectoryPath => _directory;

    /// <summary>Production defaults: four files of at most 1 MiB each, plus an empty writer lock.</summary>
    public DiagnosticLog(string directory) : this(directory, MaximumFileBytes, RetainedFiles) { }

    internal DiagnosticLog(string directory, int maximumBytes, int retainedFiles)
    {
        _directory = Path.GetFullPath(directory);
        _maximumBytes = maximumBytes;
        _retainedFiles = retainedFiles;
        try
        {
            Directory.CreateDirectory(_directory);
            Paths.RequireNoLinks(_directory, tree: true);
            _lease = new FileStream(Path.Combine(_directory, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            _lease.SetLength(0);
        }
        catch (Exception e) when (IsFileFailure(e)) { _failure = e.Message; _lease?.Dispose(); _lease = null; }
    }

    /// <summary>Per-user local diagnostics, separated by desktop/CLI to avoid competing writers.</summary>
    public static string DefaultDirectory(string client) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PS5BackportStudio", "logs", client);

    static bool IsFileFailure(Exception e) => e is IOException or UnauthorizedAccessException;
    string FileAt(int generation) => Path.Combine(_directory, generation == 0 ? "events.jsonl" : $"events.{generation}.jsonl");
    static string? Clip(string? value, int maximum) => value?.Length > maximum ? value[..maximum] : value;

    /// <summary>Appends synchronously and closes the event file; clean process exit needs no queued flush.</summary>
    public void Write(DiagnosticEvent entry)
    {
        lock (_gate)
        {
            if (_disposed || _failure is not null) return;
            try
            {
                // Bound caller strings before serialization. No binary payloads or settings snapshots are logged.
                entry = entry with { Operation = Clip(entry.Operation, 128)!, Event = Clip(entry.Event, 128)!,
                    Stage = Clip(entry.Stage, 128)!, RunId = Clip(entry.RunId, 128), Module = Clip(entry.Module, 1024),
                    Message = Clip(entry.Message, 2048), ErrorCategory = Clip(entry.ErrorCategory, 128),
                    ExceptionType = Clip(entry.ExceptionType, 256), ExceptionContext = Clip(entry.ExceptionContext, 4096) };
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(entry);
                if (bytes.Length + 1 > _maximumBytes) return; // A test's smaller file budget can refuse oversized records.
                Paths.RequireNoLinks(_directory, tree: true);
                string current = FileAt(0);
                if (File.Exists(current) && new FileInfo(current).Length + bytes.Length + 1 > _maximumBytes)
                {
                    File.Delete(FileAt(_retainedFiles - 1));
                    for (int i = _retainedFiles - 2; i >= 0; i--)
                        if (File.Exists(FileAt(i))) File.Move(FileAt(i), FileAt(i + 1));
                }
                using var file = new FileStream(current, FileMode.Append, FileAccess.Write, FileShare.Read);
                file.Write(bytes);
                file.WriteByte((byte)'\n');
            }
            catch (Exception e) when (IsFileFailure(e)) { _failure = e.Message; }
        }
    }

    /// <summary>Stable categories for local filtering; exception type/context preserve the specific failure.</summary>
    public static string Category(Exception e) => e switch
    {
        OperationCanceledException => "cancelled", PatchException => "patch", System.Text.Json.JsonException => "metadata",
        InvalidDataException => "data", UnauthorizedAccessException => "access", IOException => "io",
        ArgumentException or FormatException => "validation", _ => "unexpected",
    };

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _lease?.Dispose(); _lease = null; }
    }
}

/// <summary>Measures run and progress-boundary intervals without changing engine report/progress semantics.</summary>
internal sealed class DiagnosticRun : IProgress<BackportProgress>
{
    readonly DiagnosticLog? _log;
    readonly IProgress<BackportProgress>? _progress;
    readonly Stopwatch _timer = Stopwatch.StartNew();
    readonly string _operation, _runId;
    string _stage = "preflight", _module = "";
    double _stageStart;

    internal DiagnosticRun(DiagnosticLog? log, string operation, string runId, IProgress<BackportProgress>? progress)
    {
        _log = log; _operation = operation; _runId = runId; _progress = progress;
        _log?.Write(new(operation, "started", _stage, runId));
    }

    public void Report(BackportProgress value)
    {
        if (_stage != value.Stage || _module != value.Module)
        {
            EndStage();
            _stage = value.Stage; _module = value.Module; _stageStart = _timer.Elapsed.TotalMilliseconds;
        }
        _progress?.Report(value);
    }

    void EndStage() => _log?.Write(new(_operation, "stage_finished", _stage, _runId, _module,
        _timer.Elapsed.TotalMilliseconds - _stageStart));

    internal void ModuleError(string stage, string module, Exception error) =>
        _log?.Write(new(_operation, "module_failed", stage, _runId, module, Message: error.Message,
            ErrorCategory: DiagnosticLog.Category(error), ExceptionType: error.GetType().FullName, ExceptionContext: error.ToString()));

    internal void Finish(OperationState state, Exception? error = null)
    {
        EndStage();
        _log?.Write(new(_operation, state.ToString().ToLowerInvariant(), _stage, _runId,
            DurationMilliseconds: _timer.Elapsed.TotalMilliseconds, Message: error?.Message,
            ErrorCategory: error is null ? state == OperationState.Failed ? "module" : null : DiagnosticLog.Category(error),
            ExceptionType: error?.GetType().FullName, ExceptionContext: error?.ToString()));
    }
}
