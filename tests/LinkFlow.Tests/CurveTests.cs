namespace LinkFlow.Tests;

public class CurveTests
{
    [Theory]
    [InlineData(20, 30)]
    [InlineData(40, 30)]
    [InlineData(47.5f, 38)]
    [InlineData(70, 65)]
    [InlineData(85, 93)]
    [InlineData(120, 100)]
    public void Evaluate_interpolates_between_points(float temp, byte expected)
    {
        var curve = new FanCurveConfig();
        Assert.Equal(expected, curve.Evaluate(temp));
    }

    [Fact]
    public void Controller_rises_immediately_but_waits_hysteresis_to_drop()
    {
        var cfg = new FanCurveConfig { Smoothing = 1f, Hysteresis = 3f };
        var c = new CurveController();

        Assert.Equal(65, c.Evaluate(cfg, 70));
        Assert.Equal(85, c.Evaluate(cfg, 80));   // sobe na hora
        Assert.Equal(85, c.Evaluate(cfg, 78));   // queda de 2°C: mantém
        Assert.Equal(75, c.Evaluate(cfg, 75));   // queda de 5°C: desce
    }

    [Fact]
    public void Controller_smooths_spikes()
    {
        var cfg = new FanCurveConfig { Smoothing = 0.35f, Hysteresis = 3f };
        var c = new CurveController();
        c.Evaluate(cfg, 50);
        byte afterSpike = c.Evaluate(cfg, 90);   // pico isolado
        Assert.True(afterSpike < 80, $"pico não suavizado: {afterSpike}");
    }

    [Theory]
    [InlineData(CurveSource.MaxCpuGpu, 60f, 70f, 35f, 70f)]
    [InlineData(CurveSource.Cpu, 60f, 70f, 35f, 60f)]
    [InlineData(CurveSource.Coolant, 60f, 70f, 35f, 35f)]
    [InlineData(CurveSource.Coolant, 60f, 70f, null, 70f)]
    [InlineData(CurveSource.Cpu, null, 70f, null, 70f)]
    public void SelectTemp_falls_back_when_sensor_missing(CurveSource src, float? cpu, float? gpu, float? coolant, float expected)
    {
        Assert.Equal(expected, CurveController.SelectTemp(src, cpu, gpu, coolant));
    }

    [Fact]
    public void Normalize_sorts_and_clamps_points()
    {
        var cfg = new FanCurveConfig { Points = { new(200, 250) } };
        cfg.Points.Insert(0, new CurvePoint(95, 5));
        cfg.Normalize();
        Assert.True(cfg.Points.SequenceEqual(cfg.Points.OrderBy(p => p.Temp)));
        Assert.All(cfg.Points, p => Assert.InRange(p.Percent, FanLimits.MinPercent, 100));
        Assert.All(cfg.Points, p => Assert.InRange(p.Temp, 0, 110));
    }
}
