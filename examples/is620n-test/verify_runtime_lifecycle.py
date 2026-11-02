#!/usr/bin/env python3
"""MIT. Explicit zero-command PLC bridge sessions; never acquire axis control."""
import hashlib
import json
from pathlib import Path
import socket
import sys
import time

sys.path[:0] = ['/home/a/.local/share/st-workbench/is620n-axis',
               '/home/a/st-workbench-deployment']
import axis_worker as w
import coordinator


def main():
    config = json.loads(Path('/home/a/st-workbench-deployment/config.json').read_text())
    active_path = Path(config['prefix'], 'active.json')
    active = active_path.read_bytes()
    with coordinator.RestRuntimeConnection(config) as connection:
        assert connection.status() == 'STOPPED', 'PLC must remain STOPPED'
    cap_path = Path(config['axis_socket_path'] + '.capabilities.json')
    initial = json.loads(cap_path.read_text())
    pid = initial['PID']
    assert initial['PhysicalBasicAxis'] and initial['ControllerReady'], 'Initial bridge not ready'
    with socket.socket(socket.AF_UNIX) as raw:
        raw.settimeout(2)
        raw.connect('/run/st-workbench-is620n/controller.sock')
        core = w.ProtocolClient(raw)
        core.request('HelloRequest', {'clientName': 'readonly-session-check', 'clientVersion': '1'}, 'HelloResponse')
        before = core.request('ReadSnapshotRequest', {'includeDiagnostics': True}, 'SnapshotResponse')
        assert before['runtimeReady'] and before['axes'][0]['state'] == 'Disabled'
        for session in range(3):
            with socket.socket(socket.AF_UNIX) as plc:
                plc.settimeout(2)
                plc.connect(config['axis_socket_path'])
                for scan in range(1, 6):
                    plc.sendall(w.HEADER.pack(scan, 1) + bytes(64 * w.COMMAND.size))
                    feedback = w.FEEDBACK.unpack_from(w.read_exact(plc, w.FEEDBACK_BYTES))
                    assert feedback[0] == 0 and feedback[3] == 0 and feedback[5] == 0, ('Invalid zero-command feedback', session, feedback)
                    time.sleep(.01)
            # Shutdown revokes capability before the new session's actual Hello/
            # snapshot completes; wait for the real readiness publication.
            time.sleep(.02)
            for _ in range(50):
                assert Path(config['axis_socket_path']).is_socket(), 'Socket disappeared on PLC EOF'
                cap = json.loads(cap_path.read_text())
                assert cap['PID'] == pid, 'Bridge process restarted'
                if cap['PhysicalBasicAxis'] and cap['ControllerReady']:
                    break
                time.sleep(.05)
            else:
                raise AssertionError('Bridge did not become ready after session ended')
        after = core.request('ReadSnapshotRequest', {'includeDiagnostics': True}, 'SnapshotResponse')
    assert after['axes'][0]['state'] == 'Disabled' and after['axes'][0]['fault'] is None
    assert before['lastAppliedCommandSequence'] == after['lastAppliedCommandSequence'] == 0, 'Unexpected native axis command'
    assert active_path.read_bytes() == active, 'Active identity changed'
    with coordinator.RestRuntimeConnection(config) as connection:
        assert connection.status() == 'STOPPED'
    environment = coordinator.subprocess.check_output(['systemctl', '--user', 'show',
        'stworkbench-openplc.service', '--property=Environment', '--value'], text=True)
    checked = []
    for version in (Path(config['prefix']) / '.versions').iterdir():
        generated = version / 'core/generated'
        manifest_path = generated / 'axis-manifest.json'
        if manifest_path.exists() and manifest_path.read_bytes() == Path('/home/a/.local/share/st-workbench/is620n-axis/axis-manifest.json').read_bytes():
            stage = json.loads((version / 'stage-result.json').read_text())
            coordinator.verify_axis_ready(stage['manifest'], generated, config, environment)
            checked.append(version.name)
    result = dict(readonlyPlcConnections=3, zeroCommandScansPerConnection=5,
        workerPid=pid, socketSurvivedAllDisconnects=True, physicalBasicAxis=True,
        controllerReady=True, plcState='STOPPED', axisState=after['axes'][0]['state'],
        finalVelocity=after['axes'][0]['actualVelocity'], activeIdentityUnchanged=True,
        appliedCommandSequence=after['lastAppliedCommandSequence'],
        checkedCandidateBuildHashes=checked, diagnostics=after['diagnostics'],
        workerSha256=hashlib.sha256(Path(w.__file__).read_bytes()).hexdigest())
    Path('/home/a/st-workbench-i210/runtime-axis-lifecycle-live.json').write_text(json.dumps(result, indent=2))
    print(json.dumps(result))


if __name__ == '__main__':
    main()
