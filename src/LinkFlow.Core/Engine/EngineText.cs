using System.Globalization;
using System.Resources;

namespace LinkFlow.Core;

/// <summary>
/// Texto que o engine manda para a interface: um código e os parâmetros. O serviço roda como sistema e não sabe o idioma
/// de quem usa a interface, por isso não manda frases prontas: a interface traduz o código no idioma dela. Os textos de
/// cada código ficam em Resources\EngineStrings.resx (português) e EngineStrings.&lt;idioma&gt;.resx, no Core, porque o
/// próprio serviço também escreve no idioma do usuário (LCD da bomba). O português vai junto nos campos de texto antigos
/// (Title, Message...), como reserva, e é o que aparece no log.
/// </summary>
public sealed class EngineText
{
    public string Code { get; set; } = string.Empty;
    public List<EngineTextArg> Args { get; set; } = [];

    /// <summary>Monta o texto. Parâmetros: string (literal, ex.: nome do dispositivo), número ou outro <see cref="EngineText"/>.</summary>
    public static EngineText Of(string code, params object[] args)
    {
        if (EngineTexts.Template(code, EngineTexts.PortugueseCulture) == null) throw new ArgumentException($"Código de texto desconhecido: {code}", nameof(code));
        return new EngineText { Code = code, Args = args.Select(EngineTextArg.From).ToList() };
    }

    /// <summary>O texto em português (log e reserva para quem não conhece o código).</summary>
    public string ToPortuguese() => EngineTexts.Format(this, EngineTexts.PortugueseCulture);

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

/// <summary>Códigos dos textos do engine e a formatação deles em um idioma (textos em Resources\EngineStrings.resx).</summary>
public static class EngineTexts
{
    public static readonly CultureInfo PortugueseCulture = CultureInfo.GetCultureInfo("pt-BR");
    public static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en");

    /// <summary>
    /// Idioma dos textos que o Core entrega prontos para a interface (nomes dos perfis). A interface define o dela em
    /// UiLanguage.Apply; no serviço fica o português.
    /// </summary>
    public static CultureInfo DisplayCulture { get; set; } = PortugueseCulture;

    public static ResourceManager Resources { get; } = new("LinkFlow.Core.Resources.EngineStrings", typeof(EngineTexts).Assembly);

    /// <summary>Cultura de um idioma salvo na configuração ("en" = inglês; qualquer outro valor, português).</summary>
    public static CultureInfo CultureFor(string? language) =>
        language != null && language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? EnglishCulture : PortugueseCulture;

    /// <summary>Modelo do código no idioma (sem tradução, o português); null para código desconhecido.</summary>
    public static string? Template(string code, CultureInfo culture) => Resources.GetString(code, culture);

    /// <summary>Texto fixo (sem parâmetros) no idioma, ex.: rótulos do LCD.</summary>
    public static string Get(string code, CultureInfo culture) => Template(code, culture) ?? code;

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
    public const string TelemetryLostTitle = nameof(TelemetryLostTitle);
    public const string TelemetrySourceLost = nameof(TelemetrySourceLost);
    public const string CurveFallbackTitle = nameof(CurveFallbackTitle);
    public const string CurveFallback = nameof(CurveFallback);
    public const string TempCriticalTitle = nameof(TempCriticalTitle);
    public const string TempCritical = nameof(TempCritical);
    public const string TempWarnTitle = nameof(TempWarnTitle);
    public const string TempWarn = nameof(TempWarn);
    public const string TempWarnRecoveringTitle = nameof(TempWarnRecoveringTitle);
    public const string TempWarnRecovering = nameof(TempWarnRecovering);
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
    public const string RgbRecovering = nameof(RgbRecovering);
    public const string RgbWithRecovering = nameof(RgbWithRecovering);
    public const string RgbListPair = nameof(RgbListPair);
    public const string RgbDevHub = nameof(RgbDevHub);
    public const string RgbDevGpu = nameof(RgbDevGpu);
    public const string RgbDevMotherboard = nameof(RgbDevMotherboard);
    public const string RgbDevRam = nameof(RgbDevRam);
    public const string RgbDevPeripherals = nameof(RgbDevPeripherals);
    public const string RgbDevLcd = nameof(RgbDevLcd);

