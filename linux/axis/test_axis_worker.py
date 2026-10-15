import copy
import datetime
import json
import os
import socket
import stat
import struct
import tempfile
import threading
import unittest
from unittest import mock

import axis_worker as w


def manifest():
    return {'SchemaVersion': 1, 'InterfaceVersion': 'workbench-axis-v1',
            'PlcIntervalMilliseconds': 10, 'Axes': [dict(Index=1, Name='Axis1',
            ControllerAxis='Axis1', Unit='mm', MinPosition=-100, MaxPosition=100)]}


class Fake:
    def __init__(self, physical=False):
        self.calls = []
        self.cycle = 1
        self.fail_lease = False
        self.motion = 41
        self.physical = physical
        self.receipt = True
        self.axis = dict(name='Axis1', state='Standstill', actualPosition=0.,
                         actualVelocity=0., homed=False, fault=None, motionId=40,
                         motionBusy=False, motionDone=False, motionAborted=False)

    def request(self, kind, payload, expected=None):
        self.calls.append((kind, copy.deepcopy(payload)))
        if kind == 'HelloRequest':
            return dict(runtimeReady=True, protocolVersion=4, plcScanBatch=self.physical,
                        plcAbiVersion=3, plcActionReceipts=self.physical)
        if kind == 'ReadSnapshotRequest':
            self.cycle += 1
            return dict(runtimeReady=True, timestamp=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                        sampleCycle=self.cycle if self.physical else 0,
                        axes=[copy.deepcopy(self.axis)])
        if kind == 'AcquireControlRequest':
            if self.fail_lease:
                raise w.BackendError('control_busy')
            return dict(leaseToken='lease', grantedLeaseMilliseconds=1000)
        if kind == 'PlcScanBatchRequest':
            a = payload['actions'][0]
            return dict(accepted=True, actionReceipts=[dict(requestId=a['requestId'],
                        accepted=True, appliedCycle=self.cycle, motionId=0)] if self.receipt else [])
        return dict(accepted=True, motionId=self.motion if payload.get('command') == 'MoveAbsolute' else None)


def packet(scan=1, request=1, kind=1, position=0, velocity=1, flags=0):
    return w.HEADER.pack(scan, 1) + w.COMMAND.pack(request, kind, position, velocity, flags) + bytes(63*w.COMMAND.size)


