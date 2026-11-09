using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LinkFlow.Core;

/// <summary>
/// Controlador de iluminação para periféricos Logitech G (teclado G915 X, mouse Lightspeed, headset)
/// via Logitech LED Illumination SDK (oficial e nativo através do G HUB).
/// </summary>
public sealed class LogitechPeripheralsRgb : IRgbDevice
{
    private static readonly string SdkPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "LGHUB", "sdks", "sdk_legacy_led_x64.dll");

    private readonly object _syncLock = new();
    private bool _initialized;
    private bool _disposed;

    public bool IsAvailable { get; private set; }
    public string Name => "Periféricos Logitech G (Teclado, Mouse e Headset)";

    public LogitechPeripheralsRgb()
    {
    }

    public void Initialize()
    {
        lock (_syncLock)
        {
            if (_initialized) return;

            try
            {
                if (!File.Exists(SdkPath))
                {
                    IsAvailable = false;
                    return;
                }

                bool ok = Native.LogiLedInit();
                // Todos os tipos: monocromático, RGB por zona e RGB por tecla (G915 X)
                if (ok) { try { Native.LogiLedSetTargetDevice(0x07); } catch { } }
                if (ok)
                {
                    _initialized = true;
                    IsAvailable = true;
                }
                else
                {
                    IsAvailable = false;
                }
            }
            catch
            {
                IsAvailable = false;
            }
        }
    }

    public bool SetColor(byte r, byte g, byte b, byte brightnessPercent)
    {
        if (!IsAvailable || _disposed || !_initialized) return false;

        float factor = Math.Clamp(brightnessPercent / 100f, 0f, 1f);
        int rPct = (int)Math.Round((r / 255.0) * factor * 100);
        int gPct = (int)Math.Round((g / 255.0) * factor * 100);
        int bPct = (int)Math.Round((b / 255.0) * factor * 100);

        lock (_syncLock)
        {
            try
            {
                return Native.LogiLedSetLighting(rPct, gPct, bPct);
            }
            catch
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_disposed) return;
            _disposed = true;

            if (_initialized)
            {
                try
                {
                    Native.LogiLedShutdown();
                }
                catch { }
                _initialized = false;
            }
        }
    }

    private static class Native
    {
        static Native()
        {
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);
            }
            catch { }
        }

        private static IntPtr Resolve(string name, System.Reflection.Assembly asm, DllImportSearchPath? path) =>
            name == "LogitechLed" && File.Exists(SdkPath) ? NativeLibrary.Load(SdkPath) : IntPtr.Zero;

        [DllImport("LogitechLed", CallingConvention = CallingConvention.Cdecl, EntryPoint = "LogiLedInit")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool LogiLedInit();

        [DllImport("LogitechLed", CallingConvention = CallingConvention.Cdecl, EntryPoint = "LogiLedSetLighting")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool LogiLedSetLighting(int redPercent, int greenPercent, int bluePercent);

        [DllImport("LogitechLed", CallingConvention = CallingConvention.Cdecl, EntryPoint = "LogiLedSetTargetDevice")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool LogiLedSetTargetDevice(int targetDevice);

        [DllImport("LogitechLed", CallingConvention = CallingConvention.Cdecl, EntryPoint = "LogiLedShutdown")]
        public static extern void LogiLedShutdown();
    }
}
