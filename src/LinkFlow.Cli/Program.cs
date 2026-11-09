using System;
using LinkFlow.Core;

class Program
{
    static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("==================================================");
        Console.WriteLine("   LinkFlow - Diagnóstico de Hardware e RGB");
        Console.WriteLine("==================================================");
        Console.WriteLine();

        var factory = new RealHardwareFactory();

        Console.WriteLine("1. Verificando Placa de Vídeo (ASUS Aura)...");
        using var gpu = factory.CreateGpuRgb();
        gpu.Initialize();
        Console.WriteLine($"   Disponível: {gpu.IsAvailable} • Nome: {gpu.Name}");

        Console.WriteLine("2. Verificando Placa-Mãe (MSI Mystic Light)...");
        using var mb = factory.CreateMotherboardRgb();
        mb.Initialize();
        Console.WriteLine($"   Disponível: {mb.IsAvailable} • Nome: {mb.Name}");

        Console.WriteLine("3. Verificando Memórias (Corsair Vengeance DDR5)...");
        using var ram = factory.CreateRamRgb();
        ram.Initialize();
        Console.WriteLine($"   Disponível: {ram.IsAvailable} • Nome: {ram.Name}");

        Console.WriteLine("4. Verificando Periféricos (Logitech G)...");
        using var periph = factory.CreatePeripheralsRgb();
        periph.Initialize();
        Console.WriteLine($"   Disponível: {periph.IsAvailable} • Nome: {periph.Name}");

        Console.WriteLine();
        Console.WriteLine("Diagnóstico concluído.");
    }
}
