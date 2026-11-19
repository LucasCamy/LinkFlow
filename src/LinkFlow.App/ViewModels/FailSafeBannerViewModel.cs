using System.Globalization;
using LinkFlow.Core;
using LinkFlow.App.Services;
using LinkFlow.App.Resources;

namespace LinkFlow.App.ViewModels;

/// <summary>
/// Faixa vermelha no topo da janela enquanto o modo de segurança (ou o watchdog) estiver no controle:
/// o motivo do disparo e o que está acontecendo com as ventoinhas agora.
/// </summary>
public sealed class FailSafeBannerViewModel : ObservableObject
{
    private static CultureInfo Ui => UiLanguage.Culture;
    private bool _isVisible;
    private string _title = string.Empty, _reason = string.Empty, _status = string.Empty;
    private bool _isRecovering;

    public bool IsVisible { get => _isVisible; private set => SetProperty(ref _isVisible, value); }
    public string Title { get => _title; private set => SetProperty(ref _title, value); }

    /// <summary>O que disparou a proteção (ex.: "CPU a 98 °C (limite 95 °C).").</summary>
    public string Reason { get => _reason; private set => SetProperty(ref _reason, value); }

    /// <summary>O estado atual (ex.: "Ventoinhas e bomba forçadas a 100%").</summary>
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>Leituras normalizadas, aguardando estabilizar (a faixa fica âmbar em vez de vermelha).</summary>
    public bool IsRecovering { get => _isRecovering; private set => SetProperty(ref _isRecovering, value); }

    public void Update(EngineSnapshot s) => Update(s, DateTime.Now);

    public void Update(EngineSnapshot s, DateTime now)
    {
        var stalled = s.Alerts.FirstOrDefault(a => a.Key == "engine-stalled");
        if (stalled != null)
        {
            Set(Strings.Banner_Stalled, stalled.Message, Strings.Banner_StalledStatus, recovering: false);
            return;
        }
        if (!s.FailSafeActive)
        {
            IsVisible = false;
            return;
        }

        var cause = s.Alerts
            .Where(a => a.Severity == AlertSeverity.Critical && IsCause(a.Key))
            .OrderBy(a => a.Since)
            .FirstOrDefault();
        string reason = cause != null ? Describe(cause, now) : s.FailSafeReason ?? Strings.Banner_CriticalReading;
        string status = s.FailSafeRecovering
            ? Strings.Banner_Recovering
            : Strings.Banner_Forced;
        Set(Strings.Banner_Active, reason, status, s.FailSafeRecovering);
    }

    private void Set(string title, string reason, string status, bool recovering)
    {
        Title = title;
        Reason = reason;
        Status = status;
        IsRecovering = recovering;
        IsVisible = true;
    }

    private static bool IsCause(string key) =>
        key is "cpu-temp" or "gpu-temp" or "coolant-temp" or "sensor-lost" || key.StartsWith("pump:", StringComparison.Ordinal);

    private static string Describe(EngineAlert alert, DateTime now)
    {
        if (!alert.Key.StartsWith("pump:", StringComparison.Ordinal)) return alert.Message;
        // Bomba: quanto tempo está assim é o que importa ("Bomba com rotação baixa há 12 s · Pump: 0 RPM ...")
        var elapsed = now - alert.Since;
        return elapsed.TotalSeconds < 1 ? string.Format(Strings.Banner_PumpNow, alert.Title, alert.Message) :
            elapsed.TotalMinutes < 1
                ? string.Format(Strings.Banner_PumpForSeconds, alert.Title, elapsed.TotalSeconds.ToString("0", Ui), alert.Message)
                : string.Format(Strings.Banner_PumpForMinutes, alert.Title, Math.Floor(elapsed.TotalMinutes).ToString("0", Ui), alert.Message);
    }
}
