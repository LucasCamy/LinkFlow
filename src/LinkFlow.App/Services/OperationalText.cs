using System.Globalization;
using LinkFlow.Core;

namespace LinkFlow.App.Services;

/// <summary>Operational-center strings follow the same language selected for the application.</summary>
public static class OperationalText
{
    private static bool English => UiLanguage.Culture.TwoLetterISOLanguageName != "pt";
    public static string Pick(string pt, string en) => English ? en : pt;
    public static string Tab => Pick("Controle e histórico", "Control & history");
    public static string Title => Pick("Saúde operacional", "Operational health");
    public static string Subtitle => Pick("Leituras válidas, resultados dos comandos e histórico contínuo do LinkFlow.", "Reading quality, command outcomes and continuous LinkFlow history.");
    public static string Protection => Pick("Proteção", "Protection");
    public static string ReadingQuality => Pick("Qualidade das leituras", "Reading quality");
    public static string Configuration => Pick("Configuração", "Configuration");
    public static string Storage => Pick("Histórico persistente", "Persistent history");
    public static string Sensors => Pick("Origem e idade das leituras", "Reading source and age");
    public static string Fans => Pick("Controle por dispositivo", "Control per device");
    public static string Commands => Pick("Operações de controle", "Control operations");
    public static string Timeline => Pick("Linha do tempo", "Event timeline");
    public static string History => Pick("Histórico e comparação", "History & comparison");
    public static string Guidance => Pick("Verificações orientadas por evidência", "Evidence-based checks");
    public static string Refresh => Pick("Atualizar", "Refresh");
    public static string ExportCsv => Pick("Exportar CSV", "Export CSV");
    public static string ExportJson => Pick("Exportar JSON", "Export JSON");
    public static string Metric => Pick("Métrica", "Metric");
    public static string Range => Pick("Período", "Range");
    public static string Compare => Pick("Comparar sessões", "Compare sessions");
    public static string Baseline => Pick("Sessão de referência", "Baseline session");
    public static string Candidate => Pick("Sessão comparada", "Comparison session");
    public static string Minimum => Pick("Mínimo", "Minimum");
    public static string Maximum => Pick("Máximo", "Maximum");
    public static string Average => Pick("Média", "Average");
    public static string P95 => "P95";
    public static string Coverage => Pick("Cobertura válida", "Valid coverage");
    public static string AboveWarning => Pick("Tempo acima do alerta", "Time above warning");
    public static string NoReadings => Pick("Sem leituras válidas neste período.", "No valid readings in this range.");
    public static string GapHint => Pick("Lacunas representam ausência de leitura válida. Os horários mantêm sua posição real.", "Gaps mean no valid acquisition. Timestamps keep their actual position.");
    public static string NoCommands => Pick("Nenhum comando registrado nesta sessão.", "No commands recorded in this session.");
    public static string NoEvents => Pick("Nenhum evento neste período.", "No events in this range.");
    public static string NoSensors => Pick("Aguardando a primeira coleta de sensores.", "Waiting for the first sensor acquisition.");
    public static string Source => Pick("Origem", "Source");
    public static string Device => Pick("Dispositivo", "Device");
    public static string AllDevices => Pick("Todos os dispositivos", "All devices");
    public static string ControlRule => Pick("Motivo do controle", "Control reason");
    public static string LastSuccess => Pick("Última coleta válida", "Last valid acquisition");
    public static string Failures => Pick("Falhas seguidas", "Consecutive failures");
    public static string Requested => Pick("Solicitado", "Requested");
    public static string Accepted => Pick("Aceito pelo controlador", "Controller accepted");
    public static string Measured => Pick("Medido", "Measured");
    public static string Supported => Pick("Funções disponíveis", "Available functions");
    public static string Pending => Pick("Aguardando confirmação", "Awaiting confirmation");
    public static string NotVerified => Pick("Sem confirmação física", "No physical confirmation");
    public static string ConfirmationHint => Pick("A confirmação do serviço ou controlador é separada do retorno físico do dispositivo.", "Service or controller acknowledgement is separate from physical device feedback.");
    public static string Inventory => Pick("Inventário acompanhado", "Tracked inventory");
    public static string WindowsOperations => Pick("Drivers e manutenção do Windows", "Drivers & Windows maintenance");
    public static string NoWindowsOperations => Pick("Nenhuma operação externa registrada.", "No external operations recorded.");

