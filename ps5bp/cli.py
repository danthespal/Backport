"""Command line: python -m ps5bp <command> ..."""
import argparse
import os
import sys
import tempfile

from ps5bp import bps, fixes, fself, fwdb, game, imports, patch, sdk, store
from ps5bp.elf import SceElf
from ps5bp.nid import NameDb

REFERENCE_DIR = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), '.original', 'reference')
DEFAULT_AEROLIB = os.path.join(REFERENCE_DIR, 'sce_symbols_new', 'aerolib.csv')
DEFAULT_REFERENCE_STUBS = os.path.join(REFERENCE_DIR, 'PS5-3.20_Libs')
# per-firmware export database when present, else the 3.20 genstub stubs (lower bound)
DEFAULT_REFERENCE = fwdb.DEFAULT_DB if os.path.isdir(fwdb.DEFAULT_DB) else DEFAULT_REFERENCE_STUBS


def _names(args):
    path = args.aerolib if os.path.exists(args.aerolib) else None
    return NameDb(path)


def cmd_info(args):
    for path in args.files:
        kind = fself.file_kind(path)
        print(f'{path}: {kind}')
        if kind != 'ELF':
            continue
        elf = SceElf.load(path)
        ps5, ps4 = sdk.read_sdk(elf)
        print(f'   type={elf.type:#x} module={elf.module_name} PS5 SDK={ps5:#010x} PS4 SDK={ps4:#010x}')
        print(f'   build: {elf.build_path}')
        print(f'   {len(elf.imports())} imports, {len(elf.exports())} exports')


def cmd_unwrap(args):
    fself.unwrap(args.input, args.output)
    print(f'unwrapped {args.input} -> {args.output}')


def cmd_sign(args):
    fself.sign(args.input, args.output)
    print(f'signed {args.input} -> {args.output}')


def cmd_sdk(args):
    elf = SceElf.load(args.input)
    old = sdk.read_sdk(elf)
    if args.firmware is not None:
        ps5, ps4 = sdk.sdk_for_firmware(args.firmware)
    else:
        ps5, ps4 = int(args.ps5, 0), int(args.ps4, 0)
    sdk.write_sdk(elf, ps5, ps4)
    elf.save(args.output)
    print(f'PS5 SDK {old[0]:#010x} -> {ps5:#010x}, PS4 SDK {old[1]:#010x} -> {ps4:#010x}: {args.output}')


def cmd_report(args):
    names = _names(args)
    reference, label, exact = imports.load_reference(args.reference, args.firmware)
    print(f'reference: {label}')
    print()
    provided = imports.load_provider_exports(args.provider or [])
    for path in args.files:
        elf = SceElf.load(path)
        statuses = imports.classify(elf, reference, provided, exact)
        show = (('missing', 'unknown', 'stubbed', 'patched', 'provided', 'data') if args.all
                else ('missing', 'unknown', 'stubbed', 'patched'))
        print(imports.report(os.path.basename(path), elf, statuses, names, show))
        print()


def cmd_stub(args):
    names = _names(args)
    elf = SceElf.load(args.input)
    nids = list(args.nid)
    if args.unknown:
        reference, _, exact = imports.load_reference(args.reference, args.firmware)
        provided = imports.load_provider_exports(args.provider or [])
        nids += [s.nid for s in imports.classify(elf, reference, provided, exact)
                 if s.status in imports.UNRESOLVED and s.plt and s.nid not in nids]
    for p in patch.stub_imports(elf, nids, define_symbol=args.define_symbol):
        print(f'   GOT {p.got_vaddr:#x} -> {p.stub} @ {p.stub_vaddr:#x}   {names.name(p.nid)}')
    elf.save(args.output)
    print(f'{len(nids)} imports stubbed: {args.output}')


def cmd_backport(args):
    result = game.backport_game(args.game, args.output, args.firmware, args.reference,
                                extra_fakelib=args.fakelib, stub_unknown=args.stub_unknown,
                                stub_nids=args.stub_nid, define_symbol=args.define_symbol, names=_names(args),
                                known_fixes=not args.no_known_fixes, patch_param=args.patch_param,
                                candidate_fixes=args.candidate_fixes, extend_text=args.extend_text,
                                fakelib_store=None if args.no_fakelib_store else args.fakelib_store)
    print(game.format_report(result))
    written = sum(m.written for m in result.modules)
    errors = sum(bool(m.error) for m in result.modules)
    print()
    print(f'{written} files written, {errors} errors; report: {os.path.join(args.output, "backport_report.txt")}')
    return 1 if errors else 0


