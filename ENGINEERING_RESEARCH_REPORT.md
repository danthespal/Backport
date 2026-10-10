# PS5 Backport Studio — engineering research and architecture audit

Date: 10 October 2026. Scope: the current working tree of Core, CLI, Avalonia App, tests, project configuration, and engineering documentation.

## Recommendation

Keep .NET, Avalonia, CommunityToolkit.Mvvm, and the shared C# engine. The best route to professional quality is stronger guarantees around output, inputs, evidence, and repeatable verification. A framework rewrite would cost much more than the improvements it would deliver here.

The first priorities are transactional output, validation of knowledge data, reliable checks without private fixtures, and performance measurement. The most promising additional engine is Iced for instruction-aware analysis, initially as a diagnostic comparison rather than a replacement for the existing patcher.

No source code, dependencies, settings, binary assets, or existing audit files were changed for this report. This report is the only workspace file added. Existing uncommitted changes were present at the start and remain untouched.

## Evidence and limitations

This is a source-based architecture review plus research using primary documentation. It is not a new exhaustive bug audit, benchmark, vulnerability scan, or console certification. I did not rerun builds/tests, launch the application, patch a game, or install tools. The existing AUDIT.md reports 201 passing tests and clean Debug/Release builds; those are prior results, not independently reproduced results of this review.

The supplied OriathHub guidance describes another product. The actual workspace contains PS5 Backport Studio. Recommendations respect its existing choices: private desktop tool, one C# engine, no console integration, no installer/release platform, and no game/system binaries in Git.

## What is already worth keeping

- **One core for GUI and CLI.** Both reference Ps5Backport.Core, reducing duplicated patching behavior. Core has no third-party PackageReferences in its project file.
- **A suitable desktop stack.** Avalonia and CommunityToolkit.Mvvm already provide UI infrastructure and generated observable/command behavior. Replacing them would introduce migration risk.
- **Strict compilation.** Nullable analysis, warnings as errors, and a shared analysis level are enabled in Directory.Build.props.
- **Meaningful compatibility tests.** Golden outputs, signing/unwrap comparisons, malformed-input cases, and whole-game parity tests provide protection for behavior that ordinary unit tests can miss.
- **Useful safety primitives.** DryRun, cancellation checkpoints, normalized source/output checks, CRC verification, manifest hashes, and unique temporary-file writing already exist.
- **Evidence-aware rules.** Knowledge distinguishes proven fixes, derived behavior, candidates, source notes, and firmware references. Preserve that distinction.

These form a good foundation. The proposals below extend it rather than replace it.

## Ranked proposals: benefits, costs, and tradeoffs

Effort is a rough engineering estimate including focused verification, not a delivery commitment: S = roughly 0.5–2 days; M = 3–7 days; L = 1–3 weeks. Console testing and unexpected compatibility work can extend these estimates.

| Priority | Proposal | Pros | Cons / costs | Effort |
|---|---|---|---|---|
| 1 | Stage a complete overlay before publishing it | Prevents failed/cancelled runs from appearing complete; avoids stale files when each run gets a fresh directory | Extra disk space; recovery and overwrite policy must be explicit | M–L |
| 1 | Validate all knowledge data before work starts | Clear errors for malformed rules; rejects unsafe file paths and inconsistent rules early | Schema/version maintenance; semantic checks still need custom code | M |
| 1 | Repeatable build/test/dependency checks | Prevents regressions; clean machines can verify the public/synthetic portion of the engine | Some current tests require private fixtures; hosted runners cannot receive those binaries | M |
| 2 | FsCheck plus targeted parser fuzzing | Finds combinations missed by hand-written tests; preserves minimized failing cases | Requires good generators and resource limits; fuzzing is not proof of correctness | M |
| 2 | Profiling plus BenchmarkDotNet | Identifies real bottlenecks; measures allocation and speed regressions | Time to build representative workloads; microbenchmarks alone can mislead | S–M |
| 2 | Extract planning, transformation, and output responsibilities | Easier testing and change review; enables staged output and memory improvements | Refactoring can disturb ordering and golden compatibility | M–L |
| 2 | Structured local diagnostics and bounded UI logs | Explains failures after the window closes; prevents log accumulation | Retention/redaction policy and file sink required | S–M |
| 2 | Immutable run snapshots and finer cancellation | Reproducible operations; clearer lifecycle and cancellation behavior | More explicit state handling; cancellation during a commit needs special treatment | M |
| 3 | Two-pass module processing and targeted caches | Lower peak memory; less repeated parsing and firmware loading | Additional reads or scratch files; cache invalidation risks | M–L |
| 3 | Iced instruction decoder for analysis/validation | Better instruction interpretation than scanning every byte for opcode patterns | New dependency; executable segments can contain data; requires corpus comparison | M for evaluation, L for integration |
| 3 | Headless interaction tests and cheaper filtering | Protects busy/cancel/search behavior; reduces collection churn | Headless tests do not replace DPI, accessibility, or native-window testing | S–M |
| 4 | System.CommandLine for the CLI | Centralized syntax, validation, and help; less parser maintenance | Migration may change output, option behavior, and exit codes | M |
| Conditional | SQLite for a growing catalog/history | Indexed queries and transactions for metadata | Migrations and another storage system; does not transact external library files | L |

