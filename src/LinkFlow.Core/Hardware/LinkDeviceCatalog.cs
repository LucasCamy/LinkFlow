namespace LinkFlow.Core;

public enum LinkDeviceRole { Module, Fan, Pump }

public sealed record LinkDeviceFamily(LinkDeviceModel Model, string Name, LinkDeviceRole Role = LinkDeviceRole.Module);

public static class LinkDeviceCatalog
{
    private static readonly Dictionary<LinkDeviceModel, LinkDeviceFamily> ByModel = new LinkDeviceFamily[]
    {
        new(LinkDeviceModel.FanQxSeries, "QX", LinkDeviceRole.Fan),
        new(LinkDeviceModel.FanLxSeries, "LX", LinkDeviceRole.Fan),
        new(LinkDeviceModel.FanRxSeries, "RX", LinkDeviceRole.Fan),
        new(LinkDeviceModel.FanRxRgbSeries, "RX RGB", LinkDeviceRole.Fan),
        new(LinkDeviceModel.FanRxMaxSeries, "RX MAX", LinkDeviceRole.Fan),
        new(LinkDeviceModel.FanRxMaxRgbSeries, "RX MAX RGB", LinkDeviceRole.Fan),
        new(LinkDeviceModel.CapSwapModuleVrmFan, "CapSwap VRM", LinkDeviceRole.Fan),
        new(LinkDeviceModel.LiquidCoolerHSeries, "H-Series", LinkDeviceRole.Pump),
        new(LinkDeviceModel.LiquidCoolerTitanSeries, "TITAN", LinkDeviceRole.Pump),
        new(LinkDeviceModel.PumpXd5Series, "XD5", LinkDeviceRole.Pump),
        new(LinkDeviceModel.PumpXd6Series, "XD6", LinkDeviceRole.Pump),
        new(LinkDeviceModel.WaterBlockXc7Series, "XC7"),
        new(LinkDeviceModel.WaterBlockXg3Series, "XG3"),
        new(LinkDeviceModel.PsuHxiShiftSeries, "HXi SHIFT"),
        new(LinkDeviceModel.CommanderDuoSeries, "Commander Duo"),
        // Telas LCD na corrente do hub (tipos 6 e 14 no OpenLinkHub): ocupam um canal, sem rotação
        new(LinkDeviceModel.PumpLcd, "Pump LCD"),
        new(LinkDeviceModel.Xd5EliteLcd, "XD5 Elite LCD")
    }.ToDictionary(device => device.Model);

    private static readonly string[] HSeriesVariants = ["H100i", "H115i", "H150i", "H170i", "H100i", "H150i"];

    public static IEnumerable<LinkDeviceFamily> Families => ByModel.Values;

    internal static LinkDeviceFamily? Find(byte model) => ByModel.GetValueOrDefault((LinkDeviceModel)model);

    /// <summary>Função do dispositivo pelo nome do modelo (como em <see cref="FanState.Model"/>); null se desconhecido.</summary>
    public static LinkDeviceRole? RoleOf(string? model) =>
        Enum.TryParse<LinkDeviceModel>(model, out var m) && ByModel.TryGetValue(m, out var family) ? family.Role : null;

    /// <summary>Ventoinha (tem tacômetro e entra na calibração e no alerta de parada). Bomba não conta.</summary>
    public static bool IsFan(string? model) => RoleOf(model) == LinkDeviceRole.Fan;

    /// <summary>Informa rotação: ventoinhas e bombas. Módulos (LCD, blocos de água, fonte) e modelos desconhecidos não.</summary>
    public static bool HasTachometer(string? model) => RoleOf(model) is LinkDeviceRole.Fan or LinkDeviceRole.Pump;

    internal static string DisplayName(LinkDeviceFamily family, byte variant, int channel)
    {
        string name = family.Model == LinkDeviceModel.LiquidCoolerHSeries && variant < HSeriesVariants.Length
            ? HSeriesVariants[variant]
            : family.Name;
        return family.Role switch
        {
            LinkDeviceRole.Fan => $"{name} Fan #{channel}",
            LinkDeviceRole.Pump => $"{name} Pump",
            _ => $"{name} #{channel}"
        };
    }
}
