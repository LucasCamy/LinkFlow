using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace LinkFlow.Tests;

/// <summary>Respostas do hub: só aceitas com status ok e do tipo esperado (sem misturar comandos).</summary>
public class HubResponseTests
{
    /// <summary>Monta uma resposta como o hub envia: [0]=Report ID, [4]=status, [5..6]=tipo, [7]=quantidade, registros [status, LSB, MSB].</summary>
    private static byte[] Response(byte[] dataType, byte status, params (byte Status, short Value)[] sensors)
    {
        var packet = new byte[LinkHubProtocol.PacketSizeOut];
        packet[LinkHubProtocol.StatusOffset] = status;
        dataType.CopyTo(packet, LinkHubProtocol.DataTypeOffset);
        packet[7] = (byte)sensors.Length;
        for (int i = 0; i < sensors.Length; i++)
        {
            packet[8 + i * 3] = sensors[i].Status;
            BitConverter.GetBytes(sensors[i].Value).CopyTo(packet, 9 + i * 3);
        }
        return packet;
    }

    [Fact]
    public void Speeds_are_read_per_channel_and_unavailable_slots_are_skipped()
    {
        // Registro 0 é o próprio hub; canal N = registro N
        var packet = Response(LinkHubProtocol.DataTypeSpeeds, 0x00, (0x01, 0), (0x00, 1200), (0x01, 900), (0x00, 2400));

        var speeds = LinkHubProtocol.ParseSpeeds(packet);

        Assert.NotNull(speeds);
        Assert.Equal(1200, speeds![1]);
        Assert.False(speeds.ContainsKey(2), "registro marcado como indisponível não pode virar RPM");
        Assert.Equal(2400, speeds[3]);
    }

    [Fact]
    public void Temperature_packet_is_never_read_as_speeds()
    {
        // Resposta atrasada de temperatura (35,0 °C = 350) chegando no lugar da leitura de RPM
        var temps = Response(LinkHubProtocol.DataTypeTemperatures, 0x00, (0x00, 350), (0x00, 350), (0x00, 350));

        Assert.Null(LinkHubProtocol.ParseSpeeds(temps));
        Assert.NotNull(LinkHubProtocol.ParseTemperatures(temps));
    }

    [Fact]
    public void Speeds_packet_is_never_read_as_temperatures()
    {
        // 1200 RPM lidos como temperatura seriam 120 °C: modo de segurança falso
        var speeds = Response(LinkHubProtocol.DataTypeSpeeds, 0x00, (0x01, 0), (0x00, 1200));
        Assert.Null(LinkHubProtocol.ParseTemperatures(speeds));
    }

    [Fact]
    public void Error_status_rejects_the_response()
    {
        var packet = Response(LinkHubProtocol.DataTypeSpeeds, LinkHubProtocol.StatusIncorrectMode, (0x01, 0), (0x00, 1200));
        Assert.Null(LinkHubProtocol.ParseSpeeds(packet));
        Assert.False(LinkHubProtocol.IsResponse(packet, LinkHubProtocol.DataTypeSpeeds));
    }

    [Fact]
    public void Truncated_packet_is_rejected()
    {
        Assert.Null(LinkHubProtocol.ParseSpeeds(new byte[6]));
        Assert.Null(LinkHubProtocol.ParseTemperatures(new byte[6]));
    }
}

