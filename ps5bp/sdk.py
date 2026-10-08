"""SDK version fields in PT_SCE_PROCPARAM (eboot) / PT_SCE_MODULE_PARAM (prx).

Param layout: +0x00 size, +0x08 magic, +0x10 PS4 SDK, +0x14 PS5 SDK.
"""
import struct

from ps5bp.elf import PT_SCE_MODULE_PARAM, PT_SCE_PROCPARAM

PROCESS_PARAM_MAGIC = 0x4942524F   # 'ORBI'
MODULE_PARAM_MAGIC = 0x3C13F4BF
PS4_SDK_OFFSET = 0x10
PS5_SDK_OFFSET = 0x14

# Target pairs from PS5-BACKPORK-KITCHEN core/VersionProfiles.vb: firmware major -> (PS5 SDK, PS4 SDK).
# GT7 confirms two of them: FW 10 is the original game's values, FW 4 is the backport's.
FIRMWARE_SDK = {
    1: (0x01000050, 0x07590001),
    2: (0x02000009, 0x08050001),
    3: (0x03000027, 0x08540001),
    4: (0x04000031, 0x09040001),
    5: (0x05000033, 0x09590001),
    6: (0x06000038, 0x10090001),
    7: (0x07000038, 0x10590001),
    8: (0x08000041, 0x11090001),
    9: (0x09000040, 0x11590001),
    10: (0x10000040, 0x12090001),
}


def _param(elf):
    for p in elf.phdrs:
        if p.type in (PT_SCE_PROCPARAM, PT_SCE_MODULE_PARAM):
            magic = struct.unpack_from('<I', elf.data, p.offset + 8)[0]
            expected = PROCESS_PARAM_MAGIC if p.type == PT_SCE_PROCPARAM else MODULE_PARAM_MAGIC
            if magic != expected:
                raise ValueError(f'param segment magic {magic:#x} != {expected:#x}')
            return p.offset
    raise ValueError('no PT_SCE_PROCPARAM / PT_SCE_MODULE_PARAM segment')


def read_sdk(elf):
    """(PS5 SDK, PS4 SDK)."""
    off = _param(elf)
    ps4, ps5 = struct.unpack_from('<II', elf.data, off + PS4_SDK_OFFSET)
    return ps5, ps4


def write_sdk(elf, ps5, ps4):
    """Set the SDK pair. Returns True if anything changed."""
    off = _param(elf)
    old = struct.unpack_from('<II', elf.data, off + PS4_SDK_OFFSET)
    struct.pack_into('<II', elf.data, off + PS4_SDK_OFFSET, ps4, ps5)
    return old != (ps4, ps5)


def _bcd(n):
    return int(str(n), 16)


def parse_firmware(value):
    """'4', '4.03', 4 -> (4, 3)."""
    major, _, minor = str(value).strip().partition('.')
    if not major.isdigit() or (minor and not minor.isdigit()):
        raise ValueError(f'invalid firmware version {value!r} (use e.g. 4 or 4.03)')
    return int(major), int(minor or 0)


def sdk_for_firmware(value):
    """(PS5 SDK, PS4 SDK) for a target firmware. The PS5 value is 0xMMmm00pp in BCD:
    the major's pair comes from FIRMWARE_SDK, the minor goes into the second byte
    (CONTROL Resonant targets 4.03 with 0x04030031; Backpork's 4 is 0x04000031)."""
    major, minor = parse_firmware(value)
    if major not in FIRMWARE_SDK:
        raise ValueError(f'no SDK pair known for firmware {major} (known: {sorted(FIRMWARE_SDK)})')
    ps5, ps4 = FIRMWARE_SDK[major]
    return (ps5 & 0xFF00FFFF) | (_bcd(minor) << 16), ps4


def param_version(value):
    """param.json version string for a firmware: '4.03' -> '0x0403000000000000'."""
    major, minor = parse_firmware(value)
    return f'0x{_bcd(major):02X}{_bcd(minor):02X}000000000000'
