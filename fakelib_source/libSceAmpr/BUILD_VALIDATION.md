# Unchanged 0.3.6.6 build and static validation

Date: 2026-10-10.

Update: the public payload-SDK build now succeeds and its rebuilt output passes
the structural and firmware import checks. See `PAYLOAD_BUILD.md` for the command,
artifacts, hashes, and remaining console tests. The sections below record the
earlier attempt to use the original proprietary SDK project.

## Result

The unchanged source was submitted to the existing upstream MSBuild project.
The build stopped in ValidateProsperoSdk: the required Prospero SDK 2.000
directory is absent. No rebuilt PRX or SPRX was produced, and no fakelib was
replaced. All 70 imported upstream files still match the pinned Git tree under
upstream line-ending normalization.

Visual Studio Community 2026 and MSBuild are installed. The missing requirement
is the SDK/compiler integration and target headers/libraries, not .NET itself.

## Build attempted

From E:\PS5_TOOLS\Backport:

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' `
  fakelib_source/libSceAmpr/msbuild/libSceAmpr/libSceAmpr.vcxproj `
  /t:Build /p:Configuration=ReleaseHooks /p:Platform=Prospero `
  /p:SolutionDir=E:\PS5_TOOLS\Backport\fakelib_source\libSceAmpr\ `
  /v:minimal /nologo /fl /flp:LogFile=work/ampr-ghidra/source-build-msbuild.log
```

The upstream property sheet requires C:\SCE\Prospero SDKs\2.000. This path and
the SDK environment settings were not present in the inspected environment.
The error log is in work/ampr-ghidra/source-build-msbuild.log.

## Checks completed using the existing published release binary

The exact 0.3.6.6-test ELF already extracted from fakelibs/4.03 was checked against
all 38 available firmware export databases from 4.00 through 12.70. Every report
has zero missing and zero unknown function imports. This is validation of the
published binary, not of an output built from this source on this machine.
SDK metadata remains PS5 0x02000009 and PS4 0x08050001.

The source's 48 mandatory hook entries were mapped to NIDs using aerolib and
checked specifically against each firmware's libkernel exports. All 48 NIDs are
present in all 38 databases. This establishes export availability only; runtime
symbol resolution by name, detour installation, ABI semantics, and hook coverage
remain untested. Data imports also remain outside this function-import result.

Full reports and matrices are in work/ampr-source-validation/:

- release-import-matrix.json
- release-<firmware>-imports.txt
- mandatory-hook-matrix.json

## Alternative toolchains inspected

E:\PS5_TOOLS\ps5-payload-sdk is installed. The later local upstream clone at
E:\PS5_TOOLS\ampr_emu has a payload-SDK Makefile and compatibility files under
ps5/. These are absent from the pinned 0.3.6.6 source. Substituting that recipe
would require a separately verified build port, with matching compatibility
headers, import generation, CRT and PRX linkage. Building the later clone instead
would change the source baseline and is not an unchanged 0.3.6.6 build.

SharpProspero (https://github.com/SvenGDK/SharpProspero) describes a C# SDK using
NativeAOT and its own linker. Its linking, stub and container tools may be useful,
but it does not provide a drop-in replacement for the C++ SDK 2.000 build project.
It was inspected, not installed or used to build this source.

## Remaining verification

The original SDK build still requires the missing proprietary toolchain. The
separate public SDK build has now been implemented while preserving the pinned
AMPR implementation. Its module identity, SDK fields, exports and import dependencies
have passed static validation; details are in `PAYLOAD_BUILD.md`.
Console tests must cover module loading and hook installation, asset loading,
saving/loading saves, repeated transitions, and asynchronous completion behavior.
No console execution was performed during this validation.
