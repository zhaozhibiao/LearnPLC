"""MIT. Read-only configuration plans and fixed private axis-bridge updates."""
import hashlib
import json
import math
import os
import pathlib
import re
import shlex
import stat
import subprocess
import time
import axis_gate
import ethercat_hardware as hw

AXIS_SOCKET = '/home/a/.local/share/st-workbench/is620n-axis/axis.sock'
AXIS_SERVICE = 'stworkbench-is620n-axis.service'


def source_digest(path):
    path = pathlib.Path(path).absolute()
    for part in (path, *path.parents):
        if part.is_symlink(): raise ValueError('Symlink in candidate inventory path')
    descriptor = os.open(path, os.O_RDONLY | getattr(os,'O_NOFOLLOW',0) | getattr(os,'O_NONBLOCK',0))
    try:
        info = os.fstat(descriptor)
        if not stat.S_ISREG(info.st_mode) or info.st_size > 128 * 1024 * 1024:
            raise ValueError('Candidate inventory must contain bounded regular files')
        digest = hashlib.sha256()
        with os.fdopen(descriptor, 'rb', closefd=False) as stream:
            for chunk in iter(lambda: stream.read(65536), b''): digest.update(chunk)
        return digest.hexdigest()
    finally: os.close(descriptor)


def candidate(version):
    version = pathlib.Path(version)
    generated = version / 'core' / 'generated'
    stage = hw.parse_json(hw.read_regular(version / 'stage-result.json'))
    manifest = hw.parse_json(hw.read_regular(generated / 'build-manifest.json'))
    build = hw.hex_identity(version.name)
    if (stage.get('manifest') != manifest or hw.hex_identity(manifest.get('BuildHash')) != build
            or hw.hex_identity(stage.get('build_hash')) != build):
        raise ValueError('Candidate build/staged manifest identity mismatch')
    compiled = hw.parse_json(hw.read_regular(version / 'compile-result.json'))
    library = version / 'build' / 'new_libplc.so'
    if (compiled.get('state') != 'compiled' or compiled.get('exports_verified') is not True
            or hw.hex_identity(compiled.get('build_hash')) != build
            or hw.hex_identity(compiled.get('library_sha256')) != source_digest(library)):
        raise ValueError('Candidate has no verified successful compilation')
    inventory = manifest.get('Files')
    if not isinstance(inventory, dict): raise ValueError('Candidate inventory missing')
    for name, checksum in inventory.items():
        if (not isinstance(name, str) or '\\' in name or pathlib.PurePosixPath(name).is_absolute()
                or '..' in pathlib.PurePosixPath(name).parts or ':' in name):
            raise ValueError('Invalid inventory path')
        if hw.hex_identity(checksum) != source_digest(generated / name):
            raise ValueError('Candidate inventory changed: ' + name)
    if manifest.get('EtherCatRuntimeInterfaceVersion') != 'workbench-ethercat-v1':
        raise ValueError('Unified configuration requires the explicit generic EtherCAT interface')
    return generated, manifest, compiled


def runtime_environment():
    return subprocess.run(['systemctl', '--user', 'show', 'stworkbench-openplc.service',
        '--property=Environment', '--value'], capture_output=True, text=True, timeout=10, check=True).stdout


def validate_config(config, interface=axis_gate.INTERFACE):
    selected=axis_gate.channel(interface,config)
    if (interface==axis_gate.INTERFACE and config.get('axis_socket_path') != AXIS_SOCKET) or config.get('axis_backend') != 'EcMotion':
        raise ValueError('Unified configuration requires the configured private EcMotion axis bridge')
    values = {}
    for token in shlex.split(runtime_environment()):
        if '=' in token:
            key, value = token.split('=', 1)
            if key in values: raise ValueError('Duplicate Runtime environment variable')
            values[key] = value
    if values.get('STWORKBENCH_AXIS_BACKEND') != 'EcMotion' or values.get(selected['environment']) != selected['socket']:
        raise ValueError('Runtime axis environment differs from configured private bridge')


