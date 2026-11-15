using System.Globalization;
using LinkFlow.Core;

namespace LinkFlow.App.ViewModels;

/// <summary>
/// Faixa vermelha no topo da janela enquanto o modo de segurança (ou o watchdog) estiver no controle:
/// o motivo do disparo e o que está acontecendo com as ventoinhas agora.
/// </summary>
public sealed class FailSafeBannerViewModel : ObservableObject
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
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
            Set("Controle das ventoinhas travado", stalled.Message,
                "O hub voltou ao modo hardware e o serviço será reiniciado.", recovering: false);
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
        string reason = cause != null ? Describe(cause, now) : s.FailSafeReason ?? "Leitura crítica detectada.";
        string status = s.FailSafeRecovering
            ? "Leituras normalizadas. Retomando o perfil selecionado em instantes."
            : "Ventoinhas e bomba forçadas a 100%.";
        Set("Modo de segurança ativo", reason, status, s.FailSafeRecovering);
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
        string since = elapsed.TotalSeconds < 1 ? string.Empty :
            elapsed.TotalMinutes < 1 ? $" há {elapsed.TotalSeconds.ToString("0", PtBr)} s" :
            $" há {Math.Floor(elapsed.TotalMinutes).ToString("0", PtBr)} min";
        return $"{alert.Title}{since} · {alert.Message}";
    }
}
