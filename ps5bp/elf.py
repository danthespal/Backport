"""PS5 (SCE) ELF model: segments, dynamic tables, imports and exports.

PS5 ELFs use standard DT_* tags whose values are virtual addresses inside a
non-executable PT_LOAD (PS4 used offsets into PT_SCE_DYNLIBDATA instead).
Symbol names are "<NID>#<library-id>#<module-id>".
"""
import struct
from dataclasses import dataclass

from ps5bp.nid import split_symbol

ET_SCE_EXEC = 0xFE10
ET_SCE_DYNAMIC = 0xFE18

PT_LOAD = 1
PT_DYNAMIC = 2
PT_SCE_PROCPARAM = 0x61000001
PT_SCE_MODULE_PARAM = 0x61000002
PF_X = 1

DT_STRTAB, DT_SYMTAB, DT_RELA, DT_RELASZ, DT_JMPREL, DT_PLTRELSZ = 5, 6, 7, 8, 23, 2
DT_SCE_MODULE_INFO_PATH = 0x61000041   # build path of the module
DT_SCE_MODULE_NAME = 0x61000043        # own module name
DT_SCE_EXPORT_LIB = 0x61000047         # exported library: id << 48 | name offset
DT_SCE_IMPORT_LIB = 0x61000049         # imported library: id << 48 | name offset
DT_SCE_SYMTABSZ = 0x6100003F

STT_OBJECT = 1
STT_FUNC = 2

ELF_MAGIC = b'\x7fELF'


@dataclass
class Phdr:
    index: int
    type: int
    flags: int
    offset: int
    vaddr: int
    filesz: int
    memsz: int


@dataclass
class Symbol:
    name: str
    type: int
    shndx: int
    value: int

    @property
    def nid(self):
        parts = split_symbol(self.name)
        return parts[0] if parts else None


@dataclass
class Import:
    nid: str
    library: str
    sym_type: int
    got_vaddr: int
    plt: bool          # True for JMPREL (PLT/GOT) imports, False for RELA (data/GOT) imports
    sym_index: int
    defined_locally: bool   # backport technique: symbol rewritten into a local definition


class SceElf:
    def __init__(self, data):
        self.data = bytearray(data)
        self.allow_text_extension = False   # patch.extend_text may grow .text when padding runs out
        self.text_extended = 0              # bytes added by patch.extend_text
        if self.data[:4] != ELF_MAGIC:
            raise ValueError('not an ELF file (unwrap SELF files first)')
        d = self.data
        self.type = struct.unpack_from('<H', d, 0x10)[0]
        phoff = struct.unpack_from('<Q', d, 0x20)[0]
        phnum = struct.unpack_from('<H', d, 0x38)[0]
        self.phdrs = []
        for i in range(phnum):
            t, fl, off, va, pa, fs, ms, al = struct.unpack_from('<IIQQQQQQ', d, phoff + i * 0x38)
            self.phdrs.append(Phdr(i, t, fl, off, va, fs, ms))
        self.dynlist = []
        dyn = next((p for p in self.phdrs if p.type == PT_DYNAMIC), None)
        if dyn:
            for i in range(0, dyn.filesz, 16):
                tag, val = struct.unpack_from('<QQ', d, dyn.offset + i)
                if tag == 0:
                    break
                self.dynlist.append((tag, val))
        self.dyn = {}
        for tag, val in self.dynlist:
            self.dyn.setdefault(tag, val)

    @classmethod
    def load(cls, path):
        with open(path, 'rb') as f:
            return cls(f.read())

    def save(self, path):
        with open(path, 'wb') as f:
            f.write(self.data)

    # --- address helpers -------------------------------------------------
    def off(self, vaddr):
        for p in self.phdrs:
            if p.type == PT_LOAD and p.vaddr <= vaddr < p.vaddr + p.filesz:
                return p.offset + (vaddr - p.vaddr)
        raise ValueError(f'vaddr {vaddr:#x} is not backed by any PT_LOAD')

    def vaddr(self, offset):
        for p in self.phdrs:
            if p.type == PT_LOAD and p.offset <= offset < p.offset + p.filesz:
                return p.vaddr + (offset - p.offset)
        raise ValueError(f'file offset {offset:#x} is not inside any PT_LOAD')

    def cstr(self, offset):
        return self.data[offset:self.data.index(b'\0', offset)].decode('ascii', 'replace')

    def read_u64(self, vaddr):
        return struct.unpack_from('<Q', self.data, self.off(vaddr))[0]

    def write_u64(self, vaddr, value):
        struct.pack_into('<Q', self.data, self.off(vaddr), value)

    # --- dynamic information ---------------------------------------------
    def _dynstr(self, val):
        return self.cstr(self.off(self.dyn[DT_STRTAB]) + (val & 0xFFFFFFFF))

    @property
    def module_name(self):
        return next((self._dynstr(v) for t, v in self.dynlist if t == DT_SCE_MODULE_NAME), None)

    @property
    def build_path(self):
        return next((self._dynstr(v) for t, v in self.dynlist if t == DT_SCE_MODULE_INFO_PATH), None)

    def import_libraries(self):
        """{library id: library name} from the import-library dynamic tags."""
        return {v >> 48: self._dynstr(v) for t, v in self.dynlist if t == DT_SCE_IMPORT_LIB}

    def symtab_offset(self, index):
        return self.off(self.dyn[DT_SYMTAB]) + index * 24

    def symbols(self):
        base, strb = self.off(self.dyn[DT_SYMTAB]), self.off(self.dyn[DT_STRTAB])
        out = []
        for i in range(self.dyn.get(DT_SCE_SYMTABSZ, 0) // 24):
            name, info, other, shndx, value, size = struct.unpack_from('<IBBHQQ', self.data, base + i * 24)
            out.append(Symbol(self.cstr(strb + name), info & 0xF, shndx, value))
        return out

    def relocs(self, kind):
        """(r_offset, sym_index, r_type, addend) for 'JMPREL' or 'RELA'."""
        va, sz = (self.dyn.get(DT_JMPREL), self.dyn.get(DT_PLTRELSZ)) if kind == 'JMPREL' \
            else (self.dyn.get(DT_RELA), self.dyn.get(DT_RELASZ))
        if va is None:
            return []
        base = self.off(va)
        out = []
        for i in range(sz // 24):
            r_off, info, addend = struct.unpack_from('<QQq', self.data, base + i * 24)
            out.append((r_off, info >> 32, info & 0xFFFFFFFF, addend))
        return out

    def imports(self):
        syms, libs = self.symbols(), self.import_libraries()
        out = []
        for kind in ('JMPREL', 'RELA'):
            for r_off, si, rtype, addend in self.relocs(kind):
                if not si:
                    continue
                parts = split_symbol(syms[si].name)
                if parts is None:
                    continue
                out.append(Import(parts[0], libs.get(parts[1], f'#{parts[1]}'), syms[si].type,
                                  r_off, kind == 'JMPREL', si, syms[si].shndx != 0))
        return out

    def exports(self):
        """NIDs this module exports: defined symbols of its own export libraries.
        (Imports rewritten into local definitions keep their imported library id.)"""
        own = {v >> 48 for t, v in self.dynlist if t == DT_SCE_EXPORT_LIB}
        out = set()
        for s in self.symbols():
            parts = split_symbol(s.name)
            if s.value and s.shndx and parts and parts[1] in own:
                out.add(parts[0])
        return out

    # --- code helpers ------------------------------------------------------
    def text_segment(self):
        return next(p for p in self.phdrs if p.type == PT_LOAD and p.flags & PF_X)
