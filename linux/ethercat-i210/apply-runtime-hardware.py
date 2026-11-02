#!/usr/bin/python3 -I
# Self-contained reviewed root helper; never imports staged or user-owned code.
import sys
import contextlib
import ssl
import urllib.request
import tempfile
import time
"""Read-only bundle verification and fixed privileged EtherCAT apply interface."""
import hashlib
import json
import os
import pathlib
import re
import socket
import stat
import struct
import subprocess
import uuid

ROOT_HELPER = '/usr/local/libexec/stworkbench-ethercat-apply'
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


def read_regular(path, required_uid=None, required_mode=None):
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
        if required_uid is not None and info.st_uid != required_uid: raise ValueError('Private credentials owner mismatch')
        if required_mode is not None and stat.S_IMODE(info.st_mode) != required_mode: raise ValueError('Private credentials must be mode0600')
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
    result = dict(status='validated', buildHash=build, topologyFingerprint=fingerprint,
                  machineSha256=hashes['machine-config.json'], ioMapSha256=hashes['io-map.json'])
    required = required_runtime_capabilities(parse_json(content['machine-config.json']), manifest)
    if manifest.get('AxisInterfaceVersion') == 'workbench-axis-v2' or manifest.get('RequiredRuntimeCapabilities'):
        result['runtimeRequirements'] = {key: manifest[key] for key in
            ('AxisInterfaceVersion','AxisExportMarker','AxisCommandBytes','AxisFeedbackBytes') if key in manifest}
        result['runtimeRequirements']['RequiredRuntimeCapabilities'] = required
    return result


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
        stream.connect(endpoint)
        stream.sendall(struct.pack('<I', len(data)) + data)
        size, = struct.unpack('<I', _exact(stream, 4))
        if not 0 < size <= 1048576: raise ValueError('Invalid controller frame length')
        response = parse_json(_exact(stream, size))
    if (response.get('protocolVersion') != 4 or response.get('requestId') != request_id or response.get('sequence') != 1
            or response.get('kind') != expected or not isinstance(response.get('payload'), dict)):
        raise ValueError('Controller response identity/kind mismatch')
    return response['payload']


INSTALLATION = pathlib.Path('/etc/st-workbench-ethercat/installation.json')
CANDIDATES = pathlib.Path('/home/a/.local/share/st-workbench/runtime-v4.3.0/deployment/.versions')
VERSIONS = pathlib.Path('/var/lib/st-workbench-ethercat/versions')
ACTIVE_RECORD = pathlib.Path('/var/lib/st-workbench-ethercat/active.json')
PLC_CREDENTIALS = pathlib.Path('/home/a/.local/share/st-workbench/runtime-v4.3.0/data/coordinator-auth.json')
PLC_CA = pathlib.Path('/home/a/.local/share/st-workbench/runtime-v4.3.0/source/webserver/certOPENPLC.pem')
SERVICE = 'st-workbench-is620n.service'
APPLY_LOCK = pathlib.Path('/run/stworkbench-ethercat-apply.lock')
SAFE_ENV = {'PATH':'/usr/sbin:/usr/bin:/sbin:/bin', 'LANG':'C.UTF-8'}


def root_owned(path, mode=None):
    path = pathlib.Path(path)
    if not path.is_absolute(): raise ValueError('Root installation paths must be absolute')
    for entry in (path, *path.parents):
        info = entry.lstat()
        if stat.S_ISLNK(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022:
            raise ValueError('Installation path is not exclusively root controlled')
    if mode is not None and stat.S_IMODE(path.stat().st_mode) != mode:
        raise ValueError('Installation config must have mode 0600')


class NoRestRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, newurl):
        raise ValueError('Local Runtime REST redirects are prohibited')


