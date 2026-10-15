#!/usr/bin/env python3
"""Run only inside a private test directory on Linux; does not touch any service."""
import argparse
import json
import pathlib
import subprocess
import sys
import tempfile
import time
import hashlib
import stat

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--generated', required=True)
    parser.add_argument('--runtime-include', required=True)
    parser.add_argument('--bridge', required=True)
    args = parser.parse_args()
    generated, bridge = pathlib.Path(args.generated).resolve(), pathlib.Path(args.bridge).resolve()
    worker = pathlib.Path(__file__).with_name('virtual_worker.py')
    # AF_UNIX has a 108-byte path limit; keep private staging sockets short.
    with tempfile.TemporaryDirectory(prefix='wb-', dir=str(generated.parent.parent)) as directory:
        directory = pathlib.Path(directory)
        binary = directory / 'scan-test'
        subprocess.run(['g++', '-std=c++17', '-O2', '-Wall', '-Wextra', '-pthread',
            '-I' + args.runtime_include, '-I' + str(generated), '-I' + str(bridge),
            str(generated / 'generated.cpp'), str(bridge / 'motion_bridge.cpp'),
            str(bridge / 'generated_scan_shim.cpp'), str(bridge / 'test_scan.cpp'),
            '-o', str(binary)], check=True)
        samples = [{'TimeSeconds': i / 100,
                    'Position': {'X': i / 100, 'Y': 0., 'Z': 0.},
                    'Velocity': {'X': 1., 'Y': 0., 'Z': 0.},
                    'Acceleration': {'X': 0., 'Y': 0., 'Z': 0.}} for i in range(101)]
        manifest = {'SchemaVersion': 1, 'Backend': 'VirtualContinuous', 'Paths': [{
            'Asset': {'Id': '00000000-0000-0000-0000-000000000001', 'NumericId': 7,
                'Version': 1, 'ContentHash': 'a' * 64, 'Mode': 'Continuous', 'Unit': 'mm',
                'Axes': ['X', 'Y'], 'Profile': {'CycleSeconds': .01}},
            'Trajectory': {'DurationSeconds': 1., 'Samples': samples}}]}
        manifest_path = directory / 'motion-manifest.json'
        manifest_path.write_text(json.dumps(manifest), encoding='utf-8')
        socket_path = directory / 'virtual.sock'
        process = subprocess.Popen([sys.executable, str(worker), '--manifest', str(manifest_path), '--socket', str(socket_path)])
        try:
            for _ in range(100):
                if pathlib.Path(str(socket_path) + '.capabilities.json').exists():
                    break
                if process.poll() is not None:
                    raise RuntimeError('virtual worker failed to start')
                time.sleep(.01)
            sidecar = pathlib.Path(str(socket_path) + '.capabilities.json')
            readiness = json.loads(sidecar.read_text())
            assert readiness['PID'] == process.pid
            assert readiness['Backend'] == 'VirtualContinuous'
            assert readiness['ManifestSha256'] == hashlib.sha256(manifest_path.read_bytes()).hexdigest()
            assert stat.S_IMODE(sidecar.stat().st_mode) == 0o600
            subprocess.run([str(binary), str(socket_path)], check=True, timeout=10)
        finally:
            process.terminate()
            process.wait(timeout=5)
        assert not socket_path.exists()
        assert not pathlib.Path(str(socket_path) + '.capabilities.json').exists()
        subprocess.run([sys.executable, '-m', 'unittest', 'discover', '-s', str(worker.parent), '-p', 'test_virtual_worker.py'], check=True)
    print('ISOLATED VIRTUAL ONLY: compiled ST/native IPC/worker verification passed')

if __name__ == '__main__':
    main()
