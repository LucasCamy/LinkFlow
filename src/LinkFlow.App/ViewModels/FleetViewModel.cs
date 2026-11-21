using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using LinkFlow.App.Services;
using LinkFlow.Core;
using LinkFlow.Core.Ipc;

namespace LinkFlow.App.ViewModels;

public static class FleetText
{
    private static string T(string pt, string en) => OperationalText.Pick(pt, en);
    public static string Tab => T("Computadores", "Computers");
    public static string Title => T("Computadores pareados", "Paired computers");
    public static string Subtitle => T("As proteções continuam em cada computador, mesmo sem conexão.", "Protections remain on each computer, including while disconnected.");
    public static string Pair => T("Parear computador", "Pair computer");
    public static string Name => T("Nome", "Name");
    public static string Host => T("Endereço do agente", "Agent address");
    public static string Port => T("Porta", "Port");
    public static string Pin => T("SHA-256 do certificado TLS", "TLS certificate SHA-256");
    public static string Token => T("Credencial de pareamento", "Pairing credential");
    public static string PairHint => T("Configure o agente no destino com LinkFlow.Cli fleet init e informe a impressão e credencial fornecidas por ele. O acesso pela rede exige configuração explícita no destino.", "Configure the target with LinkFlow.Cli fleet init, then enter its certificate fingerprint and credential. Network access requires explicit configuration on the target.");
    public static string Refresh => T("Atualizar computadores", "Refresh computers");
    public static string Remove => T("Remover pareamento", "Remove pairing");
    public static string Apply => T("Aplicar perfil", "Apply profile");
    public static string History => T("Consultar CPU na última hora", "Query CPU over the last hour");
    public static string Export => T("Exportar histórico consultado", "Export queried history");
    public static string Empty => T("Nenhum computador pareado.", "No paired computers.");
    public static string Waiting => T("Aguardando consulta", "Waiting for a query");
    public static string Offline => T("Sem resposta; leituras anteriores não são atuais", "No response; previous readings are not current");
    public static string Observer => T("Somente observação", "Observation only");
    public static string Controller => T("Observação e perfis autorizados", "Observation and authorized profiles");
}

public sealed class FleetPeerViewModel(FleetPeerSettings settings) : ObservableObject
{
    private long _receivedTimestamp;
    public FleetPeerSettings Settings { get; } = settings;
    public string Name => Settings.Name;
    public string Address => $"{Settings.Host}:{Settings.Port}";
    public FleetSnapshot? Remote { get; private set; }
    public bool Connected { get; private set; }
    public DateTimeOffset? LastReceivedUtc { get; private set; }
    public string Status { get; private set; } = FleetText.Waiting;
    public bool Current => Connected && Remote?.SnapshotAgeSeconds is double age && double.IsFinite(age) &&
        age >= 0 && age + System.Diagnostics.Stopwatch.GetElapsedTime(_receivedTimestamp).TotalSeconds <= 5;
    public string Readings => Current && Remote is { } remote
        ? $"CPU {Temp(remote.Snapshot.CpuTemp)} · GPU {Temp(remote.Snapshot.GpuTemp)} · {OperationalText.Pick("Líquido", "Coolant")} {Temp(remote.Snapshot.CoolantTemp)}" : "—";
    private static string Temp(float? value) => value is { } v ? $"{v:0.0} °C" : "—";
    public bool CanControl => Current && Remote?.Permissions.HasFlag(FleetPermission.Control) == true;
    public FleetClient Client() => new(Settings, FleetSecretStore.UnprotectToken(Settings.ProtectedToken));
    public void Apply(FleetSnapshot result)
    {
        Remote = result; Connected = true; LastReceivedUtc = DateTimeOffset.UtcNow;
        _receivedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        Status = result.Snapshot.FailSafeActive ? OperationalText.Pick("Proteção ativa", "Protection active") :
            result.Snapshot.Alerts.Count > 0 ? OperationalText.Pick("Há alertas locais", "Local alerts present") : OperationalText.Pick("Conectado", "Connected");
        Notify();
    }
    public void Fail() { Connected = false; Status = FleetText.Offline; Notify(); }
    private void Notify() => OnPropertiesChanged(nameof(Remote), nameof(Connected), nameof(Current), nameof(Status), nameof(Readings), nameof(CanControl), nameof(LastReceivedUtc));
}

