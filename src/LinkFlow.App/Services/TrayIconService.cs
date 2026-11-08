using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LinkFlow.Core;

namespace LinkFlow.App.Services;

/// <summary>
/// Ícone na área de notificação (bandeja). Mostra a temperatura da CPU no próprio ícone,
/// resumo no tooltip, menu com perfis e notificações de alerta.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _header;
    private readonly ToolStripMenuItem _profilesMenu;
    private readonly ToolStripMenuItem _miniItem;
    private readonly ToolStripMenuItem _lcdItem;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly Icon _baseIcon;
    private IntPtr _currentHIcon;
    private string _lastIconKey = string.Empty;

    public event Action? OpenRequested;
    public event Action? ExitRequested;
    public event Action? MiniWidgetToggled;
    public event Action<string>? PresetRequested;
    public event Action<string>? ProfileRequested;
    public event Action<bool>? LcdStreamToggled;
    public event Action<bool>? AutoStartToggled;
    public event Action? MenuOpening;

    public TrayIconService()
    {
        var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/linkflow.ico"))!.Stream;
        _baseIcon = new Icon(stream, SystemInformation.SmallIconSize);

        _menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Font = new Font("Segoe UI", 9.5f)
        };

        _header = new ToolStripMenuItem("LinkFlow") { Enabled = false };
        var open = new ToolStripMenuItem("Abrir LinkFlow", null, (_, _) => OpenRequested?.Invoke()) { Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
        _profilesMenu = new ToolStripMenuItem("Perfis de ventoinha");
        _miniItem = new ToolStripMenuItem("Mini widget", null, (_, _) => MiniWidgetToggled?.Invoke());
        _lcdItem = new ToolStripMenuItem("Transmitir métricas no LCD", null, (_, _) => LcdStreamToggled?.Invoke(!_lcdItem!.Checked));
        _autostartItem = new ToolStripMenuItem("Iniciar com o Windows", null, (_, _) => AutoStartToggled?.Invoke(!_autostartItem!.Checked));
        var exit = new ToolStripMenuItem("Sair do LinkFlow", null, (_, _) => ExitRequested?.Invoke());

        _menu.Items.AddRange(new ToolStripItem[]
        {
            _header, new ToolStripSeparator(),
            open, _profilesMenu, _miniItem, _lcdItem, new ToolStripSeparator(),
            _autostartItem, new ToolStripSeparator(),
            exit
        });
        _menu.Opening += (_, _) => MenuOpening?.Invoke();

        _icon = new NotifyIcon
        {
            Icon = _baseIcon,
            Text = "LinkFlow",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OpenRequested?.Invoke();
        };
    }

    public void UpdateMenuState(string activePreset, IReadOnlyList<FanProfile> profiles, bool miniVisible, bool lcdStream, bool autoStart)
    {
        _profilesMenu.DropDownItems.Clear();
        foreach (var p in FanPresets.All)
        {
            var key = p.Key;
            _profilesMenu.DropDownItems.Add(new ToolStripMenuItem($"{p.Label}  ({p.Description})", null, (_, _) => PresetRequested?.Invoke(key))
            {
                Checked = activePreset == key
            });
        }
        if (profiles.Count > 0)
        {
            _profilesMenu.DropDownItems.Add(new ToolStripSeparator());
            foreach (var pr in profiles)
            {
                var name = pr.Name;
                _profilesMenu.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) => ProfileRequested?.Invoke(name))
                {
                    Checked = activePreset == name
                });
            }
        }

        _miniItem.Checked = miniVisible;
        _lcdItem.Checked = lcdStream;
        _autostartItem.Checked = autoStart;
    }

    public void UpdateStatus(EngineSnapshot s, bool showTemperature)
    {
        float? cpu = s.CpuSensorAvailable ? s.CpuTemp : null;
        string cpuTxt = cpu is { } c ? $"{c:0}°C" : "—";
        string gpuTxt = s.GpuTemp is { } g ? $"{g:0}°C" : "—";
        string fans = s.AverageFanRpm is { } r ? $"{r:N0} RPM" : "—";
        string state = s.FailSafeActive ? "MODO SEGURANÇA" : s.HubConnected ? $"{s.EffectiveGlobalPercent}%" : "hub desconectado";

        // Tooltip do NotifyIcon é limitado a 127 caracteres
        string tip = $"LinkFlow • {state}\nCPU {cpuTxt} • GPU {gpuTxt}\nFans {fans}";
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;
        _header.Text = $"CPU {cpuTxt}  •  GPU {gpuTxt}  •  {fans}";

        float? iconTemp = cpu ?? s.GpuTemp;
        bool alert = s.FailSafeActive || s.Alerts.Any(a => a.Severity == AlertSeverity.Critical);
        string key = showTemperature && iconTemp != null ? $"{iconTemp:0}|{alert}" : $"base|{alert}";
        if (key == _lastIconKey) return;
        _lastIconKey = key;

        if (showTemperature && iconTemp is { } t) SetIcon(RenderTemperatureIcon((int)Math.Round(t), alert));
        else if (alert) SetIcon(RenderTemperatureIcon(null, true));
        else SetIcon(null);
    }

    public void ShowNotification(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.BalloonTipIcon = icon;
        _icon.ShowBalloonTip(5000);
    }

    private void SetIcon(IntPtr? hIcon)
    {
        var old = _currentHIcon;
        if (hIcon is { } h)
        {
            _icon.Icon = Icon.FromHandle(h);
            _currentHIcon = h;
        }
        else
        {
            _icon.Icon = _baseIcon;
            _currentHIcon = IntPtr.Zero;
        }
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    private static IntPtr RenderTemperatureIcon(int? temp, bool alert)
    {
        int size = SystemInformation.SmallIconSize.Width;
        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        var bg = alert ? Color.FromArgb(220, 38, 38) : Color.FromArgb(15, 23, 42);
        using (var b = new SolidBrush(bg)) g.FillEllipse(b, 0, 0, size - 1, size - 1);

        Color fg = temp switch
        {
            null => Color.White,
            >= 85 => Color.FromArgb(251, 113, 133),
            >= 70 => Color.FromArgb(251, 191, 36),
            _ => Color.FromArgb(0, 240, 255)
        };
        if (alert) fg = Color.White;

        string text = temp is { } t ? Math.Clamp(t, 0, 99).ToString() : "!";
        float fontSize = size * (text.Length > 1 ? 0.52f : 0.62f);
        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(fg);
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, brush, new RectangleF(0, 0.5f, size, size), fmt);
        return bmp.GetHicon();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        if (_currentHIcon != IntPtr.Zero) DestroyIcon(_currentHIcon);
        _baseIcon.Dispose();
    }

    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.FromArgb(241, 245, 249) : Color.FromArgb(148, 163, 184);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Color.FromArgb(148, 163, 184);
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            using var pen = new Pen(Color.FromArgb(0, 240, 255), 2);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(pen, new[]
            {
                new PointF(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.55f),
                new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.75f),
                new PointF(r.Left + r.Width * 0.8f, r.Top + r.Height * 0.3f)
            });
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        private static readonly Color Bg = Color.FromArgb(30, 41, 59);
        private static readonly Color Hover = Color.FromArgb(51, 65, 85);
        private static readonly Color Border = Color.FromArgb(71, 85, 105);
        public override Color ToolStripDropDownBackground => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Bg;
        public override Color CheckBackground => Bg;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}
