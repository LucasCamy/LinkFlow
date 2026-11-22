using System.Runtime.InteropServices;
using System.Text;
using System.Globalization;
using System.Diagnostics;

namespace LinkFlow.Core;

public sealed partial class AsusGpuRgb : IRgbDevice, IRgbControlStatusProvider
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

    private readonly object _syncLock = new();
    private readonly byte[] _data = GC.AllocateArray<byte>(4, pinned: true);
    private readonly IAsusGpuRgbBackend _backend;
    private IntPtr _gpu;
    private string? _gpuHandleContext;
    private bool _controlAvailable, _needsRetry, _initializing, _disposed;
    private string _controllerName = string.Empty;
    private int _ledCount;
    private EngineText? _diagnostic;
    private RgbAdapterObservation _adapterObservation;
    private DateTimeOffset? _lastAdapterEnumeration, _lastAdapterSeen, _lastOutputAttempt, _lastOutputAcceptedAt;
    private bool? _lastOutputAccepted;
    private long _initializationAttempts, _nativeFailures, _outputFailures, _consecutiveOutputFailures, _nativeSequence;
    private long? _lastOutputAcceptedNativeSequence;
    private RgbNativeOperationResult? _lastNativeOperation, _lastNativeFailure;
    private readonly RgbNativeOperationResult?[] _nativeFailureRing = new RgbNativeOperationResult?[RgbNativeFailureHistory.MaximumRecords];
    private int _nativeFailureHead, _nativeFailureCount;
    private bool _nativeFailureHistoryDirty;
    private RgbNativeFailureHistory _nativeFailureHistory = new();
    private RgbControlStatus _published = new();
    private bool _directConfigured;
    private bool _byteWrites;
    private int _blockWriteFailures;
    private const int BlockFailuresBeforeByteWrites = 3;

    // Acesso NVAPI que falha e volta (sem provar remoção física) repetia estas linhas milhares de vezes por dia:
    // só registra quando mudam
    private string? _announced;
    private string? _stateLogged;

    public AsusGpuRgb() : this(new NativeBackend()) { }
    internal AsusGpuRgb(IAsusGpuRgbBackend backend) => _backend = backend;

    public RgbControlStatus ControlStatus => Volatile.Read(ref _published);
    public bool IsAvailable => ControlStatus.ControlAvailable;
    public string ControllerName => ControlStatus.ControllerName;
    public int LedCount => ControlStatus.ControllerLedCount;
    public EngineText? DiagnosticText => ControlStatus.DiagnosticText;
    public string? Diagnostic => DiagnosticText?.ToPortuguese();
    public bool NeedsInitializationRetry => ControlStatus.InitializationRetryNeeded;
    public string Name => ControllerName;

    public void Initialize()
    {
        lock (_syncLock)
        {
            if (_disposed) return;
            _initializing = true;
            Increment(ref _initializationAttempts);
            Publish(); // The previous completed availability remains visible while probing.
            ResetController();
            try
            {
                if (IntPtr.Size != 8)
                {
                    _diagnostic = EngineText.Of(EngineTexts.GpuNeedsX64);
                    return;
                }
                int initStatus = _backend.EnsureInitialized(out bool nativeInitializationCalled);
                if (nativeInitializationCalled) RecordNative(RgbNativeOperation.Initialize, initStatus);
                if (initStatus != 0)
                {
                    _needsRetry = IsRetryableStatus(initStatus);
                    _diagnostic = EngineText.Of(EngineTexts.GpuNvapiInit, initStatus.ToString(CultureInfo.InvariantCulture));
                    _adapterObservation = RgbAdapterObservation.EnumerationUnavailable;
                    return;
                }

                var handles = new IntPtr[64];
                int status = _backend.EnumeratePhysicalGpus(handles, out int count);
                RecordNative(RgbNativeOperation.EnumerateAdapters, status);
                _lastAdapterEnumeration = DateTimeOffset.UtcNow;
                if (status != 0)
                {
                    _needsRetry = IsRetryableStatus(status);
                    _diagnostic = EngineText.Of(EngineTexts.GpuNvapiEnum, status.ToString(CultureInfo.InvariantCulture));
                    _adapterObservation = RgbAdapterObservation.EnumerationUnavailable;
                    return;
                }

                _adapterObservation = RgbAdapterObservation.NotDetected;
                EngineText? pciError = null;
                for (int index = 0; index < Math.Min(count, handles.Length); index++)
                {
                    var handle = handles[index];
                    status = _backend.GetPciIdentifiers(handle, out uint device, out uint subsystem);
                    RecordNative(RgbNativeOperation.IdentifyAdapter, status);
                    if (status != 0)
                    {
                        _needsRetry |= IsRetryableStatus(status);
                        pciError ??= EngineText.Of(EngineTexts.GpuPci, status.ToString(CultureInfo.InvariantCulture));
                        if (_adapterObservation != RgbAdapterObservation.Present)
                            _adapterObservation = RgbAdapterObservation.EnumerationUnavailable;
                        continue;
                    }

                    if ((ushort)device != NvidiaVendor || (ushort)subsystem != AsusVendor) continue;

                    _adapterObservation = RgbAdapterObservation.Present;
                    _lastAdapterSeen = DateTimeOffset.UtcNow;
                    _gpu = handle;
                    _gpuHandleContext = unchecked((ulong)handle.ToInt64()).ToString("X16", CultureInfo.InvariantCulture);
                    if (!ProbeController()) continue;

                    _diagnostic = null;
                    _needsRetry = false;
                    _controlAvailable = true;
                    string announcement = $"Aura da GPU detectado em PCI {device:X8}/{subsystem:X8}: {_controllerName}, {_ledCount} LEDs.";
                    if (announcement != _announced) Log.Info(announcement);
                    _announced = announcement;
                    return;
                }

                _gpu = IntPtr.Zero;
                _gpuHandleContext = null;
                _diagnostic ??= pciError ?? EngineText.Of(EngineTexts.GpuNotFound);
            }
            catch (DllNotFoundException)
            {
                _diagnostic = EngineText.Of(EngineTexts.GpuNvapiMissing);
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or BadImageFormatException or SEHException)
            {
                _diagnostic = EngineText.Of(EngineTexts.GpuNvapiAccess, Limit(ex.Message, 256));
                _gpu = IntPtr.Zero;
                _gpuHandleContext = null;
            }
            finally
            {
                _initializing = false;
                Publish();
            }
        }
    }

    private bool ProbeController()
    {
        for (byte index = 0; index < 16; index++)
        {
            byte command = (byte)(0xA0 + index);
            if (!Transfer(true, command, 1, command)) return false;
            if (_data[0] != index) return Fail(EngineText.Of(EngineTexts.GpuNoSignature));
        }

        // São 15 caracteres de identificação. O último byte do campo é reservado ao terminador.
        Span<byte> firmwareBytes = stackalloc byte[15];
        for (int index = 0; index < firmwareBytes.Length; index++)
        {
            if (!ReadRegister((ushort)(FirmwareRegister + index), out firmwareBytes[index])) return false;
            if (firmwareBytes[index] is < 0x20 or > 0x7E) return Fail(EngineText.Of(EngineTexts.GpuIdUnreadable));
        }

        string firmware = Encoding.ASCII.GetString(firmwareBytes);
        if (firmware is not ("AUMA0-E6K5-0107" or "AUMA0-E6K5-1107" or "AUMA0-E6K5-1110" or "AUMA0-E6K5-1111"
            or "AUMA0-E6K5-0008" or "AUMA0-E6K5-1113" or "AUMA0-E6K5-1114"))
        {
            return Fail(EngineText.Of(EngineTexts.GpuFirmwareUnknown, firmware));
        }

        _controllerName = firmware;
        if (!ReadRegister(LedCountRegister, out byte count)) return false;
        if (count is 0 or > MaxLedCount) return Fail(EngineText.Of(EngineTexts.GpuLedCount, count.ToString(CultureInfo.InvariantCulture)));
        _ledCount = count;
        return true;
    }

    public bool SetColor(byte r, byte g, byte b, byte brightnessPercent)
    {
        lock (_syncLock)
        {
            if (_disposed) return false;
            _lastOutputAttempt = DateTimeOffset.UtcNow;
            bool accepted = false;
            try
            {
                accepted = ApplyColor(r, g, b, brightnessPercent);
                return accepted;
            }
            finally
            {
                _lastOutputAccepted = accepted;
                if (accepted)
                {
                    _lastOutputAcceptedAt = DateTimeOffset.UtcNow;
                    _lastOutputAcceptedNativeSequence = _lastNativeOperation?.Sequence;
                    _consecutiveOutputFailures = 0;
                }
                else
                {
                    Increment(ref _outputFailures);
                    Increment(ref _consecutiveOutputFailures);
                }
                Publish();
            }
        }
    }

    private bool ApplyColor(byte r, byte g, byte b, byte brightnessPercent)
    {
        if (!_controlAvailable || !EnsureDirectMode(out bool enteringDirectMode)) return false;

        int brightness = Math.Min((int)brightnessPercent, 100);
        ReadOnlySpan<byte> color = [(byte)(r * brightness / 100), (byte)(b * brightness / 100), (byte)(g * brightness / 100)];
        for (int led = 0; led < _ledCount; led++)
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

        _diagnostic = null;
        return true;
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
            string state = $"RGB GPU: estado antes de configurar o controlador: direto={previousDirect}, modo=0x{previousMode:X2}.";
            if (state != _stateLogged) Log.Info(state);
            _stateLogged = state;
        }
        if (!_controlAvailable) return false;

        // Sai de Off ou de um efeito interno inválido antes de entregar o controle ao software.
        return WriteRegister(EffectModeRegister, 1) && WriteRegister(ApplyRegister, 1) && ApplyDirectMode();
    }

    private bool ApplyDirectMode() => WriteRegister(DirectModeRegister, 1) && WriteRegister(ApplyRegister, 1);

    private bool ConfirmDirectColor(ReadOnlySpan<byte> color)
    {
        if (!ReadRegister(DirectModeRegister, out byte direct)) return false;
        if (direct != 1) return Fail(EngineText.Of(EngineTexts.GpuDirectModeLost));

        ushort first = DirectColorRegister;
        ushort last = (ushort)(first + (_ledCount - 1) * color.Length);
        for (int channel = 0; channel < color.Length; channel++)
        {
            if (!ReadRegister((ushort)(first + channel), out byte firstValue) ||
                !ReadRegister((ushort)(last + channel), out byte lastValue))
                return false;
            if (firstValue != color[channel] || lastValue != color[channel])
                return Fail(EngineText.Of(EngineTexts.GpuColorMismatch));
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
            if (Transfer(false, WriteBlockCommand, _data.Length, register))
            {
                _blockWriteFailures = 0;
                return true;
            }
            if (!_controlAvailable) return false;
        }

        // Reposiciona cada byte caso a tentativa de bloco tenha avançado o ponteiro.
        for (int channel = 0; channel < color.Length; channel++)
        {
            if (!WriteRegister((ushort)(register + channel), color[channel])) return false;
        }

        // NVAPI_ERROR (-1) é genérico: sozinho, não comprova barramento ocupado nem falta de suporte a blocos.
        // Só adota a alternativa byte a byte (3x mais transferências) após falhas consecutivas de bloco.
        if (!_byteWrites && ++_blockWriteFailures >= BlockFailuresBeforeByteWrites)
        {
            _byteWrites = true;
            Log.Info($"RGB GPU: a escrita em bloco falhou {BlockFailuresBeforeByteWrites} vezes seguidas; usando escrita byte a byte.");
        }
        return true;
    }

    private bool ReadRegister(ushort register, out byte value)
    {
        bool ok = SelectRegister(register) && Transfer(true, ReadByteCommand, 1, register);
        value = ok ? _data[0] : (byte)0;
        return ok;
    }

    private bool WriteRegister(ushort register, byte value)
    {
        if (!SelectRegister(register)) return false;
        _data[0] = value;
        return Transfer(false, WriteByteCommand, 1, register);
    }

    private bool SelectRegister(ushort register)
    {
        _data[0] = (byte)(register >> 8);
        _data[1] = (byte)register;
        return Transfer(false, SelectCommand, 2, register);
    }

    private bool Transfer(bool read, byte command, int count, ushort register)
    {
        if (_gpu == IntPtr.Zero) return Fail(EngineText.Of(EngineTexts.GpuI2cMissing));
        long transferStarted = Stopwatch.GetTimestamp();
        int status = _backend.Transfer(_gpu, read, command, _data, count);
        double duration = Stopwatch.GetElapsedTime(transferStarted).TotalMicroseconds;
        RecordNative(read ? RgbNativeOperation.Read : RgbNativeOperation.Write, status, register, command,
            double.IsFinite(duration) && duration >= 0 ? duration : null, _gpuHandleContext);
        GC.KeepAlive(_data);
        if (status == 0) return true;
        if (status is -6 or -8 or -10) _controlAvailable = false; // RGB SDK/handle unavailable; not proof of physical removal.
        if (!_controlAvailable) _needsRetry |= IsRetryableStatus(status);
        return Fail(EngineText.Of(EngineTexts.GpuAuraFailed, $"{register:X4}", $"{command:X2}", status.ToString(CultureInfo.InvariantCulture)));
    }

    // Erro genérico de transporte, GPU indisponível, identificador inválido ou dispositivo ocupado.
    private static bool IsRetryableStatus(int status) => status is -1 or -6 or -8 or -10 or -108;

    private bool Fail(EngineText diagnostic)
    {
        _diagnostic = diagnostic;
        return false;
    }

    private void ResetController()
    {
        _controlAvailable = false;
        _needsRetry = false;
        _controllerName = string.Empty;
        _ledCount = 0;
        _diagnostic = null;
        _lastOutputAccepted = null;
        _gpu = IntPtr.Zero;
        _gpuHandleContext = null;
        _directConfigured = false;
        _byteWrites = false;
        _blockWriteFailures = 0;
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_disposed) return;
            _disposed = true;
            ResetController();
            try { _backend.Dispose(); }
            finally { Publish(); }
        }
    }

    private void RecordNative(RgbNativeOperation operation, int status, ushort? register = null, byte? command = null,
        double? transferDurationMicroseconds = null, string? gpuHandle = null)
    {
        Increment(ref _nativeSequence);
        if (status != 0) Increment(ref _nativeFailures);
        var completed = new RgbNativeOperationResult
        {
            Sequence = _nativeSequence, AtUtc = DateTimeOffset.UtcNow, Operation = operation, Status = status,
            FailureOrdinal = status == 0 ? 0 : _nativeFailures,
            Register = register, Command = command, ProcessId = Environment.ProcessId,
            ThreadId = Environment.CurrentManagedThreadId, GpuHandle = gpuHandle,
            TransferDurationMicroseconds = transferDurationMicroseconds, LibraryVersion = CachedLibraryVersion()
        };
        _lastNativeOperation = completed;
        if (status == 0) return;
        _lastNativeFailure = completed;
        // Only failures touch the bounded ring. Successful transfers do not clone or serialize history.
        if (_nativeFailureCount == 0 || _nativeFailureRing[(_nativeFailureHead + _nativeFailureCount - 1) % _nativeFailureRing.Length]!.FailureOrdinal < completed.FailureOrdinal)
        {
            if (_nativeFailureCount < _nativeFailureRing.Length)
                _nativeFailureRing[(_nativeFailureHead + _nativeFailureCount++) % _nativeFailureRing.Length] = completed;
            else
            {
                _nativeFailureRing[_nativeFailureHead] = completed;
                _nativeFailureHead = (_nativeFailureHead + 1) % _nativeFailureRing.Length;
            }
            _nativeFailureHistoryDirty = true;
        }
    }

    private string? CachedLibraryVersion()
    {
        try
        {
            var version = _backend.LibraryVersion;
            return !string.IsNullOrWhiteSpace(version) && !version.Any(char.IsControl)
                ? version[..Math.Min(version.Length, 128)] : null;
        }
        catch (Exception) { return null; } // A diagnostic-only getter must never mask a completed native result.
    }

    private RgbNativeFailureHistory FailureHistory()
    {
        if (!_nativeFailureHistoryDirty) return _nativeFailureHistory;
        var records = new RgbNativeOperationResult[_nativeFailureCount];
        for (int i = 0; i < records.Length; i++) records[i] = _nativeFailureRing[(_nativeFailureHead + i) % _nativeFailureRing.Length]!;
        _nativeFailureHistory = new RgbNativeFailureHistory
        {
            TotalFailures = _nativeFailures, EvictedCount = _nativeFailures - records.Length, Records = records
        };
        _nativeFailureHistoryDirty = false;
        return _nativeFailureHistory;
    }

    private void Publish() => Volatile.Write(ref _published, new RgbControlStatus
    {
        ControllerName = _controllerName, ControllerLedCount = _ledCount,
        ControlAvailable = _controlAvailable, Initializing = _initializing, InitializationRetryNeeded = _needsRetry,
        AdapterObservation = _adapterObservation, LastAdapterEnumerationUtc = _lastAdapterEnumeration,
        LastAdapterSeenUtc = _lastAdapterSeen, LastOutputAccepted = _lastOutputAccepted,
        LastOutputAttemptUtc = _lastOutputAttempt, LastOutputAcceptedUtc = _lastOutputAcceptedAt,
        LastOutputAcceptedNativeSequence = _lastOutputAcceptedNativeSequence,
        ConsecutiveOutputFailures = _consecutiveOutputFailures, TotalOutputFailures = _outputFailures,
        TotalNativeFailures = _nativeFailures, InitializationAttempts = _initializationAttempts,
        LastNativeOperation = _lastNativeOperation, LastNativeFailure = _lastNativeFailure, DiagnosticText = _diagnostic,
        NativeFailureHistory = FailureHistory()
    });

    private static void Increment(ref long value) { if (value < long.MaxValue) value++; }
    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
}
