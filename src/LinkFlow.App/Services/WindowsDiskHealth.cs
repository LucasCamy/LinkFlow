using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using LinkFlow.App.Resources;

namespace LinkFlow.App.Services;

internal static class WindowsDiskHealth
{
    private const ushort NvmeBus = 17;
    private const uint StorageQueryProperty = 0x002D1400;
    private const uint StoragePredictFailure = 0x002D1100;
    private const uint DeviceProtocolProperty = 50;
    private const uint NvmeProtocol = 3;
    private const uint NvmeLogPage = 2;
    private const uint NvmeHealthLog = 2;
    private const int ProtocolOffset = 8;
    private const int ProtocolSize = 40;
    private const int DescriptorSize = ProtocolOffset + ProtocolSize;
    private const int HealthLogSize = 512;
    private const uint ShareReadWrite = 3;
    private const uint OpenExisting = 3;
    private static readonly (byte Mask, string Label)[] NvmeWarnings =
    [
        (1 << 0, Strings.Win_ReservaAbaixoLimite),
        (1 << 1, Strings.Win_TemperaturaForaFaixa),
        (1 << 2, Strings.Win_ConfiabilidadeDegradada),
        (1 << 3, Strings.Win_MidiaModoSomenteLeitura),
        (1 << 4, Strings.Win_ProtecaoMemoriaVolatilFalha),
        (1 << 5, Strings.Win_RegiaoMemoriaPersistenteProblema)
    ];

    private sealed record Disk(uint Number, string Name, ushort? Bus, ushort? Health);
    private sealed record SmartReading(string Status, string Details);

    public static WindowsDiagnosticResult Read()
    {
        var disks = HardwareInventory.Query("SELECT Number, FriendlyName, BusType, HealthStatus FROM MSFT_Disk", item =>
            new Disk((uint)item["Number"], item["FriendlyName"]?.ToString() ?? Strings.Win_Disco, item["BusType"] as ushort?,
                item["HealthStatus"] as ushort?), HardwareInventory.StorageScope).OrderBy(disk => disk.Number);
        var rows = new List<WindowsDiagnosticEntry>();
        int smartCount = 0;
        foreach (var disk in disks)
        {
            var smart = ReadSmart(disk);
            if (smart != null) smartCount++;
            string bus = HardwareInventory.StorageBus(disk.Bus) ?? Strings.Win_BarramentoIndisponivel;
            string health = disk.Health switch
            {
                0 => Strings.Win_Saudavel,
                1 => Strings.Win_Atencao,
                2 => Strings.Win_Falha2,
                _ => Strings.Win_Indisponivel
            };
            rows.Add(new(string.Format(Strings.Win_Disco2, disk.Number, disk.Name),
                smart?.Status ?? Strings.Win_Indisponivel2,
                string.Format(Strings.Win_SaudeWindows, bus, health) +
                (smart == null ? string.Empty : $"\n{smart.Details}")));
        }

        if (rows.Count == 0) return new(Strings.Win_NenhumDiscoFoiEncontrado, rows);
        return new(string.Format(Strings.Win_LeituraDisponivelDiscos, smartCount, rows.Count), rows,
            smartCount < rows.Count ? Strings.Win_DadosDisponiveisVariamConforme : null);
    }

    private static SmartReading? ReadSmart(Disk disk)
    {
        var health = ReadHealth(disk.Number, disk.Bus);
        if (health.Nvme is { } nvme) return Describe(nvme);
        if (health.FailurePredicted is { } predicted)
            return new(predicted ? Strings.Win_FalhaPrevista : Strings.Win_SemPrevisaoFalha, Strings.Win_PrevisaoReportadaPeloDispositivo);
        return null;
    }

