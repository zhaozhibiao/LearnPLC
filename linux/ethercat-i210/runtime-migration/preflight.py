#!/usr/bin/python3 -I
# SPDX-License-Identifier: MIT
"""Root-only migration inventory/backup. Never stops services or writes EtherCAT."""
import argparse
import datetime
import hashlib
import json
import os
import pathlib
import re
import stat
import subprocess
import time
import uuid

INSTALLATION = '/etc/st-workbench-ethercat/installation.json'
ACTIVE = '/var/lib/st-workbench-ethercat/active.json'
VERSIONS = '/var/lib/st-workbench-ethercat/versions'
BACKUPS = '/var/backups/st-workbench-runtime'
VERSION_FILES = ('build-manifest.json', 'ethercat-runtime.json', 'machine-config.json', 'io-map.json')
HELPERS = ('/usr/local/libexec/stworkbench-ethercat-apply', '/usr/local/libexec/stworkbench-ethercat-restore')
SERVICES = ('st-workbench-is620n.service', 'st-workbench-csp-smooth-3000a.service',
            'stworkbench-ethercat-restore.service', 'stworkbench-hardware.service', 'stworkbench-hardware.socket')
UNIT_FILES = tuple('/etc/systemd/system/' + s for s in SERVICES) + tuple('/run/systemd/transient/' + s for s in SERVICES)
MAX_FILE = 128 * 1024 * 1024


def sha(data):
    return hashlib.sha256(data).hexdigest()


def parse(data):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result: raise ValueError('Duplicate JSON key')
            result[key] = value
        return result
    result = json.loads(data, object_pairs_hook=unique)
    if not isinstance(result, dict): raise ValueError('Expected JSON object')
    return result


class Layout:
    """Isolated roots are a Python test seam, deliberately absent from the CLI."""
    def __init__(self, root=pathlib.Path('/'), owner=0):
        self.root = pathlib.Path(root).absolute()
        self.owner = owner

    def path(self, name):
        if not isinstance(name, str) or not name.startswith('/') or any(c in name for c in ('\\', ':', '\x00')) or '..' in name.split('/') or str(pathlib.PurePosixPath(name)) != name:
            raise ValueError('Noncanonical path')
        return self.root.joinpath(*pathlib.PurePosixPath(name).parts[1:])

    def secure(self, path, allow_missing=False):
        path = pathlib.Path(path).absolute()
        try: path.relative_to(self.root)
        except ValueError: raise ValueError('Path escapes installation root')
        chain = [path, *path.parents]
        for item in reversed(chain[:chain.index(self.root) + 1]):
            try: info = item.lstat()
            except FileNotFoundError:
                if allow_missing: continue
                raise
            if stat.S_ISLNK(info.st_mode): raise ValueError('Symlink refused')
            if self.owner is not None and (info.st_uid != self.owner or info.st_mode & 0o022):
                raise ValueError('Path must be root owned and not writable by group/others')
        return path

    def read(self, path):
        self.secure(path)
        fd = os.open(path, os.O_RDONLY | getattr(os, 'O_NOFOLLOW', 0) | getattr(os, 'O_NONBLOCK', 0))
        try:
            info = os.fstat(fd)
            if not stat.S_ISREG(info.st_mode) or not 0 <= info.st_size <= MAX_FILE:
                raise ValueError('Expected bounded regular file')
            if self.owner is not None and (info.st_uid != self.owner or info.st_mode & 0o022): raise ValueError('Untrusted file')
            with os.fdopen(fd, 'rb', closefd=False) as stream: data = stream.read(MAX_FILE + 1)
            if len(data) > MAX_FILE: raise ValueError('File exceeds limit')
            return data, info
        finally: os.close(fd)

    def mkdir(self, path):
        self.secure(path, allow_missing=True)
        path.mkdir(mode=0o700, parents=True, exist_ok=True)
        self.secure(path)


def write_file(path, data):
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, 'wb') as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())


