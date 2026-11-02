"""Read-only validation for explicitly opted-in private basic-axis candidates."""
import hashlib
import json
import math
import os
import pathlib
import re
import shlex
import stat
import time

INTERFACE = 'workbench-axis-v1'
INTERFACE_V2 = 'workbench-axis-v2'
INTERFACES = {INTERFACE, INTERFACE_V2}
PROFILE_CAPABILITIES = {'workbench_axis_move_profile_v2','workbench_axis_velocity_profile_v2'}
FILES = {'axis_control.st', 'axis-constants.st', 'axis_bridge.hpp', 'axis_bridge.cpp',
         'generated_axis_scan_shim.cpp', 'axis_lifecycle.inc', 'axis-manifest.json',
         'axes/axis_worker.py', 'axes/LICENSE'}
AXIS_KEYS = {'Index', 'Name', 'ControllerAxis', 'Unit', 'MinPosition', 'MaxPosition',
             'MaximumVelocity', 'MaximumAcceleration', 'MaximumDeceleration', 'MaximumJerk'}

def channel(interface, config):
    if interface == INTERFACE:
        return dict(socket=config.get('axis_socket_path'),service='stworkbench-is620n-axis.service',environment='STWORKBENCH_AXIS_SOCKET')
    if interface != INTERFACE_V2: raise ValueError('Unsupported axis interface')
    endpoint='/home/a/.local/share/st-workbench/axis-v2/axis.sock'
    if config.get('axis_v2_socket_path') != endpoint or endpoint == config.get('axis_socket_path'):
        raise ValueError('Explicit separate v2 axis channel required')
    return dict(socket=endpoint,service='stworkbench-axis-v2.service',environment='STWORKBENCH_AXIS_V2_SOCKET')

def verify_native_profile_capabilities(data, hello):
    required=data.get('RequiredRuntimeCapabilities',[])
    actual=hello.get('runtimeCapabilities')
    if (hello.get('runtimeReady') is not True or not isinstance(actual,list)
            or any(type(c) is not str for c in actual) or not set(required).issubset(actual)):
        raise ValueError('Native axis dynamics capabilities are unavailable')


def strict_json(raw):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result: raise ValueError('Duplicate JSON field: ' + key)
            result[key] = value
        return result
    return json.loads(raw, object_pairs_hook=pairs,
        parse_constant=lambda value: (_ for _ in ()).throw(ValueError('Nonfinite JSON value')))


