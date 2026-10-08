"""Neutralise imports that the target firmware lacks: write a small stub into
int3 padding in .text and point the import's GOT slot at it.

This is the technique used by the GT7 FW 4 backport (eboot and fakelibs) and by
BestPig/BackPork's patches. It works because PLT imports are bound lazily: the
GOT slot's initial value is the PLT lazy-bind stub, and replacing it means the
dynamic linker is never asked for the missing NID.

Optionally (define_symbol=True) the import's symbol is also rewritten into a
local definition (st_shndx=1, st_value=stub), as the GT7 fakelibs do. The linker
then resolves the import inside the module; this also covers RELA (data) imports.
"""
import struct
from dataclasses import dataclass

from ps5bp.elf import PT_LOAD

INT3 = 0xCC

# Stub bodies (x86-64). Each must be position independent.
STUBS = {
    # xor eax, eax; ret
    'ret0': bytes.fromhex('31c0c3'),
    # int sceKernelGetOperationMode(int* mode, int* lowEnergy): *mode = 2; *lowEnergy = 0; return 0
    'opmode_normal': bytes.fromhex('c70702000000' '832600' '31c0c3'),
    # out-parameter flag = 0 (sceNpEntitlementAccessGetGameTrialsFlag: not a trial): *rdi = 0; return 0
    'flag0': bytes.fromhex('c70700000000' '31c0c3'),
    # return an error so the caller takes its 'unavailable' path (CONTROL's sceHttp2WebSocket* stubs)
    'err_http2': bytes.fromhex('b801104380' 'c3'),
}

# Per-function rules learned from analysed games (NID -> stub name). Anything
# not listed falls back to 'ret0' when the caller asks to stub it.
RULES = {
    'NH6xARDOVv8': 'opmode_normal',   # sceKernelGetOperationMode (GT7: game reads mode==3 / lowEnergy==1)
}


@dataclass
class StubPatch:
    nid: str
    got_vaddr: int
    stub: str
    stub_vaddr: int


RET = 0xC3


def extend_text(elf):
    """Code-space fallback (PLAN 4.3, not console-tested): grow the executable segment into
    the unused bytes up to the next segment (PS5 modules end .text mid-page; the next
    segment starts on a 16 KB page). The new bytes become `ret` + int3 padding, so the cave
    finders use them like any other padding. Only when the gap is unused (all zero, in the
    file and in memory). Returns the number of bytes added (0 when there is no gap)."""
    text = elf.text_segment()
    if text.filesz != text.memsz:
        return 0
    end_off, end_va = text.offset + text.filesz, text.vaddr + text.memsz
    next_off = min((p.offset for p in elf.phdrs if p.filesz and p.offset >= end_off), default=len(elf.data))
    next_va = min((p.vaddr for p in elf.phdrs if p.type == PT_LOAD and p.vaddr >= end_va), default=end_va)
    gap = min(next_off - end_off, next_va - end_va)
    if gap < 16 or any(elf.data[end_off:end_off + gap]):
        return 0
    elf.data[end_off:end_off + gap] = bytes([RET]) + bytes([INT3]) * (gap - 1)
    phoff = struct.unpack_from('<Q', elf.data, 0x20)[0]
    entry = phoff + text.index * 0x38
    struct.pack_into('<QQ', elf.data, entry + 0x20, text.filesz + gap, text.memsz + gap)
    text.filesz += gap
    text.memsz += gap
    elf.text_extended += gap
    return gap


def find_cave(elf, size):
    """First run of int3 padding directly after a `ret` (inter-function padding)
    that fits `size` bytes with one guard int3 on each side.
    Returns the stub's file offset (one byte into the run), or None. With
    elf.allow_text_extension, the code segment is extended once when nothing fits."""
    off = _find_cave(elf, size)
    if off is None and elf.allow_text_extension and extend_text(elf):
        off = _find_cave(elf, size)
    return off


def _find_cave(elf, size):
    text = elf.text_segment()
    data, start, end = elf.data, text.offset, text.offset + text.filesz
    i = start + 1
    while i < end:
        if data[i] != INT3 or data[i - 1] != RET:
            i += 1
            continue
        j = i
        while j < end and data[j] == INT3:
            j += 1
        if j - i >= size + 2:
            return i + 1
        i = j
    return None


