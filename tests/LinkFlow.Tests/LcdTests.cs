using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LinkFlow.Tests;

/// <summary>Montagem dos quadros do LCD: métricas personalizadas, imagens, GIFs e apresentação de slides.</summary>
public sealed class LcdTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "linkflow-lcd-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public LcdTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>
    /// GIF89a animado padrão (com atraso por quadro e loop), escrito à mão: o GifBitmapEncoder do WPF
    /// não grava os blocos de controle e o GDI+ enxerga só o primeiro quadro.
    /// LZW "sem compressão": código de limpeza a cada 254 pixels mantém os códigos em 9 bits.
    /// </summary>
    internal static string CreateAnimatedGif(string dir, string name, int size, int frames, int delayCentis = 10)
    {
        using var ms = new MemoryStream();
        void W(params byte[] b) => ms.Write(b);
        void W16(int v) => W((byte)v, (byte)(v >> 8));

        W("GIF89a"u8.ToArray());
        W16(size); W16(size);
        W(0xF7, 0, 0); // tabela global de 256 cores
        for (int i = 0; i < 256; i++) W((byte)(i * 40), (byte)(255 - i * 30), (byte)(i * 70));
        W(0x21, 0xFF, 0x0B); W("NETSCAPE2.0"u8.ToArray()); W(3, 1, 0, 0, 0);

        for (int f = 0; f < frames; f++)
        {
            W(0x21, 0xF9, 4, 0); W16(delayCentis); W(0, 0);
            W(0x2C); W16(0); W16(0); W16(size); W16(size); W(0);
            W(8); // tamanho mínimo do código LZW

            var bytes = new List<byte>();
            int bitBuf = 0, bitCount = 0;
            void Code(int c)
            {
                bitBuf |= c << bitCount;
                bitCount += 9;
                while (bitCount >= 8) { bytes.Add((byte)bitBuf); bitBuf >>= 8; bitCount -= 8; }
            }
            int pixels = size * size, run = 0;
            Code(256);
            for (int p = 0; p < pixels; p++)
            {
                if (run == 254) { Code(256); run = 0; }
                Code(f % 256);
                run++;
            }
            Code(257);
            if (bitCount > 0) bytes.Add((byte)bitBuf);
            for (int i = 0; i < bytes.Count; i += 255)
            {
                int n = Math.Min(255, bytes.Count - i);
                W((byte)n);
                ms.Write(bytes.GetRange(i, n).ToArray());
            }
            W(0);
        }
        W(0x3B);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    internal static string CreateImage(string dir, string name, int size, Color color, int frames = 1)
    {
        if (frames > 1) return CreateAnimatedGif(dir, name, size, frames);
        BitmapEncoder encoder = new PngBitmapEncoder();
        for (int i = 0; i < frames; i++)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)(color.R / (i + 1)), color.G, color.B)), null, new System.Windows.Rect(0, 0, size, size));
            }
            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            encoder.Frames.Add(BitmapFrame.Create(rtb));
        }
        var path = Path.Combine(dir, name);
        using var fs = File.Create(path);
        encoder.Save(fs);
        return path;
    }

    private static EngineSnapshot Snapshot(bool critical = false) => new()
    {
        CpuSensorAvailable = true, CpuTemp = 55, GpuTemp = 47, CoolantTemp = 32.5f, CpuLoad = 30,
        Fans = new() { new FanState { Key = "p", IsPump = true, Rpm = 2200 }, new FanState { Key = "f", Rpm = 1100 } },
        Alerts = critical ? new() { new EngineAlert { Key = "cpu-temp", Severity = AlertSeverity.Critical, Title = "CPU em temperatura crítica" } } : new()
    };

    private static bool IsJpeg(byte[] b) => b.Length > 1000 && b[0] == 0xFF && b[1] == 0xD8;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void Metrics_layout_renders_with_any_number_of_secondary_items(int secondary)
    {
        using var composer = new LcdComposer();
        var cfg = new LcdConfig
        {
            MainMetric = LcdMetric.GpuTemp,
            SecondaryMetrics = new[] { LcdMetric.CpuTemp, LcdMetric.PumpRpm, LcdMetric.Clock, LcdMetric.RamUsage }.Take(secondary).ToList(),
            Rotation = 90
        };
        var (jpeg, next) = composer.ComposeJpeg(cfg, Snapshot(), new RgbConfig());
        Assert.True(IsJpeg(jpeg));
        Assert.Equal(TimeSpan.FromSeconds(1), next);
        Assert.Equal("Métricas", composer.Status);
    }

    [Fact]
    public void Metric_values_are_formatted_for_display()
    {
        var s = Snapshot();
        Assert.Equal("55°", LcdComposer.Metric(LcdMetric.CpuTemp, s).Value);
        Assert.Equal("32,5°", LcdComposer.Metric(LcdMetric.CoolantTemp, s).Value);
        Assert.Equal("2.200", LcdComposer.Metric(LcdMetric.PumpRpm, s).Value);
        Assert.Equal("--", LcdComposer.Metric(LcdMetric.GpuPower, s).Value);
        Assert.Equal(0.55f, LcdComposer.Metric(LcdMetric.CpuTemp, s).Ratio!.Value, 2);

        s.CpuSensorAvailable = false;
        Assert.Equal("--", LcdComposer.Metric(LcdMetric.CpuTemp, s).Value);
    }

    [Fact]
    public void Gif_frames_advance_with_time_and_report_frame_delay()
    {
        var gif = CreateImage(_dir, "anim.gif", 120, Colors.Red, frames: 4);
        using var composer = new LcdComposer();
        var cfg = new LcdConfig { Mode = LcdMode.Media, Media = { new LcdMediaItem { Path = gif, Name = "anim.gif" } } };

        var next = composer.Compose(cfg, Snapshot(), new RgbConfig());
        Assert.Contains("GIF", composer.Status);
        Assert.Contains("4 quadros", composer.Status);
        Assert.InRange(next.TotalMilliseconds, 50, 200);

        var first = composer.EncodeJpeg();
        Thread.Sleep(260);
        composer.Compose(cfg, Snapshot(), new RgbConfig());
        Assert.NotEqual(first, composer.EncodeJpeg());
    }

    [Fact]
    public void Slideshow_moves_to_next_item_after_duration()
    {
        var a = CreateImage(_dir, "a.png", 64, Colors.Blue);
        var b = CreateImage(_dir, "b.png", 64, Colors.Green);
        using var composer = new LcdComposer();
        var cfg = new LcdConfig
        {
            Mode = LcdMode.Media,
            Media = { new LcdMediaItem { Path = a, DurationSeconds = 2 }, new LcdMediaItem { Path = b, DurationSeconds = 2 } }
        };
        cfg.Normalize();

        composer.Compose(cfg, Snapshot(), new RgbConfig());
        Assert.Equal("Imagem 1/2", composer.Status);
        Thread.Sleep(2100);
        composer.Compose(cfg, Snapshot(), new RgbConfig());
        Assert.Equal("Imagem 2/2", composer.Status);
    }

    [Fact]
    public void Missing_file_is_reported_and_cached_gif_frames_are_reused()
    {
        using var composer = new LcdComposer();
        var missing = new LcdConfig { Mode = LcdMode.Media, Media = { new LcdMediaItem { Path = Path.Combine(_dir, "nao-existe.png") } } };
        var (jpeg, _) = composer.ComposeJpeg(missing, Snapshot(), new RgbConfig());
        Assert.True(IsJpeg(jpeg));
        Assert.Contains("não encontrado", composer.Status);

        var still = CreateImage(_dir, "still.png", 64, Colors.Orange);
        var cfg = new LcdConfig { Mode = LcdMode.Media, Media = { new LcdMediaItem { Path = still } } };
        var (j1, _) = composer.ComposeJpeg(cfg, Snapshot(), new RgbConfig());
        var (j2, _) = composer.ComposeJpeg(cfg, Snapshot(), new RgbConfig());
        Assert.Same(j1, j2); // sem sobreposição: o JPEG já codificado é reaproveitado

        // Alerta crítico desenha o aviso por cima (não usa o cache)
        var (j3, _) = composer.ComposeJpeg(cfg, Snapshot(critical: true), new RgbConfig());
        Assert.NotSame(j1, j3);
    }

    [Fact]
    public void Config_normalize_limits_secondary_items_and_snaps_rotation()
    {
        var cfg = new LcdConfig
        {
            Rotation = 100,
            Title = new string('x', 40),
            SecondaryMetrics = new() { LcdMetric.CpuTemp, LcdMetric.CpuTemp, LcdMetric.None, LcdMetric.GpuTemp, LcdMetric.RamUsage, LcdMetric.Clock, LcdMetric.FanRpm },
            Media = { new LcdMediaItem { Path = "" }, new LcdMediaItem { Path = "x.png", DurationSeconds = 0 } }
        };
        cfg.Normalize();
        Assert.Equal(90, cfg.Rotation);
        Assert.Equal(24, cfg.Title.Length);
        Assert.Equal(new[] { LcdMetric.CpuTemp, LcdMetric.GpuTemp, LcdMetric.RamUsage, LcdMetric.Clock }, cfg.SecondaryMetrics);
        Assert.Single(cfg.Media);
        Assert.Equal(2, cfg.Media[0].DurationSeconds);
    }

    [Fact]
    public void Engine_streams_gif_to_lcd_faster_than_metrics()
    {
        var gif = CreateImage(_dir, "fast.gif", 96, Colors.Cyan, frames: 3);
        var metricsInterval = TimeSpan.FromSeconds(2);
        using var f = new EngineFixture(configure: o => o.LcdFrameInterval = metricsInterval);
        EngineFixture.WaitUntil(() => f.Hw.Lcd.Frames > 0, because: "primeiro quadro de métricas");

        f.Engine.UpdateConfig(c =>
        {
            c.Lcd.Mode = LcdMode.Media;
            c.Lcd.Media.Add(new LcdMediaItem { Path = gif });
        });
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().LcdStatus?.Contains("GIF") == true,
            because: "primeiro quadro do GIF");

        int start = f.Hw.Lcd.Frames;
        EngineFixture.WaitUntil(() => f.Hw.Lcd.Frames - start >= 5, timeoutMs: (int)metricsInterval.TotalMilliseconds,
            because: "cinco quadros do GIF dentro de um intervalo de métricas");
    }

    [Fact]
    public void Media_library_deduplicates_by_content()
    {
        var src1 = CreateImage(_dir, "one.png", 32, Colors.Purple);
        var src2 = Path.Combine(_dir, "copy-of-one.png");
        File.Copy(src1, src2);

        var old = AppPaths.SharedDir;
        AppPaths.SharedDir = Path.Combine(_dir, "shared");
        try
        {
            var a = MediaLibrary.Import(src1);
            var b = MediaLibrary.Import(src2);
            Assert.Equal(a, b);
            Assert.StartsWith(AppPaths.MediaDir, a);
            Assert.True(File.Exists(a));
        }
        finally
        {
            AppPaths.SharedDir = old;
        }
    }
}
