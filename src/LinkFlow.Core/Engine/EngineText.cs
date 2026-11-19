using System.Globalization;

namespace LinkFlow.Core;

/// <summary>
/// Texto que o engine manda para a interface: um código e os parâmetros. O serviço roda como sistema e não sabe o idioma
/// de quem usa a interface, por isso não manda frases prontas: a interface traduz o código (Strings.Engine_&lt;Code&gt;).
/// O português fica em <see cref="EngineTexts"/>, usado no log e nos campos de texto antigos (Title, Message...), que
/// continuam indo como reserva.
/// </summary>
public sealed class EngineText
{
    public string Code { get; set; } = string.Empty;
    public List<EngineTextArg> Args { get; set; } = [];

    /// <summary>Monta o texto. Parâmetros: string (literal, ex.: nome do dispositivo), número ou outro <see cref="EngineText"/>.</summary>
    public static EngineText Of(string code, params object[] args)
    {
        if (!EngineTexts.Portuguese.ContainsKey(code)) throw new ArgumentException($"Código de texto desconhecido: {code}", nameof(code));
        return new EngineText { Code = code, Args = args.Select(EngineTextArg.From).ToList() };
    }

    /// <summary>O texto em português (log e reserva para quem não conhece o código).</summary>
    public string ToPortuguese() => EngineTexts.Format(this, _ => null, EngineTexts.PortugueseCulture);

    public override string ToString() => ToPortuguese();
}

/// <summary>Um parâmetro: texto literal (S), número (N, formatado pela interface no idioma dela) ou outro texto (T).</summary>
public sealed class EngineTextArg
{
    public string? S { get; set; }
    public double? N { get; set; }
    public EngineText? T { get; set; }

    internal static EngineTextArg From(object value) => value switch
    {
        EngineText text => new EngineTextArg { T = text },
        string s => new EngineTextArg { S = s },
        byte or short or int or long or float or double or decimal => new EngineTextArg { N = Convert.ToDouble(value, CultureInfo.InvariantCulture) },
        _ => throw new ArgumentException($"Parâmetro de texto não suportado: {value?.GetType().Name ?? "null"}")
    };
}

/// <summary>Catálogo dos textos do engine, em português. Cada código tem a tradução Engine_&lt;Code&gt; na interface.</summary>
public static class EngineTexts
{
    public static readonly CultureInfo PortugueseCulture = CultureInfo.GetCultureInfo("pt-BR");

    // ---------------------------------------------------------------- alertas
    public const string ConfigRecoveredTitle = nameof(ConfigRecoveredTitle);
    public const string ConfigRecovered = nameof(ConfigRecovered);
    public const string ConfigResetTitle = nameof(ConfigResetTitle);
    public const string ConfigReset = nameof(ConfigReset);
    public const string ConflictTitle = nameof(ConflictTitle);
    public const string Conflict = nameof(Conflict);
    public const string HubLostTitle = nameof(HubLostTitle);
    public const string HubLost = nameof(HubLost);
    public const string PumpLowTitle = nameof(PumpLowTitle);
    public const string PumpLow = nameof(PumpLow);
    public const string FanStoppedTitle = nameof(FanStoppedTitle);
    public const string FanStopped = nameof(FanStopped);
    public const string FanWearTitle = nameof(FanWearTitle);
    public const string FanWear = nameof(FanWear);
    public const string SensorLostTitle = nameof(SensorLostTitle);
    public const string SensorLost = nameof(SensorLost);
    public const string SensorLostNoFailSafe = nameof(SensorLostNoFailSafe);
    public const string TempCriticalTitle = nameof(TempCriticalTitle);
    public const string TempCritical = nameof(TempCritical);
    public const string TempWarnTitle = nameof(TempWarnTitle);
    public const string TempWarn = nameof(TempWarn);
    public const string SensorCpu = nameof(SensorCpu);
    public const string SensorGpu = nameof(SensorGpu);
    public const string SensorCoolant = nameof(SensorCoolant);
    public const string FailSafeOnTitle = nameof(FailSafeOnTitle);
    public const string FailSafeOn = nameof(FailSafeOn);
    public const string FailSafeActiveTitle = nameof(FailSafeActiveTitle);
    public const string FailSafeStabilizing = nameof(FailSafeStabilizing);
    public const string FailSafeWaitingReadings = nameof(FailSafeWaitingReadings);
    public const string EngineStalledTitle = nameof(EngineStalledTitle);
    public const string EngineStalled = nameof(EngineStalled);
    public const string WatchdogLaneEnded = nameof(WatchdogLaneEnded);
    public const string WatchdogLaneStuck = nameof(WatchdogLaneStuck);