def inventory(layout):
    installation = parse(layout.read(layout.path(INSTALLATION))[0])
    active = parse(layout.read(layout.path(ACTIVE))[0])
    fingerprint = active.get('topologyFingerprint')
    if not isinstance(fingerprint, str) or re.fullmatch('[0-9a-f]{64}', fingerprint) is None: raise ValueError('Invalid active fingerprint')
    binary = installation.get('binary')
    if not isinstance(binary, str) or re.fullmatch('/opt/st-workbench-ethercat/ecmotion-rt-[0-9a-f]{12,64}', binary) is None:
        raise ValueError('Pinned binary is outside the fixed release namespace')
    if sha(layout.read(layout.path(binary))[0]) != installation.get('binarySha256'): raise ValueError('Pinned binary hash differs')
    required = [INSTALLATION, ACTIVE, binary, *HELPERS, *(VERSIONS + '/' + fingerprint + '/' + n for n in VERSION_FILES)]
    optional = [name for name in UNIT_FILES if layout.path(name).exists() or layout.path(name).is_symlink()]
    return required + optional


def disabled(statuswords):
    if (not isinstance(statuswords, list) or len(statuswords) != 2 or
            any(type(v) is not int or not 0 <= v <= 65535 or v & 0x6f not in (0x08, 0x40, 0x21, 0x23) for v in statuswords)):
        raise ValueError('Two confirmed non-enabled CiA402 6041 samples required')


def stable_position(feedback):
    if not isinstance(feedback, dict) or feedback.get('object') != '0x6064:0' or type(feedback.get('slave')) is not int or feedback['slave'] != 0:
        raise ValueError('Read-only position evidence for slave 0 is required')
    samples, timestamps = feedback.get('samples'), feedback.get('sampleMonotonicNs')
    if (not isinstance(samples, list) or len(samples) != 20 or
            any(type(v) is not int or not -(2 ** 31) <= v < 2 ** 31 for v in samples) or
            not isinstance(timestamps, list) or len(timestamps) != 20 or
            any(type(v) is not int or v <= 0 for v in timestamps) or
            any(b - a < 25_000_000 for a, b in zip(timestamps, timestamps[1:])) or
            max(samples) - min(samples) > 4):
        raise ValueError('Stable position requires 20 signed 6064 samples spaced at least 25 ms, range at most 4 counts')


