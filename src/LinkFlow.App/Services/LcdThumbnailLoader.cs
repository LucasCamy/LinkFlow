using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using System.Windows.Media.Imaging;
using LinkFlow.Core;

namespace LinkFlow.App.Services;

internal sealed record LcdThumbnailResult(BitmapSource? Image, bool? Exists, string? Error = null);

/// <summary>One reader per view model, bounded admission/cache, and frozen 96px results. No work runs in a binding getter.</summary>
internal sealed class LcdThumbnailLoader : IDisposable
{
    private sealed record Request(string Path, Action<LcdThumbnailResult> Complete);
    private sealed record CacheEntry(string Path, long At, LcdThumbnailResult Result);
    private readonly Channel<Request> _requests;
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<string, LcdThumbnailResult> _read;
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<CacheEntry> _order = new();
    private readonly int _cacheCapacity;
    private readonly Task _worker;
    private int _disposed;
    internal event Action? CapacityAvailable;
    internal Task Completion => _worker;

    internal LcdThumbnailLoader(int queueCapacity = 32, int cacheCapacity = 96, Func<string, LcdThumbnailResult>? read = null)
    {
        _cacheCapacity = Math.Clamp(cacheCapacity, 1, 256);
        _read = read ?? Read;
        _requests = Channel.CreateBounded<Request>(new BoundedChannelOptions(Math.Clamp(queueCapacity, 1, 128))
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        _worker = Task.Run(RunAsync);
    }

    internal bool TryQueue(string path, Action<LcdThumbnailResult> complete) => Volatile.Read(ref _disposed) == 0 &&
        _requests.Writer.TryWrite(new(path, complete));

    private async Task RunAsync()
    {
        try
        {
            await foreach (var request in _requests.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                LcdThumbnailResult result;
                try { result = Resolve(request.Path); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { result = new(null, null, ex.Message.Length > 512 ? ex.Message[..512] : ex.Message); }
                if (_stop.IsCancellationRequested) break;
                try { request.Complete(result); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Debug("Thumbnail result: " + ex.Message); }
                try { CapacityAvailable?.Invoke(); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Debug("Thumbnail queue: " + ex.Message); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            while (_requests.Reader.TryRead(out _)) { }
            _cache.Clear(); _order.Clear();
        }
    }

    private LcdThumbnailResult Resolve(string path)
    {
        string key = Path.GetFullPath(path);
        if (_cache.TryGetValue(key, out var cached))
        {
            // The media library is named by content. Legacy external paths are re-read on new demand
            // after a short expiry; failed reads have a shorter expiry so recovery is not cached forever.
            if (Stopwatch.GetElapsedTime(cached.Value.At) <= TimeSpan.FromSeconds(cached.Value.Result.Image == null ? 5 : 60))
            { _order.Remove(cached); _order.AddLast(cached); return cached.Value.Result; }
            _cache.Remove(key); _order.Remove(cached);
        }
        var result = _read(key);
        if (result.Image is { IsFrozen: false } image)
        {
            if (!image.CanFreeze) throw new InvalidOperationException("Thumbnail cannot be shared across threads.");
            image.Freeze();
        }
        var node = _order.AddLast(new CacheEntry(key, Stopwatch.GetTimestamp(), result));
        _cache[key] = node;
        while (_cache.Count > _cacheCapacity && _order.First is { } oldest)
        { _cache.Remove(oldest.Value.Path); _order.RemoveFirst(); }
        return result;
    }

    private static LcdThumbnailResult Read(string path)
    {
        bool? exists = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            exists = true;
            if (stream.Length > 64L * 1024 * 1024) return new(null, true, "thumbnail-file-too-large");
            var header = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None).Frames[0];
            int width = header.PixelWidth, height = header.PixelHeight;
            if (width <= 0 || height <= 0) throw new InvalidDataException("Invalid image dimensions.");
            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            // Limit the longest side, preserving aspect ratio even for extreme portrait/panorama inputs.
            if (width >= height)
            {
                image.DecodePixelWidth = Math.Min(96, width);
                long scaled = (long)height * image.DecodePixelWidth;
                // WPF derives the other axis with Int32 arithmetic. Prevent zero-pixel axes and
                // overflow for extreme headers; both explicit dimensions still follow the source ratio.
                if (scaled < width || scaled > int.MaxValue) image.DecodePixelHeight = Math.Max(1, (int)(scaled / width));
            }
            else
            {
                image.DecodePixelHeight = Math.Min(96, height);
                long scaled = (long)width * image.DecodePixelHeight;
                if (scaled < height || scaled > int.MaxValue) image.DecodePixelWidth = Math.Max(1, (int)(scaled / height));
            }
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return new(image, true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is FileNotFoundException or DirectoryNotFoundException) exists = false;
            return new(null, exists, ex.Message.Length > 512 ? ex.Message[..512] : ex.Message);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _requests.Writer.TryComplete();
        _ = _worker.ContinueWith(_ => _stop.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
