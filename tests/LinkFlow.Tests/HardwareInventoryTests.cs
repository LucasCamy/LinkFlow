using System.Buffers.Binary;
using System.Globalization;
using LinkFlow.App.Resources;
using LinkFlow.App.Services;
using LinkFlow.App.ViewModels;

namespace LinkFlow.Tests;

/// <summary>Leituras e formatação do inventário de hardware (tela Hardware → Meu PC), com dados conhecidos.</summary>
public sealed class HardwareInventoryTests
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en");

    // ------------------------------------------------------------------ CPU

    [Theory]
    [InlineData(0x00A60F12, 0x19, 0x61, 2)]   // Ryzen 9 7900X3D (Zen 4)
    [InlineData(0x000B0671, 0x06, 0xB7, 1)]   // Core i9-13900K (Raptor Lake)
    [InlineData(0x00000F4A, 0x0F, 0x04, 0xA)] // família 0Fh sem estendida
    public void Cpuid_signature_adds_the_extended_family_and_model(int eax, int family, int model, int stepping)
    {
        Assert.Equal(new CpuSignature(family, model, stepping), HardwareSpecs.Signature(eax));
    }

    [Fact]
    public void Wmi_caption_is_the_fallback_signature()
    {
        Assert.Equal(new CpuSignature(25, 97, 2), HardwareSpecs.ParseCaption("AMD64 Family 25 Model 97 Stepping 2"));
        Assert.Null(HardwareSpecs.ParseCaption("ARMv8 (64-bit) Family 8"));
    }

    [Fact]
    public void Instruction_sets_are_decoded_from_cpuid_bits()
    {
        (int, int, int, int) Cpuid(int leaf, int sub) => (unchecked((uint)leaf), sub) switch
        {
            (0, _) => (7, 0, 0, 0),
            (1, _) => (0, 0, (1 << 0) | (1 << 9) | (1 << 19) | (1 << 20) | (1 << 25) | (1 << 28), (1 << 23) | (1 << 25) | (1 << 26)),
            (7, 0) => (1, (1 << 5) | (1 << 16) | (1 << 30) | (1 << 29), 0, 0),
            (7, 1) => (1 << 4, 0, 0, 0),
            (0x80000000, _) => (unchecked((int)0x80000001), 0, 0, 0),
            (0x80000001, _) => (0, 0, (1 << 2) | (1 << 6), 1 << 29),
            _ => (0, 0, 0, 0)
        };

        var sets = HardwareSpecs.InstructionSets(Cpuid).ToDictionary(g => g.Group, g => g.Names);
        Assert.Equal(["MMX", "SSE", "SSE2", "SSE3", "SSSE3", "SSE4.1", "SSE4.2", "SSE4A", "x86-64"], sets[InstructionGroup.Base]);
        Assert.Equal(["AVX", "AVX2", "AVX-VNNI", "AVX-512 (F, BW)"], sets[InstructionGroup.Vector]);
        Assert.Equal(["AES", "SHA"], sets[InstructionGroup.Crypto]);
        Assert.Equal(["AMD-V"], sets[InstructionGroup.Virtualization]);
        Assert.False(sets.ContainsKey(InstructionGroup.Other));
    }

    [Fact]
    public void Caches_are_read_from_logical_processor_information_and_grouped()
    {
        static byte[] Entry(int relationship, byte level = 0, byte ways = 0, ushort line = 0, uint size = 0, int type = 0)
        {
            var e = new byte[32];
            BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(8), relationship);
            e[16] = level;
            e[17] = ways;
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(18), line);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(20), size);
            BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(24), type);
            return e;
        }
        byte[] buffer =
        [
            .. Entry(0),                                           // núcleo: ignorado
            .. Entry(2, 1, 8, 64, 32 * 1024, 2), .. Entry(2, 1, 8, 64, 32 * 1024, 2),
            .. Entry(2, 1, 8, 64, 32 * 1024, 1),
            .. Entry(2, 3, 16, 64, 96 * 1024 * 1024, 0)
        ];

        var caches = HardwareSpecs.ParseCaches(buffer, 8);
        Assert.Equal(
        [
            new CacheLevel(1, CacheKind.Data, 32 * 1024, 8, 64, 2),
            new CacheLevel(1, CacheKind.Instruction, 32 * 1024, 8, 64, 1),
            new CacheLevel(3, CacheKind.Unified, 96 * 1024 * 1024, 16, 64, 1)
        ], caches);
    }

    [Fact]
    public void Processor_card_shows_the_key_specs_and_falls_back_to_wmi_caches()
    {
        var cpu = new CpuFacts("AMD Ryzen 7 7800X3D", "AuthenticAMD", "AM5", 1, 8, 16, 4200, "x64", new CpuSignature(0x19, 0x61, 2),
            [], 8192, 98304, [], null);
        var component = HardwareReport.Processor(cpu);

        Assert.Equal("AMD Ryzen 7 7800X3D", component.Name);
        Assert.Contains(new SpecRow(Strings.Hwi_CoresThreads, "8 / 16"), component.Summary);
        Assert.Contains(new SpecRow(Strings.Hwi_BaseClock, "4,20 GHz"), component.Summary);
        Assert.Contains(new SpecRow(Strings.Hwi_CacheL3Label, "96 MB"), component.Summary);
        var caches = component.Sections.Single(s => s.Title == Strings.Hwi_SectionCaches);
        Assert.Equal([new SpecRow("L2", "8 MB"), new SpecRow("L3", "96 MB")], caches.Rows);
        Assert.Contains(component.Sections[0].Rows, r => r.Label == Strings.Hwi_Signature && r.Value == "19h / 61h / 2");
    }

    // ------------------------------------------------------------------ PCI e GPU

    [Fact]
    public void Pci_ids_are_parsed_from_the_device_instance()
    {
        var id = HardwareSpecs.ParsePci(@"PCI\VEN_10DE&DEV_2704&SUBSYS_88C01043&REV_A1\4&19E58A00&0&0009");
        Assert.Equal(new PciId("10DE", "2704", "1043", "88C0", "A1"), id);
        Assert.Equal("10DE:2704 · sub 1043:88C0 · r A1", HardwareSpecs.PciIds(id!.Value, "sub {0}", "r {0}"));
        Assert.Equal("NVIDIA", HardwareSpecs.PciVendorName(id.Value.Vendor));
        Assert.Equal("ASUS", HardwareSpecs.PciVendorName(id.Value.SubsystemVendor));
        Assert.Null(HardwareSpecs.ParsePci(@"SWD\RDPIDD\RdpIdd_IndirectDisplay"));
    }

    [Fact]
    public void Pcie_link_shows_the_reduced_speed_only_when_it_differs()
    {
        Assert.Equal("PCIe 4.0 x16", HardwareReport.Pcie(new PcieLink(4, 16, 4, 16)));
        Assert.Equal(string.Format(Strings.Hwi_PcieNow, "PCIe 4.0 x16", "PCIe 1.1 x16"), HardwareReport.Pcie(new PcieLink(1, 16, 4, 16)));
        Assert.Equal("PCIe 3.0 x4", HardwareReport.Pcie(new PcieLink(3, 4, null, null)));
        Assert.Null(HardwareReport.Pcie(null));
        Assert.Null(HardwareSpecs.Pcie(9, 16));
    }

    [Theory]
    [InlineData("32.0.16.2124", "621.24")]
    [InlineData("32.0.15.6094", "560.94")]
    [InlineData("31.0.15.5222", "552.22")]
    [InlineData("27.21.14.5671", "456.71")]
    [InlineData("32.0.16", null)]
    [InlineData(null, null)]
    public void Nvidia_driver_number_comes_from_the_windows_version(string? windows, string? expected)
    {
        Assert.Equal(expected, HardwareSpecs.NvidiaDriver(windows));
    }

    [Fact]
    public void Graphics_card_picks_the_adapter_with_most_memory_and_formats_the_driver()
    {
        var igpu = new GpuFacts("AMD Radeon(TM) Graphics", "Advanced Micro Devices, Inc.", HardwareSpecs.ParsePci(@"PCI\VEN_1002&DEV_164E&REV_CA"),
            512UL << 20, "31.0.24033.1003", null, null, null, null, null, null);
        var rtx = new GpuFacts("NVIDIA GeForce RTX 4080", "NVIDIA", HardwareSpecs.ParsePci(@"PCI\VEN_10DE&DEV_2704&SUBSYS_88C01043&REV_A1"),
            17_171_480_576, "32.0.16.2124", new DateOnly(2026, 6, 12), 2560, 1440, 239, 32, new PcieLink(4, 16, 4, 16));

        var component = HardwareReport.Graphics([igpu, rtx]);
        Assert.Equal("NVIDIA GeForce RTX 4080", component.Name);
        Assert.Equal("NVIDIA GeForce RTX 4080", component.Sections[0].Title);
        Assert.Contains(new SpecRow(Strings.Hwi_VideoMemory, "16 GB"), component.Summary);
        Assert.Contains(new SpecRow("Driver", "621.24 (32.0.16.2124)"), component.Summary);
        Assert.Contains(new SpecRow(Strings.Hwi_VideoMode, "2560 × 1440 · 239 Hz"), component.Summary);
        Assert.Contains(new SpecRow(Strings.Hwi_VideoMode, "2560 × 1440 · 239 Hz · 32 bits"), component.Sections[0].Rows);
        Assert.Contains(new SpecRow(Strings.Hwi_OtherAdapters, "AMD Radeon(TM) Graphics"), component.Summary);
        Assert.Contains(component.Sections[0].Rows, r => r.Label == Strings.Hwi_BoardVendor && r.Value == "ASUS");
    }

    // ------------------------------------------------------------------ Memória

    [Theory]
    [InlineData("P0 CHANNEL A", "DIMMA2", "A")]
    [InlineData(null, "DIMM_B2", "B")]
    [InlineData("BANK 0", "Controller0-ChannelB-DIMM0", "B")]
    [InlineData("BANK 1", "A1", "A")]
    [InlineData("BANK 2", "DIMM 1", null)]
    [InlineData(null, null, null)]
    public void Memory_channel_comes_from_the_bank_or_slot_label(string? bank, string? locator, string? expected)
    {
        Assert.Equal(expected, HardwareSpecs.MemoryChannel(bank, locator));
    }

    [Theory]
    [InlineData("Unknown", "CMH64GX5M2B6000Z40", "Corsair")]
    [InlineData("029E", "", "Corsair")]
    [InlineData("80CE", "M425R2GA3BB0-CQKOL", "Samsung")]
    [InlineData("Unknown", "F5-6000J3038F16G", "G.Skill")]
    [InlineData("Kingston", "KF560C36-16", "Kingston")]
    [InlineData("Unknown", "XYZ-123", null)]
    [InlineData("Acme Memory", null, "Acme Memory")]
    public void Memory_vendor_uses_the_jedec_code_or_the_part_number(string? manufacturer, string? part, string? expected)
    {
        Assert.Equal(expected, HardwareSpecs.MemoryVendor(manufacturer, part));
    }

    [Fact]
    public void Memory_card_summarizes_modules_slots_and_channels()
    {
        MemoryModuleFacts Module(string slot, string bank) => new(slot, bank, "Unknown", "CMH64GX5M2B6000Z40", 32UL << 30, 34, 8, 6000, 4800,
            1350, 2, 64, 64);
        var component = HardwareReport.Memory(new MemoryFacts([Module("DIMMA2", "P0 CHANNEL A"), Module("DIMMB2", "P0 CHANNEL B")], 4, 128UL << 30));

        Assert.Equal("64 GB DDR5", component.Name);
        Assert.Contains(new SpecRow(Strings.Hwi_Speed, "6000 MT/s"), component.Summary);
        Assert.Contains(new SpecRow(Strings.Hwi_Modules, "2 × 32 GB"), component.Summary);
        Assert.Contains(new SpecRow(Strings.Hwi_Slots, string.Format(Strings.Hwi_SlotsValue, 2, 4)), component.Summary);
        Assert.Contains(new SpecRow(Strings.Hwi_Channels, $"{Strings.Hwi_ChannelDual} (A, B)"), component.Summary);
        Assert.Contains(new SpecRow(Strings.Hwi_PartNumber, "CMH64GX5M2B6000Z40"), component.Summary);

        var module = component.Sections.Single(s => s.Title == string.Format(Strings.Hwi_ModuleTitle, "DIMMA2"));
        Assert.Contains(new SpecRow(Strings.Hwi_Manufacturer, "Corsair"), module.Rows);
        Assert.Contains(new SpecRow(Strings.Hwi_Voltage, "1,35 V"), module.Rows);
        Assert.Contains(new SpecRow(Strings.Hwi_RatedSpeed, "4800 MT/s"), module.Rows);
        Assert.Contains(new SpecRow("ECC", Strings.Hwi_No), module.Rows);
    }

    [Fact]
    public void Missing_memory_data_shows_as_unavailable_instead_of_failing()
    {
        var component = HardwareReport.Memory(new MemoryFacts([], null, null));
        Assert.Equal(Strings.Hwi_Unavailable, component.Name);
        Assert.Contains(new SpecRow(Strings.Hwi_Speed, Strings.Hwi_Unavailable), component.Summary);
    }

    // ------------------------------------------------------------------ Placa-mãe

    [Fact]
    public void Firmware_placeholders_and_long_vendor_names_are_cleaned()
    {
        Assert.True(HardwareSpecs.IsPlaceholder("To be filled by O.E.M."));
        Assert.True(HardwareSpecs.IsPlaceholder("Default string"));
        Assert.True(HardwareSpecs.IsPlaceholder("0000000000"));
        Assert.False(HardwareSpecs.IsPlaceholder("07E1210_N31E341368"));
        Assert.Equal("MSI", HardwareSpecs.ShortVendor("Micro-Star International Co., Ltd."));
        Assert.Equal("ASUS", HardwareSpecs.ShortVendor("ASUSTeK COMPUTER INC."));
        Assert.Equal("Gigabyte", HardwareSpecs.ShortVendor("Gigabyte Technology Co., Ltd."));
        Assert.Null(HardwareSpecs.ShortVendor("System manufacturer"));
    }

    [Fact]
    public void Chipset_comes_from_the_lpc_controller_name()
    {
        Assert.Equal("Intel Z790", HardwareSpecs.Chipset(["PCI Express Root Port", "Intel(R) Z790 Chipset LPC/eSPI Controller - 7A04"]));
        Assert.Equal("Intel B660", HardwareSpecs.Chipset(["Intel(R) B660 Chipset LPC/eSPI Controller - 7A86"]));
        Assert.Null(HardwareSpecs.Chipset(["AMD SMBus", "AMD GPIO Controller", null]));
    }

    [Fact]
    public void Board_card_hides_placeholders_and_reports_the_firmware_mode()
    {
        var component = HardwareReport.Motherboard(new BoardFacts("Micro-Star International Co., Ltd.", "MAG X670E TOMAHAWK WIFI (MS-7E12)", "1.0",
            "Default string", "Micro-Star International Co., Ltd.", "MS-7E12", null, "American Megatrends International, LLC.", "1.L3",
            new DateOnly(2026, 9, 4), "3.8", true, true));

        Assert.Equal("MSI MAG X670E TOMAHAWK WIFI (MS-7E12)", component.Name);
        var board = component.Sections[0].Rows;
        Assert.DoesNotContain(board, r => r.Label == Strings.Hwi_SerialNumber);
        Assert.DoesNotContain(board, r => r.Label == Strings.Hwi_SystemModel);   // o modelo do sistema já está no nome da placa
        Assert.Contains(new SpecRow(Strings.Hwi_Chipset, Strings.Hwi_Unavailable), board);
        Assert.Contains(new SpecRow("BIOS", "1.L3 · 04/09/2026"), component.Summary);
        Assert.Contains(new SpecRow(Strings.Hwi_Firmware, $"UEFI · {Strings.Hwi_SecureBootOn}"), component.Summary);
    }

    // ------------------------------------------------------------------ Armazenamento

    [Fact]
    public void Nvme_health_log_is_parsed()
    {
        var log = new byte[512];
        log[0] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(log.AsSpan(1), 314);   // 41 °C
        log[3] = 100;
        log[4] = 10;
        log[5] = 3;
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(48), 35_937_500);   // 18,4 TB escritos
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(128), 3120);
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(144), 21);

        var health = HardwareSpecs.ParseNvmeHealth(log)!;
        Assert.Equal(41, health.TemperatureC);
        Assert.Equal(3, health.PercentUsed);
        Assert.Equal((UInt128)3120, health.PowerOnHours);
        Assert.Equal((UInt128)21, health.UnsafeShutdowns);
        Assert.Equal("18,4 TB", HardwareSpecs.Bytes((double)health.DataUnitsWritten * NvmeHealth.DataUnitBytes, false, Pt));
        Assert.Null(HardwareSpecs.ParseNvmeHealth(new byte[100]));
    }

    [Fact]
    public void Storage_card_lists_each_drive_and_marks_missing_health_as_unavailable()
    {
        var disks = new DiskFacts[]
        {
            new(0, "Samsung SSD 980 PRO 1TB", 1_000_204_886_016, 4, 17, "3B2QGXA7", 0, 0, 512, 4096, 2, true, ["C:"], new PcieLink(4, 4, 4, 4),
                new NvmeHealth(0, 53, 99, 10, 7, 0, 0, 1497, 9664, 133, 0), null),
            new(1, "WDC WD40EFRX", 4_000_787_030_016, 3, 11, "82.00A82", null, 5400, 512, 4096, 2, false, ["E:"], null, null, null)
        };
        var component = HardwareReport.Storage(disks);

        Assert.Equal(string.Format(Strings.Hwi_DiskCount, 2, "5 TB"), component.Name);
        Assert.Equal(new SpecRow("Samsung SSD 980 PRO 1TB", $"1 TB · NVMe SSD · {Strings.Win_Saudavel}"), component.Summary[0]);
        var nvme = component.Sections[0].Rows;
        Assert.Contains(new SpecRow(Strings.Hwi_Interface, "NVMe · PCIe 4.0 x4"), nvme);
        Assert.Contains(new SpecRow("S.M.A.R.T.", Strings.Hwi_SmartOk), nvme);
        Assert.Contains(new SpecRow(Strings.Hwi_PowerOnHours, "9.664 h"), nvme);
        var hdd = component.Sections[1].Rows;
        Assert.Contains(new SpecRow(Strings.Hwi_HealthWindows, Strings.Hwi_Unavailable), hdd);
        Assert.Contains(new SpecRow("S.M.A.R.T.", Strings.Hwi_Unavailable), hdd);
        Assert.Contains(new SpecRow(Strings.Hwi_Rotation, "5.400 RPM"), hdd);
    }

    [Theory]
    [InlineData(34_359_738_368d, true, "32 GB")]
    [InlineData(17_171_480_576d, true, "16 GB")]
    [InlineData(1_000_204_886_016d, false, "1 TB")]
    [InlineData(500_107_862_016d, false, "500 GB")]
    [InlineData(32 * 1024d, true, "32 KB")]
    [InlineData(1_536 * 1024d, true, "1,5 MB")]
    public void Sizes_use_binary_units_for_memory_and_decimal_for_drives(double bytes, bool binary, string expected)
    {
        Assert.Equal(expected, HardwareSpecs.Bytes(bytes, binary, Pt));
        Assert.Equal(expected.Replace(',', '.'), HardwareSpecs.Bytes(bytes, binary, En));
    }

    // ------------------------------------------------------------------ Monitores e áudio

    /// <summary>EDID base real de um Samsung Odyssey G7 (número de série trocado).</summary>
    private static readonly byte[] SamsungEdid = Convert.FromHexString(string.Concat(
        "00 FF FF FF FF FF FF 00 4C 2D 5C 10 01 00 00 00",
        "05 1F 01 04 B5 3C 22 78 3B 2E B5 AE 4F 46 A6 26",
        "11 50 54 BF EF 80 81 C0 81 00 81 80 95 00 A9 C0",
        "B3 00 71 4F 01 01 98 E2 00 A0 A0 A0 29 50 08 3D",
        "35 00 55 50 21 00 00 1A 00 00 00 FD 0C 50 F0 67",
        "67 62 01 0A 20 20 20 20 20 20 00 00 00 FC 00 4C",
        "43 32 37 47 37 78 54 0A 20 20 20 20 00 00 00 FF",
        "00 53 45 52 49 41 4C 30 30 30 31 0A 20 20 02 15").Replace(" ", ""));

    [Fact]
    public void Edid_reports_name_size_native_mode_and_refresh_range()
    {
        Assert.Equal(128, SamsungEdid.Length);
        var edid = HardwareSpecs.ParseEdid(SamsungEdid)!;
        Assert.Equal("SAM", edid.Manufacturer);
        Assert.Equal("105C", edid.ProductCode);
        Assert.Equal("LC27G7xT", edid.Name);
        Assert.Equal((5, 2021), (edid.Week, edid.Year));
        Assert.Equal("1.4", edid.Version);
        Assert.Equal((60, 34), (edid.WidthCm, edid.HeightCm));
        Assert.True(edid.Digital);
        Assert.Equal(10, edid.BitsPerColor);
        Assert.Equal("DisplayPort", edid.Interface);
        Assert.Equal((2560, 1440), (edid.NativeWidth, edid.NativeHeight));
        Assert.Equal(144, edid.PreferredRefresh!.Value, 1);
        Assert.Equal((80, 240), (edid.MinVerticalHz, edid.MaxVerticalHz));
        Assert.Equal(2.2, edid.Gamma!.Value, 2);
        Assert.Equal(2, edid.Extensions);
        Assert.Null(HardwareSpecs.ParseEdid(new byte[128]));
        Assert.Equal(27.2, HardwareSpecs.DiagonalInches(60, 34)!.Value, 1);
    }

    [Fact]
    public void Monitor_keys_from_wmi_and_from_the_display_interface_match()
    {
        string? wmi = HardwareSpecs.MonitorKeyFromWmi(@"DISPLAY\SAM105C\5&17156835&0&UID4353_0");
        string? iface = HardwareSpecs.MonitorKeyFromInterface(@"\\?\DISPLAY#SAM105C#5&17156835&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}");
        Assert.Equal(@"DISPLAY\SAM105C\5&17156835&0&UID4353", wmi);
        Assert.Equal(wmi, iface);
        Assert.Equal("Samsung", HardwareSpecs.MonitorVendor("SAM"));
        Assert.Equal("LG", HardwareSpecs.MonitorVendor("GSM"));
    }

    [Theory]
    [InlineData(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897&SUBSYS_1458A194&REV_1003\5&1", "Realtek ALC897")]
    [InlineData(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_1220&SUBSYS_10438797", "Realtek ALC1220")]
    [InlineData(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0B00&SUBSYS_1462EE12&REV_1000\5&9BB38E1&0&0001", "Realtek 0B00")]
    [InlineData(@"HDAUDIO\FUNC_01&VEN_10DE&DEV_00A4&SUBSYS_104388C0", "NVIDIA 00A4")]
    [InlineData(@"USB\VID_046D&PID_0ABA&MI_00\B&2A0E3EC5&0&0000", null)]
    public void Hd_audio_codec_is_named_from_the_device_id(string pnp, string? expected)
    {
        Assert.Equal(expected, HardwareSpecs.HdAudioCodec(pnp));
    }

    [Fact]
    public void Usb_ids_are_read_from_the_device_instance()
    {
        Assert.Equal("046D:0ABA", HardwareSpecs.UsbId(@"USB\VID_046D&PID_0ABA&MI_00\B&2A0E3EC5&0&0000"));
        Assert.Null(HardwareSpecs.UsbId(@"PCI\VEN_10EC&DEV_8125"));
    }

    // ------------------------------------------------------------------ Inventário completo e telemetria

    [Fact]
    public void Simulated_inventory_has_every_card_without_unavailable_data()
    {
        var snapshot = new EngineSnapshot { CpuName = "AMD Ryzen 9 7900X3D (simulado)", GpuName = "NVIDIA GeForce RTX 4080 (simulado)", CpuCores = 12 };
        var result = HardwareInventory.Simulated(snapshot, new DateTime(2026, 11, 20, 12, 0, 0));

        Assert.Equal(
        [
            HardwareKind.Processor, HardwareKind.Motherboard, HardwareKind.Memory, HardwareKind.Graphics, HardwareKind.Storage,
            HardwareKind.Network, HardwareKind.Audio, HardwareKind.Monitor, HardwareKind.OperatingSystem, HardwareKind.Peripherals
        ], result.Components.Select(c => c.Kind));
        Assert.Empty(result.Unavailable);
        Assert.All(result.Components, c =>
        {
            Assert.NotEmpty(c.Summary);
            Assert.NotEmpty(c.Sections);
            Assert.DoesNotContain(c.Summary, r => r.Value == Strings.Hwi_Unavailable);
        });
        Assert.Equal("AMD Ryzen 9 7900X3D (simulado)", result.Components[0].Name);

        // Pacote de diagnóstico: categoria em português e o resumo de cada cartão
        var part = result.Parts.Single(p => p.Category == "Memória");
        Assert.Equal("64 GB DDR5", part.Name);
        Assert.Contains("6000 MT/s", part.Details);
    }

    [Fact]
    public void Component_text_has_every_section_for_the_clipboard()
    {
        var component = new HardwareComponent(HardwareKind.Audio, "1 dispositivo", [new SpecRow("Realtek", "USB")],
            [new SpecSection("Realtek USB Audio", [new SpecRow("Driver", "6.4.0.2369")])], "Nota");
        Assert.Equal("Áudio: 1 dispositivo\n  Realtek USB Audio\n    Driver: 6.4.0.2369\n  Nota\n",
            component.ToText("Áudio").Replace("\r\n", "\n"));
    }

    [Fact]
    public void Live_tiles_come_from_the_engine_snapshot()
    {
        var snapshot = new EngineSnapshot
        {
            CpuSensorAvailable = true, CpuTemp = 61.25f, CpuLoad = 37.4f, CpuClockMhz = 4875, CpuPower = 88.6f, CpuCoreLoads = [20, 95, 40],
            RamUsage = 41, RamUsedGb = 26.4f, RamTotalGb = 63.9f
        };

        var cpu = HardwareDetailViewModel.LiveTilesFor(HardwareKind.Processor, snapshot, Pt);
        Assert.Equal(new LiveTile(Strings.Dash_Temperature, "61,3 °C"), cpu[0]);
        Assert.Equal(new LiveTile(Strings.Det_AvgClock, "4,88 GHz"), cpu[2]);
        Assert.Equal("95%", cpu.Single(t => t.Label == Strings.Hwi_BusiestCore).Value);

        var ram = HardwareDetailViewModel.LiveTilesFor(HardwareKind.Memory, snapshot, En);
        Assert.Equal(new LiveTile(Strings.Det_Free, "37.5 GB"), ram[2]);

        var gpu = HardwareDetailViewModel.LiveTilesFor(HardwareKind.Graphics, new EngineSnapshot(), Pt);
        Assert.All(gpu, t => Assert.Equal(Fmt.Missing, t.Value));
        Assert.Empty(HardwareDetailViewModel.LiveTilesFor(HardwareKind.Motherboard, snapshot, Pt));

        var noSensor = HardwareDetailViewModel.LiveTilesFor(HardwareKind.Processor, new EngineSnapshot { CpuTemp = 50 }, Pt);
        Assert.Equal(Fmt.Missing, noSensor[0].Value);
        Assert.Equal(Strings.Det_CpuSensorHint, noSensor[0].Hint);
    }

    [Fact]
    public void Every_card_kind_has_a_title_icon_and_color()
    {
        foreach (var kind in Enum.GetValues<HardwareKind>())
        {
            Assert.False(string.IsNullOrWhiteSpace(HardwareVisuals.Title(kind)));
            Assert.False(string.IsNullOrWhiteSpace(HardwareVisuals.Icon(kind)));
            Assert.StartsWith("#", HardwareVisuals.Color(kind));
        }
        Assert.NotNull(HardwareVisuals.SeriesGroup(HardwareKind.Storage));
        Assert.NotEmpty(MainViewModel.SeriesFor(HardwareVisuals.SeriesGroup(HardwareKind.Processor)));
    }

    [Fact]
    public void Startup_option_opens_a_hardware_detail_for_screenshots()
    {
        var options = App.StartupOptions.Parse(["--simulate", "--page", "6", "--hw-detail", "storage"]);
        Assert.Equal("storage", options.ScreenshotHardwareDetail);
    }
}
