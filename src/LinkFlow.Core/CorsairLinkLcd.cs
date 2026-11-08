using System;
using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using HidSharp;

namespace LinkFlow.Core;

public class CorsairLinkLcd : IDisposable
{
    public const int VendorId = 0x1B1C;
    public const int ProductId = 0x0C4E;

    private const int LcdBufferSize = 1024;
    private const int LcdHeaderSize = 8;
    private const int MaxChunkSize = LcdBufferSize - LcdHeaderSize; // 1016 bytes

    private readonly object _syncLock = new();
    private HidDevice? _device;
    private HidStream? _stream;

    public bool IsConnected => _stream != null;

    public bool Connect()
    {
        lock (_syncLock)
        {
            Disconnect();

            var devices = DeviceList.Local.GetHidDevices()
                .Where(d => d.VendorID == VendorId && d.ProductID == ProductId)
                .ToList();

            // Find interface that allows output reports of 1024 bytes
            _device = devices.FirstOrDefault(d => d.GetMaxOutputReportLength() >= 1024)
                      ?? devices.FirstOrDefault(d => d.GetMaxOutputReportLength() > 0);

            if (_device == null) return false;

            if (!_device.TryOpen(out _stream))
            {
                return false;
            }

            _stream.ReadTimeout = 1000;
            _stream.WriteTimeout = 1000;

            return true;
        }
    }

    public byte[] RenderDashboardFrame(float cpuTemp, float gpuTemp, int pumpRpm, byte r, byte g, byte b, string statusText = "OPERACIONAL")
    {
        using var bmp = new Bitmap(480, 480, PixelFormat.Format32bppArgb);
        using (var gCtx = Graphics.FromImage(bmp))
        {
            gCtx.SmoothingMode = SmoothingMode.AntiAlias;
            gCtx.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            gCtx.InterpolationMode = InterpolationMode.HighQualityBicubic;

            // 1. Dark Deep Background
            using var bgBrush = new SolidBrush(Color.FromArgb(11, 15, 25));
            gCtx.FillRectangle(bgBrush, 0, 0, 480, 480);

            // 2. Circular Outer Track (subtle grey)
            var themeColor = Color.FromArgb(r, g, b);
            using var trackPen = new Pen(Color.FromArgb(30, 41, 59), 10);
            gCtx.DrawArc(trackPen, 35, 35, 410, 410, 135, 270);

            // 3. Dynamic Progress Arc (Theme color based on CPU temp, max 100°C)
            float sweepAngle = Math.Clamp(cpuTemp / 100f * 270f, 10f, 270f);
            using var progressPen = new Pen(themeColor, 12);
            progressPen.StartCap = LineCap.Round;
            progressPen.EndCap = LineCap.Round;
            gCtx.DrawArc(progressPen, 35, 35, 410, 410, 135, sweepAngle);

            // 4. Inner Ring (subtle)
            using var innerPen = new Pen(Color.FromArgb(20, Color.FromArgb(r, g, b)), 2);
            gCtx.DrawEllipse(innerPen, 65, 65, 350, 350);

            // 5. Header: "LINKFLOW"
            using var headerFont = new Font("Segoe UI", 13, FontStyle.Bold);
            using var headerBrush = new SolidBrush(Color.FromArgb(148, 163, 184));
            var headerFormat = new StringFormat { Alignment = StringAlignment.Center };
            gCtx.DrawString("⚡ LINKFLOW TITAN", headerFont, headerBrush, new PointF(240, 95), headerFormat);

            // 6. Center: CPU Temp (Big bold number)
            string cpuStr = $"{(int)Math.Round(cpuTemp)}°";
            using var tempFont = new Font("Segoe UI", 68, FontStyle.Bold);
            using var tempBrush = new SolidBrush(Color.White);
            gCtx.DrawString(cpuStr, tempFont, tempBrush, new PointF(240, 135), headerFormat);

            // CPU TEMP Label
            using var labelFont = new Font("Segoe UI", 14, FontStyle.Bold);
            using var themeBrush = new SolidBrush(themeColor);
            gCtx.DrawString("CPU TEMPERATURE", labelFont, themeBrush, new PointF(240, 245), headerFormat);

            // 7. Lower Badges: GPU Temp & Pump RPM
            // GPU
            using var badgeFont = new Font("Segoe UI", 12, FontStyle.Regular);
            using var badgeValFont = new Font("Segoe UI", 16, FontStyle.Bold);
            using var textMuted = new SolidBrush(Color.FromArgb(100, 116, 139));
            using var textLight = new SolidBrush(Color.White);

            gCtx.DrawString("GPU CORE", badgeFont, textMuted, new PointF(150, 290), headerFormat);
            gCtx.DrawString($"{gpuTemp:F0}°C", badgeValFont, textLight, new PointF(150, 312), headerFormat);

            // PUMP
            gCtx.DrawString("AIO BOMBA", badgeFont, textMuted, new PointF(330, 290), headerFormat);
            gCtx.DrawString($"{pumpRpm} RPM", badgeValFont, textLight, new PointF(330, 312), headerFormat);

            // Status bar at bottom
            using var statusFont = new Font("Segoe UI", 10, FontStyle.Bold);
            using var statusBrush = new SolidBrush(Color.FromArgb(52, 211, 153)); // Emerald green
            gCtx.DrawString($"● {statusText}", statusFont, statusBrush, new PointF(240, 375), headerFormat);
        }

        using var ms = new MemoryStream();
        var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        var encParams = new EncoderParameters(1);
        encParams.Param[0] = new EncoderParameter(Encoder.Quality, 88L);
        bmp.Save(ms, encoder, encParams);
        return ms.ToArray();
    }

    public bool SendJpegFrame(byte[] jpegBytes)
    {
        lock (_syncLock)
        {
            if (_stream == null)
            {
                if (!Connect()) return false;
            }

            if (_stream == null) return false;

            int offset = 0;
            int chunkIndex = 0;
            int totalLen = jpegBytes.Length;

            while (offset < totalLen)
            {
                int chunkSize = Math.Min(MaxChunkSize, totalLen - offset);
                bool isLast = (offset + chunkSize) >= totalLen;

                var bufferW = new byte[LcdBufferSize];
                bufferW[0] = 0x02; // Report ID 0x02
                bufferW[1] = 0x05;
                bufferW[2] = 0x01;
                bufferW[3] = isLast ? (byte)0x01 : (byte)0x00;
                bufferW[4] = (byte)chunkIndex;
                bufferW[5] = 0x00;
                BinaryPrimitives.WriteUInt16LittleEndian(bufferW.AsSpan(6, 2), (ushort)chunkSize);

                Array.Copy(jpegBytes, offset, bufferW, LcdHeaderSize, chunkSize);

                try
                {
                    _stream.Write(bufferW);
                }
                catch
                {
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
            if (_stream != null)
            {
                try { _stream.Dispose(); } catch { }
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
