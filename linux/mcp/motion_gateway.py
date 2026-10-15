#!/usr/bin/env python3
"""Operator-configured EcMotion v4 session for the MCP host (MIT).

JSON-lines over stdin/stdout; never accepts socket, backend or manifest overrides.
This process is a supervisory channel, not a PLC scan or real-time motion loop.
"""
import argparse
import datetime
import hashlib
import json
import math
import os
from pathlib import Path
import queue
import socket
import sys
import threading
import time
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'axis'))
from axis_worker import (Manifest, Worker, Slot, ProtocolClient, ProtocolError,
                         BackendError, UnsupportedProof, KINDS)

MAX_LINE = 65536
MAX_IDS = 4096
TERMINAL = {'Completed', 'Failed', 'Aborted', 'Unknown'}
FIELDS = {
    'capabilities': set(), 'status': set(), 'acquire': {'durationMilliseconds'}, 'release': set(),
    'operation': {'operationId'}, 'stop': {'axis', 'operationId'},
    'execute': {'operationId', 'axis', 'kind', 'position', 'velocity', 'durationMilliseconds'}}


def identifier(value):
    if not isinstance(value, str) or not 1 <= len(value) <= 128 or not value.isascii() or any(ord(c) < 33 for c in value):
        raise ValueError('Identifier must contain 1..128 printable ASCII characters')
    return value


def finite(value):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise ValueError('Finite numeric parameter required')
    return float(value)


