# Dependency review — 10 October 2026

Scope: the four application solution projects, their resolved direct/transitive NuGet graph, and the isolated mutation-test project's overlapping test dependencies. No application dependency was upgraded in this phase. FsCheck is the only newly adopted test library; Stryker is a pinned optional local tool.

Every project has a `packages.lock.json` with resolved versions and content hashes, including projects with only project references. `Directory.Build.props` enables lock generation; verification explicitly restores in locked mode. `global.json` pins SDK 10.0.401 instead of silently moving to a newer feature band. The observed runtime was 10.0.12; servicing runtime selection is recorded rather than presented as bit-identical environment reproducibility.

## Resolved graph and checks

| Area | Resolved graph | Direct packages |
| --- | --- | --- |
| Core / CLI | No external NuGet packages | Project references only |
| Desktop UI | 28 direct/transitive packages | Avalonia, Desktop, Themes.Fluent, Headless and HarfBuzz 12.1.3; CommunityToolkit.Mvvm 8.4.2 |
| Main tests | 55 direct/transitive packages, including UI dependencies | coverlet.collector 6.0.4; Microsoft.NET.Test.Sdk 17.14.1; xunit.runner.visualstudio 3.1.4; xunit.v3 4.0.1; FsCheck.Xunit.v3 3.4.0 |
| Mutation experiment | Public test subset only | xunit.v3 4.0.1; FsCheck.Xunit.v3 3.4.0; separate local dotnet-stryker 5.0.0 tool |

`dotnet list Ps5Backport.slnx package --include-transitive --vulnerable --format json` and the corresponding `--deprecated` query succeeded against the configured NuGet feeds and reported no flagged packages. This is a dated advisory-feed result, not proof that every managed/native component is free of defects. Bundled native libraries and SDK/tool internals require their own advisory review when upgrading or distributing.

The outdated-package query found available stable updates. Examples: Avalonia 12.1.4, xUnit 4.0.2, xunit.runner.visualstudio 4.0.1, Microsoft.NET.Test.Sdk 18.10.1 and coverlet.collector 10.1.0. These were recorded, not automatically adopted. Some transitive latest versions are major-version changes (SkiaSharp, HarfBuzzSharp, FSharp.Core); independently overriding a framework's native or language-runtime graph without a compatibility experiment is not justified by an outdated badge.

The pinned packages' local NuGet specifications declare MIT licenses for the UI/MVVM/Microsoft/coverage libraries, Apache-2.0 for xUnit, and BSD-3-Clause for FsCheck. Transitive metadata was reviewed too; ANGLE supplies a bundled BSD-style notice file rather than an SPDX expression. Native packages/font assets can contain additional notices: keep the actual package notices when preparing distributions. This phase records metadata and retains dependencies; it does not create an installer or certify a complete distribution-notice bundle.

## Build/test privacy finding

Avalonia.BuildServices 11.3.2 is an existing transitive build dependency with build-time telemetry; Microsoft Testing Platform includes telemetry-related test packages. The new verification and mutation scripts set `AVALONIA_TELEMETRY_OPTOUT=1`, `TESTINGPLATFORM_TELEMETRY_OPTOUT=1` and `DOTNET_CLI_TELEMETRY_OPTOUT=1` in their child-process environment, then restore caller values. Ordinary commands outside these scripts do not inherit that guarantee; use the same environment settings when running them manually. These settings do not change application runtime behavior. See the publishers' [Avalonia build-service documentation](https://github.com/AvaloniaUI/Avalonia.BuildServices) and [MTP telemetry documentation](https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-telemetry).

Reports and test environments stay local under ignored `work/`. Neither script uploads source, private binary fixtures, golden results or mutation reports. NuGet restore/advisory queries contact the configured package feeds with ordinary public package requests. No CI host, remote diagnostics service or dependency-update automation was enabled.

## Update policy

1. Before a release or planned dependency update, rerun vulnerability/deprecation checks with transitive packages included; record feed failures as unavailable evidence, not a clean audit.
2. Investigate actionable advisories promptly against actual usage/exposure, including native dependencies. Prefer a reviewed compatible patch; if a fix requires a larger migration, record the reason and mitigation rather than silently suppressing the advisory.
3. Update related framework packages together. Keep UI/runtime and test-tool changes separate when practical, and review upstream release notes, SDK support, licenses/native notices and generated dependency-graph differences.
4. Intentionally change project versions or the SDK pin, run `dotnet restore Ps5Backport.slnx --force-evaluate` (and the mutation project if its graph changes), and review every lock-file diff. Do not use force-evaluate in ordinary verification.
5. Run the public lane in both configurations, then full private verification on a trusted local machine. Preserve golden files; investigate any byte/report drift before accepting an upgrade. Mutation-runner upgrades also need a small compatibility rerun.
6. Review analyzer/runner dependencies independently of production packages; retain the currently tested runner setup until replacing it has demonstrated equivalent discovery, filtering, skip/failure and reporting behavior.