def plc_status():
    # The webserver is the sole owner of native PLC IPC. Never connect to it here.
    import pwd
    credentials_raw = read_regular(PLC_CREDENTIALS, required_uid=pwd.getpwnam('a').pw_uid, required_mode=0o600)
    if len(credentials_raw) > 16384: raise ValueError('Private credentials capacity exceeded')
    credentials = parse_json(credentials_raw)
    ca = read_regular(PLC_CA).decode('ascii')
    context = ssl.create_default_context(cadata=ca)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRestRedirect(), urllib.request.HTTPSHandler(context=context))
    token = None
    def call(path, body=None):
        headers = {'Content-Type':'application/json'}
        if token is not None: headers['Authorization'] = 'Bearer ' + token
        request = urllib.request.Request('https://localhost:8443' + path,
            data=json.dumps(body,allow_nan=False).encode('utf-8') if body is not None else None,headers=headers)
        with opener.open(request, timeout=3) as response:
            raw = response.read(1024*1024+1)
            if len(raw) > 1024*1024: raise ValueError('Runtime REST response capacity exceeded')
            return parse_json(raw)
    try:
        token = call('/api/login', credentials).get('access_token')
        if not isinstance(token,str) or not 1 <= len(token) <= 8192 or any(not 33 <= ord(c) <= 126 for c in token):
            raise ValueError('Invalid private Runtime access token')
        response = call('/api/status').get('status')
        if not isinstance(response,str) or response not in {'STATUS:STOPPED','STATUS:RUNNING','STATUS:EMPTY','STATUS:TRANSITIONING'}:
            raise ValueError('Invalid Runtime REST STATUS value')
        return response[7:]
    finally:
        token = None
        credentials.clear()

def disabled_snapshot(snapshot, require_zero=False):
    if snapshot.get('outputsAtConfiguredSafeState') is not True:
        raise ValueError('Native outputs must be at configured safe state')
    if snapshot.get('runtimeReady') is not True or snapshot.get('ioReady') is not True:
        raise ValueError('Native runtime and IO must be ready')
    axes = snapshot.get('axes')
    if not isinstance(axes, list) or len(axes) > 64: raise ValueError('Missing or excessive native axes')
    for axis in axes:
        if not isinstance(axis, dict): raise ValueError('Invalid native axis')
        raw = axis.get('driveStatusword')
        if raw is not None and (type(raw) is not int or not 0 <= raw <= 65535 or raw & 0x6f not in {0x08,0x40,0x21,0x23}):
            raise ValueError('Drive status does not confirm torque-disabled state')
        recovered_transport = (axis.get('state') == 'ErrorStop' and axis.get('motionBusy') is False
            and type(raw) is int and 0 <= raw <= 65535 and raw & 0x6f == 0x08)
        if not recovered_transport and (axis.get('state') != 'Disabled' or 'fault' not in axis or axis.get('fault') is not None):
            raise ValueError('Every native axis must have confirmed non-enabled drive state')
    if require_zero and snapshot.get('lastAppliedCommandSequence') != 0:
        raise ValueError('New core already accepted an axis command')


def preflight(allow_initial_start=False):
    if plc_status() != 'STOPPED': raise ValueError('PLC must be STOPPED before hardware apply')
    if allow_initial_start is True and not os.path.lexists(CONTROLLER_SOCKET):
        service = command(['systemctl', 'show', SERVICE, '--property=LoadState', '--property=ActiveState', '--property=MainPID'], check=False)
        fields = {}
        for line in service.stdout.splitlines():
            key, separator, value = line.partition('=')
            if not separator or key not in {'LoadState','ActiveState','MainPID'} or key in fields:
                raise ValueError('Invalid initial native service status')
            fields[key] = value
        if (set(fields) != {'LoadState','ActiveState','MainPID'} or fields['MainPID'] != '0'
                or fields['ActiveState'] not in {'inactive','failed'}
                or (fields['LoadState'] != 'not-found' and (fields['LoadState'] != 'loaded' or service.returncode != 0))):
            raise ValueError('Initial core must be absent or inactive with MainPID=0')
        return True
    native_request(CONTROLLER_SOCKET, 'HelloRequest', {'clientName':'hardware-preflight','clientVersion':'1'}, 'HelloResponse')
    disabled_snapshot(native_request(CONTROLLER_SOCKET, 'ReadSnapshotRequest', {'includeDiagnostics':False}, 'SnapshotResponse'))


