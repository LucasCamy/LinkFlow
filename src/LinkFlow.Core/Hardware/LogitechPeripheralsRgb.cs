using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace LinkFlow.Core;

/// <summary>Controla periféricos LIGHTSYNC pelo SDK instalado pelo Logitech G HUB.</summary>
public sealed class LogitechPeripheralsRgb : IRgbDevice
{
    private const int AllDeviceTypes = 0x07;
    private readonly object _syncLock = new();
    private NativeSdk? _native;
    private bool _initialized;
    private bool _disposed;

    public bool IsAvailable { get; private set; }
    public string? Diagnostic { get; private set; }
    public string Name => "Periféricos Logitech G";

    public void Initialize()
    {
        lock (_syncLock)
        {
            if (_disposed || IsAvailable) return;

            try
            {
                using var process = Process.GetCurrentProcess();
                int sessionId = process.SessionId;
                if (sessionId == 0)
                {
                    Report("O SDK Logitech precisa da sessão interativa do Windows.");
                    return;
                }

                if (_native == null)
                {
                    string? sdkFile = RuntimeInformation.ProcessArchitecture switch
                    {
                        Architecture.X64 => "sdk_legacy_led_x64.dll",
                        Architecture.X86 => "sdk_legacy_led_x86.dll",
                        _ => null
                    };
                    if (sdkFile == null)
                    {
                        Report("O SDK Logitech não está disponível para a arquitetura deste processo.");
                        return;
                    }

                    string? sdkPath = FindSdk(sdkFile);
                    if (sdkPath == null)
                    {
                        Report("SDK de iluminação não encontrado na instalação do Logitech G HUB.");
                        return;
                    }
                    _native = new NativeSdk(sdkPath);
                }

                _initialized = _native.Initialize();
                if (!_initialized)
                {
                    Report(IsAgentInSession(sessionId) switch
                    {
                        true => "Agente do G HUB em execução. O SDK não iniciou. Confira se jogos e aplicativos podem controlar a iluminação no G HUB.",
                        false => "SDK instalado. Abra o G HUB nesta sessão do Windows para conectar a iluminação.",
                        _ => "O SDK Logitech não iniciou. Confira o G HUB e a autorização de iluminação."
                    });
                    return;
                }

                if (!_native.SelectTarget(AllDeviceTypes))
                {
                    Fail("O SDK iniciou, mas o G HUB recusou a seleção dos dispositivos LIGHTSYNC.");
                    return;
                }

                Diagnostic = null;
                IsAvailable = true;
            }
            catch (BadImageFormatException ex) { Fail("O SDK de iluminação do G HUB é incompatível com este processo.", ex); }
            catch (DllNotFoundException ex) { Fail("O SDK do G HUB está instalado, mas o Windows não conseguiu carregar a DLL ou suas dependências.", ex); }
            catch (EntryPointNotFoundException ex) { Fail("O SDK instalado do G HUB não oferece as funções de iluminação necessárias.", ex); }
            catch (Exception ex) { Fail("O SDK Logitech retornou um erro ao iniciar a iluminação.", ex); }
        }
    }

    public bool SetColor(byte r, byte g, byte b, byte brightnessPercent)
    {
        double scale = Math.Min((int)brightnessPercent, 100) / 255.0;
        lock (_syncLock)
        {
            if (_disposed || !IsAvailable) return false;

            try
            {
                if (_native!.SetLighting((int)Math.Round(r * scale), (int)Math.Round(g * scale), (int)Math.Round(b * scale)))
                    return true;

                using var process = Process.GetCurrentProcess();
                Fail(IsAgentInSession(process.SessionId) == false
                    ? "O agente do G HUB saiu desta sessão do Windows. Abra o G HUB para reconectar."
                    : "O SDK iniciou, mas o G HUB não aceitou a cor. Confira a integração e a autorização de iluminação no G HUB.");
            }
            catch (Exception ex) { Fail("O SDK Logitech retornou um erro ao enviar a cor.", ex); }
            return false;
        }
    }