class AxisTests(unittest.TestCase):
    def worker(self, physical=False):
        fake = Fake(physical)
        self.now = 1.
        worker = w.Worker(w.Manifest.from_object(manifest()), fake,
                          'EcMotion' if physical else 'EcMotionSimulator', now=lambda: self.now)
        worker.probe()
        return worker, fake

    def test_fixed_abi_and_no_startup_lease(self):
        worker, fake = self.worker()
        self.assertEqual(len(packet()), 2576)
        self.assertEqual(len(worker.feedback()), 5632)
        self.assertFalse(any(k == 'AcquireControlRequest' for k, p in fake.calls))
        self.assertFalse(worker.capabilities()['PhysicalBasicAxis'])

    def test_manifest_rejects_invalid_period_bounds_and_duplicates(self):
        for change in ('period', 'bounds', 'duplicate'):
            m = manifest()
            if change == 'period': m['PlcIntervalMilliseconds'] = 1001
            if change == 'bounds': m['Axes'][0]['MinPosition'] = float('nan')
            if change == 'duplicate': m['Axes'].append(copy.deepcopy(m['Axes'][0]))
            with self.assertRaises(ValueError): w.Manifest.from_object(m)

    def test_request_only_once_and_heartbeat_only_new_scan(self):
        worker, fake = self.worker()
        worker.scan(packet())
        worker.scan(packet())
        self.assertEqual(sum(k == 'AxisCommandRequest' for k, p in fake.calls), 1)
        self.assertEqual(sum(k == 'HeartbeatRequest' for k, p in fake.calls), 0)
        worker.scan(packet(scan=2))
        self.assertEqual(sum(k == 'HeartbeatRequest' for k, p in fake.calls), 1)

    def test_move_requires_actual_matching_motion_id_and_done(self):
        worker, fake = self.worker()
        worker.scan(packet(kind=5, position=10))
        fake.axis.update(motionId=99, motionDone=True, actualPosition=10)
        worker.poll()
        self.assertFalse(worker.slots[0].done)
        fake.axis.update(motionId=41)
        worker.poll()
        self.assertTrue(worker.slots[0].done)

    def test_jog_stays_busy_with_actual_velocity(self):
        worker, fake = self.worker()
        worker.scan(packet(kind=6, velocity=3))
        fake.axis.update(state='ContinuousMotion', actualVelocity=2.5)
        worker.poll()
        self.assertTrue(worker.slots[0].busy)
        self.assertFalse(worker.slots[0].done)
        self.assertEqual(w.FEEDBACK.unpack_from(worker.feedback())[10], 2.5)

    def test_physical_home_needs_applied_cycle_receipt(self):
        worker, fake = self.worker(True)
        fake.receipt = False
        worker.scan(packet(kind=4))
        fake.axis.update(homed=True)
        worker.poll()
        self.assertEqual(worker.slots[0].error, 2008)
        self.assertFalse(worker.slots[0].done)
        self.assertEqual([p['actions'][0]['kind'] for k,p in fake.calls if k=='PlcScanBatchRequest'], [3])

    def test_physical_home_applied_cycle_and_terminal_state(self):
        worker, fake = self.worker(True)
        worker.scan(packet(kind=4))
        fake.axis.update(homed=True)
        worker.poll()
        self.assertTrue(worker.slots[0].done)

    def test_ownership_refusal_is_latched(self):
        worker, fake = self.worker()
        fake.fail_lease = True
        worker.scan(packet())
        worker.scan(packet(scan=2, request=2))
        self.assertEqual(sum(k=='AcquireControlRequest' for k,p in fake.calls), 1)
        self.assertEqual(worker.slots[0].error, 2003)

    def test_watchdog_stops_owned_axis_releases_and_aborts(self):
        worker, fake = self.worker()
        worker.scan(packet(kind=6))
        self.now += 2.
        worker.tick()
        self.assertTrue(worker.slots[0].aborted)
        self.assertFalse(worker.slots[0].done)
        self.assertIn('ReleaseControlRequest', [k for k,p in fake.calls])
        self.assertIn('Stop', [p.get('command') for k,p in fake.calls])

    def test_stop_preempts_busy_axis(self):
        worker, fake = self.worker()
        worker.scan(packet(kind=6))
        worker.scan(packet(scan=2, request=2, kind=7, flags=1))
        self.assertEqual(worker.slots[0].request, 2)
        worker.scan(packet(scan=3, request=3, kind=5))
        self.assertEqual(worker.slots[0].error, 2006)
        self.assertEqual(sum(p.get('command')=='MoveAbsolute' for k,p in fake.calls), 0)

    def test_invalid_target_never_acquires(self):
        worker, fake = self.worker()
        worker.scan(packet(kind=5, position=101))
        self.assertEqual(worker.slots[0].error, 2007)
        self.assertFalse(any(k=='AcquireControlRequest' for k,p in fake.calls))

    def test_bad_frame_rejected(self):
        worker, fake = self.worker()
        with self.assertRaises(w.ProtocolError): worker.scan(b'x')

    def test_native_stale_sample_cycle_faults(self):
        worker, fake = self.worker(True)
        worker.scan(packet(kind=6))
        fake.cycle -= 1
        self.now += 2
        worker.poll()
        self.assertEqual(worker.slots[0].error, 2004)

    def test_request_after_watchdog_expiry_cannot_renew(self):
        worker, fake = self.worker()
        worker.scan(packet(kind=6))
        self.now += 2
        worker.scan(packet(scan=2, request=2, kind=5))
        self.assertFalse(any(k=='HeartbeatRequest' for k,p in fake.calls))
        self.assertEqual(worker.slots[0].error, 2002)

    def test_already_completed_old_motion_cannot_prove_new_move(self):
        worker, fake = self.worker(True)
        fake.axis.update(motionDone=True, motionId=41)
        worker.poll()
        worker.scan(packet(kind=5, position=10))
        worker.poll()
        self.assertFalse(worker.slots[0].done)
        self.assertEqual(worker.slots[0].error, 2008)

    def test_motion_abort_never_done(self):
        worker, fake = self.worker()
        worker.scan(packet(kind=5, position=10))
        fake.axis.update(motionId=41, motionAborted=True, motionDone=True)
        worker.poll()
        self.assertTrue(worker.slots[0].aborted)
        self.assertFalse(worker.slots[0].done)

    def test_idle_reads_publish_state_without_lease_and_stale_is_error(self):
        worker, fake = self.worker()
        self.assertEqual(w.FEEDBACK.unpack_from(worker.feedback())[5:8], (1, 0, 2))
        fake.axis['actualPosition'] = float('nan')
        worker.poll()
        self.assertEqual(worker.slots[0].error, 2004)
        self.assertFalse(any(k=='AcquireControlRequest' for k,p in fake.calls))

    def test_two_native_homes_same_local_request_get_distinct_receipts(self):
        self.now = 1.
        class TwoAxes(Fake):
            def request(inner, kind, payload, expected=None):
                result = super(TwoAxes,inner).request(kind,payload,expected)
                if kind=='ReadSnapshotRequest':
                    other = copy.deepcopy(result['axes'][0])
                    other['name'] = 'Axis2'
                    result['axes'].append(other)
                return result
        fake = TwoAxes(True)
        m = manifest()
        other = copy.deepcopy(m['Axes'][0])
        other.update(Index=2, Name='Axis2', ControllerAxis='Axis2')
        m['Axes'].append(other)
        worker = w.Worker(w.Manifest.from_object(m), fake, 'EcMotion', now=lambda: self.now)
        worker.probe()
        image = w.HEADER.pack(1,1) + w.COMMAND.pack(1,4,0,1,0)*2 + bytes(62*w.COMMAND.size)
        worker.scan(image)
        batches = [p for k,p in fake.calls if k=='PlcScanBatchRequest']
        self.assertEqual([p['actions'][0]['requestId'] for p in batches],[1,2])
        self.assertEqual([p['scanIndex'] for p in batches],[1,2])
        self.assertEqual([p['actions'][0]['axis'] for p in batches],[0,1])
        self.assertEqual(worker.slots[0].request, worker.slots[1].request)
        fake.axis['homed'] = True
        worker.poll()
        self.assertTrue(worker.slots[0].done and worker.slots[1].done)

    def test_manifest_standard_nullable_caps_and_strict_types(self):
        m = manifest()
        m['Axes'][0].update(MaximumVelocity=None, MaximumAcceleration=None,
                            MaximumDeceleration=None, MaximumJerk=None)
        w.Manifest.from_object(m)
        for field in ('MaximumVelocity','MaximumAcceleration','MaximumDeceleration','MaximumJerk'):
            for value in (float('nan'),-1,0,True,'1'):
                invalid = copy.deepcopy(m)
                invalid['Axes'][0][field] = value
                with self.assertRaises(ValueError): w.Manifest.from_object(invalid)
        for field,value in (('SchemaVersion',True),('PlcIntervalMilliseconds',1.5),('PlcIntervalMilliseconds',True)):
            invalid = manifest()
            invalid[field] = value
            with self.assertRaises(ValueError): w.Manifest.from_object(invalid)

    def test_manifest_identifier_lengths_and_case_insensitive_duplicates(self):
        for field,value in (('Name','bad-name'),('Name','1Axis'),('Name','a'*257),
                            ('ControllerAxis','c'*257),('Unit','u'*257)):
            m = manifest()
            m['Axes'][0][field] = value
            with self.assertRaises(ValueError): w.Manifest.from_object(m)
        for field,value in (('Name','axis1'),('ControllerAxis','axis1')):
            m = manifest()
            other = copy.deepcopy(m['Axes'][0])
            other.update(Index=2,Name='Axis2',ControllerAxis='Axis2')
            other[field] = value
            m['Axes'].append(other)
            with self.assertRaises(ValueError): w.Manifest.from_object(m)

    def test_standard_maximum_velocity_bounds_move_and_signed_jog(self):
        for kind,velocity in ((5,3),(6,3),(6,-3)):
            self.now = 1.
            m = manifest()
            m['Axes'][0]['MaximumVelocity'] = 2
            fake = Fake()
            worker = w.Worker(w.Manifest.from_object(m),fake,'EcMotionSimulator',now=lambda:self.now)
            worker.probe()
            worker.scan(packet(kind=kind,velocity=velocity))
            self.assertEqual(worker.slots[0].error,2007)
            self.assertFalse(any(k=='AcquireControlRequest' for k,p in fake.calls))

    def test_failed_read_revokes_capability_readiness(self):
        worker,fake = self.worker(True)
        self.assertTrue(worker.capabilities()['PhysicalBasicAxis'])
        fake.axis['actualVelocity'] = float('nan')
        worker.poll()
        self.assertFalse(worker.capabilities()['ControllerReady'])
        self.assertFalse(worker.capabilities()['PhysicalBasicAxis'])

    def test_reset_waits_through_intermediate_fault_until_confirmed_healthy(self):
        for terminal in ('Disabled','Standstill'):
            with self.subTest(terminal=terminal):
                worker,fake = self.worker(True)
                fake.axis.update(state='ErrorStop',fault='drive fault')
                worker.poll()
                worker.scan(packet(kind=3))
                worker.poll()
                self.assertTrue(worker.slots[0].busy)
                self.assertEqual(worker.slots[0].error,0)
                self.assertFalse(worker.slots[0].done)
                fake.axis.update(state=terminal,fault=None)
                worker.poll()
                self.assertTrue(worker.slots[0].done)
                self.assertFalse(worker.slots[0].busy)

    def test_reset_persistent_fault_fails_after_bounded_wait(self):
        worker,fake = self.worker(True)
        fake.axis.update(state='ErrorStop',fault='drive fault')
        worker.poll()
        worker.scan(packet(kind=3))
        self.now += .1
        worker.poll()
        self.assertTrue(worker.slots[0].busy)
        self.assertEqual(worker.slots[0].error,0)
        for scan in range(2,102):
            self.now += .1
            worker.scan(packet(scan=scan,kind=3))
            worker.poll()
        self.assertFalse(worker.slots[0].busy)
        self.assertFalse(worker.slots[0].done)
        self.assertEqual(worker.slots[0].error,2005)


