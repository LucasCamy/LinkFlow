using System.Windows.Threading;
using LinkFlow.Core;

namespace LinkFlow.App.ViewModels;

public partial class MainViewModel
{
    private readonly CancellationTokenSource _uiWorkLifetime = new();
    private bool _uiWorkStopped, _isBackupBusy;
    private string _backupStatus = string.Empty;
    internal bool UiWorkStopped => _uiWorkStopped;
    internal CancellationToken UiWorkToken => _uiWorkLifetime.Token;
    public bool IsBackupBusy { get => _isBackupBusy; private set { if (SetProperty(ref _isBackupBusy, value)) OnPropertiesChanged(nameof(CanBackup), nameof(CanImportBackup)); } }
    public bool CanBackup => !_uiWorkStopped && !IsBackupBusy;
    public bool CanImportBackup => CanBackup && CanControl;
    public string BackupStatus { get => _backupStatus; private set => SetProperty(ref _backupStatus, value); }

    internal bool TryBeginBackup()
    {
        _dispatcher.VerifyAccess();
        if (!CanBackup) return false;
        IsBackupBusy = true;
        return true;
    }
    internal void SetBackupStatus(string status) => BackupStatus = status;
    internal void EndBackup() { IsBackupBusy = false; BackupStatus = string.Empty; }
    internal void OfferBackupUndo(ControlConfig before, string message)
    {
        _dispatcher.VerifyAccess();
        LoadConfig(_engine.Config);
        _undoConfig = before;
        ShowToast(message, canUndo: true);
    }
    internal async Task PublishUiWorkAsync(Action action)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        if (_dispatcher.CheckAccess()) { action(); return; }
        try { await _dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task.ConfigureAwait(false); }
        catch (TaskCanceledException) when (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) { }
    }
    private void DisposeUiWork()
    {
        if (_uiWorkStopped) return;
        _uiWorkStopped = true;
        _uiWorkLifetime.Cancel();
        _lcdThumbnailLoader?.Dispose();
        _uiWorkLifetime.Dispose();
    }
}
