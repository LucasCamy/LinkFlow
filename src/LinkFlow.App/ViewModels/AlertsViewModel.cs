using System.Windows.Input;
using LinkFlow.Core;

namespace LinkFlow.App.ViewModels;

public sealed record AlertNotice(string Key, DateTime Since, AlertSeverity Severity, string Title, string Message)
{
    internal static AlertNotice From(EngineAlert alert) =>
        new(alert.Key, alert.Since, alert.Severity, alert.Title, alert.Message);

    internal bool SameOccurrence(AlertNotice other) => Key == other.Key && Since == other.Since;
}

public sealed record AlertHistoryItem(AlertNotice Alert, string Status);

/// <summary>A apresentação e o histórico são locais à interface. O engine continua acompanhando todos os alertas ativos.</summary>
public sealed class AlertsViewModel : ObservableObject
{
    private const int VisibleLimit = 2;
    private const int HistoryLimit = 50;
    private const string DisconnectedKey = "service-disconnected";
    public const string CpuSensorKey = "cpu-sensor-unavailable";

    private sealed record RecordedAlert(AlertNotice Alert, AlertSeverity? DismissedThrough = null);

    private readonly JsonFileStore<List<RecordedAlert>>? _store;
    private readonly List<RecordedAlert> _records;
    private readonly Dictionary<(string Key, DateTime Since), AlertSeverity> _dismissed = new();
    private IReadOnlyList<AlertNotice> _active = [];
    private AlertNotice? _serviceAlert;
    private AlertNotice? _cpuSensorAlert;
    private AlertNotice? _banner;
    private AlertNotice? _top;
    private IReadOnlyList<AlertNotice> _preview = [];
    private IReadOnlyList<AlertHistoryItem> _history = [];
    private int _pendingCount;

    public AlertsViewModel(string? historyPath = null)
    {
        if (historyPath != null) _store = new(historyPath);
        _records = _store?.Load().Where(entry => entry?.Alert is { Key.Length: > 0 })
            .OrderByDescending(entry => entry.Alert.Since).Take(HistoryLimit).ToList() ?? [];
        foreach (var entry in _records)
            if (entry.DismissedThrough is { } severity) _dismissed[Occurrence(entry.Alert)] = severity;
        DismissCommand = new RelayCommand(Dismiss);
        RefreshHistory();
    }

    public ICommand DismissCommand { get; }
    public AlertNotice? Banner => _banner;
    public AlertNotice? Top => _top;
    public IReadOnlyList<AlertNotice> Preview => _preview;
    public IReadOnlyList<AlertHistoryItem> History => _history;
    public bool HasHistory => _history.Count != 0;
    public int PendingCount => _pendingCount;
    public bool HasPending => _pendingCount != 0;
    public string? PanelMessage => HasPending ? null : "Nenhum alerta pendente.";

    public string HistoryLabel
    {
        get
        {
            int overflow = _pendingCount - _preview.Count - (_banner == null ? 0 : 1);
            return overflow > 0 ? $"Histórico (+{overflow})" : "Histórico";
        }
    }

    internal void Update(EngineSnapshot snapshot, bool serviceDisconnected)
    {
        if (serviceDisconnected)
            _serviceAlert ??= new(DisconnectedKey, DateTime.Now, AlertSeverity.Warning, "O serviço LinkFlow está desconectado",
                "As leituras podem estar desatualizadas.");
        else
            _serviceAlert = null;

        if (!serviceDisconnected && snapshot.SensorsReady && !snapshot.CpuSensorAvailable)
            _cpuSensorAlert ??= new(CpuSensorKey, DateTime.Now, AlertSeverity.Warning, "Sensor de temperatura da CPU indisponível",
                "Execute como administrador ou instale o serviço LinkFlow.");
        else if (!serviceDisconnected)
            _cpuSensorAlert = null;

        var active = snapshot.Alerts.Select(AlertNotice.From).ToList();
        if (_serviceAlert != null) active.Add(_serviceAlert);
        if (_cpuSensorAlert != null) active.Add(_cpuSensorAlert);
        if (_active.SequenceEqual(active)) return;
        _active = active;

        foreach (var occurrence in _dismissed.Keys.ToArray())
            if (!_active.Any(alert => Occurrence(alert) == occurrence)) _dismissed.Remove(occurrence);

        bool changed = false;
        foreach (var alert in active) changed |= Remember(alert);
        if (changed || _records.Count > HistoryLimit) SaveHistory();
        RefreshPresentation();
    }

