"""Phase 4 tests: triage data for unresolved imports (code references, left-unpatched evidence).

Run from the repo root:  python -m unittest discover -s tests -v
"""
import glob
import os
import tempfile
import unittest

from ps5bp import fixes, fself, imports, patch
from ps5bp.elf import SceElf

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WORK = os.path.join(ROOT, 'work')
DB = os.path.join(ROOT, 'firmware_db')
GT7_ELFS = [os.path.join(WORK, d, '*.elf') for d in ('GT7_root_elf', 'GT7_sce_module_elf', 'GT7_fakelib_elf')]
NOT_BACKPORT_EBOOT = os.path.join(WORK, 'NOT_BACKPORT_elf', 'eboot.elf')


def need(*paths):
    missing = [p for p in paths if not glob.glob(p)]
    return unittest.skipIf(missing, f'sample files missing: {missing}')


def unresolved(paths, firmware):
    """NIDs still unresolved in a set of modules loaded together."""
    elfs = [SceElf.load(p) for p in paths]
    reference, _, exact = imports.load_reference(DB, firmware)
    provided = set().union(*(e.exports() for e in elfs))
    return {s.nid for e in elfs for s in imports.classify(e, reference, provided - e.exports(), exact)
            if s.status in imports.UNRESOLVED}


class TestLeftUnpatched(unittest.TestCase):
    @need(*GT7_ELFS, os.path.join(WORK, 'CONTROL_elf', 'eboot.elf'))
    def test_matches_console_tested_backports(self):
        gt7 = unresolved([p for g in GT7_ELFS for p in glob.glob(g)], '4')
        control = unresolved(glob.glob(os.path.join(WORK, 'CONTROL_elf', '**', '*.elf'), recursive=True), '4.03')
        self.assertEqual(gt7, set(fixes.LEFT_UNPATCHED))
        self.assertEqual(control, {n for n, src in fixes.LEFT_UNPATCHED.items() if fixes.CONTROL in src})


@need(NOT_BACKPORT_EBOOT)
class TestCodeReferences(unittest.TestCase):
    def test_counts(self):
        elf = SceElf.load(NOT_BACKPORT_EBOOT)
        reference, _, exact = imports.load_reference(DB, '4')
        statuses = [s for s in imports.classify(elf, reference, exact=exact) if s.nid in
                    ('2jiIxUmcsGo', 'bIi4YUfSRys', 'f3kPeoTZnIE')]
        refs = imports.code_references(elf, statuses)
        self.assertEqual(refs, {'2jiIxUmcsGo': 0,     # sceTextToSpeech2Cancel: never referenced
                                'bIi4YUfSRys': 3,     # sceHmd2GetDeviceInformation
                                'f3kPeoTZnIE': 1})    # sceHmd2Open


GT7_LIBC = os.path.join(WORK, 'GT7_sce_module_elf', 'libc.elf')
GT7_PSML = os.path.join(WORK, 'GT7_fakelib_elf', 'libScePsml.elf')


def fill_padding(elf):
    """Use up every int3 run after a ret (simulates a module with no padding left)."""
    text = elf.text_segment()
    d = elf.data
    for i in range(text.offset + 1, text.offset + text.filesz):
        if d[i] == patch.INT3 and d[i - 1] in (patch.RET, 0x90):
            d[i] = 0x90


@need(GT7_LIBC)
class TestExtendText(unittest.TestCase):
    def test_extends_into_page_gap(self):
        elf = SceElf.load(GT7_LIBC)
        text = elf.text_segment()
        old_end = text.offset + text.filesz
        self.assertEqual(patch.extend_text(elf), 0x812)            # 0x1137ee -> next segment at 0x114000
        self.assertEqual(elf.data[old_end], patch.RET)
        again = SceElf(elf.data)
        self.assertEqual((again.text_segment().filesz, again.text_segment().memsz), (0x114000, 0x114000))
        self.assertEqual(patch.extend_text(elf), 0)                # only once

    def test_no_padding_needs_flag(self):
        elf = SceElf.load(GT7_LIBC)
        fill_padding(elf)
        nid = next(i.nid for i in elf.imports() if i.plt)
        with self.assertRaisesRegex(ValueError, 'no int3 padding'):
            patch.stub_imports(elf, [nid])
        elf.allow_text_extension = True
        (p,) = patch.stub_imports(elf, [nid])
        self.assertGreaterEqual(p.stub_vaddr, 0x1137ee)            # stub sits in the extended bytes
        self.assertEqual(elf.text_extended, 0x812)
        with tempfile.TemporaryDirectory() as tmp:               # still signs and unwraps byte-exactly
            src, signed, back = (os.path.join(tmp, n) for n in ('a.elf', 'a.sprx', 'b.elf'))
            elf.save(src)
            fself.sign(src, signed)
            fself.unwrap(signed, back)
            with open(back, 'rb') as f:
                self.assertEqual(f.read(), bytes(elf.data))

    @need(GT7_PSML)
    def test_page_exact_segment_has_no_gap(self):
        self.assertEqual(patch.extend_text(SceElf.load(GT7_PSML)), 0)


if __name__ == '__main__':
    unittest.main()
