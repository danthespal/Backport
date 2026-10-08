# Vendored third-party code

Both files are copied unchanged and are GPL-3.0 licensed, which also places `ps5bp` under GPL-3.0 when distributed.

| File | Source | Used for |
|---|---|---|
| `make_fself.py` | `drakmor/ampr_emu` `tools/make_fself.py` (upstream `ps5-payload-dev/sdk` `samples/install_app/make_fself.py` with two bugs fixed: `is_intervals_overlap` and the `segment_index` setter) | Fake-signing. Its output matches GT7's shipped SELFs byte for byte when the original ELF header is kept. |
| `make_fself_batch.py` | `drakmor/make_fself` | `unpack_fself_file()` only. Its signer writes a different (+288 byte) container and is not used. |
