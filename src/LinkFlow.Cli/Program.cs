using System;
using System.Linq;
using System.Threading;
using HidSharp;

class Program
{
    static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== Testando Envio Consecutivo MSI MS-7E12 ===");

        var device = DeviceList.Local.GetHidDevices()
            .FirstOrDefault(d => (d.VendorID == 0x0DB0 || d.VendorID == 0x1462) && d.ProductID == 0x0076);

        if (device == null || !device.TryOpen(out var stream))
        {
            Console.WriteLine("Falha ao abrir dispositivo!");
            return;
        }

        using (stream)
        {
            // 1. Ler estado atual
            byte[] oldState = new byte[185];
            oldState[0] = 0x52;
            stream.GetFeature(oldState);
            Console.WriteLine("Estado atual lido. Primeiros 16 bytes: " + BitConverter.ToString(oldState, 0, 16));

            // 2. Preparar novo estado (Vermelho puro: 255, 0, 0)
            byte[] newState = (byte[])oldState.Clone();
            int[] zoneOffsets = new int[]
            {
                0x01, // JRGB1
                0x0B, // JPIPE1
                0x15, // JPIPE2
                0x1F, // JARGB_V2_1
                0x2A, // JARGB_V2_2
                0x35, // CORSAIR
                0x40, // CORSAIR Backup
                0x4A, 0x54, 0x5E, 0x68, 0x72, 0x7C, 0x86, 0x90, 0x9A, 0xA4, // Onboard LEDs 1..10
                0xAE  // JRGB2
            };

            foreach (var off in zoneOffsets)
            {
                newState[off + 0] = 0x01; // Static
                newState[off + 1] = 0xFF; // R
                newState[off + 2] = 0x00; // G
                newState[off + 3] = 0x00; // B
                newState[off + 4] = 0xA8; // Speed + bit 7 enable (0x80 | 0x28)
                newState[off + 5] = 0xFF; // Brightness
            }
            newState[184] = 0x00; // Live RAM

            // 3. Enviar oldState primeiro
            Console.WriteLine("Enviando oldState...");
            stream.SetFeature(oldState);
            Thread.Sleep(10);

            // 4. Enviar newState
            Console.WriteLine("Enviando newState (Vermelho)...");
            stream.SetFeature(newState);
            Console.WriteLine("Concluído! Verifique se as luzes da placa-mãe/ARGB ficaram vermelhas.");
        }
    }
}
