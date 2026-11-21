using System.Diagnostics;

namespace LinkFlow.Core;

public sealed class AutomationSessionContext
{
    public string? ForegroundProcess { get; set; }
    public List<string> RunningProcesses { get; set; } = [];
    public DateTimeOffset ObservedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public int SessionId { get; set; }

    public AutomationSessionContext Normalize(DateTimeOffset now)
    {
        if (ObservedAtUtc > now.AddSeconds(10) || ObservedAtUtc < now.AddMinutes(-2))
            throw new ArgumentException("O contexto da sessão está vencido.");
        if (RunningProcesses is null || RunningProcesses.Count > 256) throw new ArgumentException("Processos demais no contexto da sessão.");
        static string? ProcessName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = value.Trim();
            if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) value = value[..^4];
            if (value.Length > 128 || value.Any(c => char.IsControl(c) || c is '/' or '\\' or ':'))
                throw new ArgumentException("Nome de processo inválido.");
            return value;
        }
        return new AutomationSessionContext
        {
            ForegroundProcess = ProcessName(ForegroundProcess),
            RunningProcesses = RunningProcesses.Select(ProcessName).Where(p => p != null).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ObservedAtUtc = ObservedAtUtc,
            SessionId = Math.Max(0, SessionId)
        };
    }
}

public sealed record AutomationState
{
    public string? ActiveProfile { get; init; }
    public string? Reason { get; init; }
    public bool Suppressed { get; init; }
    public string BaseProfile { get; init; } = FanPresets.Balanced;
    public DateTimeOffset? ManualOverrideUntilUtc { get; init; }
}

public sealed partial class AppRule
{
    /// <summary>Maior prioridade vence; empates preservam a ordem explícita da lista.</summary>
    public int Priority { get; set; } = 100;
}

/// <summary>Decisões de automação pertencem ao engine; a sessão só informa quais aplicativos estão abertos.</summary>
public sealed partial class LinkFlowEngine
{
    private readonly object _automationLock = new();
    private readonly object _automationOrigin = new();
    private readonly Dictionary<object, AutomationSessionContext> _sessionContexts = new();
    private Timer? _automationTimer;
    private string? _automationApplied, _automationReason, _automationSuppressed, _activeRule;
    private int? _automationRuleSession;
    private string _automationBase = FanPresets.Balanced;
    private DateTimeOffset? _automationOverrideUntil;
    private AutomationState _automationState = new();
    private JsonFileStore<AutomationState>? _automationStore;

