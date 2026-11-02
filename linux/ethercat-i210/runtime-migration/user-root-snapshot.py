#!/usr/bin/python3 -I
# SPDX-License-Identifier: MIT
"""Import an explicitly hash-approved private user snapshot into a root-only archive."""
import argparse
import datetime
import hashlib
import json
import os
import pathlib
import re
import stat
import uuid

SOURCES = '/home/a/.local/share/st-workbench/runtime-migration-backups'
ARCHIVES = '/var/backups/st-workbench-runtime-user'
MAX_FILE = 128 * 1024 * 1024
MAX_TOTAL = 256 * 1024 * 1024
VERSION = r'[0-9]{8}T[0-9]{6}(?:\.[0-9]{6})?Z-[0-9a-f]{32}'


def digest(data): return hashlib.sha256(data).hexdigest()


def parse(data):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result: raise ValueError('Duplicate JSON key')
            result[key] = value
        return result
    value = json.loads(data, object_pairs_hook=unique)
    if not isinstance(value, dict): raise ValueError('Expected JSON object')
    return value


def canonical(name):
    if not isinstance(name, str) or not name.startswith('/') or '..' in name.split('/') or any(c in name for c in ('\\', ':', '\x00')) or str(pathlib.PurePosixPath(name)) != name:
        raise ValueError('Noncanonical path')
    return name


class Paths:
    """The isolated root/owner parameters are test-only; production CLI exposes neither."""
    def __init__(self, root=pathlib.Path('/'), owner=0, source_uid=1000):
        self.root, self.owner, self.source_uid = pathlib.Path(root).absolute(), owner, source_uid

    def path(self, name): return self.root.joinpath(*pathlib.PurePosixPath(canonical(name)).parts[1:])

    def secure(self, path, root=False, missing=False):
        path = pathlib.Path(path).absolute()
        path.relative_to(self.root)
        chain = [path, *path.parents]
        for item in chain[:chain.index(self.root) + 1]:
            try: info = item.lstat()
            except FileNotFoundError:
                if missing: continue
                raise
            if stat.S_ISLNK(info.st_mode): raise ValueError('Symlink refused')
            if root and self.owner is not None and (info.st_uid != self.owner or info.st_mode & 0o022): raise ValueError('Archive must be root-controlled')

    def read(self, path, root=False):
        self.secure(path, root=root)
        flags = os.O_RDONLY | getattr(os, 'O_NOFOLLOW', 0) | getattr(os, 'O_NONBLOCK', 0)
        # Walk directory handles so a concurrent user rename cannot introduce a parent symlink.
        if os.open in getattr(os, 'supports_dir_fd', set()):
            directory = os.open(path.anchor, os.O_RDONLY | os.O_DIRECTORY)
            try:
                for part in path.parts[1:-1]:
                    next_fd = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=directory)
                    os.close(directory)
                    directory = next_fd
                fd = os.open(path.name, flags, dir_fd=directory)
            finally: os.close(directory)
        else: fd = os.open(path, flags)
        try:
            info = os.fstat(fd)
            owner = self.owner if root else self.source_uid
            if not stat.S_ISREG(info.st_mode) or info.st_size > MAX_FILE or info.st_nlink != 1: raise ValueError('Expected bounded regular file with no hard links')
            if owner is not None and info.st_uid != owner: raise ValueError('Source ownership differs')
            if root and self.owner is not None and info.st_mode & 0o077: raise ValueError('Root archive file must remain private')
            with os.fdopen(fd, 'rb', closefd=False) as stream: data = stream.read(MAX_FILE + 1)
            if len(data) > MAX_FILE: raise ValueError('Source exceeds limit')
            return data
        finally: os.close(fd)


def inventory(raw):
    manifest = parse(raw)
    records = manifest.get('files')
    if manifest.get('schemaVersion') != 1 or not isinstance(records, list) or not 1 <= len(records) <= 128: raise ValueError('Invalid user snapshot inventory')
    seen = set()
    for index, item in enumerate(records):
        name = canonical(item.get('path'))
        if not name.startswith('/home/a/') or name in seen: raise ValueError('Restore target outside fixed user home or duplicate')
        seen.add(name)
        if item.get('payload') != str(index): raise ValueError('Invalid payload name')
        if not isinstance(item.get('sha256'), str) or re.fullmatch('[0-9a-f]{64}', item['sha256']) is None: raise ValueError('Invalid payload hash')
        if type(item.get('mode')) is not int or not 0 <= item['mode'] <= 0o777: raise ValueError('Invalid saved file permissions')
    return manifest


def write_private(path, data):
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, 'O_NOFOLLOW', 0), 0o600)
    with os.fdopen(fd, 'wb') as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())


def create_snapshot(source, expected, paths):
    source = pathlib.Path(source).absolute()
    if source.parent != paths.path(SOURCES) or re.fullmatch(VERSION, source.name) is None: raise ValueError('Source outside fixed user backup namespace')
    if not isinstance(expected, str) or re.fullmatch('[0-9a-f]{64}', expected) is None: raise ValueError('Expected manifest SHA256 required')
    raw = paths.read(source / 'manifest.json')
    if digest(raw) != expected: raise ValueError('Audited manifest hash differs')
    manifest = inventory(raw)
    cache, total = [], len(raw)
    for item in manifest['files']:
        data = paths.read(source / item['payload'])
        total += len(data)
        if total > MAX_TOTAL or digest(data) != item['sha256']: raise ValueError('Source payload hash or capacity differs')
        cache.append(data)
    base = paths.path(ARCHIVES)
    paths.secure(base, root=True, missing=True)
    base.mkdir(mode=0o700, parents=True, exist_ok=True)
    paths.secure(base, root=True)
    archive = base / (datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%S.%fZ-') + uuid.uuid4().hex)
    archive.mkdir(mode=0o700)
    write_private(archive / 'manifest.json', raw)
    for index, data in enumerate(cache): write_private(archive / str(index), data)
    write_private(archive / 'archive.json', json.dumps(dict(schemaVersion=1, complete=True, source=str(source), sourceManifestSha256=expected, files=len(cache)), sort_keys=True).encode())
    if os.name != 'nt':
        directory = os.open(archive, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try: os.fsync(directory)
        finally: os.close(directory)
    return archive


def identity():
    import pwd
    account = pwd.getpwnam('a')
    if account.pw_uid != 1000 or account.pw_dir != '/home/a': raise ValueError('Unexpected fixed engineering account')
    return account


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source')
    parser.add_argument('expectedManifestSha256')
    args = parser.parse_args()
    if os.geteuid() != 0: raise ValueError('Root required')
    identity()
    os.umask(0o077)
    archive = create_snapshot(args.source, args.expectedManifestSha256, Paths())
    print(json.dumps(dict(status='root-user-snapshot-verified', archive=str(archive))))


if __name__ == '__main__':
    try: main()
    except (OSError, ValueError, KeyError): raise SystemExit('Private user archive failed; no credentials were printed or services changed')
