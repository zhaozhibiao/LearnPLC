#!/usr/bin/python3 -I
# SPDX-License-Identifier: MIT
"""Restricted local hardware broker. No shell, caller paths or executable uploads."""
import hashlib
import importlib.machinery
import importlib.util
import json
import os
import pathlib
import re
import socket
import stat
import struct
import sys
import threading
import tempfile
import time

SOCKET = '/run/st-workbench-hardware/control.sock'
GROUP = 'stworkbench-hardware'
HELPER = pathlib.Path('/usr/local/libexec/stworkbench-ethercat-apply')
MAX_REQUEST = 4096
MAX_RESPONSE = 1024 * 1024
LEDGER = pathlib.Path('/var/lib/st-workbench-ethercat/broker-requests.json')


def strict_json(raw):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result: raise ValueError('Duplicate JSON key')
            result[key] = value
        return result
    value = json.loads(raw, object_pairs_hook=pairs,
                       parse_constant=lambda _: (_ for _ in ()).throw(ValueError('Invalid JSON number')))
    if not isinstance(value, dict): raise ValueError('Expected JSON object')
    return value


def validate_request(request):
    if not isinstance(request, dict): raise ValueError('Expected request object')
    action = request.get('action')
    fields = {'protocolVersion', 'requestId', 'action'}
    if action in {'validate', 'apply'}: fields.add('buildHash')
    elif action != 'status': raise ValueError('Unsupported broker action')
    if set(request) != fields: raise ValueError('Unexpected or missing broker fields')
    if type(request['protocolVersion']) is not int or request['protocolVersion'] != 1:
        raise ValueError('Unsupported broker protocol')
    if not isinstance(request['requestId'], str) or not re.fullmatch('[A-Za-z0-9_-]{1,80}', request['requestId']):
        raise ValueError('Invalid request identity')
    if action != 'status' and (not isinstance(request['buildHash'], str) or not re.fullmatch('[A-Fa-f0-9]{64}', request['buildHash'])):
        raise ValueError('Invalid build identity')
    return request


def authorized_uid(uid):
    import grp
    import pwd
    if uid == 0: return True
    try:
        user = pwd.getpwuid(uid)
        gid = grp.getgrnam(GROUP).gr_gid
        return gid in os.getgrouplist(user.pw_name, user.pw_gid)
    except KeyError:
        return False


def peer_identity(stream):
    # Linux SO_PEERCRED is authoritative; never trust a UID inside client JSON.
    _, uid, _ = struct.unpack('3i', stream.getsockopt(socket.SOL_SOCKET, getattr(socket, 'SO_PEERCRED', 17), 12))
    if not authorized_uid(uid): raise PermissionError('Caller is not authorized for hardware configuration')
    return uid


def exact(stream, size, deadline):
    result = bytearray()
    while len(result) < size:
        remaining = deadline - time.monotonic()
        if remaining <= 0: raise TimeoutError('Broker request deadline exceeded')
        stream.settimeout(remaining)
        chunk = stream.recv(size - len(result))
        if not chunk: raise ValueError('Truncated broker frame')
        result.extend(chunk)
    return bytes(result)


def read_request(stream):
    deadline = time.monotonic() + 3
    length, = struct.unpack('<I', exact(stream, 4, deadline))
    if not 0 < length <= MAX_REQUEST: raise ValueError('Broker request capacity exceeded')
    return validate_request(strict_json(exact(stream, length, deadline)))