    public static string Validity(ReadingValidity state) => state switch
    {
        ReadingValidity.Fresh => Pick("Válida", "Valid"),
        ReadingValidity.Stale => Pick("Vencida", "Stale"),
        ReadingValidity.Unavailable => Pick("Indisponível", "Unavailable"),
        ReadingValidity.Invalid => Pick("Inválida", "Invalid"),
        ReadingValidity.NotSupported => Pick("Sem suporte", "Unsupported"),
        _ => Pick("Desconhecida", "Unknown")
    };

    public static string Persistence(PersistenceState state) => state switch
    {
        PersistenceState.Saved => Pick("Salva", "Saved"),
        PersistenceState.Pending => Pick("Gravação pendente", "Save pending"),
        PersistenceState.Failed => Pick("Falha ao salvar", "Save failed"),
        _ => Pick("Sem gravação necessária", "No save required")
    };

    public static string Command(CommandState state) => state switch
    {
        CommandState.Pending => Pick("Pendente", "Pending"),
        CommandState.Accepted => Pick("Aceito pelo serviço", "Service accepted"),
        CommandState.Applied => Pick("Aplicado", "Applied"),
        CommandState.NotVerifiable => Pick("Enviado, não verificável", "Sent, not verifiable"),
        CommandState.Rejected => Pick("Rejeitado", "Rejected"),
        CommandState.Expired => Pick("Prazo encerrado", "Expired"),
        CommandState.Conflict => Pick("Conflito de revisão", "Revision conflict"),
        CommandState.Failed => Pick("Falhou", "Failed"),
        CommandState.Superseded => Pick("Substituído por alteração mais recente", "Superseded by newer change"),
        _ => Pick("Sem confirmação", "Unconfirmed")
    };

    public static string Confirmation(CommandConfirmation confirmation) => confirmation switch
    {
        CommandConfirmation.Physical => Pick("Retorno físico", "Physical feedback"),
        CommandConfirmation.Controller => Pick("Resposta do controlador", "Controller response"),
        CommandConfirmation.Software => Pick("Confirmação do serviço", "Service confirmation"),
        _ => Pick("Sem confirmação", "Unconfirmed")
    };

    public static string ControlReason(string reason)
    {
        if (reason.StartsWith("group-curve:", StringComparison.Ordinal)) return Pick("Curva do grupo: ", "Group curve: ") + reason[12..];
        return reason switch
        {
            "safety" => Pick("Proteção de segurança", "Safety protection"),
            "calibration" => Pick("Calibração em andamento", "Calibration in progress"),
            "manual" => Pick("Ajuste individual", "Individual setting"),
            "global-curve" => Pick("Curva global", "Global curve"),
            "global-fixed" => Pick("Potência global fixa", "Fixed global duty"),
            "unsupported" => Pick("Módulo sem controle de rotação", "Module without speed control"),
            _ => string.IsNullOrEmpty(reason) ? Pick("Sem motivo informado", "Reason unavailable") : reason
        };
    }

    public static string Action(string kind) => kind switch
    {
        "setConfig" => Pick("Alterar configuração", "Change configuration"),
        "lcd" => Pick("Enviar quadro ao LCD", "Send LCD frame"),
        "resume" => Pick("Retomar dispositivos", "Resume devices"),
        "refreshRgb" => Pick("Revalidar iluminação", "Recheck lighting"),
        "rescan" => Pick("Procurar dispositivos", "Rescan devices"),
        "curveTest" => Pick("Teste de curva", "Curve test"),
        "calibrationStart" => Pick("Calibrar ventoinhas", "Calibrate fans"),
        "calibrationCancel" => Pick("Cancelar calibração", "Cancel calibration"),
        "sessionContext" => Pick("Contexto de aplicativos", "Application context"),
        "automationOverride" => Pick("Intervenção manual", "Manual override"),
        _ => kind
    };

