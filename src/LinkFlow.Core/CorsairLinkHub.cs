using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using HidSharp;

namespace LinkFlow.Core;

public class CorsairLinkHub : IDisposable
{
    public const int VendorId = 0x1B1C;
    public const int ProductId = 0x0C3F;

    private readonly object _syncLock = new();
    private HidDevice? _device;
    private HidStream? _stream;

    public bool IsConnected => _stream != null;
    public string FirmwareVersion { get; private set; } = "Unknown";
    public List<ConnectedSubDevice> SubDevices { get; private set; } = new();

    public bool Connect()
    {
        lock (_syncLock)
        {
            Disconnect();

            var devices = DeviceList.Local.GetHidDevices()
                .Where(d => d.VendorID == VendorId && d.ProductID == ProductId)
                .ToList();

            // Look for interface mi_00 (max output >= 512)
            _device = devices.FirstOrDefault(d => d.GetMaxOutputReportLength() >= 512);

            if (_device == null)
            {
                return false;
            }

            if (!_device.TryOpen(out _stream))
            {
                return false;
            }

            _stream.ReadTimeout = 1000;
            _stream.WriteTimeout = 1000;

            InitializeHub();
            return true;
        }
    }

    private void InitializeHub()
    {
        if (_stream == null) return;

        // 1. Read Firmware Version
        var fwResp = SendCommandInternal(LinkHubProtocol.ReadFirmwareVersion);
        if (fwResp != null && fwResp.Length > 7)
        {
            int v1 = fwResp[4];
            int v2 = fwResp[5];
            int v3 = BinaryPrimitives.ReadInt16LittleEndian(fwResp.AsSpan(6, 2));
            FirmwareVersion = $"{v1}.{v2}.{v3}";
        }

        // 2. Put Hub into Software Control Mode
        SendCommandInternal(LinkHubProtocol.EnterSoftwareMode);

        // 3. Discover Sub-devices
        RefreshSubDevices();
    }

    public void RefreshSubDevices()
    {
        lock (_syncLock)
        {
            if (_stream == null) return;

            SendCommandInternal(LinkHubProtocol.CloseEndpoint, LinkHubProtocol.EndpointSubDevices);
            SendCommandInternal(LinkHubProtocol.OpenEndpoint, LinkHubProtocol.EndpointSubDevices);
            var resp = SendCommandInternal(LinkHubProtocol.Read);

            if (resp != null)
            {
                SubDevices = LinkHubProtocol.ParseSubDevices(resp);
            }
        }
    }

    public Dictionary<int, int> ReadSpeeds()
    {
        lock (_syncLock)
        {
            if (_stream == null) return new();

            SendCommandInternal(LinkHubProtocol.CloseEndpoint, LinkHubProtocol.EndpointSpeeds);
            SendCommandInternal(LinkHubProtocol.OpenEndpoint, LinkHubProtocol.EndpointSpeeds);
            var resp = SendCommandInternal(LinkHubProtocol.Read);

            if (resp != null)
            {
                var speeds = LinkHubProtocol.ParseSpeeds(resp);
                foreach (var dev in SubDevices)
                {
                    if (speeds.TryGetValue(dev.Channel, out int rpm))
                    {
                        dev.CurrentRpm = rpm;
                    }
                }
                return speeds;
            }

            return new();
        }
    }

    public byte[]? ReadRawTemperatures()
    {
        lock (_syncLock)
        {
            if (_stream == null) return null;
            SendCommandInternal(LinkHubProtocol.CloseEndpoint, LinkHubProtocol.EndpointTemperatures);
            SendCommandInternal(LinkHubProtocol.OpenEndpoint, LinkHubProtocol.EndpointTemperatures);
            return SendCommandInternal(LinkHubProtocol.Read);
        }
    }

    public Dictionary<int, float> ReadTemperatures()
    {
        lock (_syncLock)
        {
            if (_stream == null) return new();

            SendCommandInternal(LinkHubProtocol.CloseEndpoint, LinkHubProtocol.EndpointTemperatures);
            SendCommandInternal(LinkHubProtocol.OpenEndpoint, LinkHubProtocol.EndpointTemperatures);
            var resp = SendCommandInternal(LinkHubProtocol.Read);

            if (resp != null)
            {
                var temps = LinkHubProtocol.ParseTemperatures(resp);
                foreach (var dev in SubDevices)
                {
                    if (temps.TryGetValue(dev.Channel, out float t))
                    {
                        dev.CurrentTemperature = t;
                    }
                }
                return temps;
            }

            return new();
        }
    }

    public bool SetAllFansPercent(byte percent)
    {
        var dict = new Dictionary<int, byte>();
        foreach (var dev in SubDevices)
        {
            byte p = dev.IsPump ? Math.Max((byte)60, percent) : percent;
            dev.TargetPowerPercent = p;
            dict[dev.Channel] = p;
        }
        return SetSpeedsPercent(dict);
    }

