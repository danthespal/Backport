[CmdletBinding()]
param(
    [ValidateSet('Public', 'Private', 'All')][string]$Lane = 'Public',
    [ValidateSet('Debug', 'Release')][string[]]$Configuration = @('Debug', 'Release')
)

$ErrorActionPreference = 'Stop'
$verifyRoot = Split-Path -Parent $PSScriptRoot
if ($env:PS5BP_RECORD -eq '1') { throw 'Verification refuses PS5BP_RECORD=1: golden recording is not verification.' }
$verifyReport = Join-Path $verifyRoot ('work/verification/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
$verifyPrivateBefore = $env:PS5BP_REQUIRE_PRIVATE
$verifyCliTelemetryBefore = $env:DOTNET_CLI_TELEMETRY_OPTOUT
$verifyTestTelemetryBefore = $env:TESTINGPLATFORM_TELEMETRY_OPTOUT
$verifyAvaloniaTelemetryBefore = $env:AVALONIA_TELEMETRY_OPTOUT

function Invoke-Dotnet([string[]]$DotnetArguments) {
    & dotnet @DotnetArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE): $($DotnetArguments -join ' ')" }
}

Push-Location $verifyRoot
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'
    $env:AVALONIA_TELEMETRY_OPTOUT = '1'
    if ($Lane -ne 'Public') {
        foreach ($verifyFixture in @('.original/backports', '.original/reference', 'work/NOT_BACKPORT_elf', 'fakelibs/4', 'fakelibs/4.03')) {
            if (!(Test-Path -LiteralPath (Join-Path $verifyRoot $verifyFixture) -PathType Container)) {
                throw "Private lane setup failed: required private fixture directory missing: $verifyFixture"
            }
        }
        $env:PS5BP_REQUIRE_PRIVATE = '1'
    } else { $env:PS5BP_REQUIRE_PRIVATE = $null }
    New-Item -ItemType Directory -Path $verifyReport | Out-Null
    & dotnet --info | Out-File -LiteralPath (Join-Path $verifyReport 'dotnet-info.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read .NET environment.' }
    Invoke-Dotnet -DotnetArguments @('restore', 'Ps5Backport.slnx', '--locked-mode')
    foreach ($verifyConfiguration in $Configuration) {
        Invoke-Dotnet -DotnetArguments @('build', 'Ps5Backport.slnx', '-c', $verifyConfiguration, '--no-restore', '-m:1')
        $verifyDll = Join-Path $verifyRoot "src/Ps5Backport.Tests/bin/$verifyConfiguration/net10.0/Ps5Backport.Tests.dll"
        $verifyXml = Join-Path $verifyReport "$Lane-$verifyConfiguration.xml"
        $verifyArgs = @($verifyDll, '-failSkips', '-failWarns', '-result-xml', $verifyXml)
        if ($Lane -eq 'Public') { $verifyArgs += @('-trait-', 'Lane=Private') }
        if ($Lane -eq 'Private') { $verifyArgs += @('-trait', 'Lane=Private') }
        Invoke-Dotnet -DotnetArguments $verifyArgs
        [xml]$verifyResults = Get-Content -LiteralPath $verifyXml -Raw
        $verifyAssembly = $verifyResults.assemblies.assembly
        if ([int]$verifyAssembly.total -le 0 -or [int]$verifyAssembly.failed -ne 0 -or [int]$verifyAssembly.skipped -ne 0) {
            throw "Invalid or incomplete verification results: $verifyXml"
        }
        Write-Host "$Lane/$verifyConfiguration passed: $($verifyAssembly.total) tests, zero skips."
    }
    Write-Host "Verification records: $verifyReport"
} finally {
    $env:PS5BP_REQUIRE_PRIVATE = $verifyPrivateBefore
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $verifyCliTelemetryBefore
    $env:TESTINGPLATFORM_TELEMETRY_OPTOUT = $verifyTestTelemetryBefore
    $env:AVALONIA_TELEMETRY_OPTOUT = $verifyAvaloniaTelemetryBefore
    Pop-Location
}
