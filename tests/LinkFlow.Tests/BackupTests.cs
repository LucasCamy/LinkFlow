using System.IO;
using System.Windows.Media;

namespace LinkFlow.Tests;

/// <summary>Exportar/importar configurações, perfis e mídias do LCD.</summary>
public sealed class BackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "linkflow-backup-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public BackupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private ControlConfig SampleConfig(string mediaPath)
    {
        var c = new ControlConfig { Mode = FanControlMode.Curve, GlobalPercent = 70, ActivePreset = "Jogo" };
        c.EditFan("AAA").Name = "Frontal 1";
        c.EditFan("AAA").Group = "Frontal";
        c.EditFan("BBB").Linked = false;
        c.EditFan("BBB").Percent = 90;
        c.Profiles.Add(new FanProfile { Name = "Jogo", GlobalPercent = 80 });
        c.Profiles.Add(new FanProfile { Name = "Noite", GlobalPercent = 35 });
        c.Rgb.R = 255; c.Rgb.G = 0; c.Rgb.B = 128;
        c.Lcd.Mode = LcdMode.Media;
        c.Lcd.Media.Add(new LcdMediaItem { Path = mediaPath, Name = "foto.png", DurationSeconds = 15 });
        c.Lcd.BackgroundPath = mediaPath;
        c.Safety.CpuCritical = 92;
        c.Normalize();
        return c;
    }

    [Fact]
    public void Export_and_import_everything_restores_config_and_media()
    {
        var media = LcdTests.CreateImage(_dir, "foto.png", 64, Colors.Teal);
        var source = SampleConfig(media);
        var file = Path.Combine(_dir, "backup" + ConfigBackup.Extension);
        ConfigBackup.Create(source, null, "2.1.0").Save(file);

        File.Delete(media); // simula outro PC: o arquivo original não existe mais
        var backup = ConfigBackup.Load(file);
        Assert.Single(backup.Media);

        var map = backup.ExtractMedia(Path.Combine(_dir, "imported-media"));
        var target = new ControlConfig();
        ConfigBackup.ApplyTo(target, backup.Config, BackupSections.All, map);

        Assert.Equal(FanControlMode.Curve, target.Mode);
        Assert.Equal("Frontal 1", target.GetFan("AAA").Name);
        Assert.False(target.GetFan("BBB").Linked);
        Assert.Equal(new[] { "Jogo", "Noite" }, target.Profiles.Select(p => p.Name));
        Assert.Equal("#FF0080", target.Rgb.Hex);
        Assert.Equal(92, target.Safety.CpuCritical);
        Assert.Equal(15, target.Lcd.Media[0].DurationSeconds);
        Assert.True(File.Exists(target.Lcd.Media[0].Path), "mídia extraída e caminho remapeado");
        Assert.Equal(target.Lcd.Media[0].Path, target.Lcd.BackgroundPath);
        Assert.NotEqual(media, target.Lcd.Media[0].Path);
    }

    [Fact]
    public void Importing_only_profiles_merges_by_name_and_keeps_everything_else()
    {
        var backup = ConfigBackup.Create(SampleConfig("x.png"), null, "2.1.0", includeMedia: false);
        var target = new ControlConfig { GlobalPercent = 40 };
        target.Profiles.Add(new FanProfile { Name = "Jogo", GlobalPercent = 10 });
        target.Profiles.Add(new FanProfile { Name = "Trabalho", GlobalPercent = 50 });
        target.EditFan("AAA").Name = "Meu nome";

        ConfigBackup.ApplyTo(target, backup.Config, BackupSections.Profiles);

        Assert.Equal(new[] { "Trabalho", "Jogo", "Noite" }, target.Profiles.Select(p => p.Name));
        Assert.Equal(80, target.Profiles.Single(p => p.Name == "Jogo").GlobalPercent);
        Assert.Equal(40, target.GlobalPercent);
        Assert.Equal("Meu nome", target.GetFan("AAA").Name);
        Assert.Equal(LcdMode.Metrics, target.Lcd.Mode);
    }

    [Fact]
    public void Invalid_files_are_rejected_with_a_clear_message()
    {
        var notJson = Path.Combine(_dir, "a.linkflow");
        File.WriteAllText(notJson, "isso não é json");
        Assert.Throws<InvalidDataException>(() => ConfigBackup.Load(notJson));

        var otherJson = Path.Combine(_dir, "b.json");
        File.WriteAllText(otherJson, "{\"globalPercent\": 50}");
        var ex = Assert.Throws<InvalidDataException>(() => ConfigBackup.Load(otherJson));
        Assert.Contains("LinkFlow", ex.Message);
    }

    [Fact]
    public void Describe_summarizes_each_section()
    {
        var backup = ConfigBackup.Create(SampleConfig("x.png"), null, "2.1.0", includeMedia: false);
        Assert.Equal("Jogo, Noite", backup.Describe(BackupSections.Profiles));
        Assert.Contains("2 dispositivo(s)", backup.Describe(BackupSections.Fans));
        Assert.Contains("#FF0080", backup.Describe(BackupSections.Lighting));
        Assert.Equal("Não incluídas", backup.Describe(BackupSections.Ui));
    }
}