# --- PLT-level primitives ------------------------------------------------------
# A PLT entry is: jmp [rip+GOT] (ff 25 rel32) ; push <index> ; jmp PLT0.
# The GOT slot's initial value is the entry's lazy-bind part (entry + 6).

def plt_entry(elf, imp):
    """Virtual address of an import's PLT entry (checked to be a jmp through its GOT slot)."""
    if not imp.plt:
        raise ValueError(f'{imp.nid} has no PLT entry (RELA import)')
    entry = elf.read_u64(imp.got_vaddr) - 6
    off = elf.off(entry)
    if elf.data[off:off + 2] != b'\xff\x25' or \
            entry + 6 + struct.unpack_from('<i', elf.data, off + 2)[0] != imp.got_vaddr:
        raise ValueError(f'{imp.nid}: PLT entry at {entry:#x} is not an untouched jmp through GOT {imp.got_vaddr:#x}')
    return entry


def _rel32(src_next, target):
    disp = target - src_next
    if not -2**31 <= disp < 2**31:
        raise ValueError(f'jump target {target:#x} out of rel32 range')
    return struct.pack('<i', disp)


def redirect_plt(elf, imp, target_vaddr):
    """Overwrite the PLT entry's first instruction with `jmp target; nop` (6 bytes).
    Every call through this import then goes to `target`; the lazy-bind part
    (push/jmp PLT0) and the GOT slot stay intact for code that jumps back via the GOT."""
    entry = plt_entry(elf, imp)
    off = elf.off(entry)
    elf.data[off:off + 6] = b'\xe9' + _rel32(entry + 5, target_vaddr) + b'\x90'
    return entry


def _cave_runs(elf, min_usable):
    """Yield (stub file offset, usable bytes) for int3 runs after a `ret`, keeping
    one guard int3 on each side (same rule as find_cave)."""
    text = elf.text_segment()
    data, start, end = elf.data, text.offset, text.offset + text.filesz
    i = start + 1
    while i < end:
        if data[i] != INT3 or data[i - 1] != RET:
            i += 1
            continue
        j = i
        while j < end and data[j] == INT3:
            j += 1
        if j - i - 2 >= min_usable:
            yield i + 1, j - i - 2
        i = j


CALL_GOT = 'call_got'   # placeholder instruction: call qword [rip+GOT] (6 bytes), encoded at placement


def _insn_len(insn):
    return 6 if insn == CALL_GOT else len(insn)


def place_chain(elf, insns, got_vaddr, tail_jmp=True):
    """Write branch-free instructions, split across as many int3 caves as needed
    (eboots only have <=16-byte padding); pieces are joined with `jmp rel32`.
    tail_jmp=True ends the chain with `jmp [GOT]` (pre-call adapter); with False the
    instructions must contain CALL_GOT and end with `ret` (post-call adapter).
    Returns the vaddr of the first piece."""
    insns = [i if i == CALL_GOT else bytes(i) for i in insns]
    tail = 6 if tail_jmp else 0
    pieces = []          # (file offset, [instructions], is_last)
    k = 0
    while True:
        last_need = sum(map(_insn_len, insns[k:])) + tail
        min_need = (_insn_len(insns[k]) + 5) if k < len(insns) else tail
        # prefer one cave for all remaining code; split only when no cave is big enough
        found = next(_cave_runs(elf, last_need), None) or next(_cave_runs(elf, min(last_need, min_need)), None)
        if found is None and elf.allow_text_extension and extend_text(elf):
            continue
        if found is None:
            raise ValueError(f'no int3 padding left for a {min(last_need, min_need)}-byte code piece')
        off, usable = found
        if last_need <= usable:
            pieces.append((off, insns[k:], True))
            elf.data[off:off + last_need] = b'\x90' * last_need       # reserve
            break
        n, size = k, 0
        while n < len(insns) and size + _insn_len(insns[n]) + 5 <= usable:
            size += _insn_len(insns[n])
            n += 1
        pieces.append((off, insns[k:n], False))
        elf.data[off:off + size + 5] = b'\x90' * (size + 5)              # reserve
        k = n
    for idx, (off, piece, is_last) in enumerate(pieces):
        va = elf.vaddr(off)
        body = b''
        for insn in piece:
            if insn == CALL_GOT:
                body += b'\xff\x15' + _rel32(va + len(body) + 6, got_vaddr)
            else:
                body += insn
        if is_last:
            if tail_jmp:
                body += jmp_via_got(va + len(body), got_vaddr)
        else:
            body += b'\xe9' + _rel32(va + len(body) + 5, elf.vaddr(pieces[idx + 1][0]))
        elf.data[off:off + len(body)] = body
    return elf.vaddr(pieces[0][0])


