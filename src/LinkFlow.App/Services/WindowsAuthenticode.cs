using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using LinkFlow.App.Resources;

namespace LinkFlow.App.Services;

internal static class WindowsAuthenticode
{
    private static readonly Guid VerifyAuthenticode = new("00aac56b-cd44-11d0-8cc2-00c04fc295ee");
    private const uint NoUi = 2;
    private const uint VerifyFile = 1;
    private const uint VerifyState = 1;
    private const uint CloseState = 2;
    private const uint SimpleDisplayName = 4;

    public static string TrustedPublisher(string path, SafeFileHandle handle) => TrustedSigner(path, handle).Publisher;

    public static TrustedSignerIdentity TrustedSigner(string path, SafeFileHandle handle)
    {
        nint filePath = Marshal.StringToHGlobalUni(path);
        nint fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        var data = new WinTrustData
        {
            Size = (uint)Marshal.SizeOf<WinTrustData>(),
            UiChoice = NoUi,
            UnionChoice = VerifyFile,
            FileInfo = fileInfo,
            StateAction = VerifyState,
        };
        try
        {
            Marshal.StructureToPtr(new WinTrustFileInfo
            {
                Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = filePath,
                FileHandle = handle.DangerousGetHandle(),
            }, fileInfo, false);
            int status = WinVerifyTrust(-1, in VerifyAuthenticode, ref data);
            if (status != 0)
                throw new InvalidOperationException(string.Format(Strings.Drv_WindowsRejeitouAssinaturaInstalador, status));

            // Lê o certificado da mesma assinatura que a política Authenticode acabou de verificar.
            nint provider = WTHelperProvDataFromStateData(data.StateData);
            nint signer = provider == 0 ? 0 : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            nint certificate = signer == 0 ? 0 : WTHelperGetProvCertFromChain(signer, 0);
            if (certificate == 0) throw new InvalidOperationException(Strings.Drv_WindowsNaoIdentificouEditor);

            nint context = Marshal.PtrToStructure<ProviderCertificate>(certificate).Context;
            uint size = CertGetNameString(context, SimpleDisplayName, 0, 0, null, 0);
            if (size < 2) throw new InvalidOperationException(Strings.Drv_CertificadoInstaladorNaoInformou);
            var name = new StringBuilder((int)size);
            if (CertGetNameString(context, SimpleDisplayName, 0, 0, name, size) <= 1)
                throw new InvalidOperationException(Strings.Drv_WindowsNaoConseguiuLer);
            var encoded = Marshal.PtrToStructure<CertificateContext>(context);
            if (encoded.EncodedSize is 0 or > 1024 * 1024 || encoded.Encoded == 0)
                throw new InvalidOperationException(Strings.Drv_WindowsNaoIdentificouEditor);
            var bytes = new byte[checked((int)encoded.EncodedSize)];
            Marshal.Copy(encoded.Encoded, bytes, 0, bytes.Length);
            return new(name.ToString(), Convert.ToHexString(SHA256.HashData(bytes)));
        }
        finally
        {
            if (data.StateData != 0)
            {
                data.StateAction = CloseState;
                WinVerifyTrust(-1, in VerifyAuthenticode, ref data);
            }
            Marshal.FreeHGlobal(fileInfo);
            Marshal.FreeHGlobal(filePath);
            GC.KeepAlive(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public nint FilePath;
        public nint FileHandle;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint FileInfo;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    // Campos iniciais do CRYPT_PROVIDER_CERT definidos no Windows SDK.
    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificate
    {
        public uint Size;
        public nint Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertificateContext
    {
        public uint Encoding;
        public nint Encoded;
        public uint EncodedSize;
        public nint Information;
        public nint Store;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(nint window, in Guid action, ref WinTrustData data);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperProvDataFromStateData(nint state);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvSignerFromChain(nint provider, uint signerIndex, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvCertFromChain(nint signer, uint index);

    [DllImport("crypt32.dll", EntryPoint = "CertGetNameStringW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CertGetNameString(nint context, uint type, uint flags, nint typeParameter, StringBuilder? name, uint size);
}
