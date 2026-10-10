# Optional CLI/catalog/schema decisions — Phase 8

10 October 2026. Phase 8 completes the optional decision gate. Keep the current CLI implementation and JSON catalogs. Retain the schema authoring experiment as a useful follow-up, without declaring its partial schema a complete versioned runtime contract. No production code, package version, rules, binary fixtures or golden files changed in this phase.

## Evidence and scope

The isolated `verification/Phase8Evaluation` project compares the existing private CLI parser with System.CommandLine 2.0.12 without exposing a new production API. Reflection reads the actual parser rather than a copied implementation. Its candidate defines only `backport`, `report` and `nid` and selected options; commands that could modify files are parsed, never invoked. Eighteen cases assert specified parser acceptance/error observations and record values. Safe NID/usage invocations also record the current exit code and both output streams.

The .NET worker measures the existing firmware loader and loaded-set queries, then tests three synthetic knowledge changes in copies under ignored `work/phase8/`. A Python companion builds an in-memory SQLite representation of the existing firmware metadata, asserts semantic export round-trip and query equivalence, and validates 52 real fix records plus six schema edge cases. Original catalog/rule hashes are checked for changes. No private game/system binaries are needed or opened.

These are comparison prototypes and compatibility blockers, not a migrated CLI, production database, complete schema or whole-application performance benchmark. The optional project is outside `Ps5Backport.slnx`; no application/test project references it. Its package lock resolves System.CommandLine alone, plus project references to CLI/Core.

## System.CommandLine: defer migration

