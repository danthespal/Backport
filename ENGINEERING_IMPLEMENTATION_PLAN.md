# PS5 Backport Studio — engineering implementation plan

Created: 10 October 2026. Based on ENGINEERING_RESEARCH_REPORT.md.

## Working rules and compatibility baseline

- Keep .NET, Avalonia, CommunityToolkit.Mvvm, and the shared Core engine.
- Preserve existing uncommitted work, rule ordering, patch bytes, normal CLI behavior, report formats, and existing golden files unless a phase explicitly introduces a versioned format.
- Never modify source game/system binaries or regenerate golden data to make failures pass.
- No console integration, installer, remote telemetry, generic plugin platform, or engine rewrite.
- Implement one phase at a time. A phase finishes only after its acceptance checks pass; record actual results below.
- Use small records/helpers and explicit dependencies first. Add a framework only when an experiment demonstrates a benefit.

## Phase 1 — rule validation and write-free preflight

Status: completed on 10 October 2026. Dependencies: none.

Goal: reject malformed or unsafe rule data and invalid run prerequisites before creating output files. This deliberately precedes whole-run transactions.

Tasks:

1. Establish build/test baseline; distinguish sandbox filesystem failures from software failures.
2. Make knowledge-file parsing report the responsible filename, reject malformed structure/duplicate keys, and keep free-text source attribution compatible.
3. Validate supported fix kinds and conditions, required stub/swap/redirect/adapter operands, named stub references, firmware cutoffs, option variants, and pattern byte lengths.
4. Require pattern file paths to be safe relative paths under their intended root. Normalize both separators; reject rooted/drive paths, traversal, ambiguous/invalid path components. Apply the same resolver at the read/write boundary.
5. Delay initial output-directory/param writes until game metadata, selected fakelibs/variants, and firmware references have passed preflight. Refuse missing source/explicit fakelib folders.
6. Add synthetic regressions for malformed rules, escaping paths, and failed prerequisites that preserve an existing output sentinel and create no new output.

Acceptance:

- Checked-in knowledge loads with unchanged values and ordering; malformed rules fail as InvalidDataException with useful file/rule context.
- Existing valid pattern paths and option variants work; unsafe paths fail before a write.
- Failed preflight creates no output directory and does not alter existing output.
- Debug and Release build with zero warnings/errors; full existing and new tests pass without rewriting goldens.

Limits: lexical path validation cannot establish physical identity against filesystem links or concurrent filesystem changes. No crash/power-loss or whole-overlay transaction guarantee is claimed in Phase 1. Formal JSON schemas and format versioning can follow when data interchange requires them; runtime semantic checks are the initial contract.

## Phase 2 — staged output, store recovery, and run evidence

Status: completed on 10 October 2026. Depends on Phase 1. Recovery/retention contract: [PUBLICATION_RECOVERY.md](PUBLICATION_RECOVERY.md).

Goal: publish a clearly completed result and retain a usable prior result on failures.

Tasks:

1. Define complete/incomplete criteria, including module errors and unresolved imports. Preserve analysis reports for rejected results; do not silently promote incomplete output.
2. Write to a unique sibling staging directory on the destination volume. Start with new destinations; existing-output replacement needs explicit backup/recovery semantics.
3. Validate filesystem-link/physical-alias policy before writes, including output parents, existing destination children, and source aliases. Add platform-specific tests.
4. Verify staged file lengths/hashes and write a versioned completion manifest. Preserve current patch/report golden bytes; evidence can live in a separate sidecar.
5. Record effective options, engine revision/version, reference exact/lower-bound status, knowledge and selected-library hashes, and input fingerprints. Define hashing cost and privacy policy.
6. Serialize writers to the same destination/store. Stage store library batches and manifest together; add an interruption journal/recovery protocol where replacing existing files requires it.
7. Make publication a short critical section: cancellation before it abandons staging; interruption during it is recoverable. Avoid promising multi-file atomicity from individual file renames.
8. Implement explicit recovery/cleanup for abandoned staging and backups; never recursively delete an unchecked computed path.