class Gateway:
    def __init__(self, manifest, backend, mode, now=time.monotonic):
        self.worker = Worker(manifest, backend, mode, now)
        self.now = now
        self.requests = set()
        self.operations = {}
        self.closed = False
        self.deadline = 0.
        self.heartbeat_at = 0.
        self.jog_deadline = None
        self.worker.probe()
        self._healthy()

    def _healthy(self):
        if self.worker.failed:
            raise ProtocolError('Controller snapshot invalid; session is unknown')

    def _poll(self):
        self.worker.poll()
        self._healthy()
        for index in self.worker.mapping:
            sample=self.worker.slots[index].sample
            if any(type(sample.get(key)) not in (int,float) for key in ('actualPosition','actualVelocity')):
                raise ProtocolError('Invalid actual numeric sample')
            if type(sample.get('homed')) is not bool:
                raise ProtocolError('Invalid actual homed flag')
        for op in self.operations.values():
            if op['state'] in TERMINAL:
                continue
            slot = self.worker.slots[op['axisIndex']]
            if slot.done and self.worker.mode=='EcMotion' and self.worker.cycle<=op['submittedCycle']:
                # Worker can observe the old terminal state before the command
                # reaches the native cycle. Do not leave that result latched:
                # the next actual cycle must reevaluate its own current sample.
                slot.done=False
                slot.busy=True
            completed=slot.done and (self.worker.mode!='EcMotion' or self.worker.cycle>op['submittedCycle'])
            op['state'] = ('Failed' if slot.error else 'Aborted' if slot.aborted else
                           'Completed' if completed else 'Running')
            op['sampleCycle'] = self.worker.cycle
            op['sample'] = dict(slot.sample)

    def _acquire(self, milliseconds=1000):
        result = self.worker.backend.request('AcquireControlRequest',
            dict(clientName='ST Workbench MCP motion session', requestedLeaseMilliseconds=milliseconds),
            'AcquireControlResponse')
        token, granted = result.get('leaseToken'), result.get('grantedLeaseMilliseconds')
        # Save token before verification so malformed grants are still released.
        if isinstance(token, str) and token:
            self.worker.lease = token
        if not self.worker.lease or type(granted) is not int or not 250 <= granted <= milliseconds:
            raise ProtocolError('Unbounded or malformed controller lease grant')
        try:
            expires = datetime.datetime.fromisoformat(result['expiresAt'].replace('Z', '+00:00'))
            remaining = (expires-datetime.datetime.now(datetime.timezone.utc)).total_seconds()
        except (KeyError, TypeError, ValueError, AttributeError) as error:
            raise ProtocolError('Invalid lease expiry proof') from error
        if not 0 < remaining <= milliseconds/1000 + .05:
            raise ProtocolError('Invalid controller lease expiry')
        self.lease_milliseconds = granted
        self.lease_until = self.now()+remaining
        self.heartbeat_at = self.now()+granted/3000
        return remaining

    def _release(self):
        if self.worker.lease:
            result = self.worker.backend.request('ReleaseControlRequest', dict(leaseToken=self.worker.lease))
            if result.get('accepted') is not True:
                raise ProtocolError('Release not acknowledged')
            self.worker.lease = None
        self.worker.owned.clear()
        self.jog_deadline = None

    def _stop(self, index):
        result = self.worker.direct(index, 7)
        if result.get('accepted') is not True:
            raise ProtocolError('Stop not acknowledged')
        for op in self.operations.values():
            if op['axisIndex'] == index and op['state'] not in TERMINAL:
                op['state'] = 'Aborted'

    def close(self, unknown=True):
        if self.closed:
            return
        self.closed = True
        for index in tuple(self.worker.owned):
            try:
                self._stop(index)
            except (OSError, ProtocolError, BackendError):
                unknown = True
        try:
            self._release()
        except (OSError, ProtocolError, BackendError):
            self.worker.lease = None
            unknown = True
        for op in self.operations.values():
            if op['state'] not in TERMINAL or unknown and op['state'] == 'Aborted':
                op['state'] = 'Unknown' if unknown else 'Aborted'
        self.worker.ready = False

    def tick(self):
        if self.closed:
            return
        try:
            if self.worker.lease and self.now() >= self.deadline:
                self.close(False)
                return
            if self.jog_deadline is not None and self.now() >= self.jog_deadline:
                for index in tuple(self.worker.owned):
                    self._stop(index)
                self._release()
            if self.worker.lease and self.jog_deadline is None and self.now() >= self.heartbeat_at:
                result = self.worker.backend.request('HeartbeatRequest', dict(leaseToken=self.worker.lease))
                if result.get('accepted') is not True:
                    raise ProtocolError('Heartbeat refused')
                self.lease_until = self.now()+self.lease_milliseconds/1000
                self.heartbeat_at = self.now()+self.lease_milliseconds/3000
            self._poll()
        except (OSError, ProtocolError, BackendError, KeyError, ValueError, TypeError):
            self.close(True)

    def handle(self, request):
        request_id = request.get('requestId') if isinstance(request, dict) else None
        result = dict(requestId=request_id, success=False, state='Rejected')
        try:
            if not isinstance(request, dict):
                raise ValueError('Request must be an object')
            identifier(request_id)
            if request_id in self.requests:
                raise ValueError('Duplicate requestId; no command replay')
            if len(self.requests) >= MAX_IDS:
                self.close(False)
                raise ValueError('Session request capacity exhausted; release and start a new session')
            self.requests.add(request_id)
            action = request.get('action')
            if action not in FIELDS or set(request)-{'requestId', 'action'}-FIELDS[action]:
                raise ValueError('Unsupported action or request field')
            if action == 'operation':
                op = self.operations.get(identifier(request.get('operationId')))
                if op is None:
                    raise ValueError('Unknown operationId')
                return dict(result, success=True, state=op['state'], operation=dict(op))
            if self.closed:
                raise ValueError('Session closed; results may be unknown; no reconnect or replay')
            if action == 'capabilities':
                return dict(result, success=True, state='Ready', capabilities=self.worker.capabilities(),
                    leaseHeld=bool(self.worker.lease), maximumControlMilliseconds=30000,
                    minimumJogMilliseconds=250, maximumJogMilliseconds=30000,
                    PhysicalTimedJog=False, SimulatorTimedJog=self.worker.mode=='EcMotionSimulator')
            if action in ('execute', 'stop') and request.get('operationId') in self.operations:
                raise ValueError('Duplicate operationId; no replay')
            self._poll()
            if action == 'status':
                return dict(result, success=True, state='Ready', sampleCycle=self.worker.cycle,
                    manifestSha256=self.worker.manifest.digest, backend=self.worker.mode,
                    axes=[dict(axis=m['Name'], unit=m['Unit'], sample=dict(self.worker.slots[i].sample))
                          for i,m in self.worker.mapping.items()])
            if action == 'acquire':
                if self.worker.lease:
                    raise ValueError('Session already holds control; deadline is not extended')
                duration=request.get('durationMilliseconds',1000)
                if type(duration) is not int or not 250 <= duration <= 30000:
                    raise ValueError('Lease duration must be 250..30000 ms')
                self._acquire(duration)
                self.deadline = self.now()+30
                return dict(result, success=True, state='Acquired', remainingMilliseconds=30000)
            if not self.worker.lease:
                raise ValueError('Explicit acquire required')
            if self.now() >= self.deadline:
                self.close(False)
                raise ValueError('Control session deadline elapsed')
            if action == 'release':
                for index in tuple(self.worker.owned):
                    self._stop(index)
                self._release()
                return dict(result, success=True, state='Released')
            operation_id = identifier(request.get('operationId'))
            if operation_id in self.operations:
                raise ValueError('Duplicate operationId; no replay')
            axis = request.get('axis')
            index = next((i for i,m in self.worker.mapping.items() if m['Name'] == axis), None)
            if index is None:
                raise ValueError('Axis is not in active manifest')
            kind = 'Stop' if action == 'stop' else request.get('kind')
            if kind not in KINDS.values() or action == 'execute' and kind == 'Stop':
                raise ValueError('Unsupported axis command')
            slot = self.worker.slots[index]
            if kind != 'Stop' and any(o['state'] not in TERMINAL and o['axisIndex'] == index for o in self.operations.values()):
                raise ValueError('Axis busy; use stop to preempt')
            if self.jog_deadline is not None and kind != 'Stop':
                raise ValueError('Dedicated timed Jog lease cannot accept another command')
            mapping = self.worker.mapping[index]
            position = finite(request.get('position', 0))
            velocity = finite(request.get('velocity', 0))
            if self.worker.mode == 'EcMotion' and kind in ('Home','MoveAbsolute','Jog') and any(mapping.get(key) is not None for key in ('MaximumAcceleration','MaximumDeceleration','MaximumJerk')):
                raise ValueError('capability_missing: explicit dynamic parameters are not confirmed applied by controller')
            if kind == 'Home' and (slot.sample['state'] != 'Standstill' or slot.sample['actualVelocity'] != 0 or slot.sample.get('fault')):
                raise ValueError('Home requires enabled stationary axis without fault')
            if kind in ('MoveAbsolute','Jog'):
                if slot.sample['state'] != 'Standstill' or slot.sample['actualVelocity'] != 0 or not slot.sample.get('homed'):
                    raise ValueError('Motion requires enabled, homed, stationary axis')
                if velocity == 0 or kind == 'MoveAbsolute' and velocity <= 0:
                    raise ValueError('Nonzero velocity required; positioning velocity must be positive')
                cap = mapping.get('MaximumVelocity')
                if cap is not None and abs(velocity)>cap:
                    raise ValueError('Axis velocity limit exceeded')
            if kind == 'MoveAbsolute' and not mapping['MinPosition'] <= position <= mapping['MaxPosition']:
                raise ValueError('Axis position limit exceeded')
            if kind not in ('Stop','Disable','Reset') and (not self.worker.ready or self.worker.mode == 'EcMotion' and not self.worker.capabilities()['PhysicalBasicAxis']):
                raise ValueError('capability_missing')
            if kind == 'Jog':
                if self.worker.mode == 'EcMotion':
                    raise ValueError('capability_missing: physical Jog needs confirmed device stopping bounds/timed motion')
                duration = request.get('durationMilliseconds')
                if type(duration) is not int or not 250 <= duration <= 30000 or self.lease_until > self.deadline:
                    raise ValueError('Jog duration must be 250..30000 ms within control deadline')
                if any(o['state'] not in TERMINAL for o in self.operations.values()):
                    raise ValueError('Jog requires exclusive idle session')
                target = slot.sample['actualPosition'] + velocity*duration/1000
                if not mapping['MinPosition'] <= target <= mapping['MaxPosition']:
                    raise ValueError('Jog displacement exceeds axis limits')
                # v4 has no timed Jog command. A dedicated non-renewed lease is
                # its device-side deadline. Never heartbeat or extend this lease.
                remaining=self.lease_until-self.now()
                if not 0 < remaining <= duration/1000:
                    raise ValueError('Jog requires lease with remaining device duration <= Jog duration; acquire using matching durationMilliseconds')
                self.jog_deadline = self.lease_until
            op = dict(operationId=operation_id, axis=axis, axisIndex=index, kind=kind,
                      state='Accepted', motionId=0, appliedCycle=0,
                      backend=self.worker.mode, sampleCycle=self.worker.cycle, submittedCycle=self.worker.cycle)
            self.operations[operation_id] = op
            self.worker.owned.add(index)  # timeout can conceal an accepted command
            number = next(k for k,v in KINDS.items() if v == kind)
            previous_motion = slot.sample.get('motionId')
            if kind == 'Stop':
                self._stop(index)
                op['state'] = 'Accepted'
                response, applied = dict(accepted=True), 0
            else:
                response, applied = self.worker.home(index) if kind == 'Home' else (self.worker.direct(index,number,position,velocity), 0)
            if response.get('accepted') is not True:
                raise ProtocolError('Command acceptance not proven')
            motion_id = response.get('motionId',0)
            if kind == 'MoveAbsolute' and (type(motion_id) is not int or not 0 < motion_id < 2**64 or motion_id == previous_motion):
                raise ProtocolError('Actual new MotionId missing')
            self.worker.slots[index] = Slot(kind=number,busy=True,motion_id=motion_id or 0,
                applied_cycle=applied, reset_deadline=self.now()+5, sample=slot.sample)
            op.update(motionId=motion_id or 0, appliedCycle=applied)
            if kind == 'Jog':
                op['maximumRemainingMilliseconds']=max(0,int((self.jog_deadline-self.now())*1000))
            return dict(result, success=True, state='Accepted', operation=dict(op))
        except ValueError as error:
            return dict(result, message=str(error))
        except BackendError as error:
            if str(error)=='control_busy' and not self.worker.lease:
                return dict(result,state='Conflict',message='Controller owned by PLC or another session')
            self.close(True)
            return dict(result,state='Unknown',message='Controller rejected session; no retry')
        except (OSError,ProtocolError,UnsupportedProof,KeyError,TypeError,OverflowError):
            self.close(True)
            return dict(result,state='Unknown',message='Controller result unproven; session stopped/released where possible; no retry')