    public bool SetSpeedPercent(int channel, byte percent)
    {
        var dev = SubDevices.FirstOrDefault(d => d.Channel == channel);
        if (dev != null)
        {
            byte safePercent = dev.IsPump ? Math.Max((byte)60, percent) : percent;
            dev.TargetPowerPercent = safePercent;
            return SetSpeedsPercent(new Dictionary<int, byte> { { channel, safePercent } });
        }
        return SetSpeedsPercent(new Dictionary<int, byte> { { channel, Math.Clamp(percent, (byte)0, (byte)100) } });
    }

    public bool SetSpeedsPercent(IReadOnlyDictionary<int, byte> channelSpeeds)
    {
        lock (_syncLock)
        {
            if (_stream == null) return false;

            var rawData = LinkHubProtocol.CreateSoftwareSpeedFixedPercentData(channelSpeeds);
            var writePayload = LinkHubProtocol.CreateWriteData(LinkHubProtocol.DataTypeSoftwareSpeedFixedPercent, rawData);

            SendCommandInternal(LinkHubProtocol.CloseEndpoint, LinkHubProtocol.EndpointSoftwareSpeedFixedPercent);
            SendCommandInternal(LinkHubProtocol.OpenEndpoint, LinkHubProtocol.EndpointSoftwareSpeedFixedPercent);
            SendCommandInternal(LinkHubProtocol.Write, writePayload);
            SendCommandInternal(LinkHubProtocol.CloseEndpoint, LinkHubProtocol.EndpointSoftwareSpeedFixedPercent);

            return true;
        }
    }

    public bool SetGlobalRgb(byte r, byte g, byte b, byte brightnessPercent = 100)
    {
        lock (_syncLock)
        {
            if (_stream == null) return false;

            float factor = Math.Clamp(brightnessPercent / 100f, 0f, 1f);
            byte adjR = (byte)(r * factor);
            byte adjG = (byte)(g * factor);
            byte adjB = (byte)(b * factor);

            // Re-assert software mode to ensure Corsair Hub listens to host commands
            SendCommandInternal(LinkHubProtocol.EnterSoftwareMode);

            // Close & Open Endpoint 0x22 (Set Color)
            byte[] endpointSetColor = { 0x22 };
            byte[] dataTypeSetColor = { 0x12, 0x00 };

            SendCommandInternal(LinkHubProtocol.CloseEndpoint, endpointSetColor);
            SendCommandInternal(new byte[] { 0x0d, 0x00 }, endpointSetColor);

            int totalLeds = Math.Max(SubDevices.Count * 34, 400);
            var colorBytes = new byte[totalLeds * 3];
            for (int i = 0; i < totalLeds; i++)
            {
                colorBytes[i * 3 + 0] = adjR;
                colorBytes[i * 3 + 1] = adjG;
                colorBytes[i * 3 + 2] = adjB;
            }

            int headerWriteSize = 4;
            int totalBufLen = headerWriteSize + dataTypeSetColor.Length + colorBytes.Length;
            byte[] buffer = new byte[totalBufLen];

            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0, 2), (ushort)(colorBytes.Length + 2));
            dataTypeSetColor.CopyTo(buffer.AsSpan(headerWriteSize, 2));
            colorBytes.CopyTo(buffer.AsSpan(headerWriteSize + 2));

            // Chunk buffer into max 508 bytes per HID frame
            int maxChunkSize = 508;
            int offset = 0;
            int chunkIndex = 0;

            while (offset < buffer.Length)
            {
                int chunkSize = Math.Min(maxChunkSize, buffer.Length - offset);
                byte[] chunk = new byte[chunkSize];
                Array.Copy(buffer, offset, chunk, 0, chunkSize);

                byte[] cmd = chunkIndex == 0 ? new byte[] { 0x06, 0x00 } : new byte[] { 0x07, 0x00 };
                SendCommandInternal(cmd, chunk);

                offset += chunkSize;
                chunkIndex++;
            }

            // NOTE: Do NOT close endpoint 0x22 here!
            // Keeping the endpoint open preserves the active software color stream on Corsair iCUE LINK.
            return true;
        }
    }

    private byte[]? SendCommandInternal(ReadOnlySpan<byte> command, ReadOnlySpan<byte> data = default)
    {
        if (_stream == null) return null;

        var packet = LinkHubProtocol.CreateCommandPacket(command, data);

        try
        {
            _stream.Write(packet);

            var readBuf = new byte[LinkHubProtocol.PacketSizeOut];
            int bytesRead = _stream.Read(readBuf);

            if (bytesRead > 0)
            {
                return readBuf;
            }
        }
        catch
        {
            // Transient read/write timeout or device disconnect
        }

        return null;
    }

    public void Disconnect()
    {
        lock (_syncLock)
        {
            if (_stream != null)
            {
                try
                {
                    SendCommandInternal(LinkHubProtocol.EnterHardwareMode);
                }
                catch { }

                _stream.Dispose();
                _stream = null;
            }
            _device = null;
        }
    }

    public void Dispose()
    {
        Disconnect();
        GC.SuppressFinalize(this);
    }
}
