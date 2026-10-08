"""Whole-game backport pipeline.

Source game folder (never modified) -> output folder containing only the files
that changed or were added, in the same relative layout. The output can be used
as a ShadowMountPlus overlay: <scanpath>/backports/<TITLE_ID>/.
"""
import json
import os
import re
import shutil
import tempfile
from dataclasses import asdict, dataclass, field

from ps5bp import fixes, fself, fwdb, imports, patch, sdk, store
from ps5bp.candidates import CANDIDATES
from ps5bp.elf import SceElf

MODULE_EXTENSIONS = ('.prx', '.sprx')
MAIN_EXECUTABLE = 'eboot.bin'


@dataclass
class ModuleResult:
    path: str
    container: str
    added: bool = False          # comes from --fakelib, not from the game folder
    sdk_before: str = ''
    sdk_after: str = ''
    sdk_patched: bool = False
    counts: dict = field(default_factory=dict)
    unknown: list = field(default_factory=list)
    stubbed_now: list = field(default_factory=list)
    fixed: list = field(default_factory=list)     # non-stub known fixes (swap / adapter / redirect)
    text_extended: int = 0       # bytes added to the code segment (--extend-text)
    written: bool = False
    error: str = ''


@dataclass
class GameResult:
    source: str
    output: str
    target_firmware: str
    title_id: str = ''
    content_version: str = ''
    param_warnings: list = field(default_factory=list)
    fakelib_source: str = ''       # folder the extra fakelibs came from (--fakelib or the store)
    reference: str = ''            # which export list classified the imports, and whether exact
    modules: list = field(default_factory=list)


def find_modules(root):
    """Relative paths of eboot.bin and every .prx/.sprx under the game root."""
    found = []
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames.sort()
        for name in sorted(filenames):
            if name.lower() == MAIN_EXECUTABLE or name.lower().endswith(MODULE_EXTENSIONS):
                found.append(os.path.relpath(os.path.join(dirpath, name), root))
    return found


PARAM_FIELDS = ('requiredSystemSoftwareVersion', 'sdkVersion')


AMPR_INDEX_FILES = ('ampr_emu.index', 'ampr_emu.index.tmp')


def check_ampr_index(root, result):
    """A shipped ampr_emu index belongs to one ampr_emu build and app0 layout. CONTROL's README:
    delete it in app0 before playing, the emulator rebuilds it. An overlay can't delete files."""
    found = [n for n in AMPR_INDEX_FILES if os.path.exists(os.path.join(root, n))]
    if found:
        result.param_warnings.append(
            f'{", ".join(found)} present in the game folder: delete it from app0 on the console before playing '
            '(ampr_emu rebuilds it; a stale index from another ampr_emu build can break the game). '
            'The output overlay cannot remove it.')


def read_param(root, target_firmware, result, patch_param=False, output=None):
    path = os.path.join(root, 'sce_sys', 'param.json')
    if not os.path.exists(path):
        result.param_warnings.append('sce_sys/param.json not found')
        return
    with open(path, encoding='utf-8-sig') as f:
        text = f.read()
    param = json.loads(text)
    result.title_id = param.get('titleId', '')
    result.content_version = param.get('contentVersion', '')
    target = sdk.param_version(target_firmware)
    above = [k for k in PARAM_FIELDS if param.get(k) and int(param[k], 16) > int(target, 16)]
    if not above:
        return
    if not patch_param:
        result.param_warnings.append(
            f'param.json {", ".join(f"{k}={param[k]}" for k in above)} is above the target FW {target_firmware}; '
            f'left unchanged (--patch-param lowers it, as the CONTROL Resonant backport did; GT7 kept it)')
        return
    # rewrite only the version values, keeping the file's formatting
    for k in above:
        text = re.sub(r'("' + k + r'"\s*:\s*")0x[0-9A-Fa-f]+(")', lambda m: m.group(1) + target + m.group(2), text)
    dest = os.path.join(output, 'sce_sys', 'param.json')
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    with open(dest, 'w', encoding='utf-8', newline='') as f:
        f.write(text)
    result.param_warnings.append(f'param.json {", ".join(above)} lowered to {target} (written to sce_sys/param.json)')


