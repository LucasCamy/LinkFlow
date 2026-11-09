using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace LinkFlow.Core;

/// <summary>
/// Controlador nativo e seguro para memórias Corsair Vengeance RGB DDR5 via SMBus AMD AM5 (PawnIO).
/// Utiliza o mutex global 'Global\Access_SMBUS.HTP.Method' (compartilhado com HWiNFO e LibreHardwareMonitor)
/// e trava rígida que bloqueia estritamente qualquer escrita nas faixas de SPD EEPROM (0x50-0x57).
/// </summary>
public sealed class CorsairDramRgb : IRgbDevice
{
    private const string GlobalMutexName = @"Global\Access_SMBUS.HTP.Method";
    private const string PiixModuleResource = "LibreHardwareMonitor.Resources.PawnIo.SmbusPIIX4.bin";

    private const int Read = 1, Write = 0;
    private const int ByteData = 2, WordData = 3, BlockData = 5;

    private const byte RegResetBuffer = 0x0B;
    private const byte RegSetBinaryData = 0x20;
    private const byte RegBinaryStart = 0x21;
    private const byte RegSwitchMode = 0x23;
    private const byte RegStatus = 0x30;
    private const byte RegGetBinaryData = 0x40;
    private const byte RegGetChecksum = 0x42;
    private const byte RegGetDeviceInfo = 0x61;
    private const byte RegWriteConfiguration = 0x82;
    private const byte IdEffectConfiguration = 1;
    private const byte IdColorData = 2;

    public const int VengeanceDdr5LedCount = 12;

    private readonly object _syncLock = new();
    private IntPtr _pawnHandle = IntPtr.Zero;
    private Mutex? _mutex;
    private bool _disposed;

    public bool IsAvailable { get; private set; }
    public string StatusMessage { get; private set; } = "Não inicializado";
    public List<byte> ModuleAddresses { get; } = new();
    public int ModuleCount => ModuleAddresses.Count;

    public string Name => ModuleAddresses.Count > 0
        ? $"Corsair Vengeance RGB DDR5 ({ModuleAddresses.Count} módulos)"
        : "Corsair Vengeance RGB DDR5";

    public CorsairDramRgb()
    {
    }