def cmd_fwdb(args):
    if args.action in ('import-genstub', 'import-dump'):
        if not (args.path and args.firmware and args.source):
            raise ValueError(f'fwdb {args.action} needs a folder, --firmware and --source')
        importer = fwdb.import_genstub if args.action == 'import-genstub' else fwdb.import_dump
        db = importer(args.path, args.firmware, args.source)
        os.makedirs(args.db, exist_ok=True)
        path = os.path.join(args.db, f'{db.firmware}.json')
        fwdb.save(db, path)
        print(f'{db.firmware}: {len(db.modules)} modules, {len(db.nids)} NIDs '
              f'({"functions and data" if db.data_symbols else "functions only"}) -> {path}')
        return 0
    dbs = fwdb.load_all(args.db)
    if args.action == 'list':
        for db in dbs:
            print(f'{db.firmware:6} {len(db.modules):4} modules {len(db.nids):6} NIDs  '
                  f'{"functions+data" if db.data_symbols else "functions only"}  {db.source}')
        return 0
    # diff OLD NEW
    by_fw = {db.firmware: db for db in dbs}
    old, new = fwdb.fw_name(args.path), fwdb.fw_name(args.other)
    if old not in by_fw or new not in by_fw:
        raise ValueError(f'fwdb diff needs both firmwares in {args.db} (have: {", ".join(by_fw)})')
    names = _names(args)
    added = fwdb.diff(by_fw[old], by_fw[new])
    for module in sorted(added):
        print(f'{module}: +{len(added[module])}')
        for nid in sorted(added[module], key=names.name):
            print(f'   {nid}  {names.name(nid)}')
    print(f'{sum(map(len, added.values()))} NIDs exported by {new} but not by {old}')
    return 0


def cmd_bps(args):
    if args.action == 'info':
        for path in args.paths:
            p = bps.load(path)
            print(f'== {path}: {p.source_size:#x} -> {p.target_size:#x} bytes, source CRC32 {p.source_crc:08x}, '
                  f'{len(p.actions)} actions')
            for e in bps.edits(p):
                what = e.data.hex() if e.kind == 'write' else f'from {e.from_offset:#x}'
                print(f'   {e.offset:#09x}  {e.kind:11} {e.length:4}  {what}')
        return 0
    if len(args.paths) != 3:
        raise ValueError('bps apply needs PATCH INPUT OUTPUT')
    patch_path, src, dest = args.paths
    with tempfile.TemporaryDirectory() as tmp:
        if fself.file_kind(src) in fself.SELF_MAGICS.values():   # the patches expect the decrypted ELF
            fself.unwrap(src, os.path.join(tmp, 'in.elf'))
            src = os.path.join(tmp, 'in.elf')
        with open(src, 'rb') as f:
            data = bps.apply(bps.load(patch_path), f.read())
        out = os.path.join(tmp, 'out.elf') if args.sign else dest
        with open(out, 'wb') as f:
            f.write(data)
        if args.sign:
            fself.sign(out, dest)
    print(f'patched {args.paths[1]} -> {dest}' + (' (fake-signed)' if args.sign else ''))
    return 0


def _fakelib_check(args):
    """Imports of the stored fakelib set that the target firmware may not export (PLAN 3.2)."""
    if not args.firmware:
        raise ValueError('fakelib check needs --firmware')
    folder = store.find(args.store, args.firmware)
    if not folder:
        raise ValueError(f'no fakelib folder in {args.store} for firmware {args.firmware}')
    names = _names(args)
    reference, label, exact = imports.load_reference(args.reference, args.firmware)
    print(f'fakelibs: {folder}')
    print(f'reference: {label}')
    unresolved = 0
    for name, elf, statuses in store.check(folder, reference, exact):
        print()
        print(imports.report(name, elf, statuses, names, ('missing', 'unknown')))
        for s in statuses:
            if s.status in imports.UNRESOLVED:
                unresolved += 1
                if s.nid in fixes.FIXES:
                    print(f'      -> known fix for {names.name(s.nid)}: {fixes.FIXES[s.nid].kind} (applied by backport)')
    print()
    print(f'{unresolved} unresolved import(s); a missing function only fails when called (lazy PLT binding)')
    return 0