def command(args, check=True):
    result = subprocess.run(args, check=False, capture_output=True, text=True, timeout=20, env=SAFE_ENV, cwd='/')
    if check and result.returncode:
        raise ValueError('System command failed: ' + str(args[0]) + ': ' + (result.stderr or result.stdout or str(result.returncode))[:1024].strip())
    return result


def wait_for_core_release():
    deadline = time.monotonic() + 3
    while True:
        result = command(['systemctl','show',SERVICE,'--property=LoadState','--property=ActiveState','--property=MainPID'],check=False)
        fields = dict(line.split('=',1) for line in result.stdout.splitlines() if '=' in line)
        if fields.get('MainPID') != '0' or fields.get('ActiveState') not in {'inactive','failed'}:
            raise ValueError('Core service has not stopped; no new core started')
        if fields.get('LoadState') == 'not-found' and not pathlib.Path('/run/systemd/transient/' + SERVICE).exists(): return
        if time.monotonic() >= deadline:
            raise ValueError('Stopped core unit is retained by systemd dependencies; old command will not be reused')
        time.sleep(.05)


def make_root_directory(path):
    path.mkdir(parents=True, exist_ok=True, mode=0o750)
    root_owned(path)


@contextlib.contextmanager
def apply_lock():
    import fcntl
    root_owned(APPLY_LOCK.parent)
    fd = os.open(APPLY_LOCK, os.O_RDWR | os.O_CREAT | getattr(os, 'O_NOFOLLOW', 0) | getattr(os, 'O_NONBLOCK', 0), 0o600)
    acquired = False
    try:
        info = os.fstat(fd)
        if not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or stat.S_IMODE(info.st_mode) != 0o600:
            raise ValueError('Apply lock must be a root-owned regular 0600 file')
        try:
            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            acquired = True
        except BlockingIOError as error:
            raise ValueError('Hardware apply is already running') from error
        yield
    finally:
        try:
            if acquired: fcntl.flock(fd, fcntl.LOCK_UN)
        finally: os.close(fd)


def apply(build):
    hex_identity(build)
    if os.geteuid() != 0: raise ValueError('Root helper requires root')
    with apply_lock():
        return _apply_locked(build)


TRACE_DIRECTORY = pathlib.PurePosixPath('/var/lib/st-workbench-ethercat/traces')


def axis_trace_arguments(machine, identity, nonce=None):
    """Pure bounded option builder; engineering JSON cannot choose an output path."""
    trace = machine.get('axisTrace')
    if trace is None: return []
    if (not isinstance(trace, dict) or set(trace) - {'enabled', 'capacity'}
            or type(trace.get('enabled')) is not bool):
        raise ValueError('Invalid axis trace settings')
    capacity = trace.get('capacity', 0)
    if type(capacity) is not int or not 0 <= capacity <= 120000:
        raise ValueError('Invalid axis trace capacity')
    if not trace['enabled']: return []
    if capacity == 0: raise ValueError('Enabled axis trace requires explicit capacity 1-120000')
    axes = machine.get('axes')
    if not isinstance(axes, list) or len(axes) != 1 or not isinstance(axes[0], dict):
        raise ValueError('Axis trace requires exactly one axis')
    required = machine.get('requiredRuntimeCapabilities')
    if (not isinstance(required, list) or any(type(value) is not str for value in required)
            or not {'workbench_motion_migration_v1', 'workbench_axis_trace_v1'}.issubset(required)):
        raise ValueError('Axis trace requires migration and trace runtime capabilities')
    identity = hex_identity(identity)
    nonce = uuid.uuid4().hex if nonce is None else nonce
    if type(nonce) is not str or re.fullmatch('[0-9a-f]{32}', nonce) is None:
        raise ValueError('Invalid axis trace nonce')
    output = str(TRACE_DIRECTORY / (identity + '-' + nonce + '.csv'))
    return ['--axis-trace-csv', output, '--axis-trace-capacity', str(capacity)]


