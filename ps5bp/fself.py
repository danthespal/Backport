"""SELF (fake-signed ELF) unwrap and fake-sign.

Unwrapping keeps the original ELF header from inside the SELF (the stock
unpacker zeroes the section-header fields, which can't be restored). With the
original header kept, unwrap -> sign reproduces GT7's shipped files byte for byte,
except the ex-info digest: a SHA-256 of the whole original ELF file, which can't
be recomputed when the original had data outside its segments (section headers).
"""
import contextlib
import io
import os
import struct
import tempfile

from ps5bp.vendor import make_fself as _signer
from ps5bp.vendor import make_fself_batch as _unpacker

SELF_MAGICS = {
    b'\x4f\x15\x3d\x1d': 'PS4-style fake SELF (make_fself.py)',
    b'\x54\x14\xf5\xee': 'PS5-style SELF',
}
ELF_MAGIC = b'\x7fELF'
DEFAULT_PAID = 0x3100000000000002


def file_kind(path):
    with open(path, 'rb') as f:
        magic = f.read(4)
    if magic == ELF_MAGIC:
        return 'ELF'
    return SELF_MAGICS.get(magic, 'unknown')


def _embedded_elf_header(self_data):
    """The 64-byte ELF header stored after the SELF header and segment entries."""
    count = struct.unpack_from('<H', self_data, 0x18)[0]
    start = 0x20 + count * 0x20
    header = self_data[start:start + 0x40]
    if header[:4] != ELF_MAGIC:
        raise ValueError('ELF header not found inside SELF')
    return header


SEGMENT_ENCRYPTED = 1 << 1   # SELF segment-entry flag (PS5-SELF-Decrypter include/self.h)


def encrypted_segments(self_path):
    """Number of SELF segment entries flagged as encrypted. Fake-signed files have none;
    a real (retail) SELF must be decrypted on a console first (PS5-SELF-Decrypter)."""
    with open(self_path, 'rb') as f:
        head = f.read(0x20)
        count = struct.unpack_from('<H', head, 0x18)[0]
        entries = f.read(count * 0x20)
    return sum(1 for i in range(count) if struct.unpack_from('<Q', entries, i * 0x20)[0] & SEGMENT_ENCRYPTED)


def digest_offset(self_data):
    """File offset of the 32-byte ELF digest in a make_fself SELF's ex-info."""
    count = struct.unpack_from('<H', self_data, 0x18)[0]
    ehdr = 0x20 + count * 0x20
    phnum = struct.unpack_from('<H', self_data, ehdr + 0x38)[0]
    ex_info = (ehdr + 0x40 + phnum * 0x38 + 15) & ~15
    return ex_info + 0x20


def unwrap(self_path, elf_path):
    """SELF -> ELF, keeping the original ELF header."""
    with open(self_path, 'rb') as f:
        header = _embedded_elf_header(f.read(0x1000))
    with contextlib.redirect_stdout(io.StringIO()):
        _unpacker.unpack_fself_file(self_path, elf_path)
    with open(elf_path, 'r+b') as f:
        f.write(header)


def sign(elf_path, self_path, paid=None, ptype=None, app_version=0, fw_version=0):
    """ELF -> fake-signed SELF with make_fself.py defaults (as used for GT7)."""
    with open(elf_path, 'rb') as f:
        elf_file = _signer.ElfFile(ignore_shdrs=True)
        elf_file.load(f)
    self_file = _signer.SignedElfFile(
        elf_file,
        paid=DEFAULT_PAID if paid is None else paid,
        ptype=_signer.SignedElfExInfo.PTYPE_FAKE if ptype is None else ptype,
        app_version=app_version,
        fw_version=fw_version,
        auth_info=None,
    )
    # Write to a temp file first so a failure never leaves a truncated output.
    fd, tmp = tempfile.mkstemp(dir=os.path.dirname(os.path.abspath(self_path)), suffix='.tmp')
    try:
        with os.fdopen(fd, 'wb') as f, contextlib.redirect_stdout(io.StringIO()):
            self_file.save(f)
        os.replace(tmp, self_path)
    except BaseException:
        if os.path.exists(tmp):
            os.remove(tmp)
        raise
