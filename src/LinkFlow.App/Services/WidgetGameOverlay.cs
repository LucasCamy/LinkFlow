using System.Text;
using System.Windows.Threading;
using LinkFlow.App.ViewModels;
using LinkFlow.Core;

namespace LinkFlow.App.Services;

/// <summary>Envia os sensores do widget ao RTSS a partir do snapshot já recebido do engine.</summary>
internal sealed class WidgetGameOverlay : IDisposable
{
    private readonly WidgetViewModel _widget;
    private readonly Func<EngineSnapshot> _snapshot;
    private readonly Func<bool> _isConnected;
    private readonly DispatcherTimer _timer;
    private bool _hasPublished;

    public WidgetGameOverlay(WidgetViewModel widget, Func<EngineSnapshot> snapshot, Func<bool> isConnected, Dispatcher dispatcher)
    {
        _widget = widget;
        _snapshot = snapshot;
        _isConnected = isConnected;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        widget.BehaviorChanged += UpdateEnabled;
        UpdateEnabled();
    }

    private void UpdateEnabled()
    {
        if (_timer.IsEnabled && _widget.ShowInGames && _widget.IsWidgetEnabled) return;
        Tick();
    }

    private void Tick()
    {
        if (_widget.ShowInGames && _widget.IsWidgetEnabled)
        {
            Publish();
            _timer.Start();
        }
        else
        {
            Clear();
            if (!_hasPublished) _timer.Stop();
        }
    }

    private void Publish()
    {
        if (!_widget.HasItems)
        {
            Clear();
            return;
        }

        var status = RtssOverlay.Publish(() => Format(_widget.Items, _snapshot(), _isConnected()));
        if (status == RtssOverlayStatus.Publishing) _hasPublished = true;
        else if (status == RtssOverlayStatus.NotRunning) _hasPublished = false;
        _widget.SetGameOverlayStatus(status);
    }

    private void Clear()
    {
        if (_hasPublished) _hasPublished = !RtssOverlay.TryClear();
        _widget.SetGameOverlayStatus(null);
    }

    public void Dispose()
    {
        _widget.BehaviorChanged -= UpdateEnabled;
        _timer.Stop();
        // O renderizador pode estar lendo o OSD no momento do encerramento.
        for (int attempt = 0; attempt < 4 && _hasPublished; attempt++)
        {
            if (attempt > 0) Thread.Sleep(10);
            Clear();
        }
    }

    internal static string Format(IEnumerable<WidgetItemViewModel> items, EngineSnapshot snapshot, bool isConnected)
    {
        if (!isConnected) return "LinkFlow\nTelemetria indisponível";

        const string title = "LinkFlow";
        var text = new StringBuilder(title);
        foreach (var item in items)
        {
            if (text.Length >= RtssOverlay.TextCapacity - 2) break;
            var reading = item.Read(snapshot);
            if (!reading.Available) continue;
            text.Append('\n');
            AppendPlainText(text, item.Label);
            if (text.Length < RtssOverlay.TextCapacity - 1) text.Append(' ');
            AppendPlainText(text, reading.Text);
        }

        return text.Length > title.Length ? text.ToString() : "LinkFlow\nAguardando leituras";
    }

    private static void AppendPlainText(StringBuilder text, string value)
    {
        foreach (char c in value)
        {
            if (text.Length >= RtssOverlay.TextCapacity - 1) break;
            // RTSS aceita marcação no texto. Rótulos do usuário entram como texto simples.
            text.Append(c switch
            {
                '<' => '(', '>' => ')', '↓' => 'D', '↑' => 'U', '•' => '|',
                '\0' or '\r' or '\n' or '\t' => ' ', _ => c
            });
        }
    }
}
