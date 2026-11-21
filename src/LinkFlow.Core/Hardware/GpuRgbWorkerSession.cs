using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace LinkFlow.Core;

/// <summary>Owns exactly one GPU-only child. It never loads NVAPI in the service process.</summary>
internal sealed class GpuRgbWorkerSession : IGpuRgbWorkerSession
{
    private static readonly object LaunchGate = new();
    private static Process? _unconfirmedLaunch;
    private readonly Process _process;
    private readonly Stream _input, _output;
    private readonly GpuWorkerJob _job;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _exchange = new(1, 1);
    private readonly object _disposeGate = new();
    private int _closing, _faulted, _activeExchanges, _lifetimeDisposed;
    private bool _exitConfirmed;
    private Task? _pendingIo;

    private GpuRgbWorkerSession(Process process, GpuWorkerJob job)
    {
        _process = process;
        _job = job;
        ProcessId = process.Id;
        _input = process.StandardInput.BaseStream;
        _output = process.StandardOutput.BaseStream;
    }

    public int ProcessId { get; }
    public bool HasExited => Volatile.Read(ref _exitConfirmed) || _process.HasExited;

    public static IGpuRgbWorkerSession Start(string executablePath)
    {
        lock (LaunchGate)
        {
            // A failed launch can still own a live process even though no session was returned to the wrapper.
            // Retain one handle/identity and refuse all later launches until the OS positively confirms exit.
            if (_unconfirmedLaunch != null)
            {
                if (!_unconfirmedLaunch.HasExited)
                    throw new IOException("A previous GPU RGB worker launch has not terminated; another launch is forbidden.");
                _unconfirmedLaunch.Dispose();
                _unconfirmedLaunch = null;
            }
            return StartOwned(executablePath);
        }
    }

