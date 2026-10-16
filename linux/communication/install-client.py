#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Explicit offline installer: writes private units/config, never starts services."""
import argparse
import json
import os
import pathlib
import shutil
import subprocess
import sys


def units(root):
    script=root+'/service_lifecycle.py';sock=root+'/ipc/worker.sock'
    service='[Unit]\nDescription=Private PLC communication client worker\n[Service]\nType=simple\nUMask=0077\nExecStart=/usr/bin/python3 '+script+' run-selected\nRestart=no\nKillMode=control-group\nTimeoutStopSec=15\n'
    dropin='[Service]\nEnvironment=STWORKBENCH_COMM_SOCKET='+sock+'\nExecStartPre=/usr/bin/python3 '+script+' start-active\nExecStopPost=/usr/bin/python3 '+script+' stop\n'
    return service,dropin


def main(argv=None):
    parser=argparse.ArgumentParser();parser.add_argument('--coordinator-directory',default='/home/a/st-workbench-deployment')
    parser.add_argument('--apply',action='store_true');args=parser.parse_args(argv)
    if not args.apply:parser.error('Review this installer and pass --apply explicitly; no service will be started')
    if os.name!='posix' or os.getuid()==0 or sys.version_info<(3,11):raise ValueError('Unprivileged Linux Python 3.11+ required')
    directory=pathlib.Path(args.coordinator_directory).resolve();home=pathlib.Path.home().resolve()
    if not directory.is_relative_to(home) or any(c.isspace() for c in str(directory)):raise ValueError('Private home directory without spaces required')
    if subprocess.run(['systemctl','--user','is-active','--quiet','stworkbench-openplc.service']).returncode==0:
        raise ValueError('Runtime must be explicitly stopped before installing lifecycle hooks')
    configpath=directory/'config.json';config=json.loads(configpath.read_text())
    root=directory/'communication-client';root.mkdir(mode=0o700,exist_ok=True);os.chmod(root,0o700)
    ipc=root/'ipc';ipc.mkdir(mode=0o700,exist_ok=True);os.chmod(ipc,0o700)
    source=pathlib.Path(__file__).resolve().parent;deployment=source.parent/'deployment'
    # Existing config and credentials are preserved; reviewed coordinator modules are distributed together.
    for path in [*deployment.glob('*.py'),deployment/'runtime-profile.json']:
        if path.name.startswith('test_'):continue
        shutil.copy2(path,directory/path.name)
    shutil.copy2(source/'service_lifecycle.py',root/'service_lifecycle.py')
    config.update(communication_backend='NativeClient',communication_socket_path=str(ipc/'worker.sock'))
    configpath.write_text(json.dumps(config,indent=2));os.chmod(configpath,0o600)
    lifecycle=dict(config,coordinator_directory=str(directory))
    (root/'lifecycle.json').write_text(json.dumps(lifecycle,indent=2));os.chmod(root/'lifecycle.json',0o600)
    service,dropin=units(str(root));user=home/'.config/systemd/user';user.mkdir(parents=True,exist_ok=True)
    (user/'stworkbench-communication.service').write_text(service)
    drops=user/'stworkbench-openplc.service.d';drops.mkdir(exist_ok=True)
    (drops/'communication.conf').write_text(dropin)
    print('Files installed; no service started. Review units, then run systemctl --user daemon-reload explicitly.')


if __name__=='__main__':main()