The commands and lock files provide version/restore repeatability. They do not guarantee identical timestamps, informational revisions, native rendering, runtime patch behavior or results on an untested operating system.

## Phase 5 optional benchmark dependency

The application/Core/CLI/UI/test package versions remain unchanged. The package-free `verification/Performance` harness and optional `verification/Benchmarks` project have lock files and stay outside the application solution. The benchmark pins [BenchmarkDotNet 0.15.8](https://www.nuget.org/packages/BenchmarkDotNet/0.15.8); its resolved graph has 23 external packages. Local package license metadata declares MIT for 21; CommandLineParser and Microsoft.DotNet.PlatformAbstractions carry MIT text in their package license files. Native/tool components can carry additional notices; these tools are not being bundled into an application distribution.

The transitive vulnerability query for the benchmark project succeeded on 10 October 2026 and reported no flagged packages. This dated feed result does not certify every tool/native component. BenchmarkDotNet brings diagnostic/compiler/decoder dependencies (including Iced/Capstone) into the optional measurement process only. The application's import scanner is unchanged; the separate Phase 7 evaluation is recorded below.

BenchmarkDotNet's local ShortRun completed both metadata benchmarks with MemoryDiagnoser; its initial generated-project name mismatch was corrected. It reported temporary power-plan selection/restoration. No dashboard or profiler upload was enabled. Results and licensing review are local under `work/phase5/`. The performance script sets/restores the .NET CLI telemetry opt-out; use the existing documented environment opt-outs for manual restore/build/tool commands too.

## Phase 6 verification worker

No package versions were added or upgraded. Local logging uses the existing .NET file/JSON APIs. `verification/UiProfile` has a lock file and references the app's existing Avalonia.Headless dependency; it adds no test framework adapter. It is now in the solution because the mandatory headless regression tests need a worker built in the matching Debug/Release configuration. The test project's reference uses `ReferenceOutputAssembly=false`; worker types do not become part of the test or application API. Optional benchmarks/mutation/performance projects retain their previous scope. Locked solution restore and both builds passed on 10 October 2026.

## Phase 7 optional decoder dependency

`verification/DecoderEvaluation` directly pins Iced 1.21.0 and has its own content-hash lock file. It stays outside the solution and has no application/test consumers. Its resolved external graph contains Iced alone; cached package metadata declares MIT and empty dependency groups. The loaded DLL is 1,896,448 bytes. Keep its license/copyright notice if distributing this optional tool. Application package versions and Core/CLI's package-free dependency graph remain unchanged.

The transitive vulnerability query succeeded against the configured NuGet feeds on 10 October 2026 and reported no flagged packages. Locked restore and Release compilation passed with zero warnings/errors. [INSTRUCTION_DECODER_REPORT.md](INSTRUCTION_DECODER_REPORT.md) records measured costs, precision limitations and the decision against production segment-linear scanning.

## Phase 8 optional comparisons

`verification/Phase8Evaluation` pins System.CommandLine 2.0.12 in a separate lock file and references CLI/Core. It stays outside the solution and has no application/test consumers. Its selected net8.0 asset has no additional package dependencies; local metadata declares MIT and the loaded DLL is 151,352 bytes. Locked restore/Release compilation passed with zero warnings/errors. The transitive NuGet advisory query succeeded on 10 October 2026 and reported no flagged packages; this is dated feed evidence.

The companion uses existing Python 3.11.9, standard-library SQLite 3.45.1 and jsonschema 4.26.0. The script checks the reviewed jsonschema version and records its dependency versions; it installs nothing. Local jsonschema metadata declares MIT. This Python environment is not content-hash locked or advisory-certified by this phase, and no Python/native database component enters the shipped application. Preserve actual package notices if distributing optional tools.

No production package/version changes were adopted. CLI compatibility blockers, database experiment limitations and partial-schema scope are recorded in [PHASE8_DECISION_REPORT.md](PHASE8_DECISION_REPORT.md). A complete schema/runtime-validator or .NET database-provider adoption would require a separate dependency and migration review.
