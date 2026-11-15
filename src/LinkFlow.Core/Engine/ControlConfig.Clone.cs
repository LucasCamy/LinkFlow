namespace LinkFlow.Core;

// Cópia profunda da configuração, sem passar por JSON. Cada UpdateConfig copia a configuração inteira (ex.: dezenas de
// vezes por segundo ao arrastar a cor do RGB), e a ida e volta por JSON custava CPU e memória a cada passo.
// MemberwiseClone copia todos os campos; em seguida, cada lista, dicionário e objeto interno é duplicado.
// ConfigCloneTests percorre o grafo por reflexão: um campo de referência novo que não for copiado aqui faz o teste falhar.

public sealed partial class ControlConfig
{
    /// <summary>Cópia independente: alterar a cópia nunca altera o original.</summary>
    public ControlConfig Clone()
    {
        var c = (ControlConfig)MemberwiseClone();
        c.Curve = Curve?.Clone()!;
        c.Fans = ConfigCopy.Dictionary(Fans, f => f.Clone());
        c.Rgb = Rgb?.Clone()!;
        c.Lcd = Lcd?.Clone()!;
        c.Safety = Safety?.Clone()!;
        c.Profiles = ConfigCopy.List(Profiles, p => p.Clone());
        c.GroupCurves = ConfigCopy.Dictionary(GroupCurves, g => g.Clone());
        c.Automation = Automation?.Clone()!;
        return c;
    }
}

public sealed partial class FanOverride
{
    public FanOverride Clone() => (FanOverride)MemberwiseClone();
}

public sealed partial class CurvePoint
{
    public CurvePoint Clone() => (CurvePoint)MemberwiseClone();
}

public sealed partial class FanCurveConfig
{
    public FanCurveConfig Clone()
    {
        var c = (FanCurveConfig)MemberwiseClone();
        c.Points = ConfigCopy.List(Points, p => p.Clone());
        return c;
    }
}

public sealed partial class RgbConfig
{
    public RgbConfig Clone()
    {
        var c = (RgbConfig)MemberwiseClone();
        c.RecentColors = ConfigCopy.List(RecentColors);
        c.DeviceBrightness = ConfigCopy.Dictionary(DeviceBrightness);
        c.Palette = ConfigCopy.List(Palette);
        return c;
    }
}

public sealed partial class LcdCrop
{
    public LcdCrop Clone() => (LcdCrop)MemberwiseClone();
}

public sealed partial class LcdMediaItem
{
    public LcdMediaItem Clone()
    {
        var c = (LcdMediaItem)MemberwiseClone();
        c.Crop = Crop?.Clone()!;
        return c;
    }
}

public sealed partial class LcdConfig
{
    public LcdConfig Clone()
    {
        var c = (LcdConfig)MemberwiseClone();
        c.SecondaryMetrics = ConfigCopy.List(SecondaryMetrics);
        c.BackgroundCrop = BackgroundCrop?.Clone()!;
        c.Media = ConfigCopy.List(Media, m => m.Clone());
        return c;
    }
}

public sealed partial class SafetyConfig
{
    public SafetyConfig Clone() => (SafetyConfig)MemberwiseClone();
}

public sealed partial class FanProfile
{
    public FanProfile Clone()
    {
        var c = (FanProfile)MemberwiseClone();
        c.Curve = Curve?.Clone()!;
        c.Fans = ConfigCopy.Dictionary(Fans, f => f.Clone());
        c.GroupCurves = GroupCurves == null ? null : ConfigCopy.Dictionary(GroupCurves, g => g.Clone());
        return c;
    }
}

public sealed partial class AppRule
{
    public AppRule Clone() => (AppRule)MemberwiseClone();
}

public sealed partial class QuietHours
{
    public QuietHours Clone() => (QuietHours)MemberwiseClone();
}

public sealed partial class AutomationConfig
{
    public AutomationConfig Clone()
    {
        var c = (AutomationConfig)MemberwiseClone();
        c.Rules = ConfigCopy.List(Rules, r => r.Clone());
        c.Quiet = Quiet?.Clone()!;
        return c;
    }
}

/// <summary>Cópias de listas e dicionários da configuração (preservam null, como a ida e volta por JSON).</summary>
internal static class ConfigCopy
{
    public static List<T> List<T>(List<T>? source) => source == null ? null! : new List<T>(source);

    public static List<T> List<T>(List<T>? source, Func<T, T> clone) where T : class =>
        source == null ? null! : source.ConvertAll(item => item == null ? null! : clone(item));

    public static Dictionary<TKey, TValue> Dictionary<TKey, TValue>(Dictionary<TKey, TValue>? source) where TKey : notnull =>
        source == null ? null! : new Dictionary<TKey, TValue>(source, source.Comparer);

    public static Dictionary<TKey, TValue> Dictionary<TKey, TValue>(Dictionary<TKey, TValue>? source, Func<TValue, TValue> clone)
        where TKey : notnull where TValue : class
    {
        if (source == null) return null!;
        var copy = new Dictionary<TKey, TValue>(source.Count, source.Comparer);
        foreach (var (key, value) in source) copy[key] = value == null ? null! : clone(value);
        return copy;
    }
}