/// <summary>O serviço (SYSTEM) só abre imagens da biblioteca e nunca caminhos de rede.</summary>
public sealed class MediaFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "LinkFlow-media-" + Guid.NewGuid().ToString("N"));
    private readonly string _oldShared = AppPaths.SharedDir;

    public MediaFilesTests()
    {
        AppPaths.SharedDir = Path.Combine(_dir, "shared");
        Directory.CreateDirectory(AppPaths.MediaDir);
    }

    public void Dispose()
    {
        AppPaths.SharedDir = _oldShared;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(@"\\servidor\compartilhado\foto.png")]
    [InlineData(@"//servidor/compartilhado/foto.png")]
    [InlineData(@"\\?\C:\Windows\win.ini")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"foto.png")]
    [InlineData(@"C:foto.png")]
    [InlineData(@"C:\fotos\foto.png:fluxo")]
    [InlineData("")]
    public void Network_device_relative_and_stream_paths_are_rejected_even_outside_the_service(string path)
    {
        Assert.False(MediaFiles.IsAllowedPath(path, libraryOnly: false, out _, out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void Service_only_reads_files_from_the_media_library()
    {
        var inside = Path.Combine(AppPaths.MediaDir, "a.png");
        var outside = Path.Combine(_dir, "segredo.png");
        File.WriteAllBytes(inside, new byte[] { 1, 2, 3 });
        File.WriteAllBytes(outside, new byte[] { 4, 5, 6 });

        Assert.Equal(new byte[] { 1, 2, 3 }, MediaFiles.TryRead(inside, libraryOnly: true, out _));
        Assert.Null(MediaFiles.TryRead(outside, libraryOnly: true, out var reason));
        Assert.Contains("biblioteca", reason);
        // ".." não escapa da pasta
        Assert.Null(MediaFiles.TryRead(Path.Combine(AppPaths.MediaDir, "..", "..", "segredo.png"), libraryOnly: true, out _));
        // A interface (usuário) pode abrir arquivos locais quaisquer
        Assert.NotNull(MediaFiles.TryRead(outside, libraryOnly: false, out _));
    }

    [Fact]
    public void Hard_link_inside_the_library_is_rejected()
    {
        var outside = Path.Combine(_dir, "segredo.png");
        File.WriteAllBytes(outside, new byte[] { 7, 8, 9 });
        var link = Path.Combine(AppPaths.MediaDir, "link.png");
        Assert.True(CreateHardLink(link, outside, IntPtr.Zero), "não foi possível criar o hard link de teste");

        Assert.Null(MediaFiles.TryRead(link, libraryOnly: true, out var reason));
        Assert.Contains("link", reason);
    }

    [Fact]
    public void Junction_inside_the_library_is_resolved_and_rejected()
    {
        var outsideDir = Path.Combine(_dir, "fora");
        Directory.CreateDirectory(outsideDir);
        File.WriteAllBytes(Path.Combine(outsideDir, "segredo.png"), new byte[] { 1 });
        var junction = Path.Combine(AppPaths.MediaDir, "atalho");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{outsideDir}\"")
               { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!)
        {
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }

        var viaJunction = Path.Combine(junction, "segredo.png");
        Assert.True(MediaFiles.IsAllowedPath(viaJunction, libraryOnly: true, out _, out _), "o texto do caminho parece estar na biblioteca");
        Assert.Null(MediaFiles.TryRead(viaJunction, libraryOnly: true, out var reason));
        Assert.Contains("fora da biblioteca", reason);
    }

    [Fact]
    public void Composer_in_library_mode_ignores_images_outside_the_library()
    {
        var outside = Path.Combine(_dir, "fora.png");
        using (var bmp = new System.Drawing.Bitmap(8, 8)) bmp.Save(outside, System.Drawing.Imaging.ImageFormat.Png);
        var cfg = new LcdConfig { Mode = LcdMode.Media, Media = { new LcdMediaItem { Path = outside } } };

        using var service = new LcdComposer(libraryOnly: true);
        service.ComposeJpeg(cfg, new EngineSnapshot(), new RgbConfig());
        Assert.Contains("não encontrado", service.Status);

        using var preview = new LcdComposer();
        preview.ComposeJpeg(cfg, new EngineSnapshot(), new RgbConfig());
        Assert.DoesNotContain("não encontrado", preview.Status);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr security);
}

/// <summary>Configuração gravada com cópia de segurança e recuperada após arquivo cortado.</summary>
public sealed class JsonFileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "LinkFlow-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ \"globalPercent\": 7")]
    public void Truncated_config_falls_back_to_the_previous_good_copy(string damaged)
    {
        var store = new JsonFileStore<ControlConfig>(Path.Combine(_dir, "config.json"));
        Assert.True(store.Save(new ControlConfig { GlobalPercent = 41 }));
        Assert.True(store.Save(new ControlConfig { GlobalPercent = 77 }));
        Assert.True(File.Exists(store.BackupPath));

        File.WriteAllText(store.FilePath, damaged);
        var loaded = store.Load();

        Assert.True(store.LoadedFromFallback);
        Assert.Equal(41, loaded.GlobalPercent);
        Assert.True(File.Exists(store.FilePath + ".corrompido"));
    }

    [Fact]
    public void Missing_files_load_defaults_without_fallback_flag()
    {
        var store = new JsonFileStore<ControlConfig>(Path.Combine(_dir, "nada.json"));
        Assert.Equal(new ControlConfig().GlobalPercent, store.Load().GlobalPercent);
        Assert.False(store.LoadedFromFallback);
    }

    [Fact]
    public void Save_leaves_no_temporary_file()
    {
        var store = new JsonFileStore<ControlConfig>(Path.Combine(_dir, "config.json"));
        Assert.True(store.Save(new ControlConfig { GlobalPercent = 60 }));
        Assert.True(store.Save(new ControlConfig { GlobalPercent = 61 }));
        Assert.False(File.Exists(store.FilePath + ".tmp"));
        Assert.Equal(61, store.Load().GlobalPercent);
    }
}