def validate_manifest(data):
    v2 = isinstance(data,dict) and data.get('InterfaceVersion') == INTERFACE_V2
    keys={'SchemaVersion', 'InterfaceVersion', 'PlcIntervalMilliseconds', 'Axes'}
    if v2: keys |= {'CommandBytes','FeedbackBytes','RequiredRuntimeCapabilities'}
    if not isinstance(data, dict) or set(data) != keys:
        raise ValueError('Invalid axis manifest schema')
    if type(data['SchemaVersion']) is not int or data['SchemaVersion'] != 1 or data['InterfaceVersion'] not in INTERFACES:
        raise ValueError('Unsupported axis manifest version')
    if v2:
        required=data['RequiredRuntimeCapabilities']
        if (type(data['CommandBytes']) is not int or data['CommandBytes']!=4112
                or type(data['FeedbackBytes']) is not int or data['FeedbackBytes']!=6656
                or not isinstance(required,list)
                or any(type(c) is not str or c not in PROFILE_CAPABILITIES for c in required)
                or len(set(required))!=len(required)
                or 'workbench_axis_move_profile_v2' not in required):
            raise ValueError('Invalid v2 image/capability contract')
    period = data['PlcIntervalMilliseconds']
    if type(period) is not int or not 1 <= period <= 1000:
        raise ValueError('Invalid axis PLC interval')
    axes = data['Axes']
    if not isinstance(axes, list) or not 1 <= len(axes) <= 64:
        raise ValueError('Axis count must be 1..64')
    names, controllers = set(), set()
    for index, axis in enumerate(axes, 1):
        if not isinstance(axis, dict) or set(axis) - {'Mechanics'} != AXIS_KEYS or type(axis['Index']) is not int or axis['Index'] != index:
            raise ValueError('Invalid axis schema or dense index')
        name = axis['Name']
        if not isinstance(name, str) or not 1 <= len(name) <= 256 or re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]*', name) is None or name.casefold() in names:
            raise ValueError('Invalid or duplicate logical axis name')
        for key in ('ControllerAxis', 'Unit'):
            value = axis[key]
            if not isinstance(value, str) or not 1 <= len(value) <= 256 or not value.strip() or any(ord(c) < 32 for c in value):
                raise ValueError('Invalid axis mapping/unit')
        if axis['ControllerAxis'].casefold() in controllers:
            raise ValueError('Duplicate controller axis mapping')
        names.add(name.casefold()); controllers.add(axis['ControllerAxis'].casefold())
        for key in ('MinPosition', 'MaxPosition', 'MaximumVelocity', 'MaximumAcceleration', 'MaximumDeceleration', 'MaximumJerk'):
            value = axis[key]
            if key.startswith('Maximum') and value is None and not v2: continue
            if type(value) not in (int, float) or not math.isfinite(value) or (key.startswith('Maximum') and (value < 0 if key=='MaximumJerk' else value <= 0)):
                raise ValueError('Invalid finite axis limit/cap')
        if axis['MinPosition'] >= axis['MaxPosition']:
            raise ValueError('Axis position bounds must increase')
    if axes != sorted(axes, key=lambda axis: axis['Name'].encode('utf-16-be')):
        raise ValueError('Axes must be sorted by ordinal logical name')
    return data


def validate_bundle(archive, manifest, names):
    if manifest.get('AxisInterfaceVersion') is None:
        if (FILES | {'axes/axis_worker_v2.py'}).intersection(names):
            raise ValueError('Axis dependencies require explicit interface identity')
        return
    interface=manifest['AxisInterfaceVersion']
    required_files=(FILES-{'axes/axis_worker.py'})|{'axes/axis_worker_v2.py'} if interface==INTERFACE_V2 else FILES
    if interface not in INTERFACES or not required_files.issubset(names):
        raise ValueError('Unapproved or incomplete axis dependency bundle')
    if interface==INTERFACE_V2 and (manifest.get('AxisExportMarker')!='workbench_axis_v2' or 'axes/axis_worker.py' in names):
        raise ValueError('V2 axis export/dependency identity differs')
    if archive.getinfo('axis-manifest.json').file_size > 65536:
        raise ValueError('Axis manifest capacity exceeded')
    data = validate_manifest(strict_json(archive.read('axis-manifest.json')))
    if data['InterfaceVersion']!=interface: raise ValueError('Axis package and image interface differ')
    expected = 'VAR_GLOBAL CONSTANT\n' + ''.join(
        '    AXIS_%s : AXIS_REF := %d;\n' % (axis['Name'], axis['Index']) for axis in data['Axes']) + 'END_VAR\n'
    if archive.read('axis-constants.st').replace(b'\r\n', b'\n') != expected.encode('utf8'):
        raise ValueError('Axis constants differ from manifest bindings')


def read_private_json(path):
    descriptor = os.open(path, os.O_RDONLY | getattr(os, 'O_NOFOLLOW', 0))
    with os.fdopen(descriptor, 'rb') as stream:
        info = os.fstat(stream.fileno())
        if (not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid()
                or stat.S_IMODE(info.st_mode) != 0o600 or not 0 < info.st_size <= 65536
                or not -1 <= time.time() - info.st_mtime <= 5):
            raise ValueError('Axis capability must be private, bounded and fresh')
        raw = stream.read(65537)
        if len(raw) > 65536: raise ValueError('Axis capability capacity exceeded')
        return strict_json(raw)


