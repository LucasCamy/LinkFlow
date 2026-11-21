namespace LinkFlow.App.ViewModels;

public partial class MainViewModel
{
    private OperationalCenterViewModel? _operations;
    public OperationalCenterViewModel Operations => _operations ??= new(_engine, _dispatcher);
    private void RefreshOperations(LinkFlow.Core.EngineSnapshot snapshot) => _operations?.ObserveSnapshot(snapshot);
    private void DisposeOperations() => _operations?.Dispose();
}
