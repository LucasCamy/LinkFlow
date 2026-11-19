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
    private const int ConnectionSettleMs = 1000;
    private static readonly int[] ResendDelaysMs = [0, 300, 900];
    private readonly object _syncLock = new();
    private readonly CancellationToken _lifetimeToken;
    private NativeSdk? _native;
    private bool _initialized;
    private bool _disposed;

    public bool IsAvailable { get; private set; }
    public EngineText? DiagnosticText { get; private set; }
    public string? Diagnostic => DiagnosticText?.ToPortuguese();
    public string Name => "Periféricos Logitech G";

    public LogitechPeripheralsRgb() { }

    public LogitechPeripheralsRgb(CancellationToken lifetimeToken) => _lifetimeToken = lifetimeToken;

    public void Initialize() => Initialize(legacy: false);

    private void Initialize(bool legacy)
    {
        lock (_syncLock)
        {
            if (_disposed || _initialized) return;

            try
            {
                using var process = Process.GetCurrentProcess();
                int sessionId = process.SessionId;
                if (sessionId == 0)
                {
                    Report(EngineText.Of(EngineTexts.GHubNeedsSession));
                    return;
                }

                // O G HUB recusa o SDK de um app elevado quando ele mesmo roda como usuário comum. Nem tenta:
                // a tentativa só deixa os periféricos esperando uma cor que nunca chega.
                if (IsBlockedByElevation(Environment.IsPrivilegedProcess, IsAgentElevatedInSession(sessionId)))
                {
                    ReleaseSdk();
                    Report(ElevatedDiagnostic);
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
                        Report(EngineText.Of(EngineTexts.GHubArch));
                        return;
                    }

                    string? sdkPath = FindSdk(sdkFile);
                    if (sdkPath == null)
                    {
                        Report(EngineText.Of(EngineTexts.GHubSdkMissing));
                        return;
                    }
                    _native = new NativeSdk(sdkPath);
                }

                _initialized = _native.Initialize(legacy);
                if (!_initialized)
                {
                    ReleaseSdk();
                    Report(IsAgentInSession(sessionId) switch
                    {
                        true => EngineText.Of(EngineTexts.GHubAgentNoSdk),
                        false => EngineText.Of(EngineTexts.GHubOpenHub),
                        _ => EngineText.Of(EngineTexts.GHubNotStarted)
                    });
                    return;
                }

                if (_lifetimeToken.WaitHandle.WaitOne(ConnectionSettleMs))
                {
                    ReleaseSdk();
                    return;
                }
                if (!_native.SelectTarget(AllDeviceTypes))
                {
                    Fail(EngineText.Of(EngineTexts.GHubSelectRefused));
                    return;
                }
                DiagnosticText = null;
                IsAvailable = true;
            }
            catch (BadImageFormatException ex) { Fail(EngineText.Of(EngineTexts.GHubIncompatible), ex); }
            catch (DllNotFoundException ex) { Fail(EngineText.Of(EngineTexts.GHubDllLoad), ex); }
            catch (EntryPointNotFoundException ex) { Fail(EngineText.Of(EngineTexts.GHubEntryPoint), ex); }
            catch (Exception ex) { Fail(EngineText.Of(EngineTexts.GHubInitError), ex); }
        }
    }

    public bool SetColor(byte r, byte g, byte b, byte brightnessPercent)
    {
        double scale = Math.Min((int)brightnessPercent, 100) / 255.0;
        lock (_syncLock)
        {
            if (_disposed || !IsAvailable || _lifetimeToken.IsCancellationRequested) return false;

            try
            {
                if (_native!.SetLighting((int)Math.Round(r * scale), (int)Math.Round(g * scale), (int)Math.Round(b * scale)))
                    return true;

                using var process = Process.GetCurrentProcess();
                Fail(IsAgentInSession(process.SessionId) == false
                    ? EngineText.Of(EngineTexts.GHubAgentLeft)
                    : EngineText.Of(EngineTexts.GHubColorRefused));
            }
            catch (Exception ex) { Fail(EngineText.Of(EngineTexts.GHubSendError), ex); }
            return false;
        }
    }

    public (byte R, byte G, byte B, byte Brightness)? Resend(
        Func<(byte R, byte G, byte B, byte Brightness)?> readColor, Action<int, int> onAttempt)
    {
        for (int index = 0; index < ResendDelaysMs.Length && !_lifetimeToken.IsCancellationRequested; index++)
        {
            if (readColor() == null) break;
            onAttempt(index + 1, ResendDelaysMs.Length);
            Disconnect();
            if (_lifetimeToken.WaitHandle.WaitOne(ResendDelaysMs[index])) break;

            // A última tentativa também cobre versões do SDK que só aceitam a inicialização padrão.
            Initialize(legacy: index == ResendDelaysMs.Length - 1);
            if (!IsAvailable || _lifetimeToken.IsCancellationRequested) continue;
            if (readColor() is not { } color) break;
            if (SetColor(color.R, color.G, color.B, color.Brightness)) return color;
        }
        return null;
    }

    public void Disconnect()
    {
        lock (_syncLock)
        {
            ReleaseSdk();
            DiagnosticText = null;
        }
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseSdk();
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

    private void Fail(EngineText diagnostic, Exception? exception = null)
    {
        ReleaseSdk();
        Report(diagnostic, exception);
    }

    private void ReleaseSdk()
    {
        CloseSession();
        _native?.Dispose();
        _native = null;
    }

    private void Report(EngineText diagnostic, Exception? exception = null)
    {
        string text = diagnostic.ToPortuguese();
        if (Diagnostic == text) return;
        DiagnosticText = diagnostic;
        Log.Warn(exception == null ? text : $"{text} {exception.Message}");
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

    public static EngineText ElevatedDiagnostic => EngineText.Of(EngineTexts.GHubElevated);

    /// <summary>Elevado, o SDK só funciona se o agente do G HUB também estiver elevado.</summary>
    public static bool IsBlockedByElevation(bool selfElevated, bool? agentElevated) => selfElevated && agentElevated != true;

    /// <summary>True se algum agente do G HUB nesta sessão roda elevado; null se não foi possível ler.</summary>
    private static bool? IsAgentElevatedInSession(int sessionId)
    {
        try
        {
            var agents = Process.GetProcessesByName("lghub_agent");
            try
            {
                bool unknown = false;
                foreach (var agent in agents)
                {
                    try
                    {
                        if (agent.SessionId != sessionId) continue;
                        switch (IsElevated(agent.Id))
                        {
                            case true: return true;
                            case null: unknown = true; break;
                        }
                    }
                    catch (InvalidOperationException) { /* O agente encerrou entre a enumeração e a leitura. */ }
                    catch (Win32Exception) { unknown = true; }
                }
                return unknown ? null : false;
            }
            finally { foreach (var agent in agents) agent.Dispose(); }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return null; }
    }

    private static bool? IsElevated(int processId)
    {
        const uint QueryLimitedInformation = 0x1000, TokenQuery = 0x0008;
        const int TokenElevationClass = 20;
        nint process = OpenProcess(QueryLimitedInformation, false, processId);
        if (process == 0) return null;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out nint token)) return null;
            try
            {
                return GetTokenInformation(token, TokenElevationClass, out int elevated, sizeof(int), out _) ? elevated != 0 : null;
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint OpenProcess(uint access, bool inherit, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(nint token, int infoClass, out int info, int length, out int returned);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

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
                if (NativeLibrary.TryGetExport(_library, "LogiLedInit", out var legacyInit))
                    _legacyInit = Marshal.GetDelegateForFunctionPointer<Init>(legacyInit);
                if (_namedInit == null && _legacyInit == null)
                    throw new EntryPointNotFoundException("LogiLedInitWithName / LogiLedInit");
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

        public bool Initialize(bool legacy) => (legacy || _namedInit == null) && _legacyInit != null
            ? _legacyInit() : _namedInit!("LinkFlow");
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
