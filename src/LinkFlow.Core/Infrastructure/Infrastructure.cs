using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinkFlow.Core;

public static class AppPaths
{
    /// <summary>LINKFLOW_DATA_DIR redireciona todos os dados (testes isolados).</summary>
    private static readonly string? DataOverride = Environment.GetEnvironmentVariable("LINKFLOW_DATA_DIR");

    /// <summary>Configuração compartilhada entre app e serviço (%ProgramData%\LinkFlow).</summary>
    public static string SharedDir { get; set; } = DataOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LinkFlow");

    /// <summary>Preferências da interface por usuário (%AppData%\LinkFlow).</summary>
    public static string UserDir { get; set; } = DataOverride != null ? Path.Combine(DataOverride, "user") :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LinkFlow");

    public static string ControlConfigFile => Path.Combine(SharedDir, "config.json");
    public static string LogsDir => Path.Combine(SharedDir, "logs");

    /// <summary>Imagens/GIFs do LCD (legíveis pelo serviço, que roda como SYSTEM).</summary>
    public static string MediaDir => Path.Combine(SharedDir, "media");
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

    /// <summary>Cópia da última versão boa, mantida a cada gravação.</summary>
    public string BackupPath => FilePath + ".bak";

    /// <summary>True quando o último <see cref="Load"/> encontrou o arquivo principal inválido.</summary>
    public bool LoadedFromFallback { get; private set; }

    /// <summary>True quando o arquivo principal estava inválido e a cópia <see cref="BackupPath"/> foi usada.</summary>
    public bool RecoveredFromBackup { get; private set; }

    public T Load()
    {
        lock (_lock)
        {
            LoadedFromFallback = false;
            RecoveredFromBackup = false;
            if (!File.Exists(FilePath) && !File.Exists(BackupPath)) return new T();
            if (TryRead(FilePath, out var value)) return value;

            // Arquivo vazio ou cortado (queda de energia, disco cheio): usa a cópia anterior
            LoadedFromFallback = true;
            if (File.Exists(FilePath))
            {
                try { File.Copy(FilePath, FilePath + ".corrompido", overwrite: true); } catch { }
            }
            if (TryRead(BackupPath, out var backup))
            {
                RecoveredFromBackup = true;
                Log.Warn($"Configuração inválida em {FilePath}: usando a cópia {BackupPath}.");
                return backup;
            }
            Log.Warn($"Configuração inválida em {FilePath} e sem cópia válida: usando padrão.");
            return new T();
        }
    }

    private static bool TryRead(string path, out T value)
    {
        value = null!;
        try
        {
            if (!File.Exists(path)) return false;
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return false;
            if (JsonSerializer.Deserialize<T>(json, Json.Options) is not { } parsed) return false;
            value = parsed;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Não foi possível ler {path}: {ex.Message}");
            return false;
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
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(JsonSerializer.Serialize(value, Json.Options));
                // WriteThrough + Flush(true): o conteúdo chega ao disco antes da troca de nome
                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(FilePath))
                {
                    try
                    {
                        File.Replace(tmp, FilePath, BackupPath, ignoreMetadataErrors: true);
                        return true;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                    {
                        Log.Debug($"File.Replace falhou ({ex.Message}); usando cópia e troca simples.");
                        try { File.Copy(FilePath, BackupPath, overwrite: true); } catch { }
                    }
                }
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
            // Um arquivo por origem (app/serviço): o serviço roda como SYSTEM e a interface como usuário
            var name = $"linkflow-{_source ?? "core"}-{DateTime.Now:yyyy-MM-dd}.log";
            if (TryAppend(_logDir ??= AppPaths.LogsDir, name, line)) return;

            // Sem permissão na pasta compartilhada: usa a pasta local do usuário
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinkFlow", "logs");
            if (_logDir != fallback && TryAppend(fallback, name, line))
            {
                _logDir = fallback;
                return;
            }
            _fileDisabled = true;
        }
    }

    private static string? _logDir;

    /// <summary>Pasta onde este processo está gravando o log.</summary>
    public static string CurrentLogDir => _logDir ?? AppPaths.LogsDir;

    /// <summary>Tamanho máximo de um arquivo de log; ao passar, ele vira <c>.1.log</c> e recomeça.</summary>
    public static long MaxFileBytes { get; set; } = 20L * 1024 * 1024;

    // O arquivo fica aberto entre as linhas (antes abria e fechava a cada linha). Chamar com Lock adquirido.
    private static StreamWriter? _writer;
    private static string? _writerPath;

    private static bool TryAppend(string dir, string name, string line)
    {
        var path = Path.Combine(dir, name);
        try
        {
            if (_writer == null || _writerPath != path) OpenWriter(dir, path);
            var stream = _writer!.BaseStream;
            if (stream.Length >= MaxFileBytes)
            {
                // Um laço de erros não enche o disco: guarda só o arquivo anterior
                CloseWriter();
                File.Move(path, Path.ChangeExtension(path, ".1.log"), overwrite: true);
                OpenWriter(dir, path);
                stream = _writer!.BaseStream;
            }
            // Outro processo da mesma origem (ex.: segunda instância avisando a primeira) pode ter escrito
            stream.Seek(0, SeekOrigin.End);
            _writer.Write(line);
            return true;
        }
        catch
        {
            CloseWriter();
            return false;
        }
    }

    private static void OpenWriter(string dir, string path)
    {
        CloseWriter();
        Directory.CreateDirectory(dir);
        // OpenOrCreate + Seek(End) antes de cada linha (Append proíbe voltar se outro processo rotacionou o arquivo)
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = true };
        _writerPath = path;
    }

    private static void CloseWriter()
    {
        try { _writer?.Dispose(); } catch { }
        _writer = null;
        _writerPath = null;
    }

    /// <summary>Fecha o arquivo de log (testes e encerramento). A próxima linha reabre.</summary>
    public static void Flush()
    {
        lock (Lock) CloseWriter();
    }

    public static void CleanupOldLogs(int keepDays = 7)
    {
        try
        {
            if (!Directory.Exists(AppPaths.LogsDir)) return;
            if (SharedDataSecurity.IsReparsePoint(AppPaths.LogsDir)) return;
            foreach (var f in Directory.GetFiles(AppPaths.LogsDir, "linkflow-*.log"))
            {
                // Nunca segue links: apagar pelo SYSTEM através de um link apagaria o destino
                if ((File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0) continue;
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-keepDays)) File.Delete(f);
            }
        }
        catch { }
    }
}