def worker_digest(config, interface=axis_gate.INTERFACE):
    validate_config(config,interface)
    endpoint=axis_gate.channel(interface,config)['socket']
    info = pathlib.Path(endpoint).lstat()
    if not stat.S_ISSOCK(info.st_mode) or info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) != 0o600:
        raise ValueError('Axis socket must be private and owned by the current user')
    capability = axis_gate.read_private_json(endpoint + '.capabilities.json')
    expected = dict(SchemaVersion=1, InterfaceVersion=interface, Backend='EcMotion', ControllerReady=True,
                    PhysicalBasicAxis=True, SimulatorBasicAxis=False, Continuous=False, MotionCycleApplied=False)
    if interface==axis_gate.INTERFACE_V2:
        expected.update(CommandBytes=4112,FeedbackBytes=6656)
        required=capability.get('RequiredRuntimeCapabilities')
        native=capability.get('NativeRuntimeCapabilities')
        if (not isinstance(required,list) or any(type(c) is not str or c not in axis_gate.PROFILE_CAPABILITIES for c in required)
                or 'workbench_axis_move_profile_v2' not in required or not isinstance(native,list)
                or any(type(c) is not str for c in native) or not set(required).issubset(native)):
            raise ValueError('V2 axis native profile capabilities differ')
    for key, value in expected.items():
        if type(capability.get(key)) is not type(value) or capability[key] != value:
            raise ValueError('Axis bridge capability is not ready: ' + key + ('; '+axis_gate.channel_failure_detail(capability) if key=='ControllerReady' else ''))
    pid = capability.get('PID')
    if type(pid) is not int or pid <= 0: raise ValueError('Invalid axis bridge PID')
    os.kill(pid, 0)
    if (pathlib.Path('/proc') / str(pid)).stat().st_uid != os.getuid():
        raise ValueError('Axis bridge process owner differs')
    return hw.hex_identity(capability.get('ManifestSha256'))


def native_state(config):
    endpoint = config.get('ethercat_controller_socket', hw.CONTROLLER_SOCKET)
    hello = hw.native_request(endpoint, 'HelloRequest', {'clientName':'stworkbench-configuration-plan','clientVersion':'1'}, 'HelloResponse')
    if hello.get('runtimeReady') is not True: raise ValueError('Native runtime is not ready; configuration state is unknown')
    hw.hex_identity(hello.get('ethercatTopologyFingerprint'))
    first = hw.native_request(endpoint, 'ReadSnapshotRequest', {'includeDiagnostics':False}, 'SnapshotResponse')
    hw.validate_sample(first)
    time.sleep(.02)
    second = hw.native_request(endpoint, 'ReadSnapshotRequest', {'includeDiagnostics':False}, 'SnapshotResponse')
    hw.validate_sample(second)
    if second['sampleCycle'] <= first['sampleCycle'] or second['sampleMonotonicNs'] <= first['sampleMonotonicNs']:
        raise ValueError('Native hardware feedback is not advancing')
    return hello


