# Phase 4: operation lifecycle and pipeline boundaries

The shared engine now captures each request independently, identifies terminal outcomes explicitly, and binds a successful analysis to the inputs and settings that produced it. Transformation order and legacy byte/report formats remain unchanged. No new framework or runtime dependency was added.

## Ownership and states

`BackportOperation` is a one-shot owner of the captured request and cancellation source. Its transitions are `Pending → Running → Completed | Failed | Cancelled`. A second execution is refused. Exceptions retain their original types; module errors return a failed result with diagnostics. Cancellation/disposal follows `Cancel → await ExecuteAsync → Dispose`; disposal while running is refused.

`RunSnapshot` normalizes paths, detaches option/NID collections, and deeply copies the loaded rules, including nested instruction, stub and pattern bytes. The name database has no mutation API. The captured rules are private to the operation. UI settings can be edited while work runs without changing that operation.

`GameResult.State` and `GameResult.Analysis` are runtime metadata, excluded from the legacy text/JSON reports. Completed means the engine finished without module errors; unresolved imports may still remain. A successful analysis writes nothing. A successful cook additionally requires `Published == true`, which is set after transactional commit.

The UI reserves 100% for completed work. Failed and cancelled work finishes at 0%, and queued progress from a finished or superseded operation is ignored. Progress remains a stage/module indicator rather than an estimate of elapsed time.

## Analyze and Cook

Successful full analysis returns an in-memory `AnalysisReceipt`. Its SHA-256 fingerprint includes source identity, effective settings and paths, loaded names/rules, selected library source, and the relevant input file hashes/absence records. Modules, reference lists, knowledge documents, param metadata, option data files, library inventories and app0 files are guarded and rechecked. AMPR index presence is included because it affects warnings. Rules edited on disk require reloading the knowledge instance.

Cook repeats preflight and checks an existing receipt before acquiring the output transaction or creating staging files. Changed inputs/settings are refused with an instruction to analyze again. The UI retains the receipt for the current game; opening another game or starting another analysis clears it. Cooking directly without prior analysis remains supported.

**No transformed-byte cache was introduced.** Analyze still executes the complete dry-run transformation; Cook performs the complete transformation again against guarded current inputs. A receipt authorizes reuse of the analysis context only. Moving the output destination does not invalidate analysis. Receipts are process-local and are not a persistent cache, signature, or security boundary.

Analysis now hashes/guards inputs as cooking does. That adds I/O and temporarily prevents cooperative writers from replacing relevant files on Windows. Inventory changes are rejected. The memory and I/O costs should be measured in Phase 5 before adding persistent caches or a two-pass module engine.

## Pipeline boundaries

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

## Cancellation and closing

Directory/module discovery checks cancellation between files/directories. Pattern replacement scans windows of at most 1 MiB of candidate starts, retaining overlap needed to find boundary-spanning matches. Dense-match loops and code-reference scans check every 64 KiB of progress. Transformation also checks at safe module/fix/pattern boundaries. Existing overloads remain and forward to the cancellation-aware implementations.

AMPR index staging/traversal now accepts cancellation and uses the same pre-commit cancellation rule. Closing the window blocks new workers, requests cancellation of backport/index work, awaits their cleanup, and waits for already-owned firmware/store/tool workers. Conversion stops before the next file; an in-flight conversion finishes its atomic write. The window then saves settings and closes. It does not kill a worker mid-write.

Cancellation is cooperative. Synchronous reads, hashes, signing, individual filesystem calls and some per-fix/parser work can still delay it. Publication deliberately defers cancellation once the protected rename/commit sequence begins, as documented in `PUBLICATION_RECOVERY.md`. A publication that commits during a late cancel remains completed. The window can therefore remain open briefly while cleanup/commit or an existing page worker finishes. Native closing/picker behavior still needs the desktop interaction checks planned for Phase 6.

## Local measurements

Windows, .NET 10.0.12, Release, synthetic 64-MiB inputs; measurements were taken on this workspace's host. Baseline single runs: dense one-byte replacement **614.1 ms**, code-reference scan **73.4 ms**. After adding checks, three runs:

| Scan | Complete scan (ms) | Cancellation request to observed cancellation (ms) |
| --- | --- | --- |
| Dense replacement | 645.7, 607.7, 612.4 | 1.511, 0.173, 0.179 |
| Code references | 89.8, 80.3, 77.1 | 0.135, 0.069, 0.052 |

These justify checkpoints inside measurable uninterrupted scans; they do not establish a throughput improvement or a whole-operation cancellation SLA. The single-run baseline is insufficient for a statistical overhead comparison. Local harness/results are under `work/phase4/latency/`; no private binaries were used.

## Verification and remaining work

Synthetic regressions cover one-shot execution, completed/failed/cancelled outcomes, cancellation before start/during patching, running-disposal refusal, detached options/rule bytes, stale module/reference/metadata/settings/rules/names/library inputs, current-analysis cooking, legacy report shape, boundary matches/alignment, bounded scan cancellation, delayed UI progress, close while busy, and stale UI settings.

Final verification on 10 October 2026: `pwsh -NoProfile -File scripts/verify.ps1 -Lane All` passed **342 tests in each Debug/Release run**, with zero failures/skips/warnings. This includes 27 new Phase 4 cases, 231 public and 111 private cases overall, and existing private byte/report golden comparisons. `git diff --check` passed. Rules, goldens and private binaries remain unchanged. Local records: `work/verification/20261010-020320-70e2650e82ae4af8bfb628a0399f0b57`.

Phase 5 will measure allocation, working set, metadata hashing and representative whole-game throughput. Phase 6 will extend native/headless interaction coverage and diagnostics. None of this establishes console behavior beyond the existing fixtures.
