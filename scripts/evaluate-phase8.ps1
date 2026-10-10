[CmdletBinding()]
param([string]$Python = 'python')

$ErrorActionPreference = 'Stop'
$phase8Root = Split-Path -Parent $PSScriptRoot
$phase8Report = Join-Path $phase8Root ('work/phase8/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$phase8TelemetryBefore = $env:DOTNET_CLI_TELEMETRY_OPTOUT
Push-Location $phase8Root
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    & $Python -c "import importlib.metadata; from jsonschema import Draft202012Validator; assert importlib.metadata.version('jsonschema') == '4.26.0', 'Use the reviewed jsonschema 4.26.0 environment'"
    if ($LASTEXITCODE -ne 0) { throw 'Phase 8 requires an existing Python environment with jsonschema 4.26.0; no package installation was attempted.' }
    & dotnet restore verification/Phase8Evaluation/Phase8Evaluation.csproj --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Phase 8 locked restore failed.' }
    & dotnet build verification/Phase8Evaluation/Phase8Evaluation.csproj -c Release --no-restore -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Phase 8 build failed.' }
    & dotnet verification/Phase8Evaluation/bin/Release/net10.0/Phase8Evaluation.dll (Join-Path $phase8Report 'dotnet.json')
    if ($LASTEXITCODE -ne 0) { throw 'Phase 8 parser/catalog observations failed.' }
    & $Python verification/Phase8Evaluation/catalog_schema.py (Join-Path $phase8Report 'catalog-schema.json')
    if ($LASTEXITCODE -ne 0) { throw 'Phase 8 SQLite/schema prototype failed.' }
    Write-Host "Phase 8 local records: $phase8Report"
} finally {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $phase8TelemetryBefore
    Pop-Location
}
