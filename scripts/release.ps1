<#
.SYNOPSIS
    Gera os arquivos de um release do LinkFlow em artifacts\release:
      LinkFlow-Setup-<versão>.exe   instalador (serviço, atalhos, iniciar com o Windows)
      LinkFlow-<versão>-portable.zip pasta pronta, sem instalar
    As duas versões são self-contained: não precisam do .NET instalado.
.EXAMPLE
    .\scripts\release.ps1
#>
param(
    [string]$Iscc,
    [ValidateSet('Development', 'Official')][string]$Distribution = 'Development',
    [string]$CertificateThumbprint = $env:LINKFLOW_SIGNING_THUMBPRINT,
    [string]$ExpectedPublisher = $env:LINKFLOW_SIGNING_PUBLISHER,
    [string]$ExpectedCertificateSha256 = $env:LINKFLOW_SIGNING_CERT_SHA256,
    [string]$TimestampServer = 'https://timestamp.digicert.com',
    [string]$SourceCommit = $env:GITHUB_SHA
)
$ErrorActionPreference = 'Stop'
if (-not [string]::IsNullOrWhiteSpace($SourceCommit) -and $SourceCommit -notmatch '^[0-9A-Fa-f]{40}$') {
    throw 'SourceCommit deve ser um SHA completo de commit (40 dígitos hex), ou ausente para empacotamento local sem proveniência de commit.'
}
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath
. (Join-Path $PSScriptRoot 'ReleaseSigning.ps1')
$signTool = $null
if ($Distribution -eq 'Official') {
    if ([string]::IsNullOrWhiteSpace($CertificateThumbprint) -or [string]::IsNullOrWhiteSpace($ExpectedPublisher) -or
        [string]::IsNullOrWhiteSpace($ExpectedCertificateSha256)) {
        throw 'Distribuição oficial bloqueada: configure certificado de assinatura, publicador esperado e impressão SHA-256. Use -Distribution Development para artefatos locais sem assinatura.'
    }
    $timestampUri = $null
    if (-not [Uri]::TryCreate($TimestampServer, [UriKind]::Absolute, [ref]$timestampUri) -or $timestampUri.Scheme -ne 'https' -or
        $timestampUri.UserInfo.Length -ne 0 -or $timestampUri.Query.Length -ne 0 -or $timestampUri.Fragment.Length -ne 0 -or $timestampUri.AbsoluteUri.Contains('$')) {
        throw 'O servidor de timestamp deve usar HTTPS sem credenciais no endereço.'
    }
    $TimestampServer = $timestampUri.AbsoluteUri
    $null = Get-LinkFlowSigningCertificate -Thumbprint $CertificateThumbprint -ExpectedPublisher $ExpectedPublisher -ExpectedCertificateSha256 $ExpectedCertificateSha256
    $signTool = Find-LinkFlowSignTool
}
$appProject = Join-Path $root 'src\LinkFlow.App\LinkFlow.App.csproj'
$serviceProject = Join-Path $root 'src\LinkFlow.Service\LinkFlow.Service.csproj'
$cliProject = Join-Path $root 'src\LinkFlow.Cli\LinkFlow.Cli.csproj'
$coreProject = Join-Path $root 'src\LinkFlow.Core\LinkFlow.Core.csproj'
function Get-LinkFlowProjectVersion {
    param([string]$Project)
    $nodes = ([xml](Get-Content -LiteralPath $Project -Raw)).SelectNodes('/Project/PropertyGroup/Version')
    if ($nodes.Count -ne 1) { throw "Defina uma única versão explícita no projeto: $Project" }
    $value = $nodes[0].InnerText.Trim()
    if ($value -notmatch '^\d+\.\d+\.\d+$') { throw "Versão inválida (esperado X.Y.Z): $Project" }
    return $value
}
[string]$version = Get-LinkFlowProjectVersion $appProject
foreach ($versionedProject in $serviceProject, $cliProject, $coreProject) {
    if ((Get-LinkFlowProjectVersion $versionedProject) -cne $version) {
        throw 'Defina a mesma versão (X.Y.Z) nos projetos da interface, serviço, CLI e núcleo.'
    }
}

