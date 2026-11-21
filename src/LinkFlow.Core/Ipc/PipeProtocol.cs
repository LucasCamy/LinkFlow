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
    public const int Version = 2;
    public const int MaxMessageBytes = 1024 * 1024;

    /// <summary>LINKFLOW_PIPE permite isolar testes do serviço real.</summary>
    public static string PipeName { get; } = Environment.GetEnvironmentVariable("LINKFLOW_PIPE") ?? DefaultPipeName;

    public const string DefaultPipeName = "LinkFlow.Engine.v1";

    // Servidor → cliente
    public const string Hello = "hello";
    public const string Snapshot = "snapshot";
    public const string Config = "config";
    public const string Alert = "alert";
    public const string Receipt = "receipt";
    public const string Command = "command";
    public const string ReadRequest = "readRequest";
    public const string ReadResponse = "readResponse";

    // Cliente → servidor
    public const string SetConfig = "setConfig";
    public const string LcdFrame = "lcd";
    public const string Resume = "resume";
    public const string RefreshRgb = "refreshRgb";
    public const string Rescan = "rescan";
    public const string Demand = "demand";
    public const string CurveTest = "curveTest";
    public const string CalibrationStart = "calibrationStart";
    public const string CalibrationCancel = "calibrationCancel";
    public const string SessionContext = "sessionContext";
    public const string AutomationOverride = "automationOverride";
    public const string ReportOperation = "reportOperation";

    public sealed class ReadRequestInfo
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Kind { get; set; } = string.Empty;
        public HistoryQuery? History { get; set; }
        public EventQuery? Events { get; set; }
        public string? BaselineSession { get; set; }
        public string? CandidateSession { get; set; }
    }

    public sealed class ReadResponseInfo
    {
        public string Id { get; set; } = string.Empty;
        public JsonNode? Data { get; set; }
        public string? Error { get; set; }
    }

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

    /// <summary>
    /// Separa tipo e conteúdo de uma linha. Linha que não é um objeto JSON com "t" de texto retorna false: antes
    /// <c>{"t":5}</c> ou <c>[1]</c> lançavam InvalidOperationException e derrubavam a conexão inteira.
    /// </summary>
    public static bool TryDecode(string line, out string type, out JsonNode? payload)
    {
        type = string.Empty;
        payload = null;
        try
        {
            if (JsonNode.Parse(line) is not JsonObject node) return false;
            if (node["t"] is not JsonValue t || !t.TryGetValue<string>(out var name) || name.Length == 0) return false;
            type = name;
            payload = node["d"];
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static T? Read<T>(JsonNode? payload) => payload == null ? default : payload.Deserialize<T>(Json.Compact);

    /// <summary>Como <see cref="Read{T}"/>, mas conteúdo do tipo errado (ex.: texto onde se espera um número) retorna false.</summary>
    public static bool TryRead<T>(JsonNode? payload, out T? value)
    {
        try
        {
            value = Read<T>(payload);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException or FormatException)
        {
            value = default;
            return false;
        }
    }

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
                if (nl > MaxMessageBytes) throw new InvalidDataException("Mensagem muito grande no pipe.");
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
