[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$packageRoot = Split-Path -Parent $PSScriptRoot
$packageRun = Join-Path $packageRoot ('work/packages/' + $Configuration + '/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
$packageCliTelemetryBefore = $env:DOTNET_CLI_TELEMETRY_OPTOUT
$packageAvaloniaTelemetryBefore = $env:AVALONIA_TELEMETRY_OPTOUT
Push-Location $packageRoot
try {
    if (!(Test-Path -LiteralPath (Join-Path $packageRoot 'data/aerolib.csv') -PathType Leaf)) {
        throw 'Sharing package requires data/aerolib.csv.'
    }
    if (!(Get-ChildItem -LiteralPath (Join-Path $packageRoot 'fakelibs') -Filter '*.sprx' -File -Recurse)) {
        throw 'Sharing package requires the local fakelib binaries.'
    }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:AVALONIA_TELEMETRY_OPTOUT = '1'
    foreach ($packageProject in @(
        @{ Project = 'src/Ps5Backport.App/Ps5Backport.App.csproj'; Folder = 'Studio' },
        @{ Project = 'src/Ps5Backport.Cli/Ps5Backport.Cli.csproj'; Folder = 'CLI' }
    )) {
        & dotnet publish $packageProject.Project -c $Configuration -r win-x64 --self-contained true -p:RestoreLockedMode=true -m:1 -o (Join-Path $packageRun $packageProject.Folder)
        if ($LASTEXITCODE -ne 0) { throw "Package publish failed: $($packageProject.Project)" }
        # Retain package metadata and bundled notices alongside the shared binaries.
        $packageAssets = Get-Content -LiteralPath (Join-Path (Split-Path $packageProject.Project) 'obj/project.assets.json') -Raw | ConvertFrom-Json
        $packageCacheRoots = @($packageAssets.packageFolders.PSObject.Properties.Name)
        $packageLibraries = @($packageAssets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' } | ForEach-Object { $_.Value.path })
        $packageRuntimeConfig = Get-ChildItem -LiteralPath (Join-Path $packageRun $packageProject.Folder) -Filter '*.runtimeconfig.json' | Select-Object -First 1
        $packageRuntime = (Get-Content -LiteralPath $packageRuntimeConfig.FullName -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App'
        $packageLibraries += "microsoft.netcore.app.runtime.win-x64/$($packageRuntime.version)"
        foreach ($packageLibrary in $packageLibraries) {
            $packageLibraryRoot = $packageCacheRoots | ForEach-Object { Join-Path $_ $packageLibrary } | Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
            if (!$packageLibraryRoot) { throw "Package notice source missing: $packageLibrary" }
            $packageNotices = Join-Path (Join-Path $packageRun $packageProject.Folder) ('licenses/' + $packageLibrary)
            New-Item -ItemType Directory -Path $packageNotices -Force | Out-Null
            Get-ChildItem -LiteralPath $packageLibraryRoot -File | Where-Object { $_.Extension -eq '.nuspec' -or $_.Name -match '^(LICENSE|NOTICE|THIRD[-_]PARTY[-_]NOTICES)(\.|$)' } | ForEach-Object {
                Copy-Item -LiteralPath $_.FullName -Destination $packageNotices
            }
        }
    }
    $packageArchive = Join-Path $packageRun "PS5BackportStudio-$Configuration-win-x64.zip"
    Compress-Archive -LiteralPath (Join-Path $packageRun 'Studio'), (Join-Path $packageRun 'CLI') -DestinationPath $packageArchive
    Write-Host "Package: $packageArchive"
} finally {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $packageCliTelemetryBefore
    $env:AVALONIA_TELEMETRY_OPTOUT = $packageAvaloniaTelemetryBefore
    Pop-Location
}
