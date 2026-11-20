using System.Globalization;
using LinkFlow.App.Resources;

namespace LinkFlow.App.Services;

public enum HardwareKind { Processor, Motherboard, Memory, Graphics, Storage, Network, Audio, Monitor, OperatingSystem, IcueLink, Peripherals }

/// <summary>Uma linha "rótulo → valor" da ficha técnica.</summary>
public sealed record SpecRow(string Label, string Value);

public sealed record SpecSection(string Title, IReadOnlyList<SpecRow> Rows);

/// <summary>
/// Um componente do inventário: o resumo aparece no cartão da tela Hardware e as seções completas no modal de detalhes.
/// </summary>
public sealed record HardwareComponent(HardwareKind Kind, string Name, IReadOnlyList<SpecRow> Summary, IReadOnlyList<SpecSection> Sections,
    string? Note = null)
{
    /// <summary>Quantas linhas do resumo o cartão mostra (as demais ficam no modal).</summary>
    public const int CardRows = 5;

    /// <summary>Texto puro (copiar para a área de transferência, pacote de diagnóstico).</summary>
    public string ToText(string title)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(title).Append(": ").AppendLine(Name);
        foreach (var section in Sections)
        {
            sb.Append("  ").AppendLine(section.Title);
            foreach (var row in section.Rows) sb.Append("    ").Append(row.Label).Append(": ").AppendLine(row.Value);
        }
        if (Note != null) sb.Append("  ").AppendLine(Note);
        return sb.ToString();
    }
}

internal sealed record CpuFacts(string Name, string? Manufacturer, string? Socket, int Sockets, int? Cores, int? Threads, uint? BaseMhz,
    string? Architecture, CpuSignature? Signature, IReadOnlyList<CacheLevel> Caches, uint? L2Kb, uint? L3Kb,
    IReadOnlyList<(InstructionGroup Group, IReadOnlyList<string> Names)> Instructions, bool? Virtualization);

internal sealed record BoardFacts(string? Manufacturer, string? Product, string? Version, string? Serial, string? SystemManufacturer,
    string? SystemModel, string? Chipset, string? BiosVendor, string? BiosVersion, DateOnly? BiosDate, string? SmbiosVersion, bool? Uefi,
    bool? SecureBoot);

internal sealed record MemoryModuleFacts(string? Locator, string? Bank, string? Manufacturer, string? PartNumber, ulong? Capacity,
    uint? SmbiosType, ushort? FormFactor, uint? ConfiguredMts, uint? RatedMts, uint? VoltageMv, uint? Ranks, ushort? DataWidth,
    ushort? TotalWidth);

internal sealed record MemoryFacts(IReadOnlyList<MemoryModuleFacts> Modules, int? Slots, ulong? MaxCapacityBytes);

internal sealed record GpuFacts(string Name, string? Compatibility, PciId? Pci, ulong? VramBytes, string? DriverVersion, DateOnly? DriverDate,
    uint? Width, uint? Height, uint? Refresh, uint? BitsPerPixel, PcieLink? Link);

internal sealed record DiskFacts(uint Number, string Name, ulong? Size, ushort? MediaType, ushort? Bus, string? Firmware, ushort? Health,
    uint? SpindleRpm, uint? LogicalSector, uint? PhysicalSector, ushort? PartitionStyle, bool? IsSystem, IReadOnlyList<string> Volumes,
    PcieLink? Link, NvmeHealth? Nvme, bool? FailurePredicted);

internal sealed record NetFacts(string Name, string? Connection, string? Manufacturer, string? Mac, ulong? SpeedBps, bool? Enabled,
    ushort? Status, uint? Medium, IReadOnlyList<string> Ipv4, IReadOnlyList<string> Ipv6, IReadOnlyList<string> Gateways, bool? Dhcp,
    DeviceDriverInfo? Driver, PciId? Pci, string? UsbId);

internal sealed record AudioFacts(string Name, string? Manufacturer, string? PnpId, string? Status, DeviceDriverInfo? Driver);

internal sealed record MonitorFacts(string? Name, string? VendorId, string? ProductCode, int? Week, int? Year, uint? Output, EdidInfo? Edid,
    ActiveDisplay? Display);

internal sealed record OsFacts(string? Caption, string? Version, string? Build, int? Ubr, string? DisplayVersion, string? Edition,
    string? Architecture, DateTime? Installed, DateTime? LastBoot);

internal sealed record PeripheralFacts(string Name, bool Keyboard, string? Manufacturer);

/// <summary>Monta os cartões e as fichas a partir do que foi lido (os textos saem no idioma da interface).</summary>
internal static class HardwareReport
{
    private static CultureInfo Ui => UiLanguage.Culture;

    private sealed class Rows : List<SpecRow>
    {
        /// <summary>Só entra se houver valor.</summary>
        public void Put(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) Add(new SpecRow(label, value.Trim()));
        }

