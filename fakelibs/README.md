# Per-firmware fakelib store

`ps5backport backport` (and the app) takes its fakelibs from here when `--fakelib` is not given.

- Folders are named by target firmware: `4`, `4.03`, `7`, …
- Lookup tries the exact version first (`4.03`), then the major (`4`).
- Each folder has a `manifest.json` recording SHA-256, size, container, SDK fields, the module's embedded build path, and where the files came from.
- `backport` refuses to use a folder whose files don't match its manifest.
- A set can have an **`app0/` folder**: data files its libraries read from the game folder. A file is copied to the output's game root only when a fakelib of the set that the game uses contains `/app0/<file>` (and the game doesn't ship it). `4`, `9.60`, `10` and `11` carry `app0/k9.psp` (FW 13.60's PSSR model `KPN9_v070.psp`, renamed), which their FW 13.60 `libScePsml` reads from `/app0/k9.psp` on a PS5 Pro.
- A set can have **variant subfolders** (`<FW>/<variant>/`, own manifest) with opt-in replacements for some of its files. An option in `data/fixes.json` that names the variant (`fakelib_variant`) swaps them in; add files with `fakelib add ... --variant NAME`.

```bash
ps5backport fakelib add path/to/*.sprx --firmware 4.03 --source "where they came from"
ps5backport fakelib list
ps5backport fakelib verify
```

Current contents (2026-10-10):

| Folder | Source |
|---|---|
| `4` | GT7 1.71 USA FW 4 backport (console-tested) |
| `4.03` | CONTROL Resonant 1.300 FW 4.03 backport ("4xx kit" Agc/AgcDriver/Psml, `ampr_emu` 24 Aug build; tested on FW 10.01) |
| `9.60` | EA SPORTS UFC 6 backport tested on a PS5 Pro at FW 9.60: Agc, AgcDriver, Psml from Sony build J03950001 (FW 13.x, SDK 4.00), ampr_emu Aug 24; the dump's PlayGo stub. Shipped as plain ELF, fake-signed for the store. |
| `10`, `11` | Only `libScePsml` (FW 13.60, J03950001: the file in `4`) + `app0/k9.psp`, as the Ghost of Yōtei FW 10.x backport ships them (NOT TESTED). FW 10–11 lack the FW 12.00 `libScePsml_mfsr2` exports that PSSR games import. No ampr_emu/PlayGo: the dump's own are used. |
| `7` | BackPork `patches/7xx` applied to the own FW 10.01 dump (Agc, AgcDriver, NpAuth, NpAuthAuthorizedAppDialog, SaveData.native), plus `ampr_emu` (24 Aug) and the PlayGo stub copied from `4.03` (2026-10-09: dumps need them). No Psml yet. |

Variants:

| Folder | Source |
|---|---|
| `4/gpu_scratch`, `4.03/gpu_scratch` | `libSceAgcDriver` with AC Black Flag Resynced's compute-scratch hook (option `gpu_scratch`): `4` = AC `10xx+` (GT7's AgcDriver build + hook), `4.03` = AC `4xx+` (Control's build + hook). |
| `10/ppsa34008` | Complete five-library set copied from Mortal Shell II PPSA34008 01.000.010 `10xx+`: Agc, AgcDriver and Psml = Sony J03828141 (12.70), with patched imports/lowered SDKs; Ampr = Aug 24 build; supplied PlayGo stub. Hashes and provenance are recorded in its manifest. The 10.xx target comes from the supplied backport; console compatibility was not independently verified. This set is retained separately from the existing `10` PSML/model combination and is not certified for 4.xx. |
| `4/ampr_debug`, `4.03/ampr_debug`, `7/ampr_debug`, `9.60/ampr_debug` | Official `drakmor/ampr_emu` 0.3.6.6 `test-debug` (414,614 bytes, SHA-256 `27dec32b…`), selected because the normal AMPR in `4.03` and `7` is the exact 0.3.6.6 `test` asset (255,126 bytes, SHA-256 `362c84e3…`). The debug module has the same 118 exports, adds 14 logging-related imports, and contains `/app0/ampr_emu.log`. The GUI's **Enable AMPR emulator logging** toggle swaps it in for one diagnostic run. It is not a release library and is not console-tested on these firmware sets; its imports classify cleanly against FW 4.00, 4.03, 6.50 lower bound for 7, and 9.60. |

**Do not publish this folder.** It contains console system libraries (Sony's Agc/AgcDriver/Psml), unlike the app itself.

Select the imported PPSA34008 set explicitly in Studio's extra fakelib folder, or with `--fakelib fakelibs/10/ppsa34008`. A variant folder is not selected by firmware lookup alone, and this research set has no automatic patch option. The original game sample stays intact. For other imported sets, use `fakelib add ... --firmware <target> --variant <source_label> --source <provenance>` when an existing default set should be preserved.
