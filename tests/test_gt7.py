"""Regression tests against the GT7 1.71 samples: NOT_BACKPORT (original) and GT7/USA (FW 4 backport).

Run from the repo root:  python -m unittest discover -s tests -v
Tests are skipped when the sample files are not present.
"""
import filecmp
import os
import shutil
import tempfile
import unittest

import struct

from ps5bp import fixes, fself, game, imports, patch, sdk
from ps5bp.elf import SceElf
from ps5bp.nid import compute_nid

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ORIG = os.path.join(ROOT, '.original')
WORK = os.path.join(ROOT, 'work')
REF_STUBS = os.path.join(ORIG, 'reference', 'PS5-3.20_Libs')


def read(path):
    with open(path, 'rb') as f:
        return f.read()


def need(*paths):
    missing = [p for p in paths if not os.path.exists(p)]
    return unittest.skipIf(missing, f'sample files missing: {missing}')


def diff_runs(a, b):
    runs, i = [], 0
    while i < len(a):
        if a[i] != b[i]:
            j = i
            while j < len(a) and a[j] != b[j]:
                j += 1
            runs.append((i, j - i))
            i = j
        else:
            i += 1
    return runs


class TestNid(unittest.TestCase):
    def test_known_nids(self):
        self.assertEqual(compute_nid('sceKernelGetOperationMode'), 'NH6xARDOVv8')
        self.assertEqual(compute_nid('sceSaveDataMount3'), 'ZP4e7rlzOUk')


