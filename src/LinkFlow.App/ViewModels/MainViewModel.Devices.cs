using System.Windows.Input;
using System.Windows.Threading;

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

    public string RescanLabel => _isRescanning ? "Procurando..." : "Atualizar";

    public void RescanDevices()
    {
        if (_isRescanning) return;
        _keysBeforeRescan = Fans.Select(f => f.Key).ToHashSet();
        _rescanStarted = DateTime.Now;
        IsRescanning = true;
        _engine.RescanDevices();
        ShowToast("Procurando ventoinhas, bomba, LCD e RGB...", canUndo: false);

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
            ShowToast("iCUE LINK Hub não encontrado. Verifique o USB e se o iCUE está fechado.", canUndo: false);
            return;
        }

        var now = Fans.Select(f => f.Key).ToHashSet();
        int added = now.Except(_keysBeforeRescan).Count();
        int removed = _keysBeforeRescan.Except(now).Count();
        var msg = $"{Fans.Count} dispositivo(s) encontrado(s)";
        if (added > 0) msg += $" • {added} novo(s)";
        if (removed > 0) msg += $" • {removed} não encontrado(s)";
        if (added == 0 && removed == 0) msg += " • nenhuma mudança";
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
        WithUndo($"\"{old}\" voltou a se chamar \"{fan.DefaultName}\"", () => fan.Name = fan.DefaultName);
    }
}