    public void Disconnect()
    {
        lock (_syncLock)
        {
            CloseSession();
            Diagnostic = null;
        }
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_disposed) return;
            _disposed = true;
            CloseSession();
            _native?.Dispose();
            _native = null;
        }
    }

    private void CloseSession()
    {
        IsAvailable = false;
        if (!_initialized) return;

        _initialized = false;
        try { _native!.Shutdown(); }
        catch (Exception ex) { Log.Debug($"SDK Logitech ao encerrar a sessão: {ex.Message}"); }
    }

    private void Fail(string diagnostic, Exception? exception = null)
    {
        CloseSession();
        Report(diagnostic, exception);
    }

    private void Report(string diagnostic, Exception? exception = null)
    {
        if (Diagnostic == diagnostic) return;
        Diagnostic = diagnostic;
        Log.Warn(exception == null ? diagnostic : $"{diagnostic} {exception.Message}");
    }

    private static string? FindSdk(string file)
    {
        // O wrapper oficial usa o ServerBinary registrado para a arquitetura do processo.
        try
        {
            var view = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? RegistryView.Registry64 : RegistryView.Registry32;
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = machine.OpenSubKey(@"SOFTWARE\Classes\CLSID\{A6519E67-7632-4375-AFDF-CAA889744403}\ServerBinary");
            string? registered = key?.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (registered != null && Path.IsPathFullyQualified(registered) && !registered.StartsWith(@"\\", StringComparison.Ordinal) &&
                string.Equals(Path.GetFileName(registered), file, StringComparison.OrdinalIgnoreCase) && File.Exists(registered))
                return registered;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            Log.Debug($"Registro do SDK Logitech: {ex.Message}");
        }

        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            string root = Environment.GetFolderPath(folder);
            if (!Path.IsPathFullyQualified(root)) continue;

            string path = Path.Combine(root, "LGHUB", "sdks", file);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static bool? IsAgentInSession(int sessionId)
    {
        try
        {
            var agents = Process.GetProcessesByName("lghub_agent");
            try
            {
                bool unknown = false;
                foreach (var agent in agents)
                {
                    try { if (agent.SessionId == sessionId && !agent.HasExited) return true; }
                    catch (InvalidOperationException) { /* O agente encerrou entre a enumeração e a leitura. */ }
                    catch (Win32Exception) { unknown = true; }
                }
                return unknown ? null : false;
            }
            finally { foreach (var agent in agents) agent.Dispose(); }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return null; }
    }

    private sealed class NativeSdk : IDisposable
    {
        private readonly nint _library;
        private readonly InitWithName? _namedInit;
        private readonly Init? _legacyInit;
        private readonly SetTargetDevice _target;
        private readonly SetColorPercent _color;
        private readonly EndSession _shutdown;

        public NativeSdk(string path)
        {
            _library = NativeLibrary.Load(path);
            try
            {
                if (NativeLibrary.TryGetExport(_library, "LogiLedInitWithName", out var namedInit))
                    _namedInit = Marshal.GetDelegateForFunctionPointer<InitWithName>(namedInit);
                else
                    _legacyInit = GetExport<Init>("LogiLedInit");
                _target = GetExport<SetTargetDevice>("LogiLedSetTargetDevice");
                _color = GetExport<SetColorPercent>("LogiLedSetLighting");
                _shutdown = GetExport<EndSession>("LogiLedShutdown");
            }
            catch
            {
                NativeLibrary.Free(_library);
                throw;
            }
        }

        private T GetExport<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

        public bool Initialize() => _namedInit != null ? _namedInit("LinkFlow") : _legacyInit!();
        public bool SelectTarget(int type) => _target(type);
        public bool SetLighting(int r, int g, int b) => _color(r, g, b);
        public void Shutdown() => _shutdown();
        public void Dispose() => NativeLibrary.Free(_library);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool Init();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool InitWithName([MarshalAs(UnmanagedType.LPStr)] string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool SetTargetDevice(int type);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool SetColorPercent(int red, int green, int blue);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void EndSession();
    }
}
