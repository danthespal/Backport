# Backport Studio findings

The single record of research, investigations, problems and solutions that are not part of the engine documentation. README.md keeps the engineering, verification and reference-project history. This file keeps what was learned from games, consoles and diagnostics.

Consolidated on 12 October 2026, before `tmp/` and the investigation folders in `work/` were deleted. Part 1 covers 11–12 October. Part 2 is the earlier work archive (`work/CONFIRMED_FINDINGS.md`, 10 October), merged here unchanged.

**Evidence standard.** Console observations, static binary facts and desktop test results are kept apart. A static import check or a passing desktop test is not a console compatibility result. Unless stated otherwise, console results come from the user's standard PS5 on FW 13.60, not from a physical FW 4.xx console.

**Surviving inputs.** The following remain in `work/`: the test fixtures (`*_elf`), `agc-4.03-patch/` (the `patch_agc.py` tool and ELFs that reproduce the shipped AGC patch) and `fakelib-backups/` (unpatched original `libSceAgc` for sets 4 and 4.03). Every other `tmp/…` or `work/…` path named here or in README is a former location. Its contents were deleted, and hashes identify files but cannot restore them.

---

# Part 1 — 11–12 October 2026

## 1. PPSA32935 Dynasty Warriors 3 Complete Edition Remastered: FW 4 build crashes, then saves fail

### 1.1 Launch crash: SDK-4 AGC workaround flags (fixed in the store)

- **Symptom.** The complete FW 4.00 cook on FW 13.60 showed a black screen, then crashed with `0xa0d0c006 GPU_FAULT_BAD_COMMAND_ASYNC`. The preceding packet error was `GPU Bad packet error: Bad opcode … opcode 0x00000000`. No CPU RIP was available (an asynchronous GPU fault). Complete 7.00 and 9.60 cooks of the same game worked.
- **Ruled out by console swaps.** Replacing the FW 4 AGC + AgcDriver, then AGC + AgcDriver + AMPR, with the working 9.60 files left the packet history and the fault unchanged. Psml was absent from every failing event's module list. The 7.00 cook shares the Psml-init skip, the Trinity stub, the memory adapter and the Wait32/Wait64 adapters, so those alone cannot explain a 4.00-only failure.
- **Mechanism (static, Sony AGC J03828141 / J03950001).** At init, AGC calls `sceKernelGetProsperoCompiledSdkVersion` (`GGeRJk1XdWc`), `getpid`, `sceKernelGetAppInfo` and `sceKernelTitleWorkaroundIsEnabled` with selectors `0x52`/`0x53`/`0x70`. It then keeps flag bits 0/1 **only when the process reports SDK major 4** (`cmp edx,0x04000000; cmovne eax,esi`). Bits 0/1 change AcquireMem packet emission. Bit 0 also grows `sceAgcDcbAcquireMemGetSize`/`sceAgcAcbAcquireMemGetSize` from 32 to 64 bytes, and one path emits an extra `0xc0065800` packet. Swapping the library alone cannot bypass this, because the eboot still reports SDK 4.

  | 4.00 AGC evidence | ELF VA |
  | --- | --- |
  | Compiled-SDK query | `0x8a71` |
  | SDK-major-4 compare / conditional clear | `0x8c1d` / `0x8c23` |
  | Flags store / variable | `0x8a89` / `0x45f9c` |
  | DCB / ACB AcquireMem flag reads | `0x3888`,`0x3896` / `0x222`,`0x22c` |
  | DCB / ACB AcquireMem size functions | `0xd680` / `0xd990` |

  The 9.60 AGC has the same logic at `0x8c0d`/`0x8c13`, variable `0x49f9c`.
- **Fix.** At VA `0x8c23` (file offset `0xcc23`), `0f 45 c6` (`cmovne eax,esi`) becomes `89 f0 90` (`mov eax,esi; nop`). This selects the SDK 7/9 calculation, clears bits 0/1 and keeps bit 2. It changes no SDK field or dependency, and it behaves differently only when a process reports SDK major 4. The fix is shipped in `fakelibs/4/libSceAgc.sprx` (SHA-256 `07a995b3…`) and `fakelibs/4.03/libSceAgc.sprx` (`73611ebe…`, built with unwrap → patch → sign, which reproduces the set-4 file byte for byte). Details are in `fakelibs/README.md`.
- **Status.** The set-4 file booted on FW 13.60, and a fresh Studio FW 4 build of PPSA32935 boots, saves and reloads there (Automatic Pro, Save mount + Save preparation compatibility). The 4.03 file is not console-tested, and neither file has been run on a physical FW 4.xx console. Whether those flags are *needed* on real FW 4 is unknown.
- **Repeat result.** Bisect test I (below) hit the same GPU crash later, using the backport's own unpatched AGC (`eace8ac1…`, identical to the old store file). That confirms the cause.

### 1.2 Never mix modules from different target cooks

Whole-eboot swaps between the 4.00 and 9.60 outputs gave `PRX_SCE_MODULE_LOAD_ERROR` in both directions. A hybrid (9.60 eboot/libc/graphics trio, the rest from 4.00) logged three `load_prx failed 0x63` and then a SIGSEGV at runtime RIP `0x49e21fc` (`mov rax,[rax+0xc]`, RAX 0). EOS/KITT modules were missing from its module list. An eboot-only SDK 12 build also failed: `0xa0020102` "Lack of a .prx file in /app0/sce_module". A process at SDK 12 rejects SDK 4 `sce_module` libraries. **Mixed-SDK module sets do not load.** Diagnostic tests must change a whole coherent set, or vary one rule inside one cook.

### 1.3 Save failures: DirNameSearch `0x809f8007` (resolved, console state)

- **Symptom.** The booting FW 4 build saved with CE-107098-5, or its saves did not reload. The kernel log showed every `sceSaveDataDirNameSearch` (api_id 6) failing with `ExecResult 0x809f8007`. The game's search callers turn any nonzero return into "0 saves" (e.g. VA `0x5f6a201..216`), and its commit/unmount wrapper at `0x5f78600` discards both returns. So a "Saving" message proves nothing.
- **What `0x809f8007` is.** From decrypted SceShellCore FW 12.70 and 4.03, decompiled in Ghidra: ShellCore GetResult (12.70 VA `0x1950f0`) → result copy (`0x1a3540`) returns `0x809f8007` only when the stored result is larger than the caller's buffer. It is not a database, file or missing-save error. The search handler (`0x11c070`) always stores `0x11308` bytes, and the console's own libSceSaveData.native passes a fixed `0x11308` buffer (4.03, 9.60, 10.01, 12.00, 12.70). The game supplies only the `0xc0`-byte condition and never chooses the size, so backport files cannot set either size. Per-app service flags (ctx+`0x134`) come from the reported compiledSdkVer and param ATTRIBUTE2 bit 17, not from param.json `sdkVersion`/`requiredSystemSoftwareVersion`. In the FW 9.60 client, `0x809f8007` is the IPMI reply status; client-side validators produce only `0x809f8004`/`8009`.
- **Bisect (all on FW 13.60, original-signed control + one change each).** AMPR + PlayGo fakelibs: pass. A, backport libc (heap flag 0x8000 adapter, SyncOnAddress adapters, cxa redirect, 4 stubs): pass. B, backport eboot: pass. C, backport eossdk: pass. D, all five 4.00 fakelibs: pass. E, `requiredSystemSoftwareVersion` 0x04: pass. F, A+B+C+D+E: pass. G, F + `ampr_emu.index` + `k9.psp`: pass. I, the real 4.00 eboot/prx/sce_module at process SDK 4: search passes (and the GPU crash from 1.1 appears). Probe v3 had shown that every argument variant (capacity 1024/32/1, explicit title/dirName) failed identically while in the failing state.
- **Conclusion.** No Studio file or rule causes the search failure. The full SDK 12 overlay failed, yet G passed with identical files and identical param content, so the failure came from console/title state at the time. All failing runs predate the switch to the original game folder, and the failing histories show unidentified APIs 68/71/116 before the search. The fresh Studio build no longer shows the error. On a real FW 4 console the client/service pair is Sony's matched pair, so this failure is not expected there. Still open: decrypted 13.60 ShellCore + libSceSaveData.native would confirm the 13.60 sizes. Test H (backport param + forced re-registration) was never run.

### 1.4 SaveData rules learned

- **`save_mount_compat` must never apply to a process reporting a high SDK.** Under SDK 12 the service rejects create mounts with systemBlocks 0 ("You cannot create save data with systemBlocks set to 0", `0x809f8004`) and needs −1. Under SDK 4 it accepts 0, and the library substitutes the default (logged `systemBlocks=96`). This is consistent with the rule's `below 5.00` limit.
- **PPSA32935 call sites.** Mount3 at VA `0x5f7858e` receives a struct at `rbp-0x80`. Its systemBlocks (`rbp-0x68`) is set to −1 at `0x5f7857b` (the earlier "96" was a service-side log value). The same caller zero-initialises the SaveDataPrepare mode, so the prepare mode-2→1 adapter is a no-op for this game. The save/create function at `0x5f6b520` uses Mount3 mode `0x20`.
- **Normal for this game:** `SaveInfoData` mount `0x809f8022` (not found), and GameStatusData read `0x809f8022` before the first save.
- **The working 9.60 build is not a save baseline.** It showed "Saving" but then "Failed to load data" after reopening.

### 1.5 Console/diagnostic facts from this investigation

- **Native eboot text must be X-only.** A diagnostic that added a `PF_R|PF_X` load segment was rejected: `_exec_self_imgact:1437: R+X segment in native program /app0/eboot.bin`, `0x80aa001a`/errno 8. Diagnostic code must go into the zero padding after the existing X-only text, with strings in the read-only data padding, keeping all 14 segment entries and their permissions.
- **Unsigned plain ELFs do not load.** The "decrypted" original (plain `7f454c46` eboot/sce_module) failed with `sceSblAuthMgrAuthHeader … error 46`, then `Load error (libc.prx)`. A control run of an original game needs every module fake-signed unchanged; the round trip differs only at ELF header `0x3c–0x3f`.
- **AMPR logging does not trace ordinary file I/O.** `AMPR_FILE_WRAPPER_LOGF` is defined as `((void)0)` in `fakelib_source/libSceAmpr/include/ampr_emu_log.h:66`. The supplied AMPR 0.3.6.6 log (64/64 hooks installed) contained no `/savedata` activity. Use explicit SaveData/file-I/O tracing instead. The `rc=0x80020010` AIO init message alone does not mean a broken I/O engine.
- **ShadowMountPlus** has no SaveData hooks. It mounted the game's own fakelib directly into the sandbox common/lib; no global SaveData replacement was involved.
- **BackPork** 6xx/7xx `libSceSaveData.native.bps` only lowers module SDK fields (plus 6xx relocations). It contains no search fix.
- **Same console, other title.** PPSA24003 (original, compiledSdkVer `0x11000040`) runs DirNameSearch without error. The original PPSA32935 calls it twice (count, then fetch); the backport made only one call.

### 1.6 Open issue: generic `sceKernelSyncOnAddressWait` adapter contract

The current derived rule (`proven: false`) assumes a 32-bit expected value and `_umtx_op` operation 11 (WAIT_UINT), taken from an emulator signature. Decrypted Sony FW 7.01 and 9.60 binaries keep RSI as 64 bits, use operation 2 with a tagged address and a custom sixth argument, convert timeouts and handle return values. In PPSA32935's libc, generic Wait is called at `0x118ee5` on an 8-byte state (`lock cmpxchg qword`). The 4.00 cave at `0x6eb46` has `mov edx,esi` and selects op 11, while the 7.00 cook keeps the native Wait/Wake pair. That call passes expected value 1 and a null timeout, so truncation does not explain any observed failure, and libc passed bisect A. **This is a real ABI discrepancy with no observed failure.** A "wide wait" libc (op 2, 64-bit, 17 bytes) was built but never tested, and it still does not reproduce Sony's timeout, queue/tag or return-value behaviour.

## 2. PPSA38510 The Walking Dead: Streets of Survival (Unity IL2CPP): startup SIGSEGV at RIP 0 (open)

- **Symptom.** The game crashed immediately on FW 8.60, and on a 10.20 Pro per the later log: SIGSEGV, RIP 0, fault address 0. The eboot base is `0x400000`. The backtrace in ELF terms: `0x77e216`, `0xd21cf7`, `0xd2536a`, `0xd25d85`, `0xd25ab9`, `0x162b755`, `0x16352be`, `0xaf`. The game is Unity PS5_13_00, original SDK `0x13000035`, cooked to `0x04000031`.
- **Mechanism (static + registers).** Constructor `0xcb0510` calls slot `0x22e7ac8` at `0xcb071d`. That slot is zero-initialised and is written only at `0x163397b`, with `il2cpp_api_lookup_symbol("il2cpp_set_find_plugin_callback")`. The runtime module list lacks **`Il2CppUserAssemblies.prx`**. Startup `0x163529b` → IL2CPP init `0x16318f0`, which loads `%s/Modules/Il2CppUserAssemblies.prx` at `0x16319c0`. A false return is recorded, and execution continues into `0x1622730`, which reaches the unset callback. This is the same failure pattern as Crow Country (Part 2 §1), but here the cause of the load failure is unknown: file open, loader rejection or another init failure.
- **Ruled out.** A missing null guard (the factory at `0x77e1d0` checks its callback; this superseded the first reading). Unsupported flags (RSI `0x34` is the startup argument size; ECX/R8 are 0). The static import report for all 12 modules against the exact 8.60 reference has missing = 0 and unknown = 0. The IL2CPP module differs from the original by 5 metadata bytes only.
- **Diagnostic, never console-tested.** An eboot with `48 8d` → `0f 0b` (UD2) at file offset `0x16359fc` (ELF VA `0x16319fc`, reached only by the negative-load branch at `0x16319ca`). If the load fails it raises SIGILL at runtime RIP `0x1a319fc`, with the loader error in R14's low 32 bits. Signed SHA-256 `9819c2df…`. The crash log is also the regression sample for `ps5backport crash-log`.

## 3. Indiana Jones PPSA26786 01.011.000

- **Force PS5 Pro crashes a base PS5** (user A/B): SIGSEGV, read fault at `0x10`, RIP `0x2e37315`. With the option disabled the game works. Psml is absent from the dump's module list and strings. **Leave Force PS5 Pro disabled on base PS5/Slim.** The failing Pro initialisation is not identified.
- **Automatic Pro (FW 4.00 + GPU compute scratch).** The six cooked binaries were byte-identical to a scratch-only cook. Missing target queries get fixed base answers, so automatic Pro activation is not established for FW 4.00 cooks.
- **Dialogue loss A/B** (`audio_speaker_compat` on/off; only the eboot differs). The adapter clears the 3D flag and caps the speaker mask at 8 in `sceAudioOut2GetSpeakerInfo`. No result was reported.
- **Static analysis at FW 4.00:** eboot has 6 unresolved function imports and every fakelib has 0. No direct or dynamic Psml use was found.

## 4. Psml model filename experiment (not promoted)

The FW 4-set Psml (SHA-256 `72855f03…`) reads `/app0/k9.psp` (string at VA `0x5c039`). Four `lea` references at `0xe0d2`, `0xe0d9`, `0xeb85` and `0xeb8c` were redirected to a new string, `/app0/KPN9_v070.psp`, at VA `0x4ffe0` (file `0x53fe0`) in the zero tail of the executable segment. That is 27 ELF bytes, with no segment changes; the result is `4106180918…`, and the model hashes are identical (`22ffbf06…`). It passed static and cook checks but was never console-tested with PSSR, so it was not promoted. The store keeps `k9.psp`. A useful test needs a game known to use PSSR on a Pro.

Why the name is `k9.psp` (2026-10-13): both sites are `snprintf(buf, 0x100, fmt, root, name)` and then an open. Sony's code passes `fmt = "/%s/common/psml_package/%s"` (still at file `0x600db`) and `name = "KPN9_v070.psp"`. That name is absent from the patched file. Its 14-byte slot at VA `0x5c039` (13 characters + NUL) now holds `"/app0/k9.psp\0\0"`, and both `lea`s point there; the path has no `%`, so the format arguments are ignored. An in-place name can have at most 7 characters (`/app0/` + 7 + NUL = 14), so `/app0/KN9.psp` fits exactly, with no code changes. The 9.60 Psml (`cfe210a3…`) has the same layout: same VA, the same two NULs and the same `lea` sites. The full original name needs the relocation described above.

**The first experiment was flawed (2026-10-13):** its string sat in the text segment, which is execute-only (`p_flags = 1`, X only; PS5 enforces XOM). `snprintf` reading it would fault. **Built instead: variant `psml_kpn9`** (option `psml_kpn9`, sets `4` and `9.60`). The string `/app0/KPN9_v070.psp\0` is placed in read-only rodata at VA `0x5c033..0x5c046`, ending where the old slot ended; `0x5c047` is referenced and stays unchanged. It overwrites the tail of the fatal message at `0x5bfe4` (`...shared resources memory blocks.`, printed only before a `ud2`; only its start is referenced), which now ends at `memory`. The four `lea`s are repointed to `0x5c033`, and no `/app0/k9.psp` remains. Unwrap → patch → sign, and the result unwraps back to the patched ELF. 9.60 `sign(unwrap)` reproduces the store file byte for byte; for set 4 only a 32-byte digest field in the SELF header differs, as for the set-4 AGC. Cook checks (UFC 6 dump at FW 9.60, GT7 1.71 `default` at FW 4): with the option the output has the variant Psml and `KPN9_v070.psp` but no `k9.psp`; every other file is identical. Not console-tested.

## 5. Reference updates and audits

