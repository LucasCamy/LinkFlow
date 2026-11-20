using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security;
using LinkFlow.Core;
using Microsoft.Win32;

namespace LinkFlow.App.Services;

/// <summary>
/// Leitura do inventário (estilo CPU-Z / GPU-Z) só com o que um usuário comum pode ler: WMI, registro, CPUID e
/// propriedades de dispositivo. O que o Windows não informar fica de fora ou aparece como indisponível.
/// </summary>
internal static partial class HardwareInventory
{
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>Categoria em português (chave interna e texto do pacote de diagnóstico).</summary>
    public static string CategoryOf(HardwareKind kind) => kind switch
    {
        HardwareKind.Processor => "Processador",
        HardwareKind.Motherboard => "Placa-mãe",
        HardwareKind.Memory => "Memória",
        HardwareKind.Graphics => "Placa de vídeo",
        HardwareKind.Storage => "Armazenamento",
        HardwareKind.Network => "Rede",
        HardwareKind.Audio => "Áudio",
        HardwareKind.Monitor => "Monitor",
        HardwareKind.OperatingSystem => "Windows",
        HardwareKind.IcueLink => "iCUE LINK",
        _ => "Periféricos"
    };

    /// <param name="sensorGpuName">Placa que o engine mede: se o driver não informar a VRAM, vale a dos sensores.</param>
    public static HardwareInventoryResult Read(string? sensorGpuName = null, float? sensorVramGb = null)
    {
        var components = new List<HardwareComponent>();
        var unavailable = new List<HardwareKind>();
        var started = DateTime.UtcNow;

        void Add(HardwareKind kind, Func<HardwareComponent?> read)
        {
            try
            {
                if (read() is { } component) components.Add(component);
            }
            catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or SecurityException or
                                           InvalidOperationException)
            {
                unavailable.Add(kind);
                components.Add(HardwareReport.Unavailable(kind));
                Log.Warn($"Inventário de {CategoryOf(kind)}: {ex.Message}");
            }
        }

        Add(HardwareKind.Processor, () => HardwareReport.Processor(ReadProcessor()));
        Add(HardwareKind.Motherboard, () => HardwareReport.Motherboard(ReadBoard()));
        Add(HardwareKind.Memory, () => HardwareReport.Memory(ReadMemory()));
        Add(HardwareKind.Graphics, () => HardwareReport.Graphics(ReadGraphics(sensorGpuName, sensorVramGb)));
        Add(HardwareKind.Storage, () => HardwareReport.Storage(ReadDisks()));
        Add(HardwareKind.Network, () => HardwareReport.Network(ReadNetwork()));
        Add(HardwareKind.Audio, () => HardwareReport.Audio(ReadAudio()));
        Add(HardwareKind.Monitor, () => HardwareReport.Monitors(ReadMonitors()));
        Add(HardwareKind.OperatingSystem, () => HardwareReport.OperatingSystem(ReadOs(), DateTime.Now));
        Add(HardwareKind.Peripherals, () => HardwareReport.Peripherals(ReadPeripherals()));

