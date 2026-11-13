using System.Diagnostics;
using System.IO;

namespace LinkFlow.App.Services;

internal sealed record WindowsMaintenanceVolume(string Letter, bool IsSystem)
{
    public override string ToString() => IsSystem ? $"{Letter} · Sistema" : Letter;
}

internal sealed record WindowsMaintenanceTool(string Title, IReadOnlyList<WindowsMaintenanceOperation> Operations)
{
    public override string ToString() => Title;
}

internal sealed class WindowsMaintenanceOperation
{
    public static readonly WindowsMaintenanceOperation VerifyFiles = new("Verificar arquivos", "Consulta a integridade dos arquivos protegidos do Windows.", "sfc.exe", ["/verifyonly"]);
    public static readonly WindowsMaintenanceOperation RepairFiles = new("Reparar arquivos", "Verifica e tenta restaurar os arquivos protegidos do Windows.", "sfc.exe", ["/scannow"], isRepair: true);
    public static readonly WindowsMaintenanceOperation CheckImage = ImageOperation("Consultar estado da imagem", "Lê o estado de corrupção já registrado na imagem do Windows.", "/CheckHealth");
    public static readonly WindowsMaintenanceOperation ScanImage = ImageOperation("Examinar imagem", "Examina o armazenamento de componentes do Windows. A verificação pode demorar.", "/ScanHealth");
    public static readonly WindowsMaintenanceOperation RestoreImage = ImageOperation("Reparar imagem", "Tenta restaurar a imagem do Windows. O DISM pode buscar arquivos de reparo pelo Windows Update.", "/RestoreHealth", isRepair: true);
    public static readonly WindowsMaintenanceOperation CheckDisk = new("Verificar volume", "Verifica a estrutura do sistema de arquivos do volume selecionado.", "chkdsk.exe", [], usesVolume: true);
    public static readonly WindowsMaintenanceOperation RepairDisk = new("Corrigir erros no volume", "Tenta corrigir erros no sistema de arquivos. Se o volume estiver em uso, o Windows pode pedir para desmontá-lo ou agendar a correção.", "chkdsk.exe", ["/f"], usesVolume: true, isRepair: true);
    public static readonly WindowsMaintenanceOperation DiskCleanup = new("Abrir Limpeza de Disco", "Escolha os itens e confirme a remoção na janela da Limpeza de Disco do Windows.", "cleanmgr.exe", [], requiresElevation: false);

    public static IReadOnlyList<WindowsMaintenanceTool> Tools { get; } =
    [
        new("Arquivos do Windows (SFC)", [VerifyFiles, RepairFiles]),
        new("Imagem do Windows (DISM)", [CheckImage, ScanImage, RestoreImage]),
        new("Verificação de disco (CHKDSK)", [CheckDisk, RepairDisk]),
        new("Limpeza de Disco", [DiskCleanup])
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
        "sfc.exe" => $"Log do SFC em {WindowsLog("CBS", "CBS.log")}",
        "dism.exe" => $"Log do DISM em {WindowsLog("DISM", "dism.log")}",
        "chkdsk.exe" => "Resultados no Visualizador de Eventos, em Logs do Windows → Aplicativo, nas fontes Chkdsk e Wininit.",
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
            throw new InvalidOperationException("O volume selecionado não está mais disponível. Atualize a lista de volumes.");

        string path = ExecutablePath;
        if (!File.Exists(path))
            throw new FileNotFoundException("Esta ferramenta não está disponível nesta instalação do Windows.", path);

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
