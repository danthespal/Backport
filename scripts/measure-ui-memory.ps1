[CmdletBinding()]
param(
    [string]$ApplicationDirectory = 'src/Ps5Backport.App/bin/Release/net10.0',
    [string]$Game = 'work/performance/inputs/small',
    [string]$ReportDirectory = 'work/performance/ui',
    [ValidateRange(1, 10)][int]$Iterations = 3
)

$ErrorActionPreference = 'Stop'
$uiProfileRepo = Split-Path -Parent $PSScriptRoot
Push-Location $uiProfileRepo
try {
    $uiProfileApp = [IO.Path]::GetFullPath((Join-Path $ApplicationDirectory 'Ps5BackportStudio.dll'))
    if (!(Test-Path -LiteralPath $uiProfileApp)) { throw 'Build the Release application first.' }
    New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
    $uiProfileResults = @()
    for ($uiProfileIteration = 0; $uiProfileIteration -lt $Iterations; $uiProfileIteration++) {
        $uiProfileImage = [IO.Path]::GetFullPath((Join-Path $ReportDirectory "frame-$uiProfileIteration.png"))
        $uiProfileInfo = [Diagnostics.ProcessStartInfo]::new('dotnet')
        $uiProfileInfo.WorkingDirectory = $uiProfileRepo
        $uiProfileInfo.UseShellExecute = $false
        $uiProfileInfo.CreateNoWindow = $true
        $uiProfileInfo.RedirectStandardOutput = $true
        $uiProfileInfo.RedirectStandardError = $true
        foreach ($uiProfileArgument in @($uiProfileApp, '--snapshot', $uiProfileImage, '--game', [IO.Path]::GetFullPath($Game), '--analyze')) {
            $uiProfileInfo.ArgumentList.Add($uiProfileArgument)
        }
        $uiProfileProcess = [Diagnostics.Process]::Start($uiProfileInfo)
        $uiProfileOut = $uiProfileProcess.StandardOutput.ReadToEndAsync()
        $uiProfileError = $uiProfileProcess.StandardError.ReadToEndAsync()
        $uiProfileWatch = [Diagnostics.Stopwatch]::StartNew()
        $uiProfileWorking = 0L; $uiProfilePrivate = 0L
        try {
            while (!$uiProfileProcess.WaitForExit(20)) {
                if ($uiProfileWatch.Elapsed.TotalSeconds -gt 120) { $uiProfileProcess.Kill($true); throw 'Headless UI measurement timed out.' }
                try {
                    $uiProfileProcess.Refresh()
                    $uiProfileWorking = [math]::Max($uiProfileWorking, $uiProfileProcess.PeakWorkingSet64)
                    $uiProfilePrivate = [math]::Max($uiProfilePrivate, $uiProfileProcess.PrivateMemorySize64)
                } catch [InvalidOperationException] { if (!$uiProfileProcess.HasExited) { throw } }
            }
            if ($uiProfileProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath $uiProfileImage)) { throw "Headless UI failed: $($uiProfileError.GetAwaiter().GetResult())" }
            $uiProfileResults += [pscustomobject]@{ Iteration = $uiProfileIteration; Milliseconds = $uiProfileWatch.Elapsed.TotalMilliseconds;
                PeakWorkingSet = $uiProfileWorking; SampledPeakPrivateBytes = $uiProfilePrivate;
                FrameSha256 = (Get-FileHash -LiteralPath $uiProfileImage -Algorithm SHA256).Hash }
            $uiProfileOut.GetAwaiter().GetResult() | Out-File -LiteralPath (Join-Path $ReportDirectory "stdout-$uiProfileIteration.txt")
            $uiProfileError.GetAwaiter().GetResult() | Out-File -LiteralPath (Join-Path $ReportDirectory "stderr-$uiProfileIteration.txt")
        } finally { $uiProfileProcess.Dispose() }
    }
    $uiProfileResults | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ReportDirectory 'memory.json')
    Write-Host "Headless Skia UI process measurements: $ReportDirectory"
}
finally { Pop-Location }
