# Phase 5 — measured performance and memory review

Completed 10 October 2026. Two focused engine changes were accepted: parse firmware metadata once per run, and sign ELF segments directly from the caller's existing buffer. The engine's ordering, transformation rules, publication transaction and legacy reports remain compatible. Larger ownership/concurrency proposals were evaluated and left out of production for the reasons below.

## Workloads and measurement method

All comparisons ran locally on Windows 11 10.0.26300.9550, AMD Ryzen 9 7950X, 32 logical processors, SDK 10.0.401 and .NET 10.0.12, Release/x64. The Phase 4 engine DLL was preserved before editing; the same final measurement harness ran against both engines. Engine DLL SHA-256:

- Before: `2b02ddb4051641ce40542e0827d78d72e1117a0211c46c3b443fd4a673dbf2b7`.
- After: `d3d15971e849ebf8866f8d2285658c0087a204b837239b47974edae2beea9746`.

| Workload | Input and options |
| --- | --- |
| Public small | Four synthetic ELF modules, approximately 2 MiB each; SDK 9 → FW 4; Analyze and Cook |
| Public large | Twelve synthetic ELF modules, approximately 16 MiB each; SDK 9 → FW 4; Analyze and Cook |
| Private small | Existing PPSA01317 default fixture; FW 4 with the existing fakelib store; Analyze |
| Private large | Existing PPSA28183 default fixture; FW 4.50, existing store, `force_pro` and `gpu_scratch`; Analyze and Cook |

The public reference database is synthetic. Private cases use the repository firmware databases and rules. Each pair uses identical inputs/options and a separate output folder. Module input hashes, normalized JSON/text report hashes and all cooked payload hashes are compared; every final pair matched. Publication manifests/evidence and output-path spellings are excluded from payload/report comparisons. Existing golden tests independently check legacy behavior.

Three iterations per engine/case were recorded: first is **process cold**, the next two **process warm**. OS cache, antivirus, storage and background scheduling were not controlled; no disk-cold or statistically robust speed claim is made. Full GC occurs before each iteration, not inside the operation. Comparisons ran sequentially after verification finished. Initial exploratory runs that overlapped tests are not used in the tables.

The harness records wall time and allocated bytes at progress boundaries, total managed allocation through `GC.GetTotalAllocatedBytes`, generation collection counts, runtime EventCounters and 5-ms Windows process-memory samples. The sampler stops before report serialization/output hashing, and payload hashes stream from disk. This avoids attributing verification buffers to engine memory. EventCounters have a one-second interval and cannot reliably characterize subsecond cases. Progress boundaries are orchestration intervals: the last loading interval includes provider/VR metadata; the last patching interval includes data/app0 handling and final evidence checks. They are not exclusive function profiles.

`PeakManagedBytes` includes uncollected garbage. LOH is the highest recorded **post-GC** size, not cumulative LOH allocation or an exact instantaneous peak. Working set and private bytes include native memory, code, runtime and observer overhead; subtracting managed bytes would not provide a valid native-allocation measurement. Samples can miss short spikes. Earlier attribution of high allocation to `Process.Refresh()` was not substantiated; direct Windows counters still showed the high footprint. Final records use the corrected sampler.

## Whole-operation results

Warm columns are the mean of iterations 1 and 2. MiB = 1,048,576 bytes. Timing precision is rounded to milliseconds to avoid implying stronger accuracy than these small samples support.

| Case | Process-cold ms before → after | Warm ms before → after | Warm allocated MiB before → after | Warm peak working set MiB before → after |
| --- | ---: | ---: | ---: | ---: |
| Public small Analyze | 106 → 106 | 27 → 84 | 8.7 → 8.7 | 46 → 46 |
| Public small Cook | 634 → 708 | 538 → 493 | 26.0 → 17.9 | 68 → 59 |
| Public large Analyze | 524 → 552 | 442 → 434 | 192.9 → 192.9 | 232 → 232 |
| Public large Cook | 3,522 → 3,565 | 3,741 → 3,619 | 579.6 → 387.6 | 628 → 436 |
| Private small Analyze | 1,703 → 1,519 | 1,417 → 1,340 | 1,078.7 → 970.5 | 666 → 704 |
| Private large Analyze | 2,019 → 1,760 | 1,924 → 1,622 | 1,781.3 → 1,672.7 | 1,572 → 1,418 |
| Private large Cook | 6,874 → 6,683 | 6,425 → 6,247 | 2,398.0 → 1,974.1 | 2,198 → 1,865 |

Public small Analyze had an after-run scheduling outlier (26 and 142 ms). A separately retained five-iteration check measured cold 110.7 → 110.7 ms and warm ranges 24.0–26.2 → 24.4–26.9 ms. No repeatable small-analysis slowdown was found; the initial outlier remains visible in the table and raw records.

The accepted changes reduced allocation by about **33%** in public large Cook and **18%** in private large Cook. Private analyses saved approximately **109 MiB** per run. They do not universally reduce peak working set: private small Analyze had fewer collections but higher observed managed/LOH/working-set peaks. GC scheduling and retained garbage matter; allocation savings must not be advertised as a universal RAM reduction. Whole-run timings suggest benefits in some private cases, but the evidence supports memory/allocation claims more strongly than speed percentages.