def cmd_fakelib(args):
    if args.action == 'add':
        if not args.firmware or not args.files:
            raise ValueError('fakelib add needs --firmware and at least one file')
        folder, added = store.add(args.store, args.firmware, args.files, args.source or '')
        for name, e in added:
            print(f'   {name:34} {e["container"][:14]:14} SDK {e["ps5_sdk"]}/{e["ps4_sdk"]}  {e["sha256"][:12]}')
        print(f'{len(added)} files added to {folder}')
        return 0
    if args.action == 'check':
        return _fakelib_check(args)
    store_map = store.list_store(args.store)
    if args.firmware:
        folder = store.find(args.store, args.firmware)
        store_map = {os.path.basename(folder): store.load_manifest(folder)} if folder else {}
    if not store_map:
        print(f'no fakelibs in {args.store}' + (f' for firmware {args.firmware}' if args.firmware else ''))
        return 0
    bad = 0
    for fw, manifest in store_map.items():
        print(f'== {fw}  ({os.path.join(args.store, fw)})')
        for name, e in manifest.items():
            built = f'built {e["build_date"]}  ' if e.get('build_date') else ''
            print(f'   {name:34} {e.get("container", "")[:14]:14} SDK {e.get("ps5_sdk")}/{e.get("ps4_sdk")}  '
                  f'{built}{e.get("source", "")}')
        for w in store.build_warnings(os.path.join(args.store, fw)):
            print(f'   WARNING: {w}')
        if args.action == 'verify':
            problems = store.verify(os.path.join(args.store, fw))
            bad += len(problems)
            print('   verify: ' + ('OK' if not problems else '; '.join(f'{n}: {p}' for n, p in problems)))
    return 1 if bad else 0