def create_backup(layout, audit):
    disabled(audit.get('statuswords'))
    stable_position(audit.get('positionFeedback'))
    names = inventory(layout)
    # Read every required source before creating a backup; missing source cannot produce a valid manifest.
    contents = [(name, *layout.read(layout.path(name))) for name in names]
    directory = layout.path(BACKUPS)
    layout.mkdir(directory)
    backup = directory / (datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%S.%fZ-') + uuid.uuid4().hex)
    backup.mkdir(mode=0o700)
    records = []
    for index, (name, data, info) in enumerate(contents):
        target = backup / 'files' / ('%04d' % index)
        layout.mkdir(target.parent)
        write_file(target, data)
        records.append(dict(path=name, payload='files/%04d' % index, sha256=sha(data), size=len(data), mode=stat.S_IMODE(info.st_mode), uid=info.st_uid, gid=info.st_gid))
    manifest = dict(schemaVersion=1, complete=True, files=records, absentUnitFiles=[p for p in UNIT_FILES if p not in names], audit=audit)
    # This final marker is never written after a partial copy failure.
    write_file(backup / 'manifest.json', json.dumps(manifest, sort_keys=True, indent=2).encode())
    fd = os.open(backup, os.O_RDONLY | getattr(os, 'O_DIRECTORY', 0)) if os.name != 'nt' else None
    if fd is not None:
        try: os.fsync(fd)
        finally: os.close(fd)
    return backup


def command(args):
    return subprocess.run(args, text=True, capture_output=True, timeout=15, env={'PATH': '/usr/sbin:/usr/bin:/sbin:/bin', 'LANG': 'C'})


def service_states():
    result = {}
    for unit in SERVICES:
        run = command(['systemctl', 'show', unit, '--property=LoadState', '--property=ActiveState', '--property=SubState', '--property=MainPID', '--property=FragmentPath'])
        fields = dict(line.split('=', 1) for line in run.stdout.splitlines() if '=' in line)
        required = {'LoadState', 'ActiveState', 'SubState', 'FragmentPath'}
        if unit.endswith('.service'): required.add('MainPID')
        if not required <= fields.keys(): raise ValueError('Cannot confirm systemd state')
        # Socket units have no MainPID property; do not confuse its absence with an unreadable service.
        if unit.endswith('.socket'): fields.setdefault('MainPID', '0')
        if fields['LoadState'] not in ('loaded', 'not-found') or fields['LoadState'] == 'loaded' and run.returncode: raise ValueError('Cannot inspect systemd unit')
        result[unit] = fields
    return result


def process_identity(states):
    """Hash the mapped executable, never print process arguments or environment."""
    result = {}
    for unit in SERVICES[:2]:
        pid = states[unit]['MainPID']
        if not pid.isascii() or not pid.isdecimal(): raise ValueError('Unconfirmed core PID')
        if pid == '0':
            result[unit] = dict(pid=0)
            continue
        executable = pathlib.Path('/proc') / pid / 'exe'
        # /proc/PID/exe is intentionally a kernel magic link; this read-only evidence is never restored.
        with executable.open('rb') as stream:
            info = os.fstat(stream.fileno())
            if not stat.S_ISREG(info.st_mode) or info.st_size > MAX_FILE: raise ValueError('Invalid mapped executable')
            checksum = hashlib.sha256()
            for chunk in iter(lambda: stream.read(65536), b''): checksum.update(chunk)
            result[unit] = dict(pid=int(pid), executable=os.readlink(executable), sha256=checksum.hexdigest(), size=info.st_size)
    return result


def read_status():
    run = command(['/opt/etherlab/bin/ethercat', 'upload', '-p', '0', '-t', 'uint16', '0x6041', '0'])
    if run.returncode: raise ValueError('Read-only EtherCAT 6041 failed')
    words = run.stdout.split()
    if not words or re.fullmatch('[0-9]+', words[-1]) is None: raise ValueError('Unrecognized EtherCAT 6041 response')
    return int(words[-1])


def read_position():
    run = command(['/opt/etherlab/bin/ethercat', 'upload', '-p', '0', '-t', 'int32', '0x6064', '0'])
    if run.returncode: raise ValueError('Read-only EtherCAT 6064 failed')
    words = run.stdout.split()
    if not words or re.fullmatch('-?[0-9]+', words[-1]) is None: raise ValueError('Unrecognized EtherCAT 6064 response')
    return int(words[-1])


def observe_position():
    samples, timestamps = [], []
    for index in range(20):
        if index: time.sleep(.025)
        samples.append(read_position())
        timestamps.append(time.monotonic_ns())
    feedback = dict(object='0x6064:0', slave=0, samples=samples, sampleMonotonicNs=timestamps)
    stable_position(feedback)
    return feedback


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.parse_args()
    if os.geteuid() != 0: raise ValueError('Run the reviewed script as root')
    states = service_states()
    processes = process_identity(states)
    statuses = [read_status()]
    disabled([statuses[0], statuses[0]])
    feedback = observe_position()
    statuses.append(read_status())
    backup = create_backup(Layout(), dict(statuswords=statuses, statusSlave=0, services=states, processes=processes, positionFeedback=feedback,
        plcState='not-probed; unchanged', deviceAccess='read-only 0x6041:0 and 0x6064:0; slave 0'))
    print(json.dumps(dict(status='backed-up-disabled', backup=str(backup), files=len(parse((backup / 'manifest.json').read_bytes())['files']))))


if __name__ == '__main__':
    try: main()
    except (OSError, ValueError, subprocess.SubprocessError): raise SystemExit('Preflight failed; no service or EtherCAT write was requested')
