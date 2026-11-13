using System.IO.MemoryMappedFiles;
using System.Text;
using LinkFlow.App.Services;

namespace LinkFlow.Tests;

public sealed class RtssOverlayTests
{
    private const int Table = 4096, Entry = 299520, Count = 3;

    [Fact]
    public void Updates_its_own_slot_without_touching_other_apps_or_repeating_the_same_frame()
    {
        using var mapping = MemoryMappedFile.CreateNew(null, Table + Count * Entry);
        using var view = mapping.CreateViewAccessor();
        SetHeader(view);
        SetOwner(view, 1, "Afterburner");

        Assert.Equal(RtssOverlayStatus.Publishing, RtssOverlay.Update(view, () => "LinkFlow\nCPU 63°C"));
        Assert.Equal("Afterburner", ReadText(view, Table + Entry + 256));
        Assert.Empty(ReadText(view, Table + 256));
        Assert.Equal("LinkFlow", ReadText(view, Table + 2 * Entry + 256));
        Assert.Equal("LinkFlow\nCPU 63°C", ReadText(view, Table + 2 * Entry + 512));
        Assert.Equal(1, view.ReadInt32(32));
        RtssOverlay.Update(view, () => "LinkFlow\nCPU 63°C");
        Assert.Equal(1, view.ReadInt32(32));

        RtssOverlay.Update(view, null);
        Assert.Empty(ReadText(view, Table + 2 * Entry + 512));
        Assert.Empty(ReadText(view, Table + 2 * Entry + 256));
        Assert.Equal("Afterburner", ReadText(view, Table + Entry + 256));
        Assert.Equal(2, view.ReadInt32(32));
    }

    [Fact]
    public void Skips_a_locked_or_incompatible_mapping_without_modifying_it()
    {
        using var mapping = MemoryMappedFile.CreateNew(null, Table + Count * Entry);
        using var view = mapping.CreateViewAccessor();
        SetHeader(view);
        view.Write(36, 1);
        Assert.Equal(RtssOverlayStatus.Busy, RtssOverlay.Update(view, () => "LinkFlow"));
        Assert.Equal(1, view.ReadInt32(36));
        Assert.Equal(0, view.ReadInt32(32));

        view.Write(36, 0);
        view.Write(24, uint.MaxValue);
        Assert.Equal(RtssOverlayStatus.Unavailable, RtssOverlay.Update(view, () => throw new InvalidOperationException()));
        Assert.Equal(0, view.ReadInt32(36));

        SetHeader(view);
        SetOwner(view, 1, "Afterburner");
        SetOwner(view, 2, "Outro app");
        Assert.Equal(RtssOverlayStatus.NoFreeSlot, RtssOverlay.Update(view, () => "LinkFlow"));
        Assert.Equal(0, view.ReadInt32(36));
        Assert.Equal(0, view.ReadInt32(32));
    }

    private static void SetHeader(MemoryMappedViewAccessor view)
    {
        view.WriteArray(0, new byte[] { 0x53, 0x53, 0x54, 0x52 }, 0, 4);
        view.Write(4, 0x00020015u);
        view.Write(20, Entry);
        view.Write(24, Table);
        view.Write(28, Count);
    }

    private static void SetOwner(MemoryMappedViewAccessor view, int slot, string owner)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(owner);
        view.WriteArray(Table + slot * Entry + 256, bytes, 0, bytes.Length);
    }

    private static string ReadText(MemoryMappedViewAccessor view, int offset)
    {
        var bytes = new byte[256];
        view.ReadArray(offset, bytes, 0, bytes.Length);
        return Encoding.Latin1.GetString(bytes, 0, Array.IndexOf(bytes, (byte)0));
    }
}