def verify_stationary(version, config):
    generated, _, _ = candidate(version)
    # Only this exact, currently applied fingerprint permits these thresholds.
    # A candidate with wider, unapplied limits can never authorize its own update.
    hw.verify_applied(generated, config)
    machine = hw.parse_json(hw.read_regular(generated / 'machine-config.json'))
    axes = machine.get('axes')
    if not isinstance(axes, list) or not 1 <= len(axes) <= 64:
        raise ValueError('Missing trusted native stationary policies')
    policies = {}
    for axis in axes:
        name, policy = axis.get('name'), axis.get('motionCompletion')
        if not isinstance(name, str) or not name or name in policies or not isinstance(policy, dict):
            raise ValueError('Missing trusted native stationary policies')
        for key in ('positionTolerance','velocityTolerance'):
            value = policy.get(key)
            if type(value) not in (float,int) or not math.isfinite(value) or value <= 0:
                raise ValueError('Invalid trusted native stationary policy')
        dwell = policy.get('dwellMilliseconds')
        if type(dwell) is not int or not 1 <= dwell <= 60000 or type(policy.get('positionStabilityCompletion')) is not bool:
            raise ValueError('Invalid trusted native stationary policy')
        policies[name] = policy
    endpoint = config.get('ethercat_controller_socket', hw.CONTROLLER_SOCKET)
    previous = None
    start = None
    spans = {}
    duration = max(policy['dwellMilliseconds'] for policy in policies.values()) * 1000000
    deadline = time.monotonic() + duration / 1e9 + 2
    while time.monotonic() < deadline:
        sample = hw.native_request(endpoint, 'ReadSnapshotRequest', {'includeDiagnostics':False}, 'SnapshotResponse')
        hw.validate_sample(sample)
        if (sample.get('outputsAtConfiguredSafeState') is not True or previous is not None and
                (sample['sampleCycle'] <= previous['sampleCycle'] or sample['sampleMonotonicNs'] <= previous['sampleMonotonicNs'])):
            raise ValueError('Native stationary feedback is unsafe or not advancing')
        samples = sample.get('axes')
        if not isinstance(samples, list) or len(samples) != len(policies):
            raise ValueError('Every native axis must be confirmed disabled and stationary')
        names = set()
        for axis in samples:
            name = axis.get('name')
            if name not in policies or name in names:
                raise ValueError('Every native axis must be confirmed disabled and stationary')
            names.add(name)
            policy = policies[name]
            position, velocity = axis.get('actualPosition'), axis.get('actualVelocity')
            if (axis.get('state') != 'Disabled' or axis.get('motionBusy') is not False
                    or 'fault' not in axis or axis['fault'] is not None
                    or any(type(value) not in (int,float) or not math.isfinite(value) for value in (position,velocity))):
                raise ValueError('All native axes must be disabled, stationary and fault free before configuration update')
            low, high = spans.get(name, (position,position))
            spans[name] = min(low,position), max(high,position)
            if (spans[name][1] - spans[name][0] > policy['positionTolerance']
                    or not policy['positionStabilityCompletion'] and abs(velocity) > policy['velocityTolerance']):
                raise ValueError('Native axis is not stationary under its confirmed completion policy')
        if start is None: start = sample['sampleMonotonicNs']
        if sample['sampleMonotonicNs'] - start >= duration: return
        previous = sample
        time.sleep(.02)
    raise ValueError('Native stationary feedback dwell is unconfirmed')