| Case | Warm peak private MiB before → after | Warm peak managed MiB before → after | Last warm post-GC LOH MiB before → after | Last warm GC counts Gen0/1/2 before → after |
| --- | ---: | ---: | ---: | --- |
| Public small Analyze | 20 → 20 | 9 → 9 | 6 → 8 | 1/1/1 → 1/1/1 |
| Public small Cook | 39 → 31 | 26 → 18 | 6 → 8 | 1/1/1 → 1/1/1 |
| Public large Analyze | 208 → 208 | 194 → 194 | 176 → 176 | 2/2/2 → 2/2/2 |
| Public large Cook | 607 → 412 | 579 → 387 | 176 → 176 | 2/2/2 → 2/2/2 |
| Private small Analyze | 649 → 686 | 572 → 648 | 466 → 564 | 26/21/3 → 20/16/3 |
| Private large Analyze | 1,600 → 1,475 | 1,542 → 1,390 | 1,435 → 1,246 | 23/19/4 → 15/12/2 |
| Private large Cook | 2,223 → 1,890 | 2,157 → 1,789 | 2,051 → 1,738 | 23/19/4 → 15/12/1 |

## Accepted changes: advantages and costs

**One firmware parse per run.** Pipeline now loads databases once and shares that run-owned list between reference selection and introduction lookups. Exact/lower-bound selection and genstub fallback are preserved. It is read-only within the pipeline, with no process-global cache or filesystem timestamp heuristic. Existing evidence handles/revalidation still detect changed inputs. New tests prove selection and that the internal overload uses its captured metadata while a fresh public load observes a changed file.

Advantages: removes duplicate JSON parsing/set construction, saves approximately 109 MiB on the real metadata corpus, and cannot reuse a database from a previous run. Cost: the run still retains the databases and lazily built NID sets; this does not solve all metadata allocation or make the public mutable `FirmwareExports` API immutable.

**No segment copies during signing.** `Self.Sign` uses read-only memory slices of the existing ELF for segment and version bytes, then copies them into the final output once. The independent final output and digest/layout behavior are preserved; no pooled buffer escapes to callers.

Advantages: removes approximately one payload-sized allocation per signed ELF, with unchanged payload/report hashes and private goldens. A synthetic 16-MiB regression checks the input stays unchanged, the round trip is correct, and allocation is bounded by the returned output plus 1 MiB of metadata. Cost: input and final output buffers still coexist during signing, and file I/O/signing remain synchronous cancellation boundaries. Callers must continue to keep input stable during this synchronous operation.

## Focused benchmark

