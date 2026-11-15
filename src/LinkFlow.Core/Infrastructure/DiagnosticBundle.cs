using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace LinkFlow.Core;

/// <summary>Uma pasta de logs a incluir no pacote (o rótulo vira a subpasta dentro do .zip).</summary>
public sealed record DiagnosticLogSource(string Label, string Directory);

/// <summary>Resultado de <see cref="DiagnosticBundle.Create"/>.</summary>
public sealed record DiagnosticBundleResult(string Path, int LogFiles, long Bytes);

/// <summary>
/// Pacote de diagnóstico para suporte e issues do GitHub: logs recentes, config.json e um resumo do hardware.
/// Nomes de usuário, pasta do perfil e nome do computador são trocados por marcadores antes de entrar no .zip.
/// </summary>
public static class DiagnosticBundle
{
    /// <summary>Logs modificados neste intervalo entram no pacote.</summary>
    public static TimeSpan LogAge { get; set; } = TimeSpan.FromDays(3);

    /// <summary>De cada log grande, só o final (as linhas mais recentes) entra no pacote.</summary>
    public const int MaxBytesPerLog = 8 * 1024 * 1024;

    public static string SuggestedFileName(DateTime now) => $"LinkFlow-Diagnostic-{now:yyyyMMdd-HHmm}.zip";