public sealed class FleetViewModel : ObservableObject, IDisposable
{
    internal sealed record HistoryExport(string? AgentId, string Name, string Host, HistorySeries Series);
    private readonly FleetPeerRepository _repository;
    private readonly Func<string, HistoryExport, CancellationToken, Task> _exportHistory;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private FleetPeerViewModel? _selected;
    private bool _busy, _disposed;
    private string _status = string.Empty;
    private HistorySeries? _queried;
    public FleetViewModel(FleetPeerRepository? repository = null) : this(repository, WriteHistoryAsync) { }
    internal FleetViewModel(FleetPeerRepository? repository, Func<string, HistoryExport, CancellationToken, Task> exportHistory)
    {
        _repository = repository ?? new();
        _exportHistory = exportHistory;
        foreach (var peer in _repository.Load()) Peers.Add(new(peer));
        _selected = Peers.FirstOrDefault();
        _timer.Tick += async (_, _) => await RefreshAsync();
    }
    public ObservableCollection<FleetPeerViewModel> Peers { get; } = [];
    public string NewName { get; set; } = string.Empty;
    public string NewHost { get; set; } = string.Empty;
    public string NewPort { get; set; } = "47931";
    public string NewPin { get; set; } = string.Empty;
    public string SelectedProfile { get; set; } = FanPresets.Balanced;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) OnPropertiesChanged(nameof(CanEdit), nameof(CanExport)); } }
    public bool CanEdit => !IsBusy;
    public bool CanExport => !_disposed && !IsBusy && _queried != null && Selected != null;
    public bool HasPeers => Peers.Count > 0;
    public bool HasSelection => _selected != null;
    public string HistorySummary { get; private set; } = string.Empty;
    public string Access => Selected?.Remote?.Permissions.HasFlag(FleetPermission.Control) == true ? FleetText.Controller : FleetText.Observer;
    public string SelectedDetail => Selected?.Remote is { } remote ?
        $"{remote.Name} · LinkFlow {remote.Version} · {Access}\n{OperationalText.Pick("Revisão", "Revision")} {remote.Snapshot.ConfigRevision} · {OperationalText.Persistence(remote.Snapshot.Persistence.State)}\n" +
        string.Join("\n", remote.Snapshot.Fans.Where(f => f.HasTachometer).Select(f => $"{f.DefaultName}: {(Selected.Current && f.RpmAvailable ? f.Rpm + " RPM" : "—")} · {f.ControlReason}")) : FleetText.Waiting;
    public IReadOnlyList<ProfileOption> Profiles => FanPresets.All.Select(p => new ProfileOption(p.Key, p.Label)).ToArray();
    public FleetPeerViewModel? Selected
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) { _queried = null; HistorySummary = string.Empty; OnPropertiesChanged(nameof(HasSelection), nameof(SelectedDetail), nameof(Access), nameof(HistorySummary), nameof(CanExport)); } }
    }
    public void SetActive(bool active) { if (_disposed) return; if (active) { _timer.Start(); _ = RefreshAsync(); } else _timer.Stop(); }
    public async Task<bool> PairAsync(string token)
    {
        if (IsBusy) return false;
        IsBusy = true;
        try
        {
            if (!int.TryParse(NewPort, out int port)) throw new ArgumentException("Porta inválida.");
            var peer = FleetPeerRepository.Pair(NewName, NewHost, port, NewPin, token.Trim());
            var result = await new FleetClient(peer, token.Trim()).GetSnapshotAsync(_lifetime.Token);
            if (Peers.Any(p => p.Settings.Host.Equals(peer.Host, StringComparison.OrdinalIgnoreCase) && p.Settings.Port == peer.Port))
                throw new InvalidOperationException("Endereço já pareado.");
            _repository.Save(Peers.Select(p => p.Settings).Append(peer));
            var added = new FleetPeerViewModel(peer); added.Apply(result); Peers.Add(added); Selected = added;
            Status = OperationalText.Pick("Pareamento verificado e credencial protegida pelo Windows.", "Pairing verified and credential protected by Windows.");
            OnPropertyChanged(nameof(HasPeers)); return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { Status = ex is FleetRequestException request ? request.Message : OperationalText.Pick("Pareamento não verificado. Confira endereço, certificado e credencial; remova um pareamento antigo para trocar o certificado.", "Pairing not verified. Check the address, certificate and credential; remove old pairing to replace its certificate."); return false; }
        finally { IsBusy = false; }
    }
    public async Task RefreshAsync()
    {
        if (IsBusy || _disposed) return;
        IsBusy = true;
        try
        {
            using var concurrency = new SemaphoreSlim(4);
            await Task.WhenAll(Peers.ToArray().Select(async peer =>
            {
                await concurrency.WaitAsync(_lifetime.Token);
                try { peer.Apply(await peer.Client().GetSnapshotAsync(_lifetime.Token)); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { peer.Fail(); }
                finally { concurrency.Release(); }
            }));
            OnPropertiesChanged(nameof(SelectedDetail), nameof(Access));
        }
        catch (OperationCanceledException) { }
        finally { IsBusy = false; }
    }
    public void RemoveSelected()
    {
        if (Selected == null || IsBusy) return;
        var removed = Selected; _repository.Save(Peers.Where(p => p != removed).Select(p => p.Settings));
        Peers.Remove(removed); Selected = Peers.FirstOrDefault(); OnPropertyChanged(nameof(HasPeers));
    }
    public async Task ApplyProfileAsync()
    {
        if (Selected is not { CanControl: true } peer || IsBusy) return;
        IsBusy = true;
        try
        {
        var current = await peer.Client().GetSnapshotAsync(_lifetime.Token);
            var next = current.Config.Clone(); FanPresets.Apply(next, SelectedProfile);
            var receipt = await peer.Client().SubmitCommandAsync(new() { Kind = PipeProtocol.SetConfig, ExpectedRevision = current.Config.Revision, Config = next,
                RequestedAtUtc = current.CollectedAtUtc, ExpiresAtUtc = current.CollectedAtUtc.AddSeconds(30) }, _lifetime.Token);
            Status = OperationalText.Command(receipt.State) + " · " + OperationalText.Confirmation(receipt.Confirmation);
            peer.Apply(await peer.Client().GetSnapshotAsync(_lifetime.Token)); OnPropertyChanged(nameof(SelectedDetail));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = ex is FleetRequestException request ? request.Message : FleetText.Offline; }
        finally { IsBusy = false; }
    }
    public async Task QueryHistoryAsync()
    {
        if (Selected is not { Current: true } peer || IsBusy) return;
        IsBusy = true;
        try
        {
            var current = await peer.Client().GetSnapshotAsync(_lifetime.Token);
            var queried = await peer.Client().QueryHistoryAsync(new() { MetricKey = "cpu.temp", FromUtc = current.CollectedAtUtc.AddHours(-1), ToUtc = current.CollectedAtUtc, BucketSeconds = 30, MaxPoints = 120 }, _lifetime.Token);
            AcceptHistory(peer, queried);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = FleetText.Offline; }
        finally { IsBusy = false; }
    }
    public async Task<bool> ExportHistoryAsync(string path)
    {
        if (!CanExport || Selected is not { } peer || _queried is not { } series) return false;
        // Query results are replaced, never edited. Capture both their owner and the result before yielding;
        // changing list selection during the export cannot relabel one computer's history as another's.
        var document = new HistoryExport(peer.Remote?.AgentId, peer.Name, peer.Settings.Host, series);
        IsBusy = true;
        try
        {
            await _exportHistory(path, document, _lifetime.Token);
            if (_disposed) return false;
            Status = OperationalText.Pick("Histórico exportado: ", "History exported: ") + document.Name + " · " + Path.GetFileName(path);
            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return false; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_disposed) Status = OperationalText.Pick("Não foi possível exportar o histórico: ", "Could not export history: ") + ex.Message;
            return false;
        }
        finally { IsBusy = false; }
    }
    internal bool AcceptHistory(FleetPeerViewModel peer, HistorySeries queried)
    {
        if (_disposed || Selected != peer) return false;
        _queried = queried;
        var stats = queried.Statistics;
        HistorySummary = stats.ValidSamples == 0 ? OperationalText.NoReadings :
            $"CPU · {OperationalText.Average}: {stats.Average:0.0} °C · {OperationalText.Maximum}: {stats.Max:0.0} °C · P95: {stats.P95:0.0} °C · {OperationalText.Coverage}: {stats.CoveragePercent:0.#}%";
        OnPropertiesChanged(nameof(HistorySummary), nameof(CanExport));
        return true;
    }
    private static Task WriteHistoryAsync(string path, HistoryExport document, CancellationToken cancellationToken) =>
        AtomicUserExport.WriteAsync(path, async (temporary, ct) =>
        {
            await using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await JsonSerializer.SerializeAsync(stream, document, Json.Options, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    public void Dispose() { if (_disposed) return; _disposed = true; _timer.Stop(); _lifetime.Cancel(); }
}

public partial class MainViewModel
{
    private FleetViewModel? _fleet;
    public FleetViewModel Fleet => _fleet ??= new();
    private void DisposeFleet() => _fleet?.Dispose();
}
