using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LinkFlow.App.Services;
using LinkFlow.Core;

namespace LinkFlow.App.Views;

public partial class WindowsNetworkPage : UserControl
{
    private const string Measuring = "Em medição…";
    private static readonly double[] GaugeScales = [100, 250, 500, 1_000, 2_500, 5_000, 10_000];
    private CancellationTokenSource? _activeTest;
    private double _gaugeScale;

    public WindowsNetworkPage()
    {
        InitializeComponent();
        NetworkMethod.Text = $"Download e upload via Cloudflare · {NetworkSpeedTest.Connections} conexões em paralelo · " +
            $"Até {NetworkSpeedTest.SecondsPerDirection} s ou {NetworkSpeedTest.LimitMiBPerDirection} MiB por direção.";
        GaugeTrack.Data = GaugeArc(1);
        ResetGauge();
    }

    private async void StartNetwork_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTest != null) return;
        LatencyValue.Text = DownloadValue.Text = UploadValue.Text = "—";
        LatencyDetail.Text = DownloadDetail.Text = UploadDetail.Text = string.Empty;
        ResetGauge();
        if (App.Current.Options.Simulate)
        {
            var demo = new NetworkTestResult(new(24.5, "Dados simulados"), new(436.7, "Dados simulados"), new(198.3, "Dados simulados"));
            ShowMeasurement(NetworkTestStage.Latency, demo.Latency);
            ShowMeasurement(NetworkTestStage.Download, demo.Download);
            ShowMeasurement(NetworkTestStage.Upload, demo.Upload);
            ShowFinalGauge(demo);
            NetworkStatus.Text = "Resultado de demonstração.";
            return;
        }

        using var test = new CancellationTokenSource();
        _activeTest = test;
        StartNetworkButton.Visibility = Visibility.Collapsed;
        CancelNetworkButton.Visibility = NetworkProgress.Visibility = Visibility.Visible;
        CancelNetworkButton.Focus();
        NetworkStatus.Text = "Iniciando teste…";
        NetworkProgress.IsIndeterminate = true;
        try
        {
            var progress = new Progress<NetworkTestProgress>(sample =>
            {
                if (_activeTest != test) return;
                if (sample.Measurement is { } measurement)
                {
                    ShowMeasurement(sample.Stage, measurement);
                    NetworkProgress.Value = sample.Fraction;
                }
                else if (!test.IsCancellationRequested)
                {
                    ShowProgress(sample);
                }
            });
            var result = await NetworkSpeedTest.RunAsync(progress, test.Token);
            test.Token.ThrowIfCancellationRequested();
            _activeTest = null;
            ClearLiveDetail();
            ShowFinalGauge(result);
            var measures = new[] { result.Latency, result.Download, result.Upload };
            NetworkStatus.Text = measures.All(measure => measure.Value == null) ? "Nenhuma medição concluída." :
                measures.Any(measure => measure.Incomplete) ? "Teste concluído com resultados parciais." :
                "Teste concluído.";
        }
        catch (OperationCanceledException) when (test.IsCancellationRequested)
        {
            ResetGauge();
            NetworkStatus.Text = "Teste cancelado.";
        }
        catch (Exception ex)
        {
            Log.Error("Falha no teste de rede", ex);
            ResetGauge();
            ClearStageInProgress();
            NetworkStatus.Text = "Não foi possível concluir o teste de rede.";
        }
        finally
        {
            _activeTest = null;
            bool restoreFocus = CancelNetworkButton.IsKeyboardFocused;
            NetworkProgress.IsIndeterminate = false;
            CancelNetworkButton.Visibility = Visibility.Collapsed;
            NetworkProgress.Visibility = Visibility.Hidden;
            StartNetworkButton.Visibility = Visibility.Visible;
            if (restoreFocus) StartNetworkButton.Focus();
        }
    }

    private void CancelNetwork_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTest is null || _activeTest.IsCancellationRequested) return;
        ResetGauge();
        ClearStageInProgress();
        NetworkStatus.Text = "Cancelando o teste…";
        _activeTest.Cancel();
    }

    private void ClearStageInProgress()
    {
        foreach (var detail in new[] { LatencyDetail, DownloadDetail, UploadDetail })
            if (detail.Text == Measuring) detail.Text = string.Empty;
    }

    private void ShowProgress(NetworkTestProgress sample)
    {
        var (status, detail) = sample.Stage switch
        {
            NetworkTestStage.Latency => ("Medindo ping para o Google…", LatencyDetail),
            NetworkTestStage.Download => ("Medindo download…", DownloadDetail),
            _ => ("Medindo upload…", UploadDetail)
        };
        NetworkStatus.Text = status;
        detail.Text = Measuring;
        NetworkProgress.IsIndeterminate = sample.Stage == NetworkTestStage.Latency;
        NetworkProgress.Value = sample.Fraction;
        ShowGauge(sample.Mbps, sample.Mbps.HasValue ? "Mbps · taxa provisória" : "Mbps");
        if (sample.Stage == NetworkTestStage.Latency) return;

        string dataLabel = sample.Stage == NetworkTestStage.Download ? "recebidos" : "confirmados";
        LiveTransferDetail.Text = $"{sample.Seconds:N1} s · {sample.Bytes / (double)NetworkSpeedTest.BytesPerMiB:N1} MiB {dataLabel}";
        LiveTransferDetail.Visibility = Visibility.Visible;
    }

    private void ShowMeasurement(NetworkTestStage stage, NetworkMeasurement measurement)
    {
        var (value, detail, unit) = stage switch
        {
            NetworkTestStage.Latency => (LatencyValue, LatencyDetail, "ms"),
            NetworkTestStage.Download => (DownloadValue, DownloadDetail, "Mbps"),
            _ => (UploadValue, UploadDetail, "Mbps")
        };
        value.Text = measurement.Value is { } measured ? $"{measured:N1} {unit}" : "—";
        detail.Text = measurement.Detail;
    }

    private void ShowFinalGauge(NetworkTestResult result)
    {
        if (result.Upload.Value is { } upload)
            ShowGauge(upload, "Mbps · upload");
        else if (result.Download.Value is { } download)
            ShowGauge(download, "Mbps · download");
        else
            ShowGauge(null, "Mbps");
    }

    private void ResetGauge()
    {
        _gaugeScale = GaugeScales[0];
        NetworkProgress.Value = 0;
        ClearLiveDetail();
        ShowGauge(null, "Mbps");
    }

    private void ClearLiveDetail()
    {
        LiveTransferDetail.Text = string.Empty;
        LiveTransferDetail.Visibility = Visibility.Hidden;
    }

    private void ShowGauge(double? mbps, string caption)
    {
        double value = mbps ?? 0;
        if (value > _gaugeScale)
            _gaugeScale = GaugeScales.FirstOrDefault(scale => value <= scale, Math.Ceiling(value / 5_000) * 5_000);

        GaugeRate.Text = mbps is null ? "—" : $"{value:N1}";
        GaugeCaption.Text = caption;
        GaugeMaximum.Text = $"{_gaugeScale:N0} Mbps";
        GaugeFill.Visibility = value > 0 ? Visibility.Visible : Visibility.Collapsed;
        GaugeFill.Data = value > 0 ? GaugeArc(value / _gaugeScale) : null;
    }

    private static StreamGeometry GaugeArc(double fraction)
    {
        const double radius = 136;
        const double centerX = 160;
        const double centerY = 144;
        double angle = Math.PI * fraction;
        var arc = new StreamGeometry();
        using (var context = arc.Open())
        {
            context.BeginFigure(new Point(centerX - radius, centerY), isFilled: false, isClosed: false);
            context.ArcTo(new Point(centerX - radius * Math.Cos(angle), centerY - radius * Math.Sin(angle)),
                new Size(radius, radius), 0, isLargeArc: false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }
        arc.Freeze();
        return arc;
    }
}
