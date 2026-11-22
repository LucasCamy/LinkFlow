using System.Globalization;

namespace LinkFlow.Core;

public sealed partial class OperationalHistory
{
    private sealed record RgbFailureSignature(RgbNativeOperation Operation, int Status, ushort? Register, byte? Command);
    private bool? _gpuControlAvailable, _gpuOutputAccepted;
    private bool _gpuControlSeenAvailable, _gpuNativeFailureEpisode;
    private DateTimeOffset? _gpuControlUnavailableSince, _gpuLastObservedFailureUtc;
    private long _gpuLastObservedFailureSequence;
    private long _gpuWorkerGeneration, _gpuWorkerRestarts, _gpuWorkerTransportFailures;
    private RgbAdapterObservation? _gpuAdapterObservation;
    private RgbFailureSignature? _gpuNativeFailureSignature;
    // The producer exposes the most recent eight received contexts. Its validated suffix contract prevents
    // a known context from reappearing after eviction, while still allowing an earlier missing ordinal to arrive.
    private readonly HashSet<long> _gpuNativeJournalWindow = [];
    private RgbNativeFailureHistory? _gpuNativeHistoryObservation;
    private readonly Queue<RgbNativeOperationResult> _gpuLegacyFailureEventContexts = new(RgbNativeFailureHistory.MaximumRecords);

