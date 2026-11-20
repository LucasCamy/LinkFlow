using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LinkFlow.App.Services;

/// <summary>Família/modelo/stepping como o CPUID informa (já com os campos estendidos somados).</summary>
internal readonly record struct CpuSignature(int Family, int Model, int Stepping);

internal enum CacheKind { Unified = 0, Instruction = 1, Data = 2, Trace = 3 }

/// <summary>Um nível de cache: quantas instâncias iguais existem (ex.: 6 × 32 KB de L1 de dados).</summary>
internal sealed record CacheLevel(int Level, CacheKind Kind, uint SizeBytes, int Associativity, int LineSize, int Count);

internal enum InstructionGroup { Base, Vector, Crypto, Other, Virtualization }

internal readonly record struct PciId(string Vendor, string Device, string? SubsystemVendor, string? Subsystem, string? Revision);

/// <summary>Velocidade e largura do link PCI Express (geração 1 a 6, número de pistas).</summary>
internal readonly record struct PcieLink(int? CurrentGen, int? CurrentWidth, int? MaxGen, int? MaxWidth);

/// <summary>O que dá para tirar do EDID base (128 bytes) de um monitor.</summary>
internal sealed record EdidInfo(string? Manufacturer, string ProductCode, string? Name, int? Week, int? Year, string Version,
    int? WidthCm, int? HeightCm, bool Digital, int? BitsPerColor, string? Interface, int? NativeWidth, int? NativeHeight,
    double? PreferredRefresh, int? MinVerticalHz, int? MaxVerticalHz, double? Gamma, int Extensions);

/// <summary>Página de saúde do NVMe (SMART / Health Information, log 02h).</summary>
internal sealed record NvmeHealth(byte CriticalWarning, int? TemperatureC, byte AvailableSpare, byte SpareThreshold, byte PercentUsed,
    UInt128 DataUnitsRead, UInt128 DataUnitsWritten, UInt128 PowerCycles, UInt128 PowerOnHours, UInt128 UnsafeShutdowns,
    UInt128 MediaErrors)
{
    /// <summary>Uma "data unit" do NVMe vale 1000 setores de 512 bytes.</summary>
    public const double DataUnitBytes = 512_000;
}

/// <summary>
/// Conversões e leituras puras usadas pelo inventário de hardware (sem WMI nem Win32): ficam aqui para os testes
/// conferirem com dados conhecidos.
/// </summary>
internal static partial class HardwareSpecs
{
    // ------------------------------------------------------------------ CPUID

    public static CpuSignature Signature(int eax)
    {
        int stepping = eax & 0xF, model = (eax >> 4) & 0xF, family = (eax >> 8) & 0xF;
        int extModel = (eax >> 16) & 0xF, extFamily = (eax >> 20) & 0xFF;
        int displayFamily = family == 0xF ? family + extFamily : family;
        int displayModel = family is 0x6 or 0xF ? (extModel << 4) + model : model;
        return new CpuSignature(displayFamily, displayModel, stepping);
    }