def backport_game(source, output, firmware, reference_dir, extra_fakelib=None,
                  stub_unknown=False, stub_nids=(), define_symbol=False, names=None, known_fixes=True,
                  patch_param=False, candidate_fixes=False, fakelib_store=None, extend_text=False):
    target_ps5, target_ps4 = sdk.sdk_for_firmware(firmware)
    src_abs, out_abs = os.path.abspath(source), os.path.abspath(output)
    if out_abs == src_abs or out_abs.startswith(src_abs + os.sep):
        raise ValueError('output folder must not be the game folder or inside it')
    result = GameResult(src_abs, out_abs, str(firmware))
    os.makedirs(output, exist_ok=True)
    read_param(source, firmware, result, patch_param, output)
    check_ampr_index(source, result)

    # fakelibs: an explicit folder wins; otherwise the per-firmware store (fakelibs/<FW>/)
    if extra_fakelib is None and fakelib_store:
        extra_fakelib = store.find(fakelib_store, firmware)
        if extra_fakelib:
            problems = store.verify(extra_fakelib)
            if problems:
                raise ValueError(f'fakelib store {extra_fakelib} failed verification: {problems}')
            result.param_warnings += store.build_warnings(extra_fakelib)
    result.fakelib_source = os.path.abspath(extra_fakelib) if extra_fakelib else ''

    # (relative output path, source file) for every module to process
    modules = [(rel, os.path.join(source, rel), False) for rel in find_modules(source)]
    if extra_fakelib:
        for name in sorted(os.listdir(extra_fakelib)):
            if name.lower().endswith(MODULE_EXTENSIONS):
                rel = os.path.join('fakelib', name)
                # an extra fakelib replaces the game's file of the same name
                modules = [m for m in modules if os.path.normcase(m[0]) != os.path.normcase(rel)]
                modules.append((rel, os.path.join(extra_fakelib, name), True))

    reference, result.reference, exact = imports.load_reference(reference_dir, firmware)
    # newer firmware databases tell when a missing function appeared (PLAN 2.5)
    target_key = sdk.parse_firmware(firmware)
    newer_dbs = ([d for d in fwdb.load_all(reference_dir) if fwdb.fw_key(d.firmware) > target_key]
                 if os.path.isdir(reference_dir) else [])
    os.makedirs(output, exist_ok=True)

    with tempfile.TemporaryDirectory() as tmp:
        # 1) unwrap everything, so every module's exports can serve as providers
        loaded = []
        for i, (rel, src, added) in enumerate(modules):
            mod = ModuleResult(rel.replace(os.sep, '/'), fself.file_kind(src), added)
            result.modules.append(mod)
            try:
                if mod.container == 'ELF':
                    elf_path = src
                elif mod.container in fself.SELF_MAGICS.values():
                    encrypted = fself.encrypted_segments(src)
                    if encrypted:
                        raise ValueError(f'{encrypted} encrypted SELF segment(s): this is a retail-encrypted file; '
                                         'decrypt the game on a console first (PS5-SELF-Decrypter) and use that dump')
                    elf_path = os.path.join(tmp, f'{i}.elf')
                    fself.unwrap(src, elf_path)
                else:
                    raise ValueError('unknown file format')
                elf = SceElf.load(elf_path)
                elf.allow_text_extension = extend_text
                loaded.append((mod, src, elf))
            except (ValueError, OSError) as e:
                mod.error = str(e)

        provided = set()
        for _, _, elf in loaded:
            provided |= elf.exports()

        # 2) patch each module
        for mod, src, elf in loaded:
            try:
                changed = False
                ps5, ps4 = sdk.read_sdk(elf)
                mod.sdk_before = f'{ps5:#010x}/{ps4:#010x}'
                if ps5 > target_ps5:
                    changed |= sdk.write_sdk(elf, target_ps5, target_ps4)
                    mod.sdk_patched = True
                ps5, ps4 = sdk.read_sdk(elf)
                mod.sdk_after = f'{ps5:#010x}/{ps4:#010x}'

                statuses = imports.classify(elf, reference, provided - elf.exports(), exact)
                imported = {s.nid for s in statuses}
                unknown = [s.nid for s in statuses if s.status in imports.UNRESOLVED]
                unknown_plt = {s.nid for s in statuses if s.status in imports.UNRESOLVED and s.plt}

                # 1) proven fixes (fixes.FIXES): for missing imports, plus argument fixes
                #    ('always') for functions that exist on old firmware but reject new values
                untouched = {s.nid for s in statuses if s.plt and s.status in ('reference', 'provided') + imports.UNRESOLVED}
                known = [n for n in dict.fromkeys(s.nid for s in statuses)
                         if known_fixes and n in fixes.FIXES and fixes.applicable(elf, n)
                         and ((fixes.FIXES[n].when == 'missing' and n in unknown_plt) or
                              (fixes.FIXES[n].when == 'always' and n in untouched))]
                for nid, how in fixes.apply_fixes(elf, [n for n in known if fixes.FIXES[n].kind != 'stub']):
                    mod.fixed.append({'nid': nid, 'name': fixes.FIXES[nid].name, 'how': how,
                                      'source': ', '.join(fixes.FIXES[nid].sources),
                                      'proven': fixes.FIXES[nid].proven})
                    changed = True

                # 2) stubs: known stub fixes + requested NIDs + (optionally) every other unknown import
                to_stub = [n for n in known if fixes.FIXES[n].kind == 'stub']
                if candidate_fixes:   # reference-derived (AnyPS5 silent stubs), opt-in
                    to_stub += [n for n in unknown if n in unknown_plt and n in CANDIDATES and n not in fixes.FIXES]
                to_stub += [n for n in stub_nids if n in imported]
                if stub_unknown:
                    to_stub += [n for n in unknown if (n in unknown_plt or define_symbol)
                                and not (n in fixes.FIXES and fixes.FIXES[n].kind != 'stub' and n in known)]
                to_stub = [n for n in dict.fromkeys(to_stub)
                           if not any(s.nid == n and s.status == 'stubbed' for s in statuses)]
                if to_stub:
                    rules = fixes.stub_rules()
                    rules.update({n: f'ret:{CANDIDATES[n][1]:#x}' for n in to_stub
                                  if n in CANDIDATES and n not in fixes.FIXES})
                    for p in patch.stub_imports(elf, to_stub, rules=rules, define_symbol=define_symbol):
                        origin = (('known fix, proven' if fixes.FIXES[p.nid].proven else 'known fix, derived')
                                  if p.nid in fixes.FIXES else
                                  'candidate (AnyPS5), derived' if p.nid in CANDIDATES else 'requested')
                        mod.stubbed_now.append({'nid': p.nid, 'name': _name(names, p.nid), 'stub': p.stub,
                                                'got': f'{p.got_vaddr:#x}', 'at': f'{p.stub_vaddr:#x}',
                                                'known_fix': p.nid in fixes.FIXES, 'origin': origin})
                    changed = True
                if changed:
                    statuses = imports.classify(elf, reference, provided - elf.exports(), exact)

                for s in statuses:
                    mod.counts[s.status] = mod.counts.get(s.status, 0) + 1
                unresolved = [s for s in statuses if s.status in imports.UNRESOLVED]
                refs = imports.code_references(elf, unresolved) if unresolved else {}
                mod.unknown = [{'nid': s.nid, 'name': _name(names, s.nid), 'library': s.library,
                                'candidate': (f'return {CANDIDATES[s.nid][1]:#010x} ({CANDIDATES[s.nid][2]})'
                                              if s.nid in CANDIDATES else ''),
                                'status': s.status, 'code_refs': refs[s.nid],
                                'left_by': ', '.join(fixes.LEFT_UNPATCHED.get(s.nid, ())),
                                'introduced': ' / '.join(fwdb.introduced(s.nid, newer_dbs) or ())}
                               for s in unresolved]

                mod.text_extended = elf.text_extended
                if changed:
                    _write(elf, mod, output, tmp)
                elif mod.added:   # unchanged extra fakelib: copy verbatim, keep its container
                    dest = os.path.join(output, mod.path)
                    os.makedirs(os.path.dirname(dest), exist_ok=True)
                    shutil.copy2(src, dest)
                    mod.written = True
            except (ValueError, OSError) as e:
                mod.error = str(e)

    _write_reports(result, output)
    return result


