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
    [string]$Iscc = (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$version = ([xml](Get-Content (Join-Path $root 'src\LinkFlow.App\LinkFlow.App.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Versão não encontrada em LinkFlow.App.csproj' }

$out = Join-Path $root 'artifacts\release'
$app = Join-Path $out 'LinkFlow'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $app | Out-Null

Write-Host "LinkFlow ${version}: publicando (self-contained, win-x64)..." -ForegroundColor Cyan
foreach ($proj in 'src\LinkFlow.App\LinkFlow.App.csproj', 'src\LinkFlow.Service\LinkFlow.Service.csproj') {
    dotnet publish (Join-Path $root $proj) -c Release -r win-x64 --self-contained true -o $app -nologo -v q `
        -p:DebugType=none -p:GenerateDocumentationFile=false
    if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar $proj" }
}
foreach ($exe in 'LinkFlow.App.exe', 'LinkFlow.Service.exe') {
    if (-not (Test-Path (Join-Path $app $exe))) { throw "$exe não foi gerado" }
}

Write-Host 'Gerando o instalador...' -ForegroundColor Cyan
if (-not (Test-Path $Iscc)) { throw "Inno Setup não encontrado em $Iscc (winget install JRSoftware.InnoSetup)" }
& $Iscc /Q "/DAppVersion=$version" "/DSourceDir=$app" "/DOutputDir=$out" (Join-Path $root 'installer\LinkFlow.iss')
if ($LASTEXITCODE -ne 0) { throw 'Falha ao gerar o instalador' }

Write-Host 'Compactando a versão portátil...' -ForegroundColor Cyan
$zip = Join-Path $out "LinkFlow-$version-portable.zip"
Compress-Archive -Path $app -DestinationPath $zip -CompressionLevel Optimal

Get-ChildItem $out -File | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    "{0}  {1}" -f $hash, $_.Name
} | Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding ascii

Get-ChildItem $out -File | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
Write-Host "Pronto: $out" -ForegroundColor Green