Only the confirmed duplicate firmware parse received a BenchmarkDotNet benchmark. The optional project is outside the application solution and its dependencies are locked. [BenchmarkDotNet's MemoryDiagnoser](https://benchmarkdotnet.org/articles/configs/diagnosers.html) supplies managed allocation/GC measurements; [the pinned package](https://www.nuget.org/packages/BenchmarkDotNet/0.15.8) is 0.15.8.

| Firmware metadata operation | Mean | Managed allocation |
| --- | ---: | ---: |
| Duplicate loads | 236.2 ms | 218.52 MiB |
| One run-owned load | 124.9 ms | 109.93 MiB |

ShortRun used one process launch, three warmups and three measured iterations. The 99.9% timing intervals are very wide (174.8/280.5-ms error margins); the apparent 47% microbenchmark timing improvement is **not** a whole-engine or statistically precise speed guarantee. Allocation approximately halved. BenchmarkDotNet temporarily selected its high-performance power plan and reported restoration afterward. The initial benchmark project-name discovery failure was corrected by matching the csproj and assembly names; the final run executed both benchmarks successfully.

## Two-pass loading, metadata caches, pooling and concurrency

The isolated loading experiment compares retaining decoded modules against rereading one module at a time. Both collect the same exports and parse imports; the second-pass version hashes each read and refuses a changed input. It uses the public large corpus and does not transform, publish, resolve selected store variants or implement the production app0 logic.

| Loading-only experiment | Elapsed | Allocated | Decoded ownership required by the strategy |
| --- | ---: | ---: | --- |
| Retain modules | 123.7 ms | 192.1 MiB | Approximately 192 MiB across all modules |
| Two passes | 234.7 ms | 384.1 MiB | Approximately 16 MiB for the current decoded module, plus compact metadata |

These are one exploratory run, not a controlled full-pipeline benchmark. Ownership values describe references required by the strategy, **not** measured heap peaks: garbage can remain until collection, and encoded SELF buffers/signing output add costs. The experiment proves the extra-read/allocation tradeoff on this corpus; it does not prove production equivalence.

Decision: **defer production two-pass loading**. It doubles decoding allocation/read traffic in this experiment and would require compact post-transformation state to preserve provider ownership, partial-failure behavior, final imported NIDs, added-library exports and `/app0/` references. Source hashing between passes is necessary but insufficient for those semantic guarantees. Retaining modules remains faster/simpler for this workload; future low-memory support needs an explicit budget and full pipeline prototype before adoption.

Decision: **do not add a module-symbol cache yet**. Public `SceElf.Data` is mutable. Renaming imports, defining symbols and arbitrary byte patterns would require cache invalidation, including partial failures and external mutation. Current code reparses metadata after changes, so invalidation cannot be missed. A single large fixture's symbol parse was approximately 8.9 MiB; repeated parsing is an identified follow-up, but this phase did not establish a safe cache that warrants changing the API/ownership contract. No cache was introduced whose validity depends on undocumented assumptions.

Decision: **defer ArrayPool, scratch ELF files, Channels and parallel transforms**. The signer removes redundant copies without rental lifetimes. Pooling very large arrays could retain substantial memory; scratch files add cleanup/recovery/disk obligations; parallel workers multiply decoded/signing buffers and complicate deterministic ordering and cancellation. No measured throughput bottleneck justified those changes. The retained production policy is one serial transformation/signing worker, with all decoded provider modules still retained. There is no new arbitrary input limit or claimed hard working-set budget. The measured private Cook still reaches roughly 1.8–1.9 GiB working set; low-memory operation remains a limitation, not a completed feature.

## Cancellation and UI memory

Cancellation requested at the beginning of loading the first 16-MiB synthetic module was observed after 3.34–5.99 ms before and 3.71–5.41 ms after (three observations each). The load completes before the next checkpoint. These are local observations, not an SLA; bigger SELF reads/unwraps, signing and protected publication can take longer. The Phase 4 bounded-scan cancellation tests remain green.

Three separate headless Avalonia/Skia processes loaded/analyzed the public small game with the normal repository metadata/store, then rendered a 1280×820 frame. Process time was 2,185–2,208 → 1,755–1,811 ms; peak total working set was approximately 367–369 → 361–365 MiB, and sampled private bytes approximately 319–321 → 313–321 MiB. A final frame was inspected and showed completed analysis without errors. Timestamped logs make image hashes differ; they are evidence records, not pixel-equality assertions.

These process counters include managed and native Skia/runtime memory together. They do **not** isolate native allocations, GPU memory, native desktop compositor effects, multi-monitor behavior or leaks over repeated operations in one UI process. Exact native allocation profiling and native desktop interaction remain Phase 6 follow-ups; subtracting the separate engine's managed numbers would be misleading.

## Reproduction, records and verification

```powershell
# Public synthetic measurements; restores the optional harness in locked mode.
pwsh -NoProfile -File scripts/measure-performance.ps1

# This session's exact paired comparison; baseline artifacts remain local/ignored.
pwsh -NoProfile -File scripts/measure-performance.ps1 -IncludePrivate `
  -BaselineDirectory work/phase5/baseline -InputRoot work/phase5/inputs `
  -ReportDirectory work/phase5/final

# Optional focused benchmark, outside the solution.
dotnet restore verification/Benchmarks/Ps5Backport.Benchmarks.csproj --locked-mode
dotnet run --project verification/Benchmarks/Ps5Backport.Benchmarks.csproj -c Release --no-restore -- `
  --filter '*FirmwareMetadata*' --artifacts work/phase5/benchmarks

# After building the Release app, sample a headless UI process.
pwsh -NoProfile -File scripts/measure-ui-memory.ps1 -Game work/phase5/inputs/small
```

Comparisons require the **same harness** beside the preserved old engine and its runtime files; do not overwrite the baseline Core DLL when updating the harness. Existing input roots are reused, not silently regenerated. Cooking produces actual local payloads and retained transaction records; budget disk space for repeated runs. Raw records can contain private paths, module names and report details. They remain under ignored `work/`; no upload or hosted performance service is enabled. Baseline artifacts are machine-local, not a substitute for a tagged/reproducible release baseline.

Evidence locations:

- Final paired JSON, phase intervals, runtime counters, cancellation and loading experiment: `work/phase5/final/`.
- Supplemental small Analyze check: `work/phase5/final/supplement-{before,after}-small-analyze.json`.
- Focused benchmark: `work/phase5/benchmarks/results/FirmwareMetadataBenchmarks-report-github.md`.
- Headless UI frames/process records: `work/phase5/ui-before/` and `work/phase5/ui-after/`.
- Full verification: `work/verification/20261010-021831-33e89269039d464ab0860f7771c58575/`.

`scripts/verify.ps1 -Lane All` passed locked solution restore, Debug/Release builds with **zero warnings/errors**, and **345 tests per configuration**, zero failures/skips (234 public + 111 private). All original byte/report goldens passed unchanged. There are three new cases: the signer allocation/input/round-trip regression and two firmware-selection/captured-metadata cases. The optional measurement and benchmark projects also build without warnings. No rule, golden or private binary file was changed.

Phase 5 is complete as a measurement/optimization and adoption-decision phase. The deferred production memory-budget/cache/concurrency work and exact native-memory profiling are explicitly recorded above; they are not implemented guarantees.