    internal void ObserveGpuRgbControl(EngineSnapshot snapshot, DateTimeOffset when)
    {
        var status = snapshot.GpuRgbControl;
        ObserveGpuNativeFailureHistory(status, when);
        // Initialization is a pending attempt, not a completed observation of channel or adapter availability.
        // The cached snapshot still exposes the attempt, counters and retained native error to diagnostics.
        if (status?.Initializing == true) return;
        bool available = status?.ControlAvailable ?? snapshot.GpuRgbAvailable;
        bool? accepted = status == null ? snapshot.GpuRgbApplied : status.LastOutputAccepted;
        List<(string Kind, string Message, AlertSeverity Severity, double? Recovery)> changes = [];
        lock (_gate)
        {
            if (status is { WorkerGeneration: > 0, WorkerProcessId: > 0 } && _gpuWorkerGeneration != status.WorkerGeneration)
            {
                bool replaced = _gpuWorkerGeneration > 0 && status.WorkerRestartCount > _gpuWorkerRestarts;
                changes.Add((replaced ? "rgb.worker.restarted" : "rgb.worker.observed",
                    replaced ? "Isolated GPU RGB provider replaced; the engine process was not restarted by this operation" : "Isolated GPU RGB provider observed",
                    AlertSeverity.Info, null));
                _gpuWorkerGeneration = status.WorkerGeneration;
                _gpuWorkerRestarts = status.WorkerRestartCount;
            }
            if (status != null && status.WorkerTransportFailures > _gpuWorkerTransportFailures)
            {
                changes.Add(("rgb.worker.transport.failed", "Communication with the isolated GPU RGB provider failed; this is not a native NVAPI return code",
                    AlertSeverity.Warning, null));
                _gpuWorkerTransportFailures = status.WorkerTransportFailures;
            }
            if (_gpuControlAvailable != available)
            {
                if (available)
                {
                    double? recovered = _gpuControlSeenAvailable && _gpuControlUnavailableSince is { } lost
                        ? Math.Max(0, (when - lost).TotalSeconds) : null;
                    changes.Add((recovered == null ? "rgb.control.detected" : "rgb.control.recovered",
                        recovered == null ? "GPU RGB control channel available" : "GPU RGB control channel recovered",
                        AlertSeverity.Info, recovered));
                    _gpuControlSeenAvailable = true;
                    _gpuControlUnavailableSince = null;
                }
                else if (_gpuControlAvailable == true)
                {
                    _gpuControlUnavailableSince = when;
                    changes.Add(("rgb.control.unavailable", "GPU RGB control channel unavailable; physical connection is not established by this observation",
                        AlertSeverity.Warning, null));
                }
                _gpuControlAvailable = available;
            }

            if (status != null && _gpuAdapterObservation != status.AdapterObservation)
            {
                if (status.AdapterObservation != RgbAdapterObservation.Unknown || _gpuAdapterObservation != null)
                    changes.Add(("rgb.adapter.observation", "GPU adapter enumeration result from the RGB backend: " + status.AdapterObservation,
                        status.AdapterObservation is RgbAdapterObservation.NotDetected or RgbAdapterObservation.EnumerationUnavailable
                            ? AlertSeverity.Warning : AlertSeverity.Info, null));
                _gpuAdapterObservation = status.AdapterObservation;
            }

            if (accepted is { } result)
            {
                if (_gpuOutputAccepted != result)
                    changes.Add((result ? "rgb.output.accepted" : "rgb.output.rejected",
                        result ? "GPU RGB output accepted by the controller; emitted light is unverified" : "GPU RGB output was not accepted by the controller",
                        result ? AlertSeverity.Info : AlertSeverity.Warning, null));
                // A probe without an output attempt is unknown, not a new output result. Retain the last
                // completed result so repeated probe/null/rejection cycles cannot emit once per retry.
                _gpuOutputAccepted = result;
            }

            if (status?.LastNativeFailure is { Status: not 0 } fault)
            {
                var signature = new RgbFailureSignature(fault.Operation, fault.Status, fault.Register, fault.Command);
                // Changed timestamps/counters of the same fault do not grow the journal. A new fault after
                // accepted output starts a new episode even if the SDK code/register/command are identical.
                bool newFailure = fault.Sequence > 0 ? _gpuLastObservedFailureSequence != fault.Sequence
                    : _gpuLastObservedFailureUtc != fault.AtUtc;
                if (newFailure || _gpuNativeFailureSignature != signature)
                {
                    if (!_gpuNativeFailureEpisode || _gpuNativeFailureSignature != signature)
                    {
                        changes.Add(("rgb.transport.failed", "GPU RGB native transport call failed; adapter presence and physical output are not verified by this failure",
                            AlertSeverity.Warning, null));
                        // Retain only the context actually offered to the legacy episode event. The latest
                        // observed/coalesced fault can be newer and must not be mistaken for journal evidence.
                        if (status.NativeFailureHistory == null || fault.FailureOrdinal == 0)
                        {
                            if (_gpuLegacyFailureEventContexts.Count == RgbNativeFailureHistory.MaximumRecords)
                                _gpuLegacyFailureEventContexts.Dequeue();
                            _gpuLegacyFailureEventContexts.Enqueue(fault);
                        }
                    }
                    _gpuLastObservedFailureUtc = fault.AtUtc;
                    _gpuLastObservedFailureSequence = fault.Sequence;
                    _gpuNativeFailureSignature = signature;
                    _gpuNativeFailureEpisode = true;
                }
                // Native sequence establishes call order even if UTC regresses or timestamps repeat.
                // Old providers without a sequence retain the timestamp fallback, with its known clock limits.
                bool outputAfterFault = fault.Sequence > 0 && status.LastOutputAcceptedNativeSequence is > 0
                    ? status.LastOutputAcceptedNativeSequence > fault.Sequence : status.LastOutputAcceptedUtc > fault.AtUtc;
                // LastNativeFailure is retained after recovery; its sequence/time fence prevents replay.
                if (available && accepted == true && outputAfterFault)
                    _gpuNativeFailureEpisode = false;
            }
        }

        foreach (var change in changes)
        {
            var data = GpuRgbEventData(status, available, accepted);
            if (change.Recovery is { } seconds)
            {
                data["recoverySeconds"] = seconds.ToString("F3", CultureInfo.InvariantCulture);
                Metrics.RgbControlRecovery("rgb.gpu", seconds);
            }
            data = BoundedGpuRgbEventData(data, change.Kind);
            RecordEvent(new() { TimestampUtc = when, DeviceId = "rgb.gpu", Source = "engine", Kind = change.Kind,
                Severity = change.Severity, Message = change.Message, Data = data });
        }
    }

