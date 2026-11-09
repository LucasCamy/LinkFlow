using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LinkFlow.Core.Ipc;

/// <summary>
/// Protocolo entre a interface e o serviço: uma mensagem JSON por linha num named pipe local.
/// <code>{"t":"snapshot","d":{...}}</code>
/// </summary>
public static class PipeProtocol
{
    public const int Version = 1;
    public const int MaxMessageBytes = 1024 * 1024;

    /// <summary>LINKFLOW_PIPE permite isolar testes do serviço real.</summary>
    public static string PipeName { get; } = Environment.GetEnvironmentVariable("LINKFLOW_PIPE") ?? "LinkFlow.Engine.v1";

    // Servidor → cliente
    public const string Hello = "hello";
    public const string Snapshot = "snapshot";
    public const string Config = "config";
    public const string Alert = "alert";

    // Cliente → servidor
    public const string SetConfig = "setConfig";
    public const string LcdFrame = "lcd";
    public const string Resume = "resume";
    public const string Rescan = "rescan";
    public const string Demand = "demand";

    public sealed class HelloInfo
    {
        public int Protocol { get; set; } = Version;
        public string AppVersion { get; set; } = string.Empty;
        public bool Simulated { get; set; }
        public int ProcessId { get; set; }
    }

    public static string Encode(string type, object? payload)
    {
        var node = new JsonObject { ["t"] = type };
        if (payload != null) node["d"] = JsonSerializer.SerializeToNode(payload, payload.GetType(), Json.Compact);
        return node.ToJsonString(Json.Compact);
    }

    public static bool TryDecode(string line, out string type, out JsonNode? payload)
    {
        type = string.Empty;
        payload = null;
        try
        {
            var node = JsonNode.Parse(line);
            if (node?["t"]?.GetValue<string>() is not { } t) return false;
            type = t;
            payload = node["d"];
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static T? Read<T>(JsonNode? payload) => payload == null ? default : payload.Deserialize<T>(Json.Compact);

    /// <summary>Lê uma linha limitada a <see cref="MaxMessageBytes"/> (proteção contra mensagens gigantes).</summary>
    public static async Task<string?> ReadLineAsync(Stream stream, byte[] buffer, MemoryStream pending, CancellationToken ct)
    {
        while (true)
        {
            // Procura uma quebra de linha já recebida
            var data = pending.GetBuffer();
            int len = (int)pending.Length;
            int nl = Array.IndexOf(data, (byte)'\n', 0, len);
            if (nl >= 0)
            {
                var line = Encoding.UTF8.GetString(data, 0, nl).TrimEnd('\r');
                var rest = len - nl - 1;
                Buffer.BlockCopy(data, nl + 1, data, 0, rest);
                pending.SetLength(rest);
                return line;
            }

            if (len > MaxMessageBytes) throw new InvalidDataException("Mensagem muito grande no pipe.");

            int n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0) return null;
            pending.Write(buffer, 0, n);
        }
    }
}
