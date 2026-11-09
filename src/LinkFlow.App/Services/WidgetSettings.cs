using LinkFlow.Core;

namespace LinkFlow.App.Services;

public enum WidgetLayout { Horizontal, Vertical, Grid }

public sealed class WidgetItemSetting
{
    public string Id { get; set; } = string.Empty;
    public SensorDisplay Display { get; set; } = SensorDisplay.Value;
}

/// <summary>Aparência e comportamento do widget flutuante (por usuário, em ui.json).</summary>
public sealed class WidgetSettings
{
    public List<WidgetItemSetting> Items { get; set; } = new();
    public WidgetLayout Layout { get; set; } = WidgetLayout.Horizontal;
    public int GridColumns { get; set; } = 2;

    public double Scale { get; set; } = 1.0;
    public double Opacity { get; set; } = 0.95;
    public string Background { get; set; } = "#0B1120";
    public double BackgroundOpacity { get; set; } = 0.94;
    public double CornerRadius { get; set; } = 14;
    public bool UseSensorColors { get; set; } = true;
    public string AccentColor { get; set; } = "#00E5FF";
    public string FontFamily { get; set; } = "Segoe UI Variable Text";
    public bool ShowLabels { get; set; } = true;
    public bool ShowBars { get; set; } = true;
    public bool ShowBorder { get; set; } = true;

    public bool AlwaysOnTop { get; set; } = true;
    public bool ClickThrough { get; set; }
    public bool HideInFullscreen { get; set; } = true;
    public bool SnapToEdges { get; set; } = true;
    public bool LockPosition { get; set; }

    public static List<WidgetItemSetting> DefaultItems() => new()
    {
        new() { Id = "cpu.temp" }, new() { Id = "gpu.temp" }, new() { Id = "coolant.temp" }, new() { Id = "fans.avg" }
    };

    public void Normalize()
    {
        Items ??= new();
        Items = Items.Where(i => !string.IsNullOrWhiteSpace(i.Id)).GroupBy(i => i.Id).Select(g => g.First()).ToList();
        GridColumns = Math.Clamp(GridColumns, 1, 6);
        Scale = Math.Clamp(Scale, 0.6, 2.5);
        Opacity = Math.Clamp(Opacity, 0.2, 1);
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0, 1);
        CornerRadius = Math.Clamp(CornerRadius, 0, 30);
        Background ??= "#0B1120";
        AccentColor ??= "#00E5FF";
        FontFamily ??= "Segoe UI Variable Text";
    }

    /// <summary>Converte as opções antigas (MiniShowCpu...) para a lista de itens.</summary>
    public static WidgetSettings MigrateFrom(UiSettings s)
    {
        var w = new WidgetSettings { Opacity = s.MiniOpacity };
        if (s.MiniShowCpu) w.Items.Add(new() { Id = "cpu.temp" });
        if (s.MiniShowGpu) w.Items.Add(new() { Id = "gpu.temp" });
        if (s.MiniShowCoolant) w.Items.Add(new() { Id = "coolant.temp" });
        if (s.MiniShowFans) w.Items.Add(new() { Id = "fans.avg" });
        if (s.MiniShowRam) w.Items.Add(new() { Id = "ram.usage", Display = SensorDisplay.Percent });
        if (s.MiniShowPump) w.Items.Add(new() { Id = "pump.rpm" });
        if (w.Items.Count == 0) w.Items = DefaultItems();
        return w;
    }
}