def verify_prepared_v2(version, config):
    """Admit a verified stopped first installation, never replace a failed live bridge."""
    generated,build,_=candidate(version)
    endpoint=pathlib.Path(axis_gate.channel(axis_gate.INTERFACE_V2,config)['socket'])
    if os.path.lexists(endpoint):raise ValueError('Prepared admission requires an absent v2 socket')
    directory=endpoint.parent
    for part in (directory,*directory.parents):
        if part.is_symlink():raise ValueError('Symlink in prepared v2 installation')
    info=directory.stat()
    if info.st_uid!=os.getuid() or stat.S_IMODE(info.st_mode)!=0o700 or not stat.S_ISDIR(info.st_mode):
        raise ValueError('Prepared v2 directory must be owned/private 0700')
    def read(name):
        descriptor=os.open(directory/name,os.O_RDONLY|getattr(os,'O_NOFOLLOW',0)|getattr(os,'O_NONBLOCK',0))
        with os.fdopen(descriptor,'rb') as stream:
            info=os.fstat(stream.fileno())
            if not stat.S_ISREG(info.st_mode) or info.st_uid!=os.getuid() or stat.S_IMODE(info.st_mode)!=0o600 or not 0<info.st_size<=1048576:
                raise ValueError('Prepared v2 files must be owned/private bounded regular files')
            raw=stream.read(1048577)
            if len(raw)>1048576:raise ValueError('Prepared v2 file exceeds capacity')
            return raw
    metadata=axis_gate.strict_json(read('prepared.json'))
    raw=read('axis-manifest.json');data=axis_gate.validate_manifest(axis_gate.strict_json(raw))
    worker=read('axis_worker_v2.py')
    if not isinstance(metadata,dict):raise ValueError('Invalid prepared v2 identity')
    current_hash=hw.hex_identity(build.get('BuildHash'))
    if current_hash!=hw.hex_identity(pathlib.Path(version).name):raise ValueError('Current v2 candidate build identity differs')
    provenance_hash=hw.hex_identity(metadata.get('BuildHash'))
    provenance_generated,provenance_build=generated,build
    if provenance_hash!=current_hash:
        # Reuse resources only through a second fully verified sibling candidate.
        # A stale/missing provenance record never becomes an unbound hash allowlist.
        provenance_version=pathlib.Path(version).parent/provenance_hash.upper()
        provenance_generated,provenance_build,_=candidate(provenance_version)
        if hw.hex_identity(provenance_build.get('BuildHash'))!=provenance_hash:
            raise ValueError('Prepared v2 provenance build identity differs')
    expected=dict(SchemaVersion=1,InterfaceVersion=axis_gate.INTERFACE_V2,CommandBytes=4112,FeedbackBytes=6656,
        RequiredRuntimeCapabilities=data['RequiredRuntimeCapabilities'],WorkerSha256=hashlib.sha256(worker).hexdigest(),
        ManifestSha256=hashlib.sha256(raw).hexdigest(),BuildHash=provenance_build.get('BuildHash'),started=False)
    if (build.get('AxisInterfaceVersion')!=axis_gate.INTERFACE_V2 or provenance_build.get('AxisInterfaceVersion')!=axis_gate.INTERFACE_V2
            or data['InterfaceVersion']!=axis_gate.INTERFACE_V2 or set(metadata)!=set(expected)
            or any(type(metadata.get(k)) is not type(v) or metadata[k]!=v for k,v in expected.items())):
        raise ValueError('Prepared v2 candidate identity/version/capabilities differ')
    # Installed resources remain bound to preparation provenance. A new task
    # period/logical mapping is applied later under the normal stopped/stationary
    # configuration transaction; read-only planning does not rewrite these files.
    for name,checksum in [('axis-manifest.json',expected['ManifestSha256']),('axes/axis_worker_v2.py',expected['WorkerSha256'])]:
        if (hw.hex_identity(provenance_build.get('Files',{}).get(name))!=checksum
                or source_digest(provenance_generated/name)!=checksum):
            raise ValueError('Prepared v2 worker/manifest differs from verified provenance')
    if (hw.hex_identity(build.get('Files',{}).get('axes/axis_worker_v2.py'))!=expected['WorkerSha256']
            or source_digest(generated/'axes/axis_worker_v2.py')!=expected['WorkerSha256']):
        raise ValueError('Current v2 worker differs from prepared verified worker')
    current_raw=hw.read_regular(generated/'axis-manifest.json')
    current_data=axis_gate.validate_manifest(axis_gate.strict_json(current_raw))
    if (current_data['InterfaceVersion']!=axis_gate.INTERFACE_V2
            or hw.hex_identity(build.get('Files',{}).get('axis-manifest.json'))!=hashlib.sha256(current_raw).hexdigest()):
        raise ValueError('Current v2 manifest identity/version differs')
    status=subprocess.run(['systemctl','--user','show','stworkbench-axis-v2.service','--property=LoadState','--property=ActiveState'],
        capture_output=True,text=True,timeout=10,check=False)
    values=dict(line.split('=',1) for line in status.stdout.splitlines() if '=' in line)
    missing=values.get('LoadState')=='not-found' and values.get('ActiveState') in (None,'inactive')
    inactive=status.returncode==0 and values.get('LoadState')=='loaded' and values.get('ActiveState')=='inactive'
    if not (missing or inactive):raise ValueError('Prepared v2 service is not explicitly inactive')
    return metadata


