"""Per-firmware export database: firmware_db/<firmware>.json.

Each file lists, per system module, the NIDs that firmware exports. These are
NID lists only (no Sony code), so the database can be committed.

    {"firmware": "3.20", "source": "...", "data_symbols": false,
     "modules": {"libSceAgc": ["NID", ...], ...}}

Selection for a target firmware:
  exact        a database for exactly that firmware: an import it lacks is *missing*
  lower bound  the highest older firmware: functions are rarely removed, so what it
               exports is present on the target, but an import it lacks is only *unknown*
"""
import json
import os
import re
import tempfile
from dataclasses import dataclass, field

from ps5bp import fself, sdk
from ps5bp.elf import SceElf

DEFAULT_DB = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'firmware_db')
MODULE_EXTENSIONS = ('.sprx', '.prx', '.elf')

# Functions known to appear only in a later firmware, from console-tested backports.
# Used when no database covers the range. NID -> (first firmware that has it, source)
KNOWN_INTRODUCED = {
    'tU5e3f9gSiU': ('7.61', 'CONTROL Resonant README: sceKernelIsTrinityMode exists in libkernel from FW 7.61'),
}


def fw_key(firmware):
    return sdk.parse_firmware(firmware)


def fw_name(firmware):
    major, minor = fw_key(firmware)
    return f'{major}.{minor:02d}'


@dataclass
class FirmwareExports:
    firmware: str
    source: str
    data_symbols: bool
    modules: dict = field(default_factory=dict)     # module -> set(NID)
    path: str = ''

    @property
    def nids(self):
        out = set()
        for nids in self.modules.values():
            out |= nids
        return out


def save(db, path):
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        json.dump({'firmware': db.firmware, 'source': db.source, 'data_symbols': db.data_symbols,
                   'modules': {m: sorted(n) for m, n in sorted(db.modules.items())}}, f, indent=1)
        f.write('\n')


def load(path):
    with open(path, encoding='utf-8') as f:
        d = json.load(f)
    return FirmwareExports(fw_name(d['firmware']), d.get('source', ''), d.get('data_symbols', False),
                           {m: set(n) for m, n in d['modules'].items()}, path)


def load_all(db_dir):
    if not os.path.isdir(db_dir):
        return []
    dbs = [load(os.path.join(db_dir, n)) for n in os.listdir(db_dir) if n.endswith('.json')]
    return sorted(dbs, key=lambda d: fw_key(d.firmware))


def select(db_dir, target):
    """(database, exact) for a target firmware: the exact one, else the highest older one."""
    best, key = None, fw_key(target)
    for db in load_all(db_dir):
        k = fw_key(db.firmware)
        if k == key:
            return db, True
        if k < key:
            best = db
    return best, False


def import_genstub(stub_dir, firmware, source):
    """From genstub-generated <module>.c stubs (PS5-3.20_Libs, ps5-payload-sdk sce_stubs): functions only."""
    pattern = re.compile(r'sprx_dlsym\([^,]+,\s*"([A-Za-z0-9+\-]{11})"')
    modules = {}
    for name in sorted(os.listdir(stub_dir)):
        if name.endswith('.c'):
            with open(os.path.join(stub_dir, name), encoding='utf-8', errors='replace') as f:
                nids = set(pattern.findall(f.read()))
            if nids:
                modules[name[:-2]] = nids
    return FirmwareExports(fw_name(firmware), source, False, modules)


def import_dump(dump_dir, firmware, source):
    """From decrypted system modules (fake-signed SELF or ELF), e.g. a /system/common/lib dump.
    Takes every module's own exports, data symbols included."""
    modules = {}
    with tempfile.TemporaryDirectory() as tmp:
        for dirpath, _, files in os.walk(dump_dir):
            for name in sorted(files):
                if not name.lower().endswith(MODULE_EXTENSIONS):
                    continue
                path = os.path.join(dirpath, name)
                kind = fself.file_kind(path)
                if kind in fself.SELF_MAGICS.values():
                    if fself.encrypted_segments(path):
                        raise ValueError(f'{path}: encrypted; dump decrypted modules (PS5-SELF-Decrypter)')
                    elf_path = os.path.join(tmp, 'm.elf')
                    fself.unwrap(path, elf_path)
                elif kind == 'ELF':
                    elf_path = path
                else:
                    continue
                elf = SceElf.load(elf_path)
                module = elf.module_name or os.path.splitext(name)[0]
                modules.setdefault(module, set()).update(elf.exports())
    return FirmwareExports(fw_name(firmware), source, True, modules)


def diff(older, newer):
    """{module: set(NID)} exported by `newer` but not by `older` (per module)."""
    out = {}
    for module, nids in newer.modules.items():
        added = nids - older.modules.get(module, set())
        if added:
            out[module] = added
    return out


def introduced(nid, dbs):
    """First firmware in `dbs` (sorted) that exports `nid`, else KNOWN_INTRODUCED, else None."""
    for db in dbs:
        if nid in db.nids:
            return db.firmware, f'firmware_db {db.firmware}'
    return KNOWN_INTRODUCED.get(nid)
