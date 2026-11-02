#!/usr/bin/env python3
"""Prepare the fixed, stopped v2 user service. Never start/restart a service or PLC."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile

ENDPOINT='/home/a/.local/share/st-workbench/axis-v2/axis.sock'
SERVICE='stworkbench-axis-v2.service'

def deployment_modules():
    directory=Path(__file__).resolve().parents[1]/'deployment'
    if str(directory) not in sys.path:sys.path.insert(0,str(directory))
    import configuration_download,axis_gate
    return configuration_download,axis_gate

def verified_candidate(version):
    return deployment_modules()[0].candidate(version)

def service_stopped():
    result=subprocess.run(['systemctl','--user','show',SERVICE,'--property=LoadState','--property=ActiveState'],
        check=False,capture_output=True,text=True,timeout=10)
    values=dict(line.split('=',1) for line in result.stdout.splitlines() if '=' in line)
    if values.get('LoadState')=='not-found' and values.get('ActiveState') in (None,'inactive'):
        return True
    if result.returncode!=0:raise ValueError('Cannot confirm v2 user service state')
    return values.get('LoadState')=='loaded' and values.get('ActiveState') in ('inactive','failed')

def regular(path,limit=1048576,private=False):
    path=Path(path).absolute()
    for part in (path,*path.parents):
        if part.is_symlink():raise ValueError('Symlink in installation path')
    fd=os.open(path,os.O_RDONLY|getattr(os,'O_NOFOLLOW',0)|getattr(os,'O_NONBLOCK',0))
    with os.fdopen(fd,'rb') as stream:
        info=os.fstat(stream.fileno())
        if not stat.S_ISREG(info.st_mode) or not 0<info.st_size<=limit:raise ValueError('Bounded regular installation file required')
        if private and os.name=='posix' and (info.st_uid!=os.getuid() or stat.S_IMODE(info.st_mode)!=0o600):raise ValueError('Private configuration owner/mode must be current user/0600')
        raw=stream.read(limit+1)
        if len(raw)>limit:raise ValueError('Installation file capacity exceeded')
        return raw

def private_directory(path,private=True):
    for part in (path,*path.parents):
        if part.is_symlink():raise ValueError('Symlink in private installation directory')
    existed=path.exists()
    path.mkdir(parents=True,mode=0o700,exist_ok=True)
    info=path.stat()
    if hasattr(os,'getuid') and info.st_uid!=os.getuid():raise ValueError('Installation directory owner differs')
    if private or not existed:path.chmod(0o700)
    if os.name=='posix' and stat.S_IMODE(path.stat().st_mode)&0o022:raise ValueError('Installation directory is writable by another user')

def verify_parents(path,home):
    for part in (path,*path.parents):
        if part.is_symlink():raise ValueError('Symlink in installation parent')
        if part.exists() and os.name=='posix':
            info=part.stat()
            if info.st_uid!=os.getuid() or stat.S_IMODE(info.st_mode)&0o022:
                raise ValueError('Installation parents must be owned and not writable by other users')
        if part==home:break

def atomic(path,raw):
    if path.is_symlink():raise ValueError('Symlink installation target')
    fd,name=tempfile.mkstemp(prefix='.'+path.name+'-',dir=path.parent)
    try:
        with os.fdopen(fd,'wb') as stream:
            os.chmod(name,0o600);stream.write(raw);stream.flush();os.fsync(stream.fileno())
        os.replace(name,path)
    finally:
        if os.path.exists(name):os.unlink(name)

def prepare(version,home=Path('/home/a')):
    """Stage only: no native connection, control lease, motion or service activation."""
    if not service_stopped():raise ValueError('V2 service must already be stopped')
    generated,build,_=verified_candidate(version)
    _,gate=deployment_modules()
    if build.get('AxisInterfaceVersion')!=gate.INTERFACE_V2:raise ValueError('Verified v2 candidate required')
    worker=regular(generated/'axes/axis_worker_v2.py')
    manifest_raw=regular(generated/'axis-manifest.json',65536)
    for name,raw in [('axes/axis_worker_v2.py',worker),('axis-manifest.json',manifest_raw)]:
        if build.get('Files',{}).get(name,'').lower()!=hashlib.sha256(raw).hexdigest():raise ValueError('Candidate source hash differs')
    if worker!=regular(Path(__file__).with_name('axis_worker_v2.py')):raise ValueError('Candidate worker differs from approved installer worker')
    manifest=gate.validate_manifest(gate.strict_json(manifest_raw))
    if manifest['InterfaceVersion']!=gate.INTERFACE_V2:raise ValueError('V2 manifest required')
    target=home/'.local/share/st-workbench/axis-v2'
    if os.path.lexists(target/'axis.sock'):raise ValueError('Existing v2 socket requires inspection')
    config_path=home/'st-workbench-deployment/config.json'
    original=regular(config_path,private=True)
    config=gate.strict_json(original)
    if not isinstance(config,dict) or config.get('axis_backend')!='EcMotion':raise ValueError('Existing EcMotion deployment configuration required')
    if config.get('axis_v2_socket_path') not in (None,ENDPOINT) or config.get('axis_socket_path')==ENDPOINT:raise ValueError('Conflicting v2 endpoint')
    config['axis_v2_socket_path']=ENDPOINT
    units=home/'.config/systemd/user'
    dropins=units/'stworkbench-openplc.service.d'
    unit=('''[Unit]
Description=Private Workbench axis dynamics v2 bridge (manual activation only)
PartOf=stworkbench-openplc.service

[Service]
Type=simple
ExecStart=/usr/bin/python3 /home/a/.local/share/st-workbench/axis-v2/axis_worker_v2.py --manifest /home/a/.local/share/st-workbench/axis-v2/axis-manifest.json --socket '''+ENDPOINT+''' --controller-socket /run/st-workbench-is620n/controller.sock --backend EcMotion
ExecStopPost=/usr/bin/rm -f '''+ENDPOINT+' '+ENDPOINT+'''.capabilities.json
Restart=no
UMask=0077
NoNewPrivileges=yes
''').encode()
    environment=('''[Service]
Environment=STWORKBENCH_AXIS_V2_SOCKET='''+ENDPOINT+'\n').encode()
    metadata=dict(SchemaVersion=1,InterfaceVersion=gate.INTERFACE_V2,CommandBytes=4112,FeedbackBytes=6656,
        RequiredRuntimeCapabilities=manifest['RequiredRuntimeCapabilities'],WorkerSha256=hashlib.sha256(worker).hexdigest(),
        ManifestSha256=hashlib.sha256(manifest_raw).hexdigest(),BuildHash=build.get('BuildHash'),started=False)
    for path in (target,units,dropins,config_path.parent):verify_parents(path,home)
    # Validate every destination before any write; never overwrite a foreign service/drop-in.
    for path,raw in [(units/SERVICE,unit),(dropins/'axis-v2.conf',environment)]:
        if os.path.lexists(path) and regular(path)!=raw:raise ValueError('Existing service configuration differs')
    for directory in (target,units,dropins):private_directory(directory,private=directory==target)
    for name,raw in [('axis_worker_v2.py',worker),('axis-manifest.json',manifest_raw),('prepared.json',(json.dumps(metadata,indent=2)+'\n').encode())]:atomic(target/name,raw)
    atomic(units/SERVICE,unit);atomic(dropins/'axis-v2.conf',environment)
    backup=config_path.with_name('config.before-axis-v2.json')
    if not os.path.lexists(backup):atomic(backup,original)
    # Recheck to avoid silently overwriting another configuration writer.
    if regular(config_path)!=original:raise ValueError('Deployment configuration changed during preparation')
    atomic(config_path,(json.dumps(config,indent=2)+'\n').encode())
    return metadata

def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--candidate',required=True)
    args=parser.parse_args()
    if os.name!='posix' or Path.home()!=Path('/home/a') or os.geteuid()==0:raise ValueError('Run only as unprivileged user a')
    os.umask(0o077)
    result=prepare(Path(args.candidate))
    subprocess.run(['systemctl','--user','daemon-reload'],check=True,capture_output=True,timeout=10)
    print(json.dumps(result))

if __name__=='__main__':main()