    public void Initialize()
    {
        lock (_syncLock)
        {
            try
            {
                if (!File.Exists(Native.LibraryPath))
                {
                    StatusMessage = "PawnIO não instalado em Program Files";
                    IsAvailable = false;
                    return;
                }

                // 1. Abrir handle do PawnIO
                int hr = Native.Open(out _pawnHandle);
                if (hr != 0 || _pawnHandle == IntPtr.Zero)
                {
                    StatusMessage = $"Requer permissão de Administrador (0x{hr:X8})";
                    IsAvailable = false;
                    return;
                }

                _mutex = new Mutex(false, GlobalMutexName);

                // 2. Carregar módulo AMD PIIX4 SMBus da LibreHardwareMonitorLib
                var blob = LoadModuleBlob();
                if (blob == null || blob.Length == 0)
                {
                    StatusMessage = "Módulo SmbusPIIX4 não encontrado";
                    IsAvailable = false;
                    return;
                }

                hr = Native.Load(_pawnHandle, blob, (nuint)blob.Length);
                if (hr != 0)
                {
                    StatusMessage = $"Falha ao carregar SmbusPIIX4 (0x{hr:X8})";
                    IsAvailable = false;
                    return;
                }

                // 3. Selecionar porta 0 do PIIX4 (barramento de DIMMs no AM5)
                using (var m = new Mutex(false, GlobalMutexName))
                {
                    if (m.WaitOne(TimeSpan.FromSeconds(3)))
                    {
                        try
                        {
                            Native.Execute(_pawnHandle, "ioctl_piix4_port_sel", new ulong[] { 0 }, 1, new ulong[1], 1, out _);
                        }
                        finally
                        {
                            m.ReleaseMutex();
                        }
                    }
                }

                // 4. Sondar endereços seguros de iluminação Corsair (0x58-0x5F e 0x18-0x1F - NUNCA 0x50-0x57)
                DetectModules();

                if (ModuleAddresses.Count > 0)
                {
                    IsAvailable = true;
                    StatusMessage = $"● Corsair Vengeance DDR5 ({ModuleAddresses.Count} módulos)";
                }
                else
                {
                    StatusMessage = "○ Nenhum módulo Corsair DDR5 detectado no barramento";
                    IsAvailable = false;
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Erro: {ex.Message}";
                IsAvailable = false;
            }
        }
    }

    private void DetectModules()
    {
        ModuleAddresses.Clear();
        var candidateAddresses = Enumerable.Range(0x58, 8).Concat(Enumerable.Range(0x18, 8)).Select(a => (byte)a);

        foreach (byte addr in candidateAddresses)
        {
            int r43 = ReadByte(addr, 0x43);
            if (r43 is 0x1A or 0x1B or 0x1C)
            {
                int r44 = ReadByte(addr, 0x44);
                if (r44 is 0x01 or 0x03 or 0x04)
                {
                    ModuleAddresses.Add(addr);
                }
            }
            Thread.Sleep(5);
        }
    }

    public bool SetColor(byte r, byte g, byte b, byte brightnessPercent)
    {
        if (!IsAvailable || _disposed || _pawnHandle == IntPtr.Zero || ModuleAddresses.Count == 0)
            return false;

        byte bright = (byte)(Math.Clamp((int)brightnessPercent, 0, 100) * 255 / 100);

        lock (_syncLock)
        {
            bool allOk = true;
            foreach (var addr in ModuleAddresses)
            {
                bool ok = SetModuleStaticColor(addr, r, g, b, bright);
                if (!ok) allOk = false;
            }
            return allOk;
        }
    }

    private bool SetModuleStaticColor(byte addr, byte r, byte g, byte b, byte brightness)
    {
        // 1. Definir efeito estático (Static Mode = 0x10)
        byte[] effectData = new byte[18];
        effectData[0] = 0x10; // Static Mode
        effectData[1] = 0;    // Speed
        effectData[2] = 1;    // Custom colors (não randômico)
        effectData[3] = 2;    // Direction
        effectData[4] = r; effectData[5] = g; effectData[6] = b; effectData[7] = brightness;
        effectData[8] = r; effectData[9] = g; effectData[10] = b; effectData[11] = brightness;

        if (!UploadConfiguration(addr, effectData, IdEffectConfiguration))
            return false;

        Thread.Sleep(15);

        // 2. Upload de dados de cor por LED (12 LEDs por módulo)
        byte[] colorData = new byte[VengeanceDdr5LedCount * 4];
        for (int i = 0; i < VengeanceDdr5LedCount; i++)
        {
            colorData[i * 4 + 0] = r;
            colorData[i * 4 + 1] = g;
            colorData[i * 4 + 2] = b;
            colorData[i * 4 + 3] = brightness;
        }

        return UploadConfiguration(addr, colorData, IdColorData);
    }

    private bool UploadConfiguration(byte addr, byte[] data, byte bufferId)
    {
        WriteByte(addr, RegResetBuffer, 0);
        WriteByte(addr, RegBinaryStart, 0);

        foreach (var b in data)
        {
            WriteByte(addr, RegSetBinaryData, b);
        }

        byte expectedCrc = Crc8(data);
        int readCrc = ReadByte(addr, RegGetChecksum);
        if (readCrc != expectedCrc)
            return false;

        WriteByte(addr, RegWriteConfiguration, bufferId);
        Thread.Sleep(5);

        // Aguardar pronto
        for (int i = 0; i < 20; i++)
        {
            int s = ReadByte(addr, RegStatus);
            if (s >= 0 && (s & 0x08) == 0) return true;
            Thread.Sleep(10);
        }

        return false;
    }

    private static byte Crc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
            }
        }
        return crc;
    }

    private int ReadByte(byte address, byte command)
    {
        var data = Xfer(address, Read, command, ByteData, null);
        return data is null ? -1 : data[0];
    }

    private bool WriteByte(byte address, byte command, byte value)
    {
        return Xfer(address, Write, command, ByteData, new byte[] { value }) is not null;
    }

    private byte[]? Xfer(byte address, int readWrite, byte command, int size, byte[]? data)
    {
        if (_disposed || _pawnHandle == IntPtr.Zero) return null;

        // TRAVA DE SEGURANÇA ABSOLUTA: Jamais tocar no SPD EEPROM (0x50-0x57)
        if (readWrite == Write && address is >= 0x50 and <= 0x57)
        {
            throw new InvalidOperationException($"BLOQUEIO DE SEGURANÇA: Tentativa de escrita proibida no endereço SPD 0x{address:X2}.");
        }

        var input = new ulong[9];
        input[0] = address;
        input[1] = (ulong)readWrite;
        input[2] = command;
        input[3] = (ulong)size;
        if (data != null)
        {
            var union = new byte[40];
            data.CopyTo(union, 0);
            Buffer.BlockCopy(union, 0, input, 4 * sizeof(ulong), Math.Min(40, data.Length));
        }

        if (_mutex == null || !_mutex.WaitOne(TimeSpan.FromSeconds(2))) return null;
        try
        {
            var output = new ulong[5];
            int hr = Native.Execute(_pawnHandle, "ioctl_smbus_xfer", input, (nuint)input.Length, output, (nuint)output.Length, out _);
            if (hr != 0) return null;
            var bytes = new byte[40];
            Buffer.BlockCopy(output, 0, bytes, 0, 40);
            return bytes;
        }
        catch (AbandonedMutexException)
        {
            return null;
        }
        finally
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }
    }

    private static byte[]? LoadModuleBlob()
    {
        try
        {
            var asm = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly;
            using var s = asm.GetManifestResourceStream(PiixModuleResource);
            if (s == null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_disposed) return;
            _disposed = true;

            if (_pawnHandle != IntPtr.Zero)
            {
                Native.Close(_pawnHandle);
                _pawnHandle = IntPtr.Zero;
            }

            _mutex?.Dispose();
            _mutex = null;
        }
    }

    private static class Native
    {
        public static readonly string LibraryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll");

        static Native()
        {
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);
            }
            catch { }
        }

        private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path) =>
            name == "PawnIOLib" && File.Exists(LibraryPath) ? NativeLibrary.Load(LibraryPath) : IntPtr.Zero;

        [DllImport("PawnIOLib", EntryPoint = "pawnio_open")]
        public static extern int Open(out IntPtr handle);

        [DllImport("PawnIOLib", EntryPoint = "pawnio_load")]
        public static extern int Load(IntPtr handle, byte[] blob, nuint size);

        [DllImport("PawnIOLib", EntryPoint = "pawnio_execute", CharSet = CharSet.Ansi)]
        public static extern int Execute(IntPtr handle, string name, ulong[] input, nuint inSize, ulong[] output, nuint outSize, out nuint returnSize);

        [DllImport("PawnIOLib", EntryPoint = "pawnio_close")]
        public static extern int Close(IntPtr handle);
    }
}
