using System.Runtime.InteropServices;
using System.Text;

namespace LinkFlow.Core;

public sealed class AsusGpuRgb : IRgbDevice
{
    private const ushort NvidiaVendor = 0x10DE;
    private const ushort AsusVendor = 0x1043;
    private const byte AuraAddress = 0x67 << 1;
    private const byte GpuI2cPort = 1;
    private const uint I2cInfoVersion3 = (3u << 16) | 64;

    // ENE SMBus, conforme o controlador ENESMBus e o transporte NVAPI do OpenRGB.
    // O firmware AUMA0-E6K5-1110 usa a tabela de cores diretas V2.
    private const ushort FirmwareRegister = 0x1000;
    private const ushort LedCountRegister = 0x1C03;
    private const ushort DirectModeRegister = 0x8020;
    private const ushort EffectModeRegister = 0x8021;
    private const ushort ApplyRegister = 0x80A0;
    private const ushort DirectColorRegister = 0x8100;
    private const ushort EffectColorRegister = 0x8160;
    private const int MaxLedCount = (EffectColorRegister - DirectColorRegister) / 3;
    private const byte SelectCommand = 0x00;
    private const byte WriteByteCommand = 0x01;
    private const byte WriteBlockCommand = 0x03;
    private const byte ReadByteCommand = 0x81;

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

    private readonly object _syncLock = new();
    private readonly byte[] _command = GC.AllocateArray<byte>(1, pinned: true);
    private readonly byte[] _data = GC.AllocateArray<byte>(4, pinned: true);
    private IntPtr _gpu;
    private NvApiCall? _unload;
    private NvApiI2c? _read;
    private NvApiI2c? _write;
    private bool _directConfigured;
    private bool _byteWrites;

    public bool IsAvailable { get; private set; }
    public string ControllerName { get; private set; } = string.Empty;
    public int LedCount { get; private set; }
    public string? Diagnostic { get; private set; }
    public bool NeedsInitializationRetry { get; private set; }
    public string Name => ControllerName;

    public void Initialize()
    {
        lock (_syncLock)
        {
            ResetController();
            Diagnostic = null;
            if (IntPtr.Size != 8)
            {
                Diagnostic = "O controle Aura da GPU requer o LinkFlow x64.";
                return;
            }

            try
            {
                if (_unload is null)
                {
                    var unload = GetFunction<NvApiCall>(0xD22BDD7E);
                    int initStatus = GetFunction<NvApiCall>(0x0150E828)();
                    if (initStatus != 0)
                    {
                        NeedsInitializationRetry = IsRetryableStatus(initStatus);
                        Diagnostic = $"Falha ao iniciar o NVAPI (código {initStatus}).";
                        return;
                    }

                    _unload = unload;
                }

                var enumerate = GetFunction<NvApiEnumPhysicalGpus>(0xE5AC921F);
                var pci = GetFunction<NvApiGetPciIdentifiers>(0x2DDFB66E);
                _read = GetFunction<NvApiI2c>(0x4D7B0709);
                _write = GetFunction<NvApiI2c>(0x283AC65A);

                var handles = new IntPtr[64];
                int status = enumerate(handles, out int count);
                if (status != 0)
                {
                    NeedsInitializationRetry = IsRetryableStatus(status);
                    Diagnostic = $"Falha ao listar as GPUs NVIDIA (NVAPI {status}).";
                    return;
                }

                string? pciError = null;
                for (int index = 0; index < Math.Min(count, handles.Length); index++)
                {
                    var handle = handles[index];
                    status = pci(handle, out uint device, out uint subsystem, out _, out _);
                    if (status != 0)
                    {
                        NeedsInitializationRetry |= IsRetryableStatus(status);
                        pciError ??= $"Falha ao identificar o adaptador PCI (NVAPI {status}).";
                        continue;
                    }

                    if ((ushort)device != NvidiaVendor || (ushort)subsystem != AsusVendor) continue;

                    _gpu = handle;
                    if (!ProbeController()) continue;

                    Diagnostic = null;
                    NeedsInitializationRetry = false;
                    IsAvailable = true;
                    Log.Info($"Aura da GPU detectado em PCI {device:X8}/{subsystem:X8}: {ControllerName}, {LedCount} LEDs.");
                    return;
                }

                _gpu = IntPtr.Zero;
                Diagnostic ??= pciError ?? "Nenhuma GPU NVIDIA da ASUS encontrada para o controle Aura.";
            }
            catch (DllNotFoundException)
            {
                Diagnostic = "A biblioteca NVAPI do driver NVIDIA está indisponível.";
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or BadImageFormatException or SEHException)
            {
                Diagnostic = $"Não foi possível acessar o controlador Aura via NVAPI. {ex.Message}";
                _gpu = IntPtr.Zero;
            }
        }
    }