Acceptance: fault injection at each write/copy/commit step; cancellation; process interruption; stale-output reuse; two writers; disk-full/permission failures. Prior completed output remains usable, partial output cannot claim completion, source hashes stay unchanged, and recovery is deterministic. Document filesystem and power-loss guarantees actually demonstrated.

Delivered:

- Cooking writes a fresh same-volume stage. Module errors retain reports in an incomplete diagnostic sibling; successful runs replace the active directory with a verified result and retain nonempty previous output as a backup. Unresolved imports remain explicit warnings rather than a claim of console compatibility.
- Version 1 completion inventory (sizes/SHA-256) and separate run evidence preserve existing reports/patch bytes. Effective options, engine/runtime, reference confidence, loaded knowledge, selected libraries, relevant source fingerprints and inventories are recorded without adding private absolute paths to the new sidecar.
- Stable physical-target locks, a store-wide writer lock, validated journal-derived recovery paths, and idempotent recovery protect cooperative writers. Fakelib batches/variants stage the containing firmware set; AMPR index updates stage the complete overlay and refresh its completion record.
- Windows physical aliases are checked by directory identity; link/junction inputs/destinations are refused. Before replacement, input fingerprints and inventories are checked again. Cancellation is honored before the recoverable replacement critical section.
- Nonempty backups and incomplete diagnostics have manual retention. No unrequested automatic eviction; stable lock files remain. See the recovery document for exact states, space budgeting, and cleanup limits.

Verified scope: Windows/local NTFS; injected disk-full/access/copy/write failures, actual sharing denial, cancellation, competing processes, Windows aliases/links, variant rollback, tampering, changed inputs, and child-process exits at publication boundaries. Actual disk exhaustion, physical power loss, network/cloud filesystems and adversarial path-swap races are not certified. Hashing/full-directory staging adds I/O and disk use; optimization remains measurement-driven Phase 5 work.

## Phase 3 — reproducible verification and parser resilience

Status: completed on 10 October 2026. Depends on Phases 1–2. Entry points/results: [VERIFICATION.md](VERIFICATION.md); package review: [DEPENDENCY_REVIEW.md](DEPENDENCY_REVIEW.md).

Tasks:

1. Separate mandatory synthetic/public tests from private-fixture integration tests. Report skips; fail private-lane setup when required fixtures are absent.
2. Add a repeatable local verification entry point and CI only where hosting/private-data constraints permit. Build both configurations and run required lanes.
3. Check in NuGet lock files and use locked restore; record SDK/runtime versions. Audit direct and transitive dependencies and define review/update policy.
4. Add FsCheck only after a compatibility spike; generate bounded valid/invalid BPS, firmware, SELF, and ELF cases. Keep minimized failures as deterministic regressions.
5. Add bounded mutation fuzzing for binary readers with file-size/time/allocation budgets. Do not upload private binaries.
6. Reproduce the duplicate-export provider ownership question with synthetic modules; fix only if actual classification is wrong.
7. Evaluate selective Stryker.NET mutation testing of arithmetic/range checks; avoid a blanket mutation-score target.

Acceptance: a clean checkout verifies mandatory behavior without private samples; private verification reports expected coverage; corrupted binary cases terminate within budgets; fixed failures have meaningful regressions; no golden regeneration or private-source upload.

Delivered:

