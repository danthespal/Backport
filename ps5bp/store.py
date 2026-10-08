"""Per-firmware fakelib store: fakelibs/<firmware>/*.sprx|prx + manifest.json.

Lookup for a target firmware tries the exact version folder first ('4.03'), then
the major ('4'), the way Backpork Kitchen keeps <FW>/fakelib. The manifest
records, per file, its SHA-256, size, container, SDK fields, the build path
embedded in the module, and where it came from.
"""
import hashlib
import json
import os
import re
import shutil
import tempfile

from ps5bp import fself, sdk
from ps5bp.elf import SceElf

DEFAULT_STORE = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'fakelibs')
MANIFEST = 'manifest.json'
MODULE_EXTENSIONS = ('.sprx', '.prx')

# Compiler __DATE__ ("Aug 24 2026", day space-padded), the only build id homebrew ampr_emu carries
BUILD_DATE = re.compile(rb'(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) [ 123]\d 20\d\d')

# (module, build date) -> console result. CONTROL Resonant README: the ampr_emu build of 6/9 reads
# dump data wrong (the game asks for ~9 GB); the 24/8 build works (year from the 24/8 binary).
KNOWN_BUILDS = {
    ('libSceAmpr', 'Aug 24 2026'): ('ok', 'CONTROL Resonant backport (console-tested)'),
    ('libSceAmpr', 'Sep  6 2026'): ('broken', 'CONTROL Resonant README: reads dump data wrong, game asks for ~9 GB; '
                                              'use the Aug 24 build'),
}


def firmware_dirs(store, firmware):
    """Candidate folders for a target firmware, most specific first."""
    major, minor = sdk.parse_firmware(firmware)
    names = [f'{major}.{minor:02d}', str(major)] if minor else [str(major)]
    return [os.path.join(store, n) for n in names]


def find(store, firmware):
    """The store folder used for `firmware`, or None."""
    for d in firmware_dirs(store, firmware):
        if os.path.isdir(d) and any(n.lower().endswith(MODULE_EXTENSIONS) for n in os.listdir(d)):
            return d
    return None


def _sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for block in iter(lambda: f.read(1 << 20), b''):
            h.update(block)
    return h.hexdigest()


def _describe(path):
    """Container, SDK pair and build path of a module (SELF or ELF)."""
    kind = fself.file_kind(path)
    info = {'container': kind}
    with tempfile.TemporaryDirectory() as tmp:
        elf_path = path
        if kind in fself.SELF_MAGICS.values():
            elf_path = os.path.join(tmp, 'm.elf')
            fself.unwrap(path, elf_path)
        elif kind != 'ELF':
            raise ValueError(f'{path}: not a SELF or ELF module')
        elf = SceElf.load(elf_path)
        ps5, ps4 = sdk.read_sdk(elf)
        info.update({'module': elf.module_name, 'ps5_sdk': f'{ps5:#010x}', 'ps4_sdk': f'{ps4:#010x}',
                     'build_path': elf.build_path})
        date = BUILD_DATE.search(elf.data)
        if date:
            info['build_date'] = date.group().decode()
    return info


def load_manifest(folder):
    path = os.path.join(folder, MANIFEST)
    if not os.path.exists(path):
        return {}
    with open(path, encoding='utf-8') as f:
        return json.load(f)


def _save_manifest(folder, manifest):
    with open(os.path.join(folder, MANIFEST), 'w', encoding='utf-8', newline='\n') as f:
        json.dump(dict(sorted(manifest.items())), f, indent=2)
        f.write('\n')


def add(store, firmware, files, source=''):
    """Copy modules into fakelibs/<firmware>/ and record them in the manifest.
    `firmware` is used verbatim as the folder name after validation ('4', '4.03')."""
    major, minor = sdk.parse_firmware(firmware)
    folder = os.path.join(store, f'{major}.{minor:02d}' if minor else str(major))
    os.makedirs(folder, exist_ok=True)
    manifest = load_manifest(folder)
    added = []
    for path in files:
        name = os.path.basename(path)
        if not name.lower().endswith(MODULE_EXTENSIONS):
            raise ValueError(f'{name}: fakelibs must be .sprx or .prx')
        entry = _describe(path)
        dest = os.path.join(folder, name)
        if os.path.abspath(path) != os.path.abspath(dest):
            shutil.copy2(path, dest)
        entry.update({'sha256': _sha256(dest), 'size': os.path.getsize(dest), 'source': source})
        manifest[name] = entry
        added.append((name, entry))
    _save_manifest(folder, manifest)
    return folder, added


def verify(folder):
    """[(file, problem)] for files missing, changed, or not in the manifest."""
    manifest = load_manifest(folder)
    problems = []
    present = {n for n in os.listdir(folder) if n.lower().endswith(MODULE_EXTENSIONS)}
    for name, entry in manifest.items():
        path = os.path.join(folder, name)
        if not os.path.exists(path):
            problems.append((name, 'missing'))
        elif _sha256(path) != entry.get('sha256'):
            problems.append((name, 'checksum differs from manifest'))
    for name in sorted(present - set(manifest)):
        problems.append((name, 'not in manifest'))
    return problems


def build_warnings(folder):
    """Warnings for fakelibs whose (module, build date) is known not to work."""
    out = []
    for name, entry in load_manifest(folder).items():
        state, note = KNOWN_BUILDS.get((entry.get('module'), entry.get('build_date')), ('', ''))
        if state == 'broken':
            out.append(f'{name} ({entry["module"]} built {entry["build_date"]}) is a known-broken build: {note}')
    return out


def list_store(store):
    """{folder name: manifest} for every firmware folder in the store."""
    if not os.path.isdir(store):
        return {}
    return {d: load_manifest(os.path.join(store, d)) for d in sorted(os.listdir(store))
            if os.path.isdir(os.path.join(store, d))}


def check(folder, reference, exact):
    """Import status of every fakelib in a store folder against a target firmware's export
    list (imports.load_reference). The folder's other fakelibs count as providers, as they
    are loaded together. Returns [(file, SceElf, [ImportStatus])]."""
    from ps5bp import imports
    out = []
    with tempfile.TemporaryDirectory() as tmp:
        for i, name in enumerate(sorted(n for n in os.listdir(folder) if n.lower().endswith(MODULE_EXTENSIONS))):
            path = os.path.join(folder, name)
            if fself.file_kind(path) != 'ELF':
                elf_path = os.path.join(tmp, f'{i}.elf')
                fself.unwrap(path, elf_path)
                path = elf_path
            out.append((name, SceElf.load(path)))
        provided = set()
        for _, elf in out:
            provided |= elf.exports()
        return [(name, elf, imports.classify(elf, reference, provided - elf.exports(), exact)) for name, elf in out]
