# Local verification — Phase 3

Implemented and verified on 10 October 2026. Install **.NET SDK 10.0.401** and PowerShell 7. `global.json` now selects that exact SDK; updating it is an intentional review step. The observed test runtime was Microsoft.NETCore.App 10.0.12 on Windows x64/NTFS. Runtime servicing patches remain allowed; each run records the actual SDK/runtime/OS in `dotnet-info.txt`.

## Required commands

From the repository root:

```powershell
# Required lane: tracked data and synthetic/public tests; no private fixtures needed.
pwsh -NoProfile -File scripts/verify.ps1

# Complete local verification, including private golden/integration fixtures.
pwsh -NoProfile -File scripts/verify.ps1 -Lane All

# Just the private integration lane, when maintaining the fixture corpus.
pwsh -NoProfile -File scripts/verify.ps1 -Lane Private

# Optional faster iteration; final verification normally builds both configurations.
pwsh -NoProfile -File scripts/verify.ps1 -Configuration Debug
```

The entry point uses locked restore, builds Debug and Release, runs the selected lane, treats skipped tests/warnings as failures, checks that the result contains tests, and records xUnit XML plus environment information under Git-ignored `work/verification/<timestamp>-<id>/`. It refuses `PS5BP_RECORD=1`; verification never regenerates goldens. It enables the documented .NET CLI, Microsoft Testing Platform, and Avalonia build-telemetry opt-outs for its child processes and restores the caller's environment afterward. Native/filesystem regression tests require normal local temporary-file permissions; a restricted sandbox can cause access errors.

Public/private separation uses `[Trait("Lane", "Private")]` on fixture-dependent classes or individual tests. Public is everything else. Some mixed classes have private class fixtures or shared fixture helpers, so the whole class is private. New tests must follow the same boundary. Tracked `data/`, `firmware_db/`, and text goldens are mandatory: missing tracked data fails rather than silently skipping.

Private verification requires the local, uncommitted `.original/`, `work/` ELF fixtures and `fakelibs/` corpus. The script fails setup if the main private roots are missing, then sets `PS5BP_REQUIRE_PRIVATE=1`, so any additional missing fixture also fails. Shared sample fixtures require every configured sample root; store checks no longer silently omit absent sets. Direct unfiltered test execution outside this script can still report optional private-fixture skips; its result is not a strict private-lane verification record.

No hosted CI job has been enabled. These commands are suitable for a trusted local/private runner. Deciding where this private repository may be hosted or exporting its fixtures/source is a separate permission and infrastructure decision. No fixture or report upload is part of either script.

## Generated and malformed-input checks

`GeneratedParserTests` integrates **FsCheck.Xunit.v3 3.4.0** with xUnit 4.0.1 after a successful compatibility spike. Five properties each run 200 cases with fixed replay seeds and generation sizes capped at 64. They cover firmware normalization/BCD/range rejection, independently generated BPS literal patches and checksum corruption, and synthetic ELF/SELF round trips and truncation. Fixed regressions cover invalid signs, whitespace inside components, non-ASCII digits and integer overflow, while preserving existing surrounding-whitespace and missing-minor behavior. FsCheck shrinking is available when a property fails; retain the minimized input as a deterministic regression rather than merely changing its seed.

`ParserBudgetTests` runs a deterministic local mutation corpus in isolated child processes, using generated ELF, SELF and BPS seeds only. Each format has 512 cases: bit changes, truncation, oversized fields and appended bytes. BPS checksums are repaired where possible to exercise parsing beyond the checksum gate. ELF cases exercise model construction, symbols/imports/exports, names, SDK metadata and classification.

| Budget | Limit |
| --- | --- |
| Input and decoded target size in the harness | 64 KiB |
| Observed thread allocation per case | 8 MiB |
| Worker corpus elapsed time | 10 seconds |
| Parent-enforced child lifetime | 30 seconds |
| Runtime-enforced child managed heap | 256 MiB |

Unexpected exceptions fail with format, deterministic seed and case number; malformed input is expected to produce `InvalidDataException`. Timeouts terminate the child process tree. The managed-heap limit is not a total native-memory/working-set limit, and these small synthetic seeds are not exhaustive coverage of every real binary structure. This is a regression corpus, not a security certification or long-running fuzz service.

Core now exposes caller-budget overloads `Self.Unwrap(bytes, maximumOutputBytes)` and `Bps.Apply(patch, source, maximumOutputBytes)`. They check before allocating decoded output. Existing overloads preserve the prior supported-size policy; the application has not gained an arbitrary new maximum game size. SELF span metadata readers now reject truncated headers/entry tables/digests as data errors. The allocation-limit regression uses a bounded oversized target and checks allocations, so the test itself does not require a huge allocation.

## Provider-ownership regression

The synthetic reproduction showed a real bug: excluding a current module's exported NIDs from a pooled set also removed a NID exported by a different module. Both whole-game cooking and library checks could then classify it as missing/unknown. Cooking now counts distinct module owners per exported NID; a NID remains provided if at least one other module owns it. Library checks union other modules' exports directly. A control case verifies that a module cannot act as its own provider. Existing private golden comparisons remain unchanged and pass.