- `scripts/verify.ps1` provides Public/Private/All lanes, locked restore, both build configurations, strict skip/warning checks, nonempty result checks and local environment/XML records. Private setup fails when fixtures are absent; shared sample/store checks no longer accept silently missing roots/sets. Hosted CI remains deferred to a trusted hosting decision.
- Exact SDK 10.0.401 pin, five NuGet lock files (four solution projects plus optional mutation project), and a dated direct/transitive vulnerability/deprecation/license/update review. Existing framework versions remain; FsCheck is confined to tests. Scripts apply .NET/MTP/Avalonia build-telemetry opt-outs and retain local reports.
- FsCheck.Xunit.v3 3.4.0 passed the .NET 10/xUnit compatibility spike. Five bounded replayable properties provide 1,000 generated cases per suite run across BPS, firmware, ELF and SELF; fixed malformed-token/metadata cases remain deterministic regressions.
- Three child mutation workers exercise 1,536 synthetic binary mutations with 64-KiB input/decoded-output, 8-MiB per-case allocation, 10-second corpus, 30-second child lifetime and 256-MiB managed-heap limits. Caller-budget overloads check decoded output before allocation; old overloads preserve the existing supported-size policy. Truncated SELF metadata now fails as a data error.
- The duplicate-export reproduction failed in both cooking and store checks. Fixed provider ownership to retain exports owned by another module while excluding the current module as its own provider; the positive/negative synthetic regressions and existing private goldens pass.
- Optional local Stryker 5.0.0 evaluation targets Firmware.cs through a small public-only project outside the solution. The MTP preview run exposed invalid-token coverage gaps; focused regressions were added. Final target statuses: 20 killed, 3 redundant-check survivors, 0 uncovered/timeouts, 4 compile-error and 2 ignored mutants. No whole-engine score requirement or dashboard upload.

Acceptance evidence: **315 tests in each Debug/Release full run**, zero failures/skips/warnings; **204 public tests in each configuration** in an isolated local source copy with no private fixtures or initial bin/obj; complementary **111 private tests** covered locally. The fixture-free private lane failed setup clearly, and a deliberately inconsistent package request failed locked restore with NU1004. Existing goldens, rules and private binary fixtures remain unchanged. Resource-bounded synthetic tests do not certify every real binary, operating system or native-memory behavior.

## Phase 4 — explicit operation lifecycle and orchestration seams

Status: completed on 10 October 2026. Depends on Phases 1–3.

Tasks:

1. Extract discovery/provider metadata, transformation, output publication, and reporting incrementally while preserving order.
2. Capture an immutable run snapshot and explicit completed/failed/cancelled state. Avoid mutable UI settings changing an active operation.
3. Bind Analyze-to-Apply reuse to input/config fingerprints; revalidate before applying. Keep existing full dry-run semantics until equivalence is proven.
4. Add safe cancellation checkpoints inside expensive scans/transforms after measuring latency. Define close-while-busy behavior and await/cancel ownership cleanly.
5. Reduce PatchModule's parameter bundle with a concrete run context; introduce interfaces only for justified alternate implementations or fault injection.

Acceptance: byte/report compatibility; lifecycle transitions exercised; cancelled/failed operations never present success progress; stale analysis is refused; UI and CLI still use the same engine.

Delivered:

- A one-shot `BackportOperation`, explicit terminal state, and a private run snapshot with detached options and deeply copied rules. Existing UI/CLI entry points share the same engine; no runtime framework/dependency was added.
- Separate discovery, provider/module transformation, publication and reporting partials; a concrete `PatchContext` replaces the long patch-method argument bundle. Existing processing order and legacy report serialization remain intact.
- Successful full analyses issue process-local receipts bound to relevant input hashes, absence/inventory records, loaded metadata/names and effective settings. Cook revalidates before acquiring/staging output; stale analysis is refused. Both Analyze and Cook still perform the full transformation; transformed-byte caching remains deferred.
- Cancellation checkpoints in directory traversal, pattern replacement and code-reference scans, plus safe transformation boundaries and cancellable AMPR staging. A measured 64-MiB dense pattern scan was 614.1 ms before checks; after checks, local cancellation observation was 0.17–1.51 ms for patterns and 0.05–0.14 ms for code scans. These are synthetic scan measurements, not a whole-operation SLA or throughput improvement claim.
- Terminal UI progress and stale-callback guards; close requests cancel/await backport/index cleanup and await already-owned page workers before saving settings and closing. Individual synchronous file/hash/signing calls and protected commit can still delay cancellation. Native closing/picker checks remain part of Phase 6.
- Synthetic operation/snapshot/staleness/cancellation/UI regressions. Scope, tradeoffs and measurement details are in [OPERATION_LIFECYCLE.md](OPERATION_LIFECYCLE.md).

