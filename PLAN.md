# Backport project: phase plan

Goal: a **generic** PS5 backport tool (`ps5bp`). Its knowledge is per **system-library function** (`ps5bp/fixes.py`), never per game. GT7 (FW 4) and CONTROL Resonant (FW 4.03) are the ground-truth samples. Background and findings are in [REFERENCE_OVERVIEW.md](REFERENCE_OVERVIEW.md).

Status legend: ☐ todo · ◐ in progress · ☑ done

---

## Where we are (2026-10-08)
- `ps5bp backport GAME OUT --firmware X` runs the whole pipeline:
  - unwrap/sign (byte-exact round trip)
  - SDK fields (PS5+PS4, minor versions)
  - import classification against 3.20 plus shipped modules
  - proven fixes: stubs, swaps, adapters, redirects, argument fixes
  - opt-in candidates
  - per-firmware fakelib store
  - `param.json` (opt-in)
  - overlay output and a report
- 32 regression tests against the GT7 and CONTROL samples.
- **Not yet run on a console.** Derived (non-proven) fixes: `sceSaveDataMount3`/`sceSaveDataPrepare` argument adapters, the `sceAudioOut2GetSpeakerInfo` post-call fix, generic `SyncOnAddressWait` → Wait32 translation, AnyPS5 candidates, multi-cave chains.

---

## Phase 0: Tool hardening (no console needed)
Make the tool safe and clear before real games go through it.