## Optional mutation-testing evaluation

```powershell
pwsh -NoProfile -File scripts/evaluate-mutations.ps1
```

This installs/uses **dotnet-stryker 5.0.0** under ignored `work/phase3/tools`, restores the dedicated public-only `verification/MutationTests` project in locked mode, and writes local JSON/text results under `work/phase3/stryker`. It uses two workers and a 180-second overall process limit (configurable from 30–600 seconds), with per-mutant timeouts. The project links the same generated tests rather than maintaining a second copy. It stays outside the application solution and default verification lane.

The experiment targets Basic mutations in `Firmware.cs` only. Stryker's MTP runner is preview, and its xUnit MTP integration cannot use the normal selective test-case filter, which is why this small isolated project is used. The first run exposed missing invalid-token/range tests; focused regressions were added and the evaluation rerun. Final target results: **20 killed, 3 survived, 0 uncovered, 0 timeouts**, with **4 compile-error and 2 ignored** target mutants. Three survivors are redundant-input-check changes: subsequent digit/number parsing still rejects the same malformed inputs. They are documented rather than chased with an arbitrary global score target. Stryker also reported instrumentation compile errors outside the selected file and its aggregate table counts ignored mutants confusingly; the target JSON statuses are the evidence used here. Do not treat its displayed aggregate score as a whole-engine quality measure.

Keep Stryker optional for focused reviews. Expand to binary arithmetic/range checks only after understanding instrumentation compatibility and reviewing survivors; the initial scope is deliberately small. No dashboard/report upload is enabled.

## Recorded acceptance

- Full local suite: **315 tests**, zero failures/skips in both Debug and Release; both builds had zero warnings/errors.
- Fixture-free local source copy, initially containing no bin/obj or private roots: locked restore and both builds passed. Final public lane: **204 tests**, zero failures/skips in each configuration. This used the existing NuGet cache/feed, not a disconnected empty package cache.
- Private coverage: the complementary **111 tests** passed as part of full verification. Requesting the private lane in the fixture-free copy failed setup immediately with a named missing root.
- A deliberately changed package request in that disposable copy caused locked restore to fail with NU1004; the project file was restored afterward.
- Five FsCheck properties provide 1,000 generated cases per suite run; child workers add 1,536 binary mutations. These cases are additional work inside xUnit test methods, not separate xUnit test-count entries.
- Existing goldens/rules/private binaries were not edited, and `git diff --check` passed. Phase 4 completion is recorded below.