    private void StartAutomation()
    {
        var cfg = GetConfig();
        _automationBase = cfg.ActivePreset;
        if (_opt.ConfigPath is { } configPath)
        {
            _automationStore = new(Path.Combine(Path.GetDirectoryName(configPath)!, "automation-state.json"));
            var state = _automationStore.Load();
            if (ProfileExists(cfg, state.BaseProfile)) _automationBase = state.BaseProfile;
            _automationOverrideUntil = state.ManualOverrideUntilUtc;
            _automationApplied = state.ActiveProfile == cfg.ActivePreset ? state.ActiveProfile : null;
            _automationReason = state.Reason;
        }
        EvaluateAutomationNow();
        _automationTimer = new Timer(_ =>
        {
            try { EvaluateAutomationNow(); }
            catch (Exception ex) { Log.Warn($"Automação: {ex.Message}"); }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void SetSessionContext(object owner, AutomationSessionContext context)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(context);
        var normalized = context.Normalize(DateTimeOffset.UtcNow);
        lock (_automationLock)
        {
            if (!_sessionContexts.ContainsKey(owner) && _sessionContexts.Count >= 32) throw new InvalidOperationException("Sessões demais na automação.");
            _sessionContexts[owner] = normalized;
        }
        EvaluateAutomationNow();
    }

    public void RemoveSessionContext(object owner)
    {
        lock (_automationLock) _sessionContexts.Remove(owner);
        EvaluateAutomationNow();
    }

    public void SetAutomationOverride(DateTimeOffset? until)
    {
        if (until is { } value && (value < DateTimeOffset.UtcNow || value > DateTimeOffset.UtcNow.AddDays(1)))
            throw new ArgumentOutOfRangeException(nameof(until), "A intervenção manual pode durar até 24 horas.");
        lock (_automationLock) _automationOverrideUntil = until;
        EvaluateAutomationNow();
    }

    public AutomationState GetAutomationState() { lock (_automationLock) return _automationState with { }; }

    public void EvaluateAutomationNow() => EvaluateAutomation(DateTimeOffset.Now);

    internal void EvaluateAutomation(DateTimeOffset now)
    {
        if (_cts.IsCancellationRequested) return;
        lock (_automationLock)
        {
            var cfg = GetConfig();
            string current = cfg.ActivePreset;
            if (_automationApplied != null && current != _automationApplied)
            {
                _automationSuppressed = _automationReason;
                _automationApplied = null;
                _automationBase = current;
            }
            if (_automationApplied == null) _automationBase = current;
            foreach (var expired in _sessionContexts.Where(kv => now - kv.Value.ObservedAtUtc > TimeSpan.FromSeconds(45)).Select(kv => kv.Key).ToArray())
                _sessionContexts.Remove(expired);
            var contexts = _sessionContexts.Values.OrderByDescending(c => c.ObservedAtUtc).ToArray();
            var candidates = cfg.Automation.Rules.Where(r => r.Enabled).OrderByDescending(r => r.Priority).ToArray();
            AppRule? rule = candidates.FirstOrDefault(r => contexts.Any(c => string.Equals(c.ForegroundProcess, r.Process, StringComparison.OrdinalIgnoreCase)));
            if (rule != null)
            {
                _activeRule = rule.Process;
                _automationRuleSession = contexts.First(c => string.Equals(c.ForegroundProcess, rule.Process, StringComparison.OrdinalIgnoreCase)).SessionId;
            }
            else if (_activeRule != null)
            {
                rule = candidates.FirstOrDefault(r => string.Equals(r.Process, _activeRule, StringComparison.OrdinalIgnoreCase) &&
                    contexts.Any(c => c.RunningProcesses.Contains(r.Process, StringComparer.OrdinalIgnoreCase)));
                if (rule == null) _activeRule = null;
            }
            // Sem interface, regras que já começaram continuam enquanto o processo existir na máquina.
            if (rule == null && _automationReason?.StartsWith("app:", StringComparison.Ordinal) == true && !IsSimulated)
            {
                var processName = _automationReason[4..];
                rule = candidates.FirstOrDefault(r => string.Equals(r.Process, processName, StringComparison.OrdinalIgnoreCase) && IsProcessRunning(processName, _automationRuleSession));
            }
            string? desired = rule?.Profile;
            string? reason = rule == null ? null : $"app:{rule.Process}";
            if (desired == null && cfg.Automation.Quiet.Enabled && cfg.Automation.Quiet.Contains(now.LocalDateTime))
            {
                desired = cfg.Automation.Quiet.Profile;
                reason = "quiet";
            }
            if (reason != _automationReason && reason != _automationSuppressed) _automationSuppressed = null;
            _automationReason = reason;
            if (reason?.StartsWith("app:", StringComparison.Ordinal) != true) _automationRuleSession = null;
            if (_automationOverrideUntil <= now) _automationOverrideUntil = null;
            bool suppressed = reason != null && (reason == _automationSuppressed || _automationOverrideUntil > now);
            if (desired != null && !ProfileExists(cfg, desired)) desired = null;
            if (desired != null && !suppressed)
            {
                if (current != desired) ApplyEngineProfile(desired);
                _automationApplied = desired;
            }
            else if (_automationApplied != null)
            {
                _automationApplied = null;
                if (ProfileExists(cfg, _automationBase) && current != _automationBase) ApplyEngineProfile(_automationBase);
            }
            var next = new AutomationState { ActiveProfile = _automationApplied, Reason = reason, Suppressed = suppressed,
                BaseProfile = _automationBase, ManualOverrideUntilUtc = _automationOverrideUntil };
            if (next == _automationState) return;
            _automationState = next;
            _automationStore?.Save(next);
        }
    }

    private static bool IsProcessRunning(string name, int? session)
    {
        try
        {
            var processes = Process.GetProcessesByName(name);
            bool running = processes.Any(process => { try { return session == null || process.SessionId == session; } catch { return false; } });
            foreach (var process in processes) process.Dispose();
            return running;
        }
        catch { return false; }
    }

    private static bool ProfileExists(ControlConfig cfg, string name) => FanPresets.Find(name) != null || cfg.Profiles.Any(p => p.Name == name);

    private void ApplyEngineProfile(string profile) => UpdateConfig(cfg =>
    {
        if (FanPresets.Find(profile) != null) FanPresets.Apply(cfg, profile);
        else cfg.Profiles.FirstOrDefault(p => p.Name == profile)?.ApplyTo(cfg);
    }, _automationOrigin);
}
