#!/usr/bin/env python3
"""Private fixed-image axis bridge to the existing EcMotion v4 controller."""
import argparse
import dataclasses
import datetime
import hashlib
import json
import math
import os
import re
import selectors
import socket
import stat
import struct
import sys
import time
import uuid

HEADER = struct.Struct('<QQ')
COMMAND = struct.Struct('<QQdddddQ')
FEEDBACK = struct.Struct('<9Q2d2Q')
COMMAND_BYTES = 4112
FEEDBACK_BYTES = 6656
INTERFACE = 'workbench-axis-v2'
HANDSHAKE = struct.Struct('<8sIIII')
HELLO = HANDSHAKE.pack(b'AXISV2\0\0', 2, COMMAND_BYTES, FEEDBACK_BYTES, 0)
MOVE_CAPABILITY = 'workbench_axis_move_profile_v2'
VELOCITY_CAPABILITY = 'workbench_axis_velocity_profile_v2'
KINDS = {1: 'Enable', 2: 'Disable', 3: 'Reset', 4: 'Home', 5: 'MoveAbsolute', 6: 'Jog', 7: 'Stop'}
STATES = {'Disabled': 1, 'Standstill': 2, 'Homing': 3, 'DiscreteMotion': 4,
          'ContinuousMotion': 5, 'Stopping': 6, 'ErrorStop': 7}


class ProtocolError(Exception):
    pass


class BackendError(Exception):
    pass


def read_exact(stream, count):
    result = bytearray()
    while len(result) < count:
        chunk = stream.recv(count - len(result))
        if not chunk:
            raise ProtocolError('Controller closed or truncated a frame')
        result.extend(chunk)
    return bytes(result)


class ProtocolClient:
    """One native session. Ambiguous wire failures permanently revoke its transport."""
    REQUEST_KINDS = frozenset({'HelloRequest','ReadSnapshotRequest','AcquireControlRequest',
        'ReleaseControlRequest','HeartbeatRequest','AxisCommandRequest','PlcScanBatchRequest'})

    def __init__(self, stream, now=time.perf_counter):
        self.stream, self.now = stream, now
        self.sequence = 0
        self.failure = None
        self.timings = {}
        self.original_timeout=getattr(stream,'gettimeout',lambda: .2)()
        self.io_timeout=self.original_timeout if self.original_timeout and self.original_timeout>0 else .2

    def diagnostics(self):
        return dict(State='Disconnected' if self.failure else 'Connected',
            FailureReason=self.failure, Requests={kind:dict(Count=s[0],Failures=s[1],
                LastMilliseconds=s[2],MaxMilliseconds=s[3],MeanMilliseconds=s[4]/s[0])
                for kind,s in self.timings.items()})

    def _abort(self, reason):
        if self.failure is not None:return
        self.failure = reason
        try:self.stream.shutdown(socket.SHUT_RDWR)
        except (OSError,AttributeError):pass
        try:self.stream.close()
        except OSError:pass

    def request(self, kind, payload, expected='CommandResponse'):
        if self.failure is not None:raise ProtocolError('Native channel requires explicit re-establishment')
        sequence = self.sequence + 1
        if sequence >= 2**64:
            self._abort('sequence_exhausted')
            raise ProtocolError('Protocol sequence exhausted')
        request_id = str(uuid.uuid4())
        envelope = dict(protocolVersion=4,kind=kind,requestId=request_id,sequence=sequence,payload=payload)
        # Invalid local data has never reached the wire and does not poison a session.
        data = json.dumps(envelope,allow_nan=False,separators=(',', ':')).encode('utf-8')
        if not 0 < len(data) <= 1048576:raise ProtocolError('Invalid outgoing frame length')
        self.sequence = sequence
        started, failed = self.now(), True
        deadline=time.monotonic()+self.io_timeout
        def remaining():
            wait=deadline-time.monotonic()
            if wait<=0:raise TimeoutError('Native whole-frame deadline expired')
            setter=getattr(self.stream,'settimeout',None)
            if setter:setter(wait)
        def receive(count):
            buffer=bytearray()
            while len(buffer)<count:
                remaining()
                chunk=self.stream.recv(count-len(buffer))
                if not chunk:raise ProtocolError('Controller closed or truncated a frame')
                buffer.extend(chunk)
            return bytes(buffer)
        def pairs(items):
            result={}
            for key,value in items:
                if key in result:raise ValueError('Duplicate native response field')
                result[key]=value
            return result
        def integer(value):
            if len(value.lstrip('-'))>20:raise ValueError('Native integer exceeds wire capacity')
            return int(value)
        def real(value):
            result=float(value)
            if not math.isfinite(result):raise ValueError('Nonfinite native response')
            return result
        try:
            remaining()
            self.stream.sendall(struct.pack('<I',len(data))+data)
            count, = struct.unpack('<I',receive(4))
            if not 0 < count <= 1048576:raise ProtocolError('Invalid incoming frame length')
            response=json.loads(receive(count),object_pairs_hook=pairs,parse_int=integer,parse_float=real,
                parse_constant=lambda value: (_ for _ in ()).throw(ValueError('Nonfinite native response')))
            remaining()
            if (not isinstance(response,dict) or type(response.get('protocolVersion')) is not int
                    or response['protocolVersion']!=4 or response.get('requestId')!=request_id
                    or type(response.get('sequence')) is not int or response['sequence']!=sequence
                    or not isinstance(response.get('payload'),dict)):
                raise ProtocolError('Native response identity/version/schema mismatch')
            if response.get('kind')=='ErrorResponse':
                code=response['payload'].get('code')
                if not isinstance(code,str) or not 0<len(code)<=128:raise ProtocolError('Invalid native rejection')
                raise BackendError(code)
            if response.get('kind')!=expected:raise ProtocolError('Unexpected native response kind')
            if expected=='CommandResponse' and type(response['payload'].get('accepted')) is not bool:
                raise ProtocolError('Ambiguous native command receipt')
            failed=False
            return response['payload']
        except BackendError:
            # A correlated, fully read rejection is definitive, not a lost transport.
            raise
        except (OSError,ProtocolError,KeyError,TypeError,ValueError,UnicodeError,RecursionError,OverflowError,struct.error) as error:
            self._abort('transport_timeout' if isinstance(error,TimeoutError) else
                'transport_io' if isinstance(error,OSError) else 'invalid_response')
            raise ProtocolError('Native channel failed: '+self.failure) from error
        finally:
            if self.failure is None:
                setter=getattr(self.stream,'settimeout',None)
                if setter:
                    try:setter(self.original_timeout)
                    except OSError:self._abort('transport_io')
            elapsed=max(0.,(self.now()-started)*1000.)
            label=kind if kind in self.REQUEST_KINDS else 'Other'
            sample=self.timings.setdefault(label,[0,0,0.,0.,0.])
            sample[0]+=1;sample[1]+=int(failed);sample[2]=elapsed;sample[3]=max(sample[3],elapsed);sample[4]+=elapsed


