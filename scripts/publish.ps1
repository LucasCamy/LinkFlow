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
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$staging = Join-Path $tempRoot ('LinkFlow-publish-' + [Guid]::NewGuid().ToString('N'))

Write-Host "Publicando LinkFlow em $Output ..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    # Os assemblies genérico e win-x64 de um pacote podem ter a mesma data no NuGet.
    # Publicar numa pasta vazia garante que o SDK copie o assembly certo para o Windows.
    foreach ($proj in 'src\LinkFlow.App\LinkFlow.App.csproj', 'src\LinkFlow.Service\LinkFlow.Service.csproj') {
        dotnet publish (Join-Path $root $proj) -c $Configuration -r win-x64 --self-contained false -o $staging -nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar $proj" }
    }

    foreach ($exe in 'LinkFlow.App.exe', 'LinkFlow.Service.exe') {
        if (-not (Test-Path -LiteralPath (Join-Path $staging $exe))) { throw "$exe não foi gerado" }
    }
    New-Item -ItemType Directory -Path $Output -Force | Out-Null
    Get-ChildItem -LiteralPath $staging | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $Output -Recurse -Force
    }
}
finally {
    $resolvedStaging = (Resolve-Path -LiteralPath $staging -ErrorAction SilentlyContinue).ProviderPath
    if ($resolvedStaging -eq $staging -and $resolvedStaging.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}
Write-Host "OK: LinkFlow.App.exe e LinkFlow.Service.exe em $Output" -ForegroundColor Green
