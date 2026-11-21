# Signing helpers contain no private keys. Official builds use a certificate already in CurrentUser\My.
function Get-LinkFlowCertificateSha256 {
    param([byte[]]$RawData)
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($RawData))).Replace('-', '') }
    finally { $algorithm.Dispose() }
}

function Get-LinkFlowSigningCertificate {
    param(
        [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{40}$')][string]$Thumbprint,
        [Parameter(Mandatory)][string]$ExpectedPublisher,
        [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedCertificateSha256
    )
    if ([string]::IsNullOrWhiteSpace($ExpectedPublisher) -or $ExpectedPublisher -match '[;<>"\r\n]') {
        throw 'Defina um publicador esperado válido para a distribuição oficial.'
    }
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction Stop
    if (-not $certificate.HasPrivateKey -or $certificate.NotBefore.ToUniversalTime() -gt [DateTime]::UtcNow -or
        $certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow) {
        throw 'O certificado de assinatura não tem chave privada acessível ou está fora da validade.'
    }
    $codeSigningUsage = @($certificate.Extensions | Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } |
        ForEach-Object { $_.EnhancedKeyUsages } | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.3' })
    if ($codeSigningUsage.Count -eq 0) {
        throw 'O certificado não está autorizado para assinatura de código.'
    }
    $publisher = $certificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
    $pin = Get-LinkFlowCertificateSha256 -RawData $certificate.RawData
    if ($publisher -cne $ExpectedPublisher -or $pin -cne $ExpectedCertificateSha256.ToUpperInvariant()) {
        throw 'O certificado não corresponde ao publicador e à impressão SHA-256 esperados.'
    }
    return $certificate
}

function Find-LinkFlowSignTool {
    $found = Get-Command signtool.exe -CommandType Application -ErrorAction SilentlyContinue
    if ($found) { return $found.Source }
    $sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $sdk) {
        foreach ($versionDirectory in (Get-ChildItem -LiteralPath $sdk -Directory | Sort-Object Name -Descending)) {
            $candidate = Join-Path $versionDirectory.FullName 'x64\signtool.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        }
    }
    throw 'SignTool do Windows SDK não encontrado. A distribuição oficial exige assinatura e timestamp.'
}

function Assert-LinkFlowSignature {
    param([string]$File, [string]$ExpectedPublisher, [string]$ExpectedCertificateSha256)
    $signature = Get-AuthenticodeSignature -LiteralPath $File
    if ($signature.Status -ne 'Valid' -or -not $signature.SignerCertificate -or -not $signature.TimeStamperCertificate) {
        throw "Assinatura ou timestamp não validado pelo Windows: $File"
    }
    $publisher = $signature.SignerCertificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
    $pin = Get-LinkFlowCertificateSha256 -RawData $signature.SignerCertificate.RawData
    if ($publisher -cne $ExpectedPublisher -or $pin -cne $ExpectedCertificateSha256.ToUpperInvariant()) {
        throw "Publicador ou certificado inesperado: $File"
    }
}

function Invoke-LinkFlowSign {
    param([string]$File, [string]$SignTool, [string]$Thumbprint, [string]$TimestampServer,
        [string]$ExpectedPublisher, [string]$ExpectedCertificateSha256)
    & $SignTool sign /sha1 $Thumbprint /s My /fd SHA256 /tr $TimestampServer /td SHA256 $File
    if ($LASTEXITCODE -ne 0) { throw "Falha na assinatura de $File" }
    Assert-LinkFlowSignature -File $File -ExpectedPublisher $ExpectedPublisher -ExpectedCertificateSha256 $ExpectedCertificateSha256
}
