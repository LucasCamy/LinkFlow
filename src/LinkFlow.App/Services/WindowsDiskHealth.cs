using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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
        (1 << 0, "reserva abaixo do limite"),
        (1 << 1, "temperatura fora da faixa"),
        (1 << 2, "confiabilidade degradada"),
        (1 << 3, "mídia em modo somente leitura"),
        (1 << 4, "proteção da memória volátil com falha"),
        (1 << 5, "região de memória persistente com problema")
    ];

    private sealed record Disk(uint Number, string Name, ushort? Bus, ushort? Health);
    private sealed record SmartReading(string Status, string Details);

    public static WindowsDiagnosticResult Read()
    {
        var disks = HardwareInventory.Query("SELECT Number, FriendlyName, BusType, HealthStatus FROM MSFT_Disk", item =>
            new Disk((uint)item["Number"], item["FriendlyName"]?.ToString() ?? "Disco", item["BusType"] as ushort?,
                item["HealthStatus"] as ushort?), HardwareInventory.StorageScope).OrderBy(disk => disk.Number);
        var rows = new List<WindowsDiagnosticEntry>();
        int smartCount = 0;
        foreach (var disk in disks)
        {
            var smart = ReadSmart(disk);
            if (smart != null) smartCount++;
            string bus = HardwareInventory.StorageBus(disk.Bus) ?? "Barramento indisponível";
            string health = disk.Health switch
            {
                0 => "Saudável",
                1 => "Atenção",
                2 => "Com falha",
                _ => "Indisponível"
            };
            rows.Add(new($"Disco {disk.Number} · {disk.Name}",
                smart?.Status ?? "S.M.A.R.T. indisponível",
                $"{bus} · Saúde no Windows · {health}" +
                (smart == null ? string.Empty : $"\n{smart.Details}")));
        }

        if (rows.Count == 0) return new("Nenhum disco foi encontrado pelo Windows.", rows);
        return new($"Leitura S.M.A.R.T. disponível em {smartCount} de {rows.Count} discos.", rows,
            smartCount < rows.Count ? "Os dados disponíveis variam conforme o controlador, o driver e as permissões da conta." : null);
    }

    private static SmartReading? ReadSmart(Disk disk)
    {
        // A consulta abre apenas os metadados do dispositivo, com acesso desejado zero.
        using var drive = CreateFile($@"\\.\PhysicalDrive{disk.Number}", 0, ShareReadWrite, 0, OpenExisting, 0, 0);
        if (drive.IsInvalid) return null;
        return disk.Bus == NvmeBus ? ReadNvme(drive) : ReadFailurePrediction(drive);
    }

    private static SmartReading? ReadNvme(SafeFileHandle drive)
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

        var log = response.AsSpan(ProtocolOffset + (int)offset, HealthLogSize);
        var details = new List<string>();
        int kelvin = BinaryPrimitives.ReadUInt16LittleEndian(log[1..]);
        if (kelvin is > 0 and < ushort.MaxValue) details.Add($"{kelvin - 273.15:0} °C");
        details.Add($"{log[5]}% de vida útil estimada usada");
        if (log[3] <= 100) details.Add($"Reserva disponível {log[3]}%");
        details.Add($"{BinaryPrimitives.ReadUInt128LittleEndian(log[128..]):N0} h de operação");
        details.Add($"{BinaryPrimitives.ReadUInt128LittleEndian(log[160..]):N0} erros de mídia");
        byte warning = log[0];
        if (warning != 0)
        {
            var warnings = NvmeWarnings.Where(flag => (warning & flag.Mask) != 0).Select(flag => flag.Label);
            details.Add(string.Join(", ", warnings.Prepend($"Aviso 0x{warning:X2}")));
        }

        return new(warning == 0 ? "S.M.A.R.T. NVMe · Sem aviso crítico" : "S.M.A.R.T. NVMe · Alerta do controlador",
            string.Join(" · ", details));
    }

    private static SmartReading? ReadFailurePrediction(SafeFileHandle drive)
    {
        var response = new byte[sizeof(uint) + 512];
        if (!DeviceIoControl(drive, StoragePredictFailure, null, 0, response, response.Length, out int bytes, 0) ||
            bytes < sizeof(uint)) return null;

        bool predicted = BinaryPrimitives.ReadUInt32LittleEndian(response) != 0;
        return new(predicted ? "S.M.A.R.T. · Falha prevista" : "S.M.A.R.T. · Sem previsão de falha",
            "Previsão reportada pelo dispositivo.");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, [In] byte[]? input, int inputSize,
        [Out] byte[] output, int outputSize, out int bytes, nint overlapped);
}
