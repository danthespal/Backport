"""Phase 3 tests: BPS patches (ps5bp/bps.py) and the fakelib store check.

Run from the repo root:  python -m unittest discover -s tests -v
"""
import json
import os
import struct
import tempfile
import unittest
import zlib

from ps5bp import bps, imports, sdk, store

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BACKPORK = os.path.join(ROOT, '.original', 'reference', 'BackPork', 'patches')
DB = os.path.join(ROOT, 'firmware_db')
STORE = os.path.join(ROOT, 'fakelibs')


def need(*paths):
    missing = [p for p in paths if not os.path.exists(p)]
    return unittest.skipIf(missing, f'sample files missing: {missing}')


def varint(v):
    out = bytearray()
    while True:
        x = v & 0x7F
        v >>= 7
        if not v:
            return bytes(out + bytes([x | 0x80]))
        out.append(x)
        v -= 1


def make_patch(source, target, actions):
    body = b'BPS1' + varint(len(source)) + varint(len(target)) + varint(0)
    for command, length, arg in actions:
        body += varint(((length - 1) << 2) | command)
        if command == bps.TARGET_READ:
            body += arg
        elif command in (bps.SOURCE_COPY, bps.TARGET_COPY):
            body += varint((abs(arg) << 1) | (arg < 0))
    body += struct.pack('<II', zlib.crc32(source), zlib.crc32(target))
    return body + struct.pack('<I', zlib.crc32(body))


class TestBps(unittest.TestCase):
    def test_all_four_actions(self):
        source = bytes(range(32))
        # 0..7 kept, 4 literal bytes, 4 bytes copied from source[20:], 8 bytes repeating output[14:16]
        # (an overlapping target copy), rest kept
        target = bytearray(source)
        target[8:12] = b'\x31\xc0\xc3\x90'
        target[12:16] = source[20:24]
        target[16:24] = source[22:24] * 4
        actions = [(bps.SOURCE_READ, 8, None), (bps.TARGET_READ, 4, b'\x31\xc0\xc3\x90'),
                   (bps.SOURCE_COPY, 4, 20), (bps.TARGET_COPY, 8, 14), (bps.SOURCE_READ, 8, None)]
        patch = bps.parse(make_patch(source, bytes(target), actions))
        self.assertEqual(bps.apply(patch, source), bytes(target))
        self.assertEqual([(e.offset, e.kind) for e in bps.edits(patch)],
                         [(8, 'write'), (12, 'source-copy'), (16, 'target-copy')])

    def test_wrong_source_refused(self):
        source = bytes(16)
        patch = bps.parse(make_patch(source, b'\x01' + source[1:], [(bps.TARGET_READ, 1, b'\x01'),
                                                                    (bps.SOURCE_READ, 15, None)]))
        with self.assertRaisesRegex(ValueError, 'source does not match'):
            bps.apply(patch, b'\x02' * 16)

    def test_corrupt_patch_refused(self):
        data = bytearray(make_patch(b'ab', b'ab', [(bps.SOURCE_READ, 2, None)]))
        data[5] ^= 1
        with self.assertRaisesRegex(ValueError, 'checksum'):
            bps.parse(bytes(data))

    @need(BACKPORK)
    def test_backpork_npauth_patches_are_sdk_patches(self):
        # 10.01 libSceNpAuth: PS4 SDK 0x12090001 then PS5 SDK 0x10000040; the patch rewrites only the
        # differing bytes, ending with the target's PS5 SDK (6xx: one byte of PS4 SDK kept, 0x09 is the same)
        for fw, folder in ((6, '6xx'), (7, '7xx')):
            ps5, ps4 = sdk.FIRMWARE_SDK[fw]
            pair = struct.pack('<II', ps4, ps5)
            for name in ('libSceNpAuth.bps', 'libSceNpAuthAuthorizedAppDialog.bps'):
                (edit,) = bps.edits(bps.load(os.path.join(BACKPORK, folder, name)))
                self.assertEqual(edit.kind, 'write')
                self.assertTrue(pair.endswith(edit.data), (fw, name, edit.data.hex()))

    @need(BACKPORK)
    def test_backpork_patches_parse(self):
        for folder in ('6xx', '7xx'):
            for name in os.listdir(os.path.join(BACKPORK, folder)):
                if name.endswith('.bps'):
                    p = bps.load(os.path.join(BACKPORK, folder, name))
                    self.assertEqual(p.source_size, p.target_size)   # in-place patches


@need(os.path.join(DB, '3.20.json'), os.path.join(STORE, '4.03', 'libSceAgc.sprx'))
class TestFakelibCheck(unittest.TestCase):
    def test_console_tested_set_leaves_same_imports_unresolved(self):
        reference, _, exact = imports.load_reference(DB, '4.03')
        found = {name: {s.nid for s in st if s.status in imports.UNRESOLVED}
                 for name, _, st in store.check(os.path.join(STORE, '4.03'), reference, exact)}
        self.assertEqual(found['libSceAgc.sprx'], {'GGeRJk1XdWc', 'dkBx0YqFQ+Y'})
        self.assertEqual(found['libSceAgcDriver.sprx'], {'LzoM-wVLJDE'})
        self.assertEqual(found['libScePsml.sprx'], {'GGeRJk1XdWc', 'dkBx0YqFQ+Y', 'Ygv0S+Hi+hA'})
        self.assertEqual(found['libSceAmpr.sprx'], set())


class TestBuildDate(unittest.TestCase):
    @need(os.path.join(STORE, '4.03', 'libSceAmpr.sprx'))
    def test_ampr_build_date_recorded(self):
        entry = store._describe(os.path.join(STORE, '4.03', 'libSceAmpr.sprx'))
        self.assertEqual(entry['build_date'], 'Aug 24 2026')
        self.assertEqual(store.KNOWN_BUILDS[('libSceAmpr', entry['build_date'])][0], 'ok')

    def test_known_broken_build_warns(self):
        with tempfile.TemporaryDirectory() as folder:
            with open(os.path.join(folder, store.MANIFEST), 'w') as f:
                json.dump({'libSceAmpr.sprx': {'module': 'libSceAmpr', 'build_date': 'Sep  6 2026'},
                           'libScePlayGo.sprx': {'module': 'libScePlayGo'}}, f)
            (w,) = store.build_warnings(folder)
            self.assertIn('known-broken', w)


if __name__ == '__main__':
    unittest.main()
