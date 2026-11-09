using System.Buffers.Binary;
using HidSharp;

namespace LinkFlow.Core;

/// <summary>
/// Corsair iCUE LINK System Hub (1B1C:0C3F). Todas as operações são síncronas e serializadas
/// por <c>_syncLock</c>; o engine chama apenas de uma thread dedicada.
/// </summary>
public sealed class CorsairLinkHub : IFanHub
{
    public const int VendorId = 0x1B1C;
    public const int ProductId = 0x0C3F;

    private const int IoTimeoutMs = 500;

    private readonly object _syncLock = new();
    private HidDevice? _device;
    private HidStream? _stream;
    private List<ConnectedSubDevice> _subDevices = new();
    private readonly byte[] _readBuf = new byte[LinkHubProtocol.PacketSizeOut];

    public bool IsConnected => _stream != null;
    public string FirmwareVersion { get; private set; } = "Unknown";
    public IReadOnlyList<ConnectedSubDevice> SubDevices => _subDevices;

    public bool Connect()
    {
        lock (_syncLock)
        {
            Disconnect(restoreHardwareMode: false);

            try
            {
                _device = DeviceList.Local.GetHidDevices(VendorId, ProductId)
                    .FirstOrDefault(d => d.GetMaxOutputReportLength() >= 512);
            }
            catch (Exception ex)
            {
                Log.Warn($"Enumeração HID falhou: {ex.Message}");
                return false;
            }

            if (_device == null) return false;
            if (!_device.TryOpen(out var stream)) return false;

            _stream = stream;
            _stream.ReadTimeout = IoTimeoutMs;
            _stream.WriteTimeout = IoTimeoutMs;

            InitializeHub();
            return _stream != null;
        }
    }

    private void InitializeHub()
    {
        var fwResp = SendCommandInternal(LinkHubProtocol.ReadFirmwareVersion);
        if (fwResp != null && fwResp.Length > 7)
        {
            int v1 = fwResp[4];
            int v2 = fwResp[5];
            int v3 = BinaryPrimitives.ReadUInt16LittleEndian(fwResp.AsSpan(6, 2));
            FirmwareVersion = $"{v1}.{v2}.{v3}";
        }

        SendCommandInternal(LinkHubProtocol.EnterSoftwareMode);
        RefreshSubDevicesInternal();
    }

    private void RefreshSubDevicesInternal()
    {
        SendCommandInternal(LinkHubProtocol.CloseEndpoint, LinkHubProtocol.EndpointSubDevices);
        SendCommandInternal(LinkHubProtocol.OpenEndpoint, LinkHubProtocol.EndpointSubDevices);
        var resp = SendCommandInternal(LinkHubProtocol.Read);
        if (resp != null)
        {
            _subDevices = LinkHubProtocol.ParseSubDevices(resp);
        }
    }

    public IReadOnlyDictionary<int, int>? ReadSpeeds()
    {
        lock (_syncLock)
        {
            if (_stream == null) return null;

            SendCommandInternal(LinkHubProtocol.CloseEndpoint, LinkHubProtocol.EndpointSpeeds);
            SendCommandInternal(LinkHubProtocol.OpenEndpoint, LinkHubProtocol.EndpointSpeeds);
            var resp = SendCommandInternal(LinkHubProtocol.Read);
            if (resp == null) return null;

            var speeds = LinkHubProtocol.ParseSpeeds(resp);
            foreach (var dev in _subDevices)
            {
                dev.CurrentRpm = speeds.TryGetValue(dev.Channel, out int rpm) ? rpm : 0;
            }
            return speeds;
        }
    }

    public IReadOnlyDictionary<int, float>? ReadTemperatures()
    {
        lock (_syncLock)
        {
            if (_stream == null) return null;

            SendCommandInternal(LinkHubProtocol.CloseEndpoint, LinkHubProtocol.EndpointTemperatures);
            SendCommandInternal(LinkHubProtocol.OpenEndpoint, LinkHubProtocol.EndpointTemperatures);
            var resp = SendCommandInternal(LinkHubProtocol.Read);
            if (resp == null) return null;

            var temps = LinkHubProtocol.ParseTemperatures(resp);
            foreach (var dev in _subDevices)
            {
                if (temps.TryGetValue(dev.Channel, out float t)) dev.CurrentTemperature = t;
            }
            return temps;
        }
    }

