"""BPS patches (beat format), as shipped by BackPork for 10.01 libraries -> 6xx/7xx.

    "BPS1" varint(source_size) varint(target_size) varint(metadata_size) metadata
    actions...  source_crc32 target_crc32 patch_crc32   (little-endian u32 each)

Actions: varint(((length - 1) << 2) | command)
  0 SourceRead  copy `length` bytes of the source at the current output offset
  1 TargetRead  `length` literal bytes follow in the patch
  2 SourceCopy  signed varint delta for the source cursor, then copy from the source
  3 TargetCopy  signed varint delta for the target cursor, then copy from the output (may overlap)

`edits()` reads the literal writes without the source file, so we can see what a
patch changes before having the library it applies to.
"""
import struct
import zlib
from dataclasses import dataclass

MAGIC = b'BPS1'
SOURCE_READ, TARGET_READ, SOURCE_COPY, TARGET_COPY = range(4)


def _varint(data, pos):
    value, shift = 0, 1
    while True:
        x = data[pos]
        pos += 1
        value += (x & 0x7F) * shift
        if x & 0x80:
            return value, pos
        shift <<= 7
        value += shift


def _signed(data, pos):
    v, pos = _varint(data, pos)
    return (-(v >> 1) if v & 1 else v >> 1), pos


@dataclass
class Patch:
    source_size: int
    target_size: int
    metadata: bytes
    actions: list          # (command, length, payload bytes | cursor delta | None)
    source_crc: int
    target_crc: int


def parse(data):
    if data[:4] != MAGIC:
        raise ValueError('not a BPS patch')
    if len(data) < 16:
        raise ValueError('BPS patch truncated')
    source_crc, target_crc, patch_crc = struct.unpack_from('<III', data, len(data) - 12)
    if zlib.crc32(data[:-4]) != patch_crc:
        raise ValueError('BPS patch checksum mismatch (corrupt file)')
    pos = 4
    source_size, pos = _varint(data, pos)
    target_size, pos = _varint(data, pos)
    meta_size, pos = _varint(data, pos)
    metadata = data[pos:pos + meta_size]
    pos += meta_size
    actions, end = [], len(data) - 12
    while pos < end:
        v, pos = _varint(data, pos)
        command, length = v & 3, (v >> 2) + 1
        if command == TARGET_READ:
            actions.append((command, length, data[pos:pos + length]))
            pos += length
        elif command in (SOURCE_COPY, TARGET_COPY):
            delta, pos = _signed(data, pos)
            actions.append((command, length, delta))
        else:
            actions.append((command, length, None))
    return Patch(source_size, target_size, metadata, actions, source_crc, target_crc)


def load(path):
    with open(path, 'rb') as f:
        return parse(f.read())


@dataclass
class Edit:
    offset: int            # output file offset
    kind: str              # 'write' (literal bytes) | 'source-copy' | 'target-copy'
    length: int
    data: bytes = b''      # literal bytes for 'write'
    from_offset: int = 0   # copy origin for the copy kinds


def edits(patch):
    """Every region of the output that is not an in-place source read, in output order."""
    out, pos, src_cur, tgt_cur = [], 0, 0, 0
    for command, length, arg in patch.actions:
        if command == TARGET_READ:
            out.append(Edit(pos, 'write', length, arg))
        elif command == SOURCE_COPY:
            src_cur += arg
            if src_cur != pos:
                out.append(Edit(pos, 'source-copy', length, from_offset=src_cur))
            src_cur += length
        elif command == TARGET_COPY:
            tgt_cur += arg
            out.append(Edit(pos, 'target-copy', length, from_offset=tgt_cur))
            tgt_cur += length
        pos += length
    return out


def apply(patch, source):
    """Patched bytes. Checks the source and result CRC32 so a wrong input library is refused."""
    if len(source) != patch.source_size or zlib.crc32(source) != patch.source_crc:
        raise ValueError(f'source does not match the patch (needs {patch.source_size} bytes, '
                         f'CRC32 {patch.source_crc:08x}; got {len(source)} bytes, CRC32 {zlib.crc32(source):08x})')
    out = bytearray()
    src_cur = tgt_cur = 0
    for command, length, arg in patch.actions:
        if command == SOURCE_READ:
            out += source[len(out):len(out) + length]
        elif command == TARGET_READ:
            out += arg
        elif command == SOURCE_COPY:
            src_cur += arg
            out += source[src_cur:src_cur + length]
            src_cur += length
        else:
            tgt_cur += arg
            for _ in range(length):     # byte by byte: the copy may overlap its own output
                out.append(out[tgt_cur])
                tgt_cur += 1
    if len(out) != patch.target_size or zlib.crc32(out) != patch.target_crc:
        raise ValueError('patched output does not match the patch target CRC32')
    return bytes(out)
