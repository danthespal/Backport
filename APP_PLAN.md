# PS5 Backport Studio: GUI application plan

A Windows desktop app at the repo root that does what PS5 Backpork Kitchen set out to do, plus everything `ps5bp` already does better: per-firmware import analysis, generic fixes, fakelib sets, overlay output and reports.

*Name: "PS5 Backport Studio" (namespace `Ps5Backport`). Private software.*

**2026-10-10: one engine.** The Python engine `ps5bp` (and its `tests/` and `tools/`) was retired; PS5 Backport Studio's C# engine (`Ps5Backport.Core`, command line `ps5backport`) is the only one. `python -m ps5bp X` is now `ps5backport X` (same commands); `tools/import_firmware_dumps.py`, `gen_sony_builds.py` and `gen_candidates.py` are `ps5backport fwdb import-firmwares`, `data sony-builds` and `data candidates`. Its parity results live on as golden files in `src/Ps5Backport.Tests/golden/` (re-record with `PS5BP_RECORD=1` after an intended change). Older entries below mention `ps5bp` as it was then. Python is still fine for throwaway scripts outside the software.

Status legend: ☐ todo · ◐ in progress · ☑ done

---

## 1. Analysis: what we have and what the app must be

### 1.1 What Backpork Kitchen is (REFERENCE_OVERVIEW Part 9)
- VB.NET WinForms, `net8.0-windows` (built with the .NET 10 SDK; its README asks for the .NET 10 runtime), Windows only, MIT license. Packages: FluentFTP, Newtonsoft.Json, ReaLTaiizor (UI), SharpCompress, SQLite. **Discontinued** by its author.
- Backport core: SDK patch (PS5 SDK only; **PS4 SDK written as 0**, major firmware only), fake-signing, copying BackPork's patched 10.01 fakelibs (FW 6/7 only), optional libc NID swap.
- **No import analysis**, no stubs/adapters/argument fixes, no per-game knowledge. GT7 and Control could not have been made with it.
- Around it: backups, game library, FTP, payload sender, PKG/FPKG and UFS2 image builders, PUP download/extraction (needs an on-console decrypt payload), cover art, statistics, themes, localization.
- Downloads fakelibs (Sony binaries) from a URL list at first run.

### 1.2 What we have (`ps5bp`, Python, ~2,200 lines, 45 tests)
| Area | Module | Verified against |
|---|---|---|
| NID hash, names (aerolib) | `nid.py` | aerolib |
| SCE ELF read/write, imports/exports, dynamic tags | `elf.py` | GT7, Control |
| Fake SELF unwrap / sign (make_fself), encrypted detection | `fself.py` | GT7 SELFs byte-identical |
| SDK table (FW 1–11), minor versions, param.json version | `sdk.py` | GT7 (4), Control (4.03) |
| Per-firmware export DB (exact vs lower bound), import from stubs/dumps, diff, introduced-in | `fwdb.py` | 3.20 list |
| Import classification (reference/provided/stubbed/patched/data/missing/unknown), code-reference scan | `imports.py` | GT7, Control |
| Patching: stubs, int3 caves, chained caves, PLT redirect, NID swap, symbol definition, code-space extension | `patch.py` | GT7 byte-identical where SDK-only |
| Fix table (stub/swap/adapter/redirect, argument fixes, post-call), provenance, left-unpatched evidence | `data/*.json` via `fixes.py`, `candidates.py` | GT7, Control, AnyPS5 |
| Fakelib store (manifest, verify, check, build dates, known-broken builds) | `store.py` | GT7/Control sets |
| BPS patches (BackPork) | `bps.py` | BackPork 6xx/7xx |
| Whole-game pipeline → ShadowMountPlus overlay + txt/json report | `game.py` | NOT_BACKPORT → GT7 |

Knowledge lives in **REFERENCE_OVERVIEW.md** (Parts 1–12), **PLAN.md** (engine phases), **CONSOLE_BENCH.md** (13.60 bench).

### 1.3 Decisions