    public bool SetSpeedsPercent(IReadOnlyDictionary<int, byte> channelSpeeds)
    {
        lock (_syncLock)
        {
            if (_stream == null || channelSpeeds.Count == 0) return false;

            var rawData = LinkHubProtocol.CreateSoftwareSpeedFixedPercentData(channelSpeeds);
            var writePayload = LinkHubProtocol.CreateWriteData(LinkHubProtocol.DataTypeSoftwareSpeedFixedPercent, rawData);

            SendCommandInternal(LinkHubProtocol.CloseEndpoint, LinkHubProtocol.EndpointSoftwareSpeedFixedPercent);
            SendCommandInternal(LinkHubProtocol.OpenEndpoint, LinkHubProtocol.EndpointSoftwareSpeedFixedPercent);
            var ok = SendCommandInternal(LinkHubProtocol.Write, writePayload) != null;
            SendCommandInternal(LinkHubProtocol.CloseEndpoint, LinkHubProtocol.EndpointSoftwareSpeedFixedPercent);

            if (ok)
            {
                foreach (var dev in _subDevices)
                {
                    if (channelSpeeds.TryGetValue(dev.Channel, out var p)) dev.TargetPowerPercent = p;
                }
            }
            return ok;
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

            // Reafirma o modo software para garantir que o hub escute o host
            SendCommandInternal(LinkHubProtocol.EnterSoftwareMode);

            byte[] endpointSetColor = { 0x22 };
            byte[] dataTypeSetColor = { 0x12, 0x00 };

            SendCommandInternal(LinkHubProtocol.CloseEndpoint, endpointSetColor);
            SendCommandInternal(new byte[] { 0x0d, 0x00 }, endpointSetColor);

            int totalLeds = Math.Max(_subDevices.Count * 34, 400);
            var colorBytes = new byte[totalLeds * 3];
            for (int i = 0; i < totalLeds; i++)
            {
                colorBytes[i * 3 + 0] = adjR;
                colorBytes[i * 3 + 1] = adjG;
                colorBytes[i * 3 + 2] = adjB;
            }

            const int headerWriteSize = 4;
            byte[] buffer = new byte[headerWriteSize + dataTypeSetColor.Length + colorBytes.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0, 2), (ushort)(colorBytes.Length + 2));
            dataTypeSetColor.CopyTo(buffer.AsSpan(headerWriteSize, 2));
            colorBytes.CopyTo(buffer.AsSpan(headerWriteSize + 2));

            const int maxChunkSize = 508;
            int offset = 0;
            int chunkIndex = 0;
            bool ok = true;

            while (offset < buffer.Length && _stream != null)
            {
                int chunkSize = Math.Min(maxChunkSize, buffer.Length - offset);
                byte[] cmd = chunkIndex == 0 ? new byte[] { 0x06, 0x00 } : new byte[] { 0x07, 0x00 };
                ok &= SendCommandInternal(cmd, buffer.AsSpan(offset, chunkSize)) != null;
                offset += chunkSize;
                chunkIndex++;
            }

            // NÃO fechar o endpoint 0x22: mantê-lo aberto preserva o stream de cor ativo.
            return ok && _stream != null;
        }
    }

    private byte[]? SendCommandInternal(ReadOnlySpan<byte> command, ReadOnlySpan<byte> data = default)
    {
        if (_stream == null) return null;

        var packet = LinkHubProtocol.CreateCommandPacket(command, data);

        try
        {
            _stream.Write(packet);
            int bytesRead = _stream.Read(_readBuf);
            if (bytesRead > 0)
            {
                return (byte[])_readBuf.Clone();
            }
        }
        catch (TimeoutException)
        {
            // Timeout transitório: o engine conta falhas consecutivas e reconecta se necessário.
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            // Dispositivo removido / handle inválido (ex.: retorno da suspensão).
            Log.Warn($"Hub iCUE LINK perdeu a conexão: {ex.Message}");
            CloseStream();
        }

        return null;
    }

    private void CloseStream()
    {
        try { _stream?.Dispose(); } catch { }
        _stream = null;
    }

    public void Disconnect(bool restoreHardwareMode = true)
    {
        lock (_syncLock)
        {
            if (_stream != null && restoreHardwareMode)
            {
                SendCommandInternal(LinkHubProtocol.EnterHardwareMode);
            }
            CloseStream();
            _device = null;
        }
    }

    public void Dispose() => Disconnect(restoreHardwareMode: true);
}
