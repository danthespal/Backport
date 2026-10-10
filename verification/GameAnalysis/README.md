# Game sample analyzer

Reusable local research tool for comparing decrypted/default/backported PS5 game samples. It uses Studio's Core parser, firmware classifier and dry-run pipeline, with Iced 1.21.0 confined to this developer tool. It is outside the production solution and has no reference from the app or CLI.

From the repository root:

```powershell
pwsh ./scripts/analyze-game.ps1 -SelfTest
pwsh ./scripts/analyze-game.ps1 -Profile verification/GameAnalysis/profiles/PPSA34008.json
```

Each run creates a new directory under `work/game-analysis/<profile-name>/<timestamp>-<id>/`. Supply `-Output PATH` for an explicit destination. Existing destinations and destinations overlapping inputs are refused. Source samples and the fakelib store are read only. Ctrl+C cancels the analysis. `run.json` records completed, failed or cancelled state; files from a failed run are partial evidence.

The wrapper restores locked dependencies and builds Release. `-NoRestore` uses previously restored dependencies, useful when offline. It still builds and checks failures. To run a built tool directly, from any directory:

```powershell
dotnet E:/PS5_TOOLS/Backport/verification/GameAnalysis/bin/Release/net10.0/GameAnalysis.dll PROFILE.json NEW_OUTPUT_DIRECTORY
```

## New games

Copy a profile and change the input paths, variant folders and firmware targets. **All paths inside a profile are relative to that profile file**, or absolute; they are independent of the caller's working directory. Variant names are immediate child directories of `gameDirectory`. Unknown JSON fields are errors, so misspelled options do not silently disappear.

Minimal profile:

```json
{
  "gameDirectory": "../../../.original/backports/PPSA_NEXT",
  "referenceDirectory": "../../../firmware_db",
  "baseline": "default",
  "variants": ["default", "4xx+"],
  "firmwares": ["4.03", "10.01"]
}
```

Optional fields:

| Field | Purpose |
| --- | --- |
| `aerolib` | NID names; absent means NIDs are retained |
| `dataDirectory` | Studio knowledge, also used as a fallback for known function names |
| `studioFirmwares` | Subset of `firmwares` to run through Studio against the baseline; requires `dataDirectory` and a firmware supported by Core's SDK table |
| `fakelibDirectory` | Explicit replacement set for those dry runs; checksum verification when it has a manifest |
| `options` | Studio option keys for dry runs, e.g. `gpu_scratch`; no implicit options or unknown stubbing |
| `importNids` | Extra imports whose candidate branch sites should be collected; unresolved imports are included automatically |
| `windows` | Selected `{ "module": "sce_module/libc.prx", "address": "0x118ae0", "length": 32 }` windows, applied to each variant containing the module |

No PPSA34008 addresses, variant names, firmware targets or patch options are hardcoded in the analyzer. Its supplied profile carries that game's manual research windows. Clear/change them for a different game. A variant containing no modules fails explicitly; an invalid module or encrypted SELF also fails rather than being silently skipped.

## Outputs

| File/folder | Contents |
| --- | --- |
| `run.json` | Schema version, completion state, resolved profile and successful-run hashes of source modules, reference/knowledge metadata and selected provider files |
| `analysis.json` | Per-module container-independent ELF hashes, source hashes, SDK/build metadata, firmware matrices, unresolved imports, reference counts and baseline comparisons |
| `summary.md` | Static dependency matrix with interpretation limits |
| `elf/<variant>/` | Unwrapped modules preserving relative paths, ready for Ghidra |
| `disassembly.txt` | Bounded CPU disassembly windows, never an unqualified whole-file decode |
| `ghidra-sites.txt` | ELF VAs for PLT/GOT and candidate direct CALL/JMP references |
| `studio-<firmware>.txt/.json` | Production pipeline dry-run reports; no cooked overlay |

JSON schema version is **1**. Comparisons include modules added/removed and appended/truncated bytes, plus both variants' mapped addresses. Byte previews are capped at 32 bytes per range and explicitly marked when truncated. Comparisons are by file offset, not semantic function matching; differing layouts need separate reverse engineering. The raw variant matrix uses that variant's own shipped providers; the configured external fakelib set applies only to Studio dry runs.

The tool makes no console compatibility claim. Data imports, ABI contracts, load order, indirect calls and GPU runtime behavior remain outside its proof. A zero reference count can miss reachable calls; a positive count can include dead code or chance byte matches. Disassembly requires manually confirmed instruction boundaries. Firmware references say **exact** or **lower bound**; the first observed export means only the first match in the available database. Source module hashes/inventories and metadata/provider hashes are checked again before a successful completion marker. Studio's SDK table currently supports major versions 1–11; 12.xx can be a static matrix target but cannot be requested as a Studio dry run. Unsupported dry-run targets fail before output creation.

Private source samples, unwrapped binaries and imported libraries remain under ignored `.original/`, `work/` and `fakelibs/`. Reports contain local paths and binary byte previews. The tool does not upload anything.

## Verification — 10 October 2026

Release build: zero warnings/errors. Generated-fixture self-tests passed via the wrapper, including source preservation, full reports, additions/removals and append deltas, exact/lower-bound references, disassembly and invalid output/SDK-target rejection. The PPSA34008 profile completed over 25 modules and reproduced 333 eboot ranges / 345 changed bytes; its 4.03/10.01 dry runs retained one/zero unresolved imports. A second profile completed over 19 Hades II modules with no game-specific code changes. The production Debug solution build and eight PPSA34008/distribution regressions passed. No console execution was performed.