| # | Task | Done when |
|---|---|---|
| ☑ 0.1 | **Stale `ampr_emu.index`**: warn when the game folder has `ampr_emu.index`/`.tmp` (CONTROL README: delete it on the console; the emulator rebuilds it). An overlay can't delete files, so the report must tell the user. | Warning in report; test |
| ☑ 0.2 | **Encrypted input**: detect truly encrypted SELF segments and stop with "decrypt first (PS5-SELF-Decrypter)" instead of failing deep in the unpacker. | Clear per-module error; test with a forged encrypted flag |
| ☑ 0.3 | **Fix provenance in the report**: mark every applied fix **proven** (a console-tested backport in its sources) or **derived** (references only), and say which fix failed when padding runs out. | Report lines tagged; test |
| ☑ 0.4 | **Version control** (optional, needs the user's OK): `git init` with `.original/`, `fakelibs/`, `work/` ignored. | Repo initialised |

**Exit:** all tests pass; the report tells a user everything they must do by hand.

Progress (2026-10-08): 0.1–0.3 done. `fixes.Fix.proven` marks the 4 derived fixes (generic SyncOnAddressWait, Mount3/Prepare argument fixes, GetSpeakerInfo); the report tags every fix and lists the derived ones in a NOTE. Encrypted SELFs fail with a 'decrypt first' message (all 63 sample SELFs are unencrypted). 26 tests pass (`tests/test_phase0.py` added). 0.4 waits for the user's OK.

---

## Phase 1: Console test bench
Prove `ps5bp` output on real hardware and promote derived fixes to proven.

| # | Task | Done when |
|---|---|---|
| ☑ 1.1 | Decide the test console set-up. The user's PS5 is on **FW 13.60**: Relapse (7.00–13.60, tethered; press reports) gives kernel R/W. Check kstuff-lite and ShadowMountPlus support for 13.60. **Do not update to 14.00.** | Written set-up guide |
| ☐ 1.2 | **Smoke test on 13.60**: backport GT7 with `ps5bp` (FW 4 target) and run it on 13.60. Every library exists there, so this tests that our *patching* (signing, caves, adapters, argument fixes) loads and runs, not 4.xx compatibility. | Game boots, saves, has sound |
| ☑ 1.3 | Log collection: ShadowMountPlus `debug.log`, klog over the network, crash dialogs (`PRX_NOT_RESOLVED_FUNCTION` …). | Checklist in this file |
| ◐ 1.4 | Test matrix per derived fix (save/load for argument fixes, audio for GetSpeakerInfo, threading for SyncOnAddress). On success, add the test to the fix's `sources` (derived → proven). | `fixes.py` updated |
| ☐ 1.5 | Find testers on real low-firmware consoles (4.xx/6.xx/7.xx), e.g. the ShadowMountPlus/BackPork community. | At least one low-FW test report |

**Exit:** one `ps5bp`-made backport confirmed running; derived fixes either proven or fixed.

Progress (2026-10-08): set-up guide, log checklist and test matrix in [CONSOLE_BENCH.md](CONSOLE_BENCH.md): Relapse → elfldr 9021 → kstuff-lite 1.11 → ShadowMountPlus 1.7beta3+ (no standalone BackPork). Overlay goes to `/data/homebrew/backports/<TITLE_ID>/`. Waiting on the user for 1.2.

---

## Phase 2: Firmware knowledge
Replace "3.20 lower bound" guesses with real per-firmware data.

| # | Task | Done when |
|---|---|---|
| ☑ 2.1 | Per-firmware export database format (ps4libdoc-style JSON: firmware → module → library → NIDs), with loaders in `ps5bp.imports`. | Format + loader + test |
| ◐ 2.2 | Import `PS5-3.20_Libs` into it, and the 13.60 set once dumped (2.3). | 3.20 (and 13.60) in the DB |
| ☐ 2.3 | Dump 13.60 `/system/common/lib` export lists: PS5-SELF-Decrypter needs 13.60 kernel offsets (string heuristics in its README). Export lists are NIDs, not Sony code. | 13.60 export list |
| ☑ 2.4 | Three-way classification: on the target FW (present) / only on newer FW (missing → needs a fix) / nowhere (shipped module or unknown). Use the exact target list when available. | Report uses exact data |
| ◐ 2.5 | "Introduced in FW" knowledge per function (e.g. `sceKernelIsTrinityMode` from 7.61, per CONTROL README), recorded in the DB. | Field + entries |

**Exit:** the report says *missing on FW X* from data, not from the 3.20 proxy.

Progress (2026-10-08):
- `ps5bp/fwdb.py` + `firmware_db/<fw>.json` (NID lists only; committed). `python -m ps5bp fwdb import-genstub|import-dump|list|diff`.
- `firmware_db/3.20.json` holds 275 modules and 31,275 NIDs, functions only.
- Selection: an exact database makes absent imports **`missing`**; otherwise the highest older firmware is a **lower bound** (`unknown`).
- `backport`/`report`/`stub` use `firmware_db/` by default, and the report states the reference used. `import-dump` is ready for the 13.60 dump (2.3).
- 2.5: the report annotates missing/unknown imports with the first firmware known to export them (newer databases, else `fwdb.KNOWN_INTRODUCED`, currently `sceKernelIsTrinityMode` from 7.61).
- 32 tests passed at the end of Phase 2 (`tests/test_fwdb.py`).
- Remaining: 2.3 needs the console (13.60 dump) or community export lists; any other firmware list found can be imported the same way.

---

## Phase 3: Fakelib supply
Every target firmware needs a working fakelib set.

| # | Task | Done when |
|---|---|---|
| ☑ 3.1 | BPS patch support in `ps5bp` (BackPork ships BPS patches: 10.01 libraries → 6xx/7xx). | `ps5bp fakelib patch-bps` + test against BackPork's patch files |
| ☑ 3.2 | Fakelib dependency check: each fakelib's own imports must be satisfied on the target (DB from Phase 2 + other fakelibs); run fixes on fakelibs like any module. | Report section per fakelib |
| ☐ 3.3 | Store sets for 6.xx and 7.xx (from 10.01 libraries + BackPork patches) and document their provenance in manifests. | `fakelibs/6`, `fakelibs/7` |
| ☑ 3.4 | `ampr_emu`/PlayGo versions: record in the manifest which build is used (CONTROL: the 6 Sep build broke it, 24 Aug works). | Manifest field |

**Exit:** `backport --firmware 6|7|4` finds a verified set.

Progress (2026-10-08):
- 3.1: `ps5bp/bps.py` + `python -m ps5bp bps info|apply` (CRC-checked; `--sign`). Decoding the BackPork patches shows SDK patches, `.dynsym` `st_shndx=1` redefinitions and ret0 stubs (REFERENCE_OVERVIEW Part 10).
- 3.2: `python -m ps5bp fakelib check --firmware X`; `backport` already runs fixes on fakelibs. The console-tested sets leave 4 kernel/VideoOut imports unresolved (tolerated on FW 4).
- 3.4: manifest `build_date` (compiler `__DATE__`), `store.KNOWN_BUILDS` warns on the broken ampr_emu build.
- 40 tests pass (`tests/test_phase3.py`).
- Remaining: 3.3 needs the FW 10.01 libraries (console dump) for libSceAgc, libSceAgcDriver, libSceFiber, libSceNpAuth, libSceNpAuthAuthorizedAppDialog, libScePsml, libSceSaveData.native; the patches' CRC32 identifies the exact files.

---

## Phase 4: Fix coverage and robustness
| # | Task | Done when |
|---|---|---|
| ◐ 4.1 | Review the remaining *unknown* imports of each analysed game: decide stub / adapter / leave lazily unresolved (call-site reachability via Ghidra xrefs). | Decisions in `fixes.py` |
| ☐ 4.2 | Optional precise `_umtx_op` timeouts (real `timespec`), console-tested before becoming the default. | Test result |
| ◐ 4.3 | Code-space fallback when a module has no padding left: extend the code segment into the page gap; needs a console test. | Implemented + tested, or documented as unsupported |
| ◐ 4.4 | Shim PRX option (`backport-helper` style) for fixes too big for caves. | Decision + prototype |
| ☑ 4.5 | Re-check NOT_BACKPORT's three pre-existing patches (read-only `mount_mode`, `_Mtx_lock`/`_Mtx_unlock` guards): are they general dump requirements? | Rule added or ruled out |

Progress (2026-10-08):
- 4.1: the report shows `refs=N` (direct code references, `imports.code_references`) and `left unpatched by:` (`fixes.LEFT_UNPATCHED`, 40 NIDs recomputed from the GT7/Control samples) for every unresolved import. Ghidra: PSVR2 is gated by `sceHmd2Initialize` (exists on 3.20); TTS2 by its own missing `Initialize` (the AnyPS5 error stub disables it); `D-CzAxQL0XI` is called unconditionally at startup, so GT7's test firmware exported it (likely above 4.xx). Remaining: whether a missing `DT_NEEDED` module blocks loading, and a stub decision for `D-CzAxQL0XI` once a 4.xx export list shows it missing (REFERENCE_OVERVIEW Part 11).
- 4.5: ruled out. `mount_mode = RDONLY` changes GT7's own save logic in one function; the `_Mtx` guards appear in no Control module, though Control also runs from a dump.
- 4.3: `patch.extend_text` grows the code segment into the unused bytes before the next 16 KB page (GT7 libc 0x812, eboot 0x18d4, Control eboot 0x2084, nakama 0x1c62; page-exact modules like Psml have none). Used only with `backport --extend-text` when no padding is left; the report notes it. Signs and unwraps byte-exactly. **Needs a console test** before it becomes a default.
- 4.4: decision note below; waiting on the user.
- 45 tests pass (`tests/test_phase4.py`).

#### 4.4 decision: shim PRX vs cave patches
| | Cave patches (current) | Shim PRX (`backport-helper` style) |
|---|---|---|
| What | stubs/adapters written into the module's own int3 padding (or the page gap, 4.3) | a separate module that exports replacement functions and/or hooks existing ones at `module_start` |
| Size limit | padding (≤16 bytes per run in eboots, chained) + page gap (2–8 KB) | none: real C code |
| Build | Python only, no SDK | needs a toolchain that emits **SCE PRX** (NID exports, SCE dynamic tags). `backport-helper` and ampr_emu use Sony's Prospero SDK (can't use or ship). ps5-payload-sdk only emits `.so` for its own loader, so we'd need our own `.so` → SCE PRX converter. |
| Loading | nothing extra | must be loaded: replace a fakelib the game already loads (as backport-helper replaces `libSceRtc`), or add a `DT_NEEDED` entry to the eboot (dynamic section rewrite) |
| Risk | small, local, byte-diffable | larger: runtime hooks patch code in memory; must match each firmware's library |

