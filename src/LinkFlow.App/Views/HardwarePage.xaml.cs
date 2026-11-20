using System.Windows;
using System.Windows.Controls;
using LinkFlow.App.Services;
using LinkFlow.App.ViewModels;
using LinkFlow.Core;
using LinkFlow.App.Resources;

namespace LinkFlow.App.Views;

public partial class HardwarePage : UserControl
{
    private IReadOnlyList<HardwareComponent>? _inventory;
    private IReadOnlyList<HardwareCard>? _cards;
    private string _inventoryNote = string.Empty;
    private string _linkState = string.Empty;
    private MainViewModel? _vm;
    private Task? _scan;

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
            "Ventoinhas" => "",
            "RGB" => "",
            "Display LCD" => "",
            _ => ""
        };
    }

    public HardwarePage()
    {
        InitializeComponent();
        var families = LinkDeviceCatalog.Families.ToArray();
        SupportedParts.ItemsSource = new PartGroup[]
        {
            new("Ventoinhas", [new("Ventoinhas", "Corsair iCUE LINK System Hub",
                string.Join(", ", families.Where(f => f.Role == LinkDeviceRole.Fan).Select(f => f.Name)) + Strings.Hw_RPMCurvasVelocidade)]),
            new("Bombas", [new("Bombas", "Corsair iCUE LINK System Hub",
                string.Join(", ", families.Where(f => f.Role == LinkDeviceRole.Pump).Select(f => f.Name)) + Strings.Hw_RPMControleVelocidade)]),
            new("Módulos LINK", [new("Módulos LINK", Strings.Hw_IdentificacaoPeloHub,
                string.Join(", ", families.Where(f => f.Role == LinkDeviceRole.Module).Select(f => f.Name)))]),
            new("RGB", [new("RGB", Strings.Hw_ICUELINKASUSAura, Strings.Hw_DisponibilidadeDependeControladorMemoria)]),
            new("Sensores", [new("Sensores", Strings.Hw_CPUsGPUsDiscosCompativeis, Strings.Hw_MetricasDisponiveisVariamModelo)]),
            new("Display LCD", [new("Display LCD", "Corsair iCUE LINK LCD", Strings.Hw_MetricasFotosGIFs)])
        };

        // O inventário é lido na primeira vez que a tela aparece (em segundo plano; não trava a interface)
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && _cards == null && _scan == null) _scan = ScanAsync();
        };

        // O cartão do iCUE LINK acompanha o hub (conectar, trocar dispositivos) sem precisar detectar de novo
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.SnapshotApplied -= OnSnapshotApplied;
            _vm = DataContext as MainViewModel;
            if (_vm != null) _vm.SnapshotApplied += OnSnapshotApplied;
        };
    }

    private void OnSnapshotApplied()
    {
        if (_inventory == null || _vm == null || !IsVisible || LinkState(_vm.Snapshot) == _linkState) return;
        ShowInventory(_inventory, _inventoryNote);
    }

    private static string LinkState(EngineSnapshot s) =>
        s.HubConnected ? $"{s.HubFirmware}|{s.LcdConnected}|{string.Join(",", s.Fans.Select(f => f.Key))}" : string.Empty;

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_scan is { IsCompleted: false }) return;
        _scan = ScanAsync();
        await _scan;
    }

    private async Task ScanAsync()
    {
        if (DataContext is not MainViewModel vm) return;
        ScanButton.IsEnabled = false;
        InventoryFilter.IsEnabled = false;
        InventoryStatus.Text = Strings.Hw_LendoPecasDestePC;
        try
        {
            var snapshot = vm.Snapshot;
            HardwareInventoryResult inventory;
            string note;
            if (App.Current.Options.Simulate)
            {
                inventory = HardwareInventory.Simulated(snapshot, DateTime.Now);
                note = Strings.Hw_InventarioSimulado;
            }
            else
            {
                string? gpuName = snapshot.GpuName;
                float? vram = snapshot.GpuVramTotalGb;
                inventory = await Task.Run(() => HardwareInventory.Read(gpuName, vram));
                note = inventory.Unavailable.Count == 0 ? string.Empty : string.Format(UiLanguage.Culture, Strings.Hw_ReadUnavailable,
                    string.Join(", ", inventory.Unavailable.Select(HardwareVisuals.Title)));
            }

            ShowInventory(inventory.Components, note);
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao detectar peças", ex);
            _inventory = null;
            _cards = null;
            DetectedParts.ItemsSource = null;
            InventoryFilter.Visibility = Visibility.Collapsed;
            InventoryStatus.Text = Strings.Hw_NaoFoiPossivelLer;
        }
        finally
        {
            ScanButton.IsEnabled = true;
            InventoryFilter.IsEnabled = true;
        }
    }

    /// <summary>Hub e dispositivos do iCUE LINK, a partir do estado do engine.</summary>
    private static HardwareComponent? IcueLink(MainViewModel vm, EngineSnapshot snapshot)
    {
        if (!snapshot.HubConnected) return null;
        var devices = snapshot.Fans.Select(f => new SpecRow(vm.Fans.FirstOrDefault(fan => fan.Key == f.Key)?.Name ?? f.DefaultName,
            string.Join(" · ", new[] { string.Format(UiLanguage.Culture, Strings.Hw_Canal, f.Channel),
                    LinkDeviceCatalog.Families.FirstOrDefault(family => family.Model.ToString() == f.Model)?.Name }
                .Where(s => !string.IsNullOrWhiteSpace(s))))).ToArray();
        var hub = new List<SpecRow>();
        if (!string.IsNullOrWhiteSpace(snapshot.HubFirmware)) hub.Add(new("Firmware", snapshot.HubFirmware));
        hub.Add(new(Strings.Hwi_Devices, devices.Length.ToString(UiLanguage.Culture)));
        hub.Add(new(Strings.Hw_CatLcd, snapshot.LcdConnected ? Strings.Hw_Conectado : Strings.Hwi_StateDisconnected));
        var sections = new List<SpecSection> { new("Corsair iCUE LINK System Hub", hub) };
        if (devices.Length > 0) sections.Add(new(Strings.Hwi_Devices, devices));
        return new HardwareComponent(HardwareKind.IcueLink, "Corsair iCUE LINK System Hub", hub, sections);
    }

    private void ShowInventory(IReadOnlyList<HardwareComponent> inventory, string note)
    {
        _inventory = inventory;
        _inventoryNote = note;
        var components = inventory.ToList();
        if (_vm != null)
        {
            _linkState = LinkState(_vm.Snapshot);
            if (IcueLink(_vm, _vm.Snapshot) is { } link) components.Insert(Math.Min(components.Count, 9), link);
        }
        _cards = components.Select(c => new HardwareCard(c)).ToArray();
        InventoryFilter.Visibility = _cards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ScanButton.Content = Strings.Hw_DetectarNovamente;
        ApplyFilter();
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (_cards == null) return;
        string filter = InventoryFilter.Text.Trim();
        var results = filter.Length == 0 ? _cards : _cards.Where(card => card.Matches(filter)).ToArray();
        DetectedParts.ItemsSource = results;
        InventoryStatus.Text = (filter.Length == 0 ? string.Format(UiLanguage.Culture, Strings.Hw_ItensInventario, results.Count) :
            string.Format(UiLanguage.Culture, Strings.Hw_Itens, results.Count, _cards.Count)) + _inventoryNote;
    }

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HardwareCard card } && DataContext is MainViewModel vm)
            vm.OpenHardwareDetail(card.Component);
    }

    /// <summary>Teste visual (--hw-detail): espera o inventário e abre a ficha de um componente.</summary>
    public async Task OpenDetailAsync(HardwareKind kind)
    {
        _scan ??= ScanAsync();
        await _scan;
        if (_cards?.FirstOrDefault(c => c.Component.Kind == kind) is { } card && DataContext is MainViewModel vm)
            vm.OpenHardwareDetail(card.Component);
    }
}
