using System.Text.Json;

namespace LinkFlow.Core;

public sealed partial class OperationalHistory
{
    // Independent of raw segment eviction: at most 100 sessions and 64 KiB per index/backup.
    private const int MaxSessionEntries = 100;
    private const int MaxSessionIndexBytes = 64 * 1024;
    private sealed record SessionEntry(string Id, DateTimeOffset? StartedUtc, DateTimeOffset? StoppedUtc,
        DateTimeOffset LastObservedUtc, string Version);
    private sealed record SessionIndex(int SchemaVersion, List<SessionEntry> Sessions);
    private readonly Dictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private bool _sessionMetadataAvailable = true;
    private string? _sessionMetadataError;
    private long _sessionMetadataBytes;
    private long _sessionRevision, _savedSessionRevision;
    private DateTimeOffset _lastSessionCheckpointUtc;
    private string SessionIndexPath => Path.Combine(_options.DirectoryPath!, "sessions.json");

    // Callers hold _gate, or are loading before publication/the writer has started.
    private void ObserveSessionFrame(TelemetryFrame frame)
    {
        if (string.IsNullOrWhiteSpace(frame.SessionId) || frame.SessionId.Length > 80) return;
        if (_sessions.TryGetValue(frame.SessionId, out var previous))
        {
            if (frame.TimestampUtc > previous.LastObservedUtc)
                _sessions[frame.SessionId] = previous with { LastObservedUtc = frame.TimestampUtc };
            if (frame.SessionId == SessionId && frame.TimestampUtc - _lastSessionCheckpointUtc >= TimeSpan.FromHours(1))
            {
                _lastSessionCheckpointUtc = frame.TimestampUtc;
                _sessionRevision++;
            }
            return;
        }
        _sessions[frame.SessionId] = new(frame.SessionId, null, null, frame.TimestampUtc, "unknown");
        _sessionRevision++;
        TrimSessions();
    }

    private void ObserveSessionEvent(OperationalEvent entry)
    {
        if (entry.Kind is not ("engine.started" or "engine.stopped") || string.IsNullOrWhiteSpace(entry.SessionId) || entry.SessionId.Length > 80) return;
        _sessions.TryGetValue(entry.SessionId, out var previous);
        var start = previous?.StartedUtc;
        var stop = previous?.StoppedUtc;
        string version = previous?.Version ?? "unknown";
        if (entry.Kind == "engine.started")
        {
            if (start == null || entry.TimestampUtc < start) start = entry.TimestampUtc;
            version = Limit(entry.Data.GetValueOrDefault("version") ?? version, 128);
        }
        else if (stop == null || entry.TimestampUtc > stop) stop = entry.TimestampUtc;
        var last = previous != null && previous.LastObservedUtc > entry.TimestampUtc ? previous.LastObservedUtc : entry.TimestampUtc;
        var updated = new SessionEntry(entry.SessionId, start, stop, last, version);
        if (updated == previous) return;
        _sessions[entry.SessionId] = updated;
        _sessionRevision++;
        TrimSessions();
    }

    private void TrimSessions()
    {
        var cutoff = Now - _options.Retention;
        var remove = _sessions.Values.Where(s => s.Id != SessionId && s.LastObservedUtc < cutoff).Select(s => s.Id)
            .Concat(_sessions.Values.Where(s => s.Id != SessionId).OrderByDescending(s => s.LastObservedUtc)
                .Skip(MaxSessionEntries - (_sessions.ContainsKey(SessionId) ? 1 : 0)).Select(s => s.Id)).Distinct().ToArray();
        foreach (string id in remove) if (_sessions.Remove(id)) _sessionRevision++;
    }