    private void ObserveGpuNativeFailureHistory(RgbControlStatus? status, DateTimeOffset when)
    {
        if (status?.NativeFailureHistory is not { } history) return; // Preserve the existing legacy episode path.
        List<RgbNativeOperationResult> offered = [];
        Dictionary<string, string>? historyData = null;
        lock (_gate)
        {
            var previous = _gpuNativeHistoryObservation;
            // A delayed snapshot must not rewind the window and make its replacement replay known details.
            // At a fixed total, received contexts can only increase; MissingCount is a decreasing gauge.
            if (previous != null && (history.TotalFailures < previous.TotalFailures || history.EvictedCount < previous.EvictedCount ||
                history.TotalFailures == previous.TotalFailures && history.MissingCount > previous.MissingCount)) return;

            var records = history.Records.Where(f => f.Status != 0 && f.FailureOrdinal > 0)
                .DistinctBy(f => f.FailureOrdinal).Take(RgbNativeFailureHistory.MaximumRecords).ToList();
            // A supported provider can still deliver the latest context when the optional batch was omitted.
            // Never exceed eight offered records, nor turn an out-of-window retained detail into a replay.
            if (records.Count < RgbNativeFailureHistory.MaximumRecords && status.LastNativeFailure is { Status: not 0, FailureOrdinal: > 0 } latest &&
                !records.Any(f => f.FailureOrdinal == latest.FailureOrdinal) &&
                (records.Count == 0 || latest.FailureOrdinal >= records.Max(f => f.FailureOrdinal)))
                records.Add(latest);

            int legacyContexts = 0;
            foreach (var record in records.OrderBy(f => f.FailureOrdinal))
            {
                if (_gpuNativeJournalWindow.Contains(record.FailureOrdinal)) continue;
                if (_gpuLegacyFailureEventContexts.Any(legacy => SameGpuFailureContext(legacy, record)))
                    legacyContexts++;
                else offered.Add(record);
            }
            // Unlike a single high-water mark, the set admits late lower ordinals. It retains only the
            // current bounded window; a healthy unchanged snapshot therefore cannot replay its contexts.
            _gpuNativeJournalWindow.Clear();
            _gpuNativeJournalWindow.UnionWith(records.Select(f => f.FailureOrdinal));

            bool changed = previous == null || history.TotalFailures != previous.TotalFailures ||
                history.EvictedCount != previous.EvictedCount || history.MissingCount != previous.MissingCount ||
                history.Records.Count != previous.Records.Count || offered.Count > 0 || legacyContexts > 0;
            if (changed)
            {
                historyData = new(StringComparer.Ordinal)
                {
                    ["evidence"] = "cached-native-failure-history",
                    ["observedAtUtc"] = when.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    ["nativeHistoryCapacity"] = history.Capacity.ToString(CultureInfo.InvariantCulture),
                    ["totalNativeFailures"] = history.TotalFailures.ToString(CultureInfo.InvariantCulture),
                    ["nativeHistoryRetained"] = history.Records.Count.ToString(CultureInfo.InvariantCulture),
                    ["nativeHistoryEvicted"] = history.EvictedCount.ToString(CultureInfo.InvariantCulture),
                    ["nativeHistoryMissing"] = history.MissingCount.ToString(CultureInfo.InvariantCulture),
                    ["firstObservation"] = previous == null ? "true" : "false",
                    ["nativeRecordEventsOffered"] = offered.Count.ToString(CultureInfo.InvariantCulture),
                    ["legacyContextEventsAlreadyOffered"] = legacyContexts.ToString(CultureInfo.InvariantCulture),
                    ["evictionMeaning"] = "received-context-left-window;not-automatic-loss",
                    ["missingMeaning"] = "context-not-yet-received-by-parent;gauge",
                    ["journalDelivery"] = "enqueue-attempt;not-persistence-confirmation",
                    ["physicalConnection"] = "unverified", ["physicalOutput"] = "unverified"
                };
                if (previous != null)
                {
                    historyData["nativeFailureTotalChange"] = (history.TotalFailures - previous.TotalFailures).ToString(CultureInfo.InvariantCulture);
                    historyData["evictedChange"] = (history.EvictedCount - previous.EvictedCount).ToString(CultureInfo.InvariantCulture);
                    historyData["missingChange"] = (history.MissingCount - previous.MissingCount).ToString(CultureInfo.InvariantCulture);
                }
                if (status.WorkerGeneration > 0) historyData["workerGeneration"] = status.WorkerGeneration.ToString(CultureInfo.InvariantCulture);
                if (status.WorkerProcessId is { } processId) historyData["workerProcessId"] = processId.ToString(CultureInfo.InvariantCulture);
            }
            _gpuNativeHistoryObservation = history;
        }

        foreach (var record in offered)
        {
            var data = GpuRgbEventData(status, status.ControlAvailable, status.LastOutputAccepted, record);
            data["evidence"] = "cached-native-failure-history-record";
            data["observedAtUtc"] = when.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            data["journalDelivery"] = "enqueue-attempt;not-persistence-confirmation";
            // Journal time is observation time. Native UTC remains precise metadata even for late context or
            // regressing clocks, so an arriving detail does not silently move into an old persisted segment.
            RecordEvent(new() { TimestampUtc = when, DeviceId = "rgb.gpu", Source = "engine", Kind = "rgb.native.failure",
                Severity = AlertSeverity.Info, Message = "GPU RGB native failure context received; adapter state and emitted light are separate evidence",
                Data = BoundedGpuRgbEventData(data, "rgb.native.failure") });
        }
        if (historyData != null)
            RecordEvent(new() { TimestampUtc = when, DeviceId = "rgb.gpu", Source = "engine", Kind = "rgb.native.history",
                Severity = AlertSeverity.Info, Message = "GPU RGB native context window changed; reception, eviction and journal persistence are distinct",
                Data = historyData });
    }