    // ---------------------------------------------------------------- LCD (desenhado pelo serviço)
    public const string LcdOperational = nameof(LcdOperational);
    public const string LcdAlert = nameof(LcdAlert);
    public const string LcdSafeMode = nameof(LcdSafeMode);
    public const string LcdCpuLoad = nameof(LcdCpuLoad);
    public const string LcdCpuClock = nameof(LcdCpuClock);
    public const string LcdGpuLoad = nameof(LcdGpuLoad);
    public const string LcdCoolant = nameof(LcdCoolant);
    public const string LcdPumpRpm = nameof(LcdPumpRpm);
    public const string LcdFansRpm = nameof(LcdFansRpm);
    public const string LcdFans = nameof(LcdFans);
    public const string LcdDisk = nameof(LcdDisk);
    public const string LcdDiskNone = nameof(LcdDiskNone);
    public const string LcdCore = nameof(LcdCore);
    public const string LcdDateFormat = nameof(LcdDateFormat);
    public const string LcdFileMissing = nameof(LcdFileMissing);
    public const string LcdShowingMetrics = nameof(LcdShowingMetrics);
    public const string LcdShowingBackground = nameof(LcdShowingBackground);
    public const string LcdImage = nameof(LcdImage);
    public const string LcdImageMissing = nameof(LcdImageMissing);
    public const string LcdGif = nameof(LcdGif);

    // ---------------------------------------------------------------- diagnósticos de hardware (tela Iluminação)
    public const string GpuNeedsX64 = nameof(GpuNeedsX64);
    public const string GpuNvapiInit = nameof(GpuNvapiInit);
    public const string GpuNvapiEnum = nameof(GpuNvapiEnum);
    public const string GpuPci = nameof(GpuPci);
    public const string GpuNotFound = nameof(GpuNotFound);
    public const string GpuNvapiMissing = nameof(GpuNvapiMissing);
    public const string GpuNvapiAccess = nameof(GpuNvapiAccess);
    public const string GpuRgbWorkerFailed = nameof(GpuRgbWorkerFailed);
    public const string GpuNoSignature = nameof(GpuNoSignature);
    public const string GpuIdUnreadable = nameof(GpuIdUnreadable);
    public const string GpuFirmwareUnknown = nameof(GpuFirmwareUnknown);
    public const string GpuLedCount = nameof(GpuLedCount);
    public const string GpuDirectModeLost = nameof(GpuDirectModeLost);
    public const string GpuColorMismatch = nameof(GpuColorMismatch);
    public const string GpuI2cMissing = nameof(GpuI2cMissing);
    public const string GpuAuraFailed = nameof(GpuAuraFailed);
    public const string GHubNeedsSession = nameof(GHubNeedsSession);
    public const string GHubElevated = nameof(GHubElevated);
    public const string GHubArch = nameof(GHubArch);
    public const string GHubSdkMissing = nameof(GHubSdkMissing);
    public const string GHubAgentNoSdk = nameof(GHubAgentNoSdk);
    public const string GHubOpenHub = nameof(GHubOpenHub);
    public const string GHubNotStarted = nameof(GHubNotStarted);
    public const string GHubSelectRefused = nameof(GHubSelectRefused);
    public const string GHubIncompatible = nameof(GHubIncompatible);
    public const string GHubDllLoad = nameof(GHubDllLoad);
    public const string GHubEntryPoint = nameof(GHubEntryPoint);
    public const string GHubInitError = nameof(GHubInitError);
    public const string GHubAgentLeft = nameof(GHubAgentLeft);
    public const string GHubColorRefused = nameof(GHubColorRefused);
    public const string GHubSendError = nameof(GHubSendError);

    // ---------------------------------------------------------------- perfis prontos (mostrados pela interface)
    public const string PresetSilent = nameof(PresetSilent);
    public const string PresetSilentDesc = nameof(PresetSilentDesc);
    public const string PresetBalanced = nameof(PresetBalanced);
    public const string PresetBalancedDesc = nameof(PresetBalancedDesc);
    public const string PresetPerformance = nameof(PresetPerformance);
    public const string PresetPerformanceDesc = nameof(PresetPerformanceDesc);
    public const string PresetMax = nameof(PresetMax);
    public const string PresetMaxDesc = nameof(PresetMaxDesc);
    public const string PresetAuto = nameof(PresetAuto);
    public const string PresetAutoDesc = nameof(PresetAutoDesc);