The current token parser occupies approximately 35 lines; command implementation and help text are much larger. A framework can centralize command metadata, validation, generated help and completion, but it does not remove the command handlers. Those capabilities are described in the [official overview](https://learn.microsoft.com/en-us/dotnet/standard/commandline/).

| Behavior | Existing parser | Candidate | Migration consequence |
| --- | --- | --- | --- |
| `--firmware 4` / `--firmware=4` | Accepts both | Accepts both | Compatible basic cases |
| Repeated scalar firmware values | Last value wins | Reports multiple arguments | Needs explicit compatibility handling or a documented behavior change |
| Repeated `--option` / `--provider` | Ordered lists | Ordered arrays with configured one-value-per-token options | Preserve ordering and repeat behavior |
| Global `--aerolib` before/after command | Accepted | Accepted with a recursive option | Must configure inheritance |
| `--extend-text=false` | Presence is true | Boolean value is false | Flag semantics differ |
| `--` token | Ordinary positional; later options still parsed | Ends option parsing | Arity and dash-prefixed argument behavior differ |
| Unknown option with a value | Stored and potentially ignored by handler | Reports unrecognized tokens | Existing typo handling needs an explicit policy |
| `--help` | Missing-value error, exit 1 | Selects built-in HelpAction | Output stream/text/exit behavior changes |
| `nid -h` | Computes a NID for the string `-h` | Selects HelpAction | Cannot silently introduce a short help alias |
| Option followed by another option-like token | Can consume the token as its value | This tested string option also consumes it | A framework alone does not resolve every malformed-input problem |

The [official syntax documentation](https://learn.microsoft.com/en-us/dotnet/standard/commandline/syntax) explains option/argument syntax; compatibility must be verified against this application's contract rather than assumed from those conventions. The existing `--extend-text` flag remains accepted, with extension already enabled by default; `--no-extend-text` is the effective disable control.

Concrete maintenance opportunity: duplicated help/dispatch metadata and permissive unknown-option handling. The prototype demonstrates significant compatibility work, not an immediate reduction in maintenance cost. Therefore retain the existing implementation. Unknown options and boolean/help conventions are known quirks, not newly introduced features or a recommendation to rely on them.

Before a migration, approve a documented CLI contract, expand characterization to every command/subcommand, aliases, option applicability, repeated values, `--`, malformed inputs and stdout/stderr/exit codes, and then prototype a compatibility adapter. Preserve existing valid command goldens and payloads. Intentional error-policy changes should have separate regression expectations rather than rewriting goldens to accept framework defaults. Do not add completions, response-file expansion or new aliases implicitly.

## SQLite: no measured reason to migrate

The local catalog contains **43 firmware JSON files, 16,547,646 bytes**, and 861,897 NID memberships summed across firmware union sets. Current operations are whole-catalog loading, exact/lower-bound firmware selection, set membership/diffs, and human-inspectable import/export. Phase 5 already removed duplicate loading within an operation. Fakelib manifests preserve unknown fields and describe external module files; diagnostic history uses bounded rotated files rather than a queryable database.

| Measurement | Observation |
| --- | --- |
| Existing .NET load plus materialized union sets, first sample | 320.94 ms; 174,956,208 allocated bytes |
| Three subsequent .NET samples | 328.86 / 285.01 / 180.47 ms; approximately 173.10 MB allocated each |
| 43,000 existing .NET set-membership queries | 12.78 ms |
| Python JSON parse/set construction | 100.76 ms first; 171.95–214.90 ms subsequent samples |
| Python in-memory SQLite import/index construction | 1,649.41 ms |
| Same 43,000 queries within Python | Loaded sets: 6.31–7.02 ms; indexed SQLite statements: 51.76–64.83 ms |

The .NET and Python needle sets differ; compare only equivalent queries within each environment. The Python SQL and set hits were equal in all three iterations. The semantic round-trip recovered metadata and module/NID sets from the database alone, with a dedicated synthetic empty-module assertion; arrays were normalized to sorted unique NIDs. It does not promise byte-identical JSON, property ordering, duplicate preservation, full fakelib manifest migration or an application schema.

The SQL query uses one prepared-path execution per membership check. Bulk queries could give different results. The database is in memory: opening a persisted index, query-plan alternatives, disk size, WAL/checkpoint behavior, concurrent writers, crash recovery and .NET provider overhead were not measured. The subsequent JSON samples include allocation/GC effects and are not isolated storage-throughput figures. No process-cold or whole-game latency claim is made.

SQLite could support persistent indexed history, provenance joins and relational constraints if those become product requirements. None is established by the current workload. Keep JSON and per-operation in-memory indexes. If catalog loading becomes an observed UI bottleneck, first evaluate loading only the needed firmware or retaining a safely invalidated read-only snapshot; do not assume either is correct without selection/fingerprint tests.

[SQLite's usage guidance](https://www.sqlite.org/whentouse.html) describes its embedded-database strengths and single-writer constraint. A database transaction would protect database state, not atomically publish the application's external ELF/SELF files. Existing staging, locks, evidence and recovery remain necessary in any future database design.

Before revisiting: demonstrate expensive real catalog/history queries or concurrent edit requirements; compare a .NET persisted index with current JSON on the same queries and machine; define invalidation, unknown-field retention, import/export and firmware ordering; test migrations/rollback, competing writers, recovery and unchanged patch/report output. Do not make the database the sole copy of inspectable source metadata.

## Formal JSON schemas: useful authoring safeguard, incomplete runtime proposal

Seven knowledge files total **47,665 bytes**. The runtime already checks duplicate keys, string/null types, fix kinds, conditions, firmware strings, stub references, adapter bodies and safe/equal-length patterns. Some documents are regenerated by existing tools; fixes/stubs remain hand-authored JSON. There is no established versioned external interchange contract in this repository.

The experimental Draft 2020-12 schema covers a single fix record, enumerated kinds/conditions, types and unknown-field rejection. It validates all **52 current default/option records**. Six specified cases demonstrate accepted typed data, rejection of misspelled fields/wrong types/conditions, and two limits that still require Core validation.

| Synthetic change | Prototype schema | Current Core on a copied complete data set |
| --- | --- | --- |
| `proovn: false`, with `proven` absent | Rejects unknown field | Accepts and defaults `Proven` to true |
| Unknown stub name | Accepts its string shape | Rejects unresolved stub |
| Empty adapter instruction array | Accepts its array shape | Rejects empty adapter |

The first row is a concrete authoring risk: a typo can change whether a rule is treated as proven. The test deliberately mutated a copied existing stub record; it found no such typo in repository data. The partial schema is useful when editing records, but must not be presented as a full safety check. It permits `none` without distinguishing option/default context and does not cover entire files, NID keys, sources, patterns, cross-document references or instruction semantics. It does not change Core's compatibility with unknown fields.

The [Draft 2020-12 validation specification](https://json-schema.org/draft/2020-12/json-schema-validation) defines structural validation and separates format annotations/assertions. Use explicit field constraints and retain semantic validation; do not assume a format label enforces PS5 firmware/NID semantics or that validation after parsing can recover duplicate JSON keys.

Decision: keep the prototype optional; defer a mandatory full/versioned schema until its supported contract and compatibility policy are specified. This is the strongest follow-up candidate of the three proposals. A focused implementation should cover fixes/stubs first, identify allowed annotations/extensions, version schema identities without silently rewriting existing data, support local editor association, and test unknown-field typos alongside current Core semantics. Only add a runtime validator package if runtime interchange requires it; an editor/authoring check may suffice.

## Pros and cons

| Proposal | Benefit | Cost / current decision |
| --- | --- | --- |
| System.CommandLine | Central command model, validation/help/completion support | Compatibility adapters and new dependency; defer migration |
| SQLite catalog/history | Persistent indexing, joins, relational constraints | Invalidation/migration/native-provider ownership; measured existing membership queries are inexpensive; retain JSON |
| Versioned full JSON schemas | Editor guidance and unknown-field/type safeguards | Contract maintenance and semantic checks still required; retain partial authoring prototype, prioritize a scoped follow-up |

No framework was adopted just to change the application's appearance or dependency list. These decisions preserve the behavior verified through Phases 1–7 while identifying a concrete authoring issue and explicit future acceptance criteria.

## Dependencies, reproduction and acceptance

The optional C# project pins [System.CommandLine 2.0.12](https://www.nuget.org/packages/System.CommandLine/2.0.12); the selected net8.0 asset has no additional package dependencies and is compatible with this net10.0 experiment. Local package metadata declares MIT; the loaded DLL is 151,352 bytes. Its locked transitive NuGet advisory query succeeded on 10 October 2026 and reported no flagged packages. This is dated feed evidence, not a security certification. No application dependency graph changed.

The Python companion uses the existing Python 3.11.9 environment, SQLite 3.45.1 and jsonschema 4.26.0. The script requires that jsonschema version, records dependency versions, and installs nothing. Python/transitive packages are not content-hash locked or advisory-certified by this phase; these experiments are not shipped runtime components. jsonschema's local metadata declares MIT. Preserve actual notices if distributing optional tools.

```powershell
pwsh -NoProfile -File scripts/evaluate-phase8.ps1
# If needed, select an existing compatible environment:
pwsh -NoProfile -File scripts/evaluate-phase8.ps1 -Python C:/path/to/python.exe
dotnet list verification/Phase8Evaluation/Phase8Evaluation.csproj package --include-transitive --vulnerable
pwsh -NoProfile -File scripts/verify.ps1 -Lane All
```

Run comparisons separately from builds/tests. Locked restore and the optional Release build passed with zero warnings/errors. All 18 specified parser observations, three copied-data semantic observations, SQLite round-trip/query-equivalence assertions and 52 schema records plus six edge cases passed. These counts are experiment cases, not added xUnit tests. Final records and full regression results are linked in VERIFICATION.md. Production migration tests are not claimed because no production migration was adopted.

Final comparison records: `work/phase8/20261010-032209/`. Full local verification passed **357 tests in each Debug/Release configuration**, zero failures/skips and zero-warning builds, with unchanged private byte/report goldens. Records: `work/verification/20261010-032020-04ec1872bf4d4006b2e800c901635003`. `git diff --check` passed. Prior phase changes remain preserved.