def verify_v2_worker_source(manifest, generated, endpoint):
    """Bind admission to the exact packaged/installed v2 worker before PLC teardown."""
    expected=manifest.get('Files',{}).get('axes/axis_worker_v2.py')
    if not isinstance(expected,str) or re.fullmatch('[0-9A-Fa-f]{64}',expected) is None:
        raise ValueError('Verified v2 worker inventory hash required')
    def checksum(path,private):
        path=pathlib.Path(path).absolute()
        for part in (path,*path.parents):
            if part.is_symlink():raise ValueError('Symlink in v2 worker source path')
        descriptor=os.open(path,os.O_RDONLY|getattr(os,'O_NOFOLLOW',0)|getattr(os,'O_NONBLOCK',0))
        with os.fdopen(descriptor,'rb') as stream:
            info=os.fstat(stream.fileno())
            if (not stat.S_ISREG(info.st_mode) or not 0<info.st_size<=1048576
                    or private and (info.st_uid!=os.getuid() or stat.S_IMODE(info.st_mode)!=0o600)):
                raise ValueError('V2 worker must be bounded/regular and installed private/owned 0600')
            raw=stream.read(1048577)
            if len(raw)>1048576:raise ValueError('V2 worker source exceeds capacity')
            return hashlib.sha256(raw).hexdigest()
    packaged=pathlib.Path(generated)/'axes/axis_worker_v2.py'
    installed=pathlib.Path(endpoint).with_name('axis_worker_v2.py')
    if checksum(packaged,False)!=expected.lower() or checksum(installed,True)!=expected.lower():
        raise ValueError('Installed v2 worker source differs from verified candidate')


def channel_failure_detail(capability):
    """Only fixed categories reach operator logs; never echo native payloads/tokens."""
    reasons={'transport_timeout':'原生通信超时','transport_io':'原生连接中断',
        'invalid_response':'原生回包无效或身份不匹配','sequence_exhausted':'通信会话编号耗尽',
        'plc_watchdog_expired':'PLC扫描心跳超时','control_ownership_lost':'控制租约丢失',
        'transport_or_protocol_failed':'通信或协议异常','native_command_failed':'原生指令结果未确认',
        'capability_or_receipt_invalid':'功能或动作回执校验失败','channel_failed':'轴控通道异常'}
    reason=capability.get('FailureReason')
    text=reasons.get(reason,'轴控通道未就绪') if isinstance(reason,str) else '轴控通道未就绪'
    code=capability.get('FailureCode')
    if type(code) is int and 2001<=code<=2009:text+='（通道故障码：'+str(code)+'）'
    if capability.get('RecoveryRequired') is True:
        text+='；请先停止PLC，再恢复轴控通道；恢复后需手动运行。'
    else:text+='；请查询核心及通道的实际状态。'
    return text