class WireTests(unittest.TestCase):
    def test_response_identity_and_length_validation(self):
        for mode in ('identity', 'version', 'sequence', 'length'):
            client, server = socket.socketpair()
            def respond():
                raw = w.read_exact(server, struct.unpack('<I', w.read_exact(server,4))[0])
                request = json.loads(raw)
                request.update(kind='HelloResponse', payload={})
                if mode == 'identity': request['requestId'] = 'wrong'
                if mode == 'version': request['protocolVersion'] = 3
                if mode == 'sequence': request['sequence'] += 1
                data = json.dumps(request).encode()
                server.sendall(struct.pack('<I', 1048577 if mode=='length' else len(data)))
                if mode!='length': server.sendall(data)
                server.close()
            thread = threading.Thread(target=respond)
            thread.start()
            with self.assertRaises(w.ProtocolError): w.ProtocolClient(client).request('HelloRequest',{},'HelloResponse')
            client.close()
            thread.join()

    @unittest.skipUnless(os.name=='posix', 'Unix file ownership/mode contract')
    def test_capabilities_private_regular_file_and_symlink_refusal(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory,'cap.json')
            w.publish_capabilities(path,dict(Backend='EcMotionSimulator'))
            info = os.lstat(path)
            self.assertTrue(stat.S_ISREG(info.st_mode))
            self.assertEqual(stat.S_IMODE(info.st_mode),0o600)
            self.assertEqual(info.st_uid,os.getuid())
            w.publish_capabilities(path,dict(Backend='EcMotion'))
            with open(path) as stream:
                self.assertEqual(json.load(stream)['Backend'],'EcMotion')
            link = os.path.join(directory,'link.json')
            os.symlink(path,link)
            with self.assertRaises(ValueError): w.publish_capabilities(link,{})

    @unittest.skipUnless(os.name=='posix', 'Unix startup contract')
    def test_connection_failure_revokes_prior_sidecar(self):
        with tempfile.TemporaryDirectory() as directory:
            manifest_path = os.path.join(directory,'manifest.json')
            capability_path = os.path.join(directory,'cap.json')
            with open(manifest_path,'w') as stream:
                json.dump(manifest(),stream)
            w.publish_capabilities(capability_path,dict(ControllerReady=True,PhysicalBasicAxis=True))
            args = w.argparse.Namespace(manifest=manifest_path,socket=os.path.join(directory,'axis.sock'),
                controller_socket=os.path.join(directory,'missing.sock'),backend='EcMotion',capabilities=capability_path)
            with self.assertRaises(OSError): w.serve(args)
            with open(capability_path) as stream:
                capabilities = json.load(stream)
            self.assertFalse(capabilities['ControllerReady'])
            self.assertFalse(capabilities['PhysicalBasicAxis'])

    def test_truncated_response_fails(self):
        client, server = socket.socketpair()
        def respond():
            w.read_exact(server, struct.unpack('<I', w.read_exact(server,4))[0])
            server.sendall(struct.pack('<I',100)+b'{}')
            server.close()
        thread = threading.Thread(target=respond)
        thread.start()
        with self.assertRaises(w.ProtocolError): w.ProtocolClient(client).request('HelloRequest',{},'HelloResponse')
        client.close()
        thread.join()

    @unittest.skipUnless(os.name=='posix', 'Unix private stream server')
    def test_private_stream_one_response_per_scan_idle_refresh_and_period_independence(self):
        for period in (1,1000):
            with self.subTest(period=period), tempfile.TemporaryDirectory() as directory:
                controller_path = os.path.join(directory,'controller.sock')
                bridge_path = os.path.join(directory,'bridge.sock')
                manifest_path = os.path.join(directory,'manifest.json')
                m = manifest()
                m['PlcIntervalMilliseconds'] = period
                with open(manifest_path,'w') as stream: json.dump(m,stream)
                listener = socket.socket(socket.AF_UNIX,socket.SOCK_STREAM)
                listener.bind(controller_path)
                listener.listen(1)
                fake = Fake()
                args = w.argparse.Namespace(manifest=manifest_path,socket=bridge_path,
                    controller_socket=controller_path,backend='EcMotionSimulator',capabilities=None)
                failures=[]
                def start():
                    try: w.serve(args)
                    except (w.ProtocolError,OSError) as error: failures.append(error)
                with mock.patch.object(w,'ProtocolClient',return_value=fake):
                    thread = threading.Thread(target=start)
                    thread.start()
                    peer,_ = listener.accept()
                    deadline = w.time.monotonic()+2
                    while not os.path.exists(bridge_path) and w.time.monotonic()<deadline: w.time.sleep(.001)
                    bridge = socket.socket(socket.AF_UNIX,socket.SOCK_STREAM)
                    try:
                        bridge.connect(bridge_path)
                        capability_path = bridge_path+'.capabilities.json'
                        first_stamp = os.stat(capability_path).st_mtime_ns
                        bridge.settimeout(.04)
                        # No unsolicited feedback, even while status and sidecar refresh.
                        with self.assertRaises(socket.timeout): bridge.recv(1)
                        self.assertGreater(os.stat(capability_path).st_mtime_ns,first_stamp)
                        self.assertGreater(sum(k=='ReadSnapshotRequest' for k,p in fake.calls),1)
                        self.assertFalse(any(k=='AcquireControlRequest' for k,p in fake.calls))
                        bridge.settimeout(.05)
                        reads_before = sum(k=='ReadSnapshotRequest' for k,p in fake.calls)
                        for scan in range(1,31):
                            bridge.sendall(packet(scan=scan,request=0))
                            feedback = w.FEEDBACK.unpack_from(w.read_exact(bridge,w.FEEDBACK_BYTES))
                            self.assertEqual(feedback[0],0)
                            self.assertFalse(feedback[3])
                            w.time.sleep(.001)
                        reads_after = sum(k=='ReadSnapshotRequest' for k,p in fake.calls)
                        self.assertLess(reads_after-reads_before,20)
                        for scan in range(31,61):
                            bridge.sendall(packet(scan=scan,request=scan))
                            feedback = w.FEEDBACK.unpack_from(w.read_exact(bridge,w.FEEDBACK_BYTES))
                            self.assertEqual(feedback[0],scan)
                            self.assertFalse(feedback[3])
                            w.time.sleep(.001)
                        with self.assertRaises(socket.timeout): bridge.recv(1)
                    finally:
                        bridge.close()
                        thread.join(timeout=2)
                        peer.close()
                        listener.close()
                    self.assertFalse(thread.is_alive())
                    self.assertTrue(failures)


if __name__ == '__main__': unittest.main()
