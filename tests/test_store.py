"""Tests for the per-firmware fakelib store (ps5bp/store.py), using a temporary store.

Run from the repo root:  python -m unittest discover -s tests -v
"""
import os
import shutil
import tempfile
import unittest

from ps5bp import game, store

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GT7_FAKELIB = os.path.join(ROOT, '.original', 'GT7', 'USA', 'fakelib')
NOT_BACKPORT = os.path.join(ROOT, '.original', 'GT7', 'NOT_BACKPORT')
REF_STUBS = os.path.join(ROOT, '.original', 'reference', 'PS5-3.20_Libs')


def need(*paths):
    missing = [p for p in paths if not os.path.exists(p)]
    return unittest.skipIf(missing, f'sample files missing: {missing}')


@need(GT7_FAKELIB)
class TestStore(unittest.TestCase):
    def setUp(self):
        self.store = tempfile.mkdtemp()
        self.files = sorted(os.path.join(GT7_FAKELIB, n) for n in os.listdir(GT7_FAKELIB))
        store.add(self.store, '4', self.files, 'GT7 test')

    def tearDown(self):
        shutil.rmtree(self.store)

    def test_lookup_exact_then_major(self):
        major = os.path.join(self.store, '4')
        self.assertEqual(store.find(self.store, '4'), major)
        self.assertEqual(store.find(self.store, '4.50'), major)       # no 4.50 folder -> major
        store.add(self.store, '4.03', self.files[:1], 'exact')
        self.assertEqual(store.find(self.store, '4.03'), os.path.join(self.store, '4.03'))
        self.assertIsNone(store.find(self.store, '7'))

    def test_manifest_records_module_details(self):
        entry = store.load_manifest(os.path.join(self.store, '4'))['libSceAgc.sprx']
        self.assertEqual(entry['module'], 'libSceAgc')
        self.assertEqual(entry['ps5_sdk'], '0x04000031')
        self.assertEqual(entry['source'], 'GT7 test')
        self.assertEqual(len(entry['sha256']), 64)

    def test_verify_detects_changes(self):
        folder = os.path.join(self.store, '4')
        self.assertEqual(store.verify(folder), [])
        with open(os.path.join(folder, 'libScePlayGo.sprx'), 'r+b') as f:
            f.seek(100)
            f.write(b'\xff')
        shutil.copy(os.path.join(folder, 'libSceAmpr.sprx'), os.path.join(folder, 'extra.sprx'))
        problems = dict(store.verify(folder))
        self.assertEqual(problems['libScePlayGo.sprx'], 'checksum differs from manifest')
        self.assertEqual(problems['extra.sprx'], 'not in manifest')

    @need(os.path.join(NOT_BACKPORT, 'eboot.bin'), REF_STUBS)
    def test_backport_uses_store_without_fakelib_option(self):
        out = tempfile.mkdtemp()
        try:
            result = game.backport_game(NOT_BACKPORT, out, '4.50', REF_STUBS, fakelib_store=self.store)
            self.assertEqual(result.fakelib_source, os.path.join(self.store, '4'))
            for path in self.files:
                self.assertTrue(os.path.exists(os.path.join(out, 'fakelib', os.path.basename(path))))
        finally:
            shutil.rmtree(out)


if __name__ == '__main__':
    unittest.main()
