namespace LinkFlow.Core;

/// <summary>
/// Acesso da interface ao engine. Pode ser local (engine dentro do próprio app) ou remoto
/// (serviço do Windows via named pipe). A interface nunca fala com o hardware diretamente.
/// </summary>
public interface IEngineClient : IDisposable
{
    bool IsRemote { get; }
    bool IsConnected { get; }
    bool IsSimulated { get; }
    string ModeDescription { get; }

    ControlConfig Config { get; }
    EngineSnapshot? Snapshot { get; }

    /// <summary>Disparado em thread de fundo (~2x por segundo).</summary>
    event Action<EngineSnapshot>? SnapshotUpdated;

    /// <summary>Configuração alterada por outra origem (outro cliente/serviço). Não ecoa as próprias alterações.</summary>
    event Action<ControlConfig>? ConfigChanged;

    event Action<EngineAlert>? AlertRaised;
    event Action<bool>? ConnectionChanged;

    void Start();
    void UpdateConfig(Action<ControlConfig> mutate);
    void ReplaceConfig(ControlConfig config);
    void RequestLcdFrame();
    void NotifyResume();

    /// <param name="restoreHardwareMode">Somente cliente local: devolve o hub ao modo hardware.</param>
    Task ShutdownAsync(bool restoreHardwareMode = true);
}

public sealed class LocalEngineClient : IEngineClient
{
    private readonly LinkFlowEngine _engine;
    private ControlConfig _config;
    private readonly object _lock = new();

    public LocalEngineClient(LinkFlowEngine engine)
    {
        _engine = engine;
        _config = engine.GetConfig();
        _engine.SnapshotUpdated += s => { Snapshot = s; SnapshotUpdated?.Invoke(s); };
        _engine.AlertRaised += a => AlertRaised?.Invoke(a);
        _engine.ConfigChanged += (c, origin) =>
        {
            lock (_lock) _config = c;
            if (!ReferenceEquals(origin, this)) ConfigChanged?.Invoke(c);
        };
    }

    public bool IsRemote => false;
    public bool IsConnected => true;
    public bool IsSimulated => _engine.IsSimulated;
    public string ModeDescription => _engine.IsSimulated ? "Simulação" : "Local";

    public ControlConfig Config
    {
        get { lock (_lock) return _config; }
    }

    public EngineSnapshot? Snapshot { get; private set; }

    public event Action<EngineSnapshot>? SnapshotUpdated;
    public event Action<ControlConfig>? ConfigChanged;
    public event Action<EngineAlert>? AlertRaised;
#pragma warning disable CS0067 // sempre conectado
    public event Action<bool>? ConnectionChanged;
#pragma warning restore CS0067

    public void Start() => _engine.Start();
    public void UpdateConfig(Action<ControlConfig> mutate) => _engine.UpdateConfig(mutate, this);
    public void ReplaceConfig(ControlConfig config) => _engine.ReplaceConfig(config, this);
    public void RequestLcdFrame() => _engine.RequestLcdFrame();
    public void NotifyResume() => _engine.NotifyResume();
    public Task ShutdownAsync(bool restoreHardwareMode = true) => _engine.StopAsync(restoreHardwareMode);
    public void Dispose() => _engine.Dispose();
}