/// <summary>ACL da pasta compartilhada. Precisa de administrador: sem privilégio, só confere que nada é aplicado pela metade.</summary>
public sealed class SharedDataSecurityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "LinkFlow-acl-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Harden_removes_planted_links_and_leaves_users_read_only()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "logs"));
        var target = Path.Combine(Path.GetTempPath(), "LinkFlow-acl-alvo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        try
        {
            using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(_dir, "planted")}\" \"{target}\"")
                   { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!)
                mklink.WaitForExit();

            if (!SharedDataSecurity.IsPrivileged)
            {
                // Sem administrador não dá para trocar o dono: a falha precisa aparecer (TryHarden registra no log)
                Assert.ThrowsAny<Exception>(() => SharedDataSecurity.Harden(_dir));
                return;
            }

            SharedDataSecurity.Harden(_dir);

            Assert.False(Directory.Exists(Path.Combine(_dir, "planted")), "a junção plantada deve ser removida");
            Assert.True(Directory.Exists(target), "o destino da junção nunca pode ser apagado");
            var acl = new DirectoryInfo(_dir).GetAccessControl();
            Assert.Equal(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), acl.GetOwner(typeof(SecurityIdentifier)));
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (!rule.IdentityReference.Equals(users) || rule.AccessControlType != AccessControlType.Allow) continue;
                Assert.Equal(0, (int)(rule.FileSystemRights & (FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories |
                                                               FileSystemRights.Delete | FileSystemRights.ChangePermissions)));
            }
        }
        finally
        {
            try { Directory.Delete(target, recursive: true); } catch { }
        }
    }
}

/// <summary>Modo de segurança quando a curva perde as temperaturas e quando uma fila crítica trava.</summary>
public class EngineFailSafeGapTests
{
    private static byte Speed(EngineFixture f, int ch) => f.Hw.Hub.LastSpeeds.TryGetValue(ch, out var p) ? p : (byte)0;

    private static void LoseAllTemperatures(EngineFixture f, bool lost)
    {
        f.Hw.Sensors.TemperaturesLost = lost;
        // Sem leitura do líquido: o sensor fica num canal que não é a bomba e fora da faixa plausível
        f.Hw.Hub.CoolantSensorChannel = lost ? 5 : 11;
        f.Hw.Hub.CoolantTemp = lost ? 0 : 32.5f;
    }

