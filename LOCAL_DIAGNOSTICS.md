# Phase 6 — local diagnostics and UI verification

Completed 10 October 2026 within the verification scope below. Native checks cover the available desktop configuration; higher/mixed-DPI and exact GPU/native allocation measurements remain follow-ups.

## Delivered behavior

Desktop and CLI backports use the same optional Core diagnostic sink. JSON-lines events include a UTC timestamp, operation, run ID, stage/module, measured duration, terminal state, error category and exception type/context. A pre-cancelled operation enters the engine far enough to record cancellation before doing work. Module failures retain their exception context even when the engine returns a failed result rather than throwing. AMPR index runs have their own correlation IDs; desktop activity/errors and shutdown events also persist.

Stage durations measure intervals between existing progress boundaries. They include orchestration and diagnostic overhead and are not individual CPU-function measurements. Snapshot construction can fail before an engine run exists; desktop/CLI catch diagnostics record those exceptions without an engine run ID. Normal non-backport CLI commands record caught failures, rather than a complete success timeline for every command.

The desktop's close policy now has one implementation used by production and headless checks: cancel and await owned workers, save settings, record closure, then release the log. Settings-save and worker exceptions retain their context. The opening record includes logical window size, render scale and detected screen count.

## Retention and privacy

| Storage | Policy |
| --- | --- |
| UI history | Latest 2,000 entries; message/module strings limited to 2,048/1,024 characters plus a truncation marker |
| Error/warning counts | Counts of retained history; filtering does not change them |
| Desktop files | `%LOCALAPPDATA%/PS5BackportStudio/logs/desktop/` on Windows |
| CLI files | `%LOCALAPPDATA%/PS5BackportStudio/logs/cli/` on Windows |
| Event files per client | `events.jsonl`, then `events.1.jsonl` through `events.3.jsonl`, at most 1 MiB each |
| Writer lease | Empty `writer.lock`, exclusively held until sink disposal |

The application-owned event budget is 4 MiB per client, 8 MiB when both clients have populated all generations. Rotation discards the oldest generation before moving the others. Existing unrelated files are not removed. Records are complete JSON lines; embedded newlines are escaped. File messages are limited to 2,048 characters and exception context to 4,096, with smaller limits for identifiers.

Clear empties the UI history and its counts; it leaves disk history intact. Copy exports only the visible UI rows. There is no automatic log upload or remote telemetry. Records can contain private game names, absolute paths and exception messages; review them before sharing. No game binary payloads or settings snapshots are deliberately logged. Logs inherit the user's directory permissions; they are not encrypted or automatically redacted.

Writes are synchronous, serialized and close the event stream after each record. Useful records survive ordinary window closure without a background flush queue. This is not a power-loss or forced-process-termination durability guarantee. Storage/link/access failures disable that sink and surface a warning without failing the backport or altering its exit state. A competing second instance of the same client continues with a warning and without file logging; desktop and CLI have separate leases. Caller-provided invalid log paths are outside the application's normal fixed-directory flow.

## Filtering measurements and tradeoffs

The same headless harness displayed 10,000 synthetic import rows in the actual MainWindow, then applied full-list, empty-list and single-row queries. Before measurements used preserved Phase 5 Release assemblies; after measurements used Phase 6 Release assemblies. Records are local in `work/phase6/filter-before.json` and `filter-after.json`.

| Full-list filter update | Before | After |
| --- | --- | --- |
| Collection notifications | 10,001 | 1 |
| UI-thread allocated bytes | 2,722,048–2,722,120 | 82,104–82,240 |
| Observed elapsed milliseconds | 16.02–17.33 | 0.71–1.51 |

Import and log filtering now replace the visible collection in one batch. An import remains selected if still visible; otherwise its inspector clears. Name/library searches remain case-insensitive, NID searches remain case-sensitive, and queries still trim whitespace. No debounce timer or background filtering was added: the measured batching benefit did not justify asynchronous ordering complexity.