def prepare_axis_trace(arguments, group):
    if not arguments: return
    if (len(arguments) != 4 or arguments[0] != '--axis-trace-csv' or arguments[2] != '--axis-trace-capacity'
            or re.fullmatch(r'/var/lib/st-workbench-ethercat/traces/[0-9a-f]{64}-[0-9a-f]{32}\.csv', arguments[1]) is None
            or not arguments[3].isascii() or not arguments[3].isdecimal() or not 1 <= int(arguments[3]) <= 120000):
        raise ValueError('Invalid root trace output arguments')
    directory = pathlib.Path(str(TRACE_DIRECTORY))
    # Check the parent before mkdir/chown: never follow user-controlled links.
    root_owned(directory.parent)
    directory.mkdir(mode=0o750, exist_ok=True)
    root_owned(directory)
    if not stat.S_ISDIR(directory.lstat().st_mode): raise ValueError('Trace directory must be a directory')
    os.chown(directory, 0, group)
    os.chmod(directory, 0o750)
    root_owned(directory)
    if os.path.lexists(arguments[1]): raise ValueError('Trace output already exists')


KNOWN_RUNTIME_CAPABILITIES = frozenset({'workbench_motion_migration_v1', 'workbench_axis_trace_v1',
    'workbench_axis_move_profile_v2', 'workbench_axis_velocity_profile_v2'})


def required_runtime_capabilities(machine, manifest=None):
    """Combine verified hardware requirements with the explicit program ABI proof."""
    def capabilities(value):
        if (not isinstance(value, list) or len(value) > 16
                or any(type(item) is not str or item not in KNOWN_RUNTIME_CAPABILITIES for item in value)):
            raise ValueError('Unsupported required native runtime capabilities')
        return set(value)
    required = capabilities(machine.get('requiredRuntimeCapabilities', []))
    if manifest is None: return sorted(required)
    if not isinstance(manifest, dict): raise ValueError('Invalid runtime capability manifest')
    program = capabilities(manifest.get('RequiredRuntimeCapabilities', []))
    version = manifest.get('AxisInterfaceVersion')
    if version not in (None, 'workbench-axis-v1', 'workbench-axis-v2'):
        raise ValueError('Unsupported axis interface version')
    dynamic = {'workbench_axis_move_profile_v2', 'workbench_axis_velocity_profile_v2'}
    if version == 'workbench-axis-v2':
        if (manifest.get('AxisExportMarker') != 'workbench_axis_v2'
                or type(manifest.get('AxisCommandBytes')) is not int or manifest['AxisCommandBytes'] != 4112
                or type(manifest.get('AxisFeedbackBytes')) is not int or manifest['AxisFeedbackBytes'] != 6656
                or 'workbench_axis_move_profile_v2' not in program):
            raise ValueError('Axis v2 requires its marker, image sizes and explicit move capability')
    elif program & dynamic:
        raise ValueError('Dynamic capabilities require explicit axis v2 metadata')
    return sorted(required | program)


def check_loaded_runtime_capabilities(hello, required):
    if not required: return
    actual = hello.get('runtimeCapabilities')
    if (not isinstance(actual, list) or len(actual) > 64
            or any(type(value) is not str for value in actual)
            or not set(required).issubset(actual)):
        raise ValueError('Native runtime lacks required motion capability')


def check_runtime_capabilities(binary, machine, manifest=None):
    required = required_runtime_capabilities(machine, manifest)
    if not required: return
    actual = parse_json(command([str(binary), '--runtime-capabilities']).stdout)
    check_loaded_runtime_capabilities(actual, required)


