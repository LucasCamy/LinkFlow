using LinkFlow.Core;

namespace LinkFlow.App.Services;

/// <summary>Preferências da interface, por usuário (%AppData%\LinkFlow\ui.json).</summary>
public sealed class UiSettings
{
    public bool CloseToTray { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool ShowTrayTemperature { get; set; } = true;
    public bool AlertNotifications { get; set; } = true;
    public bool CheckForAppUpdates { get; set; } = true;
    public bool GlobalHotkeys { get; set; } = true;
    public bool TrayHintShown { get; set; }

    /// <summary>Transições entre telas e diálogos (também respeita "Efeitos de animação" do Windows).</summary>
    public bool Animations { get; set; } = true;

    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public int SelectedPage { get; set; }

    public double? MiniLeft { get; set; }
    public double? MiniTop { get; set; }
    public bool MiniVisible { get; set; }
    public bool MiniShowCpu { get; set; } = true;
    public bool MiniShowGpu { get; set; } = true;
    public bool MiniShowCoolant { get; set; } = true;
    public bool MiniShowFans { get; set; } = true;
    public bool MiniShowRam { get; set; } = false;
    public bool MiniShowPump { get; set; } = false;
    public double MiniOpacity { get; set; } = 0.95;

    /// <summary>Widget flutuante (null = migrar das opções antigas acima).</summary>
    public WidgetSettings? Widget { get; set; }
}

public sealed class UiSettingsService
{
    private readonly JsonFileStore<UiSettings> _store = new(AppPaths.UiSettingsFile);
    private readonly Timer _saveTimer;

    public UiSettingsService()
    {
        Current = _store.Load();
        _saveTimer = new Timer(_ => _store.Save(Current), null, Timeout.Infinite, Timeout.Infinite);
    }

    public UiSettings Current { get; }

    public event Action? Changed;

    public void Update(Action<UiSettings> change)
    {
        change(Current);
        _saveTimer.Change(500, Timeout.Infinite);
        Changed?.Invoke();
    }

    public void Flush()
    {
        _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _store.Save(Current);
    }
}