Recommendation: **keep cave patches as the default**; with 4.3 they cover every fix we have (the largest, the `sceSaveDataMount3` argument fix, is 25 bytes including its tail jump). Build a shim only when a game needs a fix that is real code (e.g. reimplementing a missing function), and then first test whether a homebrew-built PRX loads at all. Decision needed from the user.

---

## Phase 5: Game ports (repeat per game)
The per-game workflow. Each game feeds generic rules back into the tool; nothing game-specific stays in the tool.

1. **Intake:** decrypted dump (fake-signed or plaintext ELFs), `param.json` (title, SDK, required FW), target firmware.
2. **Report:** `python -m ps5bp backport GAME OUT --firmware X` and read the unknown imports and warnings.
3. **Triage each unknown import:** is it reachable at runtime (Ghidra xrefs)? What does AnyPS5 say it returns? Is it in a newer firmware only?
4. **Generalise:** add the decision to `fixes.py` per *library function*, with the game as its source; candidate → proven after a console test.
5. **Build + test on console** (Phase 1 bench); collect logs.
6. **Record:** a short section in REFERENCE_OVERVIEW.md (as for GT7/CONTROL) and a regression test.

**Exit per game:** runs on the target firmware; all its new knowledge lives in `fixes.py` and the tests.

---

## Order of work
Phase 0 now → Phase 1 (gated on the user's console set-up) in parallel with Phase 2.1–2.2 → Phase 3 → Phase 5 games (Phase 4 items are pulled in as games need them).