    // ---------------------------------------------------------------- calibração
    public const string CalRefusedHub = nameof(CalRefusedHub);
    public const string CalRefusedFailSafe = nameof(CalRefusedFailSafe);
    public const string CalRefusedHot = nameof(CalRefusedHot);
    public const string CalRefusedNoFans = nameof(CalRefusedNoFans);
    public const string CalPreparing = nameof(CalPreparing);
    public const string CalMeasuring = nameof(CalMeasuring);
    public const string CalStalled = nameof(CalStalled);
    public const string CalStartTest = nameof(CalStartTest);
    public const string CalAppClosed = nameof(CalAppClosed);
    public const string CalCancelled = nameof(CalCancelled);
    public const string CalError = nameof(CalError);
    public const string CalNothingChanged = nameof(CalNothingChanged);
    public const string CalDoneWarnings = nameof(CalDoneWarnings);
    public const string CalDoneOk = nameof(CalDoneOk);
    public const string CalNoteNoSpin = nameof(CalNoteNoSpin);
    public const string CalNoteBelowModel = nameof(CalNoteBelowModel);
    public const string CalNoteStart = nameof(CalNoteStart);
    public const string CalNoteMin = nameof(CalNoteMin);
    public const string CalHubNotApplied = nameof(CalHubNotApplied);
    public const string CalHubNoReadings = nameof(CalHubNoReadings);
    public const string CalAbortFailSafe = nameof(CalAbortFailSafe);
    public const string CalAbortHubLost = nameof(CalAbortHubLost);
    public const string CalHotStarting = nameof(CalHotStarting);
    public const string CalHotReached = nameof(CalHotReached);
    public const string CalSubjectCpu = nameof(CalSubjectCpu);
    public const string CalSubjectGpu = nameof(CalSubjectGpu);
    public const string CalSubjectCoolant = nameof(CalSubjectCoolant);

    // ---------------------------------------------------------------- RGB
    public const string RgbDetecting = nameof(RgbDetecting);
    public const string RgbNoneSynced = nameof(RgbNoneSynced);
    public const string RgbSynced = nameof(RgbSynced);
    public const string RgbFailed = nameof(RgbFailed);
    public const string RgbSyncedAndFailed = nameof(RgbSyncedAndFailed);
    public const string RgbListPair = nameof(RgbListPair);
    public const string RgbDevHub = nameof(RgbDevHub);
    public const string RgbDevGpu = nameof(RgbDevGpu);
    public const string RgbDevMotherboard = nameof(RgbDevMotherboard);
    public const string RgbDevRam = nameof(RgbDevRam);
    public const string RgbDevPeripherals = nameof(RgbDevPeripherals);
    public const string RgbDevLcd = nameof(RgbDevLcd);

    // ---------------------------------------------------------------- status do hub
    public const string HubSearching = nameof(HubSearching);
    public const string HubSearchingDevices = nameof(HubSearchingDevices);
    public const string HubConnected = nameof(HubConnected);
    public const string HubReconnecting = nameof(HubReconnecting);
    public const string HubNotFound = nameof(HubNotFound);
    public const string HubNotFoundConflict = nameof(HubNotFoundConflict);

