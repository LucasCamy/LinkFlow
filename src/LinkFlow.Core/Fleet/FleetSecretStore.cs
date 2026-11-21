using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace LinkFlow.Core;

/// <summary>DPAPI do Windows. Tokens de pares só podem ser abertos pelo usuário que os salvou.</summary>
public static class FleetSecretStore
{
    public static string ProtectToken(string token) => Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(token), false));
    public static string UnprotectToken(string protectedToken) => Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(protectedToken)));
    internal static string ProtectMachineSecret(string value) => Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(value), true));
    internal static string UnprotectMachineSecret(string value) => Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(value)));

    private static byte[] Protect(byte[] input, bool machine)
    {
        var blob = Allocate(input);
        try
        {
            if (!CryptProtectData(ref blob, "LinkFlow fleet", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, machine ? 5u : 1u, out var output))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return CopyAndFree(output);
        }
        finally { Clear(blob); System.Security.Cryptography.CryptographicOperations.ZeroMemory(input); }
    }
    private static byte[] Unprotect(byte[] input)
    {
        var blob = Allocate(input);
        IntPtr description = IntPtr.Zero;
        try
        {
            if (!CryptUnprotectData(ref blob, out description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var output))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return CopyAndFree(output);
        }
        finally { Clear(blob); if (description != IntPtr.Zero) LocalFree(description); }
    }
    private static DataBlob Allocate(byte[] input)
    {
        var blob = new DataBlob { Length = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        Marshal.Copy(input, 0, blob.Data, input.Length);
        return blob;
    }
    private static void Clear(DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero) return;
        Marshal.Copy(new byte[blob.Length], 0, blob.Data, blob.Length);
        Marshal.FreeHGlobal(blob.Data);
    }
    private static byte[] CopyAndFree(DataBlob blob)
    {
        var data = new byte[blob.Length];
        Marshal.Copy(blob.Data, data, 0, data.Length);
        Marshal.Copy(new byte[blob.Length], 0, blob.Data, blob.Length);
        LocalFree(blob.Data);
        return data;
    }
    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, out IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