@dataclasses.dataclass(frozen=True)
class Manifest:
    document: dict
    axes: tuple
    digest: str
    period: float

    @classmethod
    def from_object(cls, document, digest=None):
        if type(document.get('SchemaVersion')) is not int or document['SchemaVersion'] != 1 or document.get('InterfaceVersion') != INTERFACE:
            raise ValueError('Unsupported axis manifest version')
        if document.get('CommandBytes') != COMMAND_BYTES or document.get('FeedbackBytes') != FEEDBACK_BYTES:
            raise ValueError('Axis image size mismatch')
        required = document.get('RequiredRuntimeCapabilities')
        if not isinstance(required,list) or MOVE_CAPABILITY not in required or any(c not in (MOVE_CAPABILITY,VELOCITY_CAPABILITY) for c in required):
            raise ValueError('Explicit v2 native requirements required')
        period = document.get('PlcIntervalMilliseconds')
        if type(period) is not int or not 1 <= period <= 1000:
            raise ValueError('Axis PLC interval must be positive and <=1000 milliseconds')
        axes = document.get('Axes')
        if not isinstance(axes, list) or not 1 <= len(axes) <= 64:
            raise ValueError('Axis count must be 1..64')
        indexes, names, controllers = set(), set(), set()
        for axis in axes:
            if axis.get('Mechanics') is not None:
                raise ValueError('Axis mechanical/rotary/homing configuration is offline only; controller apply/confirm is not implemented')
            index = axis.get('Index')
            if type(index) is not int or not 1 <= index <= 64 or index in indexes:
                raise ValueError('Invalid or duplicate axis index')
            name, controller = axis.get('Name'), axis.get('ControllerAxis')
            if (not isinstance(name, str) or not 1 <= len(name) <= 256
                    or re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]*', name) is None
                    or name.casefold() in names or not isinstance(controller, str)
                    or not 1 <= len(controller) <= 256 or not controller.strip()
                    or controller.casefold() in controllers):
                raise ValueError('Invalid or duplicate axis name/controller mapping')
            if not isinstance(axis.get('Unit'), str) or not 1 <= len(axis['Unit']) <= 256 or not axis['Unit'].strip():
                raise ValueError('Axis unit required')
            for key in ('MinPosition', 'MaxPosition'):
                if isinstance(axis.get(key), bool) or not isinstance(axis.get(key), (int, float)) or not math.isfinite(axis[key]):
                    raise ValueError('Finite axis bounds required')
            if axis['MinPosition'] >= axis['MaxPosition']:
                raise ValueError('Axis bounds must increase')
            for key in ('MaximumVelocity', 'MaximumAcceleration', 'MaximumDeceleration', 'MaximumJerk'):
                if axis.get(key) is not None and (isinstance(axis[key], bool) or not isinstance(axis[key], (int, float)) or not math.isfinite(axis[key]) or axis[key] < 0 or (axis[key] == 0 and key != 'MaximumJerk')):
                    raise ValueError('Axis dynamic cap must be finite and positive')
            for key in ('MaximumAcceleration', 'MaximumDeceleration', 'MaximumJerk'):
                value = axis.get(key)
                if isinstance(value, bool) or not isinstance(value, (int,float)) or not math.isfinite(value) or value < 0 or (key != 'MaximumJerk' and value == 0):
                    raise ValueError('Explicit finite axis dynamics required')
            indexes.add(index); names.add(name.casefold()); controllers.add(controller.casefold())
        # Index assignment is deterministic and dense, sorted using .NET Ordinal semantics.
        ordered = sorted(axes, key=lambda a: a['Name'].encode('utf-16-be'))
        if axes != ordered or [a['Index'] for a in axes] != list(range(1, len(axes)+1)):
            raise ValueError('Axes must have dense indices sorted by ordinal logical name')
        digest = digest or hashlib.sha256(json.dumps(document, sort_keys=True, allow_nan=False).encode()).hexdigest()
        return cls(document, tuple(axes), digest, period / 1000)

    @classmethod
    def load(cls, path):
        with open(path, 'rb') as stream:
            raw = stream.read()
        return cls.from_object(json.loads(raw), hashlib.sha256(raw).hexdigest())


