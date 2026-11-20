using LinkFlow.Core;

namespace LinkFlow.App.Services;

internal static partial class HardwareInventory
{
    /// <summary>Inventário plausível para o modo de simulação (demonstração e capturas de tela), coerente com os sensores simulados.</summary>
    public static HardwareInventoryResult Simulated(EngineSnapshot snapshot, DateTime now)
    {
        const uint KB = 1024, MB = 1024 * 1024;
        var caches = new CacheLevel[]
        {
            new(1, CacheKind.Data, 32 * KB, 8, 64, 12), new(1, CacheKind.Instruction, 32 * KB, 8, 64, 12),
            new(2, CacheKind.Unified, 1 * MB, 8, 64, 12), new(3, CacheKind.Unified, 96 * MB, 16, 64, 1), new(3, CacheKind.Unified, 32 * MB, 16, 64, 1)
        };
        (InstructionGroup, IReadOnlyList<string>)[] instructions =
        [
            (InstructionGroup.Base, ["MMX", "SSE", "SSE2", "SSE3", "SSSE3", "SSE4.1", "SSE4.2", "SSE4A", "x86-64"]),
            (InstructionGroup.Vector, ["AVX", "AVX2", "FMA3", "F16C", "AVX-512 (F, CD, BW, DQ, VL, IFMA, VBMI, VBMI2, VNNI, BITALG, VPOPCNTDQ, BF16)"]),
            (InstructionGroup.Crypto, ["AES", "VAES", "PCLMULQDQ", "SHA", "RDRAND", "RDSEED"]),
            (InstructionGroup.Other, ["POPCNT", "LZCNT", "BMI1", "BMI2", "ADX", "MOVBE", "CMPXCHG16B"]),
            (InstructionGroup.Virtualization, ["AMD-V"])
        ];
        var cpu = new CpuFacts(snapshot.CpuName ?? "AMD Ryzen 9 7900X3D", "AuthenticAMD", "AM5", 1, snapshot.CpuCores ?? 12,
            (snapshot.CpuCores ?? 12) * 2, 4400, "x64", new CpuSignature(0x19, 0x61, 2), caches, null, null, instructions, true);

        var board = new BoardFacts("ASUSTeK COMPUTER INC.", "ROG STRIX X670E-E GAMING WIFI", "Rev 1.xx", null, "ASUS", "System Product Name",
            "AMD X670E", "American Megatrends Inc.", "2611", new DateOnly(2025, 8, 12), "3.7", true, true);

        MemoryModuleFacts Module(string slot, string channel) => new(slot, $"P0 CHANNEL {channel}", "029E", "CMK64GX5M2B6000Z30",
            32UL << 30, 34, 8, 6000, 4800, 1350, 2, 64, 64);
        var memory = new MemoryFacts([Module("DIMM_A2", "A"), Module("DIMM_B2", "B")], 4, 192UL << 30);

        var gpu = new GpuFacts(snapshot.GpuName ?? "NVIDIA GeForce RTX 4080", "NVIDIA", new PciId("10DE", "2704", "1043", "88A0", "A1"),
            (ulong)((snapshot.GpuVramTotalGb ?? 16) * (1UL << 30)), "32.0.15.6094", new DateOnly(2024, 8, 20), 2560, 1440, 240, 32,
            new PcieLink(1, 16, 4, 16));

        static UInt128 Units(double terabytes) => (UInt128)(terabytes * 1e12 / NvmeHealth.DataUnitBytes);
        var disks = new DiskFacts[]
        {
            new(0, "Samsung SSD 990 PRO 2TB", 2_000_398_934_016, 4, 17, "4B2QJXD7", 0, 0, 512, 4096, 2, true, ["C:"], new PcieLink(4, 4, 4, 4),
                new NvmeHealth(0, 41, 100, 10, 3, Units(25.1), Units(18.4), 812, 3120, 21, 0), null),
            new(1, "WD Black SN850X 4TB", 4_000_787_030_016, 4, 17, "620361WD", 0, 0, 512, 512, 2, false, ["D:"], new PcieLink(4, 4, 4, 4),
                new NvmeHealth(0, 38, 100, 10, 1, Units(9.7), Units(6.2), 640, 2210, 9, 0), null)
        };

        var network = new NetFacts[]
        {
            new("Intel(R) Ethernet Controller I226-V", "Ethernet", "Intel Corporation", "00:1A:2B:3C:4D:5E", 2_500_000_000, true, 2, 14,
                ["192.168.0.42"], ["fe80::1a2b:3c4d:5e6f:7a8b"], ["192.168.0.1"], true,
                new DeviceDriverInfo("2.1.4.3", new DateOnly(2024, 11, 5), "Intel"), new PciId("8086", "125C", "1043", "87D2", "04"), null),
            new("Intel(R) Wi-Fi 6E AX210 160MHz", "Wi-Fi", "Intel Corporation", "00:1A:2B:3C:4D:5F", null, true, 7, 9, [], [], [], null,
                new DeviceDriverInfo("23.110.0.5", new DateOnly(2025, 2, 10), "Intel"), new PciId("8086", "2725", "8086", "0024", "1A"), null)
        };

        var audio = new AudioFacts[]
        {
            new("Realtek USB Audio", "Realtek", @"USB\VID_0B05&PID_1A52&MI_00\7&2A0E3EC5&0&0000", "OK",
                new DeviceDriverInfo("6.4.0.2369", new DateOnly(2024, 6, 3), "Realtek Semiconductor Corp.")),
            new("NVIDIA High Definition Audio", "NVIDIA", @"HDAUDIO\FUNC_01&VEN_10DE&DEV_00A4&SUBSYS_104388A0&REV_1001\5&87C4314&0&0001", "OK",
                new DeviceDriverInfo("1.4.3.2", new DateOnly(2024, 7, 22), "NVIDIA Corporation"))
        };

        var monitors = new MonitorFacts[]
        {
            new("PG27AQDM", "AUS", "27A1", 12, 2024, 10,
                new EdidInfo("AUS", "27A1", "PG27AQDM", 12, 2024, "1.4", 60, 34, true, 10, "DisplayPort", 2560, 1440, 239.97, 48, 240, 2.2, 2),
                new ActiveDisplay(@"DISPLAY\AUS27A1\SIM&0", gpu.Name, new DisplayMode(2560, 1440, 240, 32, true))),
            new("DELL S2721DGF", "DEL", "A1A8", 20, 2022, 5,
                new EdidInfo("DEL", "A1A8", "DELL S2721DGF", 20, 2022, "1.3", 60, 34, true, null, null, 2560, 1440, 59.95, 48, 165, 2.2, 1),
                new ActiveDisplay(@"DISPLAY\DELA1A8\SIM&1", gpu.Name, new DisplayMode(2560, 1440, 165, 32, false)))
        };

        var os = new OsFacts("Microsoft Windows 11 Pro", "10.0.26100", "26100", 4652, "24H2", "Professional", "x64",
            new DateTime(2025, 3, 14, 10, 12, 0), now.AddHours(-5).AddMinutes(-18));

        var peripherals = new PeripheralFacts[] { new("Corsair K70 RGB PRO", true, "Corsair"), new("Logitech G502 X", false, "Logitech") };

        var components = new List<HardwareComponent>
        {
            HardwareReport.Processor(cpu), HardwareReport.Motherboard(board), HardwareReport.Memory(memory), HardwareReport.Graphics([gpu]),
            HardwareReport.Storage(disks), HardwareReport.Network(network), HardwareReport.Audio(audio), HardwareReport.Monitors(monitors),
            HardwareReport.OperatingSystem(os, now)
        };
        if (HardwareReport.Peripherals(peripherals) is { } p) components.Add(p);
        return new HardwareInventoryResult(components, []);
    }
}
