using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using LinkFlow.Core;

namespace LinkFlow.App.Services;

/// <summary>
/// Atalhos globais do Windows (funcionam com o LinkFlow na bandeja ou durante jogos):
/// Ctrl+Alt+1..5 aplicam perfis, Ctrl+Alt+L abre/esconde a janela, Ctrl+Alt+M alterna o mini widget.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000;
    private const int IdPresetBase = 0x4C46; // "LF"
    private const int IdShow = IdPresetBase + 10;
    private const int IdMini = IdPresetBase + 11;

    private readonly IntPtr _hwnd;
    private readonly HwndSource? _source;
    private readonly List<int> _registered = new();

    public event Action<int>? PresetHotkey;
    public event Action? ShowHotkey;
    public event Action? MiniHotkey;

    public HotkeyService(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
    }

    public void SetEnabled(bool enabled)
    {
        UnregisterAll();
        if (!enabled) return;

        for (int i = 1; i <= FanPresets.All.Count; i++)
        {
            Register(IdPresetBase + i, (uint)KeyInterop.VirtualKeyFromKey(Key.D0 + i), $"Ctrl+Alt+{i}");
        }
        Register(IdShow, (uint)KeyInterop.VirtualKeyFromKey(Key.L), "Ctrl+Alt+L");
        Register(IdMini, (uint)KeyInterop.VirtualKeyFromKey(Key.M), "Ctrl+Alt+M");
    }

    private void Register(int id, uint vk, string name)
    {
        if (RegisterHotKey(_hwnd, id, ModControl | ModAlt | ModNoRepeat, vk)) _registered.Add(id);
        else Log.Warn($"Atalho global {name} indisponível (já usado por outro programa).");
    }

    private void UnregisterAll()
    {
        foreach (var id in _registered) UnregisterHotKey(_hwnd, id);
        _registered.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;
        int id = wParam.ToInt32();
        if (id > IdPresetBase && id <= IdPresetBase + FanPresets.All.Count) PresetHotkey?.Invoke(id - IdPresetBase);
        else if (id == IdShow) ShowHotkey?.Invoke();
        else if (id == IdMini) MiniHotkey?.Invoke();
        else return IntPtr.Zero;
        handled = true;
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(WndProc);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