    private static bool SameGpuFailureContext(RgbNativeOperationResult previous, RgbNativeOperationResult current) =>
        previous.Sequence > 0 && current.Sequence > 0 ? previous.Sequence == current.Sequence :
        previous.AtUtc == current.AtUtc && previous.Operation == current.Operation && previous.Status == current.Status &&
        previous.Register == current.Register && previous.Command == current.Command;

    // RecordEvent retains24 pairs. Select deliberately rather than losing the tail of the native failure context.
    private static readonly string[] GpuEventBasePriority =
        ["evidence", "controlAvailable", "outputAccepted", "physicalConnection", "physicalOutput", "recoverySeconds", "observedAtUtc", "journalDelivery"];
    private static readonly string[] GpuEventNativePriority =
        ["nativeOperation", "nativeStatus", "nativeFailureUtc", "nativeFailureOrdinal", "nativeFailureSequence", "nativeRegister", "nativeCommand",
         "nativeProcessId", "nativeThreadId", "nativeGpuHandle", "nativeTransferDurationMicroseconds", "nativeLibraryVersion",
         "nativeThreadIdKind", "nativeTransferTimingScope"];
    private static readonly string[] GpuEventWorkerPriority =
        ["workerGeneration", "workerProcessId", "workerRestartCount", "workerTransportFailures", "workerRestartReason",
         "workerLastFailure", "workerLastFailureUtc"];

    private static Dictionary<string, string> BoundedGpuRgbEventData(Dictionary<string, string> data, string kind)
    {
        const int maximum = 24;
        if (data.Count <= maximum) return data;
        var selected = new Dictionary<string, string>(maximum, StringComparer.Ordinal);
        void Include(IEnumerable<string> keys)
        {
            foreach (string key in keys)
            {
                if (selected.Count >= maximum - 1) break; // Reserve one explicit omission count.
                if (data.TryGetValue(key, out var value)) selected.TryAdd(key, value);
            }
        }
        Include(GpuEventBasePriority);
        bool workerEvent = kind.StartsWith("rgb.worker.", StringComparison.Ordinal);
        if (workerEvent)
        {
            Include(GpuEventWorkerPriority);
            Include(["totalNativeFailures", "totalOutputFailures"]);
        }
        Include(GpuEventNativePriority);
        if (!workerEvent)
        {
            Include(["workerGeneration", "workerProcessId", "totalNativeFailures", "totalOutputFailures"]);
        }
        Include(data.Keys);
        selected["omittedDataFields"] = (data.Count - selected.Count).ToString(CultureInfo.InvariantCulture);
        return selected;
    }

