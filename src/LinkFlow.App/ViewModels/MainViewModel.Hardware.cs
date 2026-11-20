using LinkFlow.App.Services;

namespace LinkFlow.App.ViewModels;

/// <summary>Modal de detalhes da tela Hardware (ficha completa do componente e telemetria ao vivo).</summary>
public partial class MainViewModel
{
    private HardwareDetailViewModel? _hardwareDetail;

    public HardwareDetailViewModel? HardwareDetail => _hardwareDetail;

    public void OpenHardwareDetail(HardwareComponent component)
    {
        _hardwareDetail?.Detach();
        _hardwareDetail = new HardwareDetailViewModel(this, component);
        OnPropertyChanged(nameof(HardwareDetail));
        UpdateSensorDemand();   // armazenamento: liga a leitura dos discos enquanto o modal estiver aberto
    }

    public bool CloseHardwareDetail()
    {
        if (_hardwareDetail == null) return false;
        _hardwareDetail.Detach();
        _hardwareDetail = null;
        OnPropertyChanged(nameof(HardwareDetail));
        UpdateSensorDemand();
        return true;
    }

    /// <summary>Últimos <paramref name="seconds"/> s de uma série do histórico do Painel (o mesmo dos Detalhes).</summary>
    internal double[]? HistoryFor(string key, int seconds)
    {
        if (!_history.TryGetValue(key, out var queue) || queue.Count == 0) return null;
        var all = queue.ToArray();
        return all.Length > seconds ? all[^seconds..] : all;
    }
}