    /// <summary>
    /// Cria o .zip em <paramref name="zipPath"/>. Grava num temporário ao lado e troca no final, então uma falha
    /// não deixa um pacote pela metade. Os logs são lidos com compartilhamento total: o serviço e a interface
    /// continuam gravando neles enquanto isso, e nenhum handle fica aberto depois.
    /// </summary>
    public static DiagnosticBundleResult Create(string zipPath, IEnumerable<DiagnosticLogSource> logSources,
        ControlConfig? config, string hardwareSummary, DateTime? now = null)
    {
        var when = now ?? DateTime.Now;
        string full = Path.GetFullPath(zipPath);
        string dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        string tmp = Path.Combine(dir, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        int logs = 0;
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                AddText(zip, "LEIA-ME.txt",
                    $"Pacote de diagnóstico do LinkFlow gerado em {when.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}.{Environment.NewLine}" +
                    "Nome de usuário, pasta do perfil e nome do computador foram substituídos por <usuario>, <perfil> e <computador>." +
                    Environment.NewLine + "Revise o conteúdo antes de anexá-lo a uma issue pública.", when);
                AddText(zip, "hardware-summary.txt", Sanitize(hardwareSummary), when);
                if (config != null)
                    AddText(zip, "config.json", Sanitize(JsonSerializer.Serialize(config, Json.Options)), when);

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var source in logSources)
                {
                    if (!seen.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Directory)))) continue;
                    foreach (var file in RecentLogs(source.Directory, when))
                    {
                        if (TryReadTail(file, out var text))
                        {
                            AddText(zip, $"logs/{source.Label}/{Path.GetFileName(file)}", Sanitize(text), File.GetLastWriteTime(file));
                            logs++;
                        }
                    }
                }
            }
            File.Move(tmp, full, overwrite: true);
            return new DiagnosticBundleResult(full, logs, new FileInfo(full).Length);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private static IEnumerable<string> RecentLogs(string directory, DateTime now)
    {
        try
        {
            if (!Directory.Exists(directory) || SharedDataSecurity.IsReparsePoint(directory)) return [];
            return Directory.GetFiles(directory, "linkflow-*.log")
                .Where(f => (File.GetAttributes(f) & FileAttributes.ReparsePoint) == 0)
                .Where(f => now - File.GetLastWriteTime(f) <= LogAge)
                .OrderBy(f => File.GetLastWriteTime(f))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"Diagnóstico: não foi possível listar {directory}: {ex.Message}");
            return [];
        }
    }

    private static bool TryReadTail(string path, out string text)
    {
        text = string.Empty;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bool cut = stream.Length > MaxBytesPerLog;
            if (cut) stream.Seek(-MaxBytesPerLog, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
            if (cut)
            {
                // Começa numa linha inteira
                int nl = text.IndexOf('\n');
                text = "[... início do arquivo omitido ...]" + Environment.NewLine + (nl >= 0 ? text[(nl + 1)..] : text);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"Diagnóstico: não foi possível ler {path}: {ex.Message}");
            return false;
        }
    }

    private static void AddText(ZipArchive zip, string name, string text, DateTime modified)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        if (modified.Year >= 1980) entry.LastWriteTime = modified;
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(text);
    }

    /// <summary>Troca pasta do perfil, nome de usuário e nome do computador por marcadores.</summary>
    public static string Sanitize(string text) =>
        Sanitize(text, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName, Environment.MachineName);

    public static string Sanitize(string text, string? userProfile, string? userName, string? machineName)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text);
        if (!string.IsNullOrEmpty(userProfile))
        {
            // Também a forma escapada do JSON (C:\\Users\\nome)
            ReplaceIgnoreCase(sb, userProfile.Replace(@"\", @"\\"), "<perfil>");
            ReplaceIgnoreCase(sb, userProfile, "<perfil>");
        }
        // Nomes muito curtos apareceriam dentro de palavras comuns
        if (userName is { Length: >= 3 }) ReplaceWord(sb, userName, "<usuario>");
        if (machineName is { Length: >= 3 }) ReplaceWord(sb, machineName, "<computador>");
        return sb.ToString();
    }

    private static void ReplaceIgnoreCase(StringBuilder sb, string find, string with)
    {
        var s = sb.ToString();
        int i = s.IndexOf(find, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return;
        var result = new StringBuilder(s.Length);
        int last = 0;
        while (i >= 0)
        {
            result.Append(s, last, i - last).Append(with);
            last = i + find.Length;
            i = s.IndexOf(find, last, StringComparison.OrdinalIgnoreCase);
        }
        result.Append(s, last, s.Length - last);
        sb.Clear().Append(result);
    }

    private static void ReplaceWord(StringBuilder sb, string word, string with)
    {
        var s = sb.ToString();
        var result = new StringBuilder(s.Length);
        int last = 0, i = s.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        while (i >= 0)
        {
            int end = i + word.Length;
            bool boundary = (i == 0 || !char.IsLetterOrDigit(s[i - 1])) && (end >= s.Length || !char.IsLetterOrDigit(s[end]));
            if (boundary)
            {
                result.Append(s, last, i - last).Append(with);
                last = end;
            }
            i = s.IndexOf(word, end, StringComparison.OrdinalIgnoreCase);
        }
        result.Append(s, last, s.Length - last);
        sb.Clear().Append(result);
    }

    /// <summary>Resumo legível do hardware detectado (hardware-summary.txt).</summary>
    public static string DescribeHardware(EngineSnapshot s, string appVersion, string engineMode,
        IEnumerable<(string Category, string Name, string Details)>? inventory = null)
    {
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        string T(float? v) => v is { } x ? x.ToString("0.#", pt) + " °C" : "—";
        var b = new StringBuilder();
        b.AppendLine($"LinkFlow {appVersion} · {engineMode}{(s.IsSimulated ? " (simulação)" : string.Empty)}");
        b.AppendLine($"Windows {Environment.OSVersion.Version} · {(Environment.Is64BitOperatingSystem ? "x64" : "x86")} · .NET {Environment.Version}");
        b.AppendLine($"Gerado em {DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", pt)} · snapshot de {s.Timestamp.ToString("HH:mm:ss", pt)}");
        b.AppendLine();
        b.AppendLine("== iCUE LINK Hub ==");
        b.AppendLine($"Conectado: {(s.HubConnected ? "sim" : "não")}");
        b.AppendLine($"Firmware: {(string.IsNullOrWhiteSpace(s.HubFirmware) ? "—" : s.HubFirmware)}");
        b.AppendLine($"Status: {s.HubStatus}");
        b.AppendLine($"Modo de segurança: {(s.FailSafeActive ? $"ATIVO ({s.FailSafeReason ?? "sem motivo registrado"})" : "inativo")}");
        b.AppendLine();
        b.AppendLine("== Dispositivos no hub ==");
        if (s.Fans.Count == 0) b.AppendLine("(nenhum)");
        foreach (var f in s.Fans.OrderBy(f => f.Channel))
            b.AppendLine($"Canal {f.Channel,2}: {f.DefaultName} [{f.Model}]{(f.IsPump ? " (bomba)" : string.Empty)} · {f.Rpm} RPM · {f.AppliedPercent}% · {T(f.Temperature)}");
        b.AppendLine();
        b.AppendLine("== Sensores ==");
        b.AppendLine($"CPU: {s.CpuName ?? "—"} · {T(s.CpuTemp)}{(s.CpuSensorAvailable ? string.Empty : " (sensor indisponível)")}");
        b.AppendLine($"GPU: {s.GpuName ?? "—"} · {T(s.GpuTemp)}");
        b.AppendLine($"Líquido: {T(s.CoolantTemp)}");
        b.AppendLine($"RAM: {(s.RamTotalGb is { } ram ? ram.ToString("0.#", pt) + " GB" : "—")}");
        b.AppendLine();
        b.AppendLine("== Periféricos ==");
        b.AppendLine($"LCD: {(s.LcdConnected ? s.LcdStatus ?? "conectado" : "não conectado")}");
        b.AppendLine($"RGB da GPU: {(s.GpuRgbAvailable ? s.GpuRgbName : "não detectado")}");
        b.AppendLine($"RGB da placa-mãe: {(s.MotherboardRgbAvailable ? s.MotherboardRgbName : "não detectado")}");
        b.AppendLine($"RGB da memória: {(s.RamRgbAvailable ? s.RamRgbName : "não detectado")}");
        b.AppendLine($"Periféricos RGB: {(s.PeripheralsRgbAvailable ? s.PeripheralsRgbName : "não detectado")}");
        b.AppendLine();
        b.AppendLine("== Alertas ativos ==");
        if (s.Alerts.Count == 0) b.AppendLine("(nenhum)");
        foreach (var a in s.Alerts)
            b.AppendLine($"[{a.Severity}] {a.Title}: {a.Message} (desde {a.Since.ToString("dd/MM HH:mm:ss", pt)})");

        var parts = inventory?.ToList();
        if (parts is { Count: > 0 })
        {
            b.AppendLine();
            b.AppendLine("== Inventário do PC ==");
            foreach (var p in parts)
                b.AppendLine($"{p.Category}: {p.Name}{(string.IsNullOrWhiteSpace(p.Details) ? string.Empty : $" · {p.Details}")}");
        }
        return b.ToString();
    }
}