Acceptance evidence: **342 tests in each Debug/Release full run**, zero failures/skips/warnings (315 previous + 27 Phase 4 cases; 231 public and 111 private cases). Existing private byte/report golden comparisons pass. `git diff --check` passes, and rule data, goldens and private binaries remain unchanged. View-model lifecycle behavior is tested; native desktop close/picker interaction remains scheduled for Phase 6.

## Phase 5 — measured memory and throughput improvements

Status: completed on 10 October 2026. Depends on Phases 3–4. Measurement, focused optimization and adoption decisions are complete; deferred experiments are documented below.

Tasks:

1. Define representative small/large workloads and cold/warm measurements on the same machine.
2. Record phase time, peak working set, managed/large-object allocation, GC activity, native UI memory, and cancellation latency with .NET diagnostics.
3. Add focused BenchmarkDotNet benchmarks only for confirmed hot operations.
4. Evaluate two-pass module processing: compact provider metadata first; one active transformed module at a time. Detect input changes between passes.
5. Cache immutable firmware metadata per run; explicitly invalidate module metadata after mutation.
6. Evaluate pooling/scratch decoded files only where measurements justify ownership complexity.
7. Consider bounded worker concurrency or Channels only after serial memory ownership is correct. Preserve deterministic report order and a documented memory budget.

Acceptance: publish before/after measurements with identical inputs/options; all correctness checks remain green; no improvement percentage promised before measurement; reject changes that trade meaningful correctness or cancellation regressions for minor speed gains.

Delivered:

- Optional locked measurement harness and scripts, with synthetic small/large and two existing private workloads; process-cold/warm observations, phase time/allocation, managed/LOH/GC/process counters, cancellation and headless Skia UI process measurements. Exact native allocation/GPU/desktop profiling remains a Phase 6 follow-up. No hosted service or data upload was added.
- Firmware databases parsed once and reused as read-only run-owned metadata, with unchanged exact/lower-bound/genstub selection and fresh loading on subsequent runs. No global cache. Module metadata still reparses after mutation; a cache with potentially missed invalidation was deliberately not adopted.
- SELF signing reads segment/version slices from the existing ELF buffer, eliminating intermediate payload copies. Synthetic allocation/input/round-trip regression and unchanged private payload/report goldens verify behavior.
- Focused optional BenchmarkDotNet 0.15.8 benchmark for duplicate firmware parsing, outside the application solution. Approximately 218.52 → 109.93 MiB allocated in that operation; timing intervals are too wide for a precise speed promise.
- Public large Cook allocation approximately 579.6 → 387.6 MiB and peak working set 628 → 436 MiB; private large Cook allocation 2,398.0 → 1,974.1 MiB. All paired input/payload/legacy-report hashes match. Private small Analyze has lower allocation but higher observed peak working set, so no universal RAM reduction is claimed.
- Loading-only two-pass experiment approximately 192 → 384 MiB allocated and 124 → 235 ms, despite lower required decoded ownership. Production two-pass loading, module caches, pooled/scratch buffers and parallel workers are deferred pending a full semantic/budget prototype. Serial transformation order remains. Large-game memory is still proportional to retained modules; a hard working-set budget is not implemented.