These are five updates in one process per version, including dispatcher yields, not a statistical benchmark or a native frame-rate/SLA claim. Current-thread allocations omit other threads and native/GPU allocations. Row/query inputs and notification counts are controlled; process scheduling and warm-up affect timings. Smaller queries have much smaller differences. The required collection reset can still cause layout work; the interaction tests check actual table selection after reset. The synchronous file sink also adds small I/O overhead; no throughput improvement is attributed to logging.

## Automated evidence

`pwsh -NoProfile -File scripts/verify.ps1 -Lane All` passed locked restore and **357 tests in each Debug/Release configuration**, with zero warnings, errors, failures or skips: 246 public and 111 private cases. Records: `work/verification/20261010-024610-a1fb4e8e72c34cf3aa7826bdb9f51e65`.

Seven diagnostics regressions cover rotation/newest-record retention, competing writers and lease release, file failure without operation failure, run/stage/module/error correlation, pre-cancellation, escaped/bounded error strings surviving UI Clear, and 2,400-entry UI eviction/counts/filter batching.

Five isolated headless scenarios use the real MainWindow bindings and the production close policy: busy command availability and keyboard cancellation; close while analyzing and cleanup; search input/table selection/inspector/accessibility name; rejection of stale analysis without output; settings-save failure preserved after closure. The parent enforces a 30-second child timeout and kills only its own worker tree if necessary.

`verification/UiProfile` is a required verification project in the solution, referenced by the test project without importing its assembly. This ensures Debug/Release workers are built in the matching configuration. It adds no package version or test framework; it uses the existing Avalonia.Headless package and [Avalonia's custom-framework HeadlessUnitTestSession approach](https://docs.avaloniaui.net/docs/testing/setting-up-the-headless-platform). The isolated process exits after assertions/measurements because headless renderer/session disposal blocked in the local profiling experiment. Production desktop shutdown is tested separately; worker process exit is not evidence that headless renderer disposal is reliable.

Existing private byte/report golden comparisons pass unchanged. Rules, goldens and private binary fixtures were not edited for Phase 6. `git diff --check` passes.

## Native desktop observations and limits

A separate Release application copy under `work/phase6/native/` was opened on Windows so desktop checks would not lock build outputs. The observed 1280 × 820 logical window at render scale 1 (100%) rendered the header, navigation, result tabs and log correctly. The Windows accessibility tree exposes readable navigation names, command labels, target firmware and retention descriptions. The native folder picker opened with its title, folder edit, selection and cancellation controls. Completed dry-run results and structured analysis/shutdown events were observed; records remained readable after the windows closed. Two screens were detected.

Initial interaction attempts were interrupted by external input and closure; the user confirmed closing those windows. Coordinated checks then verified Tab from the Imports tab into the named search edit, native text input, Escape cancellation of the folder picker with controls restored, and continued Tab navigation. After picker cancellation, focus is reported on the window and the next Tab goes to Settings in the navigation rail; focus does not automatically return to the invoking button. This is a recorded keyboard behavior, not a claim of complete screen-reader accessibility certification.

Normal and maximized layouts were inspected. The window moved to the second display and maximized correctly there: captured work areas were 1920 × 1032 at origin (0, 0) and 2560 × 1392 at origin (1920, 0). It was returned to its original normal geometry before closure. Native Analyze disabled Open/Analyze/Cook and exposed Cancel. The dry run completed before the subsequent close click, so this proves native busy bindings and clean idle closure, not native close-while-busy. The headless worker separately verifies close-while-busy and awaited cancellation.

The UI test copy finished closing, and recent events were copied to ignored `work/phase6/native-events.jsonl` as local evidence. No Cook was invoked by these checks. The resize drag did not change window dimensions, so minimum-size layout is not certified. No higher/mixed-DPI transition was verified, and no system display settings were changed to manufacture coverage. Exact native allocation and GPU memory attribution from the Phase 5 follow-up remains unmeasured; aggregate process counters cannot establish that breakdown.

Follow-up coverage: minimum-size layout; native close during a sufficiently long Analyze; 125%/150% and mixed-DPI display transitions on a suitable setup; dedicated screen-reader review if supported accessibility is to be advertised; exact GPU/native allocation profiling if a concrete native-memory issue arises. Headless results do not substitute for those guarantees.
