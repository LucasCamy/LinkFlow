namespace LinkFlow.Core;

/// <summary>
/// Owns only the RGB worker. Sensors and cooling never enter this process or its serialization lock.
/// A rejected frame is returned as rejected; recovery is requested by the existing RGB supervisor.
/// </summary>
public sealed class IsolatedGpuRgbDevice : IRgbDevice, IRgbControlStatusProvider
{
    private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ColorTimeout = TimeSpan.FromSeconds(2);
    private readonly object _gate = new();
    private readonly Func<IGpuRgbWorkerSession> _start;
    private IGpuRgbWorkerSession? _session;
    private RgbControlStatus? _childStatus;
    private RgbNativeFailureHistory? _failureHistory;
    private RgbControlStatus _published = new();
    private bool _disposed, _faulted, _hadWorker;
    private int? _processId;
    private long _generation, _requestId, _restarts, _transportFailures;
    private long _nativeFailures, _outputFailures, _initializations, _consecutiveFailures, _nativeSequence, _sequenceBase, _failureOrdinalBase;
    private string? _restartReason, _replacementReason, _lastWorkerFailure;
    private DateTimeOffset? _lastWorkerFailureUtc;

    public IsolatedGpuRgbDevice(string executablePath) : this(CreateStarter(executablePath)) { }
    internal IsolatedGpuRgbDevice(Func<IGpuRgbWorkerSession> start) => _start = start ?? throw new ArgumentNullException(nameof(start));

    // All presentation paths read one immutable publication: never process state, pipes or the mutation lock.
    public RgbControlStatus ControlStatus => Volatile.Read(ref _published);
    public string Name => ControlStatus.ControllerName;
    public bool IsAvailable => ControlStatus.ControlAvailable;
    public bool NeedsInitializationRetry => ControlStatus.InitializationRetryNeeded;
    public EngineText? DiagnosticText => ControlStatus.DiagnosticText;
    public string? Diagnostic => ControlStatus.Diagnostic;

    public void Initialize()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var previous = ControlStatus;
            if (_session != null && !_faulted)
            {
                try
                {
                    if (_session.HasExited) WorkerFailure("worker-exited", "The RGB worker exited.");
                    else if (previous.ControlAvailable && !previous.InitializationRetryNeeded && previous.LastOutputAccepted != false)
                        return;
                }
                catch (Exception ex) { WorkerFailure("worker-state-failed", Describe(ex)); }
            }