@dataclasses.dataclass
class Slot:
    request: int = 0
    last_seen_request: int = 0
    rejected_request: int = 0
    rejected_error: int = 0
    accepted: bool = False
    kind: int = 0
    busy: bool = False
    done: bool = False
    error: int = 0
    aborted: bool = False
    motion_id: int = 0
    applied_cycle: int = 0
    stop_held: bool = False
    reset_deadline: float = 0.
    sample: dict = dataclasses.field(default_factory=dict)


class Worker:
    def __init__(self, manifest, backend, mode, now=time.monotonic):
        if mode not in ('EcMotion', 'EcMotionSimulator'):
            raise ValueError('Explicit backend required')
        self.manifest, self.backend, self.mode, self.now = manifest, backend, mode, now
        self.slots = [Slot() for _ in range(64)]
        self.mapping = {a['Index']-1: a for a in manifest.axes}
        self.hello = {}
        self.ready = False
        self.motion_ready = False
        self.lease = None
        self.owned = set()
        self.failed = 0
        self.failure_reason = None
        self.scan_index = 0
        self.last_scan = None
        self.cycle = 0
        self.last_sample_change = now()
        self.watchdog = max(.25, manifest.period * 3)
        self.freshness = max(.5, manifest.period * 3)
        self.session = uuid.uuid4().int & ((1 << 64)-1) or 1
        self.batch = 0
        self.action = 0
        self.submissions = 0

    def probe(self):
        self.hello = self.backend.request('HelloRequest', dict(clientName='Workbench axis worker', clientVersion=INTERFACE), 'HelloResponse')
        if self.hello.get('protocolVersion') != 4:
            raise ProtocolError('Hello protocolVersion mismatch')
        if not all(c in self.hello.get('runtimeCapabilities', []) for c in self.manifest.document['RequiredRuntimeCapabilities']):
            raise ProtocolError('Native move profile v2 capability required')
        self.poll()
        return self.capabilities()

    def capabilities(self):
        native = self.mode == 'EcMotion' and self.hello.get('plcScanBatch') is True and self.hello.get('plcActionReceipts') is True and self.hello.get('plcAbiVersion') == 3
        return dict(SchemaVersion=1, InterfaceVersion=INTERFACE, CommandBytes=COMMAND_BYTES, FeedbackBytes=FEEDBACK_BYTES, RequiredRuntimeCapabilities=self.manifest.document['RequiredRuntimeCapabilities'], NativeRuntimeCapabilities=self.hello.get('runtimeCapabilities',[]), ManifestSha256=self.manifest.digest,
                    Backend=self.mode, PID=os.getpid(), ControllerReady=self.ready and not self.failed,
                    PhysicalBasicAxis=bool(native and self.ready and not self.failed),
                    SimulatorBasicAxis=self.mode == 'EcMotionSimulator' and self.ready and not self.failed,
                    MotionReady=self.motion_ready and self.ready and not self.failed,
                    FailureCode=self.failed,FailureReason=self.failure_reason,
                    RecoveryRequired=bool(self.failed),
                    RecoveryAction='stop_plc_and_reestablish_channel' if self.failed else 'none',
                    LinkMetrics=getattr(self.backend,'diagnostics',lambda: {})(),
                    Continuous=False, MotionCycleApplied=False)

    def poll(self):
        try:
            snapshot = self.backend.request('ReadSnapshotRequest', dict(includeDiagnostics=False), 'SnapshotResponse')
            timestamp = datetime.datetime.fromisoformat(snapshot['timestamp'].replace('Z', '+00:00'))
            age = (datetime.datetime.now(datetime.timezone.utc) - timestamp).total_seconds()
            if not -.5 <= age <= self.freshness:
                raise ProtocolError('Stale or future snapshot timestamp')
            cycle = snapshot.get('sampleCycle', 0)
            if type(cycle) is not int or cycle < 0 or cycle < self.cycle:
                raise ProtocolError('Sample cycle regressed')
            if self.mode == 'EcMotion':
                if cycle == 0:
                    raise ProtocolError('Physical snapshot missing actual sample cycle')
                if cycle != self.cycle:
                    self.last_sample_change = self.now()
                elif self.now() - self.last_sample_change > self.freshness:
                    raise ProtocolError('Actual controller cycle stalled')
            self.cycle = cycle
            axes = snapshot['axes']
            by_name = {a['name']: a for a in axes}
            if len(by_name) != len(axes):
                raise ProtocolError('Duplicate snapshot axis')
            self.ready = snapshot.get('runtimeReady') is True and self.hello.get('runtimeReady') is True
            self.motion_ready = not self.failed and snapshot.get('motionReady', snapshot.get('runtimeReady')) is True
            for index, mapping in self.mapping.items():
                sample = by_name.get(mapping['ControllerAxis'])
                if sample is None:
                    raise ProtocolError('Mapped axis absent from snapshot')
                for field in ('actualPosition', 'actualVelocity'):
                    if not isinstance(sample.get(field), (float, int)) or not math.isfinite(sample[field]):
                        raise ProtocolError('Invalid actual axis numeric')
                if sample.get('state') not in STATES:
                    raise ProtocolError('Unknown actual axis state')
                if type(sample.get('motionId', 0)) is not int or not 0 <= sample.get('motionId',0) < 2**64:
                    raise ProtocolError('Invalid MotionId')
                slot = self.slots[index]
                slot.sample = sample
                if sample.get('fault') or sample['state'] == 'ErrorStop':
                    self.motion_ready = False
                if not slot.busy or slot.error or slot.aborted:
                    continue
                if slot.kind == 3:
                    # Native fault reset is asynchronous: an accepted request may
                    # precede several snapshots still showing the old drive fault.
                    if (not sample.get('fault') and sample['state'] in ('Disabled','Standstill')
                            and sample['actualVelocity'] == 0.):
                        slot.done, slot.busy = True, False
                    elif self.now() >= slot.reset_deadline:
                        slot.error, slot.busy, slot.done = 2005, False, False
                    continue
                if sample.get('fault') or sample['state'] == 'ErrorStop':
                    slot.error, slot.busy, slot.done = 2005, False, False
                    continue
                if slot.kind == 5:
                    if sample.get('motionId') == slot.motion_id and sample.get('motionAborted'):
                        slot.aborted, slot.busy, slot.done = True, False, False
                    elif (sample.get('motionId') == slot.motion_id and sample.get('motionDone') is True
                          and sample.get('motionBusy') is False and sample['state'] == 'Standstill'):
                        slot.done, slot.busy = True, False
                elif slot.kind != 6:
                    terminal = {1: sample['state'] != 'Disabled', 2: sample['state'] == 'Disabled',
                                3: sample['state'] == 'Disabled' and not sample.get('fault'),
                                4: sample.get('homed') is True and sample['state'] == 'Standstill',
                                7: sample['state'] in ('Standstill', 'Disabled') and sample['actualVelocity'] == 0.0}
                    proof = slot.kind != 4 or self.mode == 'EcMotionSimulator' or (slot.applied_cycle > 0 and cycle >= slot.applied_cycle)
                    if terminal.get(slot.kind) and proof:
                        slot.done, slot.busy = True, False
        except (ProtocolError, OSError, BackendError, KeyError, ValueError, TypeError) as error:
            self.shutdown(2004)
            if not self.hello:
                raise ProtocolError('Startup snapshot invalid') from error

    def acquire(self):
        if not self.lease:
            lease = self.backend.request('AcquireControlRequest', dict(clientName='Workbench axis worker', requestedLeaseMilliseconds=max(1000, int(self.watchdog*2000))), 'AcquireControlResponse')
            if not isinstance(lease.get('leaseToken'), str) or not lease['leaseToken']:
                raise ProtocolError('Missing lease token')
            self.lease = lease['leaseToken']

    def direct(self, index, kind, position=0., velocity=0., dynamics=None):
        payload=dict(leaseToken=self.lease, axis=self.mapping[index]['ControllerAxis'], command=KINDS[kind])
        # Native numeric JSON accessors distinguish omitted fields from explicit null.
        if kind == 5:payload['position']=position
        if kind in (5,6):payload['velocity']=velocity
        if dynamics is not None:
            payload.update(motionProfileVersion=2, acceleration=dynamics[0], deceleration=dynamics[1], jerk=dynamics[2])
        self.submissions += 1
        return self.backend.request('AxisCommandRequest', payload)

    def home(self, index):
        if self.mode == 'EcMotionSimulator':
            return self.direct(index, 4), 0
        if not self.capabilities()['PhysicalBasicAxis']:
            raise UnsupportedProof('Native scan receipts required for Home')
        self.batch += 1
        self.action += 1
        action = dict(requestId=self.action, kind=3, axis=index, position=0., velocity=0.,
                      acceleration=0., deceleration=0., outputMask=0, outputValues=0, groupCount=0)
        self.submissions += 1
        result = self.backend.request('PlcScanBatchRequest', dict(leaseToken=self.lease,
            sessionId=self.session, scanIndex=self.batch, sourceCycle=self.cycle,
            deadlineNs=time.monotonic_ns()+50_000_000,
            axes=[a['ControllerAxis'] for a in self.manifest.axes], outputs=[], actions=[action]))
        receipts = result.get('actionReceipts', [])
        if len(receipts) != 1 or receipts[0].get('requestId') != self.action or receipts[0].get('accepted') is not True or type(receipts[0].get('appliedCycle')) is not int or receipts[0]['appliedCycle'] <= 0:
            raise UnsupportedProof('Home receipt missing AppliedCycle proof')
        return result, receipts[0]['appliedCycle']

    def reject(self, index, previous, request, error):
        # Rejection is a separate receipt, never a replacement for the accepted owner.
        self.slots[index] = dataclasses.replace(previous, last_seen_request=max(request,previous.last_seen_request),
            rejected_request=request, rejected_error=error)

    def scan(self, data):
        if len(data) != COMMAND_BYTES:
            raise ProtocolError('Invalid private command image length')
        scan, flags = HEADER.unpack_from(data)
        if flags & ~1:
            raise ProtocolError('Unknown scan flags')
        # A delayed new frame cannot rescue an expired PLC watchdog.
        self.tick()
        if not flags & 1:
            self.shutdown(2002)
            return
        if scan <= self.scan_index:
            if scan < self.scan_index:
                self.shutdown(2002)
            return
        self.scan_index, self.last_scan = scan, self.now()
        if self.lease and not self.failed:
            try:
                renewed = self.backend.request('HeartbeatRequest', dict(leaseToken=self.lease))
                if renewed.get('accepted') is not True:
                    raise BackendError('lease_refused')
            except (BackendError, ProtocolError, OSError):
                self.shutdown(2003)
        for index in range(64):
            request, kind, position, velocity, acceleration, deceleration, jerk, slot_flags = COMMAND.unpack_from(data, HEADER.size+index*COMMAND.size)
            slot = self.slots[index]
            # Current held image is authoritative; duplicate requests must not renew control.
            if not request or request == slot.last_seen_request or (request == slot.request and slot.accepted):
                # Only the accepted Stop owner may release its existing latch.
                if request == slot.request and slot.accepted and slot.kind == 7 and not slot_flags & ~1:
                    slot.stop_held = bool(slot_flags & 1)
                continue
            held = slot.stop_held
            previous = slot
            slot = Slot(request=request, last_seen_request=request, kind=kind, sample=previous.sample, stop_held=held)
            self.slots[index] = slot
            if request < previous.last_seen_request:
                self.reject(index, previous, request, 2009)
                continue
            if index not in self.mapping or kind not in KINDS:
                self.reject(index, previous, request, 2001)
                continue
            if self.failed:
                self.reject(index, previous, request, self.failed)
                continue
            if (previous.busy or held) and kind not in (2,7):
                self.reject(index, previous, request, 2006)
                continue
            if slot_flags & ~1 or not math.isfinite(position) or not math.isfinite(velocity):
                self.reject(index, previous, request, 2007)
                continue
            mapping = self.mapping[index]
            dynamics = None
            if kind in (5,6,7):
                values = (acceleration,deceleration,jerk)
                applicable = values if kind in (5,6) else (0.,deceleration,jerk)
                if kind == 7 and acceleration != 0:
                    self.reject(index, previous, request, 2007)
                    continue
                if any(not math.isfinite(v) or v < 0 for v in values):
                    self.reject(index, previous, request, 2007)
                    continue
                if kind in (6,7) and VELOCITY_CAPABILITY not in self.hello.get('runtimeCapabilities',[]):
                    self.reject(index, previous, request, 2008)
                    continue
                if kind in (5,6,7):
                    maxima = tuple(mapping[k] for k in ('MaximumAcceleration','MaximumDeceleration','MaximumJerk'))
                    if any(v > cap for v,cap in zip(values,maxima)):
                        self.reject(index, previous, request, 2007)
                        continue
                    dynamics = tuple(v if v else cap for v,cap in zip(values,maxima))
            if (kind == 5 and (not mapping['MinPosition'] <= position <= mapping['MaxPosition'] or velocity <= 0)) or (kind == 6 and velocity == 0) or (kind in (5,6) and mapping.get('MaximumVelocity') is not None and abs(velocity)>mapping['MaximumVelocity']):
                self.reject(index, previous, request, 2007)
                continue
            if kind in (1,4,5,6) and (slot.sample.get('fault') or slot.sample.get('state') == 'ErrorStop'):
                self.reject(index, previous, request, 2005)
                continue
            if not self.ready and kind not in (2,3,7):
                self.reject(index, previous, request, 2005)
                continue
            if self.mode == 'EcMotion' and not self.capabilities()['PhysicalBasicAxis'] and kind not in (2,3,7):
                self.reject(index, previous, request, 2008)
                continue
            previously_owned = index in self.owned
            try:
                self.acquire()
                # Record ownership before submission: timeout may hide an accepted command.
                self.owned.add(index)
                result, applied = self.home(index) if kind == 4 else (self.direct(index,kind,position,velocity,dynamics), 0)
                if result.get('accepted') is not True:
                    raise BackendError('command_rejected')
                slot.applied_cycle = applied
                if kind == 3:
                    slot.reset_deadline = self.now() + 5.
                if kind == 5:
                    motion = result.get('motionId')
                    if type(motion) is not int or not 0 < motion < 2**64:
                        raise UnsupportedProof('MoveAbsolute requires a real returned MotionId')
                    if motion == previous.sample.get('motionId'):
                        raise UnsupportedProof('MoveAbsolute returned the previous MotionId')
                    slot.motion_id = motion
                slot.accepted = True
                slot.stop_held = bool(slot_flags & 1)
                slot.busy = True
            except UnsupportedProof:
                slot.error = 2008
                self.shutdown(2008)
            except BackendError as error:
                slot.error = 2003 if str(error) in ('control_busy', 'lease_refused', 'invalid_lease', 'lease_expired') else 2005
                if str(error) in ('invalid_state', 'invalid_argument', 'command_rejected'):
                    # Proven rejection leaves the prior trajectory and safety ownership intact.
                    self.reject(index, previous, request, slot.error)
                    if not previously_owned:
                        self.owned.discard(index)
                else:
                    self.shutdown(slot.error)
            except (OSError, ProtocolError):
                slot.error = 2004
                self.shutdown(2004)

    def tick(self):
        if self.last_scan is not None and not self.failed and self.now()-self.last_scan > self.watchdog:
            self.shutdown(2002)

    def shutdown(self, error=2004, report=True):
        if self.failed:
            return
        self.failure_reason = getattr(self.backend,'failure',None) or {
            2002:'plc_watchdog_expired',2003:'control_ownership_lost',
            2004:'transport_or_protocol_failed',2005:'native_command_failed',
            2008:'capability_or_receipt_invalid'}.get(error,'channel_failed')
        self.failed, self.ready, self.motion_ready = error, False, False
        for index in self.mapping:
            slot = self.slots[index]
            slot.error = slot.error or error
            slot.done = False
        for index in self.owned:
            slot = self.slots[index]
            slot.done, slot.busy, slot.aborted = False, False, True
            slot.error = slot.error or error
            if self.lease:
                try:
                    self.direct(index,7)
                except (OSError, ProtocolError, BackendError):
                    pass
        if self.lease:
            try:
                self.backend.request('ReleaseControlRequest', dict(leaseToken=self.lease))
            except (OSError, ProtocolError, BackendError):
                pass
        self.lease = None
        # Log once after protective handling; never leak command payloads/leases,
        # and never let a broken logging pipe interrupt the safety path.
        try:
            if report:
                print(json.dumps(dict(event='axis_channel_failed',interface=INTERFACE,
                    code=self.failed,reason=self.failure_reason,
                    recovery='stop_plc_and_reestablish_channel')),file=sys.stderr,flush=True)
        except (OSError,ValueError):pass

    def feedback(self):
        output = bytearray(FEEDBACK_BYTES)
        for index, slot in enumerate(self.slots):
            # A command error can coexist with fresh actual data. A terminal
            # worker failure cannot: cached samples must never look valid again.
            sample = slot.sample if not self.failed and index in self.mapping else {}
            state = STATES.get(sample.get('state'), 0)
            enabled = state in (2,3,4,5,6)
            FEEDBACK.pack_into(output, index*FEEDBACK.size, slot.request, int(slot.busy), int(slot.done and not self.failed),
                               slot.error, int(slot.aborted), int(enabled), int(bool(state and sample.get('homed'))),
                               state, slot.motion_id, sample.get('actualPosition',0.), sample.get('actualVelocity',0.), slot.rejected_request, slot.rejected_error)
        return bytes(output)