    public static string Event(string kind) => kind switch
    {
        "engine.started" => Pick("Serviço iniciado", "Engine started"),
        "engine.stopped" => Pick("Serviço encerrado", "Engine stopped"),
        "configuration.changed" => Pick("Configuração alterada", "Configuration changed"),
        "alert.opened" => Pick("Alerta iniciado", "Alert opened"),
        "alert.escalated" => Pick("Alerta agravado", "Alert escalated"),
        "alert.changed" => Pick("Alerta atualizado", "Alert updated"),
        "alert.resolved" => Pick("Alerta resolvido", "Alert resolved"),
        "device.discovered" => Pick("Dispositivo detectado", "Device discovered"),
        "device.recovered" => Pick("Dispositivo recuperado", "Device recovered"),
        "device.lost" => Pick("Dispositivo indisponível", "Device unavailable"),
        "rgb.control.detected" => Pick("Controle RGB detectado", "RGB control detected"),
        "rgb.control.unavailable" => Pick("Controle RGB indisponível", "RGB control unavailable"),
        "rgb.control.recovered" => Pick("Controle RGB recuperado", "RGB control recovered"),
        "rgb.adapter.observation" => Pick("Enumeração do backend RGB", "RGB backend enumeration"),
        "rgb.output.accepted" => Pick("Saída RGB aceita pelo controlador", "RGB output accepted by controller"),
        "rgb.output.rejected" => Pick("Saída RGB recusada", "RGB output rejected"),
        "rgb.transport.failed" => Pick("Falha no transporte RGB", "RGB transport failure"),
        "rgb.native.failure" => Pick("Detalhe de falha na chamada RGB", "RGB call failure detail"),
        "rgb.native.history" => Pick("Retenção dos detalhes de falhas RGB", "RGB failure detail retention"),
        "inventory.added" => Pick("Componente adicionado", "Component added"),
        "inventory.removed" => Pick("Componente removido", "Component removed"),
        "inventory.changed" => Pick("Componente alterado", "Component changed"),
        _ => kind.StartsWith("command.", StringComparison.Ordinal) ? Pick("Resultado de comando", "Command outcome") : kind
    };

    public static string RgbEventDetail(OperationalEvent entry)
    {
        string detail = entry.Kind == "rgb.adapter.observation" ? entry.Data.GetValueOrDefault("adapterObservation") switch
        {
            "Present" => Pick("Adaptador enumerado pelo backend RGB.", "Adapter enumerated by RGB backend."),
            "NotDetected" => Pick("Nenhum adaptador compatível foi enumerado pelo backend RGB.", "RGB backend did not enumerate a compatible adapter."),
            "EnumerationUnavailable" => Pick("Enumeração de adaptadores indisponível no backend RGB.", "Adapter enumeration unavailable in RGB backend."),
            _ => Pick("Resultado da enumeração desconhecido.", "Adapter enumeration result unknown.")
        } : Event(entry.Kind) + ".";
        if (entry.Data.TryGetValue("nativeStatus", out var status))
        {
            detail += " SDK " + status;
            if (entry.Data.TryGetValue("nativeOperation", out var operation)) detail += " · " + operation;
            if (entry.Data.TryGetValue("nativeRegister", out var register)) detail += " · " + Pick("registro ", "register ") + register;
            if (entry.Data.TryGetValue("nativeCommand", out var command)) detail += " · " + Pick("comando ", "command ") + command;
            detail += ".";
        }
        return detail + " " + Pick("A presença física e a luz emitida não são verificadas por esse registro.",
            "This record does not verify physical connection or emitted light.");
    }

