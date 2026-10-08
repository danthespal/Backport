"""Phase 2 tests: per-firmware export database (ps5bp/fwdb.py) and exact vs lower-bound classification.

Run from the repo root:  python -m unittest discover -s tests -v
"""
import os
import shutil
import tempfile
import unittest

from ps5bp import fwdb, game, imports
from ps5bp.elf import SceElf

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DB = os.path.join(ROOT, 'firmware_db')
STUBS = os.path.join(ROOT, '.original', 'reference', 'PS5-3.20_Libs')
GT7_FAKELIB = os.path.join(ROOT, '.original', 'GT7', 'USA', 'fakelib')
NOT_BACKPORT = os.path.join(ROOT, '.original', 'GT7', 'NOT_BACKPORT')
GT7_EBOOT_ELF = os.path.join(ROOT, 'work', 'NOT_BACKPORT_elf', 'eboot.elf')


def need(*paths):
    missing = [p for p in paths if not os.path.exists(p)]
    return unittest.skipIf(missing, f'sample files missing: {missing}')


class TestFwdb(unittest.TestCase):
    @need(os.path.join(DB, '3.20.json'), STUBS)
    def test_320_db_matches_genstub_stubs(self):
        db = fwdb.load(os.path.join(DB, '3.20.json'))
        self.assertEqual(db.firmware, '3.20')
        self.assertFalse(db.data_symbols)
        self.assertEqual(db.nids, imports.load_genstub_exports(STUBS))
        self.assertEqual(len(db.nids), 31275)

    @need(os.path.join(DB, '3.20.json'))
    def test_select_exact_and_lower_bound(self):
        self.assertEqual([(d.firmware, e) for d, e in [fwdb.select(DB, '3.20')]], [('3.20', True)])
        db, exact = fwdb.select(DB, '4.03')
        self.assertEqual((db.firmware, exact), ('3.20', False))
        self.assertEqual(fwdb.select(DB, '2.50'), (None, False))

    @need(GT7_FAKELIB)
    def test_import_dump_and_diff(self):
        dump = fwdb.import_dump(GT7_FAKELIB, '4.00', 'test')
        self.assertTrue(dump.data_symbols)
        self.assertIn('libSceAgc', dump.modules)
        self.assertGreater(len(dump.modules['libSceAgc']), 200)
        empty = fwdb.FirmwareExports('3.20', 'empty', False, {})
        self.assertEqual(fwdb.diff(empty, dump), dump.modules)

    def test_known_introduced(self):
        self.assertEqual(fwdb.introduced('tU5e3f9gSiU', [])[0], '7.61')   # sceKernelIsTrinityMode (CONTROL README)


@need(os.path.join(DB, '3.20.json'), GT7_EBOOT_ELF)
class TestExactClassification(unittest.TestCase):
    def setUp(self):
        self.db = tempfile.mkdtemp()
        shutil.copy(os.path.join(DB, '3.20.json'), os.path.join(self.db, '3.20.json'))
        exact4 = fwdb.load(os.path.join(DB, '3.20.json'))
        exact4.firmware = '4.00'                          # pretend we had an exact 4.00 export list
        fwdb.save(exact4, os.path.join(self.db, '4.00.json'))

    def tearDown(self):
        shutil.rmtree(self.db)

    def test_unknown_becomes_missing_with_exact_db(self):
        elf = SceElf.load(GT7_EBOOT_ELF)
        lower, label_l, exact_l = imports.load_reference(self.db, '4.03')
        exact, label_e, exact_e = imports.load_reference(self.db, '4')
        self.assertFalse(exact_l)
        self.assertTrue(exact_e)
        self.assertIn('lower bound', label_l)
        self.assertIn('exact', label_e)
        st_l = {s.nid: s.status for s in imports.classify(elf, lower, exact=False)}
        st_e = {s.nid: s.status for s in imports.classify(elf, exact, exact=True)}
        self.assertEqual(st_l['UOjiprYwVNw'], 'unknown')   # sceTextToSpeech2Initialize
        self.assertEqual(st_e['UOjiprYwVNw'], 'missing')
        self.assertEqual({n for n, s in st_l.items() if s == 'unknown'}, {n for n, s in st_e.items() if s == 'missing'})

    @need(os.path.join(NOT_BACKPORT, 'eboot.bin'))
    def test_backport_reports_reference_and_fixes_missing(self):
        out = tempfile.mkdtemp()
        try:
            result = game.backport_game(NOT_BACKPORT, out, '4', self.db)
            self.assertIn('firmware_db 4.00 (exact for FW 4)', result.reference)
            eboot = next(m for m in result.modules if m.path == 'eboot.bin')
            self.assertEqual(len(eboot.stubbed_now), 6)          # known fixes still apply to 'missing'
            self.assertNotIn('unknown', eboot.counts)
            self.assertIn('missing', eboot.counts)
        finally:
            shutil.rmtree(out)


if __name__ == '__main__':
    unittest.main()