def check_pinned_candidate_runtime(helper, candidate, proof):
    """Read-only admission, before the caller may stop either axis bridge."""
    generated = candidate / 'core' / 'generated'
    manifest = helper.parse_json(helper.read_regular(generated / 'build-manifest.json'))
    machine_raw = helper.read_regular(generated / 'machine-config.json')
    machine = helper.parse_json(machine_raw)
    if hasattr(helper, 'required_runtime_capabilities'):
        required = helper.required_runtime_capabilities(machine, manifest)
    else:
        if (manifest.get('AxisInterfaceVersion') == 'workbench-axis-v2'
                or manifest.get('RequiredRuntimeCapabilities')):
            raise ValueError('Broker helper cannot verify program runtime capabilities')
        required = machine.get('requiredRuntimeCapabilities', [])
    if isinstance(proof.get('runtimeRequirements'), dict):
        saved = proof['runtimeRequirements'].get('RequiredRuntimeCapabilities')
        if saved != required: raise ValueError('Candidate runtime requirements changed during validation')
    if not required: return
    if proof.get('machineSha256') is not None and hashlib.sha256(machine_raw).hexdigest() != proof['machineSha256']:
        raise ValueError('Candidate machine changed during validation')
    helper.root_owned(helper.INSTALLATION, 0o600)
    installation = helper.parse_json(helper.read_regular(helper.INSTALLATION))
    binary = pathlib.Path(installation['binary'])
    helper.root_owned(binary)
    if not stat.S_ISREG(binary.stat().st_mode) or binary.stat().st_size > 128 * 1024 * 1024:
        raise ValueError('Invalid pinned native binary')
    checksum = hashlib.sha256()
    with binary.open('rb') as stream:
        for chunk in iter(lambda: stream.read(65536), b''): checksum.update(chunk)
    if checksum.hexdigest() != helper.hex_identity(installation.get('binarySha256')):
        raise ValueError('Pinned native binary checksum mismatch')
    if hasattr(helper, 'required_runtime_capabilities'):
        helper.check_runtime_capabilities(binary, machine, manifest)
    else:
        helper.check_runtime_capabilities(binary, machine)


