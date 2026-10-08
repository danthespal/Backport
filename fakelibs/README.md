# Per-firmware fakelib store

`ps5bp backport` takes its fakelibs from here when `--fakelib` is not given.

- Folders are named by target firmware: `4`, `4.03`, `7`, …
- Lookup tries the exact version first (`4.03`), then the major (`4`).
- Each folder has a `manifest.json` recording SHA-256, size, container, SDK fields, the module's embedded build path, and where the files came from.
- `backport` refuses to use a folder whose files don't match its manifest.

```bash
python -m ps5bp fakelib add path/to/*.sprx --firmware 4.03 --source "where they came from"
python -m ps5bp fakelib list
python -m ps5bp fakelib verify
```

Current contents (2026-10-08):

| Folder | Source |
|---|---|
| `4` | GT7 1.71 USA FW 4 backport (console-tested) |
| `4.03` | CONTROL Resonant 1.300 FW 4.03 backport ("4xx kit" Agc/AgcDriver/Psml, `ampr_emu` 24 Aug build; tested on FW 10.01) |

**Do not publish this folder.** It contains console system libraries (Sony's Agc/AgcDriver/Psml), unlike `ps5bp` itself.
