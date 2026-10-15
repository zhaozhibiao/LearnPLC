#!/usr/bin/env python3
"""Build/load an isolated candidate only. Never activates any PLC or service."""
import argparse
import hashlib
import importlib.util
import json
import pathlib
import subprocess
import sys
import time
import uuid

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--package', required=True)
    parser.add_argument('--directory', required=True)
    parser.add_argument('--coordinator', required=True)
    parser.add_argument('--runtime-source', required=True)
    parser.add_argument('--candidate-test', required=True)
    args = parser.parse_args()
    root = pathlib.Path(args.directory).resolve()
    if not root.is_dir() or root.is_symlink() or str(root) == '/':
        raise RuntimeError('private existing test directory required')
    spec = importlib.util.spec_from_file_location('coordinator', args.coordinator)
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
    package = pathlib.Path(args.package).resolve()
    import zipfile
    with zipfile.ZipFile(package) as archive:
        manifest = json.loads(archive.read('build-manifest.json'))
    coordinator = module.Coordinator({'prefix': str(root / 'staged'), 'runtime_source': args.runtime_source,
        'runtime_commit': module.RUNTIME_COMMIT, 'exclusive_control': True})
    request = {'request_id': str(uuid.uuid4()), 'build_hash': manifest['BuildHash'],
        'package_path': str(package), 'package_sha256': hashlib.sha256(package.read_bytes()).hexdigest()}
    coordinator.stage(request)
    result = coordinator.compile(request)
    if result['state'] != 'compiled':
        raise RuntimeError(result.get('log', 'candidate compilation failed'))
    version = coordinator.version(manifest['BuildHash'])
    generated = version / 'core' / 'generated'
    binary = root / 'candidate-test'
    subprocess.run(['g++', '-std=c++17', '-DSTRUCPP_THREADED', '-fno-gnu-unique', '-O1', '-Wall', '-Wextra',
        '-I' + str(generated), '-I' + str(generated / 'strucpp_runtime' / 'include'),
        args.candidate_test, '-ldl', '-o', str(binary)], check=True)
    socket = root / 'candidate.sock'
    worker = subprocess.Popen([sys.executable, str(generated / 'paths' / 'virtual_worker.py'),
        '--manifest', str(generated / 'paths' / 'motion-manifest.json'), '--socket', str(socket)])
    try:
        for _ in range(200):
            if socket.exists() and pathlib.Path(str(socket) + '.capabilities.json').exists(): break
            if worker.poll() is not None: raise RuntimeError('private virtual worker failed')
            time.sleep(.01)
        config = {'motion_backend': 'VirtualContinuous', 'motion_socket_path': str(socket)}
        module.verify_motion_ready(manifest, generated, config,
            'STWORKBENCH_MOTION_BACKEND=VirtualContinuous STWORKBENCH_MOTION_SOCKET=' + str(socket))
        subprocess.run([str(binary), str(version / 'build' / 'new_libplc.so'), str(socket)], check=True, timeout=35)
        try:
            module.verify_motion_ready(manifest, generated, config, '')
            raise RuntimeError('missing Runtime motion environment accepted')
        except module.DeploymentError: pass
    finally:
        worker.terminate(); worker.wait(timeout=5)
    print('PASS sealed package, Linux pinned Runtime compilation, matching worker capability, actual SO scan execution; virtual only')

if __name__ == '__main__': main()