class TestFself(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()

    def tearDown(self):
        shutil.rmtree(self.tmp)

    @need(os.path.join(ORIG, 'GT7', 'USA', 'sce_sys', 'about', 'right.sprx'))
    def test_roundtrip_exact(self):
        src = os.path.join(ORIG, 'GT7', 'USA', 'sce_sys', 'about', 'right.sprx')
        elf, out = os.path.join(self.tmp, 'a.elf'), os.path.join(self.tmp, 'a.sprx')
        fself.unwrap(src, elf)
        fself.sign(elf, out)
        self.assertTrue(filecmp.cmp(src, out, shallow=False))

    @need(os.path.join(ORIG, 'GT7', 'USA', 'logiWheel.prx'))
    def test_roundtrip_identical_except_elf_digest(self):
        src = os.path.join(ORIG, 'GT7', 'USA', 'logiWheel.prx')
        elf, out = os.path.join(self.tmp, 'a.elf'), os.path.join(self.tmp, 'a.prx')
        fself.unwrap(src, elf)
        fself.sign(elf, out)
        a, b = read(src), read(out)
        self.assertEqual(len(a), len(b))
        digest = fself.digest_offset(a)
        self.assertTrue(all(digest <= off and off + n <= digest + 32 for off, n in diff_runs(a, b)))


class TestSdk(unittest.TestCase):
    PAIRS = [
        ('NOT_BACKPORT_elf/logiWheel.elf', 'GT7_root_elf/logiWheel.elf'),
        ('NOT_BACKPORT_elf/sce_module/libScePfs.elf', 'GT7_sce_module_elf/libScePfs.elf'),
        ('NOT_BACKPORT_elf/sce_sys/about/right.elf', 'GT7_sce_sys_elf/right.elf'),
    ]

    @need(*[os.path.join(WORK, p) for pair in PAIRS for p in pair])
    def test_fw4_sdk_patch_reproduces_backport(self):
        """These modules differ from the backport only in their SDK fields."""
        for before, after in self.PAIRS:
            elf = SceElf.load(os.path.join(WORK, before))
            self.assertEqual(sdk.read_sdk(elf), sdk.sdk_for_firmware(10))
            sdk.write_sdk(elf, *sdk.sdk_for_firmware(4))
            self.assertEqual(bytes(elf.data), read(os.path.join(WORK, after)), before)


class TestStub(unittest.TestCase):
    STUBBED = ['5tRaBjtdTzY', 'T4ucGB8CsnM', 'kP2L8t3j-aM', 'LibwuIonIBw', 'La9uyZv4Kvw', 'NH6xARDOVv8']
    SAVEDATA_PATCH = 0x83803E   # sceSaveDataMount3 system_blocks -1 -> 0 (not automated yet)

    @need(os.path.join(WORK, 'NOT_BACKPORT_elf', 'eboot.elf'), os.path.join(WORK, 'GT7_root_elf', 'eboot.elf'))
    def test_eboot_matches_backport_behaviour(self):
        ours = SceElf.load(os.path.join(WORK, 'NOT_BACKPORT_elf', 'eboot.elf'))
        theirs = SceElf.load(os.path.join(WORK, 'GT7_root_elf', 'eboot.elf'))
        sdk.write_sdk(ours, *sdk.sdk_for_firmware(4))
        patches = patch.stub_imports(ours, self.STUBBED)

        # Every redirected slot runs the same stub body as the hand-made backport.
        for p in patches:
            body = patch.stub_body(p.stub)
            target = theirs.read_u64(p.got_vaddr)
            self.assertEqual(bytes(theirs.data[theirs.off(target):theirs.off(target) + len(body)]), body, p.nid)

        # Nothing else differs, apart from stub placement and the savedata argument patch.
        allowed = [(p.got_vaddr, 8) for p in patches]
        allowed += [(theirs.read_u64(p.got_vaddr), len(patch.stub_body(p.stub))) for p in patches]
        allowed += [(p.stub_vaddr, len(patch.stub_body(p.stub))) for p in patches]
        allowed = [(ours.off(va), n) for va, n in allowed] + [(self.SAVEDATA_PATCH, 4)]
        for off, n in diff_runs(ours.data, theirs.data):
            self.assertTrue(any(a <= off and off + n <= a + m for a, m in allowed), f'unexpected diff at {off:#x}')


class TestDefineSymbol(unittest.TestCase):
    @need(os.path.join(WORK, 'NOT_BACKPORT_elf', 'eboot.elf'))
    def test_define_symbol_marks_import_stubbed(self):
        elf = SceElf.load(os.path.join(WORK, 'NOT_BACKPORT_elf', 'eboot.elf'))
        patch.stub_imports(elf, ['5tRaBjtdTzY'], define_symbol=True)
        reloaded = SceElf(bytes(elf.data))
        imp = next(i for i in reloaded.imports() if i.nid == '5tRaBjtdTzY')
        self.assertTrue(imp.defined_locally)
        self.assertNotIn('5tRaBjtdTzY', reloaded.exports())


class TestBackportGame(unittest.TestCase):
    SAME_AS_GT7 = ['libSieRdNNabla.prx', 'logiWheel.prx', 'sce_module/libSceNpCppWebApi.prx',
                   'sce_module/libScePfs.prx', 'sce_sys/about/right.sprx']

    @need(os.path.join(ORIG, 'GT7', 'NOT_BACKPORT', 'eboot.bin'), os.path.join(ORIG, 'GT7', 'USA', 'fakelib'), REF_STUBS)
    def test_whole_game_fw4(self):
        out = tempfile.mkdtemp()
        try:
            # no --stub-nid: GT7's eboot stubs and libc fixes come from fixes.FIXES
            result = game.backport_game(os.path.join(ORIG, 'GT7', 'NOT_BACKPORT'), out, 4, REF_STUBS,
                                        extra_fakelib=os.path.join(ORIG, 'GT7', 'USA', 'fakelib'))
            mods = {m.path: m for m in result.modules}
            self.assertFalse([m.path for m in result.modules if m.error])
            self.assertEqual(result.title_id, 'PPSA01317')
            self.assertTrue(result.param_warnings)
            self.assertEqual(len(mods['eboot.bin'].stubbed_now), 6)
            self.assertEqual(mods['eboot.bin'].counts.get('unknown'), 30)
            self.assertTrue(all(s['known_fix'] for s in mods['eboot.bin'].stubbed_now))
            # eboot: GT7's call-site savedata patch is replaced by the generic sceSaveDataMount3 argument fix
            self.assertIn('ZP4e7rlzOUk', {f['nid'] for f in mods['eboot.bin'].fixed})
            # libc: all six GT7 fixes applied, plus four stubs learned from CONTROL Resonant;
            # only sceKernelDebugWriteCppExceptionInfo (left alone by both games) remains unknown
            libc = mods['sce_module/libc.prx']
            self.assertEqual({f['nid'] for f in libc.fixed},
                             {'4h6F1LLbTiw', 'B2n8aDorSH4', 'PZQhiiLXRFs', 'q2y-wDIVWZA', 'Hc4CaR6JBL0', 'qBS714-Jr3g'})
            self.assertEqual({s['nid'] for s in libc.stubbed_now},
                             {'tB59hFLH3SA', 'OQ-dzhlnM28', 'dFym5b41JLU', 'XgVs4jRehcY'})
            self.assertEqual({u['nid'] for u in libc.unknown}, {'cfwBSQyr5Ys'})
            # modules that only need the SDK patch match the hand-made GT7 backport exactly
            with tempfile.TemporaryDirectory() as t:
                for rel in self.SAME_AS_GT7:
                    a, b = os.path.join(t, 'a.elf'), os.path.join(t, 'b.elf')
                    fself.unwrap(os.path.join(out, rel), a)
                    fself.unwrap(os.path.join(ORIG, 'GT7', 'USA', rel), b)
                    self.assertEqual(read(a), read(b), rel)
            # unchanged extra fakelibs are copied verbatim
            for name in os.listdir(os.path.join(ORIG, 'GT7', 'USA', 'fakelib')):
                self.assertTrue(filecmp.cmp(os.path.join(out, 'fakelib', name),
                                            os.path.join(ORIG, 'GT7', 'USA', 'fakelib', name), shallow=False), name)
            self.assertTrue(os.path.exists(os.path.join(out, 'backport_report.json')))
        finally:
            shutil.rmtree(out)

    def test_refuses_output_inside_game(self):
        with self.assertRaises(ValueError):
            game.backport_game(ROOT, os.path.join(ROOT, 'out'), 4, REF_STUBS)


class TestLibcFixes(unittest.TestCase):
    LIBC = os.path.join(WORK, 'NOT_BACKPORT_elf', 'sce_module', 'libc.elf')
    GT7_LIBC = os.path.join(WORK, 'GT7_sce_module_elf', 'libc.elf')

    @need(LIBC, GT7_LIBC)
    def test_libc_fixes_match_gt7(self):
        elf = SceElf.load(self.LIBC)
        gt7 = SceElf.load(self.GT7_LIBC)
        nids = ['4h6F1LLbTiw', 'B2n8aDorSH4', 'PZQhiiLXRFs', 'q2y-wDIVWZA', 'Hc4CaR6JBL0', 'qBS714-Jr3g']
        got = {i.nid: i.got_vaddr for i in elf.imports() if i.plt}
        plt = {n: elf.read_u64(got[n]) - 6 for n in nids}
        fixes.apply_fixes(elf, nids)

        # NID swaps: the string table is byte-identical to GT7's backported libc
        strtab = elf.off(elf.dyn[5])
        self.assertEqual(elf.data[strtab:strtab + 0x40000], gt7.data[strtab:strtab + 0x40000])

        for n in nids:
            entry = elf.off(plt[n])
            if fixes.FIXES[n].kind == 'swap':                     # rename only; PLT untouched
                self.assertEqual(elf.data[entry:entry + 2], bytes.fromhex('ff25'), n)
                continue
            self.assertEqual(elf.data[entry], 0xE9, n)            # PLT entry now jmp rel32
            target = plt[n] + 5 + struct.unpack_from('<i', elf.data, entry + 1)[0]
            fix = fixes.FIXES[n]
            if fix.kind == 'adapter':
                code = elf.off(target)
                body = b''.join(bytes.fromhex(i) for i in fix.insns)
                self.assertEqual(bytes(elf.data[code:code + len(body)]), body, n)
                jmp = target + len(body)
                self.assertEqual(bytes(elf.data[code + len(body):code + len(body) + 2]), bytes.fromhex('ff25'))
                disp = struct.unpack_from('<i', elf.data, code + len(body) + 2)[0]
                self.assertEqual(jmp + 6 + disp, got[n], n)        # jumps through its own GOT slot
                self.assertEqual(elf.read_u64(got[n]), plt[n] + 6)  # lazy binding left intact
            elif fix.kind == 'redirect':
                self.assertEqual(target, 0x113E0)                  # __cxa_atexit, as in GT7

        statuses = imports.classify(elf, imports.load_genstub_exports(REF_STUBS))
        self.assertEqual({s.nid for s in statuses if s.status == 'unknown'},
                         {'tB59hFLH3SA', 'OQ-dzhlnM28', 'cfwBSQyr5Ys', 'dFym5b41JLU', 'XgVs4jRehcY'})


class TestImports(unittest.TestCase):
    FAKELIB = os.path.join(WORK, 'GT7_fakelib_elf')

    @need(REF_STUBS, os.path.join(FAKELIB, 'libSceAgc.elf'))
    def test_fakelib_agc_classification(self):
        reference = imports.load_genstub_exports(REF_STUBS)
        provided = imports.load_provider_exports(   # all fakelibs: libSceAmpr re-exports libkernel qSleeVC6lwI
            [os.path.join(self.FAKELIB, n) for n in os.listdir(self.FAKELIB) if n != 'libSceAgc.elf'])
        statuses = imports.classify(SceElf.load(os.path.join(self.FAKELIB, 'libSceAgc.elf')), reference, provided)
        by = {}
        for s in statuses:
            by.setdefault(s.status, set()).add(s.nid)
        self.assertEqual(len(by['stubbed']), 9)   # 8 GOT stubs + qSleeVC6lwI (symbol-only local definition)
        self.assertIn('tU5e3f9gSiU', by['stubbed'])   # sceKernelIsTrinityMode
        self.assertIn('qSleeVC6lwI', by['stubbed'])
        self.assertEqual(by['unknown'], {'GGeRJk1XdWc', 'dkBx0YqFQ+Y'})   # left lazy-bound in GT7


if __name__ == '__main__':
    unittest.main()


class TestCaveChain(unittest.TestCase):
    """Argument fixes in eboots must be split across <=16-byte int3 caves; follow the jumps."""

    @need(os.path.join(WORK, 'NOT_BACKPORT_elf', 'eboot.elf'))
    def test_mount3_fix_chain_executes_fix(self):
        elf = SceElf.load(os.path.join(WORK, 'NOT_BACKPORT_elf', 'eboot.elf'))
        nid = 'ZP4e7rlzOUk'   # sceSaveDataMount3
        imp = next(i for i in elf.imports() if i.nid == nid and i.plt)
        fixes.apply_fixes(elf, [nid])
        va, executed = elf.read_u64(imp.got_vaddr) - 6, []
        for _ in range(64):
            off = elf.off(va)
            if elf.data[off] == 0xE9:                                   # jmp rel32 to the next piece
                va = va + 5 + struct.unpack_from('<i', elf.data, off + 1)[0]
                continue
            if elf.data[off:off + 2] == bytes.fromhex('ff25'):         # final jmp [rip+GOT]
                self.assertEqual(va + 6 + struct.unpack_from('<i', elf.data, off + 2)[0], imp.got_vaddr)
                break
            insn = next(i for i in fixes.FIXES[nid].insns if elf.data[off:off + len(i) // 2].hex() == i)
            executed.append(insn)
            va += len(insn) // 2
        self.assertEqual(executed, list(fixes.FIXES[nid].insns))


class TestCandidates(unittest.TestCase):
    """Reference-derived stub candidates (ps5bp/candidates.py, generated from AnyPS5)."""

    def test_candidate_nids_match_names(self):
        from ps5bp.candidates import CANDIDATES
        for nid, (name, value, source) in CANDIDATES.items():
            self.assertEqual(compute_nid(name), nid, name)

    @need(os.path.join(WORK, 'NOT_BACKPORT_elf', 'eboot.elf'))
    def test_tts2_initialize_candidate_returns_error(self):
        """GT7 imports sceTextToSpeech2Initialize (missing on 3.20); AnyPS5: GT7 skips narration when it fails."""
        from ps5bp.candidates import CANDIDATES
        nid = 'UOjiprYwVNw'
        self.assertEqual(CANDIDATES[nid][1], 0x8002002D)
        elf = SceElf.load(os.path.join(WORK, 'NOT_BACKPORT_elf', 'eboot.elf'))
        p = patch.stub_imports(elf, [nid], rules={nid: f'ret:{CANDIDATES[nid][1]:#x}'})[0]
        stub = elf.off(elf.read_u64(p.got_vaddr))
        self.assertEqual(bytes(elf.data[stub:stub + 6]), bytes.fromhex('b82d000280c3'))   # mov eax, 0x8002002D; ret
