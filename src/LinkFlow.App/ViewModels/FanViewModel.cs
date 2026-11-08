using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LinkFlow.App.ViewModels;

public class FanViewModel : INotifyPropertyChanged
{
    private int _currentRpm;
    private byte _targetPowerPercent = 50;
    private bool _isSyncedWithGlobal = true;

    public int Channel { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public bool IsPump { get; set; }

    public int CurrentRpm
    {
        get => _currentRpm;
        set
        {
            if (_currentRpm != value)
            {
                _currentRpm = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RpmFormatted));
                OnPropertyChanged(nameof(StatusColor));
            }
        }
    }

    public string RpmFormatted => $"{_currentRpm} RPM";

    public byte TargetPowerPercent
    {
        get => _targetPowerPercent;
        set
        {
            if (_targetPowerPercent != value)
            {
                _targetPowerPercent = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PowerFormatted));
            }
        }
    }

    public string PowerFormatted => $"{_targetPowerPercent}%";

    public bool IsSyncedWithGlobal
    {
        get => _isSyncedWithGlobal;
        set
        {
            if (_isSyncedWithGlobal != value)
            {
                _isSyncedWithGlobal = value;
                OnPropertyChanged();
            }
        }
    }

    public string StatusColor
    {
        get
        {
            if (IsPump) return "#38BDF8"; // Sky Blue
            if (_currentRpm < 1100) return "#10B981"; // Emerald / Quiet
            if (_currentRpm < 1800) return "#06B6D4"; // Cyan / Balanced
            return "#F43F5E"; // Rose / High
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
