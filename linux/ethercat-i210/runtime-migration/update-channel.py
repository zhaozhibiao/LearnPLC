"""Atomically update only the two user-owned coordinator channel modules."""
import hashlib,json,os,pathlib,tempfile
if os.getuid()!=1000:raise SystemExit('Run as user a')
root=pathlib.Path('/home/a/st-workbench-i210/runtime-migration-release-20261101')
manifest=json.loads((root/'channel-hashes.json').read_text())
if set(manifest)!={'coordinator.py','ethercat_hardware.py'}:raise ValueError('Unexpected channel inventory')
captured={name:(root/name).read_bytes() for name in manifest}
for name,data in captured.items():
    if hashlib.sha256(data).hexdigest()!=manifest[name]:raise ValueError('Channel source differs')
for name,data in captured.items():
    target=pathlib.Path('/home/a/st-workbench-deployment')/name
    if target.is_symlink():raise ValueError('Symbolic channel destination')
    fd,tmp=tempfile.mkstemp(prefix='.migration-channel-',dir=target.parent)
    try:
        with os.fdopen(fd,'wb') as out:os.fchmod(out.fileno(),0o600);out.write(data);out.flush();os.fsync(out.fileno())
        os.replace(tmp,target)
    finally:
        if os.path.lexists(tmp):os.unlink(tmp)
print(json.dumps(dict(status='channel-updated',hashes=manifest)))
