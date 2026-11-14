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
            _commandsSinceColor = int.MaxValue;
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

    public bool RefreshSubDevices()
    {
        lock (_syncLock)
        {
            if (_stream == null) return false;
            string Signature(IEnumerable<ConnectedSubDevice> d) => string.Join("|", d.Select(x => $"{x.Channel}:{x.DeviceId}:{x.Model}"));
            var before = Signature(_subDevices);
            var previous = _subDevices;
            RefreshSubDevicesInternal();
            if (_subDevices.Count == 0 && previous.Count > 0)
            {
                _subDevices = previous; // leitura falhou: mantém a lista anterior
                return false;
            }
            return Signature(_subDevices) != before;
        }
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
            EnsureSoftwareMode();

            SendCommandInternal(LinkHubProtocol.CloseEndpoint, LinkHubProtocol.EndpointSpeeds);
            SendCommandInternal(LinkHubProtocol.OpenEndpoint, LinkHubProtocol.EndpointSpeeds);
            var resp = ReadResponse(LinkHubProtocol.DataTypeSpeeds);
            if (resp == null) return null;

            var speeds = LinkHubProtocol.ParseSpeeds(resp);
            if (speeds == null) return null;
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
            var resp = ReadResponse(LinkHubProtocol.DataTypeTemperatures);
            if (resp == null) return null;

            var temps = LinkHubProtocol.ParseTemperatures(resp);
            if (temps == null) return null;
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
            EnsureSoftwareMode();

            var rawData = LinkHubProtocol.CreateSoftwareSpeedFixedPercentData(channelSpeeds);
            var writePayload = LinkHubProtocol.CreateWriteData(LinkHubProtocol.DataTypeSoftwareSpeedFixedPercent, rawData);

            SendCommandInternal(LinkHubProtocol.CloseEndpoint, LinkHubProtocol.EndpointSoftwareSpeedFixedPercent);
            SendCommandInternal(LinkHubProtocol.OpenEndpoint, LinkHubProtocol.EndpointSoftwareSpeedFixedPercent);
            // Só conta como aplicado se o hub confirmar com status ok (antes, qualquer resposta valia)
            var ok = SendCommandInternal(LinkHubProtocol.Write, writePayload, checkStatus: true) != null;
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

    /// <summary>LEDs por dispositivo da corrente (ventoinhas QX); usado para espalhar efeitos.</summary>
    public const int LedsPerDevice = 34;

    private int _commandsSinceColor = int.MaxValue;
    private long _lastColorSetup;

    public bool SetGlobalRgb(byte r, byte g, byte b, byte brightnessPercent = 100)
    {
        int totalLeds = Math.Max(_subDevices.Count * LedsPerDevice, 400);
        var colors = new byte[totalLeds * 3];
        for (int i = 0; i < totalLeds; i++)
        {
            colors[i * 3 + 0] = r;
            colors[i * 3 + 1] = g;
            colors[i * 3 + 2] = b;
        }
        return SetRgbLeds(colors, brightnessPercent);
    }

    public bool SetRgbLeds(ReadOnlySpan<byte> colors, byte brightnessPercent)
    {
        lock (_syncLock)
        {
            if (_stream == null) return false;

            float factor = Math.Clamp(brightnessPercent / 100f, 0f, 1f);
            byte[] endpointSetColor = { 0x22 };
            byte[] dataTypeSetColor = { 0x12, 0x00 };

            // Durante efeitos (~20 quadros/s) o endpoint de cor fica aberto: só refaz o preparo quando
            // outro comando passou pelo hub ou a cada 2 s, reduzindo de 6 para 3 transferências por quadro.
            bool setup = _commandsSinceColor > 0 || Environment.TickCount64 - _lastColorSetup > 2000;
            if (setup)
            {
                // Reafirma o modo software para garantir que o hub escute o host
                SendCommandInternal(LinkHubProtocol.EnterSoftwareMode);
                SendCommandInternal(LinkHubProtocol.CloseEndpoint, endpointSetColor);
                SendCommandInternal(new byte[] { 0x0d, 0x00 }, endpointSetColor);
                _lastColorSetup = Environment.TickCount64;
            }

            int totalLeds = Math.Max(_subDevices.Count * LedsPerDevice, 400);
            const int headerWriteSize = 4;
            byte[] buffer = new byte[headerWriteSize + dataTypeSetColor.Length + totalLeds * 3];
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0, 2), (ushort)(totalLeds * 3 + 2));
            dataTypeSetColor.CopyTo(buffer.AsSpan(headerWriteSize, 2));
            var dst = buffer.AsSpan(headerWriteSize + 2);
            int n = Math.Min(colors.Length, dst.Length);
            for (int i = 0; i < n; i++) dst[i] = (byte)(colors[i] * factor);

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

            _commandsSinceColor = ok ? 0 : int.MaxValue;
            // NÃO fechar o endpoint 0x22: mantê-lo aberto preserva o stream de cor ativo.
            return ok && _stream != null;
        }
    }

    // Uma resposta que chega depois do timeout fica na fila do driver HID e seria lida pelo PRÓXIMO comando
    // (ex.: temperaturas interpretadas como RPM, escondendo uma bomba parada). Depois de um timeout, a fila
    // é esvaziada antes de enviar o próximo comando.
    private bool _drainPending;
    private bool _needsSoftwareMode;
    private const int DrainTimeoutMs = 15;
    private const int MaxDrainReports = 16;
    private const int ExpectedResponseWaitMs = 500;

    /// <param name="checkStatus">Rejeita respostas com status de erro (padrão: qualquer resposta vale).</param>
    private byte[]? SendCommandInternal(ReadOnlySpan<byte> command, ReadOnlySpan<byte> data = default, bool checkStatus = false)
    {
        if (_stream == null) return null;

        var packet = LinkHubProtocol.CreateCommandPacket(command, data);
        if (_commandsSinceColor != int.MaxValue) _commandsSinceColor++;

        try
        {
            if (_drainPending) DrainStaleResponses();
            _stream.Write(packet);
            int bytesRead = _stream.Read(_readBuf);
            if (bytesRead <= LinkHubProtocol.StatusOffset) return null;
            if (checkStatus && !StatusOk(_readBuf)) return null;
            return (byte[])_readBuf.Clone();
        }
        catch (TimeoutException)
        {
            // Timeout transitório: o engine conta falhas consecutivas e reconecta se necessário.
            _drainPending = true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            // Dispositivo removido / handle inválido (ex.: retorno da suspensão).
            Log.Warn($"Hub iCUE LINK perdeu a conexão: {ex.Message}");
            CloseStream();
        }

        return null;
    }

    /// <summary>
    /// Envia o comando de leitura e só aceita a resposta do tipo esperado com status ok. Respostas de outros
    /// comandos que estavam atrasadas na fila são descartadas (até 500 ms), como na implementação de referência.
    /// </summary>
    private byte[]? ReadResponse(ReadOnlySpan<byte> dataType)
    {
        var resp = SendCommandInternal(LinkHubProtocol.Read);
        if (resp == null || _stream == null) return null;
        if (LinkHubProtocol.IsResponse(resp, dataType)) return resp;
        if (!StatusOk(resp))
        {
            _drainPending = true;
            return null;
        }

        long deadline = Environment.TickCount64 + ExpectedResponseWaitMs;
        try
        {
            while (Environment.TickCount64 < deadline)
            {
                if (_stream.Read(_readBuf) <= LinkHubProtocol.DataTypeOffset + 1) continue;
                if (LinkHubProtocol.IsResponse(_readBuf, dataType)) return (byte[])_readBuf.Clone();
            }
        }
        catch (TimeoutException) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            Log.Warn($"Hub iCUE LINK perdeu a conexão: {ex.Message}");
            CloseStream();
            return null;
        }

        Log.Debug($"Hub: resposta do tipo {Convert.ToHexString(dataType)} não chegou; descartando leitura.");
        _drainPending = true;
        return null;
    }

    private bool StatusOk(byte[] response)
    {
        byte status = response[LinkHubProtocol.StatusOffset];
        if (status == LinkHubProtocol.StatusOk) return true;
        if (status == LinkHubProtocol.StatusIncorrectMode) _needsSoftwareMode = true;
        Log.Debug($"Hub: comando recusado com status 0x{status:X2}.");
        return false;
    }

    /// <summary>O hub avisou que saiu do modo software (ex.: depois de suspender): volta antes do próximo comando.</summary>
    private void EnsureSoftwareMode()
    {
        if (!_needsSoftwareMode) return;
        _needsSoftwareMode = false;
        Log.Info("Hub fora do modo software: reativando.");
        SendCommandInternal(LinkHubProtocol.EnterSoftwareMode);
    }

    private void DrainStaleResponses()
    {
        _drainPending = false;
        var stream = _stream!;
        int previous = stream.ReadTimeout;
        int drained = 0;
        try
        {
            stream.ReadTimeout = DrainTimeoutMs;
            while (drained < MaxDrainReports && stream.Read(_readBuf) > 0) drained++;
        }
        catch (TimeoutException) { /* fila vazia */ }
        finally
        {
            stream.ReadTimeout = previous;
        }
        if (drained > 0) Log.Debug($"Hub: {drained} resposta(s) atrasada(s) descartada(s).");
    }

    private void CloseStream()
    {
        try { _stream?.Dispose(); } catch { }
        _commandsSinceColor = int.MaxValue;
        _drainPending = false;
        _needsSoftwareMode = false;
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
