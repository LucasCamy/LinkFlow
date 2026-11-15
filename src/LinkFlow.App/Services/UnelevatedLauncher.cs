using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using LinkFlow.Core;

namespace LinkFlow.App.Services;

/// <summary>
/// Abre um programa como usuário comum mesmo quando este processo está elevado.
/// Um filho de um processo administrador herda a elevação; pedir ao shell da área de trabalho
/// (o Explorer, que roda sem elevação) para executar o programa evita isso e preserva os argumentos.
/// </summary>
public static class UnelevatedLauncher
{
    private const int SwcDesktop = 8;
    private const int SwfoNeedDispatch = 1;
    private const uint SvgioBackground = 0;
    private const int SwShowNormal = 1;

    private static readonly Guid ShellWindowsClsid = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid SidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid ShellBrowserIid = typeof(IShellBrowser).GUID;
    private static readonly Guid DispatchIid = new("00020400-0000-0000-C000-000000000046");

    /// <summary>
    /// Inicia <paramref name="exe"/> sem elevação. Num processo comum é um início normal; num processo elevado
    /// passa pelo shell e, se ele não estiver disponível, não inicia nada (nunca abre elevado por engano).
    /// </summary>
    /// <returns>True se o pedido foi aceito.</returns>
    public static bool Start(string exe, string arguments, string workingDirectory)
    {
        try
        {
            if (!Environment.IsPrivilegedProcess)
            {
                using var process = Process.Start(new ProcessStartInfo(exe, arguments)
                {
                    UseShellExecute = false,
                    WorkingDirectory = workingDirectory
                });
                return process != null;
            }
            return StartThroughDesktopShell(exe, arguments, workingDirectory);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or System.ComponentModel.Win32Exception
                                   or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidOperationException)
        {
            Log.Warn($"Não foi possível abrir {Path.GetFileName(exe)} sem administrador: {ex.Message}");
            return false;
        }
    }

    internal static bool StartThroughDesktopShell(string exe, string arguments, string workingDirectory)
    {
        var shellWindowsType = Type.GetTypeFromCLSID(ShellWindowsClsid, throwOnError: true)!;
        object? shellWindows = null, desktop = null, browser = null, view = null, background = null;
        try
        {
            shellWindows = Activator.CreateInstance(shellWindowsType)!;
            object location = 0, root = 0; // CSIDL_DESKTOP
            desktop = ((IShellWindows)shellWindows).FindWindowSW(ref location, ref root, SwcDesktop, out _, SwfoNeedDispatch);
            if (desktop is not IServiceProvider provider)
                throw new InvalidOperationException("A área de trabalho do Windows não está disponível.");

            Guid service = SidTopLevelBrowser, iid = ShellBrowserIid;
            browser = provider.QueryService(ref service, ref iid);
            view = ((IShellBrowser)browser).QueryActiveShellView();
            Guid dispatch = DispatchIid;
            background = ((IShellView)view).GetItemObject(SvgioBackground, ref dispatch);

            // IShellFolderViewDual.Application → IShellDispatch2, que executa no processo do Explorer.
            dynamic folderView = background;
            dynamic shell = folderView.Application;
            try { shell.ShellExecute(exe, arguments, workingDirectory, "open", SwShowNormal); }
            finally { Marshal.FinalReleaseComObject((object)shell); }
            return true;
        }
        finally
        {
            foreach (var item in new[] { background, view, browser, desktop, shellWindows })
                if (item != null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item);
        }
    }

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        int Count { get; }
        [return: MarshalAs(UnmanagedType.IDispatch)] object Item(object index);
        [return: MarshalAs(UnmanagedType.IUnknown)] object NewEnum();
        int Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass);
        int RegisterPending(int threadId, ref object loc, ref object locRoot, int swClass);
        void Revoke(int cookie);
        void OnNavigate(int cookie, ref object loc);
        void OnActivated(int cookie, [MarshalAs(UnmanagedType.VariantBool)] bool active);
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW(ref object loc, ref object locRoot, int swClass, out int hwnd, int options);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(ref Guid service, ref Guid riid);
    }

    // Só QueryActiveShellView é usado; os métodos anteriores existem para manter a ordem da vtable.
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out nint hwnd);
        [PreserveSig] int ContextSensitiveHelp(int enterMode);
        [PreserveSig] int InsertMenusSB(nint shared, nint menuWidths);
        [PreserveSig] int SetMenuSB(nint shared, nint oleMenu, nint activeObject);
        [PreserveSig] int RemoveMenusSB(nint shared);
        [PreserveSig] int SetStatusTextSB(nint text);
        [PreserveSig] int EnableModelessSB(int enable);
        [PreserveSig] int TranslateAcceleratorSB(nint msg, ushort id);
        [PreserveSig] int BrowseObject(nint pidl, uint flags);
        [PreserveSig] int GetViewStateStream(uint mode, out nint stream);
        [PreserveSig] int GetControlWindow(uint id, out nint hwnd);
        [PreserveSig] int SendControlMsg(uint id, uint msg, nint wParam, nint lParam, out nint result);
        [return: MarshalAs(UnmanagedType.IUnknown)] object QueryActiveShellView();
    }

    // Só GetItemObject é usado; os métodos anteriores existem para manter a ordem da vtable.
    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        [PreserveSig] int GetWindow(out nint hwnd);
        [PreserveSig] int ContextSensitiveHelp(int enterMode);
        [PreserveSig] int TranslateAccelerator(nint msg);
        [PreserveSig] int EnableModeless(int enable);
        [PreserveSig] int UIActivate(uint state);
        [PreserveSig] int Refresh();
        [PreserveSig] int CreateViewWindow(nint previous, nint settings, nint browser, nint rect, out nint hwnd);
        [PreserveSig] int DestroyViewWindow();
        [PreserveSig] int GetCurrentInfo(nint settings);
        [PreserveSig] int AddPropertySheetPages(uint reserved, nint callback, nint lParam);
        [PreserveSig] int SaveViewState();
        [PreserveSig] int SelectItem(nint pidl, uint flags);
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetItemObject(uint item, ref Guid riid);
    }
}
