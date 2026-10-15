"""Offline sidecar candidate storage; no upload, compilation, device control or activation API."""
import datetime
import hashlib
import json
import os
import pathlib
import re

REVISION = 'a9e38d901fb87deb5a8cf778b52639b9387834d0'
MAX_PACKAGE_BYTES = 50 * 1024 * 1024

def stage(package, candidates, expected_sha256):
    if not re.fullmatch('[0-9a-f]{64}', expected_sha256):
        raise ValueError('Expected SHA256 must be 64 lowercase hex characters')
    package = pathlib.Path(package)
    candidates = pathlib.Path(candidates)
    if package.is_symlink() or not package.is_file():
        raise ValueError('Package must be a regular file, not a symbolic link')
    with package.open('rb') as stream:
        content = stream.read(MAX_PACKAGE_BYTES + 1)
    if len(content) > MAX_PACKAGE_BYTES:
        raise ValueError('Package exceeds candidate-store limit')
    if hashlib.sha256(content).hexdigest() != expected_sha256:
        raise ValueError('Package SHA256 differs from the approved bytes')
    if candidates.is_symlink():
        raise ValueError('Candidate root may not be a symbolic link')
    candidates.mkdir(mode=0o700, parents=True, exist_ok=True)
    destination = candidates / expected_sha256
    # Exclusive creation: candidates are immutable and incomplete writes require explicit review.
    destination.mkdir(mode=0o700)
    record = {
        'schemaVersion': 1,
        'state': 'StagedArchiveUnverified',
        'archiveSha256': expected_sha256,
        'runtimeCommit': REVISION,
        'stagedAtUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'packageAbiVerified': False,
        'activationSupported': False,
    }
    for name, data in [('package.zip', content), ('candidate.json', json.dumps(record, indent=2).encode('utf-8'))]:
        descriptor = os.open(destination / name, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(descriptor, 'wb') as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
    return record

if __name__ == '__main__':
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('package', type=pathlib.Path)
    parser.add_argument('--sha256', required=True)
    arguments = parser.parse_args()
    target = pathlib.Path.home() / '.local/share/st-workbench/runtime-v4.3.0/candidates'
    print(json.dumps(stage(arguments.package, target, arguments.sha256), indent=2))