Acceptance evidence: **345 tests per Debug/Release configuration**, zero failures/skips/warnings (234 public, 111 private). Final measurements and limitations: [PERFORMANCE_REPORT.md](PERFORMANCE_REPORT.md); raw local records under `work/phase5/final/`, benchmark results and UI process/frame records under `work/phase5/`. Existing rules, goldens and private binaries remain unchanged.

## Phase 6 — local diagnostics and UI interaction quality

Status: completed on 10 October 2026 within the recorded verification scope. Native checks cover the available display configuration; higher/mixed-DPI coverage remains a follow-up. Depends on Phase 4.

Tasks:

1. Add structured local events with run/module/stage IDs, duration, error category, and exception context. Select a minimal logging/file provider; no remote telemetry.
2. Bound in-memory log retention and define local file rotation/privacy behavior.
3. Profile collection updates; batch/debounce filtering only if needed, preserving selection and existing search semantics.
4. Add compatible Avalonia headless interaction tests for busy commands, cancel, selection/search, stale results, and close-while-busy.
5. Perform focused native desktop checks for scaling, keyboard focus, accessibility, and multiple monitors.

Acceptance: repeated operations do not accumulate unbounded logs; useful diagnostics survive window closure; retention works; headless and native checks cover their respective guarantees.

Delivered: bounded local JSON-lines diagnostics with run/module/stage durations and exception context; 2,000-entry UI history; 1-MiB × four-file rotation per desktop/CLI client; shared production/test shutdown policy; batched import/log filtering with preserved visible selection; accessible navigation/search names. The 10,000-row profile reduced full-list notifications from 10,001 to one and UI-thread allocation from approximately 2.7 MB to 82 KB; timing is a local observation, not a desktop SLA.

Automated evidence: **357 tests per Debug/Release configuration**, zero failures/skips/warnings (246 public, 111 private). Twelve new cases cover diagnostic failure/rotation/correlation/retention and real headless controls, cancellation, search/selection, stale results and closing. Native checks verified keyboard search input/traversal, accessible names, Escape cancellation of the folder picker, busy command bindings, normal/maximized layouts, movement/maximization on both monitors and persisted clean-close events. Minimum-size layout, native close during active work, higher/mixed-DPI and exact native/GPU memory attribution remain unverified; the report distinguishes these from headless guarantees. Scope, commands, measurements, privacy and follow-ups: [LOCAL_DIAGNOSTICS.md](LOCAL_DIAGNOSTICS.md).

## Phase 7 — instruction-decoder evaluation

Status: evaluation completed on 10 October 2026. Depends on Phase 3; uses the Phase 5 local measurement approach. Decision: keep Iced optional for audit/emitter validation; reject replacement of the production scanner with segment-linear decoding.

Tasks:

1. Trial Iced in analysis/test code first; leave patch output unchanged.
2. Compare current reference counts against decoded instructions and independently inspected synthetic/corpus cases.
3. Include embedded data, opcode-like immediate bytes, prefixes, invalid/truncated instructions, and indirect control flow. Define how code/data ambiguity is handled.
4. Validate generated instruction sequences and register/branch interpretation where useful; retain console ABI validation as a separate requirement.
5. Measure runtime/allocation impact and review dependency/licensing footprint.

Acceptance: documented accuracy improvement, understood disagreements, acceptable costs, and no patch-output drift. Adopt only if the experiment demonstrates benefit; decoding alone is not proof of safe game behavior.

Delivered: isolated locked Iced 1.21.0 evaluation outside the application solution; 530 synthetic reference cases, eight named stubs, 45 adapter blocks and focused generated register/branch/chain checks; three read-only private corpus comparisons; warm time/allocation and cancellation measurements; license/advisory review. Decoder boundaries avoid immediate/displacement false matches and improve prefix semantics, but embedded data/padding can introduce missed code. No production dependency or patch behavior changed. The scanner's XML comment now acknowledges missed references as well as over-counts. Findings, limitations, adoption gates and reproduction: [INSTRUCTION_DECODER_REPORT.md](INSTRUCTION_DECODER_REPORT.md).

