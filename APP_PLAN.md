# PS5 Backport Studio: GUI application plan

A Windows desktop app at the repo root that does what PS5 Backpork Kitchen set out to do, plus everything `ps5bp` already does better: per-firmware import analysis, generic fixes, fakelib sets, overlay output and reports.

*Name: "PS5 Backport Studio" (namespace `Ps5Backport`). Private software.*

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
| SDK table (FW 1–10), minor versions, param.json version | `sdk.py` | GT7 (4), Control (4.03) |
| Per-firmware export DB (exact vs lower bound), import from stubs/dumps, diff, introduced-in | `fwdb.py` | 3.20 list |
| Import classification (reference/provided/stubbed/patched/data/missing/unknown), code-reference scan | `imports.py` | GT7, Control |
| Patching: stubs, int3 caves, chained caves, PLT redirect, NID swap, symbol definition, code-space extension | `patch.py` | GT7 byte-identical where SDK-only |
| Fix table (stub/swap/adapter/redirect, argument fixes, post-call), provenance, left-unpatched evidence | `fixes.py`, `candidates.py` | GT7, Control, AnyPS5 |
| Fakelib store (manifest, verify, check, build dates, known-broken builds) | `store.py` | GT7/Control sets |
| BPS patches (BackPork) | `bps.py` | BackPork 6xx/7xx |
| Whole-game pipeline → ShadowMountPlus overlay + txt/json report | `game.py` | NOT_BACKPORT → GT7 |

Knowledge lives in **REFERENCE_OVERVIEW.md** (Parts 1–12), **PLAN.md** (engine phases), **CONSOLE_BENCH.md** (13.60 bench).

### 1.3 Decisions

| Decision | Choice | Why |
|---|---|---|
| Language/UI | **C# / .NET 10 / WPF** (MVVM with CommunityToolkit.Mvvm) | .NET 10 SDK is installed; Kitchen proved a .NET Windows desktop app fits the audience. C# is far faster to build a rich UI in than C++ (Qt/ImGui) and the engine is byte manipulation, where C# `Span<byte>`/`BinaryPrimitives` are as good as C++. WPF: mature data grids, virtualization for 50k-row import tables, no extra UI dependency. |
| Engine | **Port `ps5bp` to a C# library** (`Ps5Backport.Core`), no Python at runtime | Users get one self-contained app. |
| Correctness | **`ps5bp` is the oracle**: every ported function has a parity test that runs `python -m ps5bp` (or the Python module) on the same input and compares bytes/JSON | We already proved `ps5bp` against console-tested backports; the port inherits that proof. Tests skip when Python or the sample files are missing. |
| Knowledge data | Shared files at the repo root: `firmware_db/*.json`, `fakelibs/<FW>/manifest.json` (existing formats), and **`data/*.json`** for fixes, candidates, left-unpatched (Phase 2), read by both engines | One source of truth; new fixes from game ports reach both. |
| CLI | `Ps5Backport.Cli` with the same commands as `ps5bp` | Scripting, CI parity runs, and debugging without the GUI. Cross-platform (`net10.0`). |
| License | **None: private software** (your decision) | Credits for third-party work stay in About. |
| Sony binaries | **Never shipped or downloaded** by the app | Fakelibs and firmware libraries come from the user's own dumps via the store (as now). Export lists (NIDs) are fine. |
| Output | ShadowMountPlus overlay (changed files only) + reports; the game folder is never modified | Safer than Kitchen's in-place patching + backup; matches how backports are run today. |

### 1.4 Scope versus Kitchen
**In:** analysis + fixes (beyond Kitchen), SDK/sign, fakelib store, firmware DB, BPS, reports, FTP upload of overlays and log download, payload sender, game library list, settings.
**Out for now** (revisit after Phase 9): PKG/FPKG and UFS2 builders, PUP download/extraction, cover-art download, statistics DB, themes/localization, auto-update, in-place patching.

---

## 2. Architecture

```
Ps5Backport.slnx
src/
  Ps5Backport.Core/      net10.0       engine: formats, knowledge, analysis, patching, pipeline (no UI, no I/O policy)
  Ps5Backport.Cli/       net10.0       command line, parity with ps5bp
  Ps5Backport.App/       net10.0-windows WPF, MVVM; uses Core only
  Ps5Backport.Tests/     net10.0       xUnit: unit + parity (oracle) tests
data/                    shared knowledge JSON (Phase 2)
firmware_db/  fakelibs/  existing shared stores
ps5bp/  tests/           Python engine + its tests (oracle, research tool)
```

