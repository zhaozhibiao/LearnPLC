#!/usr/bin/python3 -I
# SPDX-License-Identifier: MIT
"""Validate root user archives; --apply restores only in a fixed unprivileged child."""
import argparse
import base64
import importlib.util
import json
import os
import pathlib
import re
import stat
import subprocess


def helper(trusted=False):
    path = pathlib.Path(__file__).absolute().with_name('user-root-snapshot.py')
    if trusted:
        for item in (path, *path.parents):
            info = item.lstat()
            if stat.S_ISLNK(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022: raise ValueError('Untrusted root archive helper')
    spec = importlib.util.spec_from_file_location('user_root_snapshot', path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def load_archive(archive, paths):
    h = helper()
    archive = pathlib.Path(archive).absolute()
    if archive.parent != paths.path(h.ARCHIVES) or re.fullmatch(h.VERSION, archive.name) is None: raise ValueError('Archive outside fixed root backup namespace')
    record = h.parse(paths.read(archive / 'archive.json', root=True))
    raw = paths.read(archive / 'manifest.json', root=True)
    if record.get('schemaVersion') != 1 or record.get('complete') is not True or h.digest(raw) != record.get('sourceManifestSha256'): raise ValueError('Incomplete archive or manifest hash differs')
    manifest = h.inventory(raw)
    if record.get('files') != len(manifest['files']): raise ValueError('Incomplete file inventory')
    content, total = [], len(raw)
    for item in manifest['files']:
        data = paths.read(archive / item['payload'], root=True)
        total += len(data)
        if total > h.MAX_TOTAL or h.digest(data) != item['sha256']: raise ValueError('Archive payload hash differs')
        content.append(data)
    expected = {'manifest.json', 'archive.json', *(item['payload'] for item in manifest['files'])}
    if {path.name for path in archive.iterdir()} != expected: raise ValueError('Unexpected archive content')
    return manifest, content


def check_archive(archive, paths):
    manifest, content = load_archive(archive, paths)
    return manifest['files'], content


# Executed by a fixed /usr/bin/python3 AFTER the subprocess drops all supplementary groups,
# GID and UID to account a. The child receives cached verified bytes via stdin, no archive access.
CHILD = r'''
import base64,hashlib,json,os,pathlib,subprocess,sys,uuid
if os.geteuid() != 1000 or os.getuid() != 1000 or os.getgroups(): raise ValueError('Fixed user identity required')
os.umask(0o077)
request=json.load(sys.stdin)
if request.get('plcState') != 'STOPPED': raise ValueError('Backup did not confirm PLC STOPPED')
units=('stworkbench-openplc.service','stworkbench-is620n-axis.service','stworkbench-engineering.service')
def stopped():
 for unit in units:
  result=subprocess.run(['/usr/bin/systemctl','--user','show',unit,'--property=LoadState','--property=ActiveState','--property=SubState','--property=MainPID'],capture_output=True,text=True,timeout=10)
  fields=dict(line.split('=',1) for line in result.stdout.splitlines() if '=' in line)
  if result.returncode or fields.get('LoadState')!='loaded' or fields.get('ActiveState') not in ('inactive', 'failed') or fields.get('MainPID')!='0' or fields.get('SubState') not in ('dead','failed'): raise ValueError('All three user services must already be explicitly stopped')
stopped()
home=os.open('/home/a',os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW)
staged=[]
try:
 for item in request['files']:
  name=item['path']; p=pathlib.PurePosixPath(name)
  if not name.startswith('/home/a/') or str(p)!=name or '..' in p.parts or '\\' in name or '\x00' in name: raise ValueError('Invalid user target')
  data=base64.b64decode(item['data'],validate=True)
  if hashlib.sha256(data).hexdigest()!=item['sha256'] or type(item['mode']) is not int or not 0<=item['mode']<=0o777: raise ValueError('Child payload differs')
  directory=os.dup(home)
  try:
   for part in p.parts[3:-1]:
    try: os.mkdir(part,mode=0o700,dir_fd=directory)
    except FileExistsError: pass
    nextfd=os.open(part,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW,dir_fd=directory);os.close(directory);directory=nextfd
   try:
    info=os.stat(p.name,dir_fd=directory,follow_symlinks=False)
    if not __import__('stat').S_ISREG(info.st_mode): raise ValueError('Nonregular destination')
   except FileNotFoundError: pass
   temporary='.user-rollback-'+uuid.uuid4().hex
   fd=os.open(temporary,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600,dir_fd=directory)
   staged.append((os.dup(directory),temporary,p.name))
   with os.fdopen(fd,'wb') as stream:
    stream.write(data);stream.flush();os.fsync(stream.fileno());os.fchmod(stream.fileno(),item['mode'])
  finally: os.close(directory)
 stopped()
 for directory,temporary,name in staged: os.replace(temporary,name,src_dir_fd=directory,dst_dir_fd=directory)
finally:
 for directory,temporary,name in staged:
  try: os.unlink(temporary,dir_fd=directory)
  except FileNotFoundError: pass
  os.close(directory)
 os.close(home)
print('User files restored; all services remain stopped; no motion requested.')
'''


def confirm_disabled():
    result = subprocess.run(['/opt/etherlab/bin/ethercat', 'upload', '-p', '0', '-t', 'uint16', '0x6041', '0'],
        capture_output=True, text=True, timeout=15, env={'PATH':'/usr/bin:/bin','LANG':'C'})
    words = result.stdout.split()
    if result.returncode or not words or re.fullmatch('[0-9]+', words[-1]) is None: raise ValueError('Cannot confirm disabled axis')
    status = int(words[-1])
    if not 0 <= status <= 65535 or status & 0x6f not in (0x40, 0x21, 0x23): raise ValueError('Axis must already be disabled')


def restore_archive(archive, paths, account, runner=subprocess.run, disabled_guard=confirm_disabled):
    manifest, data = load_archive(archive, paths)
    if manifest.get('plcState') != 'STOPPED': raise ValueError('Archive must record PLC STOPPED')
    disabled_guard()
    request = dict(plcState=manifest['plcState'], files=[dict(item, data=base64.b64encode(payload).decode('ascii')) for item, payload in zip(manifest['files'], data)])
    result = runner(['/usr/bin/python3', '-I', '-c', CHILD], input=json.dumps(request), text=True, capture_output=True,
        timeout=120, user=1000, group=account.pw_gid, extra_groups=[], cwd='/home/a',
        env={'HOME':'/home/a','USER':'a','LOGNAME':'a','PATH':'/usr/bin:/bin','XDG_RUNTIME_DIR':'/run/user/1000','DBUS_SESSION_BUS_ADDRESS':'unix:path=/run/user/1000/bus'})
    if result.returncode: raise ValueError('Unprivileged restore failed; keep services stopped and retry validated archive')
    return 'User files restored; services remain stopped; no motion requested.'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('archive')
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument('--check', action='store_true')
    modes.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    if os.geteuid() != 0: raise ValueError('Root required')
    h = helper(trusted=True)
    account = h.identity()
    if args.apply: print(restore_archive(args.archive, h.Paths(), account))
    else:
        check_archive(args.archive, h.Paths())
        print('Root user archive verified; no files restored or services changed.')


if __name__ == '__main__':
    try: main()
    except (OSError, ValueError, KeyError, subprocess.SubprocessError): raise SystemExit('User rollback failed; no credentials printed; keep services stopped before retrying')
