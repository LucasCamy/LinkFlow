using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using LinkFlow.App.Services;
using LinkFlow.App.ViewModels;
using LinkFlow.Core;
using LinkFlow.App.Resources;

namespace LinkFlow.App.Views;

public partial class HardwarePage : UserControl
{
    private IReadOnlyList<HardwarePart>? _parts;
    private string _inventoryNote = string.Empty;

    private sealed record PartGroup(string Category, IReadOnlyList<HardwarePart> Parts)
    {
        /// <summary>Nome da categoria no idioma da interface (Category continua em português: é a chave interna).</summary>
        public string Label => LabelFor(Category);

        public static string LabelFor(string category) => category switch
        {
            "Processador" => Strings.Dash_Cpu,
            "Placa de vídeo" => Strings.Dash_Gpu,
            "Memória" => Strings.Dash_Memory,
            "Armazenamento" => Strings.Dash_Storage,
            "Rede" => Strings.Dash_Network,
            "Ventoinhas" => Strings.Nav_Fans,
            "Placa-mãe" => Strings.Hw_CatMotherboard,
            "Monitor" => Strings.Hw_CatMonitor,
            "Display LCD" => Strings.Hw_CatLcd,
            "Áudio" => Strings.Hw_CatAudio,
            "Periféricos" => Strings.Hw_CatPeripherals,
            "Bombas" => Strings.Hw_CatPumps,
            "Módulos LINK" => Strings.Hw_CatLinkModules,
            "Sensores" => Strings.Hw_CatSensors,
            _ => category   // Windows, BIOS, RGB, Hub, iCUE LINK
        };

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
                string.Join(", ", families.Where(f => f.Role == LinkDeviceRole.Fan).Select(f => f.Name)) + Strings.Hw_RPMCurvasVelocidade),
            new("Bombas", "Corsair iCUE LINK System Hub",
                string.Join(", ", families.Where(f => f.Role == LinkDeviceRole.Pump).Select(f => f.Name)) + Strings.Hw_RPMControleVelocidade),
            new("Módulos LINK", Strings.Hw_IdentificacaoPeloHub,
                string.Join(", ", families.Where(f => f.Role == LinkDeviceRole.Module).Select(f => f.Name))),
            new("RGB", Strings.Hw_ICUELINKASUSAura,
                Strings.Hw_DisponibilidadeDependeControladorMemoria),
            new("Sensores", Strings.Hw_CPUsGPUsDiscosCompativeis,
                Strings.Hw_MetricasDisponiveisVariamModelo),
            new("Display LCD", "Corsair iCUE LINK LCD", Strings.Hw_MetricasFotosGIFs)
        ]);
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        ScanButton.IsEnabled = false;
        InventoryFilterPanel.IsEnabled = false;
        InventoryStatus.Text = Strings.Hw_LendoPecasDestePC;
        try
        {
            var snapshot = vm.Snapshot;
            var parts = new List<HardwarePart>();
            if (snapshot.HubConnected)
            {
                parts.Add(new("Hub", "Corsair iCUE LINK System Hub", string.Format(Strings.Hw_Firmware, snapshot.HubFirmware)));
                parts.AddRange(snapshot.Fans.Select(f => new HardwarePart("iCUE LINK",
                    vm.Fans.FirstOrDefault(fan => fan.Key == f.Key)?.Name ?? f.DefaultName,
                    string.Format(Strings.Hw_Canal, f.Channel))));
            }
            if (snapshot.LcdConnected) parts.Add(new("Display LCD", "Corsair iCUE LINK LCD", Strings.Hw_Conectado));

            if (App.Current.Options.Simulate)
            {
                if (snapshot.CpuName is { } cpu) parts.Insert(0, new("Processador", cpu, Strings.Hw_Simulado));
                if (snapshot.GpuName is { } gpu) parts.Insert(0, new("Placa de vídeo", gpu, Strings.Hw_SimulatedF));
                parts.Insert(0, new("Memória", Strings.Hw_ModuloDDR5Simulado, Strings.Hw_32GiB6000MT));
                parts.Insert(0, new("Armazenamento", Strings.Hw_SSD1TBSimulado, "SSD · NVMe · 1000 GB"));
                AddGpuMemory(parts, snapshot);
                ShowInventory(parts, Strings.Hw_InventarioSimulado);
                return;
            }

            var inventory = await Task.Run(HardwareInventory.Read);
            parts.InsertRange(0, inventory.Parts);
            AddGpuMemory(parts, vm.Snapshot);
            ShowInventory(parts, inventory.Unavailable.Count == 0 ? string.Empty :
                string.Format(Strings.Hw_ReadUnavailable, string.Join(", ", inventory.Unavailable.Select(PartGroup.LabelFor))));
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao detectar peças", ex);
            _parts = null;
            DetectedParts.ItemsSource = null;
            InventoryFilterPanel.Visibility = Visibility.Collapsed;
            InventoryStatus.Text = Strings.Hw_NaoFoiPossivelLer;
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
        string vram = string.Format(Strings.Hw_GiBVRAM, snapshot.GpuVramTotalGb);
        parts[index] = gpu with { Details = gpu.Details.Length == 0 ? vram : $"{gpu.Details} · {vram}" };
    }

    private void ShowInventory(IReadOnlyList<HardwarePart> parts, string note)
    {
        _parts = parts;
        _inventoryNote = note;
        InventoryFilterPanel.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ScanButton.Content = Strings.Hw_DetectarNovamente;
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
            Contains(part.Name) || Contains(part.Category) || Contains(PartGroup.LabelFor(part.Category)) || Contains(part.Details)).ToArray();
        DetectedParts.ItemsSource = GroupParts(results);
        InventoryStatus.Text = (filter.Length == 0 ? string.Format(Strings.Hw_ItensInventario, results.Count) :
            string.Format(Strings.Hw_Itens, results.Count, _parts.Count)) + _inventoryNote;
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