        Log.Info($"Inventário de hardware lido em {(DateTime.UtcNow - started).TotalMilliseconds:0} ms " +
                 $"({components.Count} componentes, {unavailable.Count} indisponíveis).");
        return new HardwareInventoryResult(components, unavailable);
    }

    /// <summary>Consulta opcional: se o Windows recusar, segue sem esse dado.</summary>
    private static T[] TryQuery<T>(string query, Func<ManagementBaseObject, T> describe, string scope = @"root\cimv2")
    {
        try { return Query(query, describe, scope).ToArray(); }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            Log.Debug($"Inventário ({query.Split(" FROM ").LastOrDefault()}): {ex.Message}");
            return [];
        }
    }

    private static T? Number<T>(ManagementBaseObject item, string property) where T : struct =>
        item[property] is T value ? value : null;

    private static DateTime? WmiDateTime(ManagementBaseObject item, string property)
    {
        if (Text(item, property) is not { } value) return null;
        try { return ManagementDateTimeConverter.ToDateTime(value); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or ArgumentOutOfRangeException) { return null; }
    }

    private static string? Clean(string? value) => HardwareSpecs.IsPlaceholder(value) ? null : value!.Trim();

    private static object? RegistryValue(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or System.IO.IOException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ Processador

    private static CpuFacts ReadProcessor()
    {
        var cpus = Query("SELECT Name, Manufacturer, Caption, NumberOfCores, NumberOfLogicalProcessors, SocketDesignation, MaxClockSpeed, " +
                         "L2CacheSize, L3CacheSize, Architecture, VirtualizationFirmwareEnabled FROM Win32_Processor", item => new
        {
            Name = Text(item, "Name"),
            Manufacturer = Text(item, "Manufacturer"),
            Caption = Text(item, "Caption"),
            Cores = Number<uint>(item, "NumberOfCores"),
            Threads = Number<uint>(item, "NumberOfLogicalProcessors"),
            Socket = Clean(Text(item, "SocketDesignation")),
            MaxMhz = Number<uint>(item, "MaxClockSpeed"),
            L2 = Number<uint>(item, "L2CacheSize"),
            L3 = Number<uint>(item, "L3CacheSize"),
            Architecture = Number<ushort>(item, "Architecture"),
            Virtualization = item["VirtualizationFirmwareEnabled"] as bool?
        }).ToArray();
        if (cpus.Length == 0) throw new InvalidOperationException("Win32_Processor não retornou processadores.");

        var cpu = cpus[0];
        static int? Total(IEnumerable<uint?> values) => values.All(v => v == null) ? null : (int)values.Sum(v => (long)(v ?? 0));
        CpuSignature? signature = X86Base.IsSupported ? HardwareSpecs.Signature(X86Base.CpuId(1, 0).Eax) : HardwareSpecs.ParseCaption(cpu.Caption);
        var instructions = X86Base.IsSupported ? HardwareSpecs.InstructionSets((leaf, sub) => X86Base.CpuId(leaf, sub)) : [];
        return new CpuFacts(cpu.Name ?? Resources.Strings.Hwi_CardProcessor, cpu.Manufacturer, cpu.Socket, cpus.Length,
            Total(cpus.Select(c => c.Cores)), Total(cpus.Select(c => c.Threads)), cpu.MaxMhz,
            HardwareSpecs.Architecture(cpu.Architecture), signature, NativeDevices.ProcessorCaches(), cpu.L2, cpu.L3, instructions,
            cpu.Virtualization);
    }

    // ------------------------------------------------------------------ Placa-mãe e BIOS

    private static BoardFacts ReadBoard()
    {
        var board = TryQuery("SELECT Manufacturer, Product, Version, SerialNumber FROM Win32_BaseBoard",
            item => (Manufacturer: Text(item, "Manufacturer"), Product: Text(item, "Product"), Version: Text(item, "Version"),
                Serial: Text(item, "SerialNumber"))).FirstOrDefault();
        var system = TryQuery("SELECT Manufacturer, Model FROM Win32_ComputerSystem",
            item => (Manufacturer: Text(item, "Manufacturer"), Model: Text(item, "Model"))).FirstOrDefault();
        var bios = TryQuery("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SMBIOSMajorVersion, SMBIOSMinorVersion FROM Win32_BIOS",
            item => (Vendor: Text(item, "Manufacturer"), Version: Text(item, "SMBIOSBIOSVersion"), Date: WmiDate(item, "ReleaseDate"),
                Major: Number<ushort>(item, "SMBIOSMajorVersion"), Minor: Number<ushort>(item, "SMBIOSMinorVersion"))).FirstOrDefault();
        if (board == default && bios == default) throw new InvalidOperationException("Win32_BaseBoard e Win32_BIOS indisponíveis.");

        var pciNames = TryQuery("SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPClass = 'System'",
                item => (Name: Text(item, "Name"), Id: Text(item, "PNPDeviceID")))
            .Where(d => d.Id?.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) == true).Select(d => d.Name);
        bool? uefi = NativeDevices.IsUefi();
        bool? secureBoot = RegistryValue(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled") is int sb ? sb != 0 : null;
        string? smbios = bios.Major is { } major ? $"{major}.{bios.Minor ?? 0}" : null;
        return new BoardFacts(board.Manufacturer, board.Product, board.Version, board.Serial, system.Manufacturer, system.Model,
            HardwareSpecs.Chipset(pciNames), bios.Vendor, bios.Version, bios.Date, smbios, uefi, secureBoot);
    }

    // ------------------------------------------------------------------ Memória

    private static MemoryFacts ReadMemory()
    {
        var modules = Query("SELECT DeviceLocator, BankLabel, Manufacturer, PartNumber, Capacity, SMBIOSMemoryType, FormFactor, " +
                            "ConfiguredClockSpeed, Speed, ConfiguredVoltage, Attributes, DataWidth, TotalWidth FROM Win32_PhysicalMemory",
            item => new MemoryModuleFacts(Text(item, "DeviceLocator"), Text(item, "BankLabel"), Text(item, "Manufacturer"),
                Text(item, "PartNumber"), Number<ulong>(item, "Capacity"), Number<uint>(item, "SMBIOSMemoryType"),
                Number<ushort>(item, "FormFactor"), Number<uint>(item, "ConfiguredClockSpeed"), Number<uint>(item, "Speed"),
                Number<uint>(item, "ConfiguredVoltage"), Number<uint>(item, "Attributes"), Number<ushort>(item, "DataWidth"),
                Number<ushort>(item, "TotalWidth"))).ToArray();

        var arrays = TryQuery("SELECT MemoryDevices, MaxCapacity, MaxCapacityEx, Use FROM Win32_PhysicalMemoryArray",
            item => (Slots: Number<ushort>(item, "MemoryDevices"), MaxKb: Number<ulong>(item, "MaxCapacityEx") ?? Number<uint>(item, "MaxCapacity"),
                Use: Number<ushort>(item, "Use"))).Where(a => a.Use is null or 3).ToArray();
        int? slots = arrays.Length == 0 || arrays.All(a => a.Slots == null) ? null : arrays.Sum(a => a.Slots ?? 0);
        ulong? max = arrays.Length == 0 || arrays.All(a => a.MaxKb is null or 0) ? null : (ulong)arrays.Sum(a => (decimal)(a.MaxKb ?? 0)) * 1024;
        return new MemoryFacts(modules, slots, max);
    }

    // ------------------------------------------------------------------ Placa de vídeo

    private static IReadOnlyList<GpuFacts> ReadGraphics(string? sensorGpuName, float? sensorVramGb)
    {
        var adapters = Query("SELECT Name, AdapterCompatibility, AdapterRAM, DriverVersion, DriverDate, CurrentHorizontalResolution, " +
                             "CurrentVerticalResolution, CurrentRefreshRate, CurrentBitsPerPixel, PNPDeviceID FROM Win32_VideoController", item => new
        {
            Name = Text(item, "Name"),
            Compatibility = Text(item, "AdapterCompatibility"),
            Ram = Number<uint>(item, "AdapterRAM"),
            Driver = Text(item, "DriverVersion"),
            DriverDate = WmiDate(item, "DriverDate"),
            Width = Number<uint>(item, "CurrentHorizontalResolution"),
            Height = Number<uint>(item, "CurrentVerticalResolution"),
            Refresh = Number<uint>(item, "CurrentRefreshRate"),
            Bpp = Number<uint>(item, "CurrentBitsPerPixel"),
            Id = Text(item, "PNPDeviceID")
        }).Where(a => a.Name != null).ToArray();

        // Adaptadores de software (Área de Trabalho Remota, drivers básicos) só entram se não houver placa PCI
        var pci = adapters.Where(a => a.Id?.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) == true).ToArray();
        if (pci.Length > 0) adapters = pci;
        var memory = ReadVideoMemory();
        return adapters.Select(a =>
        {
            ulong? vram = memory.Where(m => a.Id != null && m.Match.Length > 0 && a.Id.StartsWith(m.Match, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.Match.Length).Select(m => (ulong?)m.Bytes).FirstOrDefault();
            if (vram == null && sensorVramGb is > 0 && string.Equals(a.Name, sensorGpuName, StringComparison.OrdinalIgnoreCase))
                vram = (ulong)(sensorVramGb.Value * (1UL << 30));
            if (vram == null && a.Ram is > 0 and < 0xFFF00000) vram = a.Ram;   // AdapterRAM satura em 4 GB
            return new GpuFacts(a.Name!, a.Compatibility, HardwareSpecs.ParsePci(a.Id), vram, a.Driver, a.DriverDate,
                a.Width, a.Height, a.Refresh, a.Bpp, NativeDevices.PcieLink(a.Id));
        }).ToArray();
    }

    /// <summary>Memória dedicada informada pelo driver de vídeo (HardwareInformation.qwMemorySize), legível sem administrador.</summary>
    private static IReadOnlyList<(string Match, ulong Bytes)> ReadVideoMemory()
    {
        var list = new List<(string, ulong)>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (root == null) return list;
            foreach (var name in root.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsAsciiDigit)))
            {
                try
                {
                    using var key = root.OpenSubKey(name);
                    if (key?.GetValue("MatchingDeviceId") is not string match) continue;
                    ulong? bytes = key.GetValue("HardwareInformation.qwMemorySize") switch
                    {
                        long q when q > 0 => (ulong)q,
                        _ => key.GetValue("HardwareInformation.MemorySize") switch
                        {
                            int d when d > 0 => (ulong)(uint)d,
                            byte[] { Length: >= 4 } b => BitConverter.ToUInt32(b),
                            _ => null
                        }
                    };
                    if (bytes is > 0) list.Add((match, bytes.Value));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or System.IO.IOException) { }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or System.IO.IOException)
        {
            Log.Debug($"Memória de vídeo no registro: {ex.Message}");
        }
        return list;
    }

    // ------------------------------------------------------------------ Armazenamento

    private static IReadOnlyList<DiskFacts> ReadDisks()
    {
        var physical = TryQuery("SELECT DeviceId, FriendlyName, MediaType, BusType, FirmwareVersion, Size, HealthStatus, SpindleSpeed, " +
                                "PhysicalSectorSize, LogicalSectorSize FROM MSFT_PhysicalDisk", item => new
        {
            Number = uint.TryParse(Text(item, "DeviceId"), out uint n) ? n : (uint?)null,
            Name = Text(item, "FriendlyName"),
            Media = Number<ushort>(item, "MediaType"),
            Bus = Number<ushort>(item, "BusType"),
            Firmware = Text(item, "FirmwareVersion"),
            Size = Number<ulong>(item, "Size"),
            Health = Number<ushort>(item, "HealthStatus"),
            Rpm = Number<uint>(item, "SpindleSpeed"),
            Physical = Number<ulong>(item, "PhysicalSectorSize"),
            Logical = Number<ulong>(item, "LogicalSectorSize")
        }, StorageScope).Where(d => d.Number != null).ToArray();

        var drives = TryQuery("SELECT Index, Model, Size, FirmwareRevision, PNPDeviceID FROM Win32_DiskDrive",
            item => (Index: Number<uint>(item, "Index"), Model: Text(item, "Model"), Size: Number<ulong>(item, "Size"),
                Firmware: Text(item, "FirmwareRevision"), Id: Text(item, "PNPDeviceID")));
        if (physical.Length == 0 && drives.Length == 0) throw new InvalidOperationException("Nenhum disco físico encontrado pelo WMI.");

        var disks = TryQuery("SELECT Number, PartitionStyle, IsSystem, IsBoot FROM MSFT_Disk",
                item => (Number: Number<uint>(item, "Number"), Style: Number<ushort>(item, "PartitionStyle"),
                    System: item["IsSystem"] as bool? == true || item["IsBoot"] as bool? == true), StorageScope)
            .Where(d => d.Number != null).ToDictionary(d => d.Number!.Value);
        var letters = TryQuery("SELECT DiskNumber, DriveLetter FROM MSFT_Partition", item =>
                (Disk: Number<uint>(item, "DiskNumber"), Letter: item["DriveLetter"] is char c && char.IsAsciiLetter(c) ? $"{c}:" : null), StorageScope)
            .Where(p => p.Disk != null && p.Letter != null).GroupBy(p => p.Disk!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(p => p.Letter!).Order().ToArray());
        var pnp = drives.Where(d => d.Index != null).GroupBy(d => d.Index!.Value).ToDictionary(g => g.Key, g => g.First());

        if (physical.Length == 0)
            return drives.Where(d => d.Index != null).OrderBy(d => d.Index).Select(d => new DiskFacts(d.Index!.Value,
                d.Model ?? Resources.Strings.Hw_DiscoFisico, d.Size, null, null, d.Firmware, null, null, null, null,
                disks.TryGetValue(d.Index.Value, out var info) ? info.Style : null, disks.TryGetValue(d.Index.Value, out var sys) ? sys.System : null,
                letters.GetValueOrDefault(d.Index.Value, []), null, null, null)).ToArray();

        return physical.OrderBy(d => d.Number).Select(d =>
        {
            uint number = d.Number!.Value;
            var (nvme, predicted) = WindowsDiskHealth.ReadHealth(number, d.Bus);
            PcieLink? link = d.Bus == 17 && pnp.TryGetValue(number, out var drive) ? NativeDevices.PcieLink(NativeDevices.Parent(drive.Id)) : null;
            disks.TryGetValue(number, out var info);
            return new DiskFacts(number, d.Name ?? Resources.Strings.Hw_DiscoFisico, d.Size, d.Media, d.Bus, d.Firmware, d.Health, d.Rpm,
                (uint?)d.Logical, (uint?)d.Physical, info.Style, info.Number == null ? null : info.System,
                letters.GetValueOrDefault(number, []), link, nvme, predicted);
        }).ToArray();
    }

    // ------------------------------------------------------------------ Rede

    private static IReadOnlyList<NetFacts> ReadNetwork()
    {
        var adapters = Query("SELECT Index, InterfaceIndex, Name, NetConnectionID, Manufacturer, MACAddress, Speed, NetEnabled, " +
                             "NetConnectionStatus, PNPDeviceID FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE", item => new
        {
            Index = Number<uint>(item, "Index"),
            Interface = Number<uint>(item, "InterfaceIndex"),
            Name = Text(item, "Name"),
            Connection = Text(item, "NetConnectionID"),
            Manufacturer = Text(item, "Manufacturer"),
            Mac = Text(item, "MACAddress"),
            Speed = Number<ulong>(item, "Speed"),
            Enabled = item["NetEnabled"] as bool?,
            Status = Number<ushort>(item, "NetConnectionStatus"),
            Id = Text(item, "PNPDeviceID")
        }).Where(a => a.Name != null).ToArray();

        // Placas de verdade (PCI/USB); túneis de VPN e a rede pessoal do Bluetooth também se dizem "físicos"
        var hardware = adapters.Where(a => a.Id is { } id && (id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) ||
                                                               id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase))).ToArray();
        if (hardware.Length > 0) adapters = hardware;

        var config = TryQuery("SELECT Index, IPAddress, DefaultIPGateway, DHCPEnabled FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE",
                item => (Index: Number<uint>(item, "Index"), Ips: item["IPAddress"] as string[] ?? [], Gateways: item["DefaultIPGateway"] as string[] ?? [],
                    Dhcp: item["DHCPEnabled"] as bool?))
            .Where(c => c.Index != null).GroupBy(c => c.Index!.Value).ToDictionary(g => g.Key, g => g.First());
        var media = TryQuery("SELECT InterfaceIndex, NdisPhysicalMedium FROM MSFT_NetAdapter",
                item => (Interface: Number<uint>(item, "InterfaceIndex"), Medium: Number<uint>(item, "NdisPhysicalMedium")), @"root\StandardCimv2")
            .Where(m => m.Interface != null).GroupBy(m => m.Interface!.Value).ToDictionary(g => g.Key, g => g.First().Medium);

        return adapters.Select(a =>
        {
            var c = a.Index is { } index && config.TryGetValue(index, out var found) ? found : default;
            var ips = c.Ips ?? [];
            return new NetFacts(a.Name!, a.Connection, a.Manufacturer, a.Mac, a.Speed, a.Enabled, a.Status,
                a.Interface is { } ifIndex ? media.GetValueOrDefault(ifIndex) : null,
                ips.Where(ip => ip.Contains('.')).ToArray(), ips.Where(ip => ip.Contains(':')).ToArray(), c.Gateways ?? [], c.Dhcp,
                NativeDevices.Driver(a.Id), HardwareSpecs.ParsePci(a.Id), HardwareSpecs.UsbId(a.Id));
        }).ToArray();
    }

    // ------------------------------------------------------------------ Áudio

    private static IReadOnlyList<AudioFacts> ReadAudio() =>
        Query("SELECT Name, Manufacturer, PNPDeviceID, Status FROM Win32_SoundDevice", item =>
                (Name: Text(item, "Name"), Manufacturer: Text(item, "Manufacturer"), Id: Text(item, "PNPDeviceID"), Status: Text(item, "Status")))
            .Where(a => a.Name != null)
            .Select(a => new AudioFacts(a.Name!, a.Manufacturer, a.Id, a.Status, NativeDevices.Driver(a.Id))).ToArray();

    // ------------------------------------------------------------------ Monitores

    private static IReadOnlyList<MonitorFacts> ReadMonitors()
    {
        var monitors = Query("SELECT InstanceName, ManufacturerName, UserFriendlyName, ProductCodeID, YearOfManufacture, WeekOfManufacture " +
                             "FROM WmiMonitorID WHERE Active = TRUE", item => new
        {
            Key = HardwareSpecs.MonitorKeyFromWmi(Text(item, "InstanceName")),
            Vendor = EdidText(item, "ManufacturerName"),
            Name = EdidText(item, "UserFriendlyName"),
            Product = EdidText(item, "ProductCodeID"),
            Year = Number<ushort>(item, "YearOfManufacture") is { } y and > 1990 ? (int?)y : null,
            Week = Number<byte>(item, "WeekOfManufacture") is { } w and > 0 and <= 54 ? (int?)w : null
        }, @"root\wmi").ToArray();

        var outputs = TryQuery("SELECT InstanceName, VideoOutputTechnology FROM WmiMonitorConnectionParams WHERE Active = TRUE",
                item => (Key: HardwareSpecs.MonitorKeyFromWmi(Text(item, "InstanceName")), Output: Number<uint>(item, "VideoOutputTechnology")), @"root\wmi")
            .Where(o => o.Key != null).GroupBy(o => o.Key!).ToDictionary(g => g.Key, g => g.First().Output);
        var displays = NativeDevices.ActiveDisplays().GroupBy(d => d.MonitorKey).ToDictionary(g => g.Key, g => g.First());

        return monitors.Select(m => new MonitorFacts(m.Name, m.Vendor, m.Product, m.Week, m.Year,
            m.Key != null ? outputs.GetValueOrDefault(m.Key) : null, m.Key != null ? ReadEdid(m.Key) : null,
            m.Key != null ? displays.GetValueOrDefault(m.Key) : null)).ToArray();
    }

    /// <summary>EDID que o Windows guarda em Enum\DISPLAY\…\Device Parameters (leitura liberada a usuários comuns).</summary>
    private static EdidInfo? ReadEdid(string monitorKey) =>
        RegistryValue($@"SYSTEM\CurrentControlSet\Enum\{monitorKey}\Device Parameters", "EDID") is byte[] edid ? HardwareSpecs.ParseEdid(edid) : null;

    // ------------------------------------------------------------------ Windows

    private static OsFacts ReadOs()
    {
        var os = Query("SELECT Caption, Version, BuildNumber, InstallDate, LastBootUpTime FROM Win32_OperatingSystem",
            item => (Caption: Text(item, "Caption"), Version: Text(item, "Version"), Build: Text(item, "BuildNumber"),
                Installed: WmiDateTime(item, "InstallDate"), Boot: WmiDateTime(item, "LastBootUpTime"))).FirstOrDefault();
        const string current = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        string? build = os.Build ?? RegistryValue(current, "CurrentBuildNumber") as string;
        int? ubr = build != null && RegistryValue(current, "CurrentBuildNumber") as string == build ? RegistryValue(current, "UBR") as int? : null;
        string architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "ARM64",
            Architecture.X86 => "x86",
            var other => other.ToString()
        };
        return new OsFacts(os.Caption, os.Version, build, ubr, RegistryValue(current, "DisplayVersion") as string,
            RegistryValue(current, "EditionID") as string, architecture, os.Installed, os.Boot);
    }

    // ------------------------------------------------------------------ Periféricos

    private static IReadOnlyList<PeripheralFacts> ReadPeripherals() =>
        TryQuery("SELECT Name, PNPClass, Manufacturer FROM Win32_PnPEntity WHERE Present = TRUE AND (PNPClass = 'Keyboard' OR PNPClass = 'Mouse')",
                item => (Name: Text(item, "Name"), Keyboard: Text(item, "PNPClass") == "Keyboard", Manufacturer: Text(item, "Manufacturer")))
            .Where(p => p.Name != null).DistinctBy(p => (p.Name, p.Keyboard))
            .Select(p => new PeripheralFacts(p.Name!, p.Keyboard, Clean(p.Manufacturer)?.Trim('(', ')'))).ToArray();
}
