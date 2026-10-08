"""Known fixes at the game <-> system-library boundary, keyed by the imported NID.

These are per *library function*, not per game: a fix applies to any module that
imports the function. Every entry comes from a backport confirmed to run on a
console; `sources` lists them.

Kinds:
  stub      neutralise the import with a stub body (patch.STUBS)
  swap      rename the import to an older NID with a compatible signature
  adapter   optionally swap to `new_nid`, then route the PLT entry through code
            (`insns`, branch-free) that ends with a jump through the import's own GOT
            slot: argument translation, or argument fixing when there is no swap.
            The code is split across int3 caves as needed (eboots only have <=16-byte padding)
  redirect  point the PLT entry at a function this module itself exports

`when`:
  missing   apply only when the import is missing on the target (reference + providers)
  always    apply whenever the function is imported (argument fixes: the function
            exists on old firmware but rejects values that newer SDKs pass)
"""
from dataclasses import dataclass

from ps5bp import patch
from ps5bp.elf import DT_SCE_EXPORT_LIB
from ps5bp.nid import split_symbol

UMTX_OP = '04AjkP0jO9U'   # _umtx_op (FreeBSD), present on 3.20

GT7 = 'GT7 1.71 FW4'
CONTROL = 'CONTROL Resonant 1.300 FW4.03'

# Shared tail of the SyncOnAddress -> _umtx_op adapters (as in GT7's libc): mov r9d, -1 ; xor ecx, ecx
_UMTX_TAIL = ('41b9ffffffff', '31c9')


@dataclass(frozen=True)
class Fix:
    kind: str
    name: str
    sources: tuple
    stub: str = ''
    new_nid: str = ''
    insns: tuple = ()      # adapter code: one hex string per instruction, branch-free
    export_nid: str = ''
    when: str = 'missing'
    post_call: bool = False   # adapter runs after the real function (insns contain patch.CALL_GOT, end in ret)
    proven: bool = True       # False = derived from references, or a proven value applied by a new mechanism;
                              # becomes True after a console test (Phase 1)
    note: str = ''


def _stub(name, sources, stub='ret0', note=''):
    return Fix('stub', name, sources, stub=stub, note=note)


