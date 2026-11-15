using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinkFlow.Tests;

/// <summary>
/// ControlConfig.Clone substituiu a ida e volta por JSON. Estes testes garantem que a cópia tem o mesmo conteúdo e
/// não compartilha nenhum objeto com o original, inclusive para propriedades que forem criadas no futuro.
/// </summary>
public class ConfigCloneTests
{
    /// <summary>Configuração com todas as listas, dicionários e objetos internos preenchidos.</summary>
    internal static ControlConfig Populated()
    {
        var c = new ControlConfig
        {
            Mode = FanControlMode.Curve,
            GlobalPercent = 70,
            ActivePreset = "Meu perfil",
            Curve = new FanCurveConfig { Source = CurveSource.Coolant, Hysteresis = 4, Smoothing = 0.5f },
            GpuFanAuto = false,
            GpuFanPercent = 60,
            Fans =
            {
                ["ch1"] = new FanOverride
                {
                    Linked = false, Percent = 80, Name = "Frontal", Group = "Radiador",
                    Calibration = new FanCalibration { Date = new DateTime(2026, 11, 15), MaxRpm = 2150, MinPercent = 30, StartPercent = 25,
                                                       Points = { new CalibrationPoint(100, 2150), new CalibrationPoint(30, 820) } }
                }
            },
            Rgb = new RgbConfig
            {
                R = 1, G = 2, B = 3, Brightness = 90, RecentColors = { "#112233" }, DeviceBrightness = { ["hub"] = 70 },
                Effect = RgbEffect.Palette, Palette = { "#445566" }, LedGamma = 2.2f
            },
            Lcd = new LcdConfig
            {
                Mode = LcdMode.Media, Title = "PC", SecondaryMetrics = { LcdMetric.FanRpm }, BackgroundPath = @"C:\m\fundo.png",
                BackgroundCrop = new LcdCrop { Zoom = 1.5, OffsetX = 0.1 },
                Media = { new LcdMediaItem { Path = @"C:\m\a.gif", Name = "a", DurationSeconds = 7, Crop = new LcdCrop { Zoom = 2 } } }
            },
            Safety = new SafetyConfig { CpuCritical = 92, PumpMinRpm = 900 },
            GroupCurves = { ["Radiador"] = new FanCurveConfig { Source = CurveSource.Cpu } },
            Automation = new AutomationConfig
            {
                Rules = { new AppRule { Process = "jogo.exe", Profile = FanPresets.Max } },
                Quiet = new QuietHours { Enabled = true, StartHour = 22 }
            }
        };
        c.Profiles.Add(FanProfile.Capture("Salvo", c));
        // Perfis não guardam a calibração (é do hardware, não do perfil); a amostra preenche para o teste de cópia
        c.Profiles[0].Fans["ch1"].Calibration = c.Fans["ch1"].Calibration!.Clone();
        return c;
    }

    [Fact]
    public void Clone_has_the_same_content_as_the_json_round_trip()
    {
        var c = Populated();
        Assert.Equal(JsonSerializer.Serialize(Json.Clone(c), Json.Options), JsonSerializer.Serialize(c.Clone(), Json.Options));
    }

    [Fact]
    public void Clone_shares_no_object_with_the_original()
    {
        var original = Populated();
        var copy = original.Clone();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        AssertIndependent(original, copy, "config", visited);
    }

    [Fact]
    public void Sample_fills_every_reference_property()
    {
        // Uma propriedade nova vazia na amostra passaria pelo teste acima sem ser checada: obriga a preenchê-la
        var empty = new List<string>();
        Walk(Populated(), "config", new HashSet<object>(ReferenceEqualityComparer.Instance), empty);
        Assert.True(empty.Count == 0, "Preencha em Populated(): " + string.Join(", ", empty));
    }

    [Fact]
    public void Editing_the_copy_never_changes_the_original()
    {
        var original = Populated();
        string before = JsonSerializer.Serialize(original, Json.Options);
        var copy = original.Clone();

        copy.Curve.Points[0].Percent = 99;
        copy.Fans["ch1"].Percent = 25;
        copy.Rgb.Palette.Add("#000000");
        copy.Rgb.DeviceBrightness["hub"] = 1;
        copy.Lcd.Media[0].Crop.Zoom = 9;
        copy.GroupCurves["Radiador"].Points.Clear();
        copy.Profiles[0].Fans["ch1"].Linked = true;
        copy.Automation.Rules[0].Enabled = false;
        copy.Automation.Quiet.StartHour = 1;
        copy.Safety.PumpMinRpm = 300;

        Assert.Equal(before, JsonSerializer.Serialize(original, Json.Options));
    }

    [Fact]
    public void Null_collections_stay_null()
    {
        var old = new FanProfile { GroupCurves = null };
        Assert.Null(old.Clone().GroupCurves);
        var c = new ControlConfig { Curve = null!, Fans = null! };
        var copy = c.Clone();
        Assert.Null(copy.Curve);
        Assert.Null(copy.Fans);
    }

    [Fact]
    public void Engine_hands_out_independent_copies()
    {
        using var f = new EngineFixture(Populated());
        var a = f.Engine.GetConfig();
        a.Curve.Points[0].Percent = 99;
        a.Rgb.Palette.Clear();
        var b = f.Engine.GetConfig();
        Assert.NotEqual(99, b.Curve.Points[0].Percent);
        Assert.NotEmpty(b.Rgb.Palette);
    }

    // ------------------------------------------------------------------ Reflexão

    private static bool IsLeaf(Type t) => t.IsValueType || t == typeof(string);

    private static IEnumerable<PropertyInfo> Properties(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
         .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<JsonIgnoreAttribute>() == null);

    private static void AssertIndependent(object a, object b, string path, HashSet<object> visited)
    {
        if (IsLeaf(a.GetType()) || !visited.Add(a)) return;
        Assert.False(ReferenceEquals(a, b), $"{path} é o mesmo objeto no original e na cópia");

        if (a is IDictionary da)
        {
            var db = (IDictionary)b;
            foreach (DictionaryEntry e in da)
                if (e.Value != null) AssertIndependent(e.Value, db[e.Key]!, $"{path}[{e.Key}]", visited);
            return;
        }
        if (a is IList la)
        {
            var lb = (IList)b;
            for (int i = 0; i < la.Count; i++)
                if (la[i] != null) AssertIndependent(la[i]!, lb[i]!, $"{path}[{i}]", visited);
            return;
        }
        foreach (var p in Properties(a.GetType()))
        {
            var va = p.GetValue(a);
            if (va != null) AssertIndependent(va, p.GetValue(b)!, $"{path}.{p.Name}", visited);
        }
    }

    private static void Walk(object o, string path, HashSet<object> visited, List<string> empty)
    {
        if (IsLeaf(o.GetType()) || !visited.Add(o)) return;
        if (o is IDictionary d)
        {
            if (d.Count == 0) empty.Add(path);
            foreach (DictionaryEntry e in d) if (e.Value != null) Walk(e.Value, $"{path}[{e.Key}]", visited, empty);
            return;
        }
        if (o is IList l)
        {
            if (l.Count == 0) empty.Add(path);
            foreach (var item in l) if (item != null) Walk(item, $"{path}[]", visited, empty);
            return;
        }
        foreach (var p in Properties(o.GetType()))
        {
            if (IsLeaf(p.PropertyType)) continue;
            var v = p.GetValue(o);
            if (v == null) empty.Add($"{path}.{p.Name}");
            else Walk(v, $"{path}.{p.Name}", visited, empty);
        }
    }
}
