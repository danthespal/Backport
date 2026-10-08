"""Tests against the CONTROL Resonant 1.300 FW 4.03 backport (no un-backported dump available).

Its README documents every change; these tests check that ps5bp recognises all of
them, that fixes.FIXES covers every stub it contains, and the 4.03 encodings.
Run from the repo root:  python -m unittest discover -s tests -v
"""
import os
import struct
import unittest

from ps5bp import fixes, imports, sdk
from ps5bp.elf import SceElf

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ELF = os.path.join(ROOT, 'work', 'CONTROL_elf')
REF_STUBS = os.path.join(ROOT, '.original', 'reference', 'PS5-3.20_Libs')

# What the CONTROL README says was stubbed, per module.
README_STUBS = {
    'eboot.elf': {'NH6xARDOVv8', 'GTfJ64qFCPw', 'TViD1EZXkNI', 'y7JD1gfwyfA', 'e6y3WMBYbiM',
                  'eb-gvTYQcoY', 'MCJ8SkzsQxY', 'tU5e3f9gSiU'},
    'sce_module/libc.elf': {'tB59hFLH3SA', 'OQ-dzhlnM28', 'qBS714-Jr3g', 'dFym5b41JLU', 'XgVs4jRehcY',
                            'B2n8aDorSH4', 'PZQhiiLXRFs', 'q2y-wDIVWZA', 'Hc4CaR6JBL0'},
    'nakama-sdk.elf': {'WYszaHaIkWs', 'ksn0O9Iilb0', '-0wUiGX74GQ', 'BlZ8niq3nHU'},
}


def need(*paths):
    missing = [p for p in paths if not os.path.exists(p)]
    return unittest.skipIf(missing, f'sample files missing: {missing}')


class TestControl(unittest.TestCase):
    @need(ELF, REF_STUBS)
    def test_classifier_finds_every_readme_stub(self):
        """CONTROL writes stub code into the PLT entry itself; all documented stubs must be seen."""
        reference = imports.load_genstub_exports(REF_STUBS)
        for rel, expected in README_STUBS.items():
            statuses = imports.classify(SceElf.load(os.path.join(ELF, rel)), reference)
            stubbed = {s.nid for s in statuses if s.status == 'stubbed'}
            self.assertEqual(stubbed, expected, rel)

    def test_fix_table_covers_every_readme_stub(self):
        for rel, nids in README_STUBS.items():
            self.assertEqual(nids - set(fixes.FIXES), set(), rel)

    def test_fw_403_encodings(self):
        self.assertEqual(sdk.sdk_for_firmware('4.03'), (0x04030031, 0x09040001))
        self.assertEqual(sdk.param_version('4.03'), '0x0403000000000000')

    @need(os.path.join(ELF, 'eboot.elf'))
    def test_speaker_info_post_call_fix(self):
        """The audio fix calls the real GetSpeakerInfo through its own GOT slot, then masks the result."""
        elf = SceElf.load(os.path.join(ELF, 'eboot.elf'))
        nid = 'DImz2Ft9E2g'   # sceAudioOut2GetSpeakerInfo
        imp = next(i for i in elf.imports() if i.nid == nid and i.plt)
        fixes.apply_fixes(elf, [nid])
        va, executed, called = elf.read_u64(imp.got_vaddr) - 6, [], None
        for _ in range(64):
            off = elf.off(va)
            if elf.data[off] == 0xE9:                                         # jmp rel32 to next piece
                va += 5 + struct.unpack_from('<i', elf.data, off + 1)[0]
                continue
            if elf.data[off:off + 2] == bytes.fromhex('ff15'):              # call [rip+GOT]
                called = va + 6 + struct.unpack_from('<i', elf.data, off + 2)[0]
                executed.append('call_got')
                va += 6
                continue
            insn = next(i for i in fixes.FIXES[nid].insns
                        if i != 'call_got' and elf.data[off:off + len(i) // 2].hex() == i)
            executed.append(insn)
            if insn == 'c3':
                break
            va += len(insn) // 2
        self.assertEqual(called, imp.got_vaddr)
        self.assertEqual(executed, list(fixes.FIXES[nid].insns))

    @need(os.path.join(ELF, 'eboot.elf'))
    def test_sdk_matches_control(self):
        self.assertEqual(sdk.read_sdk(SceElf.load(os.path.join(ELF, 'eboot.elf'))), sdk.sdk_for_firmware('4.03'))


if __name__ == '__main__':
    unittest.main()