    public static string Sensor(string key) => key switch
    {
        "cpu.temp" => Pick("Temperatura da CPU", "CPU temperature"),
        "cpu.load" => Pick("Uso da CPU", "CPU usage"),
        "cpu.power" => Pick("Potência da CPU", "CPU power"),
        "cpu.clock" => Pick("Clock da CPU", "CPU clock"),
        "gpu.temp" => Pick("Temperatura da GPU", "GPU temperature"),
        "gpu.hotspot" => Pick("Hotspot da GPU", "GPU hotspot"),
        "gpu.load" => Pick("Uso da GPU", "GPU usage"),
        "gpu.power" => Pick("Potência da GPU", "GPU power"),
        "gpu.clock" => Pick("Clock da GPU", "GPU clock"),
        "gpu.memory_clock" => Pick("Clock da memória de vídeo", "Video memory clock"),
        "gpu.vram_used" => Pick("Memória de vídeo usada", "Video memory used"),
        "gpu.vram_total" => Pick("Memória de vídeo total", "Total video memory"),
        "gpu.fan1" or "gpu.fan" => Pick("Ventoinha da GPU", "GPU fan"),
        "gpu.fan2" => Pick("Segunda ventoinha da GPU", "Second GPU fan"),
        "gpu.fan_percent" => Pick("Potência da ventoinha da GPU", "GPU fan duty"),
        "ram.usage" => Pick("Uso da memória", "Memory usage"),
        "ram.used" => Pick("Memória usada", "Memory used"),
        "ram.total" => Pick("Memória total", "Total memory"),
        "coolant.temp" => Pick("Temperatura do líquido", "Coolant temperature"),
        "hub.rpm" => Pick("Rotações do hub", "Hub rotation readings"),
        "pump.rpm" => Pick("Rotação da bomba", "Pump speed"),
        "fans.rpm" => Pick("Rotação média das ventoinhas", "Average fan speed"),
        "fans.power" => Pick("Potência global", "Global duty"),
        "fan.rpm" => Pick("Rotação por dispositivo", "Speed per device"),
        "fan.power" => Pick("Potência por dispositivo", "Duty per device"),
        "network" or "net.down" => Pick("Rede / download", "Network / download"),
        "net.up" => Pick("Upload", "Upload"),
        "disk.activity" => Pick("Atividade dos discos", "Disk activity"),
        "disk.read" => Pick("Leitura dos discos", "Disk read rate"),
        "disk.write" => Pick("Gravação dos discos", "Disk write rate"),
        "disk.device.temp" => Pick("Temperatura por disco", "Temperature per disk"),
        "disk.device.read" => Pick("Leitura por disco", "Read rate per disk"),
        "disk.device.write" => Pick("Gravação por disco", "Write rate per disk"),
        "volume.used" => Pick("Espaço usado por volume", "Used space per volume"),
        "volume.free" => Pick("Espaço livre por volume", "Free space per volume"),
        "engine.memory.private" => Pick("Memória privada do serviço", "Engine private memory"),
        "engine.memory.working_set" => Pick("Memória residente do serviço", "Engine working set"),
        "engine.cpu" => Pick("CPU do serviço", "Engine CPU"),
        _ => key.StartsWith("fan:", StringComparison.Ordinal) ? Pick("RPM: ", "RPM: ") + key[4..] : key
    };

    public static string Age(double? seconds) => seconds is not { } age ? "—" : age < 60 ? $"{age.ToString("0.0", UiLanguage.Culture)} s" :
        $"{(age / 60).ToString("0.0", UiLanguage.Culture)} min";
    public static string Time(DateTimeOffset? when) => when?.ToLocalTime().ToString("g", UiLanguage.Culture) ?? "—";
    public static string Number(double? value, string unit = "") => value is { } n && double.IsFinite(n)
        ? n.ToString(Math.Abs(n) >= 100 ? "N0" : "0.0", UiLanguage.Culture) + (unit.Length == 0 ? "" : " " + unit) : "—";
}
