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
    [string]$Iscc
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath
$appProject = Join-Path $root 'src\LinkFlow.App\LinkFlow.App.csproj'
$serviceProject = Join-Path $root 'src\LinkFlow.Service\LinkFlow.Service.csproj'
$version = ([xml](Get-Content -LiteralPath $appProject -Raw)).Project.PropertyGroup.Version
$serviceVersion = ([xml](Get-Content -LiteralPath $serviceProject -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $version -ne $serviceVersion) {
    throw 'Defina a mesma versão (X.Y.Z) nos projetos da interface e do serviço.'
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

$out = Join-Path $root 'artifacts\release'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$out = (Resolve-Path -LiteralPath $out).ProviderPath
$staging = Join-Path $out ('build-' + [Guid]::NewGuid().ToString('N'))
$app = Join-Path $staging 'LinkFlow'
$setupName = "LinkFlow-Setup-$version.exe"
$zipName = "LinkFlow-$version-portable.zip"
$hashName = 'SHA256SUMS.txt'

New-Item -ItemType Directory -Path $app | Out-Null
try {
    Write-Host "LinkFlow ${version}: publicando (self-contained, win-x64)..." -ForegroundColor Cyan
    foreach ($project in $appProject, $serviceProject) {
        dotnet publish $project -c Release -r win-x64 --self-contained true -o $app -nologo -v q `
            -p:DebugType=none -p:GenerateDocumentationFile=false
        if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar $project" }
    }
    foreach ($file in 'LinkFlow.App.exe', 'LinkFlow.Service.exe', 'THIRD-PARTY-NOTICES.txt') {
        if (-not (Test-Path -LiteralPath (Join-Path $app $file) -PathType Leaf)) { throw "$file não foi gerado" }
    }

    Write-Host 'Gerando o instalador...' -ForegroundColor Cyan
    & $Iscc /Q "/DAppVersion=$version" "/DSourceDir=$app" "/DOutputDir=$staging" (Join-Path $root 'installer\LinkFlow.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao gerar o instalador' }

    Write-Host 'Compactando a versão portátil...' -ForegroundColor Cyan
    Compress-Archive -LiteralPath $app -DestinationPath (Join-Path $staging $zipName) -CompressionLevel Optimal

    $checksums = foreach ($name in $setupName, $zipName) {
        $hash = (Get-FileHash -LiteralPath (Join-Path $staging $name) -Algorithm SHA256).Hash
        "{0}  {1}" -f $hash, $name
    }
    [System.IO.File]::WriteAllText((Join-Path $staging $hashName), (($checksums -join "`n") + "`n"))

    $files = foreach ($name in $setupName, $zipName, $hashName) {
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
