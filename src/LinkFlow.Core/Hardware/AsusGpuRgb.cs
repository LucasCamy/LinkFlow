using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace LinkFlow.Core;

public sealed class AsusGpuRgb : IRgbDevice
{
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct NvI2CInfo
    {
        [FieldOffset(0)]  public uint Version;
        [FieldOffset(4)]  public uint DisplayMask;
        [FieldOffset(8)]  public byte IsDDCPort;
        [FieldOffset(9)]  public byte I2CDevAddress;
        [FieldOffset(16)] public IntPtr I2CRegAddress;
        [FieldOffset(24)] public uint RegAddrSize;
        [FieldOffset(32)] public IntPtr Data;
        [FieldOffset(40)] public uint Size;
        [FieldOffset(44)] public uint I2CSpeed;
        [FieldOffset(48)] public uint I2CSpeedKhz;
        [FieldOffset(52)] public byte PortId;
        [FieldOffset(56)] public uint IsPortIdSet;
    }

    private delegate int NvAPI_Initialize_Delegate();
    private delegate int NvAPI_EnumPhysicalGPUs_Delegate([Out] IntPtr[] handles, out int count);
    private delegate int NvAPI_I2CWriteEx_Delegate(IntPtr gpuHandle, ref NvI2CInfo pI2cInfo, ref uint pData);
    private delegate int NvAPI_I2CReadEx_Delegate(IntPtr gpuHandle, ref NvI2CInfo pI2cInfo, ref uint pData);

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr nvapi_QueryInterface(uint id);

    private readonly object _syncLock = new();
    private IntPtr _gpu = IntPtr.Zero;
    private NvAPI_I2CReadEx_Delegate? _readEx;
    private NvAPI_I2CWriteEx_Delegate? _writeEx;
    private const byte SLAVE_ADDR = 0x67 << 1; // 0xCE
    private const byte PORT_ID = 1;

    public bool IsAvailable { get; private set; }
    public string ControllerName { get; private set; } = string.Empty;
    public int LedCount { get; private set; } = 30;

    public string Name => ControllerName;

    public AsusGpuRgb()
    {
    }

    public void Initialize()
    {
        try
        {
            IntPtr pInit = nvapi_QueryInterface(0x0150E828);
            IntPtr pEnum = nvapi_QueryInterface(0xE5AC921F);
            IntPtr pI2cReadEx = nvapi_QueryInterface(0x4D7B0709);
            IntPtr pI2cWriteEx = nvapi_QueryInterface(0x283AC65A);

            if (pInit == IntPtr.Zero || pEnum == IntPtr.Zero || pI2cWriteEx == IntPtr.Zero)
            {
                return;
            }

            var init = Marshal.GetDelegateForFunctionPointer<NvAPI_Initialize_Delegate>(pInit);
            int res = init();
            if (res != 0) return;

            var enumGpus = Marshal.GetDelegateForFunctionPointer<NvAPI_EnumPhysicalGPUs_Delegate>(pEnum);
            IntPtr[] handles = new IntPtr[64];
            res = enumGpus(handles, out int count);
            if (res != 0 || count == 0) return;

            _gpu = handles[0];
            if (pI2cReadEx != IntPtr.Zero)
            {
                _readEx = Marshal.GetDelegateForFunctionPointer<NvAPI_I2CReadEx_Delegate>(pI2cReadEx);
            }
            _writeEx = Marshal.GetDelegateForFunctionPointer<NvAPI_I2CWriteEx_Delegate>(pI2cWriteEx);

            // Read controller name
            StringBuilder name = new StringBuilder();
            for (ushort i = 0; i < 16; i++)
            {
                byte c = AuraReadRegister((ushort)(0x1000 + i));
                if (c > 0 && c != 0xFF) name.Append((char)c);
            }

            string detected = name.ToString().Trim();
            if (!string.IsNullOrEmpty(detected))
            {
                ControllerName = detected;
                IsAvailable = true;

                // Read LED Count (Reg 0x1C03)
                byte leds = AuraReadRegister(0x1C03);
                if (leds > 0 && leds <= 60)
                {
                    LedCount = leds;
                }
            }
        }
        catch
        {
            IsAvailable = false;
        }
    }

