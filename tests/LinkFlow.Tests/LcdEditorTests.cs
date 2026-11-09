using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using LinkFlow.App.ViewModels;

namespace LinkFlow.Tests;

/// <summary>GIF como fundo animado, enquadramento (zoom/posição), telemetria opcional e editor.</summary>
public sealed class LcdEditorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "linkflow-lcdedit-" + Guid.NewGuid().ToString("N")[..8]);
    public LcdEditorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static EngineSnapshot Snap() => new() { CpuSensorAvailable = true, CpuTemp = 50, GpuTemp = 40 };

    [Fact]
    public void Gif_as_metrics_background_animates_and_telemetry_can_be_hidden()
    {
        var gif = LcdTests.CreateAnimatedGif(_dir, "bg.gif", 120, frames: 4);
        using var c = new LcdComposer();
        var cfg = new LcdConfig { BackgroundPath = gif, ShowTelemetry = true };

        var next = c.Compose(cfg, Snap(), new RgbConfig());
        Assert.True(c.IsAnimating);
        Assert.InRange(next.TotalMilliseconds, 50, 200);
        Assert.Equal("Métricas", c.Status);
        var withNumbers = c.EncodeJpeg();

        cfg.ShowTelemetry = false;
        c.Compose(cfg, Snap(), new RgbConfig());
        Assert.Equal("Imagem de fundo", c.Status);
        Assert.NotEqual(withNumbers.Length, c.EncodeJpeg().Length);
    }

    [Fact]
    public void Crop_zoom_and_offset_change_the_frame_and_stay_in_bounds()
    {
        var img = LcdTests.CreateImage(_dir, "tall.png", 200, Colors.Orange);
        using var c = new LcdComposer();
        var item = new LcdMediaItem { Path = img };
        var cfg = new LcdConfig { Mode = LcdMode.Media, Media = { item } };
        c.Compose(cfg, Snap(), new RgbConfig());
        var plain = c.EncodeJpeg();

        item.Crop = new LcdCrop { Zoom = 3, OffsetX = 5, OffsetY = -5 };
        item.Crop.Normalize();
        Assert.Equal((3, 1, -1), (item.Crop.Zoom, item.Crop.OffsetX, item.Crop.OffsetY));

        var (rx, ry) = LcdComposer.PanRange(188, 294, LcdFit.Cover, 1);
        Assert.Equal(0, rx, 1);   // retrato em "preencher": largura encaixa, sem folga horizontal
        Assert.True(ry > 100);    // pode subir/descer
    }

    [Fact]
    public void Pinned_item_stays_on_screen_while_editing()
    {
        var a = LcdTests.CreateImage(_dir, "a.png", 64, Colors.Blue);
        var b = LcdTests.CreateImage(_dir, "b.png", 64, Colors.Green);
        using var c = new LcdComposer();
        var cfg = new LcdConfig { Mode = LcdMode.Media, Media = { new() { Path = a, DurationSeconds = 2 }, new() { Path = b, DurationSeconds = 2 } } };
        c.Compose(cfg, Snap(), new RgbConfig(), pinItem: 1);
        Assert.Equal("Imagem 2/2", c.Status);
        Thread.Sleep(2100);
        c.Compose(cfg, Snap(), new RgbConfig(), pinItem: 1);
        Assert.Equal("Imagem 2/2", c.Status); // não avança enquanto edita
    }

    [Fact]
    public void Editor_pans_and_zooms_selected_item()
    {
        using var f = new EngineFixture();
        var client = new LocalEngineClient(f.Engine);
        var vm = new MainViewModel(client, Dispatcher.CurrentDispatcher);
        var tall = Path.Combine(_dir, "tall.gif");
        File.Copy(LcdTests.CreateAnimatedGif(_dir, "t.gif", 100, frames: 2), tall);

        vm.AddLcdMedia(new[] { (tall, "tall.gif") });
        Assert.True(vm.HasCropTarget);
        Assert.Equal(0, vm.LcdPreviewPin);

        vm.CropZoom = 2;
        vm.PanCropBy(0, 40, 290);
        var crop = client.Config.Lcd.Media[0].Crop;
        Assert.Equal(2, crop.Zoom);
        Assert.True(crop.OffsetY > 0);
        Assert.Equal("200%", vm.CropZoomText);

        vm.ZoomCropBy(+120);
        Assert.Equal(2.2, client.Config.Lcd.Media[0].Crop.Zoom, 2);

        vm.ResetCropCommand.Execute(null);
        Assert.True(client.Config.Lcd.Media[0].Crop.IsDefault);

        vm.SetLcdModeCommand.Execute("Metrics");
        Assert.Equal(-1, vm.LcdPreviewPin);
        Assert.False(vm.HasCropTarget); // sem foto de fundo nada a enquadrar
        vm.SetLcdBackground(tall);
        Assert.True(vm.HasCropTarget);
        vm.LcdShowTelemetry = false;
        Assert.False(client.Config.Lcd.ShowTelemetry);
    }
}
