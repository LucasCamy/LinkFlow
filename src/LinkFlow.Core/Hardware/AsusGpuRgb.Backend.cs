using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LinkFlow.Core;

/// <summary>Internal seam for deterministic protocol tests. Test implementations never load an SDK or touch hardware.</summary>
internal interface IAsusGpuRgbBackend : IDisposable
{
    /// <summary>Cached metadata only. Older/fake backends may omit it without affecting RGB availability.</summary>
    string? LibraryVersion => null;
    int EnsureInitialized(out bool nativeInitializationCalled);
    int EnumeratePhysicalGpus(IntPtr[] handles, out int count);
    int GetPciIdentifiers(IntPtr gpu, out uint device, out uint subsystem);
    int Transfer(IntPtr gpu, bool read, byte command, byte[] data, int count);
}

public sealed partial class AsusGpuRgb
{
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct NvI2cInfo
    {
        [FieldOffset(0)] public uint Version;
        [FieldOffset(4)] public uint DisplayMask;
        [FieldOffset(8)] public byte IsDdcPort;
        [FieldOffset(9)] public byte DeviceAddress;
        [FieldOffset(16)] public IntPtr RegisterAddress;
        [FieldOffset(24)] public uint RegisterSize;
        [FieldOffset(32)] public IntPtr Data;
        [FieldOffset(40)] public uint DataSize;
        [FieldOffset(44)] public uint DeprecatedSpeed;
        [FieldOffset(48)] public uint Speed;
        [FieldOffset(52)] public byte PortId;
        [FieldOffset(56)] public uint IsPortIdSet;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiEnumPhysicalGpus([Out] IntPtr[] handles, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiGetPciIdentifiers(IntPtr gpu, out uint device, out uint subsystem, out uint revision, out uint externalDevice);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiI2c(IntPtr gpu, ref NvI2cInfo info, ref uint reserved);

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr QueryInterface(uint id);

    private static T GetFunction<T>(uint id) where T : Delegate
    {
        var address = QueryInterface(id);
        if (address == IntPtr.Zero) throw new EntryPointNotFoundException($"Interface NVAPI 0x{id:X8} indisponível.");
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private sealed class NativeBackend : IAsusGpuRgbBackend
    {
        private readonly byte[] _command = GC.AllocateArray<byte>(1, pinned: true);
        private NvApiCall? _unload;
        private NvApiEnumPhysicalGpus? _enumerate;
        private NvApiGetPciIdentifiers? _pci;
        private NvApiI2c? _read, _write;
        private bool _libraryVersionRead;
        private string? _libraryVersion;
        public string? LibraryVersion => _libraryVersion;

        public int EnsureInitialized(out bool nativeInitializationCalled)
        {
            ReadLibraryVersionOnce();
            nativeInitializationCalled = _unload == null;
            if (nativeInitializationCalled)
            {
                var unload = GetFunction<NvApiCall>(0xD22BDD7E);
                int status = GetFunction<NvApiCall>(0x0150E828)();
                if (status != 0) return status;
                // One paired reference for this backend lifetime; re-probing does not repeatedly initialize/unload NVAPI.
                _unload = unload;
            }
            _enumerate ??= GetFunction<NvApiEnumPhysicalGpus>(0xE5AC921F);
            _pci ??= GetFunction<NvApiGetPciIdentifiers>(0x2DDFB66E);
            _read ??= GetFunction<NvApiI2c>(0x4D7B0709);
            _write ??= GetFunction<NvApiI2c>(0x283AC65A);
            return 0;
        }

        private void ReadLibraryVersionOnce()
        {
            if (_libraryVersionRead) return;
            _libraryVersionRead = true;
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvapi64.dll");
                string? version = FileVersionInfo.GetVersionInfo(path).FileVersion?.Trim();
                if (!string.IsNullOrWhiteSpace(version) && !version.Any(char.IsControl))
                    _libraryVersion = version[..Math.Min(version.Length, 128)];
            }
            catch (Exception) { _libraryVersion = null; } // Optional file metadata cannot turn into a GPU/SDK failure.
        }

        public int EnumeratePhysicalGpus(IntPtr[] handles, out int count) => _enumerate!(handles, out count);
        public int GetPciIdentifiers(IntPtr gpu, out uint device, out uint subsystem) =>
            _pci!(gpu, out device, out subsystem, out _, out _);

        public int Transfer(IntPtr gpu, bool read, byte command, byte[] data, int count)
        {
            _command[0] = command;
            var info = new NvI2cInfo
            {
                Version = I2cInfoVersion3, DisplayMask = 0, IsDdcPort = 0, DeviceAddress = AuraAddress,
                RegisterAddress = Marshal.UnsafeAddrOfPinnedArrayElement(_command, 0), RegisterSize = 1,
                Data = Marshal.UnsafeAddrOfPinnedArrayElement(data, 0), DataSize = (uint)count,
                DeprecatedSpeed = 0xFFFF, Speed = 0, PortId = GpuI2cPort, IsPortIdSet = 1
            };
            uint reserved = 0;
            int status = (read ? _read! : _write!)(gpu, ref info, ref reserved);
            GC.KeepAlive(_command);
            GC.KeepAlive(data);
            return status;
        }

        public void Dispose()
        {
            var unload = _unload;
            _unload = null;
            unload?.Invoke();
        }
    }
}
