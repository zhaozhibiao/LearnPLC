"""Read-only bundle verification and fixed privileged EtherCAT apply interface."""
import hashlib
import json
import os
import pathlib
import re
import socket
import stat
import struct
import uuid
import time

BROKER_SOCKET = '/run/st-workbench-hardware/control.sock'
CONTROLLER_SOCKET = '/run/st-workbench-is620n/controller.sock'
MAX_JSON = 4 * 1024 * 1024
FILES = ('ethercat-runtime.json', 'machine-config.json', 'io-map.json')


def canonical_identity(topology):
    return json.dumps({k: v for k, v in topology.items() if k != 'topologyFingerprint'},
                      sort_keys=True, separators=(',', ':'), ensure_ascii=False, allow_nan=False).encode('utf-8')


def hex_identity(value):
    if not isinstance(value, str) or not re.fullmatch('[0-9a-fA-F]{64}', value):
        raise ValueError('Invalid SHA256/build identity')
    return value.lower()


def read_regular(path):
    path = pathlib.Path(path).absolute()
    for parent in (path, *path.parents):
        if parent.is_symlink(): raise ValueError('Symlink in hardware path')
    flags = os.O_RDONLY | getattr(os, 'O_NOFOLLOW', 0) | getattr(os, 'O_NONBLOCK', 0)
    if os.open in getattr(os, 'supports_dir_fd', set()):
        directory = os.open(path.anchor, os.O_RDONLY | os.O_DIRECTORY)
        try:
            for part in path.parts[1:-1]:
                next_directory = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=directory)
                os.close(directory)
                directory = next_directory
            fd = os.open(path.name, flags, dir_fd=directory)
        finally: os.close(directory)
    else:
        fd = os.open(path, flags)
    try:
        info = os.fstat(fd)
        if not stat.S_ISREG(info.st_mode) or info.st_size > MAX_JSON:
            raise ValueError('Hardware configuration must be a bounded regular file')
        with os.fdopen(fd, 'rb', closefd=False) as stream: data = stream.read(MAX_JSON + 1)
        if len(data) > MAX_JSON: raise ValueError('Hardware file exceeds capacity')
        return data
    finally: os.close(fd)


def parse_json(data):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result: raise ValueError('Duplicate JSON key')
            result[key] = value
        return result
    value = json.loads(data, object_pairs_hook=pairs, parse_constant=lambda _: (_ for _ in ()).throw(ValueError('Nonfinite JSON')))
    if not isinstance(value, dict): raise ValueError('Expected JSON object')
    return value


def validate_topology(topology, hashes):
    if type(topology.get('schemaVersion')) is not int or topology['schemaVersion'] != 1 or topology.get('readyForRuntime') is not True or topology.get('blockers') != []:
        raise ValueError('EtherCAT topology has unresolved runtime blockers')
    fingerprint = hex_identity(topology.get('topologyFingerprint'))
    if hashlib.sha256(canonical_identity(topology)).hexdigest() != fingerprint:
        raise ValueError('Topology canonical identity mismatch')
    for key, name in [('machineSha256', 'machine-config.json'), ('ioMapSha256', 'io-map.json')]:
        if hex_identity(topology.get(key)) != hashes.get(name): raise ValueError('Hardware companion hash mismatch: ' + name)
    if not isinstance(topology.get('slaves'), list) or not 1 <= len(topology['slaves']) <= 64:
        raise ValueError('Invalid EtherCAT slave topology')
    return fingerprint


def check_bundle(version, config):
    version = pathlib.Path(version)
    build = hex_identity(version.name)
    generated = version / 'core' / 'generated'
    manifest = parse_json(read_regular(generated / 'build-manifest.json'))
    if hex_identity(manifest.get('BuildHash')) != build: raise ValueError('Build manifest identity mismatch')
    inventory = manifest.get('Files')
    if not isinstance(inventory, dict): raise ValueError('Build inventory missing')
    content = {name: read_regular(generated / name) for name in FILES}
    hashes = {name: hashlib.sha256(data).hexdigest() for name, data in content.items()}
    for name, checksum in hashes.items():
        if hex_identity(inventory.get(name)) != checksum: raise ValueError('Build inventory hash mismatch: ' + name)
    for data in content.values(): parse_json(data)
    topology = parse_json(content['ethercat-runtime.json'])
    fingerprint = validate_topology(topology, hashes)
    return dict(status='validated', buildHash=build, topologyFingerprint=fingerprint,
                machineSha256=hashes['machine-config.json'], ioMapSha256=hashes['io-map.json'])


def _exact(stream, size):
    result = bytearray()
    while len(result) < size:
        chunk = stream.recv(size - len(result))
        if not chunk: raise ValueError('Truncated controller response')
        result.extend(chunk)
    return bytes(result)


def native_request(endpoint, kind, payload, expected):
    if not isinstance(endpoint, str) or not endpoint.startswith('/') or len(endpoint.encode()) >= 104:
        raise ValueError('Private native Unix socket required')
    request_id = str(uuid.uuid4())
    envelope = dict(protocolVersion=4, kind=kind, requestId=request_id, sequence=1, payload=payload)
    data = json.dumps(envelope, separators=(',', ':')).encode()
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as stream:
        stream.settimeout(3)
        try:
            stream.connect(endpoint)
        except (FileNotFoundError, ConnectionRefusedError) as error:
            raise ValueError('Native controller socket unavailable') from error
        stream.sendall(struct.pack('<I', len(data)) + data)
        size, = struct.unpack('<I', _exact(stream, 4))
        if not 0 < size <= 1048576: raise ValueError('Invalid controller frame length')
        response = parse_json(_exact(stream, size))
    if (response.get('protocolVersion') != 4 or response.get('requestId') != request_id or response.get('sequence') != 1
            or response.get('kind') != expected or not isinstance(response.get('payload'), dict)):
        raise ValueError('Controller response identity/kind mismatch')
    return response['payload']


