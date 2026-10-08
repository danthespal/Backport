# Console test bench (PLAN Phase 1)

Bench console: the user's PS5 on **FW 13.60**. Do **not** update to 14.00 (Relapse doesn't cover it).
On 13.60 every system library exists, so a backport tested here proves our **patching** works:
signing, SDK fields, caves, adapters and argument fixes all load and run. It does not prove
4.xx compatibility (Phase 1.5).

Researched 2026-10-08 from community sources; versions change quickly, so recheck before use.

## 1.1 Set-up

| Step | What | Notes |
|---|---|---|
| 1 | **Relapse** exploit in the PS5 browser | FW 7.00–13.60, tethered (repeat after every reboot or rest mode). The WebKit stage may need several tries; a kernel-stage hang or reboot means retry. |
| 2 | ELF loader on **port 9021** | Relapse brings it up. Send payloads with `nc -w 10 PS5_IP 9021 < file.elf` if the host page stalls. |
| 3 | **kstuff-lite v1.11** | Release notes list FW 1.00–13.60. Needed to load fake-signed SELFs (our output). Send it **before** ShadowMountPlus. One guide claims v1.10 is needed for backups; unconfirmed. |
| 4 | **ShadowMountPlus 1.7beta3+** | "Firmware versions up to 13.60 are supported" (1.7beta1). Needs kstuff-lite ≥ 1.07. 1.7beta4 (03 Oct) adds per-game fakelib toggles and `fakelib_exclude`. |
| 5 | **Do not** run the standalone BackPork payload | Its fakelib mount conflicts with ShadowMountPlus (release notes). |

Avoid (community 13.60 guide): kstuff 1.6.7, ShadowMountPlus 1.7beta1, OnionHEN 0.0.13, and etaHEN 2.5B unless its 13.60 support is confirmed.

### Where our output goes
- Game: a decrypted dump (folder) in a ShadowMountPlus scan path, e.g. `/data/homebrew/<GAME>`.
- Backport overlay: `ps5bp backport` output → **`<scan_path>/backports/<TITLE_ID>/`**. With the default scan path that is `/data/homebrew/backports/<TITLE_ID>/`. Applied right before the game process starts. Regular files are redirected, missing directories are NSFS overlays, **max 256 redirects per sandbox**.
- `fakelib/` inside the overlay is used like a game's own `fakelib/`.
- Delete a stale `ampr_emu.index` / `ampr_emu.index.tmp` from the game folder (the report warns; the overlay can't delete files).
- ShadowMountPlus can auto-update `libSceAmpr.sprx` (`auto_update_ampr`, off by default). Keep it off while testing, so the ampr_emu build stays the one in our manifest.

### kstuff pause
ShadowMountPlus pauses kstuff after a tracked game launches (`kstuff_game_auto_toggle=1`; 15 s direct, 25 s image) and resumes it on exit. A game that loads a fake-signed module **after** that delay (sysmodule load, late prx) can then fail. If a backport crashes only later in play, retry with `kstuff_no_pause=<TITLE_ID>` in the ShadowMountPlus config.

## 1.2 Smoke test (first run)
1. `python -m ps5bp backport GT7_DUMP OUT --firmware 4` (fakelibs from the store).
2. Copy `OUT/*` to `/data/homebrew/backports/<TITLE_ID>/` (keep `backport_report.txt` beside it for reference).
3. Launch. Check: boots to menu, **saves** (GT7's pre-existing read-only mount patch may stop that; see REFERENCE_OVERVIEW Part 3), sound, online-free play for 10 minutes.
4. Record the result and logs in REFERENCE_OVERVIEW (Phase 1 results).

## 1.3 Log checklist
| Source | Where | What to look for |
|---|---|---|
| ShadowMountPlus log | `/data/shadowmount/debug.log` (previous run: `debug.log.1`); also the web UI log viewer and the local API `/api/v1/debug-log` (port 10101, loopback only) | which backport/fakelib paths were redirected, mount errors, kstuff pause/resume times |
| ShadowMountPlus toasts | `/data/shadowmount/notify.txt` | last notifications |
| Kernel log | the Relapse host's `klosrv` option (enable it with `ftpsrv`), read over the network | module load failures, unresolved symbol messages, crash dumps |
| Files | the Relapse host's `ftpsrv` option (FTP) | pull logs, push overlays |
| Crash dialog | on screen | write down the error code (e.g. `CE-105773-3` = a known ShadowMountPlus relaunch bug with folder games, issue #128; workaround: reboot or remount) |
| ampr_emu | `/app0/ampr_emu.log` (Control's ampr_emu build writes it) | AMPR emulation errors, index rebuilds |

For each test, record: game, version, target firmware, `ps5bp` commit, fakelib set and its manifest build dates, the derived fixes listed in the report NOTE, and the result.

## 1.4 Test matrix for derived fixes
| Fix | How to exercise it | Pass |
|---|---|---|
| `sceSaveDataMount3` system_blocks −1 → 0 | create a new save, save again, reload | save appears and reloads |
| `sceSaveDataPrepare` mode 2 → 1 | same | no save error dialog |
| `sceAudioOut2GetSpeakerInfo` post-call | play with TV speakers and with headphones | sound on both |
| SyncOnAddress Wait → `_umtx_op` | load screens, menus, 10+ min of play | no hangs |
| TTS2 candidate stub (`--candidate-fixes`) | enable the system screen reader, open menus | no crash (narration silently off) |

Fixes are chosen by the **target** firmware (`--firmware 4` against the 3.20 list), not the console's. A FW 4 backport therefore runs every stub, adapter and argument fix on 13.60 too, even though the real functions exist there. That makes 13.60 a valid bench for the mechanism and the fixed values; only "is it really missing on 4.xx" needs a low-FW console.

## 1.5 Low-firmware testers
Real 4.xx/6.xx/7.xx consoles are needed for the "missing function" fixes. Candidates: the ShadowMountPlus / BackPork community. Ask testers for the firmware version, `debug.log` and the crash code.

## Sources
- Relapse 7.00–13.60 and load order: https://www.superpsx.com/ps5-relapse-jailbreak-13-60-and-lower-complete-guide/
- 13.60 payload versions and what to avoid: https://github.com/cliffjohnson860/ps5-fw1360
- kstuff-lite releases: https://github.com/EchoStretch/kstuff-lite/releases
- ShadowMountPlus releases (backports path, 13.60, BackPork conflict): https://github.com/drakmor/ShadowMountPlus/releases
- Relaunch bug: https://github.com/drakmor/ShadowMountPlus/issues/128
- Local source: `.original/reference/ShadowMountPlus/include/sm_paths.h` (log paths, default scan path `/data/homebrew`)
