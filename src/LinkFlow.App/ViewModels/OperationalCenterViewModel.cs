using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using LinkFlow.App.Services;
using LinkFlow.Core;

namespace LinkFlow.App.ViewModels;

public sealed record OperationalSensorRow(string Key, string Label, string Source, string State, string Age,
    string LastSuccess, string Failures, string Reason, string Color);
public sealed record OperationalFanRow(string Name, string Model, string Requested, string Accepted, string Rpm,
    string Reason, string Capabilities, string State, string Color);
public sealed record OperationalCommandRow(string Id, string Action, string State, string Confirmation,
    string Persistence, string When, string Detail, string Color);
public sealed record OperationalEventRow(string When, string Label, string Source, string Detail, string Color);
public sealed record OperationalGuidance(string Title, string Evidence, string Action, string Color);
public sealed record OperationalMetric(string Key, string Label, string Unit)
{
    public override string ToString() => Label;
}
public sealed record OperationalRange(int Seconds, string Label)
{
    public override string ToString() => Label;
}
public sealed record OperationalSession(string Id, string Label)
{
    public override string ToString() => Label;
}
public sealed record OperationalDevice(string? Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>The UI observes the service's history; it does not start another hardware reader.</summary>
public sealed class OperationalCenterViewModel : ObservableObject, IDisposable
{
    private readonly IEngineClient _engine;
    private readonly DispatcherTimer _refreshTimer;
    private readonly SystemOperationJournal _systemJournal;
    private EngineSnapshot _snapshot = new();
    private CancellationTokenSource? _refreshCts;
    private readonly CancellationTokenSource _lifetime = new();
    private int _queryGeneration;
    private bool _active, _disposed, _isBusy;
    private OperationalMetric _selectedMetric;
    private OperationalRange _selectedRange;
    private OperationalSession? _baselineSession, _candidateSession;
    private OperationalDevice? _selectedDevice;
    private HistorySeries? _series;
    private string _status = string.Empty, _comparison = string.Empty;
    private static CultureInfo Ui => UiLanguage.Culture;
    private const string Green = "#34D399", Amber = "#FBBF24", Red = "#FB7185", Muted = "#94A3B8";

    public OperationalCenterViewModel(IEngineClient engine, Dispatcher? dispatcher = null, SystemOperationJournal? journal = null)
    {
        _engine = engine;
        _systemJournal = journal ?? SystemOperationJournal.Shared;
        Metrics = new[]
        {
            ("cpu.temp", "°C"), ("gpu.temp", "°C"), ("coolant.temp", "°C"), ("cpu.load", "%"), ("gpu.load", "%"),
            ("cpu.power", "W"), ("gpu.power", "W"), ("fans.rpm", "RPM"), ("pump.rpm", "RPM"), ("fans.power", "%"),
            ("fan.rpm", "RPM"), ("fan.power", "%"), ("ram.usage", "%"), ("net.down", "B/s"), ("net.up", "B/s"), ("disk.activity", "%"),
            ("disk.device.temp", "°C"), ("disk.device.read", "B/s"), ("disk.device.write", "B/s"), ("volume.used", "%"), ("volume.free", "GiB"),
            ("engine.memory.private", "B"), ("engine.cpu", "%")
        }.Select(m => new OperationalMetric(m.Item1, OperationalText.Sensor(m.Item1), m.Item2)).ToArray();
        Ranges = [new(600, OperationalText.Pick("10 minutos", "10 minutes")), new(3600, OperationalText.Pick("1 hora", "1 hour")),
            new(86400, OperationalText.Pick("24 horas", "24 hours")), new(604800, OperationalText.Pick("7 dias", "7 days"))];
        _selectedMetric = Metrics[0]; _selectedRange = Ranges[0];
        Devices.Add(new(null, OperationalText.AllDevices)); _selectedDevice = Devices[0];
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher ?? Dispatcher.CurrentDispatcher) { Interval = TimeSpan.FromSeconds(10) };
        _refreshTimer.Tick += RefreshTimerTick;
        RefreshCommand = new RelayCommand(async () => await RefreshHistoryAsync(), () => !IsBusy);
        CompareCommand = new RelayCommand(async () => await CompareAsync(), () => !IsBusy && CanCompare);
        _snapshot = engine.Snapshot ?? new();
    }

    public IReadOnlyList<OperationalMetric> Metrics { get; }
    public IReadOnlyList<OperationalRange> Ranges { get; }
    public ObservableCollection<OperationalSensorRow> Sensors { get; } = [];
    public ObservableCollection<OperationalFanRow> Fans { get; } = [];
    public ObservableCollection<OperationalCommandRow> Commands { get; } = [];
    public ObservableCollection<OperationalEventRow> Events { get; } = [];
    public ObservableCollection<OperationalGuidance> Guidance { get; } = [];
    public ObservableCollection<OperationalSession> Sessions { get; } = [];
    public ObservableCollection<OperationalDevice> Devices { get; } = [];
    public ObservableCollection<OperationalEventRow> InventoryChanges { get; } = [];
    public ObservableCollection<OperationalCommandRow> WindowsOperations { get; } = [];
    public ICommand RefreshCommand { get; }
    public ICommand CompareCommand { get; }

    public OperationalMetric SelectedMetric
    {
        get => _selectedMetric;
        set { if (value != null && SetProperty(ref _selectedMetric, value)) { RebuildDevices(true); RequestRefresh(); } }
    }
    public OperationalRange SelectedRange
    {
        get => _selectedRange;
        set { if (value != null && SetProperty(ref _selectedRange, value)) RequestRefresh(); }
    }
    public OperationalSession? BaselineSession
    {
        get => _baselineSession;
        set { if (SetProperty(ref _baselineSession, value)) OnPropertyChanged(nameof(CanCompare)); }
    }
    public OperationalSession? CandidateSession
    {
        get => _candidateSession;
        set { if (SetProperty(ref _candidateSession, value)) OnPropertyChanged(nameof(CanCompare)); }
    }
    public bool CanCompare => BaselineSession != null && CandidateSession != null && BaselineSession.Id != CandidateSession.Id;
    public OperationalDevice? SelectedDevice
    {
        get => _selectedDevice;
        set { if (SetProperty(ref _selectedDevice, value)) RequestRefresh(); }
    }
    public bool HasDeviceFilter => SelectedMetric.Key.StartsWith("fan.", StringComparison.Ordinal) || SelectedMetric.Key == "pump.rpm" ||
        SelectedMetric.Key.StartsWith("disk.device.", StringComparison.Ordinal) || SelectedMetric.Key.StartsWith("volume.", StringComparison.Ordinal);
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Comparison { get => _comparison; private set => SetProperty(ref _comparison, value); }
    public HistorySeries? Series => _series;
    public IReadOnlyList<HistoryPoint> HistoryPoints => _series?.Points ?? [];
    public string ChartUnit => _series?.Unit ?? SelectedMetric.Unit;
    public string Minimum => OperationalText.Number(_series?.Statistics.Min, ChartUnit);
    public string Maximum => OperationalText.Number(_series?.Statistics.Max, ChartUnit);
    public string Average => OperationalText.Number(_series?.Statistics.Average, ChartUnit);
    public string P95 => OperationalText.Number(_series?.Statistics.P95, ChartUnit);
    public string Coverage => _series == null ? "—" : OperationalText.Number(_series.Statistics.CoveragePercent, "%");
    public string AboveWarning => _series?.Statistics.Threshold == null ? "—" : OperationalText.Age(_series.Statistics.SecondsAboveThreshold);
    public string HistoryDescription => _series == null || _series.Statistics.ValidSamples == 0 ? OperationalText.NoReadings :
        OperationalText.Pick("Amostras válidas: ", "Valid samples: ") + $"{_series.Statistics.ValidSamples:N0} · " +
        OperationalText.Time(_series.FromUtc) + " — " + OperationalText.Time(_series.ToUtc) + " · " +
        OperationalText.Pick("cadência esperada: ", "expected cadence: ") + OperationalText.Age(_series.ExpectedIntervalSeconds);
    public bool HasCommands => Commands.Count > 0;
    public bool HasEvents => Events.Count > 0;
    public bool HasSensors => Sensors.Count > 0;
    public bool HasWindowsOperations => WindowsOperations.Count > 0;
    public bool HasInventoryChanges => InventoryChanges.Count > 0;
    public string ProtectionTitle { get; private set; } = "—";
    public string ProtectionDetail { get; private set; } = string.Empty;
    public string ProtectionColor { get; private set; } = Muted;
    public string QualityTitle { get; private set; } = "—";
    public string QualityDetail { get; private set; } = string.Empty;
    public string PersistenceTitle { get; private set; } = "—";
    public string PersistenceDetail { get; private set; } = string.Empty;
    public string StorageTitle { get; private set; } = "—";
    public string StorageDetail { get; private set; } = string.Empty;
    public string InventorySummary { get; private set; } = string.Empty;

    public void SetActive(bool active)
    {
        if (_disposed || _active == active) return;
        _active = active;
        if (active)
        {
            ApplySnapshot(_snapshot);
            _refreshTimer.Start();
            _ = RefreshHistoryAsync();
        }
        else
        {
            _refreshTimer.Stop();
            _refreshCts?.Cancel();
        }
    }

    public void ApplySnapshot(EngineSnapshot snapshot)
    {
        if (_disposed) return;
        _snapshot = snapshot;
        // Snapshot presentation is cheap and bounded; persistent history queries run only while visible.
        bool snapshotExpired = DateTimeOffset.UtcNow - new DateTimeOffset(snapshot.Timestamp.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(snapshot.Timestamp, DateTimeKind.Local) : snapshot.Timestamp).ToUniversalTime() > TimeSpan.FromSeconds(5);
        bool disconnected = !_engine.IsConnected || snapshotExpired;
        bool protectionEnabled = _engine.Config.Safety.FailSafeEnabled;
        var required = snapshot.TelemetryHealth.Where(h => h.Validity != ReadingValidity.NotSupported &&
            (h.Key is "cpu.temp" or "gpu.temp" or "coolant.temp" or "hub.rpm" || h.Key.StartsWith("fan:", StringComparison.Ordinal))).ToArray();
        int fresh = disconnected ? 0 : snapshot.TelemetryHealth.Count(h => h.Validity == ReadingValidity.Fresh);
        int supported = snapshot.TelemetryHealth.Count(h => h.Validity != ReadingValidity.NotSupported);
        bool missingRequired = snapshot.SensorsReady && snapshot.TelemetryHealth.Count == 0 ||
            required.Any(h => h.Validity != ReadingValidity.Fresh && (h.LastSuccessUtc != null || h.Key == "cpu.temp"));
        bool critical = snapshot.FailSafeActive || snapshot.Alerts.Any(a => a.Severity == AlertSeverity.Critical);
        bool persistenceProblem = snapshot.Persistence.State == PersistenceState.Failed || !snapshot.HistoryStorage.StorageAvailable || snapshot.HistoryStorage.DroppedWrites > 0;
        ProtectionColor = critical ? Red : disconnected || !protectionEnabled || missingRequired || persistenceProblem || snapshot.Alerts.Any(a => a.Severity == AlertSeverity.Warning) ? Amber : !snapshot.SensorsReady ? Muted : Green;
        ProtectionTitle = disconnected ? OperationalText.Pick("Sem comunicação", "Disconnected") : snapshot.FailSafeActive
            ? OperationalText.Pick("Modo de segurança ativo", "Safety mode active") : !protectionEnabled
                ? OperationalText.Pick("Proteção desativada", "Protection disabled") : missingRequired
                    ? OperationalText.Pick("Monitoramento degradado", "Monitoring degraded") : critical || persistenceProblem || snapshot.Alerts.Any(a => a.Severity == AlertSeverity.Warning)
                        ? OperationalText.Pick("Requer atenção", "Attention required") : !snapshot.SensorsReady
                            ? OperationalText.Pick("Inicializando sensores", "Initializing sensors") : OperationalText.Pick("Proteções ativas", "Protections enabled");
        ProtectionDetail = snapshot.FailSafeActive ? EngineTextUi.Localize(snapshot.FailSafeReasonText, snapshot.FailSafeReason ?? string.Empty)
            : _engine.IsRemote ? OperationalText.Pick("A proteção continua no serviço, mesmo sem a janela aberta.", "Protection stays in the service when the window is closed.")
                : OperationalText.Pick("O motor local controla o hardware enquanto o aplicativo está em execução.", "The local engine controls hardware while the application is running.");
        QualityTitle = $"{fresh} / {supported}";
        QualityDetail = disconnected ? OperationalText.Pick("Estado atual não confirmado pelo serviço.", "Current state not confirmed by the service.")
            : OperationalText.Pick("leituras com aquisição recente", "readings with a recent acquisition");
        PersistenceTitle = OperationalText.Persistence(snapshot.Persistence.State);
        PersistenceDetail = OperationalText.Pick("Ativa / salva: ", "Active / saved: ") + $"{snapshot.ConfigRevision} / {snapshot.Persistence.SavedRevision}" +
            (snapshot.Persistence.Error == null ? string.Empty : " · " + snapshot.Persistence.Error);
        var storage = snapshot.HistoryStorage;
        StorageTitle = !storage.PersistenceEnabled ? OperationalText.Pick("Somente nesta sessão", "This session only")
            : storage.StorageAvailable ? _engine.IsRemote ? OperationalText.Pick("Coleta no serviço", "Collected by service")
                : OperationalText.Pick("Coleta local", "Collected locally") : OperationalText.Pick("Falha no armazenamento", "Storage failure");
        StorageDetail = storage.LastError ?? OperationalText.Pick("Última gravação: ", "Last write: ") + OperationalText.Time(storage.LastWriteUtc);
        if (storage.DroppedWrites > 0) StorageDetail += " · " + OperationalText.Pick("registros descartados: ", "dropped records: ") + storage.DroppedWrites.ToString("N0", Ui);
        if (storage.CorruptRecords > 0) StorageDetail += " · " + OperationalText.Pick("registros recuperados com falha: ", "damaged records skipped: ") + storage.CorruptRecords.ToString("N0", Ui);

        Replace(Sensors, snapshot.TelemetryHealth.OrderBy(h => h.Validity == ReadingValidity.Fresh ? 1 : h.Validity == ReadingValidity.NotSupported ? 2 : 0)
            .ThenBy(h => h.Key).Take(128).Select(h => new OperationalSensorRow(h.Key, OperationalText.Sensor(h.Key), h.Source,
                OperationalText.Validity(disconnected && h.Validity == ReadingValidity.Fresh ? ReadingValidity.Stale : h.Validity),
                OperationalText.Age(h.LastSuccessUtc is { } last ? Math.Max(h.AgeSeconds ?? 0, (DateTimeOffset.UtcNow - last).TotalSeconds) : h.AgeSeconds),
                OperationalText.Time(h.LastSuccessUtc), h.ConsecutiveFailures.ToString("N0", Ui), ReadingReason(h.Reason), QualityColor(disconnected && h.Validity == ReadingValidity.Fresh ? ReadingValidity.Stale : h.Validity))));
        var config = _engine.Config;
        Replace(Fans, snapshot.Fans.Take(64).Select(f => new OperationalFanRow(config.GetFan(f.Key).Name ?? f.DefaultName,
            f.Model, f.CanControlSpeed ? Percent(f.RequestedPercent) : "—", f.CanControlSpeed && f.AppliedPercent > 0 ? Percent(f.AppliedPercent) : "—",
            !disconnected && f.HasTachometer && f.RpmAvailable ? f.Rpm.ToString("N0", Ui) + " RPM" : "—",
            OperationalText.ControlReason(f.ControlReason), f.CanControlSpeed
                ? OperationalText.Pick("Velocidade · RPM", "Speed · RPM") : OperationalText.Pick("Módulo sem tacômetro", "Module without tachometer"),
            OperationalText.Validity(f.HasTachometer ? disconnected ? ReadingValidity.Stale : f.RpmValidity : ReadingValidity.NotSupported),
            QualityColor(f.HasTachometer ? disconnected ? ReadingValidity.Stale : f.RpmValidity : ReadingValidity.NotSupported))));
        var receipts = snapshot.Operations.Concat(_engine.Operations).GroupBy(o => o.Id).Select(g => g.OrderByDescending(r => r.UpdatedAtUtc).First());
        Replace(Commands, receipts.Where(r => r.Kind != "sessionContext").OrderByDescending(r => r.UpdatedAtUtc).Take(30).Select(CommandRow));
        Replace(WindowsOperations, _systemJournal.Read().Take(20).Select(r => new OperationalCommandRow(r.Id,
            r.Title, WindowsState(r.State), r.ExitCode is { } exit ? OperationalText.Pick("Código de saída: ", "Exit code: ") + exit.ToString(Ui) : OperationalText.Pending,
            _systemJournal.PersistenceAvailable ? OperationalText.Pick("Registrada", "Recorded") : OperationalText.Pick("Falha ao registrar", "Record failed"),
            OperationalText.Time(r.UpdatedAtUtc), r.Detail ?? OperationalText.NotVerified,
            r.State is WindowsOperationState.Failed ? Red : r.State is WindowsOperationState.Running ? Muted : Amber)));
        InventorySummary = snapshot.Inventory.LastScanUtc == null ? OperationalText.Pick("Aguardando inventário do serviço.", "Waiting for engine inventory.") :
            string.Format(Ui, OperationalText.Pick("{0} componentes · coleta em {1} · {2} categorias indisponíveis", "{0} components · acquired {1} · {2} unavailable categories"),
                snapshot.Inventory.Devices.Count, OperationalText.Time(snapshot.Inventory.LastScanUtc), snapshot.Inventory.UnavailableCategories.Count);
        Replace(InventoryChanges, snapshot.Inventory.Changes.Take(20).Select(c => new OperationalEventRow(OperationalText.Time(c.TimestampUtc),
            OperationalText.Event("inventory." + c.Change), c.Category, c.Name + (c.Change == "changed" ? " · " + string.Join(", ", c.After.Keys.Where(k => c.Before.GetValueOrDefault(k) != c.After[k]).Take(6)) : ""), Muted)));
        Replace(Guidance, BuildGuidance(snapshot, disconnected, protectionEnabled, missingRequired));
        RebuildDevices(false);
        OnPropertiesChanged(nameof(ProtectionTitle), nameof(ProtectionDetail), nameof(ProtectionColor), nameof(QualityTitle), nameof(QualityDetail),
            nameof(PersistenceTitle), nameof(PersistenceDetail), nameof(StorageTitle), nameof(StorageDetail), nameof(InventorySummary),
            nameof(HasCommands), nameof(HasSensors), nameof(HasWindowsOperations), nameof(HasInventoryChanges));
    }

    public async Task RefreshHistoryAsync()
    {
        if (_disposed) return;
        int generation = ++_queryGeneration;
        _refreshCts?.Cancel(); _refreshCts?.Dispose();
        _refreshCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _refreshCts.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = _refreshCts.Token;
        IsBusy = true;
        Status = OperationalText.Pick("Consultando histórico do serviço…", "Loading engine history…");
        try
        {
            var query = CreateQuery();
            var history = _engine.QueryHistoryAsync(query, ct);
            var events = _engine.QueryEventsAsync(new() { FromUtc = query.FromUtc, ToUtc = query.ToUtc, MaxItems = 100 }, ct);
            var sessions = _engine.QuerySessionsAsync(ct);
            await Task.WhenAll(history, events, sessions);
            if (_disposed || generation != _queryGeneration || ct.IsCancellationRequested) return;
            _series = await history;
            Replace(Events, (await events).Select(e => new OperationalEventRow(OperationalText.Time(e.TimestampUtc), OperationalText.Event(e.Kind),
                e.DeviceId ?? e.Source, EventDetail(e), e.Severity == AlertSeverity.Critical ? Red : e.Severity == AlertSeverity.Warning ? Amber : Muted)));
            var sessionRows = (await sessions).Select(s => new OperationalSession(s.Id,
                OperationalText.Time(s.StartedUtc) + " · " + s.Id[..Math.Min(8, s.Id.Length)])).ToArray();
            string? baseline = BaselineSession?.Id, candidate = CandidateSession?.Id;
            Replace(Sessions, sessionRows);
            BaselineSession = Sessions.FirstOrDefault(s => s.Id == baseline) ?? Sessions.Skip(1).FirstOrDefault();
            CandidateSession = Sessions.FirstOrDefault(s => s.Id == candidate) ?? Sessions.FirstOrDefault();
            Status = OperationalText.Pick("Histórico atualizado. A coleta continua sem esta tela.", "History updated. Collection continues without this screen.");
            OnPropertiesChanged(nameof(Series), nameof(HistoryPoints), nameof(ChartUnit), nameof(Minimum), nameof(Maximum), nameof(Average), nameof(P95),
                nameof(Coverage), nameof(AboveWarning), nameof(HistoryDescription), nameof(HasEvents), nameof(CanCompare));
        }
        catch (OperationCanceledException) { if (generation == _queryGeneration) Status = OperationalText.Pick("Consulta interrompida ou prazo excedido.", "Query canceled or timed out."); }
        catch (Exception ex)
        {
            if (generation == _queryGeneration)
                Status = OperationalText.Pick("Não foi possível consultar o histórico: ", "History query failed: ") + ex.Message;
        }
        finally { if (generation == _queryGeneration) IsBusy = false; }
    }

    public async Task CompareAsync()
    {
        if (_disposed || !CanCompare || IsBusy) return;
        IsBusy = true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var comparison = await _engine.CompareSessionsAsync(CreateQuery(), BaselineSession!.Id, CandidateSession!.Id, timeout.Token);
            if (_disposed) return;
            Comparison = comparison.Baseline.ValidSamples == 0 || comparison.Candidate.ValidSamples == 0
                ? OperationalText.Pick("Uma sessão não tem dados no período escolhido. Amplie o período antes de comparar.", "One session has no readings in this range. Expand the range before comparing.")
                : OperationalText.Pick("Diferença da média: ", "Average difference: ") + OperationalText.Number(comparison.AverageDifference, SelectedMetric.Unit) +
                  " · P95: " + OperationalText.Number(comparison.P95Difference, SelectedMetric.Unit) + " · " +
                  OperationalText.Pick("Compare também carga e condições de uso.", "Also compare workload and operating conditions.");
        }
        catch (Exception ex) { Comparison = OperationalText.Pick("Comparação indisponível: ", "Comparison unavailable: ") + ex.Message; }
        finally { if (!_disposed) IsBusy = false; }
    }

    public async Task ExportAsync(string path, bool csv, CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        var query = CreateQuery();
        string? temporary = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var text = await _engine.ExportHistoryAsync(query, csv, csv ? null : new EventQuery { FromUtc = query.FromUtc, ToUtc = query.ToUtc, MaxItems = 100 }, timeout.Token);
            string full = Path.GetFullPath(path);
            temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, text, new System.Text.UTF8Encoding(true), timeout.Token);
            File.Move(temporary, full, overwrite: true);
            Status = OperationalText.Pick("Histórico exportado: ", "History exported: ") + full;
        }
        catch (Exception ex) { Status = OperationalText.Pick("Falha ao exportar: ", "Export failed: ") + ex.Message; }
        finally { if (temporary != null) try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    private HistoryQuery CreateQuery()
    {
        var to = DateTimeOffset.UtcNow;
        double? threshold = SelectedMetric.Key switch
        {
            "cpu.temp" => _engine.Config.Safety.CpuWarn, "gpu.temp" => _engine.Config.Safety.GpuWarn,
            "coolant.temp" => _engine.Config.Safety.CoolantWarn, _ => null
        };
        return new() { MetricKey = SelectedMetric.Key, DeviceId = HasDeviceFilter ? SelectedDevice?.Id : null, FromUtc = to.AddSeconds(-SelectedRange.Seconds), ToUtc = to,
            MaxPoints = 900, BucketSeconds = Math.Max(1, SelectedRange.Seconds / 900), Threshold = threshold };
    }

    private void RebuildDevices(bool reset)
    {
        var options = new List<OperationalDevice> { new(null, OperationalText.AllDevices) };
        if (SelectedMetric.Key.StartsWith("fan.", StringComparison.Ordinal) || SelectedMetric.Key == "pump.rpm")
            options.AddRange(_snapshot.Fans.Where(f => f.HasTachometer && (SelectedMetric.Key != "pump.rpm" || f.IsPump)).Take(64)
                .Select(f => new OperationalDevice(f.Key, _engine.Config.GetFan(f.Key).Name ?? f.DefaultName)));
        else if (SelectedMetric.Key.StartsWith("disk.device.", StringComparison.Ordinal))
            options.AddRange((_snapshot.Disks ?? []).Take(64).Select(d => new OperationalDevice($"disk:{d.Index}", d.Name)));
        else if (SelectedMetric.Key.StartsWith("volume.", StringComparison.Ordinal))
            options.AddRange(_snapshot.Volumes.Take(64).Select(v => new OperationalDevice($"vol:{v.Letter}", v.Letter + " " + v.Label)));
        if (!reset && _selectedDevice?.Id is { } selected && options.All(o => o.Id != selected)) options.Add(_selectedDevice);
        string? keep = reset ? null : _selectedDevice?.Id;
        Replace(Devices, options.DistinctBy(o => o.Id));
        _selectedDevice = Devices.FirstOrDefault(o => o.Id == keep) ?? Devices.FirstOrDefault();
        OnPropertiesChanged(nameof(SelectedDevice), nameof(HasDeviceFilter));
    }

    private IEnumerable<OperationalGuidance> BuildGuidance(EngineSnapshot s, bool disconnected, bool protectionEnabled, bool missingRequired)
    {
        if (disconnected) yield return new(OperationalText.Pick("Restabelecer a comunicação", "Restore communication"),
            OperationalText.Pick("O serviço não confirmou o estado atual.", "The engine has not confirmed current state."),
            OperationalText.Pick("Confira o serviço em Configurações. Valores anteriores não representam novas medições.", "Check the service in Settings. Previous values are not new measurements."), Amber);
        if (s.FailSafeActive) yield return new(OperationalText.Pick("Proteção em atuação", "Protection is active"), ProtectionDetail,
            OperationalText.Pick("Verifique a origem do alerta e acompanhe as leituras válidas até a recuperação estável.", "Inspect the alert source and follow valid readings until stable recovery."), Red);
        if (!protectionEnabled) yield return new(OperationalText.Pick("Proteção térmica desativada", "Thermal protection disabled"),
            OperationalText.Pick("O modo de segurança foi desligado na configuração ativa.", "Safety mode is disabled in the active configuration."),
            OperationalText.Pick("Revise a decisão em Configurações antes de depender da proteção automática.", "Review this setting before relying on automatic protection."), Red);
        if (missingRequired) yield return new(OperationalText.Pick("Parte do monitoramento está indisponível", "Some monitoring is unavailable"),
            OperationalText.Pick("Há leituras de controle ausentes, vencidas ou inválidas.", "Control readings are missing, stale or invalid."),
            OperationalText.Pick("Consulte a origem e a idade abaixo. Uma fonte alternativa precisa de uma política de curva explícita.", "Check source and age below. An alternative source needs an explicit curve policy."), Amber);
        if (s.CurveUsingFallback) yield return new(OperationalText.Pick("Curva usando uma fonte alternativa", "Curve is using an alternative source"),
            $"{s.CurveRequestedSource} → {s.CurveEffectiveSource?.ToString() ?? "—"}",
            OperationalText.Pick("Confira a política e a potência mínima da curva em Ventoinhas.", "Check curve fallback policy and minimum duty in Fans."), Amber);
        if (s.Persistence.State == PersistenceState.Failed || !s.HistoryStorage.StorageAvailable) yield return new(
            OperationalText.Pick("Falha de persistência", "Persistence failure"), s.Persistence.Error ?? s.HistoryStorage.LastError ?? "—",
            OperationalText.Pick("Verifique espaço e acesso à pasta de dados. Alterações sem gravação confirmada podem não sobreviver ao reinício.", "Check free space and data-folder access. Unconfirmed saves may not survive restart."), Amber);
        foreach (var alert in s.Alerts.Where(a => a.Key.EndsWith("-temp", StringComparison.Ordinal)).Take(3))
            yield return new(EngineTextUi.Localize(alert.TitleText, alert.Title), EngineTextUi.Localize(alert.MessageText, alert.Message),
                OperationalText.Pick("Correlacione o período acima do alerta com a carga e a resposta da refrigeração. Revise a fonte antes de alterar limites.",
                    "Correlate time above warning with workload and cooling response. Review the source before changing thresholds."), alert.Severity == AlertSeverity.Critical ? Red : Amber);
        if (!disconnected && !s.FailSafeActive && protectionEnabled && !missingRequired && !s.Alerts.Any(a => a.Severity != AlertSeverity.Info))
            yield return new(OperationalText.Pick("Sem incidente ativo nas leituras acompanhadas", "No active incident in monitored readings"),
                OperationalText.Pick("As proteções estão habilitadas. Confira a cobertura do histórico antes de tirar conclusões sobre um período maior.", "Protections are enabled. Check history coverage before drawing conclusions about a longer period."),
                OperationalText.Pick("Use a comparação de sessões para avaliar mudanças sob cargas semelhantes.", "Use session comparison to evaluate changes under similar workloads."), Green);
    }

    private static OperationalCommandRow CommandRow(CommandReceipt r) => new(r.Id, OperationalText.Action(r.Kind), OperationalText.Command(r.State),
        OperationalText.Confirmation(r.Confirmation), OperationalText.Persistence(r.Persistence), OperationalText.Time(r.UpdatedAtUtc),
        string.Join(" · ", r.Targets.Take(6).Select(t => t.Target + ": " + (t.Accepted == true ? OperationalText.Pick("aceito", "accepted") : t.Accepted == false
            ? OperationalText.Pick("recusado", "refused") : OperationalText.Pick("não confirmado", "unconfirmed")))) is { Length: > 0 } targets ? targets : r.Message,
        r.State is CommandState.Failed or CommandState.Rejected or CommandState.Conflict ? Red : r.State is CommandState.Applied ? Green : Amber);
    private static string EventDetail(OperationalEvent e) => e.Kind == "configuration.changed"
        ? OperationalText.Pick("Campos: ", "Fields: ") + e.Data.GetValueOrDefault("fields", "—")
        : e.Kind.StartsWith("rgb.", StringComparison.Ordinal) ? OperationalText.RgbEventDetail(e)
        : e.Data.TryGetValue("kind", out var command) ? OperationalText.Action(command) + " · " + e.Message : e.Message;
    private static string Percent(byte? value) => value is { } p ? p.ToString(Ui) + "%" : "—";
    private static string QualityColor(ReadingValidity validity) => validity == ReadingValidity.Fresh ? Green : validity == ReadingValidity.NotSupported ? Muted : Amber;
    private static string ReadingReason(string? reason) => reason switch
    {
        null => string.Empty, "reading-unavailable" => OperationalText.Pick("Sem resposta da leitura", "No reading response"),
        "acquisition-overdue" => OperationalText.Pick("Coleta fora do prazo", "Acquisition overdue"),
        "hub-disconnected" => OperationalText.Pick("Hub desconectado", "Hub disconnected"),
        "not-reported" => OperationalText.Pick("Não informado pela fonte", "Not reported by source"),
        "outside-valid-range" => OperationalText.Pick("Valor fora da faixa válida", "Value outside valid range"),
        "provider-read-failed" => OperationalText.Pick("Falha no provedor de sensores", "Sensor provider read failed"),
        "network-read-unavailable" => OperationalText.Pick("Sem leitura da rede", "Network reading unavailable"),
        "volume-read-unavailable" or "volume-no-longer-reported" => OperationalText.Pick("Sem leitura do volume", "Volume reading unavailable"),
        "storage-read-unavailable" => OperationalText.Pick("Sem leitura do disco", "Disk reading unavailable"),
        "not-requested" => OperationalText.Pick("Coleta sob demanda inativa", "On-demand collection inactive"),
        "resuming-awaiting-acquisition" => OperationalText.Pick("Aguardando coleta após retomar", "Awaiting acquisition after resume"),
        "rpm-read-failed" => OperationalText.Pick("Falha na leitura de RPM", "RPM reading failed"),
        _ => OperationalText.Pick("Detalhe disponível no diagnóstico", "Details available in diagnostics")
    };
    private static string WindowsState(WindowsOperationState state) => state switch
    {
        WindowsOperationState.Running => OperationalText.Pick("Em andamento", "Running"),
        WindowsOperationState.Failed => OperationalText.Pick("Falhou", "Failed"),
        WindowsOperationState.ProcessExited => OperationalText.Pick("Processo encerrado", "Process exited"),
        WindowsOperationState.RequiresRestart => OperationalText.Pick("Reinício necessário", "Restart required"),
        WindowsOperationState.Succeeded => OperationalText.Pick("Concluída", "Completed"),
        WindowsOperationState.Canceled => OperationalText.Pick("Cancelada", "Canceled"),
        _ => OperationalText.Pick("Resultado não confirmado", "Result unconfirmed")
    };
    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> rows)
    {
        var values = rows.ToArray();
        if (collection.SequenceEqual(values)) return;
        if (collection.Count == values.Length)
        {
            for (int i = 0; i < values.Length; i++) if (!EqualityComparer<T>.Default.Equals(collection[i], values[i])) collection[i] = values[i];
        }
        else { collection.Clear(); foreach (var row in values) collection.Add(row); }
    }
    internal void ObserveSnapshot(EngineSnapshot snapshot)
    {
        _snapshot = snapshot;
        if (_active) ApplySnapshot(snapshot);
    }
    private async void RefreshTimerTick(object? sender, EventArgs e)
    {
        if (!_active) return;
        ApplySnapshot(_snapshot);
        if (!IsBusy) await RefreshHistoryAsync();
    }
    private void RequestRefresh() { Comparison = string.Empty; if (_active) _ = RefreshHistoryAsync(); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _active = false; _refreshTimer.Stop(); _refreshTimer.Tick -= RefreshTimerTick;
        _refreshCts?.Cancel(); _refreshCts?.Dispose();
        _lifetime.Cancel(); _lifetime.Dispose();
    }
}