def plan(version, config):
    generated, manifest, _ = candidate(version)
    checked = hw.check_bundle(version, config)
    hello = native_state(config)
    axis_hash = None
    interface=axis_gate.INTERFACE
    if manifest.get('AxisInterfaceVersion') is not None:
        interface=manifest['AxisInterfaceVersion']
        if interface not in axis_gate.INTERFACES:
            raise ValueError('Unsupported axis interface')
        raw = hw.read_regular(generated / 'axis-manifest.json')
        parsed = axis_gate.validate_manifest(axis_gate.strict_json(raw))
        if any(axis.get('Mechanics') is not None for axis in parsed['Axes']):
            raise ValueError('Offline axis mechanics cannot be applied through the bridge')
        if interface==axis_gate.INTERFACE_V2:
            axis_gate.verify_native_profile_capabilities(parsed,hello)
            validate_config(config,interface)
        else:validate_config(config)
        axis_hash = hashlib.sha256(raw).hexdigest()
    loaded_fingerprint = hello['ethercatTopologyFingerprint'].lower()
    kind = 'hardware' if loaded_fingerprint != checked['topologyFingerprint'] else 'none'
    loaded_axis_hash = None
    if kind == 'none' and axis_hash is not None:
        try:
            loaded_axis_hash=worker_digest(config,interface) if interface==axis_gate.INTERFACE_V2 else worker_digest(config)
        except FileNotFoundError:
            # Only an absent v2 endpoint permits prepared admission. Missing/stale
            # sidecars on an existing endpoint remain errors, as do all v1 misses.
            if interface!=axis_gate.INTERFACE_V2 or os.path.lexists(axis_gate.channel(interface,config)['socket']):raise
            verify_prepared_v2(version,config)
    if kind == 'none' and axis_hash is not None and loaded_axis_hash != axis_hash: kind = 'axis'
    result = dict(build_hash=pathlib.Path(version).name.upper(), kind=kind,
                  topology_fingerprint=checked['topologyFingerprint'], axis_manifest_sha256=axis_hash,
                  machine_sha256=checked['machineSha256'], io_map_sha256=checked['ioMapSha256'],
                  loaded_topology_fingerprint=loaded_fingerprint, loaded_axis_manifest_sha256=loaded_axis_hash)
    result['identity'] = hashlib.sha256(json.dumps(result, sort_keys=True, separators=(',', ':'), allow_nan=False).encode()).hexdigest()
    return result


def manifest_destination(config, interface=axis_gate.INTERFACE):
    validate_config(config,interface)
    destination = pathlib.Path(axis_gate.channel(interface,config)['socket']).with_name('axis-manifest.json')
    for parent in (destination.parent, *destination.parent.parents):
        if parent.is_symlink(): raise ValueError('Symlink in private axis manifest path')
    info = destination.parent.stat()
    if info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) & 0o022:
        raise ValueError('Axis manifest directory must be private and owned by the current user')
    if destination.is_symlink(): raise ValueError('Private axis manifest may not be a symlink')
    return destination


def service(action, interface=axis_gate.INTERFACE):
    if action not in ('stop','start'): raise ValueError('Invalid private bridge service action')
    if interface not in axis_gate.INTERFACES:raise ValueError('Unsupported axis service version')
    subprocess.run(['systemctl','--user',action,AXIS_SERVICE if interface==axis_gate.INTERFACE else 'stworkbench-axis-v2.service'], check=True, capture_output=True, timeout=20)


def native_pid():
    value = subprocess.run(['systemctl','show','st-workbench-is620n.service','--property=MainPID','--value'],
        check=True, capture_output=True, text=True, timeout=10).stdout.strip()
    if not value.isdecimal() or int(value) <= 0: raise ValueError('Native core PID is not confirmed')
    return int(value)


def write_prepared_receipt(directory, metadata, request_id):
    """Atomic apply receipt; failure propagates before any worker restart."""
    destination=directory/'prepared.json'
    if destination.is_symlink():raise ValueError('Prepared v2 receipt may not be a symlink')
    if destination.exists():
        info=destination.stat()
        if not stat.S_ISREG(info.st_mode) or info.st_uid!=os.getuid() or stat.S_IMODE(info.st_mode)!=0o600:
            raise ValueError('Prepared v2 receipt must be private and owned')
    temporary=directory/('prepared-'+request_id+'.tmp');created=False
    try:
        descriptor=os.open(temporary,os.O_WRONLY|os.O_CREAT|os.O_EXCL|getattr(os,'O_NOFOLLOW',0),0o600);created=True
        with os.fdopen(descriptor,'wb') as stream:
            stream.write((json.dumps(metadata,indent=2)+'\n').encode());stream.flush();os.fsync(stream.fileno())
        os.replace(temporary,destination)
        if os.name=='posix':
            descriptor=os.open(directory,os.O_RDONLY|os.O_DIRECTORY)
            try:os.fsync(descriptor)
            finally:os.close(descriptor)
    finally:
        if created and temporary.exists():temporary.unlink()


