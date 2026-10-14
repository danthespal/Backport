#!/usr/bin/env python3
"""Fetch decrypted PS5 firmware library files from a compatible FTP payload.

Usage:
    python fetch_firmware_libs.py IP_ADDRESS:PORT OUTPUT_FOLDER

Example for a Firmware 4.30 or 7.00 dump folder:
    python scripts/fetch_firmware_libs.py 192.168.1.50:2121 \
        ".original/.PS5OriginalLibs/Firmware 4.30"

The output is written to both:
    OUTPUT_FOLDER/system_dec/common/lib/
    OUTPUT_FOLDER/system_ex_dec/common_ex/lib/

That layout is deliberate: Ps5Backport's `fwdb import-firmwares` importer merges
that decrypted copy with the existing encrypted `system/common/lib` dump and
refuses to create a partial database when a module is missing. The
`system_ex_dec` copy preserves the decrypted counterpart of
`/system_ex/common_ex/lib` without overwriting the encrypted source dump.

The target FTP payload must support the non-standard `SELF` command and decrypt
PS5 SELF files during download. The script never decrypts retail SELF files on
the PC: it verifies each downloaded system module is already a 64-bit x86-64
ELF before saving it.

Only the Python standard library is used.
"""

from __future__ import annotations

import ftplib
import sys
from pathlib import Path

ELF_MAGIC = b"\x7fELF"
EM_X86_64 = 0x3E
LIBRARY_TREES = (
    ("/system/common/lib", Path("system_dec/common/lib")),
    ("/system_ex/common_ex/lib", Path("system_ex_dec/common_ex/lib")),
)
PROBE = "/system/common/lib/libkernel.sprx"
SELF_EXTENSIONS = (".sprx", ".elf", ".self", ".prx")
TRANSFER_TIMEOUT_SECONDS = 600
DOWNLOAD_ATTEMPTS = 3


def usage() -> None:
    print(__doc__.strip())


def parse_endpoint(value: str) -> tuple[str, int]:
    """Parse the requested IPv4-style HOST:PORT endpoint."""
    host, separator, port_text = value.rpartition(":")
    if not separator or not host or not port_text:
        raise ValueError("endpoint must be IP_ADDRESS:PORT, for example 192.168.1.50:2121")
    try:
        port = int(port_text, 10)
    except ValueError as exc:
        raise ValueError("port must be a decimal integer") from exc
    if not 1 <= port <= 65535:
        raise ValueError("port must be in the range 1-65535")
    return host, port


def is_x86_64_elf(data: bytes) -> bool:
    return (
        len(data) >= 0x14
        and data[:4] == ELF_MAGIC
        and data[4] == 2                 # ELFCLASS64
        and data[5] == 1                 # little-endian
        and int.from_bytes(data[0x12:0x14], "little") == EM_X86_64
    )


def retrieve(ftp: ftplib.FTP, remote_path: str) -> bytes:
    data = bytearray()
    ftp.retrbinary("RETR " + remote_path, data.extend)
    return bytes(data)


def close_quietly(ftp: ftplib.FTP | None) -> None:
    if ftp is None:
        return
    try:
        ftp.quit()
    except (OSError, EOFError) + ftplib.all_errors:
        try:
            ftp.close()
        except OSError:
            pass


def ensure_self_mode(ftp: ftplib.FTP) -> None:
    """Ensure the target decrypts SELF files, without assuming its initial mode."""
    before = retrieve(ftp, PROBE)
    if is_x86_64_elf(before):
        print("SELF transfer mode is already returning decrypted ELFs.")
        return

    try:
        reply = ftp.sendcmd("SELF")
    except ftplib.all_errors as exc:
        raise RuntimeError(
            "the FTP server rejected the SELF command. Use a firmware-compatible "
            "payload/server that decrypts SELF files during transfer."
        ) from exc

    print("SELF command: " + reply.strip())
    after = retrieve(ftp, PROBE)
    if is_x86_64_elf(after):
        print("SELF transfer mode is now returning decrypted ELFs.")
        return

    raise RuntimeError(
        "libkernel.sprx is still not a decrypted x86-64 ELF after SELF mode was enabled. "
        "Do not use this output for firmware_db."
    )


def connect_decrypting(host: str, port: int, announce: bool = False) -> ftplib.FTP:
    """Open one FTP session and verify its SELF transfers return decrypted ELFs."""
    ftp = ftplib.FTP()
    try:
        if announce:
            print(f"Connecting to {host}:{port} ...")
        ftp.connect(host, port, timeout=TRANSFER_TIMEOUT_SECONDS)
        ftp.login("anonymous", "ps5")
        if announce:
            print("Connected: " + ftp.getwelcome().splitlines()[-1])
        ensure_self_mode(ftp)
        return ftp
    except (OSError, EOFError, RuntimeError) + ftplib.all_errors:
        close_quietly(ftp)
        raise