    private static T GetFunction<T>(uint id) where T : Delegate
    {
        var address = QueryInterface(id);
        if (address == IntPtr.Zero) throw new EntryPointNotFoundException($"Interface NVAPI 0x{id:X8} indisponível.");
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private bool ProbeController()
    {
        for (byte index = 0; index < 16; index++)
        {
            byte command = (byte)(0xA0 + index);
            if (!Transfer(_read, command, 1, command)) return false;
            if (_data[0] != index) return Fail("A GPU ASUS não respondeu com a assinatura esperada do controlador Aura.");
        }

        // São 15 caracteres de identificação. O último byte do campo é reservado ao terminador.
        Span<byte> firmwareBytes = stackalloc byte[15];
        for (int index = 0; index < firmwareBytes.Length; index++)
        {
            if (!ReadRegister((ushort)(FirmwareRegister + index), out firmwareBytes[index])) return false;
            if (firmwareBytes[index] is < 0x20 or > 0x7E) return Fail("A identificação do controlador Aura está ilegível.");
        }

        string firmware = Encoding.ASCII.GetString(firmwareBytes);
        if (firmware is not ("AUMA0-E6K5-0107" or "AUMA0-E6K5-1107" or "AUMA0-E6K5-1110" or "AUMA0-E6K5-1111"
            or "AUMA0-E6K5-0008" or "AUMA0-E6K5-1113" or "AUMA0-E6K5-1114"))
        {
            return Fail($"Firmware Aura sem protocolo confirmado ({firmware}).");
        }

        ControllerName = firmware;
        if (!ReadRegister(LedCountRegister, out byte count)) return false;
        if (count is 0 or > MaxLedCount) return Fail($"Quantidade de LEDs inválida no controlador Aura ({count}).");
        LedCount = count;
        return true;
    }

    public bool SetColor(byte r, byte g, byte b, byte brightnessPercent)
    {
        lock (_syncLock)
        {
            if (!IsAvailable || !EnsureDirectMode(out bool enteringDirectMode)) return false;

            int brightness = Math.Min((int)brightnessPercent, 100);
            ReadOnlySpan<byte> color = [(byte)(r * brightness / 100), (byte)(b * brightness / 100), (byte)(g * brightness / 100)];
            for (int led = 0; led < LedCount; led++)
            {
                ushort register = (ushort)(DirectColorRegister + led * color.Length);
                if (!WriteLed(register, color)) return false;
            }

            if (enteringDirectMode)
            {
                // Alguns controladores só exibem a primeira cor após reaplicar o modo direto.
                if (!ApplyDirectMode() || !ConfirmDirectColor(color)) return false;
                _directConfigured = true;
            }

            Diagnostic = null;
            return true;
        }
    }

    private bool EnsureDirectMode(out bool enteringDirectMode)
    {
        enteringDirectMode = false;
        if (_directConfigured)
        {
            if (!ReadRegister(DirectModeRegister, out byte direct)) return false;
            if (direct == 1) return true;
        }

        _directConfigured = false;
        enteringDirectMode = true;
        if (ReadRegister(DirectModeRegister, out byte previousDirect) &&
            ReadRegister(EffectModeRegister, out byte previousMode))
        {
            Log.Info($"RGB GPU: estado antes de configurar o controlador: direto={previousDirect}, modo=0x{previousMode:X2}.");
        }
        if (!IsAvailable) return false;

        // Sai de Off ou de um efeito interno inválido antes de entregar o controle ao software.
        return WriteRegister(EffectModeRegister, 1) && WriteRegister(ApplyRegister, 1) && ApplyDirectMode();
    }

    private bool ApplyDirectMode() => WriteRegister(DirectModeRegister, 1) && WriteRegister(ApplyRegister, 1);

    private bool ConfirmDirectColor(ReadOnlySpan<byte> color)
    {
        if (!ReadRegister(DirectModeRegister, out byte direct)) return false;
        if (direct != 1) return Fail("O controlador Aura não manteve o modo direto após aplicar a cor.");

        ushort first = DirectColorRegister;
        ushort last = (ushort)(first + (LedCount - 1) * color.Length);
        for (int channel = 0; channel < color.Length; channel++)
        {
            if (!ReadRegister((ushort)(first + channel), out byte firstValue) ||
                !ReadRegister((ushort)(last + channel), out byte lastValue))
                return false;
            if (firstValue != color[channel] || lastValue != color[channel])
                return Fail("O controlador Aura devolveu uma cor diferente da enviada.");
        }

        return true;
    }

    private bool WriteLed(ushort register, ReadOnlySpan<byte> color)
    {
        if (!_byteWrites)
        {
            if (!SelectRegister(register)) return false;
            _data[0] = (byte)color.Length;
            color.CopyTo(_data.AsSpan(1));
            if (Transfer(_write, WriteBlockCommand, _data.Length, register)) return true;
            if (!IsAvailable) return false;
        }

        // Reposiciona cada byte caso a tentativa de bloco tenha avançado o ponteiro.
        for (int channel = 0; channel < color.Length; channel++)
        {
            if (!WriteRegister((ushort)(register + channel), color[channel])) return false;
        }

        _byteWrites = true;
        return true;
    }

    private bool ReadRegister(ushort register, out byte value)
    {
        bool ok = SelectRegister(register) && Transfer(_read, ReadByteCommand, 1, register);
        value = ok ? _data[0] : (byte)0;
        return ok;
    }

    private bool WriteRegister(ushort register, byte value)
    {
        if (!SelectRegister(register)) return false;
        _data[0] = value;
        return Transfer(_write, WriteByteCommand, 1, register);
    }

    private bool SelectRegister(ushort register)
    {
        _data[0] = (byte)(register >> 8);
        _data[1] = (byte)register;
        return Transfer(_write, SelectCommand, 2, register);
    }

    private bool Transfer(NvApiI2c? operation, byte command, int count, ushort register)
    {
        if (operation is null || _gpu == IntPtr.Zero) return Fail("A interface I²C da GPU está indisponível.");

        _command[0] = command;
        var info = new NvI2cInfo
        {
            Version = I2cInfoVersion3,
            DisplayMask = 0,
            IsDdcPort = 0,
            DeviceAddress = AuraAddress,
            RegisterAddress = Marshal.UnsafeAddrOfPinnedArrayElement(_command, 0),
            RegisterSize = 1,
            Data = Marshal.UnsafeAddrOfPinnedArrayElement(_data, 0),
            DataSize = (uint)count,
            DeprecatedSpeed = 0xFFFF,
            Speed = 0, // NVAPI_I2C_SPEED_DEFAULT
            PortId = GpuI2cPort,
            IsPortIdSet = 1
        };

        uint reserved = 0;
        int status = operation(_gpu, ref info, ref reserved);
        GC.KeepAlive(_command);
        GC.KeepAlive(_data);
        if (status == 0) return true;
        if (status is -6 or -8 or -10) IsAvailable = false; // Requer redetecção do adaptador.
        if (!IsAvailable) NeedsInitializationRetry |= IsRetryableStatus(status);
        return Fail($"Falha no Aura da GPU em 0x{register:X4} (comando 0x{command:X2}, NVAPI {status}).");
    }

    // Erro genérico de transporte, GPU indisponível, identificador inválido ou dispositivo ocupado.
    private static bool IsRetryableStatus(int status) => status is -1 or -6 or -8 or -10 or -108;

    private bool Fail(string diagnostic)
    {
        Diagnostic = diagnostic;
        return false;
    }

    private void ResetController()
    {
        IsAvailable = false;
        NeedsInitializationRetry = false;
        ControllerName = string.Empty;
        LedCount = 0;
        _gpu = IntPtr.Zero;
        _directConfigured = false;
        _byteWrites = false;
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            ResetController();
            var unload = _unload;
            _unload = null;
            unload?.Invoke();
        }
    }
}
