[CmdletBinding()]
param([switch]$IncludePrivate)

$ErrorActionPreference = 'Stop'
$decoderRoot = Split-Path -Parent $PSScriptRoot
$decoderReport = Join-Path $decoderRoot ('work/phase7/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '/evaluation.json')
$decoderTelemetryBefore = $env:DOTNET_CLI_TELEMETRY_OPTOUT
Push-Location $decoderRoot
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    & dotnet restore verification/DecoderEvaluation/DecoderEvaluation.csproj --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Decoder evaluation locked restore failed.' }
    & dotnet build verification/DecoderEvaluation/DecoderEvaluation.csproj -c Release --no-restore -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Decoder evaluation build failed.' }
    $decoderArgs = @('verification/DecoderEvaluation/bin/Release/net10.0/DecoderEvaluation.dll', $decoderReport)
    if ($IncludePrivate) { $decoderArgs += '--private' }
    & dotnet @decoderArgs
    if ($LASTEXITCODE -ne 0) { throw 'Decoder evaluation failed.' }
} finally {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $decoderTelemetryBefore
    Pop-Location
}
