#!/usr/bin/env python3
"""Batch classify / unwrap / verify PS5 SELF files.

Purpose: take a tree of PS5 firmware or game files and answer three questions
per file, then optionally convert the ones that are ready.

  * encrypted  -> real retail SELF whose program segments are still encrypted.
                  These CANNOT be decrypted on a PC; they must go back through
                  the console (ftpsrv 1.16-ng `SELF` mode, or PS5-SELF-Decrypter).
  * self       -> SELF with no encrypted segments (already decrypted, or
                  fake-signed). Safe to unwrap to a plain ELF with the CLI.
  * elf        -> already a plain ELF (e.g. ftpsrv `SELF`-mode output).
  * blob       -> not a SELF/ELF (e.g. the `$PS1` firmware .sbin blobs). Listed
                  but never touched.

With --unwrap it runs `ps5backport unwrap <self> <elf>` for every `self` file,
mirroring the relative directory layout into --out, and verifies every output
is a valid 64-bit little-endian x86-64 ELF.

Example:
  # just report what a firmware tree holds
  python scripts/selfbatch.py --src ".original/.PS5OriginalLibs/Firmware 13.60"

  # convert decrypted SELFs to ELFs after they come back from the console
  python scripts/selfbatch.py --src decrypted_13_60 --out decrypted_13_60_elf --unwrap
"""

import argparse
import os
import struct
import subprocess
import sys
from pathlib import Path

ELF_MAGIC = b"\x7fELF"
PS4_SELF_MAGIC = b"\x4f\x15\x3d\x1d"
PS5_SELF_MAGIC = b"\x54\x14\xf5\xee"
SEGMENT_ENCRYPTED = 1 << 1
EM_X86_64 = 0x3E
SPRX_EXTS = (".sprx", ".elf")
BLOB_EXTS = (".sbin",)

# CLI locations relative to the repo root, tried in order.
CLI_CANDIDATES = [
    "src/Ps5Backport.Cli/bin/Release/net10.0/win-x64/Ps5Backport.Cli.exe",
    "src/Ps5Backport.Cli/bin/Release/net10.0/Ps5Backport.Cli.exe",
    "src/Ps5Backport.Cli/bin/Debug/net10.0/win-x64/Ps5Backport.Cli.exe",
    "src/Ps5Backport.Cli/bin/Debug/net10.0/Ps5Backport.Cli.exe",
]


def classify(path: Path) -> str:
    """Return one of: elf, self, encrypted, blob, unknown."""
    if path.suffix.lower() in BLOB_EXTS:
        return "blob"
    try:
        with open(path, "rb") as f:
            head = f.read(0x2000)
    except OSError as e:
        print(f"  ! cannot read {path}: {e}", file=sys.stderr)
        return "unreadable"
    if len(head) < 4:
        return "unknown"
    magic = head[:4]
    if magic == ELF_MAGIC:
        return "elf"
    if magic not in (PS4_SELF_MAGIC, PS5_SELF_MAGIC):
        return "unknown"
    # SELF: count entry-table entries (u16 at 0x18) and scan props for the
    # SegmentEncrypted bit (bit 1 of each 8-byte props word).
    if len(head) < 0x1A:
        return "unknown"
    nent = struct.unpack_from("<H", head, 0x18)[0]
    for i in range(nent):
        at = 0x20 + i * 0x20
        if at + 8 > len(head):
            break
        props = struct.unpack_from("<Q", head, at)[0]
        if props & SEGMENT_ENCRYPTED:
            return "encrypted"
    return "self"


def is_valid_elf(path: Path) -> bool:
    try:
        with open(path, "rb") as f:
            head = f.read(0x20)
    except OSError:
        return False
    if len(head) < 0x14 or head[:4] != ELF_MAGIC:
        return False
    if head[4] != 2 or head[5] != 1:  # ELFCLASS64, little-endian
        return False
    return struct.unpack_from("<H", head, 0x12)[0] == EM_X86_64


def find_cli(explicit: str | None, repo: Path) -> str:
    if explicit:
        return explicit
    for rel in CLI_CANDIDATES:
        p = repo / rel
        if p.exists():
            return str(p)
    return "ps5backport"  # fall back to PATH


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--src", required=True, help="directory to scan")
    ap.add_argument("--out", help="output dir for unwrapped ELFs (only with --unwrap)")
    ap.add_argument("--unwrap", action="store_true", help="unwrap decrypted SELFs to ELFs")
    ap.add_argument("--cli", help="path to Ps5Backport.Cli.exe (auto-detected if omitted)")
    args = ap.parse_args()

    src = Path(args.src).resolve()
    if not src.is_dir():
        print(f"error: --src is not a directory: {src}", file=sys.stderr)
        return 2
    repo = src
    while not (repo / "Ps5Backport.slnx").exists() and repo.parent != repo:
        repo = repo.parent

    # Inventory
    files = [p for p in sorted(src.rglob("*")) if p.is_file()]
    counts = {"elf": 0, "self": 0, "encrypted": 0, "blob": 0, "unknown": 0, "unreadable": 0}
    buckets = {k: [] for k in counts}
    for p in files:
        k = classify(p)
        counts[k] += 1
        buckets[k].append(p)

    print(f"src: {src}")
    print(f"files scanned: {len(files)}")
    for k in ("elf", "self", "encrypted", "blob", "unknown", "unreadable"):
        if counts[k]:
            print(f"  {k:<10} {counts[k]}")
    if buckets["unknown"]:
        print("\nunknown-magic files (not SELF/ELF/PS1):")
        for p in buckets["unknown"]:
            print(f"  {p}")
    if buckets["unreadable"]:
        print("\nunreadable files:")
        for p in buckets["unreadable"]:
            print(f"  {p}")

    if not args.unwrap:
        if counts["encrypted"]:
            print("\nencrypted files still need console decryption "
                  "(ftpsrv 1.16-ng SELF mode / PS5-SELF-Decrypter) before they can be unwrapped.")
        return 0

    if not args.out:
        print("error: --unwrap requires --out", file=sys.stderr)
        return 2
    out = Path(args.out).resolve()
    cli = find_cli(args.cli, repo)

    ok, failed = 0, 0
    for p in buckets["self"]:
        rel = p.relative_to(src)
        dest = out / rel.with_suffix(".elf")
        dest.parent.mkdir(parents=True, exist_ok=True)
        cmd = [cli, "unwrap", str(p), str(dest)]
        try:
            r = subprocess.run(cmd, capture_output=True, text=True)
        except OSError as e:
            print(f"  FAIL {p}: cannot run CLI: {e}")
            failed += 1
            continue
        if r.returncode != 0 or not is_valid_elf(dest):
            print(f"  FAIL {p}: {r.stderr.strip() or r.stdout.strip() or 'invalid ELF output'}")
            failed += 1
            continue
        ok += 1

    print(f"\nunwrap: {ok} ok, {failed} failed -> {out}")
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