## 1. Make output publication a deliberate operation

**Observed:** Pipeline.Backport creates the final directory and may write param.json before module processing finishes. PatchModule writes directly into that directory. Module errors are recorded and processing continues. Later cancellation or I/O failure can leave a partial overlay; reuse can leave artifacts from earlier options. See [Pipeline.cs](E:/PS5_TOOLS/Backport/src/Ps5Backport.Core/Pipeline.cs:128) and its [module writer](E:/PS5_TOOLS/Backport/src/Ps5Backport.Core/Pipeline.cs:375).

FakelibStore.Add copies/overwrites libraries as it processes the batch, then saves the manifest at the end. A later invalid file or failed write can leave files and manifest inconsistent. See [FakelibStore.cs](E:/PS5_TOOLS/Backport/src/Ps5Backport.Core/FakelibStore.cs:104).

**Proposed method:** validate first; build into a unique sibling staging directory; verify expected files and hashes; write a completion manifest; publish only after the policy permits it. Prefer a new destination per successful run initially. Replacing an existing nonempty directory requires a recovery protocol, rather than pretending a sequence of renames is one atomic transaction.

Define whether any module error blocks publication. If partial results remain useful, mark them explicitly incomplete and keep them in staging. Cancellation before publication should leave the previously completed output intact. Once publication begins, finish or recover that short critical section.

The existing [WriteAtomic](E:/PS5_TOOLS/Backport/src/Ps5Backport.Core/Self.cs:337) is useful for individual files but does not provide a whole-folder transaction or a demonstrated power-loss guarantee. Stronger durability needs an explicit flush/recovery design tested on the supported filesystem.

**Verification:** inject failures after each write/copy/commit step; cancel at stage boundaries; reuse an output path with different options; simulate interrupted publication. Previously completed output must remain usable, and incomplete output must never claim completion.

## 2. Treat rule data as an input with a contract

**Observed:** Knowledge parses JSON fields, hex strings, rule kinds, and file paths directly. Some malformed values will fail only when their rule is exercised. Data-file pattern paths are combined with source/output roots. Paths.ValidateOverlay checks normalized lexical paths, not physical filesystem identity. See [Knowledge.cs](E:/PS5_TOOLS/Backport/src/Ps5Backport.Core/Knowledge.cs) and [Paths.cs](E:/PS5_TOOLS/Backport/src/Ps5Backport.Core/Paths.cs:10).