def verify_drive_windows(topology):
    for slave in topology.get('slaves', []):
        for sdo in slave.get('startupSdos', []):
            if sdo.get('index') != 0x6065 or sdo.get('subindex') != 0: continue
            data = sdo.get('data')
            if (not isinstance(data, list) or len(data) != 4
                    or any(type(value) is not int or not 0 <= value <= 255 for value in data)):
                raise ValueError('Drive following window must be a four-byte startup value')
            expected = int.from_bytes(bytes(data), 'little')
            position = slave.get('position')
            if type(position) is not int or not 0 <= position < 65536:
                raise ValueError('Invalid drive window station position')
            actual = command(['/opt/etherlab/bin/ethercat', 'upload', '-p', str(position),
                              '-t', 'uint32', '0x6065', '0']).stdout.strip().split()
            if not actual or int(actual[-1]) != expected:
                raise ValueError('Drive following window readback differs from engineering configuration')


def _apply_locked(build):
    root_owned(INSTALLATION, 0o600)
    installation = parse_json(read_regular(INSTALLATION))
    binary = pathlib.Path(installation['binary'])
    root_owned(binary)
    if not stat.S_ISREG(binary.stat().st_mode): raise ValueError('Pinned binary must be regular')
    checksum = hashlib.sha256()
    with binary.open('rb') as stream:
        for chunk in iter(lambda: stream.read(65536), b''): checksum.update(chunk)
    if checksum.hexdigest() != hex_identity(installation.get('binarySha256')):
        raise ValueError('Pinned native binary checksum mismatch')
    candidate = CANDIDATES / build
    verified = check_bundle(candidate, {})
    # Capture the only untrusted bytes the root process will consume, then revalidate those copies.
    captured = {name: read_regular(candidate / 'core' / 'generated' / name) for name in (*FILES, 'build-manifest.json')}
    machine = parse_json(captured['machine-config.json'])
    trace_arguments = axis_trace_arguments(machine, verified['topologyFingerprint'])
    manifest = parse_json(captured['build-manifest.json'])
    required = required_runtime_capabilities(machine, manifest)
    check_runtime_capabilities(binary, machine, manifest)
    make_root_directory(VERSIONS)
    import grp
    group = grp.getgrnam('a').gr_gid
    destination = VERSIONS / verified['topologyFingerprint']
    with tempfile.TemporaryDirectory(prefix='.apply-', dir=VERSIONS) as temporary:
        staged = pathlib.Path(temporary) / build / 'core' / 'generated'
        staged.mkdir(parents=True, mode=0o750)
        for name, data in captured.items():
            target = staged / name
            with target.open('xb') as stream:
                stream.write(data); stream.flush(); os.fsync(stream.fileno())
            os.chmod(target, 0o640); os.chown(target, 0, group)
        rechecked = check_bundle(staged.parent.parent, {})
        if rechecked != verified: raise ValueError('Candidate changed during capture')
        if destination.exists():
            root_owned(destination)
            for name, data in captured.items():
                if name != 'build-manifest.json' and read_regular(destination / name) != data:
                    raise ValueError('Existing root topology differs')
        else:
            os.chown(staged, 0, group); os.chmod(staged, 0o750)
            os.rename(staged, destination)
    paths = ['--ethercat-config', str(destination / FILES[0]), '--machine-config', str(destination / FILES[1]), '--io-map', str(destination / FILES[2])]
    command([str(binary), '--validate-ethercat-config', str(destination / FILES[0]), '--machine-config', str(destination / FILES[1]), '--io-map', str(destination / FILES[2]), *trace_arguments])
    prepare_axis_trace(trace_arguments, group)
    # Safety reads are deliberately repeated immediately before the only stop action.
    initial_start = preflight(allow_initial_start=installation.get('allowInitialStart') is True)
    try:
        if initial_start is not True:
            command(['systemctl', 'stop', SERVICE])
            command(['systemctl', 'reset-failed', SERVICE], check=False)
        wait_for_core_release()
        command(['systemd-run', '--unit=st-workbench-is620n', '--service-type=exec', '--uid=root', '--gid=a',
                 '--property=LimitMEMLOCK=infinity', '--property=LimitRTPRIO=95', '--property=NoNewPrivileges=yes',
                 '--property=UMask=0027', '--property=TimeoutStopSec=5', '--property=KillMode=control-group', '--property=RuntimeMaxSec=infinity',
                 '--property=CPUAffinity=2', '--property=After=st-workbench-cpu-isolation.service', '--property=RuntimeDirectory=st-workbench-is620n', '--property=RuntimeDirectoryMode=0750',
                 '--', str(binary), *paths, *trace_arguments, '--socket', CONTROLLER_SOCKET, '--cpu', '2'])
        deadline = time.monotonic() + 10
        while True:
            try:
                hello = native_request(CONTROLLER_SOCKET, 'HelloRequest', {'clientName':'hardware-apply','clientVersion':'1'}, 'HelloResponse')
                check_loaded_runtime_capabilities(hello, required)
                if hello.get('runtimeReady') is not True or hello.get('ethercatTopologyFingerprint') != verified['topologyFingerprint']: raise ValueError('Loaded topology proof mismatch')
                snapshot = native_request(CONTROLLER_SOCKET, 'ReadSnapshotRequest', {'includeDiagnostics':False}, 'SnapshotResponse')
                if snapshot.get('runtimeReady') is not True or snapshot.get('ioReady') is not True: raise ValueError('Native runtime not ready')
                disabled_snapshot(snapshot, require_zero=True)
                for key in ('sampleCycle', 'sampleMonotonicNs'):
                    if type(snapshot.get(key)) is not int or not 0 < snapshot[key] < 2**64: raise ValueError('Invalid native sample counter')
                time.sleep(.02)
                fresh = native_request(CONTROLLER_SOCKET, 'ReadSnapshotRequest', {'includeDiagnostics':False}, 'SnapshotResponse')
                disabled_snapshot(fresh, require_zero=True)
                for key in ('sampleCycle', 'sampleMonotonicNs'):
                    if type(fresh.get(key)) is not int or not snapshot[key] < fresh[key] < 2**64: raise ValueError('Native cycles are not advancing')
                verify_drive_windows(parse_json(captured['ethercat-runtime.json']))
                publish_active(verified, group)
                return dict(verified, status='applied', verified=True, rollback='not-required')
            except (OSError, ValueError):
                if time.monotonic() >= deadline: raise
                time.sleep(.1)
    except Exception as error:
        return failed_result(verified, error)


