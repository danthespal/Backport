[CmdletBinding()]
param([ValidateRange(30, 600)][int]$TimeoutSeconds = 180)

$ErrorActionPreference = 'Stop'
$mutationRoot = Split-Path -Parent $PSScriptRoot
$mutationTools = Join-Path $mutationRoot 'work/phase3/tools'
$mutationExe = Join-Path $mutationTools $(if ($IsWindows) { 'dotnet-stryker.exe' } else { 'dotnet-stryker' })
$mutationCliBefore = $env:DOTNET_CLI_TELEMETRY_OPTOUT
$mutationTestBefore = $env:TESTINGPLATFORM_TELEMETRY_OPTOUT
$mutationAvaloniaBefore = $env:AVALONIA_TELEMETRY_OPTOUT
if ($env:PS5BP_RECORD -eq '1') { throw 'Mutation evaluation refuses golden recording.' }
Push-Location $mutationRoot
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'
    $env:AVALONIA_TELEMETRY_OPTOUT = '1'
    if (!(Test-Path -LiteralPath (Join-Path $mutationTools '.store/dotnet-stryker/5.0.0'))) {
        & dotnet tool install dotnet-stryker --version 5.0.0 --tool-path $mutationTools
        if ($LASTEXITCODE -ne 0) { throw 'Cannot install the pinned Stryker evaluation tool.' }
    }
    & dotnet restore verification/MutationTests/MutationTests.csproj --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Mutation project locked restore failed.' }
    $mutationInfo = [System.Diagnostics.ProcessStartInfo]::new($mutationExe)
    $mutationInfo.UseShellExecute = $false
    $mutationInfo.CreateNoWindow = $true
    $mutationInfo.WorkingDirectory = Join-Path $mutationRoot 'verification/MutationTests'
    $mutationInfo.ArgumentList.Add('--output')
    $mutationInfo.ArgumentList.Add((Join-Path $mutationRoot 'work/phase3/stryker'))
    $mutationProcess = [System.Diagnostics.Process]::Start($mutationInfo)
    try {
        if (!$mutationProcess.WaitForExit($TimeoutSeconds * 1000)) {
            $mutationProcess.Kill($true)
            throw "Mutation evaluation exceeded $TimeoutSeconds seconds; process tree stopped."
        }
        if ($mutationProcess.ExitCode -ne 0) { throw "Mutation evaluation failed: $($mutationProcess.ExitCode)" }
    } finally { $mutationProcess.Dispose() }
} finally {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $mutationCliBefore
    $env:TESTINGPLATFORM_TELEMETRY_OPTOUT = $mutationTestBefore
    $env:AVALONIA_TELEMETRY_OPTOUT = $mutationAvaloniaBefore
    Pop-Location
}