def validate_sample(sample):
    if sample.get('runtimeReady') is not True or sample.get('ioReady') is not True:
        raise ValueError('Native runtime/IO is not ready')
    for key in ('sampleCycle', 'sampleMonotonicNs'):
        if type(sample.get(key)) is not int or not 0 < sample[key] < 2**64:
            raise ValueError('Native sample counter is invalid')

def verify_runtime_capabilities(machine, capabilities):
    required = machine.get('requiredRuntimeCapabilities', [])
    if (not isinstance(required, list) or len(required) > 16
            or any(type(value) is not str or value not in {'workbench_motion_migration_v1','workbench_axis_trace_v1'} for value in required)):
        raise ValueError('Unsupported required native runtime capabilities')
    if not required: return
    actual = capabilities.get('runtimeCapabilities')
    if (not isinstance(actual, list) or len(actual) > 64
            or any(type(value) is not str for value in actual)
            or not set(required).issubset(actual)):
        raise ValueError('Native runtime lacks required motion capability')


def verify_applied(generated, config):
    generated = pathlib.Path(generated)
    result = check_bundle(generated.parent.parent, config)
    hello = native_request(config.get('ethercat_controller_socket', CONTROLLER_SOCKET), 'HelloRequest',
                           {'clientName': 'stworkbench-hardware-verify', 'clientVersion': '1'}, 'HelloResponse')
    if hello.get('runtimeReady') is not True: raise ValueError('Loaded native runtime is not ready')
    verify_runtime_capabilities(parse_json(read_regular(generated / 'machine-config.json')), hello)
    for key, expected in [('ethercatTopologyFingerprint', result['topologyFingerprint'])]:
        if hello.get(key) != expected: raise ValueError('Loaded native hardware identity differs: ' + key)
    endpoint = config.get('ethercat_controller_socket', CONTROLLER_SOCKET)
    first = native_request(endpoint, 'ReadSnapshotRequest', {'includeDiagnostics':False}, 'SnapshotResponse')
    validate_sample(first)
    time.sleep(.02)
    second = native_request(endpoint, 'ReadSnapshotRequest', {'includeDiagnostics':False}, 'SnapshotResponse')
    validate_sample(second)
    if second['sampleCycle'] <= first['sampleCycle'] or second['sampleMonotonicNs'] <= first['sampleMonotonicNs']:
        raise ValueError('Native hardware feedback is not advancing')
    return dict(result, status='verified', verified=True, sampleCycle=second['sampleCycle'])


def broker_request(action, build_hash=None, request_id=None):
    if action not in {'status', 'validate', 'apply'}: raise ValueError('Unsupported broker action')
    request_id = request_id or uuid.uuid4().hex
    if not isinstance(request_id, str) or re.fullmatch('[A-Za-z0-9_-]{1,80}', request_id) is None:
        raise ValueError('Invalid broker request identity')
    request = dict(protocolVersion=1, requestId=request_id, action=action)
    if action != 'status':
        hex_identity(build_hash)
        request['buildHash'] = build_hash
    raw = json.dumps(request, separators=(',', ':')).encode('utf-8')
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as stream:
        stream.settimeout(180 if action == 'apply' else 15)
        stream.connect(BROKER_SOCKET)
        _, uid, _ = struct.unpack('3i', stream.getsockopt(socket.SOL_SOCKET, getattr(socket, 'SO_PEERCRED', 17), 12))
        if uid != 0: raise PermissionError('Hardware broker must be root owned')
        stream.sendall(struct.pack('<I', len(raw)) + raw)
        length, = struct.unpack('<I', _exact(stream, 4))
        if not 0 < length <= 1048576: raise ValueError('Invalid broker response capacity')
        response = parse_json(_exact(stream, length))
    if type(response.get('protocolVersion')) is not int or response['protocolVersion'] != 1 or response.get('requestId') != request_id:
        raise ValueError('Hardware broker response identity mismatch; query actual state')
    if response.get('success') is not True:
        result = response.get('result')
        message = response.get('message') or (result.get('error') if isinstance(result, dict) else None)
        raise ValueError('Hardware broker rejected or unconfirmed: ' + str(message or 'query actual state')[:512])
    result = response.get('result')
    if not isinstance(result, dict): raise ValueError('Invalid broker result')
    return result


def matching_bundle(result, expected):
    for key in ('buildHash', 'topologyFingerprint', 'machineSha256', 'ioMapSha256'):
        if not isinstance(result.get(key), str) or result[key].lower() != expected[key].lower():
            raise ValueError('Hardware broker bundle identity mismatch: ' + key)


def apply_bundle(version, config, before_apply=None, request_id=None):
    result = check_bundle(version, config)
    build = pathlib.Path(version).name
    # Prove service availability and the exact bundle BEFORE stopping the user bridge.
    validated = broker_request('validate', build)
    matching_bundle(validated, result)
    if validated.get('status') != 'validated': raise ValueError('Hardware broker validation unconfirmed')
    if before_apply is not None: before_apply()
    applied = broker_request('apply', build, request_id=request_id)
    matching_bundle(applied, result)
    if applied.get('status') != 'applied' or applied.get('verified') is not True:
        raise ValueError('Hardware broker apply outcome unconfirmed; query actual state')
    return dict(result, **{key: value for key, value in applied.items() if key not in result or key == 'status'})