Use versioned JSON schemas for structural validation, followed by domain checks: recognized rule kinds, valid references, permitted firmware values, nonempty find patterns, equal replacement lengths where required, valid instruction descriptions, and conflict handling. JSON Schema describes structure and constraints; it does not replace these semantic checks. [JSON Schema documentation](https://json-schema.org/overview/what-is-jsonschema).

Require rule file paths to remain within their intended root: reject rooted paths, parent traversal, and unexpected separators. Define a policy for junctions, symlinks, hard-linked files, and source/output aliases. Lexical containment alone cannot establish that writes leave the physical source untouched.

Capture a run fingerprint: engine version/revision when available, effective options, selected firmware reference and its exact/lower-bound status, knowledge-file hashes, fakelib manifest/content hashes, and relevant source hashes. Keep reports deterministic; account for the cost of hashing large inputs. Hashes show identity/integrity, not that a rule or binary is trustworthy.

**Verification:** malformed and internally inconsistent JSON produces a useful error before output is written; escaping paths are refused; changing a rule or selected library changes the run fingerprint.

## 3. Strengthen tests that run on a clean machine

**Observed:** Repo.Need skips tests when samples are absent, and .original/ plus fakelib binaries are excluded from Git. A clean checkout can therefore pass while exercising less integration behavior. No tracked CI workflow or NuGet lock file was found in the reviewed repository. See [Repo.cs](E:/PS5_TOOLS/Backport/src/Ps5Backport.Tests/Repo.cs:22).

Create two visible verification lanes: mandatory synthetic/public-fixture tests, and private-fixture integration tests on a controlled local machine or suitable private runner. Report expected and actual skips. In the private lane, missing required fixtures should fail setup rather than quietly reduce coverage. Do not upload game/system binaries to hosted CI.

Add locked restores, Debug/Release builds, tests, and explicit dependency auditing including transitive packages. Existing direct versions and global.json already help; the SDK's latestFeature roll-forward permits different feature bands, so record the SDK used for verification. NuGet supports checked-in lock files and locked mode; auditing also covers transitive dependencies. [Lock files](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files), [NuGet audit](https://learn.microsoft.com/nuget/concepts/auditing-packages).

Add FsCheck properties for BPS range behavior, firmware encode/decode, synthetic SELF/ELF round trips, and malformed offsets. Use bounded mutation fuzzing for binary parsers; preserve failures as regressions. FsCheck generates cases and shrinks counterexamples. [FsCheck](https://fscheck.github.io/FsCheck/QuickStart.html).

Later, run Stryker.NET on critical arithmetic/range branches to see whether tests detect deliberately altered logic. Mutation testing is slower and should be selective; compatibility with this project's runner/packages must be evaluated before adoption. [Stryker.NET](https://github.com/stryker-mutator/stryker-net).

**One specific correctness question:** PatchModule derives external providers with allProvided.Except(elf.Exports()). A set loses provider ownership, so if another module exports the same NID, that external provider is also removed. Add a synthetic duplicate-export case and confirm intended resolution semantics before changing it. This is a source-derived candidate, not a reproduced failure.

## 4. Measure performance, then change memory ownership

**Observed:** Backport retains every decoded SceElf in its loaded list before patching. SELF inputs and signing can introduce additional byte arrays. FirmwareDb.LoadAll is called from several flows. SceElf.Imports reparses symbol/relocation information on each call. These are plausible allocation/read costs, not measured bottlenecks.

First collect phase timing, total/peak working set, managed heap and large-object allocations, GC pauses, and cancellation latency on representative workloads. Use dotnet-counters/dotnet-trace for end-to-end behavior and BenchmarkDotNet for narrow parser/scan experiments. Native UI memory needs separate observation from managed heap size. [Microsoft profiling tools](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/profilers), [BenchmarkDotNet](https://benchmarkdotnet.org/articles/guides/how-it-works.html).

The strongest design candidate is two passes: discover/parse enough to retain compact provider metadata, then load/transform/release one module at a time. This changes peak memory from retaining all module buffers toward metadata plus the largest active module and its transformation buffers. It can add disk reads; scratch decoded files may help on some workloads. Inputs must be stable between passes, or changes must be detected.

Cache immutable firmware metadata within a run first. Cache parsed module metadata only with explicit invalidation after mutations. Blindly caching imports would be unsafe because patching changes symbols and classifications. Pooling and memory mapping should follow evidence, not precede it.

Parallelism comes later: bounded independent work, deterministic result order, and a memory budget. Unrestricted parallel loading/patching can multiply memory and I/O contention. Channels can provide bounded queues/backpressure if a worker pipeline becomes justified. [System.Threading.Channels](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels).

**Verification:** byte/report compatibility remains unchanged; measure cold/warm runs on the same inputs and machine; peak memory decreases without unacceptable I/O or cancellation regressions. No percentage improvement is claimed yet.

## 5. Simplify orchestration without building a framework

Backport is about 244 lines; PatchModule takes 13 parameters. Source inspection shows discovery, policy, parsing, mutation, writing, and reporting mixed together. These are useful seams for gradual extraction, not evidence that the entire architecture is broken.

Introduce a run snapshot, compact module metadata, an explicit planned result, transformation, and an output writer. Start with ordinary records/classes and explicit constructor parameters. Add interfaces only where multiple implementations or fault injection justify them. Microsoft.Extensions.DependencyInjection is available if composition later grows, but a container is optional. [DI documentation](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/overview).

Keep the current dry-run behavior as the compatibility baseline. A cheap Analyze-then-Apply plan needs source/config fingerprints and revalidation so changed files cannot silently invalidate approved analysis. Preserve fix order, report schemas, and exact/lower-bound firmware behavior.

Cancellation currently occurs mainly between module/stage operations. Large scans and transformations need safe internal checkpoints if profiling shows poor latency. UI operation lifecycle should make success, failure, and cancellation distinct; Run currently sets Progress to 100 in its finally block even after cancellation/failure. See [MainViewModel.Run](E:/PS5_TOOLS/Backport/src/Ps5Backport.App/ViewModels/MainViewModel.cs:359).

## 6. Add diagnostics and polish to the existing UI

LogViewModel keeps an unbounded list and observable collection. ApplyImportFilter clears and repopulates the displayed collection. Virtualized rendering does not remove that collection-update work. See [LogViewModel.cs](E:/PS5_TOOLS/Backport/src/Ps5Backport.App/ViewModels/LogViewModel.cs:21) and [filtering](E:/PS5_TOOLS/Backport/src/Ps5Backport.App/ViewModels/MainViewModel.cs:482).

Keep a bounded visible log/history, batch updates, and debounce search only if typing/filter profiling justifies it. Preserve current selection where possible. Add local structured events with run ID, module, stage, error category, duration, and exception details; the UI can show concise messages while local diagnostics retain context. ILogger with source-generated events is a reasonable standard option; a file provider still needs selection or implementation. [Microsoft logging](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging/source-generation).

This proposes local diagnostics, not remote telemetry or a full problem-report platform previously excluded from scope. Exclude binary contents and define retention; avoid exposing unnecessary private absolute paths when reports are shared.

Extend the existing headless capability with interaction tests for command availability, search/selection, cancellation, and close-while-busy. Avalonia documents headless xUnit integration; verify package compatibility with the project's xUnit v3 stack before adding it. Native focus, scaling, accessibility, and multiple monitors still need desktop checks. [Avalonia headless tests](https://docs.avaloniaui.net/docs/testing/headless-xunit).

## 7. Evaluate new engines selectively

**Iced: worth a small experiment.** CodeReferences scans executable segments at every byte for selected opcode forms. Bytes inside other instructions or embedded data can resemble those forms. Iced provides a managed x86 decoder, encoder, and instruction metadata. Use it first to compare reference counts and validate generated instruction sequences, leaving patch output unchanged. [Iced C# documentation](https://github.com/icedland/iced/blob/master/src/csharp/Intel/README.md).

A decoder does not automatically identify code versus data or discover all reachable instructions. Linear decoding can also misinterpret embedded data. Adoption needs adversarial synthetic cases and comparison against known disassembly. Correct decoding does not establish console ABI correctness or that a stub preserves game behavior. Treat improved accuracy as a hypothesis until validated; do not promise faster scans.

**System.CommandLine: useful when CLI maintenance grows.** The current parser is small; migration is less urgent than output safety. The library provides parsing, validation, and help infrastructure. Preserve command aliases, repeated options, error text/exit codes, and golden outputs during evaluation. [System.CommandLine](https://learn.microsoft.com/en-us/dotnet/standard/commandline/).

**SQLite: defer.** The current firmware/rule catalog is manageable as inspectable JSON. SQLite becomes attractive for substantial query-heavy history or concurrent metadata edits. Its transactions apply to database contents, not external fakelib files, so it does not replace staging/recovery. [SQLite atomic commit](https://www.sqlite.org/atomiccommit.html).

## Changes I would not recommend now

- A Rust/C++ engine rewrite: large parity cost and new interop/build complexity without a measured need.
- Replacing Avalonia with another UI framework: migration work with no demonstrated solution to the current engineering gaps.
- A generic plugin system, rules DSL, or workflow platform: more extension/security/versioning machinery than this private tool currently needs.
- MediatR/CQRS/event sourcing or microservices: disproportionate infrastructure for a local desktop pipeline.
- GPU scanning, unrestricted parallelism, or widespread unsafe/memory-mapped code: evaluate only after profiling proves the problem and a bounded prototype helps.
- AOT/trimming as the first performance project: startup/deployment optimization does not address retained module buffers or incomplete output handling.
- Replacing custom PS5 SELF/SCE ELF parsing with a generic ELF library wholesale: format-specific behavior and golden compatibility need proof. A second parser can be useful as a test oracle where formats overlap.

## Suggested implementation sequence, if approved later

1. **Safety and evidence:** validation contracts, run fingerprints, staged output, store recovery, failure injection. Verify source preservation and distinguish completed/incomplete output.
2. **Repeatable verification:** clean-checkout test lane, private-fixture lane, locked restores, dependency audit, parser properties/fuzzing. Verify meaningful coverage and explicit skips.
3. **Measured performance:** baseline profiling, incremental orchestration extraction, two-pass processing, narrowly scoped caches. Verify compatibility plus end-to-end memory/time measurements.
4. **Selective polish and tools:** bounded diagnostics, lifecycle/interaction tests, filtering improvements, Iced comparison; CLI migration only if justified. Verify behavior rather than library adoption alone.

The expected result is software that is easier to reason about, safer to interrupt, easier to diagnose, and demonstrably more efficient. Professional quality here comes from explicit guarantees and reproducible evidence, while keeping the technology stack small.
