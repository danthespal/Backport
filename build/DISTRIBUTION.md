# Sharing PS5 Backport Studio

Debug and Release builds copy the required `data/` rule files and `firmware_db/` catalogs beside both executables, plus locally available `fakelibs/` binaries/manifests and `data/aerolib.csv`. Publishing copies them too. Repository originals stay in place. Keep these folders and every generated DLL/runtime file with the executable when sharing; copying just the EXE is insufficient.

## Ordinary builds

```powershell
dotnet build Ps5Backport.slnx -c Debug
dotnet build Ps5Backport.slnx -c Release
```

The desktop output is `src/Ps5Backport.App/bin/<configuration>/net10.0/`, with `Ps5BackportStudio.exe`. The CLI output is `src/Ps5Backport.Cli/bin/<configuration>/net10.0/`, with `Ps5Backport.Cli.exe`. These are framework-dependent builds: recipients need a compatible .NET 10 runtime. Share the entire relevant output folder, including its `runtimes/` directory and the copied assets.

## Windows x64 package with .NET included

From the source repository, run:

```powershell
pwsh -NoProfile -File scripts/package.ps1 -Configuration Release
# Debug can also be packaged:
pwsh -NoProfile -File scripts/package.ps1 -Configuration Debug
```

The script publishes both executables as self-contained Windows x64 applications into separate `Studio/` and `CLI/` folders under `work/packages/<configuration>/<run>/`. It includes .NET and the projects' managed/native runtime dependencies, rules, firmware catalogs, local fakelibs and the symbol database, and writes `PS5BackportStudio-<configuration>-win-x64.zip`. Recipients extract the archive and run the desktop EXE in `Studio/` or the CLI EXE in `CLI/`. The build machine needs the pinned SDK and access to any missing NuGet/runtime packages; recipients do not need the SDK or a separately installed .NET runtime.

No trimming or single-file bundling is enabled. Packages retain resolved NuGet metadata and bundled license/third-party notices under `licenses/`. Packages are not signed installers and do not include source, tests or engineering reports. Package generation copies files and leaves source assets unchanged. It does not upload or publish the archive.

## Fakelib and symbol inputs and per-user files

The sharing package includes the local fakelib store's `.sprx`, `.prx`, `.psp` companion files and manifests, preserving firmware and variant subfolders. Publication staging/backup folders are excluded. It copies the maintained `data/aerolib.csv` to `data/aerolib.csv` beside each executable. Runtime symbol lookup and packaging do not depend on `.original/reference`. Both clients automatically discover these packaged inputs; Settings and CLI path overrides remain available. Other reference repository files are not bundled.

Ordinary builds tolerate absent local fakelibs; the symbol catalog is included in the source tree under `data/`. Function-name lookup falls back to NIDs if the runtime symbol database is unavailable. The sharing script requires the symbol file and local fakelib binaries and stops if they are missing. Some backport options need a matching fakelib set; the included sets do not establish compatibility for every target.

Saved paths/window settings and diagnostic logs stay in the recipient's per-user application-data directory. They are not copied from the developer machine. The runtime package does not establish console compatibility for unverified fixes; retain the existing rule evidence and testing process.