FIXES = {
    # --- libkernel ---
    'NH6xARDOVv8': _stub('sceKernelGetOperationMode', (GT7, CONTROL), 'opmode_normal',
                         'both games: *mode=2, *lowEnergy=0'),
    'tU5e3f9gSiU': _stub('sceKernelIsTrinityMode', (GT7, CONTROL),
                         note='PS5 Pro query; exists in libkernel from FW 7.61 (CONTROL README); 0 = not Pro'),
    'yu17wG8L5FI': _stub('sceKernelHasTrinityMode', (GT7,)),
    'La9uyZv4Kvw': _stub('libkernel La9uyZv4Kvw', (GT7,)),
    # Wait8/Wait16: names from AnyPS5 (NIDs computed; not in aerolib). FreeBSD _umtx_op has no
    # 8/16-bit compare, so they stay return-0 stubs as in CONTROL (callers re-check and spin).
    'XgVs4jRehcY': _stub('sceKernelSyncOnAddressWait16', (CONTROL,)),
    'dFym5b41JLU': _stub('sceKernelSyncOnAddressWait8', (CONTROL,)),
    # --- video out ---
    '5tRaBjtdTzY': _stub('sceVideoOutVrrPegToFixedRate', (GT7,)),
    'T4ucGB8CsnM': _stub('sceVideoOutVrrUnpegFromFixedRate', (GT7,)),
    'kP2L8t3j-aM': _stub('libSceVideoOutVrrStatus kP2L8t3j-aM', (GT7,)),
    'LibwuIonIBw': _stub('libSceVideoOutVrrStatus LibwuIonIBw', (GT7,)),
    'eb-gvTYQcoY': _stub('sceVideoOutLatencyControlWaitBeforeInput', (CONTROL,)),
    'MCJ8SkzsQxY': _stub('sceVideoOutLatencyMeasureSetStartPoint', (CONTROL,)),
    # --- audio / ACM ---
    'TViD1EZXkNI': _stub('sceAudioOut2Set3DLatency', (CONTROL,)),
    'GTfJ64qFCPw': _stub('sceAcmBatchJobPriority', (CONTROL,)),
    # --- save data, entitlements, HTTP2 ---
    'e6y3WMBYbiM': _stub('sceSaveDataCancel', (CONTROL,)),
    'y7JD1gfwyfA': _stub('sceNpEntitlementAccessGetGameTrialsFlag', (CONTROL,), 'flag0',
                         '*flag = 0: not a trial (full game)'),
    'WYszaHaIkWs': _stub('sceHttp2WebSocketSendTextMessageAsync', (CONTROL,), 'err_http2', 'online disabled'),
    'ksn0O9Iilb0': _stub('sceHttp2WebSocketSendDataMessageAsync', (CONTROL,), 'err_http2', 'online disabled'),
    '-0wUiGX74GQ': _stub('sceHttp2WebSocketCreateRequest', (CONTROL,), 'err_http2', 'online disabled'),
    'BlZ8niq3nHU': _stub('sceHttp2WebSocketCloseAsync', (CONTROL,), 'err_http2', 'online disabled'),
    # --- shader transcode (fakelib libSceAgc) ---
    '79WM0NIP4P8': _stub('sceShaderTranscodeIsRequired', (GT7, CONTROL)),
    'Pe7m8Y4BZ68': _stub('sceShaderTranscodeWithAllocator', (GT7, CONTROL)),
    'rzsfUFKB+L8': _stub('sceShaderTranscodeGetCode', (GT7, CONTROL)),
    'wDAAPuG6uN4': _stub('sceShaderTranscodeGetHeader', (GT7, CONTROL)),
    'prHBgnJWBb4': _stub('libSceShaderTranscode prHBgnJWBb4', (GT7, CONTROL)),
    # --- libSceLibcInternalExt (imported by the game's own libc.prx) ---
    'OQ-dzhlnM28': _stub('libSceLibcInternalExt OQ-dzhlnM28', (CONTROL,)),
    'tB59hFLH3SA': _stub('libSceLibcInternalExt tB59hFLH3SA', (CONTROL,)),
    'qBS714-Jr3g': Fix('redirect', 'libSceLibcInternalExt qBS714-Jr3g (used by __cxa_thread_atexit)', (GT7, CONTROL),
                       export_nid='tsvEmnenz48',
                       note="GT7: -> libc's own __cxa_atexit (kept, keeps destructors); CONTROL: return 0"),
    # --- memory ---
    '4h6F1LLbTiw': Fix('swap', 'sceKernelMapNamedFlexibleMemoryInternal', (GT7, CONTROL, 'BackPork 6xx'),
                       new_nid='IWIBBdTHit4',
                       note='-> sceKernelMapFlexibleMemory (name argument ignored). CONTROL also cleared flag '
                            '0x8000 at the call site; GT7 kept it and runs, so not applied'),
    # --- SyncOnAddress -> _umtx_op (GT7's exact register transforms; CONTROL stubbed them to return 0) ---
    'B2n8aDorSH4': Fix('adapter', 'sceKernelSyncOnAddressWait32', (GT7, CONTROL), new_nid=UMTX_OP,
                       insns=('4989d0', '89f2', '6a0b', '5e') + _UMTX_TAIL),   # op 11 WAIT_UINT
    'PZQhiiLXRFs': Fix('adapter', 'sceKernelSyncOnAddressWait64', (GT7, CONTROL), new_nid=UMTX_OP,
                       insns=('4989d0', '4889f2', '6a02', '5e') + _UMTX_TAIL),  # op 2 WAIT
    'q2y-wDIVWZA': Fix('adapter', 'sceKernelSyncOnAddressWake', (GT7, CONTROL), new_nid=UMTX_OP,
                       insns=('4863d6', '6a03', '5e', '4531c0') + _UMTX_TAIL),  # op 3 WAKE
    # AnyPS5 shows the generic Wait has Wait32's signature (u32* addr, u32 expected, KernelUseconds*
    # timeout, name), so it gets Wait32's translation (the console-proven Wait32 path). GT7's own
    # patch passed `expected` as the _umtx_op op code (spins instead of sleeping).
    'Hc4CaR6JBL0': Fix('adapter', 'sceKernelSyncOnAddressWait', (GT7, CONTROL, 'AnyPS5 signature'), new_nid=UMTX_OP,
                       proven=False,
                       insns=('4989d0', '89f2', '6a0b', '5e') + _UMTX_TAIL),   # as Wait32: op 11 WAIT_UINT
    # --- argument fixes: the function exists on old firmware but rejects values newer SDKs pass ---
    # Both games patched this inside their own code at the call site; doing it at the import
    # boundary covers every game that calls the function.
    'ZP4e7rlzOUk': Fix('adapter', 'sceSaveDataMount3', (GT7, CONTROL), when='always', proven=False,
                       # rax = [rdi+0x18]; r11 = rax+1; if r11 == 0 (value was -1): rax = 0; [rdi+0x18] = rax
                       insns=('488b4718', '4c8d5801', '4d85db', '490f44c3', '48894718'),
                       note='SaveDataMount3.systemBlocks (+0x18) -1 -> 0'),
    'sDCBrmc61XU': Fix('adapter', 'sceSaveDataPrepare', (CONTROL,), when='always', proven=False,
                       # eax = [rsi+4]; if eax == 2: eax = 1; [rsi+4] = eax   (r11 scratch)
                       insns=('8b4604', '83f802', '41bb01000000', '410f44c3', '894604'),
                       note='SaveDataPrepareParam.prepareMode (+0x04) 2 -> 1'),
    # Audio: on newer firmware the OS reports 3D audio / 7.1.4 speakers, the game then opens a
    # 12-channel AudioOut2 port, and an old-SDK binary is refused -> no sound (CONTROL README; CONTROL
    # fixed it inside Wwise). The game decides from GetSpeakerInfo (CONTROL eboot 0x3ca27f1: flags & 1
    # = 3D audio, popcnt(available_bits) > 8 = height speakers; struct layout from AnyPS5).
    # Post-call fix: run the real function, then clear the 3D flag and cap the report at 8 speakers.
    # Derived from references, not console-tested.
    'DImz2Ft9E2g': Fix('adapter', 'sceAudioOut2GetSpeakerInfo', ('CONTROL README + eboot analysis', 'AnyPS5 struct'),
                       proven=False,
                       when='always', post_call=True,
                       insns=('57',                    # push rdi (SpeakerInfo*); also aligns the stack
                              patch.CALL_GOT,          # call the real sceAudioOut2GetSpeakerInfo
                              '5f',                    # pop rdi
                              '836708fe',              # and dword [rdi+8], ~1      flags: no 3D audio
                              '816704ff000000',        # and dword [rdi+4], 0xff    <= 8 speakers
                              'c3'),                   # ret (eax = the real result)
                       note='SpeakerInfo.flags bit 0 cleared, available_bits & 0xff'),
}