Primary tool documentation: [NuGet lock files](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files#locking-dependencies), [FsCheck v3 integration](https://www.nuget.org/packages/FsCheck.Xunit.v3/3.4.0), [Stryker configuration and MTP limitations](https://stryker-mutator.io/docs/stryker-net/configuration/).

## Phase 4 verification

On 10 October 2026, `scripts/verify.ps1 -Lane All` passed locked restore, zero-warning Debug/Release builds and **342 tests per configuration**, with zero failures/skips. There are 27 new lifecycle/snapshot/staleness/cancellation/UI cases (231 public, 111 private cases overall). Existing byte/report goldens passed unchanged. Records: `work/verification/20261010-020320-70e2650e82ae4af8bfb628a0399f0b57`.

Synthetic scan cancellation measurements and operation/shutdown semantics are in [OPERATION_LIFECYCLE.md](OPERATION_LIFECYCLE.md). View-model closing ownership and delayed progress are covered; native desktop closing/picker interaction remains part of Phase 6. No new dependency or golden recording was required.

## Phase 5 verification and optional profiling

Full local verification on 10 October 2026 passed **345 tests in each Debug/Release configuration**, zero failures/skips and zero-warning builds (234 public + 111 private). Records: `work/verification/20261010-021831-33e89269039d464ab0860f7771c58575`. New cases verify signer allocation/input preservation/round-trip behavior and captured firmware selection. Existing byte/report goldens pass unchanged.

`scripts/measure-performance.ps1` restores/builds the optional package-free harness in locked mode, creates public synthetic inputs when absent, and records local Analyze/Cook measurements. `-IncludePrivate` uses the existing fixture/store corpus. With `-BaselineDirectory`, identical harnesses execute preserved/current engines and the script rejects mismatched input/payload/report hashes. Keep the baseline Core DLL intact. Runs produce real payloads and require disk space; raw private measurements stay under ignored `work/`. Avoid competing verification/build work during timing comparisons.

`verification/Benchmarks/Ps5Backport.Benchmarks.csproj` pins BenchmarkDotNet 0.15.8 and stays outside the solution. It measures only confirmed duplicate firmware parsing. Restore in locked mode before running. `scripts/measure-ui-memory.ps1` measures fresh headless Skia UI processes after a Release app build, retains frames and enforces a 120-second child timeout. It reports aggregate process memory, not exact native allocations or GPU/desktop memory.

See [PERFORMANCE_REPORT.md](PERFORMANCE_REPORT.md) for commands, full measurements, observer limitations, cancellation, the two-pass experiment and deferred ownership/cache/concurrency decisions. The production pipeline does not gain a hard RAM cap or parallel workers.

## Phase 6 diagnostics and interaction verification

Full local verification passed **357 tests per Debug/Release configuration**, zero failures/skips and zero-warning builds (246 public, 111 private). Records: `work/verification/20261010-024610-a1fb4e8e72c34cf3aa7826bdb9f51e65`. Existing byte/report goldens pass unchanged.

The solution now builds the package-free `verification/UiProfile` worker in both configurations. Five mandatory headless scenarios run actual controls/bindings/input and the production close policy in isolated processes with 30-second parent timeouts. Seven diagnostics cases verify rotation, failure isolation, correlation, exception persistence, and bounded UI retention. The worker exits after its checks because local headless renderer disposal blocked; this does not certify renderer disposal.

After a Release build, `dotnet verification/UiProfile/bin/Release/net10.0/UiProfile.dll work/phase6/filter-after.json` records the optional 10,000-row filter profile. Run once without competing timing work. Baseline measurements require preserved Phase 5 assemblies; do not substitute current assemblies and label them a baseline.

Native Windows keyboard search/traversal, Escape picker cancellation, accessible names, busy bindings, normal/maximized layouts and movement between both displays were checked. Scope and unverified minimum-size/native busy-close/higher-DPI/GPU cases are documented in [LOCAL_DIAGNOSTICS.md](LOCAL_DIAGNOSTICS.md). Headless verification cannot prove desktop focus, OS pickers, GPU behavior or mixed-DPI transitions.

## Phase 7 optional decoder evaluation

Full local verification on 10 October 2026 passed **357 tests in each Debug/Release configuration**, zero failures/skips and zero-warning builds. Private byte/report goldens passed unchanged. Records: `work/verification/20261010-031031-82cadd74618248d5b350078a07cefaa4`. Phase 7 adds no mandatory xUnit cases or application dependencies; its only production edit is a correction to the scanner's XML comment.

`pwsh -NoProfile -File scripts/evaluate-decoder.ps1` restores the isolated Iced 1.21.0 project in locked mode, builds Release and runs 530 specified reference cases plus emitter checks against public rule data. `-IncludePrivate` additionally compares three existing ELF fixtures without modifying them. Missing private inputs fail explicitly. The project stays outside `Ps5Backport.slnx`; ordinary solution verification does not run this optional experiment.

Eight named stubs, 45 adapter byte blocks, return constants, GOT targets, PLT redirection and split pre/post-call chains passed encoding/operand checks. These checks do not certify console ABI or general code-boundary recovery. Warm timing/allocation, pre/active cancellation, input hashes, count differences and private inspection contexts stay in the local record `work/phase7/20261010-031012/evaluation.json`. No golden recording or private upload occurred.

See [INSTRUCTION_DECODER_REPORT.md](INSTRUCTION_DECODER_REPORT.md) for the accuracy cases, measurement limits, dependency review and decision against replacing production scanning with segment-linear decoding. Run performance experiments separately from builds/tests.

## Phase 8 optional decision-gate verification

Full local verification passed **357 tests in each Debug/Release configuration**, zero failures/skips and zero-warning builds on 10 October 2026. Private byte/report goldens remain unchanged. Records: `work/verification/20261010-032020-04ec1872bf4d4006b2e800c901635003`. Phase 8 changes no production code, application package/version, rule or golden file.

`pwsh -NoProfile -File scripts/evaluate-phase8.ps1` restores the isolated System.CommandLine 2.0.12 project in locked mode, builds Release, asserts/records 18 parser observations and three copied-data semantic observations, and measures existing catalog load/query costs. The Python companion asserts SQLite semantic round-trip (including a synthetic empty module), equivalent query hits, 52 existing schema records and six specified edge cases. Final local records: `work/phase8/20261010-032209/`. Do not count those experiment cases as new xUnit tests.

The script requires an existing Python environment with jsonschema 4.26.0 (`-Python` selects its executable); it installs nothing. It records Python/SQLite/schema and dependency versions but does not content-hash lock or advisory-certify that environment. SQLite stays in memory. Synthetic copied knowledge files stay under the ignored report directory; original catalog/rule hashes are verified. Backport/report tokens are parsed without executing file-modifying handlers; only safe NID/usage commands are invoked. No private binary input or upload is needed.

The project/schema stay outside the application solution and ordinary verification. System.CommandLine and SQLite migrations were deferred; the single-record schema is a partial authoring experiment, not a mandatory full runtime contract. [PHASE8_DECISION_REPORT.md](PHASE8_DECISION_REPORT.md) records pros/cons, concrete compatibility/authoring findings, measurements, limitations and future migration requirements. Existing semantic validation and file publication/recovery remain necessary.
