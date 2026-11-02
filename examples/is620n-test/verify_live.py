#!/usr/bin/env python3
"""MIT. Compile by default. --execute-once runs REAL ST against commissioned Axis1.

Independent test process; does not activate/replace an existing OpenPLC program.
Only uses the fixed, operator-installed physical controller socket.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('output', type=Path)
    parser.add_argument('--execute-once', action='store_true')
    args = parser.parse_args()
    output = args.output.resolve(strict=True)
    with tempfile.TemporaryDirectory(prefix='is620n-live-') as directory:
        os.chmod(directory, 0o700)
        binary = str(Path(directory) / 'st-live')
        subprocess.run(['g++', '-std=c++20', '-pthread', '-I'+str(output),
                        '-I'+str(output/'strucpp_runtime/include'),
                        str(output/'generated.cpp'), str(output/'axis_bridge.cpp'),
                        str(output/'generated_axis_scan_shim.cpp'),
                        str(Path(__file__).with_name('test_live.cpp')), '-o', binary], check=True)
        print('Compiled actual ST/axis bridge; execute requested:', args.execute_once, flush=True)
        if not args.execute_once:
            return
        # Exact root-installed profile approved and validated in this commissioning.
        machine = Path('/run/st-workbench-is620n/machine.json')
        if hashlib.sha256(machine.read_bytes()).hexdigest() != 'd94ed507186b795d9493099d083f4ab556f0ecfeaeaf7de014565b906aeec4aa':
            raise RuntimeError('Commissioned machine profile hash mismatch; no motion')
        worker_script = '/home/a/st-workbench-i210/is620n-mcp/linux/axis/axis_worker.py'
        private_socket = str(Path(directory)/'axis.sock')
        manifest = output/'axis-manifest.json'
        worker = subprocess.Popen([sys.executable, worker_script, '--manifest', str(manifest),
                                   '--socket', private_socket, '--controller-socket',
                                   '/run/st-workbench-is620n/controller.sock', '--backend', 'EcMotion'],
                                  stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        try:
            capability_file = Path(private_socket+'.capabilities.json')
            deadline=time.monotonic()+5
            ready=False
            while time.monotonic()<deadline:
                if worker.poll() is not None:
                    raise RuntimeError('Physical worker exited before test')
                if capability_file.exists():
                    capability=json.loads(capability_file.read_text())
                    if capability.get('Backend')=='EcMotion' and capability.get('ControllerReady') is True and capability.get('PhysicalBasicAxis') is True:
                        if capability.get('ManifestSha256') != hashlib.sha256(manifest.read_bytes()).hexdigest():
                            raise RuntimeError('Worker manifest identity mismatch')
                        ready=True
                        break
                time.sleep(.01)
            if not ready:
                raise RuntimeError('No confirmed physical readiness; no motion')
            subprocess.run([binary, private_socket, '--execute-once'], check=True, timeout=25)
        finally:
            # Bridge closure causes worker Stop/release. Let that finish before exit.
            try:
                worker.wait(timeout=3)
            except subprocess.TimeoutExpired:
                worker.terminate()
                worker.wait(timeout=3)
            error=worker.stderr.read().decode(errors='replace')
            worker.stderr.close()
            if error:
                print(error, file=sys.stderr)


if __name__=='__main__':
    main()