    internal void Record(EngineAlert alert)
    {
        // Também guarda alertas que se resolvem entre dois snapshots.
        if (!Remember(AlertNotice.From(alert))) return;
        SaveHistory();
        RefreshHistory();
    }

    private bool Remember(AlertNotice alert)
    {
        int index = _records.FindIndex(entry => entry.Alert.SameOccurrence(alert));
        if (index < 0)
            _records.Add(new(alert, _dismissed.TryGetValue(Occurrence(alert), out var severity) ? severity : null));
        else if (alert.Severity > _records[index].Alert.Severity)
            _records[index] = _records[index] with { Alert = alert };
        else
            return false;
        return true;
    }

    private void Dismiss(object? parameter)
    {
        if (parameter is not AlertNotice notice ||
            !_active.Any(alert => alert.SameOccurrence(notice) && alert.Severity == notice.Severity))
            return;

        if (IsDismissed(notice)) return;
        _dismissed[Occurrence(notice)] = notice.Severity;
        int index = _records.FindIndex(entry => entry.Alert.SameOccurrence(notice));
        if (index >= 0) _records[index] = _records[index] with { DismissedThrough = notice.Severity };
        SaveHistory();
        RefreshPresentation();
    }

    private static (string Key, DateTime Since) Occurrence(AlertNotice alert) => (alert.Key, alert.Since);

    private bool IsDismissed(AlertNotice alert) =>
        _dismissed.TryGetValue(Occurrence(alert), out var severity) && severity >= alert.Severity;

    private void RefreshPresentation()
    {
        var pending = _active.Where(alert => !IsDismissed(alert))
            .OrderByDescending(alert => alert.Severity).ThenByDescending(alert => alert.Since).ToArray();
        var banner = pending.FirstOrDefault(alert => alert.Key == "failsafe") ??
            pending.FirstOrDefault(alert => alert.Key == DisconnectedKey) ??
            pending.FirstOrDefault(alert => alert.Key == CpuSensorKey);
        var preview = pending.Where(alert => alert != banner).Take(VisibleLimit - (banner == null ? 0 : 1)).ToArray();
        SetProperty(ref _banner, banner, nameof(Banner));
        SetProperty(ref _top, banner ?? pending.FirstOrDefault(), nameof(Top));
        if (!_preview.SequenceEqual(preview)) SetProperty(ref _preview, preview, nameof(Preview));
        _pendingCount = pending.Length;
        OnPropertiesChanged(nameof(PendingCount), nameof(HasPending), nameof(PanelMessage), nameof(HistoryLabel));
        RefreshHistory();
    }

    private void RefreshHistory()
    {
        var history = _records.Take(HistoryLimit).Select(entry =>
        {
            var active = _active.FirstOrDefault(entry.Alert.SameOccurrence);
            string status = active == null ? "Encerrado" : "Ativo";
            if (entry.DismissedThrough >= (active ?? entry.Alert).Severity) status += " · dispensado";
            return new AlertHistoryItem(entry.Alert, status);
        }).ToArray();
        if (_history.SequenceEqual(history)) return;
        _history = history;
        OnPropertiesChanged(nameof(History), nameof(HasHistory));
    }

    private void SaveHistory()
    {
        _records.Sort((a, b) => b.Alert.Since.CompareTo(a.Alert.Since));
        // Retém em memória os alertas ativos que ficaram fora da lista recente.
        for (int index = _records.Count - 1; index >= HistoryLimit; index--)
            if (!_active.Any(_records[index].Alert.SameOccurrence)) _records.RemoveAt(index);
        _store?.Save(_records.Take(HistoryLimit).ToList());
    }
}