| Decision | Choice | Why |
|---|---|---|
| Language/UI | **C# / .NET 10 / Avalonia UI 12** (Fluent theme, MVVM with CommunityToolkit.Mvvm; your choice 2026-10-08, replacing the WPF shell) | .NET 10 SDK is installed; Kitchen proved a .NET Windows desktop app fits the audience. C# is far faster to build a rich UI in than C++ (Qt/ImGui) and the engine is byte manipulation, where C# `Span<byte>`/`BinaryPrimitives` are as good as C++. Avalonia: Fluent theme built in, same XAML/MVVM model, cross-platform, virtualized TableView for large import lists. |
| Engine | **Port `ps5bp` to a C# library** (`Ps5Backport.Core`), no Python at runtime | Users get one self-contained app. |
| Correctness | **`ps5bp` was the oracle** while porting: every ported function had a parity test that ran `python -m ps5bp` on the same input and compared bytes/JSON. **Since 2026-10-10** those results are golden files (`src/Ps5Backport.Tests/golden/`, recorded while both engines matched) and Python is gone | We had proved `ps5bp` against console-tested backports; the port inherits that proof. Tests skip when sample files are missing. |
| Knowledge data | Shared files at the repo root: `firmware_db/*.json`, `fakelibs/<FW>/manifest.json` (existing formats), and **`data/*.json`** for fixes, candidates, left-unpatched (Phase 2), read by the engine | One source of truth, edited as data. |
| CLI | `Ps5Backport.Cli` (`ps5backport`), the commands `ps5bp` had plus the data generators | Scripting, CI parity runs, and debugging without the GUI. Cross-platform (`net10.0`). |
| License | **None: private software** (your decision) | Credits for third-party work stay in About. |
| Sony binaries | **Never shipped or downloaded** by the app | Fakelibs and firmware libraries come from the user's own dumps via the store (as now). Export lists (NIDs) are fine. |
| Output | ShadowMountPlus overlay (changed files only) + reports; the game folder is never modified | Safer than Kitchen's in-place patching + backup; matches how backports are run today. |

### 1.4 Scope versus Kitchen
**In:** analysis + fixes (beyond Kitchen), SDK/sign, fakelib store, firmware DB, BPS, reports, FTP upload of overlays and log download, payload sender, game library list, settings.
**Out for now** (private tool; revisit only if asked): PKG/FPKG and UFS2 builders, PUP download/extraction, cover-art download, statistics DB, themes/localization, auto-update, in-place patching.

---

## 2. Architecture

```
Ps5Backport.slnx
src/
  Ps5Backport.Core/      net10.0       engine: formats, knowledge, analysis, patching, pipeline (no UI, no I/O policy)
  Ps5Backport.Cli/       net10.0       command line (ps5backport)
  Ps5Backport.App/       net10.0        Avalonia UI, MVVM; uses Core only (cross-platform)
  Ps5Backport.Tests/     net10.0       xUnit: unit tests, golden files (golden/), checks against testers' backports
data/                    shared knowledge JSON (Phase 2)
firmware_db/  fakelibs/  existing shared stores
```

Core namespaces mirror the former `ps5bp` modules: `Nid`, `Elf`, `Self` (fself), `Sdk`, `FirmwareDb`, `Imports`, `Patching`, `Fixes`, `Fakelibs`, `Bps`, `Pipeline`.
The pipeline reports progress through `IProgress<T>` and supports `CancellationToken`, so the GUI stays responsive.

---

## 3. Phases

