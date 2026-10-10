# Build AMPR 0.3.6.6 with the public PS5 payload SDK

The pinned 0.3.6.6 implementation builds without the proprietary Prospero SDK.
All original `src/` and `include/` files are unchanged, including the 64 MiB pool,
command-buffer tracking, reactor, and hook implementation.

## Build on this machine

Ubuntu WSL, GNU make, Python 3 and LLVM are installed. The public SDK is at
`E:\PS5_TOOLS\ps5-payload-sdk`. From PowerShell:

```powershell
wsl -d Ubuntu --cd E:\PS5_TOOLS\Backport\fakelib_source\libSceAmpr -- make -j2 PS5_PAYLOAD_SDK=/mnt/e/PS5_TOOLS/ps5-payload-sdk verify
```

On Linux, from this source folder:

```sh
make -j2 PS5_PAYLOAD_SDK=/path/to/ps5-payload-sdk verify
```

Outputs:

- `out/payload-sdk/libSceAmpr.prx`: linked ELF module, usable for analysis.
- `out/payload-sdk/libSceAmpr.sprx`: fake-signed module for subsequent console testing.

`verify` checks module type, program headers, SDK records, initialization/finalization
sections, all 118 export NIDs, import naming/providers, corrected export hash buckets,
and SELF version fields. This audit is self-contained and does not require a game dump
or the original ELF. The reference export manifest records the published 0.3.6.6
binary hashes and the exported NIDs read through Ghidra.

## Build support provenance

Build-only files were imported from upstream commit
`0ed2729883522682071b4a1ab91b0fe70d6a29e1` (first ps5-payload-sdk support):

- `Makefile`
- `ps5/crt/`, `ps5/include/sce/`, `ps5/prx/`
- `tools/generate_payload_exports.py`, `prepare_prx_link_script.py`,
  `prx_hash_fix.py`, `stamp_payload_prx.py`, `verify_payload_prx.py`

The later commit's reactor/event implementation changes were not imported.
Local build adaptations:

- The added CRT uses the existing `Need_sceLibc` anchor from 0.3.6.6, so
  `src/ampr_libc_internal_link.cpp` does not need modification.
- The verifier compares with `ps5/reference/0.3.6.6-exports.json`, replacing an
  upstream dependency on an unavailable private reference disassembly and audit script.
- `.gitattributes` preserves LF for the new Makefile.

The build uses C++17, PIC, O3, hidden internal symbols, no exceptions/RTTI, the
upstream public PRX CRT, and SDK-compatible ABI declarations. It is a new toolchain
build, not a byte-for-byte reproduction of the SDK 2.000 release.

## Verified result, 2026-10-10

Compiler: Ubuntu Clang 21.1.8 targeting x86_64-sie-ps5; linker: LLD 21.1.8;
Python: 3.14.4. Compilation completed without reported warnings or errors.

- Structural audit passed: 118 AMPR exports, 122 dynamic definitions including
  module markers, 111 imports (9 libc-internal and 102 kernel).
- PS5 SDK `0x02000009`, PS4 SDK `0x08050001`; SELF app/firmware fields `0x02000001`.
- Studio reports zero missing and zero unknown function imports across all 38
  available firmware databases, from 4.00 through 12.70. The rebuilt output has no
  data imports in those reports.
- Signing and unwrapping preserved all five nonempty PT_LOAD segments byte-for-byte.
  The full ELF hashes differ because the wrapper does not preserve the complete
  non-loadable section-table tail.
- The verifier rejects both an incorrect PRX type and an incomplete reference export set.

Built SPRX SHA-256:
`9309e449d990d2135aa1777085d393eab207db1cc6cb1c59a76cdf0896c875da`

Built PRX SHA-256:
`31275a68d85f19d3130d06707bde00429720e473ac116c1bb008463689c5101f`

Build logs and firmware reports are in `work/ampr-source-validation/` at the Studio
repository root. These are local scratch artifacts, not checked-in binaries.

## Console verification still required

This is a build with static compatibility checks, not a console-tested replacement.
Existing `fakelibs/` entries were not replaced. Keep a copy of the known working game
overlay and original AMPR binary when testing this separate output.

Check module startup and hook installation on 4.xx, then asset loading, repeated
transitions, saving and loading saves, and asynchronous read completion. Record game
version, firmware, console model, module hash, and failures. The existing 0.3.6.6
release has a known save-related crash issue; this build port does not fix that issue.
Later packed-asset features were not imported, so test with the loose-file/index
layout supported by 0.3.6.6.
