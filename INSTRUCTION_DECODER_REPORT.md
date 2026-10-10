# Instruction-decoder evaluation — Phase 7

10 October 2026. Decision: retain Iced as an optional local evaluation tool; do not replace the production reference scanner or patch emitter. The experiment demonstrates useful instruction validation, but linear decoding of entire executable segments is not a safe reference-count migration.

## What changed

`verification/DecoderEvaluation` pins Iced 1.21.0 with a lock file and references Core. It stays outside `Ps5Backport.slnx` and has no references from the application, CLI or main tests. `scripts/evaluate-decoder.ps1` runs synthetic checks by default; `-IncludePrivate` also reads three existing local ELF fixtures. Reports stay under ignored `work/phase7/`. No binary input, rule, golden, patch algorithm or application package version was changed.

One production XML comment was corrected: the byte-pattern scanner can miss direct references as well as over-count them. Neither zero nor positive counts prove runtime reachability. Its behavior remains unchanged.

The candidate decodes 64-bit instructions from executable segment starts, resolving direct CALL/JMP and IP-relative MOV/LEA/indirect CALL/JMP operands. It retains the current target-map and lazy-PLT exclusions. FS/GS-based loads and branches remain unresolved because their actual addresses require runtime segment bases. It neither follows a control-flow graph nor propagates register values. Optional manually supplied code ranges are used only where synthetic instruction boundaries are independently known.

## Accuracy and failure modes

All **530 reference cases** passed: 18 named edge cases and 512 deterministic generated variants. “Passed” means each scanner produced its specified observation; it does not mean either scanner found every reference. Expected results come from explicit synthetic byte layouts, branch displacement arithmetic and annotated code/data ranges, rather than treating either implementation as an oracle.

| Case | Expected static reference | Current heuristic | Linear decoder | Interpretation |
| --- | ---: | ---: | ---: | --- |
| Ordinary CALL/JMP, GOT branches, MOV/LEA and permitted prefix | 1 | 1 | 1 | Seven basic encodings agree |
| Address-size prefix at a virtual address above 4 GiB | 1 | 0 | 1 | Decoder correctly resolves EIP-relative address truncation |
| CALL-looking bytes inside MOV's immediate | 0 | 1 | 0 | Boundaries prevent a false reference |
| Invalid LOCK-prefixed indirect CALL | 0 | 1 | 0 | Decoder rejects this encoding |
| FS-based load | Unresolved | 1 | 0 | Decoder avoids assuming a zero runtime segment base |
| RET followed by call-looking embedded data | 0 | 1 | 1 | Linear decoding still treats data as code; annotated range yields 0 |
| Short jump over one data byte to a real CALL | 1 | 1 | 0 | Linear decoding loses alignment; annotated ranges yield 1 |
| Truncated CALL | 0 | 0 | 0 | Decoder reports invalid input and makes progress |
| Register-indirect CALL | Unresolved | 0 | 0 | Neither scanner resolves runtime register contents |
| Prefixed PLT self-reference | 0 | 1 | 0 | Decoder excludes the instruction's true start |
| Short jump to a PLT entry | 1 | 0 | 1 | Decoder recognizes a currently unsupported direct branch |

Generated variants place either a valid RIP-relative MOV or a call-looking immediate after 0–64 NOP bytes. These exercise offset handling and input preservation; they are not broad instruction-set fuzzing. Cancellation is checked before/through decoding; invalid instructions do not stall the reader. Every analyzed input is hashed before and after to reject mutations.

### Existing local corpus

| Fixture | Executable bytes | Heuristic references | Linear references | Invalid decoded instructions |
| --- | ---: | ---: | ---: | ---: |
| Original eboot | 66,119,468 | 426,106 | 426,104 | 1 |
| Original libc | 1,128,430 | 3,063 | 3,063 | 5 |
| Existing backported libc | 1,128,430 | 3,062 | 3,062 | 5 |

The eboot disagrees on two NIDs, each by one reference. Prefix-aware site comparison removes differences that are merely different starts within the same prefixed instruction. Local inspection then found:

- One unmatched jump pattern begins inside the displacement of a RIP-relative store with an immediate value. Manually separating its opcode, ModRM, four-byte displacement and four-byte immediate supports a heuristic false positive.
- The other jump follows a RET, zero padding and an aligned start, with INT3 padding afterward. Segment-linear decoding consumes the jump opcode as the end of an earlier instruction. Its manually calculated displacement reaches the mapped PLT entry, supporting a likely decoder false negative.

The surrounding bytes were inspected locally; private byte contexts and addresses are retained only in the ignored report. These are supported interpretations, not independently recovered function boundaries for the entire corpus. Matching libc counts also do not certify every site. Existing three eboot reference-count regression expectations remain unchanged and are asserted by the evaluation.

No independent disassembler was available in the local command inventory. Synthetic byte-field calculations and corpus context inspection provide independent checks, but do not constitute a second full disassembly. A future migration needs stronger code-boundary evidence, including symbols/unwind metadata where available, explicit handling of ambiguous regions and independently reviewed disagreement fixtures.

## Generated-code checks

Iced decoded all eight named stub bodies and 45 configured adapter byte blocks without invalid/truncated instructions. Named stubs end at RET. Additional checks verify two nonzero EAX return constants, three GOT jump targets including the positive rel32 limit, a PLT redirect's direct target/NOP and unchanged GOT value, and two adapters split across guarded caves.

The chained checks follow emitted inter-cave jumps, verify R8/RDX operands, the GOT CALL and the final GOT jump or RET, and preserve both INT3 guards in every synthetic cave. They decoded 17 instructions across the two chains. This validates encoding and selected operands/control flow. It does not prove stack alignment, complete register preservation, pointer validity, console firmware compatibility or game ABI correctness. Data-pattern replacements are not decoded indiscriminately as executable code.

