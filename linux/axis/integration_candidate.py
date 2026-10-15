#!/usr/bin/env python3
"""Run an explicitly supplied test harness/candidate against an isolated simulator.

No activation, service control, or physical controller connection is performed.
"""
import argparse
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from integration_simulator import wait_path


def run(dll, manifest, harness, library):
    with tempfile.TemporaryDirectory(prefix='wb-axis-so-') as directory:
        os.chmod(directory, 0o700)
        simulator_socket, worker_socket = directory+'/sim.sock', directory+'/axis.sock'
        simulator = subprocess.Popen(['dotnet', str(Path(dll).resolve()), '--socket', simulator_socket],
            stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        worker = None
        try:
            wait_path(simulator_socket, simulator)
            worker = subprocess.Popen([sys.executable, str(Path(__file__).with_name('axis_worker.py')),
                '--manifest', str(Path(manifest).resolve()), '--socket', worker_socket,
                '--controller-socket', simulator_socket, '--backend', 'EcMotionSimulator'],
                stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
            wait_path(worker_socket, worker)
            # Capability is verified before any PLC scan and must remain simulator-only.
            sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'deployment'))
            import coordinator
            import json
            generated = Path(manifest).resolve().parent
            bundle = json.loads((generated/'build-manifest.json').read_text())
            config = dict(axis_backend='EcMotionSimulator', axis_socket_path=worker_socket)
            env = 'STWORKBENCH_AXIS_BACKEND=EcMotionSimulator STWORKBENCH_AXIS_SOCKET='+worker_socket
            coordinator.verify_axis_ready(bundle, generated, config, env)
            try:
                coordinator.verify_axis_ready(bundle, generated, dict(config, axis_backend='EcMotion'), env)
            except coordinator.DeploymentError:
                pass
            else:
                raise AssertionError('Simulator accepted as physical controller')
            subprocess.run([str(Path(harness).resolve()), str(Path(library).resolve()), worker_socket], check=True, timeout=60)
        finally:
            if worker:
                worker.terminate()
                worker.wait(timeout=3)
                worker.stderr.close()
            try:
                simulator.stdin.write(b'quit\n'); simulator.stdin.flush()
            except BrokenPipeError:
                pass
            simulator.stdin.close()
            try:
                simulator.wait(timeout=3)
            except subprocess.TimeoutExpired:
                simulator.terminate(); simulator.wait(timeout=3)
            simulator.stderr.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ('simulator-dll', 'manifest', 'harness', 'library'):
        parser.add_argument('--'+key, required=True)
    args = parser.parse_args()
    run(args.simulator_dll, args.manifest, args.harness, args.library)
