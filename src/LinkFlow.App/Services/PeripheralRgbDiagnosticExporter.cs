using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinkFlow.App.Services;

public sealed record PeripheralRgbDiagnosticExportStatus
{
    public bool Enabled { get; init; }
    public bool Stopped { get; init; }
    public bool StoppedAfterFailures { get; init; }
    public long Attempts { get; init; }
    public long SuccessfulWrites { get; init; }
    public long FailedWrites { get; init; }
    public int ConsecutiveFailures { get; init; }
    public DateTimeOffset? LastAttemptUtc { get; init; }
    public DateTimeOffset? LastSuccessUtc { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? LastFailureUtc { get; init; }
    public string? LastFailure { get; init; }
    public int? LastFailureHResult { get; init; }
    public string? LastFailureStage { get; init; }
}

public sealed record PeripheralRgbDiagnosticExport
{
    public int Schema { get; init; } = 1;
    public DateTimeOffset CapturedAtUtc { get; init; }
    public long CapturedTimestamp { get; init; }
    public double? InFlightElapsedMilliseconds { get; init; }
    public PeripheralRgbDiagnosticSnapshot Snapshot { get; init; } = new();
    public PeripheralRgbDiagnosticExportStatus ExporterStatusAtCapture { get; init; } = new();
}

/// <summary>One independent worker and one replaceable, bounded current-state file. Never enters the RGB backend.</summary>
internal sealed class PeripheralRgbDiagnosticExporter : IDisposable
{
    internal const int MaximumBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter() } };
    private readonly string _path;
    private readonly string _temporary;
    private readonly Func<PeripheralRgbDiagnosticSnapshot> _readSnapshot;
    private readonly TimeSpan _interval;
    private readonly int _maximumFailures;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private PeripheralRgbDiagnosticExportStatus _status = new() { Enabled = true };
    private int _disposed;

    internal PeripheralRgbDiagnosticExportStatus Status => Volatile.Read(ref _status);
    internal Task Completion => _worker;

    // A shorter interval/failure budget is only available to isolated fake tests, not the public bridge API.
    internal PeripheralRgbDiagnosticExporter(string path, Func<PeripheralRgbDiagnosticSnapshot> readSnapshot,
        TimeSpan? interval = null, int maximumFailures = 8)
    {
        _path = Path.GetFullPath(path);
        _temporary = Path.Combine(Path.GetDirectoryName(_path)!, "." + Path.GetFileName(_path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        _readSnapshot = readSnapshot;
        _interval = interval ?? TimeSpan.FromSeconds(1);
        if (_interval <= TimeSpan.Zero || _interval > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(interval));
        _maximumFailures = Math.Clamp(maximumFailures, 1, 8);
        _worker = Task.Run(RunAsync);
    }

    private static long Next(long value) => value == long.MaxValue ? value : value + 1;

    private async Task RunAsync()
    {
        long previousFinished = 0;
        TimeSpan delay = TimeSpan.Zero;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                // Delay from completion, with a monotonic fence. A delayed timer never creates a catch-up burst.
                while (previousFinished != 0)
                {
                    var remaining = delay - Stopwatch.GetElapsedTime(previousFinished);
                    if (remaining <= TimeSpan.Zero) break;
                    await Task.Delay(remaining, _stop.Token).ConfigureAwait(false);
                }
                _stop.Token.ThrowIfCancellationRequested();
                var attemptedAt = DateTimeOffset.UtcNow;
                var status = Status with { Attempts = Next(Status.Attempts), LastAttemptUtc = attemptedAt };
                Volatile.Write(ref _status, status);
                bool temporaryCreated = false;
                string stage = "snapshot";
                try
                {
                    var snapshot = _readSnapshot();
                    long captured = Stopwatch.GetTimestamp();
                    var export = new PeripheralRgbDiagnosticExport { CapturedAtUtc = attemptedAt, CapturedTimestamp = captured,
                        Snapshot = snapshot, ExporterStatusAtCapture = status,
                        InFlightElapsedMilliseconds = snapshot.InFlight is { } operation
                            ? Stopwatch.GetElapsedTime(operation.StartedTimestamp, captured).TotalMilliseconds : null };
                    stage = "serialize";
                    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(export, JsonOptions);
                    if (bytes.Length > MaximumBytes) throw new InvalidDataException("Peripheral diagnostic payload exceeds 64 KiB.");
                    stage = "create-directory";
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    stage = "create-temporary";
                    await using (var stream = new FileStream(_temporary, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 8192, FileOptions.Asynchronous))
                    {
                        temporaryCreated = true;
                        stage = "write-temporary";
                        await stream.WriteAsync(bytes, _stop.Token).ConfigureAwait(false);
                    }
                    _stop.Token.ThrowIfCancellationRequested();
                    stage = "replace-current";
                    // On Windows Move(overwrite:true) can deny access even when a reader grants
                    // FileShare.Delete. Replace preserves atomic publication with those readers.
                    if (File.Exists(_path)) File.Replace(_temporary, _path, destinationBackupFileName: null);
                    else File.Move(_temporary, _path);
                    temporaryCreated = false;
                    Volatile.Write(ref _status, status with { SuccessfulWrites = Next(status.SuccessfulWrites),
                        ConsecutiveFailures = 0, LastSuccessUtc = DateTimeOffset.UtcNow, LastError = null });
                    delay = _interval;
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    int failures = status.ConsecutiveFailures + 1;
                    bool exhausted = failures >= _maximumFailures;
                    string detail = PeripheralRgbDiagnosticRecorder.ErrorText(error);
                    Volatile.Write(ref _status, status with { FailedWrites = Next(status.FailedWrites),
                        ConsecutiveFailures = failures, LastError = detail,
                        LastFailureUtc = DateTimeOffset.UtcNow, LastFailure = detail,
                        LastFailureHResult = error.HResult, LastFailureStage = stage,
                        StoppedAfterFailures = exhausted });
                    if (exhausted) break;
                    delay = TimeSpan.FromMilliseconds(Math.Min(30_000, _interval.TotalMilliseconds * (1 << Math.Min(failures, 5))));
                }
                finally
                {
                    if (temporaryCreated)
                    {
                        try { File.Delete(_temporary); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                    }
                }
                previousFinished = Stopwatch.GetTimestamp();
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            Volatile.Write(ref _status, Status with { Stopped = true });
            _stop.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // No join on the dispatcher. Existing filesystem I/O can finish on this worker after cancellation.
        try { _stop.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}
