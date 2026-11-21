using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using LinkFlow.Core.Ipc;

namespace LinkFlow.Core;

public sealed partial class LinkFlowEngine
{
    private const int CommandHistoryLimit = 128;
    private readonly object _commandExecutionGate = new();
    private readonly object _commandGate = new();
    private readonly Dictionary<string, (string Fingerprint, CommandReceipt Receipt)> _commandReceipts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Fingerprint, CommandReceipt Receipt)> _commandReplay = new(StringComparer.Ordinal);
    private readonly Queue<string> _commandOrder = new();
    private readonly Dictionary<string, (long Revision, bool Accepted, DateTimeOffset At, string? Detail)> _deviceResults = new(StringComparer.Ordinal);
    private ConfigurationPersistence _configurationPersistence = new();
    private int _commandTracking;

    public event Action<CommandReceipt>? CommandUpdated;

    public ConfigurationPersistence Persistence { get { lock (_commandGate) return _configurationPersistence; } }
    public IReadOnlyList<CommandReceipt> GetCommandReceipts()
    {
        lock (_commandGate) return _commandOrder.Reverse().Select(id => _commandReceipts[id].Receipt).ToArray();
    }

    /// <summary>Transação idempotente. O prazo vale também para novas tentativas após reconectar.</summary>
    public CommandReceipt ExecuteCommand(EngineCommandRequest request, object? origin = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_commandExecutionGate)
        {
            EnsureCommandTracking();
            byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(request, Json.Compact);
            if (serialized.Length > PipeProtocol.MaxMessageBytes)
                return Receipt(request, CommandState.Rejected, "Solicitação maior que o limite do protocolo.");
            // A cache retém apenas o digest fixo: nunca centenas de cópias de configurações de até 1 MiB.
            string fingerprint = Convert.ToHexString(SHA256.HashData(serialized));
            lock (_commandGate)
            {
                foreach (string id in _commandReplay.Where(p => p.Value.Receipt.ExpiresAtUtc < DateTimeOffset.UtcNow.AddMinutes(-10)).Select(p => p.Key).ToArray())
                    _commandReplay.Remove(id);
                if (!string.IsNullOrWhiteSpace(request.Id) && _commandReplay.TryGetValue(request.Id, out var known))
                {
                    if (known.Fingerprint == fingerprint) return known.Receipt;
                    return Receipt(request, CommandState.Rejected, "Identificador já utilizado para uma operação diferente.");
                }
                if (_commandReplay.Count >= 4096)
                    return Receipt(request, CommandState.Rejected, "Limite de comandos récents atingido. Aguarde antes de tentar novamente.");
            }
            CommandReceipt receipt;
            if (request.Id is not { Length: > 0 and <= 80 } || request.Id.Any(char.IsControl))
                receipt = Receipt(request, CommandState.Rejected, "Identificador de comando inválido.");
            else if (request.Kind is not { Length: > 0 and <= 64 } || request.Kind.Any(char.IsControl))
                receipt = Receipt(request, CommandState.Rejected, "Tipo de comando inválido.");
            else if (request.Protocol != PipeProtocol.Version)
                receipt = Receipt(request, CommandState.Rejected, "Protocolo incompatível: atualize a interface e o serviço.");
            else if (request.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                receipt = Receipt(request, CommandState.Expired, "O prazo da solicitação terminou; nenhuma ação foi executada.");
            else if (request.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(5) || request.RequestedAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
                receipt = Receipt(request, CommandState.Rejected, "Prazo da solicitação inválido.");
            else
            {
                try { receipt = ApplyCommand(request, origin); }
                catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or JsonException)
                { receipt = Receipt(request, CommandState.Rejected, ex.Message); }
                catch (Exception ex)
                {
                    Log.Error($"Comando {request.Kind} falhou", ex);
                    receipt = Receipt(request, CommandState.Failed, "A operação falhou; consulte o diagnóstico.");
                }
            }
            if (request.Id is { Length: > 0 and <= 80 })
            {
                lock (_commandGate)
                {
                    _commandReceipts[request.Id] = (fingerprint, receipt);
                    _commandReplay[request.Id] = (fingerprint, receipt);
                    _commandOrder.Enqueue(request.Id);
                    while (_commandOrder.Count > CommandHistoryLimit) _commandReceipts.Remove(_commandOrder.Dequeue());
                }
            }
            PublishCommand(receipt);
            return receipt;
        }
    }

    private CommandReceipt ApplyCommand(EngineCommandRequest request, object? origin)
    {
        if (request.Kind == PipeProtocol.SetConfig)
        {
            if (request.Config == null || request.ExpectedRevision == null)
                return Receipt(request, CommandState.Rejected, "Configuração e revisão esperada são obrigatórias.");
            ControlConfig previous, updated;
            lock (_configLock)
            {
                previous = _config;
                if (request.ExpectedRevision != previous.Revision)
                    return Receipt(request, CommandState.Conflict, "A configuração mudou em outra origem. Releia e revise a alteração.", previous.Revision);
                var next = request.Config.Clone();
                ConfigurationSchema.ValidateAndMigrate(next);
                next.Normalize();
                if (previous.Revision == long.MaxValue) throw new InvalidOperationException("Limite de revisões atingido.");
                updated = Commit(next);
            }
            if (previous.Safety.FailSafeEnabled && !updated.Safety.FailSafeEnabled)
                Log.Warn("Modo de segurança DESLIGADO por comando explícito; alteração registrada.");
            AfterConfigChange(updated, origin);
            var targets = ConfigurationTargets(previous, updated).Select(t => new CommandTargetResult { Target = t }).ToArray();
            SupersedePendingConfigurations(updated.Revision);
            return Receipt(request, targets.Length == 0 ? CommandState.Applied : CommandState.Accepted,
                targets.Length == 0 ? "Configuração aplicada ao serviço." : "Configuração aceita; aguardando confirmação dos controladores.", updated.Revision) with
            {
                Confirmation = targets.Length == 0 ? CommandConfirmation.Software : CommandConfirmation.None,
                Targets = targets,
                Persistence = _store == null ? PersistenceState.NotRequired : PersistenceState.Pending
            };
        }

        switch (request.Kind)
        {
            case PipeProtocol.LcdFrame: RequestLcdFrame(); break;
            case PipeProtocol.Resume: NotifyResume(); break;
            case PipeProtocol.RefreshRgb: RefreshRgb(); break;
            case PipeProtocol.Rescan: RescanDevices(); break;
            case PipeProtocol.CalibrationStart: StartCalibration(); break;
            case PipeProtocol.CalibrationCancel: CancelCalibration(); break;
            case PipeProtocol.SessionContext:
                if (!PipeProtocol.TryRead<AutomationSessionContext>(request.Payload, out var context) || context == null)
                    throw new InvalidDataException("Contexto da sessão inválido.");
                SetSessionContext(origin ?? request.Id, context);
                return Receipt(request, CommandState.Applied, "Contexto da sessão recebido.") with { Confirmation = CommandConfirmation.Software };
            case PipeProtocol.AutomationOverride:
                if (!PipeProtocol.TryRead<DateTimeOffset?>(request.Payload, out var until)) throw new InvalidDataException("Prazo da automação inválido.");
                SetAutomationOverride(until);
                return Receipt(request, CommandState.Applied, "Intervenção manual da automação atualizada.") with { Confirmation = CommandConfirmation.Software };
            case PipeProtocol.ReportOperation:
                if (!PipeProtocol.TryRead<WindowsOperationRecord>(request.Payload, out var operation) || operation == null)
                    throw new InvalidDataException("Registro de operação inválido.");
                ReportExternalOperation(operation);
                return Receipt(request, CommandState.Applied, "Resultado da operação registrado.") with { Confirmation = CommandConfirmation.Software };
            case PipeProtocol.CurveTest:
                if (!PipeProtocol.TryRead<float?>(request.Payload, out var temperature)) throw new InvalidDataException("Temperatura inválida.");
                SetCurveTest(temperature);
                return Receipt(request, CommandState.Applied, "Teste de curva atualizado no serviço.") with { Confirmation = CommandConfirmation.Software };
            default: return Receipt(request, CommandState.Rejected, $"Operação não suportada: {request.Kind}.");
        }
        return Receipt(request, CommandState.NotVerifiable, "Solicitação encaminhada. A conclusão depende do estado publicado pelo dispositivo.");
    }

    private static IReadOnlyList<string> ConfigurationTargets(ControlConfig previous, ControlConfig next)
    {
        static string Serialize(object value) => JsonSerializer.Serialize(value, Json.Compact);
        var targets = new List<string>();
        if (Serialize(new { previous.Mode, previous.GlobalPercent, previous.Curve, previous.Fans, previous.GroupCurves }) !=
            Serialize(new { next.Mode, next.GlobalPercent, next.Curve, next.Fans, next.GroupCurves })) targets.Add("fans");
        if (previous.GpuFanAuto != next.GpuFanAuto || previous.GpuFanPercent != next.GpuFanPercent) targets.Add("gpu-fan");
        if (next.Rgb.SyncHub && !previous.Rgb.SameOutputFor(next.Rgb, RgbTargets.Hub)) targets.Add("rgb.hub");
        if (next.Rgb.SyncGpu && !previous.Rgb.SameOutputFor(next.Rgb, RgbTargets.Gpu)) targets.Add("rgb.gpu");
        if (next.Rgb.SyncMotherboard && !previous.Rgb.SameOutputFor(next.Rgb, RgbTargets.Motherboard)) targets.Add("rgb.motherboard");
        if (next.Rgb.SyncRam && !previous.Rgb.SameOutputFor(next.Rgb, RgbTargets.Ram)) targets.Add("rgb.ram");
        if (next.Rgb.SyncPeripherals && !previous.Rgb.SameOutputFor(next.Rgb, RgbTargets.Peripherals)) targets.Add("rgb.peripherals");
        if (Serialize(previous.Lcd) != Serialize(next.Lcd) || !previous.Rgb.SameLcdThemeAs(next.Rgb)) targets.Add("lcd");
        return targets;
    }

    private void SupersedePendingConfigurations(long revision)
    {
        lock (_commandGate)
            foreach (string id in _commandOrder.ToArray())
            {
                var entry = _commandReceipts[id];
                if (entry.Receipt.Kind != PipeProtocol.SetConfig || entry.Receipt.State != CommandState.Accepted ||
                    entry.Receipt.Revision >= revision) continue;
                var superseded = entry.Receipt with { State = CommandState.Superseded, UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Message = "Uma revisão de configuração mais recente substituiu este comando antes da confirmação completa." };
                _commandReceipts[id] = (entry.Fingerprint, superseded);
                PublishCommand(superseded);
            }
    }

    /// <summary>ACK do controlador. Não é prova de movimento da ventoinha nem da cor emitida pelo LED.</summary>
    public void ReportDeviceCommandResult(string target, long revision, bool accepted, string? detail = null)
    {
        lock (_commandGate)
        {
            if (!_deviceResults.TryGetValue(target, out var previous) || revision > previous.Revision ||
                revision == previous.Revision && (accepted != previous.Accepted || !string.Equals(detail, previous.Detail, StringComparison.Ordinal)))
                _deviceResults[target] = (revision, accepted, DateTimeOffset.UtcNow, detail);
            // Mesmo um resultado repetido deve avaliar o prazo. O primeiro timestamp do resultado
            // idêntico permanece estável; animações/reasserts não geram novas transições de comando.
            RefreshCommandResults();
        }
    }

    internal void MarkConfigurationPending(long revision)
    {
        lock (_commandGate)
            _configurationPersistence = _configurationPersistence with
            { ActiveRevision = revision, State = _store == null ? PersistenceState.NotRequired : PersistenceState.Pending, Error = null };
    }

    public void ReportConfigurationPersistence(long revision, bool saved)
    {
        lock (_commandGate)
        {
            var now = DateTimeOffset.UtcNow;
            _configurationPersistence = _configurationPersistence with
            {
                ActiveRevision = Math.Max(revision, _configurationPersistence.ActiveRevision),
                SavedRevision = saved ? Math.Max(revision, _configurationPersistence.SavedRevision) : _configurationPersistence.SavedRevision,
                State = !saved ? PersistenceState.Failed : revision < _configurationPersistence.ActiveRevision ? PersistenceState.Pending : PersistenceState.Saved,
                LastAttemptUtc = now,
                LastSuccessUtc = saved ? now : _configurationPersistence.LastSuccessUtc,
                Error = saved ? null : "A configuração está ativa, mas não foi possível salvá-la em disco."
            };
            foreach (string id in _commandOrder.ToArray())
            {
                var entry = _commandReceipts[id];
                if (entry.Receipt.Kind != PipeProtocol.SetConfig || entry.Receipt.Revision > revision ||
                    entry.Receipt.State is CommandState.Conflict or CommandState.Rejected or CommandState.Expired or CommandState.Failed) continue;
                var state = saved ? PersistenceState.Saved : PersistenceState.Failed;
                if (entry.Receipt.Persistence == state) continue;
                var next = entry.Receipt with { Persistence = state, UpdatedAtUtc = now };
                _commandReceipts[id] = (entry.Fingerprint, next);
                PublishCommand(next);
            }
        }
    }

    private CommandReceipt Receipt(EngineCommandRequest request, CommandState state, string message, long? revision = null) => new()
    {
        Id = request.Id is { Length: <= 80 } ? request.Id : string.Empty,
        Kind = request.Kind is { Length: <= 64 } ? request.Kind : string.Empty,
        State = state, Message = message.Length <= 1024 ? message : message[..1024],
        Revision = revision ?? GetConfig().Revision, RequestedAtUtc = request.RequestedAtUtc, UpdatedAtUtc = DateTimeOffset.UtcNow,
        ExpiresAtUtc = request.ExpiresAtUtc, Persistence = PersistenceState.NotRequired
    };

    private void EnsureCommandTracking()
    {
        if (Interlocked.Exchange(ref _commandTracking, 1) == 0) SnapshotUpdated += _ => { lock (_commandGate) RefreshCommandResults(); };
    }

    private void RefreshCommandResults()
    {
        foreach (string id in _commandOrder.ToArray())
        {
            var entry = _commandReceipts[id];
            var receipt = entry.Receipt;
            if (receipt.State is not (CommandState.Accepted or CommandState.NotVerifiable) || receipt.Targets.Count == 0) continue;
            var targets = receipt.Targets.Select(t => _deviceResults.TryGetValue(t.Target, out var result) && result.Revision == receipt.Revision
                ? t with { Accepted = result.Accepted, ConfirmedAtUtc = result.At, Detail = result.Detail } : t).ToArray();
            bool complete = receipt.State == CommandState.Accepted && targets.All(t => t.Accepted == true && t.ConfirmedAtUtc is { } at && at <= receipt.ExpiresAtUtc);
            bool expired = DateTimeOffset.UtcNow >= receipt.ExpiresAtUtc;
            var state = complete ? CommandState.Applied : expired || receipt.State == CommandState.NotVerifiable ? CommandState.NotVerifiable : CommandState.Accepted;
            if (receipt.State == state && receipt.Targets.SequenceEqual(targets)) continue;
            var next = receipt with
            {
                State = state, Targets = targets, UpdatedAtUtc = DateTimeOffset.UtcNow,
                Confirmation = complete ? CommandConfirmation.Controller : CommandConfirmation.None,
                Message = complete ? "Os controladores confirmaram os comandos; o resultado físico depende da telemetria disponível." :
                    state == CommandState.NotVerifiable ? targets.Any(t => t.Accepted == true && t.ConfirmedAtUtc > receipt.ExpiresAtUtc)
                        ? "Configuração aceita; um ou mais controladores confirmaram após o prazo. O comando continua sem confirmação completa dentro do prazo."
                        : "Configuração aceita, mas faltou confirmação de um ou mais controladores dentro do prazo."
                    : "Aguardando confirmação dos controladores."
            };
            _commandReceipts[id] = (entry.Fingerprint, next);
            PublishCommand(next);
        }
    }

    private void PublishCommand(CommandReceipt receipt)
    {
        lock (_commandGate)
            if (_commandReplay.TryGetValue(receipt.Id, out var replay)) _commandReplay[receipt.Id] = (replay.Fingerprint, receipt);
        var handlers = CommandUpdated;
        if (handlers == null) return;
        foreach (Action<CommandReceipt> handler in handlers.GetInvocationList())
        {
            try { handler(receipt); }
            catch (Exception ex) { try { Log.Warn($"CommandUpdated: {ex.Message}"); } catch { } }
        }
    }
}