class UnsupportedProof(Exception):
    pass


def publish_capabilities(path, capabilities):
    """Create a new private regular file without following existing symlinks."""
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, 'O_NOFOLLOW', 0)
    temporary = path + '.tmp-' + uuid.uuid4().hex
    fd = os.open(temporary, flags, 0o600)
    try:
        with os.fdopen(fd, 'w', encoding='utf-8') as stream:
            json.dump(capabilities, stream, allow_nan=False)
            stream.flush()
            # This is a volatile, PID-bound readiness heartbeat, not a durable
            # configuration journal. Atomic replacement is sufficient; fsync
            # here blocks PLC replies on slow disks/overlay filesystems.
        if os.path.lexists(path):
            info = os.lstat(path)
            if not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid():
                raise ValueError('Capability target must be an owned regular file')
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


class CapabilityPublisher:
    """Publish state changes immediately and unchanged liveness once a second."""
    def __init__(self, path, now=time.monotonic):
        self.path, self.now = path, now
        self.previous = None
        self.published_at = float('-inf')

    def __call__(self, capabilities):
        current = self.now()
        # Timing counters change per native read; they must not turn the private
        # liveness heartbeat back into a per-scan file write.
        state={key:value for key,value in capabilities.items() if key!='LinkMetrics'}
        if state != self.previous or current - self.published_at >= 1.0:
            publish_capabilities(self.path, capabilities)
            self.previous = dict(state)
            self.published_at = current


