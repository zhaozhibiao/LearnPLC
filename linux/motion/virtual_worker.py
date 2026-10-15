#!/usr/bin/env python3
"""Isolated virtual sampled-path worker. Never opens a device or EtherCAT interface.

Ownership follows EcMotion's exclusive control lease model; identity follows immutable
PathResource id/version. This adapter deliberately does not claim physical Continuous.
Only this worker's tick consumes preplanned samples; desktop time cannot set Done.
"""
import argparse
import json
import math
import os
import pathlib
import selectors
import socket
import struct
import time
import hashlib
import signal

ASSET_MISMATCH, PLC_STALE, STOPPED, OWNERSHIP = 1001, 1002, 1003, 1004
DISCONNECTED, FAULT, REQUEST_CONFLICT, BUSY, TRANSPORT = 1005, 1006, 1007, 1008, 1009
COMMAND = struct.Struct('<QQQ64sQQ')
FEEDBACK = struct.Struct('<QQQQd')
MAX_SAMPLES = 250000

def write_capabilities(sidecar, manifest_bytes, assets):
    """Publish private readiness metadata atomically, without replacing an owner."""
    sidecar = pathlib.Path(sidecar)
    if os.path.lexists(sidecar):
        raise FileExistsError('capability sidecar already exists')
    metadata = {'SchemaVersion': 1, 'Backend': 'VirtualContinuous', 'PID': os.getpid(),
        'PhysicalContinuous': False, 'ManifestSha256': hashlib.sha256(manifest_bytes).hexdigest(),
        'Paths': [{key: asset[key] for key in ('Id', 'NumericId', 'Version', 'ContentHash', 'Axes')}
                  for asset in assets]}
    temporary = pathlib.Path(str(sidecar) + '.' + str(os.getpid()) + '.tmp')
    fd = os.open(temporary, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    try:
        with os.fdopen(fd, 'w', encoding='utf-8') as stream:
            json.dump(metadata, stream, separators=(',', ':'))
            stream.flush()
            os.fsync(stream.fileno())
        # Hardlink creation publishes a complete regular file and refuses overwrite.
        os.link(temporary, sidecar)
    finally:
        temporary.unlink(missing_ok=True)
    return metadata

def feedback(request=0, error=0, busy=False, done=False, progress=0.):
    return {'RequestId': request, 'Busy': busy, 'Done': done, 'Error': bool(error),
            'ErrorID': error, 'Progress': progress, 'Backend': 'VirtualContinuous'}

class VirtualEngine:
    capability = 'VirtualContinuous'

    def __init__(self, paths, watchdog=.25, backend='VirtualContinuous'):
        if backend != self.capability:
            raise ValueError('physical EcMotion Continuous is unsupported; explicitly select VirtualContinuous')
        if not .01 <= watchdog <= 5:
            raise ValueError('invalid PLC scan watchdog')
        self.assets = {}
        numeric = {}
        if len(paths) > 256:
            raise ValueError('too many preloaded assets')
        total_samples = 0
        for item in paths:
            asset, trajectory = item['Asset'], item['Trajectory']
            key = (asset['NumericId'], asset['Version'])
            digest = asset['ContentHash']
            if (asset['Mode'] != 'Continuous' or asset['Unit'] != 'mm' or
                not 0 < key[0] < 2**63 or not 0 < key[1] < 2**32 or
                len(digest) != 64 or any(c not in '0123456789abcdef' for c in digest) or
                len(asset['Axes']) not in (2, 3) or len(set(asset['Axes'])) != len(asset['Axes'])):
                raise ValueError('invalid continuous path identity or axis map')
            if key in self.assets or (key[0] in numeric and numeric[key[0]] != asset['Id']):
                raise ValueError('duplicate immutable path or numeric ID collision')
            numeric[key[0]] = asset['Id']
            samples = trajectory['Samples']
            total_samples += len(samples)
            if total_samples > 1000000:
                raise ValueError('preloaded sample budget exceeded')
            duration = trajectory['DurationSeconds']
            if not 2 <= len(samples) <= MAX_SAMPLES or not math.isfinite(duration) or duration <= 0:
                raise ValueError('invalid bounded sampled trajectory')
            previous = -1
            cycle = asset['Profile']['CycleSeconds']
            if not math.isfinite(cycle) or not .0001 <= cycle <= .1:
                raise ValueError('invalid virtual tick cycle')
            for i, sample in enumerate(samples):
                t = sample['TimeSeconds']
                if not math.isfinite(t) or t <= previous or (i == 0 and t != 0):
                    raise ValueError('trajectory time must increase from zero')
                if i and t - previous > cycle * 1.01:
                    raise ValueError('trajectory has missing ticks')
                for field in ('Position', 'Velocity', 'Acceleration'):
                    if not all(math.isfinite(sample[field][axis]) for axis in ('X', 'Y', 'Z')):
                        raise ValueError('trajectory contains nonfinite values')
                previous = t
            if abs(previous - duration) > 1e-9:
                raise ValueError('trajectory duration mismatch')
            self.assets[key] = (digest, trajectory)
        self.watchdog = watchdog
        self.owner = None
        self.feedback = feedback()
        self.position = None
        self.velocity = None
        self.acceleration = None
        self.scan = None
        self.last_scan = 0
        self.binding = None
        self.started = 0
        self.index = 0
        self.trajectory = None

    def command(self, owner, request, path_id, version, digest, scan, running, stop, now):
        if self.owner is not None and self.owner != owner:
            return feedback(request, OWNERSHIP)
        self.owner = owner
        if self.scan is None or scan > self.scan:
            self.scan, self.last_scan = scan, now
        elif scan < self.scan:
            self._cancel(PLC_STALE)
            return self.feedback
        if not running or stop:
            if self.feedback['Busy']:
                self._cancel(STOPPED)
            return self.feedback
        if request == 0:
            return self.feedback
        binding = (request, path_id, version, digest)
        if self.binding and request == self.binding[0]:
            if binding != self.binding:
                return feedback(request, REQUEST_CONFLICT)
            return self.feedback
        if self.feedback['Busy']:
            return feedback(request, BUSY)
        if self.binding and request < self.binding[0]:
            return feedback(request, REQUEST_CONFLICT)
        self.binding = binding
        resource = self.assets.get((path_id, version))
        if resource is None or resource[0] != digest:
            self.feedback = feedback(request, ASSET_MISMATCH)
            return self.feedback
        self.trajectory = resource[1]
        self.index, self.started = 0, now
        self.feedback = feedback(request, busy=True)
        return self.feedback

    def _cancel(self, error):
        self.feedback = feedback(self.feedback['RequestId'], error,
                                 progress=self.feedback['Progress'])

    def tick(self, now):
        if not self.feedback['Busy']:
            return
        if now - self.last_scan > self.watchdog:
            self._cancel(PLC_STALE)
            return
        elapsed = max(0., now - self.started)
        samples = self.trajectory['Samples']
        # Outputs are consumed in order; completion requires the final table output.
        while self.index < len(samples) and samples[self.index]['TimeSeconds'] <= elapsed + 1e-12:
            sample = samples[self.index]
            self.position, self.velocity, self.acceleration = (sample[x] for x in ('Position', 'Velocity', 'Acceleration'))
            self.index += 1
        self.feedback['Progress'] = min(1., samples[max(0, self.index - 1)]['TimeSeconds'] / self.trajectory['DurationSeconds'])
        if self.index == len(samples):
            self.feedback['Busy'], self.feedback['Done'] = False, True

    def fault(self):
        self._cancel(FAULT)

    def release(self, owner):
        if self.owner == owner:
            if self.feedback['Busy']:
                self._cancel(DISCONNECTED)
            self.owner = None
            self.scan = None
            # New connection has its own request epoch. Old completion cannot restart.
            self.binding = None

def serve(manifest_path, socket_path, watchdog):
    manifest_bytes = pathlib.Path(manifest_path).read_bytes()
    manifest = json.loads(manifest_bytes)
    if manifest.get('SchemaVersion') != 1:
        raise ValueError('unsupported motion manifest schema')
    engine = VirtualEngine(manifest['Paths'], watchdog, manifest['Backend'])
    path = pathlib.Path(socket_path).absolute()
    sidecar = pathlib.Path(str(path) + '.capabilities.json')
    if os.path.lexists(path) or os.path.lexists(sidecar):
        raise ValueError('socket path already exists; refusing to replace another owner')
    path.parent.mkdir(parents=True, exist_ok=True)
    selector = selectors.DefaultSelector()
    server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    server.bind(str(path))
    os.chmod(path, 0o600)
    server.listen(4)
    server.setblocking(False)
    selector.register(server, selectors.EVENT_READ)
    clients = {}
    published = False
    try:
        write_capabilities(sidecar, manifest_bytes, [item['Asset'] for item in manifest['Paths']])
        published = True
        print(json.dumps({'Backend': engine.capability, 'PhysicalContinuous': False,
                          'Socket': str(path), 'Assets': len(engine.assets)}), flush=True)
        while True:
            for key, _ in selector.select(.001):
                if key.fileobj is server:
                    client, _ = server.accept()
                    if len(clients) >= 4:
                        client.close()
                        continue
                    client.setblocking(False)
                    clients[client] = bytearray()
                    selector.register(client, selectors.EVENT_READ)
                    continue
                client = key.fileobj
                try:
                    data = client.recv(COMMAND.size)
                    if not data:
                        raise ConnectionError('PLC bridge disconnected')
                    buf = clients[client]
                    buf.extend(data)
                    if len(buf) == COMMAND.size:
                        request, path_id, version, digest, scan, flags = COMMAND.unpack(buf)
                        buf.clear()
                        state = engine.command(client, request, path_id, version,
                            digest.decode('ascii'), scan, bool(flags & 1), bool(flags & 2), time.monotonic())
                        engine.tick(time.monotonic())
                        if state is not engine.feedback and state['ErrorID']:
                            result = state
                        else:
                            result = engine.feedback
                        payload = FEEDBACK.pack(result['RequestId'], result['Busy'], result['Done'], result['ErrorID'], result['Progress'])
                        if client.send(payload) != len(payload):
                            raise ConnectionError('bounded feedback write incomplete')
                except (OSError, ConnectionError, UnicodeError, ValueError):
                    engine.release(client)
                    selector.unregister(client)
                    clients.pop(client, None)
                    client.close()
            engine.tick(time.monotonic())
    finally:
        for client in clients:
            client.close()
        server.close()
        selector.close()
        path.unlink(missing_ok=True)
        if published:
            sidecar.unlink(missing_ok=True)

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', required=True)
    parser.add_argument('--socket', required=True, help='dedicated local socket; never an existing EcMotion endpoint')
    parser.add_argument('--watchdog', type=float, default=.25)
    args = parser.parse_args()
    def terminate(signum, frame):
        raise SystemExit(0)
    signal.signal(signal.SIGTERM, terminate)
    serve(args.manifest, args.socket, args.watchdog)
