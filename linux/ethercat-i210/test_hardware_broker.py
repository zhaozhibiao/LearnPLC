# SPDX-License-Identifier: MIT
import importlib.util
import hashlib
import json
import pathlib
import socket
import struct
import types
import tempfile
import unittest
from unittest.mock import Mock, patch

path = pathlib.Path(__file__).with_name('hardware-broker.py')
spec = importlib.util.spec_from_file_location('hardware_broker', path)
broker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(broker)


class BrokerTests(unittest.TestCase):
    def request(self, action='apply', **extra):
        return dict(protocolVersion=1, requestId='request_1', action=action,
                    **({} if action == 'status' else {'buildHash': 'A'*64}), **extra)

    def test_strict_actions_schema_and_identity(self):
        for request in (self.request('shell'), self.request(command='reboot'), self.request(path='/tmp/config'),
                        dict(self.request(), buildHash='../'), dict(self.request(), requestId=''),
                        dict(self.request(), protocolVersion=True), dict(self.request(), administrative_password='secret')):
            with self.assertRaises(ValueError): broker.validate_request(request)
        self.assertEqual(broker.validate_request(self.request())['buildHash'], 'A'*64)
        broker.validate_request(self.request('status'))

    def test_kernel_credentials_and_named_group_required(self):
        stream = Mock()
        stream.getsockopt.return_value = struct.pack('3i', 123, 1000, 1000)
        with patch.object(broker, 'authorized_uid', return_value=False):
            with self.assertRaises(PermissionError): broker.peer_identity(stream)
        with patch.object(broker, 'authorized_uid', return_value=True):
            self.assertEqual(broker.peer_identity(stream), 1000)
        self.assertTrue(stream.getsockopt.called)

    def test_authorization_precedes_any_request_read(self):
        stream = Mock()
        with patch.object(broker, 'peer_identity', side_effect=PermissionError('denied')):
            broker.handle_connection(stream, Mock())
        stream.recv.assert_not_called()

    def test_status_does_not_apply_or_read_user_selected_paths(self):
        helper = Mock()
        helper.plc_status.return_value = 'STOPPED'
        helper.native_request.side_effect = FileNotFoundError('not running')
        service = broker.Broker(helper)
        result = service.dispatch(self.request('status'), 1000)
        self.assertTrue(result['success'])
        self.assertEqual(result['result']['plcState'], 'STOPPED')
        self.assertFalse(result['result']['nativeReady'])
        helper.apply.assert_not_called()
        helper.check_bundle.assert_not_called()

    def test_apply_consumes_id_even_on_failure_without_retry(self):
        helper = Mock()
        helper.apply.side_effect = ValueError('PLC must be STOPPED')
        service = broker.Broker(helper)
        with self.assertRaises(ValueError): service.dispatch(self.request(), 1000)
        with self.assertRaisesRegex(ValueError, 'consumed'): service.dispatch(self.request(), 1000)
        self.assertEqual(helper.apply.call_count, 1)

    def test_validate_fixed_candidate_and_apply_result_identity(self):
        helper = Mock()
        helper.CANDIDATES = pathlib.Path('/fixed/versions')
        helper.check_bundle.return_value = {'status':'validated', 'buildHash':'a'*64}
        helper.required_runtime_capabilities.return_value = []
        helper.read_regular.return_value = b'{}'
        helper.parse_json.return_value = {}
        helper.apply.return_value = {'status':'applied', 'buildHash':'a'*64, 'verified':True}
        service = broker.Broker(helper)
        self.assertTrue(service.dispatch(self.request('validate'), 1000)['success'])
        helper.check_bundle.assert_called_once_with(pathlib.Path('/fixed/versions') / ('A'*64), {})
        response = service.dispatch(self.request(), 1000)
        self.assertEqual(response['requestId'], 'request_1')
        self.assertEqual(response['protocolVersion'], 1)
        helper.apply.assert_called_once_with('A'*64)

    def test_validate_rejects_unsupported_v2_pinned_core_without_any_mutation(self):
        spec=importlib.util.spec_from_file_location('validate_root_helper',pathlib.Path(__file__).with_name('apply-runtime-hardware.py'))
        helper=importlib.util.module_from_spec(spec);spec.loader.exec_module(helper)
        with tempfile.TemporaryDirectory() as directory:
            base=pathlib.Path(directory);generated=base/('A'*64)/'core'/'generated';generated.mkdir(parents=True)
            manifest=dict(AxisInterfaceVersion='workbench-axis-v2',AxisExportMarker='workbench_axis_v2',
                          AxisCommandBytes=4112,AxisFeedbackBytes=6656,
                          RequiredRuntimeCapabilities=['workbench_axis_move_profile_v2','workbench_axis_velocity_profile_v2'])
            (generated/'build-manifest.json').write_text(json.dumps(manifest));(generated/'machine-config.json').write_text('{}')
            binary=base/'binary';binary.write_bytes(b'pinned')
            installation=base/'installation.json'
            installation.write_text(json.dumps(dict(binary=str(binary),binarySha256=hashlib.sha256(b'pinned').hexdigest())))
            proof=dict(status='validated',buildHash='a'*64)
            reply=types.SimpleNamespace(stdout=json.dumps(dict(runtimeCapabilities=['workbench_motion_migration_v1','workbench_axis_trace_v1'])))
            with patch.object(helper,'CANDIDATES',base),patch.object(helper,'INSTALLATION',installation), \
                 patch.object(helper,'root_owned') as trusted,patch.object(helper,'check_bundle',return_value=proof), \
                 patch.object(helper,'command',return_value=reply) as command,patch.object(helper,'apply') as apply:
                with self.assertRaisesRegex(ValueError,'capability'):broker.Broker(helper).dispatch(self.request('validate'),1000)
                command.assert_called_once_with([str(binary),'--runtime-capabilities']);apply.assert_not_called()
                trusted.assert_any_call(installation,0o600);trusted.assert_any_call(binary)
                binary.write_bytes(b'tampered');command.reset_mock()
                with self.assertRaisesRegex(ValueError,'checksum'):broker.Broker(helper).dispatch(self.request('validate'),1000)
                command.assert_not_called();apply.assert_not_called()
                binary.write_bytes(b'pinned')
                reply.stdout=json.dumps(dict(runtimeCapabilities=['workbench_motion_migration_v1','workbench_axis_trace_v1',
                    'workbench_axis_move_profile_v2','workbench_axis_velocity_profile_v2']))
                self.assertTrue(broker.Broker(helper).dispatch(self.request('validate'),1000)['success'])
                command.assert_called_once_with([str(binary),'--runtime-capabilities']);apply.assert_not_called()
                trusted.side_effect=ValueError('not root controlled');command.reset_mock()
                with self.assertRaisesRegex(ValueError,'root controlled'):broker.Broker(helper).dispatch(self.request('validate'),1000)
                command.assert_not_called();apply.assert_not_called()

    def test_old_broker_helper_rejects_v2_validation(self):
        helper=types.SimpleNamespace(read_regular=lambda _:b'{}',
            parse_json=lambda _:dict(AxisInterfaceVersion='workbench-axis-v2'),check_runtime_capabilities=Mock())
        with self.assertRaisesRegex(ValueError,'capabil'):
            broker.check_pinned_candidate_runtime(helper,pathlib.Path('/candidate'),{})
        helper.check_runtime_capabilities.assert_not_called()

    def test_reject_duplicate_json_keys_and_oversize_frame(self):
        stream = Mock()
        stream.recv.return_value = struct.pack('<I', broker.MAX_REQUEST + 1)
        with self.assertRaises(ValueError): broker.read_request(stream)
        body = b'{"action":"status","action":"apply"}'
        stream.recv.side_effect = [struct.pack('<I', len(body)), body]
        with self.assertRaises(ValueError): broker.read_request(stream)

    def test_apply_failure_response_is_not_success(self):
        helper = Mock()
        helper.apply.return_value = {'status':'failed', 'nativeState':'stopped', 'error':'not ready'}
        response = broker.Broker(helper).dispatch(self.request(), 1000)
        self.assertFalse(response['success'])
        self.assertEqual(response['result']['nativeState'], 'stopped')

    def test_consumed_apply_survives_broker_restart(self):
        with tempfile.TemporaryDirectory() as directory:
            helper = Mock()
            helper.apply.return_value = {'status':'applied', 'verified':True}
            ledger = pathlib.Path(directory) / 'requests.json'
            broker.Broker(helper, ledger=ledger).dispatch(self.request(), 1000)
            with self.assertRaisesRegex(ValueError, 'consumed'):
                broker.Broker(helper, ledger=ledger).dispatch(self.request(), 1000)
            self.assertEqual(helper.apply.call_count, 1)

    def test_no_apply_when_durable_request_record_fails(self):
        with tempfile.TemporaryDirectory() as directory:
            helper = Mock()
            service = broker.Broker(helper, ledger=pathlib.Path(directory) / 'requests.json')
            with patch.object(broker.os, 'replace', side_effect=OSError('disk full')):
                with self.assertRaises(OSError): service.dispatch(self.request(), 1000)
            helper.apply.assert_not_called()

    def test_loads_only_fixed_root_controlled_helper(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / 'fixed-helper'
            path.write_text('MARKER = 1\n')
            info = types.SimpleNamespace(st_uid=0, st_mode=0o100644)
            with patch.object(broker, 'HELPER', path), patch.object(pathlib.Path, 'lstat', return_value=info):
                self.assertEqual(broker.load_helper().MARKER, 1)
            info.st_uid = 1000
            with patch.object(broker, 'HELPER', path), patch.object(pathlib.Path, 'lstat', return_value=info):
                with self.assertRaises(ValueError): broker.load_helper()


if __name__ == '__main__': unittest.main()
