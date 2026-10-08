"""Phase 0 (tool hardening) tests: encrypted input, stale ampr_emu index, fix provenance.

Run from the repo root:  python -m unittest discover -s tests -v
"""
import os
import shutil
import struct
import tempfile
import unittest

from ps5bp import fixes, fself, game

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RIGHT = os.path.join(ROOT, '.original', 'GT7', 'USA', 'sce_sys', 'about', 'right.sprx')
REF_STUBS = os.path.join(ROOT, '.original', 'reference', 'PS5-3.20_Libs')


def need(*paths):
    missing = [p for p in paths if not os.path.exists(p)]
    return unittest.skipIf(missing, f'sample files missing: {missing}')


@need(RIGHT, REF_STUBS)
class TestPhase0(unittest.TestCase):
    def setUp(self):
        self.game = tempfile.mkdtemp()
        self.out = tempfile.mkdtemp()

    def tearDown(self):
        shutil.rmtree(self.game)
        shutil.rmtree(self.out)

    def _module(self, name='right.sprx', encrypted=False):
        dest = os.path.join(self.game, name)
        shutil.copy(RIGHT, dest)
        if encrypted:
            with open(dest, 'r+b') as f:            # set the 'encrypted' flag on the first segment entry
                f.seek(0x20)
                flags = struct.unpack('<Q', f.read(8))[0]
                f.seek(0x20)
                f.write(struct.pack('<Q', flags | fself.SEGMENT_ENCRYPTED))
        return dest

    def test_encrypted_self_stops_with_decrypt_hint(self):
        path = self._module(encrypted=True)
        self.assertEqual(fself.encrypted_segments(path), 1)
        result = game.backport_game(self.game, self.out, '4', REF_STUBS)
        self.assertIn('decrypt the game on a console first', result.modules[0].error)

    def test_plain_fake_self_is_not_encrypted(self):
        self.assertEqual(fself.encrypted_segments(self._module()), 0)

    def test_stale_ampr_index_warning(self):
        self._module()
        open(os.path.join(self.game, 'ampr_emu.index'), 'wb').close()
        result = game.backport_game(self.game, self.out, '4', REF_STUBS)
        self.assertTrue(any('ampr_emu.index' in w and 'delete it' in w for w in result.param_warnings))

    def test_derived_fixes_are_flagged(self):
        derived = {f.name for f in fixes.FIXES.values() if not f.proven}
        self.assertEqual(derived, {'sceKernelSyncOnAddressWait', 'sceSaveDataMount3', 'sceSaveDataPrepare',
                                   'sceAudioOut2GetSpeakerInfo'})
        self.assertTrue(fixes.FIXES['NH6xARDOVv8'].proven)   # GetOperationMode stub: proven by both games


if __name__ == '__main__':
    unittest.main()
