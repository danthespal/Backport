# PS5 Backport Studio

Consolidated project guide and confirmed research, 10 October 2026.

PS5 Backport Studio is a private Windows desktop/CLI tool for inspecting decrypted PS5 game modules and producing backport overlays. Both clients use one C# engine, `Ps5Backport.Core`, with system-library/function knowledge and evidence-aware optional fixes. Lowered SDK fields, a clean import report or a successful cook do not establish console compatibility.

This is the single root documentation file. It retains the useful implementation contracts, verification instructions, measured results and binary/reference findings from the former documents. Obsolete planning checklists and unimplemented architecture proposals were removed; implemented decisions and meaningful limitations remain. Detailed sections retain their original historical dates/test counts. Newer implementation contracts supersede earlier audit limitations; old reports are not new tests of the current code. The current audit below records the completed local review and its verification limits; it does not establish console compatibility or the absence of every defect.

Game, console and diagnostic investigations (crashes, save failures, AGC/Psml experiments, rejected hypotheses and open items) are kept in [FINDINGS.md](FINDINGS.md). The `tmp/…` and `work/…` evidence paths cited below were consolidated there and deleted on 12 October 2026, except for the `work/*_elf` fixtures, `work/agc-4.03-patch/` and `work/fakelib-backups/`.

## Contents

- [Current software audit and confirmed fixes](#current-audit)
- [Interactive UI investigation](#ui-investigation)
- [Project decisions and layout](#project-decisions)
- [Build, runtime assets and sharing](#build-and-sharing)
- [Verification and fixture requirements](#verification)
- [Operation lifecycle and cancellation](#operation-lifecycle)
- [Publication, recovery and retention](#publication-and-recovery)
- [Local diagnostics and UI coverage](#diagnostics-and-ui)
- [Unity unset-SKU fallback](#unity-unset-sku)
- [Ubisoft offline popup option](#ubisoft-offline-popups)
- [Console tester checklist](#tester-checklist)
- [PPSA03671 and Crow Country](#ppsa03671-and-crow-country)
- [PPSA34008 investigation](#ppsa34008)
- [Performance and memory](#performance)
- [Instruction-decoder evaluation](#instruction-decoder)
- [CLI/catalog/schema decisions](#cli-catalog-schema-decisions)
- [Dependency review](#dependencies)
- [Historical confirmed audit fixes](#historical-audit)
- [Reference projects and binary research](#reference-research)
- [AnyPS5 improvement plan and reference priorities](#anyps5-plan)
- [Later engine changes (13–14 October)](#later-changes)
- [Engineering milestones](#engineering-milestones)
- [Former document index and provenance](#document-provenance)

<a id="current-audit"></a>
## Current software audit and confirmed fixes

**10 October 2026 — local source review and public verification.** Reviewed the managed Core, CLI and desktop code, parser and patching boundaries, operation lifecycle, publication/recovery, runtime assets and verification scripts. UI review included bindings, commands, selection state and shutdown; existing headless interaction tests exercised the supported scenarios. The bundled native `libSceAmpr` received a focused review of allocation/pools, file-index bounds, command bounds, hook trampolines and synchronization, plus the existing structural verifier. Its roughly 30,000 lines were not exhaustively reviewed, and it was not executed on a console. The codebase-memory MCP transport was unavailable, so source inspection used local files.

Only confirmed defects were changed. The following 11 groups describe this audit's changes, separately from earlier uncommitted work and historical fixes:

| Confirmed defect | Correction and verification |
| --- | --- |
| Base-64 library/module IDs silently overflowed `int`; empty IDs and invalid NID characters were accepted. | `Nid` rejects empty/overflowing IDs and invalid NID alphabets. Four new malformed-input cases. |
| An uppercase `0X` SDK value in `param.json` was readable but never replaced, despite the patch reporting success. | The replacement pattern accepts both prefix cases. A regression verifies the actual lowered file value. |
| Malformed firmware database types, missing fields and null NIDs/firmware escaped as runtime exceptions or produced invalid state. | `FirmwareDb.Load` reports `InvalidDataException`; the database page logs the error and restores its idle state. Five schema cases exercise both Core and the view model. |
| Sony build lookup failed for an empty catalog, an oversized build ID, or valid keys with different zero padding. | Empty/oversized lookups return no classification; range classification resolves the actual catalog key. Three cases cover the former failures. |
| Tools promised never to overwrite existing output, but a file created after the existence check was overwritten during publication. | Atomic writes support create-only publication, used by both SELF conversion commands. A regression verifies existing bytes survive and temporary files are removed. Existing two-argument APIs retain their replacement behavior. |
| A selected optional fix could use the base/candidate stub body rather than its selected override. | Stub rules and provenance use the composed fixes for the run. Analyze/cook integration cases check the chosen value and emitted instructions. |
| Cooking with custom knowledge could fail while writing the report because option names came from `Knowledge.Default`. | Reports use the run's captured knowledge; an unknown option is formatted by key. Existing API overloads remain available. The same two integration cases verify custom report names and successful publication. |
| A syntactically valid fakelib manifest with wrong field types reached typed CLI/UI getters and crashed. | Manifest loading validates recognized optional strings and nonnegative integer sizes before use, while retaining unknown fields. Four cases verify controlled CLI failure. |
| A changed upstream documentation heading caused candidate generation to fail with `IndexOutOfRangeException`. | Missing sections produce an explicit data error before writing candidates. One regression covers the changed heading. |
| Three commands discarded the disposable `Process` wrapper returned when opening folders. | The main output, settings and store commands dispose their wrappers. Opening the folder continues normally; this was verified by disposal/lifecycle source review, without launching Explorer in tests. |
| ELF load segments could span past the last representable virtual address, allowing address translation to wrap. | The parser rejects overflowing file-backed or memory ranges. Two malformed-range cases and one valid range ending at `ulong.MaxValue` guard the boundary. |

The input, integration and address-range regressions reproduced their failures before the corresponding fixes. Create-only publication has a new explicit API and preservation regression; process-wrapper disposal was a direct resource-lifecycle correction. No reference data, golden expectations, private fixture requirements or compiled fakelib binaries were changed by this audit. All new tests remain in the existing consolidated `src/Ps5Backport.Tests/Tests.cs`.

### Coverage and limits

| Requested area | Local evidence and remaining limits |
| --- | --- |
| Memory and resource leaks | Reviewed disposable streams/documents/processes, bounded logs/rows, cancellation and worker shutdown. Corrected the three process-wrapper omissions. Public retention/lifecycle tests pass; a long console/native heap soak was not performed. |
| Stack and buffer overflow | Reviewed ELF/SELF/BPS bounds, generated malformed inputs and native bounded copies. Corrected integer/address overflow. Managed bounds checks do not establish native memory safety; console execution is still required. |
| Null references, syntax and runtime errors | Zero-warning builds, schema/error-path regressions and UI/CLI public checks. Corrected firmware/manifest/build-lookup/generator failures. Unsupported or missing private inputs remain outside the public lane. |
| Logical errors and API inconsistencies | Verified SDK patch bytes, selected rule bodies, custom knowledge reports and atomic output semantics. Kept existing public overloads. Console meaning of every rule is not proven by byte-level tests. |
| Infinite loops, races and deadlocks | Reviewed loop bounds, locks, asynchronous command ownership, cancellation and publication transactions. Corrected the Tools output race. Child-process parser budgets and publication/lifecycle tests pass; native thread scheduling and console I/O cannot be proven by static review. |
| Integration, UI/UX and compatibility | Public tests cover shared engine behavior, runtime assets, portable CLI lookup and headless UI interactions. Native structural verification passes. Physical display behavior, other OS/runtime environments and actual game/firmware behavior require separate runtime evidence. |
| Leftover bugs | Reviewed remaining scripts, configuration and code paths. Historical decoder/Phase 8 verification projects still exist; they were not removed as supposed leftovers. Unconfirmed concerns were not rewritten. |

**Final managed verification:** `pwsh -NoProfile -File scripts/verify.ps1 -Lane Public` restored with locked package versions, built Debug and Release with **0 warnings / 0 errors**, and passed **335 tests in each configuration**, with **0 failures / 0 skips**. This adds 24 regression cases to the preceding 311-test public baseline. The suite includes parser budget/fuzz checks, transaction fault injection, competing publication writers, cancellation, bounded UI history and headless interaction checks. It is evidence for those scenarios, not an exhaustive proof of leak freedom or correctness.

**Native structural verification:** `make -j2 PS5_PAYLOAD_SDK=/mnt/e/PS5_TOOLS/ps5-payload-sdk verify` through Ubuntu/WSL passed on the existing build: 118 exports, 122 definitions, 111 imports (9 libc / 102 kernel), 234 hash buckets and SELF version `0x02000001`. The required modules were `libSceLibcInternal.prx` and `libkernel.prx`. This did not rebuild or deploy the binaries and does not test native runtime safety.

The Private/All lane was not run: the earlier authorized cleanup removed required game fixtures. No fixture-based golden result is claimed for this audit. Raw public verification records were consolidated here, then this audit's two generated verification folders were removed to keep `work` limited to its retained findings file. Further validation needs restored private fixtures, a physical desktop check where relevant, and console memory/threading/game tests.

<a id="ui-investigation"></a>
## Interactive UI investigation

Scenario inventory for the Windows desktop investigation started 10 October 2026. Results distinguish physical UI actions from automated/headless coverage. The inventory covers exposed actions and relevant edge cases; every possible input or option combination cannot be enumerated exhaustively.

| Area | Available actions/options | Scenarios to exercise |
| --- | --- | --- |
| Window/navigation | Backport, Fakelib store, Firmware database, Tools, Settings; minimize, maximize/restore, resize, close; log divider | First start, remembered game, switching pages during work, small/large window, scrolling, keyboard focus, close while idle/busy, restart persistence. |
| Game selection | Open folder dialog; drag/drop game folder or module; remembered game on startup | Cancel dialog, valid game, empty folder, malformed metadata/modules, missing remembered folder, switch games/reset per-game options. |
| Backport run | Target firmware; Analyze; Cook; Cancel; output folder | Valid analyze/cook, repeat run, rapid clicks, no game, missing database/fakelibs, invalid/empty/input-overlapping output, existing output, cancellation, stale results after changing inputs. |
| General options | Known fixes, candidate stubs, stub unknown imports, define stubbed symbols, extend code segment, lower param.json versions | Toggle each, synchronization with Settings, defaults across restart, representative combinations and disabled prerequisites. |
| Per-game options | Ubisoft offline popups, Unity unset-SKU fallback, Force PS5 Pro, GPU compute scratch fix; AMPR logging | Toggle each, warnings, matching/nonmatching modules, Known fixes disabled, absent variant, reset on game switch. |
| Overview/output | Metrics, warnings, reference, Open output, Build AMPR index | Before/after analysis/cook, absent output, successful index, repeated index, source/output relationship and reported counts. |
| Modules/imports/report | Module selection; import status filter/search; all-modules reset; import detail; report text selection/copy | Empty/populated tables, every filter, no match, mixed case, selected-row details, module scoping, large lists, report scroll/keyboard access. |
| Fakelib store | Set/file selection; add firmware/source/files; check imports against firmware; Open folder | Empty/valid/bad set or manifest, valid/invalid/blank firmware, cancel picker, duplicate inputs, checks while switching selection, no firmware selected. |
| BPS store workflow | Patch/input pickers; patch information; target firmware/source; fake-sign toggle; Apply and add | Cancel pickers, valid patch, malformed/truncated patch, wrong input checksum, missing input/patch, sign on/off, existing output. |
| Firmware database | Firmware/module selection; comparison firmware; changed-only toggle; NID search; import firmware/source/folder; genstub toggle | Empty/bad/valid database, same/different comparison, every module, no-match search, blank/invalid firmware, canceled/empty/valid import folder, genstub on/off. |
| SELF tools | Add/drop files; Clear list; unwrap; fake-sign; choose output; next to files | No files, ELF/SELF/invalid/encrypted input, duplicate/multiple files, canceled pickers, output collision, round trip, invalid output, busy interactions. |
| NID lookup | Function names, known/unknown NIDs, NID#library#module symbols; multiple tokens | Empty/whitespace, name, known/unknown ID, malformed/overflowing symbol IDs, mixed tokens, long paste, responsiveness. |
| Settings | Change/reset firmware database, fakelib store, aerolib CSV; six defaults and target firmware; Forget recent games; Open settings folder | Valid/empty/malformed/missing overrides, canceled picker, reset automatic discovery, cross-page refresh, recent-game state, restart preservation. |
| Development log | All/errors/warnings, Copy, Clear, entry double-click/module navigation; retention | Empty and populated filters, errors/warnings, clipboard, navigation, long messages/history, resizing divider, busy status/cancel. Clear/Forget affect retained user data and need separate action-time approval under the computer-use policy. |

Investigation results will be recorded here after physical checks. Existing settings are backed up locally before changing test state. File-writing scenarios use isolated test inputs/outputs rather than modifying the user's game or maintained stores.

**SDK indicator correction:** At the user's request, the header indicator and comparison bar now use `sdkVersion`, labeled `Game SDK`, rather than presenting `requiredSystemSoftwareVersion` as a verified game requirement. The Rogue Prince of Persia 01.006.000 PPSA28155 declares SDK `0x1100000000000000` (11.00) and required system software `0x1360000000000000` (13.60). The UI now shows `Game SDK 11.00`; neither field proves runtime compatibility. Missing SDK metadata displays `Game SDK unknown` and does not fall back to the other field. Both metadata fields remain available in Core; parameter lowering and patching are unchanged.

<a id="project-decisions"></a>
## Project decisions and layout

- One C# engine is maintained; the former Python `ps5bp` implementation was retired. `ps5backport` supplies the equivalent commands. Python remains usable for isolated local research, not a second production patching engine.
- The desktop uses Avalonia/CommunityToolkit.Mvvm, a Fluent workbench, a blue accent and system light/dark preference. Core and CLI have no external runtime NuGet packages.
- `Ps5Backport.slnx` includes Core, CLI, App, Tests and the UiProfile verification worker. Optional performance, benchmarks, mutation, decoder, game-analysis and Phase 8 tools live under `verification` and are invoked explicitly.
- The reviewed SDK is pinned by `global.json` to .NET 10.0.401. Shared compilation enables nullable analysis and treats warnings as errors. Project lock files support locked restores.
- Rule inputs live in `data`; export catalogs in `firmware_db`; local replacement modules/manifests in `fakelibs`; private references/source samples in `.original`. Golden expectations live in `src/Ps5Backport.Tests/golden`.
- Preserve the distinction between proven/reference-derived fixes and candidates. Default rules are generic by library/function; recognized engine/library-specific options must fail closed on ambiguous layouts. Game-specific instruction addresses from research are not automatically reusable rules.
- The app has no console FTP/payload/log-fetch integration and no installer/update platform. Sharing packages are now supported, superseding the old plan that dropped all release work. Private inputs remain local; package contents follow the sharing section, not the obsolete source-only plan.
- No game/system binaries belong in Git. Third-party license/notices accompany distributed dependencies. Restoring discarded plans does not override the current implementation contracts below.

## Cleanup and historical evidence

The 10 October `work` cleanup removed the private/generated fixtures at the user's explicit request; its consolidation (`work/CONFIRMED_FINDINGS.md`: Crow Country, Rogue, AMPR differences, historical xUnit summaries, measurements and evidence identities) is now Part 2 of [FINDINGS.md](FINDINGS.md).

The private fixtures `work/NOT_BACKPORT_elf`, `CONTROL_elf` and `GT7_*_elf` were recreated on 11 October and are kept, together with `work/agc-4.03-patch/` and `work/fakelib-backups/`. Other historical `work/...` paths in the sections below identify deleted records, not presently available artifacts; baseline assemblies, logs and diagnostic restore ZIPs must not be assumed to survive. Public verification remains the fixture-independent lane. New verification/packaging commands may recreate generated folders under `work`.

`build/DISTRIBUTION.md` is the short document intentionally shipped with both executables. It is retained outside the root and still appears as `DISTRIBUTION.md` in build/publish outputs. Engineering/private reference documentation is not bundled with recipients' applications.

Console setup/version statements in historical reference research are dated observations, not current release guidance. Check the intended console/tool versions before using them. Firmware export observations identify the available sampled database, not necessarily the true API introduction date or complete runtime support.

---
<a id="build-and-sharing"></a>
## Build and sharing

Former source: `DISTRIBUTION.md`. Historical results retain their original scope.

### Sharing PS5 Backport Studio

Debug and Release builds copy the required `data/` rule files and `firmware_db/` catalogs beside both executables, plus locally available `fakelibs/` binaries/manifests and `data/aerolib.csv`. Publishing copies them too. Repository originals stay in place. Keep these folders and every generated DLL/runtime file with the executable when sharing; copying just the EXE is insufficient.

#### Ordinary builds

```powershell
dotnet build Ps5Backport.slnx -c Debug
dotnet build Ps5Backport.slnx -c Release
```

The desktop output is `src/Ps5Backport.App/bin/<configuration>/net10.0/`, with `Ps5BackportStudio.exe`. The CLI output is `src/Ps5Backport.Cli/bin/<configuration>/net10.0/`, with `Ps5Backport.Cli.exe`. These are framework-dependent builds: recipients need a compatible .NET 10 runtime. Share the entire relevant output folder, including its `runtimes/` directory and the copied assets.

#### Windows x64 package with .NET included

From the source repository, run:

```powershell
pwsh -NoProfile -File scripts/package.ps1 -Configuration Release
# Debug can also be packaged:
pwsh -NoProfile -File scripts/package.ps1 -Configuration Debug
```

The script publishes both executables as self-contained Windows x64 applications into separate `Studio/` and `CLI/` folders under `work/packages/<configuration>/<run>/`. It includes .NET and the projects' managed/native runtime dependencies, rules, firmware catalogs, local fakelibs and the symbol database, and writes `PS5BackportStudio-<configuration>-win-x64.zip`. Recipients extract the archive and run the desktop EXE in `Studio/` or the CLI EXE in `CLI/`. The build machine needs the pinned SDK and access to any missing NuGet/runtime packages; recipients do not need the SDK or a separately installed .NET runtime.

No trimming or single-file bundling is enabled. Packages retain resolved NuGet metadata and bundled license/third-party notices under `licenses/`. Packages are not signed installers and do not include source, tests or engineering reports. Package generation copies files and leaves source assets unchanged. It does not upload or publish the archive.

#### Fakelib and symbol inputs and per-user files

The sharing package includes the local fakelib store's `.sprx`, `.prx`, `.psp` companion files and manifests, preserving firmware and variant subfolders. Publication staging/backup folders are excluded. It copies the maintained `data/aerolib.csv` to `data/aerolib.csv` beside each executable. Runtime symbol lookup and packaging do not depend on `.original/reference`. Both clients automatically discover these packaged inputs; Settings and CLI path overrides remain available. Other reference repository files are not bundled.

**Symbol catalog relocation — 10 October 2026.** The maintained catalog is `data/aerolib.csv`, copied byte for byte from `sce_symbols_new` at upstream commit `fe2cc2948e014f62c878a98c4dc7a53ecf4c6f75` ([zecoxao/sce_symbols](https://github.com/zecoxao/sce_symbols)). The 13,064,239-byte copy has SHA-256 `977e697acaa6637c75d0f16d282bbc343ee18be5643c7e77bda4969d39d284b4`. Build/publish inputs, default GUI/CLI lookup, tests and game-analysis profiles now use the maintained copy. Explicit user path overrides remain available. Future reference updates require deliberately refreshing this copy and validating the catalog; changing a reference checkout alone no longer changes Backport's symbols. This supersedes historical reference-path instructions below.

Validation: Debug and Release each build with zero warnings/errors and pass all **311 public tests**, with zero failures/skips. The two existing symbol-catalog checks now run publicly without reference fixtures. Runtime-asset checks verify unchanged catalog hashes in both executable outputs; the isolated portable CLI test resolves symbols from `data/aerolib.csv` outside the repository. A Release CLI publish also preserves the catalog hash under `data/aerolib.csv`. Both analysis-profile paths were resolved and verified. Catalog golden expectations were unchanged by this relocation.

**Verified name additions — 12 October 2026.** `data/aerolib.csv` is no longer a pure upstream copy. Its last 28 lines were added from a reference-repository sweep. Every identifier in `.original/reference` (AnyPS5 at `2a5eca11c`, sce_symbols `ps4_names.txt`) and `fakelib_source` was hashed with the NID algorithm, and a name was kept only when its NID is exported by a real firmware in `firmware_db` and was unnamed before. The new names cover libScePsml (`scePsmlMfsr2*` ×9, `scePsmlMfsrCreateContext1300`, `scePsmlMfsrGetDispatchMfsrPacket1300`), libkernel (`sceKernelGetAvailableCpumask`, `sceKernelSyncOnAddressWait8/16`), libSceLibcInternal (`_sceLibcInternalThreadAtexit`, `_sceLibcInternalThreadDtors`, `_sceLibcInternalForceTlsDestructor`, `__std_atomic_wait_direct_8`), ulobjmgr ×2, `sceVideoOutGetOutputStatusWithVrr`, `sceHttp2GetMaxConcurrentRequest` and libSceNpPartnerSubscription ×6. Each name agrees with the existing fix notes; for example, the `La9uyZv4Kvw` "CPU affinity mask (FW 12.40+)" stub is `sceKernelGetAvailableCpumask`. The catalog is now 13,065,514 bytes with SHA-256 `541fa85b1d59b5d48c725554b8ce8e6035d5d90873793fd3b7f45ccca87de000`. Five placeholder rule labels in `data/fixes.json` now use these names; NIDs and behaviour are unchanged. Refreshed goldens differ from a baseline recorded with the old catalog only by NID → name substitutions and report hashes. Patched binaries are byte-identical. All 647 tests pass with zero skips. When refreshing from upstream, re-append these lines (or drop any that upstream now carries). **14 October 2026:** the same sweep (AnyPS5 `30f961266`, firmware_db now including 7.00 and 13.60) added two more lines, both exported only by FW 13.60: `zCa75KLWWn4 EVP_MD_block_size` (libSceLibreSSl3) and `ZVZ++68nNI8 sceNpGetSandboxInfo` (libSceNpManager; AnyPS5 records the hash match). The catalog is now 30 lines over upstream, 13,065,578 bytes, SHA-256 `729738f08afd4765ffb8f06b774bc891e9c4e9b0ae35f9218d7a9a983913fdca`; golden `namedb.txt` 159140 → 159142.

Ordinary builds tolerate absent local fakelibs; the symbol catalog is included in the source tree under `data/`. Function-name lookup falls back to NIDs if the runtime symbol database is unavailable. The sharing script requires the symbol file and local fakelib binaries and stops if they are missing. Some backport options need a matching fakelib set; the included sets do not establish compatibility for every target.

Saved paths/window settings and diagnostic logs stay in the recipient's per-user application-data directory. They are not copied from the developer machine. The runtime package does not establish console compatibility for unverified fixes; retain the existing rule evidence and testing process.

---

<a id="verification"></a>
## Verification

Condensed on 14 October 2026 from the former `VERIFICATION.md` and the Phase 3–8 records. Per-run test counts are in [Engineering milestones](#engineering-milestones) and FINDINGS.md Part 2 §6.

### Requirements and commands

Install **.NET SDK 10.0.401** (pinned by `global.json`; changing it is a deliberate review step) and PowerShell 7. From the repository root:

```powershell
pwsh -NoProfile -File scripts/verify.ps1                         # Public lane: tracked data + synthetic tests, no private fixtures
pwsh -NoProfile -File scripts/verify.ps1 -Lane All               # Public + private golden/integration fixtures
pwsh -NoProfile -File scripts/verify.ps1 -Lane Private           # Private lane only, when maintaining the fixture corpus
pwsh -NoProfile -File scripts/verify.ps1 -Configuration Debug    # Faster iteration; final verification builds both
```

For day-to-day work, run only the affected test class: `dotnet test --solution Ps5Backport.slnx --filter-class Ps5Backport.Tests.<Class>`.

`verify.ps1` does a locked restore, builds Debug and Release, runs the lane, and fails on any warning, skipped test or empty result. It writes xUnit XML and `dotnet-info.txt` under ignored `work/verification/<timestamp>-<id>/`. It refuses `PS5BP_RECORD=1`, so it never regenerates goldens, and sets the .NET CLI, Microsoft Testing Platform and Avalonia build-telemetry opt-outs for its child processes only. Native/filesystem tests need normal temporary-file permissions; a restricted sandbox produces "Access to the path is denied" failures. No hosted CI is configured, and nothing is uploaded.

### Lanes, fixtures and goldens

- **Public** is every test without `[Trait("Lane", "Private")]`. Fixture-dependent classes or tests carry the trait; a class with private class fixtures or shared fixture helpers is private as a whole. Missing tracked `data/`, `firmware_db/` or text goldens fail rather than skip.
- **Private** needs the local, uncommitted `.original/`, the `work/*_elf` fixtures and the `fakelibs/` store. The script fails setup if the main roots are missing and sets `PS5BP_REQUIRE_PRIVATE=1`, so any other missing fixture fails too. A direct `dotnet test` outside the script can still skip missing private fixtures; that is not a strict private-lane result.
- **Goldens** live in `src/Ps5Backport.Tests/golden/`. After an intended output change, re-record with `PS5BP_RECORD=1 dotnet test --solution Ps5Backport.slnx` and review the diff.
- **Layout.** `src/Ps5Backport.Tests/Tests.cs` holds the 27 former test files as named regions. Newer features have their own files (`CrashLogTests.cs`, `OptionalAudioTests.cs`, `SceDynamicTableTests.cs`, …). Optional verification projects link the same sources: `BACKPORT_SYNTHETIC_ONLY` selects the synthetic fixtures, `BACKPORT_PARSER_TESTS_ONLY` those plus the generated parser tests.
- The 10 October redundancy review removed exactly two tests whose coverage was fully retained elsewhere (`FirmwareTests.SdkFor_KnownBackports`, `BackPorkPatchTests.AllPatches_AreInPlace`). `MutationWorker` and `InterruptionWorker` are child-process entry points for the parser-budget and recovery tests, not dead tests.

### Generated and malformed-input checks

- `GeneratedParserTests` (FsCheck.Xunit.v3 3.4.0): five properties × 200 cases with fixed replay seeds and sizes capped at 64. They cover firmware normalisation/BCD/range rejection, BPS literal patches and checksum corruption, and ELF/SELF round trips and truncation. When a property fails, keep the shrunk input as a deterministic regression; don't just change the seed.
- `ParserBudgetTests`: 512 deterministic mutations each of generated ELF, SELF and BPS seeds (bit flips, truncation, oversized fields, appended bytes; BPS checksums repaired where possible), run in child processes. Malformed input must produce `InvalidDataException`; any other exception fails with format, seed and case number.

| Budget | Limit |
| --- | --- |
| Input and decoded target size | 64 KiB |
| Thread allocation per case | 8 MiB |
| Worker corpus time | 10 s |
| Parent-enforced child lifetime | 30 s |
| Child managed heap | 256 MiB |

The heap limit is not a native-memory limit, and the small seeds are a regression corpus, not exhaustive fuzzing. Core exposes budgeted overloads `Self.Unwrap(bytes, maximumOutputBytes)` and `Bps.Apply(patch, source, maximumOutputBytes)` that check sizes before allocating; the existing overloads keep their size policy.

**Provider-ownership regression.** Excluding a module's own exports from the pooled provider set once also removed a NID exported by a different module, so cooking and library checks reported it missing. Cooking now counts distinct owners per NID, and a module cannot act as its own provider.

### Optional evaluation tools

All live under `verification/`, have their own lock files, write to ignored `work/`, and are run explicitly. Only `UiProfile` is in the solution (the headless tests need it built in the matching configuration).

| Tool | Command | What it does |
| --- | --- | --- |
| Mutation testing | `scripts/evaluate-mutations.ps1` | dotnet-stryker 5.0.0 on `Firmware.cs` only (public test subset, 2 workers, 180 s limit). Last result: 20 killed, 3 survived (redundant input checks), 0 uncovered. Not a whole-engine score. |
| Performance | `scripts/measure-performance.ps1 [-IncludePrivate] [-BaselineDirectory DIR]` | Analyze/Cook timing and memory on synthetic (and optionally private) games; with a baseline it runs both engines in the same harness and rejects payload/report hash mismatches. See [Performance](#performance). |
| Benchmark | `dotnet run --project verification/Benchmarks/Ps5Backport.Benchmarks.csproj -c Release -- --filter '*FirmwareMetadata*'` | BenchmarkDotNet 0.15.8, firmware-metadata loading only. |
| UI memory | `scripts/measure-ui-memory.ps1 -Game DIR` | Fresh headless Skia processes after a Release app build; 120 s child timeout; aggregate process memory only. |
| UI filter profile | `dotnet verification/UiProfile/bin/Release/net10.0/UiProfile.dll OUT.json` | 10,000-row import filter profile. |
| Decoder | `scripts/evaluate-decoder.ps1 [-IncludePrivate]` | Iced 1.21.0 comparison against the reference scanner and emitted code. See [Instruction decoder](#instruction-decoder). |
| CLI/catalog/schema | `scripts/evaluate-phase8.ps1 [-Python PATH]` | System.CommandLine 2.0.12 and SQLite/JSON-schema comparisons; needs an existing Python with jsonschema 4.26.0. See [CLI, catalog and schema decisions](#cli-catalog-schema-decisions). |
| Game analysis | `scripts/analyze-game.ps1 -Profile verification/GameAnalysis/profiles/<ID>.json [-Output DIR]` | Standardised import analysis per game profile; `-SelfTest` runs its synthetic checks. See `verification/GameAnalysis/README.md`. |

### Runtime assets and packages

`build/RuntimeAssets.targets` (imported by App and CLI) copies `data/**/*.json`, `firmware_db/**/*.json`, `data/aerolib.csv`, `DISTRIBUTION.md` and the local fakelib `.sprx`/`.prx`/`.psp`/`manifest.json` files (excluding `.ps5bp-*` folders) beside both executables on build and publish. Public tests compare the copied assets with the sources and launch a copied CLI from an unrelated directory to check automatic lookup. `scripts/package.ps1` was smoke-tested on 10 October by extracting each package outside the repo: every JSON, fakelib and symbol file matched its source hash, and both clients ran. That did not test a machine without .NET, native desktop interaction or console behaviour. See [Build and sharing](#build-and-sharing).

---

<a id="operation-lifecycle"></a>
## Operation lifecycle

From the former `OPERATION_LIFECYCLE.md` (Phase 4, 10 October 2026).

The shared engine now captures each request independently, identifies terminal outcomes explicitly, and binds a successful analysis to the inputs and settings that produced it. Transformation order and legacy byte/report formats remain unchanged. No new framework or runtime dependency was added.

#### Ownership and states

`BackportOperation` is a one-shot owner of the captured request and cancellation source. Its transitions are `Pending → Running → Completed | Failed | Cancelled`. A second execution is refused. Exceptions retain their original types; module errors return a failed result with diagnostics. Cancellation/disposal follows `Cancel → await ExecuteAsync → Dispose`; disposal while running is refused.

`RunSnapshot` normalizes paths, detaches option/NID collections, and deeply copies the loaded rules, including nested instruction, stub and pattern bytes. The name database has no mutation API. The captured rules are private to the operation. UI settings can be edited while work runs without changing that operation.

`GameResult.State` and `GameResult.Analysis` are runtime metadata, excluded from the legacy text/JSON reports. Completed means the engine finished without module errors; unresolved imports may still remain. A successful analysis writes nothing. A successful cook additionally requires `Published == true`, which is set after transactional commit.

The UI reserves 100% for completed work. Failed and cancelled work finishes at 0%, and queued progress from a finished or superseded operation is ignored. Progress remains a stage/module indicator rather than an estimate of elapsed time.

#### Analyze and Cook

Successful full analysis returns an in-memory `AnalysisReceipt`. Its SHA-256 fingerprint includes source identity, effective settings and paths, loaded names/rules, selected library source, and the relevant input file hashes/absence records. Modules, reference lists, knowledge documents, param metadata, option data files, library inventories and app0 files are guarded and rechecked. AMPR index presence is included because it affects warnings. Rules edited on disk require reloading the knowledge instance.

Cook repeats preflight and checks an existing receipt before acquiring the output transaction or creating staging files. Changed inputs/settings are refused with an instruction to analyze again. The UI retains the receipt for the current game; opening another game or starting another analysis clears it. Cooking directly without prior analysis remains supported.

**No transformed-byte cache was introduced.** Analyze still executes the complete dry-run transformation; Cook performs the complete transformation again against guarded current inputs. A receipt authorizes reuse of the analysis context only. Moving the output destination does not invalidate analysis. Receipts are process-local and are not a persistent cache, signature, or security boundary.

Analysis now hashes/guards inputs as cooking does. That adds I/O and temporarily prevents cooperative writers from replacing relevant files on Windows. Inventory changes are rejected. Phase 5 measured these costs; see [Performance](#performance).

#### Pipeline boundaries

| Source | Responsibility |
| --- | --- |
| `BackportOperation.cs` | Captured request, receipt fingerprint, cancellation ownership, terminal state |
| `Pipeline.cs` | Ordered orchestration and remaining data/app0 processing |
| `Pipeline.Discovery.cs` | Source/library selection and ordered overrides |
| `Pipeline.Transformation.cs` | Loading, provider ownership and module transformation through a concrete `PatchContext` |
| `Pipeline.Publication.cs` | Report staging, incomplete diagnostics, evidence verification and commit |
| `Pipeline.Reports.cs` | Existing text/JSON formatting and report writes |
| `RunEvidence.cs`, `Publication.cs` | Existing input guards and filesystem transaction/recovery |

Both CLI and UI execute `Pipeline.Execute`; the public synchronous `Pipeline.Backport` remains available. No service container, pipeline framework, new interfaces, parallel transformation, or change in patch/provider order was introduced.

#### Cancellation and closing

Directory/module discovery checks cancellation between files/directories. Pattern replacement scans windows of at most 1 MiB of candidate starts, retaining overlap needed to find boundary-spanning matches. Dense-match loops and code-reference scans check every 64 KiB of progress. Transformation also checks at safe module/fix/pattern boundaries. Existing overloads remain and forward to the cancellation-aware implementations.

AMPR index staging/traversal now accepts cancellation and uses the same pre-commit cancellation rule. Closing the window blocks new workers, requests cancellation of backport/index work, awaits their cleanup, and waits for already-owned firmware/store/tool workers. Conversion stops before the next file; an in-flight conversion finishes its atomic write. The window then saves settings and closes. It does not kill a worker mid-write.

Cancellation is cooperative. Synchronous reads, hashes, signing, individual filesystem calls and some per-fix/parser work can still delay it. Publication deliberately defers cancellation once the protected rename/commit sequence begins, (see [Publication and recovery](#publication-and-recovery)). A publication that commits during a late cancel remains completed. The window can therefore remain open briefly while cleanup/commit or an existing page worker finishes.

#### Measurements and coverage

On 64 MiB synthetic inputs, cancellation inside a dense one-byte replacement scan or a code-reference scan was observed within 0.05–1.5 ms, and full-scan times stayed within run-to-run noise (about 610–650 ms and 77–90 ms). This is no whole-operation cancellation guarantee.

Synthetic regressions cover one-shot execution, completed/failed/cancelled outcomes, cancellation before start and during patching, refusal to dispose while running, detached options and rule bytes, stale module/reference/metadata/settings/rules/names/library inputs, cooking from a current analysis, legacy report shape, boundary matches, bounded scan cancellation, delayed UI progress, close while busy, and stale UI settings.

---

<a id="publication-and-recovery"></a>
## Publication and recovery

From the former `PUBLICATION_RECOVERY.md` (Phase 2, 10 October 2026). Applies to whole-game cooking, fakelib batch imports (including variants), and AMPR index updates. Analysis remains write-free. Existing standalone binary/file tools are outside this directory-publication protocol.

#### What completion means

A cooked result is published only after all selected module/data/param/app0 operations and both legacy reports finish without a module error, relevant input fingerprints still match, and staged output passes inventory/hash verification. `GameResult.Published` becomes true after the commit record is written. A failed module produces reports in a retained `.incomplete` sibling directory; it does not replace the active output or receive a completion marker. The UI labels this incomplete and the CLI returns its existing error exit code.

Unresolved imports, lower-bound references, known-build warnings, and other existing warnings remain report findings. They do not prevent publication. A completion marker proves the file operation completed, not that the game works on a console or that unresolved imports are safe.

Each published directory has `backport_publication.json` version 1: a transaction ID and exact relative-file inventory with byte lengths and SHA-256 hashes. The marker excludes itself. `Publication.Verify(directory)` rejects changed, missing, or additional files. SHA-256 here detects accidental changes; the unsigned marker is not an authenticity mechanism. Legacy report formatting and transformed binary bytes remain unchanged.

Cooking also writes `backport_run.json` version 1. It records engine informational version/revision, runtime, title/content version, effective transform options, explicit selected NIDs/options, exact-versus-lower-bound reference status, reference NID-set hash, name-map hash, source module inventory, and relevant input lengths/hashes. Inputs include the loaded knowledge documents, top-level JSON/C firmware references, selected library inventories/manifests, library app0 files, source modules, param.json, and pattern/app0-related source files. Absence is recorded where it influences the result. The new evidence sidecar uses category labels and relative paths rather than private absolute paths. Existing legacy reports and library metadata retain their existing path/attribution fields; published output is not automatically anonymized.

Relevant inputs are guarded with read-only file handles during cooking and rechecked before publication. Inventory additions/removals are also checked. A loaded knowledge object whose disk files have changed must be reloaded. This deliberately costs file reads, hashing, and open handles; it does not hash every unrelated game asset.

#### Filesystem policy

- Stage and backup are unique siblings on the destination volume. Cooking starts from an empty stage, so files from an older output cannot leak into a new result.
- Existing parent links/junctions/reparse points and link entries inside relevant input/destination trees are refused. The source and output trees must be disjoint in both directions. Output cannot replace knowledge/reference directories or their ancestors.
- On Windows, directory-handle identity resolves normalized volume paths for overlap checks and writer-lock keys. Tests cover extended paths, short paths, SUBST drive aliases, junction parents/children, and output files hard-linked to an input. Hard-linked output children are copied into independent stage files before changes; source bytes remain intact.
- An exclusive stable `.lock` file serializes writers to the same physical destination. Fakelib imports additionally hold a store-wide lock, so imports to different firmware sets cannot race. Locks fail promptly rather than waiting; retry after the other operation ends. Readers are not given a globally atomic snapshot.
- A fakelib variant import stages and replaces the entire containing firmware set. Variant and parent completion records remain consistent. Batch files and their manifest publish together.
- Windows identity behavior was tested on local NTFS. Non-Windows overlap checks use normalized paths plus link refusal. Network shares, case-sensitive Windows directory modes, cloud-synchronized folders, other filesystems, and hostile concurrent filesystem mutation are not certified by these checks.

The implementation rechecks paths and inputs, but it is not a defense against an adversarial process swapping paths between checks and operations. Use local directories under the user's control, and do not edit inputs during a run. File sharing restrictions are strongest on Windows; metadata/content rechecks also detect ordinary changes.

#### Replacement and recovery

Sibling control names start `.ps5bp-` and contain a hash of physical destination identity. Transaction stage/backup names additionally contain a random ID. The journal stores a version, ID, whether a destination existed, and whether the transaction committed; recovery derives sibling paths from these validated values rather than trusting paths supplied in JSON.

| Persisted state | Recovery behavior |
| --- | --- |
| Journal created; stage partially written | Remove that transaction's validated stage; leave previous output intact. |
| Prior output moved to backup | Restore backup; discard incomplete stage/new output belonging to that transaction. |
| Stage moved into place; commit record not written | Roll back to backup, or remove the uncommitted new output if no prior output existed. |
| Commit record written | Verify and retain the new output; retain nonempty prior backup; remove the journal when possible. |
| Invalid journal, mismatched new-output identity, or invalid committed output | Stop for manual inspection; retain recovery evidence/backup rather than guessing or deleting unrelated files. |

Files and the completion marker are flushed before replacement. Cancellation is honored before replacement begins. The short critical section moves old output to backup, moves stage to output, and writes the committed journal. Cancellation is deferred during these steps so rollback/recovery has a definite state. This is a recoverable protocol, not one atomic rename of multiple directories: during interruption the active path may be temporarily absent or contain an uncommitted result until recovery runs.

Each next writer recovers its target under the lock before staging. Cooking and AMPR index creation also recover before checking/using the active output. The Core API exposes `Publication.Recover(destination)` for explicit recovery, and recovery is idempotent. A resumed fakelib import recovers the containing firmware set.

After an interrupted run, stop other writers and invoke recovery for the same destination, or rerun the operation that writes it. For manual inspection, identify the destination's journal and its referenced ID, preserve a copy of the journal and backup, and inspect the completion record before changing anything. An invalid journal or marker should be resolved manually; do not delete all `.ps5bp-*` files as a shortcut.

AMPR indexing uses the same staged replacement. It verifies an existing completion marker, checks cooked source fingerprints/module inventory, compares source path/size/mtime layout before and after indexing, and records that layout hash in the run sidecar. Completion/run sidecars are excluded from the AMPR file index. Legacy overlays without provenance can still be indexed; their source identity cannot be established retrospectively. Layout checks are not a content snapshot of every unrelated game asset.

#### Retention and space

The prior output is moved to a `.backup` sibling during the swap and deleted once the replacement is committed (recovery of a committed-but-interrupted run deletes it too). Deletion is best-effort: if a file is locked, the `.backup` stays, Core returns its path in `GameResult.PreviousOutputBackup` and the UI logs a warning. Incomplete module runs are retained as `.incomplete` siblings; the UI logs that diagnostic path. Store/index replacements discard their previous directories the same way.

There is no automatic age/count eviction: avoid silently deleting a user's recovery copy. After verifying and accepting the current output, the owner can remove a specifically identified old backup or diagnostic folder. Validate its exact path and ensure no operation is using it first. Stable lock files stay in place, including while unlocked; deleting them could let concurrent processes acquire different lock objects. Committed cleanup that fails is retried by recovery. Journal write temporaries left by an abrupt process exit may require manual inspection/cleanup.

Budget space for the current destination plus a full staged replacement and all retained backups. Variant imports and AMPR updates copy the whole set/overlay. Hashing and flushing add I/O. This favors correctness and recovery over minimal disk usage. Transaction artifacts are Git-ignored.

#### What verification demonstrates

Synthetic regressions exercise injected disk-full/write/copy/access failures, a real file-sharing denial, cancellation, competing writers in one and two processes, stale-output replacement, input inventory changes, loaded-knowledge changes, output tampering, malformed journals, batch/variant rollback, and Windows physical aliases/links. Child processes deliberately exit without Dispose/finally at stage creation, backup rename, new-output rename, and committed-journal checkpoints, for existing and new destinations. Recovery is then run twice and verified.

The protocol was tested with process interruption and simulated failure classes on this Windows/NTFS machine. It was not tested by filling a physical disk, cutting machine power, or forcing storage-controller failures. File flushes and recovery journals do not establish directory-entry durability under every power-loss/filesystem configuration. Physical power-loss guarantees require a dedicated hardware/storage test campaign; none are claimed here.

---

<a id="diagnostics-and-ui"></a>
## Diagnostics and UI

From the former `LOCAL_DIAGNOSTICS.md` (Phase 6, 10 October 2026).

### Behaviour

Desktop and CLI backports use the same optional Core diagnostic sink. JSON-lines events carry a UTC timestamp, operation, run ID, stage/module, measured duration, terminal state, error category and exception type/context. A pre-cancelled operation still records its cancellation. Module failures keep their exception context even when the engine returns a failed result instead of throwing. AMPR index runs have their own correlation IDs, and desktop activity, errors and shutdown are recorded too.

Stage durations are intervals between progress boundaries, including orchestration overhead, not per-function CPU time. A snapshot that fails before an engine run exists is logged without a run ID. Ordinary non-backport CLI commands log caught failures, not a full timeline.

The close policy has one implementation, shared by production and the headless checks: cancel and await owned workers, save settings, record closure, then release the log. The opening record includes logical window size, render scale and screen count.

### Retention and privacy

| Storage | Policy |
| --- | --- |
| UI history | Latest 2,000 entries; message/module strings limited to 2,048/1,024 characters plus a truncation marker |
| Error/warning counts | Counts of retained history; filtering does not change them |
| Desktop files | `%LOCALAPPDATA%/PS5BackportStudio/logs/desktop/` on Windows |
| CLI files | `%LOCALAPPDATA%/PS5BackportStudio/logs/cli/` on Windows |
| Event files per client | `events.jsonl`, then `events.1.jsonl` through `events.3.jsonl`, at most 1 MiB each |
| Writer lease | Empty `writer.lock`, exclusively held until sink disposal |

That is at most 4 MiB per client. Rotation drops the oldest generation, and unrelated files are never removed. Records are complete JSON lines with escaped newlines; file messages are limited to 2,048 characters and exception context to 4,096.

Clear empties the UI history and counts but leaves the disk logs; Copy exports only the visible rows. Nothing is uploaded. Logs can contain game names, absolute paths and exception messages, so review them before sharing. They are not encrypted or redacted.

Writes are synchronous and close the stream after each record, so records survive an ordinary close; this is no power-loss guarantee. A storage or access failure disables the sink with a warning without failing the backport. A second instance of the same client runs on with a warning and no file log; desktop and CLI have separate leases.

### Filtering

Import and log filters replace the visible collection in one batch. A selected import stays selected while visible; otherwise the inspector clears. Name/library searches are case-insensitive and NID searches case-sensitive, and queries are trimmed. On 10,000 rows a full-list update went from 10,001 collection notifications to 1, from about 2.7 MB to 82 KB of UI-thread allocation, and from 16–17 ms to 0.7–1.5 ms. That is a focused view-model measurement, not a frame-rate claim. No debounce or background filtering was added.

### Test coverage and native checks

- **Diagnostics tests (7):** rotation, competing writers and lease release, file failure without operation failure, run/stage/module/error correlation, pre-cancellation, bounded error strings surviving Clear, and 2,400-entry UI eviction and filter batching.
- **Headless scenarios (5)** in isolated `verification/UiProfile` processes (30 s timeout), using the real MainWindow bindings and close policy: busy commands and keyboard cancel, close while analysing, search/selection/inspector/accessible name, stale-analysis rejection, and settings-save failure on close. The worker exits after its checks because headless renderer disposal blocked, so renderer disposal is not tested.
- **Native Windows checks (10 October):** 1280 × 820 at 100% scale rendered correctly. The accessibility tree exposes readable navigation, command and option names. Tab reaches the import search, and Escape cancels the folder picker; focus then returns to the window, not the invoking button. Normal and maximised layouts work, including moving to and maximising on the second display. Analyze disables Open/Analyze/Cook and exposes Cancel.
- **Not verified:** minimum-size layout, native close during a long Analyze, 125%/150% and mixed-DPI transitions, full screen-reader review, and GPU/native allocation profiling.

---

<a id="unity-unset-sku"></a>
## Unity unset-SKU option

Former source: `UNITY_SKU_OFFLINE.md`. Historical results retain their original scope.

### Unity unset-SKU compatibility option

Backport Studio's **Unity unset-SKU offline fallback** option (`unity_sku_offline`) handles a recognized Unity/Esut authentication pattern that treats a successful platform request returning `NotSet` as a fatal unknown error. It is opt-in and requires Known fixes to be enabled. The user confirmed the guarded candidate works on The Rogue Prince of Persia 01.006.000, PPSA28155, on the main account with Internet enabled. Save/relaunch verification and other titles remain separate console checks.

#### Use

Restart the updated Studio, enable **Known fixes** and **Unity unset-SKU offline fallback** in Backport options, then cook from the original dump. The CLI equivalent is `--option unity_sku_offline` on the `backport` command. No diagnostic logger is added by this option.

When applied, the module's pattern report includes **Unity unset SKU: existing offline fallback**, count 1. If that entry is absent, the detector did not recognize a supported, unambiguous layout and left the code unchanged. A recognized module without usable code padding fails that module rather than emitting an incomplete patch; the normal Extend code segment option can supply verified padding.

#### Scope

Detection uses executable code patterns with relocation displacements masked, not a title ID, module filename, fixed address or whole-file hash. It requires one SKU dispatch (Trial=1, Full=3), the linked default failure branch, shared readable error-class slots, matching class initialization, and one linked consumer whose offline-support and TitleScreen branches reach the same existing continuation. The compiler's recognized register and field layout must match; differently compiled games are skipped until independently analyzed.

The guarded trampoline keeps the original error by default and changes only SKU zero (`NotSet`) to the recoverable availability error already recognized by that consumer. The task still fails; the platform SKU, trial/full paths, native request failures and other SKU values retain their behavior. This is not a global entitlement API stub. Online-service warnings may remain. No claim of compatibility with every Unity game or firmware is made.

#### Verification

- Eleven focused regression cases cover relocated synthetic ELF code, ambiguous/incomplete recognition, mismatched class/enum/continuation, executable type slots, cancellation, no padding, repeat application, option gating and pipeline reporting.
- The detector recognized the actual baseline Rogue module without supplied addresses. Independent Capstone comparison confirmed it emits the same guarded instructions and continuation as the user-tested candidate, without diagnostic logger hooks.
- All 406 project tests passed. The Release solution build completed with zero errors and zero warnings.

Updated desktop output: `src/Ps5Backport.App/bin/Release/net10.0/Ps5BackportStudio.exe`. Keep its accompanying runtime files and data folders together, as described in DISTRIBUTION.md. These changes do not modify source game folders or saves.

---

<a id="ubisoft-offline-popups"></a>
## Ubisoft offline popup option

Former source: `UBISOFT_OFFLINE_POPUPS.md`. Historical results retain their original scope.

### Ubisoft offline popup compatibility option

Backport Studio's **Ubisoft offline popups** option (`ubisoft_offline_popups`) suppresses MUFFIN-4520 and MUFFIN-4521 in a recognized Unity/Esut Ubisoft library layout. It requires **Known fixes** and is disabled by default. The user confirmed the same two severity edits remove both messages in The Rogue Prince of Persia 01.006.000, PPSA28155.

#### Use

Restart the updated Studio, enable **Known fixes** and **Ubisoft offline popups** in Backport options, then cook from the original dump. The CLI equivalent is `--option ubisoft_offline_popups` on the `backport` command. The unset-SKU fallback is a separate option; select it too when the game needs that startup fix.

The module report contains **Ubisoft offline popups: existing silent callbacks**, count 2, when both definitions are changed. Count 1 means one definition was already silent. Missing or ambiguous recognition leaves the module unchanged; no report entry is added when nothing changes. Repeat application changes nothing.

#### Recognition and behavior

The detector scans executable segments without requiring a game ID, module filename, fixed address or whole-file hash. It requires:

- One adjacent pair of error definitions for IDs 4520 and 4521, with the verified object field layout and Standard (0) or SilentError (2) severities.
- Matching allocation calls, object class slots, error-list class slots, write barriers and consecutive static storage fields; class slots must be readable data and call targets must be executable.
- One standalone and one popup handler that both resolve errors through the same lookup and class initialization, test severity 2, and invoke the existing completion callbacks with the recognized control flow.

Only the two severity bytes are changed to the library's existing SilentError value. Authentication results, saves and callback code are unchanged. No trace logger is added. Differently compiled or non-Unity Ubisoft games are skipped until their layouts are independently analyzed. Recognition on another game does not establish console compatibility; the confirmed runtime result currently covers Rogue Prince of Persia.

This option suppresses warnings about unavailable services. It does not make online services available or fix the separately investigated progress reset.

#### Verification

The detector recognizes the actual Rogue baseline ELF without supplied addresses and changes exactly the same two bytes as the console-confirmed candidate. Tests cover relocated code, repeat application, one already-silent warning, mismatched/ambiguous definitions and handlers, broken callback code, wrong call targets/storage fields, data-only lookalikes, cancellation, option gating and report counts.

All 428 project tests passed, including the 22 focused popup cases. The Release solution build completed with zero warnings and zero errors. The full suite was run outside the sandbox because its existing Windows hard-link, junction and drive-alias tests require those filesystem operations; test fixtures stayed under the workspace's temporary test directory.

Updated desktop output: `src/Ps5Backport.App/bin/Release/net10.0/Ps5BackportStudio.exe`. Keep its accompanying files together, as described in DISTRIBUTION.md.

---

<a id="tester-checklist"></a>
## Console tester checklist

Former source: `TESTER_CHECKLIST.md`. Historical results retain their original scope.

This is a preserved manual checklist for the previously selected console setup, not a newly verified compatibility/version recommendation. Exact filename case must be checked during merging/transfer, as the Crow Country findings demonstrate. Collecting a save icon is insufficient: confirm actual checkpoint progress after relaunch.

*PS5 BACKPORT — TESTER CHECKLIST*

Thank you for testing. Please follow every step and send the completed report, even if the game does not start. A failed test with logs is valuable.

*BEFORE YOU START*

• Use the *exact game title ID and version* named in the release post.
• You need a jailbroken PS5 with *ShadowMountPlus* and kstuff-lite. Do *not* run the standalone BackPork payload too — it conflicts with ShadowMountPlus.
• Turn ShadowMountPlus debug logging on. It is normally already on. Only change `/data/shadowmount/config.ini` if it contains `debug=0`; set it to `debug=1`.
• You need FTP access to copy the game and collect logs.

*INSTALL*

1. On your PC, open the decrypted game dump for the exact title and version.

2. Copy the *contents* of the backport release folder over the game folder. Choose *overwrite/replace* when asked.

3. Handle `ampr_emu.index` after merging:

   • *The release includes it:* keep it. It belongs to this exact game dump and this exact backport build, and replaces any old index from the dump.

   • *The release does not include it:* delete any `ampr_emu.index`, `ampr_emu.index.tmp`, and `ampr_emu.index.bak` from the merged game folder. AMPR will make a fresh index on the first launch.

   Never copy an index from another game version or another backport build.

4. Copy the complete merged game folder to a ShadowMountPlus scan folder, for example:
   `/data/homebrew/<GAME>/`

5. Keep the supplied libraries fixed during the test. In `/data/shadowmount/config.ini`, use `backport_fakelib=1`, `auto_update_ampr=0` and `update_emulators=0`. Add `global_fakelib_exclude=<TITLE_ID>` for the tested title so global libraries cannot replace the supplied set. On current ShadowMountPlus, select **Full** for the game's fakelib mode in the web interface, or set `fakelib_mode=<TITLE_ID>:full`; **Emulators only** excludes Agc/AgcDriver/Psml replacements. Replace `<TITLE_ID>` with the real title ID. These settings apply on the next launch. Record the settings in the report. ShadowMountPlus prefers `fakelib2` over `fakelib`; if both exist, confirm which directory is selected before testing.

6. Start the game. If it does not start or shows an error, stop and collect the logs below before trying anything else.

*TEST*

Please test as much as you can:

1. *Boot* — reaches the main menu.
2. *New save* — create a save or change and save a setting; no error appears.
3. *Save and reload* — make a second save, return to the system menu, restart, then load it.
4. *Sound: TV* — music and effects play.
5. *Sound: headphones* — controller jack or headset audio works.
6. *Play for 10+ minutes* — menus, loading, and normal offline play; no freeze or crash.
7. *Rest mode (optional)* — enter rest mode while in-game, wake the console, and continue playing.

Tests 2–5 are especially important.

If the game crashes after playing for a while, collect the logs and record how long it ran before changing settings. Current ShadowMountPlus has removed automatic kstuff pausing and the `kstuff_no_pause` setting, so adding that key is not a retry procedure for the updated loader. Its detailed launch-error monitoring lasts up to 60 seconds; also record the console's error code for later crashes.

*COLLECT THESE FILES*

Copy them immediately after testing, before launching anything else. If the system crashes the game, first copy the entire crash folder inside `/devlog/system/sce_coredumps.0/` to your PC, including all files and subfolders. Move quickly: the system will delete it.

• `/data/shadowmount/debug.log`
• `/data/shadowmount/debug.log.1` — send this too if the console crashed/rebooted and you jailbroke again.
• `ampr_emu.log`, if it exists in the game folder.
• The entire crash folder from `/devlog/system/sce_coredumps.0/`, if the system crashes the game — copy it immediately before the system deletes it.
• Kernel logs from the test, including the launch and crash (if any).
• The on-screen crash code, for example `CE-xxxxxx-x`; a photo or video is welcome.

*ARCHIVE AND SEND*

Put the completed report, all logs, photos/videos, and the exact backport archive (or its checksum) into one archive. Send it to the dedicated Telegram channel for *this game*. Do not send it to another game's channel.

*REPORT — COPY, FILL IN, AND SEND*

```
Game / title ID / version:
Backport folder name:
Console: PS5 / Slim / Pro — disc or digital:
Console firmware:
Jailbreak / kstuff-lite / ShadowMountPlus versions:
AMPR index: supplied with release / rebuilt on console / no index found
Game folder copied to: /data/homebrew/
Fakelib mode / selected folder: Full / fakelib or fakelib2
auto_update_ampr / update_emulators: 0 / 0
Global fakelib excluded for this title: yes / no

1. Boot:               pass / fail / not tested
2. New save:           pass / fail / not tested
3. Save + reload:      pass / fail / not tested
4. Sound, TV:          pass / fail / not tested
5. Sound, headphones:  pass / fail / not tested
6. 10+ min play:       pass / fail / not tested   (minutes played: )
7. Rest mode:          pass / fail / not tested

Crash code (if any):
What happened:
Time from launch to error / crash:
Files attached: debug.log, debug.log.1, ampr_emu.log, crash folder from sce_coredumps.0, kernel logs, photo/video
Archive sent to this game's Telegram channel: yes / no
```

---

<a id="ppsa03671-and-crow-country"></a>
## PPSA03671 and Crow Country

Former source: `PPSA03671_ANALYSIS.md`. Historical results retain their original scope.

### Backport cooking investigation — 2026-10-10

#### Confirmed Studio defect: import library left unchanged

An import is `NID#library-id#module-id`, not just its first eleven characters.
Studio's SyncOnAddress adapters previously replaced the function NID with
`04AjkP0jO9U` (`_umtx_op`) while retaining the original library/module suffix.

Crow Country's published `Media/Modules/PS5Util.prx` consequently requested
`04AjkP0jO9U#B#B` from `libkernel_sync_on_address`. The decrypted firmware-4.00
`libkernel.sprx` exports `_umtx_op` from **libkernel**, not that sublibrary.
This is a concrete unresolved-import defect consistent with the reported
`PRX_NOT_RESOLVED_FUNCTION` crash. The partial dump does not identify the failing
NID, so a console rerun is still necessary to establish whether this fixes the
observed crash completely. Absence of `ampr_emu.log` does not identify the import.

The four SyncOnAddress rules now declare `new_library: libkernel`. RenameImport
reuses the target library/module suffix already imported by the module. Missing,
ambiguous, or oversized target suffixes fail rather than silently emitting an
incorrect import. Existing same-library renames retain their original suffix.
Reports name the target library when a fix crosses libraries.

The audit examined the 18 title directories under `.original/backports` and
checked 138 imports covered by these four rules. Two required a library change:
the original Silksong (`PPSA12544`) PS5Util Wait/Wake imports. All 138 accepted
the corrected rename. Four files could not be examined because their payloads
were non-ELF, truncated, or encrypted/compressed; see
`work/import-library-audit/audit.txt` for the exact paths. These files do not
establish either success or failure of their containing backports.

A fresh Crow Country diagnostic overlay was cooked from
`D:\PS5\PPSA19068-app0_Crow_Country` into
`work/crow-fixed-library/PPSA19068_backport_4.00` with known fixes, text extension,
lowered param versions, and AMPR logging enabled. All 13 module results have
empty error fields. Both corrected PS5Util imports now request `_umtx_op` from
libkernel. This directory contains an overlay of cooked/replacement files, not
the complete game's assets. Its publication manifest records file hashes.

#### Crow Country follow-up: missing Unity module caused a second crash

The completed `1791629006` dump identifies a subsequent SIGSEGV at address zero.
Its TTY reports ENOENT for `/app0/Media/Modules/Il2CppUserAssemblies.prx`; the
source dump and previous cooked output named it `Il2cppUserAssemblies.prx`.
The raw stack exposes the failed call to `il2cpp_set_find_plugin_callback`.

Studio now resolves file capitalization generally, without title, engine, or
filename constants. Explicit null-terminated ASCII `/app0/` paths, relative
paths, and `%s/` suffix paths in game binaries are matched to the source inventory
without regard to case. A unique, consistent reference establishes the output
spelling; absent references preserve source spelling, and ambiguous/conflicting
references produce warnings instead of guessed renames. Directory corrections
apply to sibling files consistently. Analyze and Cook report corrections for
modules and data files, and patched metadata/data use the same output names.
Rename-only files keep their bytes; the source dump and source evidence paths
remain unchanged. Publication records the exact output capitalization. Runtime
paths assembled dynamically or stored outside the inspected game binaries are
not inferred. See `FilePathCaseTests.cs` and
`work/crow-diagnostics/completed-crash-findings.md` for the regression and dump
evidence. On 2026-10-10 the user confirmed that renaming the module on the console
to `Il2CppUserAssemblies.prx` allows the game to boot. This validates the startup
diagnosis; extended gameplay has not been verified.

#### PPSA03671: default versus 4xx+

The user's compatibility evidence is: `default` works on 13.xx and higher;
`4xx+` is the supplied firmware-4 backport. The sample's param.json identifies
Marvel's Wolverine, version 01.001.005, with SDK/system-version metadata from
13.xx. Static analysis does not independently certify those runtime results.

| Static eboot count | default | supplied 4xx+ | Studio dry run |
| --- | ---: | ---: | ---: |
| Unresolved function imports against FW 4.00 | 169 | 153 | 150 |
| Unresolved function imports against FW 12.70 | 147 | 147 | — |

147 imports belong to `libSceNpCppWebApi`, whose provider is absent from this
sample tree. The fact that the working baseline has these same unresolved
imports shows why fixture-only counts cannot prove a broken backport. Supply
the complete dependency tree before drawing conclusions about those imports.

Studio's three remaining FW-4 imports outside that dependency are:

| NID | Import library | Name/evidence |
| --- | --- | --- |
| zBsgF0q8DIM | libScePad | No name in the loaded symbol list; introduced in the 7.01 reference |
| 2mfSRGdshtk | libSceSaveData_native | sceSaveDataConvert; introduced in the 8.00 reference |
| 8EA5OMIL1lQ | libSceSaveData_native | sceSaveDataGetConvertProgress; introduced in the 8.00 reference |

These require behavior/call-path investigation, not unconditional success
stubs. Fewer unresolved imports does not prove equivalent runtime behavior.

The supplied eboot keeps the unwrapped length at 192,061,368 bytes but differs
in 52 byte ranges (407 bytes total). Changes include SDK lowering, function
stubs, custom code redirects, and import rewriting; its machine-code changes
are not interchangeable with metadata lowering alone. See the comparison
records in `work/game-analysis/PPSA03671-library-fix/analysis.json`.

One supplied change is particularly useful evidence for the library fix:

```
default: 86f0wFtxn0k#u#s    library libc
4xx+:   04AjkP0jO9U#v#G    library libkernel
```

The original NID is `__std_init_once_complete_clr`, named by aerolib.csv.
Both variants' bundled libc export it, and its implementation bytes match.
The implementation stores the initialization state, wakes waiters, and returns
one. The supplied eboot bypasses it with a custom umtx adapter. A new global
NID-only swap would discard necessary state/argument handling and is not
justified by this sample. Studio already patches the bundled libc's known
SyncOnAddress dependencies rather than copying that eboot-specific redirect.

#### Reproduction and checks

`verification/GameAnalysis/profiles/PPSA03671.json` captures this sample's
baseline, variants, reference paths, and firmware matrix. Reproduce with:

```powershell
pwsh -NoProfile -File scripts/analyze-game.ps1 -Profile verification/GameAnalysis/profiles/PPSA03671.json
```

Public regressions cover the cross-library rename, the old NID-only failure
mode, absent/ambiguous targets, suffix capacity, and loading the explicit
target-library rules. Private regressions verify the supplied PPSA03671 suffix
change and execute the actual Silksong PS5Util adapter chains with Studio's
default text-extension setting, checking their terminal GOT targets and import
libraries. Existing golden fixtures are retained unchanged.

Final verification: Debug and Release solution builds succeeded with zero
warnings/errors. After the general capitalization correction, the full Release
suite passed 395 tests with no failures or skips. The fresh Crow Country cook
publishes the corrected name and all 16 publication hashes verify. Regressions
cover arbitrary engines/file types, absolute/relative/formatted paths, unchanged
modules, dry runs, data publication, patched metadata, directory propagation,
ambiguous suffixes, conflicting references, and negative guards.
Full-suite execution used normal Windows access because the sandbox denied
temporary-file moves and link creation. Logs and XML results are under
`work/import-library-audit` and `work/crow-diagnostics/general-case-*`. The completed
console dump establishes the previous lookup failure. The subsequent `1791630525`
dump repeats the same missing-path error and null API call. The cooked overlay
has the correct name, but the assembled transfer folder `D:/PS5/PPSA19068` still
has the original lowercase `c` despite matching module bytes. A case-only overwrite
on Windows can preserve the existing directory entry's spelling. The user
confirmed that explicitly renaming the file on the console makes the game boot.
Complete-folder assembly and transfer must preserve the overlay's exact path
spelling as well as its contents.

Reference classification is still flattened by NID and does not fully validate
library ownership, dynamic module loading, data imports, or ABI compatibility.
This fix corrects the demonstrated cooking error; it is not a replacement for
a console compatibility test or a complete qualified-export reference database.

---

<a id="ppsa34008"></a>
## PPSA34008 investigation

Former source: `PPSA34008_ANALYSIS.md`. Historical results retain their original scope.

### PPSA34008 analysis — 10 October 2026

#### Finding

The supplied `10xx+` backport targets firmware 10.00, but the files do **not** establish that 10.xx is the lowest achievable firmware. They leave dependencies present on 10.01 that need additional work on 4.03. Backport Studio already handles most of those dependencies. GPU behavior and synchronization semantics still require investigation and console testing before calling a 4.xx build compatible.

Source: `.original/backports/PPSA34008/{decrypted,default,10xx+}`. The local metadata identifies Mortal Shell II, content version 01.000.010. Runtime success on 12.xx+ is user reported. Default `param.json` declares required firmware 13.60 and SDK 12.00; `10xx+` changes both to 10.00. Its executable/library SDK pair becomes `0x10000000/0x12090001`. Package fields and SDK checks are not runtime compatibility proofs. The nearest available 10.xx export database is **10.01**, not 10.00.

#### Static dependencies

The following counts describe the supplied backport **before Studio applies additional fixes**, using exact local firmware databases plus exports of shipped modules:

| Module | Function imports absent on 4.03 | Absent on 10.01 |
| --- | ---: | ---: |
| eboot.bin | 5 | 0 |
| sce_module/libc.prx | 8 | 4 |
| Each of the five fakelibs | 0 | 0 |
| FontGsm, JobManager, NpCppWebApi, Pfs | 0 | 0 |

The eboot's five extra dependencies are Trinity detection (first observed export: 7.01), coredump user-string reporting (8.00), remote-controller detection (10.01), and two VRR functions (5.02). These firmware labels are observations in the available database, not independently established API release dates.

The libc difference is important: 4.03 lacks SyncOnAddress Wait32/Wait64/Wake/Wait; 10.01 provides them. Both databases lack Wait8/Wait16 and two LibcInternalExt imports. The supplied backport patches their callers or wrappers without removing all import entries. Thus even four nominally missing imports on 10.01 do not by themselves contradict the reported success.

These checks flatten exported NIDs across providers, do not verify dynamic loading order, and classify data imports separately without certifying them. Export availability does not validate ABI, syscall arguments, driver/kernel interaction, hardware, or reachability. Zero heuristic references does not prove an import unused.

#### Changes recovered from default → 10xx+

Unwrapping removes SELF signing/container differences. The eboot has 345 changed bytes in 333 contiguous ranges:

- 164 aligned shader instructions change `010092bf` (`s_trap 1`) to `000080bf` (`s_nop`): 328 changed bytes. Studio's existing opt-in `gpu_scratch` shader pattern reproduces all 164 replacements exactly.
- Operation-mode PLT entry at ELF VA `0x7ba32d0` writes zero to both output pointers and returns. Studio's default `opmode_normal` writes mode 2, so its behavior is not identical to this sample.
- One stack argument at `0x768d02b` changes from -1 to zero before a call through the PLT at `0x7ba9570`. Its semantic purpose remains unresolved; do not promote the raw replacement to a generic rule.
- SDK fields change from `0x12000038/0x13090001` to `0x10000000/0x12090001`.

Libc has 89 changed bytes in 11 ranges, including ELF header cleanup and SDK edits:

- Three calls to LibcInternalExt PLT entries are replaced with five-byte NOPs: `0x3af2c`, `0x3afe9`, `0x80b04`.
- At `0x80af4`, a call is redirected to libc's local `__cxa_atexit` export at `0x3acb0`; GOT `0x19e4f8` is redirected there too. This agrees with Studio's existing destructor redirect.
- Wait8/Wait16 wrappers at `0x118ae0` and `0x118b70` compare the byte/word, call PLT `0x11d860` with argument 200 if equal, then return 1. The old code remains beyond their early RETs, including references to missing kernel imports. These wrappers differ from Studio's existing return-zero stubs. Their timeout, wakeup, error, and concurrency behavior needs review.
- The memory-map import NID changes from `4h6F1LLbTiw` to `IWIBBdTHit4`. The supplied change is a plain swap; Studio additionally clears flag `0x8000`, based on prior evidence. Do not treat the sample as proof that the plain swap handles every heap-growth path.

The supplied AGC, AGC Driver and PSML identify Sony build J03828141 (local database: 12.70), with lowered SDK fields and patched imports. They differ from the older J03718109 set in the existing 4.03 store. No missing function imports against 4.03 is encouraging but does not prove that this newer graphics implementation works against the older kernel. The supplied driver's scratch-hook implementation has not been verified.

#### Studio dry-run results and software coverage

Analyzed **default**, with the supplied `10xx+/fakelib` folder, known fixes, param patching, and `gpu_scratch` enabled. Analysis writes no overlay and never changes source files:

| Target | Result | Remaining unresolved function imports |
| --- | --- | --- |
| 4.03 | Completed | `32KQRUK13kI` / sceCoredumpWriteUserString in eboot |
| 10.01 | Completed | None |

Completed is an engine state, not console validation. The 4.03 run applies derived rules including generic SyncOnAddressWait and savedata/PSML changes. The selected supplied fakelib folder has no `gpu_scratch/` variant; Studio warns and retains its driver while applying the shader pattern. This does not certify the driver-side half of the option.

Added five private regression cases in `src/Ps5Backport.Tests/Ppsa34008Tests.cs`: two dependency matrices, exact shader replacements against the independent supplied backport, and two production dry runs with expected unresolved imports and no output directory. Existing patch rules and goldens remain unchanged. This expands the software's verified corpus without promoting uncertain sample-specific behavior into default fixes.

Validation: `dotnet build Ps5Backport.slnx -c Debug --no-restore -m:1` passed with zero warnings/errors. The five new cases passed with zero failures/skips using the xUnit executable's `-class Ps5Backport.Tests.Ppsa34008Tests -failSkips -failWarns` filter. `git diff --check` passed. The complete test suite and console runtime were not rerun for this investigation.

#### Focused Ghidra work

Unwrapped local files are ready under `work/ppsa34008/elf/{default,10xx+}`. Use the PS5 ELF loader if available and confirm its mapped addresses against the ELF VAs below. With a different image base, translate addresses accordingly.

1. **Eboot:** inspect all callers of coredump NID `32KQRUK13kI` (GOT `0x9ae1c78`). Determine whether these are startup/gameplay paths or guarded diagnostics, and how the return value is used. The heuristic counts four references; it does not prove four reachable callers.
2. **Eboot:** inspect the caller around `0x768d02b` and callee/PLT `0x7ba9570`, recover the argument structure and explain -1 → 0.
3. **Libc:** decompile both versions of wrappers `0x118ae0` / `0x118b70`, resolve PLT `0x11d860`, and trace their callers' retry/timeout handling. Do not follow the old dead tail after the patched RET as if it were a live call path.
4. **Libc:** inspect memory-map callers and their flags, especially heap growth. Compare the plain NID swap with Studio's flag-clearing adapter.
5. **AGC Driver:** compare J03828141 with the supplied original 12.70 firmware library and the existing `fakelibs/4.03/gpu_scratch` variant; establish whether submission/scratch allocation hooks are present and whether any newer kernel contract remains.

Local evidence: `work/ppsa34008/analysis.json` (hashes, module matrices, offsets and deltas), `disassembly.txt` (bounded instruction windows), `studio-4.03.txt` / `studio-10.01.txt` (production dry-run reports). Binary files remain ignored and private. No Ghidra connector is available in this session; decompilation, call-tree and cross-reference exports from these specific sites would advance the remaining questions.

#### Standardized follow-up

The scratch analyzer is now a reusable developer program at `verification/GameAnalysis/`, with a JSON profile for this sample and `scripts/analyze-game.ps1` as its entry point. See `verification/GameAnalysis/README.md` for new-game profiles and output semantics. The five supplied libraries were copied into `fakelibs/10/ppsa34008` through Studio's transactional import, then verified against the source hashes. Existing `fakelibs/10` files retain their previous hashes. Select the new set explicitly; this import does not change the default firmware lookup or establish 4.xx compatibility.

---

<a id="performance"></a>
## Performance and memory

Condensed from the former `PERFORMANCE_REPORT.md` (Phase 5, 10 October 2026). Full paired tables are in FINDINGS.md Part 2 "Paired engine measurements".

### Accepted changes

- **One firmware parse per run.** The pipeline loads the firmware databases once and shares that run-owned list between reference selection and introduction lookups. There is no process-global cache, so a run can never reuse a previous run's database. Cost: the run still holds the databases and their lazily built NID sets.
- **No segment copies during signing.** `Self.Sign` reads segment and version bytes as read-only slices of the existing ELF and copies them once into the output. Input and output buffers still coexist during signing, so callers must keep the input stable.

Payload and normalised report hashes were identical before and after for every workload. Allocation fell about **33%** for a public large Cook (579.6 → 387.6 MiB) and **18%** for a private large Cook (2,398 → 1,974 MiB). Private analyses saved about 109 MiB each. This is not a universal RAM reduction: private small Analyze had a *higher* working-set/LOH peak (666 → 704 MiB), and a private large Cook still reaches about 1.8–1.9 GiB working set. Timings were not controlled well enough (three iterations, no disk-cold control) to claim speed-ups. The focused metadata benchmark showed duplicate loads at 236 ms / 218.5 MiB versus one load at 125 ms / 109.9 MiB, with very wide timing intervals.

Method notes: Release x64, .NET 10.0.12, Ryzen 9 7950X. Iteration 1 is process-cold and iterations 2–3 warm, with a full GC before each. LOH is the highest post-GC size, and process counters include native/runtime/observer memory, so subtracting managed bytes does not give native allocation.

### Deferred, with reasons

- **Two-pass loading.** In a loading-only experiment it doubled allocation (192 → 384 MiB) and time (124 → 235 ms) in exchange for holding about 16 MiB of decoded modules instead of 192 MiB. It would also need compact post-transform state to keep provider ownership, partial-failure behaviour, final imported NIDs, added-library exports and `/app0/` references. Revisit only with an explicit memory budget and a full-pipeline prototype.
- **Module-symbol cache.** `SceElf.Data` is mutable, and import renames, symbol definitions and byte patterns would all need cache invalidation. The current code re-parses after changes, so invalidation can't be missed. One large fixture's symbol parse is about 8.9 MiB.
- **ArrayPool, scratch ELF files, Channels, parallel transforms.** Large pooled arrays retain memory, scratch files add cleanup and recovery duties, and parallel workers multiply buffers and complicate deterministic ordering and cancellation. No measured bottleneck justified them. Production stays one serial transform/sign worker with all provider modules retained, and has no hard memory cap.

### Cancellation and UI memory

Cancelling at the start of loading a 16 MiB synthetic module was observed within 3–6 ms; bigger SELF reads, signing and protected publication can take longer. Headless Avalonia/Skia processes analysing the public small game peaked at about 361–369 MiB working set. These counters mix managed and native Skia/runtime memory and say nothing about GPU memory or leaks across repeated operations.

### Reproduction

```powershell
pwsh -NoProfile -File scripts/measure-performance.ps1                       # public synthetic workloads
pwsh -NoProfile -File scripts/measure-performance.ps1 -IncludePrivate `
  -BaselineDirectory <preserved old engine> -InputRoot <inputs> -ReportDirectory <out>
dotnet restore verification/Benchmarks/Ps5Backport.Benchmarks.csproj --locked-mode
dotnet run --project verification/Benchmarks/Ps5Backport.Benchmarks.csproj -c Release --no-restore -- --filter '*FirmwareMetadata*'
pwsh -NoProfile -File scripts/measure-ui-memory.ps1 -Game <game folder>     # after a Release app build
```

A paired comparison needs the same harness beside a preserved copy of the old engine and its runtime files. Don't overwrite that baseline Core DLL. Cooking writes real payloads, so budget disk space. Raw records can contain private paths and stay under ignored `work/`. Run timing work on its own, not alongside builds or tests.

---

<a id="instruction-decoder"></a>
## Instruction-decoder evaluation

Condensed from the former `INSTRUCTION_DECODER_REPORT.md` (Phase 7, 10 October 2026).

**Decision:** keep Iced 1.21.0 as an optional evaluation tool (`verification/DecoderEvaluation`, outside the solution). Don't replace the production byte-pattern reference scanner or patch emitter with segment-linear decoding: it removes some false positives but introduces misses after embedded data and padding. Neither zero nor positive reference counts prove runtime reachability.

The candidate decodes 64-bit instructions from executable segment starts and resolves direct CALL/JMP and RIP-relative MOV/LEA/indirect CALL/JMP. FS/GS-based operands stay unresolved (they need runtime segment bases). There is no control-flow graph and no register propagation.

### Accuracy

All 530 specified reference cases passed (18 named, 512 generated NOP-offset variants). Expected values come from explicit synthetic byte layouts, not from either implementation.

| Case | Expected | Heuristic scanner | Linear decoder |
| --- | ---: | ---: | ---: |
| Ordinary CALL/JMP, GOT branches, MOV/LEA, permitted prefix | 1 | 1 | 1 |
| Address-size prefix above 4 GiB | 1 | 0 | 1 |
| CALL-looking bytes inside a MOV immediate | 0 | 1 | 0 |
| Invalid LOCK-prefixed indirect CALL | 0 | 1 | 0 |
| FS-based load | unresolved | 1 | 0 |
| RET followed by call-looking embedded data | 0 | 1 | 1 |
| Short jump over one data byte to a real CALL | 1 | 1 | 0 |
| Truncated CALL | 0 | 0 | 0 |
| Register-indirect CALL | unresolved | 0 | 0 |
| Prefixed PLT self-reference | 0 | 1 | 0 |
| Short jump to a PLT entry | 1 | 0 | 1 |

On the private corpus the two agreed on libc (3,063 references original, 3,062 backported). On the original eboot fixture (66 MB of code, 426,106 references) they differed by two references: one was a scanner false positive inside a store's displacement, the other a decoder false negative after RET/padding. The decoder costs about 1.6–3.7× the scanner's time with negligible extra allocation.

**Emitted-code checks.** Iced decoded all 8 named stubs and 45 adapter byte blocks without invalid or truncated instructions, and verified return constants, GOT jump targets (including the rel32 limit), a PLT redirect and two adapters split across guarded caves. This validates encoding and selected operands. It does not validate stack alignment, register preservation or console ABI.

### Conditional tail calls (13 October 2026)

Phase 7 had limited the decoder to the scanner's own instruction classes, so both missed the same forms. A full Iced pass over the 18 original sample eboots found 31,813 references the scanner missed: 98% were `jcc rel32` (`0F 80`–`8F`) straight to a PLT entry (conditional tail calls), and the rest `cmp`/`add r64, [rip+GOT]` (`3B`/`03`). 138 imports were reported with `refs=0` while code branched to them (17 of them missing on FW 4.00). For example, GT7's only call to `sceTextToSpeech2Cancel` is a `je` at `0x180bc02`. `Imports.CodeReferences` now matches those three forms. Against the decoder on that corpus it reports 8,087,524 vs 8,087,512 references: 12 over-counts and 0 under-counts, at unchanged scan time. Only `refs` tokens in four goldens changed; patched binaries are unchanged.

### Before adopting a production decoder

Define count compatibility, trusted code starts, a policy for ambiguous and unreachable regions, and conservative handling of unresolved references. Add reviewed disagreement fixtures, show an improvement with no new false negatives, budget the extra scan time, and rerun the private byte/report goldens. A full control-flow/dataflow engine is deferred until a real patching problem needs it.

```powershell
pwsh -NoProfile -File scripts/evaluate-decoder.ps1                  # synthetic + emitter checks
pwsh -NoProfile -File scripts/evaluate-decoder.ps1 -IncludePrivate  # also the three ELF fixtures (fails if absent)
```

---

<a id="cli-catalog-schema-decisions"></a>
## CLI, catalog and schema decisions

Condensed from the former `PHASE8_DECISION_REPORT.md` (Phase 8, 10 October 2026). Evaluated in `verification/Phase8Evaluation` (outside the solution; `scripts/evaluate-phase8.ps1`). No production change was adopted.

| Proposal | Decision | Main reason |
| --- | --- | --- |
| System.CommandLine | Defer | Significant compatibility work (table below) for little maintenance gain; the current parser is about 35 lines. |
| SQLite catalog | Keep JSON | Existing set-membership queries are cheap; no relational or history requirement exists. |
| Full versioned JSON schemas | Strongest follow-up | Catches authoring typos Core accepts, but needs a defined contract. |

### CLI behaviour a migration would have to preserve

| Behaviour | Existing parser | System.CommandLine 2.0.12 |
| --- | --- | --- |
| `--firmware 4` / `--firmware=4` | Both accepted | Both accepted |
| Repeated scalar firmware | Last value wins | Error: multiple arguments |
| Repeated `--option` / `--provider` | Ordered lists | Ordered arrays (needs one-value-per-token config) |
| Global `--aerolib` before/after the command | Accepted | Needs a recursive option |
| `--extend-text=false` | Presence means true | Parsed as false |
| `--` token | Ordinary positional; later options still parsed | Ends option parsing |
| Unknown option with a value | Stored, possibly ignored | Error: unrecognised token |
| `--help` | Missing-value error, exit 1 | Built-in help |
| `nid -h` | NID of the string `-h` | Help |
| Option followed by an option-like token | Can consume it as the value | Same for the tested string option |

`--extend-text` is on by default; `--no-extend-text` is the effective switch. Before a migration: write down the CLI contract, characterise every command, alias, repeated value, `--`, malformed input and stdout/stderr/exit code, prototype a compatibility adapter, and keep existing goldens unchanged.

### Catalog: JSON vs SQLite

At the time the catalog was 43 firmware JSON files (16.5 MB, 861,897 NID memberships). The .NET load plus union sets took about 180–330 ms and 173 MB, and 43,000 membership queries took 12.8 ms. In Python, the same queries took 6.3–7.0 ms on loaded sets versus 51.8–64.8 ms as indexed in-memory SQLite statements, and building the SQLite index took 1.65 s. The round trip and query results were equivalent. Persisted databases, WAL, concurrent writers and the .NET provider were not measured. A database transaction would also not make the external ELF/SELF publication atomic, so staging, locks and recovery stay necessary. If catalog loading becomes a UI bottleneck, first try loading only the needed firmware.

### JSON schema finding

A Draft 2020-12 schema for one fix record validated all 52 current records. It caught one real authoring risk: a misspelt `proovn: false` (with `proven` absent) is rejected by the schema but accepted by Core, which then defaults `Proven` to true. No such typo exists in `data/`. Conversely, Core (not the schema) rejects unknown stub names and empty adapters, so a schema would supplement semantic validation, not replace it. A future schema should cover fixes and stubs first, version its identity, allow editor association and test typo handling against Core.

---

<a id="dependencies"></a>
## Dependency review

Condensed from the former `DEPENDENCY_REVIEW.md`; versions checked against the project files on 14 October 2026.

| Project | External packages |
| --- | --- |
| Core, CLI | None (project references only) |
| App | Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Headless, Avalonia.HarfBuzz 12.1.3; CommunityToolkit.Mvvm 8.4.2 |
| Tests | xunit.v3 4.0.1, xunit.runner.visualstudio 3.1.4, Microsoft.NET.Test.Sdk 17.14.1, coverlet.collector 6.0.4, FsCheck.Xunit.v3 3.4.0 |
| verification/MutationTests | xunit.v3 4.0.1, FsCheck.Xunit.v3 3.4.0; local tool dotnet-stryker 5.0.0 |
| verification/Benchmarks | BenchmarkDotNet 0.15.8 (23 packages, brings its own Iced/Capstone) |
| verification/DecoderEvaluation, GameAnalysis | Iced 1.21.0 (MIT, no dependencies) |
| verification/Phase8Evaluation | System.CommandLine 2.0.12 (MIT); Python companion needs jsonschema 4.26.0 and installs nothing |
| verification/UiProfile, Performance | None beyond the App's Avalonia.Headless |

Every project has a `packages.lock.json` (enabled in `Directory.Build.props`), verification restores in locked mode, and App/CLI/Core also lock the `win-x64` target for self-contained packages. The 10 October transitive `--vulnerable` and `--deprecated` queries reported nothing for any project. That is dated feed evidence only. Licences: MIT for the UI, MVVM, Microsoft and coverage packages; Apache-2.0 for xUnit; BSD-3-Clause for FsCheck; ANGLE ships a BSD-style notice file. Packages keep the actual notices under `licenses/`.

**Telemetry.** Avalonia.BuildServices (transitive) and Microsoft Testing Platform have build/test telemetry. `verify.ps1`, the mutation and performance scripts set `AVALONIA_TELEMETRY_OPTOUT=1`, `TESTINGPLATFORM_TELEMETRY_OPTOUT=1` and `DOTNET_CLI_TELEMETRY_OPTOUT=1` for their child processes. Set them yourself when running `dotnet` commands manually. Application runtime behaviour is unaffected.

**Update policy.**

1. Before a planned update, rerun the transitive vulnerability and deprecation checks; a feed failure means no evidence, not a clean result.
2. Update related framework packages together. Don't override a framework's native or language-runtime graph (SkiaSharp, HarfBuzzSharp, FSharp.Core) just because a newer major exists.
3. Change versions or the SDK pin deliberately, run `dotnet restore Ps5Backport.slnx --force-evaluate` (never in ordinary verification), and review every lock-file diff.
4. Run the public lane in both configurations, then the private lane. Investigate any byte/report golden drift before accepting an upgrade.
5. Treat test-runner and analyzer upgrades separately from production packages.

---

<a id="historical-audit"></a>
## Historical confirmed audit

Former source: `AUDIT.md`. Historical results retain their original scope.

This early 201-test audit predates transactional publication, physical-path protections and later console diagnoses. Its confirmed fixes remain historical evidence; its follow-up statements must be read against the newer sections above. The subsequent full-audit request remains unfinished.

### PS5 Backport Studio audit — 2026-10-10

The audit target is the software in this workspace: `Ps5Backport.Core`, `Ps5Backport.Cli`, `Ps5Backport.App`, and their tests. The supplied OriathHub-specific instructions describe another project; no OriathHub files were present or modified. The working tree was clean at the start.

This is a source review, regression-test audit, and headless UI inspection. It is not a guarantee that every possible bug has been eliminated. Changes were limited to defects with a clear corrective behavior. No game or console was launched, no source game was patched, and no knowledge/golden data was regenerated.

#### Confirmed fixes

| Area | Defect and correction | Verification |
| --- | --- | --- |
| BPS parser | Metadata, literals, and varints could consume the checksum trailer. Parse the body separately and reject truncated fields. | Checksum-valid malformed patches in `AuditTests`. |
| BPS arithmetic | Varint arithmetic could wrap. Use checked multiplication/addition and report invalid data. | Oversized varint regression. |
| BPS consistency | Parsed action lengths were not checked against the declared target. Reject overrun and incomplete targets before applying. | Too many/few output bytes regressions. |
| BPS copying | A target-copy could read unwritten zero-filled output; invalid source ranges and copy cursors escaped as runtime exceptions. Validate ranges, cursor overflow, and already-written target data, while retaining overlapping copies. | Invalid-copy regressions plus the existing overlapping-copy test. |
| BPS public API | Constructed patch records could contain missing/mismatched literals or unknown commands. Reject them as invalid data. | Public-record action regressions. |
| ELF parser | Wrong class/encoding/machine/header sizes, overflowing offsets, and out-of-file segments were accepted or reached unchecked reads. Validate headers and file ranges before parsing. | Synthetic ELF regressions and existing sample/golden tests. |
| ELF tables | Dynamic, symbol, and relocation tables lacked complete-entry/range checks; relocation symbol indexes were unchecked. Validate bounds and indexes. | Full sample/import/patch suite. |
| ELF strings | Missing dynamic string tables escaped as `KeyNotFoundException`; invalid string offsets were not identified as malformed input. Report `InvalidDataException`. | Missing-string-table regression and existing import reports. |
| SELF parser/signer | Invalid embedded segment offsets/sizes and truncated program headers escaped as overflow/range exceptions. Validate before casts and output allocation. | SELF boundary regressions and byte-for-byte sign/unwrap golden tests. |
| SDK fields | A short nonzero parameter segment could read or overwrite bytes outside that segment. Require enough bytes for both SDK fields. | Short-parameter regression; existing all-zero parameter behavior retained. |
| Firmware API | Oversized integers escaped validation; a three-digit minor could produce an invalid BCD version and overwrite adjacent SDK bits. Reject components outside 0–99 with `FormatException`. | Oversized and `4.100` regressions plus existing firmware tests. |
| CLI | Argument parsing occurred outside the error handler, so an option without a value crashed the process. Parse inside the handler; also handle numeric overflow and malformed JSON. | Missing-value regressions and CLI golden tests. |
| Firmware import | An empty dump could become an exact empty export database; CLI imports could replace a valid database with an empty one. Refuse empty imports. | Empty-dump and existing-database-preservation regressions. |
| Output safety | Case-sensitive comparisons and trailing separators bypassed the Windows source/output guard. Normalize separators and use the platform's path comparison. | Windows path-alias regressions. |
| AMPR index | Its output path could be the source game itself. Apply the same overlay-path guard. Dispose its memory streams. | Source-path refusal and existing binary-index tests. |
| Data-file patches | Each pattern reread the original data file, so a later pattern overwrote an earlier pattern's edits. Apply all patterns for a file to one buffer. | Two independent patterns survive in the written output. |
| Pattern API | Empty find sequences, unequal replacement lengths, and invalid ranges/steps allowed incorrect writes or incidental runtime failures. Validate arguments before mutation. | Invalid-pattern regressions and golden patch outputs. |
| Cancellation | An already-cancelled run could create output/write metadata; later data and app0 stages lacked cancellation checkpoints. Check before starting and between those stages. | Pre-cancelled run leaves no output directory; existing cancellation test. |
| Directory traversal | Recursive walkers could grow the call stack and follow junction/symlink cycles. Use explicit stacks, skip directory reparse points, and share the safe walker with index/dump discovery. | Sorted traversal regression and existing module-discovery/golden tests; cycle prevention checked by source reasoning. |
| Resource ownership | Replaced cover bitmaps and completed cancellation sources were not disposed. Dispose covers on replacement/window close and sources when a run completes. Dispose snapshot frames/view models. | Ownership review, builds, existing game-loading tests, headless snapshots. No long-duration native-memory profile was performed. |
| Cover loading | An unreadable optional cover could interrupt game setup. Log a warning and continue loading the game. | Existing open/drop/command-enablement tests and sample-game snapshot. |
| Workbench state | Opening another game while busy could mix the running game's results with another game's UI. Guard loading and notify Open Game command availability. Capture the AMPR output path before background execution. | Busy-game regression and command-enablement tests. |
| Tools state | Adding/clearing/converting files during another operation could change the collection or busy flag midway. Guard operations and capture the conversion output folder once. | Busy-list regression and existing conversion round trips. |
| Background results | Older firmware refreshes/store checks could publish results after the path or selection changed. Capture request inputs and ignore obsolete results; clearing the DB path also clears selected details. | Refresh lifecycle regression, existing database-folder tests, source review. |
| Settings | Explicit JSON nulls for firmware/recent games could crash startup or remembering a game. Normalize them to defaults. | Explicit-null regression and existing settings round trips. |
| Atomic writing | Writers shared a predictable `.tmp` path, overwriting unrelated temporary files and interfering with other writers. Use a unique exclusively-created temp file with failure cleanup. Settings reuse this writer and support relative filenames. | Existing `.tmp` preservation regression and settings/SELF file tests. |
| Store input/UI | Null/non-object manifests produced null/type failures; corrupt manifests escaped the Store page. Validate manifest shape and log read errors in the UI. Firmware/path discovery no longer parses manifests unnecessarily. | Null-manifest and corrupt-Store-page regressions, existing store tests. |
| Firmware UI | Clearing selection retained old module details; searches missed function names of unchanged exports. Clear details and search the selected firmware's full export list. | Selection/search regressions and firmware page snapshot. |
| Module UI | A module changed only by byte patterns was labelled unchanged during analysis. Include pattern changes in its state. | Pattern-only module regression. |

#### Requested categories and coverage

| Category | Result of this audit |
| --- | --- |
| Memory/resource leaks | Corrected deterministic ownership gaps. Managed allocation pressure and native Avalonia/Skia retention still need a soak/profile run. |
| Stack overflow/infinite loops | Replaced recursive directory walks and prevented directory-link cycles. Reviewed parser/cave/hash-table loops for progress and termination. |
| Buffer overflow | Corrected input ranges, casts, and write-length validation. First-party code uses managed arrays/spans; this does not establish the safety of native dependencies. |
| NullReferenceException | Reproduced and fixed explicit-null settings/manifests. Optional cover failures no longer interrupt game setup. |
| Syntax errors | Both build configurations pass with warnings treated as errors. |
| Logical/runtime/integration errors | Corrected lost patches, empty imports, invalid binary inputs, CLI errors, cancellation boundaries, and source-path protection. |
| Race conditions | Corrected shared temporary-file names and stale UI/background-operation state. Multi-process whole-overlay/store transactions remain outside the tested guarantees. |
| Deadlocks | No confirmed deadlock found in the reviewed first-party code. The normal UI paths await background tasks; the snapshot-only pump advances dispatcher jobs. This is not a concurrency stress proof. |
| UI/UX | Corrected stale selection/search/status behavior and busy/error handling; visually inspected five headless snapshots. |
| Compatibility/API inconsistency | Checked Debug/Release builds, CLI/GUI flows, malformed-input error types, firmware encoding, and sample/golden compatibility. No Linux/macOS or live-console validation performed. |
| Leftover bugs | Re-ran all existing regressions and sample/golden checks. Follow-up areas below were not treated as proven fixes. |

#### Verification

Baseline: 160 tests passed with no failures/skips. Added 41 audit cases, including parameterized malformed-input cases. Final result: **201 tests passed, 0 failures, 0 skips**. Debug and Release builds both succeeded with **0 warnings and 0 errors**. `git diff --check` passed.

Commands:

```powershell
dotnet build Ps5Backport.slnx -c Debug --no-restore -m:1
dotnet build Ps5Backport.slnx -c Release --no-restore -m:1
dotnet src/Ps5Backport.Tests/bin/Release/net10.0/Ps5Backport.Tests.dll
git diff --check
```

Headless snapshots were rendered and inspected at `work/audit/backport.png`, `modules.png`, `firmware.png`, `tools.png`, and `store.png`. The Modules snapshot used a dry analysis of the Gran Turismo 7 sample. These checks exercise rendering and view-model behavior, not interactive keyboard/mouse/accessibility or multi-monitor operation.

The sandbox initially blocked MSBuild/test IPC and temporary-file moves. Normal local execution succeeded, so those sandbox failures were not classified as software bugs. MCP indexing was rejected by automatic approval review because of possible private-source export; discovery and review continued locally without transmitting source to that service.

#### Follow-up validation and design questions

- Console execution remains unverified. Native stub/adapter semantics, loader behavior, firmware database completeness, and text-extension compatibility cannot be certified by desktop tests alone. No knowledge entries, offsets, or instruction bytes were guessed at or changed.
- Large modules/data files are still processed in memory. Measure peak memory and native resource retention during sustained real-world use before claiming leak-free operation.
- Output-path protection is lexical after normalizing paths. Alternate physical aliases, directory junctions in the output path, and filesystem changes during a run require additional validation. The scan walkers skip linked subdirectories, but this is not physical-path canonicalization.
- Reusing an output folder can retain files from an earlier run with different options. Cancellation/I/O failures can leave a partially completed overlay. Choosing deletion, staging, rollback, or a new-folder-only policy needs a defined retention policy; this audit does not silently delete existing output.
- Store/database writes are not a transaction across multiple files or independent processes. A batch failure or another application changing inputs concurrently needs fault-injection testing and a defined recovery policy.
- Native dependency internals, platform/DPI/accessibility compatibility, arbitrary malformed JSON schemas, and exhaustive binary fuzzing were not proven safe by this audit. No dependency upgrades or speculative API redesigns were made.

---

<a id="reference-research"></a>
## Reference research

Former source: `REFERENCE_OVERVIEW.md`. Historical results retain their original scope.

Dated local source/binary research from 8–10 October 2026. Retained candidate/open-question labels are interpretation limits, not confirmed production fixes. Legacy Python/UI/package statements are historical; the current project decisions above supersede them. Literal NUL characters in binary examples are displayed as `\0` for a searchable text document. Detailed research is retained because offsets, NIDs and sample comparisons cannot be recovered from a short summary alone.

### Applicability of refreshed references — 10 October 2026

Reviewed changes between the previous and updated revisions in the following refresh table against Backport's symbol lookup, firmware references, fakelib selection, patching and diagnostic workflows. The immediate benefits are better symbol identification and corrected console-testing instructions. The native compatibility work below remains supporting research until independently validated for our targets.

| Updated reference | Confirmed value for Backport | Adoption decision |
|---|---|---|
| `sce_symbols_new` | 78 appended NID/name pairs; all 78 independently match the salted SHA-1 NID computation, and none overwrite an existing NID. `NameDb` grows from 159,034 to 159,112 names. Examples include `HV4j+E0MBHE` / `sceAgcCreateInterpolantMapping_0100`, `Jrs7UUkGOFo` / `sceCoredumpGetStopInfoGpu_Agc` and the formerly unnamed `D-CzAxQL0XI` / `sceUserServiceGetPlatformPrivacyWs1`. | Already wired into `Paths.AerolibPath` and `build/RuntimeAssets.targets`; rebuilding refreshes the packaged catalog for both clients. This identifies functions, without proving availability on any particular firmware or making them safe to stub. |
| `ShadowMountPlus` | Adds Full/Emulators-only/Disabled modes, applies filtering even to `fakelib2`, strips `libkernel.sprx`, and removes kstuff auto-pause settings. Emulator-only mode excludes the Agc/AgcDriver/Psml replacements that some backports require. `update_emulators=1` and global fakelib composition can replace a supplied library even with `auto_update_ampr=0`. Launch-error monitoring is bounded to 60 seconds. | Corrected the console tester checklist: select Full, keep emulator/AMPR updates disabled for a controlled test, exclude the title from global fakelib, confirm the selected folder, and collect crash timing. Removed the obsolete `kstuff_no_pause` retry. Do not rely on a `libkernel.sprx` overlay with this loader. These are documented loader requirements; no console configuration was changed remotely. |
| `pgo_stub` | Version 0.6 adds mutex-guarded shared state, bounded config parsing and chunk validation, five-scenario handling, and explicit empty-list behavior. It now requires initialization and an open handle, and rejects repeated initialization. | A candidate PlayGo replacement, not a drop-in certified upgrade. Its changed lifecycle may affect games relying on the older permissive behavior. The upstream project targets Prospero SDK 2.000; its implementation was not built or console-tested here, and existing manifested PlayGo binaries were retained. |
| `sdk` | Adds 202 Agc and 116 AgcDriver named assembly import stubs. Also fixes the Meson machine section and replaces `ln -sf` with `ln -sfn` for the include symlink. | Useful for future native build/link work. These stubs contain declarations rather than native function implementations or a firmware-versioned export corpus, so they do not justify an exact firmware database or new runtime fixes. Our C# engine is unaffected by those toolchain fixes; the separately configured AMPR build SDK was not updated by refreshing this reference checkout. |
| `AnyPS5` | Adds/refines strict import reachability analysis, import-module handling, Mach-O NID patching and ELF symbol-version rejection, alongside audio/kernel/graphics emulator changes. Some newly exported AudioOutEx entry points deliberately throw `not implemented`, which their tests verify. | Useful research for conservative reachability and ABI investigation. Host emulation and relinking do not establish that removing a PS5 import, stubbing a function or changing a game instruction is safe. No emulator implementation or import-removal rule was copied into Backport. |

Validation and changes: the reference update made the two existing symbol-catalog snapshots stale. A separate PowerShell implementation first reproduced **both old golden files exactly** from the previous Git revision, then computed the expectations for the 78 validated additions. Only `golden/aerolib-nids.txt` and `golden/namedb.txt` were refreshed; no recording mode or bulk golden regeneration was used. Both existing symbol tests pass, including resolution of the newly named privacy function. Required public verification passes **309 cases with zero failures/skips in Debug and Release**, and both builds report **zero warnings/errors**. Runtime-asset tests verify the catalog copies. The full private corpus remains unavailable after fixture cleanup. No production C# changes, exact firmware database changes, patch rules or replacement binary deployments were justified by this review.

### GitHub reference refresh — 10 October 2026

Fetched all 15 existing GitHub checkouts under `.original/reference` and fast-forwarded their configured tracking branches. Every checkout's final HEAD equals its freshly fetched upstream; there were no merge conflicts. Five checkouts received new commits:

| Reference | Tracking branch | Previous commit | Updated commit |
|---|---|---|---|
| `AnyPS5` | `origin/main` | `5850a6cd115a0e62eed83db291709a0027d285b5` | `56fb1b3b951ac057af05059d653f080a52f00fb2` |
| `pgo_stub` | `origin/main` | `b26083f64f837a2a99879cecc7ca569c16507d2d` | `c95dec77756342fc150d4aec76bcf1d48c913b2b` |
| `sce_symbols_new` | `origin/main` | `2883963a0a514ba6e77407a08ef4730a65e88254` | `fe2cc2948e014f62c878a98c4dc7a53ecf4c6f75` |
| `sdk` | `origin/master` | `f7fd02e6e195902a449b5e664917be8c01888b34` | `f5d40f02770e9b0001dd9e18056eca70b3da8b40` |
| `ShadowMountPlus` | `origin/1.7` | `4cde42a2293756132735d8e4280eb71f089865ee` | `98c524890176c7b474fcb03387bec88c4a559861` |

The other ten were already current: `ampr_emu`, `BackPork`, `backport-helper`, `make_fself`, `ppr-patch`, `PS5-3.20_Libs`, `PS5-BACKPORK-KITCHEN`, `PS5-Prolapse`, `PS5-SELF-Decrypter` and `sce_symbols`. The existing Kitchen project-file edit (SharpCompress `0.44.5` → `0.50.4`) was preserved. AnyPS5's 11 declared third-party submodules were uninitialized; their updated recorded revisions remain uninitialized. No existing nested submodule checkout needed updating.

These are source-reference updates. Historical research, golden expectations, application assets and earlier test results retain their recorded snapshot scope. For example, `sce_symbols_new` gained 78 lines in each symbol catalog, and the SDK gained Agc/AgcDriver stubs; those changes are not automatically promoted into exact firmware databases or regenerated golden files.

### PS5-Prolapse reference assessment — 10 October 2026

Reviewed `.original/reference/PS5-Prolapse`, upstream [1nk1/PS5-Prolapse](https://github.com/1nk1/PS5-Prolapse), at clean local commit `da04d80168ebd4032da19e786a1ce2fe41b3d333` (24 September 2026). This is useful supporting research for firmware-specific symbol offsets and their provenance; it does not supply a complete firmware export catalog suitable for direct import into `firmware_db`.

- The checkout has 49 firmware offset profiles. Forty have matching exact databases here. Parsed numeric `OFFSET_lk_*` / `OFFSET_lc_*` declarations, deduplicated within each profile, excluding the internal `_thread_list` and `worker_wait_return` addresses. Computed the names' NIDs using the same salted SHA-1/custom-base64 algorithm as `Nid.Compute`, with the known `sceKernelIsTrinityMode` pair as a sanity check. All **810 firmware/module/symbol entries**, covering **26 distinct module/symbol names**, already exist under `libkernel` or `libSceLibcInternal` in the corresponding database. This checks symbol membership, not address accuracy or corpus completeness. JSON comparison preserved case-sensitive module keys.
- Profiles without matching exact databases are `3.10`, `3.21`, `4.02`, `5.00`, `7.00`, `10.00`, `13.00`, `13.20` and `13.60`. Their presence is not evidence that a complete export list exists for those versions. In particular, no `13.60.json` was created.
- `offsets/13.60.js` explicitly distinguishes carried-over 13.00 maps, externally attributed values and unverified/conflicting offsets. `tools/lkfind.js` scans selected byte signatures and requires validation against a known module; it explicitly excludes export-only symbols from signature validation. Neither a successful signature match nor a syscall number establishes an exhaustive NID export list. The checkout contains payload ELFs, not a complete decrypted Sony system-library corpus.
- Current `FirmwareDb.Select` treats a matching firmware label as exact, and `Imports.Classify` can classify an absent NID as missing for an exact reference. Promoting these partial profiles into an exact database would therefore turn lack of evidence into incorrect missing-function reports. Existing complete databases and lower-bound selection remain the appropriate inputs. A new exact version still needs a complete, correctly identified decrypted system-library export corpus.

Source fingerprints: `offsets/13.60.js` SHA-256 `DDAE9F76C6F75D07386F8986625C8DE8FBDF07EDD7A208C62F4F59162929EE45`; `tools/lkfind.js` SHA-256 `27C5C7D5D77989489CB513A621EB2CCB1AE1DC3C0049F266BA3AEF4898B0E00C`; `docs/PROJECT_HANDOFF.md` SHA-256 `1E883865B191F1E04B852D3B46BD428D1110D47C8E339F39403840BE36BB219C`. No firmware database or production-code changes were warranted by this comparison.

### Reference projects: overview

Survey of `.original/reference/` (5 projects, about 4,200 files and 115 MB). The goal is to understand what each project does and how they connect, before we reverse engineer and rework the backport tooling.

| Folder | Upstream (git remote) | Language | Size | Role |
|---|---|---|---|---|
| `sdk/` | `ps5-payload-dev/sdk` (John Törnblom) | C, LLVM toolchain | ~2,000 files | Toolchain for building **payloads** (homebrew ELFs) that run on a jailbroken PS5 |
| `PS5-SELF-Decrypter/` | `idlesauce/PS5-SELF-Decrypter` (originally SpecterDev) | C payload | 13 files | Runs **on the console** and uses a kernel R/W exploit to decrypt real SELF/SPRX files to plain ELF |
| `PS5-3.20_Libs/` | `DNNDHH/PS5-3.20_Libs` | Generated C | 275 `.c` files, ~318k lines | **Import stubs** for every 3.20-firmware system library, generated with the sdk's `genstub.py` |
| `PS5-BACKPORK-KITCHEN/` | `rajeshca911/PS5-BACKPORK-KITCHEN` (discontinued) | VB.NET WinForms, .NET 8 | ~230 files | Windows GUI that **backports** a decrypted PS5 game to a lower firmware: SDK-version patch, fake-sign, add fakelibs |
| `AnyPS5/` | `boykopovar/AnyPS5` (2,477 commits, active) | C++20, CMake | ~1,600 files (submodules not checked out) | Converts a PS5 game ELF into a **native Linux/Windows executable**, with reimplemented system libraries and a GPU shader recompiler |
| `backport-helper/` | `drakmor/backport-helper` | C++, MSBuild (official Prospero SDK) | 19 files | Template **replacement `libSceRtc.prx`** that adds missing exports and installs runtime hooks, built in one variant per SDK (4–13) |
| `pgo_stub/` | `drakmor/pgo_stub` (v0.5) | C++, MSBuild (official Prospero SDK) | 11 files | **Replacement `libScePlayGo`** that reports every chunk as installed |
| `ampr_emu/` | `drakmor/ampr_emu` | C++, Makefile (payload-sdk) and MSBuild | 124 files | **Software `libSceAmpr`** (AMPR/APR/AMM command buffers on top of AIO), plus an LZ4 asset-pack system |
| `make_fself/` | `drakmor/make_fself` | Python | 1 script | `make_fself_batch.py`: sdk `make_fself.py` extended with batch signing, unpacking and repair |
| `ppr-patch/` | `drakmor/ppr-patch` | C payload and AArch64 asm | 25 files (**incomplete**, see below) | Patches the **A53 storage-controller firmware** so plaintext, unauthenticated PFS images can be mounted |
| `sce_symbols/` | `drakmor/sce_symbols` (fork) | Data | 18 files, 62 MB | NID-to-name database (`aerolib.csv`) for PS3/PS4/PS5/PSP/Vita |
| `BackPork/` | `BestPig/BackPork` | C payload | 5 files + 12 patches | Original fakelib loader (unionfs over sandbox `common/lib`) plus BPS patches for 10.01 libraries → 6xx/7xx; see [Part 6](#part-6-firmware-export-lists-step-2-research-results) |
| `ShadowMountPlus/` | `drakmor/ShadowMountPlus` | C payload (payload-sdk) | ~150 files | Auto-mounts game dumps and **overlays `fakelib/` into the game sandbox**; replaces `ps5-backpork.elf`. See [Part 4](#part-4-shadowmountplus-the-runtime-that-loads-fakelibs). |
| `sce_symbols_new/` | `zecoxao/sce_symbols` (upstream) | Data | 18 files, 62 MB | Same database, with 4,583 more PS5 names; a strict superset of `sce_symbols/` |

> Note: `.original/` also holds `GT7/` (`USA`/`EUR` = GT7 backported to FW 4, `NOT_BACKPORT`/`decrypted` = before) and `CONTROLResonant/backport` (a second game, FW 4.03; [Part 7](#part-7-control-resonant-second-backport-sample)). They are compared in [Part 3](#part-3-gt7-171-before-and-after-the-backport).
> The last seven rows were added later. They are covered in [Part 2](#part-2-drakmor-backport-toolkit).

---

> **2026-10-10: one engine.** The Python engine `ps5bp` (and its `tests/` and `tools/`) was retired; PS5 Backport Studio's C# engine (`Ps5Backport.Core`, command line `ps5backport`) is the only one. `python -m ps5bp X` is now `ps5backport X` (same commands); `tools/import_firmware_dumps.py`, `gen_sony_builds.py` and `gen_candidates.py` are `ps5backport fwdb import-firmwares`, `data sony-builds` and `data candidates`. Its parity results live on as golden files in `src/Ps5Backport.Tests/golden/` (re-record with `PS5BP_RECORD=1` after an intended change). Older entries below mention `ps5bp` as it was then. Python is still fine for throwaway scripts outside the software.

#### Relationship map

```mermaid
flowchart TD
    subgraph console["On the PS5 (jailbroken)"]
        dec["PS5-SELF-Decrypter<br/>(payload ELF)"]
        bp_elf["ps5-backpork.elf<br/>(BestPig/BackPork loader)"]
    end

    sdk["sdk<br/>ps5-payload-sdk<br/>prospero-clang, crt, libc, sce_stubs"]
    libs320["PS5-3.20_Libs<br/>sprx_dlopen/dlsym stubs per lib"]
    kitchen["PS5-BACKPORK-KITCHEN<br/>(Windows GUI)"]
    anyps5["AnyPS5<br/>(relinker + HLE libs + shader recompiler)"]

    sdk -- "builds (Makefile includes prospero.mk)" --> dec
    sdk -- "genstub.py + aerolib.csv NID db" --> libs320
    libs320 -. "drop-in sce_stubs for 3.20 FW" .-> sdk
    dec -- "dumps plaintext ELF/SPRX to USB<br/>(game eboot.bin, /system/common/lib...)" --> kitchen
    dec -- "decrypted game ELF + sce_module/" --> anyps5
    sdk -- "make_fself.py logic ported to VB (signelf/)" --> kitchen
    kitchen -- "patched game folder + fakelib/" --> bp_elf
```

Two separate **output paths** start from the same decrypted game files:

1. **Backport path (stays on the PS5):** decrypt, then rewrite the SDK version in the ELF, fake-sign it back to SELF, and ship the newer system libraries (fakelibs) alongside. The game then runs on older firmware through `ps5-backpork.elf`.
2. **Port path (leaves the PS5):** decrypt, then relink the ELF for the host OS and replace every system library (`libkernel`, `libSceAgc`, ...) with a native implementation. The game runs as a normal PC process.

##### What the projects share

- **NIDs.** PS5 symbols are imported by an 11-character NID, not by name: `SHA1(name + 518D64A635DED8C1E6B039B1C3E55230)`, take the first 8 bytes reversed, then encode them with a custom base64 alphabet (`+-`). The same algorithm appears in:
  - `sdk/host/bin/prospero-nid.c` and `sdk/crt/nid.c`
  - `AnyPS5/core/libs/nid/src/NidCompute.cpp` and `AnyPS5/tools/nid_names.py`
  - the generated stubs in `PS5-3.20_Libs` (for example `sprx_dlsym(h, "kW3GLb7QfPg", &__ptr_sceAgcInit)`)
- **Name database.** `zecoxao/sce_symbols/aerolib.csv` (NID to name) is used by `sdk/sce_stubs/Makefile` and by `AnyPS5/tools/nid_names.py`.
- **SELF format.** Magic `0x1D3D154F` (Orbis/PS4) and `0xEEF51454` (Prospero/PS5), a 0x20-byte header, and 0x20-byte segment entries with flag bits (encrypted, signed, compressed, has blocks/digests/blockinfo). It is defined in:
  - `PS5-SELF-Decrypter/include/self.h` (real decryption on the console)
  - `PS5-BACKPORK-KITCHEN/.../Services/SelfUtilCode/SelfFile.vb` (SelfUtil port: unwraps an already-decrypted or fake SELF into an ELF)
  - `PS5-BACKPORK-KITCHEN/.../signelf/*.vb` (port of `sdk/samples/install_app/make_fself.py`: wraps an ELF into a fake-signed SELF)
- **SCE program headers.** `PT_SCE_PROCPARAM = 0x61000001` (magic `ORBI`, `0x4942524F`) and `PT_SCE_MODULE_PARAM = 0x61000002` (magic `0x3C13F4BF`). The PS4 SDK version sits at param+0x10 and the PS5 SDK version at param+0x14. Backpork patches these fields, and AnyPS5's `ElfReader` parses the same headers.
- **Library set.** The libraries AnyPS5 reimplements (`core/libs/prx/*`, 115 of them) are a subset of the libraries stubbed in `PS5-3.20_Libs` (275 of them). The 3.20 list is a useful checklist of the full PS5 system-library surface.

---

#### 1. `sdk/`: ps5-payload-sdk

**What it is:** the de-facto open SDK for PS5 homebrew payloads. The resulting ELFs are sent to an ELF loader (elfldr, websrv, BD-J, the Lua loader) on an exploited console.

| Path | Contents |
|---|---|
| `host/bin/prospero-*` | Wrappers around clang/lld/cmake/meson/pkg-config targeting `x86_64-sie-ps5`; `prospero-deploy` sends an ELF to `PS5_HOST:PS5_PORT` (9021) |
| `host/toolchain/prospero.{mk,cmake,ini,sh}` | Build-system integration; projects `include $(PS5_PAYLOAD_SDK)/toolchain/prospero.mk` |
| `host/win/` | Windows `.cmd` wrappers |
| `crt/` | Payload runtime: `_start.S`, the in-payload dynamic linker (`rtld*.c`, which loads `.sprx` via `sceKernelLoadStartModule` and resolves symbols by NID), kernel R/W helpers (`kernel.c`, `kernel_iommu.c`, `kernel_procio.c`), `klog`, `mdbg`, `patch.c` |
| `include/ps5/` | Public API: `kernel.h` (kernel R/W, FW version, auth IDs), `klog.h`, `mdbg.h`, `nid.h`, `payload.h`, `iommu.h` |
| `include/freebsd`, `libc/`, `libufs/` | FreeBSD headers, extra libc functions missing from the console libc, and a UFS library |
| `sce_stubs/` | `genstub.py` reads a decrypted `.sprx` and emits a `.c` stub; 30 stub libs are prebuilt (libkernel, libSceNet, libScePad, ...) |
| `samples/` | 23 examples. The most relevant: `install_app/make_fself.py` (fake-sign ELF to SELF), `hello_sprx`, `hello_dlfcn`, `relocatable_linking` |

**Why it matters for us:** this is the toolchain that builds the decrypter and any new on-console tooling, and `make_fself.py` is the reference for fake signing.

#### 2. `PS5-SELF-Decrypter/`

**What it is:** a payload (built with the sdk) that decrypts signed executables on the console and dumps plaintext ELFs to `/mnt/usb0/PS5/` (or `/data/dump`).

How it works (`source/`, about 1,700 lines):
- `main.c`: picks a `tailored_offsets` set from `kernel_get_fw_version()`. FW 3.00 through 10.xx are covered (3.xx, 4.xx, 5.0x, 5.5x, 6.xx, 7.0x, 7.2–7.61, 8.xx, 9.xx, 10.xx). It then walks a dump queue, and for each file:
  1. verifies the SELF header with the AuthMgr,
  2. decrypts each segment's block-info,
  3. decrypts block by block,
  4. rebuilds the ELF.
  It resumes after a kernel panic.
- `sbl.c`: talks to the Secure Boot Loader (SAMU) mailbox using kernel R/W. Translates VA to PA through a page-table walk (`pmap_kextract`) and sends `sceSblServiceRequest` messages.
- `authmgr.c`: AuthMgr service calls: `VerifyHeader`, `LoadSelfSegment`, `LoadSelfBlock`, `SmFinalize`.
- `include/self.h`, `elf.h`, `sbl.h`, `authmgr.h`: format and message structs.

Build variants (Makefile `make dist`): default, `-sock-log` (logs to `PC_IP:PC_PORT`), `-system-common-lib` (`/system/common/lib`), `-full-system` (`/system` and `/system_ex`), `-shellcore`, and `-game` (the running title).

The README documents how to find the 11 kernel `.data` offsets for a new firmware from nearby strings (`"sdt"`, `"SblDrvSendSx"`, `"req mtx"`, `"invlgn"`, `"pmap"`, and so on).

**Why it matters for us:** this is the first step for both paths. It produces the plaintext game ELF and the plaintext system SPRX files (fakelib sources, and the reference for library behaviour).

#### 3. `PS5-3.20_Libs/`

**What it is:** one generated `.c` file per system library on firmware 3.20, from `gaikai-player` through `libSceXXX` to `libkernel*`. A header says each file was generated by `genstub.py`, which is the sdk tool.

Each file:
- declares `__ptr_<func>` and an asm trampoline `<func>: jmp [rip + __ptr_<func>]` for every export,
- has a `constructor(104)` that runs `sprx_dlopen("<lib>")` and then `sprx_dlsym(handle, "<NID>", &__ptr_<func>)` for each export,
- has a `destructor` that runs `sprx_dlclose`.

**Why it matters for us:** it gives the complete export list (name and NID) of every 3.20 library. That is a ready-made **NID to name table**, which is useful to diff library exports between firmwares (what a fakelib must provide) and to label imports in Ghidra.

#### 4. `PS5-BACKPORK-KITCHEN/`

**What it is:** a WinForms GUI (`net8.0-windows`; FluentFTP, Newtonsoft.Json, ReaLTaiizor UI, SharpCompress, SQLite) that automates BestPig's **BackPork** method plus idlesauce's downgrade script. The README marks it **discontinued**.

##### Core backport flow ("Start Cooking", `Form1.vb` `BtnStart_Click`)
1. Back up the folder, then remove any old `fakelib/` from the game folder.
2. `PatchingCoordinator` walks every `eboot.bin`, `*.prx` and `*.sprx` and calls `PatchElfSmart` (`core/ElfReader.vb`):
   - If the file is already a plain ELF, patch it in place.
   - Otherwise unwrap the SELF to a temp ELF (`selfutilmodule.unpackfile`, an internal SelfUtil port), patch it, **fake-sign** it (`SignSingleElf`, from `signelf/`), and copy it back.
   - Optional "6xx" fix: in `libc.prx`, replace the string `4h6F1LLbTiw#A#B` with `IWIBBdTHit4#A#B` (an import NID swap).
3. **The patch itself** (`core/ElfPatcher.vb`): find the `PT_SCE_PROCPARAM` or `PT_SCE_MODULE_PARAM` segment, check its magic, and overwrite the PS5 SDK (+0x14) and PS4 SDK (+0x10) with the target pair from `core/VersionProfiles.vb`. Files already at or below the target are skipped.

   | FW | PS5 SDK | PS4 SDK |
   |---|---|---|
   | 1 | `0x01000050` | `0x07590001` |
   | 2 | `0x02000009` | `0x08050001` |
   | 3 | `0x03000027` | `0x08540001` |
   | 4 | `0x04000031` | `0x09040001` |
   | 5 | `0x05000033` | `0x09590001` |
   | 6 | `0x06000038` | `0x10090001` |
   | 7 | `0x07000038` | `0x10590001` |
   | 8 | `0x08000041` | `0x11090001` |
   | 9 | `0x09000040` | `0x11590001` |
   | 10 | `0x10000040` | `0x12090001` |
4. Copy `<exeDir>/<FW>/fakelib/*` into `<game>/fakelib/`. Fakelibs are newer-firmware SPRX files (for 7.xx: `libSceAgc`, `libSceAgcDriver`, `libSceNpAuth`, `libSceNpAuthAuthorizedAppDialog`, `libSceSaveData.native`, plus `ps5-backpork.elf`). Their download list is `fakelibs.json`; the URLs are base64-encoded.
5. On the console, run `ps5-backpork.elf`, which sideloads `fakelib/` in place of the system libraries.

`PatchPipeLineRules-Important.txt` defines the required order: detect type, decrypt, validate, check SDK, patch header, optional libc patch, sign, then atomically replace the original.

##### Other modules (mostly extra tooling)
- `core/PKG/` builds fake PKGs (`FPKGBuilder`, `SFOBuilder`, `PKGReader`/`Writer`).
- `core/UFS2/` and `Services/UFS2*` read and build UFS2 images (shells out to UFS2Tool).
- `Services/Firmware*` download PUPs and unpack them with `tools/pup_unpacker.exe` and `ps5_pup_decrypt.elf`.
- `Services/Ftp*`, `PayloadSender*`, `RemotePkgInstall*` talk to the console over the network.
- `Services/SigningService.vb` supports three signing modes: FreeFakeSign, NPDRM, and CustomKeys.
- `Services/GameSearch/*` contains torrent and DDL search providers. These are not relevant to us.
- `Architecture/` is a partial clean-architecture refactor (coordinators, `Result` type, adapters).

**Why it matters for us:** it is the most direct description of the backport recipe (SDK fields, fakelibs, fake sign). The VB code is readable but tightly coupled to `Form1`.

> Deep dive into the actual backport pipeline, its limits and bugs: [Part 9](#part-9-how-backpork-kitchens-general-backport-actually-works-deep-dive).

#### 5. `AnyPS5/`

**What it is:** a static **relinker**: a PS5 game ELF becomes a native Linux ELF or Windows PE, with no emulator. The x86-64 game code runs directly, and system calls go to native reimplementations of the PS5 libraries. The project is GPL-2.0. It is verified with *Dreaming Sarah* (PPSA02929) at 60 fps.

Pipeline (`core/relinker/main.cpp`):
1. `--to-intel` (optional, `codegen/x86/*`): rewrite instructions that exist only on AMD Zen 2 (SSE4a, CLZERO, SHA, `VRCPPS`/`VRSQRTPS` precision) in place, or through trampolines.
2. `RelinkerPipeline`: read imports by NID, scan for raw `syscall` instructions, optionally filter unused NIDs (`unused-filter=0/1/2`), and build a SysV `.dynamic` section.
3. `GuestModuleBuilder`: convert the bundled `sce_module/*.prx` files the same way.
4. `LinuxElfPatcher` or `WindowsPePatcher`: emit the final executable (on Windows, with `sce_sys/icon0.png` as the icon).

Runtime layout: `app.elf|app.exe`, then `libs/*.prx` (the native libraries), then `app0/` (game data plus converted `sce_module/`).

Key directories:
- `core/libs/prx/<lib>/`: 115 HLE libraries. Exports use the `APS5_VABI` calling convention, or `APS5_EXPORT("<nid>", fn)` when only the NID is known. The large ones are `libkernel` (memory, pthreads, files, equeue, sockets, modules), `libc`, `libSceAgc`/`libSceAgcDriver` (PM4 command buffers to Vulkan), `libSceVideoOut`, `libSceAudioOut`, `libScePad`, and `libSceSaveData`.
- `core/libs/nid/`: `nid_patcher`, which renames compiled exports to their NIDs after the build (ELF and PE).
- `core/shader/recompiler/`: RDNA2 GCN ISA to SPIR-V, in the stages `RdnaDecoder`, `ControlFlow`, `Translation` (IR), `Optimization` (SSA, SRT walker), and `SpirvBackend`. Results are cached on disk.
- `core/Decoder/{Png,Jpeg}`: image decoders used by the `libScePngDec` and `libSceJpegDec` implementations.
- `tools/`: `nid_names.py` (checks names and NIDs against aerolib), `progress.py` (coverage badges), `hw-oracle/` (runs RDNA instructions on real hardware to check the recompiler), and `rdna_isa.txt`.
- `docs/dev/TechnicalDebt.md` lists every silent stub. These are worth reading because they show which library behaviours a game can survive without.
- `3rdparty/` submodules (SDL2, Vulkan, glslang, SPIRV, VMA, ffmpeg, freetype, LibAtrac9, stb, libjpeg-turbo) are **empty**. Building needs `git submodule update --init --recursive`.

**Why it matters for us:** it is the best documentation available of how PS5 ELFs are laid out (dynamic tags, NID imports, module params) and of what each system function does. Its `ElfReader`, NID tooling and per-library `Export.cpp` files are a strong reference for any backporting work, especially for writing **shim/fake libraries** that cover functions missing on older firmware.

---

#### Part 2: drakmor backport toolkit

Six of the seven new folders come from one author (`drakmor`). Together they form a newer and more principled backport approach than Backpork Kitchen. Backpork ships whole newer-firmware SPRX files. This toolkit **writes small replacement libraries** that provide only what the game needs, and **patches the console** so it accepts unsigned, plaintext game data.

```mermaid
flowchart LR
    syms["sce_symbols(_new)<br/>aerolib.csv"] -- "NID names" --> tools
    subgraph tools["Shared build tooling"]
        nidren["prx_rename_nids_auto.py<br/>(export by raw NID)"]
        stubs["make_*_stub.py<br/>(weak import stubs)"]
        fself["make_fself.py<br/>(fake-sign PRX to SPRX)"]
    end
    tools --> rtc["backport-helper<br/>libSceRtc.prx + hooks"]
    tools --> pgo["pgo_stub<br/>libScePlayGo.sprx"]
    tools --> ampr["ampr_emu<br/>libSceAmpr.prx + LZ4 packs"]
    ppr["ppr-patch<br/>A53 plaintext-PFS reads"] -. "lets the console mount<br/>unsigned game images" .-> game["Backported game"]
    rtc --> game
    pgo --> game
    ampr --> game
```

##### `backport-helper/`: replacement-library template (`libSceRtc`)
- It is a complete `libSceRtc` reimplementation (`rtc.cpp`) that **also** exports functions an older firmware lacks. `backports.cpp` gates them with `#if BACKPORT_SDK <= N`; the shipped example is `sceKernelGetOperationMode` for SDK ≤ 10.
- `libSceRtc` is only the host. Almost every game loads it, which makes it a convenient carrier for exports and hooks that belong to other libraries.
- **Hook engine** (`hooks.cpp`, using the HDE64 disassembler): it detours function prologues, builds trampolines that relocate RIP-relative instructions, and installs from `module_start` with rollback. It has two kinds of hook:
  - `g_backportHooks[]` for imports that are already bound, for example a `sceKernelGettimezone` fallback to UTC.
  - `g_lateDlsymHooks[]` for modules loaded later. These are caught by hooking `sceKernelLoadStartModule` and resolved with `sceKernelDlsym`.
- **Tools:**
  - `prx_rename_nids_auto.py` rewrites exported NIDs in `.dynstr` and rebuilds the SysV `.hash`. This lets you export a function whose name is unknown, using a raw-NID map in `tools/nidmaps/`.
  - `make_kernel_extra_stub.py` and `make_libc_internal_stub.py` emit weak `ET_SCE_STUBLIB` import archives for symbols the official SDK stubs don't include.
- **Build:** Visual Studio with the **official Prospero SDK platform** (`<_rtc.h>`, `save_data/save_data_api.h`). It cannot be built with the open payload-sdk as-is. `Release` produces `out/sdk4…sdk13/libSceRtc.prx`.

##### `pgo_stub/`: `libScePlayGo` replacement
- It implements 24 `scePlayGo*` exports with optimistic answers: initialized, every chunk at locus "local fast", install complete, every language available.
- Optional `/app0/playgo_stub.dat` sets the chunk count or list, the scenario count, and the language mask (names, IDs, or a `0x` hex mask). The `ReleaseLog` build writes `/app0/playlgo.log`. Note the "lgo" typo in that filename.
- A post-build step runs `tools/make_fself.py` to produce a fake-signed `.sprx`. The sources use only libc headers, but the project is still MSBuild/Prospero only.

##### `ampr_emu/`: software `libSceAmpr`
- AMPR is the PS5's asynchronous I/O and memory-mapping command-buffer API: **APR** reads files into memory and **AMM** maps memory. `src/sceampr_exports.cpp` implements about 120 exports (`sceAmprCommandBuffer*`, `sceAmprAprCommandBuffer*`, `sceAmprAmm*`, ...). It runs them in software with an APR reactor and an equeue bridge, on top of `sceKernelAioSubmitReadCommandsMultiple` and `sceKernelApr*`, which are hooked in libkernel.
- **Asset packs:** a second feature compresses `/app0` into seekable LZ4 packs (`AMPRPAK4`, index `AMPRIDX3`, runtime config `AMPRCFG1`). Reads are optionally intercepted process-wide (`open/read/pread/AIO`). The workflow is: record traces (`ampr_commands.bin`), profile them into TOML (`tools/ampr_pack_profile.py`), pack (`tools/ampr_pack.py`, or the GUI), and verify. Its purpose is to shrink games so they fit.
- **Build:** this is the only drakmor project with an open-toolchain build. The `Makefile` uses ps5-payload-sdk with its own CRT (`ps5/crt`), a linker script (`ps5/prx/prx.script`), and NID/hash fix-up tools. A CI workflow is included. `libSceAmpr.sln` provides the official-SDK build.
- `tests/` contains on-console smoke, stress, negative and PRX-boundary tests.

##### `make_fself/`
- `make_fself_batch.py` is a superset of `sdk/samples/install_app/make_fself.py`. It adds:
  - directory batch signing in place,
  - `--unpack` (SELF/FSELF to ELF, with `.selfbak` backups),
  - repair of incomplete `.sceversion` records,
  - automatic derivation of PAID and ptype.
- For our work, this is the most useful signer.

##### `ppr-patch/`: A53 plaintext PFS reads
- The A53 is an ARM core in the storage path (MP4, "PPR"). Normally every 64 KiB package read goes through `zcnDriverAddCommandsPackageDecryptToZcnBufferAndVerifySign` (AES-XTS plus SHA3-CMAC).
- The payload redirects the **ten** callers of that helper to a wrapper. The wrapper switches to `PLAINTEXT_NOAUTH` only when the kernel passes the exact key-index pair `AES=0xff, SHA=0xfe`; every other read stays native.
- It has 55 layout profiles covering 89 firmware/target mappings (1.00–11.40, retail/testkit/devkit). It fails closed on anything unknown and requires `--idle`.
- A separate `a53_kmb_range_*` payload removes the opcode-0x53 key-slot whitelist check.
- Its doc says the matching **kernel-side** marker is not part of this repo. Something else has to set `0xff/0xfe` for plaintext mounts.

##### `sce_symbols/` and `sce_symbols_new/`
- Both use the same format: `aerolib.csv` (`<NID> <name>`), the `*_names.txt` lists, and the `*_names2nid.py` generators.
- `sce_symbols_new` is the **current upstream** and contains every line of the drakmor fork, plus 4,583 hash-verified PS5 names across 167 libraries.
- AnyPS5's `nid_names.py` pins upstream commit `2883963`, which is exactly the `sce_symbols_new` HEAD.

##### Review findings

| # | Severity | Finding |
|---|---|---|
| 1 | **High** | **`ppr-patch` is incomplete.** The `Makefile` needs `kmb_range_main.c`, `kmb_range_patch.{c,h}`, `kmb_range_patch_test.c`, `kmb_range_profiles.inc`, `generate_kmb_range_profiles.py` and `verify_kmb_range_profiles.py`. None of them are in the repo, and the build's dependency rules list `kmb_range_patch.h`, so `make` / `make all` (which runs `verify` and `host-test` first) will fail. The `a53_ppr_*` sources are present, but extracting them requires editing the Makefile. `make profiles` and `make verify` also need the private `PPR_ROOT` archive of A53 images, which is not included. |
| 2 | Medium | **`backport-helper` and `pgo_stub` build only with the official Sony Prospero SDK** (MSBuild platform, SDK headers such as `<_rtc.h>`). With only ps5-payload-sdk, they need porting first. `ampr_emu`'s Makefile, CRT and linker script show how to do that port. |
| 3 | Low | **Latent bugs in upstream `make_fself.py`** (identical in `sdk/` and `pgo_stub/tools/`, also present in `make_fself_batch.py`): (a) `is_intervals_overlap` compares `p1[1] <= p2[0]` instead of `p2[0] <= p1[1]`; (b) `segment_index` is declared with `@wbits.setter`, so **reading** `segment_index` returns `wbits`. Neither breaks signing today: the function is never called, the property is only written, and the batch tool reads `props` directly. `ampr_emu/tools/make_fself.py` fixes both, so use it as the base copy. |
| 4 | Low | **Duplication across repos:** 4 copies of `make_fself`, 2 diverged copies of `hde64.cpp` (backport-helper and ampr_emu), and 2 independent pairs of `make_*_stub.py`. A shared tools folder would avoid drift. |
| 5 | Low | `sce_symbols/` is redundant. Use `sce_symbols_new/` everywhere; `sdk` and AnyPS5 already point at upstream. |
| 6 | Info | Doc issues: `ampr_emu/README.md` is two lines (the real docs are in `docs/`). `ppr-patch/README.md` links `../PPR_READ_PATHS.md`, but the file is in the same folder. The pgo_stub log name is `playlgo.log`. |

##### Where this fits in the overall picture
- Backpork Kitchen's **SDK-field patch and fake signing** are still required, and `make_fself_batch.py` replaces Backpork's VB signer.
- Backpork's **whole fakelib SPRX files** can be replaced by small purpose-built shims: `pgo_stub`, `ampr_emu`, and `backport-helper`-style libraries that export missing NIDs or hook failing calls. AnyPS5's `core/libs/prx/*` is a good reference for writing such shims.
- `ppr-patch` covers the separate problem of **mounting unsigned or plaintext game images** on the console.

---

#### Part 3: GT7 1.71, before and after the backport

Two copies of **Gran Turismo 7** (`PPSA01317`, content ID `UP9000-PPSA01317_00-GRANTURISMO70000`, version `01.710.000`) are available. Comparing them byte by byte shows exactly what a backport changes.

| Folder | Role | SDK fields in the binaries |
|---|---|---|
| `.original/NOT_BACKPORT/` | **Before**: the original, not backported (confirmed by the user) | `0x10000040` / `0x12090001`, the same values as Backpork's **FW 10** pair |
| `.original/GT7/USA/` | **After**, backported to **FW 4.xx** | `0x04000031` / `0x09040001`, which is exactly Backpork's **FW 4** pair |

> ✅ **Confirmed by the user:** NOT_BACKPORT is **not** a backport, and **both copies run on a PS5** (each on its intended firmware). So every patch listed below is known to work in practice.
>
> **SDK values are not the same thing as the firmware requirement.** The original binaries carry SDK `0x10000040` / `0x12090001`, while `param.json` says `sdkVersion 0x13…` and requires FW 13.60. The SDK values in the binaries record which SDK the game was compiled with; the package-level `param.json` sets the firmware the console requires. Backpork's FW 10 pair is therefore simply real values taken from shipped games. Don't use the binaries' SDK fields to guess a game's firmware requirement; read `param.json`.
>
> NOT_BACKPORT's `fakelib/` contains `libSceAmpr` and `libScePlayGo`. *(Correction 2026-10-08: on disk `libSceAmpr.sprx` is **0 bytes** (dated 2026-06-26); only `libScePlayGo.sprx` is byte-identical to the FW 4 copy. Tools see the empty file as kind `unknown`; `backport` replaces it with the store's `libSceAmpr.sprx`.)* **These are not part of a backport.** They were added so the game runs under **ShadowMount** with **`ampr_emu`** (confirmed by the user). So the AMPR/PlayGo replacements are a requirement of running the game from a ShadowMount mount, on any firmware; the backport's own additions are only `libSceAgc`, `libSceAgcDriver`, `libScePsml`, `k9.psp` and `ampr_emu.index`.
>
> ShadowMountPlus is now in `reference/` and covered in [Part 4](#part-4-shadowmountplus-the-runtime-that-loads-fakelibs). It skips these replacements for real installed packages, so they are only needed when running from a dump.

##### Files

| Path | NOT_BACKPORT | GT7/USA (FW 4) | What it is | Ghidra |
|---|---|---|---|---|
| `eboot.bin` (105 MB) | PS5 SELF `0xEEF51454` | PS4-style SELF `0x1D3D154F` | Main executable (ELF type `0xFE10`, `ET_SCE_EXEC`; internal name `application_gt7`) | Yes, unwrapped |
| `libSieRdNNabla.prx` (30 MB) | PS5 SELF | PS4-style SELF | Game library; the name suggests Sony's "Nabla" neural-network runtime (probably the AI driver) | Yes, unwrapped |
| `logiWheel.prx` (330 KB) | PS5 SELF | PS4-style SELF | Game library: Logitech steering-wheel support | Yes, unwrapped |
| `sce_module/libc.prx`, `libScePfs.prx`, `libSceNpCppWebApi.prx` | PS5 SELF | PS4-style SELF | Libraries bundled with the game (SELF containers despite the `.prx` extension) | Yes, unwrapped |
| `sce_sys/about/right.sprx` | PS5 SELF | PS4-style SELF | Standard rights module (ELF header ABI-version byte is 3; the others have 2) | Yes, unwrapped |
| `fakelib/libSceAmpr.sprx`, `libScePlayGo.sprx` | present | present, **byte-identical** | Software AMPR (`ampr_emu`) and PlayGo replacements (SDK `0x02000009`), needed to run under **ShadowMount** rather than for the backport | Yes, unwrapped |
| `fakelib/libSceAgc.sprx`, `libSceAgcDriver.sprx` | absent | PS5 SELF, fake-signed | Replacement GPU libraries | Yes, unwrapped |
| `fakelib/libScePsml.sprx` | absent | PS4-style SELF | PSSR (PS5 Pro upscaler) runtime | Yes, unwrapped |
| `k9.psp` (3.9 MB) | absent | `PSP\0` header, then weights | **PSSR upscaler model**. Loaded by `libScePsml` from `/app0/k9.psp`; contains layer names such as `E2R0FasterNet`. `libScePsml` also references `MFSR4K_v070.psp` and `MFSR8K_v070.psp`. | No, data |
| `ampr_emu.index` (8.5 MB) | absent | `AMPRIDX3` | `ampr_emu` file-ID index for this exact `/app0` | No, data |
| `sce_sys/param.json` | present | present, 4 fields changed (see below) | Game metadata | No |
| `sce_sys/` other files | `icon0/pic0-2` (`.png`/`.dds`), `keystone`, `license.dat/.info`, `nptitle.dat`, `pfs-version.dat` (`01.710.0`), `ext_info.dat`, `trophy2/`, `uds/` | absent | Package metadata, trophies, license | No |

All SELFs in both folders are **fake-signed with no encrypted segments**. NOT_BACKPORT uses the PS5 container (`version 16`, key type `0x10000101`, flags `0x32`); the FW 4 copy was re-signed with the PS4-style container that `make_fself.py` produces (`version 0`, key type `0x101`, flags `0x22`). The unwrapped ELFs have identical lengths between the two copies.

##### Loading the binaries in Ghidra
- None of the `.sprx`, `.prx` or `.bin` files are plain ELFs. Ghidra doesn't recognise the SELF container, so it falls back to "raw binary" and asks for a language, and the result is useless.
- All of them unwrap on a PC:
  ```bash
  python .original/reference/make_fself/make_fself_batch.py --unpack input.sprx output.elf
  ```
- Unwrapped copies:
  - FW 4 copy: `work/GT7_root_elf/`, `work/GT7_fakelib_elf/`, `work/GT7_sce_module_elf/`, `work/GT7_sce_sys_elf/`
  - NOT_BACKPORT: `work/NOT_BACKPORT_elf/`, which mirrors its folder layout

  The originals are unchanged.
- Import settings: language **`x86:LE:64:default`**, compiler spec **`gcc`**. Ghidra may warn about the SCE file type (`0xFE10` or `0xFE18`); accept the warning.
- Imports and exports appear as 11-character NIDs. Resolve them with `reference/sce_symbols_new/aerolib.csv`, or with `work/tools/sce_elf.py` (below).

##### PS5 ELF dynamic linking (learned while diffing)
- PS5 ELFs use the **standard `DT_*` tags**: `DT_JMPREL`, `DT_RELA`, `DT_SYMTAB`, `DT_STRTAB`. Their values are virtual addresses inside a non-executable `PT_LOAD` (in `eboot`: file offset `0x5d22ed0`, vaddr `0x7156ed0`). PS4 used `DT_SCE_*` offsets into `PT_SCE_DYNLIBDATA`; on PS5 only `DT_SCE_SYMTABSZ` (`0x6100003F`) is still needed, for the symbol count.
- Symbol names are `<NID>#<library-id>#<module-id>`. The IDs use the NID base64 alphabet as **base-64 digits**: `s`=44, `9`=61, `BM` = 1×64+12 = 76. They match the IDs in the `0x61000049` (import library) and `0x61000045` (needed module) dynamic tags. Examples in `eboot`: 44 = `libSceVideoOut`, 61 = `libSceVideoOutVrrStatus`, 76 = `libkernel`, 74 = `libSceDeviceService`.
- `0x61000043` holds the module's own name (`application_gt7`, `libc`). `0x61000041` holds the build path, for example `C:/pdi/src/gt7/maintenance/build-ppr.<hash>/application/gt7/Product/application_gt7.elf` and `W:/Build/J03848509/sys/internal/usermode/src/libc/Prospero_Release/libc.prx`.
- `eboot` imports 80+ libraries. Ones that matter for backporting include `libSceAgc`/`libSceAgcDriver`, `libSceAmpr`, `libScePsml` (+ `_quality8K`, `_specularAA`, `_mfsr2`), `libSceVideoOutVrrStatus`, `libSceHmd2`, `libSceVrTracker2`, `libSceTextToSpeech2`, `libSceNpTrophy2`, `libSceAudioOut2`, `libSceDeviceService`, `libSceAudioOutDeviceService`, `libMozaSDK` (Moza wheels) and `libSceUserServicePlatformPrivacyWs1`.
- **Tool:** `work/tools/sce_elf.py` parses all of this. `--imports` lists the PLT/GOT imports with names, and `--got <vaddr…>` says which import owns a GOT slot.

##### What the backport to FW 4 changed (byte diff)

**Every binary:** the SDK pair in the `PT_SCE_PROCPARAM` / `PT_SCE_MODULE_PARAM` segment changed from `0x10000040`/`0x12090001` to `0x04000031`/`0x09040001`. `libSieRdNNabla`, `logiWheel`, `libScePfs`, `libSceNpCppWebApi` and `right` have **no other change**.

**`eboot`: 6 imports neutralised by rewriting their GOT slots, plus 1 data patch.**
Two small stubs were written into `int3` padding in `.text`:
- `0x3d2`: `xor eax,eax; ret` (return 0)
- `0x573`: `mov dword [rdi],2; and dword [rsi],0; xor eax,eax; ret`

The initial GOT values of these imports were then pointed at the stubs instead of their PLT lazy-bind stubs:

| GOT slot | Import (NID) | Library | Now goes to |
|---|---|---|---|
| `0x55df470` | `sceKernelGetOperationMode` (`NH6xARDOVv8`) | libkernel | `0x573`: `*mode = 2`, `*lowEnergy = 0`, return 0 |
| `0x55df670` | `sceVideoOutVrrPegToFixedRate` (`5tRaBjtdTzY`) | libSceVideoOut | `0x3d2` (return 0) |
| `0x55df678` | `sceVideoOutVrrUnpegFromFixedRate` (`T4ucGB8CsnM`) | libSceVideoOut | `0x3d2` |
| `0x55df688` | `kP2L8t3j-aM` (unnamed; AnyPS5 calls it `sceVideoOutUnknown00`) | libSceVideoOutVrrStatus | `0x3d2` |
| `0x55df6b8` | `LibwuIonIBw` (unnamed; AnyPS5 `sceVideoOutUnknown01`) | libSceVideoOutVrrStatus | `0x3d2` |
| `0x55e0d48` | `La9uyZv4Kvw` (unnamed) | libkernel | `0x3d2` |

The data patch: at vaddr `0x83403a`, `mov qword [rdi+0x18], -1` became `mov qword [rdi+0x18], 0`. The purpose is unknown.

In effect, imports that are missing on FW 4 are pre-resolved to local stubs, so they never reach the dynamic linker. **Confirmed working on console**: the loader in this setup leaves those GOT entries alone when it can't resolve the NID. Whether that is the stock PS5 loader's behaviour (lazy binding) or something the backpork loader does is not yet known. Note that `sceKernelGetOperationMode` gets operation mode 2 here, while `backport-helper` returns 0 for the same function.

**`sce_module/libc`: 6 changes, all about imports that are missing on old firmware.**
1. **NID swap `[0x14]`:** `4h6F1LLbTiw` (`sceKernelMapNamedFlexibleMemoryInternal`) became `IWIBBdTHit4` (`sceKernelMapFlexibleMemory`). This is exactly Backpork's optional "6xx libc patch"; the extra name argument is simply ignored.
2. **NID swap `[0x74..0x77]`:** `sceKernelSyncOnAddressWait32`, `…Wait64`, `…Wake` and `…Wait` were all renamed to `04AjkP0jO9U` (`_umtx_op`, the FreeBSD futex syscall wrapper that old firmware has). Then the **PLT stubs were rewritten** to translate the arguments:

   | Original import | New PLT code | Resulting `_umtx_op` call |
   |---|---|---|
   | `SyncOnAddressWait32(addr, val, timeout)` | `mov r8,rdx; mov edx,esi; rsi=0xb` | `_umtx_op(addr, UMTX_OP_WAIT_UINT=11, val, 0, timeout)` |
   | `SyncOnAddressWait64(addr, val, timeout)` | `mov r8,rdx; mov rdx,rsi; rsi=2` | `_umtx_op(addr, UMTX_OP_WAIT=2, val, 0, timeout)` |
   | `SyncOnAddressWake(addr, count)` | `movsxd rdx,esi; rsi=3; r8=0` | `_umtx_op(addr, UMTX_OP_WAKE=3, count, 0, 0)` |
   | all three, then `SyncOnAddressWait` | shared tail at `0x113750`: `mov r9d,-1; xor ecx,ecx; jmp [GOT 0x77]` | `_umtx_op` through slot `0x77` |

   Slot `0x77`'s lazy-bind stub (`push 0x77; jmp PLT0`) was moved into padding at `0x112443`, and GOT `0x18e510` was repointed to it.
3. **Call retarget:** `__cxa_thread_atexit` (`BKSCW2bCACA`, at `0x7dd20`) used to call import `qBS714-Jr3g` from `libSceLibcInternalExt` (unnamed; probably `__cxa_thread_atexit_impl`). It now calls libc's own `__cxa_atexit` (`tsvEmnenz48`, `0x113e0`). Thread-exit destructors therefore become process-exit destructors, which is a small change in meaning.

**Ghidra verification of the libc patches** (2026-10-08, on `work/GT7_sce_module_elf/libc.elf`; the thunks are renamed `bp_thunk_*` and commented in the Ghidra project):

- **Who calls the thunks:** the MSVC-STL style atomic-wait exports in libc. `__std_atomic_wait_direct_32` and `_64` call the Wait32/Wait64 thunks. `__std_atomic_notify_one_direct` (count 1), `__std_atomic_notify_all_direct` (count `0x7fffffff`), `BS5uazcBa-k` (unnamed) and `__std_init_once_complete_clr` call Wake. `__std_init_once_begin_initialize_clr` calls the generic Wait directly through the shared tail.
- **Original signatures, reconstructed from the callers:**
  - `int sceKernelSyncOnAddressWait32(volatile u32* addr, u32 expected, u32* timeout_us /*NULL = infinite*/, const char* name)`
  - `…Wait64` takes the same arguments with a `u64` value.
  - `…Wake(void* addr, int count)`.

  The callers convert milliseconds to microseconds (`imul 1000`, saturating to `0xFFFFFFFF`). `name` is `"SceLibcV2AtomicWait"` (`0x1520d0`).
- **How faithful the translation is:**

  | Thunk | Verdict |
  |---|---|
  | Wake → `UMTX_OP_WAKE` | Exact |
  | Wait32/Wait64 with no timeout | Exact |
  | Wait32/Wait64 **with a timeout** | **Lossy.** The thunk passes the pointer to a `u32` microsecond count as `uaddr2` with `uaddr1 = 0`, so (with FreeBSD semantics) the kernel reads a 16-byte `struct timespec` from it: the µs value plus stack bytes. The result is very likely `EINVAL`/`ETIMEDOUT` straight away, i.e. a spurious wake-up. Callers re-check the value, so it still works, but it spins. |
  | Generic Wait (init-once) | **Lossy.** The tail passes `rsi` (= 1, the expected value) through as the **op code**. Op 1 is the old `UMTX_OP_UNLOCK` (unimplemented on newer FreeBSD), so it fails at once and `__std_init_once_begin_initialize` busy-spins until the other thread finishes. |

  The FreeBSD `_umtx_op` semantics are assumed; the PS5 kernel was not checked. This is **acceptable in practice (the game runs)**, but **our tool can do better**: a correct thunk would convert µs to a timespec on the stack and set `uaddr1 = sizeof(struct timespec)`, and would map generic Wait to `UMTX_OP_WAIT`. That needs more code than fits in the PLT slots, so it belongs in padding (a code cave) or a shim PRX.

**Ghidra verification of the eboot patches** (2026-10-08, on `work/GT7_root_elf/eboot.elf`). Only the game ↔ library interface was analysed; game logic is out of scope because it differs per game. The stubs are labelled `bp_stub_*` in Ghidra.

| Library function | How the game uses it | Stub | Safe? | Reusable rule |
|---|---|---|---|---|
| `sceKernelGetOperationMode(int* mode, int* lowEnergy)` | 3 call sites set flags `mode == 3` and `lowEnergy == 1` | `*mode = 2`, `*lowEnergy = 0`, return 0 | Yes: "normal mode, not low-energy" | Stub with mode ≠ 3 and lowEnergy = 0. `backport-helper` returns mode 0, which is equally fine for this check. |
| `sceVideoOutVrrPegToFixedRate(handle, 0, 0)` / `sceVideoOutVrrUnpegFromFixedRate(handle)` | Called from one toggle function; **return value ignored** | return 0 | Yes; VRR pegging just doesn't happen | return-0 stub |
| `kP2L8t3j-aM`, `LibwuIonIBw` (libSceVideoOutVrrStatus) | **No direct callers found** (imported but apparently unused, or called through a pointer) | return 0 | Yes, as far as visible | return-0 stub; the library doesn't exist on FW 4 |
| `La9uyZv4Kvw` (libkernel, unnamed) | No arguments; the int result is passed on as a value at 2 sites | return 0 | Probably; meaning unknown | return-0 stub |
| **`sceSaveDataMount3`** | `SaveDataMount3` struct built on the stack | **Argument patch**, not a stub: `system_blocks` (`+0x18`) changed from `-1` to `0` | Works on console | Newer SDKs pass `system_blocks = -1`, which FW 4 presumably rejects. This applies to every game, but the struct is filled inline in game code, so automatic patching means finding each call site. **A shim/hook that rewrites `-1 → 0` inside `sceSaveDataMount3` is the generic solution**, and `backport-helper`'s example late hook is exactly `sceSaveDataMount3`. |

`SaveDataMount3` layout (from AnyPS5 `SceTypes.hpp`, matching the game code): `+0x00 user_id`, `+0x08 dir_name*`, `+0x10 blocks` (the game clamps it to 0x30..0x4000), `+0x18 system_blocks`, `+0x20 mount_mode` (RDONLY 1, RDWR 2, CREATE 4, CREATE2 32), `+0x28 resource`, then 32 reserved bytes.

**A patch that was already there:** at `0x834048`, `xor ecx,ecx; nop` overwrites the computed `setnc` result, so `mount_mode` is **always 1 (RDONLY)**. This patch is present in **both** NOT_BACKPORT and the FW 4 copy, so it isn't part of the FW 4 backport. Its origin and purpose are unknown (see the open questions).

**A fourth patch pattern** joins the list: **patching a library-call argument value at the call site** (`system_blocks -1 → 0`). The others were NID swap, PLT thunk, GOT→stub and call retarget.

**NOT_BACKPORT is not a pristine dump: it already contains three hand patches** (found 2026-10-08; they are identical in the FW 4 backport):
1. eboot `0x834048`: `SaveDataMount3.mount_mode` forced to 1 (RDONLY), noted above.
2. eboot PLT `0x3f0a460` (`std::_Mtx_lock`, `5qXct3c1skg`) and `0x3f0a470` (`std::_Mtx_unlock`, `4bp9gcNLwMI`) are redirected (`jmp rel32; nop`) to guard wrappers in padding after a `ret` (`0x9ab3e3`, `0x9ab3f9`): `mov rax,rdi; shr rax,0x2f; jne <xor eax,eax; ret>; jmp [GOT]`. So a mutex pointer outside the user address range (bit 47 or higher set) is skipped (returns 0) instead of locked.

These look like compatibility fixes made when the dump was prepared, for example for running from a dump. Their author and purpose are unknown. `ps5bp` reports such entries as `patched`.

##### eboot patch checklist (generalised from the complete GT7 diff)

The full byte diff of `eboot.elf` (NOT_BACKPORT vs FW 4) has **exactly 11 changed ranges**, and every one is explained above. For any game, an eboot backport consists of:

1. **SDK pair** in `PT_SCE_PROCPARAM` (`+0x10` PS4 SDK, `+0x14` PS5 SDK) → the target firmware pair. *(GT7: 2 ranges)*
2. **For every import whose NID doesn't exist on the target firmware:**
   - Find its GOT slot (JMPREL relocation; `sce_elf.py --imports`).
   - Write a stub into `int3` padding in `.text` (`0xCC` runs between functions; GT7 used `0x3d2` and `0x573`).
   - Set the slot's **initial GOT value** to the stub's address.
   - Default stub: `xor eax,eax; ret`. Getter functions with out-parameters need a stub that fills them with safe values (for example `GetOperationMode`: mode 2, lowEnergy 0).
   - Reuse one stub for every import that only needs "return 0". *(GT7: 2 stubs + 6 GOT slots)*
   - Before stubbing, check how the game uses the result (return value ignored? out-params read?). Keep a per-function rule table so later games reuse the decision.
3. **For every library call whose argument values the target firmware rejects:** patch the value. GT7: `sceSaveDataMount3` `system_blocks -1 → 0`, 1 range. This is preferably done in a shim hook rather than in game code.
4. Re-sign as fake SELF.

Things **not** changed in eboot: the dynamic section, NIDs (NID swaps were only needed in libc), code size, section layout and the import library list. The patches fit entirely in existing padding and existing slots.

##### fakelib analysis (step 1.3)

Tool: `work/tools/import_check.py` checks every function import against a reference firmware's export list (`PS5-3.20_Libs`, 31,275 NIDs). Data imports such as `__stack_chk_guard`, `_Stdout` and vtables are listed separately, because the stub lists only contain functions. **Caveat:** 3.20 is older than 4.xx, so "not on 3.20" means "possibly missing on 4.xx" until we have real 4.xx export lists.

| fakelib | Origin (embedded build path) | Function imports not on 3.20 | Patched? |
|---|---|---|---|
| `libSceAgc` | Sony `orbis-build_0005c`, job `J03828141` | 20 | **Yes**: 8 GOT slots → stub `0x26e3` (`xor eax,eax; ret`): `sceKernelIsTrinityMode`, 2 unnamed libkernel, all 5 `sceShaderTranscode*` |
| `libSceAgcDriver` | same build as `libSceAgc` | 5 | **Yes**: 2 GOT slots → stub `0x450`: `__sce_v2::_Xlength_error`, `aj7wRdAWFeM` |
| `libScePsml` | Sony job `J03950001` (a different build) | 67 | **Yes**: 25 GOT slots → stub `0x4070`. These are libc++ `__sce_v2` internals (`_Mtx_init/lock/unlock/destroy`, `_Throw*`, `_Locinfo*`, `_Getctype`, `_Toupper`…), `sceKernelIsTrinityMode`, `sceKernelTitleWorkaroundIsEnabled2`, `sceVideoOutGetHandleByBus` and `…SysCursorSetSize` |
| `libSceAmpr` | `C:/dev/ps5/ampr_emu/…/Prospero_ReleaseHooks` = **drakmor's ampr_emu** (confirmed) | 0 | Not needed |
| `libScePlayGo` | `C:/dev/ps5/playgo/./out` = presumably **drakmor's pgo_stub** | 0 | Not needed |

Findings:
- **Why `libSceAgc` and `libSceAgcDriver` are shipped:** `libScePsml` (PSSR) imports 205 `libSceAgc` and 5 `libSceAgcDriver` functions, 36 of them newer than 3.20. **The fakelib `libSceAgc`/`libSceAgcDriver` export all 210.** `eboot`'s 63 AGC imports (9 newer than 3.20) are also fully covered. So the newer GPU libraries come along as **dependencies of the newer libraries the game uses**.
- **The fakelibs are patched exactly like `eboot`:** a shared `xor eax,eax; ret` stub in padding, with missing imports' GOT slots pointed at it. Same pattern, same tool.
- **A fifth technique, symbol redefinition** (found while building `ps5bp`): in the fakelibs, the stubbed imports' **symbols** were also rewritten into local definitions (`st_shndx = 1`, `st_value` = the stub address). The dynamic linker then resolves the import inside the module. Some imports were neutralised **only** this way, with the GOT untouched: `qSleeVC6lwI` in `libSceAgc`, and 11 in `libScePsml`, including the data import `std::cout` (`9Qe7XFSQ4Lk`). Totals per unique NID: `libSceAgc` 9 stubbed (8 GOT + 1 symbol-only), `libSceAgcDriver` 4, `libScePsml` 33 (36 relocations). `eboot` uses the GOT technique only. Symbol redefinition also covers RELA/data imports, which a GOT redirect cannot. Consequence for tools: such symbols look like exports unless exports are restricted to the module's own export-library ids (dynamic tag `0x61000047`).
- **Signing:** GT7's SELFs were made with **upstream `make_fself.py`** defaults (PAID `0x3100000000000002`, ptype fake, versions 0). Unwrapping and re-signing reproduces them byte for byte, except the ex-info **digest**, which is a SHA-256 of the whole original ELF file. It can't be recomputed when the original had data outside its segments (section headers), and it evidently isn't checked, since GT7 works with digests from its own ELFs. NOT_BACKPORT's PS5-container SELFs (`54 14 F5 EE`, 48 bytes smaller) came from neither `make_fself.py` nor `make_fself_batch.py`. Common header bytes 4–7 (version, mode, endian, attributes): PS4-style `00 01 01 12`, PS5-style `10 01 01 12`; both have 12 segment entries for these modules, and the same unpacker handles both (the C# port checks the endian at byte 6).
- **Risky stubs in `libScePsml`:** `std::mutex` internals (`_Mtx_lock`/`_Mtx_unlock`) now **do nothing**, which makes them unsafe if PSSR runs multi-threaded. It is presumably harmless because PSSR only runs on PS5 Pro hardware, and that path is never active on a FW 4 console.
- **Only called imports were stubbed.** After counting 3.20 exports plus everything shipped with the game (`fakelib/*`, `sce_module/*`, `libSieRdNNabla`, `logiWheel`) as available, the imports that remain **unresolved and unpatched** are:
  - `eboot`, 30: the whole PS VR2 stack (`libSceHmd2` ×16, `libSceVrTracker2` ×5), `libSceTextToSpeech2` ×7, `sceShareGetRunningStatus`, and `D-CzAxQL0XI` (`libSceUserServicePlatformPrivacyWs1`)
  - `libSceAgc`, 2: `sceKernelGetProsperoCompiledSdkVersion`, `sceKernelGetToolMemoryRange`
  - `libSceAgcDriver`, 1: `sceKernelIsThreadSanitizerEnabled`
  - `libScePsml`, 3: `sceKernelGetToolMemoryRange`, `sceKernelGetProsperoCompiledSdkVersion`, `sceVideoOutGetPipelineStatus`
  - `libc`, 6: including `qBS714-Jr3g`, whose call was retargeted, so it is no longer called

  The game runs anyway. **This strongly suggests the loader binds PLT imports lazily, so an unresolved import only fails if it is actually called.** This answers the earlier loader question in practice, though the mechanism (stock lazy binding vs kstuff/ShadowMountPlus) is unverified. Caveats: some of these may exist on 4.xx; and AnyPS5 notes GT7 calls `sceTextToSpeech2Initialize` and skips narration if it fails, so either TTS2 exists on 4.xx or that call is behind a check. Real 4.xx export lists will settle this.
- **What this means for our tool:** "missing import" alone is not enough to decide what to patch. The real rule is **missing *and* reachable/called**. Options:
  1. Stub **every** missing import. This is safe, because a return-0 stub is never worse than an unresolved import that crashes when called. It's the easiest default, but it hides real missing features.
  2. Stub only imports with call sites, found by static xrefs.
  3. Use a per-function rule table built from analysed games: stub, adapter or shim.

  Recommended: option 1 by default, plus the rule table for functions that need non-zero results or argument fixes.

**`sce_sys/param.json`:** the SDK and firmware fields were **not** changed (`sdkVersion 0x13…`, `requiredSystemSoftwareVersion 0x1360…` in both copies). Only GT7's own `userDefinedParam1..4` changed:

| Field | NOT_BACKPORT | FW 4 |
|---|---|---|
| `userDefinedParam1` | `531705937` (`0x1FB12A51`) | `536870912` (`0x20000000`) |
| `userDefinedParam2` | `11` | `20` |
| `userDefinedParam3` | `22` | `30` |
| `userDefinedParam4` | `1` | `2` |

What these mean to the game is unknown.

**Container and folder:**
- Every SELF was re-signed in the PS4-style fake-SELF format, which is what `make_fself.py` produces.
- `fakelib/` gained `libSceAgc`, `libSceAgcDriver` and `libScePsml`.
- `k9.psp` and `ampr_emu.index` were added at the root.
- Most of `sce_sys/` (icons, license, keystone, trophies, uds, `nptitle.dat`) was dropped; only `param.json` and `about/right.sprx` remain.

##### The backport recipe, as observed
1. Start from decrypted (fake-signed) binaries.
2. Patch the SDK pair in every executable/module to the target firmware (Backpork's table).
3. **Neutralise imports that don't exist on the target firmware:**
   - by **swapping the NID** to an older equivalent (libc `MapNamedFlexibleMemory` → `MapFlexibleMemory`),
   - by **swapping the NID and rewriting the PLT stub** to adapt arguments (`SyncOnAddress*` → `_umtx_op`),
   - by **retargeting the call** to a local function (`__cxa_thread_atexit`),
   - or by **pointing the GOT slot at a local return-0 stub** (eboot VRR, operation mode, etc.).
4. Ship replacement libraries in `fakelib/`: for the backport, `libSceAgc`, `libSceAgcDriver` and `libScePsml` (with `k9.psp`). The AMPR (`ampr_emu`, with `ampr_emu.index`) and PlayGo replacements are there for **ShadowMount**, not for the firmware downgrade.
5. Re-sign everything as fake SELF.
6. Run it with **ShadowMountPlus**, which mounts the dump and overlays `<game>/fakelib/` over the sandbox's `common/lib` (see [Part 4](#part-4-shadowmountplus-the-runtime-that-loads-fakelibs)). The older standalone `ps5-backpork.elf` does the same job, but must not run together with ShadowMountPlus.

Steps 1, 2 and 5 are what Backpork Kitchen automates. **Step 3 is manual work that none of the reference tools do**, apart from Backpork's single libc string patch. It is the main thing our tooling would need to add: automatic detection of missing NIDs per target firmware, plus a library of these patch patterns.

##### Relevance to the other references
- **Backpork Kitchen** has SDK pairs only up to FW 10 and has no 13.x profile. Its FW 4 pair is the one used here, and its FW 10 pair equals the original GT7 binaries' SDK values.
- **backport-helper** covers SDK 4–13 and is the hook-based alternative to the GOT and PLT patches above. Its `sceKernelGetOperationMode` example is the same function the eboot patch stubs out.
- **AnyPS5** already targets this title. `docs/dev/TechnicalDebt.md` mentions `PPSA01317` (the PS VR2 `libSceHmd2` and `libSceTextToSpeech2` start-up paths), and its `libSceVideoOut` names the two unnamed VrrStatus NIDs patched here.
- **PS5-3.20_Libs** and the firmware-specific export lists are what step 3 needs, to know which NIDs exist on the target firmware.

##### Open questions
- Is our reading of why dumps need `ampr_emu`/PlayGo right (system AMPR/PlayGo need package-backed `ppr_pfs` storage)? ShadowMountPlus's code is consistent with it, but it isn't stated anywhere ([Part 4](#part-4-shadowmountplus-the-runtime-that-loads-fakelibs)).
- `fakelib/libSceAmpr.sprx` is `ampr_emu` (confirmed by the user and by its build path). `libScePlayGo.sprx` was built from `C:/dev/ps5/playgo/`, presumably `pgo_stub`.
- Which **firmware version** are `fakelib/libSceAgc*` (Sony build `orbis-build_0005c`, job `J03828141`) and `libScePsml` (job `J03950001`) from? Matching these job IDs against firmware dumps would answer it.
- The eboot GOT patches work on console, and many unresolved imports were left **unpatched** (PS VR2, TTS2, etc.; see the fakelib analysis). This strongly indicates lazy PLT binding (an unresolved import only fails when called). Whether that is the stock loader or kstuff is still unverified.
- Does the loader ignore `requiredSystemSoftwareVersion` in `param.json`?
- Names for the NIDs `La9uyZv4Kvw` (libkernel), `kP2L8t3j-aM` and `LibwuIonIBw` (libSceVideoOutVrrStatus), and `qBS714-Jr3g` (libSceLibcInternalExt).
- ~~What does `[rdi+0x18]` at eboot `0x83403a` control?~~ Answered: `SaveDataMount3.system_blocks` (see "Ghidra verification of the eboot patches").
- Who added the three pre-existing NOT_BACKPORT patches (`mount_mode = RDONLY` at eboot `0x834048` and the `_Mtx_lock`/`_Mtx_unlock` guards), and why? If NOT_BACKPORT is untouched, this would have to be original code, but a compiler wouldn't emit `setnc cl; xor ecx,ecx; nop`.
- What do GT7's `userDefinedParam1..4` mean?

#### Part 4: `ShadowMountPlus/`, the runtime that loads fakelibs

Source: `drakmor/ShadowMountPlus` (cloned shallow into `reference/ShadowMountPlus`, HEAD `4cde42a`). It is a C payload built with ps5-payload-sdk. It requires **kstuff-lite v1.07+**, and the README states support down to firmware 2.00. It continues VoidWhisper's ShadowMount and credits BestPig's BackPork.

**What it is:** a background "auto-mounter" payload. It finds game **dumps** (folders or disk images) on internal or external storage, mounts them, and registers them in the system library (`app.db`), so they install and launch like normal games. It **replaces the standalone `ps5-backpork.elf` payload**; the README says running both at the same time conflicts.

##### How fakelibs reach the game (`src/sm_fakelib.c`)
- ShadowMountPlus hooks `SceShellCore` (`sm_shellcore_hooks.c`, per-firmware offsets in `sm_shellcore_offsets.inc`) to learn when a game's **sandbox is ready**, just before the process spawns.
- It then mounts the selected fakelib directory over the sandbox's `/mnt/sandbox/<TITLE_ID>_<n>/<…>/common/lib`. The mount is `nullfs` for a single source, or `unionfs` when several sources are combined. The game's loader resolves system `.sprx` files from that directory, so **any file in `fakelib/` replaces the system library of the same name**, for that game only.
- **Search order** for the fakelib directory:
  1. `<scanpath>/backports/<TITLE_ID>/fakelib2`, then `…/fakelib`
  2. `<game source>/fakelib2`, then `<game source>/fakelib` (this is where GT7's is)
  3. `/data/homebrew/backports/<TITLE_ID>/` as a fallback
- A `fakelib2` directory is used **exclusively**: nothing is merged into it.
- A plain `fakelib` can be combined with a **global fakelib** (`/data/shadowmount/fakelib`, with priority `game` or `global`) and with **emulator updates** from `/data/shadowmount/emus`, which replace files of the same name. The combined result is cached and expires after 7 days.
- For each game it also mounts writable `nullfs` views of `/mnt` and `/data` into the sandbox.

##### Why dumps need `ampr_emu` and the PlayGo stub
- When a game is a **real installed package** (app0 on an `nsfs` sandbox backed by `ppr_pfs`, mounted at `/mnt/sandbox/pfsmnt/<TITLE_ID>-app0`), ShadowMountPlus **deletes `libSceAmpr.sprx`, `libScePlayGo.sprx` and `libkernel.sprx`** from that title's `/data/homebrew/backports/<TITLE_ID>/fakelib` before mounting (`cleanup_ppr_backport_fakelib`). The real libraries work there.
- So the replacements are only needed when the game runs **from a dump** (folder or image) instead of an installed package. The likely reason: the system AMPR and PlayGo services expect package-backed storage that a dump doesn't have. This explanation is inferred from the code, not stated in the repo.
- That's why GT7's `fakelib/libSceAmpr.sprx` (`ampr_emu`) and `libScePlayGo.sprx` are in **both** GT7 copies: they're needed to run under ShadowMount, and are unrelated to the firmware downgrade.
- **AMPR auto-update** (`sm_ampr_updater.c`, setting `auto_update_ampr=1`): downloads `libSceAmpr.sprx` from `drakmor/ampr_emu` releases and installs it as an emulator update.
- A fakelib may also contain a replacement **`libkernel.sprx`**; it's in the cleanup list.

##### Backports for installed packages (`src/sm_pkg_backport.c`)
- For an installed package, `<scanpath>/backports/<TITLE_ID>/` files are injected into the package's `app0` through **NSFS redirects**. This uses `/dev/nsfsctl` with ioctls `0xC0406E00` (create) and `0xC0406E01` (delete), "plain" redirects for files, and "overlay" redirects for missing directories, with up to 256 per sandbox.
- This means patched `eboot.bin` / `sce_module/*.prx` files can be overlaid onto an installed game **without repacking it**.

##### Supported game sources
| Extension | Filesystem | Notes |
|---|---|---|
| folder | n/a | `<GAME_DIR>/sce_sys/param.json` must exist |
| `.ffpkg` | UFS2 | Recommended. Build with `mkufs2.sh` or UFS2Tool (`newfs -O 2 -b 65536 -f 65536 -m 0 -S 4096`). |
| `.exfat` | exFAT | 64 KiB clusters; fast LVD/BFS path when stored under `/data` |
| `.ffpfs` / `.ffpfsc` | PFS / compressed PFS container | Experimental. Built with PSBrew/MkPFS; unsigned images are mounted with signature checks disabled. |

Devices are attached through LVD or `/dev/mdctl`. Default scan paths cover `/data/homebrew`, `/data/etaHEN/games`, `ext0/1` and `usb0-7`; a `manual.lst` lists extra sources.

##### Other features
- kstuff auto-pause/resume around game launches, with crash detection that tunes the pause delay into `/data/shadowmount/autotune.ini`.
- Fan target control.
- An HTTP/JSON API with a web UI (port 10101; `docs/api.md`, `docs/openapi.yaml`).
- Notifications in 31 languages.

##### Relevance
- It is the **loader half** of the backport. Our tooling only has to produce a game folder or image with patched binaries plus `fakelib/`, and ShadowMountPlus does the library injection. No separate backport payload is needed.
- `backports/<TITLE_ID>/` lets backport files live **outside** the game dump. This is a cleaner layout for distributing a backport, since the original dump stays untouched.
- `sm_shellcore_offsets.inc` and `tools/generate_shellcore_offsets*.py` are a reference for per-firmware ShellCore offsets.

---

#### Part 6: Firmware export lists (step 2), research results

**Goal:** the list of exported NIDs per system library for **FW 4.xx**, used to decide which imports are missing on the target.

**Result (2026-10-08): no public 4.xx export list was found.** Searched: GitHub repos and code search (genstub output, "ps5 4.03/4.51 libs"), the DNNDHH account (only `PS5-3.20_Libs` and a 3.20 core dump), BestPig/BackPork, ps5rs, and the PS4/PSP libdoc projects.

| Source | What it has | Usable for 4.xx? |
|---|---|---|
| `PS5-3.20_Libs` (DNNDHH, already in `reference/`) | Every 3.20 export (31,275 NIDs, functions only) | **Lower bound.** Functions are rarely removed, so anything on 3.20 should exist on 4.xx. Imports newer than 3.20 stay unknown. |
| [ps5rs](https://github.com/claimore22/ps5rs) | Rust PS5 analysis tool with a NID→name catalog (~195K) and a `system_modules/*.exports.json` format | No. Export files are generated by the user from their own dumps; no per-firmware data ships with it. |
| [ps4libdoc](https://github.com/idc/ps4libdoc) / PSPLibDoc | Per-firmware, per-module export docs | PS4/PSP only. A **good format model** for our own PS5 database. |
| [BestPig/BackPork](https://github.com/BestPig/BackPork) (cloned to `reference/BackPork`) | Runtime fakelib loader (predecessor of ShadowMountPlus) plus **BPS patches for 10.01 libraries → 6xx and 7xx** | No 4.xx set. Its patches confirm the patch patterns (below). |

**Decoded BackPork patches** (`patches/6xx`, `patches/7xx`; patching 10.01 libraries):

| Library | Patch content |
|---|---|
| `libSceNpAuth`, `libSceNpAuthAuthorizedAppDialog`, `libSceSaveData.native` (both), `libScePsml` (6xx) | **SDK fields only** in the module param (`… 10 \| 38 00 00 06` = FW 6 pair; `59 10 \| 38 00 00 07` = FW 7 pair). These libraries need no code change for 6/7xx. |
| `libSceAgc` | SDK fields + `xor eax,eax; ret` stub at `0x1aa96` + a run of **GOT slot redirects** (8-byte copies around `0x2cb88`) + **NID string swaps** (18–19-byte copies around `0x4d4xx`). 6xx additionally has a `mov …; ret` constant-return stub at `0xd100`. |
| `libSceAgcDriver` | SDK fields + GOT redirects (`0x18000…`) + a NID string swap |
| `libSceFiber` (6xx only) | SDK fields + GOT redirects + NID string swaps |
| `sce_module/libc.prx` (6xx notes) | NID swap `4h6F1LLbTiw` → `IWIBBdTHit4` (the same one found in GT7's libc) |

These are the same four techniques found in GT7 (SDK fields, GOT → stub, NID swap, return-constant stub), so the patch library in Part 5 covers BackPork's work too. The lower the firmware, the more libraries need patching (6xx adds `libSceFiber` and the libc swap).

**Options for getting 4.xx data:**
1. **Use 3.20 as a lower bound** (works now). Anything missing from 3.20 is treated as "possibly missing". Risk: **over-stubbing** a function that does exist on 4.xx, replacing a working function with `return 0`. This matters if PLT binding is lazy, since a pre-set GOT value bypasses the resolver.
2. **Dump a 4.xx console** with PS5-SELF-Decrypter `-system-common-lib`; this is exact. It needs access to a 4.xx console (the user has none).
3. **Ask the community** (ShadowMountPlus/BackPork Discord) for 4.xx export lists. Lists are NID data, not Sony code.
4. **Generate the lists ourselves on any firmware we can dump**, using ps4libdoc-style per-firmware JSON, and keep them in the repo so the database grows.

**User's console: FW 13.60** (2026-10-08). According to press reports from late September 2026 (not verified against the repo), the public **Relapse** exploit (ntfargo, GitHub) gives kernel R/W on **FW 7.00–13.60**, PS5 Pro included. It is tethered (re-run after every reboot) and may hang or panic the console. **FW 14.00 is not vulnerable**, so the console must stay on 13.60 for this route.

What a 13.60 dump would add:
- The **newest export list**. Combined with 3.20, imports fall into three classes: *on 3.20* → present on 4.xx; *not on 3.20 but on 13.60* → **unknown, flag it**; *not on 13.60 either* → must come from a fakelib or the game's own modules.
- The natural **fakelib source** for backporting 13.x games, the same role 10.01 libraries play for BackPork.
- It **can't test** 4.xx compatibility, because every function exists on 13.60.

Blockers:
- `PS5-SELF-Decrypter` has offsets only up to 10.xx. 13.60 needs its 11 kernel `.data` offsets, found via the string heuristics in its README.
- kstuff-lite support for 13.60 (needed by ShadowMountPlus) is unconfirmed.

---

#### Part 7: CONTROL Resonant (second backport sample)

`.original/CONTROLResonant/backport/` holds **CONTROL Resonant** (`PPSA34547`, version `01.300.300`), backported to **FW 4.03** by a different author than GT7's. Only the backport is available (no original dump to diff), but its `README.txt` (Vietnamese) documents every change and `SHA256SUMS` lists the files. Unwrapped ELFs are in `work/CONTROL_elf/`.

**Layout note (2026-10-08):** NOT_BACKPORT moved to `.original/GT7/NOT_BACKPORT/`. `.original/GT7/decrypted/` is byte-identical to it, and `.original/GT7/EUR/` has the same binaries as `GT7/USA/`.

##### What the README says (translated)
- **Tested on PS5 Pro FW 10.01**, *not* on 4.03 itself: game starts, sound works, save/load works, first cutscene passes. On a Pro, the game runs in normal PS5 mode.
- `param.json`: `requiredSystemSoftwareVersion` **and** `sdkVersion` set to `0x0403000000000000`. GT7 left them at 13.60, and both work.
- SDK 4.03 on eboot and every module, re-signed as fake SELF.
- libc: kept the game's own SDK 12 libc, because the "4xx kit" libc lacks 18 functions that eboot/cohtml/Renoir need. `MapNamedFlexibleMemoryInternal → MapFlexibleMemory` with flag `0x8000 → 0`; stubbed 3 LibcInternalExt + 6 SyncOnAddress functions.
- eboot: stubbed 8 functions missing on 4.03. **`sceKernelIsTrinityMode` exists in libkernel only from FW 7.61**; an earlier build crashed with `PRX_NOT_RESOLVED_FUNCTION` at launch on old firmware.
- **Audio:** Wwise picks 7.1.4 (12 channels) when the console reports 3D audio; SDK 4.03 rejects a 12-channel port, so there is no sound. Forced 7.1 by patching a Wwise constant (VA `0x3ca31f0`, channel mask `0x63f108`).
- **Saves:** `SceSaveDataMount3 +0x18: -1 → 0` (VA `0x374e1ee`, the same fix as GT7) and `SceSaveDataPrepareParam.prepareMode 2 → 1` (VA `0x374c732`).
- **Crash in the first cutscene** ("Unable to reserve direct memory"): the `ampr_emu` build that shipped with the dump (6 Sep) misread data, so the game asked for about 9 GB. Fixed by using the 24 Aug build. Install note: delete `ampr_emu.index` (and `.tmp`) from app0; the emulator rebuilds it.
- nakama-sdk: 4 `sceHttp2WebSocket*` functions return an error, so online play is off.
- Fakelibs: Agc, AgcDriver, Psml from a "4xx kit" (Sony build job `J03718109`; GT7's are `J03828141`). Ampr = `ampr_emu`, PlayGo = from the dump.

##### New findings from analysing it
- **A sixth stubbing technique: stub code written into the PLT entry itself**, over the `jmp [GOT]`. No cave is needed, since a 16-byte entry holds bodies up to 16 bytes. Every Control stub uses it: `xor eax,eax; ret`; `GetOperationMode` → `mov [rdi],2; mov [rsi],0; xor eax,eax; ret` (**the same values as GT7**); `GetGameTrialsFlag` → `mov [rdi],0; xor eax,eax; ret` (not a trial = full game); `sceHttp2WebSocket*` → `mov eax,0x80431001; ret` (**an error return**, so callers take their "unavailable" path).
- **SDK values encode the minor version:** PS5 SDK `0xMMmm00pp` in BCD. 4.03 is `0x04030031`; Backpork's "4" is `0x04000031`. The `param.json` value is `0xMMmm000000000000`.
- **SyncOnAddress:** GT7 adapts these to `_umtx_op`; Control stubs them to return 0 (waits return at once, so callers spin). Both run.
- **MapFlexibleMemory flag `0x8000`:** GT7's backported libc still passes it and runs; Control clears it. So clearing it isn't required, and the tool doesn't do it.
- **Argument-level patches are library problems, not game problems.** Both games patched `sceSaveDataMount3` arguments inside their own code; the fix belongs at the import boundary (see Part 5, argument fixes). The Wwise audio patch is the only truly engine-specific one. Its generic counterpart would be to make the 3D-audio capability query, or the audio-port open, refuse 12 channels; that is still to be analysed.
- Control's eboot imports far more from shipped modules (cohtml, Renoir, nakama, MediaDecoders) than GT7's; the provider logic handles this.
- `libcohtml` has 2 stubbed imports from an unlisted library id (`#4`; `aajWxENMjH0`, `HMXO7jN3FjM`). Not yet examined.

##### Open questions
- ~~Which API reports 3D-audio support in Control?~~ Answered: `sceAudioOut2GetSpeakerInfo` (flags bit 0, available_bits); generic post-call fix in Part 8.
- Is `prepareMode 2 → 1` needed by GT7 too (GT7 also imports `sceSaveDataPrepare`)? The generic fix only acts on the value 2.
- Control was tested on FW 10.01. Does it run on a real 4.03 console?

---

#### Part 8: Using the references as the knowledge base (generic, not per game)

GT7 and CONTROL are the **ground truth** (console-tested patched files). The references explain *why* those patches work and let each fix be stated once per **library function**, so it applies to every game. Every fix in `ps5bp/fixes.py` records its sources.

##### What each reference contributes
| Reference | Used for |
|---|---|
| **AnyPS5** `core/libs/prx/*` (HLE reimplementations) | Signatures and struct layouts behind each import, and what a "harmless" return value is. Cross-check of `fixes.py` (2026-10-08): IsTrinityMode → 0, VRR peg/unpeg/status → 0 and `sceAudioOut2Set3DLatency` → 0 all agree with our stubs. AnyPS5's `GetOperationMode` returns mode 0 (both games used 2; any value ≠ 3 passes the games' check). `docs/dev/TechnicalDebt.md` lists functions that games survive as no-ops, which are candidates for future stub rules (not applied automatically, since not console-proven). |
| **backport-helper** | The shim/hook alternative for argument fixes. Its example hooks are exactly `sceKernelGetOperationMode` (0) and `sceSaveDataMount3`, the same functions both games needed. |
| **ampr_emu / pgo_stub** | Source of the AMPR/PlayGo fakelibs; needed only when running from a dump (ShadowMount). The Control README says the 6 Sep `ampr_emu` build broke Control (about 9 GB direct-memory request) and the 24 Aug build works: the fakelib *version* matters. |
| **BackPork / ShadowMountPlus** | How fakelibs are loaded (union-mount over sandbox `common/lib`), plus BPS patches showing the same techniques for 6xx/7xx. |
| **Backpork Kitchen** | SDK pairs per firmware major and the 6xx libc NID swap. It does nothing about missing imports. |
| **PS5-3.20_Libs** | Lower-bound export list ("present on 4.xx"). It confirmed that **every AudioOut2 function Control uses exists on 3.20** except `Set3DLatency`, so the audio problem is an argument problem, not a missing function. |
| **sce_symbols(_new)** | NID → name. It lacks some names; AnyPS5 plus `compute_nid` filled them (below). |

##### Facts confirmed through the references
- **`dFym5b41JLU` = `sceKernelSyncOnAddressWait8`, `XgVs4jRehcY` = `sceKernelSyncOnAddressWait16`** (names from AnyPS5, NIDs computed; not in aerolib). Control's "6 SyncOnAddress" stubs are therefore Wait, Wait8, Wait16, Wait32, Wait64 and Wake.
- **SyncOnAddress signatures** (AnyPS5): `Wait/Wait8/16/32/64(T* addr, T expected, const KernelUseconds* timeout /*u32 µs, NULL = forever*/, const char* name)`; `Wake(void* addr, int32 count)`. This matches what we reconstructed from GT7's callers. **The generic `Wait` has Wait32's signature**, so `ps5bp` now gives it Wait32's (console-proven) `_umtx_op` translation; GT7's own patch passed `expected` as the op code and spun. Wait8/16 stay return-0 stubs, as in Control: FreeBSD `_umtx_op` has no 8/16-bit compare.
- **Audio, made generic.**
  - `sceAudioOut2GetSpeakerInfo(SpeakerInfo* info, u32 flags)`. `SpeakerInfo` (AnyPS5): `+0x00 type u8`, `+0x04 available_bits u32`, `+0x08 flags u32`, `+0x10 angles[16]`.
  - Control's eboot (`0x3ca27f1` and `0x3ca42fa`) takes `flags & 1` as "3D audio" and `popcnt(available_bits) > 8` as "has height speakers". Wwise then chooses 7.1.4 (12 channels).
  - AudioOut2 `data_format`: channel count in bits 8–11 (AnyPS5).
  - **Root cause:** a newer OS (Control was tested on 10.01) reports 3D/7.1.4, but a port opened by an SDK-4.03 binary is refused at 12 channels. This is an OS-vs-SDK mismatch, so it can hit any game whose engine reads `GetSpeakerInfo`.
  - **Generic fix:** a post-call adapter on `sceAudioOut2GetSpeakerInfo`. It runs the real function, then clears `flags` bit 0 and masks `available_bits` to 8 speakers, so the game itself chooses 7.1.
  - Lowering the channel count at `PortCreate` instead would be wrong: the game would still push 12-channel buffers.
  - Status: **derived, not console-tested**. Control fixed it inside Wwise instead.

##### Stub candidates from AnyPS5's TechnicalDebt "Silent stubs" (2026-10-08)
- `tools/gen_candidates.py` reads the silent-stubs section.
  - If a bullet names specific functions, only those are taken; otherwise every function in that library file.
  - A function is kept only if its body is exactly `return <constant>;` (ignoring `(void)arg;`), with named constants resolved from AnyPS5's headers. Anything that throws, calls `NotImplemented…`, or does real work is excluded.
  - The result is written to the generated **`ps5bp/candidates.py`** (26 functions). All NIDs were verified against both aerolib and `compute_nid`.
- **Finding: 25 of the 26 already exist on 3.20.** AnyPS5's silent stubs mostly cover functions that old firmware *has*; that is an emulator concern, not a backport one. Since candidates only apply to imports missing on the target, they rarely fire.
- **The one that matters: `sceTextToSpeech2Initialize` → `0x8002002D` (`SCE_KERNEL_ERROR_EOPNOTSUPP`).** It is missing on 3.20, GT7 imports it, and AnyPS5 documents that GT7 (PPSA01317) skips narration when it fails. `sceHmd2Initialize` → `0x81110016` (unsupported feature) is also a candidate, but it exists on 3.20, so it doesn't fire.
- Tool:
  - `--candidate-fixes` applies candidates, opt-in, because they are reference-derived and not console-proven. They are written as `mov eax, imm32; ret` (stub name `ret:<value>`).
  - Without the flag, the report lists the candidate next to each unknown import that has one.
- Open question: GT7's backport left `libSceTextToSpeech2` unpatched and runs. Either TTS2 exists on 4.xx (added after 3.20), or the call is gated. A real 4.xx export list would decide it.

##### New tool capability
`patch.place_chain` supports **post-call adapters**: a `call [GOT]` in the middle of the chain (with `push rdi` for 16-byte stack alignment) and a final `ret`. The tests walk both chain types from the PLT entry (`sceSaveDataMount3` pre-call, `sceAudioOut2GetSpeakerInfo` post-call).

---

#### Part 9: How Backpork Kitchen's "general" backport actually works (deep dive)

Source: `reference/PS5-BACKPORK-KITCHEN/PS5 BACKPORK KITCHEN/`. The path traced is what **"Start Cooking"** runs today:
`Form1.BtnStart_Click` → `CompositionRoot.CreatePatchingCoordinator` → `PatchingCoordinator.ExecutePatchingAsync` → `ElfPatchingService.PatchFileAsync` for each file → copy fakelibs.

##### The pipeline
1. **Back up** the game folder (`Backupfiles`), then delete any old `<game>/fakelib/`.
2. **Collect files:** every `*.bin`, `*.prx` and `*.sprx` under the game folder (recursively, so not just `eboot.bin`).
3. **For each file** (`ElfPatchingService.PatchFileAsync`):
   1. A file counts as "decrypted" when it starts with the ELF magic. Otherwise it is unwrapped with the internal **SelfUtil port**. This only handles **fake/plaintext SELFs**; truly encrypted files fail ("Decrypt failed").
   2. `ElfInspector.ReadInfo` → must be patchable, i.e. have a `PT_SCE_PROCPARAM` / `PT_SCE_MODULE_PARAM`.
   3. Skip if the current PS5 SDK equals the target, or is already ≤ the target (`ShouldPatch` = `current > target`).
   4. `ElfPatcher.PatchFile`: overwrite the PS5 SDK (`param+0x14`) and the PS4 SDK (`param+0x10`).
   5. Optional "6xx" checkbox: for `libc.prx` only, the string swap `4h6F1LLbTiw#A#B → IWIBBdTHit4#A#B` (MapNamedFlexibleMemoryInternal → MapFlexibleMemory).
   6. **Fake-sign** (FreeFakeSign, a `make_fself.py` port) and **overwrite the original file** in place.
4. **Copy fakelibs:** `<exe dir>/<target FW>/fakelib/*` → `<game>/fakelib/`.

##### Where the fakelibs come from
- `fakelibs.json` (downloaded from the repo; URLs base64-encoded) defines exactly **two sets**, and they are **BestPig/BackPork's patched 10.01 libraries**:
  - **FW 7 (recommended):** libSceAgc, libSceAgcDriver, libSceNpAuth, libSceNpAuthAuthorizedAppDialog, libSceSaveData.native, plus `ps5-backpork.elf`. This is BackPork `patches/7xx`.
  - **FW 6:** libSceAgc, libSceAgcDriver, libSceFiber. This is part of BackPork `patches/6xx`.
- `FirmwareExtractorModule` can unpack a PUP (`pup_unpacker.exe`), but decryption needs the **on-console payload `ps5_pup_decrypt.elf`**. The extracted `.sprx` files go to `<FW>/fakelib` **unpatched**.
- `LibraryProcessorModule` only catalogues fakelibs: size, magic, SHA-256 and a manifest. **It never patches them.**

##### What it does *not* do
- **No import analysis.** Nothing checks which functions the game (eboot, `sce_module/*`, its own prx files) imports that the target firmware lacks. `SdkDetector` only computes SDK statistics.
- **No eboot or game-module fixes.** No stubs, no argument fixes. GT7 needed 6 eboot stubs and a savedata fix; Control needed 8 stubs, 2 savedata fixes and an audio fix. Kitchen can't produce either.
- **No fakelib patching.** It relies on fakelibs patched elsewhere (BackPork's BPS patches).
- No `param.json` change; output overwrites the dump in place (after a backup).

##### Bugs and limits found
- **PS4 SDK is written as 0** on the current path. The coordinator only carries the PS5 SDK; `ElfPatchingService` passes `0UI` (commented *"Not used for now"*), and `PatchFile` writes it unconditionally. `Ps4SdkResolver` (PS5 → PS4 for FW 5–10, fallback `0x09590001`) exists but is unused. GT7 and Control both keep a real PS4 SDK (`0x09040001`), so **neither was made with Kitchen's current build**. Whether 0 matters on a console is unknown.
- `*.bin` collects every `.bin` file in the dump, not just `eboot.bin`. Non-ELF files fail and are counted as errors.
- The target is the firmware *major* only (SDK table 1–10); no minor version (Control needs 4.03).
- In practice only FW 6/7 are usable, because only those fakelib sets exist.

##### What "general" means here
Kitchen is general **only for games whose missing functions all live in the libraries BackPork patched**, i.e. games built with SDKs up to about 10.x whose only problems are AGC/NpAuth/SaveData/Fiber. That is the original BackPork method, automated:
**SDK patch + fake-sign + BackPork's patched 10.01 fakelibs (+ the libc 6xx swap).**

Newer games (GT7 = SDK 13 package, Control = SDK 12 libc) also import functions missing in **their own code** (eboot, libc, nakama…). Kitchen has no answer for those. That is the gap `ps5bp` fills with import classification plus the per-library-function fix table.

##### Kitchen vs ps5bp
| Capability | Kitchen | ps5bp |
|---|---|---|
| Fake-SELF unwrap / fake-sign | yes | yes (round-trip byte-exact) |
| SDK patch | PS5 only; PS4 written as 0; major only | PS5 + PS4 pair; minor versions (`4.03`) |
| libc 6xx NID swap | optional checkbox | automatic when the import is missing |
| Fakelibs | per-target-FW sets (BackPork 6xx/7xx), copied | `--fakelib DIR`, copied or patched like any module |
| Import analysis (missing on target) | no | yes (reference + providers) |
| Stubs / adapters / argument fixes | no | yes (`fixes.py`, `candidates.py`) |
| `param.json` | no | `--patch-param` |
| Output | overwrites dump (after backup) | overlay of changed files (ShadowMountPlus `backports/<TITLE_ID>`) |
| Firmware PUP extraction | yes (needs console payload) | no |
| GUI, FTP, PKG/UFS2 builders, payload sender | yes | no |

**Adopted (2026-10-08):** `ps5bp` now has the per-firmware fakelib store (`fakelibs/<FW>/` + `manifest.json`, `python -m ps5bp fakelib add|list|verify`), seeded with GT7's FW 4 set and Control's FW 4.03 set (Control's checksums match its own `SHA256SUMS`). `backport` uses it automatically.

**Worth adopting from Kitchen:** a per-target-firmware fakelib store (`<FW>/fakelib`) so `backport` picks fakelibs automatically from the target firmware, plus its library manifest (checksums). Kitchen's PUP extraction depends on an on-console decrypt payload and isn't needed for the tool itself.

---

#### Part 10: Fakelib supply findings (Phase 3, 2026-10-08)

##### BackPork's BPS patches, decoded without the 10.01 libraries
`python -m ps5bp bps info` lists every literal write in a patch, so we can see what BackPork changes before having the source libraries.
- **Source = FW 10.01 libraries.** Each patch records the source size and CRC32. `libSceNpAuth`, `libSceNpAuthAuthorizedAppDialog` and `libSceSaveData.native` have the **same source CRC32 in 6xx and 7xx**; Agc and AgcDriver differ (two different 10.01 builds, or the 7xx set was made from another dump).
- **SDK patch.** `libSceNpAuth` and `libSceNpAuthAuthorizedAppDialog` are **only** an SDK patch: 6xx writes `10 38 00 00 06` and 7xx writes `59 10 38 00 00 07` at the module param. Those are the tail bytes of PS4 SDK + PS5 SDK = `0x10090001/0x06000038` (6) and `0x10590001/0x07000038` (7), matching `sdk.FIRMWARE_SDK` exactly (10.01's own pair is `0x12090001/0x10000040`). So `ps5bp`'s SDK patch already reproduces these two patches. The same SDK write (or its differing bytes only, e.g. `10 38`) also appears in Fiber, Psml, SaveData.native and 7xx Agc/AgcDriver; 6xx Agc writes only `09` there and copies the rest from elsewhere in the file.
- **Local symbol redefinition** (technique 3, as in GT7's fakelibs): writes of `01 00` repeated at a **24-byte stride** (Agc around `0x4d40e`, Fiber around `0x1044e`) are `.dynsym` entries getting `st_shndx = 1`, with `st_value` copied from another entry. Single `01` writes (Agc `0x4d22e`, AgcDriver `0x2809e`/`0x29466`, 6xx SaveData `0x4d096`) are probably the same change on one symbol. Imports become local definitions pointing at a stub.
- **Stub code:** Agc `0x1aa96` = `31 c0 c3 90…` (`xor eax,eax; ret` + nops, return 0, both sets). 6xx Agc also has `0xd100` = `48 c7 ?? ?? ?? ?? ?? c3` (`mov r64, imm32; ret`, return a constant), which is 6xx only.
- The other Agc/AgcDriver/Fiber edits (`0x2cb88…`, `0x18000…`) are runs of 8- and 16-byte copies at regular strides, probably relocation/GOT entries redirected to the stub. Naming them needs the 10.01 source library (Phase 3.3).
- **Conclusion:** BackPork used the same three techniques `ps5bp` has (SDK patch, local symbol stub, ret0 stub), so BPS application is mainly for reproducing their console-tested sets byte-for-byte. Once we have the 10.01 libraries, running `ps5bp` on them and diffing against the BPS output will check our generic rules.

##### The console-tested fakelib sets leave four imports unresolved
`python -m ps5bp fakelib check --firmware 4.03` (and `4`) against 3.20 shows imports neither backport author touched:
| NID | Function | In |
|---|---|---|
| `GGeRJk1XdWc` | `sceKernelGetProsperoCompiledSdkVersion` | Agc, Psml |
| `dkBx0YqFQ+Y` | `sceKernelGetToolMemoryRange` | Agc, Psml |
| `LzoM-wVLJDE` | `sceKernelIsThreadSanitizerEnabled` | AgcDriver |
| `Ygv0S+Hi+hA` | `sceVideoOutGetPipelineStatus` | Psml |

Both GT7 (FW 4 set) and Control (4.03 set) ship them unpatched and both run. So each one is either **exported on 4.xx** (added between 3.20 and 4.00) or **never called** (lazy binding). They are not in PS5-3.20_Libs, but they are in ps5-payload-sdk's `sce_stubs` (which records no firmware, so it can't be a firmware_db entry). Treat them as "tolerated on FW 4" until a 4.xx export list settles it.

##### ampr_emu builds are identified by their compile date
- Control's `libSceAmpr` (24 Aug build, the one its README says works) embeds the compiler `__DATE__` **`Aug 24 2026`** plus a `[AMPR_EMU] name=libSceAmpr version=%s build=%s %s` log line. GT7's older ampr_emu has neither, and PlayGo has no date.
- The store manifest now records `build_date`; `store.KNOWN_BUILDS` marks `Aug 24 2026` ok and `Sep  6 2026` broken (Control README: "the 6/9 build reads dump data wrong, the game asks for ~9 GB"; the year is inferred).
- Control's README also says that backport was **tested on FW 10.01**, not on a 4.xx console.
- **`param.json` line endings:** all three sample `param.json` files are CRLF, including the one Control lowered and console-tested. `ps5bp --patch-param` used to write the lowered copy with LF (Python text mode); since 2026-10-08 it keeps the original line endings, so only the version strings change (`tests/test_phase4.py`).

---

#### Part 11: Triage of unresolved imports and the pre-existing patches (Phase 4, 2026-10-08)

##### Code references per unresolved import
`imports.code_references` counts instructions in executable segments that call, jump to or load an import's PLT entry or GOT slot (`call/jmp rel32`, `call/jmp [rip+x]`, `mov/lea reg,[rip+x]`; byte-pattern scan, 2 s on GT7's 110 MB eboot). The backport report shows `refs=N` for every unresolved import.
- `refs=0`: no direct reference. Reachable only through data (vtables, pointer tables), or not at all. In GT7: `sceTextToSpeech2Cancel`.
- Most of GT7's PSVR2 (`sceHmd2*`, `sceVrTracker2*`), TTS2, Share and privacy imports have **exactly 1** reference: a thin wrapper. Whether that wrapper runs needs Ghidra (who calls it, and is it behind a PSVR2/TTS feature check?).

##### GT7 keeps every startup module, even ones 3.20 lacks
GT7's eboot lists `libSceTextToSpeech2.prx`, `libSceHmd2.prx`, `libSceShare.prx`, `libSceVrTracker2.prx` etc. as `DT_NEEDED`, and the FW 4 backport kept the list unchanged. 3.20 has no `libSceTextToSpeech2` module at all (only `libSceTextToSpeech`, 11 functions). So either 4.xx has it, or a missing needed module doesn't stop the game from loading. Which one needs a 4.xx export list or a test.

##### Evidence table: imports a console-tested backport left unpatched
`fixes.LEFT_UNPATCHED` (40 NIDs) holds the imports still unresolved in the shipped backports, with each game's own modules as providers:
- **Both GT7 and Control (5):** `sceKernelDebugWriteCppExceptionInfo` (libc), and in the fakelibs `sceKernelGetProsperoCompiledSdkVersion`, `sceKernelGetToolMemoryRange`, `sceKernelIsThreadSanitizerEnabled`, `sceVideoOutGetPipelineStatus`.
- **GT7 only (35):** the eboot's PSVR2, TTS2, Share and `D-CzAxQL0XI` (UserServicePlatformPrivacyWs1) imports, plus 5 libc imports that Control stubbed (Wait8, Wait16, `OQ-dzhlnM28`, `tB59hFLH3SA`, and `qBS714-Jr3g` whose only call GT7 retargeted).
- Control's game code has **no** unresolved imports left; it stubbed everything.

The report adds `left unpatched by: <backport>`. This is evidence, not a fix: the import exists on the test firmware or isn't called there. Caveat: Control was tested on **10.01**, and GT7's test firmware isn't recorded. `tests/test_phase4.py` recomputes the table from the samples.

##### Ghidra: are GT7's left-unpatched eboot imports reachable? (2026-10-08)
- **PSVR2 (`sceHmd2*`, `sceVrTracker2*`) is gated by a function that exists on old firmware.** `0x942d40`: `sceSysmoduleLoadModule(0x12a)` (result ignored), then `sceHmd2Initialize` (`c812oYs7Vsc`, exported on 3.20). If it returns non-zero, the code jumps past everything: `GetDeviceInformation`, `GetFieldOfViewWithoutHandle`, `VrTracker2Initialize`, and the flag at `0x631a614` that the single writer at `0x943119` sets to 1. Every other PSVR2 call site (`sceHmd2Open` at `0x1a41067`, etc.) checks that flag first. So with no PSVR2 support, none of the missing PSVR2 functions run. Ghidra's auto-analysis had not even created functions there; they are reached through vtables.
- **Text-to-speech 2 is gated by its own missing init function.** `FUN_00bd3970` calls `sceTextToSpeech2Initialize` first; only on 0 does it call `Open` and the rest. The routine is reached through a callback table built by a static initializer (`0x18097f8`, `0x180956f`), so when it runs depends on runtime state (probably the system screen-reader setting). The AnyPS5 candidate stub (`return 0x8002002D`) makes `Initialize` fail, so the routine returns cleanly. Kept as an opt-in candidate (`--candidate-fixes`), because GT7's tested backport didn't stub it.
- *(Corrected in Part 13: the exact 4.00 export list contains `D-CzAxQL0XI`, so the conclusion below about GT7's test firmware is withdrawn.)*
- **`D-CzAxQL0XI` (libSceUserServicePlatformPrivacyWs1) is called unconditionally at startup.** `FUN_00bd2e10` registers each signed-in user (map lookup, user name, trophies) and calls `FUN_00bd3dc0`, which always calls `D-CzAxQL0XI(userId, &out)`. GT7 shipped this unpatched and ran, so **GT7's test firmware exports it**. 3.20 has no such library. So GT7 was probably tested above 4.xx, and its "left unpatched" list is evidence for that firmware, not for 4.xx. A real 4.xx target may need a stub here; its meaning (`out == 1` combined with another flag into an argument of `FUN_00586ea0`) is unknown, so no rule yet.
- **Generic lesson (gate pattern):** a feature library's missing functions are harmless when an init call that **exists** on the target fails first (Hmd2). When the init function is itself missing (TTS2), an error stub on it alone disables the feature. Prefer stubbing the gate with an error over stubbing every function with 0.
- `sceShareGetRunningStatus` (`0x25f09a`) sits in code Ghidra did not analyse (reached by a vtable call); not traced further.

##### NOT_BACKPORT's pre-existing patches are GT7-specific (PLAN 4.5: ruled out as general rules)
1. **`mount_mode` at `0x834048`** (inside the save-mount routine `FUN_00833f60`, which also calls `sceSaveDataMount3` and then `sceSaveDataPrepare` unless mode 3). Original: `ecx = setnc(param_2 - 2 >= 3); mount_mode = ecx + 1`, i.e. **RDWR (2)** for most save operations and **RDONLY (1)** for modes 2–4. The patch `xor ecx,ecx; nop` makes **every** mount read-only, so GT7's saves can never be written through this path. It changes game logic in one function, so it isn't a library-boundary rule.
2. **`_Mtx_lock`/`_Mtx_unlock` guards** (skip mutex pointers with bit 47+ set). No Control module has a guard or any other `patched` PLT entry, and Control also runs from a dump with ampr_emu. So it isn't a general dump requirement; it works around something in GT7.

Neither becomes a `fixes.py` rule. Who made them and why is still open (they were already in the dump the user has).

##### Code space and shim PRX (PLAN 4.3 / 4.4)
- **Page gap:** PS5 modules end the code segment mid-page and start the next segment on a 16 KB page. The gap is zero in the file and unmapped-but-in-page in memory: GT7 eboot `0x18d4`, GT7 libc `0x812`, Control eboot `0x2084`, nakama `0x1c62`; page-exact modules (GT7 fakelib Psml) have none. `patch.extend_text` grows the segment's `p_filesz`/`p_memsz` into it (`--extend-text`). Console test pending. The new bytes are 64-byte blocks of `ret` + 63 `int3`: a cave must follow a `ret`, and placed code ends with a `jmp`, so one long run would hold only one fix (found while porting to C#; all 10 NOT_BACKPORT libc fixes now fit).
- **No open toolchain for SCE PRX:** `backport-helper` (a replacement `libSceRtc` with runtime hooks and raw-NID exports via a post-link NID rename and `.hash` rebuild) and ampr_emu (`C:/dev/ps5/ampr_emu/out/Prospero_ReleaseHooks`) are built with Sony's Prospero SDK. ps5-payload-sdk builds `.so` files for its own payload loader only. A shim PRX for `ps5bp` would need a `.so` → SCE PRX converter.

---

#### Part 12: 13.60 console bench facts (2026-10-08)
Details and sources in [CONSOLE_BENCH.md](#tester-checklist).
- **Chain:** Relapse (browser, 7.00–13.60, tethered) → ELF loader on port 9021 → kstuff-lite **v1.11** (release notes: FW 1.00–13.60) → ShadowMountPlus **1.7beta3+** ("up to 13.60", needs kstuff-lite ≥ 1.07).
- **ShadowMountPlus 1.7 backports:** applied from `<scan_path>/backports/<TITLE_ID>` (default scan path `/data/homebrew`) **just before the game process starts**. Files are redirected, missing directories are NSFS overlays, **max 256 redirects per sandbox** (a large overlay could hit this). 1.7beta4 adds per-game fakelib toggles, `fakelib_exclude`, and auto-removal of incompatible libraries from FPKG backport fakelib folders.
- **Standalone BackPork + ShadowMountPlus conflict** (both mount fakelib); don't run both.
- **kstuff auto-pause:** ShadowMountPlus pauses kstuff 15 s (direct) / 25 s (image) after a tracked game starts. Fake-signed modules loaded after that may fail; `kstuff_no_pause=<TITLE_ID>` turns this off per game.
- **Logs:** `/data/shadowmount/debug.log` (+ `.1`), `/data/shadowmount/notify.txt`, the ShadowMountPlus API `/api/v1/debug-log` (port 10101, loopback), the Relapse host's `klosrv`/`ftpsrv` options, and Control's ampr_emu `/app0/ampr_emu.log`.
- **ampr auto-update** (`auto_update_ampr`, off by default) would replace our manifest-tracked ampr_emu build; keep it off while testing.
- Known bug: folder games can fail to relaunch on 13.60 with `CE-105773-3` (ShadowMountPlus issue #128, open).

---

#### Part 13: Firmware dumps for 46 firmwares, exact export lists, new samples (2026-10-09)

##### What arrived
- `.original/.PS5OriginalLibs/Firmware X.YY/`: system library dumps for **46 firmwares, 1.00 to 13.60**. Decrypted ELFs except **4.30, 7.00** (encrypted SELFs) and half of 11.20 (the other half is decrypted). 13.60 keeps its encrypted SELFs in `system/` and the on-console decrypted ELFs in `system_dec/` (since 2026-10-13; the importer merges both and skips the encrypted copies). 7.00 also has an unreadable SELF (malformed trailing PT_SCE_VERSION).
- `.original/backports/<Game>/{default,4.00+,decrypted}`: GT7 (default = old NOT_BACKPORT), **Control's original dump** (`default`, new), and a new game, **Ghost of Yōtei Complete Edition** (`default` + `4.00+ NOT TESTED`).

##### Exact export lists (PLAN 2.3)
- `python tools/import_firmware_dumps.py` imports every dump's `*/common/lib` (what games load; `system_1`/`system_2` duplicates merged) into `firmware_db/<fw>.json`. A firmware is imported only if every module has a decrypted copy, so 4.30, 7.00 and 13.60 are skipped. **43 exact lists**, 300–400 KB each (16 MB together), data symbols included.
- Managed **Mono/.NET assemblies** (`*.dll.sprx`, `*.exe.sprx`: mscorlib, System*, I18N…) sit in some dumps' common/lib (186 in 3.20) and carry ~250k NIDs. Games don't import them; both engines' `import_dump` skip them.
- `firmware_db/3.20.json` now comes from the real dump. The old PS5-3.20_Libs genstub list is contained in it except `mscorlib.dll`'s 375 NIDs. **The genstub list was incomplete**: it has no `libSceTextToSpeech2` at all, which is why TTS2 looked missing on old firmware.
- **4.00 and 4.03 export the same 17,901 NIDs.** For a FW 4 target the report is now exact.

##### What the exact 4.00 list settles
- **Every fix we apply for a missing function targets a function that really is missing on 4.00** (all 33 stub/swap/adapter/redirect fixes). The three argument fixes (`sceSaveDataMount3`, `sceSaveDataPrepare`, `sceAudioOut2GetSpeakerInfo`) target functions that exist on 4.00, as intended.
- Of the 40 imports the tested backports shipped unpatched, **33 exist on 4.00** (TTS2, PSVR2 except two, Share, `D-CzAxQL0XI`, the four kernel/VideoOut queries in the fakelibs, `sceKernelDebugWriteCppExceptionInfo`). Missing on 4.00: `sceHmd2ReprojectionSetAllowPositionalReprojection` and `sceHmd2ReprojectionSetParamWithBuffer` (behind the `sceHmd2Initialize` check, Part 11), and the five libc imports Control stubbed (Wait8, Wait16, `OQ-dzhlnM28`, `tB59hFLH3SA`, `qBS714-Jr3g`).
- **Correction to Part 11:** `D-CzAxQL0XI` exists on 4.00, so its unconditional call says nothing about GT7's test firmware. The conclusion "GT7 was probably tested above 4.xx" is withdrawn.
- GT7 backported to FW 4 with the exact list: the same 19 fixes; **only 2 imports stay unresolved** (the two PSVR2 functions, status `missing`), instead of 37 `unknown` against the 3.20 genstub list.
- Control's 4.03 fakelib set: **every import resolved** against the exact 4.03 list (the four "unresolved" kernel/VideoOut queries of Part 10 exist on 4.03).

##### When fixed functions appear (first decrypted dump exporting them)
| Function | First firmware |
|---|---|
| `sceSaveDataMount3`, `sceSaveDataPrepare`, `sceAudioOut2GetSpeakerInfo`, TTS2 | 1.00 |
| `sceVideoOutVrr*`, `sceHttp2WebSocket*` | 5.02 |
| `sceKernelSyncOnAddressWake`/`Wait`, `sceAudioOut2Set3DLatency`, `sceSaveDataCancel`, `sceNpEntitlementAccessGetGameTrialsFlag` | 6.00 |
| `sceKernelIsTrinityMode`/`HasTrinityMode`, `sceKernelMapNamedFlexibleMemoryInternal`, `sceVideoOutLatency*` | 7.01 |
| `sceKernelSyncOnAddressWait32`/`Wait64` | 9.00 |
| `sceAcmBatchJobPriority` | 10.01 |
| `sceKernelGetOperationMode`, libSceLibcInternalExt `OQ-dzhlnM28`/`tB59hFLH3SA`/`qBS714-Jr3g` | 11.00 |
| `sceKernelSyncOnAddressWait8`/`Wait16` | 12.00 |
| libkernel `La9uyZv4Kvw` | 12.40 |
| `sceShaderTranscode*` (5 functions) | **never**: no firmware exports them; only the fakelibs' own stubs satisfy them |

`sceKernelIsTrinityMode` is in 7.01 (6.50 lacks it; 7.00 is encrypted), not 7.61 as Control's README said; `data/introduced.json` is corrected. The report's "introduced" column now comes from the dumps.

##### Functions are removed too
Consecutive firmwares drop NIDs, including game-facing ones: libSceVideoOut (1.00→2.00), libSceUserService (−27 at 6.00), libSceNpUniversalDataSystem (several releases), libwebrtc, PlayReady. So an older list is **not** a strict lower bound for a newer target. With 43 exact lists the lower bound is now rarely used (only for firmwares without a dump, e.g. 4.10 → 4.03).

##### BackPork patches against the real 10.01 libraries (PLAN 3.3)
- **10 of 12 patches match the 10.01 dump** (size + CRC32) and apply with the right target CRC: all five 7xx patches and the 6xx Fiber, NpAuth, NpAuthAuthorizedAppDialog, Psml, SaveData.native.
- The **6xx libSceAgc / libSceAgcDriver** patches expect other sources (CRC `f98b1366` / `f719b950`). They are not the 7xx output and not any Agc in the 10.01–10.60 dumps. Their source is unknown, so a FW 6 set can't be built yet.
- **`fakelibs/7` built**: the five 7xx libraries patched, fake-signed (SDK 0x07000038/0x10590001), stored with provenance. Checked against the exact 7.01 and 7.61 lists, one import stays missing: `sceFsBfsGetAvailableSize` (libSceSaveData.native), which BackPork leaves unpatched too.

---

#### Part 14: Audit findings (2026-10-09)

##### PT_SCE_VERSION records may carry two different versions
- Each record in the trailing PT_SCE_VERSION block is `00 00`, u16 payload size, `08`, `name:`, then two 8-byte versions. Upstream `make_fself` (and our ports of it) assumed the two versions are always equal and rejected anything else as "malformed trailing PT_SCE_VERSION data".
- **Ghost of Yōtei's eboot breaks that assumption**: 2 of its 135 records (`libScePsmlMfsr2:` and `libScePsml_mfsr2_stub_weak:`, the PS5 Pro PSSR libraries) have `13 00 00 81 …` and `09 00 00 87 …`. Every structural check still holds (markers, tag, printable `name:`, sizes ending exactly at the block end), so the file is valid and the equality test was too strict.
- Fixed in both engines (`ps5bp/vendor/make_fself_batch.py` `is_complete_sceversion`, `Self.IsCompleteSceVersion`): the versions are no longer compared. Both engines now unwrap the Yōtei eboot to the same ELF; sign → unwrap returns it byte for byte. Tests: `TestSceVersionRecords` (Python), `SceVersionTests` (C#).
- Re-signing Yōtei's eboot differs from the shipped file in the digest (as for GT7) and in where the trailing block sits: the original packer put it 8 bytes before the declared file size, our signer at it. Same bytes; the unwrapper accepts both positions.
- The 7.00 dump's one unreadable SELF (Part 13) failed with the same message and may have the same cause; 7.00 stays skipped anyway (its other modules are encrypted).

##### Ghost of Yōtei (PPSA26344 v02.002.000) through the pipeline
- Backports to FW 4 and FW 7 now complete; both engines give identical output (9 files) and reports.
- FW 4 (exact 4.00 list): the same 4 derived fixes as GT7 apply; **one import stays missing**: `gWT7X8H0bYs` in `libSceVideoOutVrrStatus` (eboot, refs=3), first exported by FW 10.01. GT7 needed proven stubs for two other VrrStatus NIDs (`kP2L8t3j-aM`, `LibwuIonIBw`), so a stub is the likely fix, but it needs triage (what it returns, how the 3 call sites use it) and a tester run before it goes into `data/fixes.json`.

##### Tester checklist assumptions, checked against the ShadowMountPlus and ampr_emu sources
- **debug.log**: `log_debug` writes only when `debug_enabled` is set; `debug=1` is the default (`config.ini.example`, `sm_config_mount.c`). The log is rotated (`debug.log` → `debug.log.1`) when **ShadowMountPlus starts** (`main.c`), not per game launch: after a crash, reboot and re-jailbreak, the failed run's log is `debug.log.1`.
- **notify.txt is never written**: `TOAST_FILE` is defined in `sm_paths.h` but used nowhere. Removed from the checklist (CONSOLE_BENCH 1.3 listed it too).
- **Folder dumps get the backport as a unionfs layer** above `/system_ex/app/<TITLE_ID>` when ShadowMountPlus mounts the game (`mount_backport_overlay`, `sm_filesystem.c`), logged as `[IMG] backport overlay mounted (rw|ro): <backport> -> /system_ex/app/<TITLE_ID>` or `[IMG] backport overlay failed: …` plus a popup. (The NSFS file redirects and their 256 limit are the installed-package path, `sm_pkg_backport.c`.) The overlay is looked up under the game's own scan folder first, then every scan folder plus `/data/homebrew`, which is always included (`get_backport_scan_path`).
- Because the backport folder is the **upper, writable layer**, files the game creates in `/app0` (ampr_emu's `ampr_emu.index`, its log) presumably land in the backport folder. This is inferred from unionfs semantics and not console-checked. Hence the checklist: delete stale indexes in both places, and delete an old backport folder before installing a new build.
- **ampr_emu.log is compiled out by default** (`AMPR_EMU_DEBUG_LOG 0` in `ampr_emu_config.h`). Our `fakelibs/4.03` build (24 Aug) contains `/app0/ampr_emu.log`; the `fakelibs/4` build (GT7) has no log path at all, so FW 4 backports write no ampr_emu log. Index autobuild is on by default (`/app0/ampr_emu.index`).
- Config keys confirmed: `/data/shadowmount/config.ini`, `auto_update_ampr=0` default, `kstuff_no_pause=<TITLE_ID>` (repeatable). ShadowMountPlus stops a running BackPork payload itself (`main.c`).

##### Gap: the FW 7 fakelib set can't run a dump on its own
`fakelibs/7` holds only the five BackPork 7xx libraries. It has **no `libSceAmpr` (ampr_emu) and no `libScePlayGo` stub**, which every dump needs under ShadowMountPlus (Part 4), and **no `libScePsml`**: a Yōtei FW 7 backport leaves 9 `libScePsml_mfsr2` imports (FW 12.00) unknown that the FW 4 set's Psml provides. A FW 7 backport would depend on whatever ampr_emu the tester's ShadowMountPlus provides (global fakelib or auto-update). **Fixed (2026-10-09):** ampr_emu (24 Aug) and the PlayGo stub copied into `fakelibs/7` (manifest source says so); both engines now warn when neither the game nor the fakelib set has `fakelib/libSceAmpr.sprx` / `libScePlayGo.sprx` (`check_dump_fakelibs`, `GameScan.DumpFakelibWarning`). Psml stays out until a tester shows it's needed (its imports matter only on PS5 Pro).

---

#### Part 15: AC Black Flag Resynced and Ghost of Yōtei backport diffs (2026-10-09)

Sample folders were renamed again: `.original/backports/<TITLE_ID>/{default,4xx+,10xx+,decrypted}`: PPSA01317 = GT7, PPSA34547 = Control, PPSA26344 = Ghost of Yōtei, PPSA28183 = AC Black Flag (was `<Game>/4.00+`); both test suites updated.

##### AC Black Flag Resynced (PPSA28183 v01.006.038, Ubisoft Scimitar/Anvil engine)
Three variants. All SELFs in `default` are decrypted (PS5-style SELF, unwrap works); the backports are make_fself SELFs.
- `default`: original dump; SDK 0x12000038/0x13090001, param 13.60/12.00.
- `4xx+`: "FW 4.xx + PSSR + Quality 60 FPS (v2)", **PS5 Pro only**, tested on a Pro at FW 10.01. SDK 4.00 on every module, a "4xx-kit" libc, an 8-file fakelib set (Agc, AgcDriver, Ampr, AppContent, GameUpdate, NpEntitlementAccess, PlayGo, Psml 13.60), `psml_package/KPN9_v070.psp`. README in the folder.
- `10xx+`: only `eboot.bin`, `ShaderData.bin`, `fakelib/libSceAgcDriver.sprx` (a different AgcDriver build). It is a delta laid over `4xx+`: the user's note says "fix black screen crash on Pro, tested on 10.20".

**eboot `default` → `4xx+`: 408 bytes in 139 ranges.**
| Change | How | Generic? |
|---|---|---|
| SDK pair → 0x04000031/0x09040001 | procparam | yes (we do it) |
| `sceSaveDataMount3` `systemBlocks -1 → 0` | call site: `mov qword [rbp-0x88], -1` → `0` (VA `0x2bced72`) | **yes: the third game with this exact fix.** The README gives the reason: *ShellCore rejects -1 when the game SDK is below 5.00*. |
| `sceAudioOut2Set3DLatency` | call → `xor eax,eax` | yes (we stub it ret0) |
| `sceVideoOutAllowOutputResolutionWqhdDetection` (FW 6.00) | 3 calls → `nop` | **new rule**: ret0 |
| `sceNpCommerceDialogOpen2` (FW 7.01) | call → `mov eax, 0x80000001` | **new rule**: error stub |
| 126 `s_trap 1` → `s_nop` (`010092bf` → `000080bf`) in shader code inside a RW data segment, plus 168 more in `ShaderData.bin` | GPU hang fix for FW < 12 (BVH asserts), together with an AgcDriver hook that allocates a 24 MB compute scratch ring (`COMPUTE_TMPRING_SIZE`) | **new category: shader binaries.** The pattern is a library/hardware rule, not game logic, but it patches game data. Not supported yet. |
| `sceKernelGetOperationMode` → mode 3 (PLT + one call site), `sceKernelIsTrinityMode` → 1, `sceAgcGetIsTrinityMode` → 1 | PLT entries overwritten with stubs | **forces PS5 Pro mode**; an option, not a backport fix |
| Target-FPS clamp floor 20 → 60 (VA `0x304000a`, rip-relative operand pointed at a 60.0 constant) | game logic | game-specific tweak (Quality 60 FPS) |

**`4xx+` → `10xx+` (128 bytes, 14 ranges)** undoes everything Pro-specific and the 60 FPS tweak, and uses GT7's GOT→stub technique instead of overwriting PLT entries: a new cave stub `mode 2, lowEnergy 0` (= our `opmode_normal`), GOT of `sceKernelGetOperationMode` → it, GOT of `sceKernelIsTrinityMode` and `...WqhdDetection` → the `xor eax,eax; ret` tail. The 3 Wqhd calls and `sceNpCommerceDialogOpen2` are restored (they exist on 10.x). The savedata fix, Set3DLatency, the s_trap patches and SDK 4.00 stay. So the "Pro black screen" was the Pro-forcing itself on a console/firmware where PSSR doesn't work.

**Modules:** FontGsm, JobManager, NpCppWebApi, Pfs, memorywrapper: SDK only. `libaegir_f.prx`: SDK + 4 Http2 WebSocket calls (CreateRequest, SendDataMessage, SendTextMessage, Close; FW 5.02) → `mov eax, 0x80000001` at the call site. libc: replaced by the "4xx-kit" libc (README: 3 libSceLibcInternalExt functions → 0).

**`ps5bp backport default --firmware 4.50` compared:** SDK, savedata, Set3DLatency, IsTrinityMode, GetOperationMode (mode 2) and WebSocketCreateRequest are covered. **Missing** (reported `missing`): `sceVideoOutAllowOutputResolutionWqhdDetection`, `sceNpCommerceDialogOpen2`, `sceHttp2WebSocketSendDataMessage`/`SendTextMessage`/`Close`. Not covered: the shader `s_trap` fix and the AgcDriver scratch-ring hook. `sceSaveDataPrepare` is not patched by this game (our rule only rewrites the value 2, so it's a no-op if AC passes another value).

##### Ghost of Yōtei `4xx+ NOT TESTED` is really an SDK 10.00 backport
- eboot SDK 0x11000040/0x12590001 → **0x10000040/0x12090001**; param.json `requiredSystemSoftwareVersion`/`sdkVersion` 0x10000000. Despite the folder name it targets FW 10.x, which is why only one function needed fixing: `sceKernelGetOperationMode` (FW 11.00).
- Fix technique (new): the PLT entry is overwritten with a stub returning **mode 3 (Pro)**, and the import's NID string in `.dynstr` is renamed from `NH6xARDOVv8` to `+L98PIbGttk` (`scePthreadRwlockUnlock`, exists everywhere) so the loader resolves the slot. The slot is never used because the PLT no longer jumps through it.
- **The `default` eboot is not pristine** (like GT7's NOT_BACKPORT): 15 entitlement-label strings are replaced with `YOTEIDUPLEX`, an `or byte [rbp-0x240], 0x20` became `, 0`, and a `jmp rcx` became `jmp short 0xc9f776`. The backport eboot has the original values, so it was built from a different (clean) eboot. These differences are dump artefacts, not backport changes.

##### Update (2026-10-09, later): Yōtei `decrypted` and `dump` added, `4xx+` completed
- **`dump`** (same v02.002.000, same libc, same param.json as `default`) is the **clean eboot**: unwrap(`dump`) differs from unwrap(`default`) only in the 15 `YOTEIDUPLEX` labels, the `or`/`jmp` bytes and ELF header byte 0x3c. Its fakelib/ holds the dump's homebrew set: ampr_emu **Sep 6** (the same file as AC's), AppContent, GameUpdate, NpEntitlementAccess stubs (make_fself, SDK 2.00). No PlayGo stub.
- **`decrypted`** = unwrap(`dump`) byte for byte (eboot and libc), plain ELFs, no param.json.
- **The backport was built from `dump`**: dump → `4xx+` eboot is only 4 changes (ELF header 0x3c, SDK pair, the GetOperationMode PLT stub → mode 3, the NID rename). Everything from the earlier section stands.
- **`4xx+` now also ships `k9.psp` and `fakelib/libScePsml.sprx`**: the same files as the GT7 backport and our `fakelibs/4` set (13.60 Psml, J03950001, reads /app0/k9.psp). This is the third backport (GT7 FW 4, UFC 6 FW 9.60, Yōtei FW 10.x) that pairs that Psml with k9.psp. It confirms our automatic k9.psp rule. There's no ampr_emu in `4xx+`: under the ShadowMountPlus overlay the dump's own Sep 6 build stays in use, the build that ran on a Pro at 10.01 (AC).
- **libc (new in this delta)**: SDK → 10.00, and the three FW 11.00 libSceLibcInternalExt imports (`tB59hFLH3SA`, `OQ-dzhlnM28`, `qBS714-Jr3g`) get PLT stubs `xor eax,eax; ret`, with their NID strings renamed to `EHsF2i9FXPM` (`sceLibcInternalBacktraceForGame`, in every firmware). It's the same PLT-stub-plus-rename technique as the eboot. We stub the first two ret0 too. `qBS714-Jr3g` we **redirect** to the local `__cxa_thread_atexit_impl` (proven on GT7/Control) instead of ret0, so our output differs there on purpose.
- **Our pipeline on `dump`**:
  - **FW 4:** everything resolves except the known `gWT7X8H0bYs` (VrrStatus, FW 10.01, still to triage). The store set adds ampr_emu Aug 24, PlayGo and Psml, plus k9.psp.
  - **FW 10.01:** this is the tester's target. There is **no store set for FW 10** (lookup is exact folder, then major), so the 9 `libScePsml_mfsr2` imports (FW 12.00) stay missing. The tester solved them with the 13.60 Psml + k9.psp. We also warn about the Sep 6 ampr_emu and the missing PlayGo stub.
- **Added (same day): `fakelibs/10` and `fakelibs/11`**, each with only that Psml + `app0/k9.psp` (NOT TESTED, like the source). FW 11 also got an **SDK pair, `0x11000040/0x12590001`**, in both engines. Kitchen's table stops at 10. The pair is Yōtei's original modules' value; the FW 11.00 system libraries have the same PS4 half (`0x11000043/0x12590001`). The `dump` now backports to FW 10.01 and 11.00 with nothing missing, and both engines give identical output.

##### Conclusions
- **Saves:** no save-path redirect in any of the four games. `systemBlocks -1 → 0` is in GT7, Control and AC, so it's confirmed as a generic rule. Its condition is the **target SDK < 5.00**, not "always"; for FW 5+ targets it is unnecessary. GT7's `mount_mode → RDONLY` (Part 11) is still the only save patch that is game-specific.
- **"Force PS5 Pro mode" is a recurring, generic option** (AC 4xx+, Yōtei): `sceKernelGetOperationMode` → 3, `sceKernelIsTrinityMode` → 1, `sceAgcGetIsTrinityMode` → 1. It is a per-function rule set, but must be opt-in: on a base console or a Pro firmware where PSSR fails it gives a black screen (AC `10xx+`).
- New default rules (added 2026-10-09): Wqhd detection ret0, CommerceDialogOpen2 `ret:0x80000001`, WebSocket SendDataMessage/SendTextMessage/Close `err_http2` (same as CreateRequest). `sceSaveDataMount3` now has `"below": "5.00"`. AC at FW 4.50: nothing left unresolved in the eboot or `libaegir_f`.

##### Optional fixes (implemented 2026-10-09)
- `data/fixes.json` `"options"`: opt-in groups, never applied by default; a selected option's fixes replace the default fix for the same NID. CLI `--option KEY` (both engines), report line `options:` plus a WARNING with the option's warning text, JSON `options`.
- New fix kind **`plt_stub`**: stub body in a cave, the PLT entry jumps to it, the GOT slot is left alone. This is how AC and Yōtei force values, and it works even where the target firmware has the function (a GOT-only stub could be overwritten if the loader binds the slot).
- First option **`force_pro`**: `sceKernelGetOperationMode` → `opmode_pro` (mode 3), `sceKernelIsTrinityMode` → `ret:1`, `sceAgcGetIsTrinityMode` → `byte1`. Both engines produce byte-identical AC output with it.
- Experimental C# option **`auto_pro`** (Automatic Pro mode) preserves native console queries supported by the selected target; missing operation-mode/Trinity queries fall back to base-console values, including a one-byte false output for `sceAgcGetIsTrinityMode`. Keeps the default Psml-init guard below FW 9.00. Requires Known fixes. Mutually exclusive with `force_pro` in the GUI and rejected together by the pipeline. A FW 4 cook gives fixed base answers only on consoles whose firmware also lacks the query; where the firmware exports it (Trinity queries from 7.00, `sceAgcGetIsTrinityMode` from 9.00, `sceKernelGetOperationMode` from 11.00) the loader binds the native function over the GOT pre-fill, so a Pro is detected there (corrected 2026-10-14 after FINDINGS §6b; the original text claimed the fixed answers persisted on newer firmware). This is not a runtime resolver for APIs missing from the target. Replacement-library query behavior and both console paths require console testing. Already patched inputs are not restored to their original queries.
- **`audio_speaker_compat`** (Audio speaker compatibility) makes the previously automatic `sceAudioOut2GetSpeakerInfo` adapter opt-in, disabled by default. It clears the 3D-audio flag and caps the speaker mask at eight speakers. Enable it to reproduce the previous adapter; disable it to test native speaker reporting. Requires Known fixes. Required missing audio API stubs (including `sceAudioOut2Set3DLatency`) and existing Wwise patterns remain independent. Re-cook both variants from the same source: this option cannot undo input patches. Console compatibility and dialogue loss require A/B testing.
- **`save_mount_compat`** (Save mount compatibility) and **`save_prepare_compat`** (Save preparation compatibility) make the two derived save-data adapters independently opt-in, disabled by default. Mount changes `systemBlocks = -1` to `0` only below target FW 5.00; preparation changes mode `2` to `1`. Requires Known fixes. Enable both to restore their previous automatic behavior. Leaving them off preserves native arguments, which old firmware may reject; neither toggle undoes patches already present in the source. GUI options and Analyze/Cook use the same fix table.

##### The shader `s_trap` fix and the AgcDriver scratch hook (investigated 2026-10-09)
- **AC replaced every `01 00 92 BF` (`s_trap 1`) with `00 00 80 BF` (`s_nop 0`)**: all 164 in the eboot (all 4-aligned, all in the RW segment 7) and all 168 in `ShaderData.bin` (only 20 of them 4-aligned in the file). It's a blind byte-pattern replace; nothing else in `ShaderData.bin` changed.
- **The 164 come from SDK code, not the game**: the original eboots of **Yōtei and Control also have exactly 164** (GT7: 202), in the same kind of RW segment. README: they are BVH (ray-tracing acceleration structure) asserts. **Neither GT7's nor Control's console-tested backport touched them** (Control ran on FW 10.01, GT7 on FW 4), so the patch is not required by itself; AC applied it together with the scratch hook below.
- **`libSceAgcDriver` scratch hook.** Both AC AgcDrivers are a stock Sony build plus the same 1,373-byte hook: `4xx+` = Control's build (J03718109, our `fakelibs/4.03`), `10xx+` = GT7's build (J03828141, our `fakelibs/4`). The code segment is extended (`p_filesz`/`p_memsz` 0xb9ce → 0xbf2d, the `--extend-text` technique) and the first instruction of `sceAgcDriverSubmitMultiDcbs` (0x4cc0) and `sceAgcDriverSubmitMultiAcbs` (0x4d40) jumps to a wrapper. The wrapper saves the argument registers and calls a PM4 scanner on each submitted command buffer (≤ 0x40000 dwords). The scanner:
  - walks type-3 packets, follows `INDIRECT_BUFFER` (op 0x63) chains, and tracks `SET_SH_REG` (op 0x76) writes to registers 0x213, 0x218 and the compute user-data registers 0x240–0x249;
  - on `DISPATCH_DIRECT`/`DISPATCH_INDIRECT` (op 0x15/0x16) with scratch enabled, it allocates a **24 MB direct-memory block named `ac_scratch`** once, writes its address into the scratch descriptor, and rewrites the tmpring size dword to `0x40080`.

  The README calls this "COMPUTE_TMPRING_SIZE"; older firmware does not set up a compute scratch area. The `10xx+` "Pro black-screen fix" is the same hook moved onto GT7's AgcDriver build, plus removal of the Pro forcing.
- **Implemented as option `gpu_scratch` (2026-10-09):** the two hooked AgcDrivers are store variants `fakelibs/4/gpu_scratch` (AC 10xx+) and `fakelibs/4.03/gpu_scratch` (AC 4xx+); the option also applies the `s_trap 1` → `s_nop` pattern to modules (4-aligned hits in non-executable segments only: all 164/202 hits in AC, Control, Yōtei and GT7 are there; the 3 unaligned hits in Control's Agc fakelib are skipped) and to every hit in `ShaderData.bin`. On AC at FW 4.50 it reproduces AC's `ShaderData.bin` and `10xx+` AgcDriver byte for byte. GUI: Overview tab, "Optional patches" (per game, not remembered).
- **Generic?** The hook acts on Agc command buffers for any game, so it belongs to the fakelib, not to a game; the name `ac_scratch` is cosmetic. Whether a game needs it depends on its compute shaders using scratch; GT7 and Control ran without it. Candidate approach: keep it as an **opt-in fakelib variant** in the store (AgcDriver + hook, with provenance) and the `s_trap` replace as a companion option. Shader patching needs a new pipeline step, because `ShaderData.bin` is not a module and the pattern is unaligned there.

---

#### Part 16: Battlefield 6 (PPSA19534) backport diff (2026-10-09)

Frostbite engine. `default` works on FW 10.xx and later (its executables are already fake SELFs with SDK 0x10000040; param.json requires 12.70); `4xx+` is the FW 4.xx backport.

##### Two executables
- Besides `eboot.bin` the game ships **`SP/BattlefieldGame.Main_Ps5_retail.elf`**, a second fake-signed executable (the single-player game, 249 MB). The backport patched it like the eboot (SDK 4.00, two guarded wrappers).
- **Our tool skipped it**: module discovery only took `eboot.bin` and `*.prx`/`*.sprx`. **Fixed**: both engines now also take `*.elf` (`game.EXECUTABLE_EXTENSIONS`, `GameScan.ModuleExtensions`). With that, `SP/…elf` gets SDK 4.00 and the same fixes as the eboot.

##### What `default` → `4xx+` changed
| Module | Change |
|---|---|
| all | SDK → 0x04000031/0x09040001 |
| `libc.prx` | only the known `MapNamedFlexibleMemoryInternal` → `MapFlexibleMemory` NID swap |
| `libSceNpCppWebApi.prx` | SDK only |
| `eboot.bin`, `SP/…elf`, both `Engine.Render.Core2.PlatformPs5.retail.prx` | **guarded wrappers** for `scePadIsRemoteController` (FW 10.01), `sceKernelIsTrinityMode` (7.01), `sceVideoOutAllowOutputResolutionWqhdDetection` (6.00) |
| `eboot.bin` | memory hooks on `sceKernelMapDirectMemory2`, `sceKernelBatchMap2` and one `sceKernelAllocateDirectMemory` call site (below) |
| `fakelib/` | the store-like set (Agc, AgcDriver, Ampr, PlayGo, Psml) plus AppContent, GameUpdate, NpEntitlementAccess and a **`libkernel.sprx`** |

##### Guarded wrapper (new technique)
The PLT entry jumps to a chain that loads the import's GOT value into r11 and returns a fallback when r11 is 0, or equals the PLT entry's own `push` address (never bound), or equals **`0x800032780`** (libkernel base `0x8_0000_0000` + `0x32780`: the address the loader binds unresolved imports to, which raises `PRX_NOT_RESOLVED_FUNCTION`). Otherwise it `jmp r11`, so the real function is used on firmware that has it.
- Fallbacks: `scePadIsRemoteController` → `*(u8*)rsi = 0; return 0`; WqhdDetection → `0x8029001c` (a VideoOut error; AC used `return 0`, which we keep); IsTrinityMode → GOT pointed at `xor eax,eax; ret`.
- **Trade-off**: one backport works on every firmware and uses the real function where present, but the trap address is firmware-specific (libkernel layout). Candidate future fix kind (`guarded`), needs the trap offset per firmware from our libkernel dumps.

##### Memory hooks (firmware-specific, not rules)
- `sceKernelMapDirectMemory2`: GOT → `mov eax, 1; ret` and one call site forced to `eax = 1` (a non-zero result makes the engine take its other mapping path).
- `sceKernelBatchMap2`: PLT → code that, when the GOT holds the not-resolved trap, jumps to **trap address − 0x19d70**: libkernel's internal implementation on the target firmware, reached by a fixed offset.
- One call site (`0x1c92fe2`) now goes through a dispatcher that compares the low 32 bits of `sceKernelAllocateDirectMemory`'s GOT value with several known offsets (0x17d60, 0x17990, 0x17a10, 0x18430, …): **it identifies the firmware by where libkernel's export sits**, then jumps to a per-firmware internal function.
These rely on per-firmware libkernel offsets. Our 43 firmware dumps could provide such tables, but it's a large step; recorded only.

##### `fakelib/libkernel.sprx`
A Sony libkernel (build J03718109, the "4xx kit" build id; SDK 12.00), fake-signed. Its exports are closest to FW 9.x and include 12 functions from FW 10.01–12.00, among them `sceKernelGetOperationMode`, `SyncOnAddressWait8/16` and the `CreateUuidV1/V4/V7` set. Shipping a newer libkernel as a fakelib is another way to provide missing kernel functions. ShadowMountPlus deletes `fakelib/libkernel.sprx` for installed packages (Part 4), so it only works for dumps.

##### Our tool on `default` at FW 4 (after the `.elf` fix)
- New rule `scePadIsRemoteController` → `out2_byte0` (BF6's fallback).
- Still `missing` in eboot and `SP/…elf`: `WxLcN76q19o`, `8weiQyIdI3A` (libSceNpPremiumAddcont, FW 9.00, refs=1 each). BF6's backport left them unpatched too; recorded in `data/left_unpatched.json`.
- Everything else BF6 needed is covered by existing rules (IsTrinityMode, Wqhd, VRR peg, CommerceDialogOpen2, SyncOnAddress, the libc swap, saves).

---

#### Part 17: Beast of Reincarnation (PPSA29343) backport diff (2026-10-09)

Game Freak, Wwise audio (25 `prx/ak*.prx` plugins). `default` = original dump (SDK 0x11000040, param requires 12.60; works on FW 11.xx+), `4xx+` = FW 4.xx backport.

##### What `default` → `4xx+` changed
- **All 30 modules**: SDK → 0x04000031/0x09040001 (the Wwise plugins and sce_module libraries: nothing else).
- `libc.prx`: the known `MapNamedFlexibleMemoryInternal` swap, and the `qBS714-Jr3g` call retargeted to libc's own `__cxa_atexit` (our redirect rule, done at the call site).
- `eboot.bin`: `sceKernelIsTrinityMode` GOT → ret0, `sceAudioOut2Set3DLatency` call → `xor eax,eax` (both existing rules), plus:
  - **Wwise 7.1.4 → 7.1**: the channel mask `0x2d63f10c` → `0x63f108` at two sites, the same fix CONTROL made (Part 7). Both games carry the **same Wwise build**: the speaker-setup site (`mov [rbp-0x30], rdx; mov edx, 0x2d63f10c; mov eax, 0x63f108`) and a channel-switch site (`mov ecx, 0x3102; jmp; mov ecx, 0x2d63f10c; jmp; mov ecx, 0x4101`) each occur exactly once in Beast and CONTROL and in no other sample (AC has a different Wwise build; GT7, Yōtei and BF6 use other audio). CONTROL patched the first, Beast both.
  - **`usleep` → busy-wait**: the PLT entry becomes `push rdi; call scePthreadYield; call sceKernelGetProcessTimeCounter; deadline = counter + usec * 0x63a`, then a loop on `sceKernelGetProcessTimeCounter` until the deadline, `return 0`. `usleep` exists on FW 4, so this is a behaviour change (probably precise frame pacing; 0x63a = 1594 counter ticks per µs). Not made a rule: its reason is unknown and it spins a core. Candidate opt-in option if a tester reports stutter.
- `sceCoredumpWriteUserString` (FW 8.00, eboot, refs=1) was shipped unpatched (recorded in `data/left_unpatched.json`).

##### Implemented
- **Default byte patterns** (`data/fixes.json` `"patterns"`, applied with the known fixes): name, find/replace, `code: true` = search executable segments at any offset (the find bytes carry enough context to match one function). The two Wwise sites are the first entries; on Beast at FW 4 they reproduce the backport's audio patch exactly (`0x2d63f10c` 3 left, `0x63f108` 12, as in `4xx+`). Report: `pattern  Wwise 7.1.4 -> 7.1 (…)  x1`. Both engines identical.
- With this, `ps5bp backport default --firmware 4` covers everything the Beast backport did except the `usleep` busy-wait; the only unresolved import is `sceCoredumpWriteUserString`, which the backport left too.

---

#### Part 18: Borderlands 4 (PPSA01494) backport diff (2026-10-09)

Unreal Engine 5, Wwise, Coherent (libcohtml, librenoircore), libbifrost. `default` = original dump (SDK 0x10000040, param requires 12.70; works on FW 10.xx–12.xx), `4xx+` = FW 4.xx backport.

##### SELFs with data after the version block (unwrapper fix)
- The backport's eboot, libbifrost, libcohtml and librenoircore are PS5-style SELFs with header byte 4 = `0x10` and flags `0x32` (the same variant as AC's `10xx+` AgcDriver). **0x1E8 bytes follow the trailing PT_SCE_VERSION block** (apparently an appended signature), so our unwrapper, which took the block from the file's end, failed with "truncated trailing PT_SCE_VERSION data".
- **Fixed in both engines**: when the end position doesn't fit, the block is looked for at the declared file size ± 0xF and accepted only if every record is well-formed. Both engines unwrap the four files to identical ELFs. Tests: `tests/test_bl4.py`, `SceVersionTests.AppendedDataAfterVersionBlock_UnwrapsLikePython`.

##### What `default` → `4xx+` changed
- **libc replaced** by an older Sony libc (build J01397812, 2,565 exports instead of 2,925). The modules' libc++ imports it lacks (`_Throw_bad_weak_ptr`, `__throw_bad_variant_access`, `_Cnd_init_with_default_name_override`) are pointed at `xor eax,eax; ret`. We keep and patch the game's own libc instead, so this doesn't apply to us.
- **eboot**:
  - **`sceSaveDataMount3` systemBlocks −1 → 0**, the fourth game with it. Done as an adapter in an **extended code segment** (`p_filesz` 0x13b5dc → 0x13c000), with four call sites retargeted to it (`cmp qword [rdi+0x18], -1; jne; mov qword [rdi+0x18], 0; jmp` to the PLT).
  - GOT → `ret0` **and the symbols rewritten into local definitions** (`st_shndx = 1`, our `--define-symbol`): `sceKernelIsTrinityMode`, `scePadIsRemoteController` (plain ret0, which leaves `*isRemote` unset; our `out2_byte0` also clears it), `sceVideoOutVrrPeg/UnpegFromFixedRate`, `sceAudioOut2Set3DLatency`.
  - **Wwise 7.1.4 → 7.1 at three sites**: the speaker-setup and channel-switch sites already in our patterns (same Wwise build as CONTROL and Beast), plus a **packed default** `movabs rdx, 0x2d63f10c2d63f10c` whose low half becomes `0x63f108`. CONTROL and Beast left that one; added as the third pattern (unique in every sample, present in BL4, Beast and CONTROL).
  - **`sceNetPoolCreate` → `mov eax, 1; ret`** in the eboot and in libbifrost (PLT and symbol). The function exists since FW 1.00 and no other sample touches it; returning pool id 1 skips the real call. Reason unknown (maybe the newer SDK's size or flags are refused on FW 4). Recorded, not a rule: a fake pool id could break later net calls.
- **Other modules**: SDK only (mediadecoders, FontGsm, JobManager, NpCppWebApi, Pfs). The three middleware libraries got only the libc++ stubs and libbifrost the NetPoolCreate stub.

##### Our tool on `default` at FW 4
Nothing unresolved: all 31 imports BL4 needed are covered (existing rules + the three Wwise patterns). Differences from the backport: we keep the game's libc (patched), clear `*isRemote`, and leave `sceNetPoolCreate` alone.

---

#### Part 19: Dying Light: The Beast (PPSA24003) backport diff (2026-10-09)

Techland (C-Engine), Wwise, Epic Online Services (`EOSSDK-PS5-Shipping.prx`), ffmpeg, NVIDIA NRD. Three folders: `default` = original dump (SDK 0x11000040, param 13.60/11.00), `decrypted` = the same modules as plain ELFs, `4xx+` = FW 4.xx backport.

##### `decrypted` = what our unwrapper produces
Every segment of `decrypted/*.prx` equals our unwrap of `default/*`; only stale section-header fields in the ELF header differ (`e_shoff`/`e_shnum` point past the file end). Backporting `decrypted` gives the same fixes as `default`. **Our output for ELF input used to be plain ELF**, which the console can't load; **since 2026-10-09 both engines always write fake-signed SELFs** (the user's choice), so `decrypted` and `default` give byte-identical output, and the report warns about plain-ELF modules left unchanged in the game folder (they're not in the overlay and won't load). Tests: `tests/test_decrypted.py`, `DecryptedInputTests`.

##### What `default` → `4xx+` changed
- SDK only: the five Wwise `Ak*.prx` plugins, ffmpeg, libNRD, runtime_prospero.
- `libc.prx`: the known `MapNamedFlexibleMemoryInternal` swap and the `qBS714-Jr3g` → `__cxa_atexit` redirect (here as GOT → a `jmp` stub in padding).
- `EOSSDK-PS5-Shipping.prx`: `sceKernelGetOperationMode` → mode 2, written over the PLT entry (our `opmode_normal`).
- `eboot.bin`:
  - IsTrinityMode GOT → ret0, Set3DLatency call → `xor eax,eax` (existing rules).
  - `scePadIsRemoteController` calls → `mov byte [rsi], 0; xor eax, eax; ret`, in a page-extended code segment: **identical to our `out2_byte0`** (BF6's fallback).
  - VRR calls (`PegToFixedRate`, `UnpegFromFixedRate`, VrrStatus `kP2L8t3j-aM`, `LibwuIonIBw`, `-q-vbO859Tw`) → `mov eax, 0x8029001c; ret` (a VideoOut error), where GT7 used ret0. We keep ret0 and **added `-q-vbO859Tw` → ret0** (FW 5.02), the third VrrStatus function.
  - **No Wwise patch**, although DL has the same Wwise build (all three of our Wwise patterns match). So DL's backport ran without the 7.1.4 → 7.1 fix: the fix is needed only when the console reports 3D audio. It stays a default (its cost is 7.1 instead of 7.1.4).
- **EOS SDK: 112 `libSceNpCppWebApi` C++ imports left missing.** DL doesn't ship its own `sce_module/libSceNpCppWebApi.prx` (most samples do), so the console's FW 4 library is used, and it lacks the newer friends, presence, blocking and profanity-filter APIs. The backport left them all unpatched (reached only from online features; lazy binding). Recorded in `data/left_unpatched.json` (source DL).

##### Our tool on `default` at FW 4
All eboot imports resolved after the new VrrStatus rule; only the 112 EOS WebApi imports stay `missing`, now tagged "left unpatched by" DL.

##### Test fix
`tests/test_phase4.py` compared the whole `LEFT_UNPATCHED` table with GT7's unresolved set. Since other backports add entries (BF6, Beast, DL), it now compares GT7's own entries, as it already did for Control.

---

#### Part 20: EA SPORTS FC 27 (PPSA34015): two backports that differ only in ampr_emu (2026-10-09)

Frostbite (like BF6). **No original dump**: `default` (works on FW 12.xx and later) and `4xx+` (FW 4.xx) are both backports, already fake-signed with fakelibs.

##### The only difference is the ampr_emu build
Every module is byte-identical between the two folders except `fakelib/libSceAmpr.sprx` (and `default` carries a `sce_sys/param.json`):
- `default`: ampr_emu **Sep 6 2026** (`Prospero_ReleaseHooksLooseStdio`, 303,878 bytes). The same file ships in AC Black Flag's `default` and `4xx+`.
- `4xx+`: ampr_emu **Aug 24 2026** (`Prospero_ReleaseHooks`, 255,126 bytes): our `fakelibs/4.03` and `fakelibs/7` build, also in Control's and Dying Light's backports.

So the Sep 6 build is what limits `default` to FW 12.xx+. That matches Control's README ("reads dump data wrong, asks for ~9 GB"). AC's Pro build ran with it at FW 10.01, so it is broken on older firmware rather than everywhere. `data/known_builds.json`'s note now says so (state stays `broken`, Aug 24 stays `ok`).

All ampr_emu files across the samples: GT7's (`8248448608f3`, no build date, 218,678 bytes; also BF6 `default`, Control `default`, `fakelibs/4`), BF6 `4xx+` (`d906bb7b014d`, same size, another undated build), Aug 24 (`362c84e31e3a`), Sep 6 (`0aadd31dbe72`).

##### Implemented: check the game's own fakelibs too
Known-broken builds were flagged only for the fakelib store's files (via its manifest). Both engines now also check the game's own `fakelib/*` modules that the store or `--fakelib` doesn't replace (build date read from the module), and warn: `fakelib/libSceAmpr.sprx (libSceAmpr built Sep  6 2026) is a known-broken build: …`. Tests: `tests/test_decrypted.py` `TestGameFakelibBuilds`, `GameFakelibBuildTests` (Python-matched warnings).

##### What the FC 27 backport left unpatched
Running our tool over the already-backported `4xx+` at FW 4 (store off) adds what the backport did not do: the three argument fixes (`always`), `sceNpCommerceDialogOpen2`, `sceNpEntitlementAccessGetGameTrialsFlag`, VRR peg/unpeg (Engine.Render), and in libc the `qBS714-Jr3g` redirect, the SyncOnAddress adapters and the four libSceLibcInternalExt stubs (the MapNamedFlexibleMemory swap was already there). Reapplying the argument fixes is harmless (each only rewrites the newer value). The game evidently never calls the others on its tested paths. Its three still-missing imports (`sceCoredumpWriteUserString`, two libSceNpPremiumAddcont) are the ones Beast and BF6 also left; FC 27 added as a source in `data/left_unpatched.json`.

---

#### Part 21: EA SPORTS UFC 6 (PPSA23566), a plain-ELF backport tested on a PS5 Pro at FW 9.60 (2026-10-09)

Frostbite (EA Sports). `default` = a fake-signed dump (SDK 0x12000038, param 13.60/12.00) with ampr_emu and a PlayGo stub; `tested on PS5 Pro 9.60` = its backport. No README; the folder name is the only information.

##### Update (2026-10-09, later): `decrypted` and `dump` added
- `decrypted` (dated 2026-09-19): the **original v1.004 modules as plain ELFs** (SDK 0x12000038), with a partial `sce_sys` (no `param.json`). Every segment equals our unwrap of `default`, so it is the exact baseline the tester backported; `decrypted` → `tested` differs only by the changes listed below.
- `dump` (dated 2026-10-07): **v1.005.000**, a newer game update, as decrypted PS5-style SELFs (no encrypted segments), without `fakelib/`. `ps5bp backport dump` resolves every import at **FW 9.60** (`fakelibs/9.60`, `k9.psp` added: the game imports libScePsml) and at **FW 4** (`fakelibs/4`). The EA Sports Frostbite pattern and all rules still match the update (the Mount3 adapter even lands at the same cave), so the rules carry over from one game version to the next. The store sets supply ampr_emu and the PlayGo stub the dump lacks.

##### The tested backport ships plain ELFs
Every module in the tested folder is a **plain, unsigned ELF**, and it ran on a PS5 Pro at FW 9.60. So with that loader setup plain ELFs load too. Our output stays fake-signed (what every other tested backport ships); the plain-ELF warning now says "most tested backports ship fake SELFs … sign them if the game does not start" instead of "the console loads signed files".

##### What changed (default → tested)
- SDK → **0x04000031/0x09040001 (4.00)** on every module, although the console was at 9.60.
- eboot: `sceSaveDataMount3` systemBlocks −1 → 0 at the call site (**the fifth game**), and one EA Sports Frostbite value: in the branch where a global format equals `0x30c`, the size field `[rbp-0xf0]` = `0xc00` → `0x800` (read as 12 × 0x100 → 8 × 0x100: probably 12 → 8 audio channels, the Frostbite counterpart of the Wwise fix; inferred, not traced).
- libc: MapNamedFlexibleMemory swap; the `qBS714-Jr3g` wrapper made `return 0` (Control's variant).
- Engine.Render, NpCppWebApi: SDK only.
- fakelibs: **Agc, AgcDriver and Psml from Sony build J03950001** (FW 13.x, newer than our newest decrypted dump, 12.70) with SDK 4.00, and ampr_emu **Aug 24** (the dump ships the **Sep 6** build, "PackedStdio" flavour): the same swap as FC 27.

##### Implemented
- **Default pattern** "EA Sports Frostbite: 0x30c output buffer 0xc00 -> 0x800" (34-byte signature). FC 27 already carries `0x800` in both its backports and UFC's tested backport made the change, so two EA backports agree; not in Battlefield 6 (different code). On UFC `default` it matches once.
- **Store set `fakelibs/9.60`** from the tested backport: Agc, AgcDriver, Psml (J03950001), ampr_emu Aug 24, and the dump's PlayGo stub. The tested files are plain ELF; they were fake-signed for the store (an unchanged store fakelib is copied as it is). With it, `ps5bp backport default --firmware 9.60` resolves every import (without it: 14 Agc/Psml imports of FW 10.01–12.00 missing, and no fakelibs at all for 9.x targets).
- At FW 4 UFC resolves fully with the existing rules and `fakelibs/4`.

##### Sony build id → firmware (from the 43 dumps' libSceAgcDriver)
The `W:/Build/J…` id in a Sony library identifies the firmware it came from: e.g. J01675412 = 4.00, J01736804 = 4.03, J03308729 = 9.60, J03376575 = 10.01, **J03718109 = 12.00** (Control's and AC's "4xx kit" Agc/AgcDriver/Psml, BF6's libkernel), **J03828141 = 12.70** (GT7's Agc set, `fakelibs/4`), J03950001 = newer than 12.70 (13.x; 13.60 is encrypted). Each firmware has three ids (one per Sony build tree), rising with time; 5.02 and 5.10 share theirs.
- **Implemented**: `data/sony_builds.json` (generated by `tools/gen_sony_builds.py` from the dumps: 123 ids; an `evidence` section adds J03950001 = 13.60 from AC's README). Both engines print `Sony build J03828141 = FW 12.70` per Sony module in the report (JSON `sony_build`), `fakelib list` shows it, and the app's Store page has a Firmware row. Unknown ids are placed by number ("between FW 12.70 and 13.60"). A game's own `sce_module` libraries show the SDK they came with (UFC 6's libc: between 11.60 and 12.00).
- Our store sets: `4` = Agc/AgcDriver from **12.70**, Psml from **13.60**; `4.03` = **12.00**; `7` = **10.01** (our BackPork build); `9.60` = **13.60**.

##### PSSR models and k9.psp
- **PSSR** (PlayStation Spectral Super Resolution, the PS5 Pro's ML upscaler) runs in **libScePsml** (`scePsmlMfsr*` / `libScePsml_mfsr2`). Its neural-network weights are **`.psp` model packages** (magic `PSP\0`) in the firmware: `/system/common/psml_package/` holds `MFSR4K_v070.psp`, `MFSR8K_v070.psp`, `BCSISR_v070.psp`, `MFSRLW_v070.psp` (11.20+) and **`KPN9_v070.psp`** (from 12.40: 349 KB there, **4,135,904 bytes in 13.60**). Psml builds the path with `"/%s/common/psml_package/%s"`. The models change with firmware (MFSR4K: 8.0 MB in 9.60, 10.7 MB in 10.20, 13.4 MB in 11.20, 8.2 MB from 12.40).
- **`k9.psp` is FW 13.60's `KPN9_v070.psp`, byte for byte, renamed.** Games built against a 13.x SDK use the KPN9 model, which older firmware lacks (or has in its early 12.40 form). The **13.60 Psml** used as a fakelib (Sony build J03950001: GT7, Yōtei, UFC 6, our `fakelibs/4` and `fakelibs/9.60`) is patched to read **`/app0/k9.psp`** (a fixed string; the zones `KPN9::zone0..2` are the 13.x model's), so the backport ships the model in the game folder. AC uses another layout: its 13.60 Psml reads `/app0/psml_package/%.0s%s` and ships `psml_package/KPN9_v070.psp` (2,757,600 bytes, a version between 12.40 and 13.60). The 12.00 "4xx kit" Psml (Control, BL4, BF6, Beast, DL, FC 27, `fakelibs/4.03`) reads only the system folder.
- It matters on a **PS5 Pro** only (PSSR); a base PS5 never loads the model. Without `k9.psp` an app0-patched Psml can't load KPN9 on a Pro: PSSR fails (black or unupscaled image, depending on the game).
- **Implemented**: a store set can have an **`app0/` folder** (`fakelibs/4/app0/k9.psp`, `fakelibs/9.60/app0/k9.psp`). A file is added to the output's game root **only when it is needed**: a fakelib of the set that the game uses (it exports a function some game module imports) contains `"/app0/<file>"`, and the game doesn't ship the file itself (report: `added file k9.psp (from the fakelib set)`, JSON `app0_files`; both engines).
- **Not an optional patch**, by design: the model is read only on a PS5 Pro, for a game that imports `libScePsml`, when the set's Psml is the app0-patched 13.60 build. A base PS5 never loads it, so shipping it costs only 4 MB, while leaving it out breaks PSSR on a Pro. The automatic rule adds it exactly when a Pro could need it. Example: *Harvest Moon: The Winds of Anthos* (PPSA28548) imports no Psml at all, so its FW 4 backport no longer gets `k9.psp` (the first cook, before this rule, did); GT7 imports `libScePsml`/`_mfsr2`/`_quality8K`/`_specularAA` and gets it.
- Online: nothing public documents `k9.psp` or app0-patched Psml (searches return only spam); PSSR is a PS5 Pro feature, and a 2026 firmware made PSSR 2.0 the Pro default even for games built for 1.0 (VGC), which fits KPN9 being the newer model in 12.40/13.60.

---

#### Part 22: God of War Sons of Sparta (PPSA28997), a Unity FW 4.xx backport (2026-10-09)

The folder is a single flat backport, with no original dump next to it and no tester note, so its test status is unknown. Contents: v01.008.001, param.json left at 13.20/11.00. **Every module is a make_fself SELF at SDK 0x04000031/0x09040001**, `sce_sys/about/right.sprx` included. The first **Unity (IL2CPP)** game in the samples:
- Modules: `eboot.bin` (PS5Player_IL2CPP, Unity "PS5_11_00"), `Media/Modules/{Il2cppUserAssemblies (114 MB), PS5Util}.prx`, `Media/Plugins/{CommonDialog, PSN, SaveData, VoiceInputPlugin, lib_burst_generated, libfmod, libfmodstudio, libresonanceaudio}.prx`, `sce_module/{libc, libSceNpCppWebApi}.prx`. Our module scan already finds them all (any `.prx/.sprx` under the game).
- fakelib/: Agc, AgcDriver and PlayGo are **GT7's** (= `fakelibs/4`), ampr_emu is the **Aug 24** build (= `fakelibs/4.03`/`7`), and Psml is the 12.70 build J03828141 shipped by DL and FC 27. There's no `k9.psp`, and none is needed: we add none either, because no game module imports a function that this Psml exports.

##### Gap found and fixed: opaque PT_SCE_VERSION data
- `Media/Modules/PS5Util.prx` (Unity's playstation-extras psutils) failed to unwrap with "malformed trailing PT_SCE_VERSION data". Its PT_SCE_VERSION segment (0x125 bytes) sits **exactly at the declared size and ends the file**, but its bytes aren't `name:` records (opaque, hash-like).
- Fixed in both engines (`unpack_fself_file`, `Self.Unwrap`): a block at exactly the declared size that ends the file is taken as-is. The record check now runs only when the block is found by searching around that position (the Yōtei −8 and BL4 appended-data cases). Unwrap is identical in both engines, and sign → unwrap gives the same ELF back. Tests: `tests/test_gowsos.py`, `SceVersionTests.OpaqueVersionBlock_UnwrapsLikePython_AndSignRoundTrips`.

##### What the tester patched
- **eboot**: `sceKernelGetOperationMode` gets a GOT stub `mov dword [rdi],2; mov dword [rsi],0; xor eax,eax; ret` (mode 2 = base PS5), equivalent to our `opmode_normal` (source `GOWSOS` added to that rule).
- **Left unpatched, with untouched PLT entries** (lazy binding: each fails only if called):
  - eboot: `sceKernelIsTrinityMode`, `scePadIsRemoteController`, `sceVideoOutVrrPeg/UnpegFromFixedRate`, `sceHttp2SetRequestNoContentLength` (FW 5.02);
  - PSN.prx: `sceNpCommerceDialogOpen2`;
  - libc: all 7 FW 6–11 imports (`tB59hFLH3SA`, `OQ-dzhlnM28`, `qBS714-Jr3g`, SyncOnAddressWait32/64/Wake/Wait).

  We have proven rules for all of them except the HTTP/2 one; recorded in `data/left_unpatched.json` as `GOWSOS`. The libc ones matter if the game calls them on FW 4. GT7 needed the SyncOnAddress adapters.

##### New rule
- **`bEegosRhgM0` `sceHttp2SetRequestNoContentLength` → ret0, DERIVED (not console-tested).** The Unity player calls it once, only when a request has no known length (else `sceHttp2SetRequestContentLength`), and ignores the result in both branches.

##### Our pipeline on it (FW 4)
Every import resolves. Fixes applied:
- eboot: IsTrinity, PadIsRemote and Vrr stubs, plus the new HTTP/2 stub;
- PSN.prx: CommerceDialogOpen2 → 0x80000001;
- **SaveData.prx** (the Unity plugin, not the eboot): Mount3/Prepare adapters;
- libc: the full GT7 set.

Both engines produce identical files.

---

#### Part 23: Hades II (PPSA36082), a console-tested FW 4.03 backport with a full README (2026-10-09)

Folders: `default` (original v01.006.000, SDK 0x12000038/0x13090001, param 13.60/12.00; FMOD audio, no Psml import) and `4xx+`. The `4xx+` backport is console-tested on FW 4.03 (boots, saves, loads). Its README (Vietnamese/English) documents every change, and the diff `default` → `4xx+` matches it exactly. The README is the most useful one so far, because it explains why each change was made.

##### What the backport does
- **SDK fields**: 0x04030031/0x09040001 on every module (FW 4.03 preset). param.json lowered to 4.03. The .sceversion block is left as-is ("firmware doesn't enforce it").
- **Signing**: every module make_fself (PS4-style). fakelib/ = **our `fakelibs/4.03` Agc + AgcDriver byte for byte** ("4xx kit"), no Psml (not imported), no ampr_emu or PlayGo.
- **eboot**: 3 **PLT entries overwritten in place, NIDs not renamed** (the unresolved imports stay; the game still loads on 4.03, which confirms lazy binding again):
  - `La9uyZv4Kvw` → `mov eax,0xff; ret`;
  - `sceKernelGetOperationMode` → `xor eax,eax; ret` (the caller pre-initialises both outputs and branches only on `*rsi == 1`);
  - Wqhd detection → ret0.

  Plus the save fix at the call site: `mov qword [rbp-0x138], -1` → `0`, field +0x18 of the Mount3 struct. This is the **sixth game with the systemBlocks fix**. The README adds that libSceSaveData is identical on 4.03, 4.50 and 12.00, so the 0x809F0000 comes from the old save-data service.
- **libc replaced** by the SDK 11 libc of Beast of Reincarnation's console-run backport. On top of it:
  - 7 PLT stubs `xor eax,eax; ret` (libSceLibcInternalExt ×3, SyncOnAddress ×4);
  - `mov ecx,0x8000` → `mov ecx,0` at both `sceKernelMapFlexibleMemory` call sites (VA 0xb0ad, 0xb2c0), "heap fix";
  - libSceJobManager's `g0mZgRTFPoQ` (`std::_Xbad_function_call`, FW 11) renamed to `L1SBTkC+Cvw` (abort), only because the replacement libc doesn't export it. We keep the game's own libc, which does, so this doesn't apply to our output.

##### Rule changes (both engines, data only)
1. **`La9uyZv4Kvw` ret0 → `ret:0xff`.** It is the process CPU-affinity mask, verified in FW 12.70 libkernel: a pthread_once initialiser reads the app info and caches **0xff** (native title) or **0x1fff**, never 0 for a game. Hades II feeds it to thread affinity (`and rax,0xaa`) and checks `popcnt(mask) >= 5`, so ret0 breaks it. GT7 (our old source) only forwards the value to another call, so 0xff is just as valid there. `test_gt7.TestStub` now skips the body comparison for this NID. Our dumps say the function first appears in FW 12.40 (the README says 12.70).
2. **`4h6F1LLbTiw` swap → adapter**: `and ecx, 0xffff7fff`, then jump to `sceKernelMapFlexibleMemory` (`IWIBBdTHit4`). libc's heap growth passes flags **0x8000**, which only the *internal* entry accepts. The public function rejects it on **every** firmware:
   - FW 1.00–6.50: `and ecx, 0xffbfff6f` (allowed bits 0x400090);
   - FW 12.70: `test ecx, 0x88000` → `0x80020016` (EINVAL) as its first instruction.

   So the old plain swap failed as soon as libc grew its heap ("Failed to create the heap", 0xa002000e; the README says Onimusha PPSA27836 died at boot from it). GT7 got away with it because it never took that path. The swap stays console-proven, and the flag clearing ships in this console-tested backport.
3. Source `HADES2` added to GetOperationMode, Wqhd, Mount3 and both rules above.

##### Our pipeline on `default` (FW 4.03)
Everything resolves, using the `fakelibs/4.03` set. Both engines give identical files and reports. Differences from the tester's build, all deliberate:
- We keep the game's own libc, with our proven adapters for SyncOnAddress, the libSceLibcInternalExt redirect/stubs and the new MapFlexibleMemory adapter.
- We use GOT stubs instead of PLT overwrites.
- We copy the whole store set (Ampr, PlayGo, Psml too). The tester shipped only Agc + AgcDriver, since Hades II doesn't import Psml.

---

#### Part 24: Hollow Knight: Silksong (PPSA12544), a Unity FW 4.xx backport (2026-10-09)

Folders: `default` (original v01.000.010, SDK 0x11000040/0x12590001, param 13.00/11.00; works 11.xx+; the dump's fakelib/ has the Sep 6 ampr_emu, PlayGo and the AppContent/GameUpdate/NpEntitlementAccess stubs) and `4xx+`. There's no README and the test status is unknown. Same Unity layout as Sons of Sparta (Part 22): `Media/Modules/{Il2cppUserAssemblies,PS5Util}.prx`, `Media/Plugins/{CommonDialog,PSN,SaveData,Share,lib_burst_generated}.prx`.

##### What the backport does
- **Signing**: every module is an unencrypted PS5-style SELF (not make_fself). SDK 0x04000031/0x09040001 everywhere. param.json gets `requiredSystemSoftwareVersion` 0x09000000…, `sdkVersion` **0** (new). `sce_sys/about/right.sprx` is replaced by a **retail-encrypted** right.sprx from elsewhere (12,752 bytes; nothing can unwrap it).
- **fakelib/**:
  - Agc + AgcDriver = **our `fakelibs/4.03` byte for byte** (4xx kit; the third backport using them, after Hades II and Control);
  - the three dump stubs re-signed;
  - **ampr_emu and PlayGo removed**, so the console's own are used;
  - new **`libSceSaveData.native.sprx`**: Sony build J03718123 (FW 12.00), SDK lowered to 4.00. Our pipeline sees no SaveData import missing at FW 4, so its purpose is unclear (BackPork's 7xx set patches the same library). Recorded, not adopted.
- **eboot**, mostly at the call sites (the PLT stays):
  - IsTrinityMode → `xor eax,eax` ×2;
  - PadIsRemoteController → `mov byte [rsi],0; xor eax,eax` ×2 (= our `out2_byte0`);
  - GetOperationMode PLT → `xor eax,eax; ret`;
  - **`sceUserServiceGetGamePresets` struct size 0x30 → 0x28** (new, see below);
  - plus edits inside the PT_SCE_VERSION records (no effect on loading).
- **SaveData.prx** (Unity plugin): the 16-byte copy of `{blocks, systemBlocks}` into the Mount3 struct becomes an 8-byte copy of `blocks`, so systemBlocks stays 0. That's the systemBlocks fix again (7th game, 2nd time inside SaveData.prx), and our Mount3 adapter covers it.
- **libc**: GT7's recipe:
  - FW 11 libSceLibcInternalExt ×3 → PLT ret0;
  - SyncOnAddress adapters written straight into the PLT (our Wait32 bytes);
  - the MapNamedFlexibleMemoryInternal → MapFlexibleMemory and Wait32 → `_umtx_op` NID renames.

  The **heap flag 0x8000 is left in place** (Part 23: EINVAL on every firmware when libc grows its heap).
- **PS5Util.prx**:
  - the **code segment is extended from 0x3ee to 0x4000** (into the zero gap before the next page; Sons of Sparta's PS5Util has the same 0x4000 size);
  - three import slots are repurposed: `puts` → `clock_gettime`, SyncOnAddressWait → `_umtx_op`, SyncOnAddressWake → `__error`;
  - hand-written Wait (UMTX_OP_WAIT, op 2, ETIMEDOUT handling) and Wake (op 3) are placed in the new space.

##### Gaps found
1. **New rule `-sD02mFDBh4` `sceUserServiceGetGamePresets`**: adapter, `when: always`, `below: 11.00`, DERIVED. It clamps the struct's size field to 0x28 (`cmova`, branch-free). The size check lives in the system service: FW 4.03's library just forwards the call over IPC, and that service isn't in our dumps. So the first firmware that accepts 0x30 is unknown, and 11.00 is the earliest one we know works. The game zeroes the struct first and reads only 0x28 bytes back.
2. **PS5Util has no room for our SyncOnAddress adapters.** Without `--extend-text` its backport ends in ERROR ("no int3 padding left for a 11-byte code piece"). With it, both engines extend the segment to 0x4000 (+0x3c12), exactly where the tester's ends, and both produce identical files. Two independent Unity backports used the same extension. **Changed (same day): extension is now the automatic fallback** in both engines, the CLI and the app. It happens only when a module has no padding left, and the report notes it. Opt out with `--no-extend-text` or the app toggle. The app stores the setting under a new key, `AutoExtendText`, so a saved value from the old off-by-default doesn't carry over. Still not console-confirmed.

Our pipeline otherwise covers everything the tester did (eboot stubs, Mount3 in SaveData.prx, the libc set plus the heap-flag adapter) at FW 4, with the store set.

---

#### Part 25: Horizon Call of the Mountain (PPSA05666), a PSVR2 game, FW 5.xx backport (2026-10-09)

Folders: `default` (v01.004.011, Guerrilla "Azimuth" engine, SDK **0x05000033/0x09590001**, param 5.00/5.00; the user notes it works on 7.xx+) and `5xx+`. The `5xx+` README is only donation links, and the test status is unknown. Modules: eboot (10,465 imports), 33 Wwise/audio plugins in `prx/` (ak*, auro, izotope, mcdsp, masteringsuite, sceaudio3d), and `sce_module/` Face, FaceTracker, JobManager, NpCppWebApi, Pfs, libc.
- **`default` is not a pristine dump.** Its fakelib/ already holds a backport-style set:
  - Agc, AgcDriver and PlayGo = **our `fakelibs/4` (GT7's)**;
  - Psml = the 12.70 build from DL/FC 27/Sons of Sparta;
  - an **undated ampr_emu** (86 imports, no `__DATE__`), plus AppContent and NpEntitlementAccess stubs.
- **What the backport does**:
  - every module make_fself'd at SDK 0x04000031/0x09040001: the game, all plugins, `right.sprx` (1.00 → 4.00), and **even the homebrew stubs (2.00 → 4.00)**;
  - no param.json, so the overlay keeps 5.00 (hence "5xx+");
  - libc and every plugin: SDK change only. A SDK 5 game needs nothing more at FW 4/5, apart from the one import below.
- **eboot: one guarded wrapper** for `uv0Ae+jCeWY` `sceHmd2ReprojectionSetAllowPositionalReprojection` (libSceHmd2ReprojectionPositional, FW 6.50). The PLT entry jumps into a 5-piece chain in int3 padding:
  - `r11 = GOT`;
  - if `r11 >> 32 == 8` and `r11d == 0x32782`: return 0;
  - else `jmp r11`.

  BF6 (Part 16) compared with **0x800032780**. This build uses **0x800032782**.

##### The unresolved-function trap per firmware
Every libkernel has exactly one `int 0x43; ret` after int3 padding: the function the loader binds unresolved imports to (PRX_NOT_RESOLVED_FUNCTION). Offsets (libkernel loads at 0x8_0000_0000):

| FW | trap | FW | trap |
|---|---|---|---|
| 1.00 | 0x2fb60 | 7.01 | 0x350a0 |
| 2.00–2.50 | 0x30c00 | 7.20–7.61 | 0x35100 |
| 3.00–3.20 | 0x31dd0 | 8.00–8.60 | 0x354e0 |
| **4.00–4.51** | **0x32780** | 9.00–9.60, 11.00–11.60 | 0x362e0 |
| 5.02–5.10 | 0x33380 | 10.01–10.60 | 0x35b90 |
| 5.50 | 0x33450 | 12.00–12.20 | 0x369c0 |
| 6.00–6.02 | 0x342c0 | 12.40–12.70 | 0x36a80 |
| 6.50 | 0x34330 | | |

- BF6's 0x800032780 is the FW 4.00–4.51 trap exactly. Horizon's **0x800032782 is the `ret` after it**, and it matches no firmware's trap, at 4.xx or at 5.xx (0x33380). Either the loader binds to trap+2 (not seen anywhere) or the guard never fires and the call reaches the trap. That's a console question for the testers.
- This table is what a generic **`guarded` fix kind** needs: the trap address for the target firmware, so one backport would use the real function where it exists and a fallback elsewhere. It isn't implemented yet. For the firmwares we can't decrypt (4.30, 7.00, 11.20, 13.60), neighbours with the same trap are likely but unconfirmed.

##### PS VR2 detection (implemented 2026-10-09)
- **Signal: imports from `libSceHmd2*` (headset, reprojection) or `libSceVrTracker2`.** Of the 14 sample games only GT7 (optional VR mode: Hmd2 ×17, VrTracker2 ×6) and Horizon (VR-only: Hmd2 ×22, Hmd2ReprojectionPositional ×1, VrTracker2 ×7) import them.
- **Not usable**:
  - param.json `attribute` bit 29 (0x20000000) is also set on BF6, BL4, UFC 6 and DL, so it isn't VR;
  - `attribute3` differs for Horizon (0x001c0c00), but one VR-only sample can't name a "requires PS VR2" bit. The PS5 dev wiki's param.json page is behind a bot check.
  - The firmware lists can't date headset support either: 29 of Horizon's 30 VR imports exist from FW 2.00–4.00, and only the reprojection call is FW 6.50. When the console got headset support isn't visible in the libraries.
- Both engines: `GameResult.psvr2` (per game module and library, distinct NIDs; JSON `psvr2`), plus one WARNING saying that VR-only games can't run below the headset firmware and that optional-VR games usually run without it. App: a **"PS VR2" badge** in the game header after Analyze, whose tooltip lists the libraries; the warning appears in Overview → Warnings. Tests: `tests/test_psvr2.py`, `Psvr2BadgeTests`.
- Open: "supports" vs "requires" needs the param.json attribute bit (more VR samples or the wiki).

##### Rule change
- **`uv0Ae+jCeWY` → ret0, DERIVED** (new). GT7 imports it too, so GT7's FW 4 backport now gets 7 eboot stubs and 29 unresolved imports (30 before); `test_gt7` and `GuiSupportTests` are updated. GT7 (console-tested at FW 4) left it unpatched because it doesn't call it without a headset, while a VR-only game does. Our pipeline at FW 4 now leaves nothing unresolved in Horizon, and both engines give identical files. We also apply the derived Mount3/Prepare and GetSpeakerInfo adapters, which the tester didn't need for a FW 5 target with a 5.00 param.

---

#### Part 26: Kena: Bridge of Spirits (PPSA01746), a re-signed dump and an SDK-only FW 4.xx backport (2026-10-09)

Folders: `default` (v02.008.000, SDK 0x05000033/0x09590001, param 6.02/5.00; the user notes it works 5.xx–10.xx) and `4xx+`. Wwise plus the same audio plugin set as Horizon (`prx/ak*`, auro, izotope, mcdsp, masteringsuite, sceaudio3d), and `sce_module/` Face, FaceTracker, JobManager, NpCppWebApi, Pfs, libc. There's no README and the test status is unknown. The backport renames the title to "Kena: Bridge of Spirits By BADERLINK".

##### `default` is a re-signed decrypted dump
- Every module has a **`.esbak`** next to it: the decrypted original ELF, with section headers. The PS5-style SELFs in `default` were rebuilt from them by an unknown tool, and the code is identical (`test_kena`). That tool:
  - **zeroed** the digest bytes after the program-header table;
  - **zeroed** the PT_SCE_VERSION block (plugins, eboot), or **cut it off**: the `sce_module` SELFs end at their last segment, and libc/libSceFace are 8 or 4 bytes short of their declared size;
  - **damaged `sce_sys/about/right.sprx`**: its last segment runs 0x40 bytes past the end of the file. Both engines refuse it, which is correct, and the backport ships a working one.
- **Sony system modules' PT_SCE_VERSION is a build-path string**, not records: e.g. libc's 0x1a bytes = `Prospero_Release/libc.prx\0`.

##### Unwrapper generalised (both engines)
The trailing PT_SCE_VERSION block is now found in this order:
1. `name:` records at the end of the file, or within 0xF of the declared size (Yōtei −8, BL4 appended data);
2. otherwise the bytes after the last SELF segment, as they are (path string, Unity's opaque PS5Util block, zeros);
3. if nothing follows the last segment, zeros. The loader doesn't check this segment (Hades II README).

A declared size larger than the file is tolerated, as long as every entry lies inside the file. The "malformed/truncated trailing PT_SCE_VERSION data" errors are gone, and Part 22's special case is folded into rule 2. All 75 of Kena's readable modules unwrap identically in both engines.

##### Wwise plugins without SDK fields
21 of the `prx/ak*` plugins (decrypted originals included) have an **all-zero PT_SCE_MODULE_PARAM**: no magic, no SDK. Our pipeline errored on them ("param segment magic 0x0"). Now `sdk.read_sdk`/`Sdk.Read` return (0, 0) and `write_sdk` changes nothing, so they're analysed and kept, reported as "SDK 0x00000000/0x00000000 (kept)". The tester shipped them unchanged too.

##### What the backport does
- **SDK fields only**: eboot, libc and the SDK-carrying plugins go 5.00 → 0x04000031/0x09040001. No code patch anywhere, no fakelib/. param.json: `sdkVersion` 4.00, `requiredSystemSoftwareVersion` **left at 6.02**.
- `sce_module` SELFs re-signed with their version strings restored.
- An SDK 5 game needs nothing else at FW 4 by the tester's account. Our pipeline additionally applies the derived Mount3/Prepare and GetGamePresets adapters, plus the store's FW 4 fakelib set (the dump has no fakelib/). Both engines give identical files and reports.

---

#### Part 27: Review of every sample folder (2026-10-09)

A sweep over all 15 samples with the current engines. Scripts are in the session scratchpad, not the repo.

##### 1. Unwrap: every module in every folder (568 files), both engines
- **564 unwrap**, and C# matches Python byte for byte on all 536 SELFs (the other 32 are plain ELFs).
- The 4 failures are all non-code: GT7 `default/fakelib/libSceAmpr.sprx` (0 bytes), Kena's damaged `right.sprx` (Part 26), Silksong's retail-encrypted backport `right.sprx`, and **UFC 6's tested `right.sprx`, which is 12,512 zero bytes**.
- 42 modules have no SDK fields: Kena's Wwise plugins, `default` and `4xx+`.

##### 2. Our pipeline on every original at the tester's firmware (20 runs)
- No crash. The only module error is Kena's damaged `right.sprx`.
- Unresolved imports that remain, all with evidence:
  - GT7: `sceHmd2ReprojectionSetParamWithBuffer` (FW 5.02, PSVR2; GT7 left it unpatched);
  - BF6/FC 27: libSceNpPremiumAddcont ×2 (FW 9.00; both backports left them);
  - Beast/FC 27: `sceCoredumpWriteUserString` (FW 8.00; both left it);
  - DL: the 112 EOS imports (left by the tested backport).
- **Closed: `gWT7X8H0bYs` (libSceVideoOutVrrStatus, FW 10.01), open since Part 14.** FW 10.01's libSceVideoOut shows it's a VRR status getter `(handle, status*)`. It writes exactly 0x50 bytes, or returns an error without writing. Yōtei ignores the return value and reads the struct (sometimes from uninitialised stack), so a plain ret0 is wrong. New stub **`out2_zero50`** (zero 0x50 bytes at rsi if not null, return 0): an all-zero status takes the game's "VRR off" path. Rule DERIVED. Yōtei now backports to FW 4 with nothing unresolved, identically in both engines.

##### 3. Imports the testers touched vs our rules (14 original/backport pairs)
Every import a tester redirected, stubbed or renamed is covered by a rule, recorded as a firmware-specific technique (BF6 memory hooks, Beast `usleep`, BL4 `sceNetPoolCreate`), or a side effect of swapping libc (BL4, AC, Hades II: different libc builds, so the symbol-index comparison is noise there; the JobManager rename). **Nothing new was missed.**

##### 4. Other files the testers add or change
- **`SHA256SUMS`** ships with three backports from different testers (AC, Control, Hades II), so testers can verify a copy.
- **`dlc_emu.ini`** (AC `4xx+`). The homebrew AppContent / NpEntitlementAccess / GameUpdate stubs that many dumps ship are **dlc_emu** (`C:/dev/ps5/dlc_emu`, the ampr_emu author's tree). They fake the entitlement services and read `/app0/dlc_emu.ini` (`[PSAL]` entries: label, download_status, *_visible, active_flag, entitlement_key…) and log to `/app0/dlc_emu.log`. AC's tester listed the game's ULC labels to unlock paid DLC. Recorded as fact; **not a Studio feature** (it unlocks content the user hasn't bought).
- `ampr_emu.index` ships in GT7's and AC's backports (stale-index risk, Part 14 checklist); `k9.psp` as before; Wwise games' `ShaderData.bin` (AC option).
- **Homebrew stubs re-signed at SDK 4.00** (BF6, Horizon): only the SDK field differs from the 2.00 originals. We keep 2.00, which is below every target.
- **ampr_emu inventory**: Aug 24 builds (ok), Sep 6 builds (known broken), and undated builds. GT7's (our `fakelibs/4`, also in BF6's and Control's dumps; 146 imports) and Horizon's (86 imports) carry no `__DATE__`, so `known_builds` can't name them.

##### Follow-ups proposed (not done)
- **Done (same day): an unreadable `sce_sys/about/right.sprx` is a warning, not a module error** (both engines). It's left out of the output, and the dump's own copy stays in use under the overlay. It is **still lowered like any module when its SDK is above the target**: GT7's is SDK 10.00 (`0x10000040`; all other originals are 1.00), and GT7's console-tested FW 4 backport lowered it, so skipping the file outright would undo something a tested backport did. Evidence it isn't needed to start a game: UFC 6 ran on a Pro with an all-zero one, and Hades II, AC and Yōtei ship none. Tests: `test_phase0.test_unreadable_rights_module_is_a_warning`, `RightsModuleTests`.
- Write a **`SHA256SUMS`** next to the backport output (both engines), the convention three testers already use.

---

#### Part 28: Kingdom Come: Deliverance II (PPSA21704), a minimal FW 4.xx backport (2026-10-09)

CryEngine (Warhorse), FMOD audio (`bin/prosperoshared/libfmod*.prx`, plus `pros.sdk.Prospero-9.000.prx` and `Quatmosphere.prx`), `sce_module/` libc and NpCppWebApi. `default` = v01.052.000 (SDK 0x09000040/0x11590001, param `sdkVersion` 9.00 and `requiredSystemSoftwareVersion` **12.20**; the user notes it works on **9.xx and higher**), with the dlc_emu AppContent / GameUpdate / NpEntitlementAccess stubs in `fakelib/`. `4xx+` = the FW 4.xx backport. No README, test status unknown.

##### `4xx+` is an overlay, not a full folder
It has no `sce_sys/param.json` and no dlc_emu stubs: only the changed modules plus its own `fakelib/`. It's meant to be copied over the dump, so the dump's param.json (12.20 / 9.00) and dlc_emu stay in use.

##### What changed (default → 4xx+)
- Every module re-signed as a PS4-style fake SELF (make_fself) with **SDK 0x04000031/0x09040001 (4.00)**. That includes `right.sprx`, whose SDK the tester **raised** from 1.00 to 4.00 (a blanket SDK pass; we keep 1.00, which is below every target).
- **libc**: the MapNamedFlexibleMemory → MapFlexibleMemory NID swap (rule `4h6F1LLbTiw`). libc's SyncOnAddressWait/Wake (`Hc4CaR6JBL0`, `q2y-wDIVWZA`) were **left unpatched**, as in GOWSOS.
- **eboot, 5 patches at call sites**, all `call <PLT>` → `xor eax,eax` + NOPs, except the last:
  - `sceVideoOutVrrPegToFixedRate`, `sceVideoOutVrrUnpegFromFixedRate`: ret0, as our stubs do.
  - `sceKernelIsTrinityMode` ×2: ret0, as our stub does.
  - **`scePsmlMfsrInit` (`3WVD91e12ZQ`)**: the game's one-instruction thunk `jmp scePsmlMfsrInit` became `xor eax,eax; ret`, so PSSR is never initialised and init reports success. **New: no other sample backport touches it.**
- SaveData Mount3/Prepare call sites untouched (our derived argument fixes are applied anyway: `when: always`).
- `fakelib/` = Agc, AgcDriver, Psml **byte-identical to `fakelibs/4.03`** (the FW 12.00 "4xx kit", Sony J03718109). No ampr_emu and no PlayGo stub: KCD2 imports nothing from libSceAmpr. (Our output still gets them from the store set: a dump needs them under ShadowMountPlus, Part 4.)

##### Why KCD2 needed `scePsmlMfsrInit` disabled and other games didn't
`scePsmlMfsrInit` (and libScePsml as a whole) first appears in **FW 9.00** (firmware_db). Eight sample games import it, KCD2 included. Every other one calls it **only behind a PS5 Pro check**:
- Dying Light: `sceKernelIsTrinityMode() != 0` and `sceSysmoduleLoadModule(0x137) == 0`, then init;
- Borderlands 4: a flag plus `LoadModule(0x137)` succeeding;
- Control and GT7: global flags (Control's is a "Pro" bool getter).

With `sceKernelIsTrinityMode` stubbed to 0 those games never reach it on old firmware. **KCD2 calls `sceSysmoduleLoadModule(0x137)` and then `scePsmlMfsrInit` unconditionally**, from a renderer constructor (`0x1c0e24c`), and ignores the result. A second helper (`0x2a24330`, reached only through data) returns `init == 0` as "PSSR available". (0x137 is the libScePsml sysmodule id: every game loads it right before init. Inferred.) So on any console, KCD2 always runs MFSR init. The 9.xx floor the user observed for the original matches the firmware where libScePsml appears. Inferred, not console-verified: the FW 12.00 Psml fakelib's init fails or hangs below FW 9.00, and only KCD2 runs into it.

##### Our pipeline on `default`
At FW 4 (`fakelibs/4`) and FW 4.03 (`fakelibs/4.03`), every import resolves, 0 errors. The VRR, Trinity and MapNamedFlexibleMemory rules cover the tester's other patches; KCD2 was added to their `sources` and to `left_unpatched` for SyncOnAddress. **Gap: `scePsmlMfsrInit` isn't stubbed**, because the Psml fakelib provides it (rules only fire for missing imports, unless a rule says `always`).

##### Implemented: a default `scePsmlMfsrInit` rule below FW 9.00
- `data/fixes.json` `3WVD91e12ZQ`: `plt_stub` `ret0`, `when: always`, `below: 9.00`, DERIVED (source KCD2). It's `always` and `plt_stub` because the Psml fakelib provides the function. It's `below 9.00` because libScePsml first appears in 9.00 and the original runs from 9.xx. Other games only reach the call on a Pro, so the rule doesn't change what they do.
- **Option fix kind `none`** (new, both engines' fix-table builder): an option entry with `kind: "none"` removes the default fix for that NID. **`force_pro`** uses it to keep the real `scePsmlMfsrInit`, since PSSR needs it.
- KCD2 at FW 4 now gets `PLT -> ret0` for it, and at FW 9.60 nothing. C# and Python give byte-identical output folders (13 files). Test: `KnowledgeTests.FixTable_OptionOverridesDefault_AndBelowLimitsTargets` (the Python twin went with ps5bp on 2026-10-10). The GT7, Control, BF6, BL4 and AC `force_pro` parity runs still pass.

---

<a id="anyps5-plan"></a>
## AnyPS5 improvement plan and reference priorities (11–12 October 2026)

Condensed on 14 October 2026. The plan reviewed `.original/reference/AnyPS5` at commit `9a1f6f44ac75ac42aea6fa0d60f99fad1cde7bc4` and allowed only fixes for defects reproduced by a failing test first, while keeping accepted cook output unchanged. Each phase ended with a pass/fail gate; a failed gate stopped work until explained. SCE dynamic tables, provider/reachability, synchronization adapters, AMPR changes and new automatic stubs needed an affected Backport input before they got priority. Reference code alone is not evidence of a Backport improvement. The per-run XML, capture harnesses and `tmp/` verdict files cited by the original entries were deleted with `tmp/` on 12 October.

### Phases 1–4: string-table bounds and candidate scope

**Phase 1: baseline and defect evidence.** Six characterization tests reproduced two defects. The ELF parser accepted a symbol-name offset at the declared `DT_STRSZ` end and a NUL just outside it. Candidate generation broadened a function link label, or an explicitly named missing function, to every constant-return function in the linked file. The cook baseline failed for three GT7 configurations (`notbackport-4`, `notbackport-403-all-options`, `notbackport-4-no-store`) on report hashes only. All 33 payload hashes matched, and Debug and Release were identical. The cause was three NIDs that the newly committed `data/aerolib.csv` now names: `kP2L8t3j-aM` `sceVideoOutAddVrrStatusFlagsPrivilege`, `LibwuIonIBw` `sceVideoOutAddVrrActiveStatusEvent` and `-KRzWekV120` `sceAgcDcbSetIndexSize_0600`. Reversing just those labels reproduced every original golden exactly. With the user's approval, the three goldens were edited by hand (six report hashes, seven labels; payload hashes untouched), and all 7 parity tests passed.

**Phase 2: string tables bounded by `DT_STRSZ`.** `SceElf` now validates the declared table against the file, rejects name offsets at or past its end, and searches for NUL only inside it. This applies to symbol names, module names, build paths and import-library names. Inputs without `DT_STRSZ` keep the file-bounded fallback. 16 desired-behaviour tests replaced the characterizations: 10 failed before the fix and all passed after. A before/after run over 667 retained ELF/SELF files gave identical parsed strings and the same nine pre-existing rejections.

**Phase 3: candidate scope.** `DataGenerators.ScanCandidates` treats an explicit function link label (one name or a comma list, backticks optional) as the selection boundary. A named function missing from the linked file now yields no candidates instead of all of them. A library-directory or descriptive label keeps library-wide selection, still narrowed by matching backtick mentions. 11 tests (4 failing before). On the pinned AnyPS5 checkout the capture went from 41 to 38 entries. The fallback no longer adds `sceSystemServiceDisableNoticeScreenSkipFlagAutoSet`, `GetAppIdOfRunningBigApp`, `SetNoticeScreenSkipFlag` or `HideSplashScreen`, and `sceAudioInInit` (`SxQprgjttKE`), previously lost, is now added. `data/candidates.json` was not regenerated (26 entries), and no stub was added: an emulator's constant return is not evidence that stubbing a console API is safe.

**Phase 4: integration and fixture recovery.** A pipeline test checks that a NUL outside `DT_STRSZ` aborts the cook with `InvalidDataException`, preserving the source and previous output and leaving no stage or journal, and that a corrected input then cooks to the same destination. Recreating the deleted `work/*_elf` fixtures from `.original/backports` (9 NOT_BACKPORT from GT7 `decrypted`, 13 GT7 from `4xx+/USA`, 11 CONTROL from PPSA34547 `4xx+`) first failed five goldens. The cause: **the historical fixtures were made by `make_fself_batch.py`, which zeroes the section-header fields after unpacking** (`e_shoff` at `0x28`, 8 bytes; `e_shentsize`/`e_shnum`/`e_shstrndx` at `0x3A`/`0x3C`/`0x3E`, 2 bytes each). `Self.Unwrap` preserves them. Applying that 14-byte normalisation reproduced every historical hash, including the AMPR fixture `c681b42e…`. The remaining two report goldens differed only by the same three catalog names and the resulting sort order, and were migrated the same way. The full All lane then passed 481/481 in Debug and Release. `Self.Unwrap` itself was not changed.

**Gate principles kept for future work:** write the failing test first and keep valid controls green; never re-record goldens to make a gate pass (migrate by hand only after proving the whole difference); re-run the parser budget/mutation checks and the cook parity tests after any parser change; and remember that lifecycle/concurrency tests cover only their exercised interleavings.

### Reference priorities 1–8

| # | Topic | Verdict | Notes |
| --- | --- | --- | --- |
| 1 | Dry-run option coverage | Fixed (test only) | `GuiSupportTests` expected 21 fixes; the default is correctly 18, and 21 with `save_mount_compat`, `save_prepare_compat` and `audio_speaker_compat`. The theory now asserts each adapter NID's presence or absence. |
| 2 | Provider audit | Implemented, CLI only | `ps5backport report MODULE --firmware F --provider P … --provider-audit` lists bundled export candidates per declared library and NID (`same-library` / `cross-library` / `unknown-library`), excluding the importing module. Diagnostic only: cooking and classification don't use it. |
| 3 | SCE-relative dynamic tables | Implemented | String, symbol, RELA and JMPREL tables are read relative to the file-backed `PT_SCE_DYNLIBDATA` segment and may lie outside every `PT_LOAD`. Requires one unambiguous segment and matching sizes, and rejects out-of-segment extents and unsupported entry formats. Renames and symbol definitions use the same resolved locations. Standard tables keep precedence. Validated on synthetic binaries; no real-game benefit shown yet. |
| 4 | PlayGo 0.6 | Optional, experimental | Options `playgo_0_6` and `playgo_0_6_log` (mutually exclusive; the log goes to `/app0/playlgo.log`) use the official variants in store sets 4, 4.03, 7, 9.60, 10 and 11. Set 4's default equals official 0.4 and 9.60's equals official 0.5; 4.03/7 match neither. Defaults are unchanged pending console evidence. |
| 5 | PlayGo config generation | Deferred | Only two metadata files exist (35 chunks, one scenario). The stub already bounds enumeration at 1,000 chunks. Note the config syntax: `9,` means one ID while `9` means a count. Copying scenario count `1` turns scenario-ID-1 requests into errors. |
| 6 | 13 new AnyPS5 candidates | Not promoted | 12 exist in every firmware since 1.00 and their constant returns rely on host assumptions (offline, no USB, no media player); the VRR one already has a `ret0` fix. Dry runs with the 26- and 39-entry catalogs gave identical reports. |
| 7 | Deployment guidance in reports | Implemented | The text/JSON report has a deployment section: Full-mode requirements, libkernel excluded even in Full, fakelib2/source preference, package filtering, PPR cleanup, added app0 data and diagnostic log paths. Rules are pinned to ShadowMountPlus 1.7beta5-fix1. |
| 8 | Crash-log diagnostics | Implemented | `ps5backport crash-log LOG [--report backport_report.json] [--json]` reads full fatal dumps, numbered excerpts and ShadowMount summaries, and prints fatal events, runtime addresses, module mappings and text-relative offsets (half-open, relative to the logged xotext start). Cook-report association is always marked unverified. Limits: 1 MiB per log, 4,096-character lines, 64 events, 128 addresses/event, 256 modules/event, 256 ranges/module; reports up to 8 MiB / 4,096 modules. |

Every implemented priority was checked by before/after GT7 cooks with identical payload hashes (priority 7 changed only report text and hashes) and a full Debug and Release suite: 506 → 515 → 549 → 576 → 596 → 631 tests. During priority 2 the headless `busy-cancel` test failed once in Release (a timeout, then `writer.lock` still in use at cleanup); later full runs passed. None of these establishes console compatibility.

<a id="later-changes"></a>
## Later engine changes (13–14 October 2026)

### Full source audit (2026-10-13)

Every C# file in Core, CLI and App was reviewed (parsers of untrusted SELF/ELF/BPS/JSON/log input, patching, the pipeline, publication transactions, view-model threading and command error handling). Only defects that were reproduced by a test failing first were changed:

| # | Defect | Fix | Test |
| --- | --- | --- | --- |
| 1 | `boot_notice` cave called libkernel with rsp = 8 mod 16. `_start` is entered like a function (rsp = 8 mod 16, as the game's own `_start` shows: five pushes before its calls), and the cave's 11 pushes plus `sub rsp, 0xC68` left every call misaligned (SysV ABI violation). | `StackAlloc` 0xC68 → 0xC60 | `BootNoticeTests.Cave_KeepsStackAlignedAtCalls` |
| 2 | `--boot-notice-delay` above 4294 s wrapped the 32-bit usleep microsecond count, and values above `int.MaxValue` became negative (no delay). The 256-byte message cut could split a UTF-8 character. | Range check (CLI error / `ArgumentOutOfRangeException`); cut at a character boundary | `Delay_BeyondUsleepRange_IsRejected`, `LongMessage_IsCutAtACharacterBoundary` |
| 3 | `RenameImport` refused a shared string only when another name started inside the import's name; an import name that is the tail of a longer name (suffix merging the other way round) was rewritten in place, corrupting the other symbol. | Range-overlap test in both directions | `ImportLibraryRenameTests.NameThatIsTheTailOfAnotherName_IsRefusedWithoutChangingBytes` |
| 4 | `SceElf.TextSegment()` threw `InvalidOperationException` for a module without an executable PT_LOAD, which is not a module error, so the whole run (or the CLI) aborted. | `InvalidDataException` | `ModuleWithoutCode_IsADataError` |
| 5 | Editing the boot-notice message after Analyze did not mark the analysis stale, although the message is part of the cook fingerprint; Cook then failed with "Analysis is stale" with no warning shown. | `BootNoticeMessage` added to the stale-marking properties | `GuiPresentationTests.RelevantChanges_MarkDisplayedResultsStale("notice")` |
| 6 | Store page "Choose BPS patch": an unreadable file (`UnauthorizedAccessException`) escaped the async command, which ends the app. | Caught and shown in the BPS panel | `StorePageTests.ChooseBpsPatch_UnreadableFile_IsReportedNotThrown` |
| 7 | Store page Add / Apply BPS: a corrupt `manifest.json` (`JsonException`) escaped the command and ended the app. | Caught and logged | `StorePageTests.CorruptManifest_AddAndApplyBps_LogErrors` |

Full suite 686/686, 0 skipped, 0 warnings. Observed but not changed (not proven to occur, or a design decision): the Store page verifies (SHA-256) every set on the UI thread when it opens; Add and Apply BPS also run on the UI thread; `FilePathCase` repeats the "ambiguous file path casing" warning once per occurrence of the string; a symbol named like `NID#x#y` with invalid library characters throws `FormatException` (not a module error); `PyJson.WriteFile` writes `firmware_db` lists non-atomically; the Firmware DB page's Import writes into the folder it reads (the bundled copy when running from `bin`), unlike the CLI's `fwdb import-*`.

### Load-segment size check (2026-10-14)

`SceElf` now rejects a `PT_LOAD` whose file size exceeds its memory size (`InvalidDataException`). The loader maps only `p_memsz` bytes, so `Off()` would otherwise translate addresses past the loaded image to file bytes the console never maps. None of 112,384 `PT_LOAD` segments in 22,481 local modules has that layout (FINDINGS Part 1 §5, 14 October afternoon). Tests: `AuditTests.Elf_RejectsLoadSegmentWithFileSizeLargerThanMemorySize` and `Elf_AcceptsLoadSegmentWithMemorySizeLargerThanFileSize`.

### Optional patches panel: grouped grid (2026-10-13)

The Overview's optional patches were a fixed-height (196 px) list beside a details panel, which had to be scrolled once there were 13 options. They are now a wrapping grid of area cards. Each option in `data/fixes.json` has a `group`, and the top-level `option_groups` sets the card order: Saves, Audio, PS5 Pro, Libraries, Engine, Diagnostics. Groups that are not declared come after these in option order, and an option without a group goes to "Other". A new option therefore lands in its card with no XAML change, and a new area only needs a `group` value. Clicking a name shows its description and warning in the details strip below the grid; the boot-notice message box appears only while `boot_notice` is enabled. The search box filters across all cards. The test is `OptionalPatchTests.PatchGroups_ShowEveryOptionOnce_InDataOrder_AndFilter`. The snapshot mode gained `--expand` (opens every expander) and `--height PX` for reviewing panels below the fold. Full suite 687/687.

### Audio 7.1 bed option (2026-10-14)

`audio_bed_71` (Audio 7.1 bed (engine), group Audio) is the stateless alternative to `audio_port_compat`: instead of folding the engine's 7.1.4 beds to 8 channels every grain, it makes Scream/NCA render 7.1 natively, the approach already used for Wwise and Frostbite. Two code patterns (`snd_SynthInit`'s permitted output modes `0x3fff → 0x3fef`; the start-up probe's port format `0xC00 → 0x800`, both unique in Ghost of Yōtei's eboot) plus one post-call adapter on `sceAudioOut2PortGetState` (`num_channels` capped at 8 below FW 11, so a console reporting 12 channels does not push the engine into 7.1.4 mode). No caves beyond the 31-byte cap, no runtime state, no page-tail requirement. `Knowledge.FixTable` refuses the two audio bed options together. Tests: `OptionalAudioTests.Cook_AppliesSevenOneBedOnlyWithOption` (3 cases), `SevenOneBed_PatternsMatchYoteiOnce` (private eboot), `FixTable_RejectsBothAudioBedOptions`. Static basis and the tester comparison plan: FINDINGS §6j.

### Requirements probe (2026-10-14)

Analyze and Cook now give every optional patch a verdict from the game's own modules before anything is patched: `needed` (static evidence: the Unity unset-SKU layout is recognised, or `scePadSetVibrationMode` is called with a constant mode above 3), `applicable` (the game imports the option's functions or its byte patterns match; necessity is a console question), `not-applicable` (nothing to change) or `console-choice` (auto/force Pro, PlayGo, psml_kpn9, boot notice). The rule per option is data: `data/fixes.json` option `probe` (`{"kind": "console"}`, `{"kind": "detector", "recognized": "needed|applicable"}`, `{"kind": "argument", "nid", "argument", "above"}`; absent = by imports and patterns), validated by `Knowledge`. Results: `GameResult.Recommendations`, report lines `probe <verdict> <key> <evidence>`, JSON `recommendations`; `Pipeline.Probe(source, knowledge)` runs the probe alone. Engine pieces: `UnitySkuFallback.Recognize`/`UbisoftOfflinePopups.Recognize` (no bytes changed), `Imports.ArgumentConstants` (constant in an argument register before each call site; byte-pattern evidence), `FixApplier.CountPattern`/`CountMatches`. Tests: `ProbeTests` (4), recognise tests in both detector classes; the five cook goldens gained 15 `probe` lines each. **GUI (same day, after the mock-up review in `work/mockups/requirements-probe.html`):** Analyze runs `Pipeline.Probe` first, then the analysis, so a pre-ticked `needed` option is part of the result and the receipt is not stale (the pre-tick is excluded from stale marking; a setting changed by the user during the run still marks it stale). Each option row shows a verdict chip (needed = ok tone, applicable = warning tone, not applicable and console choice = muted; not-applicable rows at 55 % opacity but still selectable), the details strip gets a "Probe" line with the chip and the evidence, the expander header appends `probe: n needed, …`, and the log records the summary and every pre-selection. Cancel and closing the window stop the probe as well as the operation, and progress reports keep the caller's synchronization context captured before the probe's first await (lifecycle tests unchanged). Verdicts and ticks are cleared when another game is opened. Only `needed` is ever pre-ticked; nothing is ever unticked. Tests: `OptionalPatchTests.Analyze_PreselectsNeededOptions_FromTheProbe` (synthetic game, pad mode 4) and the extended `EnabledOption_ReachesTheRun_AndResetsForTheNextGame` (AC: verdicts set, nothing pre-selected, cleared on reload). Classification and limits: FINDINGS §6n.

### Rule-data safety nets: unknown keys and comment round trip (2026-10-14)

Two checks on the hand-written rule data. **Unknown keys are now errors.** `Knowledge` validates every record of the seven `data/*.json` files against an allow-list (root keys per file; fix, option, pattern, stub, candidate, introduced, build and evidence records) and reports `<file>.json: unknown key 'belo' at $.fixes.<NID> (allowed: …)`. Before, a misspelt `below` made a rule unconditional, a misspelt `proven` marked it proven and a misspelt `contiguous` let a branching adapter be split across caves (the Phase 8 schema finding). Keys starting with `_` are comments and allowed everywhere. Tests: `PreflightTests.UnknownKey_FailsAtLoadWithPathAndAllowedKeys` (11 record kinds) and `CommentKeys_StartingWithUnderscore_AreAllowedEverywhere`. **Comment round trip.** `verification/DecoderEvaluation` (`scripts/evaluate-decoder.ps1`) now decodes every adapter instruction and stub body with Iced and compares mnemonic and operands with the `; asm` comment structurally (registers, memory operands, immediates under 8/16/32/64-bit masks, branch targets relative to the block start, `{state+N}` placeholders; size words, `ptr`, notes after two spaces or " (" and label operands are not compared). The first run passed with no comment corrections needed: 291 instructions, the 260 adapter instruction blocks (263 entries minus the three `call_got` tokens) and the 31 instructions of the 9 stub bodies; the only adjustment was to the checker itself, for the implicit operands of `rep stosb`. It stays in the explicit evaluation tool because Core and Tests take no external packages; run it after editing `insns` or `stubs.json`.

### Pad vibration mode compatibility option (2026-10-14)

`pad_vibration_compat` (Pad vibration mode compatibility, new group **Input**) is an opt-in adapter on `scePadSetVibrationMode` that clamps a mode above 3 to 3 for targets below FW 8.00. libScePad validates the mode against the process's compiled SDK (1–3 from SDK 3.00, 1–5 from SDK 8.00, and only the FW 8.00+ libraries know 4 and 5), so a 4.00 cook of a game using mode 4 or 5 gets `0x80920001` on every console; UFC 6 passes a constant 4 and discards the result. Not console-tested and not a default: FINDINGS §6m has the library readings, the 14-game call-site survey and the reasoning. Test: `OptionalInputTests` (4 cases).

### Report portability: native-from firmware and missing-later imports (2026-10-14)

A cook targets the lowest firmware but runs on every newer console, so the report now says what changes there. Each `stubbed` line (a GOT pre-fill) ends with `GOT pre-fill, native from FW X / <source>` when a later `firmware_db` list or an `introduced.json` fact exports the function: from that console firmware on, the loader binds the native function over the stub (FINDINGS §6b, §6c). `StubbedEntry.Introduced` carries it, the JSON report has `stubbed_now[].introduced`, and the GUI import inspector shows it under "Introduced" for stubbed rows. Separately, every import that the target list exports (`reference` or `patched`) is checked against every later list: a NID a later firmware no longer exports (13.60 dropped 2,049, FINDINGS §6g) is reported as `missing-later NID name [library] exported on FW T, not on FW X, Y`, with a top-level NOTE, `ModuleResult.LaterMissing`, JSON `modules[].later_missing[]` and a GUI warning per module. Unresolved imports keep their existing `introduced:` hint. No sample produces a missing-later line. Tests: `PortabilityReportTests` (4 cases: native-from present/absent, missing-later present/absent, text and JSON). Goldens: the five cook goldens gained the native-from suffix on their stubbed lines (and new report hashes); `patch/all-known-fixes` is unchanged by this.

### Save mount compatibility is a default rule (2026-10-14)

`ZP4e7rlzOUk` (`sceSaveDataMount3` `systemBlocks` −1 → 0) moved from the opt-in option `save_mount_compat` to the default fixes in `data/fixes.json`, unchanged otherwise (`when: always`, `below: 5.00`). It is `proven` (user decision): the PPSA32935 FW 4 cook with this adapter saves and reloads on FW 13.60 (FINDINGS §1.1). Reason (FINDINGS §6d, §6g, Part 1 §8): libSceSaveData.native chooses the create-mount validator by the process's compiled SDK, and SDK < 5.00 requires `systemBlocks = 0` on every firmware 1.00–13.60, so a cook stamped below 5.00 needs the rewrite on every console. Targets ≥ 5.00 keep the game's −1 (`below` is exact). The option key no longer exists: `--option save_mount_compat` is an unknown-option error, and the Saves card keeps only Save preparation compatibility. Known side effect (FINDINGS §6d): with `systemBlocks = 0`, native `sceSaveDataCancel` fails on 6.00+ consoles; the `ret0` stub covers only consoles below 6.00. Tests: `OptionalSaveDataTests.Mount3Adapter_IsDefaultBelowFirmware5` (4, 4.51 apply; 5.02 does not), `Gui_PrepareToggleStartsDisabled_AndMountIsNoLongerAnOption`; `GuiSupportTests.DryRun_…` default count 18 → 19; `DerivedFixes_AreFlagged` does not list it and `KnowledgeTests` asserts `Proven`. Six goldens were re-recorded (`patch/all-known-fixes`, `backport/notbackport-4`, `notbackport-4-no-store`, `notbackport-403-all-options`, `control-403`, `ac-450-options`); the reviewed diff is one new `fixed ZP4e7rlzOUk … via cave … proven` line per eboot, `patched` +1 / `reference` −1, shifted stub cave addresses, and the eboot/report hashes. libc, fakelibs and param.json hashes are unchanged.

### Hide PSN error dialogs option (2026-10-14)

`np_error_dialogs` (Hide PSN error dialogs, group **Online**, a new optional-patches card) answers the system error dialog for online errors at the import boundary instead of changing any game logic. Ghost of Yotei's "Something went wrong. NP-104602-3" at first boot is the game's own `sceErrorDialogOpen` call with `0x8055A011`, the code ShellCore hands every pending NpManager request when a PSN sign-in fails; the game queues every failed NP result for that dialog, so any console that cannot reach PSN shows it (FINDINGS §6a). Four adapters share a one-byte state block (`np_error_dialog`, 16 bytes in the last data page): `sceErrorDialogOpen` returns `SCE_OK` without opening when the error code's facility is NP (`0x8055xxxx`) or libSceNet (`0x8041xxxx`) and sets the mark, otherwise clears it and runs the real function; `sceErrorDialogUpdateStatus` and `sceErrorDialogGetStatus` report `FINISHED` (3) once and clear the mark, otherwise run the real function; `sceErrorDialogClose` clears the mark. No firmware gate (`when: always`, no `below`), opt-in, not console-tested. A game's own message box and a game that blocks until PSN answers are out of scope. Tests: `OptionalOnlineTests` (adapters only with the option, on FW 4 and 11; placement, state displacement and tail jump; every short branch lands on an instruction boundary).

<a id="engineering-milestones"></a>
## Engineering milestones

The completed engineering work supersedes the earlier research proposals. These are historical acceptance records, not tests newly executed for this consolidation.

| Phase | Delivered outcome | Historical recorded acceptance |
| --- | --- | --- |
| 1 | Contextual rule parsing, duplicate/null rejection, semantic checks, safe pattern paths and write-free preflight | 260 Debug tests; clean Debug/Release builds |
| 2 | Staged publication, physical overlap checks, input evidence, recovery and retention | 293 Debug tests; clean Debug/Release builds |
| 3 | Pinned/locked verification, generated parser properties, bounded mutation corpus, provider ownership correction | 315 tests per Debug/Release; zero failures/skips |
| 4 | Run snapshots, one-shot lifecycle, stale-analysis receipts, cancellation and closing ownership | 342 tests per Debug/Release; zero failures/skips |
| 5 | One firmware parse per run; signing without redundant segment copies; measured memory/throughput tradeoffs | 345 tests per Debug/Release; matching paired payload/report hashes |
| 6 | Bounded local diagnostics, filtering improvements and headless/native UI checks | 357 tests per Debug/Release; native/DPI limits retained above |
| 7 | Optional Iced evaluation and emitter validation; production scanner retained | 530 specified reference cases, 8 stubs, 45 adapter blocks; 357 tests per configuration |
| 8 | Optional parser/catalog/schema comparisons; CLI/JSON migration deferred | 18 parser observations, 52 schema records plus edge cases; 357 tests per configuration |
| Later fixes | Runtime asset copying, cross-library import correction, general path-case correction, opt-in Unity SKU and Ubisoft severity recognition | Successive historical counts grew to 428 tests; see the work consolidation for original XML summaries |

Pending designs are not silently enabled: module caches need mutation invalidation; two-pass loading/pooling/concurrency need a budget and semantic equivalence; mandatory schemas need a versioned authoring contract; low-firmware/ABI checks and longer console gameplay need actual runtime evidence. Earlier broad proposals, delivery estimates and superseded checklists were discarded.

<a id="document-provenance"></a>
## Former document index and provenance

Original root source identities, computed before removal. Former names in comments/research can be resolved here. Planning-only documents were condensed into project decisions/milestones; CONSOLE_BENCH version/setup prescriptions were replaced by the dated tester checklist and reference limits.

| Former file | Retained section | Original bytes | Original SHA-256 |
| --- | --- | ---: | --- |
| APP_PLAN.md | [Section](#project-decisions) | 27912 | f590986fd79371b85d9320ee07df6bbe087d06e9d9c62c96926ef7c53d03880a |
| AUDIT.md | [Section](#historical-audit) | 12477 | e027da98df21917576bd562b1ecb0532c79d8e09742aaf4d17c7923efda87e6d |
| CONSOLE_BENCH.md | [Section](#tester-checklist) | 6235 | 9c6ad428f394fc077202b7b05ecd4d405e5b7ca1452ee29b942c499c3e82b661 |
| DEPENDENCY_REVIEW.md | [Section](#dependencies) | 11635 | 8cf0969a60318f3e56c692493b8d6e3160c736b10f94a9985ecb81f177827d33 |
| DISTRIBUTION.md | [Section](#build-and-sharing) | 3329 | 37b8c24d9f353e1adf93aa33453d52ea16581f800c759cd43bbc9cc418b4675c |
| ENGINEERING_IMPLEMENTATION_PLAN.md | [Section](#engineering-milestones) | 30921 | f0d5963e06a2b8e8a5780cf4867c11518c569e89ee4232b9296d58bde6542e28 |
| ENGINEERING_RESEARCH_REPORT.md | [Section](#engineering-milestones) | 21890 | 4eeb9662412f450b0d438148686556d1d8e79ac90c76ea68d08ee1272b3050a2 |
| INSTRUCTION_DECODER_REPORT.md | [Section](#instruction-decoder) | 12459 | 5f6fa4f9160ac0686bca334829e6fc5177e1aecf0fda4a717ad71c10573495f3 |
| LOCAL_DIAGNOSTICS.md | [Section](#diagnostics-and-ui) | 10025 | 1546f1fff9c5173fa050b4443a33bfd799fa573d9d9dfc641465b50979f863ae |
| OPERATION_LIFECYCLE.md | [Section](#operation-lifecycle) | 7746 | 09c1a27ed34a3bfecf4bba6733f489713f51f8cfe04c62f5bd2708df0e2d5f10 |
| PERFORMANCE_REPORT.md | [Section](#performance) | 16503 | 5a89fe849d696381ee81aca6ed9a61df5ee313c3a7dcbedff6603846f422091c |
| PHASE8_DECISION_REPORT.md | [Section](#cli-catalog-schema-decisions) | 14236 | b0b23c4f8549eef8a7bf34f65f3500e64b14bafbb96fb1da399178a93c30ff49 |
| PLAN.md | [Section](#project-decisions) | 14449 | 6f652327b8ef7fa8972ab75dc5542f8fa478e1baabc53aaf6bd80f5c2e66a054 |
| PPSA03671_ANALYSIS.md | [Section](#ppsa03671-and-crow-country) | 8823 | f569c9d9420cf3ad10c91c3b49487b19c4605e08ef4a6b1b47452ef9c5d93a1b |
| PPSA34008_ANALYSIS.md | [Section](#ppsa34008) | 8549 | 3bde6794a0dac28b53986c71d30b0e6916ad565d4bf96210fa6a6c7d690f3e06 |
| PUBLICATION_RECOVERY.md | [Section](#publication-and-recovery) | 10161 | 8462e805641a34d0a6feccde7278102551360e96d57a908afe26099627bfc5d2 |
| REFERENCE_OVERVIEW.md | [Section](#reference-research) | 176446 | 525fb9eac57546f92037dfa9359aac3471f8816f5cf84786797d07c5eab57ec3 |
| TESTER_CHECKLIST.md | [Section](#tester-checklist) | 4121 | 07dc516f9f630a81bc36e3a7212d323c6510f75dc29efcc76205510fc96583f1 |
| UBISOFT_OFFLINE_POPUPS.md | [Section](#ubisoft-offline-popups) | 3284 | bc9ae88af4ccbdf5e64ec6c86f329d5f56c18858a5a472730c02061404614974 |
| UNITY_SKU_OFFLINE.md | [Section](#unity-unset-sku) | 3074 | fa5d1c45802e3fa54b797e82b7e80f62139d3c008e01721939c793122b3e7503 |
| VERIFICATION.md | [Section](#verification) | 21626 | d4ae7a7cb8da51739aad7141e9f3d9b549d91ea385495f390dd7d38d8bafa8b1 |

Documentation cleanup status: completed. All 21 former root Markdown files were removed after source-hash verification; README.md is the only root Markdown file. build/DISTRIBUTION.md retains the exact shipped instructions and RuntimeAssets.targets now copies from that path. Debug and Release solution builds passed with zero warnings/errors; all three Debug DistributionTests passed with zero failures/skips. Internal README links and Debug packaged instruction hashes were checked; git diff --check passed. Private tests were not run because their work fixtures were intentionally deleted in the preceding cleanup.