def list_dir(ftp: ftplib.FTP, remote_dir: str) -> list[str]:
    """Return direct child names, falling back when a payload lacks NLST."""
    ftp.cwd(remote_dir)
    lines: list[str] = []
    try:
        ftp.retrlines("NLST", lines.append)
    except ftplib.error_perm as exc:
        # Some minimal payload FTP servers implement LIST but not NLST (502).
        if not str(exc).startswith(("500", "501", "502", "504")):
            raise
        lines.clear()
        ftp.retrlines("LIST", lines.append)

    names: list[str] = []
    for line in lines:
        # Standard LIST output has eight metadata columns followed by the file name;
        # ftpsrv variants may return just the name, as NLST does.
        parts = line.split(maxsplit=8)
        name = parts[8] if len(parts) == 9 and line[:1] in {"-", "d", "l"} else line
        name = name.rsplit("/", 1)[-1]
        if name not in (".", ".."):
            names.append(name)
    return names


def download_with_retry(ftp: ftplib.FTP, host: str, port: int, remote: str) -> tuple[bytes, ftplib.FTP]:
    """Transfer one module, replacing the connection after a timed-out data socket."""
    last_error: BaseException | None = None
    for attempt in range(1, DOWNLOAD_ATTEMPTS + 1):
        try:
            return retrieve(ftp, remote), ftp
        except (OSError, EOFError) + ftplib.all_errors as exc:
            last_error = exc
            if attempt == DOWNLOAD_ATTEMPTS:
                break
            print(f"  retry {attempt}/{DOWNLOAD_ATTEMPTS - 1}: {Path(remote).name}: {exc}", file=sys.stderr)
            close_quietly(ftp)
            ftp = connect_decrypting(host, port)
    raise RuntimeError(f"download failed after {DOWNLOAD_ATTEMPTS} attempts: {last_error}")


def download_library_tree(
    ftp: ftplib.FTP, host: str, port: int, remote_dir: str, destination: Path
) -> tuple[int, int, ftplib.FTP]:
    destination.mkdir(parents=True, exist_ok=True)

    ok = failed = 0
    names = list_dir(ftp, remote_dir)
    if not names:
        raise RuntimeError(f"{remote_dir} is empty or could not be listed")

    for name in names:
        remote = remote_dir + "/" + name
        # Ignore subdirectories and non-module payload data if the FTP listing contains any.
        if not name.lower().endswith(SELF_EXTENSIONS):
            continue
        try:
            data, ftp = download_with_retry(ftp, host, port, remote)
        except (OSError, EOFError, RuntimeError) + ftplib.all_errors as exc:
            print(f"FAIL {name}: download failed: {exc}", file=sys.stderr)
            failed += 1
            continue
        if not is_x86_64_elf(data):
            print(f"FAIL {name}: not a decrypted x86-64 ELF (magic {data[:4].hex()})", file=sys.stderr)
            failed += 1
            continue
        (destination / name).write_bytes(data)
        ok += 1

    return ok, failed, ftp


def main(argv: list[str]) -> int:
    if len(argv) != 3 or argv[1] in {"-h", "--help"}:
        usage()
        return 0 if len(argv) == 2 else 2

    try:
        host, port = parse_endpoint(argv[1])
    except ValueError as exc:
        print("error: " + str(exc), file=sys.stderr)
        return 2

    output = Path(argv[2]).expanduser().resolve()
    ftp: ftplib.FTP | None = None
    try:
        ftp = connect_decrypting(host, port, announce=True)
        ok = failed = 0
        for remote_dir, relative_destination in LIBRARY_TREES:
            destination = output / relative_destination
            print(f"Downloading {remote_dir} -> {destination}")
            tree_ok, tree_failed, ftp = download_library_tree(ftp, host, port, remote_dir, destination)
            ok += tree_ok
            failed += tree_failed
    except (OSError, EOFError, RuntimeError) + ftplib.all_errors as exc:
        print("error: " + str(exc), file=sys.stderr)
        return 1
    finally:
        close_quietly(ftp)

    print(f"Downloaded: {ok} decrypted modules, {failed} failed -> {output}")
    if failed:
        return 1
    if not ok:
        print("error: no modules were downloaded", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
