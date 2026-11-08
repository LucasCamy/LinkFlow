using System;
using System.Linq;
using System.Threading;
using HidSharp;

namespace LinkFlow.Core;

public sealed class MsiMotherboardRgb : IRgbDevice
{
    private const int MSI_VID_1 = 0x0DB0;
    private const int MSI_VID_2 = 0x1462;
    private const int MSI_PID_MYSTIC_LIGHT = 0x0076;

    private readonly object _syncLock = new();
    private HidDevice? _device;
    private HidStream? _stream;
    private byte[]? _lastPacket;

    public bool IsAvailable { get; private set; }
    public string DeviceName { get; private set; } = string.Empty;
    public string Name => DeviceName;

    // Header & Zone offsets in the 185-byte report (0x52)
    private static readonly int[] ZoneOffsets = new int[]
    {
        0x01, // JRGB1 (12V RGB Header 1)
        0x0B, // JPIPE1 (Motherboard Integrated)
        0x15, // JPIPE2 (Motherboard Secondary)
        0x1F, // JARGB_V2_1 (5V ARGB Header 1)
        0x2A, // JARGB_V2_2 (5V ARGB Header 2)
        0x35, // CORSAIR Header
        0x40, // CORSAIR Backup
        0x4A, 0x54, 0x5E, 0x68, 0x72, 0x7C, 0x86, 0x90, 0x9A, 0xA4, // Onboard LEDs 1..10
        0xAE  // JRGB2 (12V RGB Header 2)
    };

    public MsiMotherboardRgb()
    {
    }

    public void Initialize()
    {
        try
        {
            _device = DeviceList.Local.GetHidDevices()
                .FirstOrDefault(d => (d.VendorID == MSI_VID_1 || d.VendorID == MSI_VID_2) &&
                                     d.ProductID == MSI_PID_MYSTIC_LIGHT);

            if (_device != null && _device.TryOpen(out _stream))
            {
                DeviceName = _device.GetProductName() ?? "MSI Mystic Light";
                if (string.IsNullOrWhiteSpace(DeviceName)) DeviceName = "MSI MAG X670E TOMAHAWK WIFI";
                IsAvailable = true;

                // Tentar ler estado inicial da controladora
                try
                {
                    byte[] initial = new byte[185];
                    initial[0] = 0x52;
                    _stream.GetFeature(initial);
                    _lastPacket = initial;
                }
                catch
                {
                    _lastPacket = null;
                }
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

    public bool SetColor(byte r, byte g, byte b, byte brightness = 0xFF)
    {
        if (!IsAvailable || _device == null) return false;

        lock (_syncLock)
        {
            try
            {
                if (_stream == null)
                {
                    if (!_device.TryOpen(out _stream)) return false;
                }

                // 185-byte Feature Report (0x52)
                byte[] packet = _lastPacket != null ? (byte[])_lastPacket.Clone() : new byte[185];
                packet[0] = 0x52; // Report ID

                foreach (var off in ZoneOffsets)
                {
                    packet[off + 0] = 0x01;       // Static Mode
                    packet[off + 1] = r;          // Red
                    packet[off + 2] = g;          // Green
                    packet[off + 3] = b;          // Blue
                    packet[off + 4] = 0xA8;       // Normal Speed + Bit 7 enable (0x80 | 0x28)
                    packet[off + 5] = brightness; // Brightness level
                }

                packet[184] = 0x00; // Live RAM apply (sem desgastar EEPROM flash)

                // MSI MS-7E12 firmware requer transição consecutiva (old state -> new state)
                if (_lastPacket != null)
                {
                    try
                    {
                        _stream.SetFeature(_lastPacket);
                        Thread.Sleep(5);
                    }
                    catch { }
                }

                _stream.SetFeature(packet);
                _lastPacket = packet;
                return true;
            }
            catch
            {
                // Reconnect attempt on failure
                try { _stream?.Dispose(); } catch { }
                _stream = null;
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            try
            {
                _stream?.Dispose();
                _stream = null;
            }
            catch { }
            GC.SuppressFinalize(this);
        }
    }
}