# Imports that a console-tested backport shipped unpatched (still unresolved against 3.20
# with the game's own modules as providers) and the game ran. Each one exists on the
# test firmware or is never called (lazy binding). Not a fix: the report notes it so triage
# can focus on imports no backport has dealt with. NID -> sources.
# Caveat (Ghidra, REFERENCE_OVERVIEW Part 11): GT7 calls D-CzAxQL0XI unconditionally in its
# per-user setup, so GT7's test firmware exported it; that firmware is newer than 3.20 and
# maybe not 4.xx. Control was tested on 10.01. So this is evidence for the test firmware only.
_GT7_LEFT = (
    # eboot: PSVR2, text-to-speech 2, share, privacy
    'oPhtjySuHa8 retc+-uRMhk bIi4YUfSRys gF8+lvc7GuQ f3kPeoTZnIE Ocf081WpBpA wj1kOyNF4vM VVvFh51o20s '
    'SVEG+1D7qHA C0rPwER-yxg U-CnbmeyYaA -C2nkoEYOnU uv0Ae+jCeWY QxBJF-G2v5k hA9LshbSkzw Al4qjNREVQQ '
    'J4Vh3VVX0iU 6Jy73SRfG-o TwqZnaIjWv4 Dog+g25QYjw UVCMLmS-Eas '
    '2jiIxUmcsGo t4e879M-cSw 08JSg9p6bgQ UOjiprYwVNw X0HZNbSiqyg 8ntsRd07EQA SoWHuVW0gpU '
    'crFxyW3HdK0 D-CzAxQL0XI '
    # libc (CONTROL stubbed these; GT7 left them, qBS714-Jr3g with its only call retargeted)
    'dFym5b41JLU XgVs4jRehcY OQ-dzhlnM28 tB59hFLH3SA qBS714-Jr3g').split()
