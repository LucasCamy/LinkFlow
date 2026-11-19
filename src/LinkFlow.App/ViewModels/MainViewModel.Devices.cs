using System.Windows.Input;
using System.Windows.Threading;
using LinkFlow.App.Resources;

namespace LinkFlow.App.ViewModels;

/// <summary>Renomear dispositivos e reatualizar a lista de dispositivos encontrados.</summary>
public partial class MainViewModel
{
    private ICommand? _rescanDevicesCommand;
    private bool _isRescanning;
    private DispatcherTimer? _rescanTimer;
    private DateTime _rescanStarted;
    private HashSet<string> _keysBeforeRescan = new();

    public ICommand RescanDevicesCommand => _rescanDevicesCommand ??= new RelayCommand(RescanDevices, () => !_isRescanning);

    public bool IsRescanning
    {
        get => _isRescanning;
        private set
        {
            if (!SetProperty(ref _isRescanning, value)) return;
            OnPropertyChanged(nameof(RescanLabel));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string RescanLabel => _isRescanning ? Strings.Fan_Procurando : Strings.Fan_Atualizar;

    public void RescanDevices()
    {
        if (_isRescanning) return;
        _keysBeforeRescan = Fans.Select(f => f.Key).ToHashSet();
        _rescanStarted = DateTime.Now;
        IsRescanning = true;
        _engine.RescanDevices();
        ShowToast(Strings.Fan_ProcurandoVentoinhasBombaLCD, canUndo: false);

        _rescanTimer ??= new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
        _rescanTimer.Tick -= OnRescanTick;
        _rescanTimer.Tick += OnRescanTick;
        _rescanTimer.Start();
    }

    private void OnRescanTick(object? sender, EventArgs e)
    {
        var elapsed = DateTime.Now - _rescanStarted;
        // Espera o hub reconectar (mínimo 2,5 s para o LCD/RGB também responderem), no máximo 12 s
        if (elapsed < TimeSpan.FromSeconds(2.5) || (!Snapshot.HubConnected && elapsed < TimeSpan.FromSeconds(12))) return;

        _rescanTimer!.Stop();
        IsRescanning = false;

        if (!Snapshot.HubConnected)
        {
            ShowToast(Strings.Fan_ICUELINKHubNao, canUndo: false);
            return;
        }

        var now = Fans.Select(f => f.Key).ToHashSet();
        int added = now.Except(_keysBeforeRescan).Count();
        int removed = _keysBeforeRescan.Except(now).Count();
        var msg = string.Format(Strings.Fan_DispositivoEncontrado, Fans.Count);
        if (added > 0) msg += string.Format(Strings.Fan_Novo, added);
        if (removed > 0) msg += string.Format(Strings.Fan_NaoEncontrado, removed);
        if (added == 0 && removed == 0) msg += Strings.Fan_NenhumaMudanca;
        ShowToast(msg, canUndo: false);
    }

    // ------------------------------------------------------------------ Renomear

    /// <summary>Abre a edição do nome (só um cartão por vez).</summary>
    public void BeginRename(FanViewModel fan)
    {
        foreach (var f in Fans) if (f != fan) f.IsEditingName = false;
        fan.IsEditingName = true;
    }

    /// <summary>Ctrl+R: renomeia o único cartão selecionado.</summary>
    public bool BeginRenameSelected()
    {
        var selected = Fans.Where(f => f.IsSelected).ToList();
        if (selected.Count != 1) return false;
        BeginRename(selected[0]);
        return true;
    }

    public void ResetName(FanViewModel fan)
    {
        if (!fan.HasCustomName) return;
        var old = fan.Name;
        WithUndo(string.Format(Strings.Fan_VoltouChamar, old, fan.DefaultName), () => fan.Name = fan.DefaultName);
    }
}