    /// <summary>Reserva quando o CPUID não está disponível: "AMD64 Family 25 Model 97 Stepping 2" (Win32_Processor.Caption).</summary>
    public static CpuSignature? ParseCaption(string? caption)
    {
        if (caption == null) return null;
        var m = CaptionRegex().Match(caption);
        return m.Success ? new CpuSignature(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)) : null;
    }

    [GeneratedRegex(@"Family\s+(\d+)\s+Model\s+(\d+)\s+Stepping\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex CaptionRegex();

    /// <summary>Conjuntos de instruções que o processador anuncia, agrupados como no CPU-Z.</summary>
    public static IReadOnlyList<(InstructionGroup Group, IReadOnlyList<string> Names)> InstructionSets(
        Func<int, int, (int Eax, int Ebx, int Ecx, int Edx)> cpuid)
    {
        int maxLeaf = cpuid(0, 0).Eax;
        var l1 = maxLeaf >= 1 ? cpuid(1, 0) : default;
        var l7 = maxLeaf >= 7 ? cpuid(7, 0) : default;
        var l71 = maxLeaf >= 7 && l7.Eax >= 1 ? cpuid(7, 1) : default;
        uint maxExt = (uint)cpuid(unchecked((int)0x80000000), 0).Eax;
        var e1 = maxExt >= 0x80000001 ? cpuid(unchecked((int)0x80000001), 0) : default;
        static bool Bit(int register, int bit) => ((register >> bit) & 1) != 0;

        var groups = new List<(InstructionGroup, IReadOnlyList<string>)>();
        void Add(InstructionGroup group, params (bool Present, string Name)[] flags)
        {
            var names = flags.Where(f => f.Present).Select(f => f.Name).ToArray();
            if (names.Length > 0) groups.Add((group, names));
        }

        Add(InstructionGroup.Base, (Bit(l1.Edx, 23), "MMX"), (Bit(l1.Edx, 25), "SSE"), (Bit(l1.Edx, 26), "SSE2"), (Bit(l1.Ecx, 0), "SSE3"),
            (Bit(l1.Ecx, 9), "SSSE3"), (Bit(l1.Ecx, 19), "SSE4.1"), (Bit(l1.Ecx, 20), "SSE4.2"), (Bit(e1.Ecx, 6), "SSE4A"),
            (Bit(e1.Edx, 29), "x86-64"));

        var avx512 = new (bool, string)[]
        {
            (Bit(l7.Ebx, 16), "F"), (Bit(l7.Ebx, 28), "CD"), (Bit(l7.Ebx, 30), "BW"), (Bit(l7.Ebx, 17), "DQ"), (Bit(l7.Ebx, 31), "VL"),
            (Bit(l7.Ebx, 21), "IFMA"), (Bit(l7.Ecx, 1), "VBMI"), (Bit(l7.Ecx, 6), "VBMI2"), (Bit(l7.Ecx, 11), "VNNI"),
            (Bit(l7.Ecx, 12), "BITALG"), (Bit(l7.Ecx, 14), "VPOPCNTDQ"), (Bit(l71.Eax, 5), "BF16")
        }.Where(f => f.Item1).Select(f => f.Item2).ToArray();
        Add(InstructionGroup.Vector, (Bit(l1.Ecx, 28), "AVX"), (Bit(l7.Ebx, 5), "AVX2"), (Bit(l1.Ecx, 12), "FMA3"), (Bit(e1.Ecx, 16), "FMA4"),
            (Bit(l1.Ecx, 29), "F16C"), (Bit(l71.Eax, 4), "AVX-VNNI"),
            (avx512.Length > 0, $"AVX-512 ({string.Join(", ", avx512)})"));
        Add(InstructionGroup.Crypto, (Bit(l1.Ecx, 25), "AES"), (Bit(l7.Ecx, 9), "VAES"), (Bit(l1.Ecx, 1), "PCLMULQDQ"),
            (Bit(l7.Ebx, 29), "SHA"), (Bit(l1.Ecx, 30), "RDRAND"), (Bit(l7.Ebx, 18), "RDSEED"));
        Add(InstructionGroup.Other, (Bit(l1.Ecx, 23), "POPCNT"), (Bit(e1.Ecx, 5), "LZCNT"), (Bit(l7.Ebx, 3), "BMI1"), (Bit(l7.Ebx, 8), "BMI2"),
            (Bit(l7.Ebx, 19), "ADX"), (Bit(l1.Ecx, 22), "MOVBE"), (Bit(l1.Ecx, 13), "CMPXCHG16B"));
        Add(InstructionGroup.Virtualization, (Bit(l1.Ecx, 5), "VT-x"), (Bit(e1.Ecx, 2), "AMD-V"));
        return groups;
    }

    public static string? CpuVendor(string? manufacturer) => manufacturer?.Trim() switch
    {
        "AuthenticAMD" => "AMD",
        "GenuineIntel" => "Intel",
        "CentaurHauls" => "VIA",
        { Length: > 0 } other => other,
        _ => null
    };

    public static string? Architecture(ushort? wmiArchitecture) => wmiArchitecture switch
    {
        0 => "x86",
        5 => "ARM",
        9 => "x64",
        12 => "ARM64",
        _ => null
    };

    // ------------------------------------------------------------------ Caches (GetLogicalProcessorInformation)

    /// <summary>
    /// Lê as entradas de cache (Relationship = 2) do buffer de SYSTEM_LOGICAL_PROCESSOR_INFORMATION e junta as iguais.
    /// </summary>
    public static IReadOnlyList<CacheLevel> ParseCaches(ReadOnlySpan<byte> buffer, int pointerSize)
    {
        // ProcessorMask (ULONG_PTR), Relationship (int) e a união de 16 bytes alinhada a 8
        int entrySize = pointerSize == 8 ? 32 : 24, unionOffset = pointerSize == 8 ? 16 : 8;
        var found = new List<(int Level, CacheKind Kind, uint Size, int Ways, int Line)>();
        for (int offset = 0; offset + entrySize <= buffer.Length; offset += entrySize)
        {
            var entry = buffer.Slice(offset, entrySize);
            if (BinaryPrimitives.ReadInt32LittleEndian(entry[pointerSize..]) != 2) continue;   // RelationCache
            var cache = entry[unionOffset..];
            int type = BinaryPrimitives.ReadInt32LittleEndian(cache[8..]);
            if (type is < 0 or > 3) continue;
            found.Add((cache[0], (CacheKind)type, BinaryPrimitives.ReadUInt32LittleEndian(cache[4..]), cache[1],
                BinaryPrimitives.ReadUInt16LittleEndian(cache[2..])));
        }
        return found.GroupBy(c => c).Select(g => new CacheLevel(g.Key.Level, g.Key.Kind, g.Key.Size, g.Key.Ways, g.Key.Line, g.Count()))
            .OrderBy(c => c.Level).ThenByDescending(c => c.Kind).ThenByDescending(c => c.SizeBytes).ToArray();
    }

    // ------------------------------------------------------------------ PCI

    public static PciId? ParsePci(string? pnpId)
    {
        if (pnpId == null) return null;
        var m = PciRegex().Match(pnpId);
        if (!m.Success) return null;
        string? subsys = m.Groups["subsys"].Success ? m.Groups["subsys"].Value.ToUpperInvariant() : null;
        return new PciId(m.Groups["ven"].Value.ToUpperInvariant(), m.Groups["dev"].Value.ToUpperInvariant(),
            subsys?[4..], subsys?[..4], m.Groups["rev"].Success ? m.Groups["rev"].Value.ToUpperInvariant() : null);
    }

    [GeneratedRegex(@"^PCI\\VEN_(?<ven>[0-9A-F]{4})&DEV_(?<dev>[0-9A-F]{4})(?:&SUBSYS_(?<subsys>[0-9A-F]{8}))?(?:&REV_(?<rev>[0-9A-F]{2}))?",
        RegexOptions.IgnoreCase)]
    private static partial Regex PciRegex();

    /// <summary>"10DE:2704 · 1043:88C0 · rev. A1" (o rótulo do subsistema e da revisão vem traduzido).</summary>
    public static string PciIds(PciId id, string subsystemLabel, string revisionLabel)
    {
        var parts = new List<string> { $"{id.Vendor}:{id.Device}" };
        if (id.SubsystemVendor != null && id.Subsystem != null) parts.Add(string.Format(subsystemLabel, $"{id.SubsystemVendor}:{id.Subsystem}"));
        if (id.Revision != null) parts.Add(string.Format(revisionLabel, id.Revision));
        return string.Join(" · ", parts);
    }

    public static string? PciVendorName(string? vendor) => vendor?.ToUpperInvariant() switch
    {
        "10DE" => "NVIDIA",
        "1002" or "1022" => "AMD",
        "8086" => "Intel",
        "1414" => "Microsoft",
        "1043" => "ASUS",
        "1462" => "MSI",
        "1458" => "Gigabyte",
        "3842" => "EVGA",
        "19DA" => "Zotac",
        "1569" => "Palit",
        "10B0" => "Gainward",
        "1DA2" => "Sapphire",
        "148C" => "PowerColor",
        "1682" => "XFX",
        "196E" => "PNY",
        "1B4C" => "KFA2 / Galax",
        "7377" => "Colorful",
        "1849" => "ASRock",
        "17AA" => "Lenovo",
        "1028" => "Dell",
        "103C" => "HP",
        "144D" => "Samsung",
        "15B7" => "Western Digital",
        "1987" => "Phison",
        "126F" => "Silicon Motion",
        "1C5C" => "SK hynix",
        "1344" => "Micron",
        "10EC" => "Realtek",
        "14C3" => "MediaTek",
        "168C" or "17CB" => "Qualcomm",
        "14E4" => "Broadcom",
        "1D6A" => "Marvell (Aquantia)",
        "1B21" => "ASMedia",
        _ => null
    };

    public static string? PcieGeneration(int? speed) => speed switch
    {
        1 => "1.1",
        2 => "2.0",
        3 => "3.0",
        4 => "4.0",
        5 => "5.0",
        6 => "6.0",
        _ => null
    };

    /// <summary>"PCIe 4.0 x16"; null sem os dois dados.</summary>
    public static string? Pcie(int? gen, int? width) =>
        PcieGeneration(gen) is { } g && width is > 0 ? $"PCIe {g} x{width}" : null;

    // ------------------------------------------------------------------ GPU

    /// <summary>A NVIDIA numera o driver pelos 5 últimos dígitos da versão do Windows: 32.0.16.2124 → 621.24.</summary>
    public static string? NvidiaDriver(string? windowsVersion)
    {
        var parts = windowsVersion?.Split('.');
        if (parts is not { Length: 4 } || !parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit)) || parts[3].Length > 4) return null;
        string digits = parts[2] + parts[3].PadLeft(4, '0');
        if (digits.Length < 5) return null;
        digits = digits[^5..];
        return $"{int.Parse(digits[..3], CultureInfo.InvariantCulture)}.{digits[3..]}";
    }

    // ------------------------------------------------------------------ Memória

    public static string? MemoryType(uint? smbiosType) => smbiosType switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => null
    };

    public static string? MemoryFormFactor(ushort? formFactor) => formFactor switch
    {
        8 => "DIMM",
        12 => "SO-DIMM",
        13 => "SO-DIMM",   // alguns firmwares marcam SODIMM como 13
        _ => null
    };

    /// <summary>Canal do módulo pelo rótulo do banco ("P0 CHANNEL A") ou pelo slot ("DIMM_A2", "ChannelB-DIMM0", "A1").</summary>
    public static string? MemoryChannel(string? bank, string? locator)
    {
        foreach (var text in new[] { bank, locator })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var m = ChannelRegex().Match(text);
            if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
        }
        foreach (var text in new[] { locator, bank })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var m = DimmRegex().Match(text);
            if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
        }
        return null;
    }

    [GeneratedRegex(@"CHANNEL\s*[-_]?\s*([A-H])\b", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelRegex();

    [GeneratedRegex(@"(?:^|DIMM[\s_-]?|[\s_-])([A-H])[\s_-]?\d(?:$|[\s_-])", RegexOptions.IgnoreCase)]
    private static partial Regex DimmRegex();

    /// <summary>Fabricante do módulo: o código JEDEC do SPD ou, se o firmware não informa, o prefixo do part number.</summary>
    public static string? MemoryVendor(string? manufacturer, string? partNumber)
    {
        string code = (manufacturer ?? string.Empty).Trim().ToUpperInvariant();
        string? byCode = code switch
        {
            "029E" or "9E02" or "CORSAIR" => "Corsair",
            "80CE" or "CE00" or "00CE" or "SAMSUNG" => "Samsung",
            "80AD" or "AD00" or "00AD" or "SK HYNIX" or "HYNIX" => "SK hynix",
            "802C" or "2C00" or "002C" or "MICRON" or "MICRON TECHNOLOGY" => "Micron",
            "04CD" or "CD04" or "G SKILL" or "G.SKILL" => "G.Skill",
            "0198" or "9801" or "KINGSTON" => "Kingston",
            "859B" or "9B85" or "059B" or "CRUCIAL" or "CRUCIAL TECHNOLOGY" => "Crucial",
            "04CB" or "CB04" or "A-DATA" or "ADATA" => "ADATA",
            "04EF" or "EF04" or "TEAM GROUP" or "TEAMGROUP" => "TeamGroup",
            "0A83" or "PATRIOT" => "Patriot",
            _ => null
        };
        if (byCode != null) return byCode;

        string part = (partNumber ?? string.Empty).Trim().ToUpperInvariant();
        string? byPart = part switch
        {
            _ when part.StartsWith("CM", StringComparison.Ordinal) => "Corsair",
            _ when part.StartsWith("F5-", StringComparison.Ordinal) || part.StartsWith("F4-", StringComparison.Ordinal) ||
                   part.StartsWith("F3-", StringComparison.Ordinal) => "G.Skill",
            _ when part.StartsWith("KF", StringComparison.Ordinal) || part.StartsWith("KVR", StringComparison.Ordinal) ||
                   part.StartsWith("KHX", StringComparison.Ordinal) => "Kingston",
            _ when part.StartsWith("CT", StringComparison.Ordinal) || part.StartsWith("BL", StringComparison.Ordinal) => "Crucial",
            _ when part.StartsWith("M3", StringComparison.Ordinal) || part.StartsWith("M4", StringComparison.Ordinal) => "Samsung",
            _ when part.StartsWith("HMA", StringComparison.Ordinal) || part.StartsWith("HMC", StringComparison.Ordinal) ||
                   part.StartsWith("HMCG", StringComparison.Ordinal) => "SK hynix",
            _ when part.StartsWith("MTC", StringComparison.Ordinal) || part.StartsWith("MTA", StringComparison.Ordinal) => "Micron",
            _ when part.StartsWith("AX5", StringComparison.Ordinal) || part.StartsWith("AX4", StringComparison.Ordinal) => "ADATA XPG",
            _ when part.StartsWith("TF", StringComparison.Ordinal) || part.StartsWith("TED", StringComparison.Ordinal) ||
                   part.StartsWith("FF", StringComparison.Ordinal) => "TeamGroup",
            _ => null
        };
        if (byPart != null) return byPart;
        return IsPlaceholder(manufacturer) || code.All(c => char.IsAsciiHexDigit(c)) ? null : manufacturer!.Trim();
    }

    /// <summary>Textos que o firmware põe quando não preencheu o campo.</summary>
    public static bool IsPlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        string v = value.Trim().TrimEnd('.').ToUpperInvariant();
        return v is "TO BE FILLED BY O.E.M" or "TO BE FILLED BY OEM" or "DEFAULT STRING" or "SYSTEM SERIAL NUMBER" or "SYSTEM PRODUCT NAME"
                 or "SYSTEM MANUFACTURER" or "SYSTEM VERSION" or "BASE BOARD SERIAL NUMBER" or "NONE" or "N/A" or "NA" or "UNKNOWN"
                 or "NOT SPECIFIED" or "NOT APPLICABLE" or "NOT AVAILABLE" or "INVALID" or "OEM" or "O.E.M" or "123456789" or "X.X"
                 or "UNDEFINED" or "EMPTY" ||
               v.All(c => c == '0') || v.All(c => c == 'F') || v.All(c => c == ' ');
    }

    /// <summary>Nome curto do fabricante: "Micro-Star International Co., Ltd." → "MSI".</summary>
    public static string? ShortVendor(string? vendor)
    {
        if (IsPlaceholder(vendor)) return null;
        string v = vendor!.Trim();
        string upper = v.ToUpperInvariant();
        if (upper.StartsWith("MICRO-STAR", StringComparison.Ordinal)) return "MSI";
        if (upper.StartsWith("ASUSTEK", StringComparison.Ordinal) || upper == "ASUS") return "ASUS";
        if (upper.StartsWith("GIGABYTE", StringComparison.Ordinal)) return "Gigabyte";
        if (upper.StartsWith("ASROCK", StringComparison.Ordinal)) return "ASRock";
        if (upper.StartsWith("DELL", StringComparison.Ordinal)) return "Dell";
        if (upper is "LENOVO") return "Lenovo";
        if (upper is "HP" or "HEWLETT-PACKARD" || upper.StartsWith("HP ", StringComparison.Ordinal)) return "HP";
        if (upper.StartsWith("ACER", StringComparison.Ordinal)) return "Acer";
        if (upper.StartsWith("BIOSTAR", StringComparison.Ordinal)) return "Biostar";
        if (upper.StartsWith("EVGA", StringComparison.Ordinal)) return "EVGA";
        if (upper.StartsWith("INTEL", StringComparison.Ordinal)) return "Intel";
        if (upper.StartsWith("MICROSOFT", StringComparison.Ordinal)) return "Microsoft";
        if (upper.StartsWith("AMERICAN MEGATRENDS", StringComparison.Ordinal)) return "AMI";
        if (upper.StartsWith("PHOENIX", StringComparison.Ordinal)) return "Phoenix";
        if (upper.StartsWith("INSYDE", StringComparison.Ordinal)) return "Insyde";
        return v;
    }

    // ------------------------------------------------------------------ Chipset

    /// <summary>Chipset pelo nome do controlador LPC/eSPI ("Intel(R) Z790 Chipset LPC/eSPI Controller - 7A04" → "Intel Z790").</summary>
    public static string? Chipset(IEnumerable<string?> pciDeviceNames)
    {
        foreach (var name in pciDeviceNames)
        {
            if (name == null) continue;
            var m = ChipsetRegex().Match(name);
            if (!m.Success) continue;
            string vendor = name.StartsWith("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel " :
                name.StartsWith("AMD", StringComparison.OrdinalIgnoreCase) ? "AMD " : string.Empty;
            return vendor + m.Groups[1].Value.Trim();
        }
        return null;
    }

    [GeneratedRegex(@"\b([A-Z]{0,2}\d{2,4}[A-Z]?(?:\s+Series)?)\s+Chipset\b", RegexOptions.IgnoreCase)]
    private static partial Regex ChipsetRegex();

    // ------------------------------------------------------------------ Áudio

    /// <summary>Codec HD Audio: "HDAUDIO\FUNC_01&amp;VEN_10EC&amp;DEV_0897…" → "Realtek ALC897"; IDs fora do padrão ALC viram "Realtek 0B00".</summary>
    public static string? HdAudioCodec(string? pnpId)
    {
        if (pnpId == null) return null;
        var m = HdAudioRegex().Match(pnpId);
        if (!m.Success) return null;
        string vendorId = m.Groups[1].Value.ToUpperInvariant(), device = m.Groups[2].Value.ToUpperInvariant();
        string vendor = PciVendorName(vendorId) ?? vendorId;
        if (vendorId == "10EC" && device.All(char.IsAsciiDigit)) return $"{vendor} ALC{device.TrimStart('0')}";
        return $"{vendor} {device}";
    }

    [GeneratedRegex(@"^HDAUDIO\\FUNC_\d+&VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex HdAudioRegex();

    /// <summary>"USB\VID_046D&amp;PID_0ABA…" → "046D:0ABA".</summary>
    public static string? UsbId(string? pnpId)
    {
        if (pnpId == null) return null;
        var m = UsbRegex().Match(pnpId);
        return m.Success ? $"{m.Groups[1].Value.ToUpperInvariant()}:{m.Groups[2].Value.ToUpperInvariant()}" : null;
    }

    [GeneratedRegex(@"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex UsbRegex();

    // ------------------------------------------------------------------ Monitores

    public static EdidInfo? ParseEdid(ReadOnlySpan<byte> edid)
    {
        ReadOnlySpan<byte> header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        if (edid.Length < 128 || !edid[..8].SequenceEqual(header)) return null;

        ushort id = BinaryPrimitives.ReadUInt16BigEndian(edid[8..]);
        char Letter(int value) => value is >= 1 and <= 26 ? (char)('A' + value - 1) : '?';
        string manufacturer = new([Letter((id >> 10) & 0x1F), Letter((id >> 5) & 0x1F), Letter(id & 0x1F)]);
        string product = BinaryPrimitives.ReadUInt16LittleEndian(edid[10..]).ToString("X4", CultureInfo.InvariantCulture);
        int? week = edid[16] is >= 1 and <= 54 ? edid[16] : null;
        int? year = edid[17] > 0 ? 1990 + edid[17] : null;
        string version = $"{edid[18]}.{edid[19]}";

        byte input = edid[20];
        bool digital = (input & 0x80) != 0;
        int? depth = null;
        string? iface = null;
        if (digital && (edid[18] > 1 || edid[19] >= 4))
        {
            depth = ((input >> 4) & 0x7) switch { 1 => 6, 2 => 8, 3 => 10, 4 => 12, 5 => 14, 6 => 16, _ => null };
            iface = (input & 0xF) switch { 1 => "DVI", 2 or 3 => "HDMI", 4 => "MDDI", 5 => "DisplayPort", _ => null };
        }
        int? widthCm = edid[21] > 0 ? edid[21] : null, heightCm = edid[22] > 0 ? edid[22] : null;
        double? gamma = edid[23] != 0xFF ? (edid[23] + 100) / 100.0 : null;

        string? name = null;
        int? nativeW = null, nativeH = null, minV = null, maxV = null;
        double? refresh = null;
        for (int offset = 54; offset <= 108; offset += 18)
        {
            var d = edid.Slice(offset, 18);
            int pixelClock = BinaryPrimitives.ReadUInt16LittleEndian(d);
            if (pixelClock != 0)
            {
                if (nativeW != null) continue;   // o primeiro detalhado é o modo preferido
                int hActive = d[2] | ((d[4] >> 4) << 8), hBlank = d[3] | ((d[4] & 0xF) << 8);
                int vActive = d[5] | ((d[7] >> 4) << 8), vBlank = d[6] | ((d[7] & 0xF) << 8);
                if (hActive == 0 || vActive == 0) continue;
                nativeW = hActive;
                nativeH = vActive;
                refresh = pixelClock * 10_000.0 / ((double)(hActive + hBlank) * (vActive + vBlank));
                continue;
            }
            switch (d[3])
            {
                case 0xFC:
                    name = DescriptorText(d[5..]);
                    break;
                case 0xFD:
                    byte flags = d[4];
                    minV = d[5] + ((flags & 0x3) == 0x3 ? 255 : 0);
                    maxV = d[6] + ((flags & 0x2) != 0 ? 255 : 0);
                    break;
            }
        }
        return new EdidInfo(manufacturer, product, name, week, year, version, widthCm, heightCm, digital, depth, iface,
            nativeW, nativeH, refresh, minV, maxV, gamma, edid[126]);
    }

    private static string? DescriptorText(ReadOnlySpan<byte> text)
    {
        var sb = new StringBuilder();
        foreach (byte b in text)
        {
            if (b == 0x0A || b == 0) break;
            if (b is >= 0x20 and < 0x7F) sb.Append((char)b);
        }
        string value = sb.ToString().Trim();
        return value.Length > 0 ? value : null;
    }

    /// <summary>Diagonal em polegadas a partir do tamanho da imagem em centímetros.</summary>
    public static double? DiagonalInches(int? widthCm, int? heightCm) =>
        widthCm is > 0 && heightCm is > 0 ? Math.Sqrt(widthCm.Value * widthCm.Value + heightCm.Value * heightCm.Value) / 2.54 : null;

    /// <summary>"DISPLAY\SAM105C\5&amp;17156835&amp;0&amp;UID4353_0" (WmiMonitorID) → chave do dispositivo sem o sufixo "_0".</summary>
    public static string? MonitorKeyFromWmi(string? instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName)) return null;
        string key = instanceName.Trim();
        int underscore = key.LastIndexOf('_');
        if (underscore > key.LastIndexOf('\\') && key[(underscore + 1)..].All(char.IsAsciiDigit)) key = key[..underscore];
        return key.ToUpperInvariant();
    }

    /// <summary>"\\?\DISPLAY#SAM105C#5&amp;17156835&amp;0&amp;UID4353#{e6f07b5f-…}" (EnumDisplayDevices) → "DISPLAY\SAM105C\5&amp;17156835&amp;0&amp;UID4353".</summary>
    public static string? MonitorKeyFromInterface(string? deviceInterface)
    {
        if (string.IsNullOrWhiteSpace(deviceInterface)) return null;
        string path = deviceInterface.StartsWith(@"\\?\", StringComparison.Ordinal) ? deviceInterface[4..] : deviceInterface;
        var parts = path.Split('#');
        return parts.Length >= 3 ? string.Join('\\', parts[..3]).ToUpperInvariant() : null;
    }

    public static string? MonitorVendor(string? pnpId) => pnpId?.Trim().ToUpperInvariant() switch
    {
        "AOC" => "AOC",
        "SAM" or "SEC" => "Samsung",
        "DEL" => "Dell",
        "GSM" => "LG",
        "LGD" => "LG Display",
        "AUS" => "ASUS",
        "ACR" or "ACI" => "Acer",
        "BNQ" => "BenQ",
        "MSI" => "MSI",
        "HWP" or "HPN" => "HP",
        "LEN" => "Lenovo",
        "VSC" => "ViewSonic",
        "PHL" => "Philips",
        "GBT" => "Gigabyte",
        "SNY" => "Sony",
        "APP" => "Apple",
        "BOE" => "BOE",
        "AUO" => "AU Optronics",
        "SHP" => "Sharp",
        "CMN" => "Innolux",
        "IVM" => "iiyama",
        "NEC" => "NEC",
        "EIZ" => "EIZO",
        "XMI" => "Xiaomi",
        "HKC" => "HKC",
        "KTC" => "KTC",
        "PNP" or null or "" => null,
        { } other => other
    };

    /// <summary>D3DKMDT_VIDEO_OUTPUT_TECHNOLOGY de WmiMonitorConnectionParams.</summary>
    public static string? VideoOutput(uint? technology) => technology switch
    {
        0 => "VGA",
        1 => "S-Video",
        2 => "Composite",
        3 => "Component",
        4 => "DVI",
        5 => "HDMI",
        6 => "LVDS",
        10 => "DisplayPort",
        11 => "eDP",
        15 => "Miracast",
        _ => null   // 0x80000000 = tela interna: o texto traduzido fica com quem monta a tela
    };

    // ------------------------------------------------------------------ NVMe

    public static NvmeHealth? ParseNvmeHealth(ReadOnlySpan<byte> log)
    {
        if (log.Length < 512) return null;
        int kelvin = BinaryPrimitives.ReadUInt16LittleEndian(log[1..]);
        return new NvmeHealth(log[0], kelvin is > 0 and < ushort.MaxValue ? (int)Math.Round(kelvin - 273.15) : null, log[3], log[4], log[5],
            BinaryPrimitives.ReadUInt128LittleEndian(log[32..]), BinaryPrimitives.ReadUInt128LittleEndian(log[48..]),
            BinaryPrimitives.ReadUInt128LittleEndian(log[112..]), BinaryPrimitives.ReadUInt128LittleEndian(log[128..]),
            BinaryPrimitives.ReadUInt128LittleEndian(log[144..]), BinaryPrimitives.ReadUInt128LittleEndian(log[160..]));
    }

    // ------------------------------------------------------------------ Formatação

    /// <summary>Tamanho legível: binário (memória, VRAM) ou decimal (discos), como o Windows mostra cada um.</summary>
    public static string Bytes(double bytes, bool binary, CultureInfo culture)
    {
        double unit = binary ? 1024 : 1000;
        string[] labels = ["B", "KB", "MB", "GB", "TB", "PB"];
        int i = 0;
        while (bytes >= unit * 0.995 && i < labels.Length - 1)
        {
            bytes /= unit;
            i++;
        }
        string format = bytes >= 100 || i == 0 ? "0" : bytes >= 10 ? "0.#" : "0.##";
        return $"{bytes.ToString(format, culture)} {labels[i]}";
    }

    public static string Clock(double mhz, CultureInfo culture) =>
        mhz >= 1000 ? $"{(mhz / 1000).ToString("0.00", culture)} GHz" : $"{mhz.ToString("0", culture)} MHz";

    public static string Resolution(int width, int height) => $"{width} × {height}";
}