    /// <summary>Português de cada código. Números entram como double: o formato fica no texto ({0:0.#}).</summary>
    public static IReadOnlyDictionary<string, string> Portuguese { get; } = new Dictionary<string, string>
    {
        [ConfigRecoveredTitle] = "Configuração recuperada da cópia",
        [ConfigRecovered] = "O arquivo de configuração estava danificado. O LinkFlow usou a última cópia salva; confira perfis e curvas.",
        [ConfigResetTitle] = "Configuração redefinida",
        [ConfigReset] = "O arquivo de configuração estava danificado e não havia cópia. O LinkFlow voltou ao padrão; o arquivo antigo foi guardado como config.json.corrompido.",
        [ConflictTitle] = "Outro programa controla o hub",
        [Conflict] = "{0} está em execução. Dois programas no iCUE LINK Hub embaralham os comandos (cores trocadas, leituras de 0 RPM e modo de segurança sem motivo). Feche-o ou desative o controle do hub nele.",
        [HubLostTitle] = "Hub desconectado",
        [HubLost] = "O iCUE LINK Hub parou de responder. Tentando reconectar automaticamente.",
        [PumpLowTitle] = "Bomba com rotação baixa",
        [PumpLow] = "{0}: {1} RPM (mínimo {2}). Verifique a bomba!",
        [FanStoppedTitle] = "Ventoinha parada",
        [FanStopped] = "{0} está em 0 RPM com {1}% de potência.",
        [FanWearTitle] = "Ventoinha girando abaixo da calibração",
        [FanWear] = "{0}: {1} RPM a {2}%, esperado cerca de {3} RPM ({4:P0} abaixo). Limpe a poeira; se continuar, o rolamento pode estar gasto.",
        [SensorLostTitle] = "Curva sem leitura de temperatura",
        [SensorLost] = "Nenhuma temperatura de CPU, GPU ou líquido há {0:0} s.",
        [SensorLostNoFailSafe] = "Nenhuma temperatura de CPU, GPU ou líquido há {0:0} s. Com o modo de segurança desligado, as ventoinhas mantêm a última velocidade.",
        [TempCriticalTitle] = "{0} em temperatura crítica",
        [TempCritical] = "{0} a {1:0.#} °C (limite {2:0.#} °C).",
        [TempWarnTitle] = "{0} muito quente",
        [TempWarn] = "{0} a {1:0.#} °C (alerta em {2:0.#} °C).",
        [SensorCpu] = "CPU",
        [SensorGpu] = "GPU",
        [SensorCoolant] = "Líquido",
        [FailSafeOnTitle] = "Modo de segurança ativado",
        [FailSafeOn] = "{0} Ventoinhas em 100%.",
        [FailSafeActiveTitle] = "Modo de segurança ativo",
        [FailSafeStabilizing] = "Leituras abaixo do limite crítico. Aguardando estabilização para retomar o perfil selecionado.",
        [FailSafeWaitingReadings] = "Aguardando leituras válidas dos sensores que acionaram a proteção.",
        [EngineStalledTitle] = "Controle das ventoinhas travado",
        [EngineStalled] = "O LinkFlow detectou que {0}. O hub voltou ao controle próprio (modo hardware).",
        [WatchdogLaneEnded] = "a fila '{0}' encerrou sozinha",
        [WatchdogLaneStuck] = "a fila '{0}' está travada há {1:0} s",
        [HubSearching] = "Procurando iCUE LINK Hub...",
        [HubSearchingDevices] = "Procurando dispositivos...",
        [HubConnected] = "Conectado • FW {0} • {1} dispositivos",
        [HubReconnecting] = "Reconectando ao iCUE LINK Hub...",
        [HubNotFound] = "iCUE LINK Hub não encontrado (verifique o USB ou se o iCUE está aberto)",
        [HubNotFoundConflict] = "iCUE LINK Hub não encontrado: feche {0}",
        [CalRefusedHub] = "O hub precisa estar conectado e informando a rotação.",
        [CalRefusedFailSafe] = "O modo de segurança está ativo. Aguarde as temperaturas normalizarem.",
        [CalRefusedHot] = "{0} A calibração baixa as ventoinhas até 20% e seria interrompida. Tente com o PC ocioso, depois que a temperatura cair.",
        [CalRefusedNoFans] = "Nenhuma ventoinha com tacômetro foi encontrada no hub.",
        [CalPreparing] = "Preparando as ventoinhas...",
        [CalMeasuring] = "Medindo a rotação em {0}%...",
        [CalStalled] = "Algumas ventoinhas pararam. Testando a partida...",
        [CalStartTest] = "Testando a partida em {0}%...",
        [CalAppClosed] = "O LinkFlow foi encerrado durante a calibração.",
        [CalCancelled] = "Calibração cancelada. Nada foi alterado.",
        [CalError] = "Erro na calibração: {0}",
        [CalNothingChanged] = "{0} Nada foi alterado.",
        [CalDoneWarnings] = "Calibração concluída. Confira as ventoinhas marcadas.",
        [CalDoneOk] = "Calibração concluída. Todas as ventoinhas responderam normalmente.",
        [CalNoteNoSpin] = "Não girou em nenhuma potência. Verifique o cabo e se a hélice está livre.",
        [CalNoteBelowModel] = "Máximo {0:P0} abaixo das outras do mesmo modelo. Pode ser poeira ou desgaste.",
        [CalNoteStart] = "Parou em potência baixa e só voltou a girar em {0}%. O LinkFlow vai mantê-la em pelo menos {1}%.",
        [CalNoteMin] = "O LinkFlow vai mantê-la em pelo menos {0}%.",
        [CalHubNotApplied] = "O hub não aplicou as potências da calibração.",
        [CalHubNoReadings] = "O hub parou de informar a rotação durante a calibração.",
        [CalAbortFailSafe] = "O modo de segurança foi acionado.",
        [CalAbortHubLost] = "O hub deixou de responder.",
        [CalHotStarting] = "{0} está a {1:0.#} °C, acima do alerta ({2:0.#} °C).",
        [CalHotReached] = "{0} chegou a {1:0.#} °C.",
        [CalSubjectCpu] = "A CPU",
        [CalSubjectGpu] = "A GPU",
        [CalSubjectCoolant] = "O líquido",
        [RgbDetecting] = "Detectando dispositivos RGB...",
        [RgbNoneSynced] = "Nenhum dispositivo sincronizado",
        [RgbSynced] = "Sincronizado: {0}",
        [RgbFailed] = "Falhou: {0}",
        [RgbSyncedAndFailed] = "Sincronizado: {0} • Falhou: {1}",
        [RgbListPair] = "{0}, {1}",
        [RgbDevHub] = "Fans/Bomba",
        [RgbDevGpu] = "GPU",
        [RgbDevMotherboard] = "Placa-mãe",
        [RgbDevRam] = "RAM DDR5",
        [RgbDevPeripherals] = "Periféricos",
        [RgbDevLcd] = "LCD",
    };

    /// <summary>
    /// Formata o texto com o modelo de <paramref name="template"/> (ou o português, se ele não tiver o código) e a
    /// cultura dada. Textos aninhados usam a mesma tradução. Código desconhecido devolve null.
    /// </summary>
    public static string? TryFormat(EngineText text, Func<string, string?> template, IFormatProvider culture)
    {
        string? format = template(text.Code) ?? Portuguese.GetValueOrDefault(text.Code);
        if (format == null) return null;
        var args = text.Args.Select(a => a.T is { } nested ? TryFormat(nested, template, culture) ?? string.Empty
            : a.N is { } n ? (object)n : a.S ?? string.Empty).ToArray();
        try
        {
            return string.Format(culture, format, args);
        }
        catch (FormatException)
        {
            return null; // tradução com {n} a mais: quem chamou usa a reserva
        }
    }

    public static string Format(EngineText text, Func<string, string?> template, IFormatProvider culture) =>
        TryFormat(text, template, culture) ?? text.Code;
}