Acceptance evidence: **357 tests per Debug/Release configuration**, zero failures/skips/warnings; private byte/report goldens unchanged. Optional evaluation's locked restore, Release build, 530 reference cases and emitter checks pass. Records: `work/phase7/20261010-031012/evaluation.json` and `work/verification/20261010-031031-82cadd74618248d5b350078a07cefaa4`. Rule/golden/private binaries were not edited.

## Phase 8 — optional CLI/catalog changes

Status: optional decision gate completed on 10 October 2026. Retain the current CLI and JSON storage; keep the partial schema authoring prototype optional pending a complete contract. No production migration was adopted.

- System.CommandLine: evaluate if command/parser maintenance grows. Preserve aliases, repeated options, help/error behavior, exit codes, and golden outputs.
- SQLite: evaluate only when JSON catalog/history queries or concurrent metadata edits become a measured burden. Keep inspectable import/export; database transactions do not replace external-file recovery.
- Formal versioned JSON schemas: add if rules become an interchange format or are authored by more tools; keep semantic validation regardless of schema library choice.

Acceptance: each proposal needs a concrete maintenance/performance problem, a small comparison prototype, migration tests, and an explicit adoption decision. Otherwise keep the simpler existing implementation.

Delivered: isolated locked System.CommandLine 2.0.12 prototype with 18 specified parser observations; .NET catalog load/query baseline; in-memory SQLite semantic round-trip and equivalent membership queries; Draft 2020-12 single-record schema tested against 52 current fixes plus six edge cases and three copied-data Core checks. Framework defaults conflict with repeated scalar options, boolean presence, help aliases and `--`; loaded catalog membership queries are inexpensive; a schema catches misspelled `proven` fields but cannot replace semantic validation. No production dependency/code, rule, binary or golden change. Decisions, measurements, limits and scoped follow-up gates: [PHASE8_DECISION_REPORT.md](PHASE8_DECISION_REPORT.md).

Acceptance evidence: optional locked restore/Release build and all specified comparison assertions passed; **357 tests per Debug/Release configuration**, zero failures/skips/warnings, private byte/report goldens unchanged. Records: `work/phase8/20261010-032209/` and `work/verification/20261010-032020-04ec1872bf4d4006b2e800c901635003`. No migration tests are claimed for proposals explicitly deferred; future adoption must pass the report's compatibility/migration gates.

## Execution log

