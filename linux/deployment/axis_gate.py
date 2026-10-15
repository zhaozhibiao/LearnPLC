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
FILES = {'axis_control.st', 'axis-constants.st', 'axis_bridge.hpp', 'axis_bridge.cpp',
         'generated_axis_scan_shim.cpp', 'axis_lifecycle.inc', 'axis-manifest.json',
         'axes/axis_worker.py', 'axes/LICENSE'}
AXIS_KEYS = {'Index', 'Name', 'ControllerAxis', 'Unit', 'MinPosition', 'MaxPosition',
             'MaximumVelocity', 'MaximumAcceleration', 'MaximumDeceleration', 'MaximumJerk'}


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
    if not isinstance(data, dict) or set(data) != {'SchemaVersion', 'InterfaceVersion', 'PlcIntervalMilliseconds', 'Axes'}:
        raise ValueError('Invalid axis manifest schema')
    if type(data['SchemaVersion']) is not int or data['SchemaVersion'] != 1 or data['InterfaceVersion'] != INTERFACE:
        raise ValueError('Unsupported axis manifest version')
    period = data['PlcIntervalMilliseconds']
    if type(period) is not int or not 1 <= period <= 1000:
        raise ValueError('Invalid axis PLC interval')
    axes = data['Axes']
    if not isinstance(axes, list) or not 1 <= len(axes) <= 64:
        raise ValueError('Axis count must be 1..64')
    names, controllers = set(), set()
    for index, axis in enumerate(axes, 1):
        if not isinstance(axis, dict) or set(axis) != AXIS_KEYS or type(axis['Index']) is not int or axis['Index'] != index:
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
            if key.startswith('Maximum') and value is None: continue
            if type(value) not in (int, float) or not math.isfinite(value) or (key.startswith('Maximum') and value <= 0):
                raise ValueError('Invalid finite axis limit/cap')
        if axis['MinPosition'] >= axis['MaxPosition']:
            raise ValueError('Axis position bounds must increase')
    if axes != sorted(axes, key=lambda axis: axis['Name'].encode('utf-16-be')):
        raise ValueError('Axes must be sorted by ordinal logical name')
    return data


def validate_bundle(archive, manifest, names):
    if manifest.get('AxisInterfaceVersion') is None:
        if FILES.intersection(names):
            raise ValueError('Axis dependencies require explicit interface identity')
        return
    if manifest['AxisInterfaceVersion'] != INTERFACE or not FILES.issubset(names):
        raise ValueError('Unapproved or incomplete axis dependency bundle')
    if archive.getinfo('axis-manifest.json').file_size > 65536:
        raise ValueError('Axis manifest capacity exceeded')
    data = validate_manifest(strict_json(archive.read('axis-manifest.json')))
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


def verify_ready(manifest, generated, config, environment):
    if manifest.get('AxisInterfaceVersion') is None: return
    backend = config.get('axis_backend')
    if manifest['AxisInterfaceVersion'] != INTERFACE or backend not in ('EcMotion', 'EcMotionSimulator'):
        raise ValueError('Basic axis backend must be explicitly configured')
    if backend == 'EcMotion' and manifest.get('MotionInterfaceVersion') is not None:
        raise ValueError('A mixed physical EcMotion axis and VirtualContinuous candidate is unsupported')
    endpoint = config.get('axis_socket_path')
    if not isinstance(endpoint, str) or not endpoint.startswith('/') or '\x00' in endpoint or len(endpoint.encode()) >= 104:
        raise ValueError('Private absolute axis socket path required')
    values = {}
    for token in shlex.split(environment):
        if '=' in token:
            key, value = token.split('=', 1)
            if key in values: raise ValueError('Duplicate Runtime environment variable')
            values[key] = value
    if values.get('STWORKBENCH_AXIS_BACKEND') != backend or values.get('STWORKBENCH_AXIS_SOCKET') != endpoint:
        raise ValueError('Runtime axis environment differs from configured private worker')
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
    raw = (pathlib.Path(generated) / 'axis-manifest.json').read_bytes()
    validate_manifest(strict_json(raw))
    expected = dict(SchemaVersion=1, InterfaceVersion=INTERFACE, Backend=backend,
        ManifestSha256=hashlib.sha256(raw).hexdigest(), ControllerReady=True,
        PhysicalBasicAxis=backend == 'EcMotion', SimulatorBasicAxis=backend == 'EcMotionSimulator',
        Continuous=False, MotionCycleApplied=False)
    for key, value in expected.items():
        if type(capability.get(key)) is not type(value) or capability[key] != value:
            raise ValueError('Axis worker capability or candidate manifest identity differs: ' + key)