### Phase 0: Foundations
| # | Task | Done when |
|---|---|---|
| ☑ 0.1 | Analysis + this plan | APP_PLAN.md |
| ☑ 0.2 | Solution skeleton: Core, Cli, App (WPF), Tests; `Directory.Build.props` (nullable, warnings as errors, latest C#); `global.json`; `.gitignore` for `bin/`, `obj/` | `dotnet build` and `dotnet test` pass |
| ☑ 0.3 | Oracle harness: locate repo root, Python, sample files; run `ps5bp` snippets from tests; skip cleanly when missing | Helper + a test that uses it |
| ☑ 0.4 | First ported primitives with parity tests: NID hash, `split_symbol`, aerolib name DB, firmware parsing, SDK pair table, `param.json` version | Parity tests green against Python |
| ☑ 0.5 | App shell: main window with the planned layout (game panel, target firmware, analysis tabs, log) as placeholders; About shows credits | App starts |
| ☑ 0.6 | Decisions recorded: private (no license), name, no Kitchen extras, GUI design needs your approval | Section 5 |

Progress (2026-10-08):
- Solution `Ps5Backport.slnx` with `src/Ps5Backport.{Core,Cli,App,Tests}`; `Directory.Build.props` (nullable, warnings as errors, latest C#); `global.json` pins SDK 10.0.300+ and the Microsoft.Testing.Platform runner (required by .NET 10 `dotnet test` with xUnit v3).
- Tests use **xUnit v3** (`Assert.Skip` for missing Python/samples). `Oracle.Run(code)` pipes a Python snippet to `python -` in the repo root; `Oracle.Need(path)` skips when a sample is missing.
- Ported: `Nid.Compute`, `Nid.IdFromChars`, `Nid.SplitSymbol`, `NameDb`, `Firmware.Parse/Name/SdkFor/ParamVersion`, the SDK pair table. Parity: every 7th aerolib name (C++ mangled included), NameDb count + lookups, firmware values incl. `4.3`, `4.30`, ` 7.61 `. **17 tests pass, 0 skipped.**
- CLI: `ps5backport nid NAME...`, `ps5backport sdk FW`.
- App shell: menu (File, Tools, Console, Help/About), game panel, target panel (firmware picker from Core showing the SDK pair, option checkboxes), Modules/Imports/Report tabs, log. Starts and responds.
- 0.6: decisions recorded (section 5).

Build, test and run:
```bash
dotnet build Ps5Backport.slnx
```
```bash
dotnet test --solution Ps5Backport.slnx
```
```bash
dotnet run --project src/Ps5Backport.App
```

### Phase 1: Binary formats
| # | Task | Done when |
|---|---|---|
| ☑ 1.1 | `SceElf`: program headers, dynamic table, dynstr, symbols, relocations (JMPREL/RELA), imports (PLT/data, defined-locally), exports (own libraries), module/build path, text segment, vaddr↔offset, save | Imports/exports identical to Python for every sample module |
| ☑ 1.2 | File kind detection (ELF, PS4-style and PS5 SELF), encrypted-segment detection | Same results as Python |
| ☑ 1.3 | SELF unwrap (keeps the original ELF header) | Byte-identical to `ps5bp unwrap` on all samples |
| ☑ 1.4 | Fake-sign (make_fself port: same defaults, PAID, ptype) | Byte-identical to `ps5bp sign`; GT7 SELFs reproduced |
| ☑ 1.5 | BPS parse/apply/edits | Same as Python on BackPork patches + synthetic round-trips |

Progress (2026-10-08):
- `SceElf` (program headers, dynamic table, symbols, relocations, imports, exports, text segment, vaddr↔offset), `Self` (kind, encrypted segments, digest offset, unwrap, sign, atomic file writes), `Bps` (+ zlib-compatible `Crc32`).
- **Parity on all 33 sample modules** (GT7 USA, GT7 NOT_BACKPORT, Control; ~450 MB): file kind, encrypted counts, unwrap bytes, sign bytes and the whole ELF model (phdrs, import libraries, every import tuple, exports, text segment) equal `ps5bp`. Unwrap → sign reproduces every shipped PS4-style SELF except the digest. BPS: parse + edits equal Python for all BackPork patches; synthetic patches cover all four actions incl. overlapping target copies; wrong source and corrupt patch refused.
- CLI: `info`, `unwrap`, `sign`.
- Pitfall found: the C# literal `"\x7FELF"` is not the ELF magic (`\x` takes up to 4 hex digits, so `\x7FE` is one character); the bytes are spelled out.
- **29 tests pass, 0 skipped** (about 25 s).

### Phase 2: Knowledge data
| # | Task | Done when |
|---|---|---|
| ☑ 2.1 | `data/fixes.json`, `data/candidates.json`, `data/left_unpatched.json` exported from Python (`tools/export_data.py`); Python switched to read them | Python tests still pass; files committed |
| ☑ 2.2 | C# loaders for `data/*.json`, `firmware_db/*.json` (load, select exact/lower bound, introduced-in, import from genstub/dump, diff) | Parity with `fwdb` |
| ☑ 2.3 | Fakelib store: find (exact → major), manifest add/verify, build-date detection, known-broken builds, store check | Same manifests as Python |

Progress (2026-10-08):
- 2.1: `data/fixes.json` (36 fixes, now with `area`, per-instruction asm notes and the rationale that used to be Python comments), `data/stubs.json`, `data/candidates.json` (written by `tools/gen_candidates.py`), `data/left_unpatched.json`, plus `data/introduced.json` and `data/known_builds.json` (moved from `fwdb.py`/`store.py`). `ps5bp` loads them through `ps5bp/knowledge.py`; the loaded tables are identical to the old Python literals (snapshot compared) and all 45 Python tests pass unchanged.
- 2.2: C# `Knowledge` (all six files), `PyJson` (writes JSON exactly like Python's `json.dump(indent=n)`), `Paths`, `Sdk` (read/write), `FirmwareDb` (load/save/select/import genstub/import dump/diff/introduced). `firmware_db/3.20.json` survives load→save byte for byte, and re-importing `PS5-3.20_Libs` reproduces it exactly; `import_dump` output equals Python's.
- 2.3: `FakelibStore` (find, describe, add, verify, list, build warnings): `Describe` equals Python for the 4.03 set, and `Add` writes a byte-identical `manifest.json`. The store check came with Phase 3 (`FakelibStore.Check`).
- **41 tests pass, 0 skipped.**

### Phase 3: Analysis engine
| # | Task | Done when |
|---|---|---|
| ☑ 3.1 | Import classification (all statuses, provided exports, exact vs lower bound); reference loading (firmware_db or genstub folder); fakelib store check (2.3 remainder) | Identical status lists for GT7, Control, NOT_BACKPORT |
| ☑ 3.2 | Code-reference scan | Same counts as Python |
| ☑ 3.3 | Game scan: module discovery, `param.json` (title, version, required FW), ampr index warning, encrypted input | Same warnings as Python |

Progress (2026-10-08):
- `Imports`: `Classify` (all seven statuses, PLT preferred per NID, exact vs lower bound), `LoadReference` (firmware_db or genstub folder, same labels), `LoadProviderExports`, `CodeReferences` (byte scan as a plain loop), `Report` (same text). `GameScan`: `FindModules` (os.walk order), `ReadParam` (title, version, warnings, lowered text), `AmprIndexWarning`. `FakelibStore.Check` (2.3 remainder).
- **Parity on all three sample games** (GT7 USA, NOT_BACKPORT, Control; every module): module list, statuses with a 3.20 lower-bound and an exact reference, code-reference counts for every import, report text, param.json results for targets 4 / 4.03 / 10 with and without lowering, ampr warning, and the store check of both fakelib sets.
- Found and fixed in `ps5bp`: `--patch-param` rewrote the sample `param.json` files (all CRLF, including Control's console-tested one) with LF line endings. It now keeps the file's line endings; a new Python test checks that only the version values change.
- **49 C# tests and 46 Python tests pass, 0 skipped.**

### Phase 4: Patch engine
| # | Task | Done when |
|---|---|---|
| ☑ 4.1 | SDK read/write | Same bytes |
| ☑ 4.2 | Caves, chained caves, stubs (incl. `ret:<value>`), symbol definition | Same bytes as Python for each fix |
| ☑ 4.3 | NID swap, PLT redirect, adapters (pre/post-call), argument fixes | Same bytes |
| ☑ 4.4 | Code-space extension | Same bytes |

Progress (2026-10-08):
- `Patching` (caves, `FindCave`, `PltEntry`, `RedirectPlt`, `PlaceChain` with chained caves and post-call `call_got`, `RenameImport`, `DefineSymbol`, `StubImports`, `ExtendText`) and `FixApplier` (`StubRules`, `LocalExport`, `Applicable`, `Apply`). SDK read/write came with Phase 2.
- **Byte parity with `ps5bp`**: every applicable known fix on every NOT_BACKPORT module (eboot: 3 adapters incl. the audio post-call and split chains, 6 stubs; libc: swap, redirect, 4 SyncOnAddress adapters, 4 stubs) gives identical bytes, descriptions and stub records; the same on a libc with all padding used up and the code-space fallback allowed; symbol-defining stubs on a fakelib incl. a data (RELA) import and a `ret:<value>` rule. Error cases match.
- **Bug found in both engines:** the code-space fallback filled the page gap as one long int3 run. A cave must start after a `ret`, and placed code ends with a `jmp`, so the whole extension served only one fix. It is now filled with 64-byte blocks (`ret` + 63 `int3`), one cave each (61 usable bytes; the largest fix needs 25). Normal padding is unchanged, so the GT7 reproduction is unaffected. New Python regression test: all 10 NOT_BACKPORT libc fixes fit into the extension.
- **54 C# tests and 47 Python tests pass, 0 skipped.**

### Phase 5: Pipeline + CLI parity
| # | Task | Done when |
|---|---|---|
| ☑ 5.1 | Whole-game backport (store lookup + verify, providers, known/candidate/requested fixes, overlay writing, param patch) | Output folder byte-identical to `ps5bp backport` on NOT_BACKPORT (FW 4) and Control |
| ☑ 5.2 | Reports: same `backport_report.json` schema; txt report | JSON equal to Python's |
| ☑ 5.3 | CLI commands: info, unwrap, sign, sdk, report, stub, backport, fwdb, fakelib, bps | Help + smoke tests |

Progress (2026-10-08):
- `Pipeline.Backport` (param.json, ampr warning, store lookup + verify + build warnings, module discovery and fakelib replacement, unwrap, providers, SDK patch, known fixes, candidates, requested/unknown stubs, re-classification, unresolved list with refs / candidate / left-by / introduced, signing, verbatim fakelib copies) with `IProgress<BackportProgress>` and cancellation for the GUI; `FormatReport`, `ToJson`, `WriteReports` (same CRLF text as ps5bp on Windows).
- CLI `ps5backport`: info, unwrap, sign, sdk, nid, report, stub, backport, fwdb (import-genstub/import-dump/list/diff), bps (info/apply), fakelib (add/list/verify/check), same options and output text as `python -m ps5bp`.
- **Output folders byte-identical to `ps5bp backport`** (every module, copied fakelibs, lowered param.json, both reports) for: NOT_BACKPORT → FW 4; NOT_BACKPORT → 4.03 with patch-param, candidate fixes, stub-unknown, define-symbol and extend-text; Control → 4.03; NOT_BACKPORT without the store (error path for the empty libSceAmpr). CLI stdout (and written files) equal Python for info, report, fwdb list/diff, bps info, fakelib list/verify/check, sdk, stub, sign, unwrap.
- `ps5bp stub` now uses every known stub body (`fixes.stub_rules()`), as `backport` does; before it only knew `patch.RULES`.
- C# CLI backports the GT7 dump in about 19 s.
- **64 C# tests and 47 Python tests pass, 0 skipped.**

### Phase 6: GUI MVP (the Kitchen replacement)
☑ **6.0 design review** (2026-10-08): four mock-ups shown; chosen: the **Fluent workbench** = B's workbench + A's game header + C's firmware-gap bar.
- Layout: icon nav rail (Library, Backport, Fakelibs, Firmware DB, Console, Settings); game header (cover from `sce_sys/icon0.png`, name, title ID, region from the content-ID prefix UP/EP/JP/HP, version, SDK, required FW, firmware-gap bar, target picker, Cook button); tabs Overview / Modules / Imports / Report; Options flyout; collapsible inspector for the selected import; docked development log (Errors / Warnings / All counts, copy, click to jump to the module).
- Style: Avalonia Fluent theme, follows Windows light/dark, system (calm blue) accent only for the primary action and selection; color only for status (green stubbed/OK, blue patched, amber unknown/warning, red error); Mica backdrop where available; virtualized import grid; remembered window/panel sizes.

| # | Task | Done when |
|---|---|---|
| ☑ 6.1 | Game panel: browse / drag-drop a game folder (drop added 2026-10-09: a folder or a file in it, anywhere on the Backport page); title, ID, version, icon (`sce_sys/icon0.png`), SDK, required FW | Shows GT7 and Control correctly |
| ☑ 6.2 | Target: firmware picker (store sets + DB coverage shown: exact / lower bound), options (candidate fixes, stub unknown, extend text, patch param.json), output folder (default `<game>_backport_<FW>`) | Options reach the pipeline |
| ☑ 6.3 | **Analyze**: module list (container, SDK before → after, counts) and an import grid per module (status, NID, name, library, refs, introduced, left-unpatched-by, fix that will apply), filters and search | GT7 eboot grid usable (virtualized) |
| ☑ 6.4 | **Cook**: run the pipeline async with progress, cancel, live log; result summary with warnings, derived-fix NOTE, errors | Same output as CLI |
| ☑ 6.5 | Report viewer + "open output folder" + copy-to-clipboard | — |
| ☑ 6.6 | Settings: paths (fakelib store, firmware_db, aerolib), defaults; persisted per user | — |

Progress (2026-10-08):
- `src/Ps5Backport.App` is now **Avalonia 12.1** (`Ps5BackportStudio.exe`, net10.0): Fluent theme following Windows light/dark with the system accent; navigation rail; game header (real `icon0.png` cover, title, ID, region, version, SDK, firmware-gap bar, "PS VR2" badge after Analyze when the game imports the headset libraries (added 2026-10-09), target picker, fakelib set, Open game / Options flyout / Analyze / Cook); tabs Overview (summary cards, reference, warnings, output), Modules, Imports (search, Unresolved/Changed/All, per-module filter, 2,384 GT7 imports), Report; inspector (applied fix, known fix with proven flag, sources, note and rationale, AnyPS5 candidate, left-unpatched evidence, introduced-in, code references); docked development log (Errors/Warnings/All counts, copy, clear, double-click jumps to the module, cancel + progress).
- Analyze = the pipeline with `DryRun` (nothing written); Cook writes the overlay and reports. Both run off the UI thread with progress and cancel.
- Core additions: `GameScan.ReadInfo` (title from localizedParameters, region from the content ID, required FW / SDK as versions), `BackportOptions.DryRun`, `ModuleResult.Statuses` (every import, not in the JSON report).
- Tables use Avalonia 12.1's built-in **TableView** (the DataGrid package is deprecated).
- `Ps5BackportStudio.exe --snapshot OUT.png [--game DIR] [--analyze] [--tab N] [--select-import NAME] [--dark]` renders the window headlessly (Skia) for UI review without a display.
- **75 tests pass, 0 skipped** (new: region, FW parsing, GT7 header info, dry run writes nothing yet gives the same 19 fixes / 37 unresolved).
- 6.6 (2026-10-09): `AppSettings` (per-user JSON in %APPDATA%/PS5BackportStudio: window size/position/maximized, log height, target firmware, options, last game reopened at start, 10 recent games, path overrides used by the view model); Mica backdrop on Windows 11 (Acrylic on 10, solid theme background otherwise). Settings page (2026-10-09, layout A "grouped rows" chosen from two mock-ups): knowledge folders (firmware DB, fakelib store, aerolib.csv) with path, automatic/custom tag, status (list count, store sets, found) and Change/Reset, which reloads the firmware picker, fakelib text and names; defaults bound to the same values as the Options flyout (target firmware + six toggles); Forget recent games; Open settings folder. Snapshot flag `--settings`. **85 C# tests pass** (3 new: path override/reset, missing folders warn, forget recent games).
- Sample folders moved to `.original/backports/<Game>/{default,4.00+,decrypted}`; both test suites updated. `firmware_db/` now holds 43 exact lists from the user's dumps (REFERENCE_OVERVIEW Part 13): FW 4 targets are classified exactly (GT7: 19 fixes, 2 truly missing PSVR2 functions).
- **82 C# tests and 49 Python tests pass, 0 skipped.**
- Optional patches (2026-10-09, placement B chosen from two mock-ups): Overview tab section "Optional patches" listing `data/fixes.json` options (name, description, warning, sources, toggle; an enabled card gets a warning outline). Chosen per game: reset when another game is opened, not saved in settings. Snapshot flag `--option KEY` (repeatable). Samples now live in `.original/backports/<TITLE_ID>/`.

### Phase 7: Tools inside the app
Design (2026-10-09): list and detail layout (A) for the store and the firmware DB; the BPS patcher lives in the store ("Apply BPS patch"); SELF and NID tools share a Tools page.

| # | Task |
|---|---|
| ☑ 7.1 | Fakelib store manager: list sets, add files (with source note), verify, check against a firmware, build dates, known-broken warnings |
| ☑ 7.2 | Firmware DB manager: list, import (genstub folder / decrypted dump), diff two firmwares |
| ☑ 7.3 | BPS patcher: info view of a patch, apply to a library (CRC-checked), optional sign, add to store |
| ☑ 7.4 | SELF tools: info, unwrap, sign, batch |
| ☑ 7.5 | NID tool: name → NID, NID → name, symbol split |

Progress (2026-10-09):
- Navigation: one `MainViewModel.Page` (`AppPage`) drives the rail (`PageIsConverter`); the disabled Library and Console placeholders were removed (Console was Phase 8, dropped; Library was never planned).
- 7.1 `StoreViewModel` + `StoreView`: sets with library count and verify/known-broken problems; per library container, SDK, build date, hash state, source, build path, SHA-256; "Check imports against" any firmware_db list (defaults to the set's own firmware, runs off the UI thread); Add flyout (firmware + source note; a new firmware makes a new set; the picker gets the new set).
- 7.3 in the store: choose patch (shows expected size/CRC and edit count) and library (SELF unwrapped first), optional fake-sign, added with an automatic source note. **Rebuilding `fakelibs/7/libSceAgc.sprx` from BackPork's 7xx patch and the 10.01 dump through the page is byte-identical to the stored file**; a wrong library is refused (CRC) and nothing is added.
- 7.2 `FirmwareDbViewModel` + `FirmwareDbView`: all lists loaded off the UI thread; per module exports, added, removed against any other firmware (default: the next newer one), changed-only filter, search by module, NID or function name; inspector lists the added/removed functions by name; Import flyout (dump or genstub, source note required).
- Snapshot: `--page settings|store|firmwaredb`.
- 7.4/7.5 `ToolsViewModel` + `ToolsView` (new rail item): SELF/ELF list (add or drop files; kind, module, SDK, import/export counts; encrypted and non-module files flagged); Unwrap (`<name>.elf`) and Fake-sign (`<name>` without `.elf`, else `<name>.self`) for the whole list, next to each file or into a chosen folder, **never overwriting**; NID lookup for several words at once: name → NID, NID → name, `NID#lib#mod` split, each with first firmware, known fix (proven or not), AnyPS5 candidate and left-unpatched evidence. An 11-character word counts as a NID when aerolib, the knowledge base or a firmware list knows it (e.g. libkernel `La9uyZv4Kvw`, not in aerolib).
- **93 C# tests pass** after 7.4/7.5 (2 more: unwrap → sign round trip with the no-overwrite rule and batch de-duplication; NID lookup in both directions with knowledge).
- Earlier: **91 C# tests pass** (6 new: add/verify/tamper, 4.03 set fully resolved on 4.03, BPS byte-exact rebuild, BPS wrong library, 6.50→7.01 adds `sceKernelIsTrinityMode`, genstub import).

### Phase 8: Console integration: dropped (2026-10-09)
Not needed: backports are shared with a group of testers who run them on their consoles and report back. No FTP upload, log fetch or payload sender in the app.

### Phase 9: Release: dropped (2026-10-09)
Not needed: private tool (no installer, single-file publish, user guide or problem-report bundle). Run it with `dotnet run --project src/Ps5Backport.App`.

---|---|
| 9.1 | Self-contained single-file publish (win-x64), version stamping |
| 9.2 | User guide (README for the app), credits (make_fself, BackPork patches, AnyPS5 knowledge) |
| 9.3 | Crash log + "report a problem" bundle (report json + logs, no game binaries) |

---

## 4. Rules for the port
- **Generic, not per game** (memory rule): fixes stay per library function in `data/fixes.json`.
- **Python stays the reference** until a C# component has parity; after that both must agree (parity tests in CI).
- **Record findings** in REFERENCE_OVERVIEW.md as we go.
- No Sony binaries in git, in the app, or downloaded by it.

## 5. Decisions from you (2026-10-08)
1. **Private software:** no license file or license work; keep third-party credits in About.
2. **Name:** "PS5 Backport Studio".
3. **Kitchen extras** (PKG/FPKG builder, UFS2 image, PUP extraction, game library, cover art): none for now.
4. **GUI must look different from Kitchen:** design options shown; you chose the Fluent workbench, the calm blue accent, follow-Windows light/dark, and **Avalonia UI**.
