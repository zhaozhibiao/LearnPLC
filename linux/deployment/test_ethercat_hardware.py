import hashlib
import json
import pathlib
import tempfile
import unittest
from unittest.mock import patch
import ethercat_hardware as hw


def fixture(directory):
    version = pathlib.Path(directory) / ('a' * 64)
    generated = version / 'core' / 'generated'
    generated.mkdir(parents=True)
    files = {}
    for name in ('machine-config.json', 'io-map.json'):
        content = b'{}'
        (generated / name).write_bytes(content)
        files[name] = hashlib.sha256(content).hexdigest()
    topology = dict(schemaVersion=1, readyForRuntime=True, blockers=[], cycleTimeNs=1000000,
                    slaves=[dict(position=0)], machineSha256=files['machine-config.json'], ioMapSha256=files['io-map.json'])
    topology['topologyFingerprint'] = hashlib.sha256(hw.canonical_identity(topology)).hexdigest()
    content = json.dumps(topology).encode()
    (generated / 'ethercat-runtime.json').write_bytes(content)
    files['ethercat-runtime.json'] = hashlib.sha256(content).hexdigest()
    (generated / 'build-manifest.json').write_text(json.dumps(dict(BuildHash=version.name, Files=files)))
    return version, generated, topology


class HardwareTests(unittest.TestCase):
    def test_runtime_feature_requirements_fail_closed(self):
        trace = {'requiredRuntimeCapabilities': ['workbench_motion_migration_v1', 'workbench_axis_trace_v1']}
        hw.verify_runtime_capabilities(trace, {'runtimeCapabilities': trace['requiredRuntimeCapabilities']})
        with self.assertRaises(ValueError): hw.verify_runtime_capabilities(trace, {'runtimeCapabilities': ['workbench_motion_migration_v1']})
        required = {'requiredRuntimeCapabilities': ['workbench_motion_migration_v1']}
        hw.verify_runtime_capabilities({}, {})
        hw.verify_runtime_capabilities(required, {'runtimeCapabilities': ['workbench_motion_migration_v1']})
        for capability in ({}, {'runtimeCapabilities': 'workbench_motion_migration_v1'},
                           {'runtimeCapabilities': [True]}, {'runtimeCapabilities': []}):
            with self.assertRaises(ValueError): hw.verify_runtime_capabilities(required, capability)
        for value in (None, 'workbench_motion_migration_v1', [True], ['unknown_feature']):
            with self.assertRaises(ValueError): hw.verify_runtime_capabilities({'requiredRuntimeCapabilities': value}, {})

    def test_ordinary_user_apply_uses_broker_without_sudo(self):
        with tempfile.TemporaryDirectory() as directory:
            version, _, _ = fixture(directory)
            checked = hw.check_bundle(version, {})
            events = []
            def reply(action, build_hash=None, request_id=None):
                events.append(action)
                return dict(checked, status='applied' if action == 'apply' else 'validated', verified=True)
            with patch.object(hw, 'broker_request', side_effect=reply):
                result = hw.apply_bundle(version, {}, before_apply=lambda: events.append('stop-bridge'), request_id='apply1')
            self.assertTrue(result['verified'])
            self.assertEqual(events, ['validate', 'stop-bridge', 'apply'])

    def test_broker_failure_does_not_stop_bridge_or_retry(self):
        with tempfile.TemporaryDirectory() as directory:
            version, _, _ = fixture(directory)
            before = unittest.mock.Mock()
            with patch.object(hw, 'broker_request', side_effect=OSError('not installed')) as send:
                with self.assertRaises(OSError): hw.apply_bundle(version, {}, before_apply=before)
                before.assert_not_called()
                self.assertEqual(send.call_count, 1)

    def test_broker_rejects_mismatched_hardware_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            version, _, _ = fixture(directory)
            wrong = dict(hw.check_bundle(version, {}), topologyFingerprint='f'*64)
            with patch.object(hw, 'broker_request', return_value=wrong):
                with self.assertRaisesRegex(ValueError, 'identity'): hw.apply_bundle(version, {})

    def test_broker_protocol_rejects_nonroot_server_wrong_id_and_failure(self):
        class BrokerSocket:
            uid = 0
            wrong_id = False
            success = True
            def __enter__(self): return self
            def __exit__(self, *args): pass
            def settimeout(self, value): pass
            def connect(self, endpoint): self.endpoint = endpoint
            def getsockopt(self, *args): return hw.struct.pack('3i', 1, self.uid, 0)
            def sendall(self, data):
                r = json.loads(data[4:])
                response = dict(protocolVersion=1, requestId='wrong' if self.wrong_id else r['requestId'],
                    success=self.success, result={'status':'validated'}, message='rejected')
                raw = json.dumps(response).encode()
                self.data = hw.struct.pack('<I', len(raw)) + raw
            def recv(self, count):
                value,self.data = self.data[:count],self.data[count:]
                return value
        for uid, wrong, success in [(1000,False,True),(0,True,True),(0,False,False)]:
            stream = BrokerSocket()
            stream.uid,stream.wrong_id,stream.success = uid,wrong,success
            with patch.object(hw.socket, 'AF_UNIX', 1, create=True), patch.object(hw.socket, 'socket', return_value=stream):
                with self.assertRaises((ValueError,PermissionError)): hw.broker_request('validate', 'a'*64)
        with patch.object(hw.socket, 'AF_UNIX', 1, create=True), patch.object(hw.socket, 'socket', return_value=BrokerSocket()):
            self.assertEqual(hw.broker_request('validate', 'a'*64)['status'], 'validated')

    def test_validates_inventory_hashes_and_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            version, generated, topology = fixture(directory)
            self.assertEqual(hw.check_bundle(version, {})['topologyFingerprint'], topology['topologyFingerprint'])
            (generated / 'machine-config.json').write_text('{"changed":true}')
            with self.assertRaises(ValueError): hw.check_bundle(version, {})

    def test_native_protocol_checks_response_identity(self):
        class FakeSocket:
            def __enter__(self): return self
            def __exit__(self, *args): pass
            def settimeout(self, timeout): pass
            def connect(self, endpoint): pass
            def sendall(self, frame):
                request = json.loads(frame[4:])
                self.request = request
                response = dict(request, kind='HelloResponse', payload={'ethercatTopologyFingerprint':'a'*64})
                data = json.dumps(response).encode()
                self.response = hw.struct.pack('<I', len(data)) + data
            def recv(self, count):
                result, self.response = self.response[:count], self.response[count:]
                return result
        fake = FakeSocket()
        with patch.object(hw.socket, 'AF_UNIX', 1, create=True), patch.object(hw.socket, 'socket', return_value=fake):
            reply = hw.native_request('/run/private.sock', 'HelloRequest', {}, 'HelloResponse')
            self.assertEqual(reply['ethercatTopologyFingerprint'], 'a'*64)
            self.assertEqual(fake.request['kind'], 'HelloRequest')
        with patch.object(hw.socket, 'AF_UNIX', 1, create=True), patch.object(hw.socket, 'socket', return_value=FakeSocket()):
            with self.assertRaises(ValueError):
                hw.native_request('/run/private.sock', 'HelloRequest', {}, 'SnapshotResponse')

    def test_no_arbitrary_command_or_build_path(self):
        with tempfile.TemporaryDirectory() as directory:
            version, _, _ = fixture(directory)
            with patch.object(hw, 'broker_request') as run:
                checked = hw.check_bundle(version, {})
                run.side_effect = [checked, dict(checked, status='applied', verified=True)]
                hw.apply_bundle(version, {'apply_command': '/tmp/evil'})
                self.assertEqual(run.call_args.args, ('apply', version.name))
                self.assertNotIn('/tmp/evil', str(run.call_args))
            with self.assertRaises(ValueError): hw.check_bundle(version.parent, {})

    def test_live_identity_must_match_all_bindings(self):
        with tempfile.TemporaryDirectory() as directory:
            _, generated, topology = fixture(directory)
            hello = dict(runtimeReady=True, ethercatTopologyFingerprint=topology['topologyFingerprint'],
                         machineSha256=topology['machineSha256'], ioMapSha256=topology['ioMapSha256'])
            with patch.object(hw, 'native_request', side_effect=[hello, {'runtimeReady':True,'ioReady':True,'sampleCycle':1,'sampleMonotonicNs':100}, {'runtimeReady':True,'ioReady':True,'sampleCycle':2,'sampleMonotonicNs':200}]):
                self.assertTrue(hw.verify_applied(generated, {})['verified'])
            hello['ethercatTopologyFingerprint'] = '0' * 64
            with patch.object(hw, 'native_request', return_value=hello):
                with self.assertRaises(ValueError): hw.verify_applied(generated, {})

    def test_rejects_static_or_unready_native_proof(self):
        with tempfile.TemporaryDirectory() as directory:
            _, generated, topology = fixture(directory)
            hello = dict(runtimeReady=True, ethercatTopologyFingerprint=topology['topologyFingerprint'])
            sample = dict(runtimeReady=True, ioReady=True, sampleCycle=10, sampleMonotonicNs=100)
            with patch.object(hw, 'native_request', side_effect=[hello,sample,sample]):
                with self.assertRaises(ValueError): hw.verify_applied(generated, {})
            hello['runtimeReady'] = False
            with patch.object(hw, 'native_request', return_value=hello):
                with self.assertRaises(ValueError): hw.verify_applied(generated, {})

    def test_refuses_symlink_and_nonready_and_fake_fingerprint(self):
        with tempfile.TemporaryDirectory() as directory:
            version, generated, topology = fixture(directory)
            topology['topologyFingerprint'] = '0' * 64
            (generated / 'ethercat-runtime.json').write_text(json.dumps(topology))
            with self.assertRaises(ValueError): hw.check_bundle(version, {})
            topology['readyForRuntime'] = False
            with self.assertRaises(ValueError): hw.validate_topology(topology, {})


if __name__ == '__main__': unittest.main()
