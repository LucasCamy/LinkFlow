using System;
using LinkFlow.Core;

class Program
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // O diagnóstico abre o hardware direto: com o serviço rodando, os dois disputariam GPU, placa-mãe e SMBus
        bool serviceRunning;
        try { serviceRunning = System.IO.Directory.EnumerateFiles(@"\\.\pipe\", LinkFlow.Core.Ipc.PipeProtocol.PipeName).Any(); }
        catch { serviceRunning = false; }
        if (serviceRunning && !args.Contains("--force", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("O serviço LinkFlow está em execução e controla o hardware.");
            Console.WriteLine("Pare o serviço antes do diagnóstico ou use --force (pode causar cores e leituras erradas).");
            return 2;
        }

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
        return 0;
    }
}
