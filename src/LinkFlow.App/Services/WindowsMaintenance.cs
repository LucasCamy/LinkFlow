using System.Diagnostics;
using System.IO;
using LinkFlow.App.Resources;

namespace LinkFlow.App.Services;

internal sealed record WindowsMaintenanceVolume(string Letter, bool IsSystem)
{
    public override string ToString() => IsSystem ? string.Format(Strings.Win_Sistema2, Letter) : Letter;
}

internal sealed record WindowsMaintenanceTool(string Title, IReadOnlyList<WindowsMaintenanceOperation> Operations)
{
    public override string ToString() => Title;
}

internal sealed class WindowsMaintenanceOperation
{
    public static readonly WindowsMaintenanceOperation VerifyFiles = new(Strings.Win_VerificarArquivos, Strings.Win_ConsultaIntegridadeArquivosProtegidos, "sfc.exe", ["/verifyonly"]);
    public static readonly WindowsMaintenanceOperation RepairFiles = new(Strings.Win_RepararArquivos, Strings.Win_VerificaTentaRestaurarArquivos, "sfc.exe", ["/scannow"], isRepair: true);
    public static readonly WindowsMaintenanceOperation CheckImage = ImageOperation(Strings.Win_ConsultarEstadoImagem, Strings.Win_LeEstadoCorrupcaoJa, "/CheckHealth");
    public static readonly WindowsMaintenanceOperation ScanImage = ImageOperation(Strings.Win_ExaminarImagem, Strings.Win_ExaminaArmazenamentoComponentesWindows, "/ScanHealth");
    public static readonly WindowsMaintenanceOperation RestoreImage = ImageOperation(Strings.Win_RepararImagem, Strings.Win_TentaRestaurarImagemWindows, "/RestoreHealth", isRepair: true);
    public static readonly WindowsMaintenanceOperation CheckDisk = new(Strings.Win_VerificarVolume, Strings.Win_VerificaEstruturaSistemaArquivos, "chkdsk.exe", [], usesVolume: true);
    public static readonly WindowsMaintenanceOperation RepairDisk = new(Strings.Win_CorrigirErrosVolume, Strings.Win_TentaCorrigirErrosSistema, "chkdsk.exe", ["/f"], usesVolume: true, isRepair: true);
    public static readonly WindowsMaintenanceOperation DiskCleanup = new(Strings.Win_AbrirLimpezaDisco, Strings.Win_EscolhaItensConfirmeRemocao, "cleanmgr.exe", [], requiresElevation: false);

    public static IReadOnlyList<WindowsMaintenanceTool> Tools { get; } =
    [
        new(Strings.Win_ArquivosWindowsSFC, [VerifyFiles, RepairFiles]),
        new(Strings.Win_ImagemWindowsDISM, [CheckImage, ScanImage, RestoreImage]),
        new(Strings.Win_VerificacaoDiscoCHKDSK, [CheckDisk, RepairDisk]),
        new(Strings.Win_LimpezaDisco, [DiskCleanup])
    ];

    private readonly string _executable;
    private readonly string[] _options;

    private WindowsMaintenanceOperation(string title, string impact, string executable, string[] options,
        bool usesVolume = false, bool isRepair = false, bool requiresElevation = true)
    {
        Title = title;
        Impact = impact;
        _executable = executable;
        _options = options;
        UsesVolume = usesVolume;
        IsRepair = isRepair;
        RequiresElevation = requiresElevation;
    }

    public string Title { get; }
    public string Impact { get; }
    public bool UsesVolume { get; }
    public bool IsRepair { get; }
    public bool RequiresElevation { get; }
    public bool IsDiskCleanup => ReferenceEquals(this, DiskCleanup);
    public string? ResultsLocation => _executable switch
    {
        "sfc.exe" => string.Format(Strings.Win_SfcLog, WindowsLog("CBS", "CBS.log")),
        "dism.exe" => string.Format(Strings.Win_DismLog, WindowsLog("DISM", "dism.log")),
        "chkdsk.exe" => Strings.Win_ResultadosVisualizadorEventosLogs,
        _ => null
    };
    public override string ToString() => Title;

    private string ExecutablePath => Path.Combine(Environment.SystemDirectory, _executable);

    public string Preview(string? volume)
    {
        var arguments = Arguments(volume);
        return arguments.Length == 0 ? $"\"{ExecutablePath}\"" : $"\"{ExecutablePath}\" {string.Join(' ', arguments)}";
    }

    public Process? Start(string? volume) => Process.Start(CreateStartInfo(volume));

    private ProcessStartInfo CreateStartInfo(string? volume)
    {
        var arguments = Arguments(volume);
        if (UsesVolume && !IsReadyFixedVolume(new DriveInfo(arguments[0] + "\\")))
            throw new InvalidOperationException(Strings.Win_VolumeSelecionadoNaoEsta);

        string path = ExecutablePath;
        if (!File.Exists(path))
            throw new FileNotFoundException(Strings.Win_EstaFerramentaNaoEsta, path);

        var startInfo = new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.SystemDirectory,
            WindowStyle = ProcessWindowStyle.Normal
        };
        if (RequiresElevation && !AutoStartManager.IsElevated) startInfo.Verb = "runas";
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    public static IReadOnlyList<WindowsMaintenanceVolume> ReadVolumes()
    {
        string? systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        return DriveInfo.GetDrives()
            .Where(drive => drive.Name.Length == 3 && drive.Name[2] == '\\' && IsDriveLetter(drive.Name[..2]) && IsReadyFixedVolume(drive))
            .OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase)
            .Select(drive => new WindowsMaintenanceVolume(drive.Name[..2].ToUpperInvariant(),
                string.Equals(drive.Name, systemRoot, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private string[] Arguments(string? volume)
    {
        if (!UsesVolume) return _options;
        if (!IsDriveLetter(volume))
            throw new ArgumentException("Selecione um volume local pelo nome da unidade.", nameof(volume));
        return [volume!.ToUpperInvariant(), .. _options];
    }

    private static bool IsDriveLetter(string? value) => value is { Length: 2 } && char.IsAsciiLetter(value[0]) && value[1] == ':';

    private static bool IsReadyFixedVolume(DriveInfo drive)
    {
        try { return drive.DriveType == DriveType.Fixed && drive.IsReady; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static WindowsMaintenanceOperation ImageOperation(string title, string impact, string option, bool isRepair = false) =>
        new(title, impact, "dism.exe", ["/Online", "/NoRestart", "/Cleanup-Image", option], isRepair: isRepair);

    private static string WindowsLog(string folder, string file) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs", folder, file);
}
