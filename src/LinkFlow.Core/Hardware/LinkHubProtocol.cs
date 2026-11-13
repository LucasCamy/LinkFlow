using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace LinkFlow.Core;

public static class LinkHubProtocol
{
    public const int PacketSize = 512;
    public const int PacketSizeOut = PacketSize + 1; // 513 bytes (Report ID 0x00 + 512 bytes payload)

    public const byte HandleId = 0x01;

    // Commands
    public static readonly byte[] EnterSoftwareMode = { 0x01, 0x03, 0x00, 0x02 };
    public static readonly byte[] EnterHardwareMode = { 0x01, 0x03, 0x00, 0x01 };
    public static readonly byte[] ReadFirmwareVersion = { 0x02, 0x13 };
    public static readonly byte[] OpenEndpoint = { 0x0d, HandleId };
    public static readonly byte[] CloseEndpoint = { 0x05, 0x01, HandleId };
    public static readonly byte[] Read = { 0x08, HandleId };
    public static readonly byte[] Write = { 0x06, HandleId };

    // Endpoints
    public static readonly byte[] EndpointSpeeds = { 0x17 };
    public static readonly byte[] EndpointTemperatures = { 0x21 };
    public static readonly byte[] EndpointSoftwareSpeedFixedPercent = { 0x18 };
    public static readonly byte[] EndpointSubDevices = { 0x36 };

    // Data Types
    public static readonly byte[] DataTypeSpeeds = { 0x25, 0x00 };
    public static readonly byte[] DataTypeTemperatures = { 0x10, 0x00 };
    public static readonly byte[] DataTypeSoftwareSpeedFixedPercent = { 0x07, 0x00 };
    public static readonly byte[] DataTypeSubDevices = { 0x21, 0x00 };

    public static byte[] CreateCommandPacket(ReadOnlySpan<byte> command, ReadOnlySpan<byte> data = default)
    {
        const int headerLength = 3;
        var writeBuf = new byte[PacketSizeOut];
        writeBuf[2] = 0x01;

        command.CopyTo(writeBuf.AsSpan(headerLength, command.Length));
        if (!data.IsEmpty)
        {
            data.CopyTo(writeBuf.AsSpan(headerLength + command.Length, data.Length));
        }

        return writeBuf;
    }

    public static byte[] CreateWriteData(ReadOnlySpan<byte> dataType, ReadOnlySpan<byte> data)
    {
        const int headerLength = 4;
        var writeBuf = new byte[dataType.Length + data.Length + headerLength];
        BinaryPrimitives.WriteInt16LittleEndian(writeBuf.AsSpan(0, 2), (short)(data.Length + 2));
        dataType.CopyTo(writeBuf.AsSpan(headerLength, dataType.Length));
        data.CopyTo(writeBuf.AsSpan(headerLength + dataType.Length, data.Length));
        return writeBuf;
    }

    public static byte[] CreateSoftwareSpeedFixedPercentData(IReadOnlyDictionary<int, byte> channelSpeeds)
    {
        var data = new byte[channelSpeeds.Count * 4 + 1];
        data[0] = (byte)channelSpeeds.Count;
        int i = 1;

        foreach (var kvp in channelSpeeds)
        {
            data[i] = (byte)kvp.Key;
            data[i + 2] = kvp.Value;
            i += 4;
        }

        return data;
    }

    public static List<ConnectedSubDevice> ParseSubDevices(ReadOnlySpan<byte> rawPacket)
    {
        var devices = new List<ConnectedSubDevice>();
        if (rawPacket.Length < 16) return devices;

        int payloadStart = 7;
        for (int s = 0; s < 10 && s + 2 < rawPacket.Length; s++)
        {
            if (rawPacket[s] == 0x21 && rawPacket[s + 1] == 0x00)
            {
                payloadStart = s + 2;
                break;
            }
        }

        var d = rawPacket.Slice(payloadStart);
        if (d.Length < 2) return devices;

        byte lastChannel = d[0];
        d = d.Slice(1);
        int idx = 0;

        for (int ch = 1; ch <= lastChannel; ch++)
        {
            if (idx + 8 > d.Length) break;

            int deviceIdLength = d[idx + 7];
            if (deviceIdLength == 0)
            {
                idx += 8;
                continue;
            }

            var deviceInfo = d.Slice(idx, 8);
            var deviceIdStart = idx + 8;
            if (deviceIdStart + deviceIdLength > d.Length) break;

            var idBytes = d.Slice(deviceIdStart, deviceIdLength).ToArray();
            string deviceId = Encoding.ASCII.GetString(idBytes).Trim('\0');

            byte rawModel = deviceInfo[2];
            byte variant = deviceInfo[3];
            var family = LinkDeviceCatalog.Find(rawModel);

            devices.Add(new ConnectedSubDevice
            {
                Channel = ch,
                DeviceId = deviceId,
                Model = family?.Model ?? LinkDeviceModel.Unknown,
                Variant = variant,
                Name = family == null ? $"Device #{ch} (0x{rawModel:X2})" : LinkDeviceCatalog.DisplayName(family, variant, ch),
                IsPump = family?.Role == LinkDeviceRole.Pump
            });

            idx += (8 + deviceIdLength);
        }

        return devices;
    }

    public static Dictionary<int, int> ParseSpeeds(ReadOnlySpan<byte> rawPacket)
    {
        var speeds = new Dictionary<int, int>();
        if (rawPacket.Length < 20) return speeds;

        // In iCUE LINK firmware, data type 0x25 0x00 is followed by length byte at [7]
        // Channels 0 to N start at offset 12, each 3 bytes: [LSB, MSB, STATUS]
        int startOffset = 12;

        for (int ch = 0; ch < 24 && startOffset + 3 <= rawPacket.Length; ch++, startOffset += 3)
        {
            short rpm = BinaryPrimitives.ReadInt16LittleEndian(rawPacket.Slice(startOffset, 2));
            if (rpm > 0)
            {
                speeds[ch + 1] = rpm;
            }
        }

        return speeds;
    }

    public static Dictionary<int, float> ParseTemperatures(ReadOnlySpan<byte> rawPacket)
    {
        var temps = new Dictionary<int, float>();
        if (rawPacket.Length < 16) return temps;

        // In raw packet from endpoint 0x21:
        // [5..6] is 0x10 0x00, [7] is payload length (e.g. 52 bytes)
        // Sensor slots start at offset 9, each 3 bytes: [LSB, MSB, STATUS]
        int startOffset = 9;

        for (int ch = 0; ch < 24 && startOffset + 3 <= rawPacket.Length; ch++, startOffset += 3)
        {
            byte status = rawPacket[startOffset + 2];
            short rawTemp = BinaryPrimitives.ReadInt16LittleEndian(rawPacket.Slice(startOffset, 2));
            if (rawTemp > 0)
            {
                temps[ch + 1] = rawTemp / 10f;
            }
        }

        return temps;
    }
}