    private bool I2CWrite(byte reg, byte[] data)
    {
        if (_writeEx == null || _gpu == IntPtr.Zero) return false;

        GCHandle hReg = GCHandle.Alloc(new byte[] { reg }, GCHandleType.Pinned);
        GCHandle hData = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var info = new NvI2CInfo
            {
                Version = (3 << 16) | 64,
                DisplayMask = 0,
                IsDDCPort = 0,
                I2CDevAddress = SLAVE_ADDR,
                I2CRegAddress = hReg.AddrOfPinnedObject(),
                RegAddrSize = 1,
                Data = hData.AddrOfPinnedObject(),
                Size = (uint)data.Length,
                I2CSpeed = 0xFFFF,
                I2CSpeedKhz = 4, // 100 kHz
                PortId = PORT_ID,
                IsPortIdSet = 1
            };

            uint dummy = 0;
            int status = _writeEx(_gpu, ref info, ref dummy);
            return status == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            hReg.Free();
            hData.Free();
        }
    }

    private byte AuraReadRegister(ushort reg)
    {
        if (_readEx == null || _gpu == IntPtr.Zero) return 0xFF;

        // Write address to 0x00
        I2CWrite(0x00, new byte[] { (byte)((reg >> 8) & 0xFF), (byte)(reg & 0xFF) });

        // Read from 0x81
        byte[] data = new byte[1];
        GCHandle hReg = GCHandle.Alloc(new byte[] { 0x81 }, GCHandleType.Pinned);
        GCHandle hData = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var info = new NvI2CInfo
            {
                Version = (3 << 16) | 64,
                DisplayMask = 0,
                IsDDCPort = 0,
                I2CDevAddress = SLAVE_ADDR,
                I2CRegAddress = hReg.AddrOfPinnedObject(),
                RegAddrSize = 1,
                Data = hData.AddrOfPinnedObject(),
                Size = 1,
                I2CSpeed = 0xFFFF,
                I2CSpeedKhz = 4,
                PortId = PORT_ID,
                IsPortIdSet = 1
            };

            uint dummy = 0;
            int status = _readEx(_gpu, ref info, ref dummy);
            return status == 0 ? data[0] : (byte)0xFF;
        }
        catch
        {
            return 0xFF;
        }
        finally
        {
            hReg.Free();
            hData.Free();
        }
    }

    private void AuraWriteRegister(ushort reg, byte value)
    {
        I2CWrite(0x00, new byte[] { (byte)((reg >> 8) & 0xFF), (byte)(reg & 0xFF) });
        I2CWrite(0x01, new byte[] { value });
    }

    public bool SetColor(byte r, byte g, byte b, byte brightnessPercent)
    {
        float f = Math.Clamp(brightnessPercent / 100f, 0f, 1f);
        return SetColorRaw((byte)(r * f), (byte)(g * f), (byte)(b * f));
    }

    private bool SetColorRaw(byte r, byte g, byte b)
    {
        if (!IsAvailable) return false;

        lock (_syncLock)
        {
            try
            {
                // 1. Enable Direct Mode (Reg 0x8020 = 0x01, Reg 0x80A0 = 0x01)
                AuraWriteRegister(0x8020, 0x01);
                AuraWriteRegister(0x80A0, 0x01);

                // 2. Point to Color Register 0x8100
                I2CWrite(0x00, new byte[] { 0x81, 0x00 });

                // 3. Prepare payload: [R, B, G] order for ASUS GPU Aura
                int leds = LedCount > 0 ? LedCount : 30;
                List<byte> rgbList = new List<byte>(leds * 3);
                for (int i = 0; i < leds; i++)
                {
                    rgbList.Add(r);
                    rgbList.Add(b); // Blue before Green in Aura GPU protocol
                    rgbList.Add(g);
                }

                // 4. Send chunks (max 31 bytes payload per packet)
                int offset = 0;
                while (offset < rgbList.Count)
                {
                    int packetSize = Math.Min(rgbList.Count - offset, 31);
                    byte[] packet = new byte[packetSize + 1];
                    packet[0] = (byte)packetSize;
                    for (int j = 0; j < packetSize; j++)
                    {
                        packet[1 + j] = rgbList[offset + j];
                    }

                    I2CWrite(0x03, packet);
                    offset += packetSize;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        // Safe cleanup if necessary
    }
}