    [Fact]
    public void Curve_without_any_temperature_goes_to_full_speed_and_recovers()
    {
        using var f = new EngineFixture(new ControlConfig { Mode = FanControlMode.Curve, Curve = new FanCurveConfig { Smoothing = 1f } },
            o => o.SensorLossTimeout = TimeSpan.FromMilliseconds(250));
        f.Hw.Sensors.ForcedCpuTemp = 40;
        f.Hw.Sensors.ForcedGpuTemp = 40;
        f.Hw.Hub.CoolantTemp = 30;
        EngineFixture.WaitUntil(() => Speed(f, 1) == 30, because: "curva a 40°C = 30%");

        LoseAllTemperatures(f, true);
        EngineFixture.WaitUntil(() => Speed(f, 1) == 100, because: "sem temperatura a curva não pode congelar em 30%");
        var snap = f.Engine.GetSnapshot();
        Assert.True(snap.FailSafeActive);
        Assert.Contains(snap.Alerts, a => a.Key == "sensor-lost" && a.Severity == AlertSeverity.Critical);

        LoseAllTemperatures(f, false);
        f.Hw.Hub.CoolantTemp = 30;
        EngineFixture.WaitUntil(() => Speed(f, 1) == 30, because: "leituras voltaram: retoma a curva");
        Assert.DoesNotContain(f.Engine.GetSnapshot().Alerts, a => a.Key is "sensor-lost" or "failsafe");
    }

    [Fact]
    public void Fixed_speed_mode_does_not_react_to_missing_temperatures()
    {
        using var f = new EngineFixture(new ControlConfig { GlobalPercent = 45 },
            o => o.SensorLossTimeout = TimeSpan.FromMilliseconds(100));
        EngineFixture.WaitUntil(() => Speed(f, 1) == 45);

        LoseAllTemperatures(f, true);
        Thread.Sleep(500);

        Assert.Equal(45, Speed(f, 1));
        Assert.False(f.Engine.GetSnapshot().FailSafeActive);
    }

    [Fact]
    public void With_fail_safe_disabled_missing_temperatures_only_warn()
    {
        var cfg = new ControlConfig { Mode = FanControlMode.Curve, Curve = new FanCurveConfig { Smoothing = 1f } };
        cfg.Safety.FailSafeEnabled = false;
        using var f = new EngineFixture(cfg, o => o.SensorLossTimeout = TimeSpan.FromMilliseconds(150));
        f.Hw.Sensors.ForcedCpuTemp = 40;
        f.Hw.Sensors.ForcedGpuTemp = 40;
        f.Hw.Hub.CoolantTemp = 30;
        EngineFixture.WaitUntil(() => Speed(f, 1) == 30);

        LoseAllTemperatures(f, true);
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Alerts.Any(a => a.Key == "sensor-lost" && a.Severity == AlertSeverity.Warning),
            because: "aviso de sensor perdido");
        Assert.False(f.Engine.GetSnapshot().FailSafeActive);
    }

    [Fact]
    public async Task Stalled_hub_lane_returns_hub_to_hardware_mode_and_reports()
    {
        var stalled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var f = new EngineFixture(new ControlConfig { GlobalPercent = 40 }, o =>
        {
            o.LaneStallTimeout = TimeSpan.FromMilliseconds(300);
            o.LaneStalled = problem => stalled.TrySetResult(problem);
        });
        EngineFixture.WaitUntil(() => Speed(f, 1) == 40 && f.Hw.Hub.InSoftwareMode);
        Assert.False(stalled.Task.IsCompleted, "nenhuma fila travada no funcionamento normal");

        f.Hw.Hub.ReadGate.Reset(); // a leitura USB passa a não voltar nunca
        try
        {
            var problem = await stalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("hub", problem);
            Assert.False(f.Hw.Hub.InSoftwareMode, "o hub deve voltar à curva própria (modo hardware)");
            Assert.Contains(f.Engine.GetSnapshot().Alerts, a => a.Key == "engine-stalled" && a.Severity == AlertSeverity.Critical);
        }
        finally
        {
            f.Hw.Hub.ReadGate.Set();
        }
    }
}