- Phase 1 baseline: Debug build succeeded with 0 warnings/errors. Sandboxed test execution encountered 7 temporary-file move/access failures; the normal-permission baseline rerun passed all 201 tests, with 0 failures/skips.
- Phase 1 implementation: contextual knowledge parsing/semantic validation; duplicate-key and null-string rejection; safe portable pattern paths; mixed-separator pattern grouping; missing source/explicit-library checks; output creation and param writes delayed until preflight completes. Source changes are limited to Knowledge.cs, Paths.cs, and Pipeline.cs, plus the new PreflightTests.cs. Earlier uncommitted edits are preserved.
- Phase 1 final verification: Debug and Release builds both passed with 0 warnings/errors. Full Debug test execution passed **260 tests, 0 failures, 0 skips** (201 existing + 59 new synthetic cases). Existing golden comparisons passed; no goldens, rules, or private binaries were edited. git diff --check passed.
- Commands: `dotnet build Ps5Backport.slnx -c Debug --no-restore -m:1`; `dotnet build Ps5Backport.slnx -c Release --no-restore -m:1`; `dotnet src/Ps5Backport.Tests/bin/Debug/net10.0/Ps5Backport.Tests.dll` (normal local permissions for temporary-file operations); `git diff --check`.
- Phase 1 boundary at completion: invalid module contents, later cancellation, and failures after preflight could still leave partial output. Phase 2 now adds staged publication, physical-path checks, recovery and fingerprints.
- Phase 2 implementation: new Publication/DirectoryTransaction, PhysicalPaths, RunEvidence and synthetic PublicationTests; focused integration in Pipeline, FakelibStore, AmprIndex, Paths, knowledge/name fingerprints, CLI and cooking UI. Existing uncommitted work remains preserved. No framework/dependency adoption or engine rewrite.
- Phase 2 final verification: Debug and Release solution builds passed with **0 warnings/errors**. Full Debug execution passed **293 tests, 0 failures, 0 skips** (260 prior + 33 publication cases), including actual child-process exits and a competing-process lock test. Existing golden comparisons passed; no golden files, knowledge rules or private binary fixtures were edited. `git diff --check` passed. Temporary filesystem tests ran with normal local permissions.
- Phase 2 documentation: [PUBLICATION_RECOVERY.md](PUBLICATION_RECOVERY.md) defines completion, evidence/privacy, filesystem policy, recovery states, manual backup retention, disk costs and the limits of power-loss claims.
- Phase 3 baseline: 293 tests from Phase 2. Completed local full verification now passes **315 tests, 0 failures, 0 skips** in both configurations; Debug/Release builds have **0 warnings/errors**. The added 22 xUnit cases contain additional generated/fuzz cases rather than inflating the xUnit count for each generated input. Existing byte/report goldens pass without edits.
- Phase 3 clean-source verification: no private fixtures or initial build artifacts were copied; locked restore and both configurations passed. Final Public lane passed **204 tests** per configuration. Missing Private setup and changed-dependency locked-restore failures were deliberately reproduced. NuGet cache/feed availability was retained, so this is not a disconnected empty-cache restore claim.
- Phase 3 tool/dependency review: NuGet vulnerable/deprecated transitive queries returned no flagged packages; available stable updates were documented without changing existing versions. FsCheck compatibility and the bounded optional Stryker workflow passed. Tool/runtime versions, limitations and update policy are in the verification/dependency documents. All generated reports stayed local; no hosted CI or upload was enabled.
- Repository graph indexing was rejected by automatic approval review because it would export private source to the indexing service. Local code reads/searches completed the work without that export.
- Phase 4 implementation: explicit operation states and detached snapshots; extracted ordered pipeline boundaries; analysis receipts revalidated before output acquisition; cooperative scan/index cancellation; terminal UI progress guards and awaited shutdown ownership. Existing uncommitted changes remain preserved; no dependency additions, rule/golden edits or binary export.
- Phase 4 final verification: `pwsh -NoProfile -File scripts/verify.ps1 -Lane All` passed locked restore, warning-free builds and **342 tests in each Debug/Release run**, with zero failures/skips. Local records: `work/verification/20261010-020320-70e2650e82ae4af8bfb628a0399f0b57`. Synthetic latency results: `work/phase4/latency/after.txt`. Existing byte/report goldens passed without edits; `git diff --check` passed.
- Phase 5 implementation: single per-run firmware parse, zero intermediate segment copies during signing, optional locked measurement/benchmark projects, public/private before/after comparisons, cancellation and headless UI process profiling. Rejected/deferred experiments and remaining memory/native-profile limitations are documented in PERFORMANCE_REPORT.md.
- Phase 5 final verification: `scripts/verify.ps1 -Lane All` passed **345 tests per configuration**, zero failures/skips and zero-warning builds. Records: `work/verification/20261010-021831-33e89269039d464ab0860f7771c58575`. Final payload/legacy report hashes match between preserved Phase 4 and Phase 5 engines. No golden/rule/private-binary changes.
- Phase 6 completed within its documented native/headless scope; Phase 7 completed with an optional evaluation and a decision against production linear decoding. Phase 8's optional decision gate is completed; CLI/database migrations are deferred and the partial authoring schema remains optional. This plan does not authorize publishing, sending tester messages, or uploading private fixtures.