_BOTH_LEFT = (
    'cfwBSQyr5Ys '                          # libc: sceKernelDebugWriteCppExceptionInfo
    'GGeRJk1XdWc dkBx0YqFQ+Y LzoM-wVLJDE '  # fakelib Agc/AgcDriver/Psml: kernel SDK/tool/TSan queries
    'Ygv0S+Hi+hA').split()                  # fakelib Psml: sceVideoOutGetPipelineStatus
LEFT_UNPATCHED = {**{n: (GT7,) for n in _GT7_LEFT}, **{n: (GT7, CONTROL) for n in _BOTH_LEFT}}


def stub_rules():
    rules = dict(patch.RULES)
    rules.update({nid: f.stub for nid, f in FIXES.items() if f.kind == 'stub'})
    return rules


def _local_export(elf, nid):
    """Address of `nid` if this module exports it (own export library), else None."""
    own = {v >> 48 for t, v in elf.dynlist if t == DT_SCE_EXPORT_LIB}
    for s in elf.symbols():
        parts = split_symbol(s.name)
        if parts and parts[0] == nid and parts[1] in own and s.value and s.shndx:
            return s.value
    return None


def applicable(elf, nid):
    """False when a fix can't apply to this module (redirect target not exported here)."""
    fix = FIXES.get(nid)
    if fix is None:
        return False
    if fix.kind == 'redirect':
        return _local_export(elf, fix.export_nid) is not None
    return True


def apply_fixes(elf, nids):
    """Apply the non-stub fixes for `nids` (stubs are batched by the caller via
    patch.stub_imports with stub_rules()). Returns [(nid, description)]."""
    applied = []
    by_nid = {}
    for imp in elf.imports():
        if imp.nid in nids and imp.plt:
            by_nid.setdefault(imp.nid, imp)
    for nid in nids:
        try:
            applied += _apply_one(elf, nid, by_nid.get(nid))
        except ValueError as e:
            raise ValueError(f'fix for {FIXES[nid].name} ({nid}): {e}') from e
    return applied


def _apply_one(elf, nid, imp):
    applied = []
    fix = FIXES[nid]
    if fix.kind == 'stub':
        return applied
    if imp is None:
        raise ValueError(f'{nid}: not a PLT import of this module')
    if fix.kind == 'swap':
        patch.rename_import(elf, imp, fix.new_nid)
        applied.append((nid, f'swap -> {fix.new_nid}'))
    elif fix.kind == 'adapter':
        got = imp.got_vaddr
        insns = [i if i == patch.CALL_GOT else bytes.fromhex(i) for i in fix.insns]
        cave = patch.place_chain(elf, insns, got, tail_jmp=not fix.post_call)
        if fix.new_nid:
            patch.rename_import(elf, imp, fix.new_nid)
        patch.redirect_plt(elf, imp, cave)
        target = f' -> {fix.new_nid}' if fix.new_nid else ' (argument fix)'
        applied.append((nid, f'adapter{target} via cave {cave:#x}'))
    elif fix.kind == 'redirect':
        target = _local_export(elf, fix.export_nid)
        if target is None:
            raise ValueError(f'{nid}: this module does not export {fix.export_nid}')
        patch.redirect_plt(elf, imp, target)
        applied.append((nid, f'redirect -> local export {fix.export_nid} @ {target:#x}'))
    return applied
