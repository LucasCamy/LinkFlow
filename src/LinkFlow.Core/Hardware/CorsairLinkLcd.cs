using System.Buffers.Binary;
using HidSharp;

namespace LinkFlow.Core;

/// <summary>Display LCD 480x480 da bomba Corsair (1B1C:0C4E).</summary>
public sealed class CorsairLinkLcd : ILcdDisplay
{
    public const int VendorId = 0x1B1C;
    public const int ProductId = 0x0C4E;

    private const int LcdBufferSize = 1024;
    private const int LcdHeaderSize = 8;
    private const int MaxChunkSize = LcdBufferSize - LcdHeaderSize; // 1016 bytes

    private readonly object _syncLock = new();
    private readonly byte[] _buffer = new byte[LcdBufferSize];
    private HidStream? _stream;

    public bool IsConnected => _stream != null;

    public bool Connect()
    {
        lock (_syncLock)
        {
            Disconnect();

            HidDevice? device;
            try
            {
                var devices = DeviceList.Local.GetHidDevices(VendorId, ProductId).ToList();
                device = devices.FirstOrDefault(d => d.GetMaxOutputReportLength() >= 1024)
                         ?? devices.FirstOrDefault(d => d.GetMaxOutputReportLength() > 0);
            }
            catch
            {
                return false;
            }

            if (device == null || !device.TryOpen(out var stream)) return false;

            _stream = stream;
            _stream.ReadTimeout = 1000;
            _stream.WriteTimeout = 1000;
            return true;
        }
    }

    public bool SendJpegFrame(byte[] jpegBytes)
    {
        lock (_syncLock)
        {
            if (_stream == null) return false;

            int offset = 0;
            int chunkIndex = 0;
            int totalLen = jpegBytes.Length;

            while (offset < totalLen)
            {
                int chunkSize = Math.Min(MaxChunkSize, totalLen - offset);
                bool isLast = (offset + chunkSize) >= totalLen;

                Array.Clear(_buffer);
                _buffer[0] = 0x02; // Report ID 0x02
                _buffer[1] = 0x05;
                _buffer[2] = 0x01;
                _buffer[3] = isLast ? (byte)0x01 : (byte)0x00;
                _buffer[4] = (byte)chunkIndex;
                _buffer[5] = 0x00;
                BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(6, 2), (ushort)chunkSize);
                Array.Copy(jpegBytes, offset, _buffer, LcdHeaderSize, chunkSize);

                try
                {
                    _stream.Write(_buffer);
                }
                catch (Exception ex)
                {
                    Log.Debug($"LCD write falhou: {ex.Message}");
                    Disconnect();
                    return false;
                }

                offset += chunkSize;
                chunkIndex++;
            }

            return true;
        }
    }

    public void Disconnect()
    {
        lock (_syncLock)
        {
            try { _stream?.Dispose(); } catch { }
            _stream = null;
        }
    }

    public void Dispose() => Disconnect();
}
