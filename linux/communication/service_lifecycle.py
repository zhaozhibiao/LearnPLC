#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Operator-installed private worker lifecycle; no peer IO during preparation."""
import argparse
import hashlib
import json
import os
import pathlib
import re
import subprocess
import sys
import time

UNIT='stworkbench-communication.service'
RUNTIME='stworkbench-openplc.service'


def candidate(prefix, build):
    if not isinstance(build,str) or re.fullmatch('[0-9a-fA-F]{64}',build) is None:
        raise ValueError('Candidate needs an exact SHA256 build identity')
    return pathlib.Path(prefix)/'.versions'/build.upper()/'core/generated'


def load(path):return json.loads(pathlib.Path(path).read_text())


def control(*args,check=True):
    return subprocess.run(['systemctl','--user',*args],check=check,capture_output=True,text=True,timeout=20)


def atomic(path,value):
    temporary=path.with_suffix('.tmp')
    fd=os.open(temporary,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
    try:
        with os.fdopen(fd,'w') as stream:
            json.dump(value,stream);stream.flush();os.fsync(stream.fileno())
        os.replace(temporary,path)
    finally:
        if temporary.exists():temporary.unlink()


def selected(root,config,build):
    generated=candidate(config['prefix'],build)
    stage=load(generated.parent.parent/'stage-result.json')['manifest']
    if stage.get('CommunicationInterfaceVersion') is None:return None
    if stage.get('BuildHash','').upper()!=build.upper():raise ValueError('Staged build identity differs')
    # Import reviewed installed validation, never execute code from the archive to validate it.
    sys.path.insert(0,config['coordinator_directory'])
    import communication_gate
    raw=(generated/'communication-manifest.json').read_bytes()
    communication_gate.validate_manifest(communication_gate.strict_json(raw))
    for name in communication_gate.FILES:
        file=generated/name
        if file.is_symlink() or not file.is_file() or hashlib.sha256(file.read_bytes()).hexdigest().upper()!=stage['Files'].get(name,'').upper():
            raise ValueError('Candidate communication inventory differs: '+name)
    return dict(BuildHash=build.upper(),Manifest=str(generated/'communication-manifest.json'),Worker=str(generated/'communication/communication_worker.py'))


def reset(root,config,build):
    choice=selected(root,config,build) if build else None
    control('stop',UNIT)
    # Only after systemd confirms the old service has exited can its private socket be removed.
    for suffix in ('','.capabilities.json'):
        path=pathlib.Path(config['communication_socket_path']+suffix)
        if path.exists() or path.is_symlink():
            if path.lstat().st_uid!=os.getuid() or path.is_symlink():raise ValueError('Refusing foreign worker residue')
            path.unlink()
    if choice is None:return
    atomic(root/'selected.json',choice)
    control('start',UNIT)
    sys.path.insert(0,config['coordinator_directory']);import communication_gate
    until=time.monotonic()+5
    while time.monotonic()<until:
        try:
            communication_gate.verify_ready({'CommunicationInterfaceVersion':communication_gate.INTERFACE},pathlib.Path(choice['Manifest']).parent,
                config,'STWORKBENCH_COMM_SOCKET='+config['communication_socket_path'])
            return
        except (OSError,ValueError):time.sleep(.05)
    control('stop',UNIT)
    raise ValueError('Private communication worker did not become ready')


def prepare(root,config,build):
    # Runtime must stay available for subsequent compile/activation queries.
    # The PLC itself must already be stopped; this helper never issues STOP.
    sys.path.insert(0,config['coordinator_directory']);import coordinator
    owner=coordinator.Coordinator(config)
    with owner.lock():
        if control('is-active','--quiet',RUNTIME,check=False).returncode!=0:
            raise ValueError('Start private Runtime in safe mode before preparing a candidate; PLC must remain STOPPED')
        with owner.connection() as connection: owner.stopped(connection,True)
        reset(root,config,build)


def main(argv=None):
    parser=argparse.ArgumentParser();parser.add_argument('action',choices=['start-active','stop','prepare-candidate','run-selected'])
    parser.add_argument('--build');args=parser.parse_args(argv)
    root=pathlib.Path(__file__).resolve().parent;config=load(root/'lifecycle.json')
    if args.action=='run-selected':
        choice=load(root/'selected.json');verified=selected(root,config,choice['BuildHash'])
        if choice!=verified:raise ValueError('Worker selection changed')
        os.execv(sys.executable,[sys.executable,choice['Worker'],'--manifest',choice['Manifest'],'--socket',config['communication_socket_path']])
    elif args.action=='stop':control('stop',UNIT)
    elif args.action=='prepare-candidate':
        prepare(root,config,args.build)
    else:
        active=pathlib.Path(config['prefix'])/'active.json'
        build=load(active).get('build_hash') if active.exists() else None
        reset(root,config,build if isinstance(build,str) and re.fullmatch('[0-9a-fA-F]{64}',build) else None)


if __name__=='__main__':main()
