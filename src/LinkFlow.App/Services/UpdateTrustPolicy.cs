using System.IO;
using System.Reflection;
using Microsoft.Win32.SafeHandles;

namespace LinkFlow.App.Services;

internal enum DistributionChannel { Development, Official }

internal sealed record TrustedSignerIdentity(string Publisher, string CertificateSha256);
internal sealed record UpdateTrustResult(DistributionChannel Channel, bool PublisherVerified, string? Publisher = null);

internal interface IUpdateSignatureVerifier
{
    TrustedSignerIdentity Verify(string path, SafeFileHandle handle);
}

internal sealed class AuthenticodeUpdateSignatureVerifier : IUpdateSignatureVerifier
{
    public TrustedSignerIdentity Verify(string path, SafeFileHandle handle) => WindowsAuthenticode.TrustedSigner(path, handle);
}

/// <summary>The official channel is pinned in the signed assembly; a user-writable file cannot disable verification.</summary>
internal sealed record UpdateTrustPolicy(DistributionChannel Channel, string? ExpectedPublisher = null, string? CertificateSha256 = null)
{
    public static UpdateTrustPolicy Development { get; } = new(DistributionChannel.Development);

    public static UpdateTrustPolicy ForAssembly(Assembly assembly)
    {
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);
        if (metadata.GetValueOrDefault("LinkFlowDistribution") != "Official") return Development;
        return new(DistributionChannel.Official, metadata.GetValueOrDefault("LinkFlowExpectedPublisher"), metadata.GetValueOrDefault("LinkFlowPublisherCertificateSha256"));
    }

    public UpdateTrustResult Verify(string path, SafeFileHandle handle, IUpdateSignatureVerifier verifier)
    {
        if (Channel == DistributionChannel.Development) return new(Channel, false);
        if (string.IsNullOrWhiteSpace(ExpectedPublisher) || CertificateSha256 is not { Length: 64 } pin || !pin.All(Uri.IsHexDigit))
            throw new InvalidDataException("A distribuição oficial não possui uma política de assinatura válida. Reinstale uma versão oficial verificada.");
        TrustedSignerIdentity identity;
        try { identity = verifier.Verify(path, handle); }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            throw new InvalidDataException("O Windows não confirmou uma assinatura confiável para o instalador do LinkFlow.", ex);
        }
        if (!string.Equals(identity.Publisher, ExpectedPublisher, StringComparison.Ordinal) ||
            !string.Equals(identity.CertificateSha256, pin, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O instalador não foi assinado pelo publicador e certificado esperados do LinkFlow.");
        return new(Channel, true, identity.Publisher);
    }
}
