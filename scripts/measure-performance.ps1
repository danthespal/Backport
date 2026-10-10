[CmdletBinding()]
param(
    [ValidateRange(1, 10)][int]$Iterations = 3,
    [switch]$IncludePrivate,
    [string]$BaselineDirectory,
    [string]$InputRoot = 'work/performance/inputs',
    [string]$ReportDirectory = ('work/performance/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
)

$ErrorActionPreference = 'Stop'
$performanceRepo = Split-Path -Parent $PSScriptRoot
$performanceTelemetryBefore = $env:DOTNET_CLI_TELEMETRY_OPTOUT
function Invoke-Measurement([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Measurement failed: $($Arguments -join ' ')" }
}
Push-Location $performanceRepo
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    Invoke-Measurement @('restore', 'verification/Performance/Performance.csproj', '--locked-mode')
    Invoke-Measurement @('build', 'verification/Performance/Performance.csproj', '-c', 'Release', '--no-restore')
    $performanceEngine = 'verification/Performance/bin/Release/net10.0/Performance.dll'
    if (!(Test-Path -LiteralPath $InputRoot)) { Invoke-Measurement @($performanceEngine, 'prepare', $InputRoot) }
    foreach ($performanceInput in @('small/eboot.bin', 'large/eboot.bin', 'reference/4.00.json')) {
        if (!(Test-Path -LiteralPath (Join-Path $InputRoot $performanceInput))) { throw "Incomplete input root: $InputRoot" }
    }
    New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
    $performanceCases = @(@('small', 'analyze'), @('small', 'cook'), @('large', 'analyze'), @('large', 'cook'))
    if ($IncludePrivate) { $performanceCases += @(@('private-small', 'analyze'), @('private-large', 'analyze'), @('private-large', 'cook')) }
    if ($BaselineDirectory) {
        $performanceBaseline = Join-Path $BaselineDirectory 'Performance.dll'
        if (!(Test-Path -LiteralPath $performanceBaseline)) { throw 'BaselineDirectory must contain a compatible measurement harness and its preserved engine dependencies.' }
    }
    foreach ($performanceCase in $performanceCases) {
        $performanceName = $performanceCase -join '-'
        if ($BaselineDirectory) {
            Invoke-Measurement @($performanceBaseline, 'run', '.', $InputRoot, $performanceCase[0], $performanceCase[1], "$Iterations", (Join-Path $ReportDirectory "before-$performanceName.json"))
        }
        Invoke-Measurement @($performanceEngine, 'run', '.', $InputRoot, $performanceCase[0], $performanceCase[1], "$Iterations", (Join-Path $ReportDirectory "after-$performanceName.json"))
        if ($BaselineDirectory) {
            $performanceBefore = Get-Content (Join-Path $ReportDirectory "before-$performanceName.json") -Raw | ConvertFrom-Json
            $performanceAfter = Get-Content (Join-Path $ReportDirectory "after-$performanceName.json") -Raw | ConvertFrom-Json
            if (($performanceBefore.Inputs | ConvertTo-Json -Compress) -ne ($performanceAfter.Inputs | ConvertTo-Json -Compress)) { throw "Input mismatch: $performanceName" }
            for ($performanceIndex = 0; $performanceIndex -lt $Iterations; $performanceIndex++) {
                $performanceB = $performanceBefore.Records[$performanceIndex]
                $performanceA = $performanceAfter.Records[$performanceIndex]
                if ($performanceB.ReportSha256 -ne $performanceA.ReportSha256 -or $performanceB.TextReportSha256 -ne $performanceA.TextReportSha256 -or
                    ($performanceB.OutputFiles | ConvertTo-Json -Compress) -ne ($performanceA.OutputFiles | ConvertTo-Json -Compress)) { throw "Output/report mismatch: $performanceName" }
            }
        }
    }
    Invoke-Measurement @($performanceEngine, 'ownership', (Join-Path $InputRoot 'large'), (Join-Path $ReportDirectory 'ownership-large.json'))
    Invoke-Measurement @($performanceEngine, 'cancel', (Join-Path $InputRoot 'large'), (Join-Path $InputRoot 'reference'), (Join-Path $ReportDirectory 'after-cancellation.json'))
    if ($BaselineDirectory) { Invoke-Measurement @($performanceBaseline, 'cancel', (Join-Path $InputRoot 'large'), (Join-Path $InputRoot 'reference'), (Join-Path $ReportDirectory 'before-cancellation.json')) }
    Write-Host "Local measurements: $ReportDirectory"
}
finally { $env:DOTNET_CLI_TELEMETRY_OPTOUT = $performanceTelemetryBefore; Pop-Location }
