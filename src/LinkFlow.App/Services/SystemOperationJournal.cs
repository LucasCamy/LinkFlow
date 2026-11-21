using System.Diagnostics;
using System.IO;
using LinkFlow.Core;

namespace LinkFlow.App.Services;

public sealed class SystemOperationJournal
{
    private readonly object _gate = new();
    private readonly JsonFileStore<List<WindowsOperationRecord>>? _store;
    private readonly List<WindowsOperationRecord> _records;
    public static SystemOperationJournal Shared { get; } = new(Path.Combine(AppPaths.UserDir, "system-operations.json"));
    public event Action<WindowsOperationRecord>? Changed;
    public bool PersistenceAvailable { get; private set; } = true;

    public SystemOperationJournal(string? path = null)
    {
        _store = path == null ? null : new(path);
        _records = _store?.Load().Where(r => r != null).Take(100).ToList() ?? [];
        // Após reabrir, um processo que desapareceu não comprova a conclusão de uma instalação/reparo.
        bool reconciled = false;
        for (int i = 0; i < _records.Count; i++)
            if (_records[i].State == WindowsOperationState.Running)
            {
                bool stillRunning = SameProcess(_records[i]);
                _records[i] = _records[i] with { State = WindowsOperationState.Unconfirmed, UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Detail = stillRunning ? "O processo continua aberto, mas o acompanhamento anterior foi interrompido. Confira a ferramenta." :
                        "O acompanhamento foi interrompido. Consulte o resultado da ferramenta ou o histórico do Windows." };
                reconciled = true;
            }
        if (reconciled) PersistenceAvailable = _store?.Save(_records) ?? true;
    }

    private static bool SameProcess(WindowsOperationRecord record)
    {
        if (record.ProcessId is not { } id || record.ProcessStartUtc is not { } started) return false;
        try { using var process = Process.GetProcessById(id); return Math.Abs((process.StartTime.ToUniversalTime() - started.UtcDateTime).TotalSeconds) < 1; }
        catch { return false; }
    }

    public IReadOnlyList<WindowsOperationRecord> Read() { lock (_gate) return _records.ToArray(); }

    public string Start(string kind, string title)
    {
        var record = new WindowsOperationRecord { Kind = kind, Title = title, State = WindowsOperationState.Running };
        Put(record);
        return record.Id;
    }

    public void AttachProcess(string id, Process process)
    {
        WindowsOperationRecord? current;
        lock (_gate) current = _records.FirstOrDefault(r => r.Id == id);
        if (current != null) Put(current with { ProcessId = process.Id, ProcessStartUtc = process.StartTime.ToUniversalTime(), UpdatedAtUtc = DateTimeOffset.UtcNow });
    }

    public void Complete(string id, WindowsOperationState state, int? exitCode = null, string? detail = null)
    {
        WindowsOperationRecord? current;
        lock (_gate) current = _records.FirstOrDefault(r => r.Id == id);
        if (current == null) return;
        Put(current with { State = state, ExitCode = exitCode, Detail = detail, UpdatedAtUtc = DateTimeOffset.UtcNow });
    }

    private void Put(WindowsOperationRecord record)
    {
        lock (_gate)
        {
            _records.RemoveAll(r => r.Id == record.Id);
            _records.Insert(0, record);
            if (_records.Count > 100) _records.RemoveRange(100, _records.Count - 100);
            PersistenceAvailable = _store?.Save(_records) ?? true;
        }
        try { Changed?.Invoke(record); }
        catch (Exception ex) { Log.Warn($"Não foi possível publicar a operação no serviço: {ex.Message}"); }
    }
}