        /// <summary>Dado importante: sem valor, aparece como indisponível.</summary>
        public void Always(string label, string? value) =>
            Add(new SpecRow(label, string.IsNullOrWhiteSpace(value) ? Strings.Hwi_Unavailable : value.Trim()));
    }

    private static string? Join(params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        return present.Length == 0 ? null : string.Join(" · ", present);
    }

    private static string? Spaced(params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()).ToArray();
        return present.Length == 0 ? null : string.Join(' ', present);
    }

    private static string? Glued(params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()).ToArray();
        return present.Length == 0 ? null : string.Concat(present);
    }

    private static string YesNo(bool value) => value ? Strings.Hwi_Yes : Strings.Hwi_No;
    private static string OnOff(bool value) => value ? Strings.Hwi_Enabled : Strings.Hwi_Disabled;
    private static string Date(DateOnly date) => date.ToString("d", Ui);
    private static string Count(UInt128 value) => ((decimal)value).ToString("N0", Ui);

    public static HardwareComponent Unavailable(HardwareKind kind) =>
        new(kind, Strings.Hwi_Unavailable, [], [], Strings.Hwi_ReadFailed);

    // ------------------------------------------------------------------ Processador

    public static HardwareComponent Processor(CpuFacts f)
    {
        string? coresThreads = f.Cores is { } c ? f.Threads is { } t ? $"{c} / {t}" : c.ToString(Ui) : null;
        string? socket = f.Socket == null ? null : f.Sockets > 1 ? $"{f.Sockets} × {f.Socket}" : f.Socket;
        string? baseClock = f.BaseMhz is > 0 and var mhz ? HardwareSpecs.Clock(mhz, Ui) : null;

        var main = new Rows();
        main.Put(Strings.Hwi_Name, f.Name);
        main.Put(Strings.Hwi_Manufacturer, HardwareSpecs.CpuVendor(f.Manufacturer));
        main.Always(Strings.Hwi_Socket, socket);
        main.Always(Strings.Hwi_CoresThreads, coresThreads);
        main.Always(Strings.Hwi_BaseClock, baseClock);
        main.Put(Strings.Hwi_Architecture, f.Architecture);
        main.Put(Strings.Hwi_Signature, f.Signature is { } sig ? $"{sig.Family:X}h / {sig.Model:X}h / {sig.Stepping}" : null);
        if (f.Virtualization is { } virt) main.Put(Strings.Hwi_Virtualization, OnOff(virt));

        var caches = new Rows();
        foreach (var group in f.Caches.GroupBy(cache => (cache.Level, cache.Kind)))
        {
            string label = group.Key switch
            {
                (1, CacheKind.Data) => Strings.Hwi_CacheL1Data,
                (1, CacheKind.Instruction) => Strings.Hwi_CacheL1Instruction,
                var (level, _) => $"L{level}"
            };
            caches.Put(label, string.Join(" + ", group.Select(cache =>
            {
                string size = $"{cache.Count} × {HardwareSpecs.Bytes(cache.SizeBytes, true, Ui)}";
                return cache.Associativity is > 0 and < 0xFF ? $"{size} · {string.Format(Ui, Strings.Hwi_CacheWays, cache.Associativity)}" : size;
            })));
        }
        if (caches.Count == 0)
        {
            if (f.L2Kb is > 0) caches.Put("L2", HardwareSpecs.Bytes(f.L2Kb.Value * 1024.0, true, Ui));
            if (f.L3Kb is > 0) caches.Put("L3", HardwareSpecs.Bytes(f.L3Kb.Value * 1024.0, true, Ui));
        }

        var instructions = new Rows();
        foreach (var (group, names) in f.Instructions)
            instructions.Put(group switch
            {
                InstructionGroup.Base => Strings.Hwi_InstrBase,
                InstructionGroup.Vector => Strings.Hwi_InstrVector,
                InstructionGroup.Crypto => Strings.Hwi_InstrCrypto,
                InstructionGroup.Other => Strings.Hwi_InstrOther,
                _ => Strings.Hwi_InstrVirtualization
            }, string.Join(", ", names));

        double l3 = f.Caches.Where(cache => cache.Level == 3).Sum(cache => (double)cache.SizeBytes * cache.Count);
        if (l3 == 0 && f.L3Kb is > 0) l3 = f.L3Kb.Value * 1024.0;
        var summary = new Rows();
        summary.Always(Strings.Hwi_CoresThreads, coresThreads);
        summary.Always(Strings.Hwi_Socket, socket);
        summary.Always(Strings.Hwi_BaseClock, baseClock);
        summary.Put(Strings.Hwi_CacheL3Label, l3 > 0 ? HardwareSpecs.Bytes(l3, true, Ui) : null);
        summary.Put(Strings.Hwi_Architecture, f.Architecture);

        var sections = new List<SpecSection> { new(Strings.Hwi_CardProcessor, main) };
        if (caches.Count > 0) sections.Add(new(Strings.Hwi_SectionCaches, caches));
        if (instructions.Count > 0) sections.Add(new(Strings.Hwi_SectionInstructions, instructions));
        return new HardwareComponent(HardwareKind.Processor, f.Name, summary, sections);
    }

    // ------------------------------------------------------------------ Placa-mãe e BIOS

    public static HardwareComponent Motherboard(BoardFacts f)
    {
        string? vendor = HardwareSpecs.ShortVendor(f.Manufacturer);
        string? product = HardwareSpecs.IsPlaceholder(f.Product) ? null : f.Product!.Trim();
        string name = Spaced(vendor, product) ?? Strings.Hwi_Unavailable;

        var board = new Rows();
        board.Put(Strings.Hwi_Manufacturer, HardwareSpecs.IsPlaceholder(f.Manufacturer) ? null : f.Manufacturer);
        board.Put(Strings.Hwi_Model, product);
        board.Put(Strings.Hwi_Revision, HardwareSpecs.IsPlaceholder(f.Version) ? null : f.Version);
        board.Always(Strings.Hwi_Chipset, f.Chipset);
        string? systemModel = HardwareSpecs.IsPlaceholder(f.SystemModel) ? null : f.SystemModel!.Trim();
        if (systemModel != null && (product == null || !product.Contains(systemModel, StringComparison.OrdinalIgnoreCase)))
            board.Put(Strings.Hwi_SystemModel, Spaced(HardwareSpecs.ShortVendor(f.SystemManufacturer), systemModel));
        board.Put(Strings.Hwi_SerialNumber, HardwareSpecs.IsPlaceholder(f.Serial) ? null : f.Serial);

        var bios = new Rows();
        bios.Put(Strings.Hwi_Manufacturer, HardwareSpecs.IsPlaceholder(f.BiosVendor) ? null : f.BiosVendor);
        bios.Always(Strings.Hwi_Version, f.BiosVersion);
        bios.Always(Strings.Hwi_Date, f.BiosDate is { } date ? Date(date) : null);
        bios.Put(Strings.Hwi_SmbiosVersion, f.SmbiosVersion);
        string? mode = f.Uefi switch { true => "UEFI", false => Strings.Hwi_FirmwareLegacy, _ => null };
        bios.Put(Strings.Hwi_FirmwareMode, mode);
        if (f.Uefi == true && f.SecureBoot is { } secure) bios.Put("Secure Boot", OnOff(secure));

        var summary = new Rows();
        summary.Put(Strings.Hwi_Manufacturer, vendor);
        summary.Put(Strings.Hwi_Chipset, f.Chipset);
        summary.Always("BIOS", Join(f.BiosVersion, f.BiosDate is { } d ? Date(d) : null));
        summary.Put(Strings.Hwi_Firmware, Join(mode, f.Uefi == true && f.SecureBoot is { } sb ? sb ? Strings.Hwi_SecureBootOn : Strings.Hwi_SecureBootOff : null));

        return new HardwareComponent(HardwareKind.Motherboard, name, summary,
            [new(Strings.Hwi_SectionBoard, board), new(Strings.Hwi_SectionBios, bios)]);
    }

    // ------------------------------------------------------------------ Memória

    public static HardwareComponent Memory(MemoryFacts f)
    {
        var modules = f.Modules;
        double total = modules.Sum(m => (double)(m.Capacity ?? 0));
        string? type = modules.Select(m => HardwareSpecs.MemoryType(m.SmbiosType)).Where(t => t != null).GroupBy(t => t)
            .OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        string? form = modules.Select(m => HardwareSpecs.MemoryFormFactor(m.FormFactor)).FirstOrDefault(t => t != null);
        string name = total > 0 ? Spaced(HardwareSpecs.Bytes(total, true, Ui), type)! : Strings.Hwi_Unavailable;

        string? speed = Distinct(modules.Select(m => m.ConfiguredMts is > 0 ? m.ConfiguredMts : m.RatedMts).Where(s => s is > 0)
            .Select(s => $"{s} MT/s"));
        string? layout = modules.Count == 0 ? null : string.Join(" + ", modules.Where(m => m.Capacity is > 0).GroupBy(m => m.Capacity!.Value)
            .OrderByDescending(g => g.Key).Select(g => $"{g.Count()} × {HardwareSpecs.Bytes(g.Key, true, Ui)}"));
        string? slots = f.Slots is > 0 and var n && n >= modules.Count ? string.Format(Ui, Strings.Hwi_SlotsValue, modules.Count, n) : null;
        var channels = modules.Select(m => HardwareSpecs.MemoryChannel(m.Bank, m.Locator)).Where(c => c != null).Distinct().Order().ToArray();
        string? channelText = channels.Length switch
        {
            0 => null,
            1 => $"{Strings.Hwi_ChannelSingle} ({channels[0]})",
            2 => $"{Strings.Hwi_ChannelDual} ({string.Join(", ", channels)})",
            _ => $"{string.Format(Ui, Strings.Hwi_ChannelCount, channels.Length)} ({string.Join(", ", channels)})"
        };
        string? partNumber = modules.Select(m => m.PartNumber?.Trim()).Distinct().Count() == 1 &&
                             !HardwareSpecs.IsPlaceholder(modules[0].PartNumber) ? modules[0].PartNumber!.Trim() : null;

        var overview = new Rows();
        overview.Always(Strings.Hwi_Total, total > 0 ? HardwareSpecs.Bytes(total, true, Ui) : null);
        overview.Always(Strings.Hwi_Type, Spaced(type, form));
        overview.Always(Strings.Hwi_Speed, speed);
        overview.Put(Strings.Hwi_Modules, layout);
        overview.Put(Strings.Hwi_Slots, slots);
        overview.Put(Strings.Hwi_Channels, channelText);
        overview.Put(Strings.Hwi_MaxCapacity, f.MaxCapacityBytes is > 0 and var max ? HardwareSpecs.Bytes(max, true, Ui) : null);

        var sections = new List<SpecSection> { new(Strings.Hwi_SectionOverview, overview) };
        int index = 0;
        foreach (var m in modules)
        {
            index++;
            var rows = new Rows();
            rows.Put(Strings.Hwi_Manufacturer, HardwareSpecs.MemoryVendor(m.Manufacturer, m.PartNumber));
            rows.Put(Strings.Hwi_PartNumber, HardwareSpecs.IsPlaceholder(m.PartNumber) ? null : m.PartNumber);
            rows.Put(Strings.Hwi_Capacity, m.Capacity is > 0 and var cap ? HardwareSpecs.Bytes(cap, true, Ui) : null);
            rows.Put(Strings.Hwi_Type, Spaced(HardwareSpecs.MemoryType(m.SmbiosType), HardwareSpecs.MemoryFormFactor(m.FormFactor)));
            rows.Put(Strings.Hwi_ConfiguredSpeed, m.ConfiguredMts is > 0 ? $"{m.ConfiguredMts} MT/s" : null);
            rows.Put(Strings.Hwi_RatedSpeed, m.RatedMts is > 0 ? $"{m.RatedMts} MT/s" : null);
            rows.Put(Strings.Hwi_Voltage, m.VoltageMv is > 0 and var mv ? $"{(mv / 1000.0).ToString("0.00", Ui)} V" : null);
            rows.Put(Strings.Hwi_Ranks, m.Ranks is > 0 ? m.Ranks.Value.ToString(Ui) : null);
            rows.Put(Strings.Hwi_DataWidth, m.DataWidth is > 0 ? $"{m.DataWidth} bits" : null);
            if (m.DataWidth is > 0 && m.TotalWidth is > 0) rows.Put("ECC", YesNo(m.TotalWidth > m.DataWidth));
            rows.Put(Strings.Hwi_Slot, m.Locator);
            rows.Put(Strings.Hwi_Bank, m.Bank);
            sections.Add(new(string.Format(Ui, Strings.Hwi_ModuleTitle, m.Locator?.Trim() is { Length: > 0 } loc ? loc : index.ToString(Ui)), rows));
        }

        var summary = new Rows();
        summary.Always(Strings.Hwi_Speed, speed);
        summary.Put(Strings.Hwi_Modules, layout);
        summary.Put(Strings.Hwi_Slots, slots);
        summary.Put(Strings.Hwi_Channels, channelText);
        summary.Put(Strings.Hwi_PartNumber, partNumber);
        return new HardwareComponent(HardwareKind.Memory, name, summary, sections);
    }

    private static string? Distinct(IEnumerable<string> values)
    {
        var list = values.Distinct().ToArray();
        return list.Length == 0 ? null : string.Join(" / ", list);
    }

    // ------------------------------------------------------------------ Placa de vídeo

    public static HardwareComponent Graphics(IReadOnlyList<GpuFacts> adapters)
    {
        if (adapters.Count == 0) return Unavailable(HardwareKind.Graphics);
        var primary = adapters.OrderByDescending(a => a.VramBytes ?? 0).First();
        var sections = new List<SpecSection>();
        SpecRow[]? primarySummary = null;
        foreach (var gpu in adapters.OrderByDescending(a => a == primary))
        {
            string? chip = HardwareSpecs.PciVendorName(gpu.Pci?.Vendor) ?? gpu.Compatibility;
            string? partner = HardwareSpecs.PciVendorName(gpu.Pci?.SubsystemVendor);
            string? vram = gpu.VramBytes is > 0 and var bytes ? HardwareSpecs.Bytes(bytes, true, Ui) : null;
            string? driver = gpu.DriverVersion == null ? null :
                gpu.Pci?.Vendor == "10DE" && HardwareSpecs.NvidiaDriver(gpu.DriverVersion) is { } nvidia ? $"{nvidia} ({gpu.DriverVersion})" : gpu.DriverVersion;
            string? mode = gpu.Width is > 0 && gpu.Height is > 0
                ? Join(HardwareSpecs.Resolution((int)gpu.Width.Value, (int)gpu.Height.Value),
                    gpu.Refresh is > 1 and <= 1000 ? $"{gpu.Refresh} Hz" : null, gpu.BitsPerPixel is > 0 ? $"{gpu.BitsPerPixel} bits" : null)
                : null;
            string? bus = Pcie(gpu.Link);

            var rows = new Rows();
            rows.Put(Strings.Hwi_ChipVendor, chip);
            if (partner != null && partner != chip) rows.Put(Strings.Hwi_BoardVendor, partner);
            rows.Always(Strings.Hwi_VideoMemory, vram);
            rows.Always("Driver", driver);
            rows.Put(Strings.Hwi_DriverDate, gpu.DriverDate is { } date ? Date(date) : null);
            rows.Put(Strings.Hwi_VideoMode, mode);
            rows.Put(Strings.Hwi_Bus, bus);
            rows.Put(Strings.Hwi_PciIds, gpu.Pci is { } pci ? HardwareSpecs.PciIds(pci, Strings.Hwi_PciSubsystem, Strings.Hwi_PciRevision) : null);
            sections.Add(new(gpu.Name, rows));

            if (gpu == primary)
            {
                var summary = new Rows();
                summary.Always(Strings.Hwi_VideoMemory, vram);
                summary.Always("Driver", driver);
                summary.Put(Strings.Hwi_VideoMode, gpu.Width is > 0 && gpu.Height is > 0
                    ? Join(HardwareSpecs.Resolution((int)gpu.Width.Value, (int)gpu.Height.Value), gpu.Refresh is > 1 and <= 1000 ? $"{gpu.Refresh} Hz" : null)
                    : null);
                summary.Put(Strings.Hwi_Bus, bus);
                primarySummary = [.. summary];
            }
        }
        var all = new Rows();
        all.AddRange(primarySummary ?? []);
        if (adapters.Count > 1) all.Put(Strings.Hwi_OtherAdapters, string.Join(", ", adapters.Where(a => a != primary).Select(a => a.Name)));
        return new HardwareComponent(HardwareKind.Graphics, primary.Name, all, sections);
    }

    /// <summary>"PCIe 4.0 x16" ou, com o link reduzido agora (economia de energia), "PCIe 4.0 x16 (agora PCIe 1.1 x16)".</summary>
    internal static string? Pcie(PcieLink? link)
    {
        if (link is not { } l) return null;
        string? max = HardwareSpecs.Pcie(l.MaxGen, l.MaxWidth);
        string? current = HardwareSpecs.Pcie(l.CurrentGen, l.CurrentWidth);
        if (max == null) return current;
        return current == null || current == max ? max : string.Format(Ui, Strings.Hwi_PcieNow, max, current);
    }

    // ------------------------------------------------------------------ Armazenamento

    public static HardwareComponent Storage(IReadOnlyList<DiskFacts> disks)
    {
        if (disks.Count == 0) return Unavailable(HardwareKind.Storage);
        double total = disks.Sum(d => (double)(d.Size ?? 0));
        string totalText = HardwareSpecs.Bytes(total, false, Ui);
        string name = disks.Count == 1 ? string.Format(Ui, Strings.Hwi_DiskCountOne, totalText) : string.Format(Ui, Strings.Hwi_DiskCount, disks.Count, totalText);

        var summary = new Rows();
        var sections = new List<SpecSection>();
        foreach (var d in disks.OrderBy(d => d.Number))
        {
            string? media = d.MediaType switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => null };
            string? bus = HardwareInventory.StorageBus(d.Bus);
            string? health = d.Health switch { 0 => Strings.Win_Saudavel, 1 => Strings.Win_Atencao, 2 => Strings.Win_Falha2, _ => null };
            string? smart = d.Nvme is { } nv ? nv.CriticalWarning == 0 ? Strings.Hwi_SmartOk : Strings.Hwi_SmartAlert :
                d.FailurePredicted is { } predicted ? predicted ? Strings.Hwi_SmartPredicted : Strings.Hwi_SmartNoPrediction : null;

            var rows = new Rows();
            rows.Put(Strings.Hwi_Capacity, d.Size is > 0 and var size ? HardwareSpecs.Bytes(size, false, Ui) : null);
            rows.Put(Strings.Hwi_MediaType, media);
            rows.Put(Strings.Hwi_Interface, Join(bus, Pcie(d.Link)));
            rows.Put(Strings.Hwi_Firmware, d.Firmware);
            rows.Always(Strings.Hwi_HealthWindows, health);
            rows.Always("S.M.A.R.T.", smart);
            if (d.Nvme is { } n)
            {
                rows.Put(Strings.Dash_Temperature, n.TemperatureC is { } t ? $"{t} °C" : null);
                rows.Put(Strings.Hwi_LifeUsed, $"{n.PercentUsed}%");
                if (n.AvailableSpare <= 100) rows.Put(Strings.Hwi_AvailableSpare, string.Format(Ui, Strings.Hwi_SpareValue, n.AvailableSpare, n.SpareThreshold));
                rows.Put(Strings.Hwi_PowerOnHours, $"{Count(n.PowerOnHours)} h");
                rows.Put(Strings.Hwi_DataWritten, HardwareSpecs.Bytes((double)n.DataUnitsWritten * NvmeHealth.DataUnitBytes, false, Ui));
                rows.Put(Strings.Hwi_DataRead, HardwareSpecs.Bytes((double)n.DataUnitsRead * NvmeHealth.DataUnitBytes, false, Ui));
                rows.Put(Strings.Hwi_PowerCycles, Count(n.PowerCycles));
                rows.Put(Strings.Hwi_UnsafeShutdowns, Count(n.UnsafeShutdowns));
                rows.Put(Strings.Hwi_MediaErrors, Count(n.MediaErrors));
                if (n.CriticalWarning != 0)
                    rows.Put(Strings.Hwi_CriticalWarning, string.Join(", ", WindowsDiskHealth.NvmeWarningLabels(n.CriticalWarning)
                        .Prepend(string.Format(Ui, Strings.Win_Aviso0x, n.CriticalWarning))));
            }
            if (d.MediaType == 3 && d.SpindleRpm is > 0 and < uint.MaxValue) rows.Put(Strings.Hwi_Rotation, $"{d.SpindleRpm.Value.ToString("N0", Ui)} RPM");
            if (d.LogicalSector is > 0 && d.PhysicalSector is > 0) rows.Put(Strings.Hwi_SectorSize, $"{d.LogicalSector} B / {d.PhysicalSector} B");
            rows.Put(Strings.Hwi_PartitionStyle, d.PartitionStyle switch { 1 => "MBR", 2 => "GPT", 0 => "RAW", _ => null });
            rows.Put(Strings.Hwi_Volumes, d.Volumes.Count > 0 ? string.Join(", ", d.Volumes) : null);
            if (d.IsSystem is { } system) rows.Put(Strings.Hwi_SystemDisk, YesNo(system));
            sections.Add(new(d.Name, rows));

            summary.Put(d.Name, Join(d.Size is > 0 and var s ? HardwareSpecs.Bytes(s, false, Ui) : null,
                Spaced(bus, media), health));
        }
        return new HardwareComponent(HardwareKind.Storage, name, summary, sections);
    }

    // ------------------------------------------------------------------ Rede

    public static HardwareComponent Network(IReadOnlyList<NetFacts> adapters)
    {
        if (adapters.Count == 0) return Unavailable(HardwareKind.Network);
        var summary = new Rows();
        var sections = new List<SpecSection>();
        static bool Connected(NetFacts a) => a.Enabled != false && a.Status == 2;
        foreach (var a in adapters.OrderByDescending(Connected))
        {
            string? medium = a.Medium switch
            {
                1 or 8 or 9 => "Wi-Fi",
                14 => "Ethernet",
                10 => "Bluetooth",
                _ when a.Name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
                       a.Name.Contains("Wireless", StringComparison.OrdinalIgnoreCase) => "Wi-Fi",
                _ when a.Name.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) ||
                       a.Name.Contains("GbE", StringComparison.OrdinalIgnoreCase) => "Ethernet",
                _ => null
            };
            string state = a.Enabled == false ? Strings.Hwi_Disabled : Connected(a) ? Strings.Hwi_StateConnected : Strings.Hwi_StateDisconnected;
            string? speed = Connected(a) && a.SpeedBps is > 0 and < long.MaxValue and var bps ? LinkSpeed(bps) : null;

            var rows = new Rows();
            rows.Put(Strings.Hwi_Connection, a.Connection);
            rows.Put(Strings.Hwi_Type, medium);
            rows.Put(Strings.Hwi_Status, state);
            rows.Put(Strings.Det_LinkSpeed, speed);
            rows.Put(Strings.Hwi_Mac, a.Mac);
            rows.Put("IPv4", a.Ipv4.Count > 0 ? string.Join(", ", a.Ipv4) : null);
            rows.Put("IPv6", a.Ipv6.Count > 0 ? string.Join(", ", a.Ipv6.Take(2)) : null);
            rows.Put(Strings.Hwi_Gateway, a.Gateways.Count > 0 ? string.Join(", ", a.Gateways) : null);
            if (a.Dhcp is { } dhcp && a.Ipv4.Count > 0) rows.Put("DHCP", YesNo(dhcp));
            rows.Put(Strings.Hwi_Manufacturer, a.Manufacturer);
            rows.Put("Driver", a.Driver?.Version);
            rows.Put(Strings.Hwi_DriverDate, a.Driver?.Date is { } date ? Date(date) : null);
            rows.Put(Strings.Hwi_PciIds, a.Pci is { } pci ? HardwareSpecs.PciIds(pci, Strings.Hwi_PciSubsystem, Strings.Hwi_PciRevision) : null);
            rows.Put(Strings.Hwi_UsbId, a.UsbId);
            sections.Add(new(a.Name, rows));
            summary.Put(a.Name, Join(medium, state, speed));
        }
        string name = adapters.Count == 1 ? adapters[0].Name : string.Format(Ui, Strings.Hwi_AdapterCount, adapters.Count);
        return new HardwareComponent(HardwareKind.Network, name, summary, sections);
    }

    internal static string LinkSpeed(ulong bps) => bps >= 1_000_000_000
        ? $"{(bps / 1e9).ToString("0.#", Ui)} Gbps"
        : $"{(bps / 1e6).ToString("0", Ui)} Mbps";

    // ------------------------------------------------------------------ Áudio

    public static HardwareComponent Audio(IReadOnlyList<AudioFacts> devices)
    {
        if (devices.Count == 0) return Unavailable(HardwareKind.Audio);
        var summary = new Rows();
        var sections = new List<SpecSection>();
        foreach (var a in devices)
        {
            string id = a.PnpId ?? string.Empty;
            string? bus = id switch
            {
                _ when id.StartsWith(@"HDAUDIO\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"INTELAUDIO\", StringComparison.OrdinalIgnoreCase) => "HD Audio",
                _ when id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) => "USB",
                _ when id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) => "PCI",
                _ when id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) => "Bluetooth",
                _ when id.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase) => Strings.Hwi_Virtual,
                _ => null
            };
            string? status = a.Status == null ? null : a.Status.Equals("OK", StringComparison.OrdinalIgnoreCase)
                ? Strings.Hwi_StatusOk : string.Format(Ui, Strings.Hwi_StatusProblem, a.Status);

            var rows = new Rows();
            rows.Put(Strings.Hwi_Manufacturer, a.Manufacturer?.Trim('(', ')'));
            rows.Put(Strings.Hwi_Bus, bus);
            rows.Put("Codec", HardwareSpecs.HdAudioCodec(a.PnpId));
            rows.Put(Strings.Hwi_UsbId, HardwareSpecs.UsbId(a.PnpId));
            rows.Put(Strings.Hwi_Status, status);
            rows.Put("Driver", a.Driver?.Version);
            rows.Put(Strings.Hwi_DriverDate, a.Driver?.Date is { } date ? Date(date) : null);
            rows.Put(Strings.Hwi_DriverProvider, a.Driver?.Provider);
            sections.Add(new(a.Name, rows));
            summary.Put(a.Name, Join(bus, a.Manufacturer?.Trim('(', ')')) ?? status);
        }
        return new HardwareComponent(HardwareKind.Audio, string.Format(Ui, Strings.Hwi_DeviceCount, devices.Count), summary, sections);
    }

    // ------------------------------------------------------------------ Monitores

    public static HardwareComponent Monitors(IReadOnlyList<MonitorFacts> monitors)
    {
        if (monitors.Count == 0) return Unavailable(HardwareKind.Monitor);
        var summary = new Rows();
        var sections = new List<SpecSection>();
        foreach (var m in monitors.OrderByDescending(m => m.Display?.Mode.Primary == true))
        {
            var e = m.Edid;
            string title = m.Name ?? e?.Name ?? Glued(m.VendorId, m.ProductCode) ?? Strings.Hw_MonitorConectado;
            string? connection = m.Output == 0x80000000 ? Strings.Hwi_ConnectionInternal : HardwareSpecs.VideoOutput(m.Output) ?? e?.Interface;
            string? native = e is { NativeWidth: > 0, NativeHeight: > 0 } ? HardwareSpecs.Resolution(e.NativeWidth.Value, e.NativeHeight.Value) : null;
            string? current = m.Display is { } display
                ? Join(HardwareSpecs.Resolution(display.Mode.Width, display.Mode.Height),
                    display.Mode.Refresh > 1 ? $"{display.Mode.Refresh} Hz" : null, display.Mode.BitsPerPixel > 0 ? $"{display.Mode.BitsPerPixel} bits" : null)
                : null;
            if (current != null && m.Display!.Mode.Primary) current = string.Format(Ui, Strings.Hwi_PrimaryValue, current);
            double? inches = HardwareSpecs.DiagonalInches(e?.WidthCm, e?.HeightCm);
            string? size = inches is { } i ? $"{i.ToString("0.#", Ui)}″ ({e!.WidthCm} × {e.HeightCm} cm)" : null;
            int? year = m.Year ?? e?.Year;
            int? week = m.Week ?? e?.Week;
            string? made = year == null ? null : week is > 0 ? string.Format(Ui, Strings.Hwi_ManufacturedValue, week, year) : year.Value.ToString(Ui);

            var rows = new Rows();
            rows.Put(Strings.Hwi_Manufacturer, HardwareSpecs.MonitorVendor(m.VendorId ?? e?.Manufacturer));
            rows.Put(Strings.Hwi_ProductCode, Glued(m.VendorId ?? e?.Manufacturer, m.ProductCode ?? e?.ProductCode));
            rows.Put(Strings.Hwi_Connection, connection);
            rows.Put(Strings.Hwi_NativeResolution, native);
            rows.Put(Strings.Hwi_CurrentMode, current);
            rows.Put(Strings.Hwi_RefreshRange, e is { MinVerticalHz: > 0, MaxVerticalHz: > 0 } ? $"{e.MinVerticalHz}–{e.MaxVerticalHz} Hz" : null);
            rows.Put(Strings.Hwi_ScreenSize, size);
            rows.Put(Strings.Hwi_ColorDepth, e?.BitsPerColor is { } bits ? string.Format(Ui, Strings.Hwi_ColorDepthValue, bits) : null);
            rows.Put(Strings.Hwi_Manufactured, made);
            rows.Put(Strings.Hwi_EdidVersion, e?.Version);
            rows.Put(Strings.Hwi_Gamma, e?.Gamma is { } gamma ? gamma.ToString("0.00", Ui) : null);
            rows.Put(Strings.Hwi_GraphicsAdapter, m.Display?.Adapter);
            sections.Add(new(title, rows));

            string? shortMode = m.Display is { } d ? Join(HardwareSpecs.Resolution(d.Mode.Width, d.Mode.Height), d.Mode.Refresh > 1 ? $"{d.Mode.Refresh} Hz" : null)
                : native;
            summary.Put(title, Join(shortMode, inches is { } inch ? $"{inch.ToString("0", Ui)}″" : null, connection) ?? Strings.Hwi_Unavailable);
        }
        string name = monitors.Count == 1 ? Strings.Hwi_MonitorCountOne : string.Format(Ui, Strings.Hwi_MonitorCount, monitors.Count);
        return new HardwareComponent(HardwareKind.Monitor, name, summary, sections);
    }

    // ------------------------------------------------------------------ Windows

    public static HardwareComponent OperatingSystem(OsFacts f, DateTime now)
    {
        string? caption = f.Caption?.Trim();
        if (caption?.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase) == true) caption = caption[10..];
        string name = caption ?? "Windows";
        string? build = f.Build == null ? null : f.Ubr is { } ubr ? $"{f.Build}.{ubr}" : f.Build;
        string? uptime = null;
        if (f.LastBoot is { } boot && now > boot)
        {
            var span = now - boot;
            uptime = string.Format(Ui, Strings.Hwi_UptimeValue, (int)span.TotalDays, span.Hours, span.Minutes);
        }

        var rows = new Rows();
        rows.Put(Strings.Hwi_Edition, caption ?? f.Edition);
        rows.Always(Strings.Hwi_Version, f.DisplayVersion);
        rows.Always(Strings.Hwi_Build, build);
        rows.Put(Strings.Hwi_Kernel, f.Version);
        rows.Put(Strings.Hwi_Architecture, f.Architecture);
        rows.Put(Strings.Hwi_Installed, f.Installed is { } installed ? installed.ToString("d", Ui) : null);
        rows.Put(Strings.Hwi_LastBoot, f.LastBoot is { } last ? last.ToString("g", Ui) : null);
        rows.Put(Strings.Hwi_Uptime, uptime);

        var summary = new Rows();
        summary.Always(Strings.Hwi_Version, f.DisplayVersion);
        summary.Always(Strings.Hwi_Build, build);
        summary.Put(Strings.Hwi_Architecture, f.Architecture);
        summary.Put(Strings.Hwi_Installed, f.Installed is { } inst ? inst.ToString("d", Ui) : null);
        return new HardwareComponent(HardwareKind.OperatingSystem, name, summary, [new(Strings.Hwi_CardOs, rows)]);
    }

    // ------------------------------------------------------------------ Periféricos

    public static HardwareComponent? Peripherals(IReadOnlyList<PeripheralFacts> devices)
    {
        if (devices.Count == 0) return null;
        var summary = new Rows();
        var rows = new Rows();
        foreach (var d in devices.OrderByDescending(d => d.Keyboard))
        {
            string kind = d.Keyboard ? Strings.Hw_Teclado : "Mouse";
            summary.Put(d.Name, kind);
            rows.Put(d.Name, Join(kind, d.Manufacturer));
        }
        return new HardwareComponent(HardwareKind.Peripherals, string.Format(Ui, Strings.Hwi_DeviceCount, devices.Count), summary,
            [new(Strings.Hwi_CardPeripherals, rows)]);
    }
}
