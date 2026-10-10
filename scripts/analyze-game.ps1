[CmdletBinding()]
param(
    [string]$Profile,
    [string]$Output,
    [switch]$SelfTest,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$analysisRoot = Split-Path -Parent $PSScriptRoot
if (!$SelfTest -and !$Profile) { throw 'Supply -Profile PROFILE.json or -SelfTest.' }
if ($SelfTest -and ($Profile -or $Output)) { throw '-SelfTest cannot be combined with -Profile or -Output.' }
# Resolve caller-supplied paths before changing the working directory.
$analysisProfile = if ($Profile) { [IO.Path]::GetFullPath($Profile) } else { $null }
$analysisOutput = if ($Output) { [IO.Path]::GetFullPath($Output) } elseif ($analysisProfile) {
    Join-Path $analysisRoot ('work/game-analysis/' + [IO.Path]::GetFileNameWithoutExtension($analysisProfile) + '/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
} else { $null }
$analysisTelemetryBefore = $env:DOTNET_CLI_TELEMETRY_OPTOUT
Push-Location $analysisRoot
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    if (!$NoRestore) {
        & dotnet restore verification/GameAnalysis/GameAnalysis.csproj --locked-mode
        if ($LASTEXITCODE -ne 0) { throw 'Game analysis locked restore failed.' }
    }
    & dotnet build verification/GameAnalysis/GameAnalysis.csproj -c Release --no-restore -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Game analysis build failed.' }
    $analysisArguments = @('verification/GameAnalysis/bin/Release/net10.0/GameAnalysis.dll')
    if ($SelfTest) { $analysisArguments += '--self-test' } else { $analysisArguments += @($analysisProfile, $analysisOutput) }
    & dotnet @analysisArguments
    if ($LASTEXITCODE -ne 0) { throw 'Game analysis failed; inspect run.json if an output folder was created.' }
} finally {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $analysisTelemetryBefore
    Pop-Location
}
