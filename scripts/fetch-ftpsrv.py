#!/usr/bin/env python3
"""Pull decrypted PS5 firmware files from a jailbroken console's ftpsrv.

ftpsrv (Drakmor's 1.16-ng) runs an FTP server (default port 2121) on the
jailbroken console and can decrypt SELF files on the fly (SELF->ELF). On
1.16-ng-stable SELF mode is OFF by default; the `SELF` FTP command *toggles* it
per-connection and its reply reports the new state ("... enabled/disabled").
This script calibrates: download one known SELF, and if still encrypted, toggle
SELF on and verify.

Usage:
  python scripts/fetch-ftpsrv.py --host 192.168.1.132 \
      --root /system --paths common/lib common/psml_package priv/lib \
      --dest ".original/.PS5OriginalLibs/Firmware 13.60/system_dec"

Remote /system/<path> is mirrored into <dest>/<path>, layout preserved. Every
.sprx/.elf/.self must come back as a valid 64-bit little-endian x86-64 ELF;
.sbin and .psp are not SELF files and are copied as-is.
"""

import argparse
import ftplib
import sys
from pathlib import Path

ELF_MAGIC = b"\x7fELF"
PS5_SELF_MAGIC = b"\x54\x14\xf5\xee"
PS4_SELF_MAGIC = b"\x4f\x15\x3d\x1d"
EM_X86_64 = 0x3E
SELF_EXTS = (".sprx", ".elf", ".self")


def is_valid_elf(data: bytes) -> bool:
    if len(data) < 0x14 or data[:4] != ELF_MAGIC:
        return False
    if data[4] != 2 or data[5] != 1:  # ELFCLASS64, little-endian
        return False
    return int.from_bytes(data[0x12:0x14], "little") == EM_X86_64


def is_self(data: bytes) -> bool:
    return data[:4] in (PS4_SELF_MAGIC, PS5_SELF_MAGIC)


def connect(host: str, port: int) -> ftplib.FTP:
    ftp = ftplib.FTP()
    ftp.connect(host, port, timeout=30)
    ftp.login("anonymous", "ps5")
    return ftp


def ensure_self_decrypt(ftp: ftplib.FTP, probe: str) -> None:
    """Download one known SELF; if still encrypted, toggle SELF on and verify."""

    def fetch() -> bytes:
        b = bytearray()
        ftp.retrbinary("RETR " + probe, b.extend)
        return bytes(b)

    try:
        data = fetch()
    except ftplib.error_perm as e:
        print(f"  ! cannot probe {probe}: {e}", file=sys.stderr)
        return
    if is_valid_elf(data):
        print("  SELF->ELF already on")
        return
    if not is_self(data):
        print(f"  ! probe {probe} is neither SELF nor ELF ({data[:4].hex()}); "
              "skipping calibration", file=sys.stderr)
        return
    reply = ftp.sendcmd("SELF")
    print(f"  SELF toggled: {reply.strip()}")
    if "disabled" in reply.lower():       # was ON, now OFF -> toggle back ON
        reply = ftp.sendcmd("SELF")
        print(f"  SELF toggled again: {reply.strip()}")
    if not is_valid_elf(fetch()):
        print("  ! still encrypted after toggling SELF; "
              "is ftpsrv 1.16-ng running on a 13.60 console?", file=sys.stderr)


def download_file(ftp: ftplib.FTP, remote_path: str, dest: Path) -> bool:
    dest.parent.mkdir(parents=True, exist_ok=True)
    buf = bytearray()
    ftp.retrbinary("RETR " + remote_path, buf.extend)
    data = bytes(buf)
    name = Path(remote_path).name
    if name.lower().endswith(SELF_EXTS):
        if not is_valid_elf(data):
            print(f"  FAIL {remote_path}: not a valid ELF after download "
                  f"(magic {data[:4].hex()})")
            return False
    dest.write_bytes(data)
    return True


def mirror(ftp: ftplib.FTP, remote_dir: str, local_prefix: str,
           dest_root: Path) -> tuple[int, int]:
    ok = failed = 0
    stack = [remote_dir.rstrip("/")]
    while stack:
        cur = stack.pop()
        ftp.cwd(cur)
        names = []
        ftp.retrlines("NLST", names.append)
        for name in names:
            if name in (".", ".."):
                continue
            remote = cur + "/" + name
            # NLST gives no type; probe with CWD to tell dir from file.
            try:
                ftp.cwd(remote)
                stack.append(remote)   # it's a directory
                ftp.cwd(cur)
                continue
            except ftplib.error_perm:
                pass
            rel = Path(cur).relative_to(remote_dir)
            local = dest_root / local_prefix / rel / name
            if download_file(ftp, remote, local):
                ok += 1
            else:
                failed += 1
    return ok, failed


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", required=True, help="console IP")
    ap.add_argument("--port", type=int, default=2121)
    ap.add_argument("--root", default="/system", help="remote mount root")
    ap.add_argument("--paths", nargs="+", default=["common/lib", "priv/lib"],
                    help="subdirs under --root to mirror")
    ap.add_argument("--probe", default=None,
                    help="known SELF file used to calibrate SELF mode")
    ap.add_argument("--dest", required=True, help="local output dir (layout preserved)")
    args = ap.parse_args()

    ftp = connect(args.host, args.port)
    try:
        print(f"connected to {args.host}:{args.port} "
              f"({ftp.getwelcome().splitlines()[-1]})")
        probe = args.probe or (args.root.rstrip("/") + "/common/lib/libkernel.sprx")
        ensure_self_decrypt(ftp, probe)

        total_ok = total_fail = 0
        for sub in args.paths:
            remote_dir = args.root.rstrip("/") + "/" + sub.strip("/")
            print(f"mirroring {remote_dir} -> {args.dest}/{sub}")
            ok, failed = mirror(ftp, remote_dir, sub, Path(args.dest))
            total_ok += ok
            total_fail += failed
        print(f"\ndone: {total_ok} ok, {total_fail} failed -> {args.dest}")
        return 0 if total_fail == 0 else 1
    finally:
        ftp.quit()


if __name__ == "__main__":
    raise SystemExit(main())
