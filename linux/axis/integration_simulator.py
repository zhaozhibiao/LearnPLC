#!/usr/bin/env python3
"""Isolated Linux smoke test against an explicitly supplied original simulator DLL.

Build copied EcMotion sources outside their original checkout before using this tool.
This helper never discovers, starts, or connects to a physical controller.
"""
import argparse
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import time

from axis_worker import HEADER, COMMAND, FEEDBACK, FEEDBACK_BYTES, read_exact


def wait_path(path, process):
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError('Process exited before socket became ready')
        if os.path.exists(path):
            return
        time.sleep(.01)
    raise RuntimeError('Socket startup timeout')


def run(simulator_dll):
    if os.name != 'posix':
        raise RuntimeError('Run simulator integration on isolated Linux')
    with tempfile.TemporaryDirectory(prefix='wb-axis-') as directory:
        os.chmod(directory, 0o700)
        simulator_socket = os.path.join(directory, 'sim.sock')
        worker_socket = os.path.join(directory, 'axis.sock')
        manifest_path = os.path.join(directory, 'axis-manifest.json')
        Path(manifest_path).write_text(json.dumps(dict(SchemaVersion=1,
            InterfaceVersion='workbench-axis-v1', PlcIntervalMilliseconds=10,
            Axes=[dict(Index=1, Name='Axis1', ControllerAxis='Axis1', Unit='mm',
                       MinPosition=-100, MaxPosition=100)])), encoding='utf-8')
        simulator = subprocess.Popen(['dotnet', str(Path(simulator_dll).resolve()), '--socket', simulator_socket],
                                     stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        worker = None
        bridge = None
        try:
            wait_path(simulator_socket, simulator)
            worker = subprocess.Popen([sys.executable, str(Path(__file__).with_name('axis_worker.py')),
                '--manifest', manifest_path, '--socket', worker_socket, '--controller-socket', simulator_socket,
                '--backend', 'EcMotionSimulator'], stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
            wait_path(worker_socket, worker)
            capabilities = json.loads(Path(worker_socket+'.capabilities.json').read_text())
            assert capabilities['Backend'] == 'EcMotionSimulator'
            assert not capabilities['PhysicalBasicAxis']
            assert not capabilities['Continuous'] and not capabilities['MotionCycleApplied']
            bridge = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            bridge.settimeout(1)
            bridge.connect(worker_socket)
            scan, request = 0, 0

            def command(kind, position=0., velocity=1., done=True):
                nonlocal scan, request
                request += 1
                deadline = time.monotonic()+10
                while time.monotonic() < deadline:
                    scan += 1
                    packet = HEADER.pack(scan,1) + COMMAND.pack(request,kind,position,velocity,0) + bytes(63*COMMAND.size)
                    bridge.sendall(packet)
                    feedback = FEEDBACK.unpack_from(read_exact(bridge,FEEDBACK_BYTES))
                    if feedback[0] == request:
                        assert feedback[3] == 0, ('Axis error', feedback)
                        assert not feedback[4], ('Command aborted', feedback)
                        if (done and feedback[2]) or (not done and feedback[1] and abs(feedback[10])>.01):
                            return feedback
                    time.sleep(.01)
                raise AssertionError(('Command completion timed out', kind))

            command(3)
            command(1)
            assert command(4)[6] == 1
            positioned = command(5, position=.5, velocity=5)
            assert positioned[8] > 0 and abs(positioned[9]-.5) < .001
            jogging = command(6, velocity=2, done=False)
            assert jogging[1] and not jogging[2] and jogging[10] > 0
            command(7)
            command(2)
            # Stop advancing scan: watchdog must abort and revoke readiness.
            deadline = time.monotonic()+2
            while time.monotonic() < deadline:
                # Repeated IPC frames keep communication alive, but are not PLC scans.
                bridge.sendall(HEADER.pack(scan,1) + COMMAND.pack(request,2,0.,1.,0) + bytes(63*COMMAND.size))
                feedback = FEEDBACK.unpack_from(read_exact(bridge,FEEDBACK_BYTES))
                if feedback[3] == 2002:
                    assert feedback[4] and not feedback[2]
                    break
                time.sleep(.01)
            else:
                raise AssertionError('Scan watchdog did not fire')
            print('PASS original C# simulator: Reset/Enable/Home/MoveAbsolute/Jog/Stop/Disable/watchdog; simulator capability remains non-physical')
        finally:
            if bridge: bridge.close()
            if worker:
                try: worker.wait(timeout=3)
                except subprocess.TimeoutExpired:
                    worker.terminate()
                    worker.wait(timeout=3)
                if worker.stderr: worker.stderr.close()
            if simulator.stdin:
                try: simulator.stdin.write(b'quit\n'); simulator.stdin.flush()
                except BrokenPipeError: pass
                simulator.stdin.close()
            try: simulator.wait(timeout=3)
            except subprocess.TimeoutExpired:
                simulator.terminate()
                simulator.wait(timeout=3)
            if simulator.stderr: simulator.stderr.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--simulator-dll', required=True,
                        help='Explicit copied-source EcMotion.Simulator.dll; never a controller service')
    run(parser.parse_args().simulator_dll)