            Increment(ref _initializations);
            string reason = _replacementReason ?? (previous.LastOutputAccepted == false ? "output-rejected" : "control-unavailable");
            Publish(previous with { Initializing = true, ControlAvailable = false, LastOutputAccepted = null, InitializationRetryNeeded = true });
            try
            {
                // A failed Dispose or an unconfirmed exit retains ownership. No factory call may bypass this barrier.
                if (!RetireSession()) return;
                if (_generation == long.MaxValue)
                {
                    WorkerFailure("generation-exhausted", "The RGB worker generation limit was reached.");
                    return;
                }
                _generation++;
                _sequenceBase = _nativeSequence;
                _failureOrdinalBase = _nativeFailures;
                _childStatus = null;
                _session = _start() ?? throw new InvalidOperationException("The RGB worker factory returned no session.");
                _faulted = false;
                if (_hadWorker) { Increment(ref _restarts); _restartReason = reason; }
                _hadWorker = true;
                int processId = _session.ProcessId;
                if (processId <= 0) throw new InvalidDataException("The RGB worker process identity is invalid.");
                _processId = processId;
                if (_session.HasExited) throw new EndOfStreamException("The RGB worker exited before initialization.");
                Publish(ControlStatus);
                var reply = Exchange(GpuRgbWorkerProtocol.Initialize, InitializeTimeout);
                Accept(reply, isColor: false);
                _replacementReason = null;
            }
            catch (Exception ex)
            {
                WorkerFailure(Reason(ex), Describe(ex));
                RetireSession();
            }
            finally { Publish(ControlStatus with { Initializing = false }); }
        }
    }

    public bool SetColor(byte r, byte g, byte b, byte brightnessPercent)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            var attemptedAt = DateTimeOffset.UtcNow;
            try
            {
                if (_session == null || _faulted) return RejectLocalOutput(attemptedAt);
                if (_session.HasExited) throw new EndOfStreamException("The RGB worker exited before the color request.");
                var reply = Exchange(GpuRgbWorkerProtocol.Color, ColorTimeout, r, g, b, brightnessPercent);
                Accept(reply, isColor: true);
                // Native -1 can recover on the next frame in this same child. Do not restart or retry it here.
                return reply.Accepted;
            }
            catch (Exception ex)
            {
                RejectLocalOutput(attemptedAt);
                WorkerFailure(Reason(ex), Describe(ex));
                RetireSession();
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true; // Also blocks Initialize after an unsuccessful termination attempt.
            Publish(ControlStatus with { ControlAvailable = false, Initializing = false, InitializationRetryNeeded = false, LastOutputAccepted = null });
            if (!RetireSession())
            {
                Publish(ControlStatus with { InitializationRetryNeeded = false });
                throw new InvalidOperationException("The RGB worker exit could not be confirmed; its ownership is retained.");
            }
        }
    }

    private GpuRgbWorkerReply Exchange(string operation, TimeSpan timeout, byte r = 0, byte g = 0, byte b = 0, byte brightness = 0)
    {
        if (_requestId == long.MaxValue) throw new InvalidDataException("The RGB request identity limit was reached.");
        var request = new GpuRgbWorkerRequest
        {
            Generation = _generation, RequestId = ++_requestId, Operation = operation,
            R = r, G = g, B = b, Brightness = (byte)Math.Min((int)brightness, 100)
        };
        var reply = _session!.Exchange(request, timeout);
        Validate(reply, request);
        return reply;
    }

    private void Accept(GpuRgbWorkerReply reply, bool isColor)
    {
        var raw = reply.Status;
        var old = ControlStatus;
        long nextNativeFailures = Add(_nativeFailures, raw.TotalNativeFailures - (_childStatus?.TotalNativeFailures ?? 0));
        var nextHistory = GpuRgbNativeFailureHistory.Merge(_failureHistory, _nativeFailures, nextNativeFailures,
            reply.NativeFailureBatch, _sequenceBase, _failureOrdinalBase, raw.LastNativeFailure, old.LastNativeFailure);
        // Validation and the complete immutable history merge succeeded before advancing any acknowledgement counters.
        _nativeFailures = nextNativeFailures;
        _failureHistory = nextHistory;
        _outputFailures = Add(_outputFailures, raw.TotalOutputFailures - (_childStatus?.TotalOutputFailures ?? 0));
        _nativeSequence = Add(_sequenceBase, raw.LastNativeOperation?.Sequence ?? 0);
        if (isColor)
        {
            if (reply.Accepted) _consecutiveFailures = 0;
            else Increment(ref _consecutiveFailures);
        }
        _childStatus = raw;
        Publish(raw with
        {
            LastAdapterEnumerationUtc = raw.LastAdapterEnumerationUtc ?? old.LastAdapterEnumerationUtc,
            LastAdapterSeenUtc = raw.LastAdapterSeenUtc ?? old.LastAdapterSeenUtc,
            LastOutputAttemptUtc = raw.LastOutputAttemptUtc ?? old.LastOutputAttemptUtc,
            LastOutputAcceptedUtc = raw.LastOutputAcceptedUtc ?? old.LastOutputAcceptedUtc,
            LastOutputAcceptedNativeSequence = raw.LastOutputAcceptedNativeSequence is { } accepted
                ? Add(_sequenceBase, accepted) : old.LastOutputAcceptedNativeSequence,
            LastNativeOperation = Map(raw.LastNativeOperation) ?? old.LastNativeOperation,
            LastNativeFailure = Map(raw.LastNativeFailure) ?? old.LastNativeFailure
        });
    }

    private bool RejectLocalOutput(DateTimeOffset at)
    {
        Increment(ref _outputFailures);
        Increment(ref _consecutiveFailures);
        Publish(ControlStatus with { LastOutputAccepted = false, LastOutputAttemptUtc = at });
        return false;
    }

    private bool RetireSession()
    {
        var session = _session;
        if (session == null) return true;
        _faulted = true;
        Exception? failure = null;
        try { session.Dispose(); }
        catch (Exception ex) { failure = ex; }
        try
        {
            if (!session.HasExited)
            {
                WorkerFailure("worker-termination-unconfirmed", failure == null ? "The RGB worker did not confirm exit." : Describe(failure));
                return false;
            }
        }
        catch (Exception ex)
        {
            WorkerFailure("worker-termination-unconfirmed", Describe(ex));
            return false;
        }
        // A confirmed dead process cannot overlap its replacement even when handle cleanup reported an error.
        if (failure != null) WorkerFailure("worker-cleanup-failed", Describe(failure));
        _session = null;
        _processId = null;
        Publish(ControlStatus);
        return true;
    }

    private void WorkerFailure(string reason, string message)
    {
        _faulted = true;
        _replacementReason = reason;
        Increment(ref _transportFailures);
        _lastWorkerFailureUtc = DateTimeOffset.UtcNow;
        _lastWorkerFailure = Limit(reason + ": " + message, 512);
        Publish(ControlStatus with
        {
            ControlAvailable = false, InitializationRetryNeeded = !_disposed, LastOutputAccepted = false,
            DiagnosticText = EngineText.Of(EngineTexts.GpuRgbWorkerFailed, _lastWorkerFailure)
        });
    }

    private void Publish(RgbControlStatus status) => Volatile.Write(ref _published, status with
    {
        InitializationAttempts = _initializations, TotalNativeFailures = _nativeFailures,
        TotalOutputFailures = _outputFailures, ConsecutiveOutputFailures = _consecutiveFailures,
        WorkerProcessId = _processId, WorkerGeneration = _generation, WorkerRestartCount = _restarts,
        WorkerTransportFailures = _transportFailures, LastWorkerRestartReason = _restartReason,
        LastWorkerFailureUtc = _lastWorkerFailureUtc, LastWorkerFailure = _lastWorkerFailure,
        NativeFailureHistory = _failureHistory
    });

    private RgbNativeOperationResult? Map(RgbNativeOperationResult? operation) => operation == null ? null
        : GpuRgbNativeFailureHistory.Map(operation, _sequenceBase, _failureOrdinalBase);

    private void Validate(GpuRgbWorkerReply? reply, GpuRgbWorkerRequest request)
    {
        if (reply == null || reply.Version != GpuRgbWorkerProtocol.Version || reply.Generation != request.Generation || reply.RequestId != request.RequestId)
            throw new InvalidDataException("The RGB reply protocol, generation or request identity is invalid.");
        var s = reply.Status;
        if (s == null || !Bounded(s.Source, 128, allowEmpty: false) || !Bounded(s.ControllerName, 128) || s.ControllerLedCount is < 0 or > 32 ||
            !Enum.IsDefined(s.AdapterObservation) || s.Initializing || s.InitializationAttempts != 1 ||
            s.TotalNativeFailures < 0 || s.TotalOutputFailures < 0 || s.ConsecutiveOutputFailures < 0 ||
            s.TotalNativeFailures < (_childStatus?.TotalNativeFailures ?? 0) || s.TotalOutputFailures < (_childStatus?.TotalOutputFailures ?? 0) ||
            s.WorkerProcessId != null || s.WorkerGeneration != 0 || s.WorkerRestartCount != 0 || s.WorkerTransportFailures != 0 ||
            s.LastWorkerRestartReason != null || s.LastWorkerFailureUtc != null || s.LastWorkerFailure != null ||
            s.RecoveryPending || s.RecoveryAttemptCount != 0 || s.RecoveryBackoffStep != 0 || s.NextInitializationAttemptUtc != null)
            throw new InvalidDataException("The RGB reply status has invalid or unbounded fields.");

        long sequence = s.LastNativeOperation?.Sequence ?? 0, previousSequence = _childStatus?.LastNativeOperation?.Sequence ?? 0;
        int textBudget = 2048;
        if (!ValidNative(s.LastNativeOperation) || !ValidNative(s.LastNativeFailure) || sequence < previousSequence ||
            sequence - previousSequence > 8192 ||
            sequence == previousSequence && s.LastNativeOperation != _childStatus?.LastNativeOperation ||
            s.TotalNativeFailures > sequence || (s.TotalNativeFailures > 0) != (s.LastNativeFailure != null) ||
            s.LastNativeFailure is { } failure && (failure.Status == 0 || failure.Sequence > sequence ||
                failure.FailureOrdinal != 0 && failure.FailureOrdinal != s.TotalNativeFailures) ||
            s.LastNativeFailure is { } lastFailure && lastFailure.Sequence == sequence && lastFailure != s.LastNativeOperation ||
            s.LastNativeOperation is { Status: not 0 } lastOperation && lastOperation != s.LastNativeFailure ||
            s.LastOutputAcceptedNativeSequence is { } acceptedSequence && (acceptedSequence <= 0 || acceptedSequence > sequence) ||
            s.ControlAvailable && (s.ControllerName.Length == 0 || s.ControllerLedCount == 0 || s.AdapterObservation != RgbAdapterObservation.Present || s.InitializationRetryNeeded) ||
            !ValidDate(s.LastAdapterEnumerationUtc) || !ValidDate(s.LastAdapterSeenUtc) || !ValidDate(s.LastOutputAttemptUtc) || !ValidDate(s.LastOutputAcceptedUtc) ||
            !ValidText(s.DiagnosticText, 0, ref textBudget))
            throw new InvalidDataException("The RGB reply evidence is inconsistent.");

        var previousFailure = _childStatus?.LastNativeFailure;
        if (previousFailure != null && (s.LastNativeFailure == null || s.LastNativeFailure.Sequence < previousFailure.Sequence ||
            s.LastNativeFailure.Sequence == previousFailure.Sequence && s.LastNativeFailure != previousFailure))
            throw new InvalidDataException("The RGB reply erased or changed native failure evidence.");
        if (s.TotalNativeFailures == (_childStatus?.TotalNativeFailures ?? 0) && previousFailure != s.LastNativeFailure)
            throw new InvalidDataException("The RGB failure evidence changed without a native failure.");
        if (s.TotalNativeFailures - (_childStatus?.TotalNativeFailures ?? 0) > sequence - previousSequence ||
            s.TotalNativeFailures > (_childStatus?.TotalNativeFailures ?? 0) && s.LastNativeFailure!.Sequence <= previousSequence)
            throw new InvalidDataException("The RGB native counter advanced without new native evidence.");
        if (_childStatus?.LastAdapterEnumerationUtc != null && s.LastAdapterEnumerationUtc == null ||
            _childStatus?.LastAdapterSeenUtc != null && s.LastAdapterSeenUtc == null ||
            !reply.Accepted && (s.LastOutputAcceptedUtc != _childStatus?.LastOutputAcceptedUtc ||
                s.LastOutputAcceptedNativeSequence != _childStatus?.LastOutputAcceptedNativeSequence))
            throw new InvalidDataException("The RGB reply erased prior acquisition or accepted-output evidence.");

        long previousOutputs = _childStatus?.TotalOutputFailures ?? 0;
        if (request.Operation == GpuRgbWorkerProtocol.Initialize)
        {
            if (reply.Accepted != s.ControlAvailable || s.LastOutputAccepted != null || s.LastOutputAttemptUtc != null ||
                s.LastOutputAcceptedUtc != null || s.LastOutputAcceptedNativeSequence != null || s.TotalOutputFailures != 0 || s.ConsecutiveOutputFailures != 0)
                throw new InvalidDataException("An RGB initialization reply cannot acknowledge a color.");
        }
        else if (s.LastOutputAccepted != reply.Accepted || s.LastOutputAttemptUtc == null ||
            s.TotalOutputFailures != (reply.Accepted ? previousOutputs : Add(previousOutputs, 1)) ||
            s.ConsecutiveOutputFailures != (reply.Accepted ? 0 : Add(_childStatus?.ConsecutiveOutputFailures ?? 0, 1)) ||
            reply.Accepted && (!s.ControlAvailable || s.LastOutputAcceptedUtc == null ||
                s.LastOutputAcceptedNativeSequence != sequence || sequence <= previousSequence))
            throw new InvalidDataException("The RGB color reply does not confirm this exact output.");

        GpuRgbNativeFailureHistory.Validate(reply.NativeFailureBatch, s, ValidNative, _childStatus);
    }

    private static bool ValidText(EngineText? text, int depth, ref int budget)
    {
        if (text == null) return true;
        if (depth > 4 || !Bounded(text.Code, 128, allowEmpty: false) || text.Args == null || text.Args.Count > 8 ||
            EngineTexts.Template(text.Code, EngineTexts.PortugueseCulture) == null) return false;
        budget -= text.Code.Length;
        foreach (var arg in text.Args)
        {
            if (arg == null || arg.S != null && !Bounded(arg.S, 512) || arg.N is { } n && !double.IsFinite(n)) return false;
            budget -= arg.S?.Length ?? 0;
            if (budget < 0 || !ValidText(arg.T, depth + 1, ref budget)) return false;
        }
        return budget >= 0;
    }

    private bool ValidNative(RgbNativeOperationResult? operation)
    {
        if (operation == null) return true;
        if (operation.Sequence <= 0 || operation.FailureOrdinal < 0 || operation.Status == 0 && operation.FailureOrdinal != 0 ||
            operation.AtUtc == default || !Enum.IsDefined(operation.Operation)) return false;
        if (operation.ProcessId is { } pid && (pid <= 0 || pid != _processId)) return false;
        if (operation.ThreadId is <= 0) return false;
        if (operation.GpuHandle is { } handle && (handle.Length != 16 ||
            !handle.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F'))) return false;
        if (operation.TransferDurationMicroseconds is { } microseconds && (!double.IsFinite(microseconds) || microseconds < 0)) return false;
        if (operation.LibraryVersion is { } version && !Bounded(version, 128)) return false;
        return operation.Operation is RgbNativeOperation.Read or RgbNativeOperation.Write
            ? operation.Register != null && operation.Command != null : operation.Register == null && operation.Command == null;
    }
    private static bool ValidDate(DateTimeOffset? value) => value == null || value != default(DateTimeOffset);
    private static bool Bounded(string? value, int maximum, bool allowEmpty = true) => value != null &&
        (allowEmpty || value.Length > 0) && value.Length <= maximum && !value.Any(char.IsControl);
    private static string Reason(Exception ex) => ex switch
    {
        TimeoutException => "exchange-timeout", InvalidDataException => "invalid-reply", EndOfStreamException => "worker-exited", _ => "worker-transport-failed"
    };
    private static string Describe(Exception ex) => Limit(ex.GetType().Name + ": " + ex.Message, 384);
    private static string Limit(string value, int maximum) => new(value.Where(c => !char.IsControl(c)).Take(maximum).ToArray());
    private static void Increment(ref long value) { if (value < long.MaxValue) value++; }
    private static long Add(long left, long right) => right >= long.MaxValue - left ? long.MaxValue : left + right;
    private static Func<IGpuRgbWorkerSession> CreateStarter(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        return () => GpuRgbWorkerSession.Start(executablePath);
    }
}
