using System.Text.Json;

namespace LinkFlow.Core;

/// <summary>Partes da configuração que podem ser importadas separadamente.</summary>
[Flags]
public enum BackupSections
{
    None = 0,
    Profiles = 1,
    Control = 2,
    Fans = 4,
    Lighting = 8,
    Lcd = 16,
    Safety = 32,
    Ui = 64,
    All = Profiles | Control | Fans | Lighting | Lcd | Safety | Ui
}

/// <summary>
/// Arquivo de backup do LinkFlow (.linkflow): configuração completa, preferências da interface
/// e as imagens/GIFs do LCD embutidas, para levar tudo para outro PC ou reinstalação.
/// </summary>
public sealed class ConfigBackup
{
    public const string FormatId = "linkflow-backup";
    public const string Extension = ".linkflow";
    private const long MaxMediaFileBytes = 40L * 1024 * 1024;
    private const long MaxMediaTotalBytes = 120L * 1024 * 1024;

    // Sem valor padrão: um JSON qualquer (sem "format") não pode passar por backup válido
    public string Format { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    public DateTime ExportedAt { get; set; } = DateTime.Now;
    public string? AppVersion { get; set; }
    public string? Machine { get; set; }
    public ControlConfig Config { get; set; } = null!;
    public JsonElement? Ui { get; set; }

    /// <summary>Mídias do LCD: nome no backup → conteúdo em base64.</summary>
    public Dictionary<string, string> Media { get; set; } = new();

    /// <summary>Caminho original → nome no backup.</summary>
    public Dictionary<string, string> MediaMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static ConfigBackup Create(ControlConfig config, JsonElement? ui, string? appVersion, bool includeMedia = true)
    {
        var backup = new ConfigBackup
        {
            Format = FormatId,
            Config = Json.Clone(config),
            Ui = ui,
            AppVersion = appVersion,
            Machine = Environment.MachineName
        };
        if (!includeMedia) return backup;

        long total = 0;
        int index = 0;
        foreach (var path in ReferencedMedia(config.Lcd))
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxMediaFileBytes || total + info.Length > MaxMediaTotalBytes) continue;
                string name = $"{index++:D2}_{info.Name}";
                backup.Media[name] = Convert.ToBase64String(File.ReadAllBytes(path));
                backup.MediaMap[path] = name;
                total += info.Length;
            }
            catch (Exception ex)
            {
                Log.Warn($"Backup: mídia ignorada {path}: {ex.Message}");
            }
        }
        return backup;
    }

    public static IEnumerable<string> ReferencedMedia(LcdConfig lcd)
    {
        var paths = lcd.Media.Select(m => m.Path).ToList();
        if (!string.IsNullOrWhiteSpace(lcd.BackgroundPath)) paths.Add(lcd.BackgroundPath!);
        return paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json.Options));
    }

    /// <exception cref="InvalidDataException">Arquivo não é um backup do LinkFlow.</exception>
    public static ConfigBackup Load(string path)
    {
        ConfigBackup? backup;
        try
        {
            backup = JsonSerializer.Deserialize<ConfigBackup>(File.ReadAllText(path), Json.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("O arquivo não é um backup válido do LinkFlow.", ex);
        }
        if (backup == null || backup.Format != FormatId || backup.Config == null)
            throw new InvalidDataException("O arquivo não é um backup do LinkFlow.");
        if (backup.Version > 1)
            throw new InvalidDataException($"Backup criado por uma versão mais nova do LinkFlow (formato {backup.Version}).");
        backup.Config.Normalize();
        backup.Media ??= new();
        backup.MediaMap ??= new(StringComparer.OrdinalIgnoreCase);
        return backup;
    }

    /// <summary>Resumo de cada parte, para a tela de importação.</summary>
    public string Describe(BackupSections section)
    {
        var c = Config;
        int named = c.Fans.Values.Count(f => f.Name != null || f.Group != null);
        return section switch
        {
            BackupSections.Profiles => c.Profiles.Count == 0 ? "Nenhum perfil salvo" : string.Join(", ", c.Profiles.Select(p => p.Name)),
            BackupSections.Control => c.Mode == FanControlMode.Curve
                ? $"Curva automática ({c.Curve.Points.Count} pontos) • perfil {c.ActivePreset}"
                : $"Potência fixa {c.GlobalPercent}% • perfil {c.ActivePreset}",
            BackupSections.Fans => $"{c.Fans.Count} dispositivo(s) • {named} com nome ou grupo",
            BackupSections.Lighting => $"Cor {c.Rgb.Hex} • brilho {c.Rgb.Brightness}%",
            BackupSections.Lcd => c.Lcd.Mode == LcdMode.Media
                ? $"Imagens/GIFs ({c.Lcd.Media.Count}) • {Media.Count} arquivo(s) incluído(s)"
                : $"Métricas • {c.Lcd.SecondaryMetrics.Count + 1} informação(ões)",
            BackupSections.Safety => $"CPU {c.Safety.CpuCritical:0}° • GPU {c.Safety.GpuCritical:0}° • líquido {c.Safety.CoolantCritical:0}°",
            BackupSections.Ui => Ui == null ? "Não incluídas" : "Bandeja, atalhos e notificações",
            _ => string.Empty
        };
    }

    /// <summary>Grava as mídias embutidas na pasta indicada e devolve o mapa caminho original → novo caminho.</summary>
    public Dictionary<string, string> ExtractMedia(string targetDir)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Media.Count == 0) return result;
        Directory.CreateDirectory(targetDir);
        foreach (var (original, name) in MediaMap)
        {
            if (!Media.TryGetValue(name, out var b64)) continue;
            try
            {
                var bytes = Convert.FromBase64String(b64);
                var target = Path.Combine(targetDir, MediaLibrary.FileNameFor(bytes, Path.GetExtension(name)));
                if (!File.Exists(target)) File.WriteAllBytes(target, bytes);
                result[original] = target;
            }
            catch (Exception ex)
            {
                Log.Warn($"Importação: não foi possível gravar {name}: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>Aplica as partes escolhidas do backup sobre a configuração atual.</summary>
    public static void ApplyTo(ControlConfig target, ControlConfig source, BackupSections sections,
        IReadOnlyDictionary<string, string>? mediaMap = null)
    {
        var src = Json.Clone(source);

        if (sections.HasFlag(BackupSections.Profiles))
        {
            foreach (var p in src.Profiles)
            {
                target.Profiles.RemoveAll(x => x.Name == p.Name);
                target.Profiles.Add(p);
            }
        }

        if (sections.HasFlag(BackupSections.Control))
        {
            target.Mode = src.Mode;
            target.GlobalPercent = src.GlobalPercent;
            target.ActivePreset = src.ActivePreset;
            target.Curve = src.Curve;
            target.GpuFanAuto = src.GpuFanAuto;
            target.GpuFanPercent = src.GpuFanPercent;
            target.GroupCurves = src.GroupCurves;
            target.Automation = src.Automation;
        }

        if (sections.HasFlag(BackupSections.Fans))
        {
            foreach (var (key, f) in src.Fans) target.Fans[key] = f;
        }

        if (sections.HasFlag(BackupSections.Lighting)) target.Rgb = src.Rgb;
        if (sections.HasFlag(BackupSections.Safety)) target.Safety = src.Safety;

        if (sections.HasFlag(BackupSections.Lcd))
        {
            var lcd = src.Lcd;
            string Map(string p) => mediaMap != null && mediaMap.TryGetValue(p, out var n) ? n : p;
            foreach (var m in lcd.Media) m.Path = Map(m.Path);
            if (lcd.BackgroundPath != null) lcd.BackgroundPath = Map(lcd.BackgroundPath);
            target.Lcd = lcd;
        }

        target.Normalize();
    }
}

/// <summary>Biblioteca de imagens/GIFs do LCD em %ProgramData%\LinkFlow\media (nomes pelo conteúdo, sem duplicar).</summary>
public static class MediaLibrary
{
    public static readonly string[] SupportedExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    public static string FileNameFor(byte[] content, string extension)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content))[..16].ToLowerInvariant();
        return hash + extension.ToLowerInvariant();
    }

    /// <summary>
    /// Copia um arquivo para a biblioteca compartilhada (o serviço precisa conseguir ler).
    /// Se não houver permissão, usa a pasta do usuário; em último caso, o caminho original.
    /// </summary>
    public static string Import(string sourcePath, byte[]? convertedContent = null, string? convertedExtension = null)
    {
        var bytes = convertedContent ?? File.ReadAllBytes(sourcePath);
        var name = FileNameFor(bytes, convertedExtension ?? Path.GetExtension(sourcePath));
        foreach (var dir in new[] { AppPaths.MediaDir, Path.Combine(AppPaths.UserDir, "media") })
        {
            try
            {
                Directory.CreateDirectory(dir);
                var target = Path.Combine(dir, name);
                if (!File.Exists(target)) File.WriteAllBytes(target, bytes);
                return target;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                Log.Debug($"Biblioteca de mídia sem acesso em {dir}: {ex.Message}");
            }
        }
        return sourcePath;
    }
}