def install_manifest(version, config, request_id):
    if not isinstance(request_id, str) or re.fullmatch('[A-Za-z0-9_-]{1,80}', request_id) is None:
        raise ValueError('Invalid configuration request identity')
    raw = hw.read_regular(pathlib.Path(version) / 'core' / 'generated' / 'axis-manifest.json')
    parsed=axis_gate.validate_manifest(axis_gate.strict_json(raw))
    destination = manifest_destination(config,parsed['InterfaceVersion']) if parsed['InterfaceVersion']==axis_gate.INTERFACE_V2 else manifest_destination(config)
    receipt=None
    if parsed['InterfaceVersion']==axis_gate.INTERFACE_V2:
        generated,build,_=candidate(version)
        if (build.get('AxisInterfaceVersion')!=axis_gate.INTERFACE_V2
                or hw.hex_identity(build.get('BuildHash'))!=hw.hex_identity(pathlib.Path(version).name)
                or hw.hex_identity(build.get('Files',{}).get('axis-manifest.json'))!=hashlib.sha256(raw).hexdigest()):
            raise ValueError('Applied v2 candidate manifest identity differs')
        axis_gate.verify_v2_worker_source(build,generated,str(destination.with_name('axis.sock')))
        receipt=dict(SchemaVersion=1,InterfaceVersion=axis_gate.INTERFACE_V2,CommandBytes=4112,FeedbackBytes=6656,
            RequiredRuntimeCapabilities=parsed['RequiredRuntimeCapabilities'],
            WorkerSha256=source_digest(destination.with_name('axis_worker_v2.py')),
            ManifestSha256=hashlib.sha256(raw).hexdigest(),BuildHash=build['BuildHash'],started=False)
    temporary = destination.with_name('axis-manifest-' + request_id + '.tmp')
    created = False
    try:
        descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os,'O_NOFOLLOW',0), 0o600)
        created = True
        with os.fdopen(descriptor, 'wb') as stream:
            stream.write(raw)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, destination)
        if os.name == 'posix':
            descriptor = os.open(destination.parent, os.O_RDONLY | os.O_DIRECTORY)
            try: os.fsync(descriptor)
            finally: os.close(descriptor)
    finally:
        if created and temporary.exists(): temporary.unlink()
    if receipt is not None:write_prepared_receipt(destination.parent,receipt,request_id)



def wait_ready(version, config):
    generated, manifest, _ = candidate(version)
    environment = runtime_environment() if manifest.get('AxisInterfaceVersion') is not None else ''
    deadline = time.monotonic() + 10
    error = None
    while time.monotonic() < deadline:
        try:
            axis_gate.verify_ready(manifest, generated, config, environment)
            return
        except (OSError, ValueError) as caught:
            error = caught
            time.sleep(.1)
    raise ValueError('Configuration feedback is unconfirmed; query actual state: ' + str(error))


def apply_axis(version, config, request_id):
    generated,manifest,_=candidate(version)
    if manifest.get('AxisInterfaceVersion')==axis_gate.INTERFACE_V2:
        data=axis_gate.validate_manifest(axis_gate.strict_json(hw.read_regular(generated/'axis-manifest.json')))
        axis_gate.verify_native_profile_capabilities(data,native_state(config))
        validate_config(config,axis_gate.INTERFACE_V2)
        if not os.path.lexists(axis_gate.channel(axis_gate.INTERFACE_V2,config)['socket']):verify_prepared_v2(version,config)
        manifest_destination(config,axis_gate.INTERFACE_V2)
        verify_stationary(version,config)
        pid=native_pid()
        service('stop',axis_gate.INTERFACE_V2)
        if native_pid()!=pid:raise ValueError('Native core identity changed during v2 axis update')
        verify_stationary(version,config)
        install_manifest(version,config,request_id)
        service('start',axis_gate.INTERFACE_V2)
        wait_ready(version,config)
        if native_pid()!=pid:raise ValueError('Native core identity changed during v2 axis update')
        return
    validate_config(config)
    manifest_destination(config)
    verify_stationary(version, config)
    pid = native_pid()
    service('stop')
    # Recheck after releasing the old PLC bridge; never reconnect a changed core.
    if native_pid() != pid: raise ValueError('Native core identity changed during axis update')
    verify_stationary(version, config)
    install_manifest(version, config, request_id)
    service('start')
    wait_ready(version, config)
    if native_pid() != pid: raise ValueError('Native core identity changed during axis update')
