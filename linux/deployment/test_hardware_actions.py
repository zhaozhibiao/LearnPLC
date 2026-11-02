"""MIT. Coordinator action routing; fake Runtime only."""
from unittest import mock
import unittest
import hashlib
import test_coordinator as fixtures
import coordinator


class HardwareActions(unittest.TestCase):
    setUp = fixtures.Tests.setUp
    tearDown = fixtures.Tests.tearDown
    request = fixtures.Tests.request
    package = fixtures.Tests.package

    def test_runtime_files_require_explicit_interface_marker(self):
        path, checksum, build = self.package({'ethercat-runtime.json': b'{}'})
        with self.assertRaisesRegex(coordinator.DeploymentError, 'EtherCAT'):
            coordinator.validate_archive(path, checksum, build)
    def test_hardware_validate_routes_without_start_or_root_action(self):
        with mock.patch.object(coordinator.ethercat_hardware, 'check_bundle', return_value={'topologyFingerprint': 'f'*64}):
            result = self.request('hardware-validate', build_hash='A'*64)
        self.assertEqual(result['state'], 'hardware-validated')
        self.assertTrue(result['success'])
        self.assertNotIn(b'START\n', self.daemon.commands)

    def test_hardware_apply_refuses_running_before_privileged_helper(self):
        self.daemon.state = 'RUNNING'
        with mock.patch.object(coordinator.ethercat_hardware, 'check_bundle', return_value={}), \
             mock.patch.object(coordinator.ethercat_hardware, 'apply_bundle') as apply:
            with self.assertRaisesRegex(coordinator.DeploymentError, 'STOPPED'):
                self.request('hardware-apply', build_hash='A'*64)
            apply.assert_not_called()

    def test_start_refuses_old_active_program_after_hardware_change(self):
        library = self.runtime / 'build' / 'libplc_existing.so'
        library.write_bytes(b'old-application')
        active = dict(build_hash='B'*64, library_path=str(library), library_sha256=hashlib.sha256(library.read_bytes()).hexdigest())
        coordinator.atomic_json(self.coordinator.active_path, active)
        coordinator.atomic_json(self.coordinator.prefix / 'hardware-active.json', dict(topologyFingerprint='f'*64))
        with self.assertRaisesRegex(coordinator.DeploymentError, 'hardware|Hardware'):
            self.request('start', expected_active_build_hash='B'*64)
        self.assertNotIn(b'START\n', self.daemon.commands)


class VersionedHardwareActions(HardwareActions):
    def test_v2_preflight_failure_restores_both_bridges_only_on_same_core(self):
        (self.runtime / 'build' / 'libplc_existing.so').write_bytes(b'baseline')
        self.request('adopt-baseline')
        from test_axis_gate_v2 import manifest
        import json
        self.coordinator.config.update(axis_backend='EcMotion',
            axis_socket_path='/home/a/.local/share/st-workbench/is620n-axis/axis.sock',
            axis_v2_socket_path='/home/a/.local/share/st-workbench/axis-v2/axis.sock')
        self.coordinator.version = lambda build: self.root
        self.coordinator.native_core_pid = lambda: 101
        raw = json.dumps(manifest()).encode()
        def rejected(*args, **kwargs):
            kwargs['before_apply']()
            raise ValueError('pinned core preflight rejected')
        with mock.patch.object(coordinator.scoped_transfer,'inventory',return_value=(self.root,{'Files':{'axis-manifest.json':'hash'}})), \
             mock.patch.object(coordinator.ethercat_hardware,'check_bundle',return_value={}), \
             mock.patch.object(coordinator.ethercat_hardware,'read_regular',return_value=raw), \
             mock.patch.object(coordinator.ethercat_hardware,'apply_bundle',side_effect=rejected), \
             mock.patch.object(coordinator.subprocess,'run',return_value=mock.Mock(returncode=0)) as run:
            with self.assertRaisesRegex(coordinator.DeploymentError,'preflight rejected'):
                self.request('hardware-apply',build_hash='A'*64,expected_active_build_hash=self.coordinator.active()['build_hash'])
            actions = [call.args[0][2:] for call in run.call_args_list]
            for unit in ['stworkbench-is620n-axis.service','stworkbench-axis-v2.service']:
                self.assertIn(['stop',unit],actions)
                self.assertIn(['start',unit],actions)

    def test_v2_channel_must_be_explicit_before_stopping_bridge(self):
        (self.runtime / 'build' / 'libplc_existing.so').write_bytes(b'baseline')
        self.request('adopt-baseline')
        from test_axis_gate_v2 import manifest
        import json
        self.coordinator.version = lambda build: self.root
        self.coordinator.native_core_pid = lambda: 101
        with mock.patch.object(coordinator.scoped_transfer,'inventory',return_value=(self.root,{'Files':{'axis-manifest.json':'hash'}})), \
             mock.patch.object(coordinator.ethercat_hardware,'check_bundle',return_value={}), \
             mock.patch.object(coordinator.ethercat_hardware,'read_regular',return_value=json.dumps(manifest()).encode()), \
             mock.patch.object(coordinator.ethercat_hardware,'apply_bundle') as apply, \
             mock.patch.object(coordinator.subprocess,'run',return_value=mock.Mock(returncode=0)) as run:
            with self.assertRaisesRegex(coordinator.DeploymentError,'separate v2'):
                self.request('hardware-apply',build_hash='A'*64,expected_active_build_hash=self.coordinator.active()['build_hash'])
            apply.assert_not_called()
            self.assertFalse(any('stop' in call.args[0] for call in run.call_args_list))