class Broker:
    def __init__(self, helper, ledger=None):
        self.helper = helper
        self.consumed = set()
        self.lock = threading.Lock()
        self.ledger = ledger
        if ledger is not None:
            helper.root_owned(ledger.parent)
            if os.path.lexists(ledger):
                helper.root_owned(ledger, 0o600)
                if ledger.stat().st_size > MAX_RESPONSE: raise ValueError('Persistent apply ledger exceeds capacity')
                data = strict_json(ledger.read_bytes())
                entries = data.get('entries')
                if set(data) != {'entries'} or not isinstance(entries, list) or len(entries) > 4096:
                    raise ValueError('Invalid persistent apply ledger')
                for entry in entries:
                    if (not isinstance(entry, list) or len(entry) != 2 or type(entry[0]) is not int or entry[0] < 0
                        or not isinstance(entry[1], str) or not re.fullmatch('[A-Za-z0-9_-]{1,80}', entry[1])):
                        raise ValueError('Invalid persistent apply identity')
                    self.consumed.add(tuple(entry))

    def persist_consumed(self):
        if self.ledger is None: return
        self.helper.root_owned(self.ledger.parent)
        fd, temporary = tempfile.mkstemp(prefix='.broker-requests-', dir=self.ledger.parent)
        try:
            with os.fdopen(fd, 'wb') as stream:
                stream.write(json.dumps({'entries':sorted(self.consumed)}, separators=(',', ':')).encode())
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, self.ledger)
            if os.name == 'posix':
                directory = os.open(self.ledger.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
                try: os.fsync(directory)
                finally: os.close(directory)
        finally:
            if os.path.lexists(temporary): os.unlink(temporary)

    def dispatch(self, request, uid):
        request = validate_request(request)
        action = request['action']
        if action == 'apply':
            with self.lock:
                key = (uid, request['requestId'])
                if key in self.consumed: raise ValueError('Apply request already consumed; query actual status')
                if len(self.consumed) >= 4096: raise ValueError('Apply request capacity reached; maintenance required')
                self.consumed.add(key)
                self.persist_consumed()  # Consume durably BEFORE any possible hardware action.
            result = self.helper.apply(request['buildHash'])
            success = result.get('status') == 'applied' and result.get('verified') is True
        elif action == 'validate':
            candidate = self.helper.CANDIDATES / request['buildHash']
            result = self.helper.check_bundle(candidate, {})
            check_pinned_candidate_runtime(self.helper, candidate, result)
            success = True
        else:
            result = {'plcState': self.helper.plc_status(), 'nativeReady': False}
            try:
                hello = self.helper.native_request(self.helper.CONTROLLER_SOCKET, 'HelloRequest',
                    {'clientName':'hardware-broker-status', 'clientVersion':'1'}, 'HelloResponse')
                snapshot = self.helper.native_request(self.helper.CONTROLLER_SOCKET, 'ReadSnapshotRequest',
                    {'includeDiagnostics':False}, 'SnapshotResponse')
                result.update(nativeReady=hello.get('runtimeReady') is True and snapshot.get('runtimeReady') is True,
                    topologyFingerprint=hello.get('ethercatTopologyFingerprint'), snapshot=snapshot)
            except (OSError, ValueError):
                result['nativeState'] = 'unavailable-or-unknown'
            success = True
        return {'protocolVersion':1, 'requestId':request['requestId'], 'success':success, 'result':result}


def handle_connection(stream, broker):
    request = {}
    uid = None
    try:
        uid = peer_identity(stream)
        stream.settimeout(3)
        request = read_request(stream)
        response = broker.dispatch(request, uid)
    except Exception as error:
        # No caller paths/commands/passwords are accepted; bounded diagnostics only.
        response = {'protocolVersion':1, 'requestId':request.get('requestId'), 'success':False,
                    'message':str(error)[:512]}
    try:
        data = json.dumps(response, separators=(',', ':'), allow_nan=False).encode('utf-8')
        if len(data) > MAX_RESPONSE: raise ValueError('Broker response capacity exceeded')
        stream.settimeout(3)
        stream.sendall(struct.pack('<I', len(data)) + data)
    except (OSError, ValueError):
        pass  # Operation outcome remains queryable; never replay after a disconnect.
    print(json.dumps({'event':'hardware-broker', 'uid':uid, 'action':request.get('action'),
                      'requestId':request.get('requestId'), 'success':response['success']}), flush=True)


def load_helper():
    for path in (HELPER, *HELPER.parents):
        info = path.lstat()
        if stat.S_ISLNK(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022:
            raise ValueError('Broker helper must be exclusively root controlled')
    loader = importlib.machinery.SourceFileLoader('root_hardware_apply', str(HELPER))
    # Fixed root-owned code only; Python runs isolated and never imports a staged module.
    spec = importlib.util.spec_from_loader(loader.name, loader)
    module = importlib.util.module_from_spec(spec)
    loader.exec_module(module)
    return module


def main():
    if len(sys.argv) != 1 or os.geteuid() != 0: raise ValueError('Broker requires its root system service')
    if os.environ.get('LISTEN_PID') != str(os.getpid()) or os.environ.get('LISTEN_FDS') != '1':
        raise ValueError('Broker requires exactly one systemd activation socket')
    server = socket.socket(fileno=3)
    if server.family != socket.AF_UNIX or server.getsockname() != SOCKET or not server.getsockopt(socket.SOL_SOCKET, socket.SO_ACCEPTCONN):
        raise ValueError('Unexpected broker activation socket')
    os.environ.pop('LISTEN_PID', None)
    os.environ.pop('LISTEN_FDS', None)
    broker = Broker(load_helper(), ledger=LEDGER)
    slots = threading.BoundedSemaphore(4)
    def serve(stream):
        try:
            with stream: handle_connection(stream, broker)
        finally: slots.release()
    while True:
        stream, _ = server.accept()
        if not slots.acquire(blocking=False):
            stream.close()
            continue
        threading.Thread(target=serve, args=(stream,), daemon=False).start()


if __name__ == '__main__': main()