## Measured costs

Release, .NET runtime 10.0.12 on the local machine. Each workload has one first sample followed by three warm samples, alternating scanner order. Input construction, hashing, GC preparation and disagreement inspection are outside timing. The first sample occurs after synthetic checks and is **not process-cold**. Allocation uses the current thread and includes target dictionaries and scanner setup; it excludes corpus loading and report construction. This is a focused scanner comparison, not a whole-game performance benchmark.

| Workload | Heuristic warm mean (range), ms | Iced warm mean (range), ms | Warm allocated bytes, heuristic → Iced |
| --- | ---: | ---: | ---: |
| Synthetic 1 MiB | 3.54 (3.47–3.66) | 11.32 (10.60–12.12) | 608 → 1,160 |
| Synthetic 16 MiB | 58.41 (53.83–61.27) | 91.42 (87.27–98.96) | 608 → 1,160 |
| Original eboot | 131.74 (131.11–132.50) | 425.51 (403.06–463.21) | 4,247,544 → 4,248,096 |
| Original libc | 2.45 (2.41–2.49) | 8.48 (8.37–8.57) | 39,992 → 40,544 |
| Existing backported libc | 2.31 (2.27–2.36) | 8.47 (8.08–9.12) | 39,992 → 40,544 |

The decoder costs approximately 1.6–3.7 times the scanner time in these workloads, with 552 additional steady scanner-setup bytes. It reads the original array through a bounded reader without copying executable segments or retaining all decoded instructions. Optional site inspection has additional memory/time costs outside this table. These results support selective validation, not a performance improvement claim.

During the 16-MiB scan, cancellation was observed approximately 0.17 ms after the timer actually requested it. The nominal 5-ms timer fired after 22.10 ms, illustrating scheduling delay; this is one local observation, not a latency guarantee. Pre-cancelled scans also threw. Cancellation checks occur at most approximately 64 KiB of decoded bytes apart; target-map construction precedes the first candidate check.

## Dependency review

[Iced 1.21.0's package metadata](https://www.nuget.org/packages/Iced/1.21.0) and its cached NuSpec identify MIT licensing and no external dependency packages. The loaded managed DLL is 1,896,448 bytes (approximately 1.81 MiB). Its package pins repository commit `601c40570d6c986c8e1a6a2e483dd2fdf5ade552`; the lock records its content hash. Retain its copyright/license notice if distributing the evaluation tool.

The [official C# documentation](https://github.com/icedland/iced/blob/master/src/csharp/Intel/README.md) documents decoder/operand and instruction-information capabilities. The experiment uses decoded operands directly; it does not adopt the assembler, encoder, formatter configuration or automated register-liveness framework into production.

The local transitive NuGet vulnerability query succeeded on 10 October 2026 against the configured feeds and reported no flagged packages for this optional project. This is dated advisory evidence, not a security certification. Core/CLI remain free of external package dependencies; Iced does not enter the application's dependency graph.

## Pros, cons and adoption gate

| Proposal | Pros | Cons / decision |
| --- | --- | --- |
| Optional decoder audit | Correct prefix/operand semantics; explains byte-pattern disagreements; very small per-scan allocation overhead | Slower; needs trustworthy boundaries; keep as the completed evaluation tool |
| Decoder checks for emitted code | Known starts and lengths; catches invalid/truncated instructions and wrong registers/targets | Cannot certify console ABI; retain optional checks, consider making them mandatory only when changing emitters |
| Replace reference scanner with segment-linear decoding | Reduces immediate/displacement false matches and supports more encodings | Introduces missed code after embedded data/padding; reject this migration |
| Full control-flow recovery/dataflow engine | Could address alignment and some indirect references | Substantially greater complexity; incomplete symbols, jump tables and runtime state still create uncertainty; defer until an actual patching problem requires it |

Before adopting a production decoder, define count compatibility, trusted starts, ambiguous/unreachable-region policy and conservative treatment of unresolved references. Add reviewed regression fixtures for disagreements, demonstrate an improvement without new false negatives, budget the extra scan time, and rerun all private byte/report goldens. Console ABI/runtime validation remains a separate requirement. Phase 8 is an optional decision gate, not an automatic library migration.

## Reproduction and acceptance

```powershell
pwsh -NoProfile -File scripts/evaluate-decoder.ps1
pwsh -NoProfile -File scripts/evaluate-decoder.ps1 -IncludePrivate
dotnet list verification/DecoderEvaluation/DecoderEvaluation.csproj package --include-transitive --vulnerable
pwsh -NoProfile -File scripts/verify.ps1 -Lane All
```

Run timing work separately from builds/tests. Public evaluation needs repository rule data and the locked package restore, but no private fixtures. The private option intentionally fails if its named fixtures are absent. It never unwraps or writes a source module. The script temporarily opts out of .NET CLI telemetry and restores the prior setting. No dashboard, upload or native application launch is involved.

Final experiment record: `work/phase7/20261010-031012/evaluation.json`. Accuracy/emitter checks passed; locked restore and the optional Release build had zero warnings/errors. Full solution verification passed **357 tests in each Debug/Release configuration**, zero failures/skips and zero-warning builds, with unchanged private byte/report goldens. Records: `work/verification/20261010-031031-82cadd74618248d5b350078a07cefaa4`. `git diff --check` passed. Acceptance is an explicit non-adoption decision for linear production scanning, supported by measured benefits, identified regressions and recorded costs.
