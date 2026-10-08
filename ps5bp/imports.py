"""Classify a module's imports against what the target firmware provides.

Classes:
  reference  - exported by the reference firmware (lower bound: present on the target)
  provided   - exported by a module shipped with the game (fakelib/, sce_module/, game prx)
  stubbed    - neutralised: GOT slot points at local code, stub code written into the
               PLT entry, or the symbol rewritten into a local definition
  patched    - PLT entry jumps to a cave or local function (adapter, redirect, argument fix)
  data       - data import; reference lists contain functions only, so unchecked
  missing    - not exported by the target firmware (exact firmware_db): needs a fix
  unknown    - none of the above, with a lower-bound reference: possibly missing on the target
"""
import os
import re
import struct
from collections import Counter, defaultdict
from dataclasses import dataclass

from ps5bp import fwdb
from ps5bp.elf import PF_X, PT_LOAD, STT_OBJECT, SceElf

PUSH_IMM32 = 0x68   # an untouched lazy-bind PLT stub starts with 'push <index>'
JMP_REL32 = 0xE9    # a redirected PLT entry starts with 'jmp rel32'
JMP_VIA_GOT = bytes.fromhex('ff25')   # an untouched PLT entry starts with 'jmp [rip+GOT]'


def load_genstub_exports(stub_dir):
    """All NIDs from a directory of genstub-generated <module>.c files (e.g. PS5-3.20_Libs)."""
    nids = set()
    pattern = re.compile(r'sprx_dlsym\([^,]+,\s*"([A-Za-z0-9+\-]{11})"')
    for name in os.listdir(stub_dir):
        if name.endswith('.c'):
            with open(os.path.join(stub_dir, name), encoding='utf-8', errors='replace') as f:
                nids.update(pattern.findall(f.read()))
    return nids


def load_provider_exports(paths):
    """NIDs exported by unwrapped ELF modules shipped with the game."""
    nids = set()
    for path in paths:
        nids |= SceElf.load(path).exports()
    return nids


@dataclass
class ImportStatus:
    nid: str
    library: str
    got_vaddr: int
    plt: bool
    status: str


UNRESOLVED = ('unknown', 'missing')   # statuses a fix may target


def load_reference(path, target):
    """(NIDs, label, exact) for a target firmware. `path` is a firmware_db folder (per-firmware
    JSON, see fwdb.py) or a folder of genstub .c stubs (always a lower bound)."""
    if os.path.isdir(path) and any(n.endswith('.json') for n in os.listdir(path)):
        db, exact = fwdb.select(path, target)
        if db is None:
            raise ValueError(f'firmware_db {path} has no firmware at or below {target}')
        kind = 'exact' if exact else 'lower bound'
        return db.nids, f'firmware_db {db.firmware} ({kind} for FW {target})', exact
    return load_genstub_exports(path), f'genstub stubs {path} (lower bound)', False


def classify(elf, reference, provided=frozenset(), exact=False):
    """One status per imported NID (a NID can have many relocations; the PLT one is preferred).
    With exact=True (`reference` is the target firmware's own export list) an import that is
    neither there nor provided is 'missing'; with a lower-bound reference it is only 'unknown'."""
    unique = {}
    for imp in elf.imports():
        if imp.nid not in unique or (imp.plt and not unique[imp.nid].plt):
            unique[imp.nid] = imp
    out = []
    for imp in unique.values():
        if imp.defined_locally:
            status = 'stubbed'
        elif imp.sym_type == STT_OBJECT:
            status = 'data'
        elif imp.plt and elf.data[elf.off(elf.read_u64(imp.got_vaddr))] != PUSH_IMM32:
            status = 'stubbed'   # GOT slot points at local code (GT7 eboot / fakelibs)
        elif imp.plt and elf.data[elf.off(elf.read_u64(imp.got_vaddr) - 6)] == JMP_REL32:
            status = 'patched'   # PLT entry jumps to a cave or local function (adapter / redirect / argument fix)
        elif imp.plt and elf.data[elf.off(elf.read_u64(imp.got_vaddr) - 6):
                                  elf.off(elf.read_u64(imp.got_vaddr) - 6) + 2] != JMP_VIA_GOT:
            status = 'stubbed'   # stub code written into the PLT entry itself (CONTROL Resonant)
        elif imp.nid in reference:
            status = 'reference'
        elif imp.nid in provided:
            status = 'provided'
        else:
            status = 'missing' if exact else 'unknown'
        out.append(ImportStatus(imp.nid, imp.library, imp.got_vaddr, imp.plt, status))
    return out


# call/jmp rel32, call/jmp [rip+disp32], mov/lea reg, [rip+disp32]: (opcode bytes, disp32 follows)
_CODE_REF = re.compile(rb'(?=([\xe8\xe9]|\xff[\x15\x25]|[\x8b\x8d][\x05\x0d\x15\x1d\x25\x2d\x35\x3d]))', re.S)


def code_references(elf, statuses):
    """{NID: number of instructions in executable segments that call, jump to or load the
    import's PLT entry or GOT slot}. A byte-pattern scan (no disassembly): it can over-count
    on chance matches but never misses a direct reference. 0 means only reachable through
    data (vtables, function-pointer tables), which a scan can't see; >0 says nothing about
    whether the caller itself runs."""
    targets, plt_entries = {}, set()
    for s in statuses:
        targets[s.got_vaddr] = s.nid
        if s.plt:
            entry = elf.read_u64(s.got_vaddr) - 6
            targets[entry] = s.nid
            plt_entries.add(entry)
    counts = dict.fromkeys((s.nid for s in statuses), 0)
    for p in elf.phdrs:
        if p.type != PT_LOAD or not p.flags & PF_X:
            continue
        seg = bytes(elf.data[p.offset:p.offset + p.filesz])
        for m in _CODE_REF.finditer(seg):
            disp_at = m.start() + len(m.group(1))
            if disp_at + 4 > len(seg):
                continue
            target = p.vaddr + disp_at + 4 + struct.unpack_from('<i', seg, disp_at)[0]
            if target in targets and p.vaddr + m.start() not in plt_entries:   # skip the PLT's own jmp [GOT]
                counts[targets[target]] += 1
    return counts


def report(name, elf, statuses, names, show=('missing', 'unknown', 'stubbed', 'patched')):
    lines = [f'== {name}  module={elf.module_name}', f'   build: {elf.build_path}']
    counts = Counter(s.status for s in statuses)
    lines.append('   ' + ', '.join(f'{k}={counts[k]}' for k in
                                   ('reference', 'provided', 'stubbed', 'patched', 'data', 'missing', 'unknown')))
    by_lib = defaultdict(list)
    for s in statuses:
        if s.status in show:
            by_lib[s.library].append(s)
    for lib in sorted(by_lib):
        lines.append(f'   {lib}:')
        for s in sorted(by_lib[lib], key=lambda s: (s.status, names.name(s.nid))):
            lines.append(f'      {s.status:8} {s.nid}  {names.name(s.nid)}  (GOT {s.got_vaddr:#x})')
    return '\n'.join(lines)