def jmp_via_got(at_vaddr, got_vaddr):
    """`jmp qword [rip+disp]` placed at at_vaddr, jumping through got_vaddr."""
    return b'\xff\x25' + _rel32(at_vaddr + 6, got_vaddr)


def place_code(elf, body_fn, size):
    """Find a cave for `size` bytes, build the code for that address and write it.
    body_fn(vaddr) -> bytes of exactly `size`. Returns the vaddr."""
    off = find_cave(elf, size)
    if off is None:
        raise ValueError(f'no int3 padding large enough for {size} bytes')
    vaddr = elf.vaddr(off)
    body = body_fn(vaddr)
    assert len(body) == size
    elf.data[off:off + size] = body
    return vaddr


def rename_import(elf, imp, new_nid):
    """Swap an import's NID in .dynstr (same library/module suffix). Imports are
    never looked up in this module's hash table, so no hash rebuild is needed."""
    if len(new_nid) != 11:
        raise ValueError(f'invalid NID {new_nid!r}')
    sym_off = elf.symtab_offset(imp.sym_index)
    name_off = struct.unpack_from('<I', elf.data, sym_off)[0]
    strtab = elf.off(elf.dyn[5])
    start = strtab + name_off
    full = elf.cstr(start)
    # refuse when another symbol's name shares these bytes (string-table suffix merging)
    for i, s in enumerate(elf.symbols()):
        other = struct.unpack_from('<I', elf.data, elf.symtab_offset(i))[0]
        if i != imp.sym_index and name_off <= other < name_off + len(full):
            raise ValueError(f'{imp.nid}: symbol name is shared with symbol #{i}')
    if full[:11] != imp.nid:
        raise ValueError(f'{imp.nid}: unexpected symbol name {full!r}')
    elf.data[start:start + 11] = new_nid.encode('ascii')


def _define_symbol(elf, sym_index, vaddr):
    """Rewrite symbol `sym_index` into a local definition at `vaddr` (st_shndx=1, as GT7's fakelibs)."""
    off = elf.symtab_offset(sym_index)
    struct.pack_into('<H', elf.data, off + 6, 1)       # st_shndx
    struct.pack_into('<Q', elf.data, off + 8, vaddr)   # st_value


def stub_body(stub):
    """Body for a stub name: a key of STUBS, or 'ret:<value>' = return that 32-bit value."""
    if stub.startswith('ret:'):
        value = int(stub[4:], 0) & 0xFFFFFFFF
        return STUBS['ret0'] if value == 0 else b'\xb8' + struct.pack('<I', value) + b'\xc3'   # mov eax, imm32; ret
    return STUBS[stub]


def stub_imports(elf, nids, rules=None, define_symbol=False):
    """Point the given imports at stubs: always the GOT slot of PLT imports, and with
    define_symbol=True also the import's symbol (needed for RELA imports). One stub
    body is shared by every import that uses it. Returns the applied patches."""
    rules = RULES if rules is None else rules
    by_nid = {}
    for imp in elf.imports():
        if imp.nid in nids:
            if not imp.plt and not define_symbol:
                raise ValueError(f'{imp.nid} is a RELA (non-PLT) import; it needs define_symbol=True')
            by_nid.setdefault(imp.nid, []).append(imp)
    missing = set(nids) - set(by_nid)
    if missing:
        raise ValueError(f'not imported by this module: {", ".join(sorted(missing))}')

    placed = {}
    patches = []
    for nid in nids:
        stub = rules.get(nid, 'ret0')
        if stub not in placed:
            body = stub_body(stub)
            off = find_cave(elf, len(body))
            if off is None:
                raise ValueError(f'no int3 padding large enough for stub {stub!r} ({len(body)} bytes) '
                                 f'needed by import {nid}')
            elf.data[off:off + len(body)] = body
            placed[stub] = elf.vaddr(off)
        for imp in by_nid[nid]:
            if imp.plt:
                elf.write_u64(imp.got_vaddr, placed[stub])
            if define_symbol:
                _define_symbol(elf, imp.sym_index, placed[stub])
            patches.append(StubPatch(nid, imp.got_vaddr, stub, placed[stub]))
    return patches