def serve(args):
    with open(args.manifest,'rb') as manifest_file:
        raw=manifest_file.read(1048577)
    if len(raw)>1048576:
        raise ValueError('Manifest exceeds 1 MiB')
    manifest = Manifest.from_object(json.loads(raw),hashlib.sha256(raw).hexdigest())
    stream = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    stream.settimeout(.2)
    stream.connect(args.controller_socket)
    gateway = Gateway(manifest, ProtocolClient(stream), args.backend)
    inbox = queue.PriorityQueue(maxsize=16)
    outbox = queue.Queue(maxsize=8)
    transport_failed = threading.Event()
    def enqueue(value, sequence):
        priority=1
        if value is None or isinstance(value,Exception):
            priority=-1
        else:
            try:
                parsed=json.loads(value)
                if isinstance(parsed,dict) and parsed.get('action') in ('stop','release'):
                    priority=0
            except (ValueError,UnicodeError,RecursionError):
                pass
        try:
            inbox.put_nowait((priority,sequence,value))
        except queue.Full:
            # Input overload must stop control instead of hiding EOF/stop behind
            # an unbounded producer or blocking the supervisory polling loop.
            transport_failed.set()
            return False
        return True
    def reader():
        # Unbuffered OS reads avoid a daemon holding BufferedReader's lock
        # during process teardown while the host leaves stdin open.
        buffer = bytearray()
        sequence=0
        try:
            while True:
                chunk=os.read(sys.stdin.fileno(),4096)
                if not chunk:
                    enqueue(None if not buffer else ValueError('Truncated request'),sequence)
                    return
                buffer.extend(chunk)
                while b'\n' in buffer:
                    end=buffer.index(b'\n')+1
                    if end>MAX_LINE:
                        enqueue(ValueError('Oversized request'),sequence);return
                    if not enqueue(bytes(buffer[:end]),sequence):return
                    sequence+=1
                    del buffer[:end]
                if len(buffer)>MAX_LINE:
                    enqueue(ValueError('Oversized request'),sequence);return
        except Exception:
            transport_failed.set()
    def writer():
        try:
            while True:
                data=outbox.get()
                if data is None:return
                view=memoryview(data)
                while view:
                    count=os.write(sys.stdout.fileno(),view)
                    if count<=0:raise OSError('Output disconnected')
                    view=view[count:]
                outbox.task_done()
        except (OSError,ValueError):
            transport_failed.set()
    threading.Thread(target=reader,daemon=True).start()
    output_thread=threading.Thread(target=writer,daemon=True)
    output_thread.start()
    barrier=-1
    try:
        while not gateway.closed:
            if transport_failed.is_set():break
            gateway.tick()
            try:
                _,sequence,value=inbox.get(timeout=.025)
            except queue.Empty:
                continue
            if value is None or isinstance(value,Exception):
                break
            try:
                request=json.loads(value,parse_constant=lambda s: (_ for _ in ()).throw(ValueError(s)))
                action=request.get('action') if isinstance(request,dict) else None
                if action in ('stop','release'):
                    barrier=max(barrier,sequence)
                if action in ('acquire','execute') and sequence<barrier:
                    response=dict(requestId=request.get('requestId'),success=False,state='Aborted',message='Queued control invalidated by newer stop/release')
                else:
                    response=gateway.handle(request)
            except (ValueError,UnicodeError,RecursionError):
                response=dict(requestId=None,success=False,state='Rejected',message='Invalid bounded JSON request')
            data=(json.dumps(response,allow_nan=False,separators=(',',':'))+'\n').encode('utf-8')
            try:
                outbox.put_nowait(data)
            except queue.Full:
                break
    finally:
        gateway.close(True)
        stream.close()
        # Bounded drain only. A client that stops reading must never freeze
        # lease deadlines, controller polling or process teardown.
        try:outbox.put_nowait(None)
        except queue.Full:pass
        output_thread.join(timeout=.1)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest',required=True)
    parser.add_argument('--controller-socket',required=True)
    parser.add_argument('--backend',required=True,choices=('EcMotion','EcMotionSimulator'))
    args=parser.parse_args()
    if sys.version_info < (3,11):
        print('Motion gateway requires Python 3.11+ for controller timestamp verification',file=sys.stderr)
        return 1
    try:
        serve(args)
    except (OSError,ValueError,ProtocolError,BackendError,KeyError,TypeError):
        print('Motion gateway failed to establish a verified controller session',file=sys.stderr)
        return 1
    return 0

if __name__=='__main__':
    raise SystemExit(main())
