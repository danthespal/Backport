# Reference projects: overview

Survey of `.original/reference/` (5 projects, about 4,200 files and 115 MB). The goal is to understand what each project does and how they connect, before we reverse engineer and rework the backport tooling.

| Folder | Upstream (git remote) | Language | Size | Role |
|---|---|---|---|---|
| `sdk/` | `ps5-payload-dev/sdk` (John Törnblom) | C, LLVM toolchain | ~2,000 files | Toolchain for building **payloads** (homebrew ELFs) that run on a jailbroken PS5 |
| `PS5-SELF-Decrypter/` | `idlesauce/PS5-SELF-Decrypter` (originally SpecterDev) | C payload | 13 files | Runs **on the console** and uses a kernel R/W exploit to decrypt real SELF/SPRX files to plain ELF |
| `PS5-3.20_Libs/` | `DNNDHH/PS5-3.20_Libs` | Generated C | 275 `.c` files, ~318k lines | **Import stubs** for every 3.20-firmware system library, generated with the sdk's `genstub.py` |
| `PS5-BACKPORK-KITCHEN/` | `rajeshca911/PS5-BACKPORK-KITCHEN` (discontinued) | VB.NET WinForms, .NET 8 | ~230 files | Windows GUI that **backports** a decrypted PS5 game to a lower firmware: SDK-version patch, fake-sign, add fakelibs |
| `AnyPS5/` | `boykopovar/AnyPS5` (2,477 commits, active) | C++20, CMake | ~1,600 files (submodules not checked out) | Converts a PS5 game ELF into a **native Linux/Windows executable**, with reimplemented system libraries and a GPU shader recompiler |
| `backport-helper/` | `drakmor/backport-helper` | C++, MSBuild (official Prospero SDK) | 19 files | Template **replacement `libSceRtc.prx`** that adds missing exports and installs runtime hooks, built in one variant per SDK (4–13) |
| `pgo_stub/` | `drakmor/pgo_stub` (v0.5) | C++, MSBuild (official Prospero SDK) | 11 files | **Replacement `libScePlayGo`** that reports every chunk as installed |
| `ampr_emu/` | `drakmor/ampr_emu` | C++, Makefile (payload-sdk) and MSBuild | 124 files | **Software `libSceAmpr`** (AMPR/APR/AMM command buffers on top of AIO), plus an LZ4 asset-pack system |
| `make_fself/` | `drakmor/make_fself` | Python | 1 script | `make_fself_batch.py`: sdk `make_fself.py` extended with batch signing, unpacking and repair |
| `ppr-patch/` | `drakmor/ppr-patch` | C payload and AArch64 asm | 25 files (**incomplete**, see below) | Patches the **A53 storage-controller firmware** so plaintext, unauthenticated PFS images can be mounted |
| `sce_symbols/` | `drakmor/sce_symbols` (fork) | Data | 18 files, 62 MB | NID-to-name database (`aerolib.csv`) for PS3/PS4/PS5/PSP/Vita |
| `BackPork/` | `BestPig/BackPork` | C payload | 5 files + 12 patches | Original fakelib loader (unionfs over sandbox `common/lib`) plus BPS patches for 10.01 libraries → 6xx/7xx; see [Part 6](#part-6-firmware-export-lists-step-2-research-results) |
| `ShadowMountPlus/` | `drakmor/ShadowMountPlus` | C payload (payload-sdk) | ~150 files | Auto-mounts game dumps and **overlays `fakelib/` into the game sandbox**; replaces `ps5-backpork.elf`. See [Part 4](#part-4-shadowmountplus-the-runtime-that-loads-fakelibs). |
| `sce_symbols_new/` | `zecoxao/sce_symbols` (upstream) | Data | 18 files, 62 MB | Same database, with 4,583 more PS5 names; a strict superset of `sce_symbols/` |

> Note: `.original/` also holds `GT7/` (`USA`/`EUR` = GT7 backported to FW 4, `NOT_BACKPORT`/`decrypted` = before) and `CONTROLResonant/backport` (a second game, FW 4.03; [Part 7](#part-7-control-resonant-second-backport-sample)). They are compared in [Part 3](#part-3-gt7-171-before-and-after-the-backport).
> The last seven rows were added later. They are covered in [Part 2](#part-2-drakmor-backport-toolkit).

---

> **2026-10-10: one engine.** The Python engine `ps5bp` (and its `tests/` and `tools/`) was retired; PS5 Backport Studio's C# engine (`Ps5Backport.Core`, command line `ps5backport`) is the only one. `python -m ps5bp X` is now `ps5backport X` (same commands); `tools/import_firmware_dumps.py`, `gen_sony_builds.py` and `gen_candidates.py` are `ps5backport fwdb import-firmwares`, `data sony-builds` and `data candidates`. Its parity results live on as golden files in `src/Ps5Backport.Tests/golden/` (re-record with `PS5BP_RECORD=1` after an intended change). Older entries below mention `ps5bp` as it was then. Python is still fine for throwaway scripts outside the software.

## Relationship map

```mermaid
flowchart TD
    subgraph console["On the PS5 (jailbroken)"]
        dec["PS5-SELF-Decrypter<br/>(payload ELF)"]
        bp_elf["ps5-backpork.elf<br/>(BestPig/BackPork loader)"]
    end

    sdk["sdk<br/>ps5-payload-sdk<br/>prospero-clang, crt, libc, sce_stubs"]
    libs320["PS5-3.20_Libs<br/>sprx_dlopen/dlsym stubs per lib"]
    kitchen["PS5-BACKPORK-KITCHEN<br/>(Windows GUI)"]
    anyps5["AnyPS5<br/>(relinker + HLE libs + shader recompiler)"]

    sdk -- "builds (Makefile includes prospero.mk)" --> dec
    sdk -- "genstub.py + aerolib.csv NID db" --> libs320
    libs320 -. "drop-in sce_stubs for 3.20 FW" .-> sdk
    dec -- "dumps plaintext ELF/SPRX to USB<br/>(game eboot.bin, /system/common/lib...)" --> kitchen
    dec -- "decrypted game ELF + sce_module/" --> anyps5
    sdk -- "make_fself.py logic ported to VB (signelf/)" --> kitchen
    kitchen -- "patched game folder + fakelib/" --> bp_elf
```

Two separate **output paths** start from the same decrypted game files:

1. **Backport path (stays on the PS5):** decrypt, then rewrite the SDK version in the ELF, fake-sign it back to SELF, and ship the newer system libraries (fakelibs) alongside. The game then runs on older firmware through `ps5-backpork.elf`.
2. **Port path (leaves the PS5):** decrypt, then relink the ELF for the host OS and replace every system library (`libkernel`, `libSceAgc`, ...) with a native implementation. The game runs as a normal PC process.

### What the projects share

- **NIDs.** PS5 symbols are imported by an 11-character NID, not by name: `SHA1(name + 518D64A635DED8C1E6B039B1C3E55230)`, take the first 8 bytes reversed, then encode them with a custom base64 alphabet (`+-`). The same algorithm appears in:
  - `sdk/host/bin/prospero-nid.c` and `sdk/crt/nid.c`
  - `AnyPS5/core/libs/nid/src/NidCompute.cpp` and `AnyPS5/tools/nid_names.py`
  - the generated stubs in `PS5-3.20_Libs` (for example `sprx_dlsym(h, "kW3GLb7QfPg", &__ptr_sceAgcInit)`)
- **Name database.** `zecoxao/sce_symbols/aerolib.csv` (NID to name) is used by `sdk/sce_stubs/Makefile` and by `AnyPS5/tools/nid_names.py`.
- **SELF format.** Magic `0x1D3D154F` (Orbis/PS4) and `0xEEF51454` (Prospero/PS5), a 0x20-byte header, and 0x20-byte segment entries with flag bits (encrypted, signed, compressed, has blocks/digests/blockinfo). It is defined in:
  - `PS5-SELF-Decrypter/include/self.h` (real decryption on the console)
  - `PS5-BACKPORK-KITCHEN/.../Services/SelfUtilCode/SelfFile.vb` (SelfUtil port: unwraps an already-decrypted or fake SELF into an ELF)
  - `PS5-BACKPORK-KITCHEN/.../signelf/*.vb` (port of `sdk/samples/install_app/make_fself.py`: wraps an ELF into a fake-signed SELF)
- **SCE program headers.** `PT_SCE_PROCPARAM = 0x61000001` (magic `ORBI`, `0x4942524F`) and `PT_SCE_MODULE_PARAM = 0x61000002` (magic `0x3C13F4BF`). The PS4 SDK version sits at param+0x10 and the PS5 SDK version at param+0x14. Backpork patches these fields, and AnyPS5's `ElfReader` parses the same headers.
- **Library set.** The libraries AnyPS5 reimplements (`core/libs/prx/*`, 115 of them) are a subset of the libraries stubbed in `PS5-3.20_Libs` (275 of them). The 3.20 list is a useful checklist of the full PS5 system-library surface.

---

## 1. `sdk/`: ps5-payload-sdk

**What it is:** the de-facto open SDK for PS5 homebrew payloads. The resulting ELFs are sent to an ELF loader (elfldr, websrv, BD-J, the Lua loader) on an exploited console.

| Path | Contents |
|---|---|
| `host/bin/prospero-*` | Wrappers around clang/lld/cmake/meson/pkg-config targeting `x86_64-sie-ps5`; `prospero-deploy` sends an ELF to `PS5_HOST:PS5_PORT` (9021) |
| `host/toolchain/prospero.{mk,cmake,ini,sh}` | Build-system integration; projects `include $(PS5_PAYLOAD_SDK)/toolchain/prospero.mk` |
| `host/win/` | Windows `.cmd` wrappers |
| `crt/` | Payload runtime: `_start.S`, the in-payload dynamic linker (`rtld*.c`, which loads `.sprx` via `sceKernelLoadStartModule` and resolves symbols by NID), kernel R/W helpers (`kernel.c`, `kernel_iommu.c`, `kernel_procio.c`), `klog`, `mdbg`, `patch.c` |
| `include/ps5/` | Public API: `kernel.h` (kernel R/W, FW version, auth IDs), `klog.h`, `mdbg.h`, `nid.h`, `payload.h`, `iommu.h` |
| `include/freebsd`, `libc/`, `libufs/` | FreeBSD headers, extra libc functions missing from the console libc, and a UFS library |
| `sce_stubs/` | `genstub.py` reads a decrypted `.sprx` and emits a `.c` stub; 30 stub libs are prebuilt (libkernel, libSceNet, libScePad, ...) |
| `samples/` | 23 examples. The most relevant: `install_app/make_fself.py` (fake-sign ELF to SELF), `hello_sprx`, `hello_dlfcn`, `relocatable_linking` |

**Why it matters for us:** this is the toolchain that builds the decrypter and any new on-console tooling, and `make_fself.py` is the reference for fake signing.

## 2. `PS5-SELF-Decrypter/`

**What it is:** a payload (built with the sdk) that decrypts signed executables on the console and dumps plaintext ELFs to `/mnt/usb0/PS5/` (or `/data/dump`).

How it works (`source/`, about 1,700 lines):
- `main.c`: picks a `tailored_offsets` set from `kernel_get_fw_version()`. FW 3.00 through 10.xx are covered (3.xx, 4.xx, 5.0x, 5.5x, 6.xx, 7.0x, 7.2–7.61, 8.xx, 9.xx, 10.xx). It then walks a dump queue, and for each file:
  1. verifies the SELF header with the AuthMgr,
  2. decrypts each segment's block-info,
  3. decrypts block by block,
  4. rebuilds the ELF.
  It resumes after a kernel panic.
- `sbl.c`: talks to the Secure Boot Loader (SAMU) mailbox using kernel R/W. Translates VA to PA through a page-table walk (`pmap_kextract`) and sends `sceSblServiceRequest` messages.
- `authmgr.c`: AuthMgr service calls: `VerifyHeader`, `LoadSelfSegment`, `LoadSelfBlock`, `SmFinalize`.
- `include/self.h`, `elf.h`, `sbl.h`, `authmgr.h`: format and message structs.

Build variants (Makefile `make dist`): default, `-sock-log` (logs to `PC_IP:PC_PORT`), `-system-common-lib` (`/system/common/lib`), `-full-system` (`/system` and `/system_ex`), `-shellcore`, and `-game` (the running title).

The README documents how to find the 11 kernel `.data` offsets for a new firmware from nearby strings (`"sdt"`, `"SblDrvSendSx"`, `"req mtx"`, `"invlgn"`, `"pmap"`, and so on).

**Why it matters for us:** this is the first step for both paths. It produces the plaintext game ELF and the plaintext system SPRX files (fakelib sources, and the reference for library behaviour).

## 3. `PS5-3.20_Libs/`

**What it is:** one generated `.c` file per system library on firmware 3.20, from `gaikai-player` through `libSceXXX` to `libkernel*`. A header says each file was generated by `genstub.py`, which is the sdk tool.

Each file:
- declares `__ptr_<func>` and an asm trampoline `<func>: jmp [rip + __ptr_<func>]` for every export,
- has a `constructor(104)` that runs `sprx_dlopen("<lib>")` and then `sprx_dlsym(handle, "<NID>", &__ptr_<func>)` for each export,
- has a `destructor` that runs `sprx_dlclose`.

**Why it matters for us:** it gives the complete export list (name and NID) of every 3.20 library. That is a ready-made **NID to name table**, which is useful to diff library exports between firmwares (what a fakelib must provide) and to label imports in Ghidra.

## 4. `PS5-BACKPORK-KITCHEN/`

**What it is:** a WinForms GUI (`net8.0-windows`; FluentFTP, Newtonsoft.Json, ReaLTaiizor UI, SharpCompress, SQLite) that automates BestPig's **BackPork** method plus idlesauce's downgrade script. The README marks it **discontinued**.

### Core backport flow ("Start Cooking", `Form1.vb` `BtnStart_Click`)
1. Back up the folder, then remove any old `fakelib/` from the game folder.
2. `PatchingCoordinator` walks every `eboot.bin`, `*.prx` and `*.sprx` and calls `PatchElfSmart` (`core/ElfReader.vb`):
   - If the file is already a plain ELF, patch it in place.
   - Otherwise unwrap the SELF to a temp ELF (`selfutilmodule.unpackfile`, an internal SelfUtil port), patch it, **fake-sign** it (`SignSingleElf`, from `signelf/`), and copy it back.
   - Optional "6xx" fix: in `libc.prx`, replace the string `4h6F1LLbTiw#A#B` with `IWIBBdTHit4#A#B` (an import NID swap).
3. **The patch itself** (`core/ElfPatcher.vb`): find the `PT_SCE_PROCPARAM` or `PT_SCE_MODULE_PARAM` segment, check its magic, and overwrite the PS5 SDK (+0x14) and PS4 SDK (+0x10) with the target pair from `core/VersionProfiles.vb`. Files already at or below the target are skipped.

   | FW | PS5 SDK | PS4 SDK |
   |---|---|---|
   | 1 | `0x01000050` | `0x07590001` |
   | 2 | `0x02000009` | `0x08050001` |
   | 3 | `0x03000027` | `0x08540001` |
   | 4 | `0x04000031` | `0x09040001` |
   | 5 | `0x05000033` | `0x09590001` |
   | 6 | `0x06000038` | `0x10090001` |
   | 7 | `0x07000038` | `0x10590001` |
   | 8 | `0x08000041` | `0x11090001` |
   | 9 | `0x09000040` | `0x11590001` |
   | 10 | `0x10000040` | `0x12090001` |
4. Copy `<exeDir>/<FW>/fakelib/*` into `<game>/fakelib/`. Fakelibs are newer-firmware SPRX files (for 7.xx: `libSceAgc`, `libSceAgcDriver`, `libSceNpAuth`, `libSceNpAuthAuthorizedAppDialog`, `libSceSaveData.native`, plus `ps5-backpork.elf`). Their download list is `fakelibs.json`; the URLs are base64-encoded.
5. On the console, run `ps5-backpork.elf`, which sideloads `fakelib/` in place of the system libraries.

`PatchPipeLineRules-Important.txt` defines the required order: detect type, decrypt, validate, check SDK, patch header, optional libc patch, sign, then atomically replace the original.

### Other modules (mostly extra tooling)
- `core/PKG/` builds fake PKGs (`FPKGBuilder`, `SFOBuilder`, `PKGReader`/`Writer`).
- `core/UFS2/` and `Services/UFS2*` read and build UFS2 images (shells out to UFS2Tool).
- `Services/Firmware*` download PUPs and unpack them with `tools/pup_unpacker.exe` and `ps5_pup_decrypt.elf`.
- `Services/Ftp*`, `PayloadSender*`, `RemotePkgInstall*` talk to the console over the network.
- `Services/SigningService.vb` supports three signing modes: FreeFakeSign, NPDRM, and CustomKeys.
- `Services/GameSearch/*` contains torrent and DDL search providers. These are not relevant to us.
- `Architecture/` is a partial clean-architecture refactor (coordinators, `Result` type, adapters).

**Why it matters for us:** it is the most direct description of the backport recipe (SDK fields, fakelibs, fake sign). The VB code is readable but tightly coupled to `Form1`.

> Deep dive into the actual backport pipeline, its limits and bugs: [Part 9](#part-9-how-backpork-kitchens-general-backport-actually-works-deep-dive).

## 5. `AnyPS5/`

**What it is:** a static **relinker**: a PS5 game ELF becomes a native Linux ELF or Windows PE, with no emulator. The x86-64 game code runs directly, and system calls go to native reimplementations of the PS5 libraries. The project is GPL-2.0. It is verified with *Dreaming Sarah* (PPSA02929) at 60 fps.

Pipeline (`core/relinker/main.cpp`):
1. `--to-intel` (optional, `codegen/x86/*`): rewrite instructions that exist only on AMD Zen 2 (SSE4a, CLZERO, SHA, `VRCPPS`/`VRSQRTPS` precision) in place, or through trampolines.
2. `RelinkerPipeline`: read imports by NID, scan for raw `syscall` instructions, optionally filter unused NIDs (`unused-filter=0/1/2`), and build a SysV `.dynamic` section.
3. `GuestModuleBuilder`: convert the bundled `sce_module/*.prx` files the same way.
4. `LinuxElfPatcher` or `WindowsPePatcher`: emit the final executable (on Windows, with `sce_sys/icon0.png` as the icon).

Runtime layout: `app.elf|app.exe`, then `libs/*.prx` (the native libraries), then `app0/` (game data plus converted `sce_module/`).

Key directories:
- `core/libs/prx/<lib>/`: 115 HLE libraries. Exports use the `APS5_VABI` calling convention, or `APS5_EXPORT("<nid>", fn)` when only the NID is known. The large ones are `libkernel` (memory, pthreads, files, equeue, sockets, modules), `libc`, `libSceAgc`/`libSceAgcDriver` (PM4 command buffers to Vulkan), `libSceVideoOut`, `libSceAudioOut`, `libScePad`, and `libSceSaveData`.
- `core/libs/nid/`: `nid_patcher`, which renames compiled exports to their NIDs after the build (ELF and PE).
- `core/shader/recompiler/`: RDNA2 GCN ISA to SPIR-V, in the stages `RdnaDecoder`, `ControlFlow`, `Translation` (IR), `Optimization` (SSA, SRT walker), and `SpirvBackend`. Results are cached on disk.
- `core/Decoder/{Png,Jpeg}`: image decoders used by the `libScePngDec` and `libSceJpegDec` implementations.
- `tools/`: `nid_names.py` (checks names and NIDs against aerolib), `progress.py` (coverage badges), `hw-oracle/` (runs RDNA instructions on real hardware to check the recompiler), and `rdna_isa.txt`.
- `docs/dev/TechnicalDebt.md` lists every silent stub. These are worth reading because they show which library behaviours a game can survive without.
- `3rdparty/` submodules (SDL2, Vulkan, glslang, SPIRV, VMA, ffmpeg, freetype, LibAtrac9, stb, libjpeg-turbo) are **empty**. Building needs `git submodule update --init --recursive`.

**Why it matters for us:** it is the best documentation available of how PS5 ELFs are laid out (dynamic tags, NID imports, module params) and of what each system function does. Its `ElfReader`, NID tooling and per-library `Export.cpp` files are a strong reference for any backporting work, especially for writing **shim/fake libraries** that cover functions missing on older firmware.

---

## Part 2: drakmor backport toolkit

Six of the seven new folders come from one author (`drakmor`). Together they form a newer and more principled backport approach than Backpork Kitchen. Backpork ships whole newer-firmware SPRX files. This toolkit **writes small replacement libraries** that provide only what the game needs, and **patches the console** so it accepts unsigned, plaintext game data.

```mermaid
flowchart LR
    syms["sce_symbols(_new)<br/>aerolib.csv"] -- "NID names" --> tools
    subgraph tools["Shared build tooling"]
        nidren["prx_rename_nids_auto.py<br/>(export by raw NID)"]
        stubs["make_*_stub.py<br/>(weak import stubs)"]
        fself["make_fself.py<br/>(fake-sign PRX to SPRX)"]
    end
    tools --> rtc["backport-helper<br/>libSceRtc.prx + hooks"]
    tools --> pgo["pgo_stub<br/>libScePlayGo.sprx"]
    tools --> ampr["ampr_emu<br/>libSceAmpr.prx + LZ4 packs"]
    ppr["ppr-patch<br/>A53 plaintext-PFS reads"] -. "lets the console mount<br/>unsigned game images" .-> game["Backported game"]
    rtc --> game
    pgo --> game
    ampr --> game
```

### `backport-helper/`: replacement-library template (`libSceRtc`)
- It is a complete `libSceRtc` reimplementation (`rtc.cpp`) that **also** exports functions an older firmware lacks. `backports.cpp` gates them with `#if BACKPORT_SDK <= N`; the shipped example is `sceKernelGetOperationMode` for SDK ≤ 10.
- `libSceRtc` is only the host. Almost every game loads it, which makes it a convenient carrier for exports and hooks that belong to other libraries.
- **Hook engine** (`hooks.cpp`, using the HDE64 disassembler): it detours function prologues, builds trampolines that relocate RIP-relative instructions, and installs from `module_start` with rollback. It has two kinds of hook:
  - `g_backportHooks[]` for imports that are already bound, for example a `sceKernelGettimezone` fallback to UTC.
  - `g_lateDlsymHooks[]` for modules loaded later. These are caught by hooking `sceKernelLoadStartModule` and resolved with `sceKernelDlsym`.
- **Tools:**
  - `prx_rename_nids_auto.py` rewrites exported NIDs in `.dynstr` and rebuilds the SysV `.hash`. This lets you export a function whose name is unknown, using a raw-NID map in `tools/nidmaps/`.
  - `make_kernel_extra_stub.py` and `make_libc_internal_stub.py` emit weak `ET_SCE_STUBLIB` import archives for symbols the official SDK stubs don't include.
- **Build:** Visual Studio with the **official Prospero SDK platform** (`<_rtc.h>`, `save_data/save_data_api.h`). It cannot be built with the open payload-sdk as-is. `Release` produces `out/sdk4…sdk13/libSceRtc.prx`.

### `pgo_stub/`: `libScePlayGo` replacement
- It implements 24 `scePlayGo*` exports with optimistic answers: initialized, every chunk at locus "local fast", install complete, every language available.
- Optional `/app0/playgo_stub.dat` sets the chunk count or list, the scenario count, and the language mask (names, IDs, or a `0x` hex mask). The `ReleaseLog` build writes `/app0/playlgo.log`. Note the "lgo" typo in that filename.
- A post-build step runs `tools/make_fself.py` to produce a fake-signed `.sprx`. The sources use only libc headers, but the project is still MSBuild/Prospero only.

### `ampr_emu/`: software `libSceAmpr`
- AMPR is the PS5's asynchronous I/O and memory-mapping command-buffer API: **APR** reads files into memory and **AMM** maps memory. `src/sceampr_exports.cpp` implements about 120 exports (`sceAmprCommandBuffer*`, `sceAmprAprCommandBuffer*`, `sceAmprAmm*`, ...). It runs them in software with an APR reactor and an equeue bridge, on top of `sceKernelAioSubmitReadCommandsMultiple` and `sceKernelApr*`, which are hooked in libkernel.
- **Asset packs:** a second feature compresses `/app0` into seekable LZ4 packs (`AMPRPAK4`, index `AMPRIDX3`, runtime config `AMPRCFG1`). Reads are optionally intercepted process-wide (`open/read/pread/AIO`). The workflow is: record traces (`ampr_commands.bin`), profile them into TOML (`tools/ampr_pack_profile.py`), pack (`tools/ampr_pack.py`, or the GUI), and verify. Its purpose is to shrink games so they fit.
- **Build:** this is the only drakmor project with an open-toolchain build. The `Makefile` uses ps5-payload-sdk with its own CRT (`ps5/crt`), a linker script (`ps5/prx/prx.script`), and NID/hash fix-up tools. A CI workflow is included. `libSceAmpr.sln` provides the official-SDK build.
- `tests/` contains on-console smoke, stress, negative and PRX-boundary tests.

### `make_fself/`
- `make_fself_batch.py` is a superset of `sdk/samples/install_app/make_fself.py`. It adds:
  - directory batch signing in place,
  - `--unpack` (SELF/FSELF to ELF, with `.selfbak` backups),
  - repair of incomplete `.sceversion` records,
  - automatic derivation of PAID and ptype.
- For our work, this is the most useful signer.

### `ppr-patch/`: A53 plaintext PFS reads
- The A53 is an ARM core in the storage path (MP4, "PPR"). Normally every 64 KiB package read goes through `zcnDriverAddCommandsPackageDecryptToZcnBufferAndVerifySign` (AES-XTS plus SHA3-CMAC).
- The payload redirects the **ten** callers of that helper to a wrapper. The wrapper switches to `PLAINTEXT_NOAUTH` only when the kernel passes the exact key-index pair `AES=0xff, SHA=0xfe`; every other read stays native.
- It has 55 layout profiles covering 89 firmware/target mappings (1.00–11.40, retail/testkit/devkit). It fails closed on anything unknown and requires `--idle`.
- A separate `a53_kmb_range_*` payload removes the opcode-0x53 key-slot whitelist check.
- Its doc says the matching **kernel-side** marker is not part of this repo. Something else has to set `0xff/0xfe` for plaintext mounts.

### `sce_symbols/` and `sce_symbols_new/`
- Both use the same format: `aerolib.csv` (`<NID> <name>`), the `*_names.txt` lists, and the `*_names2nid.py` generators.
- `sce_symbols_new` is the **current upstream** and contains every line of the drakmor fork, plus 4,583 hash-verified PS5 names across 167 libraries.
- AnyPS5's `nid_names.py` pins upstream commit `2883963`, which is exactly the `sce_symbols_new` HEAD.

### Review findings

| # | Severity | Finding |
|---|---|---|
| 1 | **High** | **`ppr-patch` is incomplete.** The `Makefile` needs `kmb_range_main.c`, `kmb_range_patch.{c,h}`, `kmb_range_patch_test.c`, `kmb_range_profiles.inc`, `generate_kmb_range_profiles.py` and `verify_kmb_range_profiles.py`. None of them are in the repo, and the build's dependency rules list `kmb_range_patch.h`, so `make` / `make all` (which runs `verify` and `host-test` first) will fail. The `a53_ppr_*` sources are present, but extracting them requires editing the Makefile. `make profiles` and `make verify` also need the private `PPR_ROOT` archive of A53 images, which is not included. |
| 2 | Medium | **`backport-helper` and `pgo_stub` build only with the official Sony Prospero SDK** (MSBuild platform, SDK headers such as `<_rtc.h>`). With only ps5-payload-sdk, they need porting first. `ampr_emu`'s Makefile, CRT and linker script show how to do that port. |
| 3 | Low | **Latent bugs in upstream `make_fself.py`** (identical in `sdk/` and `pgo_stub/tools/`, also present in `make_fself_batch.py`): (a) `is_intervals_overlap` compares `p1[1] <= p2[0]` instead of `p2[0] <= p1[1]`; (b) `segment_index` is declared with `@wbits.setter`, so **reading** `segment_index` returns `wbits`. Neither breaks signing today: the function is never called, the property is only written, and the batch tool reads `props` directly. `ampr_emu/tools/make_fself.py` fixes both, so use it as the base copy. |
| 4 | Low | **Duplication across repos:** 4 copies of `make_fself`, 2 diverged copies of `hde64.cpp` (backport-helper and ampr_emu), and 2 independent pairs of `make_*_stub.py`. A shared tools folder would avoid drift. |
| 5 | Low | `sce_symbols/` is redundant. Use `sce_symbols_new/` everywhere; `sdk` and AnyPS5 already point at upstream. |
| 6 | Info | Doc issues: `ampr_emu/README.md` is two lines (the real docs are in `docs/`). `ppr-patch/README.md` links `../PPR_READ_PATHS.md`, but the file is in the same folder. The pgo_stub log name is `playlgo.log`. |

### Where this fits in the overall picture
- Backpork Kitchen's **SDK-field patch and fake signing** are still required, and `make_fself_batch.py` replaces Backpork's VB signer.
- Backpork's **whole fakelib SPRX files** can be replaced by small purpose-built shims: `pgo_stub`, `ampr_emu`, and `backport-helper`-style libraries that export missing NIDs or hook failing calls. AnyPS5's `core/libs/prx/*` is a good reference for writing such shims.
- `ppr-patch` covers the separate problem of **mounting unsigned or plaintext game images** on the console.

---

## Part 3: GT7 1.71, before and after the backport

Two copies of **Gran Turismo 7** (`PPSA01317`, content ID `UP9000-PPSA01317_00-GRANTURISMO70000`, version `01.710.000`) are available. Comparing them byte by byte shows exactly what a backport changes.

| Folder | Role | SDK fields in the binaries |
|---|---|---|
| `.original/NOT_BACKPORT/` | **Before**: the original, not backported (confirmed by the user) | `0x10000040` / `0x12090001`, the same values as Backpork's **FW 10** pair |
| `.original/GT7/USA/` | **After**, backported to **FW 4.xx** | `0x04000031` / `0x09040001`, which is exactly Backpork's **FW 4** pair |

> ✅ **Confirmed by the user:** NOT_BACKPORT is **not** a backport, and **both copies run on a PS5** (each on its intended firmware). So every patch listed below is known to work in practice.
>
> **SDK values are not the same thing as the firmware requirement.** The original binaries carry SDK `0x10000040` / `0x12090001`, while `param.json` says `sdkVersion 0x13…` and requires FW 13.60. The SDK values in the binaries record which SDK the game was compiled with; the package-level `param.json` sets the firmware the console requires. Backpork's FW 10 pair is therefore simply real values taken from shipped games. Don't use the binaries' SDK fields to guess a game's firmware requirement; read `param.json`.
>
> NOT_BACKPORT's `fakelib/` contains `libSceAmpr` and `libScePlayGo`. *(Correction 2026-10-08: on disk `libSceAmpr.sprx` is **0 bytes** (dated 2026-06-26); only `libScePlayGo.sprx` is byte-identical to the FW 4 copy. Tools see the empty file as kind `unknown`; `backport` replaces it with the store's `libSceAmpr.sprx`.)* **These are not part of a backport.** They were added so the game runs under **ShadowMount** with **`ampr_emu`** (confirmed by the user). So the AMPR/PlayGo replacements are a requirement of running the game from a ShadowMount mount, on any firmware; the backport's own additions are only `libSceAgc`, `libSceAgcDriver`, `libScePsml`, `k9.psp` and `ampr_emu.index`.
>
> ShadowMountPlus is now in `reference/` and covered in [Part 4](#part-4-shadowmountplus-the-runtime-that-loads-fakelibs). It skips these replacements for real installed packages, so they are only needed when running from a dump.

### Files

| Path | NOT_BACKPORT | GT7/USA (FW 4) | What it is | Ghidra |
|---|---|---|---|---|
| `eboot.bin` (105 MB) | PS5 SELF `0xEEF51454` | PS4-style SELF `0x1D3D154F` | Main executable (ELF type `0xFE10`, `ET_SCE_EXEC`; internal name `application_gt7`) | Yes, unwrapped |
| `libSieRdNNabla.prx` (30 MB) | PS5 SELF | PS4-style SELF | Game library; the name suggests Sony's "Nabla" neural-network runtime (probably the AI driver) | Yes, unwrapped |
| `logiWheel.prx` (330 KB) | PS5 SELF | PS4-style SELF | Game library: Logitech steering-wheel support | Yes, unwrapped |
| `sce_module/libc.prx`, `libScePfs.prx`, `libSceNpCppWebApi.prx` | PS5 SELF | PS4-style SELF | Libraries bundled with the game (SELF containers despite the `.prx` extension) | Yes, unwrapped |
| `sce_sys/about/right.sprx` | PS5 SELF | PS4-style SELF | Standard rights module (ELF header ABI-version byte is 3; the others have 2) | Yes, unwrapped |
| `fakelib/libSceAmpr.sprx`, `libScePlayGo.sprx` | present | present, **byte-identical** | Software AMPR (`ampr_emu`) and PlayGo replacements (SDK `0x02000009`), needed to run under **ShadowMount** rather than for the backport | Yes, unwrapped |
| `fakelib/libSceAgc.sprx`, `libSceAgcDriver.sprx` | absent | PS5 SELF, fake-signed | Replacement GPU libraries | Yes, unwrapped |
| `fakelib/libScePsml.sprx` | absent | PS4-style SELF | PSSR (PS5 Pro upscaler) runtime | Yes, unwrapped |
| `k9.psp` (3.9 MB) | absent | `PSP\0` header, then weights | **PSSR upscaler model**. Loaded by `libScePsml` from `/app0/k9.psp`; contains layer names such as `E2R0FasterNet`. `libScePsml` also references `MFSR4K_v070.psp` and `MFSR8K_v070.psp`. | No, data |
| `ampr_emu.index` (8.5 MB) | absent | `AMPRIDX3` | `ampr_emu` file-ID index for this exact `/app0` | No, data |
| `sce_sys/param.json` | present | present, 4 fields changed (see below) | Game metadata | No |
| `sce_sys/` other files | `icon0/pic0-2` (`.png`/`.dds`), `keystone`, `license.dat/.info`, `nptitle.dat`, `pfs-version.dat` (`01.710.0`), `ext_info.dat`, `trophy2/`, `uds/` | absent | Package metadata, trophies, license | No |

All SELFs in both folders are **fake-signed with no encrypted segments**. NOT_BACKPORT uses the PS5 container (`version 16`, key type `0x10000101`, flags `0x32`); the FW 4 copy was re-signed with the PS4-style container that `make_fself.py` produces (`version 0`, key type `0x101`, flags `0x22`). The unwrapped ELFs have identical lengths between the two copies.

### Loading the binaries in Ghidra
- None of the `.sprx`, `.prx` or `.bin` files are plain ELFs. Ghidra doesn't recognise the SELF container, so it falls back to "raw binary" and asks for a language, and the result is useless.
- All of them unwrap on a PC:
  ```bash
  python .original/reference/make_fself/make_fself_batch.py --unpack input.sprx output.elf
  ```
- Unwrapped copies:
  - FW 4 copy: `work/GT7_root_elf/`, `work/GT7_fakelib_elf/`, `work/GT7_sce_module_elf/`, `work/GT7_sce_sys_elf/`
  - NOT_BACKPORT: `work/NOT_BACKPORT_elf/`, which mirrors its folder layout

  The originals are unchanged.
- Import settings: language **`x86:LE:64:default`**, compiler spec **`gcc`**. Ghidra may warn about the SCE file type (`0xFE10` or `0xFE18`); accept the warning.
- Imports and exports appear as 11-character NIDs. Resolve them with `reference/sce_symbols_new/aerolib.csv`, or with `work/tools/sce_elf.py` (below).

### PS5 ELF dynamic linking (learned while diffing)
- PS5 ELFs use the **standard `DT_*` tags**: `DT_JMPREL`, `DT_RELA`, `DT_SYMTAB`, `DT_STRTAB`. Their values are virtual addresses inside a non-executable `PT_LOAD` (in `eboot`: file offset `0x5d22ed0`, vaddr `0x7156ed0`). PS4 used `DT_SCE_*` offsets into `PT_SCE_DYNLIBDATA`; on PS5 only `DT_SCE_SYMTABSZ` (`0x6100003F`) is still needed, for the symbol count.
- Symbol names are `<NID>#<library-id>#<module-id>`. The IDs use the NID base64 alphabet as **base-64 digits**: `s`=44, `9`=61, `BM` = 1×64+12 = 76. They match the IDs in the `0x61000049` (import library) and `0x61000045` (needed module) dynamic tags. Examples in `eboot`: 44 = `libSceVideoOut`, 61 = `libSceVideoOutVrrStatus`, 76 = `libkernel`, 74 = `libSceDeviceService`.
- `0x61000043` holds the module's own name (`application_gt7`, `libc`). `0x61000041` holds the build path, for example `C:/pdi/src/gt7/maintenance/build-ppr.<hash>/application/gt7/Product/application_gt7.elf` and `W:/Build/J03848509/sys/internal/usermode/src/libc/Prospero_Release/libc.prx`.
- `eboot` imports 80+ libraries. Ones that matter for backporting include `libSceAgc`/`libSceAgcDriver`, `libSceAmpr`, `libScePsml` (+ `_quality8K`, `_specularAA`, `_mfsr2`), `libSceVideoOutVrrStatus`, `libSceHmd2`, `libSceVrTracker2`, `libSceTextToSpeech2`, `libSceNpTrophy2`, `libSceAudioOut2`, `libSceDeviceService`, `libSceAudioOutDeviceService`, `libMozaSDK` (Moza wheels) and `libSceUserServicePlatformPrivacyWs1`.
- **Tool:** `work/tools/sce_elf.py` parses all of this. `--imports` lists the PLT/GOT imports with names, and `--got <vaddr…>` says which import owns a GOT slot.

### What the backport to FW 4 changed (byte diff)

**Every binary:** the SDK pair in the `PT_SCE_PROCPARAM` / `PT_SCE_MODULE_PARAM` segment changed from `0x10000040`/`0x12090001` to `0x04000031`/`0x09040001`. `libSieRdNNabla`, `logiWheel`, `libScePfs`, `libSceNpCppWebApi` and `right` have **no other change**.

**`eboot`: 6 imports neutralised by rewriting their GOT slots, plus 1 data patch.**
Two small stubs were written into `int3` padding in `.text`:
- `0x3d2`: `xor eax,eax; ret` (return 0)
- `0x573`: `mov dword [rdi],2; and dword [rsi],0; xor eax,eax; ret`

The initial GOT values of these imports were then pointed at the stubs instead of their PLT lazy-bind stubs:

| GOT slot | Import (NID) | Library | Now goes to |
|---|---|---|---|
| `0x55df470` | `sceKernelGetOperationMode` (`NH6xARDOVv8`) | libkernel | `0x573`: `*mode = 2`, `*lowEnergy = 0`, return 0 |
| `0x55df670` | `sceVideoOutVrrPegToFixedRate` (`5tRaBjtdTzY`) | libSceVideoOut | `0x3d2` (return 0) |
| `0x55df678` | `sceVideoOutVrrUnpegFromFixedRate` (`T4ucGB8CsnM`) | libSceVideoOut | `0x3d2` |
| `0x55df688` | `kP2L8t3j-aM` (unnamed; AnyPS5 calls it `sceVideoOutUnknown00`) | libSceVideoOutVrrStatus | `0x3d2` |
| `0x55df6b8` | `LibwuIonIBw` (unnamed; AnyPS5 `sceVideoOutUnknown01`) | libSceVideoOutVrrStatus | `0x3d2` |
| `0x55e0d48` | `La9uyZv4Kvw` (unnamed) | libkernel | `0x3d2` |

The data patch: at vaddr `0x83403a`, `mov qword [rdi+0x18], -1` became `mov qword [rdi+0x18], 0`. The purpose is unknown.

In effect, imports that are missing on FW 4 are pre-resolved to local stubs, so they never reach the dynamic linker. **Confirmed working on console**: the loader in this setup leaves those GOT entries alone when it can't resolve the NID. Whether that is the stock PS5 loader's behaviour (lazy binding) or something the backpork loader does is not yet known. Note that `sceKernelGetOperationMode` gets operation mode 2 here, while `backport-helper` returns 0 for the same function.

**`sce_module/libc`: 6 changes, all about imports that are missing on old firmware.**
1. **NID swap `[0x14]`:** `4h6F1LLbTiw` (`sceKernelMapNamedFlexibleMemoryInternal`) became `IWIBBdTHit4` (`sceKernelMapFlexibleMemory`). This is exactly Backpork's optional "6xx libc patch"; the extra name argument is simply ignored.
2. **NID swap `[0x74..0x77]`:** `sceKernelSyncOnAddressWait32`, `…Wait64`, `…Wake` and `…Wait` were all renamed to `04AjkP0jO9U` (`_umtx_op`, the FreeBSD futex syscall wrapper that old firmware has). Then the **PLT stubs were rewritten** to translate the arguments:

   | Original import | New PLT code | Resulting `_umtx_op` call |
   |---|---|---|
   | `SyncOnAddressWait32(addr, val, timeout)` | `mov r8,rdx; mov edx,esi; rsi=0xb` | `_umtx_op(addr, UMTX_OP_WAIT_UINT=11, val, 0, timeout)` |
   | `SyncOnAddressWait64(addr, val, timeout)` | `mov r8,rdx; mov rdx,rsi; rsi=2` | `_umtx_op(addr, UMTX_OP_WAIT=2, val, 0, timeout)` |
   | `SyncOnAddressWake(addr, count)` | `movsxd rdx,esi; rsi=3; r8=0` | `_umtx_op(addr, UMTX_OP_WAKE=3, count, 0, 0)` |
   | all three, then `SyncOnAddressWait` | shared tail at `0x113750`: `mov r9d,-1; xor ecx,ecx; jmp [GOT 0x77]` | `_umtx_op` through slot `0x77` |

   Slot `0x77`'s lazy-bind stub (`push 0x77; jmp PLT0`) was moved into padding at `0x112443`, and GOT `0x18e510` was repointed to it.
3. **Call retarget:** `__cxa_thread_atexit` (`BKSCW2bCACA`, at `0x7dd20`) used to call import `qBS714-Jr3g` from `libSceLibcInternalExt` (unnamed; probably `__cxa_thread_atexit_impl`). It now calls libc's own `__cxa_atexit` (`tsvEmnenz48`, `0x113e0`). Thread-exit destructors therefore become process-exit destructors, which is a small change in meaning.

**Ghidra verification of the libc patches** (2026-10-08, on `work/GT7_sce_module_elf/libc.elf`; the thunks are renamed `bp_thunk_*` and commented in the Ghidra project):

- **Who calls the thunks:** the MSVC-STL style atomic-wait exports in libc. `__std_atomic_wait_direct_32` and `_64` call the Wait32/Wait64 thunks. `__std_atomic_notify_one_direct` (count 1), `__std_atomic_notify_all_direct` (count `0x7fffffff`), `BS5uazcBa-k` (unnamed) and `__std_init_once_complete_clr` call Wake. `__std_init_once_begin_initialize_clr` calls the generic Wait directly through the shared tail.
- **Original signatures, reconstructed from the callers:**
  - `int sceKernelSyncOnAddressWait32(volatile u32* addr, u32 expected, u32* timeout_us /*NULL = infinite*/, const char* name)`
  - `…Wait64` takes the same arguments with a `u64` value.
  - `…Wake(void* addr, int count)`.

  The callers convert milliseconds to microseconds (`imul 1000`, saturating to `0xFFFFFFFF`). `name` is `"SceLibcV2AtomicWait"` (`0x1520d0`).
- **How faithful the translation is:**

  | Thunk | Verdict |
  |---|---|
  | Wake → `UMTX_OP_WAKE` | Exact |
  | Wait32/Wait64 with no timeout | Exact |
  | Wait32/Wait64 **with a timeout** | **Lossy.** The thunk passes the pointer to a `u32` microsecond count as `uaddr2` with `uaddr1 = 0`, so (with FreeBSD semantics) the kernel reads a 16-byte `struct timespec` from it: the µs value plus stack bytes. The result is very likely `EINVAL`/`ETIMEDOUT` straight away, i.e. a spurious wake-up. Callers re-check the value, so it still works, but it spins. |
  | Generic Wait (init-once) | **Lossy.** The tail passes `rsi` (= 1, the expected value) through as the **op code**. Op 1 is the old `UMTX_OP_UNLOCK` (unimplemented on newer FreeBSD), so it fails at once and `__std_init_once_begin_initialize` busy-spins until the other thread finishes. |

  The FreeBSD `_umtx_op` semantics are assumed; the PS5 kernel was not checked. This is **acceptable in practice (the game runs)**, but **our tool can do better**: a correct thunk would convert µs to a timespec on the stack and set `uaddr1 = sizeof(struct timespec)`, and would map generic Wait to `UMTX_OP_WAIT`. That needs more code than fits in the PLT slots, so it belongs in padding (a code cave) or a shim PRX.

**Ghidra verification of the eboot patches** (2026-10-08, on `work/GT7_root_elf/eboot.elf`). Only the game ↔ library interface was analysed; game logic is out of scope because it differs per game. The stubs are labelled `bp_stub_*` in Ghidra.

| Library function | How the game uses it | Stub | Safe? | Reusable rule |
|---|---|---|---|---|
| `sceKernelGetOperationMode(int* mode, int* lowEnergy)` | 3 call sites set flags `mode == 3` and `lowEnergy == 1` | `*mode = 2`, `*lowEnergy = 0`, return 0 | Yes: "normal mode, not low-energy" | Stub with mode ≠ 3 and lowEnergy = 0. `backport-helper` returns mode 0, which is equally fine for this check. |
| `sceVideoOutVrrPegToFixedRate(handle, 0, 0)` / `sceVideoOutVrrUnpegFromFixedRate(handle)` | Called from one toggle function; **return value ignored** | return 0 | Yes; VRR pegging just doesn't happen | return-0 stub |
| `kP2L8t3j-aM`, `LibwuIonIBw` (libSceVideoOutVrrStatus) | **No direct callers found** (imported but apparently unused, or called through a pointer) | return 0 | Yes, as far as visible | return-0 stub; the library doesn't exist on FW 4 |
| `La9uyZv4Kvw` (libkernel, unnamed) | No arguments; the int result is passed on as a value at 2 sites | return 0 | Probably; meaning unknown | return-0 stub |
| **`sceSaveDataMount3`** | `SaveDataMount3` struct built on the stack | **Argument patch**, not a stub: `system_blocks` (`+0x18`) changed from `-1` to `0` | Works on console | Newer SDKs pass `system_blocks = -1`, which FW 4 presumably rejects. This applies to every game, but the struct is filled inline in game code, so automatic patching means finding each call site. **A shim/hook that rewrites `-1 → 0` inside `sceSaveDataMount3` is the generic solution**, and `backport-helper`'s example late hook is exactly `sceSaveDataMount3`. |

`SaveDataMount3` layout (from AnyPS5 `SceTypes.hpp`, matching the game code): `+0x00 user_id`, `+0x08 dir_name*`, `+0x10 blocks` (the game clamps it to 0x30..0x4000), `+0x18 system_blocks`, `+0x20 mount_mode` (RDONLY 1, RDWR 2, CREATE 4, CREATE2 32), `+0x28 resource`, then 32 reserved bytes.

**A patch that was already there:** at `0x834048`, `xor ecx,ecx; nop` overwrites the computed `setnc` result, so `mount_mode` is **always 1 (RDONLY)**. This patch is present in **both** NOT_BACKPORT and the FW 4 copy, so it isn't part of the FW 4 backport. Its origin and purpose are unknown (see the open questions).

**A fourth patch pattern** joins the list: **patching a library-call argument value at the call site** (`system_blocks -1 → 0`). The others were NID swap, PLT thunk, GOT→stub and call retarget.

**NOT_BACKPORT is not a pristine dump: it already contains three hand patches** (found 2026-10-08; they are identical in the FW 4 backport):
1. eboot `0x834048`: `SaveDataMount3.mount_mode` forced to 1 (RDONLY), noted above.
2. eboot PLT `0x3f0a460` (`std::_Mtx_lock`, `5qXct3c1skg`) and `0x3f0a470` (`std::_Mtx_unlock`, `4bp9gcNLwMI`) are redirected (`jmp rel32; nop`) to guard wrappers in padding after a `ret` (`0x9ab3e3`, `0x9ab3f9`): `mov rax,rdi; shr rax,0x2f; jne <xor eax,eax; ret>; jmp [GOT]`. So a mutex pointer outside the user address range (bit 47 or higher set) is skipped (returns 0) instead of locked.

These look like compatibility fixes made when the dump was prepared, for example for running from a dump. Their author and purpose are unknown. `ps5bp` reports such entries as `patched`.

### eboot patch checklist (generalised from the complete GT7 diff)

The full byte diff of `eboot.elf` (NOT_BACKPORT vs FW 4) has **exactly 11 changed ranges**, and every one is explained above. For any game, an eboot backport consists of:

1. **SDK pair** in `PT_SCE_PROCPARAM` (`+0x10` PS4 SDK, `+0x14` PS5 SDK) → the target firmware pair. *(GT7: 2 ranges)*
2. **For every import whose NID doesn't exist on the target firmware:**
   - Find its GOT slot (JMPREL relocation; `sce_elf.py --imports`).
   - Write a stub into `int3` padding in `.text` (`0xCC` runs between functions; GT7 used `0x3d2` and `0x573`).
   - Set the slot's **initial GOT value** to the stub's address.
   - Default stub: `xor eax,eax; ret`. Getter functions with out-parameters need a stub that fills them with safe values (for example `GetOperationMode`: mode 2, lowEnergy 0).
   - Reuse one stub for every import that only needs "return 0". *(GT7: 2 stubs + 6 GOT slots)*
   - Before stubbing, check how the game uses the result (return value ignored? out-params read?). Keep a per-function rule table so later games reuse the decision.
3. **For every library call whose argument values the target firmware rejects:** patch the value. GT7: `sceSaveDataMount3` `system_blocks -1 → 0`, 1 range. This is preferably done in a shim hook rather than in game code.
4. Re-sign as fake SELF.

Things **not** changed in eboot: the dynamic section, NIDs (NID swaps were only needed in libc), code size, section layout and the import library list. The patches fit entirely in existing padding and existing slots.

### fakelib analysis (step 1.3)

Tool: `work/tools/import_check.py` checks every function import against a reference firmware's export list (`PS5-3.20_Libs`, 31,275 NIDs). Data imports such as `__stack_chk_guard`, `_Stdout` and vtables are listed separately, because the stub lists only contain functions. **Caveat:** 3.20 is older than 4.xx, so "not on 3.20" means "possibly missing on 4.xx" until we have real 4.xx export lists.

| fakelib | Origin (embedded build path) | Function imports not on 3.20 | Patched? |
|---|---|---|---|
| `libSceAgc` | Sony `orbis-build_0005c`, job `J03828141` | 20 | **Yes**: 8 GOT slots → stub `0x26e3` (`xor eax,eax; ret`): `sceKernelIsTrinityMode`, 2 unnamed libkernel, all 5 `sceShaderTranscode*` |
| `libSceAgcDriver` | same build as `libSceAgc` | 5 | **Yes**: 2 GOT slots → stub `0x450`: `__sce_v2::_Xlength_error`, `aj7wRdAWFeM` |
| `libScePsml` | Sony job `J03950001` (a different build) | 67 | **Yes**: 25 GOT slots → stub `0x4070`. These are libc++ `__sce_v2` internals (`_Mtx_init/lock/unlock/destroy`, `_Throw*`, `_Locinfo*`, `_Getctype`, `_Toupper`…), `sceKernelIsTrinityMode`, `sceKernelTitleWorkaroundIsEnabled2`, `sceVideoOutGetHandleByBus` and `…SysCursorSetSize` |
| `libSceAmpr` | `C:/dev/ps5/ampr_emu/…/Prospero_ReleaseHooks` = **drakmor's ampr_emu** (confirmed) | 0 | Not needed |
| `libScePlayGo` | `C:/dev/ps5/playgo/./out` = presumably **drakmor's pgo_stub** | 0 | Not needed |

Findings:
- **Why `libSceAgc` and `libSceAgcDriver` are shipped:** `libScePsml` (PSSR) imports 205 `libSceAgc` and 5 `libSceAgcDriver` functions, 36 of them newer than 3.20. **The fakelib `libSceAgc`/`libSceAgcDriver` export all 210.** `eboot`'s 63 AGC imports (9 newer than 3.20) are also fully covered. So the newer GPU libraries come along as **dependencies of the newer libraries the game uses**.
- **The fakelibs are patched exactly like `eboot`:** a shared `xor eax,eax; ret` stub in padding, with missing imports' GOT slots pointed at it. Same pattern, same tool.
- **A fifth technique, symbol redefinition** (found while building `ps5bp`): in the fakelibs, the stubbed imports' **symbols** were also rewritten into local definitions (`st_shndx = 1`, `st_value` = the stub address). The dynamic linker then resolves the import inside the module. Some imports were neutralised **only** this way, with the GOT untouched: `qSleeVC6lwI` in `libSceAgc`, and 11 in `libScePsml`, including the data import `std::cout` (`9Qe7XFSQ4Lk`). Totals per unique NID: `libSceAgc` 9 stubbed (8 GOT + 1 symbol-only), `libSceAgcDriver` 4, `libScePsml` 33 (36 relocations). `eboot` uses the GOT technique only. Symbol redefinition also covers RELA/data imports, which a GOT redirect cannot. Consequence for tools: such symbols look like exports unless exports are restricted to the module's own export-library ids (dynamic tag `0x61000047`).
- **Signing:** GT7's SELFs were made with **upstream `make_fself.py`** defaults (PAID `0x3100000000000002`, ptype fake, versions 0). Unwrapping and re-signing reproduces them byte for byte, except the ex-info **digest**, which is a SHA-256 of the whole original ELF file. It can't be recomputed when the original had data outside its segments (section headers), and it evidently isn't checked, since GT7 works with digests from its own ELFs. NOT_BACKPORT's PS5-container SELFs (`54 14 F5 EE`, 48 bytes smaller) came from neither `make_fself.py` nor `make_fself_batch.py`. Common header bytes 4–7 (version, mode, endian, attributes): PS4-style `00 01 01 12`, PS5-style `10 01 01 12`; both have 12 segment entries for these modules, and the same unpacker handles both (the C# port checks the endian at byte 6).
- **Risky stubs in `libScePsml`:** `std::mutex` internals (`_Mtx_lock`/`_Mtx_unlock`) now **do nothing**, which makes them unsafe if PSSR runs multi-threaded. It is presumably harmless because PSSR only runs on PS5 Pro hardware, and that path is never active on a FW 4 console.
- **Only called imports were stubbed.** After counting 3.20 exports plus everything shipped with the game (`fakelib/*`, `sce_module/*`, `libSieRdNNabla`, `logiWheel`) as available, the imports that remain **unresolved and unpatched** are:
  - `eboot`, 30: the whole PS VR2 stack (`libSceHmd2` ×16, `libSceVrTracker2` ×5), `libSceTextToSpeech2` ×7, `sceShareGetRunningStatus`, and `D-CzAxQL0XI` (`libSceUserServicePlatformPrivacyWs1`)
  - `libSceAgc`, 2: `sceKernelGetProsperoCompiledSdkVersion`, `sceKernelGetToolMemoryRange`
  - `libSceAgcDriver`, 1: `sceKernelIsThreadSanitizerEnabled`
  - `libScePsml`, 3: `sceKernelGetToolMemoryRange`, `sceKernelGetProsperoCompiledSdkVersion`, `sceVideoOutGetPipelineStatus`
  - `libc`, 6: including `qBS714-Jr3g`, whose call was retargeted, so it is no longer called

  The game runs anyway. **This strongly suggests the loader binds PLT imports lazily, so an unresolved import only fails if it is actually called.** This answers the earlier loader question in practice, though the mechanism (stock lazy binding vs kstuff/ShadowMountPlus) is unverified. Caveats: some of these may exist on 4.xx; and AnyPS5 notes GT7 calls `sceTextToSpeech2Initialize` and skips narration if it fails, so either TTS2 exists on 4.xx or that call is behind a check. Real 4.xx export lists will settle this.
- **What this means for our tool:** "missing import" alone is not enough to decide what to patch. The real rule is **missing *and* reachable/called**. Options:
  1. Stub **every** missing import. This is safe, because a return-0 stub is never worse than an unresolved import that crashes when called. It's the easiest default, but it hides real missing features.
  2. Stub only imports with call sites, found by static xrefs.
  3. Use a per-function rule table built from analysed games: stub, adapter or shim.

  Recommended: option 1 by default, plus the rule table for functions that need non-zero results or argument fixes.

**`sce_sys/param.json`:** the SDK and firmware fields were **not** changed (`sdkVersion 0x13…`, `requiredSystemSoftwareVersion 0x1360…` in both copies). Only GT7's own `userDefinedParam1..4` changed:

| Field | NOT_BACKPORT | FW 4 |
|---|---|---|
| `userDefinedParam1` | `531705937` (`0x1FB12A51`) | `536870912` (`0x20000000`) |
| `userDefinedParam2` | `11` | `20` |
| `userDefinedParam3` | `22` | `30` |
| `userDefinedParam4` | `1` | `2` |

What these mean to the game is unknown.

**Container and folder:**
- Every SELF was re-signed in the PS4-style fake-SELF format, which is what `make_fself.py` produces.
- `fakelib/` gained `libSceAgc`, `libSceAgcDriver` and `libScePsml`.
- `k9.psp` and `ampr_emu.index` were added at the root.
- Most of `sce_sys/` (icons, license, keystone, trophies, uds, `nptitle.dat`) was dropped; only `param.json` and `about/right.sprx` remain.

### The backport recipe, as observed
1. Start from decrypted (fake-signed) binaries.
2. Patch the SDK pair in every executable/module to the target firmware (Backpork's table).
3. **Neutralise imports that don't exist on the target firmware:**
   - by **swapping the NID** to an older equivalent (libc `MapNamedFlexibleMemory` → `MapFlexibleMemory`),
   - by **swapping the NID and rewriting the PLT stub** to adapt arguments (`SyncOnAddress*` → `_umtx_op`),
   - by **retargeting the call** to a local function (`__cxa_thread_atexit`),
   - or by **pointing the GOT slot at a local return-0 stub** (eboot VRR, operation mode, etc.).
4. Ship replacement libraries in `fakelib/`: for the backport, `libSceAgc`, `libSceAgcDriver` and `libScePsml` (with `k9.psp`). The AMPR (`ampr_emu`, with `ampr_emu.index`) and PlayGo replacements are there for **ShadowMount**, not for the firmware downgrade.
5. Re-sign everything as fake SELF.
6. Run it with **ShadowMountPlus**, which mounts the dump and overlays `<game>/fakelib/` over the sandbox's `common/lib` (see [Part 4](#part-4-shadowmountplus-the-runtime-that-loads-fakelibs)). The older standalone `ps5-backpork.elf` does the same job, but must not run together with ShadowMountPlus.

Steps 1, 2 and 5 are what Backpork Kitchen automates. **Step 3 is manual work that none of the reference tools do**, apart from Backpork's single libc string patch. It is the main thing our tooling would need to add: automatic detection of missing NIDs per target firmware, plus a library of these patch patterns.

### Relevance to the other references
- **Backpork Kitchen** has SDK pairs only up to FW 10 and has no 13.x profile. Its FW 4 pair is the one used here, and its FW 10 pair equals the original GT7 binaries' SDK values.
- **backport-helper** covers SDK 4–13 and is the hook-based alternative to the GOT and PLT patches above. Its `sceKernelGetOperationMode` example is the same function the eboot patch stubs out.
- **AnyPS5** already targets this title. `docs/dev/TechnicalDebt.md` mentions `PPSA01317` (the PS VR2 `libSceHmd2` and `libSceTextToSpeech2` start-up paths), and its `libSceVideoOut` names the two unnamed VrrStatus NIDs patched here.
- **PS5-3.20_Libs** and the firmware-specific export lists are what step 3 needs, to know which NIDs exist on the target firmware.

### Open questions
- Is our reading of why dumps need `ampr_emu`/PlayGo right (system AMPR/PlayGo need package-backed `ppr_pfs` storage)? ShadowMountPlus's code is consistent with it, but it isn't stated anywhere ([Part 4](#part-4-shadowmountplus-the-runtime-that-loads-fakelibs)).
- `fakelib/libSceAmpr.sprx` is `ampr_emu` (confirmed by the user and by its build path). `libScePlayGo.sprx` was built from `C:/dev/ps5/playgo/`, presumably `pgo_stub`.
- Which **firmware version** are `fakelib/libSceAgc*` (Sony build `orbis-build_0005c`, job `J03828141`) and `libScePsml` (job `J03950001`) from? Matching these job IDs against firmware dumps would answer it.
- The eboot GOT patches work on console, and many unresolved imports were left **unpatched** (PS VR2, TTS2, etc.; see the fakelib analysis). This strongly indicates lazy PLT binding (an unresolved import only fails when called). Whether that is the stock loader or kstuff is still unverified.
- Does the loader ignore `requiredSystemSoftwareVersion` in `param.json`?
- Names for the NIDs `La9uyZv4Kvw` (libkernel), `kP2L8t3j-aM` and `LibwuIonIBw` (libSceVideoOutVrrStatus), and `qBS714-Jr3g` (libSceLibcInternalExt).
- ~~What does `[rdi+0x18]` at eboot `0x83403a` control?~~ Answered: `SaveDataMount3.system_blocks` (see "Ghidra verification of the eboot patches").
- Who added the three pre-existing NOT_BACKPORT patches (`mount_mode = RDONLY` at eboot `0x834048` and the `_Mtx_lock`/`_Mtx_unlock` guards), and why? If NOT_BACKPORT is untouched, this would have to be original code, but a compiler wouldn't emit `setnc cl; xor ecx,ecx; nop`.
- What do GT7's `userDefinedParam1..4` mean?

## Part 4: `ShadowMountPlus/`, the runtime that loads fakelibs

Source: `drakmor/ShadowMountPlus` (cloned shallow into `reference/ShadowMountPlus`, HEAD `4cde42a`). It is a C payload built with ps5-payload-sdk. It requires **kstuff-lite v1.07+**, and the README states support down to firmware 2.00. It continues VoidWhisper's ShadowMount and credits BestPig's BackPork.

**What it is:** a background "auto-mounter" payload. It finds game **dumps** (folders or disk images) on internal or external storage, mounts them, and registers them in the system library (`app.db`), so they install and launch like normal games. It **replaces the standalone `ps5-backpork.elf` payload**; the README says running both at the same time conflicts.

### How fakelibs reach the game (`src/sm_fakelib.c`)
- ShadowMountPlus hooks `SceShellCore` (`sm_shellcore_hooks.c`, per-firmware offsets in `sm_shellcore_offsets.inc`) to learn when a game's **sandbox is ready**, just before the process spawns.
- It then mounts the selected fakelib directory over the sandbox's `/mnt/sandbox/<TITLE_ID>_<n>/<…>/common/lib`. The mount is `nullfs` for a single source, or `unionfs` when several sources are combined. The game's loader resolves system `.sprx` files from that directory, so **any file in `fakelib/` replaces the system library of the same name**, for that game only.
- **Search order** for the fakelib directory:
  1. `<scanpath>/backports/<TITLE_ID>/fakelib2`, then `…/fakelib`
  2. `<game source>/fakelib2`, then `<game source>/fakelib` (this is where GT7's is)
  3. `/data/homebrew/backports/<TITLE_ID>/` as a fallback
- A `fakelib2` directory is used **exclusively**: nothing is merged into it.
- A plain `fakelib` can be combined with a **global fakelib** (`/data/shadowmount/fakelib`, with priority `game` or `global`) and with **emulator updates** from `/data/shadowmount/emus`, which replace files of the same name. The combined result is cached and expires after 7 days.
- For each game it also mounts writable `nullfs` views of `/mnt` and `/data` into the sandbox.

### Why dumps need `ampr_emu` and the PlayGo stub
- When a game is a **real installed package** (app0 on an `nsfs` sandbox backed by `ppr_pfs`, mounted at `/mnt/sandbox/pfsmnt/<TITLE_ID>-app0`), ShadowMountPlus **deletes `libSceAmpr.sprx`, `libScePlayGo.sprx` and `libkernel.sprx`** from that title's `/data/homebrew/backports/<TITLE_ID>/fakelib` before mounting (`cleanup_ppr_backport_fakelib`). The real libraries work there.
- So the replacements are only needed when the game runs **from a dump** (folder or image) instead of an installed package. The likely reason: the system AMPR and PlayGo services expect package-backed storage that a dump doesn't have. This explanation is inferred from the code, not stated in the repo.
- That's why GT7's `fakelib/libSceAmpr.sprx` (`ampr_emu`) and `libScePlayGo.sprx` are in **both** GT7 copies: they're needed to run under ShadowMount, and are unrelated to the firmware downgrade.
- **AMPR auto-update** (`sm_ampr_updater.c`, setting `auto_update_ampr=1`): downloads `libSceAmpr.sprx` from `drakmor/ampr_emu` releases and installs it as an emulator update.
- A fakelib may also contain a replacement **`libkernel.sprx`**; it's in the cleanup list.

### Backports for installed packages (`src/sm_pkg_backport.c`)
- For an installed package, `<scanpath>/backports/<TITLE_ID>/` files are injected into the package's `app0` through **NSFS redirects**. This uses `/dev/nsfsctl` with ioctls `0xC0406E00` (create) and `0xC0406E01` (delete), "plain" redirects for files, and "overlay" redirects for missing directories, with up to 256 per sandbox.
- This means patched `eboot.bin` / `sce_module/*.prx` files can be overlaid onto an installed game **without repacking it**.

### Supported game sources
| Extension | Filesystem | Notes |
|---|---|---|
| folder | n/a | `<GAME_DIR>/sce_sys/param.json` must exist |
| `.ffpkg` | UFS2 | Recommended. Build with `mkufs2.sh` or UFS2Tool (`newfs -O 2 -b 65536 -f 65536 -m 0 -S 4096`). |
| `.exfat` | exFAT | 64 KiB clusters; fast LVD/BFS path when stored under `/data` |
| `.ffpfs` / `.ffpfsc` | PFS / compressed PFS container | Experimental. Built with PSBrew/MkPFS; unsigned images are mounted with signature checks disabled. |

Devices are attached through LVD or `/dev/mdctl`. Default scan paths cover `/data/homebrew`, `/data/etaHEN/games`, `ext0/1` and `usb0-7`; a `manual.lst` lists extra sources.

### Other features
- kstuff auto-pause/resume around game launches, with crash detection that tunes the pause delay into `/data/shadowmount/autotune.ini`.
- Fan target control.
- An HTTP/JSON API with a web UI (port 10101; `docs/api.md`, `docs/openapi.yaml`).
- Notifications in 31 languages.

### Relevance
- It is the **loader half** of the backport. Our tooling only has to produce a game folder or image with patched binaries plus `fakelib/`, and ShadowMountPlus does the library injection. No separate backport payload is needed.
- `backports/<TITLE_ID>/` lets backport files live **outside** the game dump. This is a cleaner layout for distributing a backport, since the original dump stays untouched.
- `sm_shellcore_offsets.inc` and `tools/generate_shellcore_offsets*.py` are a reference for per-firmware ShellCore offsets.

---

## Part 5: Plan for our own backport tool

The GUI application (C#/.NET 10/WPF port of `ps5bp`) has its own plan: [APP_PLAN.md](APP_PLAN.md). Since 2026-10-08 the fix tables live in `data/*.json` (shared by both engines); older mentions of `fixes.py` / `candidates.py` below refer to the same tables.

> The forward-looking phase plan (hardening, console test bench, firmware data, fakelib supply, game ports) is in [PLAN.md](PLAN.md).

**Principle:** most of the pipeline already exists as open source in `reference/`: SELF handling, the SDK patcher, the AMPR/PlayGo shims, the hook template and the ShadowMountPlus loader. The part with **no source** is the hand-made import neutralisation seen in GT7 (Part 3), so that is what the tool must automate. Ghidra is used to **learn and verify those patch patterns**, not to rewrite Sony libraries. Replacement libraries are written from scratch, `ampr_emu`-style.

### Order of work
**Step 0: foundation (port existing open code)**
1. PS5 ELF reader: extend `work/tools/sce_elf.py` with per-library exports and read/write of the SDK fields.
2. SELF unwrap and fake-sign: base it on `ampr_emu/tools/make_fself.py` (bug-fixed copy), plus batch and unpack from `make_fself_batch.py`.
3. SDK patcher: the logic from `PS5-BACKPORK-KITCHEN/.../core/ElfPatcher.vb`.

**Step 1: Ghidra, patch patterns (smallest binary first)**

Always open the **backported** binaries (`work/GT7_*_elf/`). They contain the patches, and all unpatched code is identical to the originals. `work/NOT_BACKPORT_elf/` is only a byte-diff reference; its original bytes are already recorded in Part 3.

1. `work/GT7_sce_module_elf/libc.elf`. It contains all three libc pattern types: NID swap, PLT argument-adapter thunks (`SyncOnAddress*` → `_umtx_op`), and call retarget.
2. `work/GT7_root_elf/eboot.elf` (the **backported** one; its patches and stubs are visible): the callers of the 6 GOT-stubbed imports (is return 0 / mode 2 safe?), and the unknown `[rdi+0x18]` patch at `0x83403a`.
3. `work/GT7_fakelib_elf/libSceAgc*.elf`, `libScePsml.elf`: inspect their imports only. These are newer-firmware system libraries running on FW 4: do all their imports exist on FW 4, or were they patched too?

**Step 2: data**
4. Export lists for each target firmware: dump `/system/common/lib` with PS5-SELF-Decrypter (`-system-common-lib`) per firmware. `PS5-3.20_Libs` covers 3.20 only. Without these lists, missing imports can't be detected automatically.

**Step 3: our tool**
5. Missing-import detector: game imports minus the target firmware's exports, reported per game.
6. Patch library, one module per pattern: NID swap to an older equivalent, PLT adapter thunk, GOT → local stub, call retarget, call-site argument patch, and a `backport-helper`-style shim PRX for complex cases. Prefer the shim for argument fixes such as `sceSaveDataMount3` `system_blocks`, because it works for every game without searching game code.
7. Output in ShadowMountPlus layout: `backports/<TITLE_ID>/` holding the patched binaries plus `fakelib/`, leaving the original dump untouched.

### Status
- [x] `work/tools/sce_elf.py`: imports, GOT lookup, dynamic tags (Part 3)
- [x] Step 1.1: libc patches verified in Ghidra (Part 3, "Ghidra verification"); target firmware for the tool: **4.xx and up** (per the user)
- [x] Step 1.2: eboot stubbed imports and the `0x83403a` patch verified (game ↔ library interface only)
- [x] Step 1.3: fakelib imports analysed (`work/tools/import_check.py`); the fakelibs are GOT-stub patched like eboot; only called imports were patched
- [x] **Step 0 + first part of step 3: `ps5bp` Python package** (`ps5bp/`, README there): `info`, `unwrap`, `sign`, `sdk`, `report`, `stub` (GOT and optional symbol redefinition). 7 unittest regression tests against the GT7 samples (`tests/test_gt7.py`) all pass; stubbing NOT_BACKPORT's eboot reproduces the GT7 backport's behaviour.
- [x] **Whole-game command** `python -m ps5bp backport GAME OUT --firmware N [--fakelib DIR] [--stub-nid …] [--stub-unknown]`. It writes only changed/added files (a ShadowMountPlus `backports/<TITLE_ID>/` overlay) plus `backport_report.txt/.json`. Verified on NOT_BACKPORT → FW 4: `libSieRdNNabla`, `logiWheel`, `libSceNpCppWebApi`, `libScePfs` and `right` come out **identical to the hand-made GT7 backport**; `eboot` matches in behaviour; fakelibs are copied verbatim. 9 tests pass in about 12 s.
- [x] **libc patterns automated** via `ps5bp/fixes.py`, a table of proven fixes keyed by imported NID: stub / swap / adapter / redirect, each with its source. `backport` applies them to missing imports by default, so the GT7 FW 4 backport is reproduced **without `--stub-nid`**. Verified: libc's string table is byte-identical to GT7's, the adapters use GT7's register transforms through each import's own GOT slot (lazy binding intact), `qBS714-Jr3g` redirects to libc's own `__cxa_atexit`, and the remaining unknown imports equal the five GT7 left alone. 10 tests pass. The adapters sit in int3 caves reached from the PLT entry, not in adjacent rewritten PLT entries, so they work in any module.
- [x] **Generalised with a second game (CONTROL Resonant, Part 7).**
  - `fixes.py` holds per-library-function knowledge from both games, with `sources` per entry.
  - The classifier recognises in-PLT stubs (new `patched` status for cave/redirect routes).
  - New stub bodies: `flag0` and the error return `err_http2`.
  - `--firmware 4.03` (minor version) and `--patch-param` (lowers `param.json`, as Control did).
  - **Argument fixes at the import boundary** (`when='always'`): `sceSaveDataMount3` `systemBlocks -1 → 0` (both games) and `sceSaveDataPrepare` `prepareMode 2 → 1` (Control). They replace per-game call-site patches with one rule per library function.
  - Code is branch-free and split across chained int3 caves, because eboots only have ≤16-byte padding (verified by walking the chain: PLT → 4 caves → own GOT).
  - 15 tests pass (`tests/test_gt7.py`, `tests/test_control.py`).
- [x] **References used as the knowledge base (Part 8):** generic audio fix (post-call adapter on `sceAudioOut2GetSpeakerInfo`), generic `SyncOnAddressWait` corrected to Wait32's translation, Wait8/Wait16 identified. 16 tests pass.
- [x] AnyPS5 silent-stub **candidates** generated (`tools/gen_candidates.py` → `ps5bp/candidates.py`), opt-in via `--candidate-fixes`, suggested in the report otherwise (Part 8). 18 tests pass.
- [x] Per-firmware fakelib store, as in Backpork Kitchen but with verification (Part 9). 22 tests pass.
- [x] Phase 0 hardening and Phase 2 firmware export database (`firmware_db/`, exact vs lower-bound classification, `missing` status, introduced-in annotation); details in [PLAN.md](PLAN.md). Git initialised (code and docs only). 32 tests pass.
- [ ] Next: console-test the derived fixes (argument fixes, audio, generic Wait, candidates); optionally "precise" `_umtx_op` timeouts.
- [ ] Step 2: real FW 4.xx export lists. **No public source found** (Part 6); 3.20 is the lower bound for now
- [ ] Everything else

---

## Part 6: Firmware export lists (step 2), research results

**Goal:** the list of exported NIDs per system library for **FW 4.xx**, used to decide which imports are missing on the target.

**Result (2026-10-08): no public 4.xx export list was found.** Searched: GitHub repos and code search (genstub output, "ps5 4.03/4.51 libs"), the DNNDHH account (only `PS5-3.20_Libs` and a 3.20 core dump), BestPig/BackPork, ps5rs, and the PS4/PSP libdoc projects.

| Source | What it has | Usable for 4.xx? |
|---|---|---|
| `PS5-3.20_Libs` (DNNDHH, already in `reference/`) | Every 3.20 export (31,275 NIDs, functions only) | **Lower bound.** Functions are rarely removed, so anything on 3.20 should exist on 4.xx. Imports newer than 3.20 stay unknown. |
| [ps5rs](https://github.com/claimore22/ps5rs) | Rust PS5 analysis tool with a NID→name catalog (~195K) and a `system_modules/*.exports.json` format | No. Export files are generated by the user from their own dumps; no per-firmware data ships with it. |
| [ps4libdoc](https://github.com/idc/ps4libdoc) / PSPLibDoc | Per-firmware, per-module export docs | PS4/PSP only. A **good format model** for our own PS5 database. |
| [BestPig/BackPork](https://github.com/BestPig/BackPork) (cloned to `reference/BackPork`) | Runtime fakelib loader (predecessor of ShadowMountPlus) plus **BPS patches for 10.01 libraries → 6xx and 7xx** | No 4.xx set. Its patches confirm the patch patterns (below). |

**Decoded BackPork patches** (`patches/6xx`, `patches/7xx`; patching 10.01 libraries):

| Library | Patch content |
|---|---|
| `libSceNpAuth`, `libSceNpAuthAuthorizedAppDialog`, `libSceSaveData.native` (both), `libScePsml` (6xx) | **SDK fields only** in the module param (`… 10 \| 38 00 00 06` = FW 6 pair; `59 10 \| 38 00 00 07` = FW 7 pair). These libraries need no code change for 6/7xx. |
| `libSceAgc` | SDK fields + `xor eax,eax; ret` stub at `0x1aa96` + a run of **GOT slot redirects** (8-byte copies around `0x2cb88`) + **NID string swaps** (18–19-byte copies around `0x4d4xx`). 6xx additionally has a `mov …; ret` constant-return stub at `0xd100`. |
| `libSceAgcDriver` | SDK fields + GOT redirects (`0x18000…`) + a NID string swap |
| `libSceFiber` (6xx only) | SDK fields + GOT redirects + NID string swaps |
| `sce_module/libc.prx` (6xx notes) | NID swap `4h6F1LLbTiw` → `IWIBBdTHit4` (the same one found in GT7's libc) |

These are the same four techniques found in GT7 (SDK fields, GOT → stub, NID swap, return-constant stub), so the patch library in Part 5 covers BackPork's work too. The lower the firmware, the more libraries need patching (6xx adds `libSceFiber` and the libc swap).

**Options for getting 4.xx data:**
1. **Use 3.20 as a lower bound** (works now). Anything missing from 3.20 is treated as "possibly missing". Risk: **over-stubbing** a function that does exist on 4.xx, replacing a working function with `return 0`. This matters if PLT binding is lazy, since a pre-set GOT value bypasses the resolver.
2. **Dump a 4.xx console** with PS5-SELF-Decrypter `-system-common-lib`; this is exact. It needs access to a 4.xx console (the user has none).
3. **Ask the community** (ShadowMountPlus/BackPork Discord) for 4.xx export lists. Lists are NID data, not Sony code.
4. **Generate the lists ourselves on any firmware we can dump**, using ps4libdoc-style per-firmware JSON, and keep them in the repo so the database grows.

**User's console: FW 13.60** (2026-10-08). According to press reports from late September 2026 (not verified against the repo), the public **Relapse** exploit (ntfargo, GitHub) gives kernel R/W on **FW 7.00–13.60**, PS5 Pro included. It is tethered (re-run after every reboot) and may hang or panic the console. **FW 14.00 is not vulnerable**, so the console must stay on 13.60 for this route.

What a 13.60 dump would add:
- The **newest export list**. Combined with 3.20, imports fall into three classes: *on 3.20* → present on 4.xx; *not on 3.20 but on 13.60* → **unknown, flag it**; *not on 13.60 either* → must come from a fakelib or the game's own modules.
- The natural **fakelib source** for backporting 13.x games, the same role 10.01 libraries play for BackPork.
- It **can't test** 4.xx compatibility, because every function exists on 13.60.

Blockers:
- `PS5-SELF-Decrypter` has offsets only up to 10.xx. 13.60 needs its 11 kernel `.data` offsets, found via the string heuristics in its README.
- kstuff-lite support for 13.60 (needed by ShadowMountPlus) is unconfirmed.

---

## Part 7: CONTROL Resonant (second backport sample)

`.original/CONTROLResonant/backport/` holds **CONTROL Resonant** (`PPSA34547`, version `01.300.300`), backported to **FW 4.03** by a different author than GT7's. Only the backport is available (no original dump to diff), but its `README.txt` (Vietnamese) documents every change and `SHA256SUMS` lists the files. Unwrapped ELFs are in `work/CONTROL_elf/`.

**Layout note (2026-10-08):** NOT_BACKPORT moved to `.original/GT7/NOT_BACKPORT/`. `.original/GT7/decrypted/` is byte-identical to it, and `.original/GT7/EUR/` has the same binaries as `GT7/USA/`.

### What the README says (translated)
- **Tested on PS5 Pro FW 10.01**, *not* on 4.03 itself: game starts, sound works, save/load works, first cutscene passes. On a Pro, the game runs in normal PS5 mode.
- `param.json`: `requiredSystemSoftwareVersion` **and** `sdkVersion` set to `0x0403000000000000`. GT7 left them at 13.60, and both work.
- SDK 4.03 on eboot and every module, re-signed as fake SELF.
- libc: kept the game's own SDK 12 libc, because the "4xx kit" libc lacks 18 functions that eboot/cohtml/Renoir need. `MapNamedFlexibleMemoryInternal → MapFlexibleMemory` with flag `0x8000 → 0`; stubbed 3 LibcInternalExt + 6 SyncOnAddress functions.
- eboot: stubbed 8 functions missing on 4.03. **`sceKernelIsTrinityMode` exists in libkernel only from FW 7.61**; an earlier build crashed with `PRX_NOT_RESOLVED_FUNCTION` at launch on old firmware.
- **Audio:** Wwise picks 7.1.4 (12 channels) when the console reports 3D audio; SDK 4.03 rejects a 12-channel port, so there is no sound. Forced 7.1 by patching a Wwise constant (VA `0x3ca31f0`, channel mask `0x63f108`).
- **Saves:** `SceSaveDataMount3 +0x18: -1 → 0` (VA `0x374e1ee`, the same fix as GT7) and `SceSaveDataPrepareParam.prepareMode 2 → 1` (VA `0x374c732`).
- **Crash in the first cutscene** ("Unable to reserve direct memory"): the `ampr_emu` build that shipped with the dump (6 Sep) misread data, so the game asked for about 9 GB. Fixed by using the 24 Aug build. Install note: delete `ampr_emu.index` (and `.tmp`) from app0; the emulator rebuilds it.
- nakama-sdk: 4 `sceHttp2WebSocket*` functions return an error, so online play is off.
- Fakelibs: Agc, AgcDriver, Psml from a "4xx kit" (Sony build job `J03718109`; GT7's are `J03828141`). Ampr = `ampr_emu`, PlayGo = from the dump.

### New findings from analysing it
- **A sixth stubbing technique: stub code written into the PLT entry itself**, over the `jmp [GOT]`. No cave is needed, since a 16-byte entry holds bodies up to 16 bytes. Every Control stub uses it: `xor eax,eax; ret`; `GetOperationMode` → `mov [rdi],2; mov [rsi],0; xor eax,eax; ret` (**the same values as GT7**); `GetGameTrialsFlag` → `mov [rdi],0; xor eax,eax; ret` (not a trial = full game); `sceHttp2WebSocket*` → `mov eax,0x80431001; ret` (**an error return**, so callers take their "unavailable" path).
- **SDK values encode the minor version:** PS5 SDK `0xMMmm00pp` in BCD. 4.03 is `0x04030031`; Backpork's "4" is `0x04000031`. The `param.json` value is `0xMMmm000000000000`.
- **SyncOnAddress:** GT7 adapts these to `_umtx_op`; Control stubs them to return 0 (waits return at once, so callers spin). Both run.
- **MapFlexibleMemory flag `0x8000`:** GT7's backported libc still passes it and runs; Control clears it. So clearing it isn't required, and the tool doesn't do it.
- **Argument-level patches are library problems, not game problems.** Both games patched `sceSaveDataMount3` arguments inside their own code; the fix belongs at the import boundary (see Part 5, argument fixes). The Wwise audio patch is the only truly engine-specific one. Its generic counterpart would be to make the 3D-audio capability query, or the audio-port open, refuse 12 channels; that is still to be analysed.
- Control's eboot imports far more from shipped modules (cohtml, Renoir, nakama, MediaDecoders) than GT7's; the provider logic handles this.
- `libcohtml` has 2 stubbed imports from an unlisted library id (`#4`; `aajWxENMjH0`, `HMXO7jN3FjM`). Not yet examined.

### Open questions
- ~~Which API reports 3D-audio support in Control?~~ Answered: `sceAudioOut2GetSpeakerInfo` (flags bit 0, available_bits); generic post-call fix in Part 8.
- Is `prepareMode 2 → 1` needed by GT7 too (GT7 also imports `sceSaveDataPrepare`)? The generic fix only acts on the value 2.
- Control was tested on FW 10.01. Does it run on a real 4.03 console?

---

## Part 8: Using the references as the knowledge base (generic, not per game)

GT7 and CONTROL are the **ground truth** (console-tested patched files). The references explain *why* those patches work and let each fix be stated once per **library function**, so it applies to every game. Every fix in `ps5bp/fixes.py` records its sources.

### What each reference contributes
| Reference | Used for |
|---|---|
| **AnyPS5** `core/libs/prx/*` (HLE reimplementations) | Signatures and struct layouts behind each import, and what a "harmless" return value is. Cross-check of `fixes.py` (2026-10-08): IsTrinityMode → 0, VRR peg/unpeg/status → 0 and `sceAudioOut2Set3DLatency` → 0 all agree with our stubs. AnyPS5's `GetOperationMode` returns mode 0 (both games used 2; any value ≠ 3 passes the games' check). `docs/dev/TechnicalDebt.md` lists functions that games survive as no-ops, which are candidates for future stub rules (not applied automatically, since not console-proven). |
| **backport-helper** | The shim/hook alternative for argument fixes. Its example hooks are exactly `sceKernelGetOperationMode` (0) and `sceSaveDataMount3`, the same functions both games needed. |
| **ampr_emu / pgo_stub** | Source of the AMPR/PlayGo fakelibs; needed only when running from a dump (ShadowMount). The Control README says the 6 Sep `ampr_emu` build broke Control (about 9 GB direct-memory request) and the 24 Aug build works: the fakelib *version* matters. |
| **BackPork / ShadowMountPlus** | How fakelibs are loaded (union-mount over sandbox `common/lib`), plus BPS patches showing the same techniques for 6xx/7xx. |
| **Backpork Kitchen** | SDK pairs per firmware major and the 6xx libc NID swap. It does nothing about missing imports. |
| **PS5-3.20_Libs** | Lower-bound export list ("present on 4.xx"). It confirmed that **every AudioOut2 function Control uses exists on 3.20** except `Set3DLatency`, so the audio problem is an argument problem, not a missing function. |
| **sce_symbols(_new)** | NID → name. It lacks some names; AnyPS5 plus `compute_nid` filled them (below). |

### Facts confirmed through the references
- **`dFym5b41JLU` = `sceKernelSyncOnAddressWait8`, `XgVs4jRehcY` = `sceKernelSyncOnAddressWait16`** (names from AnyPS5, NIDs computed; not in aerolib). Control's "6 SyncOnAddress" stubs are therefore Wait, Wait8, Wait16, Wait32, Wait64 and Wake.
- **SyncOnAddress signatures** (AnyPS5): `Wait/Wait8/16/32/64(T* addr, T expected, const KernelUseconds* timeout /*u32 µs, NULL = forever*/, const char* name)`; `Wake(void* addr, int32 count)`. This matches what we reconstructed from GT7's callers. **The generic `Wait` has Wait32's signature**, so `ps5bp` now gives it Wait32's (console-proven) `_umtx_op` translation; GT7's own patch passed `expected` as the op code and spun. Wait8/16 stay return-0 stubs, as in Control: FreeBSD `_umtx_op` has no 8/16-bit compare.
- **Audio, made generic.**
  - `sceAudioOut2GetSpeakerInfo(SpeakerInfo* info, u32 flags)`. `SpeakerInfo` (AnyPS5): `+0x00 type u8`, `+0x04 available_bits u32`, `+0x08 flags u32`, `+0x10 angles[16]`.
  - Control's eboot (`0x3ca27f1` and `0x3ca42fa`) takes `flags & 1` as "3D audio" and `popcnt(available_bits) > 8` as "has height speakers". Wwise then chooses 7.1.4 (12 channels).
  - AudioOut2 `data_format`: channel count in bits 8–11 (AnyPS5).
  - **Root cause:** a newer OS (Control was tested on 10.01) reports 3D/7.1.4, but a port opened by an SDK-4.03 binary is refused at 12 channels. This is an OS-vs-SDK mismatch, so it can hit any game whose engine reads `GetSpeakerInfo`.
  - **Generic fix:** a post-call adapter on `sceAudioOut2GetSpeakerInfo`. It runs the real function, then clears `flags` bit 0 and masks `available_bits` to 8 speakers, so the game itself chooses 7.1.
  - Lowering the channel count at `PortCreate` instead would be wrong: the game would still push 12-channel buffers.
  - Status: **derived, not console-tested**. Control fixed it inside Wwise instead.

### Stub candidates from AnyPS5's TechnicalDebt "Silent stubs" (2026-10-08)
- `tools/gen_candidates.py` reads the silent-stubs section.
  - If a bullet names specific functions, only those are taken; otherwise every function in that library file.
  - A function is kept only if its body is exactly `return <constant>;` (ignoring `(void)arg;`), with named constants resolved from AnyPS5's headers. Anything that throws, calls `NotImplemented…`, or does real work is excluded.
  - The result is written to the generated **`ps5bp/candidates.py`** (26 functions). All NIDs were verified against both aerolib and `compute_nid`.
- **Finding: 25 of the 26 already exist on 3.20.** AnyPS5's silent stubs mostly cover functions that old firmware *has*; that is an emulator concern, not a backport one. Since candidates only apply to imports missing on the target, they rarely fire.
- **The one that matters: `sceTextToSpeech2Initialize` → `0x8002002D` (`SCE_KERNEL_ERROR_EOPNOTSUPP`).** It is missing on 3.20, GT7 imports it, and AnyPS5 documents that GT7 (PPSA01317) skips narration when it fails. `sceHmd2Initialize` → `0x81110016` (unsupported feature) is also a candidate, but it exists on 3.20, so it doesn't fire.
- Tool:
  - `--candidate-fixes` applies candidates, opt-in, because they are reference-derived and not console-proven. They are written as `mov eax, imm32; ret` (stub name `ret:<value>`).
  - Without the flag, the report lists the candidate next to each unknown import that has one.
- Open question: GT7's backport left `libSceTextToSpeech2` unpatched and runs. Either TTS2 exists on 4.xx (added after 3.20), or the call is gated. A real 4.xx export list would decide it.

### New tool capability
`patch.place_chain` supports **post-call adapters**: a `call [GOT]` in the middle of the chain (with `push rdi` for 16-byte stack alignment) and a final `ret`. The tests walk both chain types from the PLT entry (`sceSaveDataMount3` pre-call, `sceAudioOut2GetSpeakerInfo` post-call).

---

## Part 9: How Backpork Kitchen's "general" backport actually works (deep dive)

Source: `reference/PS5-BACKPORK-KITCHEN/PS5 BACKPORK KITCHEN/`. The path traced is what **"Start Cooking"** runs today:
`Form1.BtnStart_Click` → `CompositionRoot.CreatePatchingCoordinator` → `PatchingCoordinator.ExecutePatchingAsync` → `ElfPatchingService.PatchFileAsync` for each file → copy fakelibs.

### The pipeline
1. **Back up** the game folder (`Backupfiles`), then delete any old `<game>/fakelib/`.
2. **Collect files:** every `*.bin`, `*.prx` and `*.sprx` under the game folder (recursively, so not just `eboot.bin`).
3. **For each file** (`ElfPatchingService.PatchFileAsync`):
   1. A file counts as "decrypted" when it starts with the ELF magic. Otherwise it is unwrapped with the internal **SelfUtil port**. This only handles **fake/plaintext SELFs**; truly encrypted files fail ("Decrypt failed").
   2. `ElfInspector.ReadInfo` → must be patchable, i.e. have a `PT_SCE_PROCPARAM` / `PT_SCE_MODULE_PARAM`.
   3. Skip if the current PS5 SDK equals the target, or is already ≤ the target (`ShouldPatch` = `current > target`).
   4. `ElfPatcher.PatchFile`: overwrite the PS5 SDK (`param+0x14`) and the PS4 SDK (`param+0x10`).
   5. Optional "6xx" checkbox: for `libc.prx` only, the string swap `4h6F1LLbTiw#A#B → IWIBBdTHit4#A#B` (MapNamedFlexibleMemoryInternal → MapFlexibleMemory).
   6. **Fake-sign** (FreeFakeSign, a `make_fself.py` port) and **overwrite the original file** in place.
4. **Copy fakelibs:** `<exe dir>/<target FW>/fakelib/*` → `<game>/fakelib/`.

### Where the fakelibs come from
- `fakelibs.json` (downloaded from the repo; URLs base64-encoded) defines exactly **two sets**, and they are **BestPig/BackPork's patched 10.01 libraries**:
  - **FW 7 (recommended):** libSceAgc, libSceAgcDriver, libSceNpAuth, libSceNpAuthAuthorizedAppDialog, libSceSaveData.native, plus `ps5-backpork.elf`. This is BackPork `patches/7xx`.
  - **FW 6:** libSceAgc, libSceAgcDriver, libSceFiber. This is part of BackPork `patches/6xx`.
- `FirmwareExtractorModule` can unpack a PUP (`pup_unpacker.exe`), but decryption needs the **on-console payload `ps5_pup_decrypt.elf`**. The extracted `.sprx` files go to `<FW>/fakelib` **unpatched**.
- `LibraryProcessorModule` only catalogues fakelibs: size, magic, SHA-256 and a manifest. **It never patches them.**

### What it does *not* do
- **No import analysis.** Nothing checks which functions the game (eboot, `sce_module/*`, its own prx files) imports that the target firmware lacks. `SdkDetector` only computes SDK statistics.
- **No eboot or game-module fixes.** No stubs, no argument fixes. GT7 needed 6 eboot stubs and a savedata fix; Control needed 8 stubs, 2 savedata fixes and an audio fix. Kitchen can't produce either.
- **No fakelib patching.** It relies on fakelibs patched elsewhere (BackPork's BPS patches).
- No `param.json` change; output overwrites the dump in place (after a backup).

### Bugs and limits found
- **PS4 SDK is written as 0** on the current path. The coordinator only carries the PS5 SDK; `ElfPatchingService` passes `0UI` (commented *"Not used for now"*), and `PatchFile` writes it unconditionally. `Ps4SdkResolver` (PS5 → PS4 for FW 5–10, fallback `0x09590001`) exists but is unused. GT7 and Control both keep a real PS4 SDK (`0x09040001`), so **neither was made with Kitchen's current build**. Whether 0 matters on a console is unknown.
- `*.bin` collects every `.bin` file in the dump, not just `eboot.bin`. Non-ELF files fail and are counted as errors.
- The target is the firmware *major* only (SDK table 1–10); no minor version (Control needs 4.03).
- In practice only FW 6/7 are usable, because only those fakelib sets exist.

### What "general" means here
Kitchen is general **only for games whose missing functions all live in the libraries BackPork patched**, i.e. games built with SDKs up to about 10.x whose only problems are AGC/NpAuth/SaveData/Fiber. That is the original BackPork method, automated:
**SDK patch + fake-sign + BackPork's patched 10.01 fakelibs (+ the libc 6xx swap).**

Newer games (GT7 = SDK 13 package, Control = SDK 12 libc) also import functions missing in **their own code** (eboot, libc, nakama…). Kitchen has no answer for those. That is the gap `ps5bp` fills with import classification plus the per-library-function fix table.

### Kitchen vs ps5bp
| Capability | Kitchen | ps5bp |
|---|---|---|
| Fake-SELF unwrap / fake-sign | yes | yes (round-trip byte-exact) |
| SDK patch | PS5 only; PS4 written as 0; major only | PS5 + PS4 pair; minor versions (`4.03`) |
| libc 6xx NID swap | optional checkbox | automatic when the import is missing |
| Fakelibs | per-target-FW sets (BackPork 6xx/7xx), copied | `--fakelib DIR`, copied or patched like any module |
| Import analysis (missing on target) | no | yes (reference + providers) |
| Stubs / adapters / argument fixes | no | yes (`fixes.py`, `candidates.py`) |
| `param.json` | no | `--patch-param` |
| Output | overwrites dump (after backup) | overlay of changed files (ShadowMountPlus `backports/<TITLE_ID>`) |
| Firmware PUP extraction | yes (needs console payload) | no |
| GUI, FTP, PKG/UFS2 builders, payload sender | yes | no |

**Adopted (2026-10-08):** `ps5bp` now has the per-firmware fakelib store (`fakelibs/<FW>/` + `manifest.json`, `python -m ps5bp fakelib add|list|verify`), seeded with GT7's FW 4 set and Control's FW 4.03 set (Control's checksums match its own `SHA256SUMS`). `backport` uses it automatically.

**Worth adopting from Kitchen:** a per-target-firmware fakelib store (`<FW>/fakelib`) so `backport` picks fakelibs automatically from the target firmware, plus its library manifest (checksums). Kitchen's PUP extraction depends on an on-console decrypt payload and isn't needed for the tool itself.

---

## Part 10: Fakelib supply findings (Phase 3, 2026-10-08)

### BackPork's BPS patches, decoded without the 10.01 libraries
`python -m ps5bp bps info` lists every literal write in a patch, so we can see what BackPork changes before having the source libraries.
- **Source = FW 10.01 libraries.** Each patch records the source size and CRC32. `libSceNpAuth`, `libSceNpAuthAuthorizedAppDialog` and `libSceSaveData.native` have the **same source CRC32 in 6xx and 7xx**; Agc and AgcDriver differ (two different 10.01 builds, or the 7xx set was made from another dump).
- **SDK patch.** `libSceNpAuth` and `libSceNpAuthAuthorizedAppDialog` are **only** an SDK patch: 6xx writes `10 38 00 00 06` and 7xx writes `59 10 38 00 00 07` at the module param. Those are the tail bytes of PS4 SDK + PS5 SDK = `0x10090001/0x06000038` (6) and `0x10590001/0x07000038` (7), matching `sdk.FIRMWARE_SDK` exactly (10.01's own pair is `0x12090001/0x10000040`). So `ps5bp`'s SDK patch already reproduces these two patches. The same SDK write (or its differing bytes only, e.g. `10 38`) also appears in Fiber, Psml, SaveData.native and 7xx Agc/AgcDriver; 6xx Agc writes only `09` there and copies the rest from elsewhere in the file.
- **Local symbol redefinition** (technique 3, as in GT7's fakelibs): writes of `01 00` repeated at a **24-byte stride** (Agc around `0x4d40e`, Fiber around `0x1044e`) are `.dynsym` entries getting `st_shndx = 1`, with `st_value` copied from another entry. Single `01` writes (Agc `0x4d22e`, AgcDriver `0x2809e`/`0x29466`, 6xx SaveData `0x4d096`) are probably the same change on one symbol. Imports become local definitions pointing at a stub.
- **Stub code:** Agc `0x1aa96` = `31 c0 c3 90…` (`xor eax,eax; ret` + nops, return 0, both sets). 6xx Agc also has `0xd100` = `48 c7 ?? ?? ?? ?? ?? c3` (`mov r64, imm32; ret`, return a constant), which is 6xx only.
- The other Agc/AgcDriver/Fiber edits (`0x2cb88…`, `0x18000…`) are runs of 8- and 16-byte copies at regular strides, probably relocation/GOT entries redirected to the stub. Naming them needs the 10.01 source library (Phase 3.3).
- **Conclusion:** BackPork used the same three techniques `ps5bp` has (SDK patch, local symbol stub, ret0 stub), so BPS application is mainly for reproducing their console-tested sets byte-for-byte. Once we have the 10.01 libraries, running `ps5bp` on them and diffing against the BPS output will check our generic rules.

### The console-tested fakelib sets leave four imports unresolved
`python -m ps5bp fakelib check --firmware 4.03` (and `4`) against 3.20 shows imports neither backport author touched:
| NID | Function | In |
|---|---|---|
| `GGeRJk1XdWc` | `sceKernelGetProsperoCompiledSdkVersion` | Agc, Psml |
| `dkBx0YqFQ+Y` | `sceKernelGetToolMemoryRange` | Agc, Psml |
| `LzoM-wVLJDE` | `sceKernelIsThreadSanitizerEnabled` | AgcDriver |
| `Ygv0S+Hi+hA` | `sceVideoOutGetPipelineStatus` | Psml |

Both GT7 (FW 4 set) and Control (4.03 set) ship them unpatched and both run. So each one is either **exported on 4.xx** (added between 3.20 and 4.00) or **never called** (lazy binding). They are not in PS5-3.20_Libs, but they are in ps5-payload-sdk's `sce_stubs` (which records no firmware, so it can't be a firmware_db entry). Treat them as "tolerated on FW 4" until a 4.xx export list settles it.

### ampr_emu builds are identified by their compile date
- Control's `libSceAmpr` (24 Aug build, the one its README says works) embeds the compiler `__DATE__` **`Aug 24 2026`** plus a `[AMPR_EMU] name=libSceAmpr version=%s build=%s %s` log line. GT7's older ampr_emu has neither, and PlayGo has no date.
- The store manifest now records `build_date`; `store.KNOWN_BUILDS` marks `Aug 24 2026` ok and `Sep  6 2026` broken (Control README: "the 6/9 build reads dump data wrong, the game asks for ~9 GB"; the year is inferred).
- Control's README also says that backport was **tested on FW 10.01**, not on a 4.xx console.
- **`param.json` line endings:** all three sample `param.json` files are CRLF, including the one Control lowered and console-tested. `ps5bp --patch-param` used to write the lowered copy with LF (Python text mode); since 2026-10-08 it keeps the original line endings, so only the version strings change (`tests/test_phase4.py`).

---

## Part 11: Triage of unresolved imports and the pre-existing patches (Phase 4, 2026-10-08)

### Code references per unresolved import
`imports.code_references` counts instructions in executable segments that call, jump to or load an import's PLT entry or GOT slot (`call/jmp rel32`, `call/jmp [rip+x]`, `mov/lea reg,[rip+x]`; byte-pattern scan, 2 s on GT7's 110 MB eboot). The backport report shows `refs=N` for every unresolved import.
- `refs=0`: no direct reference. Reachable only through data (vtables, pointer tables), or not at all. In GT7: `sceTextToSpeech2Cancel`.
- Most of GT7's PSVR2 (`sceHmd2*`, `sceVrTracker2*`), TTS2, Share and privacy imports have **exactly 1** reference: a thin wrapper. Whether that wrapper runs needs Ghidra (who calls it, and is it behind a PSVR2/TTS feature check?).

### GT7 keeps every startup module, even ones 3.20 lacks
GT7's eboot lists `libSceTextToSpeech2.prx`, `libSceHmd2.prx`, `libSceShare.prx`, `libSceVrTracker2.prx` etc. as `DT_NEEDED`, and the FW 4 backport kept the list unchanged. 3.20 has no `libSceTextToSpeech2` module at all (only `libSceTextToSpeech`, 11 functions). So either 4.xx has it, or a missing needed module doesn't stop the game from loading. Which one needs a 4.xx export list or a test.

### Evidence table: imports a console-tested backport left unpatched
`fixes.LEFT_UNPATCHED` (40 NIDs) holds the imports still unresolved in the shipped backports, with each game's own modules as providers:
- **Both GT7 and Control (5):** `sceKernelDebugWriteCppExceptionInfo` (libc), and in the fakelibs `sceKernelGetProsperoCompiledSdkVersion`, `sceKernelGetToolMemoryRange`, `sceKernelIsThreadSanitizerEnabled`, `sceVideoOutGetPipelineStatus`.
- **GT7 only (35):** the eboot's PSVR2, TTS2, Share and `D-CzAxQL0XI` (UserServicePlatformPrivacyWs1) imports, plus 5 libc imports that Control stubbed (Wait8, Wait16, `OQ-dzhlnM28`, `tB59hFLH3SA`, and `qBS714-Jr3g` whose only call GT7 retargeted).
- Control's game code has **no** unresolved imports left; it stubbed everything.

The report adds `left unpatched by: <backport>`. This is evidence, not a fix: the import exists on the test firmware or isn't called there. Caveat: Control was tested on **10.01**, and GT7's test firmware isn't recorded. `tests/test_phase4.py` recomputes the table from the samples.

### Ghidra: are GT7's left-unpatched eboot imports reachable? (2026-10-08)
- **PSVR2 (`sceHmd2*`, `sceVrTracker2*`) is gated by a function that exists on old firmware.** `0x942d40`: `sceSysmoduleLoadModule(0x12a)` (result ignored), then `sceHmd2Initialize` (`c812oYs7Vsc`, exported on 3.20). If it returns non-zero, the code jumps past everything: `GetDeviceInformation`, `GetFieldOfViewWithoutHandle`, `VrTracker2Initialize`, and the flag at `0x631a614` that the single writer at `0x943119` sets to 1. Every other PSVR2 call site (`sceHmd2Open` at `0x1a41067`, etc.) checks that flag first. So with no PSVR2 support, none of the missing PSVR2 functions run. Ghidra's auto-analysis had not even created functions there; they are reached through vtables.
- **Text-to-speech 2 is gated by its own missing init function.** `FUN_00bd3970` calls `sceTextToSpeech2Initialize` first; only on 0 does it call `Open` and the rest. The routine is reached through a callback table built by a static initializer (`0x18097f8`, `0x180956f`), so when it runs depends on runtime state (probably the system screen-reader setting). The AnyPS5 candidate stub (`return 0x8002002D`) makes `Initialize` fail, so the routine returns cleanly. Kept as an opt-in candidate (`--candidate-fixes`), because GT7's tested backport didn't stub it.
- *(Corrected in Part 13: the exact 4.00 export list contains `D-CzAxQL0XI`, so the conclusion below about GT7's test firmware is withdrawn.)*
- **`D-CzAxQL0XI` (libSceUserServicePlatformPrivacyWs1) is called unconditionally at startup.** `FUN_00bd2e10` registers each signed-in user (map lookup, user name, trophies) and calls `FUN_00bd3dc0`, which always calls `D-CzAxQL0XI(userId, &out)`. GT7 shipped this unpatched and ran, so **GT7's test firmware exports it**. 3.20 has no such library. So GT7 was probably tested above 4.xx, and its "left unpatched" list is evidence for that firmware, not for 4.xx. A real 4.xx target may need a stub here; its meaning (`out == 1` combined with another flag into an argument of `FUN_00586ea0`) is unknown, so no rule yet.
- **Generic lesson (gate pattern):** a feature library's missing functions are harmless when an init call that **exists** on the target fails first (Hmd2). When the init function is itself missing (TTS2), an error stub on it alone disables the feature. Prefer stubbing the gate with an error over stubbing every function with 0.
- `sceShareGetRunningStatus` (`0x25f09a`) sits in code Ghidra did not analyse (reached by a vtable call); not traced further.

### NOT_BACKPORT's pre-existing patches are GT7-specific (PLAN 4.5: ruled out as general rules)
1. **`mount_mode` at `0x834048`** (inside the save-mount routine `FUN_00833f60`, which also calls `sceSaveDataMount3` and then `sceSaveDataPrepare` unless mode 3). Original: `ecx = setnc(param_2 - 2 >= 3); mount_mode = ecx + 1`, i.e. **RDWR (2)** for most save operations and **RDONLY (1)** for modes 2–4. The patch `xor ecx,ecx; nop` makes **every** mount read-only, so GT7's saves can never be written through this path. It changes game logic in one function, so it isn't a library-boundary rule.
2. **`_Mtx_lock`/`_Mtx_unlock` guards** (skip mutex pointers with bit 47+ set). No Control module has a guard or any other `patched` PLT entry, and Control also runs from a dump with ampr_emu. So it isn't a general dump requirement; it works around something in GT7.

Neither becomes a `fixes.py` rule. Who made them and why is still open (they were already in the dump the user has).

### Code space and shim PRX (PLAN 4.3 / 4.4)
- **Page gap:** PS5 modules end the code segment mid-page and start the next segment on a 16 KB page. The gap is zero in the file and unmapped-but-in-page in memory: GT7 eboot `0x18d4`, GT7 libc `0x812`, Control eboot `0x2084`, nakama `0x1c62`; page-exact modules (GT7 fakelib Psml) have none. `patch.extend_text` grows the segment's `p_filesz`/`p_memsz` into it (`--extend-text`). Console test pending. The new bytes are 64-byte blocks of `ret` + 63 `int3`: a cave must follow a `ret`, and placed code ends with a `jmp`, so one long run would hold only one fix (found while porting to C#; all 10 NOT_BACKPORT libc fixes now fit).
- **No open toolchain for SCE PRX:** `backport-helper` (a replacement `libSceRtc` with runtime hooks and raw-NID exports via a post-link NID rename and `.hash` rebuild) and ampr_emu (`C:/dev/ps5/ampr_emu/out/Prospero_ReleaseHooks`) are built with Sony's Prospero SDK. ps5-payload-sdk builds `.so` files for its own payload loader only. A shim PRX for `ps5bp` would need a `.so` → SCE PRX converter.

---

## Part 12: 13.60 console bench facts (2026-10-08)
Details and sources in [CONSOLE_BENCH.md](CONSOLE_BENCH.md).
- **Chain:** Relapse (browser, 7.00–13.60, tethered) → ELF loader on port 9021 → kstuff-lite **v1.11** (release notes: FW 1.00–13.60) → ShadowMountPlus **1.7beta3+** ("up to 13.60", needs kstuff-lite ≥ 1.07).
- **ShadowMountPlus 1.7 backports:** applied from `<scan_path>/backports/<TITLE_ID>` (default scan path `/data/homebrew`) **just before the game process starts**. Files are redirected, missing directories are NSFS overlays, **max 256 redirects per sandbox** (a large overlay could hit this). 1.7beta4 adds per-game fakelib toggles, `fakelib_exclude`, and auto-removal of incompatible libraries from FPKG backport fakelib folders.
- **Standalone BackPork + ShadowMountPlus conflict** (both mount fakelib); don't run both.
- **kstuff auto-pause:** ShadowMountPlus pauses kstuff 15 s (direct) / 25 s (image) after a tracked game starts. Fake-signed modules loaded after that may fail; `kstuff_no_pause=<TITLE_ID>` turns this off per game.
- **Logs:** `/data/shadowmount/debug.log` (+ `.1`), `/data/shadowmount/notify.txt`, the ShadowMountPlus API `/api/v1/debug-log` (port 10101, loopback), the Relapse host's `klosrv`/`ftpsrv` options, and Control's ampr_emu `/app0/ampr_emu.log`.
- **ampr auto-update** (`auto_update_ampr`, off by default) would replace our manifest-tracked ampr_emu build; keep it off while testing.
- Known bug: folder games can fail to relaunch on 13.60 with `CE-105773-3` (ShadowMountPlus issue #128, open).

---

## Part 13: Firmware dumps for 46 firmwares, exact export lists, new samples (2026-10-09)

### What arrived
- `.original/.PS5OriginalLibs/Firmware X.YY/`: system library dumps for **46 firmwares, 1.00 to 13.60**. Decrypted ELFs except **4.30, 7.00, 13.60** (encrypted SELFs) and half of 11.20 (the other half is decrypted). 7.00 also has an unreadable SELF (malformed trailing PT_SCE_VERSION).
- `.original/backports/<Game>/{default,4.00+,decrypted}`: GT7 (default = old NOT_BACKPORT), **Control's original dump** (`default`, new), and a new game, **Ghost of Yōtei Complete Edition** (`default` + `4.00+ NOT TESTED`).

### Exact export lists (PLAN 2.3)
- `python tools/import_firmware_dumps.py` imports every dump's `*/common/lib` (what games load; `system_1`/`system_2` duplicates merged) into `firmware_db/<fw>.json`. A firmware is imported only if every module has a decrypted copy, so 4.30, 7.00 and 13.60 are skipped. **43 exact lists**, 300–400 KB each (16 MB together), data symbols included.
- Managed **Mono/.NET assemblies** (`*.dll.sprx`, `*.exe.sprx`: mscorlib, System*, I18N…) sit in some dumps' common/lib (186 in 3.20) and carry ~250k NIDs. Games don't import them; both engines' `import_dump` skip them.
- `firmware_db/3.20.json` now comes from the real dump. The old PS5-3.20_Libs genstub list is contained in it except `mscorlib.dll`'s 375 NIDs. **The genstub list was incomplete**: it has no `libSceTextToSpeech2` at all, which is why TTS2 looked missing on old firmware.
- **4.00 and 4.03 export the same 17,901 NIDs.** For a FW 4 target the report is now exact.

### What the exact 4.00 list settles
- **Every fix we apply for a missing function targets a function that really is missing on 4.00** (all 33 stub/swap/adapter/redirect fixes). The three argument fixes (`sceSaveDataMount3`, `sceSaveDataPrepare`, `sceAudioOut2GetSpeakerInfo`) target functions that exist on 4.00, as intended.
- Of the 40 imports the tested backports shipped unpatched, **33 exist on 4.00** (TTS2, PSVR2 except two, Share, `D-CzAxQL0XI`, the four kernel/VideoOut queries in the fakelibs, `sceKernelDebugWriteCppExceptionInfo`). Missing on 4.00: `sceHmd2ReprojectionSetAllowPositionalReprojection` and `sceHmd2ReprojectionSetParamWithBuffer` (behind the `sceHmd2Initialize` check, Part 11), and the five libc imports Control stubbed (Wait8, Wait16, `OQ-dzhlnM28`, `tB59hFLH3SA`, `qBS714-Jr3g`).
- **Correction to Part 11:** `D-CzAxQL0XI` exists on 4.00, so its unconditional call says nothing about GT7's test firmware. The conclusion "GT7 was probably tested above 4.xx" is withdrawn.
- GT7 backported to FW 4 with the exact list: the same 19 fixes; **only 2 imports stay unresolved** (the two PSVR2 functions, status `missing`), instead of 37 `unknown` against the 3.20 genstub list.
- Control's 4.03 fakelib set: **every import resolved** against the exact 4.03 list (the four "unresolved" kernel/VideoOut queries of Part 10 exist on 4.03).

### When fixed functions appear (first decrypted dump exporting them)
| Function | First firmware |
|---|---|
| `sceSaveDataMount3`, `sceSaveDataPrepare`, `sceAudioOut2GetSpeakerInfo`, TTS2 | 1.00 |
| `sceVideoOutVrr*`, `sceHttp2WebSocket*` | 5.02 |
| `sceKernelSyncOnAddressWake`/`Wait`, `sceAudioOut2Set3DLatency`, `sceSaveDataCancel`, `sceNpEntitlementAccessGetGameTrialsFlag` | 6.00 |
| `sceKernelIsTrinityMode`/`HasTrinityMode`, `sceKernelMapNamedFlexibleMemoryInternal`, `sceVideoOutLatency*` | 7.01 |
| `sceKernelSyncOnAddressWait32`/`Wait64` | 9.00 |
| `sceAcmBatchJobPriority` | 10.01 |
| `sceKernelGetOperationMode`, libSceLibcInternalExt `OQ-dzhlnM28`/`tB59hFLH3SA`/`qBS714-Jr3g` | 11.00 |
| `sceKernelSyncOnAddressWait8`/`Wait16` | 12.00 |
| libkernel `La9uyZv4Kvw` | 12.40 |
| `sceShaderTranscode*` (5 functions) | **never**: no firmware exports them; only the fakelibs' own stubs satisfy them |

`sceKernelIsTrinityMode` is in 7.01 (6.50 lacks it; 7.00 is encrypted), not 7.61 as Control's README said; `data/introduced.json` is corrected. The report's "introduced" column now comes from the dumps.

### Functions are removed too
Consecutive firmwares drop NIDs, including game-facing ones: libSceVideoOut (1.00→2.00), libSceUserService (−27 at 6.00), libSceNpUniversalDataSystem (several releases), libwebrtc, PlayReady. So an older list is **not** a strict lower bound for a newer target. With 43 exact lists the lower bound is now rarely used (only for firmwares without a dump, e.g. 4.10 → 4.03).

### BackPork patches against the real 10.01 libraries (PLAN 3.3)
- **10 of 12 patches match the 10.01 dump** (size + CRC32) and apply with the right target CRC: all five 7xx patches and the 6xx Fiber, NpAuth, NpAuthAuthorizedAppDialog, Psml, SaveData.native.
- The **6xx libSceAgc / libSceAgcDriver** patches expect other sources (CRC `f98b1366` / `f719b950`). They are not the 7xx output and not any Agc in the 10.01–10.60 dumps. Their source is unknown, so a FW 6 set can't be built yet.
- **`fakelibs/7` built**: the five 7xx libraries patched, fake-signed (SDK 0x07000038/0x10590001), stored with provenance. Checked against the exact 7.01 and 7.61 lists, one import stays missing: `sceFsBfsGetAvailableSize` (libSceSaveData.native), which BackPork leaves unpatched too.

---

## Part 14: Audit findings (2026-10-09)

### PT_SCE_VERSION records may carry two different versions
- Each record in the trailing PT_SCE_VERSION block is `00 00`, u16 payload size, `08`, `name:`, then two 8-byte versions. Upstream `make_fself` (and our ports of it) assumed the two versions are always equal and rejected anything else as "malformed trailing PT_SCE_VERSION data".
- **Ghost of Yōtei's eboot breaks that assumption**: 2 of its 135 records (`libScePsmlMfsr2:` and `libScePsml_mfsr2_stub_weak:`, the PS5 Pro PSSR libraries) have `13 00 00 81 …` and `09 00 00 87 …`. Every structural check still holds (markers, tag, printable `name:`, sizes ending exactly at the block end), so the file is valid and the equality test was too strict.
- Fixed in both engines (`ps5bp/vendor/make_fself_batch.py` `is_complete_sceversion`, `Self.IsCompleteSceVersion`): the versions are no longer compared. Both engines now unwrap the Yōtei eboot to the same ELF; sign → unwrap returns it byte for byte. Tests: `TestSceVersionRecords` (Python), `SceVersionTests` (C#).
- Re-signing Yōtei's eboot differs from the shipped file in the digest (as for GT7) and in where the trailing block sits: the original packer put it 8 bytes before the declared file size, our signer at it. Same bytes; the unwrapper accepts both positions.
- The 7.00 dump's one unreadable SELF (Part 13) failed with the same message and may have the same cause; 7.00 stays skipped anyway (its other modules are encrypted).

### Ghost of Yōtei (PPSA26344 v02.002.000) through the pipeline
- Backports to FW 4 and FW 7 now complete; both engines give identical output (9 files) and reports.
- FW 4 (exact 4.00 list): the same 4 derived fixes as GT7 apply; **one import stays missing**: `gWT7X8H0bYs` in `libSceVideoOutVrrStatus` (eboot, refs=3), first exported by FW 10.01. GT7 needed proven stubs for two other VrrStatus NIDs (`kP2L8t3j-aM`, `LibwuIonIBw`), so a stub is the likely fix, but it needs triage (what it returns, how the 3 call sites use it) and a tester run before it goes into `data/fixes.json`.

### Tester checklist assumptions, checked against the ShadowMountPlus and ampr_emu sources
- **debug.log**: `log_debug` writes only when `debug_enabled` is set; `debug=1` is the default (`config.ini.example`, `sm_config_mount.c`). The log is rotated (`debug.log` → `debug.log.1`) when **ShadowMountPlus starts** (`main.c`), not per game launch: after a crash, reboot and re-jailbreak, the failed run's log is `debug.log.1`.
- **notify.txt is never written**: `TOAST_FILE` is defined in `sm_paths.h` but used nowhere. Removed from the checklist (CONSOLE_BENCH 1.3 listed it too).
- **Folder dumps get the backport as a unionfs layer** above `/system_ex/app/<TITLE_ID>` when ShadowMountPlus mounts the game (`mount_backport_overlay`, `sm_filesystem.c`), logged as `[IMG] backport overlay mounted (rw|ro): <backport> -> /system_ex/app/<TITLE_ID>` or `[IMG] backport overlay failed: …` plus a popup. (The NSFS file redirects and their 256 limit are the installed-package path, `sm_pkg_backport.c`.) The overlay is looked up under the game's own scan folder first, then every scan folder plus `/data/homebrew`, which is always included (`get_backport_scan_path`).
- Because the backport folder is the **upper, writable layer**, files the game creates in `/app0` (ampr_emu's `ampr_emu.index`, its log) presumably land in the backport folder. This is inferred from unionfs semantics and not console-checked. Hence the checklist: delete stale indexes in both places, and delete an old backport folder before installing a new build.
- **ampr_emu.log is compiled out by default** (`AMPR_EMU_DEBUG_LOG 0` in `ampr_emu_config.h`). Our `fakelibs/4.03` build (24 Aug) contains `/app0/ampr_emu.log`; the `fakelibs/4` build (GT7) has no log path at all, so FW 4 backports write no ampr_emu log. Index autobuild is on by default (`/app0/ampr_emu.index`).
- Config keys confirmed: `/data/shadowmount/config.ini`, `auto_update_ampr=0` default, `kstuff_no_pause=<TITLE_ID>` (repeatable). ShadowMountPlus stops a running BackPork payload itself (`main.c`).

### Gap: the FW 7 fakelib set can't run a dump on its own
`fakelibs/7` holds only the five BackPork 7xx libraries. It has **no `libSceAmpr` (ampr_emu) and no `libScePlayGo` stub**, which every dump needs under ShadowMountPlus (Part 4), and **no `libScePsml`**: a Yōtei FW 7 backport leaves 9 `libScePsml_mfsr2` imports (FW 12.00) unknown that the FW 4 set's Psml provides. A FW 7 backport would depend on whatever ampr_emu the tester's ShadowMountPlus provides (global fakelib or auto-update). **Fixed (2026-10-09):** ampr_emu (24 Aug) and the PlayGo stub copied into `fakelibs/7` (manifest source says so); both engines now warn when neither the game nor the fakelib set has `fakelib/libSceAmpr.sprx` / `libScePlayGo.sprx` (`check_dump_fakelibs`, `GameScan.DumpFakelibWarning`). Psml stays out until a tester shows it's needed (its imports matter only on PS5 Pro).

---

## Part 15: AC Black Flag Resynced and Ghost of Yōtei backport diffs (2026-10-09)

Sample folders were renamed again: `.original/backports/<TITLE_ID>/{default,4xx+,10xx+,decrypted}`: PPSA01317 = GT7, PPSA34547 = Control, PPSA26344 = Ghost of Yōtei, PPSA28183 = AC Black Flag (was `<Game>/4.00+`); both test suites updated.

### AC Black Flag Resynced (PPSA28183 v01.006.038, Ubisoft Scimitar/Anvil engine)
Three variants. All SELFs in `default` are decrypted (PS5-style SELF, unwrap works); the backports are make_fself SELFs.
- `default`: original dump; SDK 0x12000038/0x13090001, param 13.60/12.00.
- `4xx+`: "FW 4.xx + PSSR + Quality 60 FPS (v2)", **PS5 Pro only**, tested on a Pro at FW 10.01. SDK 4.00 on every module, a "4xx-kit" libc, an 8-file fakelib set (Agc, AgcDriver, Ampr, AppContent, GameUpdate, NpEntitlementAccess, PlayGo, Psml 13.60), `psml_package/KPN9_v070.psp`. README in the folder.
- `10xx+`: only `eboot.bin`, `ShaderData.bin`, `fakelib/libSceAgcDriver.sprx` (a different AgcDriver build). It is a delta laid over `4xx+`: the user's note says "fix black screen crash on Pro, tested on 10.20".

**eboot `default` → `4xx+`: 408 bytes in 139 ranges.**
| Change | How | Generic? |
|---|---|---|
| SDK pair → 0x04000031/0x09040001 | procparam | yes (we do it) |
| `sceSaveDataMount3` `systemBlocks -1 → 0` | call site: `mov qword [rbp-0x88], -1` → `0` (VA `0x2bced72`) | **yes: the third game with this exact fix.** The README gives the reason: *ShellCore rejects -1 when the game SDK is below 5.00*. |
| `sceAudioOut2Set3DLatency` | call → `xor eax,eax` | yes (we stub it ret0) |
| `sceVideoOutAllowOutputResolutionWqhdDetection` (FW 6.00) | 3 calls → `nop` | **new rule**: ret0 |
| `sceNpCommerceDialogOpen2` (FW 7.01) | call → `mov eax, 0x80000001` | **new rule**: error stub |
| 126 `s_trap 1` → `s_nop` (`010092bf` → `000080bf`) in shader code inside a RW data segment, plus 168 more in `ShaderData.bin` | GPU hang fix for FW < 12 (BVH asserts), together with an AgcDriver hook that allocates a 24 MB compute scratch ring (`COMPUTE_TMPRING_SIZE`) | **new category: shader binaries.** The pattern is a library/hardware rule, not game logic, but it patches game data. Not supported yet. |
| `sceKernelGetOperationMode` → mode 3 (PLT + one call site), `sceKernelIsTrinityMode` → 1, `sceAgcGetIsTrinityMode` → 1 | PLT entries overwritten with stubs | **forces PS5 Pro mode**; an option, not a backport fix |
| Target-FPS clamp floor 20 → 60 (VA `0x304000a`, rip-relative operand pointed at a 60.0 constant) | game logic | game-specific tweak (Quality 60 FPS) |

**`4xx+` → `10xx+` (128 bytes, 14 ranges)** undoes everything Pro-specific and the 60 FPS tweak, and uses GT7's GOT→stub technique instead of overwriting PLT entries: a new cave stub `mode 2, lowEnergy 0` (= our `opmode_normal`), GOT of `sceKernelGetOperationMode` → it, GOT of `sceKernelIsTrinityMode` and `...WqhdDetection` → the `xor eax,eax; ret` tail. The 3 Wqhd calls and `sceNpCommerceDialogOpen2` are restored (they exist on 10.x). The savedata fix, Set3DLatency, the s_trap patches and SDK 4.00 stay. So the "Pro black screen" was the Pro-forcing itself on a console/firmware where PSSR doesn't work.

**Modules:** FontGsm, JobManager, NpCppWebApi, Pfs, memorywrapper: SDK only. `libaegir_f.prx`: SDK + 4 Http2 WebSocket calls (CreateRequest, SendDataMessage, SendTextMessage, Close; FW 5.02) → `mov eax, 0x80000001` at the call site. libc: replaced by the "4xx-kit" libc (README: 3 libSceLibcInternalExt functions → 0).

**`ps5bp backport default --firmware 4.50` compared:** SDK, savedata, Set3DLatency, IsTrinityMode, GetOperationMode (mode 2) and WebSocketCreateRequest are covered. **Missing** (reported `missing`): `sceVideoOutAllowOutputResolutionWqhdDetection`, `sceNpCommerceDialogOpen2`, `sceHttp2WebSocketSendDataMessage`/`SendTextMessage`/`Close`. Not covered: the shader `s_trap` fix and the AgcDriver scratch-ring hook. `sceSaveDataPrepare` is not patched by this game (our rule only rewrites the value 2, so it's a no-op if AC passes another value).

### Ghost of Yōtei `4xx+ NOT TESTED` is really an SDK 10.00 backport
- eboot SDK 0x11000040/0x12590001 → **0x10000040/0x12090001**; param.json `requiredSystemSoftwareVersion`/`sdkVersion` 0x10000000. Despite the folder name it targets FW 10.x, which is why only one function needed fixing: `sceKernelGetOperationMode` (FW 11.00).
- Fix technique (new): the PLT entry is overwritten with a stub returning **mode 3 (Pro)**, and the import's NID string in `.dynstr` is renamed from `NH6xARDOVv8` to `+L98PIbGttk` (`scePthreadRwlockUnlock`, exists everywhere) so the loader resolves the slot. The slot is never used because the PLT no longer jumps through it.
- **The `default` eboot is not pristine** (like GT7's NOT_BACKPORT): 15 entitlement-label strings are replaced with `YOTEIDUPLEX`, an `or byte [rbp-0x240], 0x20` became `, 0`, and a `jmp rcx` became `jmp short 0xc9f776`. The backport eboot has the original values, so it was built from a different (clean) eboot. These differences are dump artefacts, not backport changes.

### Update (2026-10-09, later): Yōtei `decrypted` and `dump` added, `4xx+` completed
- **`dump`** (same v02.002.000, same libc, same param.json as `default`) is the **clean eboot**: unwrap(`dump`) differs from unwrap(`default`) only in the 15 `YOTEIDUPLEX` labels, the `or`/`jmp` bytes and ELF header byte 0x3c. Its fakelib/ holds the dump's homebrew set: ampr_emu **Sep 6** (the same file as AC's), AppContent, GameUpdate, NpEntitlementAccess stubs (make_fself, SDK 2.00). No PlayGo stub.
- **`decrypted`** = unwrap(`dump`) byte for byte (eboot and libc), plain ELFs, no param.json.
- **The backport was built from `dump`**: dump → `4xx+` eboot is only 4 changes (ELF header 0x3c, SDK pair, the GetOperationMode PLT stub → mode 3, the NID rename). Everything from the earlier section stands.
- **`4xx+` now also ships `k9.psp` and `fakelib/libScePsml.sprx`**: the same files as the GT7 backport and our `fakelibs/4` set (13.60 Psml, J03950001, reads /app0/k9.psp). This is the third backport (GT7 FW 4, UFC 6 FW 9.60, Yōtei FW 10.x) that pairs that Psml with k9.psp. It confirms our automatic k9.psp rule. There's no ampr_emu in `4xx+`: under the ShadowMountPlus overlay the dump's own Sep 6 build stays in use, the build that ran on a Pro at 10.01 (AC).
- **libc (new in this delta)**: SDK → 10.00, and the three FW 11.00 libSceLibcInternalExt imports (`tB59hFLH3SA`, `OQ-dzhlnM28`, `qBS714-Jr3g`) get PLT stubs `xor eax,eax; ret`, with their NID strings renamed to `EHsF2i9FXPM` (`sceLibcInternalBacktraceForGame`, in every firmware). It's the same PLT-stub-plus-rename technique as the eboot. We stub the first two ret0 too. `qBS714-Jr3g` we **redirect** to the local `__cxa_thread_atexit_impl` (proven on GT7/Control) instead of ret0, so our output differs there on purpose.
- **Our pipeline on `dump`**:
  - **FW 4:** everything resolves except the known `gWT7X8H0bYs` (VrrStatus, FW 10.01, still to triage). The store set adds ampr_emu Aug 24, PlayGo and Psml, plus k9.psp.
  - **FW 10.01:** this is the tester's target. There is **no store set for FW 10** (lookup is exact folder, then major), so the 9 `libScePsml_mfsr2` imports (FW 12.00) stay missing. The tester solved them with the 13.60 Psml + k9.psp. We also warn about the Sep 6 ampr_emu and the missing PlayGo stub.
- **Added (same day): `fakelibs/10` and `fakelibs/11`**, each with only that Psml + `app0/k9.psp` (NOT TESTED, like the source). FW 11 also got an **SDK pair, `0x11000040/0x12590001`**, in both engines. Kitchen's table stops at 10. The pair is Yōtei's original modules' value; the FW 11.00 system libraries have the same PS4 half (`0x11000043/0x12590001`). The `dump` now backports to FW 10.01 and 11.00 with nothing missing, and both engines give identical output.

### Conclusions
- **Saves:** no save-path redirect in any of the four games. `systemBlocks -1 → 0` is in GT7, Control and AC, so it's confirmed as a generic rule. Its condition is the **target SDK < 5.00**, not "always"; for FW 5+ targets it is unnecessary. GT7's `mount_mode → RDONLY` (Part 11) is still the only save patch that is game-specific.
- **"Force PS5 Pro mode" is a recurring, generic option** (AC 4xx+, Yōtei): `sceKernelGetOperationMode` → 3, `sceKernelIsTrinityMode` → 1, `sceAgcGetIsTrinityMode` → 1. It is a per-function rule set, but must be opt-in: on a base console or a Pro firmware where PSSR fails it gives a black screen (AC `10xx+`).
- New default rules (added 2026-10-09): Wqhd detection ret0, CommerceDialogOpen2 `ret:0x80000001`, WebSocket SendDataMessage/SendTextMessage/Close `err_http2` (same as CreateRequest). `sceSaveDataMount3` now has `"below": "5.00"`. AC at FW 4.50: nothing left unresolved in the eboot or `libaegir_f`.

### Optional fixes (implemented 2026-10-09)
- `data/fixes.json` `"options"`: opt-in groups, never applied by default; a selected option's fixes replace the default fix for the same NID. CLI `--option KEY` (both engines), report line `options:` plus a WARNING with the option's warning text, JSON `options`.
- New fix kind **`plt_stub`**: stub body in a cave, the PLT entry jumps to it, the GOT slot is left alone. This is how AC and Yōtei force values, and it works even where the target firmware has the function (a GOT-only stub could be overwritten if the loader binds the slot).
- First option **`force_pro`**: `sceKernelGetOperationMode` → `opmode_pro` (mode 3), `sceKernelIsTrinityMode` → `ret:1`, `sceAgcGetIsTrinityMode` → `byte1`. Both engines produce byte-identical AC output with it.

### The shader `s_trap` fix and the AgcDriver scratch hook (investigated 2026-10-09)
- **AC replaced every `01 00 92 BF` (`s_trap 1`) with `00 00 80 BF` (`s_nop 0`)**: all 164 in the eboot (all 4-aligned, all in the RW segment 7) and all 168 in `ShaderData.bin` (only 20 of them 4-aligned in the file). It's a blind byte-pattern replace; nothing else in `ShaderData.bin` changed.
- **The 164 come from SDK code, not the game**: the original eboots of **Yōtei and Control also have exactly 164** (GT7: 202), in the same kind of RW segment. README: they are BVH (ray-tracing acceleration structure) asserts. **Neither GT7's nor Control's console-tested backport touched them** (Control ran on FW 10.01, GT7 on FW 4), so the patch is not required by itself; AC applied it together with the scratch hook below.
- **`libSceAgcDriver` scratch hook.** Both AC AgcDrivers are a stock Sony build plus the same 1,373-byte hook: `4xx+` = Control's build (J03718109, our `fakelibs/4.03`), `10xx+` = GT7's build (J03828141, our `fakelibs/4`). The code segment is extended (`p_filesz`/`p_memsz` 0xb9ce → 0xbf2d, the `--extend-text` technique) and the first instruction of `sceAgcDriverSubmitMultiDcbs` (0x4cc0) and `sceAgcDriverSubmitMultiAcbs` (0x4d40) jumps to a wrapper. The wrapper saves the argument registers and calls a PM4 scanner on each submitted command buffer (≤ 0x40000 dwords). The scanner:
  - walks type-3 packets, follows `INDIRECT_BUFFER` (op 0x63) chains, and tracks `SET_SH_REG` (op 0x76) writes to registers 0x213, 0x218 and the compute user-data registers 0x240–0x249;
  - on `DISPATCH_DIRECT`/`DISPATCH_INDIRECT` (op 0x15/0x16) with scratch enabled, it allocates a **24 MB direct-memory block named `ac_scratch`** once, writes its address into the scratch descriptor, and rewrites the tmpring size dword to `0x40080`.

  The README calls this "COMPUTE_TMPRING_SIZE"; older firmware does not set up a compute scratch area. The `10xx+` "Pro black-screen fix" is the same hook moved onto GT7's AgcDriver build, plus removal of the Pro forcing.
- **Implemented as option `gpu_scratch` (2026-10-09):** the two hooked AgcDrivers are store variants `fakelibs/4/gpu_scratch` (AC 10xx+) and `fakelibs/4.03/gpu_scratch` (AC 4xx+); the option also applies the `s_trap 1` → `s_nop` pattern to modules (4-aligned hits in non-executable segments only: all 164/202 hits in AC, Control, Yōtei and GT7 are there; the 3 unaligned hits in Control's Agc fakelib are skipped) and to every hit in `ShaderData.bin`. On AC at FW 4.50 it reproduces AC's `ShaderData.bin` and `10xx+` AgcDriver byte for byte. GUI: Overview tab, "Optional patches" (per game, not remembered).
- **Generic?** The hook acts on Agc command buffers for any game, so it belongs to the fakelib, not to a game; the name `ac_scratch` is cosmetic. Whether a game needs it depends on its compute shaders using scratch; GT7 and Control ran without it. Candidate approach: keep it as an **opt-in fakelib variant** in the store (AgcDriver + hook, with provenance) and the `s_trap` replace as a companion option. Shader patching needs a new pipeline step, because `ShaderData.bin` is not a module and the pattern is unaligned there.

---

## Part 16: Battlefield 6 (PPSA19534) backport diff (2026-10-09)

Frostbite engine. `default` works on FW 10.xx and later (its executables are already fake SELFs with SDK 0x10000040; param.json requires 12.70); `4xx+` is the FW 4.xx backport.

### Two executables
- Besides `eboot.bin` the game ships **`SP/BattlefieldGame.Main_Ps5_retail.elf`**, a second fake-signed executable (the single-player game, 249 MB). The backport patched it like the eboot (SDK 4.00, two guarded wrappers).
- **Our tool skipped it**: module discovery only took `eboot.bin` and `*.prx`/`*.sprx`. **Fixed**: both engines now also take `*.elf` (`game.EXECUTABLE_EXTENSIONS`, `GameScan.ModuleExtensions`). With that, `SP/…elf` gets SDK 4.00 and the same fixes as the eboot.

### What `default` → `4xx+` changed
| Module | Change |
|---|---|
| all | SDK → 0x04000031/0x09040001 |
| `libc.prx` | only the known `MapNamedFlexibleMemoryInternal` → `MapFlexibleMemory` NID swap |
| `libSceNpCppWebApi.prx` | SDK only |
| `eboot.bin`, `SP/…elf`, both `Engine.Render.Core2.PlatformPs5.retail.prx` | **guarded wrappers** for `scePadIsRemoteController` (FW 10.01), `sceKernelIsTrinityMode` (7.01), `sceVideoOutAllowOutputResolutionWqhdDetection` (6.00) |
| `eboot.bin` | memory hooks on `sceKernelMapDirectMemory2`, `sceKernelBatchMap2` and one `sceKernelAllocateDirectMemory` call site (below) |
| `fakelib/` | the store-like set (Agc, AgcDriver, Ampr, PlayGo, Psml) plus AppContent, GameUpdate, NpEntitlementAccess and a **`libkernel.sprx`** |

### Guarded wrapper (new technique)
The PLT entry jumps to a chain that loads the import's GOT value into r11 and returns a fallback when r11 is 0, or equals the PLT entry's own `push` address (never bound), or equals **`0x800032780`** (libkernel base `0x8_0000_0000` + `0x32780`: the address the loader binds unresolved imports to, which raises `PRX_NOT_RESOLVED_FUNCTION`). Otherwise it `jmp r11`, so the real function is used on firmware that has it.
- Fallbacks: `scePadIsRemoteController` → `*(u8*)rsi = 0; return 0`; WqhdDetection → `0x8029001c` (a VideoOut error; AC used `return 0`, which we keep); IsTrinityMode → GOT pointed at `xor eax,eax; ret`.
- **Trade-off**: one backport works on every firmware and uses the real function where present, but the trap address is firmware-specific (libkernel layout). Candidate future fix kind (`guarded`), needs the trap offset per firmware from our libkernel dumps.

### Memory hooks (firmware-specific, not rules)
- `sceKernelMapDirectMemory2`: GOT → `mov eax, 1; ret` and one call site forced to `eax = 1` (a non-zero result makes the engine take its other mapping path).
- `sceKernelBatchMap2`: PLT → code that, when the GOT holds the not-resolved trap, jumps to **trap address − 0x19d70**: libkernel's internal implementation on the target firmware, reached by a fixed offset.
- One call site (`0x1c92fe2`) now goes through a dispatcher that compares the low 32 bits of `sceKernelAllocateDirectMemory`'s GOT value with several known offsets (0x17d60, 0x17990, 0x17a10, 0x18430, …): **it identifies the firmware by where libkernel's export sits**, then jumps to a per-firmware internal function.
These rely on per-firmware libkernel offsets. Our 43 firmware dumps could provide such tables, but it's a large step; recorded only.

### `fakelib/libkernel.sprx`
A Sony libkernel (build J03718109, the "4xx kit" build id; SDK 12.00), fake-signed. Its exports are closest to FW 9.x and include 12 functions from FW 10.01–12.00, among them `sceKernelGetOperationMode`, `SyncOnAddressWait8/16` and the `CreateUuidV1/V4/V7` set. Shipping a newer libkernel as a fakelib is another way to provide missing kernel functions. ShadowMountPlus deletes `fakelib/libkernel.sprx` for installed packages (Part 4), so it only works for dumps.

### Our tool on `default` at FW 4 (after the `.elf` fix)
- New rule `scePadIsRemoteController` → `out2_byte0` (BF6's fallback).
- Still `missing` in eboot and `SP/…elf`: `WxLcN76q19o`, `8weiQyIdI3A` (libSceNpPremiumAddcont, FW 9.00, refs=1 each). BF6's backport left them unpatched too; recorded in `data/left_unpatched.json`.
- Everything else BF6 needed is covered by existing rules (IsTrinityMode, Wqhd, VRR peg, CommerceDialogOpen2, SyncOnAddress, the libc swap, saves).

---

## Part 17: Beast of Reincarnation (PPSA29343) backport diff (2026-10-09)

Game Freak, Wwise audio (25 `prx/ak*.prx` plugins). `default` = original dump (SDK 0x11000040, param requires 12.60; works on FW 11.xx+), `4xx+` = FW 4.xx backport.

### What `default` → `4xx+` changed
- **All 30 modules**: SDK → 0x04000031/0x09040001 (the Wwise plugins and sce_module libraries: nothing else).
- `libc.prx`: the known `MapNamedFlexibleMemoryInternal` swap, and the `qBS714-Jr3g` call retargeted to libc's own `__cxa_atexit` (our redirect rule, done at the call site).
- `eboot.bin`: `sceKernelIsTrinityMode` GOT → ret0, `sceAudioOut2Set3DLatency` call → `xor eax,eax` (both existing rules), plus:
  - **Wwise 7.1.4 → 7.1**: the channel mask `0x2d63f10c` → `0x63f108` at two sites, the same fix CONTROL made (Part 7). Both games carry the **same Wwise build**: the speaker-setup site (`mov [rbp-0x30], rdx; mov edx, 0x2d63f10c; mov eax, 0x63f108`) and a channel-switch site (`mov ecx, 0x3102; jmp; mov ecx, 0x2d63f10c; jmp; mov ecx, 0x4101`) each occur exactly once in Beast and CONTROL and in no other sample (AC has a different Wwise build; GT7, Yōtei and BF6 use other audio). CONTROL patched the first, Beast both.
  - **`usleep` → busy-wait**: the PLT entry becomes `push rdi; call scePthreadYield; call sceKernelGetProcessTimeCounter; deadline = counter + usec * 0x63a`, then a loop on `sceKernelGetProcessTimeCounter` until the deadline, `return 0`. `usleep` exists on FW 4, so this is a behaviour change (probably precise frame pacing; 0x63a = 1594 counter ticks per µs). Not made a rule: its reason is unknown and it spins a core. Candidate opt-in option if a tester reports stutter.
- `sceCoredumpWriteUserString` (FW 8.00, eboot, refs=1) was shipped unpatched (recorded in `data/left_unpatched.json`).

### Implemented
- **Default byte patterns** (`data/fixes.json` `"patterns"`, applied with the known fixes): name, find/replace, `code: true` = search executable segments at any offset (the find bytes carry enough context to match one function). The two Wwise sites are the first entries; on Beast at FW 4 they reproduce the backport's audio patch exactly (`0x2d63f10c` 3 left, `0x63f108` 12, as in `4xx+`). Report: `pattern  Wwise 7.1.4 -> 7.1 (…)  x1`. Both engines identical.
- With this, `ps5bp backport default --firmware 4` covers everything the Beast backport did except the `usleep` busy-wait; the only unresolved import is `sceCoredumpWriteUserString`, which the backport left too.

---

## Part 18: Borderlands 4 (PPSA01494) backport diff (2026-10-09)

Unreal Engine 5, Wwise, Coherent (libcohtml, librenoircore), libbifrost. `default` = original dump (SDK 0x10000040, param requires 12.70; works on FW 10.xx–12.xx), `4xx+` = FW 4.xx backport.

### SELFs with data after the version block (unwrapper fix)
- The backport's eboot, libbifrost, libcohtml and librenoircore are PS5-style SELFs with header byte 4 = `0x10` and flags `0x32` (the same variant as AC's `10xx+` AgcDriver). **0x1E8 bytes follow the trailing PT_SCE_VERSION block** (apparently an appended signature), so our unwrapper, which took the block from the file's end, failed with "truncated trailing PT_SCE_VERSION data".
- **Fixed in both engines**: when the end position doesn't fit, the block is looked for at the declared file size ± 0xF and accepted only if every record is well-formed. Both engines unwrap the four files to identical ELFs. Tests: `tests/test_bl4.py`, `SceVersionTests.AppendedDataAfterVersionBlock_UnwrapsLikePython`.

### What `default` → `4xx+` changed
- **libc replaced** by an older Sony libc (build J01397812, 2,565 exports instead of 2,925). The modules' libc++ imports it lacks (`_Throw_bad_weak_ptr`, `__throw_bad_variant_access`, `_Cnd_init_with_default_name_override`) are pointed at `xor eax,eax; ret`. We keep and patch the game's own libc instead, so this doesn't apply to us.
- **eboot**:
  - **`sceSaveDataMount3` systemBlocks −1 → 0**, the fourth game with it. Done as an adapter in an **extended code segment** (`p_filesz` 0x13b5dc → 0x13c000), with four call sites retargeted to it (`cmp qword [rdi+0x18], -1; jne; mov qword [rdi+0x18], 0; jmp` to the PLT).
  - GOT → `ret0` **and the symbols rewritten into local definitions** (`st_shndx = 1`, our `--define-symbol`): `sceKernelIsTrinityMode`, `scePadIsRemoteController` (plain ret0, which leaves `*isRemote` unset; our `out2_byte0` also clears it), `sceVideoOutVrrPeg/UnpegFromFixedRate`, `sceAudioOut2Set3DLatency`.
  - **Wwise 7.1.4 → 7.1 at three sites**: the speaker-setup and channel-switch sites already in our patterns (same Wwise build as CONTROL and Beast), plus a **packed default** `movabs rdx, 0x2d63f10c2d63f10c` whose low half becomes `0x63f108`. CONTROL and Beast left that one; added as the third pattern (unique in every sample, present in BL4, Beast and CONTROL).
  - **`sceNetPoolCreate` → `mov eax, 1; ret`** in the eboot and in libbifrost (PLT and symbol). The function exists since FW 1.00 and no other sample touches it; returning pool id 1 skips the real call. Reason unknown (maybe the newer SDK's size or flags are refused on FW 4). Recorded, not a rule: a fake pool id could break later net calls.
- **Other modules**: SDK only (mediadecoders, FontGsm, JobManager, NpCppWebApi, Pfs). The three middleware libraries got only the libc++ stubs and libbifrost the NetPoolCreate stub.

### Our tool on `default` at FW 4
Nothing unresolved: all 31 imports BL4 needed are covered (existing rules + the three Wwise patterns). Differences from the backport: we keep the game's libc (patched), clear `*isRemote`, and leave `sceNetPoolCreate` alone.

---

## Part 19: Dying Light: The Beast (PPSA24003) backport diff (2026-10-09)

Techland (C-Engine), Wwise, Epic Online Services (`EOSSDK-PS5-Shipping.prx`), ffmpeg, NVIDIA NRD. Three folders: `default` = original dump (SDK 0x11000040, param 13.60/11.00), `decrypted` = the same modules as plain ELFs, `4xx+` = FW 4.xx backport.

### `decrypted` = what our unwrapper produces
Every segment of `decrypted/*.prx` equals our unwrap of `default/*`; only stale section-header fields in the ELF header differ (`e_shoff`/`e_shnum` point past the file end). Backporting `decrypted` gives the same fixes as `default`. **Our output for ELF input used to be plain ELF**, which the console can't load; **since 2026-10-09 both engines always write fake-signed SELFs** (the user's choice), so `decrypted` and `default` give byte-identical output, and the report warns about plain-ELF modules left unchanged in the game folder (they're not in the overlay and won't load). Tests: `tests/test_decrypted.py`, `DecryptedInputTests`.

### What `default` → `4xx+` changed
- SDK only: the five Wwise `Ak*.prx` plugins, ffmpeg, libNRD, runtime_prospero.
- `libc.prx`: the known `MapNamedFlexibleMemoryInternal` swap and the `qBS714-Jr3g` → `__cxa_atexit` redirect (here as GOT → a `jmp` stub in padding).
- `EOSSDK-PS5-Shipping.prx`: `sceKernelGetOperationMode` → mode 2, written over the PLT entry (our `opmode_normal`).
- `eboot.bin`:
  - IsTrinityMode GOT → ret0, Set3DLatency call → `xor eax,eax` (existing rules).
  - `scePadIsRemoteController` calls → `mov byte [rsi], 0; xor eax, eax; ret`, in a page-extended code segment: **identical to our `out2_byte0`** (BF6's fallback).
  - VRR calls (`PegToFixedRate`, `UnpegFromFixedRate`, VrrStatus `kP2L8t3j-aM`, `LibwuIonIBw`, `-q-vbO859Tw`) → `mov eax, 0x8029001c; ret` (a VideoOut error), where GT7 used ret0. We keep ret0 and **added `-q-vbO859Tw` → ret0** (FW 5.02), the third VrrStatus function.
  - **No Wwise patch**, although DL has the same Wwise build (all three of our Wwise patterns match). So DL's backport ran without the 7.1.4 → 7.1 fix: the fix is needed only when the console reports 3D audio. It stays a default (its cost is 7.1 instead of 7.1.4).
- **EOS SDK: 112 `libSceNpCppWebApi` C++ imports left missing.** DL doesn't ship its own `sce_module/libSceNpCppWebApi.prx` (most samples do), so the console's FW 4 library is used, and it lacks the newer friends, presence, blocking and profanity-filter APIs. The backport left them all unpatched (reached only from online features; lazy binding). Recorded in `data/left_unpatched.json` (source DL).

### Our tool on `default` at FW 4
All eboot imports resolved after the new VrrStatus rule; only the 112 EOS WebApi imports stay `missing`, now tagged "left unpatched by" DL.

### Test fix
`tests/test_phase4.py` compared the whole `LEFT_UNPATCHED` table with GT7's unresolved set. Since other backports add entries (BF6, Beast, DL), it now compares GT7's own entries, as it already did for Control.

---

## Part 20: EA SPORTS FC 27 (PPSA34015): two backports that differ only in ampr_emu (2026-10-09)

Frostbite (like BF6). **No original dump**: `default` (works on FW 12.xx and later) and `4xx+` (FW 4.xx) are both backports, already fake-signed with fakelibs.

### The only difference is the ampr_emu build
Every module is byte-identical between the two folders except `fakelib/libSceAmpr.sprx` (and `default` carries a `sce_sys/param.json`):
- `default`: ampr_emu **Sep 6 2026** (`Prospero_ReleaseHooksLooseStdio`, 303,878 bytes). The same file ships in AC Black Flag's `default` and `4xx+`.
- `4xx+`: ampr_emu **Aug 24 2026** (`Prospero_ReleaseHooks`, 255,126 bytes): our `fakelibs/4.03` and `fakelibs/7` build, also in Control's and Dying Light's backports.

So the Sep 6 build is what limits `default` to FW 12.xx+. That matches Control's README ("reads dump data wrong, asks for ~9 GB"). AC's Pro build ran with it at FW 10.01, so it is broken on older firmware rather than everywhere. `data/known_builds.json`'s note now says so (state stays `broken`, Aug 24 stays `ok`).

All ampr_emu files across the samples: GT7's (`8248448608f3`, no build date, 218,678 bytes; also BF6 `default`, Control `default`, `fakelibs/4`), BF6 `4xx+` (`d906bb7b014d`, same size, another undated build), Aug 24 (`362c84e31e3a`), Sep 6 (`0aadd31dbe72`).

### Implemented: check the game's own fakelibs too
Known-broken builds were flagged only for the fakelib store's files (via its manifest). Both engines now also check the game's own `fakelib/*` modules that the store or `--fakelib` doesn't replace (build date read from the module), and warn: `fakelib/libSceAmpr.sprx (libSceAmpr built Sep  6 2026) is a known-broken build: …`. Tests: `tests/test_decrypted.py` `TestGameFakelibBuilds`, `GameFakelibBuildTests` (Python-matched warnings).

### What the FC 27 backport left unpatched
Running our tool over the already-backported `4xx+` at FW 4 (store off) adds what the backport did not do: the three argument fixes (`always`), `sceNpCommerceDialogOpen2`, `sceNpEntitlementAccessGetGameTrialsFlag`, VRR peg/unpeg (Engine.Render), and in libc the `qBS714-Jr3g` redirect, the SyncOnAddress adapters and the four libSceLibcInternalExt stubs (the MapNamedFlexibleMemory swap was already there). Reapplying the argument fixes is harmless (each only rewrites the newer value). The game evidently never calls the others on its tested paths. Its three still-missing imports (`sceCoredumpWriteUserString`, two libSceNpPremiumAddcont) are the ones Beast and BF6 also left; FC 27 added as a source in `data/left_unpatched.json`.

---

## Part 21: EA SPORTS UFC 6 (PPSA23566), a plain-ELF backport tested on a PS5 Pro at FW 9.60 (2026-10-09)

Frostbite (EA Sports). `default` = a fake-signed dump (SDK 0x12000038, param 13.60/12.00) with ampr_emu and a PlayGo stub; `tested on PS5 Pro 9.60` = its backport. No README; the folder name is the only information.

### Update (2026-10-09, later): `decrypted` and `dump` added
- `decrypted` (dated 2026-09-19): the **original v1.004 modules as plain ELFs** (SDK 0x12000038), with a partial `sce_sys` (no `param.json`). Every segment equals our unwrap of `default`, so it is the exact baseline the tester backported; `decrypted` → `tested` differs only by the changes listed below.
- `dump` (dated 2026-10-07): **v1.005.000**, a newer game update, as decrypted PS5-style SELFs (no encrypted segments), without `fakelib/`. `ps5bp backport dump` resolves every import at **FW 9.60** (`fakelibs/9.60`, `k9.psp` added: the game imports libScePsml) and at **FW 4** (`fakelibs/4`). The EA Sports Frostbite pattern and all rules still match the update (the Mount3 adapter even lands at the same cave), so the rules carry over from one game version to the next. The store sets supply ampr_emu and the PlayGo stub the dump lacks.

### The tested backport ships plain ELFs
Every module in the tested folder is a **plain, unsigned ELF**, and it ran on a PS5 Pro at FW 9.60. So with that loader setup plain ELFs load too. Our output stays fake-signed (what every other tested backport ships); the plain-ELF warning now says "most tested backports ship fake SELFs … sign them if the game does not start" instead of "the console loads signed files".

### What changed (default → tested)
- SDK → **0x04000031/0x09040001 (4.00)** on every module, although the console was at 9.60.
- eboot: `sceSaveDataMount3` systemBlocks −1 → 0 at the call site (**the fifth game**), and one EA Sports Frostbite value: in the branch where a global format equals `0x30c`, the size field `[rbp-0xf0]` = `0xc00` → `0x800` (read as 12 × 0x100 → 8 × 0x100: probably 12 → 8 audio channels, the Frostbite counterpart of the Wwise fix; inferred, not traced).
- libc: MapNamedFlexibleMemory swap; the `qBS714-Jr3g` wrapper made `return 0` (Control's variant).
- Engine.Render, NpCppWebApi: SDK only.
- fakelibs: **Agc, AgcDriver and Psml from Sony build J03950001** (FW 13.x, newer than our newest decrypted dump, 12.70) with SDK 4.00, and ampr_emu **Aug 24** (the dump ships the **Sep 6** build, "PackedStdio" flavour): the same swap as FC 27.

### Implemented
- **Default pattern** "EA Sports Frostbite: 0x30c output buffer 0xc00 -> 0x800" (34-byte signature). FC 27 already carries `0x800` in both its backports and UFC's tested backport made the change, so two EA backports agree; not in Battlefield 6 (different code). On UFC `default` it matches once.
- **Store set `fakelibs/9.60`** from the tested backport: Agc, AgcDriver, Psml (J03950001), ampr_emu Aug 24, and the dump's PlayGo stub. The tested files are plain ELF; they were fake-signed for the store (an unchanged store fakelib is copied as it is). With it, `ps5bp backport default --firmware 9.60` resolves every import (without it: 14 Agc/Psml imports of FW 10.01–12.00 missing, and no fakelibs at all for 9.x targets).
- At FW 4 UFC resolves fully with the existing rules and `fakelibs/4`.

### Sony build id → firmware (from the 43 dumps' libSceAgcDriver)
The `W:/Build/J…` id in a Sony library identifies the firmware it came from: e.g. J01675412 = 4.00, J01736804 = 4.03, J03308729 = 9.60, J03376575 = 10.01, **J03718109 = 12.00** (Control's and AC's "4xx kit" Agc/AgcDriver/Psml, BF6's libkernel), **J03828141 = 12.70** (GT7's Agc set, `fakelibs/4`), J03950001 = newer than 12.70 (13.x; 13.60 is encrypted). Each firmware has three ids (one per Sony build tree), rising with time; 5.02 and 5.10 share theirs.
- **Implemented**: `data/sony_builds.json` (generated by `tools/gen_sony_builds.py` from the dumps: 123 ids; an `evidence` section adds J03950001 = 13.60 from AC's README). Both engines print `Sony build J03828141 = FW 12.70` per Sony module in the report (JSON `sony_build`), `fakelib list` shows it, and the app's Store page has a Firmware row. Unknown ids are placed by number ("between FW 12.70 and 13.60"). A game's own `sce_module` libraries show the SDK they came with (UFC 6's libc: between 11.60 and 12.00).
- Our store sets: `4` = Agc/AgcDriver from **12.70**, Psml from **13.60**; `4.03` = **12.00**; `7` = **10.01** (our BackPork build); `9.60` = **13.60**.

### PSSR models and k9.psp
- **PSSR** (PlayStation Spectral Super Resolution, the PS5 Pro's ML upscaler) runs in **libScePsml** (`scePsmlMfsr*` / `libScePsml_mfsr2`). Its neural-network weights are **`.psp` model packages** (magic `PSP `) in the firmware: `/system/common/psml_package/` holds `MFSR4K_v070.psp`, `MFSR8K_v070.psp`, `BCSISR_v070.psp`, `MFSRLW_v070.psp` (11.20+) and **`KPN9_v070.psp`** (from 12.40: 349 KB there, **4,135,904 bytes in 13.60**). Psml builds the path with `"/%s/common/psml_package/%s"`. The models change with firmware (MFSR4K: 8.0 MB in 9.60, 10.7 MB in 10.20, 13.4 MB in 11.20, 8.2 MB from 12.40).
- **`k9.psp` is FW 13.60's `KPN9_v070.psp`, byte for byte, renamed.** Games built against a 13.x SDK use the KPN9 model, which older firmware lacks (or has in its early 12.40 form). The **13.60 Psml** used as a fakelib (Sony build J03950001: GT7, Yōtei, UFC 6, our `fakelibs/4` and `fakelibs/9.60`) is patched to read **`/app0/k9.psp`** (a fixed string; the zones `KPN9::zone0..2` are the 13.x model's), so the backport ships the model in the game folder. AC uses another layout: its 13.60 Psml reads `/app0/psml_package/%.0s%s` and ships `psml_package/KPN9_v070.psp` (2,757,600 bytes, a version between 12.40 and 13.60). The 12.00 "4xx kit" Psml (Control, BL4, BF6, Beast, DL, FC 27, `fakelibs/4.03`) reads only the system folder.
- It matters on a **PS5 Pro** only (PSSR); a base PS5 never loads the model. Without `k9.psp` an app0-patched Psml can't load KPN9 on a Pro: PSSR fails (black or unupscaled image, depending on the game).
- **Implemented**: a store set can have an **`app0/` folder** (`fakelibs/4/app0/k9.psp`, `fakelibs/9.60/app0/k9.psp`). A file is added to the output's game root **only when it is needed**: a fakelib of the set that the game uses (it exports a function some game module imports) contains `"/app0/<file>"`, and the game doesn't ship the file itself (report: `added file k9.psp (from the fakelib set)`, JSON `app0_files`; both engines).
- **Not an optional patch**, by design: the model is read only on a PS5 Pro, for a game that imports `libScePsml`, when the set's Psml is the app0-patched 13.60 build. A base PS5 never loads it, so shipping it costs only 4 MB, while leaving it out breaks PSSR on a Pro. The automatic rule adds it exactly when a Pro could need it. Example: *Harvest Moon: The Winds of Anthos* (PPSA28548) imports no Psml at all, so its FW 4 backport no longer gets `k9.psp` (the first cook, before this rule, did); GT7 imports `libScePsml`/`_mfsr2`/`_quality8K`/`_specularAA` and gets it.
- Online: nothing public documents `k9.psp` or app0-patched Psml (searches return only spam); PSSR is a PS5 Pro feature, and a 2026 firmware made PSSR 2.0 the Pro default even for games built for 1.0 (VGC), which fits KPN9 being the newer model in 12.40/13.60.

---

## Part 22: God of War Sons of Sparta (PPSA28997), a Unity FW 4.xx backport (2026-10-09)

The folder is a single flat backport, with no original dump next to it and no tester note, so its test status is unknown. Contents: v01.008.001, param.json left at 13.20/11.00. **Every module is a make_fself SELF at SDK 0x04000031/0x09040001**, `sce_sys/about/right.sprx` included. The first **Unity (IL2CPP)** game in the samples:
- Modules: `eboot.bin` (PS5Player_IL2CPP, Unity "PS5_11_00"), `Media/Modules/{Il2cppUserAssemblies (114 MB), PS5Util}.prx`, `Media/Plugins/{CommonDialog, PSN, SaveData, VoiceInputPlugin, lib_burst_generated, libfmod, libfmodstudio, libresonanceaudio}.prx`, `sce_module/{libc, libSceNpCppWebApi}.prx`. Our module scan already finds them all (any `.prx/.sprx` under the game).
- fakelib/: Agc, AgcDriver and PlayGo are **GT7's** (= `fakelibs/4`), ampr_emu is the **Aug 24** build (= `fakelibs/4.03`/`7`), and Psml is the 12.70 build J03828141 shipped by DL and FC 27. There's no `k9.psp`, and none is needed: we add none either, because no game module imports a function that this Psml exports.

### Gap found and fixed: opaque PT_SCE_VERSION data
- `Media/Modules/PS5Util.prx` (Unity's playstation-extras psutils) failed to unwrap with "malformed trailing PT_SCE_VERSION data". Its PT_SCE_VERSION segment (0x125 bytes) sits **exactly at the declared size and ends the file**, but its bytes aren't `name:` records (opaque, hash-like).
- Fixed in both engines (`unpack_fself_file`, `Self.Unwrap`): a block at exactly the declared size that ends the file is taken as-is. The record check now runs only when the block is found by searching around that position (the Yōtei −8 and BL4 appended-data cases). Unwrap is identical in both engines, and sign → unwrap gives the same ELF back. Tests: `tests/test_gowsos.py`, `SceVersionTests.OpaqueVersionBlock_UnwrapsLikePython_AndSignRoundTrips`.

### What the tester patched
- **eboot**: `sceKernelGetOperationMode` gets a GOT stub `mov dword [rdi],2; mov dword [rsi],0; xor eax,eax; ret` (mode 2 = base PS5), equivalent to our `opmode_normal` (source `GOWSOS` added to that rule).
- **Left unpatched, with untouched PLT entries** (lazy binding: each fails only if called):
  - eboot: `sceKernelIsTrinityMode`, `scePadIsRemoteController`, `sceVideoOutVrrPeg/UnpegFromFixedRate`, `sceHttp2SetRequestNoContentLength` (FW 5.02);
  - PSN.prx: `sceNpCommerceDialogOpen2`;
  - libc: all 7 FW 6–11 imports (`tB59hFLH3SA`, `OQ-dzhlnM28`, `qBS714-Jr3g`, SyncOnAddressWait32/64/Wake/Wait).

  We have proven rules for all of them except the HTTP/2 one; recorded in `data/left_unpatched.json` as `GOWSOS`. The libc ones matter if the game calls them on FW 4. GT7 needed the SyncOnAddress adapters.

### New rule
- **`bEegosRhgM0` `sceHttp2SetRequestNoContentLength` → ret0, DERIVED (not console-tested).** The Unity player calls it once, only when a request has no known length (else `sceHttp2SetRequestContentLength`), and ignores the result in both branches.

### Our pipeline on it (FW 4)
Every import resolves. Fixes applied:
- eboot: IsTrinity, PadIsRemote and Vrr stubs, plus the new HTTP/2 stub;
- PSN.prx: CommerceDialogOpen2 → 0x80000001;
- **SaveData.prx** (the Unity plugin, not the eboot): Mount3/Prepare adapters;
- libc: the full GT7 set.

Both engines produce identical files.

---

## Part 23: Hades II (PPSA36082), a console-tested FW 4.03 backport with a full README (2026-10-09)

Folders: `default` (original v01.006.000, SDK 0x12000038/0x13090001, param 13.60/12.00; FMOD audio, no Psml import) and `4xx+`. The `4xx+` backport is console-tested on FW 4.03 (boots, saves, loads). Its README (Vietnamese/English) documents every change, and the diff `default` → `4xx+` matches it exactly. The README is the most useful one so far, because it explains why each change was made.

### What the backport does
- **SDK fields**: 0x04030031/0x09040001 on every module (FW 4.03 preset). param.json lowered to 4.03. The .sceversion block is left as-is ("firmware doesn't enforce it").
- **Signing**: every module make_fself (PS4-style). fakelib/ = **our `fakelibs/4.03` Agc + AgcDriver byte for byte** ("4xx kit"), no Psml (not imported), no ampr_emu or PlayGo.
- **eboot**: 3 **PLT entries overwritten in place, NIDs not renamed** (the unresolved imports stay; the game still loads on 4.03, which confirms lazy binding again):
  - `La9uyZv4Kvw` → `mov eax,0xff; ret`;
  - `sceKernelGetOperationMode` → `xor eax,eax; ret` (the caller pre-initialises both outputs and branches only on `*rsi == 1`);
  - Wqhd detection → ret0.

  Plus the save fix at the call site: `mov qword [rbp-0x138], -1` → `0`, field +0x18 of the Mount3 struct. This is the **sixth game with the systemBlocks fix**. The README adds that libSceSaveData is identical on 4.03, 4.50 and 12.00, so the 0x809F0000 comes from the old save-data service.
- **libc replaced** by the SDK 11 libc of Beast of Reincarnation's console-run backport. On top of it:
  - 7 PLT stubs `xor eax,eax; ret` (libSceLibcInternalExt ×3, SyncOnAddress ×4);
  - `mov ecx,0x8000` → `mov ecx,0` at both `sceKernelMapFlexibleMemory` call sites (VA 0xb0ad, 0xb2c0), "heap fix";
  - libSceJobManager's `g0mZgRTFPoQ` (`std::_Xbad_function_call`, FW 11) renamed to `L1SBTkC+Cvw` (abort), only because the replacement libc doesn't export it. We keep the game's own libc, which does, so this doesn't apply to our output.

### Rule changes (both engines, data only)
1. **`La9uyZv4Kvw` ret0 → `ret:0xff`.** It is the process CPU-affinity mask, verified in FW 12.70 libkernel: a pthread_once initialiser reads the app info and caches **0xff** (native title) or **0x1fff**, never 0 for a game. Hades II feeds it to thread affinity (`and rax,0xaa`) and checks `popcnt(mask) >= 5`, so ret0 breaks it. GT7 (our old source) only forwards the value to another call, so 0xff is just as valid there. `test_gt7.TestStub` now skips the body comparison for this NID. Our dumps say the function first appears in FW 12.40 (the README says 12.70).
2. **`4h6F1LLbTiw` swap → adapter**: `and ecx, 0xffff7fff`, then jump to `sceKernelMapFlexibleMemory` (`IWIBBdTHit4`). libc's heap growth passes flags **0x8000**, which only the *internal* entry accepts. The public function rejects it on **every** firmware:
   - FW 1.00–6.50: `and ecx, 0xffbfff6f` (allowed bits 0x400090);
   - FW 12.70: `test ecx, 0x88000` → `0x80020016` (EINVAL) as its first instruction.

   So the old plain swap failed as soon as libc grew its heap ("Failed to create the heap", 0xa002000e; the README says Onimusha PPSA27836 died at boot from it). GT7 got away with it because it never took that path. The swap stays console-proven, and the flag clearing ships in this console-tested backport.
3. Source `HADES2` added to GetOperationMode, Wqhd, Mount3 and both rules above.

### Our pipeline on `default` (FW 4.03)
Everything resolves, using the `fakelibs/4.03` set. Both engines give identical files and reports. Differences from the tester's build, all deliberate:
- We keep the game's own libc, with our proven adapters for SyncOnAddress, the libSceLibcInternalExt redirect/stubs and the new MapFlexibleMemory adapter.
- We use GOT stubs instead of PLT overwrites.
- We copy the whole store set (Ampr, PlayGo, Psml too). The tester shipped only Agc + AgcDriver, since Hades II doesn't import Psml.

---

## Part 24: Hollow Knight: Silksong (PPSA12544), a Unity FW 4.xx backport (2026-10-09)

Folders: `default` (original v01.000.010, SDK 0x11000040/0x12590001, param 13.00/11.00; works 11.xx+; the dump's fakelib/ has the Sep 6 ampr_emu, PlayGo and the AppContent/GameUpdate/NpEntitlementAccess stubs) and `4xx+`. There's no README and the test status is unknown. Same Unity layout as Sons of Sparta (Part 22): `Media/Modules/{Il2cppUserAssemblies,PS5Util}.prx`, `Media/Plugins/{CommonDialog,PSN,SaveData,Share,lib_burst_generated}.prx`.

### What the backport does
- **Signing**: every module is an unencrypted PS5-style SELF (not make_fself). SDK 0x04000031/0x09040001 everywhere. param.json gets `requiredSystemSoftwareVersion` 0x09000000…, `sdkVersion` **0** (new). `sce_sys/about/right.sprx` is replaced by a **retail-encrypted** right.sprx from elsewhere (12,752 bytes; nothing can unwrap it).
- **fakelib/**:
  - Agc + AgcDriver = **our `fakelibs/4.03` byte for byte** (4xx kit; the third backport using them, after Hades II and Control);
  - the three dump stubs re-signed;
  - **ampr_emu and PlayGo removed**, so the console's own are used;
  - new **`libSceSaveData.native.sprx`**: Sony build J03718123 (FW 12.00), SDK lowered to 4.00. Our pipeline sees no SaveData import missing at FW 4, so its purpose is unclear (BackPork's 7xx set patches the same library). Recorded, not adopted.
- **eboot**, mostly at the call sites (the PLT stays):
  - IsTrinityMode → `xor eax,eax` ×2;
  - PadIsRemoteController → `mov byte [rsi],0; xor eax,eax` ×2 (= our `out2_byte0`);
  - GetOperationMode PLT → `xor eax,eax; ret`;
  - **`sceUserServiceGetGamePresets` struct size 0x30 → 0x28** (new, see below);
  - plus edits inside the PT_SCE_VERSION records (no effect on loading).
- **SaveData.prx** (Unity plugin): the 16-byte copy of `{blocks, systemBlocks}` into the Mount3 struct becomes an 8-byte copy of `blocks`, so systemBlocks stays 0. That's the systemBlocks fix again (7th game, 2nd time inside SaveData.prx), and our Mount3 adapter covers it.
- **libc**: GT7's recipe:
  - FW 11 libSceLibcInternalExt ×3 → PLT ret0;
  - SyncOnAddress adapters written straight into the PLT (our Wait32 bytes);
  - the MapNamedFlexibleMemoryInternal → MapFlexibleMemory and Wait32 → `_umtx_op` NID renames.

  The **heap flag 0x8000 is left in place** (Part 23: EINVAL on every firmware when libc grows its heap).
- **PS5Util.prx**:
  - the **code segment is extended from 0x3ee to 0x4000** (into the zero gap before the next page; Sons of Sparta's PS5Util has the same 0x4000 size);
  - three import slots are repurposed: `puts` → `clock_gettime`, SyncOnAddressWait → `_umtx_op`, SyncOnAddressWake → `__error`;
  - hand-written Wait (UMTX_OP_WAIT, op 2, ETIMEDOUT handling) and Wake (op 3) are placed in the new space.

### Gaps found
1. **New rule `-sD02mFDBh4` `sceUserServiceGetGamePresets`**: adapter, `when: always`, `below: 11.00`, DERIVED. It clamps the struct's size field to 0x28 (`cmova`, branch-free). The size check lives in the system service: FW 4.03's library just forwards the call over IPC, and that service isn't in our dumps. So the first firmware that accepts 0x30 is unknown, and 11.00 is the earliest one we know works. The game zeroes the struct first and reads only 0x28 bytes back.
2. **PS5Util has no room for our SyncOnAddress adapters.** Without `--extend-text` its backport ends in ERROR ("no int3 padding left for a 11-byte code piece"). With it, both engines extend the segment to 0x4000 (+0x3c12), exactly where the tester's ends, and both produce identical files. Two independent Unity backports used the same extension. **Changed (same day): extension is now the automatic fallback** in both engines, the CLI and the app. It happens only when a module has no padding left, and the report notes it. Opt out with `--no-extend-text` or the app toggle. The app stores the setting under a new key, `AutoExtendText`, so a saved value from the old off-by-default doesn't carry over. Still not console-confirmed.

Our pipeline otherwise covers everything the tester did (eboot stubs, Mount3 in SaveData.prx, the libc set plus the heap-flag adapter) at FW 4, with the store set.

---

## Part 25: Horizon Call of the Mountain (PPSA05666), a PSVR2 game, FW 5.xx backport (2026-10-09)

Folders: `default` (v01.004.011, Guerrilla "Azimuth" engine, SDK **0x05000033/0x09590001**, param 5.00/5.00; the user notes it works on 7.xx+) and `5xx+`. The `5xx+` README is only donation links, and the test status is unknown. Modules: eboot (10,465 imports), 33 Wwise/audio plugins in `prx/` (ak*, auro, izotope, mcdsp, masteringsuite, sceaudio3d), and `sce_module/` Face, FaceTracker, JobManager, NpCppWebApi, Pfs, libc.
- **`default` is not a pristine dump.** Its fakelib/ already holds a backport-style set:
  - Agc, AgcDriver and PlayGo = **our `fakelibs/4` (GT7's)**;
  - Psml = the 12.70 build from DL/FC 27/Sons of Sparta;
  - an **undated ampr_emu** (86 imports, no `__DATE__`), plus AppContent and NpEntitlementAccess stubs.
- **What the backport does**:
  - every module make_fself'd at SDK 0x04000031/0x09040001: the game, all plugins, `right.sprx` (1.00 → 4.00), and **even the homebrew stubs (2.00 → 4.00)**;
  - no param.json, so the overlay keeps 5.00 (hence "5xx+");
  - libc and every plugin: SDK change only. A SDK 5 game needs nothing more at FW 4/5, apart from the one import below.
- **eboot: one guarded wrapper** for `uv0Ae+jCeWY` `sceHmd2ReprojectionSetAllowPositionalReprojection` (libSceHmd2ReprojectionPositional, FW 6.50). The PLT entry jumps into a 5-piece chain in int3 padding:
  - `r11 = GOT`;
  - if `r11 >> 32 == 8` and `r11d == 0x32782`: return 0;
  - else `jmp r11`.

  BF6 (Part 16) compared with **0x800032780**. This build uses **0x800032782**.

### The unresolved-function trap per firmware
Every libkernel has exactly one `int 0x43; ret` after int3 padding: the function the loader binds unresolved imports to (PRX_NOT_RESOLVED_FUNCTION). Offsets (libkernel loads at 0x8_0000_0000):

| FW | trap | FW | trap |
|---|---|---|---|
| 1.00 | 0x2fb60 | 7.01 | 0x350a0 |
| 2.00–2.50 | 0x30c00 | 7.20–7.61 | 0x35100 |
| 3.00–3.20 | 0x31dd0 | 8.00–8.60 | 0x354e0 |
| **4.00–4.51** | **0x32780** | 9.00–9.60, 11.00–11.60 | 0x362e0 |
| 5.02–5.10 | 0x33380 | 10.01–10.60 | 0x35b90 |
| 5.50 | 0x33450 | 12.00–12.20 | 0x369c0 |
| 6.00–6.02 | 0x342c0 | 12.40–12.70 | 0x36a80 |
| 6.50 | 0x34330 | | |

- BF6's 0x800032780 is the FW 4.00–4.51 trap exactly. Horizon's **0x800032782 is the `ret` after it**, and it matches no firmware's trap, at 4.xx or at 5.xx (0x33380). Either the loader binds to trap+2 (not seen anywhere) or the guard never fires and the call reaches the trap. That's a console question for the testers.
- This table is what a generic **`guarded` fix kind** needs: the trap address for the target firmware, so one backport would use the real function where it exists and a fallback elsewhere. It isn't implemented yet. For the firmwares we can't decrypt (4.30, 7.00, 11.20, 13.60), neighbours with the same trap are likely but unconfirmed.

### PS VR2 detection (implemented 2026-10-09)
- **Signal: imports from `libSceHmd2*` (headset, reprojection) or `libSceVrTracker2`.** Of the 14 sample games only GT7 (optional VR mode: Hmd2 ×17, VrTracker2 ×6) and Horizon (VR-only: Hmd2 ×22, Hmd2ReprojectionPositional ×1, VrTracker2 ×7) import them.
- **Not usable**:
  - param.json `attribute` bit 29 (0x20000000) is also set on BF6, BL4, UFC 6 and DL, so it isn't VR;
  - `attribute3` differs for Horizon (0x001c0c00), but one VR-only sample can't name a "requires PS VR2" bit. The PS5 dev wiki's param.json page is behind a bot check.
  - The firmware lists can't date headset support either: 29 of Horizon's 30 VR imports exist from FW 2.00–4.00, and only the reprojection call is FW 6.50. When the console got headset support isn't visible in the libraries.
- Both engines: `GameResult.psvr2` (per game module and library, distinct NIDs; JSON `psvr2`), plus one WARNING saying that VR-only games can't run below the headset firmware and that optional-VR games usually run without it. App: a **"PS VR2" badge** in the game header after Analyze, whose tooltip lists the libraries; the warning appears in Overview → Warnings. Tests: `tests/test_psvr2.py`, `Psvr2BadgeTests`.
- Open: "supports" vs "requires" needs the param.json attribute bit (more VR samples or the wiki).

### Rule change
- **`uv0Ae+jCeWY` → ret0, DERIVED** (new). GT7 imports it too, so GT7's FW 4 backport now gets 7 eboot stubs and 29 unresolved imports (30 before); `test_gt7` and `GuiSupportTests` are updated. GT7 (console-tested at FW 4) left it unpatched because it doesn't call it without a headset, while a VR-only game does. Our pipeline at FW 4 now leaves nothing unresolved in Horizon, and both engines give identical files. We also apply the derived Mount3/Prepare and GetSpeakerInfo adapters, which the tester didn't need for a FW 5 target with a 5.00 param.

---

## Part 26: Kena: Bridge of Spirits (PPSA01746), a re-signed dump and an SDK-only FW 4.xx backport (2026-10-09)

Folders: `default` (v02.008.000, SDK 0x05000033/0x09590001, param 6.02/5.00; the user notes it works 5.xx–10.xx) and `4xx+`. Wwise plus the same audio plugin set as Horizon (`prx/ak*`, auro, izotope, mcdsp, masteringsuite, sceaudio3d), and `sce_module/` Face, FaceTracker, JobManager, NpCppWebApi, Pfs, libc. There's no README and the test status is unknown. The backport renames the title to "Kena: Bridge of Spirits By BADERLINK".

### `default` is a re-signed decrypted dump
- Every module has a **`.esbak`** next to it: the decrypted original ELF, with section headers. The PS5-style SELFs in `default` were rebuilt from them by an unknown tool, and the code is identical (`test_kena`). That tool:
  - **zeroed** the digest bytes after the program-header table;
  - **zeroed** the PT_SCE_VERSION block (plugins, eboot), or **cut it off**: the `sce_module` SELFs end at their last segment, and libc/libSceFace are 8 or 4 bytes short of their declared size;
  - **damaged `sce_sys/about/right.sprx`**: its last segment runs 0x40 bytes past the end of the file. Both engines refuse it, which is correct, and the backport ships a working one.
- **Sony system modules' PT_SCE_VERSION is a build-path string**, not records: e.g. libc's 0x1a bytes = `Prospero_Release/libc.prx `.

### Unwrapper generalised (both engines)
The trailing PT_SCE_VERSION block is now found in this order:
1. `name:` records at the end of the file, or within 0xF of the declared size (Yōtei −8, BL4 appended data);
2. otherwise the bytes after the last SELF segment, as they are (path string, Unity's opaque PS5Util block, zeros);
3. if nothing follows the last segment, zeros. The loader doesn't check this segment (Hades II README).

A declared size larger than the file is tolerated, as long as every entry lies inside the file. The "malformed/truncated trailing PT_SCE_VERSION data" errors are gone, and Part 22's special case is folded into rule 2. All 75 of Kena's readable modules unwrap identically in both engines.

### Wwise plugins without SDK fields
21 of the `prx/ak*` plugins (decrypted originals included) have an **all-zero PT_SCE_MODULE_PARAM**: no magic, no SDK. Our pipeline errored on them ("param segment magic 0x0"). Now `sdk.read_sdk`/`Sdk.Read` return (0, 0) and `write_sdk` changes nothing, so they're analysed and kept, reported as "SDK 0x00000000/0x00000000 (kept)". The tester shipped them unchanged too.

### What the backport does
- **SDK fields only**: eboot, libc and the SDK-carrying plugins go 5.00 → 0x04000031/0x09040001. No code patch anywhere, no fakelib/. param.json: `sdkVersion` 4.00, `requiredSystemSoftwareVersion` **left at 6.02**.
- `sce_module` SELFs re-signed with their version strings restored.
- An SDK 5 game needs nothing else at FW 4 by the tester's account. Our pipeline additionally applies the derived Mount3/Prepare and GetGamePresets adapters, plus the store's FW 4 fakelib set (the dump has no fakelib/). Both engines give identical files and reports.

---

## Part 27: Review of every sample folder (2026-10-09)

A sweep over all 15 samples with the current engines. Scripts are in the session scratchpad, not the repo.

### 1. Unwrap: every module in every folder (568 files), both engines
- **564 unwrap**, and C# matches Python byte for byte on all 536 SELFs (the other 32 are plain ELFs).
- The 4 failures are all non-code: GT7 `default/fakelib/libSceAmpr.sprx` (0 bytes), Kena's damaged `right.sprx` (Part 26), Silksong's retail-encrypted backport `right.sprx`, and **UFC 6's tested `right.sprx`, which is 12,512 zero bytes**.
- 42 modules have no SDK fields: Kena's Wwise plugins, `default` and `4xx+`.

### 2. Our pipeline on every original at the tester's firmware (20 runs)
- No crash. The only module error is Kena's damaged `right.sprx`.
- Unresolved imports that remain, all with evidence:
  - GT7: `sceHmd2ReprojectionSetParamWithBuffer` (FW 5.02, PSVR2; GT7 left it unpatched);
  - BF6/FC 27: libSceNpPremiumAddcont ×2 (FW 9.00; both backports left them);
  - Beast/FC 27: `sceCoredumpWriteUserString` (FW 8.00; both left it);
  - DL: the 112 EOS imports (left by the tested backport).
- **Closed: `gWT7X8H0bYs` (libSceVideoOutVrrStatus, FW 10.01), open since Part 14.** FW 10.01's libSceVideoOut shows it's a VRR status getter `(handle, status*)`. It writes exactly 0x50 bytes, or returns an error without writing. Yōtei ignores the return value and reads the struct (sometimes from uninitialised stack), so a plain ret0 is wrong. New stub **`out2_zero50`** (zero 0x50 bytes at rsi if not null, return 0): an all-zero status takes the game's "VRR off" path. Rule DERIVED. Yōtei now backports to FW 4 with nothing unresolved, identically in both engines.

### 3. Imports the testers touched vs our rules (14 original/backport pairs)
Every import a tester redirected, stubbed or renamed is covered by a rule, recorded as a firmware-specific technique (BF6 memory hooks, Beast `usleep`, BL4 `sceNetPoolCreate`), or a side effect of swapping libc (BL4, AC, Hades II: different libc builds, so the symbol-index comparison is noise there; the JobManager rename). **Nothing new was missed.**

### 4. Other files the testers add or change
- **`SHA256SUMS`** ships with three backports from different testers (AC, Control, Hades II), so testers can verify a copy.
- **`dlc_emu.ini`** (AC `4xx+`). The homebrew AppContent / NpEntitlementAccess / GameUpdate stubs that many dumps ship are **dlc_emu** (`C:/dev/ps5/dlc_emu`, the ampr_emu author's tree). They fake the entitlement services and read `/app0/dlc_emu.ini` (`[PSAL]` entries: label, download_status, *_visible, active_flag, entitlement_key…) and log to `/app0/dlc_emu.log`. AC's tester listed the game's ULC labels to unlock paid DLC. Recorded as fact; **not a Studio feature** (it unlocks content the user hasn't bought).
- `ampr_emu.index` ships in GT7's and AC's backports (stale-index risk, Part 14 checklist); `k9.psp` as before; Wwise games' `ShaderData.bin` (AC option).
- **Homebrew stubs re-signed at SDK 4.00** (BF6, Horizon): only the SDK field differs from the 2.00 originals. We keep 2.00, which is below every target.
- **ampr_emu inventory**: Aug 24 builds (ok), Sep 6 builds (known broken), and undated builds. GT7's (our `fakelibs/4`, also in BF6's and Control's dumps; 146 imports) and Horizon's (86 imports) carry no `__DATE__`, so `known_builds` can't name them.

### Follow-ups proposed (not done)
- **Done (same day): an unreadable `sce_sys/about/right.sprx` is a warning, not a module error** (both engines). It's left out of the output, and the dump's own copy stays in use under the overlay. It is **still lowered like any module when its SDK is above the target**: GT7's is SDK 10.00 (`0x10000040`; all other originals are 1.00), and GT7's console-tested FW 4 backport lowered it, so skipping the file outright would undo something a tested backport did. Evidence it isn't needed to start a game: UFC 6 ran on a Pro with an all-zero one, and Hades II, AC and Yōtei ship none. Tests: `test_phase0.test_unreadable_rights_module_is_a_warning`, `RightsModuleTests`.
- Write a **`SHA256SUMS`** next to the backport output (both engines), the convention three testers already use.

---

## Part 28: Kingdom Come: Deliverance II (PPSA21704), a minimal FW 4.xx backport (2026-10-09)

CryEngine (Warhorse), FMOD audio (`bin/prosperoshared/libfmod*.prx`, plus `pros.sdk.Prospero-9.000.prx` and `Quatmosphere.prx`), `sce_module/` libc and NpCppWebApi. `default` = v01.052.000 (SDK 0x09000040/0x11590001, param `sdkVersion` 9.00 and `requiredSystemSoftwareVersion` **12.20**; the user notes it works on **9.xx and higher**), with the dlc_emu AppContent / GameUpdate / NpEntitlementAccess stubs in `fakelib/`. `4xx+` = the FW 4.xx backport. No README, test status unknown.

### `4xx+` is an overlay, not a full folder
It has no `sce_sys/param.json` and no dlc_emu stubs: only the changed modules plus its own `fakelib/`. It's meant to be copied over the dump, so the dump's param.json (12.20 / 9.00) and dlc_emu stay in use.

### What changed (default → 4xx+)
- Every module re-signed as a PS4-style fake SELF (make_fself) with **SDK 0x04000031/0x09040001 (4.00)**. That includes `right.sprx`, whose SDK the tester **raised** from 1.00 to 4.00 (a blanket SDK pass; we keep 1.00, which is below every target).
- **libc**: the MapNamedFlexibleMemory → MapFlexibleMemory NID swap (rule `4h6F1LLbTiw`). libc's SyncOnAddressWait/Wake (`Hc4CaR6JBL0`, `q2y-wDIVWZA`) were **left unpatched**, as in GOWSOS.
- **eboot, 5 patches at call sites**, all `call <PLT>` → `xor eax,eax` + NOPs, except the last:
  - `sceVideoOutVrrPegToFixedRate`, `sceVideoOutVrrUnpegFromFixedRate`: ret0, as our stubs do.
  - `sceKernelIsTrinityMode` ×2: ret0, as our stub does.
  - **`scePsmlMfsrInit` (`3WVD91e12ZQ`)**: the game's one-instruction thunk `jmp scePsmlMfsrInit` became `xor eax,eax; ret`, so PSSR is never initialised and init reports success. **New: no other sample backport touches it.**
- SaveData Mount3/Prepare call sites untouched (our derived argument fixes are applied anyway: `when: always`).
- `fakelib/` = Agc, AgcDriver, Psml **byte-identical to `fakelibs/4.03`** (the FW 12.00 "4xx kit", Sony J03718109). No ampr_emu and no PlayGo stub: KCD2 imports nothing from libSceAmpr. (Our output still gets them from the store set: a dump needs them under ShadowMountPlus, Part 4.)

### Why KCD2 needed `scePsmlMfsrInit` disabled and other games didn't
`scePsmlMfsrInit` (and libScePsml as a whole) first appears in **FW 9.00** (firmware_db). Eight sample games import it, KCD2 included. Every other one calls it **only behind a PS5 Pro check**:
- Dying Light: `sceKernelIsTrinityMode() != 0` and `sceSysmoduleLoadModule(0x137) == 0`, then init;
- Borderlands 4: a flag plus `LoadModule(0x137)` succeeding;
- Control and GT7: global flags (Control's is a "Pro" bool getter).

With `sceKernelIsTrinityMode` stubbed to 0 those games never reach it on old firmware. **KCD2 calls `sceSysmoduleLoadModule(0x137)` and then `scePsmlMfsrInit` unconditionally**, from a renderer constructor (`0x1c0e24c`), and ignores the result. A second helper (`0x2a24330`, reached only through data) returns `init == 0` as "PSSR available". (0x137 is the libScePsml sysmodule id: every game loads it right before init. Inferred.) So on any console, KCD2 always runs MFSR init. The 9.xx floor the user observed for the original matches the firmware where libScePsml appears. Inferred, not console-verified: the FW 12.00 Psml fakelib's init fails or hangs below FW 9.00, and only KCD2 runs into it.

### Our pipeline on `default`
At FW 4 (`fakelibs/4`) and FW 4.03 (`fakelibs/4.03`), every import resolves, 0 errors. The VRR, Trinity and MapNamedFlexibleMemory rules cover the tester's other patches; KCD2 was added to their `sources` and to `left_unpatched` for SyncOnAddress. **Gap: `scePsmlMfsrInit` isn't stubbed**, because the Psml fakelib provides it (rules only fire for missing imports, unless a rule says `always`).

### Implemented: a default `scePsmlMfsrInit` rule below FW 9.00
- `data/fixes.json` `3WVD91e12ZQ`: `plt_stub` `ret0`, `when: always`, `below: 9.00`, DERIVED (source KCD2). It's `always` and `plt_stub` because the Psml fakelib provides the function. It's `below 9.00` because libScePsml first appears in 9.00 and the original runs from 9.xx. Other games only reach the call on a Pro, so the rule doesn't change what they do.
- **Option fix kind `none`** (new, both engines' fix-table builder): an option entry with `kind: "none"` removes the default fix for that NID. **`force_pro`** uses it to keep the real `scePsmlMfsrInit`, since PSSR needs it.
- KCD2 at FW 4 now gets `PLT -> ret0` for it, and at FW 9.60 nothing. C# and Python give byte-identical output folders (13 files). Test: `KnowledgeTests.FixTable_OptionOverridesDefault_AndBelowLimitsTargets` (the Python twin went with ps5bp on 2026-10-10). The GT7, Control, BF6, BL4 and AC `force_pro` parity runs still pass.

---

## Suggested reading order for reverse engineering

1. `PS5-SELF-Decrypter/include/self.h` and `source/main.c`: the SELF container and how plaintext ELFs are obtained.
2. `PS5-BACKPORK-KITCHEN/.../core/ElfPatcher.vb`, `VersionProfiles.vb`, and `PatchPipeLineRules-Important.txt`: the backport recipe.
3. `sdk/samples/install_app/make_fself.py`, then `PS5-BACKPORK-KITCHEN/.../signelf/`: fake signing.
4. `AnyPS5/core/relinker/relinker/src/parsing/ElfReader.cpp` and `core/libs/nid/`: PS5 ELF dynamic linking by NID.
5. `PS5-3.20_Libs/*.c` and `AnyPS5/tools/nid_names.py`: NID and name tables for Ghidra labelling and cross-firmware export diffs.