def main(argv=None):
    ap = argparse.ArgumentParser(prog='ps5bp', description='PS5 game backport toolkit')
    ap.add_argument('--aerolib', default=DEFAULT_AEROLIB, help='NID name database (aerolib.csv)')
    sub = ap.add_subparsers(dest='command', required=True)

    p = sub.add_parser('info', help='file type, module name, SDK fields, import/export counts')
    p.add_argument('files', nargs='+')
    p.set_defaults(func=cmd_info)

    p = sub.add_parser('unwrap', help='SELF -> ELF (keeps the original ELF header)')
    p.add_argument('input')
    p.add_argument('output')
    p.set_defaults(func=cmd_unwrap)

    p = sub.add_parser('sign', help='ELF -> fake-signed SELF (make_fself.py format)')
    p.add_argument('input')
    p.add_argument('output')
    p.set_defaults(func=cmd_sign)

    p = sub.add_parser('sdk', help='set the SDK version fields')
    p.add_argument('input')
    p.add_argument('output')
    g = p.add_mutually_exclusive_group(required=True)
    g.add_argument('--firmware', help='target firmware, e.g. 4 or 4.03')
    g.add_argument('--ps5', help='explicit PS5 SDK value (with --ps4)')
    p.add_argument('--ps4', help='explicit PS4 SDK value (with --ps5)')
    p.set_defaults(func=cmd_sdk)

    p = sub.add_parser('report', help='classify imports against a reference firmware')
    p.add_argument('files', nargs='+')
    p.add_argument('--reference', default=DEFAULT_REFERENCE, help='firmware_db folder or genstub .c folder')
    p.add_argument('--firmware', default='4', help='target firmware used to pick the firmware_db entry')
    p.add_argument('--provider', action='append', help='unwrapped ELF shipped with the game (repeatable)')
    p.add_argument('--all', action='store_true', help='also list provided and data imports')
    p.set_defaults(func=cmd_report)

    p = sub.add_parser('stub', help='point missing imports at local stubs')
    p.add_argument('input')
    p.add_argument('output')
    p.add_argument('--nid', action='append', default=[], help='NID to stub (repeatable)')
    p.add_argument('--unknown', action='store_true', help='stub every PLT import classified as unknown/missing')
    p.add_argument('--firmware', default='4', help='target firmware used to pick the firmware_db entry')
    p.add_argument('--define-symbol', action='store_true',
                   help='also rewrite the import symbol into a local definition (GT7 fakelib style; needed for RELA imports)')
    p.add_argument('--reference', default=DEFAULT_REFERENCE, help='firmware_db folder or genstub .c folder')
    p.add_argument('--provider', action='append')
    p.set_defaults(func=cmd_stub)

    p = sub.add_parser('backport', help='backport a whole game folder (writes only changed files)')
    p.add_argument('game', help='game folder (dump root containing eboot.bin and sce_sys/)')
    p.add_argument('output', help='output folder, e.g. <scanpath>/backports/<TITLE_ID>; never inside the game folder')
    p.add_argument('--firmware', required=True, help='target firmware, e.g. 4 or 4.03')
    p.add_argument('--patch-param', action='store_true',
                   help='lower param.json requiredSystemSoftwareVersion/sdkVersion to the target (written to OUT)')
    p.add_argument('--fakelib', help='folder of extra fakelib .sprx/.prx to add (overrides the store)')
    p.add_argument('--fakelib-store', default=store.DEFAULT_STORE,
                   help='per-firmware fakelib store; fakelibs/<FW>/ is used when --fakelib is not given')
    p.add_argument('--no-fakelib-store', action='store_true', help='do not take fakelibs from the store')
    p.add_argument('--stub-nid', action='append', default=[], help='NID to stub wherever it is imported (repeatable)')
    p.add_argument('--stub-unknown', action='store_true',
                   help='stub every unknown import (risky with the 3.20 reference: may replace functions that exist on the target)')
    p.add_argument('--define-symbol', action='store_true', help='also rewrite stubbed import symbols into local definitions')
    p.add_argument('--no-known-fixes', action='store_true', help='do not apply the proven fixes from ps5bp/fixes.py')
    p.add_argument('--candidate-fixes', action='store_true',
                   help='also stub missing imports listed in ps5bp/candidates.py (AnyPS5 silent stubs; not console-proven)')
    p.add_argument('--extend-text', action='store_true',
                   help='when a module has no int3 padding left, grow its code segment into the page gap '
                        '(not console-tested)')
    p.add_argument('--reference', default=DEFAULT_REFERENCE, help='firmware_db folder or genstub .c folder')
    p.set_defaults(func=cmd_backport)

    p = sub.add_parser('fwdb', help='per-firmware export database: import-genstub, import-dump, list, diff')
    p.add_argument('action', choices=('import-genstub', 'import-dump', 'list', 'diff'))
    p.add_argument('path', nargs='?', help='import: source folder; diff: older firmware')
    p.add_argument('other', nargs='?', help='diff: newer firmware')
    p.add_argument('--firmware', help='import: firmware of the source (e.g. 3.20, 13.60)')
    p.add_argument('--source', help='import: where the data came from (recorded in the JSON)')
    p.add_argument('--db', default=fwdb.DEFAULT_DB)
    p.set_defaults(func=cmd_fwdb)

    p = sub.add_parser('bps', help='BPS patches (BackPork 10.01 -> 6xx/7xx libraries): info, apply')
    p.add_argument('action', choices=('info', 'apply'))
    p.add_argument('paths', nargs='+', help='info: PATCH...; apply: PATCH INPUT OUTPUT')
    p.add_argument('--sign', action='store_true', help='apply: fake-sign the output (make_fself)')
    p.set_defaults(func=cmd_bps)

    p = sub.add_parser('fakelib', help='per-firmware fakelib store: add, list, verify, check')
    p.add_argument('action', choices=('add', 'list', 'verify', 'check'))
    p.add_argument('files', nargs='*', help='modules to add (add)')
    p.add_argument('--firmware', help='firmware folder, e.g. 4 or 4.03 (add: required; list/verify: filter)')
    p.add_argument('--source', help='where the files came from (add; recorded in manifest.json)')
    p.add_argument('--store', default=store.DEFAULT_STORE)
    p.add_argument('--reference', default=DEFAULT_REFERENCE, help='check: firmware_db folder or genstub .c folder')
    p.set_defaults(func=cmd_fakelib)

    args = ap.parse_args(argv)
    if args.command == 'sdk' and args.ps5 is not None and args.ps4 is None:
        ap.error('--ps5 requires --ps4')
    try:
        return args.func(args) or 0
    except (ValueError, OSError) as e:
        print(f'error: {e}', file=sys.stderr)
        return 1