- **11 October** (AnyPS5 `9a1f6f4` → `a11c8f7`; other repos current) produced priorities 1–8. Outcomes are recorded in README under "Reference improvement priority 1–3" and "Reference priority 4–8". Done: dry-run option coverage (1); optional provider audit `report --provider-audit` (2; strict `(library, NID)` cook matching deferred, since every one of 1,142 mismatches in the corpus is `libSceLibcInternal → libc`); SCE-relative dynamic tables (3); opt-in PlayGo 0.6 variants (4); deployment notes in reports, pinned to ShadowMountPlus 1.7beta5-fix1 `98c52489` (7); and the `crash-log` command (8). Deferred: PlayGo config generation (5; pgo_stub enumeration is already finite at 1,000 IDs by default; only two `playgo-chunk.dat` samples, both 35 chunks at `0x0A`). Rejected: 13 candidate additions (6).
- **12 October** (AnyPS5 `a11c8f7` → `2a5eca11c`, 949 commits; the other 15 repos unchanged). **Applied:** 28 hash-verified NID names appended to `data/aerolib.csv` (README "Verified name additions — 12 October 2026"), and 5 placeholder labels in `data/fixes.json` renamed. The `data candidates` / `data sony-builds` commands now write to the repository `data/` instead of the bundled copy. **Rejected:** regenerating `candidates.json` (17 of 18 new entries exist on every firmware ≥ 2.00, so they would never fire; the 18th is the already-covered VRR privilege call). The `sceAmprAmmSetPageTablePoolOccupancyNotificationThreshold` export is already in our fakelib, which covers all 118 FW 12.70 Ampr exports. The relinker's `R_X86_64_NONE` and section-symbol `RELATIVE` cases are already skipped by `SceElf.Imports()`. `.suprx`/`.debug_prx` suffixes do not occur in any sample. Sysmodule IDs and `kern.sdk_version` have no rule surface in Studio. ShadowMountPlus's "FAKE title ID" commit is on `main`, not the tracked `1.7`.
- **13 October** (AnyPS5 `2a5eca11c` → `8299b2f7e`, 572 commits / 336 non-merge; the other 15 repos unchanged). **Applied:** `data candidates` fix. AnyPS5 moved every "Silent stubs" bullet from `docs/dev/TechnicalDebt.md` into `docs/dev/debt/<library>.md` (links now `../../../core/libs/prx/…`). The old generator found the now-empty section and wrote an **empty** `candidates.json` with exit 0. It now reads both places, resolves each link relative to its own file, and throws when no bullet is found anywhere. Same output as the HEAD generator on the `2a5eca11c` tree (42 = 42 entries). On `8299b2f7e` it finds 44: `+sceKernelGetMainSocId`, `+sceKernelIsThreadSanitizerEnabled`, `+sceRazorCpuSync`, `−sceUsbdInit` (now a libusb pass-through). All three additions exist on every firmware from 1.00 (TSan from 3.00), so they never fire, and the catalog stays unchanged. **Rejected with corpus evidence (110 sample ELFs):** weak-import binding (the only weak undefined imports are `__cxa_finalize`/`__pthread_cxa_finalize`, in 43/43 firmwares); PT_SCE_RELRO outside PT_LOAD, DT_GNU_HASH, and bad `e_version`/`e_ehsize` (0 occurrences; PS4 and host cases). **NID sweep:** 889,958 identifiers hashed; 0 new names (this update's new functions were already in `aerolib.csv`). **Notes:** AnyPS5 now ignores flag `0x8000` in the *public* `sceKernelMapFlexibleMemory` because Animal Well's (PPSA22520, SDK 9.04) bundled libc passes it. This is host behaviour, not console evidence; our `IWIBBdTHit4` rationale (public entry rejects `0x88000` on FW 7.01–12.70) stands. `sceAgcInit` now returns `0x80890001` for version ≥ 14 in AnyPS5 (a host choice; no console threshold known). AudioOut2 `UserGetSupportedAttributes` reports port attributes 0 and 1 only, which agrees with the Yōtei adapter's attribute model; the function is on 43/43 firmwares and Yōtei doesn't import it. **NuGet audit (same day):** no vulnerable packages. Available updates are patch/test-tooling only. Avalonia 12.1.4's fixes (style-set `Command` leak, `CalendarDatePicker`, `ColorPicker`, `TreeView`) don't touch anything Studio's XAML uses, so it was not bumped. No new package was adopted: System.CommandLine, SQLite and Iced were already decided in Phases 7–8. The Iced re-run found the scanner's conditional-tail-call gap instead (README "Follow-up: conditional tail calls"), which was fixed without a dependency.
- **14 October** (AnyPS5 `8299b2f7e` → `30f961266`, 420 commits; the other 15 repos unchanged). **Applied:** 2 hash-verified NID names appended to `data/aerolib.csv`, `EVP_MD_block_size` (`zCa75KLWWn4`) and `sceNpGetSandboxInfo` (`ZVZ++68nNI8`). Both are exported only by FW 13.60, so the newly imported 13.60 list found them, not new AnyPS5 code (1,075,110 identifiers hashed against 7,695 unnamed exported NIDs). **Rejected:** regenerating `candidates.json` (46 entries; +22/−2 against the committed file). 19 additions exist on every firmware from 1.00 or 2.00. `sceKernelIsThreadSanitizerEnabled` (from 3.00) and `sceSystemServiceOpenChallengeActivity` (from 4.00) could fire only below those targets, with no sample needing them, and their constants are host choices. `sceVideoOutAddVrrStatusFlagsPrivilege` is already covered by a fix. `sceAudioOut2EnableChat` (now implemented, Palworld/PPSA28416) exists on all 45 firmwares. **Notes:** PlayGo now returns `BAD_CHUNK_ID` for any chunk ≠ 0 when `sce_sys` has no `playgo-*` file (Sifu PPSA03001 walks chunk IDs until the first error). This is host behaviour; pgo_stub is unchanged. The NID patcher's verneed and GNU-hash bounds fixes apply to host executables (0 DT_GNU_HASH in the corpus, per 13 October). The rest is AGC/shader/Vulkan, relinker console output and Windows host I/O.
- **NID-name method (reusable).** Hash every identifier in the reference repos with the NID algorithm, and keep a name only when its NID is exported by a real firmware in `firmware_db` and is still unnamed. The false-match probability is about 1e-9 for that run.

## 6. Rejected or superseded conclusions

Keep these out of new work unless new evidence appears.

| Claim | Status |
| --- | --- |
| PPSA32935 crash is caused by the FW 4 AGC/Driver/AMPR binaries themselves | Refuted by swaps; the cause is the SDK-4 flag path in any AGC build (§1.1) |
| `gpu_scratch` fixes the PPSA32935 bad-opcode crash | Not supported by the evidence |
| Automatic Pro mode causes the PPSA32935 crash | Refuted: the working 9.60 cook uses it too |
| Libc heap adapter clearing mmap flag 0x8000 breaks the DirNameSearch reply | Refuted by bisect A |
| Process SDK 4 breaks DirNameSearch | Refuted by bisect I |
| The SDK value or lowered `requiredSystemSoftwareVersion` causes the search failure | Refuted by the SDK 12 runs and bisect E |
| AMPR/PlayGo fakelibs cause the save failure | Refuted by the fakelib control |
| `0x809f8007` means a missing or corrupt save | Wrong: it is ShellCore's result-size check (§1.3) |
| Logged `systemBlocks=96` is the game's argument | Wrong: the game passes −1; 96 is the service-side value |
| The PPSA32935 9.60 cook is a working save baseline | Wrong: its saves do not reload |
| AMPR debug logging can trace save-file I/O | Wrong: the file-wrapper log macro is a no-op |
| Swapping whole eboots between target outputs is a valid isolation test | Wrong: mixed-SDK sets fail to load (§1.2) |
| PPSA38510 crash is a missing null guard / unsupported start flags | Superseded by the kernel-log analysis (§2) |
| Missing Psml model causes the Indiana Force Pro crash | Not supported: Psml was not loaded |
| Regenerate `candidates.json` from each AnyPS5 update | Rejected four times (11, 12, 13 and 14 October); review per entry |
| `work/CONFIRMED_FINDINGS.md` §7: fixture roots are deleted | Outdated: the `*_elf` fixtures were recreated and are kept |

## 6a. PPSA26344 Ghost of Yōtei 02.002.000: FW 4 cook crashes at startup (open, 2026-10-13)

- **Console (FW 13.60, three launches, identical):** SIGSEGV, read fault at `0x4790`, RIP `0x6dba52` (eboot loaded at `0x400000`, so RIP = ELF VA + `0x400000`). Cook: FW 4.00, store set 4, options `save_mount_compat`, `save_prepare_compat`, `auto_pro`. Coredump: `D:\PS5\PPSA26344_1791847804`.
- **Static (unwrapped eboot; code identical to the original apart from the listed fixes):** the faulting function is Scream's `snd_SynthAuxBussSetOutputDest` (assert strings `nca_synth_interface.cpp`). It reads `qword [0x48ddbf0]` (= NULL) + bus 0x12 × `0x3f8` + `0x20`. That global is the auxiliary-bus table, written only by `snd_SynthInit` (`0x704980`). So `snd_SynthInit` returned early and the game kept going; Scream has no NULL check later.
- **Likely early exit:** `snd_SynthInit` → `NCA::System::CreateInstance()` (`0x747690`) failing (error print at `0x703bb7`, return at `0x703c39`). NCA's AudioOut2 setup (`0x74b5a0`: Initialize → Set3DLatency → ContextResetParam/QueryMemory/ContextCreate/SetAttributes → UserCreate → PortCreate) and its speaker probe (`0x74d42b`: … PortCreate → `sceAudioOut2GetSpeakerInfo`, layout byte ≤ 3, flags bit 0 = 3D audio) are the only system-call paths. All audio imports exist on 4.00 except `Set3DLatency` (stubbed ret0). Game error prints do not reach the klog, so the failing call is not identified.
- **Hypothesis (not confirmed):** the same old-SDK AudioOut2 refusal as CONTROL (`audio_speaker_compat` rationale): FW 13.60 reports 3D audio / a > 8-speaker layout, and a port opened for it is refused for an SDK 4.00 binary. Cheapest test: launch with 3D audio disabled and stereo output in console settings; then a re-cook with `--option audio_speaker_compat`. The tester's own Yōtei build is SDK 10.00 (README Part 15), which would avoid an SDK < 5 gate.
- **Re-cook with `audio_speaker_compat` (02:38, dump `D:\PS5\PPSA26344_1791848400`): identical crash** (RIP `0x6dba52`, fault `0x4790`). The adapter is wired (PLT `0x1be7480` → cave `0x4008f9`). The speaker-layout hypothesis alone is not enough.
- **Call chain:** `snd_SynthInit` → `CreateInstance` (`0x747690`) → output object constructor `0x74d8c0` (vtable) → init `0x74d950` → `0x74da20` (probe), then AudioOut2 setup `0x74b5a0`, whose every exit (success included) passes `0x74b77c` with a signed result in `r14b`.
- **Diagnostic eboot** (`PPSA26344_diag_audio/eboot.bin`, built from the 02:38 cook): 15 `snd_SynthInit(): …` error prints → `ud2` (the "Clamping" warning left alone); `0x74b77c` → cave `0x1bebfd0` (`test r14b,r14b; jns; ud2` at `0x1bebfd5`, then the original 7 bytes and `jmp 0x74b783`). Crash at `0x1bebfd5` → `rax` = the failing AudioOut2 error code; at `0x703bc2` → CreateInstance failed outside the AudioOut2 setup (`esi` = code); another `ud2` RIP → that `snd_SynthInit` message.
- **Diagnostic 1 result (dump `D:\PS5\PPSA26344_1791848690`, two launches):** SIGILL at `0x703bc2`, `esi = 0xfffffff8`: `CreateInstance` returned NCA code **-8** and the AudioOut2 setup hook did not fire. The NCA allocator (`0x749c10`) returns -10 (`0xf6`), so this is not memory. -8 comes from the speaker probe `0x74d3c0` (default `bl = 0xf8`): Initialize (other than `0x80268004`/`0x80268007`) → ContextResetParam/QueryMemory → ContextCreate → UserCreate → PortCreate (12-byte param `0xbb8000000c00`) → `sceAudioOut2GetSpeakerInfo(…, 1)` (layout byte must be ≤ 3) → poll `sceAudioOut2PortGetState` until byte `+2` ≠ 0, then a switch on that byte that accepts only 1, 2, 4, 6, 8 and 12 (jump table `0x1ecde50`); anything else returns -8. `audio_speaker_compat` touches neither the layout byte nor the state byte.
- **Probe confirmed on the path:** relocations put `0x74d3c0` (probe) at vtable `0x20a5cf0 + 0x10` and `0x74d950` (init) at `+0x18`; `CreateInstance` calls `[vtable+0x10]` at `0x747801` and fails on a negative result (`0x747865`). With diagnostic 2, every -8 exit of the probe passes T1–T4. A plain `0x703bc2` / `esi = -8` crash therefore means diagnostic 1 is still deployed (the 02:49 run `D:\PS5\PPSA26344_1791848942` looked exactly like this; deployment unconfirmed).
- **Diagnostic 2 result (dump `D:\PS5\PPSA26344_1791849043`): T3 at `0x74d5d1`, `rax = 0x8026800E`.** Registers (`rsi = rbp-0x140`, `rdx = rbp-0x158`) identify the probe's `sceAudioOut2PortCreate`. Its parameter block is `+0 port_type 0 (main)`, `+4 data_format 0xC00` (12 channels, float), `+8 48000 Hz`, `+0x10 user handle`. AnyPS5's AudioOut2 port-format table (from its tests; firmware and caller SDK not recorded) lists `0xC00 → 0` and calls `0x8026800E` `PORT_ERROR_UNSUPPORTED_FORMAT`, so on 13.60 the 12-channel (7.1.4) main port is most likely refused **because the caller's SDK is 4.00**, which fits the CONTROL README. The probe always asks for 12 channels, regardless of the speaker setup, so `audio_speaker_compat` could not help.
- **Candidate generic rule:** for old-SDK targets (cut-off SDK unknown), adapter on `sceAudioOut2PortCreate`: main port (`port_type == 0`) with channel field `0xC` → rewrite the field to `0x8` and call the real function. Test build `PPSA26344_test_port8/eboot.bin` (SHA-256 `9dd99eb4…`) = diagnostic 2 + this adapter at cave `0x1be8d50`, PLT `0x1be87f0` → cave, GOT `0x227aa18`.
- **Console result (23:53, pid 1941): `test_port8` boots.** No trap fired. ACM started (`sceAcvHwStartWave`), the shell left the splash for `AppScreen`, and the game mounted saves: `sce_sdmemory` mount `0x809f8022` (not found, normal before the first save) with `systemBlocks=0` (the save-mount adapter is active). A ShellUI `AppErrorDialog` (`BodyFactoryDialog`) opened and closed, and the game kept focus; its message is not in the log. **The 12→8-channel `sceAudioOut2PortCreate` adapter is the fix for the startup crash**; `audio_speaker_compat` was also enabled in this build.
- **Rule added (2026-10-13):** `data/fixes.json` `JK2wamZPzwM` (default adapter, `when: always`, `below: 11.00`, `proven: false`, source `YOTEI4`). Branch-free (cmov) version of the test cave, since `PlaceChain` may split adapters across caves. Re-cook `PPSA26344_backport_4.00_audiofix` (same options as the 02:38 cook): the report lists the fix (cave `0x2b17`), and the split chain in the eboot reproduces the instructions and ends with `jmp [GOT 0x227aa18]`. Test: `OptionalAudioTests.Cook_AdaptsPortCreateOnlyBelowFirmware11`. - **Console result of the clean re-cook:** boots and plays; sound works but is **garbled**. A system error "Something went wrong. NP-104602-3" appears at first boot; the game continues and plays. The log shows PSN unreachable throughout (`PFAuthClient 0x8041013d`, `NpPush Disconnected`, no connection to `ps5.np.playstation.net`), so this is most likely the offline setup. Not yet compared with another game launched the same way.
- **NP-104602-3 decoded (2026-10-14, static; not a backport defect):** ShellCore carries the hex → display-code table (FW 10.01 `SceShellCore.elf` file offset `0x1db16e0`…, 20-byte entries `u32 code, char[16] "XX-nnnnnn-c"`, check digit = digit sum mod 10): `NP-104602-3` = **`0x8055A011`**, the libSceNpManager server-request range (`0x8055A000` = NP-104585-3 … `0x8055A02A` = NP-104627-0 "lost connection to the server"; `0x8041013d` = NW-102261-2). The display comes from the game itself: `sceErrorDialogOpen` has a single caller (`0x12f1075`, dialog manager `0x12f0f30`) that drains a global error queue (`0x6b80938`, entries `code,u32 state` at `+0x10`, 8-byte slots, writer pattern `lock cmpxchg [rdi+rcx*8+0x14]; mov [+0x10], code; mov [+0x14], 2`). The NP sign-in state machine (`0x1243020`: `sceNpCheckCallback` → `sceNpCreateRequest` + `sceNpCheckNpReachability` → state 6 → `sceGameUpdateCheck`, `sceNpAuthGetIdTokenV3`, `sceNpGetAccountLanguage2`, `sceNpCheckPremium`) pushes every failure into that queue except `0x80550003..18` (jump table, state changes), `0x8055A02A` (lost connection → state 5, silent) and `0x8C000001..4`; the queue de-duplicates recent codes, hence "first boot only". In ShellCore (`0xbb3c10`) `0x8055A011` is the code given to every pending NP request when the NP manager resets after a failed sign-in (`0x8055A038` for the active one), which is what the blocked-PSN setup produces (`PFAuthClient 0x8041013d` = ECONNREFUSED to `ps5.np.playstation.net`). No backport fix touches NP (eboot fixes: SaveData, AudioOut2, UserService, VideoOut, kernel); all NP imports resolve on FW 4.00 unchanged. Expected on the unmodified SDK 11 game launched the same way on 13.60. Confirmation: launch the original (or any PSN-using title) with the same loader; or launch with the console signed out / network disabled, which should leave the state machine before the reachability request. Only a game-wide NP stub (not a per-function fix in `data/fixes.json`) could suppress it; not implemented.
- **Option added (2026-10-14): `np_error_dialogs` "Hide PSN error dialogs", new group `Online`** (user decision: suppress at the dialog rather than fake a signed-out console or enumerate NP errors). Adapters on `sceErrorDialogOpen` (NP `0x8055xxxx` / net `0x8041xxxx` codes return 0 and set a one-byte mark in state block `np_error_dialog`), `sceErrorDialogUpdateStatus` and `sceErrorDialogGetStatus` (mark set → clear it and return FINISHED = 3, the value Yotei's dialog manager at `0x12f10c0` waits for), `sceErrorDialogClose` (clear). Not console-tested; next: re-cook `PPSA26344_backport_4xx+_audio_engine` with the option and confirm no box at first boot, then try a second title that shows an NP box offline. Tests: `OptionalOnlineTests`.
- **Garble cause (static):** `snd_SynthInit` turns the probe's flags into an output mode (`0x703ce3`: bit `0x10` → 7.1.4, `8` → 7.1, `4` → 5.1, `2` → stereo, `1` → mono). The probe gets those flags from a table indexed by `sceAudioOut2PortGetState().num_channels` (`0x1ecde50`-adjacent table at `0x74d684+0x1780c88`: 8 → `0x3fef`, 12 → `0x3fff`). `num_channels` is the **system output** count (AnyPS5 `AudioOut2PortState`), so a 12-channel output → 7.1.4 → bed ports built as `0xC00` at `0x74ba6c`. The PortCreate rule silently made those 8-channel ports carrying 12-channel data.
- **Second rule (2026-10-13):** `gatEUKG+Ea4` `sceAudioOut2PortGetState`, post-call adapter, `below: 11.00`: `num_channels` capped at 8, so the game picks 7.1 and opens real 8-channel ports. The PortCreate rule stays for the probe port, which only reads state. Re-cook `PPSA26344_backport_4.00_audiofix` (SHA-256 `80e0f495…`): both adapters verified in the eboot. Tests: `OptionalAudioTests.Cook_AdaptsAudioOut2PortsOnlyBelowFirmware11` (4 cases). Console (user report): **sound still not clean with the PortGetState cap.** So the 12-on-8 mismatch is not the cause, or not the only one (the actual `num_channels` on this setup is unknown). Remaining suspects: `sceAudioOut2Set3DLatency` stubbed ret0 although 13.60 has it; SDK-dependent `ContextResetParam` defaults; ACM convolution-reverb behaviour for an old-SDK binary. Asked for the kind of distortion, an A/B with 3D audio off and stereo/LPCM output, the audio hardware, and the eboot hash.
- **User report: crackling; unchanged with 3D audio off.** This points to timing (buffer underruns), not channel layout. A/B build `PPSA26344_diag_native1270`: same SDK 4.00 stamp, argument fixes and fakelibs, but resolved against the 12.70 export list relabelled as 4.00 (`--reference`), so nothing is "missing". The eboot keeps native Set3DLatency, GetOperationMode, Trinity and VRR; libc keeps native SyncOnAddress Wait/Wake, ThreadAtexit/Dtors and the flexible-memory call. Diagnostic only; it will not load on FW 4. Clean sound → bisect the replaced functions; still crackling → SDK-4.00-gated system behaviour (or an argument fix or fakelib).
- **`noampr` still crackles** → ampr_emu is not the cause. Byte inventory of `noampr` against the original: SDK stamp only (eboot procparam `0x1e7ffea`, libc `0x196762`, plus libc ELF header byte `0x3c`), param.json `sdkVersion`/`requiredSystemSoftwareVersion` 11.00/13.60 → 4.00, six PLT adapters (three of them audio), and the fakelibs Agc/AgcDriver/Psml + `k9.psp`.
- **Static audit of the SDK gates (12.70 `libSceAudioOut`, closest decrypted build to 13.60):** the library reads `sceKernelGetProsperoCompiledSdkVersion` into globals (`0x6c014`, `0x6c018`, `0xad290`, `0x6cf64`, flag `0xad2a4 = sdk ≥ 8.00`) and sends it to the server during `sceAudioOut2Initialize` (`0x1f540`). Gates by threshold:
  - `sceAudioOut2ContextCreate` (`0x2be10`): `≥ 3.00` (`0x2c030`), `≥ 4.00` (`0x2c109`), `≤ 3.ff` (`0x2c3b1`). SDK `0x04000031` takes the same branches as SDK 11.
  - `sceAudioOut2ContextAdvance` (`0x2ca50`): one gate (`0x2d2e3`). If the system output reported by `0x20cb0` has 12 channels, the internal ports get attribute `0x39` = 1 (SDK < 8.00) or 2 (SDK ≥ 8.00); otherwise 0 for both. Its callees `0x2f510`/`0x2ff20`/`0x303b0` compare against 1.50 only (same for both).
  - Port-state builder `0x211b0` (PortGetState and others): `≥ 5.00` only sets an extra flag bit 2; NCA's probe reads bit 1 and `0x40`.
  - Other gates (not on NCA's hot path): mastering `≥ 8.00`, user attributes `≥ 9.00`, PortSetAttributesEx `≥ 9.00`, GetSpeakerInfo-region `5.00`/`6.00`/flag `≥ 8.00`, legacy sceAudioOut `3.00`/`5.00`.
  - Server `libSceAudioSystem` (12.70, loaded by `orbis_audiod`): no SDK-shaped comparisons (the only hits, `0x41d48`/`0x42170`, are message-type byte checks).
  - **Conclusion:** with a non-12-channel output, the audio stack treats the SDK 4.00 cook the same as SDK 11 in setup and the per-grain advance. The crackle is therefore more likely late audio from the game's mixer thread: CPU scheduling or contention.
- **Controlled test `PPSA26344_diag_noampr_sdk11`:** `noampr` with only the SDK stamp restored (eboot + libc procparam = `0x11000040/0x12590001`, original param.json). Same adapters and fakelibs. Clean → the SDK stamp acts through the kernel (e.g. `[BAPM] Param Set: 2 (Ver:04000031)`, CPU-set or scheduling); still crackling → the AGC/AgcDriver/Psml fakelibs or the adapters. Diagnostic only.
- **`noampr_sdk11` still crackles** → the SDK stamp is not the cause (neither through the audio libraries nor through the kernel). NCA's runtime monitor (`0x74d059`) polls `sceAudioOut2PortGetState` on the bed ports and reconfigures only when the state differs from its cache (`[rbx+0xb0..0xb8]`). With the cap, `num_channels` is a stable 8, so the monitor does not flap.
- **Next split (all diagnostic only):**
  - `PPSA26344_diag_T1_fakelibs_only`: untouched original eboot, libc and param.json + only the Agc/AgcDriver/Psml fakelibs + `k9.psp`.
  - `PPSA26344_diag_T2_no_audio_adapters`: `noampr_sdk11` with the GetSpeakerInfo, PortCreate and PortGetState PLT entries restored (`0x1be7480`/`0x1be87f0`/`0x1be8820` → `jmp [GOT]`); only the three non-audio adapters remain.
  - T1 crackles → fakelibs (or the overlay/loader itself). T2 clean → the audio adapters. Baseline still unconfirmed: the installed original with no backport overlay.
- **Results (console, FW 13.60):** original game clean; **T1 (original + fakelibs) clean**; **T2 (SDK 11, no audio adapters) clean**; `noampr_sdk11` (= T2 + the three audio adapters) crackles. **The crackle comes from the audio adapters**, not from the SDK stamp, fakelibs, ampr_emu, replaced functions or the loader.
- NCA's runtime `sceAudioOut2GetSpeakerInfo` (`0x74cbc8`) reads `+0` layout, `+4` speaker mask (`~mask & 0xfff == 0` → 7.1.4 table, `0xff` → 7.1, `& 0x3f` → 5.1, else stereo), `+8` flags bits 0/1, and per-speaker data at `+0x10..+0x4f`. This matches the `audio_speaker_compat` layout (mask & 0xff → 7.1).
- **One-adapter split on the T2 base (SDK 11):** `PPSA26344_diag_T3_portcreate` (`0d85364c…`), `_T4_portgetstate` (`3c83fa01…`), `_T5_speakerinfo` (`44d6f655…`).
- **One-adapter results (headphones): T3 (PortCreate rewrite) crackles; T4 (PortGetState cap) and T5 (speaker info) are clean.** The PortCreate rewrite is the cause.
- **Why the rewrite can garble:** NCA builds persistent port descriptors (`[obj+0x20]`; per-port channel count kept separately at `+0x68`, `0x74b90e`) and reuses them for per-user ports (`0x750611`–`0x75077c`). A 12-channel bed port (`0x74ba6c` → `0xC00`) rewritten to 8 channels still receives 12-channel frames. The adapter also writes the format back into NCA's own descriptor.
- **Caller diagnostic:** in front of the PortCreate adapter, a cave at `0x1be8d10` (text segment extended to `0x17ec000`) does `ud2` when a main port with channel field `0xC` comes from any caller other than the probe (return address `0x74d544`). At the crash, `r11` = return address (= caller + 5) and `rax` = format. Builds: `PPSA26344_diag_pcdiag_t3` (on T3, SDK 11, `e21b5d00…`) and `PPSA26344_diag_pcdiag_sdk4` (on `native1270_noampr`, SDK 4 with all three adapters, `2c73868b…`).
- **Caller diagnostic result (console, headphones; `pcdiag_t3` once, `pcdiag_sdk4` twice): every run traps at `0x1be8d34` with `r11 = 0x74baff`, `rax = 0xC00`.** The call is `0x74bafa` in the bed-port setup `0x74b5a0` (backtrace `0x74d979` init → `0x747ef6` CreateInstance). So a non-probe 12-channel main port exists, **also in the SDK 4 build with the PortGetState cap and the speaker-info adapter active**.
- **Why the cap cannot help (static):** the bed loop takes its channel count from NCA parameter 1 (`[System+0x10]`; `0x7538d0` SetParam stores parameter *n* at `obj + n*0x10`), not from the probe. `snd_SynthInit` sets parameter 1 to a constant `0x3fff` (`0x70407c`, every boot), and bit `0x10` gives 12 channels for bed ports 0–2 (`0x74b8b5`). The probe result only goes to parameter 0 (`[System+0]`) and the mode choice at `0x703ce3`. **The game always asks for a 7.1.4 bed**, whatever the output device. On 13.60 an SDK ≥ 11 caller gets it (the system downmixes); an SDK 4 caller gets `0x8026800E`.
- **Consequence:** the mixer stride is fixed when the port is built (`[desc+0x68]`, `[desc+0x80]` = 12, buffer advance `table[12] × grain`). The PCM attribute (id 0, value = `&[desc+0x78]`, `0x74c709`) hands that 12-channel interleaved buffer to the system, which reads it as 8 channels → crackle. Rewriting the format at PortCreate can never be clean. A clean fix has to convert 12 → 8 channels in the data (stateful: port handle, buffer pointer, grain) or have the system accept 12 channels. Branch-free PLT adapters cannot do either.
- **12.70 client check (static):** the main-port path (`0x4e0d0` → `0x4e500` → `0x170b0`) accepts channel field `0xC` for main ports (`(fmt & 0xb00) == 0x800`). The `0x80` flag is only legal with 8 channels (`0x17123`), so `0xC80` is no alternative. The SDK-dependent refusal seen on 13.60 is therefore not in this client path (13.60 build or server side); still unlocated.
- **Rules moved to an option (2026-10-13, user decision):** `JK2wamZPzwM` and `gatEUKG+Ea4` are no longer default fixes. They form the opt-in option `audio_port_compat` ("Audio port compatibility", `--option audio_port_compat`), separate from `audio_speaker_compat`. A Yōtei FW 4 cook without the option crashes at startup again (the original NULL bus table). Tests: `OptionalAudioTests.Cook_AdaptsAudioOut2PortsOnlyWithOptionBelowFirmware11` (6 cases; replaced by the downmix tests below).
- **FW 4.00 client check (static, `Firmware 4.00/system/common/lib/libSceAudioOut.sprx`):** `sceAudioOut2LoPortCreate` (`0x15620`) accepts main-port channel counts from mask `0x504` = 2, 8, 10 (`0x157fd`); 12 is refused with `0x8026800E`. On real FW 4 a 7.1.4 bed port is impossible for any caller, so rewriting to 8 channels and folding the data is the generic route.
- **12→8 downmix (2026-10-13, user decision: option 1).** `audio_port_compat` is now five contiguous adapters sharing a 0x90-byte runtime state `audio_out2_downmix`; the PortGetState cap (`gatEUKG+Ea4`) was dropped (disproven, unnecessary).
  - `sceAudioOut2ContextCreate` (`0x6o1VVAYSY`, pre): grain = `param+0x10` num_grains (0 → 256) → state +0.
  - `sceAudioOut2PortCreate` (`JK2wamZPzwM`, post): main port with `fmt & 0xfff == 0xC00` (float) → `0x800` in place; on success the handle goes into one of 16 slots (state +8). **Now 8 slots and 0x48 bytes (§6g).**
  - `sceAudioOut2PortSetAttributes` (`8XTArSPyWHk`, pre): for a tracked port, attribute 0 (value = 8 bytes holding the PCM pointer) → fold grain × 12 floats to grain × 8 in place (ch 0–7 kept; 8/9 → 0/1, 10/11 → 6/7 at 0.7071, AnyPS5's 12-channel order), once per port and grain (bitmask state +4); attribute 1 (gains) of size 48 → 32.
  - `sceAudioOut2ContextAdvance` (`PE2zHMqLSHs`, pre): clears the bitmask. `sceAudioOut2PortDestroy` (`cd+Rtw+D1x8`, pre): frees the slot.
  - Why at SetAttributes: NCA submits port attributes after rendering (vtable `+0x48` = `0x74e6b0`: attribute sets, then `0x74ca30` advance/push), so the data is final at that call whether the library copies it then or later. NCA's buffer per bed port is `table[12] = 12` × grain floats (table `0x1ecd2d0`, advance at `0x74bb25`), so the fold's frame count matches.
- **Engine additions (generic):** fix fields `contiguous` (adapter placed in one piece, may branch; `Patching.FindContiguousCave` joins untouched 64-byte blocks of the text extension), `state`/`state_size` (`Patching.AllocateState`: zero-initialised memory past memsz in the last 16 KB page of a writable PT_LOAD; refused when the page is shared with PT_GNU_RELRO or another mapped PT_LOAD) and `{state+0xN}` in an instruction's hex (rip-relative displacement to state + N, relative to the end of that instruction). Evidence that the page tail is free: every checked PS5 module (GT7 eboot/prx/fakelibs, Control eboot/prx, Yōtei) has one flags-0 PT_LOAD (dynamic tables) starting inside the last bss page, which the loader cannot map there without unmapping live bss, and Yōtei's crash dumps list `nsegs: 4` = its four nonzero-flag PT_LOADs. Yōtei: state at ELF `0x717d610` (runtime `0x757d610`), caves `0x17e8d0e`–`0x17e8f53` in the existing extension.
- **Verification:** the assembled adapters ran natively (x64 host, AVX) through a SysV thunk: grain capture, rewrite/track only 12-ch float main ports, fold matches reference within 1e-5, one fold per grain, other ports untouched, destroy frees the slot, 17th port ignored, all six argument registers forwarded. The cooked `PPSA26344_backport_4.00_downmix` eboot's own bytes ran the same way at their real addresses, entered through the game's PLT entries (grain 128). Tests: `OptionalAudioTests` (21, incl. `Cook_AppliesAudioDownmixOnlyWithOptionBelowFirmware11` 15 cases, `AllocateState_*`, `PlaceChain_Contiguous_JoinsExtensionBlocks`, `Insn_StatePlaceholder_RoundTrips`); full suite 666/666.
- **Console test pending:** `PPSA26344_backport_4.00_downmix` (options as `_audiofix` + `audio_port_compat`).
- **`diag_native1270` still crackles** → the replaced or wrapped functions are not the cause. Next suspect: the store's FW 4 **ampr_emu** (`fakelibs/4`, Aug 24, 218 KB) replacing native libSceAmpr on 13.60. The installed original (`PPSA26344-app0`, no fakelib folder) uses native Ampr, and audio banks are presumably streamed through Ampr. The dump's own ampr_emu is a different build (Sep 6, 304 KB). A/B `PPSA26344_diag_native1270_noampr` = `diag_native1270` without `fakelib/libSceAmpr.sprx`, `fakelib/libScePlayGo.sprx` or `ampr_emu.index`. The files must also be deleted on the console, because the overlay keeps old files, and global fakelib composition must not re-inject Ampr. Baseline still open: does the original game sound clean on this console?
- **Diagnostic 2** (`PPSA26344_diag_audio2/eboot.bin`, SHA-256 `417afd8a…`, = diagnostic 1 plus): T1 `0x74d5c8` (Initialize/QueryMemory error, `rax`), T2 `0x1bebfe4` (ContextCreate error, `rax`), T3 `0x74d5d1` (UserCreate/PortCreate error in `rax`, or `rax` ≤ 0xff = the layout byte), T4 `0x1bebfed` (channel byte not accepted, `rax` = the byte; cave `0x1bebfe8` checks `bl == 0xf8` before the original `movzx/and` at `0x74d6e0`).

### 6b. Same startup crash on FW 8.60 with the `audio_port_compat` cook (open, 2026-10-13)

- **Tester (PS5 fat, FW 8.60, ShadowMount+ 1.7-beta5fix1):** `PPSA26344_backport_4.00` (04:11 cook, options `save_mount_compat`, `save_prepare_compat`, `audio_port_compat` (downmix), `auto_pro`, candidate fixes on; shared as `PPSA26344_backport_4xx+.7z`). Four launches, all identical: SIGSEGV RIP `0x6dba52`, read at `0x4790`, i.e. the §6a NULL aux-bus table after `snd_SynthInit` failed. Kernel log: `SDK vesion: PS4:09040001 PPR:04000031`. Coredump `D:\PS5\PPSA26344_1791867408`. An earlier build installed from app0 (`debug.log.1`, 04:38–05:55) crashed elsewhere (RIP `0x1a395c5`, NULL read); not analysed.
- **Coredump format:** `.prosperodmp` = LZ4 frame → ELF core (`e_type` 4). Notes: `PROCESS_INFO`, `THREAD_REG_INFO`, `MODULE_INFO`, `TTY_INFO` (system processes only; the game's prints are not in it), etc. PT_LOADs: thread stacks (main thread `0x7eeff8000`–`0x7eeffc000`) and 0x80–0x600-byte windows around RIP and register targets. The `PROCESS_INFO`/crash-log `fingerprint` is the game's build fingerprint (identical in every cook and the original), so it cannot identify a cook.
- **Stack residue:** the main-thread stack holds `0x703dd7` (return address inside `snd_SynthInit`'s failure cleanup), the print argument `0xfffffff8` at `0x7eeffb5a0`, and the formatted text `FFFFFFF8` at `0x7eeffb620`. So the "`NCA::System::CreateInstance()` has failed with error code 0x%X" path ran, and **CreateInstance returned -8 on 8.60**, as it did on 13.60 before the PortCreate rewrite. The probe's frame was overwritten by the print, so the failing call is not in the dump.
- **-8 has two sources:** the speaker probe (`0x74d3c0`, `bl = 0xf8`) and the bed-port setup `0x74b5a0` (`r14b = 0xf8` after Initialize, `0x1be8770(0xff, 2)`, QueryMemory, ContextCreate, ContextSetAttributes or UserCreate fails). Init order: CreateInstance calls the probe (`0x747801`), then init `0x74d950` → `0x74da20`, then setup `0x74b5a0`.
- **8.60 `libSceAudioOut` (static, `Firmware 8.60/system_1`, identical to `system_2`), checked for an SDK 4.00 caller:**
  - `sceAudioOut2PortCreate` → `0x4ae20` → `0x4b250` → `0x152c0`: a main port (type 0) accepts 8 channels at 48 kHz (`0x157d5` → `0x158c9`). 12 channels only if byte `[0xa901c]` (12-channel output support) is set; otherwise `0x8026800E`. So the downmix's 0xC00→0x800 rewrite is accepted on 8.60.
  - `sceAudioOut2PortGetState` (`0x4bca0`): when `[0xa901c]` is 0 it turns `num_channels` 12 into 8. NCA accepts 1, 2, 4, 6, 8 and 12.
  - `sceAudioOut2GetSpeakerInfo` (`0x211f0`): SDK gates `> 1.50` (mode 1 allowed), `< 6.00` (fixed layout 3), `≥ 5.00`; every fixed table has layout byte 3 (≤ 3 is accepted). Without 12-channel support, mask bits `0xf00` force layout 0.
  - `ContextResetParam` defaults are the same as 4.00. QueryMemory/ContextCreate: grain ≥ 0x100 and a multiple of 0x100; with object ports, `+0x14` must be 1–2 and grain ≤ 0x400 (unless `[0xba768]`). A ContextCreate gate at SDK `≥ 4.00` (`0x2a296`) takes the same branch as SDK 11.
  - SDK globals are `[0xa9008]` and `[0x68018]`. Other gates: `0x1f6d6` `≥ 5.00`, `0x20326` `≥ 3.00`, `0x21077` `≥ 8.00`, plus QueryMemory/ContextCreate `≥ 3.00`.
  - **No static cause found.** The audio_port_compat adapters forward all argument registers, keep the stack aligned and cannot themselves return -8.
- **Diagnostic `PPSA26344_diag_860_traps/eboot.bin`** (SHA-256 `d47c0e1f…`; exactly the 04:11 cook plus 115 patched ELF bytes, same size): every -8 branch goes to its own `ud2`, so RIP identifies the failing call and `rax` holds its error code. Probe: `0x1be9010` Initialize, `0x1be9020` QueryMemory, `0x1be9030` ContextCreate, `0x1be9050` UserCreate, `0x1be9060` PortCreate, `0x1be9070` layout byte > 3 (`rax` = byte; via `0x74d5d1`), `0x1be9090` unsupported `num_channels` (jump-table entries 3/5/7/9/10/11), `0x74d712` `num_channels` > 12. Setup: `0x1be90a0` call `0x1be8770(0xff, 2)`, `0x1be90b0` QueryMemory, `0x1be90d0` ContextCreate, `0x1be9115` any other negative setup exit (hook at `0x74b77c`, `rax` = last error). Catch-all `0x703bc2` (CreateInstance print, `esi` = code). Tester: replace only `eboot.bin` in the 4.00 folder.
- **Diagnostic result (tester log `PS5-FAT-8.60-20261013-071714-GOY2.log`, dump `D:\PS5\PPSA26344_1791868662`, two launches, identical): SIGILL at `0x1be90a0`, `rax = 0x80268001`, `rdi = 0xff`, `rsi = 2`.** The probe passes on 8.60 (so `audio_port_compat` is fine there). The setup's call at `0x74b5dc` fails: PLT `0x1be8770` → GOT `0x227a9d8` = `TViD1EZXkNI` **`sceAudioOut2Set3DLatency(0xff, 2)`**, which returns `0x80268001` (invalid parameter).
- **Cause:** the cook's `TViD1EZXkNI` rule is `kind: stub` (GOT pre-filled with ret0), but the `JUMP_SLOT` relocation stays, so where the firmware exports the function the loader overwrites the stub with the real one. Survey of the decrypted `libSceAudioOut` exports: absent on 1.00–5.50 (the stub works there); **6.00–9.60 accept latency 0–1 only** (`cmp r14d, 1; ja` → `0x80268001`, and 0 is refused for user `0xff`); from **10.01** the check moved into a helper (10.01 `0x1d840`) that also accepts **2** for user `0xff` only. 7.00, 11.20, 12.60 and 13.60 are encrypted or missing; 13.60 accepts 2 by observation. So **every SDK ≥ 10 game that calls `Set3DLatency(…, 2)`, cooked for a target below 10.01, fails on FW 6.00–9.60** (Yōtei: NCA setup -8 → `snd_SynthInit` fails → NULL aux-bus crash). FW 4.x/5.x consoles are unaffected.
- **Test build `PPSA26344_test_860_3dlatency/eboot.bin`** (SHA-256 `a23e19a7…`) = `diag_860_traps` + PLT `0x1be8770` → `jmp 0x4008ec` (the cook's ret0 stub). Generic fix candidate: `TViD1EZXkNI` as `plt_stub` ret0 with `below: 10.01` (the PLT is patched, so the stub holds even where the export exists). Trade-off: a sub-10.01 cook then also skips the native call on 10.01+ consoles, the same as on FW 4/5.
- **Rule changed (2026-10-13, user decision):** `data/fixes.json` `TViD1EZXkNI` is now `plt_stub` ret0, `when: always`, `below: 10.01`, `proven: false` (the PLT form on 6.00–9.60 is untested; ret0 itself is proven on FW 4), source `YOTEI4` added. Cooks below 10.01 report it under `fixed` (`PLT -> ret0`) instead of `stubbed`. Tests: `KnowledgeTests.DerivedFixes_AreFlagged` (list extended); golden `backport/ac-450-options.txt` re-recorded (only the Set3DLatency line and cave addresses moved). The affected classes pass (77/77).
- **Clean re-cook `PPSA26344_backport_4.00_3dlatency`** (same source and options as the 04:11 cook, store `fakelibs/4`; eboot SHA-256 `59f7c18d…`). The ELF differs from the 04:11 cook in 17 bytes: PLT `0x1be8770` → `jmp 0x4008ec`, the Set3DLatency GOT slot no longer pre-filled, and the remaining GOT stubs moved to a second ret0 at `0x4008f9`. libc, fakelibs and param.json are identical. This is the build for the 8.60 tester (no diagnostic traps).
- **Console result (tester, FW 8.60): `PPSA26344_backport_4.00_3dlatency` starts, no crash, everything works.** Rule set to proven (the `proven: false` line removed, rationale updated); `DerivedFixes_AreFlagged` and golden `ac-450-options` updated again.

### 6b2. Yōtei FW 4 cook `PPSA26344_backport_4.00` (14:31) fails to load; the FW 9 DUPLEX backport works (corrected, 2026-10-13)

- **Cook:** options boot_notice, save_mount/save_prepare, audio_port (8 slots), auto_pro, psml_kpn9, plus a Studio-built `ampr_emu.index`. No crash dump (the `D:\PS5\PPSA26344_1791876947` dump at 10:35 is the earlier raw-syscall boot_notice: `SYSTEM_ILLEGAL_FUNCTION_CALL`). Working reference: `FW_900_…_backport_files` (SDK 9.00, a few stubs, dlc_emu, no Ampr/PlayGo fakelibs, no index).
- **User test:** the FW 9 files + only the FW 4 cook's libraries work, which clears the libraries. Static checks agree: compared with `_waitfix` (09:23), libc, param.json, Agc, AgcDriver, Ampr and PlayGo are byte-identical. Psml differs only in the 26 intended `psml_kpn9` bytes. The eboot differs only in the 6-byte `e_entry` redirect, the three slot bounds and a 244-byte boot_notice cave. The cave is correct (`sub rsp, 0xC60`, so aligned; PLT targets usleep `0x17e6920`, open `0x17e6620`, write `0x17e6670`, close `0x17e65d0`; registers restored; `jmp 0x76`).
- **Initial suspicion, superseded below: `ampr_emu.index`.** The FW 4 store's ampr_emu reads AMPRIDX3, and a valid index is **authoritative for `/app0`**: "A miss returns immediately; only an indexed loose file reaches the physical open syscall" (`ampr_emu_index.cpp:2107`). `stat` sizes and mtimes come from the index. Only an invalid index triggers a rescan. Studio's index (100,287 entries: the 100,281 files of the PC folder `PPSA26344-app0` plus 6 overlay files) passes ampr_emu's full validation (header, canonical records, sort order, slots, every lookup; `scratchpad/yotei/check_index.py`), so it is used as-is. Any difference between the console's `/app0` and that PC folder therefore becomes "file not found" for the game. Earlier FW 4 cooks had no index (ampr_emu scanned the real `/app0`).
- **Bisect plan:** (1) remove `ampr_emu.index` from the console overlay; (2) if it still fails, use `_waitfix`'s eboot (identical apart from boot_notice and the slot bounds); (3) then libc / param.json. The error text or code is still needed.
- **First console result (user): "deleted `ampr_emu.index`, the game works". Second result with a log (15:52, `PPSA26344_backport_4.00`): without the index the game stalls; with it, it works.** The earlier conclusion (Studio's index caused the failure) was **wrong**. The log: eboot `EXEC` at 15:52:01.612 (SDK `PPR:04000031`), then 8.2 s of silence (boot_notice `usleep(8 s)`, which returned normally), then the game opens common dialog `NPXS40093` (15:52:09.86) and inits SaveData (15:52:10). After that nothing: the splash screen is never dismissed, the user presses PS (15:52:59), and the shell closes it (`KillApp … requested from 0x7`, user close, no dump). The `PFAuthClient 0x8041013d` lines also occur outside the game (offline licence checks).
- **Mechanism (ampr_emu source):** `AMPR_EMU_APP0_INDEX_AUTOBUILD=1`. The first safe AMPR entrypoint loads `/app0/ampr_emu.index` or scans **all of `/app0`** once and saves the result. Every other file access waits on the index (`ensure_app0_index_ready_slow`: `cv.wait_for` loop, no timeout). For this dump that is 100,281 files (175 GB) behind the overlay, so the game sits on the splash screen for minutes. The first "works without the index" run most likely still found an index ampr_emu had saved on the console during an earlier launch. **Studio's PC-built index is valid (`check_index.py`) and makes the start fast. Ship it, built from exactly the files the console will have.** The cause of the very first failure (14:31, index included) is not established.
- **Studio:** the "Build AMPR index" warning was corrected to say this (index = complete `/app0` list, build it from the exact console files, rebuild after changes; without it the first start scans and can take minutes). `MainViewModel.AmprIndexWarning`.

### 6c. Audit: rules that rely on the target firmware lacking the function (2026-10-13)

- **Principle:** a cook targets the *lowest* firmware, but the same eboot runs on every newer console. A `kind: stub` rule only pre-fills the GOT. Wherever the console's firmware exports the function, the loader binds the real one, and the real one runs with the game's arguments and the lowered SDK stamp (4.00). `plt_stub`, `adapter`, `swap` and `redirect` patch the PLT or rename the import, so they hold on every firmware. Risk class: a `stub` function whose accepted arguments widen in a later firmware (Set3DLatency: 0–1 on 6.00–9.60, 2 from 10.01). SDK-gated differences are harmless, because the stamp makes every firmware take the SDK 4.00 path.
- **First firmware exporting each rule's function** (`firmware_db`, 43 lists; 7.00/11.20/12.60/13.60 dumps encrypted or missing): GetOperationMode 11.00; IsTrinityMode/HasTrinityMode 7.01; GetAvailableCpumask 12.40; SyncOnAddressWait8/16 12.00; VRR peg/unpeg/status 5.02; LatencyControlWaitBeforeInput/LatencyMeasureSetStartPoint 7.01; AllowOutputResolutionWqhdDetection 6.00; Set3DLatency 6.00; AcmBatchJobPriority 10.01; SaveDataCancel 6.00; GetGameTrialsFlag 6.00; CommerceDialogOpen2 7.01; PadIsRemoteController 10.01; Http2 WebSocket/SetRequestNoContentLength 5.02; ShaderTranscode never; libc ThreadDtors/ForceTlsDestructor/ThreadAtexit 11.00; Hmd2 SetAllowPositionalReprojection 6.50; GetOutputStatusWithVrr 10.01; AgcGetIsTrinityMode 9.00. Every renamed-to or redirect target (`IWIBBdTHit4`, `04AjkP0jO9U`, `tsvEmnenz48`) is exported from 1.00 to 12.70 without gaps.
- **Validation screen:** for every stub-like rule's function, the immediates of `cmp`/`test` and the `0x80xxxxxx` error codes in the export and its direct callees were compared across all readable firmwares. Calibration: Set3DLatency shows exactly the 10.01 transition (new `cmp 2`). Each flagged transition was disassembled:
  - AcmBatchJobPriority 10.01 `cmp 3; ja` vs 11.00+ `cmp 4; setae`: both accept 0–3, so it is the same check compiled differently.
  - LatencyControlWaitBeforeInput 7.01 vs 8.00+: the mode (0/1) and count (1–16) checks moved into a tail callee; identical from 8.00 to 12.70.
  - LatencyMeasureSetStartPoint: only checks `arg ≥ 0` on all firmwares. VRR status `-q-vbO859Tw`, Hmd2 setter, Http2 WebSocket, CommerceDialogOpen2 and SaveDataCancel: the differences are internal callees or SDK gates (`cmp 0x11000000`), not checks on the game's arguments.
  - **Result: no other `stub`-kind rule has the Set3DLatency problem.** (Corrected wording: this screen covered only `stub` rules; adapters and renames are covered in §6d.) Limits: an immediate-constant screen (it would miss a changed struct layout with no constant change), and nothing beyond 12.70 is readable.
- **Always-active adapters** (`save_mount_compat` below 5.00, `save_prepare_compat`, `audio_speaker_compat`, `audio_port_compat` below 11.00, GetGamePresets below 11.00) patch the PLT and run on every firmware. Console evidence: the Yōtei 4.00 cook with save_mount/save_prepare/audio_port/auto_pro boots on 8.60 (this build) and the earlier audiofix on 13.60. Static per-firmware check in §6d.

### 6d. Audit: adapters and renamed imports on every readable firmware (2026-10-13)

Goal: every known and optional rule works from FW 4.00 to 11.60 (the last fpkg firmware) and ideally 13.60. Readable: libraries 1.00–12.70 (not 4.30, 7.00, 11.20, 12.60, 13.60) and SceShellCore for 3.20, 4.03, 4.51, 5.02, 5.50, 6.02, 6.50, 7.01, 7.60, 7.61, 8.60, 9.00, 9.40, 9.60, 10.01, 11.00, 11.40, 11.60, 12.00, 12.20, 12.70. Principle: the cook stamps SDK 4.00, and every firmware must keep running genuine SDK 4.00 games, so a value a genuine SDK 4.00 game passes is accepted everywhere. Where a firmware-specific check exists, it was read.

- **`sceUserServiceGetGamePresets` (`size` capped at 0x28, below 11.00): no effect on any firmware.** libSceUserService dispatches through a function table (copied from a static table, entry `+0xc8`) to a client that does only NULL checks, then IPC method `0x30026` with the game's struct as an *output* buffer of fixed length: 0x28 on 1.00–6.50, 0x30 on 7.01–12.70 (4.30/7.00/11.20 encrypted; 12.60 pattern not matched). The `size` field is never read, so the cap is harmless but unnecessary. (Assumption: IPC output buffers are not sent to the server.)
- **`save_mount_compat` (`systemBlocks` −1 → 0, below 5.00): correct on 1.00–12.70.** libSceSaveData.native picks the create-mount validator by the process's compiled SDK. On 12.70, function `0x1de80` checks `≥ 0x12000000` → `0x8220`, `≥ 0x09000000` → `0x7f70`, `≥ 0x05000000` → `0x7cc0`, else `0x7a00`. Rules for create modes (`mountMode & 0x24`), all firmwares scanned: SDK < 5.00 → `systemBlocks` must be **0**; SDK 5.00–11.x → 0 or −1 (FW 5.x: `inc; cmp 1; ja`; FW 6.00+: `dec; cmp -2; jb`); SDK ≥ 12.00 (FW 12.00+ only) → −1/−2, and 0 is refused with the "systemBlocks set to 0" message. So an SDK-4 stamp needs 0 on every firmware, and targets ≥ 5.00 accept the game's −1: `below: 5.00` is exact. The adapter offset `+0x18` matches the Mount3 layout read by the validator. Side note: ShellCore 6.00+ logs "Cancel operation is not supported when systemBlocks is 0", so on 6.00+ consoles a native `sceSaveDataCancel` fails for these mounts (the Cancel stub only covers < 6.00).
- **`save_prepare_compat` (`prepareMode` 2 → 1): correct on 3.20–12.70.** No client-side check (IPC method `0x65`). ShellCore 3.20–4.51 refuse mode > 1 (`cmp eax, 1; ja` → `0x809f8004`). From 5.02 the handler passes the mode through without refusal, and from 6.02 bit `2` selects an extra path. Mode 1 is accepted everywhere. The rule could get `below: 5.00`, but it is not needed for correctness.
- **`audio_port_compat` (main port 0xC00 → 0x800, gain attribute 0x30 → 0x20, PCM fold): correct on 1.00–12.70.** In the `_sceAudioOut2LoPortCreate` validator of every readable libSceAudioOut, all channel masks contain 8 (`0x106` = 1/2/8, `0x504` = 2/8/10, `0x1106` = 1/2/8/12), plus `cmp eax, 8` on 3.00–5.50 and 7.01–9.60, and the `(fmt & 0xb00) == 0x800` path on 12.x. Spot checks: 4.00, 8.60 and 12.70. An 8-channel port takes 8 gains, so the 0x20 gain size and the 8-channel PCM fold follow.
- **`audio_speaker_compat`** only masks the result (`+4` mask, `+8` flags). The layout is ABI (8.60 writes the same offsets from its fixed tables), so it is the same on every firmware.
- **`sceKernelMapNamedFlexibleMemoryInternal` → `sceKernelMapFlexibleMemory` (`IWIBBdTHit4`), flags `&= ~0x8000`: safe on 1.00–12.70 for every sample libc.** From **7.01**, `MapFlexibleMemory` refuses flags with any bit of `0x88000` (`test ecx, 0x88000` → `0x80020016`); 1.00–6.50 have no check. All 15 sample libcs (SDK 9–13: Yōtei, GT7, BL4, BF6, Silksong, KCD2, UFC 6, Dying Light, AC, Beast, PPSA34008, Control, Hades II, PPSA03671) call it only with the constant `0x8000`, so after the adapter the flags are 0. A future libc passing `0x80000` would fail on 7.01+; hardening the mask to `0xfff77fff` would cover that.
- **SyncOnAddress Wait/Wait32/Wait64/Wake → `_umtx_op` (`04AjkP0jO9U`): the target is the same `mov rax, 0x1c6; syscall` stub on 3.00–12.70**, and the ops (0xb WAIT_UINT_PRIVATE, 2 WAIT, 3 WAKE) are kernel ABI. **Defect found (firmware-independent): timed waits.** Sony's 12.70 `sceKernelSyncOnAddressWait32/64` read `[rdx]` as a 32-bit **microsecond** count and convert it. Game libcs call Wait32/Wait64 with `rdx = &u32_us` on the timed path (Yōtei libc `0x112091`–`0x1120b4`: ms × 1000 into `[rbp-0x14]`, `lea rdx, [rbp-0x14]`; same in GT7, Control). The adapters pass that pointer to `_umtx_op` as `uaddr2` with `uaddr = 0`, which the kernel reads as a 16-byte `struct timespec`: `tv_sec` = µs | stack canary bits << 32, `tv_nsec` = more stack. In practice this is almost always EINVAL (nsec ≥ 10⁹), so the timed wait returns at once (possible busy loops), and occasionally a near-infinite wait. Untimed waits (`edx = 0`) are correct. Wait (`Hc4CaR6JBL0`) is only called untimed in the samples. Wake is correct. Related: §1.6 (generic Wait's 64-bit value and op mismatch). This is the timeout half, and it also affects the `proven` Wait32/Wait64 rules.
- **`_sceLibcInternalThreadAtexit` → the game libc's own `__cxa_atexit`**: local to the game, firmware-independent.
- **Fixed (2026-10-13, user decision): Wait32/Wait64/Wait timeout and the MapFlexibleMemory mask.**
  - **Wait adapters** (`B2n8aDorSH4`, `PZQhiiLXRFs`, `Hc4CaR6JBL0`) are now post-call (`post_call: true`) and branch-free, so `PlaceChain` may split them across small caves. GT7's original libc has no int3 run for a 70-byte piece, and a first `contiguous` version failed there. Logic: open a stack frame with a 16-byte timespec; with a timeout pointer, `us / 1000000` → `tv_sec`, remainder × 1000 → `tv_nsec`, `r8 = &timespec`; without one, `setne`/`cmov` force `r8 = 0`. Expected value (32-bit, or 64-bit for Wait64), op (0xb/0xb/2), `rcx = 0` and `r9d = -1` as before, then `call [GOT]`, `leave`, `ret` with `_umtx_op`'s result. Marked `proven: false` (the untimed path is GT7's console-proven one; the timespec path is not console-tested). Wake is unchanged.
  - **MapNamedFlexibleMemoryInternal** now uses `and ecx, 0xfff77fff` (clears `0x8000` and `0x80000`); rationale corrected (the `0x88000` refusal starts at FW 7.01, not 12.70). For every sample libc the result is the same flags 0, so it stays proven.
  - **Verification:** Unicorn emulation of the assembled bytes, 21 cases per variant: NULL, 0, 1, 999 999, 1 000 000, 1 500 000 and 0xFFFFFFFF µs give `(0,0)`, `(0,1000)`, `(0,999999000)`, `(1,0)`, `(1,500000000)` and `(4294,967295000)`. The fake `_umtx_op` sees the right op and value, rsp ≡ 8 mod 16 at entry, callee-saved registers and stack are intact, and the result is returned. The same holds with every instruction in its own cave joined by `jmp rel32`. Also run in the re-cooked Yōtei `libc.prx` (`PPSA26344_backport_4.00_waitfix`, libc SHA-256 `0513b147…`, eboot identical to `_3dlatency`), entered at the real PLT entries (`0x116b80`/`0x116b90`/`0x116bb0` → caves `0x154`/`0x1b9`/`0x7f9`); 2.5 s → `(2, 500000000)`. Cave `0x642` holds the new mask.
  - **Tests (Wait fix):** `SyncWaitAdapters_ConvertMicrosecondTimeout` (new: post-call, branch-free, frame, `/1000000`, `×1000`, `&timespec`, `call_got; leave; ret`); `LibcFixes_MatchGt7` now follows the cave chain with `Samples.Trace` (post-call adapters end with `call [GOT]`… `ret`); `DerivedFixes_AreFlagged` lists Wait32/Wait64; `Ppsa03671Tests.UnitySyncAdapters_ProduceImportsInLibkernel` expects `call [GOT]` for the post-call Wait and `jmp [GOT]` for Wake (Silksong PS5Util.prx, `libkernel_sync_on_address` → `libkernel`); goldens re-recorded (`backport/ac-450-options`, `notbackport-4`, `notbackport-4-no-store`, `notbackport-403-all-options`, `patch/all-known-fixes`, `patch/libc-extended`: only the three adapters' caves/DERIVED notes and shifted stub addresses). Affected classes 187/187.

### 6e. Audit: plt_stub and Pro-mode stubs, byte patterns, fakelibs and their variants (2026-10-13)

- **Native conventions (decrypted libkernel/libSceAgc 11.00–12.70).** `sceKernelGetOperationMode(int* mode, int* lowEnergy)` writes −1, then maps an internal byte through the table `(2, 0, 1, 3)`: 2 = normal, 3 = Pro; `lowEnergy` 0/1. That matches `opmode_normal` (2, 0) and `opmode_pro` (3, 0). `sceKernelIsTrinityMode` returns the flag in `eax` (CPUID `& ~0x1f == 0x840fc0` plus sysctl `kern.gjevmtrb`), matching `ret0`/`ret:1`. `sceKernelHasTrinityMode` reads `hw.sce_main_socid`. `sceAgcGetIsTrinityMode(bool* out)` calls `sceKernelIsTrinityMode`, stores the byte, **and returns it in `eax`**. `byte0` (auto_pro fallback: `*out = 0`, `eax = 0`) equals a native base console. `byte1` (force_pro: `*out = 1`, `eax = 0`) differs from a native Pro (`eax = 1`). AC Black Flag ignores the return (`0x35a5c73`: `xor ebx, ebx` after the call), so this is harmless for its source game. **Changed (2026-10-13, user decision):** `byte1` is now `mov byte [rdi], 1; mov eax, 1; ret` (`c60701b801000000c3`), the same answer as a real Pro.
- **`sceAudioOut2Set3DLatency` plt_stub (below 10.01):** see 6b.
- **`scePsmlMfsrInit` plt_stub ret0 (below 9.00): returns success without initialising.** In 12.70 `libScePsml`, every other Mfsr call (`GetSharedResourcesInitRequirement`, `CreateSharedResources`, `ReleaseContext`, `GetDispatchMfsrPacketSizeInDwords`, …) starts with `cmp byte [0x16ec18], 1` and returns `0x8a810001` (not initialised) otherwise. A native base console's `MfsrInit` returns **`0x8a810012`** ("MFSR isn't supported in sceKernelIsTrinityMode() == 0", `0x2a85f`), so every game shipped on base PS5s handles that code. With ret0 the game believes PSSR is ready and then gets `0x8a810001` from every call. On a PS5 Pro the default `IsTrinityMode` stub (GOT, native from 7.01) and auto_pro both report Pro, so a sub-9.00 cook on a Pro takes the PSSR path with init skipped. **Candidate change:** `ret:0x8a810012` (the native base-console answer) instead of ret0. KCD2 (the source) runs natively on base consoles that return exactly that. **Changed (2026-10-13, user decision):** stub `ret:0x8a810012` (`b81200818ac3`), note and rationale updated, still `proven: false`.
- **force_pro** `plt_stub`s answer "Pro" on every firmware by design. Psml is lifted (`kind: none`), so the real or fakelib MfsrInit runs.
- **Byte patterns (applied with Known fixes, no firmware gate):** the three Wwise patterns change the requested speaker mask 7.1.4 `0x2d63f10c` → 7.1 `0x63f108` at the source (the engine then renders 8 channels consistently). The Frostbite pattern sets a size field `0xc00` → `0x800` in the `0x30c` (12-channel) branch (meaning inferred, not traced; UFC 6 was tested on a Pro at 9.60). Both make the game request 8 channels, which every readable firmware (1.00–12.70) accepts for a main port (6d), so they are safe across the range. On targets that would allow 12 channels they only cost the height channels. The `gpu_scratch` pattern (`s_trap 1` → `s_nop` in shaders) does not depend on firmware.
- **Fakelibs vs every later firmware.** Every fakelib in every set (`4`, `4.03`, `7`, `9.60`, `10`, `10/ppsa34008`, `11`) and variant (`gpu_scratch`, `playgo_0_6`, `playgo_0_6_log`, `psml_kpn9`, `ampr_debug`) was checked: each import must be exported by the firmware (`firmware_db`), provided by a sibling fakelib, or GOT-stubbed, at the set's firmware and every later list up to 12.70. **No import is removed in any later firmware.** At the set's own firmware everything resolves except:
  - `9.60/libScePsml` (and `psml_kpn9`): two libc++ data symbols, `std::__sce_v2::num_put<char>::id` (`3ZotGOwzi9k`) and `numpunct<char>::id` (`5+FD4VpX+nw`), exported by `libSceLibcInternal` only from 11.00, so they are unresolved on 9.60–10.60 consoles. The set comes from UFC 6, tested on a Pro at 9.60, so this path was not hit there.
  - `7/libSceSaveData.native`: `sceFsBfsGetAvailableSize` (`MSQ1n+tzSEA`, `libSceFsInternalForVsh`). This is a `firmware_db` artifact (only `common/lib` is imported); the module is in `priv/lib` and exports it on 7.01, 8.60 and 12.70.
  - Set 4's FW 12.70 Psml/Agc imports that FW 4.00 lacks (21 and 4) are all provided by the set's own Agc/AgcDriver.
- **Variants:** `gpu_scratch` AgcDriver and `psml_kpn9` Psml have exactly the base library's imports and exports (code or string changes only; `psml_kpn9` holds `KPN9_v070.psp` instead of `/app0/k9.psp`). `playgo_0_6` (+12 imports), `playgo_0_6_log` (+14) and `ampr_debug` (+38) all resolve from 4.00. `gpu_scratch` has no firmware gate (no new imports). It came from AC's `10xx+` build, so it is used as-is on 12+ consoles too; whether its scratch hook is harmless there cannot be decided statically.
- **Tests for the two changes:** `KnowledgeTests` now pins both bodies (`StubBytes`: Psml `B81200818AC3`, force_pro `BfBDZGbti7A` `C60701B801000000C3`). Goldens re-recorded (`backport/ac-450-options`, `control-403`, `notbackport-4`, `notbackport-4-no-store`, `notbackport-403-all-options`, `patch/all-known-fixes`): only `PLT -> ret:0x8a810012`, the 9-byte `byte1` in a new cave, and the `ret0` stubs it displaced. Affected classes 199/199.
- **Not decidable statically:** the fakelibs' kernel/driver interface on newer firmware (FW 12.70 Agc/AgcDriver/Psml on 13.60; ampr_emu). Console evidence: set 4 boots on 8.60 (Yōtei) and 13.60 (Yōtei, PPSA32935).

### 6f. Research: an in-game "Debug" notification from a backport (2026-10-13; implemented 2026-10-13)

- **How ShadowMount+ shows its toasts:** from its own payload process, `sceKernelSendNotificationRequest(0, &req, sizeof req, 0)` with `notify_request_t { char unused[45]; char message[3075]; }` (`src/sm_log.c:24`, `include/sm_platform.h:62`). That simple request is what the console renders as the "Debug:" toast in the 8.60 screenshot. It raises "Backport applied to game: %s" when it mounts the fakelib folder (`sm_fakelib.c:1877`), and "Error in game %s:
%s" when its log monitor (`sm_mdbg.c`, 60 s window) sees a fatal-signal block or a line containing `[rtld] <pid> ERROR`. Neither path reads any text from the backport files.
- **The API:** `sceKernelSendNotificationRequest` (`zl7hupSO0C0`) is a libkernel export on all 43 `firmware_db` lists (1.00–12.70). Implementation (4.00 `0x1b320`, 12.70 `0x1c030`): a once-init opens `/dev/notification%d` (0, 1, 7, blocking and non-blocking), then `write(fd, req, size)`; first argument ≤ 8 selects the device. So it is a plain device write, no privileged syscall.
- **Game modules do not import it.** None of 18 sample libcs nor 6 sample eboots; `sceKernelDlsym` only in some eboots (not Yōtei); `sceKernelLoadStartModule` in most. The engine cannot add imports (only rename), so a call would have to be made without an import.
- **Raw syscalls work from app code:** ps5-payload-sdk `samples/install_app/eboot.c` is an installed app eboot that executes `syscall` from its own text (no libkernel). So a cave can do `open`(5) `"/dev/notification0"`, `write`(4) the 3120-byte request, `close`(6), with no import. **Correction (console test below): this holds only for payload/exploit processes, not `native_game` — a game issuing a raw `syscall` is killed by the kernel.**
- **Generic hook point:** every sample eboot (GT7, Silksong, KCD2, Yōtei, AC, Control) has the same SDK `_start` at `e_entry` (`push rbp; …; mov r14d, [rdi]; mov rbx, rsi`), and its text is X-only (`flags 0x1`). A cave reached by redirecting `e_entry` can save registers, build the request and the path on the stack with immediates (X-only text cannot be read, so no string constants in text), make the three syscalls, ignore their results (branch-free, so `PlaceChain` may split it), restore, and `jmp` to the original entry. Engine pieces needed: write `e_entry`, a `PlaceChain` tail `jmp rel32 <entry>` variant, an option in `data/fixes.json` built by C# (like `UnitySkuFallback`), and a GUI text/checkbox (mock-up first).
- **Unknown, console test needed:** whether `/dev/notification0` is reachable from a game's sandbox (ShadowMount and the SDK samples run as payload processes). Cheapest test: a diagnostic eboot with the `e_entry` cave on one sample.
- **Alternatives:** (1) ask ShadowMount+ to append a text from the backport folder (for example `backport_publication.json` or a `backport_notice.txt`) to its "Backport applied" toast; no game patching, but a third-party change. (2) klog line via syscall `0x259` (7, text, 0) as in the SDK `klog()` sample: lands in the kernel log and ShadowMount's `debug.log`, not a toast unless it imitates `[rtld] <pid> ERROR`, which abuses the error path.
- **Implemented (2026-10-13; reworked to route through the game's own libkernel imports after the console test below):** option `boot_notice` in `data/fixes.json` + `src/Ps5Backport.Core/BootNotice.cs`. Redirects `e_entry` (recognised by the SDK `_start` prologue `push rbp; mov rbp,rsp; push r15` + the `mov r14d,[rdi]; mov rbx,rsi` signature) into one contiguous cave that replicates the overwritten 6 bytes, saves the caller-saved registers, builds `/dev/notification0\0` and the 3120-byte request on the stack as immediates, zeroes it with `rep stosq`, optionally sleeps (default 8 s) via the game's imported `sceKernelUsleep`, then calls the game's imported `sceKernelOpen`/`sceKernelWrite`/`sceKernelClose` **through their PLT stubs** (so each syscall runs from libkernel's text, not the game's), and `jmp`s back to the real entry. Message from `BackportOptions.BootNoticeMessage` (GUI text box, CLI `--boot-notice`; delay `--boot-notice-delay`). Verified by `BootNoticeTests` (redirect, immediate-encoded message/path, PLT call targeting, delay emission, pipeline wiring).

- **Console test — FAILED: a raw `syscall` from a game is fatal (2026-10-13, PPSA26344 on FW 4.00 `PPR:04000031`, three runs).** The cave crashed at its first `syscall`. Kernel log `PPRBUG-22859: the process pid=126 directly issued a syscall 240 at 0x1be9082`, then fatal `SYSTEM_ILLEGAL_FUNCTION_CALL` (`0xa002030a`) at `rip=0x1be9082`, inside eboot text (`0x400000:0x1bec000`; the process runs `category=native_game`). The kernel kills any `native_game` process that executes `syscall` directly (outside libkernel's text); the nanosleep (240) was merely the first, and `open`(5)/`write`(4)/`close`(6) would crash identically. **Fix paths:** (a) call the game's own libc imports (`open`/`write`/`close`/`nanosleep`) through their PLT stubs so the syscall runs from libc.prx's sanctioned text — needs the game's dynamic-symbol table to resolve the libc NIDs → PLT addresses (not present in the 180 KB minidump); (b) jump into a `syscall; ret` gadget inside libkernel's text (needs libkernel base + gadget offset); (c) post the toast from a payload process instead (ShadowMount-style). ShadowMount avoids this entirely because it is a payload (`shadowmountplus.elf`), not a game.

- **Option 1 implemented and statically verified (2026-10-13, user-selected).** The cave now calls the eboot's own imported libkernel functions through their PLT entries — `sceKernelOpen` (`1G3lF1Gg1k8`), `sceKernelWrite` (`4wSze92BhLI`), `sceKernelClose` (`UK2Tl2DWUns`), and `sceKernelUsleep` (`1jfXLRVzisc`) for the delay — so each `syscall` runs inside libkernel's text. `BootNotice.Apply` resolves these NIDs at cook time via `Patching.PltEntry` and returns 0 (leaves the module unchanged) when any of open/write/close is missing. Verified on the unwrapped Yōtei eboot (`PPSA26344`): the cave (VA `0x17e8d0e`, text segment extended) emits four `call rel32`s to the game's real PLT entries — usleep `0x17e6920`, open `0x17e6620`, write `0x17e6670`, close `0x17e65d0` — then `jmp`s back to `e_entry+6`. **Still needs the console test (the same `native_game` sandbox question as before, but now the write is a libkernel call, identical to what ShadowMount does from its own process).**

### 6g. FW 13.60 decrypted: firmware_db and the 4.00–13.60 rule audit (2026-10-13)

The 13.60 dump now has on-console decrypted ELFs in `Firmware 13.60/system_dec/` beside the encrypted `system/`. This section extends §6c–6e from 12.70 to 13.60.

- **Import:** `firmware_db/13.60.json`: 255 modules, 20,123 NIDs. The importer merges `system/common/lib` (276 encrypted, skipped) with `system_dec/common/lib`; every module name has a decrypted copy. `fwdb import-*` used to write into the CLI's bundled `bin/…/firmware_db` (a Release copy from 13:12 was found there). It now writes the repository `firmware_db/` when run inside the repository (`Cli.ImportDb`, test `Cli_FirmwareImportsDefaultToRepositoryDatabase`). Rebuild to refresh the bundled copy that cooks read.
- **12.70 → 13.60:** +156 NIDs and one new module (`libSceWebTransport`). **2,049 NIDs removed:** 2,028 from `libwebrtc` (2,471 → 443 exports), 10 from `libSceUrlConfigResolver`, 7 from `libSceVnaWebsocket`, 3 from `libSceNotification`, and **`sceNpGetSandboxId` (`S5gCyCvieII`)** from `libSceNpManager`. None moved to another module. **No sample binary imports any of them** (792 ELF/SELF files: every sample game, `work/` and every fakelib set and variant), and no rule, stub or candidate refers to them. A future game importing `sceNpGetSandboxId` would not resolve on 13.60.
- **Sony builds:** `data sony-builds` now has `J03950001`, `J03950011`, `J03950016` = 13.60 from the dump. This confirms the `evidence` entry for `J03950001` (the FW 13.60 Psml/Agc in the 4 and 9.60 sets).
- **Rule targets on every firmware 4.00–13.60 (39 lists):** every `new_nid` (`IWIBBdTHit4`, `04AjkP0jO9U` ×4), `umtx_op`, and every import that an always-active adapter keeps (GetGamePresets, SaveDataMount3/Prepare, AudioOut2 GetSpeakerInfo/ContextCreate/PortCreate/PortSetAttributes/ContextAdvance/PortDestroy) is exported by all 39, with no gaps. Stub bodies make no calls.
- **§6c screen extended (constants in each rule function's export and its direct callees, 12.70 vs 13.60):** identical for every rule function except four, each read:
  - `sceAudioOut2Set3DLatency`: same `cmp 0/1/2/0xff` as 10.01–12.70. **The "13.60 accepts 2" observation (§6b) is now confirmed statically.**
  - `sceKernelGetAvailableCpumask`: the export is identical; its once-initialiser gains a branch for app types 1/2 (masks `0x3f`/`0x7f`, by a sysctl ≥ `0x3000000`). App types 0/3 keep 12.70's `0x1fff`/`0xff`.
  - `sceHttp2SetRequestNoContentLength`: the export is identical; its worker adds an internal 0x70-byte allocation (`0x817B1022` on failure) once the request state is ≥ 3. There is no new check on the game's arguments.
  - `scePadIsRemoteController`: the controller-table search is vectorised (loop bound `0x8adc` → `0x5c80`, stride `0x5c8`). No argument check.
  - `scePsmlMfsrInit`: still returns **`0x8a810012`** when Pro support is absent (print, then return), the value of the `ret:0x8a810012` stub.
  - SaveData Cancel/Prepare/Mount3, GetOperationMode (`cmp 1/3`), IsTrinityMode (`cmp 0x840fc0`), AgcGetIsTrinityMode, VRR, LatencyControl, Http2 WebSocket, Hmd2, NpCommerce, GameTrialsFlag, libc Thread*: same.
  - The ShaderTranscode stubs and `prHBgnJWBb4` are exported by neither 12.70 nor 13.60.
- **§6d adapters on 13.60:**
  - **MapFlexibleMemory:** the public `IWIBBdTHit4` still starts with `test ecx, 0x88000` → `0x80020016`, so the adapter's mask is still needed and sufficient.
  - **SyncOnAddress:** `_umtx_op` is still `mov rax, 0x1c6; syscall`. Sony's Wait32 still reads `[rdx]` as u32 µs (÷1,000,000). Its sync helpers (`0x3520` wait, `0x36d0` wake, `0x380` syscall) are instruction-identical to 12.70.
  - **GetGamePresets:** the same table dispatch (`[0x1c230+0xc8]` ← static `0x180c8` → `0x4300` → `0x1460`), NULL checks only, then IPC `0x30026` with a fixed 0x30 output window. The size field is never read.
  - **save_mount_compat:** the same SDK dispatcher (13.60 `0x1ecc0`: ≥ 0x12000000 / 0x09000000 / 0x05000000), and the SDK < 5.00 validator (`0x7a20`) still requires `systemBlocks` (`+0x18`) = 0. **So an SDK-4 cook needs the option on 13.60 too** for games that pass −1 (unchanged from §6d).
  - **audio_port_compat:** the port-format check accepts `(fmt & 0x700) == 0x200` or `(fmt & 0xb00) == 0x800`, so 8-channel float `0x800` passes.
- **Fakelibs:** none of the set 4, 4.03, 7, 9.60, 10 and 11 libraries or their variants imports a NID that 13.60 removed. So §6e's "no import removed in any later firmware" now holds through 13.60. Their kernel/driver interface on 13.60 is still not decidable statically (console evidence: set 4 on 13.60, Yōtei and PPSA32935).
- **End-to-end FW 4 cooks** (`work/fw1360-range`): 18 samples (every `default`, PPSA28997, Yōtei `dump`, UFC 6 `decrypted`) × 3 configurations (A = known fixes; B = every compatible option: ubisoft/unity fallbacks, boot_notice, save_mount/save_prepare, audio_speaker/audio_port, auto_pro, gpu_scratch, playgo_0_6, psml_kpn9; C = force_pro + playgo_0_6_log). 52 of 54 published; the 2 B failures are the `audio_port_compat` state below. Every import of every output module (fakelibs included; outputs and the source game's unchanged modules count as providers) was checked against all 39 lists 4.00–13.60: **no import that resolves on 4.00 is missing on any later firmware**. 44 outputs resolve completely on 13.60. The rest are imports also unresolved on 4.00: the known stubbed or left-unpatched functions (all exported by 13.60), plus `NpCppWebApi` C++ symbols from the bundled `sce_module` library, which is still encrypted in the PPSA03671/PPSA24003 sample dumps (an input limitation, not a firmware difference).
- **`audio_port_compat` state too big for EA eboots → 8 slots (fixed 2026-10-13, user decision after investigation).** The 0x90-byte state (16 port slots) did not fit the writable page tail of Battlefield 6 (`PPSA19534` eboot and `SP/BattlefieldGame.Main_Ps5_retail.elf`) or PPSA34008: both data segments end `0x58` bytes before the page end (80 usable after 16-byte alignment), and RELRO ends on a page boundary. `AllocateState` threw, and selecting the option **aborted the whole cook** (2 of 18 samples).
  - **Does BF6 need the downmix? No.** All 4 `sceAudioOut2PortCreate` sites in each BF6 executable are fed by one decision tree (eboot `0x19fdb80`, SP `0x194e920`). The main port (`port_type` 0) is always `0x800` (8 channels, 7.1); the other ports are `0x100`/`0x200` (1/2 channels) or invalid (−1). No path builds `0xC00`. PPSA34008's 3 sites use only `0x100`/`0x200`/`0x800` (`0x7685046`–`0x76850b4`, `0x7685275`, `0x768547c`). The adapter only rewrites 12-channel float main ports, so it is a no-op in both games; the human BF6 4xx+ backport made no audio change either (README Part 16).
  - **How many slots does Yōtei need?** The bed setup (`0x34b5a0` file / `0x74b5a0` runtime) loops over 12 descriptor types. Its per-type count is 1 for types 0–6 (system user) and the user count only for types 7–11 (`cmp rbx, 7; cmovb edx, 1` at `0x34b7da`). Only types 0–2 can get 12 channels (`cmp rbx, 2; ja` at `0x34b8a4`, then `mov ecx, 0xC00` at `0x34ba6c`). The per-user routine (`0x350611`–`0x35077c`) creates types 7–11 only. So at most 3 bed ports plus the transient probe port are 12-channel at once, and 8 slots leaves a 2× margin.
  - **Change:** `state_size` 144 → 72 (+0 grain, +4 fold bitmask, +8 8 × u64 handles), loop bounds `cmp esi/ebp/r10d, 0x10` → `8` in PortCreate/PortSetAttributes/PortDestroy (same-length immediates, so no offsets move), option text 0x90 → 0x48. Test `OptionalAudioTests.AudioDownmixState_FitsEaPageTail` (BF6 eboot, BF6 SP, PPSA34008: failed at 144, passes at 72). Yōtei B re-cook vs the 16-slot cook: the eboot differs in exactly the three bound bytes (`0x17ecdea`, `0x17ece31`, `0x17ece7d`; plus the boot-notice text, which differed between the two test runs). BF6 and PPSA34008 now publish (state at `0xf5f3fb0` / page end `0xf5f4000`). Full suite 678/678.

### 6h. firmware_db coverage: which firmwares have no NID list (2026-10-13)

- **Dumps vs lists:** `.original/.PS5OriginalLibs` has 46 firmware dumps; `firmware_db/` has 44 lists (1.00–13.60). The two without a list are **4.30** (`system/common/lib`: 261 `.sprx`, 0 plain ELF) and **7.00** (265 `.sprx`, 0 plain ELF). Every module is encrypted and the importer skips encrypted modules, so nothing is imported. Adding them needs decrypted copies (e.g. an on-console `system_dec/`, as for 13.60). Until then the nearest lists are 4.03 and 7.01.
- **Encryption survey (every ELF/SELF in all 46 dumps, by header; SELF segment flag bit 1).** 42 dumps are fully plain ELF. Encrypted files are only in:
  - **4.30:** all 287 (`system/common/lib` 264, `system/priv/lib` 23); no decrypted copy.
  - **7.00:** 511 of 512 (`system/common/lib` 268, `system_ex/common_ex/lib` 243). The exception, `store.prx`, is a SELF with no encrypted segment. **Decrypted copies added later the same day (below).**
  - **11.20:** `system_1/` is encrypted (`common/lib` 278, `priv/lib/lib` 26); `system_2/common/lib` has decrypted copies of all 278 common modules, but the 26 `priv` modules (e.g. `libSceFsInternalForVsh`, `libSceSdma`, `libSceVcodec`, `libSceAudioSystem`) have none. So the 11.20 list is complete for `common/lib`.
  - **13.60:** `system/` is encrypted (279 + 28); `system_dec/` has decrypted copies of all 307.
  - **12.60** is plain ELF throughout, but its `system/common/lib` copies are damaged (strings zeroed, see below); use its `lib/`.
- **7.00 imported (decrypted by the user, 2026-10-13).** The dump gained `system_dec/common/lib` (268 ELFs: every `common/lib` module except `store.prx`, whose original is unencrypted) and `system_ex_dec/common_ex/lib` (186 of 243; 57 missing, e.g. `libSceJsc`, `libSceNKWebKit`, `libmonosgen-2.0`, `mscorlib.dll`; `common_ex` is not imported). Copies are intact: 264 carry a build id, and the 6 without one (`libwebrtc`, PlayReady `.self`/`.sbin`) lack it on 7.01 too.
  - `firmware_db/7.00.json`: 246 modules, 19,696 NIDs. **Its NID set equals 7.01's exactly**; the extra module `store` exports nothing (it is also in the 2.50, 4.03, 8.00, 9.20 and 9.60 dumps). 6.50 → 7.00: +1,330, −15 NIDs.
  - So every "first exported in 7.01" in §6c (IsTrinityMode/HasTrinityMode, LatencyControlWaitBeforeInput/LatencyMeasureSetStartPoint, CommerceDialogOpen2) is really **7.00**. `data/introduced.json` (`sceKernelIsTrinityMode`) now says 7.00; the 6.50→next comparison in the Firmware DB page now picks 7.00.
  - Sony builds: **`J02550186`, `J02550200`, `J02550207` = 7.00** (132 ids).
  - The rule and adapter audits (§6c–6e) on 7.00 binaries: §6i.
- **11.20 and 12.60 do have lists.** The earlier "7.00/11.20/12.60/13.60 encrypted or missing" notes (§6b, §6c, §6d) are out of date for 12.60 and 13.60. 11.20's encrypted `system_1` adds no module to `common/lib` (survey above), so its list is complete.
- **`data/sony_builds.json`:** every firmware in `builds` has a list. 12.60 had a list but no build ids. Cause: its `system/common/lib` copies are decrypted ELFs with the string data zeroed (no `W:/Build` path in any of the 508 files; `libkernel` is the same size as the good copy but 416,730 bytes differ), and the intact libraries are in the dump's top-level `lib/` (274 `.sprx`). `ScanSonyBuilds` walked only `*/common/lib`. **Fixed:** it now uses the importer's `LibDirs` (adds `lib/` and 12.40's `system_3/common`). Regenerated: **`J03819182`, `J03819208`, `J03819235` = 12.60**, with no other change (126 → 129 ids).
- **Firmwares with no dump at all** have neither a list nor build ids. Known case: **10.00** (README: PPSA34008's `10xx+` targets 10.00; the nearest list is 10.01). Other public point releases (likely 3.10/3.21, 4.02, 5.00, 11.02, 13.00–13.40) are unverified against an official release list.

### 6i. §6c–6e re-run on FW 7.00 (2026-10-13)

Method: export addresses from `SceElf` (scratch C# helper), capstone disassembly, 7.00 (`system_dec/common/lib`) compared with 7.01 and 6.50.

- **7.00 code = 7.01 code in every module a rule touches.** Text segments have the same size. Byte-identical: `libScePad`, `libSceVideoOut`, `libSceAcm`, `libSceNpEntitlementAccess`, `libSceNpCommerce`, `libSceHttp2`, `libSceHmd2`, `libSceAgc`, `libSceAgcDriver`, `libSceSysmodule`. In `libkernel` (+ `_sys`, `_web`), `libSceAudioOut`, `libSceSaveData`, `libSceSaveData.native`, `libSceUserService` and `libSceLibcInternal` every differing instruction (40–1,319 per module) differs only in a RIP-relative displacement, and every such target holds the same bytes except source-path strings that embed the build id (`W:/Build/J02550186/…` vs `J02561904`). The data segments differ in 2 bytes: the module's own version field (`0x07000044` vs `0x07010000`). Function by function (export + direct callees, addresses masked), 0 of every common export differs.
- **So every 7.01 reading in §6c–6e holds for 7.00**, and the ranges move down: MapFlexibleMemory's `test ecx, 0x88000` → `0x80020016` refusal starts at **7.00** (6.50 has none; read directly); GetGamePresets' 0x30 output window is 7.00–12.70; `cmp eax, 8` in the AudioOut2 port validator covers 7.00–9.60; Set3DLatency on 7.00 is `cmp 0/1/0xff` (0–1 only, like 6.00–9.60); IsTrinityMode/HasTrinityMode `cmp 0x840fc0`; LatencyControlWaitBeforeInput same constants as 7.01; `_umtx_op` is `mov rax, 0x1c6; mov r10, rcx; syscall` (as on 6.50/7.01); the save_mount validators in `libSceSaveData.native` are the 7.01 code. GetOperationMode, AgcGetIsTrinityMode and Psml are not exported by 7.00.
- **Not checkable:** `save_prepare_compat`'s ShellCore check. The 7.00 dump has only `common/lib` (no `vsh/SceShellCore.elf`). 6.50 and 7.01 pass the mode through, and mode 1 is accepted on every firmware read, so the rule is safe either way.
- **§6e fakelibs:** the result for 7.00 equals 7.01's, since the NID sets are identical (§6h). `7/libSceSaveData.native`'s `sceFsBfsGetAvailableSize` is the same `priv/lib` artifact (no `priv` in this dump).
- **Cook change for target 7.00.** Before 7.00 had a list, `FirmwareDb.Select` used the lower bound **6.50** for a 7.00 target. Re-cooking GT7 `default` and Control `4xx+` for 7.00 with and without `7.00.json`:
  - GT7 `libc.prx`: the **`sceKernelMapNamedFlexibleMemoryInternal` adapter is no longer applied**: `4h6F1LLbTiw` is exported by `libkernel` on every list 7.00–13.60 (none ≤ 6.50), so the game calls it natively, as 7.01 cooks already did. The internal entry accepts `0x8000`.
  - Fakelib set 7 `libSceAgc`/`libSceAgcDriver` (both games): the `ret0` GOT pre-fills for `sceKernelIsTrinityMode`/`sceKernelHasTrinityMode` are dropped. These are `stub` kind, which the loader overrides wherever the firmware exports the function (7.00+), so the runtime is unchanged.
  - Reports: about 1,330 NIDs added in 7.00 are no longer flagged missing (e.g. Control's Agc MarkerSpan/AcquireMemSetEngine, GT7's MapNamedFlexibleMemoryInternal). Nothing else in the outputs changed.
  - The console-tested "complete 7.00" PPSA32935 cook (§1.1) was built with the 6.50 list, so it carried the memory adapter. A re-cook for 7.00 does not.
- **`data/fixes.json` texts corrected** (no golden contains them): Trinity "from FW 7.00", CommerceDialogOpen2 "FW 7.00 and later", MapFlexibleMemory "rejects … on FW 7.00–13.60", "cannot fail on 7.00+". Knowledge/Audit/FirmwareDb/CliParity tests 115/115.

### 6j. Yōtei `PPSA26344_backport_4.00` (23:35, audio_port_compat): TV audio broken after a headset test (open, 2026-10-13)

- **Tester report (Valac; firmware not stated):** TV sound worked. The tester then plugged in a headset and later unplugged it. After that, TV audio is broken. Headset audio is still fine. Restarting the game or the PS5 does not fix it. What "broken" means (silence, crackle or distortion) is not stated.
- **The adapters keep no state across a launch.** `audio_out2_downmix` lives in bss past memsz and is zero at every load. NCA probes the output and builds its ports at every boot (§6a). So a failure that survives a PS5 restart needs a changed *input*: a PS5 sound setting, the TV/HDMI audio negotiation, or a game setting stored in the save.
- **Static (decrypted eboot, file VAs = runtime − 0x400000): a device switch does not rebuild ports.**
  - The output monitor (`0x34d0ca`) polls `sceAudioOut2PortGetState`. On a change it writes NCA parameter 0 (speaker flags `0x3fe0/0x3fe1/0x3fe3/0x3fe7/0x3fef/0x3fff`) and the `+0x1a0/+0x1b0/+0x1c0` fields (`0x34d2c1`–`0x34d306`). It then calls `0x347550` (`[vtable+0x30]` → `0x34f190`) and `0x348fc0` (per-voice loop `0x355530`). Neither path calls PortCreate or PortDestroy.
  - Port-reopen helpers that reuse a descriptor (`0x34b350`/`0x34b370`/`0x34b450`, the `0x34b440` deferred-destroy mark) have no call, `lea` or relocation reference, so they are dead code. The adapter's in-place `0xC00 → 0x800` rewrite of the descriptor therefore cannot leave an untracked 8-channel bed.
  - All 14 PortCreate sites: bed setup `0x34b5a0` (12 types; types 0–2 `0xC00`, the others `0x100/0x200/0x880` by a table), mono `0x100/0x100` object-style ports (`0x34b9b5` types 5–6, `0x34bc9a` `table[0x16]` ports for types 0–1, driven by the constant parameter 1 = `0x3fff`), the probe `0x34d53f` and per-user `0x350611`–`0x35077c`. The port set is the same for every output device. The output device changes only how NCA mixes (parameter 0).
  - The only port destruction at runtime (`0x34caed`/`0x34cb3f` in the advance `0x34ca30`) handles descriptors marked `+0x81 = 1`, a release path. The downmix's PortDestroy rule frees those slots.
- **The game knows the output device:** strings `SND_SYNTH_DEVICE_FLAG_TV | _AV_RECEIVER | _SOUNDBAR | _HEADPHONES`, `kProperty_DeviceSpeakerMode`, `master_headphone`, `master_headphone_3D_passthru`, `controller_audio`. Whether the device is a saved in-game option or auto-detected is not traced.
- **Best static candidate: the TV output now reports 12 channels (7.1.4).** Possible causes: "Enable 3D Audio for TV Speakers", or an Atmos receiver or soundbar. Then:
  - the probe and monitor choose 7.1.4 (`0x3fff`), because the PortGetState cap was dropped with the downmix (§6a);
  - the 12.70 `sceAudioOut2ContextAdvance` takes its only SDK gate (`0x2d2e3`), which applies to 12-channel system outputs only: attribute `0x39` = 1 for SDK < 8.00 (this cook) versus 2 for SDK ≥ 8.00. §6a's "SDK 4 behaves like SDK 11" holds only for non-12-channel outputs.
  
  Not confirmed. Questions for the tester: PS5 Sound → Audio Output settings (Output Device, Audio Format, "Enable 3D Audio for TV Speakers"), the in-game audio output setting, what the broken sound is like, the firmware, and whether the original game sounds right on the same TV.
- **Tester firmware: 5.50.** Deep static pass (2026-10-14; no console builds, user decision). The 12-channel-output hypothesis above does not apply on 5.50. File VAs in the eboot; 5.50 addresses in `Firmware 5.50/system/common/lib/libSceAudioOut.sprx`.
- **The game saves no output device; it re-derives it on every boot.**
  - `CSoundManager` is a singleton at `0x6d94010`. Its output mode (`+0x90`: 0 HomeTheater, 1 TVSpeakers, 2 SoundBar, 3 NightMode, 4 Headphones; names at `0x205b700`) is written only by the refresh `0x145b260`.
  - The refresh maps Scream's device flags from `0x2c7a60` (`0x10000000` TV, `0x20000000` home theater, `0x40000000` soundbar, bit 31 headphones) to the mode. `+0x94` = headphones. NightMode is the one override (byte `0x4ff62a9`).
  - The device flags come from NCA parameter `0x1a`, set by the output monitor (`0x34cbc8`): layout byte of `sceAudioOut2GetSpeakerInfo(…, 1)` + 1 (layout 0 TV, 1 AV receiver, 2 soundbar, 3 headphones; Scream switch `0x305c8a`, table `0x1acc1a0`), forced to 4 when the main port's `PortGetState` flags give `& 0x41 == 0x40`.
  - The enum is converted to and from text (`0xf78b20`/`0xf78b40`, telemetry `0xd4e122`), but nothing writes it back to a setting.
- **Playback mode per device (Scream `snd_SynthSetPlaybackMode` `0x31e390`, parameter 2):**
  - At init (`0x303ce3`) the highest channel config in the probe flags: 7.1.4 / 7.1 / 5.1 / stereo / mono.
  - On refresh, not headphones (`0x145b542`): by output channel count (`[this+0x98]`).
  - Headphones with port-state bit 1 (`0x145b4c4`): index `0xb` or `0xe`, an **ambisonic** mode (mode bits `0x20`–`0x2000` = 0OA…5OA, names `0x1ca5af0`, code map `0x345c60`).
  - In ambisonic modes the per-grain driver (`0x34e6b0`, branch `0x34ebcd`) submits bed types 0–1 as mono per-channel ports (`0x34ecb0`–`0x34ed85`, attributes 8/1/9/0). The 12-channel main bed is not submitted, so the downmix fold never runs. In channel modes (any TV) the main bed goes through the fold every grain (`0x34ede5`–`0x34ee4e`: gain count from the descriptor format, already rewritten to `0x800` → 8, then PCM).
  - So headphones can bypass the fold while TV always uses it, which fits "headset fine, TV broken".
- **The fold's inputs are correct:** NCA's context `num_grains` (`+0x10`) is `[obj+0x138]`, the same frame count the bed buffers advance by (`0x34b63f`, `0x34bb2a`). Only NCA creates contexts (probe `0x34d4ca`, then the real one `0x34b6c3`; the last one wins). Every bed port's PCM is submitted every grain. 5.50 `LoPortSetAttributes` copies or converts the PCM during the call (`0x13902` memcpy, `0x138e6` convert), so folding in place before the call is safe for the library.
- **NCA does not read the bed buffer after submitting it (checked 2026-10-14):**
  - Every access to the descriptor's PCM pointer `+0x78` in NCA (`0x343000`–`0x358000`): the setup stores, the four submit helpers (`0x34c709`, `0x34c7d9`, `0x34c889`, `0x34c9af`), the grain driver (`0x34ed51`, `0x34eeb6`) and the bed writer `0x34fd39`. `0x350afd` (another object type, `memcpy` into its own buffer) and `0x353300`/`0x353357` (a mixer buffer table with stride 0x28) are not bed descriptors.
  - The writer `0x34fce0` (vtable `+0x40`) fills a 12-channel bed through the interleaver `0x36c7d0`. That function only stores: 48 floats per 4 frames, no load from the destination. Missing source channels point at a shared buffer (`0x41ac…`).
  - Every source layout code goes to a writing path (tables `0x34fe99`/`0x34fe61`; out-of-range codes take the generic path `0x350165`). Ambisonic sources skip the main bed, but then it is not submitted either (§6j above).
  - The writer's frame count `[System+0x50]` is also the bed size and fold length: `[obj+0x138] = [System+0x50]` at `0x34da4d`.
  - **So each grain overwrites the whole 12 × grain buffer before it is submitted.** The in-place fold cannot leak into a later grain, and nothing else in NCA sees the folded data.
- **Tester (2026-10-14): other games work on every audio mode.** The console and the TV are therefore fine; the fault is specific to this game or backport. Persistent stores that survive a PS5 restart:
  - **The game's saved options** (block `0x4ff6200`–`0x4ff6300`; setters `0xa6ee60`–`0xa70500` mark it dirty). The sound code reads night mode (`0x4ff62a9`), controller speaker (`0x4ff62a8`) and `0x4ff626d` (combined with the 3D flag at `0x145b2bd`). The setters have no static references (no calls, `lea`s, relocations or raw pointers), so the options could not be named.
  - **`ampr_emu`'s own index:** the shipped `fakelib/libSceAmpr.sprx` (set 4, 218,678 bytes) contains `/app0/ampr_emu.index` and `.tmp`; the source autobuilds and saves it on the overlay (`ampr_emu_index.cpp:119`). This build ships none, so the console keeps one from the first launch. All 28,811 files under `sounds/` (`.xvag`, `.at9`, `.mid`, one `.bus`/`.dml`/`.gmx`) are shared by every output device, so a stale index would affect headphones too. **Not a TV-only cause.**
  - A console setting that only this game reacts to: on 5.50, an "8 channels + 3D flag" port state (the builder's `dl == 3` case) makes the game run 2 output channels with the 7.1 flags `0x3fef` (`0x34d1ca`). An SDK-11 game on firmware 11 or later never receives this combination from 5.50's builder.
- **From-scratch pass over every audio path (2026-10-14):**
  - **Imports:** AudioOut2 Initialize, MasteringInit, ContextResetParam/QueryMemory/Create/SetAttributes/Advance/Push/GetQueueLevel/Destroy, UserCreate/Destroy, PortCreate/SetAttributes/GetState/Destroy, GetSpeakerInfo, Set3DLatency (plt_stub ret0); plus Ajm, Acm and AudioIn. Only NCA calls AudioOut2, except `Initialize` + `MasteringInit(0)` in the game's sound init `0x1444ea0`.
  - **Lifecycle:** `CreateInstance` (`0x347690`) is reached only from `snd_SynthInit` (`0x302c60` ← `0x250620` ← `0x14582e0` ← `0x1444ea0`). The teardown `0x34bfc0` (PortDestroy for every port, then ContextDestroy) is reached only from the destructor `0x34e500`. **NCA builds its context and ports once per boot.** The downmix state therefore holds the 3 bed slots (types 0–2; the probe's slot is freed) for the whole session. No slot leak, no re-creation.
  - **Formats by channel code** (table `0x34ba4e`): 1 → `0x100`, 2 → `0x200`, 9 → `0x200`, 12 → `0xC00`, everything else → `0x880`. Only the type 0–2 beds are `0xC00`. The BGM types 3–4 are `0x880` (port type 1). A failed per-user `PortCreate` (`0x3507cb`) returns without cleanup, so the remaining per-user ports of that user are not created.
  - **5.50 `LoPortSetAttributes` PCM (attribute 0, `0x13321`):** converts from the caller's buffer into the port's queue slot through `0x4eb0` during the call (frames = the context grain `[ctx+0x88]`, slot = `[port+0x30] + queue index × align(frames × 4, 0x80)`). The fold runs before that conversion, so the timing is correct.
  - **The output refresh `0x145b260` runs only on Scream event 6** (`0x1458cc0`, jump table entry 6 → `0x1458d49`). The Scream tick (`0x305d13`) raises event 6 whenever (channels | 3D, device flags) differs from its cached value, which also happens on the first tick after init. **So the refresh runs at every boot,** and each boot's routing is a pure function of the console's audio state plus the saved options.
  - **Saved audio options** (names are hashed, no strings; inferred from use): `0x4ff626d` disables 3D audio (the refresh computes r12 = 3D flag ∧ (¬opt ∨ `[0x50924d8]` = 1) for sound variable `0xdfb952415301922c`; the ambisonic switch also needs it); `0x4ff62a8` controller speaker (`0x1445980`/`0x145b454`: TV, home theater or soundbar and not headphones → aux bus `0x12` output = `0x10000`, the pad-speaker port; otherwise −1); `0x4ff62a9` night mode (output mode 3). A group of bytes `0x4ff626c`–`0x4ff6272` sits next to them. The only writers are the option setters (`0xa6e000`–`0xa71000`) and the options-menu handlers (`0xf4…`–`0xf6…`). **The game never changes an option because of a headset.**
  - **Result:** within one boot the patch behaves the same regardless of the device. It cannot become broken by being used; a different path needs different inputs. A TV-only asymmetry exists only through the ambisonic bypass (headphones) and the controller-speaker routing (TV).
- **Second from-scratch pass (2026-10-14, Fable):**
  - **NCA property indices** (table `0x44fec90`, stride 0x48, names from the initialiser): 0 OutputModesSupported, 1 OutputModesPermitted, 2 OutputMode, 3 SampleRate, 5 NumSamplesPerFrame, 0x1a DeviceType, 0x1b DeviceStatus, 0x1c DeviceSpeakerMode. The game's 38 `SetParam` sites (`0x304xxx` init, `0x31e4xx` SetPlaybackMode) never set 0x1a–0x1c; only the monitor does (`0x34d2c1`–`0x34d306`). The device-flags getter `0x2c7a60` is a pure read of the Scream cache. **No saved option can force a device mode.** The device-change handlers `0x34f190` (ambisonic sub-port re-plan, types 0–1, skips descriptors without a sub-port array) and `0x355530`/`0x348fc0` (per-voice) never touch the main bed.
  - **Bed layout proven:** the 12-channel interleaver `0x36c7d0`, run natively on the host with numbered sources, is an identity (bed slot *i* = writer table slot *i*, every frame). In the 7.1-source case (`0x3502db`) the writer puts four zero-buffers in slots 8–11, so **the heights are bed channels 8–11, as the fold assumes.** Unicorn mis-executes the AVX unpack sequence (QEMU-5 TCG), so the run was native (Win64→SysV shim, `native_interleave.py`).
  - **5.50 server and client are SDK-neutral on the TV path:** `libSceAudioSystem` 5.50's SDK-shaped immediates are message-type bytes (`shl r12d, 0x18` before each `cmp`), `orbis_audiod` has none; the client's `0xa0f28` is an init-parameter field (`[rbp-0x118]`), not the SDK (`0xa0f30`/`0x60018`, plus `0x60014` for the legacy API only).
  - **Port-state field names (AnyPS5 `AudioOut2PortState`):** +0 `output` (route; `& 0x41 == 0x40` = headphones), +2 `num_channels`, +4 `volume`, +6 `reroute_counter`, +8 `flags` (bit 0 = 3D). The builder's "force 0x40" construct (`0x1c4b2`, from per-output bit 0 and a nibble of `0xa0248`) exists identically on 12.70 (`0x214a0`): it is the normal "all audio to headset" route, fed by the same system state other games use.
  - **Result:** the patch is correct and device-independent at every layer that can be read. Persistent, Yōtei-only, TV-only candidates remain the saved options and the console's TV audio configuration.
- **Implemented as the separate option `audio_bed_71` (2026-10-14, user decision: keep the downmix and compare).** Option "Audio 7.1 bed (engine)", two code patterns + the PortGetState cap below, `FixTable` refuses it together with `audio_port_compat`. Comparison cook `PPSA26344_backport_4.00_bed71` (same settings as the 23:35 cook, `audio_bed_71` instead of `audio_port_compat`, boot notice "Yotei FW4 cook B: audio_bed_71 (7.1 bed, no downmix)"; eboot SHA-256 `61bf68ae…`). Byte diff against the 23:35 eboot: the two pattern bytes (`0x310a4f` ff→ef, `0x359eca` 0c→08), three PLT entries back to `jmp [GOT]`, the cap cave in place of the downmix caves, the notice text, two bookkeeping bytes; nothing else. Tests: `OptionalAudioTests.Cook_AppliesSevenOneBedOnlyWithOption` (3 cases), `SevenOneBed_PatternsMatchYoteiOnce`, `FixTable_RejectsBothAudioBedOptions`; OptionalAudio/Knowledge/OptionalPatch classes pass. **Console test pending on FW 5.50 (Valac): TV vs headset with build B; if TV is still broken with B, nothing TV-specific remains in the backport.** Design, from the proposal:
  - Pattern A, Scream `snd_SynthInit` permitted output modes `0x3fff → 0x3fef`: `be01000000c745a0ff3f000031c9c645a802` → `…c745a0ef3f0000…` (`SetParam(1, 0x3fff)` at `0x30407c`; unique). Bit `0x10` of property 1 is read only by the bed-channel selection `0x34b8b5`; the other readers (`0x34bb60`, `0x34dab6`/`0x34db1d`/`0x34db82`) use the ambisonic bits. Beds become code 8 → format `0x880`, buffer stride 8, 8 gains, written by the 8-channel bed path (`0x34fe52`: 7.1 → `0x34ffe0`, 5.1 → `0x350038`, stereo → `0x34fe71`), which the BGM ports (types 3–4, `0x880`) already exercise in the shipped game.
  - Pattern B, probe port `0xC00 → 0x800`: `48b8000c000080bb0000` → `48b800080000` `80bb0000` (`movabs rax, 0xbb8000000c00` at `0x34d4f7`; unique).
  - `0x880` main ports are accepted on 4.00 (`0x1582c`: `bt 0x106, 8`; `test sil, 0x7e`), 5.50 (`0x127ec`/`0x12739`) and 12.70 (§6a). No other `0xC00` creation site exists (`0x34ba6c` is reached only with code 12).
  - Keep a PortGetState cap (`num_channels` 12 → 8, post-call, branch-free) for consoles that report 12 channels: otherwise the game picks 7.1.4 (`0x34d1a9`) and the writer takes the generic fallback (`0x350079`) into an 8-channel bed. T4 showed the cap alone is clean.
  - Drops the five downmix adapters, the 0x48-byte state, the caves and the EA page-tail constraint (§6g). Height channels are lost instead of folded at 0.7071 (same as Wwise/Frostbite). Options can carry patterns (`gpu_scratch` does; `Pipeline.Discovery` concatenates them).
- **Per-user ports** (types 7–11, `0x350550`, created at user login; per-type tables `0x1acdf30` codes, `0x1acdf40` kinds, `0x1ace2fa` port types, `0x1acdf00` flags): type 7 controller speaker (AudioOut2 port type 4, stereo), 8 personal (3, mono), 9 voice (2, mono), 10/11 port type 6 (code 9). Types 3–4 are BGM (port type 1, 8 channels) and 5–6 main stereo. Only types 0–2 are the 12-channel main beds, so the downmix does not touch the per-user ports.
- **Every SDK gate in 5.50 libSceAudioOut** (SDK copies `0xa0f30` and `0x60018`):
  - GetSpeakerInfo: mode-1 check ≥ 1.50 and the real-info path ≥ 1.50 (both pass); flags bit 1 (`= 2`) only for SDK ≥ 5.00, in the synthesized **headphone** result `0x1de7e`.
  - Port-state builder `0x1c4e3`: `+8` bit 1 only for SDK ≥ 5.00, on **headphone** ports.
  - MasteringInit `0x1d108`: ≥ 3.00 (passes; the game calls `MasteringInit(0)` at `0x1444ec8`).
  - An attribute at `0x1354b`/`0x13853`: ≥ 3.00 (passes).
  - Legacy sceAudioOut `0x170d`: ≥ 5.00 (not used).
  
  **The TV path is identical for SDK 4.00 and SDK 11 on 5.50.** The headphone path differs only in those two flag bits.
- **The speaker info is the system's own state:** libSceAudioOut copies a 0x984-byte block from audiod shared memory when its version counter changes (`0x1dd04`, `0x1c254`). With headphones (system state `& 0x40`) GetSpeakerInfo synthesizes layout 3 (mask 3, angles ±90°). Otherwise it returns the system's copy.
- **Monitor channel mapping** (`0x34d16d`/`0x34d187` tables, same as the probe `0x34b30f`): 1 → mono, 2/4 → stereo, 6 → 5.1, 8 → 7.1 (but **2 output channels when port-state `+8` bit 0, the 3D flag, is set**: `0x34d1ca`; the 5.50 builder reports 8 channels + bit 0 for its "3D" case, `0x1c547`), 12 → 7.1.4. 3, 5, 7, 9, 10 and 11 → flags `0x3fe0`, 0 channels. At boot the probe refuses those (start-up crash), so they can only appear at runtime.
- **Conclusion:** the game and the backport hold no state across a launch. With the same console state, the 5.50 TV path is SDK-neutral, so a fault that survives a PS5 restart means the console's audio state changed: a sound setting, or the TV/HDMI/receiver side. Inside the backport the one TV-only component is the fold: it runs only in channel playback, while headphones can go ambisonic and bypass it. Next evidence needed (no build): whether the PS5 menu and another game have TV sound; the Sound → Audio Output settings ("Enable 3D Audio for TV Speakers", output device, audio format); how the sound is broken; whether audio was already broken in gameplay before the headset (the first TV check may have been the menu only).

## 7. Open items

1. Run a physical FW 4.xx console test of the patched set-4 AGC, and any console test of the patched set-4.03 AGC.
2. Decide on the generic SyncOnAddress Wait adapter contract (§1.6).
3. PPSA38510: run the UD2 load diagnostic, or get TTY output with the `Il2CppUserAssemblies.prx` load return code.
4. Indiana Jones: report the audio A/B result; identify the Force Pro failure.
5. Console-test option `psml_kpn9` (Psml reading `/app0/KPN9_v070.psp`) with a PSSR game on a Pro.
6. Optional: confirm the 13.60 SaveData result sizes from decrypted 13.60 ShellCore/libSceSaveData.native.
7. PPSA26344 (§6a): console-test `PPSA26344_backport_4.00_downmix` (is the sound clean with the 12→8 downmix?); NP-104602-3 is decoded (§6a: game-side dialog for NpManager `0x8055A011` from the blocked-PSN sign-in); confirm with the original game or a signed-out launch. Narrow the PortCreate rule's `below` once a firmware/SDK that accepts 12-channel ports from an older SDK is known. Re-test Indiana Jones (§3) dialogue with the new rule.
8. PPSA26344 (§6b): fixed and confirmed on FW 8.60. Still open: run `PPSA26344_backport_4.00_3dlatency` on 13.60 (the downmix/audio_port_compat cook has not run there) and on a 10.x/11.x console if a tester has one.
9. §6d: SyncOnAddress Wait timeout fixed and MapFlexibleMemory mask widened (emulator-verified). Console-test `PPSA26344_backport_4.00_waitfix`, then set Wait32/Wait64 back to proven. plt_stub, patterns and fakelibs: see §6e.
10. §6e: Psml stub now returns `0x8a810012` and force_pro `byte1` returns 1 (done). Console-check: a sub-9.00 cook on a PS5 Pro (PSSR skipped cleanly?), KCD2 on a base console, and `gpu_scratch` on FW 12+.
11. §6h: get decrypted 4.30 libraries for `firmware_db` (and, optionally, the 26 11.20 `priv/lib` modules). (12.60 build ids and 7.00: done.)
12. §6i: a FW 7.00 cook no longer applies the MapNamedFlexibleMemoryInternal adapter (native call, as for 7.01 targets). Console-check one re-cooked 7.00 build (e.g. PPSA32935) before relying on it.
13. §6j (tester on FW 5.50; other games work on every mode): Yōtei TV audio broken after a headset test, and a restart does not fix it. Statically the downmix is correct at every layer; candidates are the game's saved options and the console's TV audio configuration ("Enable 3D Audio for TV Speakers"). Compare build A (`PPSA26344_backport_4.00`, downmix) with build B (`PPSA26344_backport_4.00_bed71`, native 7.1 bed, no downmix) on TV and headset, and get the in-game audio options and the PS5 sound settings.

---

# Part 2 — Work archive, 10 October 2026

The former `work/CONFIRMED_FINDINGS.md`, unchanged except that its headings are one level lower. Its §7 statement that the fixtures are deleted is outdated (see the Part 1 table above).


Consolidated on 10 October 2026 before cleaning `E:\PS5_TOOLS\Backport\work`.

### Scope and evidence standard

This document preserves useful results from the local research, game analysis, diagnostic captures, benchmarks and verification records. It distinguishes observed console behavior, static binary facts and desktop test results. A static import check or successful desktop test is not a console compatibility certificate. Historical reports describe the inputs and code at their original run; they are not new tests of the current working tree.

The user explicitly chose to delete **all fixtures too**, retaining only this file in `work`. Raw logs, diagnostic ZIPs, binaries, temporary tools, package outputs, screenshots, benchmark baselines and private ELF fixtures are therefore removed. Source files, root documentation, `data`, `firmware_db`, `fakelibs`, `fakelib_source`, `.original`, `build` and Ghidra projects outside `work` are outside this cleanup. Evidence paths below identify former locations, not surviving files. Hashes retain identity/provenance but cannot reconstruct a deleted file.

### 1. Crow Country / PPSA19068: confirmed startup failure

Source: `crow-diagnostics/completed-crash-findings.md`.

Two complete LZ4 dumps decompressed to ELF cores: 303,529 → 3,982,760 bytes and 308,681 → 4,007,032 bytes. The main thread had RIP=0. Its raw stack contained return address `0x10dd6c1`; subtracting the eboot load base `0x400000` gives ELF VA `0xcdd6c1`. The preceding indirect call at `0xcdd6bb` used slot `0x20267e0` (runtime `0x24267e0`). Initialization at `0x1491206` resolves `il2cpp_set_find_plugin_callback`, storing the result into this exact slot at `0x1491212`.

TTY recorded `unable to load /app0/Media/Modules/Il2CppUserAssemblies.prx, error:0x80020002`. The local kernel reference identifies this as ENOENT. The assembled deployment contained `Il2cppUserAssemblies.prx` with lowercase `c`, whereas the compiled runtime path requires uppercase `C` in `Il2Cpp`.

**Console confirmation recorded in the report:** explicitly renaming the module on the console made the game boot. This confirms startup, not extended gameplay. The latest overlay already published the uppercase spelling; Windows assembly/overwrite retained the old directory-entry case. Byte hashes matched, so hash verification alone did not detect the deployment error.

Operational conclusion: preserve exact runtime filename spelling during full-game assembly and transfer; verify by enumerating directory entries. A case-only rename may need an intermediate name. AMPR reported 64 hooks with no missing/failed hooks in the captured launch; that does not certify every AMPR path. An earlier 128-KiB partial dump recovered only a 2,592,288-byte prefix and no required stack/heap data; it was insufficient to identify the null call and is superseded by the complete dumps.

### 2. The Rogue Prince of Persia / PPSA28155 / 01.006.000

#### Authentication: console observations and confirmed native result

Sources: `crow-diagnostics/rogue-runtime-finding.md`, `rogue-save-comparison/README.md`, `rogue-sku-offline-fallback/README.md`; surviving production documentation: `../README.md#unity-unset-sku`.

The recorded main-account/network-enabled condition produced KNAFFE-0010 at Start on the reported FW 13.60 console. Disabling the console's Internet connection allowed that account to play, save and reload. A local offline account also reached gameplay despite PSN/Ubisoft warnings. These observations isolate a network/account initialization condition; they do not establish damaged saves.

KNAFFE-0010 is the game's generic `ErrorList.UnknownError`: initializer `0xf739a1` assigns ID 10 at static offset `0x20`, and formatting uses `D4`. It is not a unique SaveData error code.

The online trace entered partner connectivity and `CheckFullApplicationAccess.HandleSkuFlagResult`; the working offline trace entered `HandleOfflineMode` and did not reach that SKU check. `FinishAsFailure` appeared in both captures and alone did not distinguish fatal startup from a working offline flow. Later formatted platform logs reported `Got SkuFlag! NotSet!` and `Got Unset SkuFlag!`.

Static verification tied PSN request `0x0FA00002` to PSN ELF function `0x41f0`, calling `sceNpEntitlementAccessGetSkuFlag` through `0x32f60`. It serializes the returned integer unchanged and reports request success for nonnegative platform status. IL2CPP `GetSkuFlagRequest.Run` is at `0x57f2740`. Metadata enum values: NotSet=0, Trial=1, Full=3. Wrapper/authentication executable bytes in the audited baseline matched the original game. Why the platform returned NotSet remains unestablished.

`HandleSkuFlagResult` at `0xf4a990` handles native request failure separately, Full as success and Trial as trial failure; its other-SKU branch selects UnknownError at `0xf4ae2a`. The linked permission consumer at `0x39a2ef4` recognizes InternetNotAvailable and can continue through its existing offline/TitleScreen policy.

**Later console confirmation supersedes the old candidate README's pending status:** the user confirmed the guarded SKU-zero fallback starts gameplay on the main account with Internet enabled. The production opt-in option is `unity_sku_offline`, requiring Known fixes. It changes only the recognized zero-SKU error classification, retaining a failed task and existing offline policy; it does not force Full, native request success, trial bypass or entitlement success. Production recognition uses linked code/data patterns and fails closed on unsupported/ambiguous layouts. The shipped option adds no diagnostic logger. Confirmation is limited to this title/layout; other games and firmware remain separate checks.

#### Offline warning popups: confirmed and integrated

Sources: `rogue-offline-popups/README.md`; surviving documentation: `../README.md#ubisoft-offline-popups`.

`UbiservicesErrorList::.cctor` at `0xf95270` creates OfflineModeEntered=4520 at `0xf957a8` and AuthenticationNotReachable=4521 at `0xf957f0`. Their severity field is `+0x1c`: Standard=0, SilentError=2. Both popup implementations (`0xf39ec0`, `0xf3a3c0`) recognize severity 2 and execute their existing non-null completion callbacks without rendering.

The verified candidate changes exactly two severity bytes at `0xf957b2` and `0xf957fa`, 0 → 2. The user subsequently confirmed both messages were removed. Production option `ubisoft_offline_popups` is opt-in, requires Known fixes, recognizes the related definitions/handlers rather than a title ID, and adds no trace logging. It does not make Ubisoft services available or alter authentication/save results. Counts are 2 when both change, 1 when one was already silent, and no new entry when unchanged/unrecognized.

#### Saving: level-transition checkpoint confirmed

Sources: `rogue-save-trace/evidence-20261010/findings.md`, `rogue-save-trace-v2/evidence-two-launches/findings.md`, `rogue-save-trace-v2/evidence-latest/findings.md`.

The initial capture had 4 successful reads (UbisoftSave once, global three times) and 11 successful writes (UbisoftSave ten times, global once), without SaveRun, SavePersistent or skipped-cloud-slot markers. A two-launch follow-up showed one global/profile creation and successful global reads on relaunch; subsequent icon events accompanied UbisoftSave writes. Neither earlier capture requested a run-progress write. Closing through the console did not record the traced managed OnApplicationQuit callback in those captures.

The later level-exit capture contains `SAVE:transition-save → SAVE:run → SAVE:gameplay-file (run) → SAVE:write-file (run) → SAVE:write-ok`. The user confirmed progress persisted after finishing the first level. This supports the observed level-transition checkpoint, rather than a SaveData adapter delaying an earlier run-write request. It does not prove every later checkpoint or universal save policy. The second-launch trace ended before gameplay loading; restored progress is user-confirmed, not evidenced by a run-read marker in that trace.

Verified static path: `TransitionStackElement.OnUpdate` (`0x5df500`) waits for its timer/flag at `+0x88/+0x8c`, then transfers persistent shards, invokes `TransitionStackElement.SaveRun` (`0x5dfaa0`) and executes exit triggers. SaveRun calls `GameplaySaveManager.SaveRun` (`0x8c51b0`) unless cloud-slot loading is active. `CreateSave` (`0x8c2250`) writes a blank global profile through `Save` (`0x8c32a0`) without SavePersistent. `GameplaySaveManager.OnApplicationQuit` (`0x8c1e30`) dispatches account shutdown, not SaveRun directly.

`SaveHintUI.OnSaveOperationStart` (`0x8bd680`) displays the shared icon for Write/Delete/Duplicate/Rename (0/1/3/4), not Exists/Read (2/5). Its corresponding stop callback does not receive commit success. An icon or an existing save container does not establish a gameplay checkpoint or durable native commit. `PS5Account.WriteSave` (`0x5a6b70`) returns true only on filesystem status 0; native commit/validation is a separate layer.

Keep the normal backported SaveData plugin: the adapter-off persistence comparison hung at connection. No forced-save or forced-success patch was justified. The recorded native errors `0x809f8032` (service API 41) and `0x809f8500` (API 43) were not sufficiently classified to call harmless or causal. Reference-library disassembly maps IDs 41/43 to Backup/GetEventResult for the older SDK path; this is not direct analysis of FW 13.60's encrypted native library.

#### Mid-level "progress automatically saved" icon is a meta save, not a run checkpoint (2026-10-12)

Static analysis of the original Il2CppUserAssemblies (metadata v31 parsed directly; method addresses reproduce the ones above, e.g. SaveRun `0x8c51b0`). The FW 4 cook changes only the SKU byte at `0xf4ae2a`, the two popup bytes, the SDK fields and the code extension in this module; no save code differs from the original.

- **Run state is written only at level exits.** Direct callers of `GameplaySaveManager.SaveRun` (`0x8c51b0`) are `TransitionStackElement.SaveRun` and three debug/state-select tools. The only data reference to SaveRun is its IL2CPP method-pointer entry, so there is no hidden delegate path.
- **The mid-level flame comes from `SavePersistent` (`0x8c5690`).** Its callers include `SavePersistentFeature.OnStart`, `ForceSavePersistentEffectData`, `ArchivistService.ClaimReward`, skill tree, forge, altar, collection and skins UIs. SavePersistent calls `Save` with an exclude list containing only `PlayerSaveData`. Meta, quest and global sections are saved; player run state is not. Ubisoft profile writes show the same icon (`SaveHintUI` ops 0/1/3/4).
- **Run-save freshness rule.** `RunSaveExists` → `RunSaveExistsAndIsFresh` (`0x8ce520`) loads both files and compares `FindSaveSection<GlobalSaveDataSection>().totalRunsRealPlaytime` (double, +0x20). The run save is used only if its value is greater than the persistent file's. If the persistent file cannot be loaded or has no global section, the run save counts as fresh. `GlobalSaver.OnSaveRequested` (`0x7e44a0`) copies `PlaytimeService.totalRunsRealPlaytime` (+0x38), which `PlaytimeService.Update` (`0x872180`) increases every frame. A persistent save written after the last level-exit save can therefore make that run save stale. This is retail logic.
- **Conclusion.** Resuming from the start of the level after a mid-level quit is the game's design, not a backport save failure. A developer reply from the Early Access period ([Steam](https://steamcommunity.com/app/2717880/discussions/0/4335356530803351458)) describes the same behaviour. No rule or option should change this: forcing SaveRun mid-level would be a per-game gameplay patch the game's load path does not support. Console check if needed: run the original game on 13.60 with the same mid-level quit; it should behave identically.

#### Experimental mid-level checkpoint resume (2026-10-12, not console-tested)

Per-game experiment, not an engine rule. Generator: `PPSA28155_checkpoint_test/cpatch.py`, input = unwrapped backported Il2CppUserAssemblies. Signed output SHA-256 `27834712f277a4348758a8581ef29e0b297a106ebcfe1e6b8ff320966f441a32`.

- **No saved position exists.** None of the 11 savers stores coordinates, a room or a checkpoint ID. `PlayerSaveData` has `lastStateID`, stats, items, gold, energy and medallions. `LoadLastRunStackElement` loads the level by `lastStateID`, then `FinaliseLoading` (`0x5dc070`) calls ApplyData, ExecuteExitTriggers and Pop, and sets `ApplicationFlowService.IsRunActive` (+0xb8) = 1.
- **The mid-level triggers are action-framework code.** `SavePersistentFeature.OnStart(Entity, ActionInstance, List)` and `ForceSavePersistentEffectData.ExecuteInternal` both tail-jump to `SavePersistent(true, true)`.
- **Useful game APIs:**
  - `TeleportExtensions.Teleport(Entity, Vector3, bool)` `0x4f87f0`
  - `RuntimeServiceLocator.Get<T>` `0x1d07630` (MethodInfo in rdi)
  - player: `ItemProgressionManagementService.playerVariable` (+0x28) → `ScriptableVariable<Entity>.get_CurrentValue` `0x39493b0`
  - `SaveOperationHandler.SaveAsync` / `ExistsSync` / `LoadSync` (`0x8cb730` / `0x8c7420` / `0x8c7e80`; LoadSync success = 1)
  - `Array.NewSpecific` `0x415580`, `String.NewSize` `0x412fb0` (chars at +0x14)
  - `byte[]` class slot `0x8328980`
  - `GlobalSaver.OnDataLoaded` restores `PlaytimeService.totalRunsRealPlaytime`.
- **What the patch does:**
  1. At both triggers it runs SavePersistent, then, only if IsRunActive, `TransitionStackElement.SaveRun`. It then queues the side file `chkposN` (24 bytes: 'RPCP', x, y, z, playtime stamp) through SaveAsync.
  2. `seta`→`setae` at `0x8ce819`, so the same-frame persistent + run saves stay fresh.
  3. FinaliseLoading reads `chkposN` and accepts it only if the stamp equals the restored playtime ±0.05 s. It then arms a 20-frame countdown.
  4. A hook on `PlaytimeService.Update` (`0x872180`) teleports the player when the countdown reaches 0.
- **Space used:** code goes in `DebugTriggerItemMenu.SpawnItemDebugWindow` (`0x5244c0`; entry now `ret`). State lives in metadata slots `0x83846e0..0x838470f`, which only that function referenced; a whole-text disp32 scan found 0 other references.
- **v1 console result.** The game booted. The SavePersistent trigger fires before the level starts, so resuming returned to the beginning; no mid-level capture occurred.
- **Where the mid-level flame comes from.** It is most likely the Ubisoft profile save, though the call path is not yet confirmed on console. Only two paths write save files: `GameplaySaveManager.Save` and `UbisoftSaveManager.SaveDirect` (`0xa72f60`). `UbisoftSaveManager.Save` (`0xa738e0`) is called by `PlaystationAchievementManager.SetProgressInt/Float`, `ES_TrackingManager.SendPlayerPlayedTime` and the Ubisoft bookkeeping after SaveRun/SavePersistent. It either saves immediately (call `0xa73958`) or sets `wantsToSave`. `TickUnimportantSave` (call `0xa737e3`) then saves once `UNIMPORTANT_SAVE_INTERVAL` = 19.0 s has passed.
- **v2 changes** (SHA-256 `2d710d762cfe6b9c1f5acb35ebe11c2d8a01c3e247d905528cbf9dd49d2d62e1`):
  - Both SaveDirect call sites also run `checkpoint()`.
  - `checkpoint()` writes the run file directly: `Save(account, GetRunFileName(), async)`. This skips SaveRun's Ubisoft bookkeeping, which would otherwise re-request a Ubisoft save every 19 s. It keeps the guards: busy flag, IsRunActive, and `staticStorage+0x12` (cloud-slot).
  - The level-exit call `0x5df638` arms a 600-frame no-checkpoint cooldown, decremented in the Update hook, so transition-room positions are not captured. `TransitionStackController._currentTransitionStackElement` cannot serve this purpose: it is set per transition and never cleared.
- **Open risks:** whether a checkpoint can fire after death or victory while IsRunActive is still 1.
- **v2 console result.** The run file was saved (the slot showed playtime and biome) but no teleport happened at the Paachi trigger or the last flame. The failing stage is unknown.
- **v3 = v2 + diagnostic log** (generator `cpatch3.py`, SHA-256 `68fff8ba83deba707fb0daa187746623e58bbd1b7547ee068268cf85de6be713`). Lines are appended to `/app0/rpcp.log` as `RPCP <event> <hex>` through the module's own libkernel imports: `open` GOT `0x820ffa8`, `write` `0x820ffe0`, `close` `0x820ffb0`; flags `0x209`, mode `0666`. Lines cover every checkpoint, continue and teleport decision.
- **v3 console result: `0xa0020328` SYSTEM_XO_VIOLATION.** PS5 text segments are execute-only, and v3 read its log strings from the code cave. Rule for every cave patch: put no strings, constants or tables in text. Use immediates or the stack, and read only data segments (GOT, metadata slots, .data). v2's stamp comparison also read two doubles from the cave; v2 never crashed, so its restore path must have exited before that point, most likely at ExistsSync.
- **v4** (generator `cpatch4.py`, SHA-256 `6216a4dfd11def32b25018a250861a2aa255e5ed6640407bb88fc65b2ff6a975`):
  - Log lines are `RPCP <2-hex code> <16-hex value>`; codes are in `rpcp_codes.txt`.
  - The log path is built on the stack and the tolerance doubles come from `movabs`.
  - Of 778 cave instructions, a scan found 0 data accesses into text.
- **v4 console log.** Capture works. Ubisoft flames before the run started logged `0a` (no active run). Flames during the run logged run save → position → `19` (position file queued). Decoded positions were x=22.5/y=19.1 and x=134.9/y=48.1, z=0; stamps were 400.3 s → 438.5 s. No restore line (`1d`) was logged, so `LoadLastRunStackElement.FinaliseLoading` never ran.
- **Resume chain.** `ResumeRunNode.Run` (`0x53ecf0`) always pushes `LoadLastRunStackElement`. Its `OnPushInternal` (`0x5db610`) calls `RevertRun` and pops without FinaliseLoading in three cases (return addresses after the call):
  - `LoadRunWithoutApplying` fails: `0x5db8c9`;
  - no `PlayerSaveData` section: `0x5db8e5`;
  - `ApplicationFlowManager.FindState(lastStateID)` returns null: `0x5db850`.
  Otherwise `OnStateReady` (`0x5dbc70`) waits for the player and a 0.25 s delay, then `OnLateUpdate` calls FinaliseLoading.
- **v5 = v4 + entry logs** with the caller return address, codes `32`–`37`, on ResumeRunNode.Run, LoadRunOrPersistent, OnPushInternal, OnStateReady, FinaliseLoading and RevertRun. The prologues `55 48 89 e5 41 5x` are relocated. SHA-256 `1c8c90deb676397869724bc6ddfec163bdc603d5620424e3d39cb96b007477c8`.
- **v5 console log.** Continue at session start logged `LoadRunOrPersistent` (caller runtime `0x8063b43d` = static `0x48b43d`, StartGameplay, so the module base was `0x801b0000`). It was not followed by ResumeRunNode or LoadLastRun. The game judged the run save absent or stale, loaded the persistent data only, and a new run began at level 1. Checkpoints then worked again (`19`). Leading hypothesis: something saves `global` after the last checkpoint, while IsRunActive is already 0. `RunManagerSystem.OnSystemDisabled` clears it.
- **v6 = v5 + logs:**
  - `Save` entry (`0x8c32a0`): caller, and the first 4 UTF-16 chars of the file name;
  - `SavePersistent` entry: caller;
  - RunSaveExistsAndIsFresh: run and global stamps at `0x8ce80e`, result at `0x8ce81d`.
  SHA-256 `f630616869be4677f4279b84b23391890657d47336f397b15b2a2236db18562f`.
- **v6 console log.** Continue → RunSaveExistsAndIsFresh compared run stamp 547.9 s against global stamp **0** and returned true. The global file's GlobalSaveDataSection.totalRunsRealPlaytime is 0, so the stale-run hypothesis is refuted. ResumeRunNode and LoadLastRunStackElement were never entered. The real Continue path is `SaveSlotsViewUI.StartGameplay` → `LoadRunOrPersistent` (call `0x48b438`), which loads and applies the fresh run file directly. The player starts at the level start, so v1–v6 never restored because they hooked the wrong path. The checkpoint `Save` file name begins with "run".
- **v7 restore on the real path:**
  - The call at `0x48b438` is wrapped and sets a flag (D+0x19).
  - The freshness compare captures the run file stamp (D+0x20).
  - After LoadRunOrPersistent returns, `chkposN` is accepted if its stamp equals the run file stamp ±0.05 s. The position is then held pending (D+0x1c, 3600 frames).
  - The Update hook arms the 20-frame teleport once IsRunActive and the player exist, using a non-logging player lookup. A level exit clears pending/armed state.
  - SHA-256 `de5f485639ff6874ceba8bd569eaf62462c3f7691bcdaf67381ef05ed2a0e038`.
- **v7 console log.** The restore chain ran end to end: run stamp 637.24 = file stamp → accepted → run active, armed → teleport due → teleported. However, later checkpoints sat at x≈30.7 near the level start (22.5, 19.05), so the teleport did not stick. A Ubisoft flame between "armed" and "due" also wrote a level-start checkpoint, overwriting the good one.
- **v8 changes** (SHA-256 `5c495b0629774a1bd75ddb25786f016647f0686d43fdcf6a206ea1b96879c85f`):
  - No checkpoints while pending, armed or verifying.
  - The teleport fires 60 frames after the run becomes active.
  - 45 frames later the player position is checked against the target, x and y within 2 units. If it has moved, it re-teleports, up to 4 attempts. The log records the actual position.
- **v8 console result (final; experiment closed).** Continue placed the player at the last flame. Side effects followed, as expected from a raw transform teleport: level logic tied to walking through rooms was skipped or re-triggered. The specific symptoms were not recorded. Not integrated, and not an engine rule. Remaining work, if ever resumed:
  - document the side effects;
  - consider the game's own in-level teleport path (`TeleportFeature.Teleport` / `UseLevelTeleportFeature` / `LevelTeleportComponent.TeleportProxy`) instead of `TeleportExtensions.Teleport`;
  - classify deaths/victories that coincide with a flame.
- **Lessons that carry over to other titles:**
  - PS5 text is execute-only.
  - The `/app0` file log through the module's own `open`/`write`/`close` imports is a practical runtime-trace channel.
  - Verify the deployed module before reading a log: the expected codes must appear.
  - Hook the path the console actually runs (here `LoadRunOrPersistent` from StartGameplay), not the first plausible one.

#### Failed diagnostic comparisons worth retaining

| Comparison | Recorded outcome | What it establishes |
| --- | --- | --- |
| SaveData SDK-only; Mount3/Prepare adapters removed | KNAFFE-0010 persisted | Removing these adapters did not fix that startup condition; it does not prove all SaveData behavior correct. |
| Restore executable SDK to PS5 `0x11000040` / PS4 `0x12590001` | PRX_SCE_MODULE_LOAD_ERROR `0xa0020102` before initialization | No comparison of the Start-screen error was possible; the exact failed module was not identified. |
| Stub only imported `sceNetCtlGetInfo` with not-connected error | KNAFFE-0010 persisted | This selected import was insufficient; dynamically resolved calls/cached state remained possible. |
| Report unavailable via imported `sceNpGetNpReachabilityState` | KNAFFE-0010 persisted | This selected reachability query did not resolve the startup condition. |
| Earlier `EnsureInternetAvailabilityTask` UnknownError-classification candidate at `0xf433b5` | User reported failure, corroborated by later investigation | Do not revive this earlier producer as the confirmed fix; the later SKU handler was the validated target. |
| Remove SaveData adapters for persistence isolation | Connection message and hang | Not a successful persistence comparison; normal plugin retained for checkpoint confirmation. |

#### Useful binary identities

SHA-256 values identify the historical candidate/restore combination; diagnostic packages themselves are deleted.

| Historical file | SHA-256 |
| --- | --- |
| Cooked baseline eboot | `192d120a51d58949d736f11c98870572a7eb0415b09af169ecf374c9a8602658` |
| Baseline Il2CppUserAssemblies | `93e0b179b9c39b805a6d5f68b8d1b30cdd24fcf0eb54d4e39ef1c3405780bacc` |
| Baseline ubiservices | `d5d4018f55f23cf5c4c42401da6d7810bb292df9340e88c844ccf593c507edd0` |
| Normal cooked SaveData | `23ce1e1dea0c1088167fb230f868d9b16e862cb2f4f9f9ccd329a0e4086a780e` |
| SDK-only SaveData comparison | `32a5743e693db0bfeac2389919f044b995aca1abef6d8eb282288be009f45fee` |
| Guarded SKU candidate with logger | `f84dcc0f08f8a2a72f8daa8ab7e587ed2ba6fee77b09eed50016cfbeab3e56bb` |
| Two-popup candidate module | `02cfa748db1412af787fecea634d7d75b2dcfd136f62484f3e20712bec11ce9a` |

Candidate loggers are not part of the integrated production options. Addresses and hashes above apply to the analyzed build, not arbitrary Unity binaries.

### 3. AMPR 0.3.6.6: verified binary/source differences

Sources: `ampr-ghidra/COMPARISON.md`, `ampr-source-validation/*`.

| Binary | ELF SHA-256 |
| --- | --- |
| Generic FW4, exact revision unknown | `26367ffde3e746eeaf552cb5f595002f271f91890eb22125070b7a92ace99076` |
| Published 0.3.6.6 | `87ddeabc10093e8b9bc6a38f7f65198586b695bd5629f7a56ebfe0fd55b27a3b` |

The 0.3.6.6 release identity matches upstream commit `c343e32736ca5320aab5575b907f5ab916746296`; its SPRX hash is `362c84e31e3a06a42f50fe4d6ea124d15e73f77c901470f90823935d7f36ca76`.

- Both export the same 118 sceAmpr functions (122 entries including initialization aliases).
- The old allocator (`0xd0`) uses a 256-MiB internal pool; the newer allocator (`0x3a0`) uses 64 MiB. Both use 16-KiB alignment and a 512-entry chunk table. Exact old provenance and relative runtime suitability remain unknown.
- The old AMM command-buffer constructor/destructor (`0x1b640/0x1b650`) only return zero. The new counterparts (`0x20a80/0x20d10`) register/unregister command-buffer lifetime in a side table, matching imported source. Registration entered upstream in c343e32. Preserve newer behavior absent an observed regression.
- The new binary uses Multiple/Requests AIO imports versus the old single-request APIs; upstream 56ecc32 (0.3.6.4) introduced batch submission in the reactor. Added event/wait/join/time-counter imports demonstrate implementation changes, not executed runtime paths.
- `sceAmprAmmSubmitCommandBuffer` (`0x1cfa0` vs `0x22830`) has the same normalized 39-instruction body. Completion-wait wrappers also retain the same forwarding behavior.
- Against exact FW 4.03 metadata, old/new published binaries respectively resolve 98/112 function imports, plus one data import each, with no missing/unknown functions. The source-rebuilt module resolves 111 function imports with no data imports, missing or unknown functions.
- The 38-reference mandatory-hook matrix covers FW 4.00–12.70 entries, with 48 mandatory hook entries and no absent NID exports in each reference. This is export presence, not ABI/hook-runtime certification.
- Payload verification completed with `exports=118`, `definitions=122`, `imports=111`, `libc_internal_imports=9`, `kernel_imports=102`, `hash_buckets=234`, `hash_changed_bytes=231`, `fself_version=0x02000001`; required libraries were libSceLibcInternal.prx and libkernel.prx.

No conclusion that a larger pool or fewer imports is universally better is supported. Static reference/export checks do not prove console ABI, data-import behavior or complete runtime compatibility.

### 4. Import-library and standardized game analysis

Source: `import-library-audit/audit.txt`. The audit scanned 138 modules, recording 2 cross-library mappings and 4 unreadable/unsupported inputs. The two cross mappings are PPSA12544 default `Media/Modules/PS5Util.prx`: NIDs `Hc4CaR6JBL0` and `q2y-wDIVWZA`, from `libkernel_sync_on_address` to `libkernel`. The 4 errors included non-ELF, invalid SELF ranges and encrypted/compressed SELF inputs; do not present those as successfully analyzed modules.

In PPSA03671, the default eboot's `86f0wFtxn0k` import belongs to libc, and its libc already exports the initialization implementation at `0x10ed70`. FW4 UMTX NID `04AjkP0jO9U` belongs to libkernel; the existing 4xx+ eboot uses that import and still has the libc export. NID similarity/replacement alone must not lose library ownership.

Completed standardized analyses retained in the tables below: HadesII-standardized-v2 (19 modules), PPSA03671-library-fix (17), PPSA34008-standardized-v2 (25). The first PPSA34008-standardized run **failed** because no SDK pair was known for firmware 12. The completed V2 profile separates firmware import analysis (including 12.00) from supported Studio dry-run targets (4.03/10.01); completion does not add SDK-12 cooking support.

Unresolved function counts are static observations. Data imports are unchecked; provider sets are flattened by NID and cannot validate real module load order/ABI. Byte reference counts do not prove reachability. Zero unresolved functions is not a successful console boot. Whole-game contextual findings also survive in `../README.md#ppsa03671-and-crow-country` and `../README.md#ppsa34008`.

### 5. Performance, UI, decoder and architecture evaluations

Surviving reports: `../README.md#performance`, `../README.md#diagnostics-and-ui`, `../README.md#instruction-decoder`, `../README.md#cli-catalog-schema-decisions`, `../README.md#verification`.

Accepted performance changes were one firmware metadata parse per run and signing directly from existing ELF memory slices. Before/after payload and normalized legacy report hashes matched for the documented paired workloads. The accepted changes reduced public large Cook allocation 579.6 → 387.6 MiB (~33%) and private large Cook allocation 2,398.0 → 1,974.1 MiB (~18%). Private analyses saved about 109 MiB per operation. Private small Analyze nevertheless had higher measured working-set/LOH peaks, so reduced allocation is not a universal peak-RAM or leak-free claim.

Focused metadata benchmark: duplicate loads 218.52 MiB / 236.2 ms versus one load 109.93 MiB / 124.9 ms. Small sample counts and wide timing intervals prevent precise speed guarantees. The private large Cook still reached approximately 1.8–1.9 GiB working set. Two-pass loading, ArrayPool, scratch ELF files, Channels and parallel transformations were deferred because of measured tradeoffs and unproven full-pipeline equivalence.

UI filtering experiment on 10,000 rows: showing the full result dropped from 10,001 collection notifications to 1, approximately 2.72 MB allocated to 82 KB, and roughly 16–17 ms to 0.7–1.5 ms in recorded runs. This is a focused view-model measurement, not an interactive latency/accessibility proof. Headless and native process memory do not isolate every Skia/GPU allocation or demonstrate a long-running leak-free UI.

Decoder evaluation passed 530 specified synthetic reference cases (18 named plus 512 generated), and decoded 8 named stub bodies plus 45 adapter blocks without invalid/truncated instructions. Linear segment decoding avoids some byte-pattern false positives but can lose alignment after embedded data and miss real code. Existing eboot counts differed by two sites. Iced 1.21.0 stays an optional evaluation dependency; replacing the production scanner was rejected. No reference count establishes runtime reachability or complete ABI safety.

Phase 8 retained the existing CLI parser and JSON catalog. The SQLite experiment checked semantic round-trip and equal query hits, not persisted .NET provider/crash/concurrent-write behavior. 43,000 membership queries took about 6.3–7.0 ms with JSON sets versus 51.8–64.8 ms with per-query in-memory SQLite calls; this does not justify a production migration. The prototype schema validated 52 existing fix records and 6 edge cases. A copied-record `proovn` typo was rejected by the schema but accepted/defaulted to Proven=true by the then-current Core; no such typo was found in repository data. Unknown stub references and empty adapters still require Core semantic validation. Mandatory/versioned full schemas remain an authoring-contract decision, not a completed runtime feature.

Mutation evaluation of Firmware.cs recorded 20 killed, 3 survived, 0 uncovered and 0 timeouts, with 4 compile-error and 2 ignored target mutants. The survivors were redundant input checks whose removal still rejected malformed inputs downstream. This scoped experiment is not a whole-engine mutation score.

### 6. Verification retained from actual records

The XML table below preserves all recognizable project result summaries found in `work`, including failed runs so that they are not silently represented as passes. Test counts changed as regressions were added. XML paths and source hashes preserve provenance; deleting XML removes the original detailed execution evidence.

The popup unrestricted run records **428 tests, 0 failed, 0 skipped, 0 errors**, corroborated by its log. The latest stored strict All/Debug run also records 428 with zeros. Earlier unity/general runs reported 379/395 tests in their text summaries; the later production option documentation records 406/428 after additional tests. These describe different historical code states.

Sandbox-constrained runs recorded failures and were followed by successful unrestricted runs. Failure reasons are retained below from the XML rather than inferred solely from counts. Root reports retain historical zero-warning build evidence. No new build, regression run, live game launch or console test was performed during this cleanup.

### 7. Consequences and reproduction after cleanup

Deleted fixture roots: `NOT_BACKPORT_elf`, `CONTROL_elf`, `GT7_root_elf`, `GT7_sce_module_elf`, `GT7_sce_sys_elf`, `GT7_fakelib_elf`. Existing private tests, CLI goldens and `verification/DecoderEvaluation` refer to these exact `work` paths. They must be recreated from the appropriate original corpus before strict private verification. `scripts/verify.ps1 -Lane All` / `-Lane Private` will refuse setup when `work/NOT_BACKPORT_elf` is absent; direct private fixture tests can skip or fail depending on their policy. Public verification remains the appropriate fixture-independent lane; it was not newly run here.

Keep source/reports outside `work` as the maintained implementation reference. Generated `work` folders can be recreated by verification, packaging, performance and game-analysis scripts. Historical baseline DLLs, traces and diagnostic candidate packages cannot be recreated from hashes alone; reproducing them may require original revisions, original binaries and new console captures. Deleted candidates must not be referenced as available restore packages.

The broader software audit requested earlier remains unfinished. This cleanup consolidates existing confirmed results and does not upgrade them into a new full audit.

<!-- GENERATED_EVIDENCE -->
### Preserved data tables

#### Historical xUnit result summaries

| Former XML path | Total | Failed | Skipped | Errors | SHA-256 |
| --- | ---: | ---: | ---: | ---: | --- |
| crow-diagnostics/unity-tests-all.xml | 379 | 0 | 0 | 0 | f439f3d7ee6f3e223da3d58a8dfeb1ae61936a864313ec1458852c5b541ee83e |
| import-library-audit/tests-debug-unrestricted.xml | 370 | 0 | 0 | 0 | 955440abd6051f100124b90ec326471cf5616ef1d38ac94ecccf93c584fb958d |
| import-library-audit/tests-debug.xml | 370 | 58 | 0 | 0 | 1ab2f621fbc050cb2cfc4ef35aa8180b545fa4ae0a0eac1261f46449bf25b99b |
| import-library-audit/tests-release.xml | 373 | 0 | 0 | 0 | a4e1aec2a9c93b5e3abab44870895e69c62c74c54b841c0d5b790d1ee86e200d |
| phase3/clean-verification/20261010-013805-e5d0d6dd7dec4d529bd18781ee84c2eb/Public-Debug.xml | 196 | 0 | 0 | 0 | c3aa5b08076f1520942c1a3750218dbae790fd33de626a0bb91948f3484e8d94 |
| phase3/clean-verification/20261010-013805-e5d0d6dd7dec4d529bd18781ee84c2eb/Public-Release.xml | 196 | 0 | 0 | 0 | 8bb280990c26e8141923f93f622abca867ded4d2e6b6e0b1fcaae56a8a39b82f |
| phase3/clean-verification/20261010-014326-22d19587892f46379c0aa40cae53480e/Public-Debug.xml | 204 | 0 | 0 | 0 | 2387b3c62e12e12c11b2551a9fe930b8ee6f368c98c812f854da56c61603f8ed |
| phase3/clean-verification/20261010-014326-22d19587892f46379c0aa40cae53480e/Public-Release.xml | 204 | 0 | 0 | 0 | dea93b1679205cfd31f32664406224013a62789a6ef35400c0e3aad1fab8dcf9 |
| ubisoft-popup-verification/tests-unrestricted.xml | 428 | 0 | 0 | 0 | 9dc30327db0e26fb7992b33c0fad10a81438efcbbd9d71a5dadcabbc3af8c5fe |
| ubisoft-popup-verification/tests-workspace.xml | 428 | 3 | 0 | 0 | dc9e0c00d25ed92332d02735b24f5aa2022d6f532d61332d1913dd7ad5d1b05e |
| ubisoft-popup-verification/tests.xml | 428 | 66 | 0 | 0 | cc8639d0d1df65f042660c0a061f455234da11e3c3774d9637b849d01cbd642c |
| verification/20261010-013558-6a85530179d54d3c9ca9bf11f1369d3c/Public-Debug.xml | 196 | 0 | 0 | 0 | 6b454c0090dac4d0876d7f4e83b837f03ee7dd093bc1a061dbd9c26282d21975 |
| verification/20261010-013705-63eb68d00f4b48f99fb13357ac7aa45d/All-Debug.xml | 307 | 0 | 0 | 0 | 8d8632b8b22be36d875a2e999737031c077438afb691cf7bd4bcdfb84a421484 |
| verification/20261010-013705-63eb68d00f4b48f99fb13357ac7aa45d/All-Release.xml | 307 | 0 | 0 | 0 | 95713425587a4298d9d42b29db0afc930d12872af4988d82a6ef1116f28c8e48 |
| verification/20261010-014149-71ef80c9a44745b1b956008b433f75cf/All-Debug.xml | 315 | 0 | 0 | 0 | 3d9ba1b869c793e4962bca488b98d5688a091399fc5438fb432abf1fafdcd082 |
| verification/20261010-014149-71ef80c9a44745b1b956008b433f75cf/All-Release.xml | 315 | 0 | 0 | 0 | 625d223ec7d09979ace9bb1b04bd89c8df7d4026259256524b6dd2c9c91c02dc |
| verification/20261010-015726-b929e9c95fab4c38968c6cb804d2dcff/All-Debug.xml | 335 | 0 | 0 | 0 | ba2ef922d0f5b3a71fe13bc8163b75d215b30aa351545189b5b3e9ac93e8dce1 |
| verification/20261010-015726-b929e9c95fab4c38968c6cb804d2dcff/All-Release.xml | 335 | 0 | 0 | 0 | 84b08dba0e38e9197575645bf6b1bfb10ecd599da0da7a1aec2f19d6fd17e24f |
| verification/20261010-020149-98103a498e98481ab603a06ea49b3ada/All-Debug.xml | 340 | 0 | 0 | 0 | d37571ac31eec61a1353660862fd62ef30ea19e662d436bb167f0b215ef62d42 |
| verification/20261010-020149-98103a498e98481ab603a06ea49b3ada/All-Release.xml | 340 | 0 | 0 | 0 | 571b29866284a9782e66d109a23d420007e8368496545cbfda83daf9ee0a77c0 |
| verification/20261010-020320-70e2650e82ae4af8bfb628a0399f0b57/All-Debug.xml | 342 | 0 | 0 | 0 | 01a4a6696a5e2f663423f972092ba6ddcde21baf7c6294b7175e2f8a80ab1bc0 |
| verification/20261010-020320-70e2650e82ae4af8bfb628a0399f0b57/All-Release.xml | 342 | 0 | 0 | 0 | 77aee0e89d2d342b736974d9af7fea9b18e2bf6326bab5b1a03da2fb6349404c |
| verification/20261010-021831-33e89269039d464ab0860f7771c58575/All-Debug.xml | 345 | 0 | 0 | 0 | ab86c6737694a1f2e7255357a9cc84165adb016db05ad0732de4fbd53987103a |
| verification/20261010-021831-33e89269039d464ab0860f7771c58575/All-Release.xml | 345 | 0 | 0 | 0 | 4a9033aa7cb6ef26016427a0b4b6548b60ff2e111a012507bcacf01a55e581ac |
| verification/20261010-024404-5ee838d65cc0491e885b1cb78be8235e/All-Debug.xml | 357 | 0 | 0 | 0 | f0cc1d285010203bec3513ce5be3913d02bdf310553394c8639ed2fefcc6fbc4 |
| verification/20261010-024404-5ee838d65cc0491e885b1cb78be8235e/All-Release.xml | 357 | 5 | 0 | 0 | 98b48c64c1dbf121797b3d327923648e23635360b95691d689fef37bfa897456 |
| verification/20261010-024610-a1fb4e8e72c34cf3aa7826bdb9f51e65/All-Debug.xml | 357 | 0 | 0 | 0 | 80367e8411ddecef0b97c5b9295c67ea7c8abc3df265aa47b2b0e59b0a67cd4e |
| verification/20261010-024610-a1fb4e8e72c34cf3aa7826bdb9f51e65/All-Release.xml | 357 | 0 | 0 | 0 | 84f3663a76de111a91e2251e6d98c56741e346120eacd941f238d6e31547951f |
| verification/20261010-031031-82cadd74618248d5b350078a07cefaa4/All-Debug.xml | 357 | 0 | 0 | 0 | 752050bee7772f5171d1e0f2370ff1da7c9c692c8ce97f7ba0838ca7b56b455c |
| verification/20261010-031031-82cadd74618248d5b350078a07cefaa4/All-Release.xml | 357 | 0 | 0 | 0 | 9beeb43f043694510a452feb21332d41922d6cb59f70a58a5532f6760bcb4ca1 |
| verification/20261010-032020-04ec1872bf4d4006b2e800c901635003/All-Debug.xml | 357 | 0 | 0 | 0 | 0db1e08ada3d45e22bb6c80f33d0a3cb369f28e2e2dd37544f9377de1ed984b2 |
| verification/20261010-032020-04ec1872bf4d4006b2e800c901635003/All-Release.xml | 357 | 0 | 0 | 0 | e105c3f42ef11214186d69e7dcb0085523d0ee4e10d806c021fd665797607cbc |
| verification/20261010-033423-a569d4839f4c4c9e8ab08dd05df6f48c/All-Debug.xml | 360 | 0 | 0 | 0 | 9a90a1a0bedf024135aee4b4ae5e383b97b55011397f23584a19ff72936b712e |
| verification/20261010-033423-a569d4839f4c4c9e8ab08dd05df6f48c/All-Release.xml | 360 | 0 | 0 | 0 | 34d2a85c8af23a6bbc73cd5adbbfe04d32e2f6fb0f449b1f20c384d70205253a |
| verification/20261010-034148-a1ba172b9cd14724a8c293992786e60b/All-Debug.xml | 360 | 1 | 0 | 0 | 0b1cb0e829b3763717adb495e6276882f2bc37419f3867a7a22297f132223a8e |
| verification/20261010-034236-d3245083451d4c61882b080500452d7d/All-Debug.xml | 360 | 0 | 0 | 0 | 2e39e342ea10735b91274ab3b5316df8d004e31cce4f6a299799bc900d780369 |
| verification/20261010-034236-d3245083451d4c61882b080500452d7d/All-Release.xml | 360 | 0 | 0 | 0 | 7736d303e1520a922c10954c8a51149a21effb4774f89bc46aaa736ce70af2f4 |
| verification/20261010-224459-2fc643784bc448f1acea6e25a9f8402a/All-Debug.xml | 428 | 0 | 0 | 0 | 3057133ba2ba6e7df13efcbce8625b9414543c95660342617a55552853c37d45 |

#### Failed-run context

##### import-library-audit/tests-debug.xml

Distinct failure messages (up to five, each bounded to 600 characters):

- Assert.Equal() Failure: Values differ Expected: 0 Actual:   1
- Assert.Equal() Failure: Values differ Expected: 37 Actual:   1
- Assert.Null() Failure: Value is not null Expected: null Actual:   \"Access to the path is denied.\"
- Assert.Single() Failure: The collection was empty
- Assert.Throws() Failure: Exception type was not an exact match Expected: typeof(System.IO.IOException) Actual:   typeof(System.UnauthorizedAccessException) ---- System.UnauthorizedAccessException : Access to the path is denied.

##### ubisoft-popup-verification/tests-workspace.xml

Distinct failure messages (up to five, each bounded to 600 characters):

- Assert.Equal() Failure: Values differ Expected: 0 Actual:   1

##### ubisoft-popup-verification/tests.xml

Distinct failure messages (up to five, each bounded to 600 characters):

- Assert.Equal() Failure: Values differ Expected: 0 Actual:   1
- Assert.Equal() Failure: Values differ Expected: 37 Actual:   1
- Assert.Null() Failure: Value is not null Expected: null Actual:   \"Access to the path is denied.\"
- Assert.Single() Failure: The collection was empty
- Assert.Throws() Failure: Exception type was not an exact match Expected: typeof(System.IO.IOException) Actual:   typeof(System.UnauthorizedAccessException) ---- System.UnauthorizedAccessException : Access to the path is denied.

##### verification/20261010-024404-5ee838d65cc0491e885b1cb78be8235e/All-Release.xml

Distinct failure messages (up to five, each bounded to 600 characters):

- Assert.Contains() Failure: Sub-string not found String:    \"\" Not found: \"Passed: busy-cancel\"
- Assert.Contains() Failure: Sub-string not found String:    \"\" Not found: \"Passed: close-busy\"
- Assert.Contains() Failure: Sub-string not found String:    \"\" Not found: \"Passed: close-save-failure\"
- Assert.Contains() Failure: Sub-string not found String:    \"\" Not found: \"Passed: search-selection\"
- Assert.Contains() Failure: Sub-string not found String:    \"\" Not found: \"Passed: stale-analysis\"

##### verification/20261010-034148-a1ba172b9cd14724a8c293992786e60b/All-Debug.xml

Distinct failure messages (up to five, each bounded to 600 characters):

- Assert.Equal() Failure: Strings differ                                    ↓ (pos 22) Expected: \"exit 0\\n== 10  (<repo>\\\\fakelibs\\\\10)\\n   libScePsml.s\"··· Actual:   \"exit 0\\n== 10  (<repo>\\\\src\\\\Ps5Backport.Tests\\\\bin\\\\De\"···                                    ↑ (pos 22)

#### Paired engine measurements

From phase5/final/summary.json. Times are milliseconds; allocation and working set are MiB. Warm values are two-sample means. See the limitations in section 5.

| Case | Cold before → after | Warm before → after | Allocated before → after | Working set before → after |
| --- | ---: | ---: | ---: | ---: |
| large-analyze | 524 → 552 | 442 → 434 | 192.9 → 192.9 | 232 → 232 |
| large-cook | 3522 → 3565 | 3741 → 3619 | 579.6 → 387.6 | 628 → 436 |
| private-large-analyze | 2019 → 1760 | 1924 → 1622 | 1781.3 → 1672.7 | 1572 → 1418 |
| private-small-analyze | 1703 → 1519 | 1417 → 1340 | 1078.7 → 970.5 | 666 → 704 |
| small-analyze | 106 → 106 | 27 → 84 | 8.7 → 8.7 | 46 → 46 |
| small-cook | 634 → 708 | 538 → 493 | 26 → 17.9 | 68 → 59 |

Private-large Cook is absent from that summary array; its documented paired measurements in PERFORMANCE_REPORT.md were warm 6,425 → 6,247 ms, allocated 2,398.0 → 1,974.1 MiB, working set 2,198 → 1,865 MiB. The memory-summary.json independently records its memory counters below.

| Case | Private MiB before → after | Managed MiB before → after | Post-GC LOH MiB before → after | Gen0/1/2 counts before → after |
| --- | ---: | ---: | ---: | --- |
| small-analyze | 20 → 20 | 9 → 9 | 6 → 8 | 1/1/1 → 1/1/1 |
| small-cook | 39 → 31 | 26 → 18 | 6 → 8 | 1/1/1 → 1/1/1 |
| large-analyze | 208 → 208 | 194 → 194 | 176 → 176 | 2/2/2 → 2/2/2 |
| large-cook | 607 → 412 | 579 → 387 | 176 → 176 | 2/2/2 → 2/2/2 |
| private-small-analyze | 649 → 686 | 572 → 648 | 466 → 564 | 26/21/3 → 20/16/3 |
| private-large-analyze | 1600 → 1475 | 1542 → 1390 | 1435 → 1246 | 23/19/4 → 15/12/2 |
| private-large-cook | 2223 → 1890 | 2157 → 1789 | 2051 → 1738 | 23/19/4 → 15/12/1 |

#### AMPR firmware matrix

Former sources: ampr-source-validation/release-import-matrix.json and mandatory-hook-matrix.json. These are static export/import observations against the stored reference databases.

| Firmware | Missing functions | Unknown functions | Mandatory hooks | Absent hook exports |
| --- | ---: | ---: | ---: | ---: |
| 4.00 | 0 | 0 | 48 | 0 |
| 4.03 | 0 | 0 | 48 | 0 |
| 4.50 | 0 | 0 | 48 | 0 |
| 4.51 | 0 | 0 | 48 | 0 |
| 5.02 | 0 | 0 | 48 | 0 |
| 5.10 | 0 | 0 | 48 | 0 |
| 5.50 | 0 | 0 | 48 | 0 |
| 6.00 | 0 | 0 | 48 | 0 |
| 6.02 | 0 | 0 | 48 | 0 |
| 6.50 | 0 | 0 | 48 | 0 |
| 7.01 | 0 | 0 | 48 | 0 |
| 7.20 | 0 | 0 | 48 | 0 |
| 7.40 | 0 | 0 | 48 | 0 |
| 7.60 | 0 | 0 | 48 | 0 |
| 7.61 | 0 | 0 | 48 | 0 |
| 8.00 | 0 | 0 | 48 | 0 |
| 8.20 | 0 | 0 | 48 | 0 |
| 8.40 | 0 | 0 | 48 | 0 |
| 8.60 | 0 | 0 | 48 | 0 |
| 9.00 | 0 | 0 | 48 | 0 |
| 9.05 | 0 | 0 | 48 | 0 |
| 9.20 | 0 | 0 | 48 | 0 |
| 9.40 | 0 | 0 | 48 | 0 |
| 9.60 | 0 | 0 | 48 | 0 |
| 10.01 | 0 | 0 | 48 | 0 |
| 10.20 | 0 | 0 | 48 | 0 |
| 10.40 | 0 | 0 | 48 | 0 |
| 10.60 | 0 | 0 | 48 | 0 |
| 11.00 | 0 | 0 | 48 | 0 |
| 11.20 | 0 | 0 | 48 | 0 |
| 11.40 | 0 | 0 | 48 | 0 |
| 11.60 | 0 | 0 | 48 | 0 |
| 12.00 | 0 | 0 | 48 | 0 |
| 12.02 | 0 | 0 | 48 | 0 |
| 12.20 | 0 | 0 | 48 | 0 |
| 12.40 | 0 | 0 | 48 | 0 |
| 12.60 | 0 | 0 | 48 | 0 |
| 12.70 | 0 | 0 | 48 | 0 |

#### Completed game-analysis import counts

##### game-analysis/HadesII-standardized-v2/summary.md

State: completed. Modules: 19. Finished UTC: 10/10/2026 11:50:31.

| Variant/module | Firmware reference | Unresolved function imports |
| --- | --- | ---: |
| default/eboot.bin | firmware_db 4.03 (exact for FW 4.03) | 7 |
| default/eboot.bin | firmware_db 10.01 (exact for FW 10.01) | 2 |
| default/libfmod.prx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| default/libfmod.prx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| default/libfmodstudio.prx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| default/libfmodstudio.prx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| default/sce_module/libSceFontGsm.prx | firmware_db 4.03 (exact for FW 4.03) | 2 |
| default/sce_module/libSceFontGsm.prx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| default/sce_module/libSceJobManager.prx | firmware_db 4.03 (exact for FW 4.03) | 18 |
| default/sce_module/libSceJobManager.prx | firmware_db 10.01 (exact for FW 10.01) | 1 |
| default/sce_module/libSceNpCppWebApi.prx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| default/sce_module/libSceNpCppWebApi.prx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| default/sce_module/libScePfs.prx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| default/sce_module/libScePfs.prx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| default/sce_module/libc.prx | firmware_db 4.03 (exact for FW 4.03) | 10 |
| default/sce_module/libc.prx | firmware_db 10.01 (exact for FW 10.01) | 5 |
| default/sce_sys/about/right.sprx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| default/sce_sys/about/right.sprx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| 4xx+/eboot.bin | firmware_db 4.03 (exact for FW 4.03) | 0 |
| 4xx+/eboot.bin | firmware_db 10.01 (exact for FW 10.01) | 0 |
| 4xx+/libfmod.prx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| 4xx+/libfmod.prx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| 4xx+/libfmodstudio.prx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| 4xx+/libfmodstudio.prx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| 4xx+/fakelib/libSceAgc.sprx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| 4xx+/fakelib/libSceAgc.sprx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| 4xx+/fakelib/libSceAgcDriver.sprx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| 4xx+/fakelib/libSceAgcDriver.sprx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| 4xx+/sce_module/libSceFontGsm.prx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| 4xx+/sce_module/libSceFontGsm.prx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| 4xx+/sce_module/libSceJobManager.prx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| 4xx+/sce_module/libSceJobManager.prx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| 4xx+/sce_module/libSceNpCppWebApi.prx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| 4xx+/sce_module/libSceNpCppWebApi.prx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| 4xx+/sce_module/libScePfs.prx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| 4xx+/sce_module/libScePfs.prx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| 4xx+/sce_module/libc.prx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| 4xx+/sce_module/libc.prx | firmware_db 10.01 (exact for FW 10.01) | 0 |

##### game-analysis/PPSA03671-library-fix/summary.md

State: completed. Modules: 17. Finished UTC: 10/10/2026 13:25:32.

| Variant/module | Firmware reference | Unresolved function imports |
| --- | --- | ---: |
| default/eboot.bin | firmware_db 4.00 (exact for FW 4.00) | 169 |
| default/eboot.bin | firmware_db 4.03 (exact for FW 4.03) | 169 |
| default/eboot.bin | firmware_db 9.60 (exact for FW 9.60) | 157 |
| default/eboot.bin | firmware_db 12.70 (exact for FW 12.70) | 147 |
| default/libRenoirCore.PS5.prx | firmware_db 4.00 (exact for FW 4.00) | 0 |
| default/libRenoirCore.PS5.prx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| default/libRenoirCore.PS5.prx | firmware_db 9.60 (exact for FW 9.60) | 0 |
| default/libRenoirCore.PS5.prx | firmware_db 12.70 (exact for FW 12.70) | 0 |
| default/libcohtml.Prospero.prx | firmware_db 4.00 (exact for FW 4.00) | 0 |
| default/libcohtml.Prospero.prx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| default/libcohtml.Prospero.prx | firmware_db 9.60 (exact for FW 9.60) | 0 |
| default/libcohtml.Prospero.prx | firmware_db 12.70 (exact for FW 12.70) | 0 |
| default/fakelib/libSceAmpr.sprx | firmware_db 4.00 (exact for FW 4.00) | 0 |
| default/fakelib/libSceAmpr.sprx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| default/fakelib/libSceAmpr.sprx | firmware_db 9.60 (exact for FW 9.60) | 0 |
| default/fakelib/libSceAmpr.sprx | firmware_db 12.70 (exact for FW 12.70) | 0 |
| default/fakelib/libScePlayGo.sprx | firmware_db 4.00 (exact for FW 4.00) | 0 |
| default/fakelib/libScePlayGo.sprx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| default/fakelib/libScePlayGo.sprx | firmware_db 9.60 (exact for FW 9.60) | 0 |
| default/fakelib/libScePlayGo.sprx | firmware_db 12.70 (exact for FW 12.70) | 0 |
| default/sce_module/libc.prx | firmware_db 4.00 (exact for FW 4.00) | 10 |
| default/sce_module/libc.prx | firmware_db 4.03 (exact for FW 4.03) | 10 |
| default/sce_module/libc.prx | firmware_db 9.60 (exact for FW 9.60) | 5 |
| default/sce_module/libc.prx | firmware_db 12.70 (exact for FW 12.70) | 0 |
| default/sce_sys/about/right.sprx | firmware_db 4.00 (exact for FW 4.00) | 0 |
| default/sce_sys/about/right.sprx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| default/sce_sys/about/right.sprx | firmware_db 9.60 (exact for FW 9.60) | 0 |
| default/sce_sys/about/right.sprx | firmware_db 12.70 (exact for FW 12.70) | 0 |
| 4xx+/eboot.bin | firmware_db 4.00 (exact for FW 4.00) | 153 |
| 4xx+/eboot.bin | firmware_db 4.03 (exact for FW 4.03) | 153 |
| 4xx+/eboot.bin | firmware_db 9.60 (exact for FW 9.60) | 148 |
| 4xx+/eboot.bin | firmware_db 12.70 (exact for FW 12.70) | 147 |
| 4xx+/libRenoirCore.PS5.prx | firmware_db 4.00 (exact for FW 4.00) | 0 |
| 4xx+/libRenoirCore.PS5.prx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| 4xx+/libRenoirCore.PS5.prx | firmware_db 9.60 (exact for FW 9.60) | 0 |
| 4xx+/libRenoirCore.PS5.prx | firmware_db 12.70 (exact for FW 12.70) | 0 |
| 4xx+/libcohtml.Prospero.prx | firmware_db 4.00 (exact for FW 4.00) | 0 |
| 4xx+/libcohtml.Prospero.prx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| 4xx+/libcohtml.Prospero.prx | firmware_db 9.60 (exact for FW 9.60) | 0 |
| 4xx+/libcohtml.Prospero.prx | firmware_db 12.70 (exact for FW 12.70) | 0 |
| 4xx+/fakelib/libSceAgc.sprx | firmware_db 4.00 (exact for FW 4.00) | 0 |
| 4xx+/fakelib/libSceAgc.sprx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| 4xx+/fakelib/libSceAgc.sprx | firmware_db 9.60 (exact for FW 9.60) | 0 |
| 4xx+/fakelib/libSceAgc.sprx | firmware_db 12.70 (exact for FW 12.70) | 0 |
| 4xx+/fakelib/libSceAgcDriver.sprx | firmware_db 4.00 (exact for FW 4.00) | 0 |
| 4xx+/fakelib/libSceAgcDriver.sprx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| 4xx+/fakelib/libSceAgcDriver.sprx | firmware_db 9.60 (exact for FW 9.60) | 0 |
| 4xx+/fakelib/libSceAgcDriver.sprx | firmware_db 12.70 (exact for FW 12.70) | 0 |
| 4xx+/fakelib/libSceAmpr.sprx | firmware_db 4.00 (exact for FW 4.00) | 0 |
| 4xx+/fakelib/libSceAmpr.sprx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| 4xx+/fakelib/libSceAmpr.sprx | firmware_db 9.60 (exact for FW 9.60) | 0 |
| 4xx+/fakelib/libSceAmpr.sprx | firmware_db 12.70 (exact for FW 12.70) | 0 |
| 4xx+/fakelib/libScePlayGo.sprx | firmware_db 4.00 (exact for FW 4.00) | 0 |
| 4xx+/fakelib/libScePlayGo.sprx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| 4xx+/fakelib/libScePlayGo.sprx | firmware_db 9.60 (exact for FW 9.60) | 0 |
| 4xx+/fakelib/libScePlayGo.sprx | firmware_db 12.70 (exact for FW 12.70) | 0 |
| 4xx+/fakelib/libScePsml.sprx | firmware_db 4.00 (exact for FW 4.00) | 0 |
| 4xx+/fakelib/libScePsml.sprx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| 4xx+/fakelib/libScePsml.sprx | firmware_db 9.60 (exact for FW 9.60) | 0 |
| 4xx+/fakelib/libScePsml.sprx | firmware_db 12.70 (exact for FW 12.70) | 0 |
| 4xx+/sce_module/libc.prx | firmware_db 4.00 (exact for FW 4.00) | 9 |
| 4xx+/sce_module/libc.prx | firmware_db 4.03 (exact for FW 4.03) | 9 |
| 4xx+/sce_module/libc.prx | firmware_db 9.60 (exact for FW 9.60) | 5 |
| 4xx+/sce_module/libc.prx | firmware_db 12.70 (exact for FW 12.70) | 0 |
| 4xx+/sce_sys/about/right.sprx | firmware_db 4.00 (exact for FW 4.00) | 0 |
| 4xx+/sce_sys/about/right.sprx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| 4xx+/sce_sys/about/right.sprx | firmware_db 9.60 (exact for FW 9.60) | 0 |
| 4xx+/sce_sys/about/right.sprx | firmware_db 12.70 (exact for FW 12.70) | 0 |

##### game-analysis/PPSA34008-standardized-v2/summary.md

State: completed. Modules: 25. Finished UTC: 10/10/2026 11:50:33.

| Variant/module | Firmware reference | Unresolved function imports |
| --- | --- | ---: |
| decrypted/eboot.bin | firmware_db 4.03 (exact for FW 4.03) | 19 |
| decrypted/eboot.bin | firmware_db 4.51 (exact for FW 4.51) | 19 |
| decrypted/eboot.bin | firmware_db 10.01 (exact for FW 10.01) | 4 |
| decrypted/eboot.bin | firmware_db 10.40 (exact for FW 10.40) | 4 |
| decrypted/eboot.bin | firmware_db 12.00 (exact for FW 12.00) | 0 |
| decrypted/sce_module/libSceFontGsm.prx | firmware_db 4.03 (exact for FW 4.03) | 2 |
| decrypted/sce_module/libSceFontGsm.prx | firmware_db 4.51 (exact for FW 4.51) | 2 |
| decrypted/sce_module/libSceFontGsm.prx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| decrypted/sce_module/libSceFontGsm.prx | firmware_db 10.40 (exact for FW 10.40) | 0 |
| decrypted/sce_module/libSceFontGsm.prx | firmware_db 12.00 (exact for FW 12.00) | 0 |
| decrypted/sce_module/libSceJobManager.prx | firmware_db 4.03 (exact for FW 4.03) | 18 |
| decrypted/sce_module/libSceJobManager.prx | firmware_db 4.51 (exact for FW 4.51) | 18 |
| decrypted/sce_module/libSceJobManager.prx | firmware_db 10.01 (exact for FW 10.01) | 1 |
| decrypted/sce_module/libSceJobManager.prx | firmware_db 10.40 (exact for FW 10.40) | 1 |
| decrypted/sce_module/libSceJobManager.prx | firmware_db 12.00 (exact for FW 12.00) | 0 |
| decrypted/sce_module/libSceNpCppWebApi.prx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| decrypted/sce_module/libSceNpCppWebApi.prx | firmware_db 4.51 (exact for FW 4.51) | 0 |
| decrypted/sce_module/libSceNpCppWebApi.prx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| decrypted/sce_module/libSceNpCppWebApi.prx | firmware_db 10.40 (exact for FW 10.40) | 0 |
| decrypted/sce_module/libSceNpCppWebApi.prx | firmware_db 12.00 (exact for FW 12.00) | 0 |
| decrypted/sce_module/libScePfs.prx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| decrypted/sce_module/libScePfs.prx | firmware_db 4.51 (exact for FW 4.51) | 0 |
| decrypted/sce_module/libScePfs.prx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| decrypted/sce_module/libScePfs.prx | firmware_db 10.40 (exact for FW 10.40) | 0 |
| decrypted/sce_module/libScePfs.prx | firmware_db 12.00 (exact for FW 12.00) | 0 |
| decrypted/sce_module/libc.prx | firmware_db 4.03 (exact for FW 4.03) | 10 |
| decrypted/sce_module/libc.prx | firmware_db 4.51 (exact for FW 4.51) | 10 |
| decrypted/sce_module/libc.prx | firmware_db 10.01 (exact for FW 10.01) | 5 |
| decrypted/sce_module/libc.prx | firmware_db 10.40 (exact for FW 10.40) | 5 |
| decrypted/sce_module/libc.prx | firmware_db 12.00 (exact for FW 12.00) | 0 |
| decrypted/sce_sys/about/right.sprx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| decrypted/sce_sys/about/right.sprx | firmware_db 4.51 (exact for FW 4.51) | 0 |
| decrypted/sce_sys/about/right.sprx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| decrypted/sce_sys/about/right.sprx | firmware_db 10.40 (exact for FW 10.40) | 0 |
| decrypted/sce_sys/about/right.sprx | firmware_db 12.00 (exact for FW 12.00) | 0 |
| default/eboot.bin | firmware_db 4.03 (exact for FW 4.03) | 19 |
| default/eboot.bin | firmware_db 4.51 (exact for FW 4.51) | 19 |
| default/eboot.bin | firmware_db 10.01 (exact for FW 10.01) | 4 |
| default/eboot.bin | firmware_db 10.40 (exact for FW 10.40) | 4 |
| default/eboot.bin | firmware_db 12.00 (exact for FW 12.00) | 0 |
| default/sce_module/libSceFontGsm.prx | firmware_db 4.03 (exact for FW 4.03) | 2 |
| default/sce_module/libSceFontGsm.prx | firmware_db 4.51 (exact for FW 4.51) | 2 |
| default/sce_module/libSceFontGsm.prx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| default/sce_module/libSceFontGsm.prx | firmware_db 10.40 (exact for FW 10.40) | 0 |
| default/sce_module/libSceFontGsm.prx | firmware_db 12.00 (exact for FW 12.00) | 0 |
| default/sce_module/libSceJobManager.prx | firmware_db 4.03 (exact for FW 4.03) | 18 |
| default/sce_module/libSceJobManager.prx | firmware_db 4.51 (exact for FW 4.51) | 18 |
| default/sce_module/libSceJobManager.prx | firmware_db 10.01 (exact for FW 10.01) | 1 |
| default/sce_module/libSceJobManager.prx | firmware_db 10.40 (exact for FW 10.40) | 1 |
| default/sce_module/libSceJobManager.prx | firmware_db 12.00 (exact for FW 12.00) | 0 |
| default/sce_module/libSceNpCppWebApi.prx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| default/sce_module/libSceNpCppWebApi.prx | firmware_db 4.51 (exact for FW 4.51) | 0 |
| default/sce_module/libSceNpCppWebApi.prx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| default/sce_module/libSceNpCppWebApi.prx | firmware_db 10.40 (exact for FW 10.40) | 0 |
| default/sce_module/libSceNpCppWebApi.prx | firmware_db 12.00 (exact for FW 12.00) | 0 |
| default/sce_module/libScePfs.prx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| default/sce_module/libScePfs.prx | firmware_db 4.51 (exact for FW 4.51) | 0 |
| default/sce_module/libScePfs.prx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| default/sce_module/libScePfs.prx | firmware_db 10.40 (exact for FW 10.40) | 0 |
| default/sce_module/libScePfs.prx | firmware_db 12.00 (exact for FW 12.00) | 0 |
| default/sce_module/libc.prx | firmware_db 4.03 (exact for FW 4.03) | 10 |
| default/sce_module/libc.prx | firmware_db 4.51 (exact for FW 4.51) | 10 |
| default/sce_module/libc.prx | firmware_db 10.01 (exact for FW 10.01) | 5 |
| default/sce_module/libc.prx | firmware_db 10.40 (exact for FW 10.40) | 5 |
| default/sce_module/libc.prx | firmware_db 12.00 (exact for FW 12.00) | 0 |
| default/sce_sys/about/right.sprx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| default/sce_sys/about/right.sprx | firmware_db 4.51 (exact for FW 4.51) | 0 |
| default/sce_sys/about/right.sprx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| default/sce_sys/about/right.sprx | firmware_db 10.40 (exact for FW 10.40) | 0 |
| default/sce_sys/about/right.sprx | firmware_db 12.00 (exact for FW 12.00) | 0 |
| 10xx+/eboot.bin | firmware_db 4.03 (exact for FW 4.03) | 5 |
| 10xx+/eboot.bin | firmware_db 4.51 (exact for FW 4.51) | 5 |
| 10xx+/eboot.bin | firmware_db 10.01 (exact for FW 10.01) | 0 |
| 10xx+/eboot.bin | firmware_db 10.40 (exact for FW 10.40) | 0 |
| 10xx+/eboot.bin | firmware_db 12.00 (exact for FW 12.00) | 0 |
| 10xx+/fakelib/libSceAgc.sprx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| 10xx+/fakelib/libSceAgc.sprx | firmware_db 4.51 (exact for FW 4.51) | 0 |
| 10xx+/fakelib/libSceAgc.sprx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| 10xx+/fakelib/libSceAgc.sprx | firmware_db 10.40 (exact for FW 10.40) | 0 |
| 10xx+/fakelib/libSceAgc.sprx | firmware_db 12.00 (exact for FW 12.00) | 0 |
| 10xx+/fakelib/libSceAgcDriver.sprx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| 10xx+/fakelib/libSceAgcDriver.sprx | firmware_db 4.51 (exact for FW 4.51) | 0 |
| 10xx+/fakelib/libSceAgcDriver.sprx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| 10xx+/fakelib/libSceAgcDriver.sprx | firmware_db 10.40 (exact for FW 10.40) | 0 |
| 10xx+/fakelib/libSceAgcDriver.sprx | firmware_db 12.00 (exact for FW 12.00) | 0 |
| 10xx+/fakelib/libSceAmpr.sprx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| 10xx+/fakelib/libSceAmpr.sprx | firmware_db 4.51 (exact for FW 4.51) | 0 |
| 10xx+/fakelib/libSceAmpr.sprx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| 10xx+/fakelib/libSceAmpr.sprx | firmware_db 10.40 (exact for FW 10.40) | 0 |
| 10xx+/fakelib/libSceAmpr.sprx | firmware_db 12.00 (exact for FW 12.00) | 0 |
| 10xx+/fakelib/libScePlayGo.sprx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| 10xx+/fakelib/libScePlayGo.sprx | firmware_db 4.51 (exact for FW 4.51) | 0 |
| 10xx+/fakelib/libScePlayGo.sprx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| 10xx+/fakelib/libScePlayGo.sprx | firmware_db 10.40 (exact for FW 10.40) | 0 |
| 10xx+/fakelib/libScePlayGo.sprx | firmware_db 12.00 (exact for FW 12.00) | 0 |
| 10xx+/fakelib/libScePsml.sprx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| 10xx+/fakelib/libScePsml.sprx | firmware_db 4.51 (exact for FW 4.51) | 0 |
| 10xx+/fakelib/libScePsml.sprx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| 10xx+/fakelib/libScePsml.sprx | firmware_db 10.40 (exact for FW 10.40) | 0 |
| 10xx+/fakelib/libScePsml.sprx | firmware_db 12.00 (exact for FW 12.00) | 0 |
| 10xx+/sce_module/libSceFontGsm.prx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| 10xx+/sce_module/libSceFontGsm.prx | firmware_db 4.51 (exact for FW 4.51) | 0 |
| 10xx+/sce_module/libSceFontGsm.prx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| 10xx+/sce_module/libSceFontGsm.prx | firmware_db 10.40 (exact for FW 10.40) | 0 |
| 10xx+/sce_module/libSceFontGsm.prx | firmware_db 12.00 (exact for FW 12.00) | 0 |
| 10xx+/sce_module/libSceJobManager.prx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| 10xx+/sce_module/libSceJobManager.prx | firmware_db 4.51 (exact for FW 4.51) | 0 |
| 10xx+/sce_module/libSceJobManager.prx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| 10xx+/sce_module/libSceJobManager.prx | firmware_db 10.40 (exact for FW 10.40) | 0 |
| 10xx+/sce_module/libSceJobManager.prx | firmware_db 12.00 (exact for FW 12.00) | 0 |
| 10xx+/sce_module/libSceNpCppWebApi.prx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| 10xx+/sce_module/libSceNpCppWebApi.prx | firmware_db 4.51 (exact for FW 4.51) | 0 |
| 10xx+/sce_module/libSceNpCppWebApi.prx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| 10xx+/sce_module/libSceNpCppWebApi.prx | firmware_db 10.40 (exact for FW 10.40) | 0 |
| 10xx+/sce_module/libSceNpCppWebApi.prx | firmware_db 12.00 (exact for FW 12.00) | 0 |
| 10xx+/sce_module/libScePfs.prx | firmware_db 4.03 (exact for FW 4.03) | 0 |
| 10xx+/sce_module/libScePfs.prx | firmware_db 4.51 (exact for FW 4.51) | 0 |
| 10xx+/sce_module/libScePfs.prx | firmware_db 10.01 (exact for FW 10.01) | 0 |
| 10xx+/sce_module/libScePfs.prx | firmware_db 10.40 (exact for FW 10.40) | 0 |
| 10xx+/sce_module/libScePfs.prx | firmware_db 12.00 (exact for FW 12.00) | 0 |
| 10xx+/sce_module/libc.prx | firmware_db 4.03 (exact for FW 4.03) | 8 |
| 10xx+/sce_module/libc.prx | firmware_db 4.51 (exact for FW 4.51) | 8 |
| 10xx+/sce_module/libc.prx | firmware_db 10.01 (exact for FW 10.01) | 4 |
| 10xx+/sce_module/libc.prx | firmware_db 10.40 (exact for FW 10.40) | 4 |
| 10xx+/sce_module/libc.prx | firmware_db 12.00 (exact for FW 12.00) | 0 |

#### Rogue two-launch event counts

These earlier captures precede the confirmed level-exit run-save result. Successful profile/UbisoftSave operations must not be equated with saved run progress.

| Capture | Reads | Read OK | Writes | Write OK | Icon | Profile create |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| rogue_auth_details_first_save.log | 4 | 4 | 9 | 9 | 9 | 1 |
| rogue_auth_details_relaunched.log | 4 | 4 | 4 | 4 | 4 | 0 |

### Evidence source identities

Former work-relative paths. Source SHA-256 values were computed before deletion. Former root documents listed above are now consolidated into the linked sections of ../README.md.

| Former source | Bytes | SHA-256 |
| --- | ---: | --- |
| ampr-ghidra/COMPARISON.md | 5460 | 074de9f6ccc8634a0489a0f935d0179ace2c0b077c7c32a405d3ade51fc7782f |
| ampr-source-validation/mandatory-hook-matrix.json | 3705 | 5da17cb8be448fff05fd901c4c9ff0b1d0b4000a08d6dd6dba0b26baf15eb1fc |
| ampr-source-validation/payload-verify.log | 23511 | c4f9c68aa603b1d48a17e2206b198e3403e3bb80bc46d5201d656302bdba36f5 |
| ampr-source-validation/rebuilt/import-matrix.json | 7163 | 68e68214401e3b5f82d45704ad75bd1d6948d70a5d98e7d11fbcfde7e8a1bb8d |
| ampr-source-validation/release-import-matrix.json | 7163 | 01de6bac4459af32c060b60b0c77c4c1cec4a15d168049fd9dd58b71c5d07760 |
| crow-diagnostics/completed-crash-findings.md | 3134 | 6a2463b17d6b86873c49e459fc275fbc6424b99e45304f260be7e2176959e933 |
| crow-diagnostics/rogue-auth-first-collected.log | 84 | 23b1062a1777cf7997465f854d4c2b2e9ea15adae358873d1d9d746a6f569161 |
| crow-diagnostics/rogue-auth-working-offline.log | 112 | c59a3c5f30782cde76ebb26bd020c2c549aadd9c985d606db1c0b689621a123f |
| crow-diagnostics/rogue-runtime-finding.md | 2722 | e6e35e2c6e4b58908533af988da8a983984b0ae611c70aa41c1ca50169559bfb |
| game-analysis/HadesII-standardized-v2/run.json | 9206 | a7f272487a5ae3d2a8ad19d71357cb81268bbda9a1fd4ed1159478e5004b066b |
| game-analysis/HadesII-standardized-v2/summary.md | 3430 | 0cba7bf9dd5cc15738b149db0828ad172ffe1880f7baec8949f54d6558043a1e |
| game-analysis/HadesII-standardized/run.json | 3833 | d59a89b1285ce5c701ea01f049c944b58fd3569fb8f69e482655e7a63e2e7896 |
| game-analysis/HadesII-standardized/summary.md | 3430 | 0cba7bf9dd5cc15738b149db0828ad172ffe1880f7baec8949f54d6558043a1e |
| game-analysis/PPSA03671-first-pass/run.json | 11535 | 563ac3104f115b1640403911f7f0d10d459cdb2b6ef7413dcf29c45100aac41e |
| game-analysis/PPSA03671-first-pass/summary.md | 5696 | ed8800532e4df6693372ffbef4c578604c66eceb5300b10709219e9b3cc24037 |
| game-analysis/PPSA03671-library-fix/run.json | 11534 | b3790767682a398997a478808d0d3c52fc4c55208058df184fec6b696d282500 |
| game-analysis/PPSA03671-library-fix/summary.md | 5696 | ed8800532e4df6693372ffbef4c578604c66eceb5300b10709219e9b3cc24037 |
| game-analysis/PPSA34008-standardized-v2/run.json | 13336 | 5161b95b1ce08a0322fc3e85e8f7693405b0113eb5d36817c3c47317cdc4056a |
| game-analysis/PPSA34008-standardized-v2/summary.md | 10689 | 73c6634d5419b630798faf001de579e7f3e24dca9aec6bc99ed8fc5f8d18c70e |
| game-analysis/PPSA34008-standardized/run.json | 1777 | 425dec90792e19841c5b31d45c94799da99f7c516f24af7bd23d57ca269ddc6d |
| import-library-audit/audit.txt | 1620 | f81a809b8d2b33f1a0d689310b2a273edaf2d7a53b869f53f2da326ef8b4065b |
| phase5/benchmarks/results/FirmwareMetadataBenchmarks-report-github.md | 916 | c61870b901547952318fabebc13abd20aea59a0106a088c6217a67d7f4443da6 |
| phase5/final/memory-summary.json | 1804 | da898563d14d15a8b61203a1839d4f429cff3a2c2dc1de33600111b09b9aa269 |
| phase5/final/summary.json | 2074 | 8e33c87f2dd6590a6e3c0b68f41db1992007ac5f1707dfe263558995b159387b |
| phase6/filter-after.json | 706 | 6a1998b00877eb2c7d2b9c272c558b09281a8f54ffd9729ee4a364868225cd77 |
| phase6/filter-before.json | 727 | 6f54dee29e1a43523bf2c7e606f502c017f69e0f6fbbfb2022483bf79ee94165 |
| phase7/20261010-031012/evaluation.json | 94754 | fa55ae379ddf3d9e04f0e46f4b5158716a7d248e50d156328fdf983e45062ebe |
| phase7/public/evaluation.json | 87885 | f6de4b82ee66e525f75fbdceafa16d1facd36ab78bdc15c2d74748eee83896f3 |
| phase8/20261010-032209/catalog-schema.json | 2356 | a0d9a395d3a257a3f09838aa41d6ba12f6942e02fa3d6c900cb3595fc22beec8 |
| phase8/20261010-032209/dotnet.json | 21504 | f29de70bb74590c762602e5926e908f7b7236809db271a416f7d0faeacb2d614 |
| rogue-auth-fallback/README.md | 6871 | 3cd918fc525806c2fa55d0a1aaaed563ecf8561ae43bf6a6d334b7611dd7ee4d |
| rogue-auth-trace-details/README.md | 3179 | 22634f7c269fdc484ce23243cd293aa4548666d609188948c4fe00e89d48dc4a |
| rogue-auth-trace-file/README.md | 4050 | e10dd43cafbef6133a1f3ed13c583a0f45e55d9aaabb85740feee01fe42ce3cd |
| rogue-auth-trace-v2/README.md | 3504 | c812432232b40ba5ea95ab4f9bb8bdef0e5df51ad11776a419ed435b7697c5c5 |
| rogue-auth-trace/README.md | 5140 | 90945509a4c2b8713da926c9c91cefab356a4c9fc701d73e1c72982f4e84da14 |
| rogue-offline-comparison/README.md | 3252 | f87fb67d40d1af5ac7f5516e95acbfd728d54cc22d8e38bdc358916e82c61645 |
| rogue-offline-popups/README.md | 5121 | d8bbb83b1ade33bcf4293ab7a633d0573afba412db6462880081fe840edde289 |
| rogue-offline-reachability/README.md | 4022 | 0b0e99ccbb028db959b7bafbee50c35e82563672949d68b6eb3738c2a841f1ef |
| rogue-save-adapter-comparison/README.md | 2715 | 00e7dcd47455e6cde23bab12b5b481082c05a475d10db9a2e332a078e7e7867f |
| rogue-save-comparison/README.md | 9901 | 400575ffbe4f327f8e46dbe0c41dffeb528988e50cfdeb226aad93c00e2be52d |
| rogue-save-trace-v2/evidence-latest/ampr_emu.log | 61224 | ba46233dc4e006d8f055f6edbc91bbadb2a9329f3cfb391ce4207437d51de092 |
| rogue-save-trace-v2/evidence-latest/findings.md | 1377 | e6630e027c7dcd71f67888c65c56fb5344e93a0251a8370e20584abb07736b8a |
| rogue-save-trace-v2/evidence-latest/rogue_auth_details.log | 5526 | 4edcadde46cd2a8652226fa622c7f24a98eb815d10050a81c9e0c5a2ed7a8c75 |
| rogue-save-trace-v2/evidence-two-launches/ampr_emu_relaunched.log | 374288 | 1b105608c4be9cbd25acd91c11cbba0a4481d6db4816821b6e5ee37a5b5473f1 |
| rogue-save-trace-v2/evidence-two-launches/ampr_emu._first_save.log | 857161 | a04a03b93a906c8b174e15b98a3e9a8e6402c28e7ab7eb7c4d3b9a808c406190 |
| rogue-save-trace-v2/evidence-two-launches/console.log | 132801 | 8b29c4fd06e8740847f22679f58ffc7fcdf1b41d248bf561960721a57c225e2d |
| rogue-save-trace-v2/evidence-two-launches/findings.md | 3266 | 189919461a42c291a77f0cc7b8e1a87b73830e9ddcc0eeaacbe2b1f072b006a7 |
| rogue-save-trace-v2/evidence-two-launches/rogue_auth_details_first_save.log | 2616 | 6101533b0c73f96614e466f5753e12fa4c0ccf3963b877481327b5aed32f6144 |
| rogue-save-trace-v2/evidence-two-launches/rogue_auth_details_relaunched.log | 1954 | 1e2d4a849d16d8dab306991b7828cc120170a57a2c323d00cf6dd4d836e4a717 |
| rogue-save-trace-v2/evidence-two-launches/summary.json | 1335 | 763dc9e7a1199c8fc9a44174e5cad624530ecfa9f64a217a4ffee6db17588e53 |
| rogue-save-trace-v2/README.md | 4069 | 29fa4b7e3805de2a2cb1e52b36a9b312a1a32b3f05e2f86c75be806625a71e21 |
| rogue-save-trace/evidence-20261010/ampr_emu.log | 1125115 | d5649d8fcd8a115aaebfbb8274578e477249d165616c18809518c77c53b517e7 |
| rogue-save-trace/evidence-20261010/findings.md | 3873 | 79c4bc61aac6a383ef2ca3e4ca1978ea348cbd394693b5ff777e2fb753314f6f |
| rogue-save-trace/evidence-20261010/rogue_auth_details.log | 2404 | c43a7f4601c823788e7f8c076683f1a8eac01fcceb5369494ea39b3e073a7c31 |
| rogue-save-trace/README.md | 4873 | 69dda2915077a729e6360694a0c2dafcb3c49b2b29a797ec4b6ed670fb18a5ae |
| rogue-sku-offline-fallback/evidence/ampr_emu_offline.log | 386494 | f290cc6dff9e9e5b942bcd2f7b5f1792d14bb575c701a7d392bba4558fbed7c7 |
| rogue-sku-offline-fallback/evidence/ampr_emu_online.log | 111208 | ab5ef5f4488d1348cf107ea55436fef636a412913edc6dfbb0f7c19da25c125e |
| rogue-sku-offline-fallback/evidence/rogue_auth_details_offline.log | 930 | 996b7349eed54c09747ecf052b5729845aebb16f4b7bb0731bd556ce5425c2e9 |
| rogue-sku-offline-fallback/evidence/rogue_auth_details_online.log | 1082 | 75c2a5ef369f255eb6e2bcf66aeedc03c7cf171be1dbf690ceb7b52d0ab97173 |
| rogue-sku-offline-fallback/README.md | 4312 | cc432dabedc9fa41fa02bf8f7b361f155cd036e9c15274221eedd03d4a1ded90 |

### Cleanup inventory

Original work files (excluding this report and its temporary consolidation helper): **6406**. Logical size: **26483545836 bytes (24.665 GiB)**. This is file length, not guaranteed physical disk-space recovery; hard links/compression can differ.

| Original top-level entry | Files | Logical MiB |
| --- | ---: | ---: |
| ampr-ghidra | 31 | 1.262 |
| ampr-source-0.3.6.6.zip | 1 | 0.327 |
| ampr-source-validation | 85 | 2.077 |
| app-check | 124 | 564.759 |
| audit | 5 | 0.36 |
| cli-artifacts | 50 | 1.341 |
| codebase-memory-install-0.11.0.ps1 | 1 | 0.018 |
| CONTROL_elf | 12 | 131.475 |
| crow-diagnostics | 130 | 282.719 |
| crow-fixed-library | 103 | 599.127 |
| game-analysis | 166 | 2264.729 |
| GT7_fakelib_elf | 5 | 2.243 |
| GT7_root_elf | 3 | 136.121 |
| GT7_sce_module_elf | 3 | 12.03 |
| GT7_sce_sys_elf | 1 | 0.064 |
| import-library-audit | 70 | 2.448 |
| NOT_BACKPORT_elf | 9 | 148.558 |
| p4_gt7 | 14 | 149.775 |
| package-smoke.ps1 | 1 | 0.004 |
| packages | 3527 | 2799.193 |
| phase3 | 300 | 179.324 |
| phase4 | 37 | 0.769 |
| phase5 | 738 | 7658.209 |
| phase6 | 159 | 1126.616 |
| phase7 | 4 | 0.381 |
| phase8 | 48 | 0.307 |
| popup-build-diagnostic.log | 1 | 1.495 |
| ppsa34008 | 64 | 638.139 |
| rogue-auth-fallback | 10 | 544.557 |
| rogue-auth-trace | 12 | 695.711 |
| rogue-auth-trace-details | 12 | 695.711 |
| rogue-auth-trace-file | 12 | 695.711 |
| rogue-auth-trace-v2 | 12 | 695.71 |
| rogue-offline-comparison | 9 | 549.435 |
| rogue-offline-popups | 44 | 696.546 |
| rogue-offline-reachability | 3 | 72.11 |
| rogue-save-adapter-comparison | 5 | 0.091 |
| rogue-save-comparison | 5 | 72.18 |
| rogue-save-trace | 16 | 696.874 |
| rogue-save-trace-v2 | 21 | 697.09 |
| rogue-sku-general | 2 | 302.538 |
| rogue-sku-offline-fallback | 16 | 696.187 |
| test-artifacts | 401 | 1140.399 |
| tools | 4 | 0.023 |
| ubisoft-popup-verification | 85 | 297.119 |
| verification | 45 | 4.812 |

Cleanup status: completed. All other work entries were deleted, including the temporary consolidation helper. A final directory check confirmed exactly one file remains: CONFIRMED_FINDINGS.md.