def serve(args):
    if sys.byteorder != 'little' or os.name != 'posix':
        raise ValueError('Private axis worker requires little-endian Unix')
    manifest = Manifest.load(args.manifest)
    capability_path = args.capabilities or args.socket + '.capabilities.json'
    publish = CapabilityPublisher(capability_path)
    # Revoke an earlier run's sidecar before any controller connection attempt.
    publish(dict(SchemaVersion=1, InterfaceVersion=INTERFACE, CommandBytes=COMMAND_BYTES, FeedbackBytes=FEEDBACK_BYTES, RequiredRuntimeCapabilities=manifest.document['RequiredRuntimeCapabilities'], NativeRuntimeCapabilities=[],
        ManifestSha256=manifest.digest, Backend=args.backend, PID=os.getpid(),
        ControllerReady=False, PhysicalBasicAxis=False, SimulatorBasicAxis=False,
        Continuous=False, MotionCycleApplied=False))
    # All paths are explicit. Never start, discover, or automatically reconnect a controller.
    controller = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    controller.settimeout(.2)
    try:
        controller.connect(args.controller_socket)
    except OSError:
        controller.close()
        raise
    worker = Worker(manifest, ProtocolClient(controller), args.backend)
    try:
        worker.probe()
    except (ProtocolError, BackendError, OSError):
        controller.close()
        raise
    publish(worker.capabilities())
    server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    selector = selectors.DefaultSelector()
    client = None
    handshaken = False
    handshake_deadline = 0.
    bound_socket = False
    input_buffer, output_buffer = bytearray(), bytearray()
    next_poll = time.monotonic()
    def end_plc_session():
        nonlocal worker, client, handshaken
        handshaken = False
        # A loader probe / PLC unload ends one PLC session, not the listening
        # service. Stop/release and discard ALL old pending frames first.
        worker.shutdown(report=any(slot.busy for slot in worker.slots))
        publish(worker.capabilities())
        selector.unregister(client)
        client.close()
        client = None
        input_buffer.clear()
        output_buffer.clear()
        # Same physical transport: no controller reconnect, retry, request or
        # lease carries over. A lost physical connection still fails closed.
        worker = Worker(manifest, worker.backend, args.backend)
        worker.probe()
        publish(worker.capabilities())
    try:
        if os.path.lexists(args.socket):
            raise ValueError('Private bridge socket already exists; refuse replacing it')
        server.bind(args.socket)
        bound_socket = True
        os.chmod(args.socket, 0o600)
        server.listen(1)
        server.setblocking(False)
        selector.register(server, selectors.EVENT_READ)
        while True:
            current = time.monotonic()
            if client is not None and not handshaken and current > handshake_deadline:
                end_plc_session()
            if current >= next_poll:
                worker.tick()
                worker.poll()
                publish(worker.capabilities())
                # Readiness and watchdogs operate before any PLC/native connection.
                # Poll timing is independent of the configured PLC scan interval.
                next_poll = current + .01
            for key, events in selector.select(max(0., min(.01,next_poll-time.monotonic()))):
                if key.fileobj is server:
                    accepted, _ = server.accept()
                    if client is not None:
                        accepted.close()
                        continue
                    client = accepted
                    handshaken = False
                    handshake_deadline = time.monotonic() + .2
                    output_buffer.extend(HELLO)
                    client.setblocking(False)
                    selector.register(client, selectors.EVENT_READ | selectors.EVENT_WRITE)
                else:
                    if events & selectors.EVENT_READ:
                        try:
                            data = client.recv(4*COMMAND_BYTES)
                        except ConnectionResetError:
                            data = b''
                        if not data:
                            end_plc_session()
                            continue
                        input_buffer.extend(data)
                        if len(input_buffer) > 4*COMMAND_BYTES:
                            raise ProtocolError('Private bridge command capacity exceeded')
                        if not handshaken and len(input_buffer) >= len(HELLO):
                            if bytes(input_buffer[:len(HELLO)]) != HELLO:
                                end_plc_session()
                                continue
                            del input_buffer[:len(HELLO)]
                            handshaken = True
                        while handshaken and len(input_buffer) >= COMMAND_BYTES:
                            previous_submissions, previous_failure = worker.submissions, worker.failed
                            worker.scan(bytes(input_buffer[:COMMAND_BYTES]))
                            del input_buffer[:COMMAND_BYTES]
                            if worker.submissions != previous_submissions:
                                # Refresh after a new backend command, without issuing
                                # expensive snapshots for every repeated 1 ms PLC image.
                                worker.poll()
                                publish(worker.capabilities())
                                next_poll = time.monotonic() + .01
                            elif worker.failed != previous_failure:
                                publish(worker.capabilities())
                            if len(output_buffer) >= 4*FEEDBACK_BYTES:
                                raise ProtocolError('Private bridge feedback consumer stalled')
                            # Exactly one correlated reply for every complete input image,
                            # including repeated scan counters. No unsolicited frames.
                            output_buffer.extend(worker.feedback())
                            selector.modify(client, selectors.EVENT_READ | selectors.EVENT_WRITE)
                    if events & selectors.EVENT_WRITE and output_buffer:
                        try:
                            count = client.send(output_buffer)
                        except (BrokenPipeError, ConnectionResetError):
                            end_plc_session()
                            continue
                        del output_buffer[:count]
                        if not output_buffer:
                            selector.modify(client, selectors.EVENT_READ)
    finally:
        worker.shutdown(report=any(slot.busy for slot in worker.slots))
        publish_capabilities(capability_path, worker.capabilities())
        selector.close()
        if client: client.close()
        server.close()
        controller.close()
        if bound_socket and os.path.exists(args.socket): os.unlink(args.socket)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', required=True)
    parser.add_argument('--socket', default='/home/a/.local/share/st-workbench/axis-v2/axis.sock')
    parser.add_argument('--controller-socket', required=True)
    parser.add_argument('--backend', required=True, choices=['EcMotion','EcMotionSimulator'])
    parser.add_argument('--capabilities')
    args = parser.parse_args()
    try:
        serve(args)
    except (ValueError, ProtocolError, BackendError, OSError, KeyboardInterrupt) as error:
        print('axis worker: ' + str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
