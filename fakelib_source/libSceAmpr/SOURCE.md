# libSceAmpr source provenance

Imported on 2026-10-10 without implementation changes.

The public payload-SDK build port was added on the same date. All original
`src/` and `include/` files remain unchanged. Added build support and its few
local adaptations are documented in `PAYLOAD_BUILD.md`; the upstream
`.gitattributes` now also keeps `Makefile` line endings as LF.

- Upstream: https://github.com/drakmor/ampr_emu
- Release: https://github.com/drakmor/ampr_emu/releases/tag/0.3.6.6
- Tag: `0.3.6.6`
- Commit: `c343e32736ca5320aab5575b907f5ab916746296`
- Commit link: https://github.com/drakmor/ampr_emu/commit/c343e32736ca5320aab5575b907f5ab916746296
- License: upstream GPL-3.0, retained in `LICENSE`.

## Binary identification

`fakelibs/4.03/libSceAmpr.sprx` has SHA-256
`362c84e31e3a06a42f50fe4d6ea124d15e73f77c901470f90823935d7f36ca76`
and size 255126 bytes. Both match GitHub's release asset
`libSceAmpr.sprx-0.3.6.6-test` exactly. Its embedded strings include
`Aug 24 2026` and `0.3.6.6 (public beta) (c) Drakmor`.

The generic `fakelibs/4/libSceAmpr.sprx` is a different build: 218678 bytes,
SHA-256 `8248448608f36d8c937c77bd35b9b5b037b14d781544a14a39491a9bf88bc83b`.
It matches none of the asset hashes in the upstream GitHub release list checked
on the import date. Its exact source revision is unconfirmed. Do not label it
as 0.3.6.6 based on the firmware folder name.

## Import verification

The upstream tag was verified with `git ls-remote`. The snapshot was exported
from the existing local upstream clone at the full commit above. All 70 files
were checked against that commit's Git blob hashes using upstream line-ending
attributes (`text=auto eol=crlf`). No nested Git repository was added.

The exact release binary match identifies the associated tagged source; it does
not establish a reproducible build. This source has not been compiled here.
The tag provides the original `msbuild/` projects; it predates the later
ps5-payload-sdk Makefile. Existing compiled fakelibs were left unchanged.

## Compatibility scope

Local knowledge records console success for the Aug 24 build in specific 4.xx
backports. This is not certification for every game or firmware. Upstream's
0.3.6.6 release still reports a crash issue when working with saves.
The newer 0.4.2.1 source was deliberately not substituted for this baseline.
