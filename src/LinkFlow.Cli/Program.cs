using System.Text;
using System.Text.Json;
using LinkFlow.Core;
using LinkFlow.Core.Ipc;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            if (args.Any(a => a is "--help" or "-h" or "help")) { Help(); return 0; }
            if (args.FirstOrDefault() == "fleet") return await FleetAsync(args);
            if (args.Contains("--diagnose") || args.FirstOrDefault() == "diagnose") return Diagnose();
            if (args.Length != 0 && args[0] != "status") throw new ArgumentException("Comando desconhecido. Use --help.");
            using var engine = PipeEngineClient.TryConnect(TimeSpan.FromSeconds(2));
            if (engine == null) throw new IOException("Serviço indisponível. O diagnóstico direto exige --diagnose e controle exclusivo do hardware.");
            Console.WriteLine(JsonSerializer.Serialize(engine.Snapshot, Json.Options));
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException or
                                       System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception or
                                       System.Security.Authentication.AuthenticationException or System.Net.Sockets.SocketException or
                                       OperationCanceledException or TimeoutException or FormatException or OverflowException or JsonException)
        { Console.Error.WriteLine(ex.Message); return 2; }
    }

    private static string? Option(string[] args, string name)
    {
        int index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Informe um valor para {name}.");
        return args[index + 1];
    }
    private static string Required(string[] args, string name) => Option(args, name) ?? throw new ArgumentException($"Informe {name}.");

    private static async Task<int> FleetAsync(string[] args)
    {
        string verb = args.ElementAtOrDefault(1) ?? "help";
        if (verb == "help") { Help(); return 0; }
        if (verb == "init")
        {
            if (!SharedDataSecurity.IsPrivileged) throw new UnauthorizedAccessException("O pareamento do serviço precisa ser executado como administrador.");
            int port = int.Parse(Option(args, "--port") ?? "47931", System.Globalization.CultureInfo.InvariantCulture);
            var pairing = FleetProvisioning.Create(FleetProvisioning.DefaultDirectory, Option(args, "--name") ?? Environment.MachineName,
                Option(args, "--bind") ?? "127.0.0.1", port, args.Contains("--allow-network"), args.Contains("--allow-control"));
            // Esta é a entrega explícita de segredos de pareamento, uma única vez. Nunca vai para o Log do serviço.
            Console.WriteLine(JsonSerializer.Serialize(pairing, Json.Options));
            Console.WriteLine("Configuração privada criada. Reinicie o serviço para ativar o agente. Guarde as credenciais com acesso restrito.");
            return 0;
        }
        if (verb == "add-peer")
        {
            string path = Path.GetFullPath(Required(args, "--peer"));
            if (File.Exists(path)) throw new IOException("O arquivo do par já existe; nenhuma credencial foi substituída.");
            string token = ReadToken();
            var peer = new FleetPeerSettings
            {
                Name = Option(args, "--name") ?? "LinkFlow", Host = Required(args, "--host"),
                Port = int.Parse(Option(args, "--port") ?? "47931", System.Globalization.CultureInfo.InvariantCulture),
                CertificateSha256 = Required(args, "--pin"), ProtectedToken = FleetSecretStore.ProtectToken(token)
            };
            _ = new FleetClient(peer, token); // valida endereço, impressão e tamanho antes de salvar
            string directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            if (SharedDataSecurity.IsReparsePoint(directory) || SharedDataSecurity.IsReparsePoint(path)) throw new IOException("A pasta do par foi redirecionada.");
            if (!new JsonFileStore<FleetPeerSettings>(path).Save(peer)) throw new IOException("Não foi possível salvar o par.");
            Console.WriteLine("Par salvo com credencial protegida pelo DPAPI deste usuário.");
            return 0;
        }
        var settings = ReadPeer(Required(args, "--peer"));
        var client = new FleetClient(settings, FleetSecretStore.UnprotectToken(settings.ProtectedToken));
        switch (verb)
        {
            case "monitor":
                Console.WriteLine(JsonSerializer.Serialize(await client.GetSnapshotAsync(), Json.Options));
                break;
            case "profile":
                string profile = Required(args, "--profile");
                var snapshot = await client.GetSnapshotAsync();
                var config = snapshot.Config.Clone();
                if (FanPresets.Find(profile) is { } preset) FanPresets.Apply(config, preset.Key);
                else if (config.Profiles.FirstOrDefault(p => p.Name == profile) is { } saved) saved.ApplyTo(config);
                else throw new ArgumentException("Perfil não encontrado no agente.");
                Console.WriteLine(JsonSerializer.Serialize(await client.SubmitCommandAsync(new()
                { Kind = PipeProtocol.SetConfig, ExpectedRevision = snapshot.Config.Revision, Config = config,
                    RequestedAtUtc = snapshot.CollectedAtUtc, ExpiresAtUtc = snapshot.CollectedAtUtc.AddSeconds(30) }), Json.Options));
                break;
            case "command":
                var request = JsonSerializer.Deserialize<EngineCommandRequest>(ReadBounded(Required(args, "--file")), Json.Options)
                    ?? throw new InvalidDataException("Comando ilegível.");
                Console.WriteLine(JsonSerializer.Serialize(await client.SubmitCommandAsync(request), Json.Options));
                break;
            case "history":
                int minutes = Math.Clamp(int.Parse(Option(args, "--minutes") ?? "60", System.Globalization.CultureInfo.InvariantCulture), 1, 1440 * 30);
                Console.WriteLine(JsonSerializer.Serialize(await client.QueryHistoryAsync(new()
                { MetricKey = Option(args, "--metric") ?? "cpu.temp", FromUtc = DateTimeOffset.UtcNow.AddMinutes(-minutes), ToUtc = DateTimeOffset.UtcNow }), Json.Options));
                break;
            default: throw new ArgumentException("Comando fleet desconhecido. Use --help.");
        }
        return 0;
    }

    private static FleetPeerSettings ReadPeer(string path) => JsonSerializer.Deserialize<FleetPeerSettings>(ReadBounded(path), Json.Options)
        ?? throw new InvalidDataException("Arquivo do par ilegível.");
    private static string ReadBounded(string path)
    {
        if (new FileInfo(path).Length > PipeProtocol.MaxMessageBytes) throw new InvalidDataException("Arquivo grande demais.");
        return File.ReadAllText(path);
    }
    private static string ReadToken()
    {
        if (Console.IsInputRedirected) return Console.ReadLine()?.Trim() ?? throw new ArgumentException("Credencial ausente na entrada padrão.");
        Console.Write("Credencial de pareamento (entrada oculta): ");
        var token = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (token.Length > 0) token.Length--; }
            else if (!char.IsControl(key.KeyChar) && token.Length < 256) token.Append(key.KeyChar);
        }
        Console.WriteLine();
        return token.ToString().Trim();
    }

    private static int Diagnose()
    {
        if (PipeIdentity.ServiceProcessId() != null)
            throw new InvalidOperationException("O serviço controla o hardware. Use status ou pare o serviço antes do diagnóstico direto.");
        using var ownership = HardwareControlLease.Acquire();
        var factory = new RealHardwareFactory();
        using var gpu = factory.CreateGpuRgb();
        using var board = factory.CreateMotherboardRgb();
        using var ram = factory.CreateRamRgb();
        using var peripherals = factory.CreatePeripheralsRgb();
        foreach (var device in new[] { gpu, board, ram, peripherals })
        {
            device.Initialize();
            Console.WriteLine($"{device.Name}: {(device.IsAvailable ? "disponível" : "indisponível")}");
        }
        return 0;
    }

    private static void Help() => Console.WriteLine("""
        LinkFlow.Cli status                         Lê o serviço local sem abrir hardware.
        LinkFlow.Cli --diagnose                     Diagnóstico direto, somente sem outro controlador.
        LinkFlow.Cli fleet init [--name PC] [--port 47931] [--bind 127.0.0.1] [--allow-network] [--allow-control]
          Execute init como administrador. O padrão permite apenas observação em loopback.
        LinkFlow.Cli fleet add-peer --peer par.json --host HOST --pin SHA256 [--port 47931] [--name PC]
          Digite a credencial na entrada oculta ou forneça-a pela entrada padrão; não use argumentos para segredos.
        LinkFlow.Cli fleet monitor --peer par.json
        LinkFlow.Cli fleet profile --peer par.json --profile Equilibrado
        LinkFlow.Cli fleet command --peer par.json --file comando.json
        LinkFlow.Cli fleet history --peer par.json [--metric cpu.temp] [--minutes 60]
        """);
}