    private void LoadSessionMetadata()
    {
        string path = SessionIndexPath;
        bool found = false;
        foreach (string candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            found = true;
            try
            {
                foreach (var session in ReadSessionIndex(candidate).Sessions) _sessions[session.Id] = session;
                _sessionMetadataAvailable = true;
                _sessionMetadataError = null;
                RefreshSessionMetadataBytes();
                return;
            }
            catch (Exception ex) when (IsStorageError(ex) || ex is JsonException)
            {
                if (ex is JsonException or InvalidDataException) Interlocked.Increment(ref _corruptRecords);
                SessionMetadataFailure(ex);
            }
        }
        if (!found) _sessionMetadataAvailable = true;
    }

    private static SessionIndex ReadSessionIndex(string path)
    {
        if (SharedDataSecurity.IsReparsePoint(path)) throw new IOException("Session metadata is redirected.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > MaxSessionIndexBytes) throw new InvalidDataException("Session metadata exceeds the size limit.");
        var index = JsonSerializer.Deserialize<SessionIndex>(stream, Json.Compact);
        if (index is not { SchemaVersion: 1, Sessions: not null } || index.Sessions.Count > MaxSessionEntries ||
            index.Sessions.Any(s => s == null || s.Id is not { Length: > 0 and <= 80 } || string.IsNullOrWhiteSpace(s.Id) ||
                s.Version is not { Length: <= 128 } || s.LastObservedUtc == default ||
                s.StartedUtc is { } start && (start == default || start > s.LastObservedUtc) ||
                s.StoppedUtc is { } stop && (stop == default || stop > s.LastObservedUtc)) ||
            index.Sessions.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != index.Sessions.Count)
            throw new InvalidDataException("Invalid session metadata.");
        return index;
    }

    private async Task PersistSessionMetadataAsync()
    {
        if (_options.DirectoryPath == null) return;
        SessionIndex index;
        long revision;
        lock (_gate)
        {
            TrimSessions();
            revision = _sessionRevision;
            if (revision == _savedSessionRevision) return;
            index = new(1, _sessions.Values.OrderByDescending(s => s.LastObservedUtc).ToList());
        }
        string path = SessionIndexPath, temporary = path + ".tmp", backup = path + ".bak";
        try
        {
            EnsureSafeDirectory();
            foreach (string candidate in new[] { path, temporary, backup })
                if (File.Exists(candidate) && SharedDataSecurity.IsReparsePoint(candidate)) throw new IOException("Session metadata is redirected.");
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(index, Json.Compact);
            if (bytes.Length > MaxSessionIndexBytes) throw new InvalidDataException("Session metadata exceeds the size limit.");
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, _dispose.Token).ConfigureAwait(false);
                await stream.FlushAsync(_dispose.Token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            // Keep the previous valid index. A damaged main file must not overwrite a good backup.
            bool validPrevious = false;
            if (File.Exists(path))
                try { ReadSessionIndex(path); validPrevious = true; }
                catch (Exception ex) when (ex is JsonException or InvalidDataException) { }
            if (validPrevious) File.Copy(path, backup, overwrite: true);
            File.Move(temporary, path, overwrite: true);
            long storedBytes = ReadSessionMetadataBytes();
            lock (_gate)
            {
                _savedSessionRevision = revision;
                _sessionMetadataAvailable = true;
                _sessionMetadataError = null;
                _sessionMetadataBytes = storedBytes;
            }
        }
        catch (Exception ex) when (IsStorageError(ex) || ex is JsonException) { SessionMetadataFailure(ex); }
    }

    private void RefreshSessionMetadataBytes() => _sessionMetadataBytes = ReadSessionMetadataBytes();
    private long ReadSessionMetadataBytes() => new[] { SessionIndexPath, SessionIndexPath + ".bak" }
        .Where(File.Exists).Sum(path => new FileInfo(path).Length);

    private void SessionMetadataFailure(Exception exception)
    {
        lock (_gate)
        {
            _sessionMetadataAvailable = false;
            _sessionMetadataError = "Session metadata: " + Limit(exception.Message, 400);
        }
        Metrics.Storage(false);
        try { StorageChanged?.Invoke(Status); } catch { }
    }
}
