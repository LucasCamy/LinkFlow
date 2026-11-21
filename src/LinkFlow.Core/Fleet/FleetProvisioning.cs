using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace LinkFlow.Core;

/// <summary>Entrega explícita de credenciais; não grava tokens em claro nem exporta a chave TLS.</summary>
public sealed record FleetPairing(string AgentId, string Name, int Port, string CertificateSha256, string ObserverToken, string? ControlToken);

public static class FleetProvisioning
{
    public static string DefaultDirectory => Path.Combine(AppPaths.SharedDir, "fleet");
    public static string DefaultConfigPath => Path.Combine(DefaultDirectory, "agent.json");

    public static FleetPairing Create(string directory, string name, string bindAddress = "127.0.0.1", int port = 47931,
        bool allowNetwork = false, bool allowControl = false)
    {
        if (!IPAddress.TryParse(bindAddress, out var address) || !IPAddress.IsLoopback(address) && !allowNetwork)
            throw new ArgumentException("Acesso pela rede exige um endereço explícito e --allow-network.");
        if (port is < 1 or > 65535 || name.Length is < 1 or > 100) throw new ArgumentException("Nome ou porta inválidos.");
        directory = Path.GetFullPath(directory);
        if (SharedDataSecurity.IsReparsePoint(directory)) throw new IOException("A pasta do agente foi redirecionada.");
        Directory.CreateDirectory(directory);
        ProtectDirectory(directory);
        string configPath = Path.Combine(directory, "agent.json"), certPath = Path.Combine(directory, "agent.pfx");
        if (File.Exists(configPath) || File.Exists(certPath)) throw new IOException("Já existe um pareamento nesta pasta. Nenhuma credencial foi substituída.");
        string observer = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string? control = allowControl ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) : null;
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var options = new FleetAgentOptions
        {
            Enabled = true, Name = name, BindAddress = bindAddress, Port = port, AllowNetwork = allowNetwork,
            AllowControl = allowControl, ProtectedCertificatePassword = FleetSecretStore.ProtectMachineSecret(password),
            AccessKeys = [new() { Name = "Observador", TokenSha256 = FleetClient.TokenHash(observer) }]
        };
        if (control != null) options.AccessKeys.Add(new() { Name = "Controle", TokenSha256 = FleetClient.TokenHash(control), Permissions = FleetPermission.Observe | FleetPermission.Control });
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest("CN=LinkFlow Fleet Agent", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback); san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2));
        byte[] pfx = certificate.Export(X509ContentType.Pfx, password);
        try
        {
            using (var file = new FileStream(certPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(pfx); file.Flush(true); }
            if (!new JsonFileStore<FleetAgentOptions>(configPath).Save(options)) throw new IOException("Não foi possível salvar a configuração do agente.");
        }
        finally { CryptographicOperations.ZeroMemory(pfx); }
        return new(options.AgentId, options.Name, port, FleetClient.CertificatePin(certificate), observer, control);
    }

    public static (FleetAgentOptions Options, X509Certificate2 Certificate)? Load(string path)
    {
        if (!File.Exists(path)) return null;
        path = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(path)!;
        if (SharedDataSecurity.IsReparsePoint(directory) || SharedDataSecurity.IsReparsePoint(path)) throw new IOException("A configuração do agente foi redirecionada.");
        ProtectDirectory(directory);
        var options = new JsonFileStore<FleetAgentOptions>(path).Load();
        if (!options.Enabled) return null;
        if (options.CertificateFile != Path.GetFileName(options.CertificateFile) || string.IsNullOrWhiteSpace(options.CertificateFile))
            throw new IOException("O certificado precisa estar na pasta privada do agente.");
        string certPath = Path.Combine(directory, options.CertificateFile);
        if (SharedDataSecurity.IsReparsePoint(certPath)) throw new IOException("O certificado do agente foi redirecionado.");
        string password = FleetSecretStore.UnprotectMachineSecret(options.ProtectedCertificatePassword);
        // Schannel no Windows precisa de uma chave importada: EphemeralKeySet não é aceito pelo SslStream servidor.
        // Sem PersistKeySet, a chave temporária importada é removida ao descartar o certificado.
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(certPath, password, X509KeyStorageFlags.DefaultKeySet);
        return (options, certificate);
    }

    public static void ProtectDirectory(string directory)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        using var me = WindowsIdentity.GetCurrent();
        if (!SharedDataSecurity.IsPrivileged && me.User != null)
            security.AddAccessRule(new FileSystemAccessRule(me.User, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }
}
