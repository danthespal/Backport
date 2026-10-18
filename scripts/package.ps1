[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateSet('win-x64', 'osx-arm64', 'osx-x64')][string]$Runtime = 'win-x64'
)

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
        & dotnet publish $packageProject.Project -c $Configuration -r $Runtime --self-contained true -p:RestoreLockedMode=true -m:1 -o (Join-Path $packageRun $packageProject.Folder)
        if ($LASTEXITCODE -ne 0) { throw "Package publish failed: $($packageProject.Project)" }
        # Retain package metadata and bundled notices alongside the shared binaries.
        $packageAssets = Get-Content -LiteralPath (Join-Path (Split-Path $packageProject.Project) 'obj/project.assets.json') -Raw | ConvertFrom-Json
        $packageCacheRoots = @($packageAssets.packageFolders.PSObject.Properties.Name)
        $packageLibraries = @($packageAssets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' } | ForEach-Object { $_.Value.path })
        $packageRuntimeConfig = Get-ChildItem -LiteralPath (Join-Path $packageRun $packageProject.Folder) -Filter '*.runtimeconfig.json' | Select-Object -First 1
        $packageRuntime = (Get-Content -LiteralPath $packageRuntimeConfig.FullName -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App'
        $packageLibraries += "microsoft.netcore.app.runtime.$Runtime/$($packageRuntime.version)"
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
    if ($Runtime -eq 'win-x64') {
        $packageArchive = Join-Path $packageRun "PS5BackportStudio-$Configuration-win-x64.zip"
        Compress-Archive -LiteralPath (Join-Path $packageRun 'Studio'), (Join-Path $packageRun 'CLI') -DestinationPath $packageArchive
    } else {
        # macOS: the program goes into "PS5 Backport Studio.app"; data/, firmware_db/, fakelibs/, licenses/ and
        # DISTRIBUTION.md stay beside the bundle, where Studio finds them by searching upwards and can write to them.
        $packageStudio = Join-Path $packageRun 'Studio'
        $packageContents = Join-Path $packageStudio 'PS5 Backport Studio.app/Contents'
        $packageMacOS = Join-Path $packageContents 'MacOS'
        New-Item -ItemType Directory -Path $packageMacOS, (Join-Path $packageContents 'Resources') -Force | Out-Null
        $packageBeside = @('data', 'firmware_db', 'fakelibs', 'licenses', 'DISTRIBUTION.md', 'PS5 Backport Studio.app')
        Get-ChildItem -LiteralPath $packageStudio | Where-Object { $packageBeside -notcontains $_.Name } | ForEach-Object {
            Move-Item -LiteralPath $_.FullName -Destination $packageMacOS
        }
        $packageVersion = ([xml](Get-Content -LiteralPath (Join-Path $packageRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
        $packagePlist = (Get-Content -LiteralPath (Join-Path $packageRoot 'build/macos/Info.plist') -Raw).Replace('@VERSION@', $packageVersion)
        [IO.File]::WriteAllText((Join-Path $packageContents 'Info.plist'), $packagePlist, (New-Object Text.UTF8Encoding $false))
        Copy-Item -LiteralPath (Join-Path $packageRoot 'build/macos/PS5BackportStudio.icns') -Destination (Join-Path $packageContents 'Resources')

        # A tar.gz keeps Unix modes (a zip written on Windows loses them): bsdtar reads each entry's mode from an mtree
        # list, 0755 for folders and Mach-O executables (the two apphosts, createdump), 0644 for everything else.
        $packageTime = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        $packageList = New-Object Collections.Generic.List[string]
        $packageList.Add('#mtree')
        foreach ($packageEntry in @(Get-Item -LiteralPath $packageStudio, (Join-Path $packageRun 'CLI')) + @(Get-ChildItem -LiteralPath $packageStudio, (Join-Path $packageRun 'CLI') -Recurse -Force)) {
            $packageRelative = $packageEntry.FullName.Substring($packageRun.Length + 1).Replace('\', '/')
            # mtree names escape every byte outside [A-Za-z0-9._/+-] as \ooo
            $packageName = -join ([Text.Encoding]::UTF8.GetBytes('./' + $packageRelative) | ForEach-Object {
                if (($_ -ge 0x30 -and $_ -le 0x39) -or ($_ -ge 0x41 -and $_ -le 0x5a) -or ($_ -ge 0x61 -and $_ -le 0x7a) -or '._/+-'.Contains([string][char]$_)) { [string][char]$_ }
                else { '\' + [Convert]::ToString($_, 8).PadLeft(3, '0') } })
            if ($packageEntry.PSIsContainer) {
                $packageList.Add("$packageName type=dir mode=0755 time=$packageTime.0")
            } else {
                $packageHead = New-Object byte[] 16
                $packageStream = [IO.File]::OpenRead($packageEntry.FullName)
                try { $packageRead = $packageStream.Read($packageHead, 0, 16) } finally { $packageStream.Dispose() }
                # 64-bit Mach-O (cf fa ed fe) of file type MH_EXECUTE (2)
                $packageExecutable = $packageRead -eq 16 -and $packageHead[0] -eq 0xcf -and $packageHead[1] -eq 0xfa -and $packageHead[2] -eq 0xed -and $packageHead[3] -eq 0xfe -and [BitConverter]::ToUInt32($packageHead, 12) -eq 2
                $packageList.Add("$packageName type=file mode=$(if ($packageExecutable) { '0755' } else { '0644' })")
            }
        }
        $packageSpec = Join-Path $packageRun 'package.mtree'
        [IO.File]::WriteAllText($packageSpec, ($packageList -join "`n") + "`n", (New-Object Text.UTF8Encoding $false))
        $packageArchive = Join-Path $packageRun "PS5BackportStudio-$Configuration-$Runtime.tar.gz"
        Push-Location $packageRun
        try { & (Join-Path $env:SystemRoot 'System32/tar.exe') -czf $packageArchive --format pax '@package.mtree' } finally { Pop-Location }
        if ($LASTEXITCODE -ne 0) { throw 'Package archive failed: tar' }
        Remove-Item -LiteralPath $packageSpec
    }
    Write-Host "Package: $packageArchive"
} finally {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $packageCliTelemetryBefore
    $env:AVALONIA_TELEMETRY_OPTOUT = $packageAvaloniaTelemetryBefore
    Pop-Location
}
