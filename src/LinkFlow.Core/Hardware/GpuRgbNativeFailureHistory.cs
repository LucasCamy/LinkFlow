namespace LinkFlow.Core;

/// <summary>Validates the bounded wire window and plans an immutable merge before any proxy counter is changed.</summary>
internal static class GpuRgbNativeFailureHistory
{
    internal static void Validate(GpuRgbNativeFailureBatch? batch, RgbControlStatus status,
        Func<RgbNativeOperationResult?, bool> validNative, RgbControlStatus? previous = null)
    {
        if (batch == null) return; // Legacy providers and unchanged healthy replies intentionally omit it.
        long total = batch.TotalFailures;
        if (total < 0) throw new InvalidDataException("The RGB native failure total is negative.");
        int window = (int)Math.Min(total, RgbNativeFailureHistory.MaximumRecords);
        if (total != status.TotalNativeFailures || batch.RingEvictedCount != total - window ||
            batch.TransportOmittedCount < 0 || batch.TransportOmittedCount > window ||
            batch.Records.Count != window - batch.TransportOmittedCount ||
            total > 0 && status.LastNativeFailure?.FailureOrdinal != total)
            throw new InvalidDataException("The RGB native failure batch counters are inconsistent.");

        // Strict contiguous tail: no arbitrary old batches or holes disguised as a complete window.
        long ordinal = total - batch.Records.Count;
        long previousSequence = 0;
        foreach (var record in batch.Records)
        {
            if (record == null || !validNative(record) || record.Status == 0 || record.FailureOrdinal != ++ordinal ||
                record.Sequence <= previousSequence || record.Sequence > status.LastNativeOperation!.Sequence ||
                (record.FailureOrdinal > (previous?.TotalNativeFailures ?? 0)) !=
                    (record.Sequence > (previous?.LastNativeOperation?.Sequence ?? 0)))
                throw new InvalidDataException("The RGB native failure batch is not an ordered recent suffix.");
            // A legacy reply already delivered its latest failure even without an ordinal. A later capable
            // batch may introduce that ordinal, but cannot rewrite the call's identity or any other context.
            if (previous?.LastNativeFailure is { } prior &&
                (record.Sequence == prior.Sequence || record.FailureOrdinal == previous.TotalNativeFailures) &&
                (record.FailureOrdinal != previous.TotalNativeFailures ||
                    (record with { FailureOrdinal = prior.FailureOrdinal }) != prior))
                throw new InvalidDataException("The RGB failure batch changed previously delivered legacy evidence.");
            previousSequence = record.Sequence;
        }
        if (batch.Records.Count > 0 && batch.Records[^1] != status.LastNativeFailure)
            throw new InvalidDataException("The RGB failure batch changed the latest native failure.");
    }

    internal static RgbNativeFailureHistory? Merge(RgbNativeFailureHistory? old, long currentTotal, long nextTotal,
        GpuRgbNativeFailureBatch? batch, long sequenceBase, long ordinalBase,
        RgbNativeOperationResult? lastFailure = null, RgbNativeOperationResult? priorKnownFailure = null)
    {
        // A positive ordinal is itself evidence of the new capability, even if the first batch could not fit.
        // A legacy provider supplies ordinal zero and retains the existing null-history contract.
        if (old == null && batch == null && lastFailure is not { FailureOrdinal: > 0 }) return null;
        if (nextTotal < currentTotal || old != null && old.TotalFailures != currentTotal)
            throw new InvalidDataException("The RGB native failure history regressed.");
        if (batch == null && nextTotal == currentTotal) return old;

        // At most sixteen entries are considered. No all-time seen set is necessary because the wire is a recent suffix.
        var records = old?.Records.ToList() ?? [];
        long oldMissing = old?.MissingCount ?? currentTotal;
        // A legacy publication can already have delivered one attributable context before a supported window arrives.
        if (old == null && priorKnownFailure is { } prior && currentTotal > 0)
        {
            // LastNativeFailure is the failure at the preceding observed total, including a legacy ordinal zero.
            // Give only this already-received context its inferred ordinal; other legacy details stay missing.
            if (prior.FailureOrdinal != 0 && prior.FailureOrdinal != currentTotal)
                throw new InvalidDataException("The RGB previous latest failure has an inconsistent ordinal.");
            records.Add(prior with { FailureOrdinal = currentTotal });
            oldMissing--;
        }
        int newlyReceived = 0, backfilled = 0;
        if (batch != null)
            foreach (var raw in batch.Records)
                Receive(Map(raw, sequenceBase, ordinalBase));
        // The existing compatibility field still delivers the latest detail even when byte budgeting omitted its batch.
        if (lastFailure is { FailureOrdinal: > 0 }) Receive(Map(lastFailure, sequenceBase, ordinalBase));

        void Receive(RgbNativeOperationResult record)
        {
            var known = records.FirstOrDefault(r => r.FailureOrdinal == record.FailureOrdinal);
            if (known != null)
            {
                if (known != record) throw new InvalidDataException("The RGB worker changed a received failure context.");
                return;
            }
            if (record.FailureOrdinal <= currentTotal) backfilled++;
            else newlyReceived++;
            records.Add(record);
        }

        long delta = nextTotal - currentTotal;
        if (newlyReceived > delta || backfilled > oldMissing)
            throw new InvalidDataException("The RGB worker repeated evicted or exhausted failure ordinals.");
        if (newlyReceived == 0 && backfilled == 0 && delta == 0 && old != null) return old;
        records.Sort((left, right) => left.FailureOrdinal.CompareTo(right.FailureOrdinal));
        int removed = Math.Max(0, records.Count - RgbNativeFailureHistory.MaximumRecords);
        if (removed > 0) records.RemoveRange(0, removed);
        long evicted = Add(old?.EvictedCount ?? 0, removed);
        long expectedMissing = Add(oldMissing, delta - newlyReceived) - backfilled;
        if (evicted > nextTotal || nextTotal - evicted < records.Count ||
            nextTotal - evicted - records.Count != expectedMissing)
            throw new InvalidDataException("The RGB native failure history exceeded its counter range.");
        return new RgbNativeFailureHistory
        {
            TotalFailures = nextTotal, EvictedCount = evicted, MissingCount = expectedMissing, Records = records
        };
    }

    internal static RgbNativeOperationResult Map(RgbNativeOperationResult operation, long sequenceBase, long ordinalBase) =>
        operation with
        {
            Sequence = Add(sequenceBase, operation.Sequence),
            FailureOrdinal = operation.FailureOrdinal == 0 ? 0 : Add(ordinalBase, operation.FailureOrdinal)
        };

    private static long Add(long left, long right) => right >= long.MaxValue - left ? long.MaxValue : left + right;
}