    private static Dictionary<string, string> GpuRgbEventData(RgbControlStatus? status, bool available, bool? accepted,
        RgbNativeOperationResult? nativeFault = null)
    {
        var data = new Dictionary<string, string>
        {
            ["evidence"] = status == null ? "legacy-rgb-availability" : "cached-rgb-control-status",
            ["controlAvailable"] = available ? "true" : "false",
            ["outputAccepted"] = accepted is { } value ? value ? "true" : "false" : "unknown",
            ["physicalConnection"] = "unverified", ["physicalOutput"] = "unverified"
        };
        if (status == null) return data;
        data["backend"] = Limit(status.Source, 160);
        data["controller"] = Limit(status.ControllerName, 160);
        data["adapterObservation"] = status.AdapterObservation.ToString();
        data["totalNativeFailures"] = status.TotalNativeFailures.ToString(CultureInfo.InvariantCulture);
        data["totalOutputFailures"] = status.TotalOutputFailures.ToString(CultureInfo.InvariantCulture);
        data["consecutiveOutputFailures"] = status.ConsecutiveOutputFailures.ToString(CultureInfo.InvariantCulture);
        data["initializationAttempts"] = status.InitializationAttempts.ToString(CultureInfo.InvariantCulture);
        if (status.WorkerGeneration > 0)
        {
            data["workerGeneration"] = status.WorkerGeneration.ToString(CultureInfo.InvariantCulture);
            data["workerRestartCount"] = status.WorkerRestartCount.ToString(CultureInfo.InvariantCulture);
            data["workerTransportFailures"] = status.WorkerTransportFailures.ToString(CultureInfo.InvariantCulture);
            if (status.WorkerProcessId is { } pid) data["workerProcessId"] = pid.ToString(CultureInfo.InvariantCulture);
            if (status.LastWorkerRestartReason is { } reason) data["workerRestartReason"] = Limit(reason, 160);
            if (status.LastWorkerFailure is { } error) data["workerLastFailure"] = Limit(error, 512);
            if (status.LastWorkerFailureUtc is { } failedAt)
                data["workerLastFailureUtc"] = failedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }
        if (status.LastOutputAttemptUtc is { } attempt) data["outputAttemptUtc"] = attempt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        if (status.LastOutputAcceptedUtc is { } acceptedAt) data["outputAcceptedUtc"] = acceptedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        if (status.LastOutputAcceptedNativeSequence is > 0)
            data["outputAcceptedNativeSequence"] = status.LastOutputAcceptedNativeSequence.Value.ToString(CultureInfo.InvariantCulture);
        if (status.Diagnostic is { } diagnostic) data["diagnostic"] = Limit(diagnostic, 512);
        if ((nativeFault ?? status.LastNativeFailure) is { } fault)
        {
            data["nativeOperation"] = fault.Operation.ToString();
            data["nativeStatus"] = fault.Status.ToString(CultureInfo.InvariantCulture);
            data["nativeFailureUtc"] = fault.AtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            if (fault.FailureOrdinal > 0) data["nativeFailureOrdinal"] = fault.FailureOrdinal.ToString(CultureInfo.InvariantCulture);
            if (fault.Sequence > 0) data["nativeFailureSequence"] = fault.Sequence.ToString(CultureInfo.InvariantCulture);
            if (fault.Register is { } register) data["nativeRegister"] = "0x" + register.ToString("X4", CultureInfo.InvariantCulture);
            if (fault.Command is { } command) data["nativeCommand"] = "0x" + command.ToString("X2", CultureInfo.InvariantCulture);
            if (fault.ProcessId is > 0) data["nativeProcessId"] = fault.ProcessId.Value.ToString(CultureInfo.InvariantCulture);
            if (fault.ThreadId is > 0)
            {
                data["nativeThreadId"] = fault.ThreadId.Value.ToString(CultureInfo.InvariantCulture);
                data["nativeThreadIdKind"] = "managed";
            }
            if (fault.GpuHandle is { Length: 16 } handle && handle.All(Uri.IsHexDigit)) data["nativeGpuHandle"] = handle;
            if (fault.TransferDurationMicroseconds is { } duration && double.IsFinite(duration) && duration >= 0)
            {
                data["nativeTransferDurationMicroseconds"] = duration.ToString("R", CultureInfo.InvariantCulture);
                data["nativeTransferTimingScope"] = "backend-call-including-wrapper";
            }
            if (fault.LibraryVersion is { Length: > 0 and <= 128 } version && !version.Any(char.IsControl))
                data["nativeLibraryVersion"] = version;
        }
        return data;
    }
}
