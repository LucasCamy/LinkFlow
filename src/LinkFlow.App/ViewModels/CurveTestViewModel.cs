using System.Globalization;
using LinkFlow.Core;
using LinkFlow.App.Services;

namespace LinkFlow.App.ViewModels;

/// <summary>
/// "Testar curva" do editor: uma temperatura simulada que as ventoinhas seguem na hora, para ouvir o ruído de
/// cada ponto sem esquentar o PC. O engine encerra o teste sozinho se ele não for renovado
/// (<see cref="Renew"/> a cada ~2 s), então fechar ou travar a interface devolve o controle às leituras reais.
/// </summary>
public sealed class CurveTestViewModel : ObservableObject
{
    public const double MinTemp = 20, MaxTemp = 100;
    private static CultureInfo Ui => UiLanguage.Culture;

    private readonly Action<float?> _send;
    private readonly Func<IReadOnlyList<CurvePoint>> _points;
    private readonly Func<double?> _currentTemp;
    private bool _isActive;
    private double _temperature = 60;

    /// <param name="send">Envia a temperatura ao engine (null encerra o teste).</param>
    /// <param name="points">Pontos da curva exibida no editor.</param>
    /// <param name="currentTemp">Temperatura real da curva agora: o teste começa dela (o slider não salta).</param>
    public CurveTestViewModel(Action<float?> send, Func<IReadOnlyList<CurvePoint>> points, Func<double?>? currentTemp = null)
    {
        _send = send;
        _points = points;
        _currentTemp = currentTemp ?? (() => null);
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (value == _isActive) return;
            if (value && _currentTemp() is { } t && double.IsFinite(t))
            {
                _temperature = Math.Round(Math.Clamp(t, MinTemp, MaxTemp));
                OnPropertiesChanged(nameof(Temperature), nameof(PreviewPercent));
            }
            _isActive = value;
            OnPropertyChanged();
            _send(value ? (float)_temperature : null);
            OnPropertiesChanged(nameof(Summary));
        }
    }

    /// <summary>Temperatura simulada (°C), de <see cref="MinTemp"/> a <see cref="MaxTemp"/>.</summary>
    public double Temperature
    {
        get => _temperature;
        set
        {
            value = Math.Round(Math.Clamp(value, MinTemp, MaxTemp));
            if (!SetProperty(ref _temperature, value)) return;
            if (_isActive) _send((float)value);
            OnPropertiesChanged(nameof(PreviewPercent), nameof(Summary));
        }
    }

    /// <summary>Velocidade que a curva desenhada dá para <see cref="Temperature"/>.</summary>
    public byte PreviewPercent => new FanCurveConfig { Points = _points().ToList() }.Evaluate((float)_temperature);

    public string Summary => $"{_temperature.ToString("0", Ui)} °C → {PreviewPercent}%";

    /// <summary>Encerra o teste (saiu da tela, mudou de modo, escondeu a janela).</summary>
    public void Stop() => IsActive = false;

    /// <summary>Mantém o teste vivo no engine. Chamado por um timer enquanto <see cref="IsActive"/>.</summary>
    public void Renew()
    {
        if (_isActive) _send((float)_temperature);
    }

    /// <summary>A curva foi editada: atualiza a prévia.</summary>
    public void CurveChanged() => OnPropertiesChanged(nameof(PreviewPercent), nameof(Summary));
}
