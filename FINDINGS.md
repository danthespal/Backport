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

## 5. Reference updates and audits

- **11 October** (AnyPS5 `9a1f6f4` → `a11c8f7`; other repos current) produced priorities 1–8. Outcomes are recorded in README under "Reference improvement priority 1–3" and "Reference priority 4–8". Done: dry-run option coverage (1); optional provider audit `report --provider-audit` (2; strict `(library, NID)` cook matching deferred, since every one of 1,142 mismatches in the corpus is `libSceLibcInternal → libc`); SCE-relative dynamic tables (3); opt-in PlayGo 0.6 variants (4); deployment notes in reports, pinned to ShadowMountPlus 1.7beta5-fix1 `98c52489` (7); and the `crash-log` command (8). Deferred: PlayGo config generation (5; pgo_stub enumeration is already finite at 1,000 IDs by default; only two `playgo-chunk.dat` samples, both 35 chunks at `0x0A`). Rejected: 13 candidate additions (6).
- **12 October** (AnyPS5 `a11c8f7` → `2a5eca11c`, 949 commits; the other 15 repos unchanged). **Applied:** 28 hash-verified NID names appended to `data/aerolib.csv` (README "Verified name additions — 12 October 2026"), and 5 placeholder labels in `data/fixes.json` renamed. The `data candidates` / `data sony-builds` commands now write to the repository `data/` instead of the bundled copy. **Rejected:** regenerating `candidates.json` (17 of 18 new entries exist on every firmware ≥ 2.00, so they would never fire; the 18th is the already-covered VRR privilege call). The `sceAmprAmmSetPageTablePoolOccupancyNotificationThreshold` export is already in our fakelib, which covers all 118 FW 12.70 Ampr exports. The relinker's `R_X86_64_NONE` and section-symbol `RELATIVE` cases are already skipped by `SceElf.Imports()`. `.suprx`/`.debug_prx` suffixes do not occur in any sample. Sysmodule IDs and `kern.sdk_version` have no rule surface in Studio. ShadowMountPlus's "FAKE title ID" commit is on `main`, not the tracked `1.7`.
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
| Regenerate `candidates.json` from each AnyPS5 update | Rejected twice (11 and 12 October); review per entry |
| `work/CONFIRMED_FINDINGS.md` §7: fixture roots are deleted | Outdated: the `*_elf` fixtures were recreated and are kept |

## 7. Open items

1. Run a physical FW 4.xx console test of the patched set-4 AGC, and any console test of the patched set-4.03 AGC.
2. Decide on the generic SyncOnAddress Wait adapter contract (§1.6).
3. PPSA38510: run the UD2 load diagnostic, or get TTY output with the `Il2CppUserAssemblies.prx` load return code.
4. Indiana Jones: report the audio A/B result; identify the Force Pro failure.
5. Console-test Psml `KPN9_v070.psp` with a PSSR game on a Pro.
6. Optional: confirm the 13.60 SaveData result sizes from decrypted 13.60 ShellCore/libSceSaveData.native.

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
