#!/usr/bin/env python3
"""MIT. Bind the private Runtime to the verified IS620N commissioning core.

Run as user a. Does not activate a candidate, START a PLC, or command an axis.
Requires the existing Runtime to be STOPPED and the physical core to be ready.
"""
import hashlib
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import time
import urllib.error


def run(*args):
    return subprocess.run(args, check=True, capture_output=True, text=True, timeout=30).stdout.strip()


def write(path, content):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + '.is620n-tmp')
    with temporary.open('xb') as stream:
        stream.write(content)
    temporary.chmod(0o600)
    temporary.replace(path)


def main():
    os.umask(0o077)
    if Path.home() != Path('/home/a') or os.geteuid() == 0:
        raise ValueError('Run only as unprivileged user a')
    deployment = Path('/home/a/st-workbench-deployment')
    prefix = Path('/home/a/.local/share/st-workbench/runtime-v4.3.0/deployment')
    source = Path('/home/a/st-workbench-i210/is620n-runtime-axis-worker.py')
    manifest = Path('/home/a/st-workbench-i210/is620n-mcp/axis-manifest.json')
    core = '/run/st-workbench-is620n/controller.sock'
    target = Path('/home/a/.local/share/st-workbench/is620n-axis')
    bridge = str(target / 'axis.sock')
    if hashlib.sha256(source.read_bytes()).hexdigest() != '59b1ea48910b1de7470cfe9409cd28aa7d810be01f6df18f2f140e3ad27ff612':
        raise ValueError('Verified axis worker identity differs')
    if hashlib.sha256(manifest.read_bytes()).hexdigest() != 'e4c1b530fe5631089dc4fc404cd954f577c3fef389742afc638edc3a54f751e0':
        raise ValueError('Commissioned axis manifest identity differs')
    sys.path[:0] = [str(deployment), str(source.parent)]
    import coordinator
    from axis_worker import ProtocolClient
    config_path = deployment / 'config.json'
    config = json.loads(config_path.read_text())
    active = (prefix / 'active.json').read_bytes()
    with coordinator.RestRuntimeConnection(config) as connection:
        if connection.status() != 'STOPPED':
            raise ValueError('Runtime must already be STOPPED; no implicit PLC stop is performed')
    with socket.socket(socket.AF_UNIX) as connection:
        connection.settimeout(2)
        connection.connect(core)
        client = ProtocolClient(connection)
        hello = client.request('HelloRequest', {'clientName': 'is620n-runtime-setup', 'clientVersion': '1'}, 'HelloResponse')
        if not (hello.get('plcAbiVersion') == 3 and hello.get('plcScanBatch') and hello.get('plcActionReceipts')):
            raise ValueError('Physical native ABI3/action receipts are required')
        snapshot = client.request('ReadSnapshotRequest', {'includeDiagnostics': False}, 'SnapshotResponse')
        if not snapshot.get('runtimeReady') or not snapshot.get('ioReady'):
            raise ValueError('Physical core is not ready')
        if any(axis.get('state') != 'Disabled' or axis.get('fault') for axis in snapshot['axes']):
            raise ValueError('All physical axes must be disabled and fault-free')
    if config.get('axis_backend') not in (None, 'EcMotion') or config.get('axis_socket_path') not in (None, bridge):
        raise ValueError('Refusing to replace another configured axis backend')
    if os.path.lexists(bridge):
        raise ValueError('Existing bridge socket requires inspection before reconfiguration')
    target.mkdir(mode=0o700, parents=True, exist_ok=True)
    target.chmod(0o700)
    write(target / 'axis_worker.py', source.read_bytes())
    write(target / 'axis-manifest.json', manifest.read_bytes())
    units = Path('/home/a/.config/systemd/user')
    unit = '''[Unit]
Description=Private IS620N Runtime axis bridge (no automatic motion)
PartOf=stworkbench-openplc.service

[Service]
Type=simple
ExecStart=/usr/bin/python3 /home/a/.local/share/st-workbench/is620n-axis/axis_worker.py --manifest /home/a/.local/share/st-workbench/is620n-axis/axis-manifest.json --socket /home/a/.local/share/st-workbench/is620n-axis/axis.sock --controller-socket /run/st-workbench-is620n/controller.sock --backend EcMotion
ExecStopPost=/usr/bin/rm -f /home/a/.local/share/st-workbench/is620n-axis/axis.sock /home/a/.local/share/st-workbench/is620n-axis/axis.sock.capabilities.json
Restart=no
UMask=0077
NoNewPrivileges=yes
'''
    dropin = '''[Unit]
Wants=stworkbench-is620n-axis.service
After=stworkbench-is620n-axis.service

[Service]
Environment=STWORKBENCH_AXIS_BACKEND=EcMotion
Environment=STWORKBENCH_AXIS_SOCKET=/home/a/.local/share/st-workbench/is620n-axis/axis.sock
'''
    for path, content in [(units / 'stworkbench-is620n-axis.service', unit),
                          (units / 'stworkbench-openplc.service.d/is620n-axis.conf', dropin)]:
        if path.exists() and path.read_text() != content:
            raise ValueError('Existing service configuration differs: ' + str(path))
        write(path, content.encode())
    backup = config_path.with_name('config.before-is620n-axis.json')
    if not backup.exists():
        write(backup, config_path.read_bytes())
    config.update(axis_backend='EcMotion', axis_socket_path=bridge)
    write(config_path, (json.dumps(config, indent=2) + '\n').encode())
    run('systemctl', '--user', 'daemon-reload')
    # Existing PLC is STOPPED; restart only its safe-mode management service.
    run('systemctl', '--user', 'restart', 'stworkbench-openplc.service')
    for _ in range(50):
        if Path(bridge + '.capabilities.json').exists():
            capability = json.loads(Path(bridge + '.capabilities.json').read_text())
            if capability.get('PhysicalBasicAxis') and capability.get('ControllerReady'):
                break
        time.sleep(.1)
    else:
        raise ValueError('Private physical axis bridge did not become ready')
    for attempt in range(50):
        try:
            with coordinator.RestRuntimeConnection(config) as connection:
                if connection.status() != 'STOPPED':
                    raise ValueError('Runtime must remain STOPPED after management restart')
            break
        except (OSError, urllib.error.URLError):
            if attempt == 49:
                raise
            time.sleep(.1)
    if (prefix / 'active.json').read_bytes() != active:
        raise ValueError('Active program identity unexpectedly changed')
    print(json.dumps({'configured': True, 'backend': 'EcMotion', 'socket': bridge,
                      'controllerReady': True, 'physicalBasicAxis': True,
                      'runtimeState': 'STOPPED', 'activeIdentityUnchanged': True,
                      'motionCommandsSent': 0}))


if __name__ == '__main__':
    main()
