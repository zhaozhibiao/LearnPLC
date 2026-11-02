#!/usr/bin/python3 -I
# SPDX-License-Identifier: MIT
"""Restore the last root-approved core at boot; never start PLC or enable axes."""
import hashlib
import importlib.machinery
import importlib.util
import os
import pathlib
import re
import stat
import time

HELPER = pathlib.Path('/usr/local/libexec/stworkbench-ethercat-apply')


def hex_identity(value):
    if not isinstance(value, str) or re.fullmatch('[0-9a-fA-F]{64}', value) is None:
        raise ValueError('Invalid saved hardware identity')
    return value.lower()


def load_helper():
    for path in (HELPER, *HELPER.parents):
        info = path.lstat()
        if stat.S_ISLNK(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022:
            raise ValueError('Restore helper must remain root controlled')
    loader = importlib.machinery.SourceFileLoader('root_hardware_restore', str(HELPER))
    spec = importlib.util.spec_from_loader(loader.name, loader)
    helper = importlib.util.module_from_spec(spec)
    loader.exec_module(helper)
    return helper


def restore_arguments(helper):
    helper.root_owned(helper.INSTALLATION, 0o600)
    installation = helper.parse_json(helper.read_regular(helper.INSTALLATION))
    binary = pathlib.Path(installation['binary'])
    helper.root_owned(binary)
    if not binary.is_file() or binary.stat().st_size > 128 * 1024 * 1024:
        raise ValueError('Invalid pinned binary')
    checksum = hashlib.sha256()
    with binary.open('rb') as stream:
        for chunk in iter(lambda:stream.read(65536), b''): checksum.update(chunk)
    if checksum.hexdigest() != hex_identity(installation.get('binarySha256')):
        raise ValueError('Pinned binary checksum differs')
    helper.root_owned(helper.ACTIVE_RECORD)
    active = helper.parse_json(helper.read_regular(helper.ACTIVE_RECORD))
    fingerprint = hex_identity(active.get('topologyFingerprint'))
    directory = helper.VERSIONS / fingerprint
    helper.root_owned(directory)
    manifest_path = directory / 'build-manifest.json'
    helper.root_owned(manifest_path)
    manifest = helper.parse_json(helper.read_regular(manifest_path))
    # The root directory is keyed by physical topology and deliberately reused
    # by later program builds. Its original manifest BuildHash need not equal
    # the latest applied program; prove the physical files against both records.
    hex_identity(manifest.get('BuildHash'))
    hex_identity(active.get('buildHash'))
    content, hashes = {}, {}
    for name in helper.FILES:
        path = directory / name
        helper.root_owned(path)
        content[name] = helper.read_regular(path)
        hashes[name] = hashlib.sha256(content[name]).hexdigest()
        if hashes[name] != hex_identity(manifest.get('Files', {}).get(name)):
            raise ValueError('Saved hardware checksum differs')
    for key, name in [('machineSha256', 'machine-config.json'), ('ioMapSha256', 'io-map.json')]:
        if hex_identity(active.get(key)) != hashes[name]: raise ValueError('Saved hardware and root record differ')
    if helper.validate_topology(helper.parse_json(content['ethercat-runtime.json']), hashes) != fingerprint:
        raise ValueError('Saved topology and root record differ')
    # Reused topology directories may retain an older program's manifest. The
    # latest root-approved active record carries the program ABI requirements.
    machine = helper.parse_json(content['machine-config.json'])
    program_manifest = active.get('runtimeRequirements', manifest)
    if hasattr(helper, 'required_runtime_capabilities'):
        required = helper.required_runtime_capabilities(machine, program_manifest)
        if required and not hasattr(helper, 'check_loaded_runtime_capabilities'):
            raise ValueError('Restore helper cannot verify loaded runtime capabilities')
        helper.check_runtime_capabilities(binary, machine, program_manifest)
    else:
        if (not isinstance(program_manifest, dict) or program_manifest.get('AxisInterfaceVersion') == 'workbench-axis-v2'
                or program_manifest.get('RequiredRuntimeCapabilities')):
            raise ValueError('Restore helper cannot verify program runtime capabilities')
        if hasattr(helper, 'check_runtime_capabilities'):
            helper.check_runtime_capabilities(binary, machine)
        elif machine.get('requiredRuntimeCapabilities'):
            raise ValueError('Restore helper cannot verify required runtime capabilities')
    trace_arguments = []
    if hasattr(helper, 'axis_trace_arguments'):
        trace_arguments = helper.axis_trace_arguments(machine, fingerprint)
        if trace_arguments:
            import grp
            helper.prepare_axis_trace(trace_arguments, grp.getgrnam('a').gr_gid)
    elif machine.get('axisTrace') is not None:
        raise ValueError('Restore helper cannot verify axis trace settings')
    return [str(binary), '--ethercat-config', str(directory / 'ethercat-runtime.json'),
            '--machine-config', str(directory / 'machine-config.json'), '--io-map', str(directory / 'io-map.json'),
            '--socket', helper.CONTROLLER_SOCKET, '--cpu', '2', *trace_arguments]


def loaded_requirements(helper, args):
    if not hasattr(helper, 'required_runtime_capabilities'): return []
    directory = pathlib.Path(args[2]).parent
    active = helper.parse_json(helper.read_regular(helper.ACTIVE_RECORD))
    manifest = helper.parse_json(helper.read_regular(directory / 'build-manifest.json'))
    machine = helper.parse_json(helper.read_regular(directory / 'machine-config.json'))
    return helper.required_runtime_capabilities(machine, active.get('runtimeRequirements', manifest))


def start_saved_core(helper):
    result = helper.command(['systemctl', 'show', helper.SERVICE, '--property=LoadState', '--property=ActiveState', '--property=MainPID'], check=False)
    fields = {}
    for line in result.stdout.splitlines():
        key, separator, value = line.partition('=')
        if not separator or key not in {'LoadState','ActiveState','MainPID'} or key in fields:
            raise ValueError('Core service state is unconfirmed')
        fields[key] = value
    if set(fields) != {'LoadState','ActiveState','MainPID'} or not fields['MainPID'].isascii() or not fields['MainPID'].isdecimal():
        raise ValueError('Core service state is unconfirmed')
    if fields['LoadState'] == 'loaded' and fields['ActiveState'] == 'active' and int(fields['MainPID']) > 0 and result.returncode == 0:
        if hasattr(helper, 'required_runtime_capabilities'):
            args = restore_arguments(helper)
            required = loaded_requirements(helper, args)
            if required:
                if not hasattr(helper, 'check_loaded_runtime_capabilities'):
                    raise ValueError('Restore helper cannot verify loaded runtime capabilities')
                hello = helper.native_request(helper.CONTROLLER_SOCKET,'HelloRequest',dict(clientName='boot-restore',clientVersion='1'),'HelloResponse')
                helper.check_loaded_runtime_capabilities(hello, required)
        return 'already-running' 
    if (fields['LoadState'] not in {'loaded','not-found'} or fields['ActiveState'] not in {'inactive','failed'} or fields['MainPID'] != '0'
            or fields['LoadState'] == 'loaded' and result.returncode != 0):
        raise ValueError('Core service state is unconfirmed')
    args = restore_arguments(helper)
    helper.command([args[0], '--validate-ethercat-config', args[2], *args[3:7], *args[11:]])
    helper.command(['systemctl','reset-failed',helper.SERVICE], check=False)
    if hasattr(helper,'wait_for_core_release'): helper.wait_for_core_release()
    helper.command(['systemd-run', '--unit=st-workbench-is620n', '--service-type=exec', '--uid=root', '--gid=a',
        '--property=LimitMEMLOCK=infinity', '--property=LimitRTPRIO=95', '--property=NoNewPrivileges=yes', '--property=UMask=0027',
        '--property=TimeoutStopSec=5', '--property=KillMode=control-group', '--property=RuntimeMaxSec=infinity', '--property=CPUAffinity=2',
        '--property=After=st-workbench-cpu-isolation.service', '--property=RuntimeDirectory=st-workbench-is620n', '--property=RuntimeDirectoryMode=0750', '--', *args])
    required = loaded_requirements(helper, args)
    fingerprint = pathlib.Path(args[2]).parent.name
    deadline = time.monotonic() + 10
    while True:
        try:
            hello = helper.native_request(helper.CONTROLLER_SOCKET,'HelloRequest',dict(clientName='boot-restore',clientVersion='1'),'HelloResponse')
            if required:
                if not hasattr(helper, 'check_loaded_runtime_capabilities'):
                    raise ValueError('Restore helper cannot verify loaded runtime capabilities')
                helper.check_loaded_runtime_capabilities(hello, required)
            if hello.get('runtimeReady') is not True or hello.get('ethercatTopologyFingerprint') != fingerprint:
                raise ValueError('Restored core identity is unconfirmed')
            samples = []
            for _ in range(2):
                sample = helper.native_request(helper.CONTROLLER_SOCKET,'ReadSnapshotRequest',dict(includeDiagnostics=False),'SnapshotResponse')
                if sample.get('runtimeReady') is not True or sample.get('ioReady') is not True or not isinstance(sample.get('axes'), list):
                    raise ValueError('Restored feedback is not ready')
                if hasattr(helper,'disabled_snapshot'):
                    helper.disabled_snapshot(sample,require_zero=True)
                elif any(a.get('state') != 'Disabled' or a.get('fault') is not None for a in sample['axes']):
                    raise ValueError('Restored axes must remain disabled')
                if any(type(sample.get(k)) is not int or sample[k] <= 0 for k in ('sampleCycle','sampleMonotonicNs')):
                    raise ValueError('Restored feedback counters are invalid')
                samples.append(sample)
                time.sleep(.02)
            if any(samples[1][k] <= samples[0][k] for k in ('sampleCycle','sampleMonotonicNs')):
                raise ValueError('Restored feedback is not advancing')
            topology = helper.parse_json(helper.read_regular(pathlib.Path(args[2])))
            if hasattr(helper, 'verify_drive_windows'):
                helper.verify_drive_windows(topology)
            elif any(sdo.get('index') == 0x6065 for slave in topology.get('slaves', []) for sdo in slave.get('startupSdos', [])):
                raise ValueError('Restore helper cannot confirm drive following windows')
            return 'restored-disabled'
        except (OSError, ValueError):
            if time.monotonic() >= deadline: raise
            time.sleep(.1)


if __name__ == '__main__':
    import sys
    if os.geteuid() != 0 or len(sys.argv) != 1: raise SystemExit('Root restore service takes no arguments')
    print(start_saved_core(load_helper()), flush=True)
