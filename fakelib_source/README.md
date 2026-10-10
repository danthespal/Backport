# Fakelib sources

Each library has its own folder named after the module, without the `.sprx` extension. Keep upstream source, license, and build files together in that folder. Record the pinned upstream revision and binary evidence in `SOURCE.md` before changing the source.

## Imported libraries

- `libSceAmpr/`: drakmor/ampr_emu 0.3.6.6, source baseline associated with the exact release binary in `fakelibs/4.03/libSceAmpr.sprx`. See its `SOURCE.md` for evidence and limitations.

AMPR can now be built with the public payload SDK. See `libSceAmpr/PAYLOAD_BUILD.md`.

Source snapshots do not replace the compiled libraries in `fakelibs/`. Firmware compatibility remains specific to the build and tested game.
