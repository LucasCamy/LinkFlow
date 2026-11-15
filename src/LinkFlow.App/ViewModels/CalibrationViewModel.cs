using System.Globalization;
using System.Windows.Input;
using LinkFlow.Core;

namespace LinkFlow.App.ViewModels;

public enum CalibrationStage { Intro, Running, Done }

/// <summary>
/// Assistente "Calibrar ventoinhas": explica o que vai acontecer, inicia a calibração no engine, mostra o progresso e o
/// resultado por ventoinha. O engine é quem mede (no serviço, quando instalado); aqui só se acompanha o snapshot.
/// </summary>
public sealed class CalibrationViewModel : ObservableObject
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private readonly IEngineClient _engine;
    private CalibrationStage _stage;
    private int _baselineRun, _latestRun;
    private double _progress;
    private string _message = string.Empty;
    private CalibrationOutcome _outcome;
    private IReadOnlyList<CalibrationResultRow> _results = [];
    private int _fanCount;
    private string? _lastCalibration;

    public CalibrationViewModel(IEngineClient engine, EngineSnapshot snapshot)
    {
        _engine = engine;
        StartCommand = new RelayCommand(Start, () => CanStart);
        CancelCommand = new RelayCommand(Cancel);   // sempre habilitado: Cancel() ignora fora da calibração
        // Calibração já em andamento (ex.: iniciada por outra janela): acompanha a mesma
        if (snapshot.Calibration is { Running: true } running)
        {
            _baselineRun = running.RunId - 1;
            _stage = CalibrationStage.Running;
        }
        else _baselineRun = snapshot.Calibration?.RunId ?? 0;
        Update(snapshot);
    }

    public ICommand StartCommand { get; }
    public ICommand CancelCommand { get; }

    public CalibrationStage Stage
    {
        get => _stage;
        private set
        {
            if (!SetProperty(ref _stage, value)) return;
            OnPropertiesChanged(nameof(IsIntro), nameof(IsRunning), nameof(IsDone), nameof(CanStart));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsIntro => _stage == CalibrationStage.Intro;
    public bool IsRunning => _stage == CalibrationStage.Running;
    public bool IsDone => _stage == CalibrationStage.Done;

    /// <summary>Ventoinhas com tacômetro no hub (a bomba e dispositivos sem rotação ficam de fora).</summary>
    public int FanCount { get => _fanCount; private set { if (SetProperty(ref _fanCount, value)) OnPropertiesChanged(nameof(FanCountText), nameof(CanStart)); } }
    public string FanCountText => _fanCount switch
    {
        0 => "Nenhuma ventoinha com tacômetro conectada ao hub.",
        1 => "1 ventoinha será calibrada. A bomba não muda.",
        _ => $"{_fanCount} ventoinhas serão calibradas ao mesmo tempo. A bomba não muda."
    };

    /// <summary>"Última calibração: 15/11/2026" (null se nenhuma ventoinha foi calibrada).</summary>
    public string? LastCalibration { get => _lastCalibration; private set => SetProperty(ref _lastCalibration, value); }

    public bool CanStart => _stage != CalibrationStage.Running && _fanCount > 0;

    /// <summary>0 a 100.</summary>
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    public CalibrationOutcome Outcome
    {
        get => _outcome;
        private set { if (SetProperty(ref _outcome, value)) OnPropertyChanged(nameof(Succeeded)); }
    }

    public bool Succeeded => _outcome == CalibrationOutcome.Completed;
    public IReadOnlyList<CalibrationResultRow> Results { get => _results; private set { if (SetProperty(ref _results, value)) OnPropertyChanged(nameof(HasResults)); } }
    public bool HasResults => _results.Count > 0;

    public void Start()
    {
        if (!CanStart) return;
        Progress = 0;
        Message = "Iniciando a calibração...";
        Results = [];
        _baselineRun = _latestRun;   // ignora resultados de calibrações anteriores
        Stage = CalibrationStage.Running;
        _engine.StartCalibration();
    }

    public void Cancel()
    {
        if (!IsRunning) return;
        Message = "Cancelando...";
        _engine.CancelCalibration();
    }

    /// <summary>Atualiza a partir do snapshot do engine (chamado a cada snapshot enquanto o assistente está aberto).</summary>
    public void Update(EngineSnapshot s)
    {
        FanCount = s.Fans.Count(f => !f.IsPump && f.Model != nameof(LinkDeviceModel.Unknown));
        var dates = _engine.Config.Fans.Values.Select(f => f.Calibration?.Date).Where(d => d != null).ToList();
        LastCalibration = dates.Count == 0 ? null : $"Última calibração: {dates.Max()!.Value.ToString("dd/MM/yyyy 'às' HH:mm", PtBr)}";

        if (s.Calibration is { } seen) _latestRun = Math.Max(_latestRun, seen.RunId);
        // Só o resultado do pedido feito aqui (RunId maior que o anterior ao clique)
        if (_stage != CalibrationStage.Running || s.Calibration is not { } c || c.RunId <= _baselineRun) return;
        if (c.Running)
        {
            Progress = Math.Round(c.Progress * 100);
            Message = c.Message;
            return;
        }
        Outcome = c.Outcome;
        Progress = c.Outcome == CalibrationOutcome.Completed ? 100 : Progress;
        Message = c.Message;
        Results = c.Results.Select(x => new CalibrationResultRow(x.Name, Describe(x), x.Note, x.Warning)).ToList();
        _baselineRun = c.RunId;
        Stage = CalibrationStage.Done;
    }

    /// <summary>Texto de uma linha do resultado: "2.150 RPM no máximo · mínimo 20%".</summary>
    public static string Describe(CalibrationFanResult r) =>
        r.MaxRpm > 0 ? $"{r.MaxRpm.ToString("N0", PtBr)} RPM no máximo · mínimo {r.MinPercent}%" : "Sem rotação";
}

/// <summary>Uma linha do resultado no assistente.</summary>
public sealed record CalibrationResultRow(string Name, string Detail, string? Note, bool Warning);
