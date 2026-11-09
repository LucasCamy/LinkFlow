using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using LinkFlow.App.ViewModels;
using LinkFlow.Core;

namespace LinkFlow.App.Services;

/// <summary>
/// Perfil automático: por jogo/app em primeiro plano e por horário (silencioso à noite).
/// Prioridade: regra de app > horário silencioso > perfil escolhido pelo usuário.
/// Custo: sem nenhuma regra, nada é observado. Com regras, o Windows avisa quando a janela em
/// primeiro plano muda (sem varredura); a checagem periódica é a cada 30 s.
/// Se o usuário trocar o perfil à mão, a automação respeita até o motivo mudar.
/// </summary>
public sealed class AutomationService : IDisposable
{
    private const uint EventSystemForeground = 3, WinEventOutOfContext = 0;

    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly WinEventDelegate _callback;
    private IntPtr _hook;
    private string? _foreground;
    private string? _activeRuleProcess;
    private string? _applied;       // perfil aplicado pela automação
    private string? _reasonKey;     // motivo atual (regra/horário)
    private string? _suppressed;    // motivo ignorado porque o usuário mudou à mão
    private string _base = FanPresets.Balanced;

    public AutomationService(MainViewModel vm)
    {
        _vm = vm;
        _callback = OnForeground;
        _timer.Tick += (_, _) => Evaluate();
        _vm.AutomationChanged += Reconfigure;
        Reconfigure();
    }

    public string Status { get; private set; } = string.Empty;
    public event Action? StatusChanged;

    private void Reconfigure()
    {
        var a = _vm.Engine.Config.Automation;
        bool rules = a.Rules.Any(r => r.Enabled);
        if (rules && _hook == IntPtr.Zero)
            _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _callback, 0, 0, WinEventOutOfContext);
        else if (!rules && _hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
        if (rules || a.Quiet.Enabled) _timer.Start(); else _timer.Stop();
        Evaluate();
    }

    private void OnForeground(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            using var p = Process.GetProcessById((int)pid);
            _foreground = p.ProcessName;
        }
        catch { _foreground = null; }
        Evaluate();
    }

    private static bool IsRunning(string process)
    {
        try
        {
            var list = Process.GetProcessesByName(process);
            foreach (var p in list) p.Dispose();
            return list.Length > 0;
        }
        catch { return false; }
    }

    public void Evaluate()
    {
        var cfg = _vm.Engine.Config;
        var a = cfg.Automation;
        string current = cfg.ActivePreset;

        // Usuário trocou o perfil enquanto a automação estava ativa: respeita
        if (_applied != null && current != _applied)
        {
            _suppressed = _reasonKey;
            _applied = null;
            _base = current;
        }
        if (_applied == null) _base = current;

        // 1) Regra de app: começa quando o programa vem para frente e vale enquanto estiver aberto
        AppRule? rule = a.Rules.FirstOrDefault(r => r.Enabled && string.Equals(r.Process, _foreground, StringComparison.OrdinalIgnoreCase));
        if (rule != null) _activeRuleProcess = rule.Process;
        else if (_activeRuleProcess != null)
        {
            rule = a.Rules.FirstOrDefault(r => r.Enabled && string.Equals(r.Process, _activeRuleProcess, StringComparison.OrdinalIgnoreCase));
            if (rule == null || !IsRunning(_activeRuleProcess))
            {
                _activeRuleProcess = null;
                rule = null;
            }
        }

        string? desired = null, reason = null, label = null;
        if (rule != null) { desired = rule.Profile; reason = $"app:{rule.Process}"; label = $"{rule.Process} aberto"; }
        else if (a.Quiet.Enabled && a.Quiet.Contains(DateTime.Now)) { desired = a.Quiet.Profile; reason = "quiet"; label = $"horário silencioso até {a.Quiet.EndHour:00}h"; }

        if (reason != _reasonKey && reason != _suppressed) _suppressed = null; // motivo novo: volta a valer
        _reasonKey = reason;

        if (desired != null && reason != _suppressed && !ProfileExists(cfg, desired)) desired = null;

        if (desired != null && reason != _suppressed)
        {
            if (current != desired)
            {
                if (_applied == null) _base = current;
                _vm.ApplyAutomationProfile(desired, label!);
            }
            _applied = desired;
            SetStatus($"Ativo: {Label(desired)} ({label})");
        }
        else if (_applied != null)
        {
            // Motivo acabou: volta ao perfil que o usuário tinha
            _applied = null;
            if (ProfileExists(cfg, _base) && current != _base) _vm.ApplyAutomationProfile(_base, "automação terminou");
            SetStatus(string.Empty);
        }
        else
        {
            SetStatus(reason != null && reason == _suppressed ? "Pausada: você escolheu outro perfil" : string.Empty);
        }
    }

    private static bool ProfileExists(ControlConfig c, string key) =>
        FanPresets.Find(key) != null || c.Profiles.Any(p => p.Name == key);

    private static string Label(string key) => FanPresets.Find(key)?.Label ?? key;

    private void SetStatus(string s)
    {
        if (Status == s) return;
        Status = s;
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        _timer.Stop();
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>Teste: simula o programa em primeiro plano.</summary>
    public void SimulateForeground(string? process)
    {
        _foreground = process;
        Evaluate();
    }
}
