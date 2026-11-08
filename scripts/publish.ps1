<#
.SYNOPSIS
    Publica o LinkFlow (interface + serviço) numa única pasta.
.EXAMPLE
    .\scripts\publish.ps1                       # -> bin\LinkFlow_v2
    .\scripts\publish.ps1 -Output D:\Apps\LinkFlow
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\bin\LinkFlow_v2'),
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$Output = [System.IO.Path]::GetFullPath($Output)

Write-Host "Publicando LinkFlow em $Output ..." -ForegroundColor Cyan
foreach ($proj in 'src\LinkFlow.App\LinkFlow.App.csproj', 'src\LinkFlow.Service\LinkFlow.Service.csproj') {
    dotnet publish (Join-Path $root $proj) -c $Configuration -r win-x64 --self-contained false -o $Output -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar $proj" }
}

foreach ($exe in 'LinkFlow.App.exe', 'LinkFlow.Service.exe') {
    if (-not (Test-Path (Join-Path $Output $exe))) { throw "$exe não foi gerado" }
}
Write-Host "OK: LinkFlow.App.exe e LinkFlow.Service.exe em $Output" -ForegroundColor Green
