using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using LinkFlow.App.Resources;
using LinkFlow.App.ViewModels;
using LinkFlow.Core;

namespace LinkFlow.App.Services;

/// <summary>Observa a sessão; o engine decide os perfis e horários, inclusive sem interface.</summary>
public sealed class AutomationService : IDisposable
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly WinEventDelegate _callback;
    private IntPtr _hook;
    private string? _foreground;
    private bool _disposed;
    public AutomationService(MainViewModel vm)
    {
        _vm = vm;
        _callback = OnForeground;
        _timer.Tick += (_, _) => Evaluate();
        _vm.AutomationChanged += Reconfigure;
        _vm.SnapshotApplied += RefreshStatus;
        Reconfigure();
    }
    public string Status { get; private set; } = string.Empty;
    public event Action? StatusChanged;
    private void Reconfigure()
    {
        bool rules = _vm.Engine.Config.Automation.Rules.Any(r => r.Enabled);
        if (rules && _hook == IntPtr.Zero)
        {
            _hook = SetWinEventHook(3, 3, IntPtr.Zero, _callback, 0, 0, 0);
            _foreground = ForegroundProcess(GetForegroundWindow());
        }
        else if (!rules && _hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        if (rules) _timer.Start(); else _timer.Stop();
        Evaluate();
    }
    private static string? ForegroundProcess(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch { return null; }
    }
    private void OnForeground(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    { _foreground = ForegroundProcess(hwnd); Evaluate(); }
    private static bool IsRunning(string name)
    {
        try
        {
            var processes = Process.GetProcessesByName(name);
            int session = Process.GetCurrentProcess().SessionId;
            bool running = processes.Any(p => { try { return p.SessionId == session; } catch { return false; } });
            foreach (var process in processes) process.Dispose();
            return running;
        }
        catch { return false; }
    }
    public void Evaluate()
    {
        if (_disposed) return;
        var running = _vm.Engine.Config.Automation.Rules.Where(r => r.Enabled).Select(r => r.Process)
            .Where(IsRunning).Distinct(StringComparer.OrdinalIgnoreCase).Take(255).ToList();
        if (_foreground != null && !running.Contains(_foreground, StringComparer.OrdinalIgnoreCase)) running.Add(_foreground);
        _vm.Engine.SetSessionContext(new AutomationSessionContext
        {
            ForegroundProcess = _foreground, RunningProcesses = running, ObservedAtUtc = DateTimeOffset.UtcNow,
            SessionId = Process.GetCurrentProcess().SessionId
        });
        RefreshStatus();
    }
    private void RefreshStatus()
    {
        var state = _vm.Engine.Automation;
        string next = state.Suppressed ? Strings.Fan_PausadaVoceEscolheuOutro : state.ActiveProfile is not { } profile ? string.Empty :
            string.Format(Strings.Fan_Ativo2, PresetNames.Label(profile), state.Reason == "quiet"
                ? string.Format(Strings.Fan_HorarioSilenciosoAte, _vm.Engine.Config.Automation.Quiet.EndHour)
                : string.Format(Strings.Fan_Aberto, state.Reason?.StartsWith("app:", StringComparison.Ordinal) == true ? state.Reason[4..] : state.Reason));
        if (next == Status) return;
        Status = next;
        StatusChanged?.Invoke();
    }
    public void SimulateForeground(string? process) { _foreground = process; Evaluate(); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _vm.AutomationChanged -= Reconfigure;
        _vm.SnapshotApplied -= RefreshStatus;
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }
    private delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventDelegate callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
