# PS5 Backport Studio audit — 2026-10-10

The audit target is the software in this workspace: `Ps5Backport.Core`, `Ps5Backport.Cli`, `Ps5Backport.App`, and their tests. The supplied OriathHub-specific instructions describe another project; no OriathHub files were present or modified. The working tree was clean at the start.

This is a source review, regression-test audit, and headless UI inspection. It is not a guarantee that every possible bug has been eliminated. Changes were limited to defects with a clear corrective behavior. No game or console was launched, no source game was patched, and no knowledge/golden data was regenerated.

## Confirmed fixes

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

## Requested categories and coverage

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

## Verification

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

## Follow-up validation and design questions

- Console execution remains unverified. Native stub/adapter semantics, loader behavior, firmware database completeness, and text-extension compatibility cannot be certified by desktop tests alone. No knowledge entries, offsets, or instruction bytes were guessed at or changed.
- Large modules/data files are still processed in memory. Measure peak memory and native resource retention during sustained real-world use before claiming leak-free operation.
- Output-path protection is lexical after normalizing paths. Alternate physical aliases, directory junctions in the output path, and filesystem changes during a run require additional validation. The scan walkers skip linked subdirectories, but this is not physical-path canonicalization.
- Reusing an output folder can retain files from an earlier run with different options. Cancellation/I/O failures can leave a partially completed overlay. Choosing deletion, staging, rollback, or a new-folder-only policy needs a defined retention policy; this audit does not silently delete existing output.
- Store/database writes are not a transaction across multiple files or independent processes. A batch failure or another application changing inputs concurrently needs fault-injection testing and a defined recovery policy.
- Native dependency internals, platform/DPI/accessibility compatibility, arbitrary malformed JSON schemas, and exhaustive binary fuzzing were not proven safe by this audit. No dependency upgrades or speculative API redesigns were made.