    private static IGpuRgbWorkerSession StartOwned(string executablePath)
    {
        GpuRgbWorkerIdentity.ValidateLauncher(executablePath);
        var job = GpuWorkerJob.Create();
        Process? child = null;
        try
        {
            var start = new ProcessStartInfo(Path.GetFullPath(executablePath))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true,
                // No diagnostic reader/queue and no protocol text on stdout.
                RedirectStandardError = false,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath))!
            };
            start.ArgumentList.Add("--gpu-rgb-worker");
            child = Process.Start(start) ?? throw new IOException("GPU RGB worker could not be started.");
            // The child does not construct hardware until the first request. No request is sent before assignment.
            job.Assign(child);
            return new GpuRgbWorkerSession(child, job);
        }
        catch (Exception launchFailure)
        {
            job.Dispose();
            if (child != null)
            {
                _unconfirmedLaunch = child;
                try
                {
                    try { if (!child.HasExited) child.Kill(); }
                    catch (InvalidOperationException) { }
                    if (!child.WaitForExit(2000))
                        throw new IOException("GPU RGB worker launch failed and its termination could not be confirmed.");
                    child.Dispose();
                    _unconfirmedLaunch = null;
                }
                catch (Exception terminationFailure)
                {
                    throw new IOException("GPU RGB launch failed with termination unconfirmed; future launches are blocked.",
                        new AggregateException(launchFailure, terminationFailure));
                }
            }
            throw;
        }
    }

    public GpuRgbWorkerReply Exchange(GpuRgbWorkerRequest request, TimeSpan timeout)
    {
        long started = Stopwatch.GetTimestamp();
        if (timeout < TimeSpan.FromMilliseconds(1) || timeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        GpuRgbWorkerFraming.ValidateRequest(request);
        if (!_exchange.Wait(0)) throw new InvalidOperationException("A GPU RGB exchange is already in progress.");
        Interlocked.Increment(ref _activeExchanges);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closing) != 0, this);
            if (Volatile.Read(ref _faulted) != 0 || HasExited)
                throw new IOException("GPU RGB worker session is no longer usable.");
            TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("GPU RGB exchange deadline expired.");
            return ExchangeWithinDeadlineAsync(_input, _output, request, remaining, _lifetime.Token,
                SignalTermination, pending => Volatile.Write(ref _pendingIo, pending)).GetAwaiter().GetResult();
        }
        catch
        {
            Interlocked.Exchange(ref _faulted, 1);
            throw;
        }
        finally
        {
            _exchange.Release();
            Interlocked.Decrement(ref _activeExchanges);
            TryDisposeLifetime();
        }
    }

    internal static async Task<GpuRgbWorkerReply> ExchangeWithinDeadlineAsync(Stream input, Stream output,
        GpuRgbWorkerRequest request, TimeSpan timeout, CancellationToken lifetime, Action abort,
        Action<Task>? recordPending = null)
    {
        long started = Stopwatch.GetTimestamp();
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        deadline.CancelAfter(timeout);
        Task<GpuRgbWorkerReply> pending = ExchangeIoAsync(input, output, request, deadline.Token);
        recordPending?.Invoke(pending);
        try
        {
            // Outer deadline remains effective even when redirected pipe IO ignores cancellation.
            var reply = await pending.WaitAsync(deadline.Token).ConfigureAwait(false);
            if (Stopwatch.GetElapsedTime(started) > timeout) throw new TimeoutException("GPU RGB exchange deadline expired.");
            return reply;
        }
        catch (Exception failure)
        {
            try { abort(); }
            catch (Exception cleanup)
            {
                try { failure.Data["WorkerTerminationSignalError"] = cleanup.GetType().Name; }
                catch { /* Preserve the original transport failure. */ }
            }
            if (failure is OperationCanceledException && !lifetime.IsCancellationRequested)
                throw new TimeoutException("GPU RGB worker did not complete the exchange within its deadline.", failure);
            throw;
        }
        finally
        {
            // A late IO continuation can still reference the token; do not dispose its CTS underneath it.
            if (pending.IsCompleted) { Observe(pending); deadline.Dispose(); }
            else _ = pending.ContinueWith(t => { _ = t.Exception; deadline.Dispose(); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static async Task<GpuRgbWorkerReply> ExchangeIoAsync(Stream input, Stream output,
        GpuRgbWorkerRequest request, CancellationToken token)
    {
        await GpuRgbWorkerFraming.WriteAsync(input, request, GpuRgbWorkerProtocol.MaximumRequestBytes, token).ConfigureAwait(false);
        var reply = await GpuRgbWorkerFraming.ReadAsync<GpuRgbWorkerReply>(output,
            GpuRgbWorkerProtocol.MaximumReplyBytes, token).ConfigureAwait(false)
            ?? throw new EndOfStreamException("GPU RGB worker closed its reply stream.");
        if (reply.Version != GpuRgbWorkerProtocol.Version || reply.Generation != request.Generation ||
            reply.RequestId != request.RequestId || reply.Status == null)
            throw new InvalidDataException("GPU RGB worker reply does not match the outstanding request.");
        return reply;
    }

    private void SignalTermination()
    {
        Interlocked.Exchange(ref _faulted, 1);
        _job.Dispose(); // No wait on the blocked reader. Dispose below confirms exit before replacement.
    }

    public void Dispose()
    {
        lock (_disposeGate)
        {
            if (_exitConfirmed) return;
            Interlocked.Exchange(ref _closing, 1);
            _lifetime.Cancel();
            // Closing the non-inherited job handle kills only our owned child, including a hung native call.
            _job.Dispose();
            if (!_process.HasExited)
            {
                try { _process.Kill(); }
                catch (InvalidOperationException) { }
                if (!_process.WaitForExit(2000))
                    throw new IOException("GPU RGB worker termination was not confirmed; replacement is forbidden.");
            }
            if (!_process.HasExited)
                throw new IOException("GPU RGB worker is still alive; replacement is forbidden.");
            Volatile.Write(ref _exitConfirmed, true);
            // Exit is the ownership fence. Cleanup failure cannot turn a confirmed dead child into a live one.
            try { _input.Dispose(); } catch (IOException) { }
            try { _output.Dispose(); } catch (IOException) { }
            _process.Dispose();
            var pending = Volatile.Read(ref _pendingIo);
            if (pending == null || pending.IsCompleted) TryDisposeLifetime();
            else _ = pending.ContinueWith(_ => TryDisposeLifetime(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private void TryDisposeLifetime()
    {
        if (Volatile.Read(ref _closing) == 0 || !Volatile.Read(ref _exitConfirmed) || Volatile.Read(ref _activeExchanges) != 0)
            return;
        var pending = Volatile.Read(ref _pendingIo);
        if (pending is { IsCompleted: false }) return;
        if (Interlocked.CompareExchange(ref _lifetimeDisposed, 1, 0) == 0) _lifetime.Dispose();
    }

    private static void Observe(Task pending) => _ = pending.ContinueWith(t => _ = t.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);
}

internal static class GpuRgbWorkerFraming
{
    private static readonly JsonSerializerOptions Wire = new(Json.Compact)
    {
        ReadCommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false,
        MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static void ValidateRequest(GpuRgbWorkerRequest request)
    {
        if (request.Version != GpuRgbWorkerProtocol.Version || request.Generation <= 0 || request.RequestId <= 0 ||
            request.Operation is not (GpuRgbWorkerProtocol.Initialize or GpuRgbWorkerProtocol.Color) || request.Brightness > 100)
            throw new InvalidDataException("Invalid private GPU RGB worker request.");
    }

    internal static async Task WriteAsync<T>(Stream stream, T value, int maximum, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Wire);
        if (bytes.Length is < 1 || bytes.Length > maximum)
            throw new InvalidDataException("GPU RGB frame exceeds its size limit.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    internal static async Task<T?> ReadAsync<T>(Stream stream, int maximum, CancellationToken token) where T : class
    {
        byte[] header = new byte[4];
        int first = await stream.ReadAsync(header.AsMemory(0, 1), token).ConfigureAwait(false);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), token).ConfigureAwait(false);
        int count = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (count < 1 || count > maximum) throw new InvalidDataException("Invalid GPU RGB frame length.");
        byte[] bytes = new byte[count];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        using (var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 }))
            RejectDuplicateMembers(parsed.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Wire) ?? throw new InvalidDataException("Empty GPU RGB frame.");
    }

    private static void RejectDuplicateMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in value.EnumerateObject())
            {
                if (!names.Add(member.Name)) throw new InvalidDataException("Duplicate GPU RGB frame member.");
                RejectDuplicateMembers(member.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var member in value.EnumerateArray()) RejectDuplicateMembers(member);
    }
}

internal sealed class GpuWorkerJob : SafeHandleZeroOrMinusOneIsInvalid
{
    private GpuWorkerJob() : base(true) { }
    internal static GpuWorkerJob Create()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 | 0x8, ActiveProcessLimit = 1 } };
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        {
            int error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error);
        }
        return job;
    }
    internal void Assign(Process process)
    {
        if (!AssignProcessToJobObject(this, process.SafeHandle)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    protected override bool ReleaseHandle() => CloseHandle(handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessUserTime, JobUserTime;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern GpuWorkerJob CreateJobObjectW(IntPtr securityAttributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(GpuWorkerJob job, int informationClass, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(GpuWorkerJob job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