if (-not $Iscc) {
    $Iscc = @(
        (Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue).Source
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
}
if (-not $Iscc -or -not (Test-Path -LiteralPath $Iscc -PathType Leaf)) {
    throw 'Inno Setup 6 não encontrado. Instale-o ou indique o caminho de ISCC.exe com -Iscc.'
}

$out = Join-Path $root $(if ($Distribution -eq 'Official') { 'artifacts\release' } else { 'artifacts\development' })
New-Item -ItemType Directory -Path $out -Force | Out-Null
$out = (Resolve-Path -LiteralPath $out).ProviderPath
$staging = Join-Path $out ('build-' + [Guid]::NewGuid().ToString('N'))
$app = Join-Path $staging 'LinkFlow'
$setupName = if ($Distribution -eq 'Official') { "LinkFlow-Setup-$version.exe" } else { "LinkFlow-Dev-Setup-$version.exe" }
$zipName = if ($Distribution -eq 'Official') { "LinkFlow-$version-portable.zip" } else { "LinkFlow-$version-dev-portable.zip" }
$hashName = 'SHA256SUMS.txt'
$manifestName = 'release-manifest.json'

New-Item -ItemType Directory -Path $app | Out-Null
try {
    Write-Host "LinkFlow ${version}: publicando (self-contained, win-x64)..." -ForegroundColor Cyan
    foreach ($project in $appProject, $serviceProject, $cliProject) {
        $distributionProperties = @("-p:LinkFlowDistribution=$Distribution")
        if ($Distribution -eq 'Official') {
            # MSBuild treats commas/semicolons as property separators even inside one process argument.
            $escapedPublisher = $ExpectedPublisher.Replace('%', '%25').Replace(',', '%2C').Replace(';', '%3B').Replace('$', '%24').Replace('@', '%40').Replace("'", '%27').Replace('(', '%28').Replace(')', '%29')
            $distributionProperties += "-p:LinkFlowExpectedPublisher=$escapedPublisher", "-p:LinkFlowPublisherCertificateSha256=$ExpectedCertificateSha256"
        }
        dotnet publish $project -c Release -r win-x64 --self-contained true -o $app -nologo -v q `
            -p:DebugType=none -p:GenerateDocumentationFile=false @distributionProperties
        if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar $project" }
    }

    $innoOptions = @()
    if ($Distribution -eq 'Official') {
        foreach ($binary in (Get-ChildItem -LiteralPath $app -File -Recurse | Where-Object { $_.Name -like 'LinkFlow*.exe' -or $_.Name -like 'LinkFlow*.dll' })) {
            Invoke-LinkFlowSign -File $binary.FullName -SignTool $signTool -Thumbprint $CertificateThumbprint -TimestampServer $TimestampServer `
                -ExpectedPublisher $ExpectedPublisher -ExpectedCertificateSha256 $ExpectedCertificateSha256
        }
        # Inno signs both the setup and uninstaller; only public certificate-store selectors enter the command.
        $signCommand = '"{0}" sign /sha1 {1} /s My /fd SHA256 /tr "{2}" /td SHA256 $f' -f $signTool, $CertificateThumbprint, $TimestampServer
        $innoOptions += '/DOfficialSigning=1', "/Slinkflow=$signCommand"
    }
    else { $innoOptions += '/DDevelopmentBuild=1' }
    foreach ($file in 'LinkFlow.App.exe', 'LinkFlow.Service.exe', 'LinkFlow.Cli.exe', 'THIRD-PARTY-NOTICES.txt') {
        if (-not (Test-Path -LiteralPath (Join-Path $app $file) -PathType Leaf)) { throw "$file não foi gerado" }
    }

    Write-Host 'Gerando o instalador...' -ForegroundColor Cyan
    & $Iscc /Q "/DAppVersion=$version" "/DSourceDir=$app" "/DOutputDir=$staging" @innoOptions (Join-Path $root 'installer\LinkFlow.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao gerar o instalador' }
    if ($Distribution -eq 'Official') {
        Assert-LinkFlowSignature -File (Join-Path $staging $setupName) -ExpectedPublisher $ExpectedPublisher -ExpectedCertificateSha256 $ExpectedCertificateSha256
    }

    Write-Host 'Compactando a versão portátil...' -ForegroundColor Cyan
    Compress-Archive -LiteralPath $app -DestinationPath (Join-Path $staging $zipName) -CompressionLevel Optimal

    # Older clients discover only the original names. These aliases keep the
    # exact Development bytes and never change the channel or signing claims.
    $compatibilityAliases = @()
    if ($Distribution -eq 'Development') {
        foreach ($pair in @(
            @{ Name = "LinkFlow-Setup-$version.exe"; Source = $setupName },
            @{ Name = "LinkFlow-$version-portable.zip"; Source = $zipName }
        )) {
            $sourceFile = Join-Path $staging $pair.Source
            $aliasFile = Join-Path $staging $pair.Name
            [IO.File]::Copy($sourceFile, $aliasFile, $false)
            $sourceHash = (Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash
            $aliasHash = (Get-FileHash -LiteralPath $aliasFile -Algorithm SHA256).Hash
            if ($sourceHash -cne $aliasHash -or (Get-Item -LiteralPath $sourceFile).Length -ne (Get-Item -LiteralPath $aliasFile).Length) {
                throw 'O alias de compatibilidade não corresponde ao artefato Development.'
            }
            $compatibilityAliases += [ordered]@{ name = $pair.Name; source = $pair.Source; sha256 = $aliasHash; contentIdentical = $true }
        }
    }

    $manifest = [ordered]@{
        version = $version; channel = $Distribution; publisherVerified = ($Distribution -eq 'Official')
        publisher = $(if ($Distribution -eq 'Official') { $ExpectedPublisher } else { $null })
        certificateSha256 = $(if ($Distribution -eq 'Official') { $ExpectedCertificateSha256.ToUpperInvariant() } else { $null })
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        sourceCommit = $(if ([string]::IsNullOrWhiteSpace($SourceCommit)) { $null } else { $SourceCommit.ToLowerInvariant() })
        compatibilityAliases = @($compatibilityAliases)
    }
    [System.IO.File]::WriteAllText((Join-Path $staging $manifestName), ($manifest | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
    $artifactNames = @($setupName, $zipName, $manifestName) + @($compatibilityAliases | ForEach-Object { $_.name })
    $checksums = foreach ($name in $artifactNames) {
        $hash = (Get-FileHash -LiteralPath (Join-Path $staging $name) -Algorithm SHA256).Hash
        "{0}  {1}" -f $hash, $name
    }
    [System.IO.File]::WriteAllText((Join-Path $staging $hashName), (($checksums -join "`n") + "`n"))

    $files = foreach ($name in (@($setupName, $zipName, $hashName, $manifestName) + @($compatibilityAliases | ForEach-Object { $_.name }))) {
        Move-Item -LiteralPath (Join-Path $staging $name) -Destination (Join-Path $out $name) -Force -PassThru
    }
    $files | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
}
finally {
    $resolvedStaging = (Resolve-Path -LiteralPath $staging -ErrorAction SilentlyContinue).ProviderPath
    if ($resolvedStaging -eq $staging -and (Split-Path -Parent $resolvedStaging) -eq $out) {
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}
Write-Host "Pronto: $out" -ForegroundColor Green