    private static SmartReading Describe(NvmeHealth log)
    {
        var details = new List<string>();
        if (log.TemperatureC is { } celsius) details.Add($"{celsius} °C");
        details.Add(string.Format(Strings.Win_VidaUtilEstimadaUsada, log.PercentUsed));
        if (log.AvailableSpare <= 100) details.Add(string.Format(Strings.Win_ReservaDisponivel, log.AvailableSpare));
        details.Add(string.Format(Strings.Win_Operacao2, log.PowerOnHours));
        details.Add(string.Format(Strings.Win_ErrosMidia, log.MediaErrors));
        if (log.CriticalWarning != 0)
            details.Add(string.Join(", ", NvmeWarningLabels(log.CriticalWarning).Prepend(string.Format(Strings.Win_Aviso0x, log.CriticalWarning))));

        return new(log.CriticalWarning == 0 ? Strings.Win_NVMeSemAvisoCritico : Strings.Win_NVMeAlertaControlador,
            string.Join(" · ", details));
    }

    /// <summary>Avisos ligados no byte "Critical Warning" do NVMe, já traduzidos.</summary>
    internal static IEnumerable<string> NvmeWarningLabels(byte warning) =>
        NvmeWarnings.Where(flag => (warning & flag.Mask) != 0).Select(flag => flag.Label);

    /// <summary>
    /// Saúde do disco físico N sem administrador: a página de saúde do NVMe ou, nos demais, a previsão de falha do S.M.A.R.T.
    /// O que o dispositivo não responder volta como null.
    /// </summary>
    internal static (NvmeHealth? Nvme, bool? FailurePredicted) ReadHealth(uint number, ushort? bus)
    {
        // A consulta abre apenas os metadados do dispositivo, com acesso desejado zero.
        using var drive = CreateFile($@"\\.\PhysicalDrive{number}", 0, ShareReadWrite, 0, OpenExisting, 0, 0);
        if (drive.IsInvalid) return default;
        return bus == NvmeBus ? (ReadNvme(drive), null) : (null, ReadFailurePrediction(drive));
    }

    private static NvmeHealth? ReadNvme(SafeFileHandle drive)
    {
        var request = new byte[DescriptorSize + HealthLogSize];
        BinaryPrimitives.WriteUInt32LittleEndian(request, DeviceProtocolProperty);
        var protocol = request.AsSpan(ProtocolOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(protocol, NvmeProtocol);
        BinaryPrimitives.WriteUInt32LittleEndian(protocol[4..], NvmeLogPage);
        BinaryPrimitives.WriteUInt32LittleEndian(protocol[8..], NvmeHealthLog);
        BinaryPrimitives.WriteUInt32LittleEndian(protocol[16..], ProtocolSize);
        BinaryPrimitives.WriteUInt32LittleEndian(protocol[20..], HealthLogSize);

        var response = new byte[request.Length];
        if (!DeviceIoControl(drive, StorageQueryProperty, request, request.Length, response, response.Length, out int bytes, 0) ||
            bytes < DescriptorSize + HealthLogSize || bytes > response.Length ||
            BinaryPrimitives.ReadUInt32LittleEndian(response) != DescriptorSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(4)) != DescriptorSize) return null;

        protocol = response.AsSpan(ProtocolOffset);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(protocol[16..]);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(protocol[20..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(protocol) != NvmeProtocol ||
            BinaryPrimitives.ReadUInt32LittleEndian(protocol[4..]) != NvmeLogPage ||
            offset < ProtocolSize || offset > bytes - ProtocolOffset - HealthLogSize || length < HealthLogSize) return null;

        return HardwareSpecs.ParseNvmeHealth(response.AsSpan(ProtocolOffset + (int)offset, HealthLogSize));
    }

    private static bool? ReadFailurePrediction(SafeFileHandle drive)
    {
        var response = new byte[sizeof(uint) + 512];
        if (!DeviceIoControl(drive, StoragePredictFailure, null, 0, response, response.Length, out int bytes, 0) ||
            bytes < sizeof(uint)) return null;
        return BinaryPrimitives.ReadUInt32LittleEndian(response) != 0;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, [In] byte[]? input, int inputSize,
        [Out] byte[] output, int outputSize, out int bytes, nint overlapped);
}