def publish_active(verified, group):
    root_owned(ACTIVE_RECORD.parent)
    record = {key: hex_identity(verified[key]) for key in ('buildHash','topologyFingerprint','machineSha256','ioMapSha256')}
    if 'runtimeRequirements' in verified: record['runtimeRequirements'] = verified['runtimeRequirements']
    fd, temporary = tempfile.mkstemp(prefix='.active-', dir=ACTIVE_RECORD.parent)
    try:
        with os.fdopen(fd, 'wb') as stream:
            os.chmod(temporary, 0o640)
            os.chown(temporary, 0, group)
            stream.write(json.dumps(record, sort_keys=True, separators=(',', ':')).encode('utf-8'))
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, ACTIVE_RECORD)
        if os.name == 'posix':
            directory = os.open(ACTIVE_RECORD.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
            try: os.fsync(directory)
            finally: os.close(directory)
    finally:
        if os.path.lexists(temporary): os.unlink(temporary)

def failed_result(verified, error):
    result = dict(verified, status='failed', nativeState='unknown', rollback='not-attempted-unknown-baseline', error=str(error))
    try:
        cleanup = command(['systemctl', 'stop', SERVICE], check=False)
        if cleanup.returncode == 0:
            result['nativeState'] = 'stopped'
        else:
            result['cleanupError'] = 'systemctl stop exit ' + str(cleanup.returncode)
    except Exception as cleanup_error:
        result['cleanupError'] = str(cleanup_error)
    return result

if __name__ == '__main__':
    try:
        if len(sys.argv) != 2: raise ValueError('Exactly one 64-hex build argument required')
        result = apply(sys.argv[1])
        print(json.dumps(result, separators=(',', ':')))
        sys.exit(0 if result.get('status') == 'applied' else 1)
    except Exception as error:
        print(json.dumps({'status':'rejected','error':str(error)}, separators=(',', ':')))
        sys.exit(1)