def verify_ready(manifest, generated, config, environment):
    generic_applied = False
    if manifest.get('EtherCatRuntimeInterfaceVersion') is not None:
        if manifest['EtherCatRuntimeInterfaceVersion'] != 'workbench-ethercat-v1':
            raise ValueError('Unsupported generic EtherCAT runtime interface')
        import ethercat_hardware
        ethercat_hardware.verify_applied(generated, config)
        generic_applied = True
    hardware_path = pathlib.Path(generated) / 'hardware-configuration.json'
    try:
        hardware_raw = hardware_path.read_bytes()
    except FileNotFoundError:
        hardware_raw = None
    if hardware_raw is not None:
        hardware = strict_json(hardware_raw)
        if not generic_applied and any(axis.get('Mechanics') is not None for axis in (hardware.get('Hardware') or {}).get('Axes', [])):
            raise ValueError('Axis mechanical/rotary/homing configuration is offline only; controller apply/confirm is not implemented')
    if manifest.get('AxisInterfaceVersion') is None: return
    # Offline staging/compilation preserves these properties for review. Activation
    # must refuse them before contacting a worker: the protocol cannot apply them.
    raw = (pathlib.Path(generated) / 'axis-manifest.json').read_bytes()
    data = validate_manifest(strict_json(raw))
    if any(axis.get('Mechanics') is not None for axis in data['Axes']):
        raise ValueError('Axis mechanical/rotary/homing configuration is offline only; controller apply/confirm is not implemented')
    backend = config.get('axis_backend')
    interface=manifest['AxisInterfaceVersion']
    if interface not in INTERFACES or data['InterfaceVersion']!=interface or backend not in ('EcMotion', 'EcMotionSimulator'):
        raise ValueError('Basic axis backend must be explicitly configured')
    if backend == 'EcMotion' and manifest.get('MotionInterfaceVersion') is not None:
        raise ValueError('A mixed physical EcMotion axis and VirtualContinuous candidate is unsupported')
    if interface==INTERFACE_V2 and backend=='EcMotion':
        import ethercat_hardware
        hello=ethercat_hardware.native_request(config.get('ethercat_controller_socket',ethercat_hardware.CONTROLLER_SOCKET),'HelloRequest',{'clientName':'axis-v2-admission','clientVersion':'2'},'HelloResponse')
        verify_native_profile_capabilities(data,hello)
    selected=channel(interface,config)
    endpoint = selected['socket']
    if not isinstance(endpoint, str) or not endpoint.startswith('/') or '\x00' in endpoint or len(endpoint.encode()) >= 104:
        raise ValueError('Private absolute axis socket path required')
    values = {}
    for token in shlex.split(environment):
        if '=' in token:
            key, value = token.split('=', 1)
            if key in values: raise ValueError('Duplicate Runtime environment variable')
            values[key] = value
    if values.get('STWORKBENCH_AXIS_BACKEND') != backend or values.get(selected['environment']) != endpoint:
        raise ValueError('Runtime axis environment differs from configured private worker')
    if interface==INTERFACE_V2:verify_v2_worker_source(manifest,generated,endpoint)
    info = pathlib.Path(endpoint).lstat()
    if not stat.S_ISSOCK(info.st_mode) or info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) != 0o600:
        raise ValueError('Axis socket must be private and owned by the current user')
    capability = read_private_json(endpoint + '.capabilities.json')
    if not isinstance(capability, dict): raise ValueError('Invalid axis capability schema')
    pid = capability.get('PID')
    if type(pid) is not int or pid <= 0: raise ValueError('Invalid axis worker PID')
    os.kill(pid, 0)
    if (pathlib.Path('/proc') / str(pid)).stat().st_uid != os.getuid():
        raise ValueError('Axis worker process owner differs')
    expected = dict(SchemaVersion=1, InterfaceVersion=interface, Backend=backend,
        ManifestSha256=hashlib.sha256(raw).hexdigest(), ControllerReady=True,
        PhysicalBasicAxis=backend == 'EcMotion', SimulatorBasicAxis=backend == 'EcMotionSimulator',
        Continuous=False, MotionCycleApplied=False)
    if interface==INTERFACE_V2:
        expected.update(CommandBytes=4112,FeedbackBytes=6656,RequiredRuntimeCapabilities=data['RequiredRuntimeCapabilities'])
        native=capability.get('NativeRuntimeCapabilities')
        if not isinstance(native,list) or any(type(c) is not str for c in native) or not set(data['RequiredRuntimeCapabilities']).issubset(native):
            raise ValueError('Worker native dynamics capabilities differ')
    for key, value in expected.items():
        if type(capability.get(key)) is not type(value) or capability[key] != value:
            raise ValueError('Axis worker capability or candidate manifest identity differs: ' + key + ('; '+channel_failure_detail(capability) if key=='ControllerReady' else ''))
