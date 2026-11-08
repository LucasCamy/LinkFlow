using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinkFlow.Core;

public static class AppPaths
{
    /// <summary>Configuração compartilhada entre app e serviço (%ProgramData%\LinkFlow).</summary>
    public static string SharedDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LinkFlow");

    /// <summary>Preferências da interface por usuário (%AppData%\LinkFlow).</summary>
    public static string UserDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LinkFlow");

    public static string ControlConfigFile => Path.Combine(SharedDir, "config.json");
    public static string LogsDir => Path.Combine(SharedDir, "logs");
    public static string UiSettingsFile => Path.Combine(UserDir, "ui.json");
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    public static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, Compact), Compact)!;
}

/// <summary>Armazena um objeto em JSON com escrita atômica (arquivo temporário + replace).</summary>
public sealed class JsonFileStore<T> where T : class, new()
{
    private readonly object _lock = new();
    public string FilePath { get; }

    public JsonFileStore(string filePath) => FilePath = filePath;

    public T Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    return JsonSerializer.Deserialize<T>(json, Json.Options) ?? new T();
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Configuração inválida em {FilePath}: {ex.Message}. Usando padrão.");
                try { File.Copy(FilePath, FilePath + ".corrompido", overwrite: true); } catch { }
            }
            return new T();
        }
    }

    public bool Save(T value)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json.Options), Encoding.UTF8);
                File.Move(tmp, FilePath, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"Falha ao salvar {FilePath}: {ex.Message}");
                return false;
            }
        }
    }
}

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>Log simples em arquivo (%ProgramData%\LinkFlow\logs) com rotação diária.</summary>
public static class Log
{
    private static readonly object Lock = new();
    private static string? _source;
    private static bool _fileDisabled;

    public static LogLevel MinLevel { get; set; } = LogLevel.Info;
    public static event Action<LogLevel, string>? Written;

    public static void Configure(string source, LogLevel minLevel = LogLevel.Info)
    {
        _source = source;
        MinLevel = minLevel;
    }

    public static void Debug(string msg) => Write(LogLevel.Debug, msg);
    public static void Info(string msg) => Write(LogLevel.Info, msg);
    public static void Warn(string msg) => Write(LogLevel.Warn, msg);
    public static void Error(string msg, Exception? ex = null) => Write(LogLevel.Error, ex == null ? msg : $"{msg}: {ex}");

    private static void Write(LogLevel level, string msg)
    {
        System.Diagnostics.Debug.WriteLine($"[{level}] {msg}");
        if (level < MinLevel) return;

        Written?.Invoke(level, msg);
        if (_fileDisabled) return;

        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level,-5}] [{_source ?? "core"}#{Environment.ProcessId}] {msg}{Environment.NewLine}";
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogsDir);
                var file = Path.Combine(AppPaths.LogsDir, $"linkflow-{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(file, line);
            }
            catch
            {
                _fileDisabled = true; // sem permissão: segue só com Debug
            }
        }
    }

    public static void CleanupOldLogs(int keepDays = 7)
    {
        try
        {
            if (!Directory.Exists(AppPaths.LogsDir)) return;
            foreach (var f in Directory.GetFiles(AppPaths.LogsDir, "linkflow-*.log"))
            {
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-keepDays)) File.Delete(f);
            }
        }
        catch { }
    }
}

public static class Throttle
{
    public static bool Elapsed(ref long lastTicks, TimeSpan interval)
    {
        long now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(lastTicks, now) < interval) return false;
        lastTicks = now;
        return true;
    }
}
