using System.Runtime.InteropServices;
using LinkFlow.Core;

namespace LinkFlow.App.Services;

internal readonly record struct DisplayMode(int Width, int Height, int Refresh, int BitsPerPixel, bool Primary);

/// <summary>Monitor ativo como o Windows o enxerga agora: modo de vídeo atual e a placa que o alimenta.</summary>
internal sealed record ActiveDisplay(string MonitorKey, string Adapter, DisplayMode Mode);

/// <summary>Driver instalado para um dispositivo (Gerenciador de Dispositivos).</summary>
internal sealed record DeviceDriverInfo(string? Version, DateOnly? Date, string? Provider);

/// <summary>
/// Leituras do Windows que o WMI não traz (ou traz mal), todas sem administrador: propriedades de dispositivo
/// (link PCIe, driver, dispositivo pai), caches da CPU, tipo de firmware e o modo de vídeo de cada monitor.
/// </summary>
internal static class NativeDevices
{
    private const int CrSuccess = 0;
    private const int CrBufferSmall = 0x1A;
    private const uint DevpropTypeUInt32 = 0x07;
    private const uint DevpropTypeFileTime = 0x10;
    private const uint DevpropTypeString = 0x12;

    private static readonly Guid PciDevice = new("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62");
    private static readonly Guid DeviceDriver = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6");
    private static readonly Guid DeviceRelations = new("4340a6c5-93fa-4706-972c-7b648008a5a7");

    // ------------------------------------------------------------------ Propriedades de dispositivo (cfgmgr32)

    public static PcieLink? PcieLink(string? instanceId)
    {
        if (!Locate(instanceId, out uint node)) return null;
        int? Read(uint pid) => UInt32(node, PciDevice, pid) is { } v and > 0 and < 64 ? (int)v : null;
        var link = new PcieLink(Read(9), Read(10), Read(11), Read(12));
        return link.CurrentGen == null && link.MaxGen == null ? null : link;
    }

    public static string? Parent(string? instanceId) =>
        Locate(instanceId, out uint node) ? String(node, DeviceRelations, 8) : null;

    public static DeviceDriverInfo? Driver(string? instanceId)
    {
        if (!Locate(instanceId, out uint node)) return null;
        string? version = String(node, DeviceDriver, 3);
        DateOnly? date = FileTime(node, DeviceDriver, 2) is { } time ? DateOnly.FromDateTime(time) : null;
        string? provider = String(node, DeviceDriver, 9);
        return version == null && date == null && provider == null ? null : new DeviceDriverInfo(version, date, provider);
    }

    private static bool Locate(string? instanceId, out uint node)
    {
        node = 0;
        if (string.IsNullOrWhiteSpace(instanceId)) return false;
        try { return CM_Locate_DevNodeW(out node, instanceId, 0) == CrSuccess; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static byte[]? Property(uint node, Guid fmtid, uint pid, uint expectedType)
    {
        var key = new DevPropKey { FmtId = fmtid, Pid = pid };
        uint size = 0;
        int result = CM_Get_DevNode_PropertyW(node, ref key, out uint type, null, ref size, 0);
        if (result != CrBufferSmall || size == 0 || size > 4096 || type != expectedType) return null;
        var buffer = new byte[size];
        result = CM_Get_DevNode_PropertyW(node, ref key, out type, buffer, ref size, 0);
        return result == CrSuccess && type == expectedType ? buffer : null;
    }

    private static uint? UInt32(uint node, Guid fmtid, uint pid) =>
        Property(node, fmtid, pid, DevpropTypeUInt32) is { Length: >= 4 } b ? BitConverter.ToUInt32(b) : null;

    private static string? String(uint node, Guid fmtid, uint pid)
    {
        if (Property(node, fmtid, pid, DevpropTypeString) is not { Length: >= 2 } b) return null;
        string value = System.Text.Encoding.Unicode.GetString(b).TrimEnd('\0').Trim();
        return value.Length > 0 ? value : null;
    }

    private static DateTime? FileTime(uint node, Guid fmtid, uint pid)
    {
        if (Property(node, fmtid, pid, DevpropTypeFileTime) is not { Length: >= 8 } b) return null;
        long ticks = BitConverter.ToInt64(b);
        return ticks > 0 ? DateTime.FromFileTimeUtc(ticks) : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid FmtId;
        public uint Pid;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, out uint type, [Out] byte[]? buffer,
        ref uint size, uint flags);

    // ------------------------------------------------------------------ Caches e firmware

    public static IReadOnlyList<CacheLevel> ProcessorCaches()
    {
        try
        {
            uint length = 0;
            GetLogicalProcessorInformation(0, ref length);
            if (length == 0 || length > 1 << 20) return [];
            nint buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetLogicalProcessorInformation(buffer, ref length)) return [];
                var bytes = new byte[length];
                Marshal.Copy(buffer, bytes, 0, (int)length);
                return HardwareSpecs.ParseCaches(bytes, IntPtr.Size);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or OutOfMemoryException)
        {
            Log.Debug($"Caches da CPU: {ex.Message}");
            return [];
        }
    }

    /// <summary>true = UEFI, false = BIOS legado, null = não deu para saber.</summary>
    public static bool? IsUefi()
    {
        try
        {
            return GetFirmwareType(out int type) ? type switch { 1 => false, 2 => true, _ => null } : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformation(nint buffer, ref uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFirmwareType(out int firmwareType);

    // ------------------------------------------------------------------ Monitores ativos

    private const int EnumCurrentSettings = -1;
    private const uint AttachedToDesktop = 0x1;
    private const uint PrimaryDevice = 0x4;
    private const uint GetDeviceInterfaceName = 0x1;

    public static IReadOnlyList<ActiveDisplay> ActiveDisplays()
    {
        var list = new List<ActiveDisplay>();
        try
        {
            for (uint i = 0; i < 32; i++)
            {
                var adapter = new DisplayDevice { Cb = Marshal.SizeOf<DisplayDevice>() };
                if (!EnumDisplayDevicesW(null, i, ref adapter, 0)) break;
                if ((adapter.StateFlags & AttachedToDesktop) == 0) continue;

                var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
                if (!EnumDisplaySettingsW(adapter.DeviceName, EnumCurrentSettings, ref mode)) continue;
                var current = new DisplayMode((int)mode.PelsWidth, (int)mode.PelsHeight, (int)mode.DisplayFrequency, (int)mode.BitsPerPel,
                    (adapter.StateFlags & PrimaryDevice) != 0);

                for (uint m = 0; m < 8; m++)
                {
                    var monitor = new DisplayDevice { Cb = Marshal.SizeOf<DisplayDevice>() };
                    if (!EnumDisplayDevicesW(adapter.DeviceName, m, ref monitor, GetDeviceInterfaceName)) break;
                    if (HardwareSpecs.MonitorKeyFromInterface(monitor.DeviceId) is { } key)
                        list.Add(new ActiveDisplay(key, adapter.DeviceString.Trim(), current));
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Debug($"Monitores ativos: {ex.Message}");
        }
        return list;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;
        public int PositionX;
        public int PositionY;
        public uint DisplayOrientation;
        public uint DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel;
        public uint PelsWidth;
        public uint PelsHeight;
        public uint DisplayFlags;
        public uint DisplayFrequency;
        public uint IcmMethod;
        public uint IcmIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(string? device, uint index, ref DisplayDevice displayDevice, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(string device, int mode, ref DevMode devMode);
}
