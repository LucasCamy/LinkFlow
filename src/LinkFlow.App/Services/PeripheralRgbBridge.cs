using LinkFlow.Core;

namespace LinkFlow.App.Services;

/// <summary>
/// Periféricos Logitech G quando o controle está no serviço do Windows.
/// O SDK de iluminação do G HUB só atende programas na sessão do usuário; o serviço roda como SYSTEM
/// (sessão 0), então a interface — que fica na bandeja — aplica a cor. Ao sair, o SDK é encerrado e
/// o G HUB volta a controlar as luzes sozinho (não briga com o app da Logitech).
/// </summary>
public sealed class PeripheralRgbBridge : IDisposable
{
    private readonly IEngineClient _engine;
    private readonly LogitechPeripheralsRgb _device = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _disposed;
    private string _status = "Procurando o G HUB...";

    public PeripheralRgbBridge(IEngineClient engine)
    {
        _engine = engine;
        _engine.AnyConfigChanged += _ => _wake.Set();
        _thread = new Thread(Run) { IsBackground = true, Name = "LinkFlow Logitech", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public string Status => _status;
    public bool IsAvailable => _device.IsAvailable;
    public event Action? StatusChanged;

    private void SetStatus(string s)
    {
        if (_status == s) return;
        _status = s;
        StatusChanged?.Invoke();
    }

    private void Run()
    {
        RgbConfig? applied = null;
        DateTime nextInit = DateTime.MinValue;
        while (!_disposed)
        {
            try
            {
                var rgb = _engine.Config.Rgb;
                if (!rgb.SyncPeripherals)
                {
                    if (applied != null) { applied = null; }
                    SetStatus("Sincronização desligada • o G HUB controla as luzes");
                }
                else
                {
                    if (!_device.IsAvailable && DateTime.Now >= nextInit)
                    {
                        _device.Initialize();
                        // O G HUB pode abrir depois do LinkFlow no logon: tenta de novo a cada 30 s
                        nextInit = DateTime.Now.AddSeconds(30);
                        if (_device.IsAvailable) applied = null;
                    }

                    if (!_device.IsAvailable)
                    {
                        SetStatus("G HUB não encontrado ou sem permissão • abra o G HUB e ative \"Permitir que jogos e aplicativos controlem a iluminação\"");
                    }
                    else if (applied == null || !applied.SameOutputAs(rgb) || rgb.IsAnimated)
                    {
                        // Efeitos: mesma conta e mesmo relógio do serviço, então fica no compasso das ventoinhas
                        var o = rgb.OutputFor(RgbTargets.Peripherals);
                        bool ok = _device.SetColor(o.R, o.G, o.B, o.Brightness);
                        bool changed = applied == null || !applied.SameOutputAs(rgb);
                        applied = rgb;
                        if (changed || !ok)
                            SetStatus(ok ? "Conectado • via G HUB (pela interface)" : "O G HUB recusou a cor • verifique a permissão do SDK no G HUB");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Periféricos Logitech: {ex.Message}");
            }

            var cur = _engine.Config.Rgb;
            if (_device.IsAvailable && cur.SyncPeripherals && cur.IsAnimated)
            {
                // Efeito animado: ~20 quadros/s (a chamada ao SDK é barata)
                _wake.WaitOne(RgbEffects.FastFrame);
                continue;
            }

            // Arrastes de cor geram muitos eventos: aplica no máximo ~10x por segundo
            _wake.WaitOne(_device.IsAvailable ? Timeout.Infinite : 30_000);
            if (!_disposed) Thread.Sleep(100);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _wake.Set();
        _thread.Join(500);
        _device.Dispose(); // devolve o controle das luzes ao G HUB
    }
}
