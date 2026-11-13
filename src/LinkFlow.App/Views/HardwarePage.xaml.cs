using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using LinkFlow.App.Services;
using LinkFlow.App.ViewModels;
using LinkFlow.Core;

namespace LinkFlow.App.Views;

public partial class HardwarePage : UserControl
{
    private IReadOnlyList<HardwarePart>? _parts;
    private string _inventoryNote = string.Empty;

    private sealed record PartGroup(string Category, IReadOnlyList<HardwarePart> Parts)
    {
        public string Icon => Category switch
        {
            "Windows" => "\uECAA",
            "Processador" or "Placa-mãe" or "BIOS" or "Memória" => "\uE950",
            "Placa de vídeo" or "Monitor" or "Display LCD" => "\uE7F4",
            "Armazenamento" => "\uE8B7",
            "Rede" => "\uE701",
            "Áudio" => "\uE767",
            "Ventoinhas" => "\uEC4A",
            "RGB" => "\uE790",
            _ => "\uE7F8"
        };
    }

    public HardwarePage()
    {
        InitializeComponent();
        var families = LinkDeviceCatalog.Families.ToArray();
        SupportedParts.ItemsSource = GroupParts(
        [
            new("Ventoinhas", "Corsair iCUE LINK System Hub",
                string.Join(", ", families.Where(f => f.Role == LinkDeviceRole.Fan).Select(f => f.Name)) + " · RPM e curvas de velocidade"),
            new("Bombas", "Corsair iCUE LINK System Hub",
                string.Join(", ", families.Where(f => f.Role == LinkDeviceRole.Pump).Select(f => f.Name)) + " · RPM e controle de velocidade"),
            new("Módulos LINK", "Identificação pelo hub",
                string.Join(", ", families.Where(f => f.Role == LinkDeviceRole.Module).Select(f => f.Name))),
            new("RGB", "iCUE LINK, ASUS Aura, MSI Mystic Light, Corsair DDR5 RGB e Logitech G",
                "A disponibilidade depende do controlador. A memória usa PawnIO e os periféricos Logitech usam G HUB."),
            new("Sensores", "CPUs, GPUs e discos compatíveis com LibreHardwareMonitor",
                "As métricas disponíveis variam por modelo e permissões do Windows."),
            new("Display LCD", "Corsair iCUE LINK LCD", "Métricas, fotos e GIFs")
        ]);
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        ScanButton.IsEnabled = false;
        InventoryFilterPanel.IsEnabled = false;
        InventoryStatus.Text = "Lendo as peças deste PC…";
        try
        {
            var snapshot = vm.Snapshot;
            var parts = new List<HardwarePart>();
            if (snapshot.HubConnected)
            {
                parts.Add(new("Hub", "Corsair iCUE LINK System Hub", $"Firmware {snapshot.HubFirmware}"));
                parts.AddRange(snapshot.Fans.Select(f => new HardwarePart("iCUE LINK",
                    vm.Fans.FirstOrDefault(fan => fan.Key == f.Key)?.Name ?? f.DefaultName,
                    $"Canal {f.Channel}")));
            }
            if (snapshot.LcdConnected) parts.Add(new("Display LCD", "Corsair iCUE LINK LCD", "Conectado"));

            if (App.Current.Options.Simulate)
            {
                if (snapshot.CpuName is { } cpu) parts.Insert(0, new("Processador", cpu, "Simulado"));
                if (snapshot.GpuName is { } gpu) parts.Insert(0, new("Placa de vídeo", gpu, "Simulada"));
                parts.Insert(0, new("Memória", "Módulo DDR5 (simulado)", "32 GiB · 6000 MT/s configurados · DIMMA2"));
                parts.Insert(0, new("Armazenamento", "SSD de 1 TB (simulado)", "SSD · NVMe · 1000 GB"));
                AddGpuMemory(parts, snapshot);
                ShowInventory(parts, " Inventário simulado.");
                return;
            }

            var inventory = await Task.Run(HardwareInventory.Read);
            parts.InsertRange(0, inventory.Parts);
            AddGpuMemory(parts, vm.Snapshot);
            ShowInventory(parts, inventory.Unavailable.Count == 0 ? string.Empty :
                $" Leitura indisponível para {string.Join(", ", inventory.Unavailable)}.");
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao detectar peças", ex);
            _parts = null;
            DetectedParts.ItemsSource = null;
            InventoryFilterPanel.Visibility = Visibility.Collapsed;
            InventoryStatus.Text = "Não foi possível ler o inventário. Tente novamente.";
        }
        finally
        {
            ScanButton.IsEnabled = true;
            InventoryFilterPanel.IsEnabled = true;
        }
    }

    private static void AddGpuMemory(List<HardwarePart> parts, EngineSnapshot snapshot)
    {
        if (snapshot.GpuVramTotalGb is not > 0 || string.IsNullOrWhiteSpace(snapshot.GpuName)) return;
        bool Matches(HardwarePart part) => part.Category == "Placa de vídeo" &&
            part.Name.Equals(snapshot.GpuName, StringComparison.OrdinalIgnoreCase);
        if (parts.Count(Matches) != 1) return;

        int index = parts.FindIndex(Matches);
        var gpu = parts[index];
        string vram = $"{snapshot.GpuVramTotalGb:0.#} GiB de VRAM";
        parts[index] = gpu with { Details = gpu.Details.Length == 0 ? vram : $"{gpu.Details} · {vram}" };
    }

    private void ShowInventory(IReadOnlyList<HardwarePart> parts, string note)
    {
        _parts = parts;
        _inventoryNote = note;
        InventoryFilterPanel.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ScanButton.Content = "Detectar novamente";
        ApplyFilter();
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (_parts == null) return;
        string filter = InventoryFilter.Text.Trim();
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        bool Contains(string value) => compare.IndexOf(value, filter, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
        var results = filter.Length == 0 ? _parts : _parts.Where(part =>
            Contains(part.Name) || Contains(part.Category) || Contains(part.Details)).ToArray();
        DetectedParts.ItemsSource = GroupParts(results);
        InventoryStatus.Text = (filter.Length == 0 ? $"{results.Count} itens no inventário." :
            $"{results.Count} de {_parts.Count} itens.") + _inventoryNote;
    }

    private static IReadOnlyList<PartGroup> GroupParts(IEnumerable<HardwarePart> parts) =>
        parts.GroupBy(part => part.Category)
            .OrderBy(group => group.Key switch
            {
                "Processador" => 0,
                "Placa de vídeo" => 1,
                "Memória" => 2,
                "Armazenamento" => 3,
                _ => 4
            })
            .Select(group => new PartGroup(group.Key, group.ToArray())).ToArray();
}
