using System.Collections.ObjectModel;
using System.Windows.Input;
using LinkFlow.Core;

namespace LinkFlow.App.ViewModels;

/// <summary>Grupo na tela de curvas: tem ou não curva própria e qual sensor segue.</summary>
public sealed class GroupCurveItem : ObservableObject
{
    private readonly MainViewModel _owner;
    private bool _hasCurve;
    private CurveSource _source;
    private string _status = string.Empty;

    public GroupCurveItem(MainViewModel owner, string name) { _owner = owner; Name = name; }

    public string Name { get; }
    public int Members { get; set; }

    public bool HasOwnCurve
    {
        get => _hasCurve;
        set { if (SetProperty(ref _hasCurve, value) && !_owner.LoadingGroups) _owner.SetGroupCurveEnabled(Name, value); }
    }

    public CurveSource Source
    {
        get => _source;
        set { if (SetProperty(ref _source, value) && !_owner.LoadingGroups) _owner.SetGroupSource(Name, value); }
    }

    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public bool IsEditing => _owner.CurveTarget == Name;

    internal void Load(bool hasCurve, CurveSource source, int members)
    {
        _hasCurve = hasCurve;
        _source = source;
        Members = members;
        OnPropertiesChanged(nameof(HasOwnCurve), nameof(Source), nameof(Members), nameof(IsEditing));
    }
}

/// <summary>Curvas por grupo (ex.: radiador segue o líquido, gabinete segue a GPU).</summary>
public partial class MainViewModel
{
    private string? _curveTarget;
    private ICommand? _editCurveCommand;

    public ObservableCollection<GroupCurveItem> GroupCurves { get; } = new();
    public bool HasGroupCurves => GroupCurves.Count > 0;
    internal bool LoadingGroups { get; private set; }

    /// <summary>Curva em edição no editor: null = global; senão o nome do grupo.</summary>
    public string? CurveTarget => _curveTarget;
    public bool IsEditingGroupCurve => _curveTarget != null;
    public string CurveTitle => _curveTarget == null ? "Curva de temperatura" : $"Curva do grupo \"{_curveTarget}\"";

    /// <summary>O cartão da curva aparece no modo curva ou ao editar um grupo.</summary>
    public bool ShowCurveCard => IsCurveMode || _curveTarget != null;

    /// <summary>Editar: param = nome do grupo, ou null/"" para a curva global.</summary>
    public ICommand EditCurveCommand => _editCurveCommand ??= new RelayCommand(p =>
    {
        var name = p as string;
        if (!string.IsNullOrEmpty(name) && !_engine.Config.GroupCurves.ContainsKey(name)) SetGroupCurveEnabled(name, true);
        _curveTarget = string.IsNullOrEmpty(name) ? null : name;
        ReloadShownCurve();
    });

    internal FanCurveConfig TargetCurve(ControlConfig c)
    {
        if (_curveTarget == null) return c.Curve;
        if (!c.GroupCurves.TryGetValue(_curveTarget, out var g))
        {
            g = Json.Clone(c.Curve);
            c.GroupCurves[_curveTarget] = g;
        }
        return g;
    }

    private FanCurveConfig ShownCurve(ControlConfig c) =>
        _curveTarget != null && c.GroupCurves.TryGetValue(_curveTarget, out var g) ? g : c.Curve;

    private void ReloadShownCurve()
    {
        var c = _engine.Config;
        if (_curveTarget != null && !c.GroupCurves.ContainsKey(_curveTarget)) _curveTarget = null;
        LoadConfig(c);
        OnPropertiesChanged(nameof(CurveTarget), nameof(IsEditingGroupCurve), nameof(CurveTitle), nameof(ShowCurveCard), nameof(CurveCurrentOutput));
        foreach (var g in GroupCurves) g.Load(g.HasOwnCurve, g.Source, g.Members);
    }

    internal void SetGroupCurveEnabled(string group, bool enabled)
    {
        WithUndo(enabled ? $"Grupo \"{group}\" agora segue a própria curva" : $"Grupo \"{group}\" voltou a seguir o global", () =>
            _engine.UpdateConfig(c =>
            {
                if (enabled) c.GroupCurves.TryAdd(group, Json.Clone(c.Curve));
                else c.GroupCurves.Remove(group);
            }));
        if (!enabled && _curveTarget == group) _curveTarget = null;
        if (enabled) _curveTarget = group;
        ReloadShownCurve();
    }

    internal void SetGroupSource(string group, CurveSource source)
    {
        _engine.UpdateConfig(c => { if (c.GroupCurves.TryGetValue(group, out var g)) g.Source = source; });
        if (_curveTarget == group) ReloadShownCurve();
    }

    private void RefreshCurveTargets(ControlConfig c)
    {
        var names = Fans.Select(f => f.Group).Where(g => !string.IsNullOrEmpty(g)).Cast<string>().Distinct().OrderBy(g => g).ToList();
        LoadingGroups = true;
        try
        {
            if (!names.SequenceEqual(GroupCurves.Select(g => g.Name)))
            {
                GroupCurves.Clear();
                foreach (var n in names) GroupCurves.Add(new GroupCurveItem(this, n));
                OnPropertyChanged(nameof(HasGroupCurves));
            }
            foreach (var g in GroupCurves)
            {
                bool has = c.GroupCurves.TryGetValue(g.Name, out var curve);
                g.Load(has, curve?.Source ?? c.Curve.Source, Fans.Count(f => f.Group == g.Name));
            }
        }
        finally
        {
            LoadingGroups = false;
        }
    }

    /// <summary>Status ao vivo: "seguindo 32,5°C → 48%".</summary>
    private void RefreshGroupStatus(EngineSnapshot s)
    {
        foreach (var g in GroupCurves)
            g.Status = g.HasOwnCurve
                ? s.GroupOutputs.TryGetValue(g.Name, out var o) ? $"{o}% agora" : "aguardando sensor"
                : "segue o global";
    }
}
