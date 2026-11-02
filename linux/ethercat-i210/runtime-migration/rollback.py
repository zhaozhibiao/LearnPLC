#!/usr/bin/python3 -I
# SPDX-License-Identifier: MIT
"""Verify a root migration backup; --apply restores files only, never starts services."""
import argparse
import importlib.util
import os
import pathlib
import re
import stat
import subprocess
import tempfile


def helper(require_trusted=False):
    # The invoking reviewed scripts must be installed together in a root-controlled directory.
    path = pathlib.Path(__file__).absolute().with_name('preflight.py')
    if require_trusted:
        for item in (path, *path.parents):
            info = item.lstat()
            if stat.S_ISLNK(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022: raise ValueError('Untrusted migration helper')
    spec = importlib.util.spec_from_file_location('migration_preflight', path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def check_backup(backup, layout):
    p = helper()
    backup = pathlib.Path(backup).absolute()
    if backup.parent != layout.path(p.BACKUPS) or re.fullmatch(r'[0-9]{8}T[0-9]{6}\.[0-9]{6}Z-[0-9a-f]{32}', backup.name) is None:
        raise ValueError('Backup must be a direct version under the fixed root backup directory')
    manifest = p.parse(layout.read(backup / 'manifest.json')[0])
    if manifest.get('schemaVersion') != 1 or manifest.get('complete') is not True: raise ValueError('Incomplete backup')
    p.disabled(manifest.get('audit', {}).get('statuswords'))
    p.stable_position(manifest.get('audit', {}).get('positionFeedback'))
    records = manifest.get('files')
    if not isinstance(records, list) or not 9 <= len(records) <= 9 + len(p.UNIT_FILES): raise ValueError('Invalid backup inventory')
    content, seen = {}, set()
    for index, record in enumerate(records):
        name = record.get('path')
        target = layout.path(name)
        if name in seen: raise ValueError('Duplicate backup target')
        seen.add(name)
        if record.get('payload') != 'files/%04d' % index: raise ValueError('Invalid backup payload path')
        data, _ = layout.read(backup / record['payload'])
        if p.sha(data) != record.get('sha256') or len(data) != record.get('size'): raise ValueError('Backup checksum mismatch')
        if any(type(record.get(k)) is not int for k in ('uid', 'gid', 'mode')) or record['uid'] < 0 or record['gid'] < 0:
            raise ValueError('Invalid original ownership')
        if not 0 <= record['mode'] <= 0o777 or record['mode'] & 0o022: raise ValueError('Unsafe original permissions')
        if layout.owner is not None and record['uid'] != layout.owner: raise ValueError('Original file was not root-owned')
        layout.secure(target, allow_missing=True)
        if target.exists() and not stat.S_ISREG(target.lstat().st_mode): raise ValueError('Nonregular restore destination')
        content[name] = data
    if p.INSTALLATION not in content or p.ACTIVE not in content: raise ValueError('Required root records missing')
    installation = p.parse(content[p.INSTALLATION])
    active = p.parse(content[p.ACTIVE])
    fingerprint, binary = active.get('topologyFingerprint'), installation.get('binary')
    if not isinstance(fingerprint, str) or re.fullmatch('[0-9a-f]{64}', fingerprint) is None: raise ValueError('Invalid saved topology')
    if not isinstance(binary, str) or re.fullmatch('/opt/st-workbench-ethercat/ecmotion-rt-[0-9a-f]{12,64}', binary) is None: raise ValueError('Invalid saved binary')
    required = {p.INSTALLATION, p.ACTIVE, binary, *p.HELPERS, *(p.VERSIONS + '/' + fingerprint + '/' + n for n in p.VERSION_FILES)}
    absent = manifest.get('absentUnitFiles')
    if not isinstance(absent, list) or len(set(absent)) != len(absent) or set(absent) - set(p.UNIT_FILES): raise ValueError('Invalid absent unit inventory')
    require_absent_units(absent, layout)
    if seen != required | (set(p.UNIT_FILES) - set(absent)): raise ValueError('Incomplete inventory or path outside restore allowlist')
    if p.sha(content[binary]) != installation.get('binarySha256'): raise ValueError('Saved installation binary hash differs')
    # Reject extra payloads as well as missing files, including extra links/directories.
    expected = {'manifest.json', *(r['payload'] for r in records)}
    actual = set()
    for directory, directories, files in os.walk(backup, followlinks=False):
        for name in directories:
            layout.secure(pathlib.Path(directory) / name)
        for name in files:
            path = pathlib.Path(directory) / name
            layout.secure(path)
            actual.add(path.relative_to(backup).as_posix())
    if actual != expected: raise ValueError('Unexpected backup content')
    return manifest, content


def require_absent_units(absent, layout):
    # An introduced override/transient unit could supersede restored files. Require explicit
    # operator cleanup rather than deleting unknown newer unit content or accepting it silently.
    for name in absent:
        path = layout.path(name)
        layout.secure(path, allow_missing=True)
        if path.exists() or path.is_symlink(): raise ValueError('Originally absent unit now exists; review and remove it explicitly')


def require_stopped():
    p = helper()
    states = p.service_states()
    for name in p.SERVICES[:3]:
        state = states[name]
        if state['ActiveState'] not in ('inactive', 'failed') or state['MainPID'] != '0': raise ValueError('Core and restore units must already be stopped')


def restore_backup(backup, layout, stopped_guard=require_stopped):
    manifest, content = check_backup(backup, layout)
    stopped_guard()
    staged = []
    try:
        # Stage all files before replacing anything. A failed write never publishes a partial file.
        for record in manifest['files']:
            path = layout.path(record['path'])
            layout.mkdir(path.parent)
            fd, temporary = tempfile.mkstemp(prefix='.migration-restore-', dir=path.parent)
            staged.append((pathlib.Path(temporary), path))
            with os.fdopen(fd, 'wb') as stream:
                stream.write(content[record['path']])
                stream.flush()
                os.fsync(stream.fileno())
                if layout.owner is not None: os.fchown(stream.fileno(), record['uid'], record['gid'])
                os.fchmod(stream.fileno(), record['mode']) if hasattr(os, 'fchmod') else os.chmod(temporary, record['mode'])
        stopped_guard()
        require_absent_units(manifest['absentUnitFiles'], layout)
        # Keep the active and installation records last. Cross-directory restoration is not transactional;
        # on replace failure leave services stopped and report failure, then rerun the same validated backup.
        staged.sort(key=lambda item: item[1] in (layout.path(helper().ACTIVE), layout.path(helper().INSTALLATION)))
        for temporary, target in staged:
            layout.secure(target, allow_missing=True)
            os.replace(temporary, target)
    finally:
        for temporary, _ in staged:
            if temporary.exists(): temporary.unlink()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('backup', help='Exact version directory under /var/backups/st-workbench-runtime')
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--check', action='store_true', help='Default; validate without restoring')
    mode.add_argument('--apply', action='store_true', help='Explicitly restore files; core and restore units must already be stopped')
    args = parser.parse_args()
    if os.geteuid() != 0: raise ValueError('Run the reviewed script as root')
    layout = helper(require_trusted=True).Layout()
    if args.apply:
        restore_backup(args.backup, layout)
        print('Restored validated files; services remain stopped. No PLC start or axis enable requested.')
    else:
        check_backup(args.backup, layout)
        print('Backup verified; no files restored.')


if __name__ == '__main__':
    try: main()
    except (OSError, ValueError, subprocess.SubprocessError): raise SystemExit('Rollback failed; keep services stopped and inspect the root backup before retrying')
