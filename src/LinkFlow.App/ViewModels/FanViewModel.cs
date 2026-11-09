using LinkFlow.Core;

namespace LinkFlow.App.ViewModels;

/// <summary>
/// Ventoinha/bomba exibida na interface. O valor do slider é calculado:
/// vinculada → acompanha a potência global; desvinculada → valor manual.
/// Atualizações vindas do engine nunca alteram o vínculo (corrige a "desvinculação em cascata").
/// </summary>
public sealed class FanViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private int _rpm;
    private byte _appliedPercent;
    private bool _isLinked = true;
    private byte _manualPercent = 55;
    private string? _customName;
    private string? _group;
    private bool _isSelected;
    private float? _temperature;
    private bool _isEditingName;

    public FanViewModel(MainViewModel owner, FanState state)
    {
        _owner = owner;
        Key = state.Key;
        Channel = state.Channel;
        DeviceId = state.DeviceId;
        DefaultName = state.DefaultName;
        Model = state.Model;
        IsPump = state.IsPump;
        UpdateFrom(state);
    }

    public string Key { get; }
    public int Channel { get; }
    public string DeviceId { get; }
    public string DefaultName { get; }
    public string Model { get; }
    public bool IsPump { get; }

    public double SliderMinimum => IsPump ? FanLimits.PumpMinPercent : 25;

    public string Name
    {
        get => _customName ?? DefaultName;
        set
        {
            var trimmed = string.IsNullOrWhiteSpace(value) || value.Trim() == DefaultName ? null : value.Trim();
            if (_customName == trimmed) return;
            _customName = trimmed;
            OnPropertiesChanged(nameof(Name), nameof(HasCustomName), nameof(NameTooltip));
            _owner.OnFanRenamed(this, trimmed);
        }
    }

    public bool IsUnknownModel => Model == nameof(LinkDeviceModel.Unknown);

    public string ChannelLabel =>
        $"Canal {Channel:D2}{(Group is { Length: > 0 } g ? $" • {g}" : string.Empty)}{(IsUnknownModel ? " • modelo não identificado" : string.Empty)}";

    public bool HasCustomName => _customName != null;
    public string NameTooltip => HasCustomName ? $"{DefaultName} • clique duplo ou Ctrl+R para renomear" : "Clique duplo ou Ctrl+R para renomear";

    public string? Group
    {
        get => _group;
        set
        {
            if (SetProperty(ref _group, string.IsNullOrWhiteSpace(value) ? null : value.Trim()))
                OnPropertiesChanged(nameof(ChannelLabel), nameof(HasGroup));
        }
    }

    public bool HasGroup => _group != null;
    public bool IsManual => !_isLinked;

    public bool IsEditingName
    {
        get => _isEditingName;
        set => SetProperty(ref _isEditingName, value);
    }

    public int Rpm
    {
        get => _rpm;
        private set
        {
            if (SetProperty(ref _rpm, value))
                OnPropertiesChanged(nameof(RpmFormatted), nameof(StatusColor), nameof(IsStopped), nameof(ShowStoppedWarning));
        }
    }

    public string RpmFormatted => $"{_rpm:N0} RPM";
    public bool IsStopped => _rpm == 0;

    /// <summary>Borda vermelha só para ventoinhas reais (modelos desconhecidos podem não ter tacômetro).</summary>
    public bool ShowStoppedWarning => _rpm == 0 && !IsUnknownModel;

    public float? Temperature
    {
        get => _temperature;
        private set => SetProperty(ref _temperature, value);
    }

    public byte AppliedPercent
    {
        get => _appliedPercent;
        private set => SetProperty(ref _appliedPercent, value);
    }

    public bool IsLinked
    {
        get => _isLinked;
        set
        {
            if (_isLinked == value) return;
            _isLinked = value;
            if (!value) _manualPercent = (byte)Math.Round(SliderValue);
            OnPropertiesChanged(nameof(IsLinked), nameof(SliderValue), nameof(PowerFormatted), nameof(LinkTooltip), nameof(IsManual));
            _owner.OnFanOverrideChanged(this);
        }
    }

    public string LinkTooltip => _isLinked
        ? "Vinculada à potência global. Mova o slider para controlar só esta ventoinha."
        : "Controle manual. Clique para voltar a seguir a potência global.";

    public byte ManualPercent => _manualPercent;

    /// <summary>Valor exibido no slider. Ao arrastar, desvincula a ventoinha do global.</summary>
    public double SliderValue
    {
        get
        {
            byte v = _isLinked ? _owner.LinkedTargetFor(this) : _manualPercent;
            return Math.Max(SliderMinimum, v);
        }
        set
        {
            byte v = (byte)Math.Clamp(Math.Round(value), SliderMinimum, 100);
            if (!_isLinked && v == _manualPercent) return;
            if (_isLinked && Math.Abs(value - SliderValue) < 0.5) return; // eco de atualização, não é ação do usuário
            _manualPercent = v;
            _isLinked = false;
            OnPropertiesChanged(nameof(SliderValue), nameof(IsLinked), nameof(PowerFormatted), nameof(LinkTooltip), nameof(IsManual));
            _owner.OnFanOverrideChanged(this);
        }
    }

    public string PowerFormatted => $"{(int)SliderValue}%";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value)) _owner.OnSelectionChanged();
        }
    }

    public string StatusColor
    {
        get
        {
            if (_rpm == 0) return IsUnknownModel ? "#64748B" : "#F43F5E";
            if (IsPump) return "#38BDF8";
            if (_rpm < 1100) return "#10B981";
            if (_rpm < 1800) return "#06B6D4";
            return "#F59E0B";
        }
    }

    public void UpdateFrom(FanState s)
    {
        Rpm = s.Rpm;
        AppliedPercent = s.AppliedPercent;
        Temperature = s.Temperature;
    }

    /// <summary>Carrega vínculo/nome/grupo da configuração sem disparar escrita no engine.</summary>
    public void LoadOverride(FanOverride o)
    {
        bool changed = _isLinked != o.Linked || _manualPercent != o.Percent;
        _isLinked = o.Linked;
        _manualPercent = o.Percent;
        if (_customName != o.Name)
        {
            _customName = o.Name;
            OnPropertiesChanged(nameof(Name), nameof(HasCustomName), nameof(NameTooltip));
        }
        Group = o.Group;
        if (changed) OnPropertiesChanged(nameof(IsLinked), nameof(SliderValue), nameof(PowerFormatted), nameof(LinkTooltip), nameof(IsManual));
    }

    public void SetManual(byte percent)
    {
        _manualPercent = (byte)Math.Clamp(percent, (int)SliderMinimum, 100);
        _isLinked = false;
        OnPropertiesChanged(nameof(SliderValue), nameof(IsLinked), nameof(PowerFormatted), nameof(LinkTooltip), nameof(IsManual));
    }

    public void SetLinkedSilently(bool linked)
    {
        if (_isLinked == linked) return;
        _isLinked = linked;
        OnPropertiesChanged(nameof(SliderValue), nameof(IsLinked), nameof(PowerFormatted), nameof(LinkTooltip), nameof(IsManual));
    }

    public void RefreshLinkedValue()
    {
        if (_isLinked) OnPropertiesChanged(nameof(SliderValue), nameof(PowerFormatted));
    }
}
