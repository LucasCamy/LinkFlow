using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace LinkFlow.App.Services;

internal enum RtssOverlayStatus { NotRunning, Unavailable, NoFreeSlot, Busy, Publishing }

/// <summary>Publica somente texto no OSD documentado do RivaTuner Statistics Server.</summary>
internal static class RtssOverlay
{
    private const string MappingName = "RTSSSharedMemoryV2";
    private const uint Signature = 0x52545353; // 'RTSS', literal multicaractere do SDK
    private const int FrameOffset = 32, BusyOffset = 36, HeaderSize = 40;
    private const int OwnerOffset = 256, ExtendedTextOffset = 512;
    internal const int TextCapacity = 4096;
    private static ReadOnlySpan<byte> Owner => "LinkFlow\0"u8;

    public static RtssOverlayStatus Publish(Func<string> readText) => Access(readText);

    public static bool TryClear() => Access(null) != RtssOverlayStatus.Busy;

    private static RtssOverlayStatus Access(Func<string>? readText)
    {
        try
        {
            using var mapping = MemoryMappedFile.OpenExisting(MappingName, MemoryMappedFileRights.ReadWrite);
            using var view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            return Update(view, readText);
        }
        catch (FileNotFoundException) { return RtssOverlayStatus.NotRunning; }
        catch (UnauthorizedAccessException) { return RtssOverlayStatus.Unavailable; }
        catch (IOException) { return RtssOverlayStatus.Unavailable; }
    }

    internal static RtssOverlayStatus Update(MemoryMappedViewAccessor view, Func<string>? readText)
    {
        if (view.Capacity is < HeaderSize or > int.MaxValue || view.ReadUInt32(0) != Signature)
            return RtssOverlayStatus.Unavailable;

        // O SDK oferece trava de escrita a partir do layout 2.14.
        uint version = view.ReadUInt32(4);
        uint entrySize = view.ReadUInt32(20);
        uint tableOffset = view.ReadUInt32(24);
        uint count = view.ReadUInt32(28);
        if (version is < 0x0002_000E or >= 0x0003_0000 || entrySize < ExtendedTextOffset + TextCapacity ||
            tableOffset < HeaderSize || tableOffset > view.Capacity || count > (view.Capacity - tableOffset) / entrySize)
            return RtssOverlayStatus.Unavailable;
        if (count < 2) return RtssOverlayStatus.NoFreeSlot;

        // O chamador mantém a vista aberta durante a operação síncrona.
        return UpdateRegion(ViewBytes(view), readText?.Invoke(), (int)tableOffset, (int)entrySize, (int)count);
    }

    private static RtssOverlayStatus UpdateRegion(Span<byte> region, string? text, int tableOffset, int entrySize, int count)
    {
        ref int busy = ref MemoryMarshal.AsRef<int>(region.Slice(BusyOffset, sizeof(int)));
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return RtssOverlayStatus.Busy;

        try
        {
            int free = -1;
            int ours = -1;
            // A primeira entrada é reservada pelo SDK para clientes principais, como o Afterburner.
            for (int index = 1; index < count; index++)
            {
                int offset = tableOffset + index * entrySize;
                var owner = region.Slice(offset + OwnerOffset, Owner.Length);
                if (owner.SequenceEqual(Owner)) { ours = offset; break; }
                if (owner[0] == 0 && free < 0) free = offset;
            }

            if (text == null)
            {
                if (ours >= 0)
                {
                    region.Slice(ours, ExtendedTextOffset + TextCapacity).Clear();
                    AdvanceFrame(region);
                }
                return RtssOverlayStatus.Publishing;
            }

            int slot = ours >= 0 ? ours : free;
            if (slot < 0) return RtssOverlayStatus.NoFreeSlot;

            Span<byte> encoded = stackalloc byte[TextCapacity];
            int length = Encoding.Latin1.GetBytes(text.AsSpan(0, Math.Min(text.Length, TextCapacity - 1)), encoded);
            encoded[length] = 0;
            var target = region.Slice(slot + ExtendedTextOffset, length + 1);
            if (ours < 0 || !target.SequenceEqual(encoded[..(length + 1)]))
            {
                if (ours < 0) region[slot] = 0;
                encoded[..(length + 1)].CopyTo(target);
                Owner.CopyTo(region[(slot + OwnerOffset)..]);
                AdvanceFrame(region);
            }
            return RtssOverlayStatus.Publishing;
        }
        finally { Volatile.Write(ref busy, 0); }
    }

    private static void AdvanceFrame(Span<byte> region) =>
        Interlocked.Increment(ref MemoryMarshal.AsRef<int>(region.Slice(FrameOffset, sizeof(int))));

    private static unsafe Span<byte> ViewBytes(MemoryMappedViewAccessor view) =>
        new((byte*)view.SafeMemoryMappedViewHandle.DangerousGetHandle() + view.PointerOffset, (int)view.Capacity);
}