    // ---------------------------------------------------------------- catálogo de sensores (widget, overlay)
    public const string SensorGroupCpu = nameof(SensorGroupCpu);
    public const string SensorGroupGpu = nameof(SensorGroupGpu);
    public const string SensorGroupCooling = nameof(SensorGroupCooling);
    public const string SensorGroupMemory = nameof(SensorGroupMemory);
    public const string SensorGroupNetwork = nameof(SensorGroupNetwork);
    public const string SensorGroupStorage = nameof(SensorGroupStorage);
    public const string SensorGroupSystem = nameof(SensorGroupSystem);
    public const string SensorCpuTemp = nameof(SensorCpuTemp);
    public const string SensorCpuLoad = nameof(SensorCpuLoad);
    public const string SensorCpuCoreMax = nameof(SensorCpuCoreMax);
    public const string SensorShortCore = nameof(SensorShortCore);
    public const string SensorCpuClock = nameof(SensorCpuClock);
    public const string SensorCpuPower = nameof(SensorCpuPower);
    public const string SensorGpuTemp = nameof(SensorGpuTemp);
    public const string SensorGpuHotspot = nameof(SensorGpuHotspot);
    public const string SensorGpuLoad = nameof(SensorGpuLoad);
    public const string SensorGpuVram = nameof(SensorGpuVram);
    public const string SensorGpuClock = nameof(SensorGpuClock);
    public const string SensorGpuPower = nameof(SensorGpuPower);
    public const string SensorGpuFan = nameof(SensorGpuFan);
    public const string SensorShortGpuFan = nameof(SensorShortGpuFan);
    public const string SensorCoolantTemp = nameof(SensorCoolantTemp);
    public const string SensorShortCoolant = nameof(SensorShortCoolant);
    public const string SensorPump = nameof(SensorPump);
    public const string SensorFansAvg = nameof(SensorFansAvg);
    public const string SensorRam = nameof(SensorRam);
    public const string SensorShortNetDown = nameof(SensorShortNetDown);
    public const string SensorShortNetUp = nameof(SensorShortNetUp);
    public const string SensorNetSession = nameof(SensorNetSession);
    public const string SensorShortTraffic = nameof(SensorShortTraffic);
    public const string SensorTime = nameof(SensorTime);
    public const string SensorUptime = nameof(SensorUptime);
    public const string SensorShortUptime = nameof(SensorShortUptime);
    public const string SensorVolume = nameof(SensorVolume);
    public const string SensorVolumeUsed = nameof(SensorVolumeUsed);
    public const string SensorVolumeFree = nameof(SensorVolumeFree);
    public const string SensorDiskActivity = nameof(SensorDiskActivity);
    public const string SensorDiskShort = nameof(SensorDiskShort);
    public const string SensorDiskRw = nameof(SensorDiskRw);
    public const string SensorDiskRwValue = nameof(SensorDiskRwValue);
    public const string SensorDiskTemp = nameof(SensorDiskTemp);

    // ---------------------------------------------------------------- status do hub
    public const string HubSearching = nameof(HubSearching);
    public const string HubSearchingDevices = nameof(HubSearchingDevices);
    public const string HubConnected = nameof(HubConnected);
    public const string HubReconnecting = nameof(HubReconnecting);
    public const string HubNotFound = nameof(HubNotFound);
    public const string HubNotFoundConflict = nameof(HubNotFoundConflict);


    /// <summary>
    /// Formata o texto no idioma de <paramref name="culture"/> (números também). Textos aninhados usam o mesmo idioma.
    /// Código desconhecido (ex.: serviço de versão mais nova) ou tradução que não formata devolve null.
    /// </summary>
    public static string? TryFormat(EngineText text, CultureInfo culture)
    {
        string? format = Template(text.Code, culture);
        if (format == null) return null;
        var args = text.Args.Select(a => a.T is { } nested ? TryFormat(nested, culture) ?? string.Empty
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

    public static string Format(EngineText text, CultureInfo culture) => TryFormat(text, culture) ?? text.Code;
}