Core namespaces mirror `ps5bp`: `Nid`, `Elf`, `Self` (fself), `Sdk`, `FirmwareDb`, `Imports`, `Patching`, `Fixes`, `Fakelibs`, `Bps`, `Pipeline`.
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
| 2.1 | `data/fixes.json`, `data/candidates.json`, `data/left_unpatched.json` exported from Python (`tools/export_data.py`); Python switched to read them | Python tests still pass; files committed |
| 2.2 | C# loaders for `data/*.json`, `firmware_db/*.json` (load, select exact/lower bound, introduced-in, import from genstub/dump, diff) | Parity with `fwdb` |
| 2.3 | Fakelib store: find (exact → major), manifest add/verify, build-date detection, known-broken builds, store check | Same manifests as Python |

### Phase 3: Analysis engine
| # | Task | Done when |
|---|---|---|
| 3.1 | Import classification (all statuses, provided exports, exact vs lower bound) | Identical status lists for GT7, Control, NOT_BACKPORT |
| 3.2 | Code-reference scan | Same counts as Python |
| 3.3 | Game scan: module discovery, `param.json` (title, version, required FW), ampr index warning, encrypted input | Same warnings as Python |

### Phase 4: Patch engine
| # | Task | Done when |
|---|---|---|
| 4.1 | SDK read/write | Same bytes |
| 4.2 | Caves, chained caves, stubs (incl. `ret:<value>`), symbol definition | Same bytes as Python for each fix |
| 4.3 | NID swap, PLT redirect, adapters (pre/post-call), argument fixes | Same bytes |
| 4.4 | Code-space extension | Same bytes |

### Phase 5: Pipeline + CLI parity
| # | Task | Done when |
|---|---|---|
| 5.1 | Whole-game backport (store lookup + verify, providers, known/candidate/requested fixes, overlay writing, param patch) | Output folder byte-identical to `ps5bp backport` on NOT_BACKPORT (FW 4) and Control |
| 5.2 | Reports: same `backport_report.json` schema; txt report | JSON equal to Python's |
| 5.3 | CLI commands: info, unwrap, sign, sdk, report, stub, backport, fwdb, fakelib, bps | Help + smoke tests |

### Phase 6: GUI MVP (the Kitchen replacement)
Before 6.1: **6.0 design review**, present 2–3 visual/layout directions (mock-ups) that look clearly different from Kitchen; you choose; then build. The Phase 0 shell is a placeholder only.

| # | Task | Done when |
|---|---|---|
| 6.1 | Game panel: browse / drag-drop a game folder; title, ID, version, icon (`sce_sys/icon0.png`), SDK, required FW | Shows GT7 and Control correctly |
| 6.2 | Target: firmware picker (store sets + DB coverage shown: exact / lower bound), options (candidate fixes, stub unknown, extend text, patch param.json), output folder (default `<game>_backport_<FW>`) | Options reach the pipeline |
| 6.3 | **Analyze**: module list (container, SDK before → after, counts) and an import grid per module (status, NID, name, library, refs, introduced, left-unpatched-by, fix that will apply), filters and search | GT7 eboot grid usable (virtualized) |
| 6.4 | **Cook**: run the pipeline async with progress, cancel, live log; result summary with warnings, derived-fix NOTE, errors | Same output as CLI |
| 6.5 | Report viewer + "open output folder" + copy-to-clipboard | — |
| 6.6 | Settings: paths (fakelib store, firmware_db, aerolib), defaults; persisted per user | — |

### Phase 7: Tools inside the app
| # | Task |
|---|---|
| 7.1 | Fakelib store manager: list sets, add files (with source note), verify, check against a firmware, build dates, known-broken warnings |
| 7.2 | Firmware DB manager: list, import (genstub folder / decrypted dump), diff two firmwares |
| 7.3 | BPS patcher: info view of a patch, apply to a library (CRC-checked), optional sign, add to store |
| 7.4 | SELF tools: info, unwrap, sign, batch |
| 7.5 | NID tool: name → NID, NID → name, symbol split |

### Phase 8: Console integration (CONSOLE_BENCH.md)
| # | Task |
|---|---|
| 8.1 | Console profiles (IP, ports) |
| 8.2 | FTP (FluentFTP, MIT, as Kitchen uses): upload the overlay to `/data/homebrew/backports/<TITLE_ID>/`, show the 256-redirect limit, delete a stale `ampr_emu.index` (with confirmation) |
| 8.3 | Log fetch: `/data/shadowmount/debug.log` (+ `.1`), `notify.txt`, `/app0/ampr_emu.log`; viewer with error highlighting |
| 8.4 | Payload sender (port 9021) for user-supplied payloads (kstuff-lite, ShadowMountPlus) in the documented order |
| 8.5 | Test record: per run, store game, FW, fixes, result, logs (feeds PLAN Phase 1.4) |

### Phase 9: Release
| # | Task |
|---|---|
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
4. **GUI must look different from Kitchen:** before any GUI design work (Phase 6), present design options to you and wait for your choice.