def _name(names, nid):
    return names.name(nid) if names else nid


def _write(elf, mod, output, tmp):
    dest = os.path.join(output, mod.path)
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    if mod.container == 'ELF':
        elf.save(dest)
    else:
        elf_path = os.path.join(tmp, 'out.elf')
        elf.save(elf_path)
        fself.sign(elf_path, dest)
    mod.written = True


def format_report(result):
    lines = [f'Backport of {result.source}',
             f'  title {result.title_id} version {result.content_version} -> FW {result.target_firmware}',
             f'  output: {result.output}']
    lines.append(f'  reference: {result.reference}')
    lines.append(f'  fakelibs: {result.fakelib_source or "none (no --fakelib and no store folder for this firmware)"}')
    lines += [f'  WARNING: {w}' for w in result.param_warnings]
    lines.append('  refs = direct code references to an unresolved import (0: reachable only through data, '
                 'e.g. a vtable, or not at all)')
    derived = sorted({f['name'] for m in result.modules for f in m.fixed if not f['proven']} |
                     {s['name'] for m in result.modules for s in m.stubbed_now if 'derived' in s['origin']})
    if derived:
        lines.append(f'  NOTE: {len(derived)} derived fix(es) applied, not yet console-tested: {", ".join(derived)}')
    for m in result.modules:
        lines.append('')
        state = 'ERROR' if m.error else ('written' if m.written else 'unchanged')
        lines.append(f'{m.path}  [{m.container}]  {state}')
        if m.error:
            lines.append(f'  error: {m.error}')
            continue
        lines.append(f'  SDK {m.sdk_before} -> {m.sdk_after}' if m.sdk_patched else f'  SDK {m.sdk_before} (kept)')
        lines.append('  imports: ' + ', '.join(f'{k}={v}' for k, v in sorted(m.counts.items())))
        if m.text_extended:
            lines.append(f'  NOTE: code segment extended by {m.text_extended:#x} bytes for fix code '
                         '(--extend-text; not console-tested)')
        for f in m.fixed:
            tag = 'proven' if f['proven'] else 'DERIVED, not console-tested'
            lines.append(f'  fixed    {f["nid"]}  {f["name"]}  ({f["how"]}; {tag}; from {f["source"]})')
        for s in m.stubbed_now:
            lines.append(f'  stubbed  {s["nid"]}  {s["name"]}  ({s["stub"]} @ {s["at"]}; {s["origin"]})')
        for u in m.unknown:
            hint = f'   candidate: {u["candidate"]} (--candidate-fixes)' if u['candidate'] else ''
            intro = f'   introduced: FW {u["introduced"]}' if u['introduced'] else ''
            left = f'   left unpatched by: {u["left_by"]}' if u['left_by'] else ''
            lines.append(f'  {u["status"]:8} {u["nid"]}  {u["name"]}  [{u["library"]}]  refs={u["code_refs"]}'
                         f'{hint}{intro}{left}')
    return '\n'.join(lines)


def _write_reports(result, output):
    with open(os.path.join(output, 'backport_report.json'), 'w', encoding='utf-8') as f:
        json.dump(asdict(result), f, indent=2)
    with open(os.path.join(output, 'backport_report.txt'), 'w', encoding='utf-8') as f:
        f.write(format_report(result) + '\n')
