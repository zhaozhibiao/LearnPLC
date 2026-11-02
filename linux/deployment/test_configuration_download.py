"""MIT. Unified configuration planning/application; all device calls are fixtures."""
import contextlib
import hashlib
import json
import pathlib
import tempfile
import unittest
from unittest import mock
import coordinator
import ethercat_hardware as hw
from test_ethercat_hardware import fixture
import test_coordinator as coordinator_fixtures


class ConfigurationDownloadTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.version, self.generated, self.topology = fixture(self.temp.name)
        self.build = self.version.name.upper()
        upper = self.version.with_name(self.build)
        self.version.rename(upper)
        self.version, self.generated = upper, upper / 'core' / 'generated'
        machine = dict(axes=[dict(name='Axis1', motionCompletion=dict(positionTolerance=.0001, velocityTolerance=.0005,
                            dwellMilliseconds=100, positionStabilityCompletion=True))])
        (self.generated / 'machine-config.json').write_text(json.dumps(machine))
        self.topology['machineSha256'] = hashlib.sha256((self.generated / 'machine-config.json').read_bytes()).hexdigest()
        self.topology['topologyFingerprint'] = hashlib.sha256(hw.canonical_identity(self.topology)).hexdigest()
        (self.generated / 'ethercat-runtime.json').write_text(json.dumps(self.topology))
        self.axis = dict(SchemaVersion=1, InterfaceVersion='workbench-axis-v1', PlcIntervalMilliseconds=10,
            Axes=[dict(Index=1, Name='X', ControllerAxis='Axis1', Unit='rev', MinPosition=-2, MaxPosition=2,
                MaximumVelocity=.01, MaximumAcceleration=.01, MaximumDeceleration=.01, MaximumJerk=.01)])
        self.raw = json.dumps(self.axis).encode()
        self.manifest = json.loads((self.generated / 'build-manifest.json').read_text())
        for name in ('machine-config.json','ethercat-runtime.json'):
            self.manifest['Files'][name] = hashlib.sha256((self.generated / name).read_bytes()).hexdigest()
        self.manifest.update(BuildHash=self.build, EtherCatRuntimeInterfaceVersion='workbench-ethercat-v1', AxisInterfaceVersion='workbench-axis-v1')
        self.manifest['Files']['axis-manifest.json'] = hashlib.sha256(self.raw).hexdigest()
        (self.generated / 'axis-manifest.json').write_bytes(self.raw)
        (self.generated / 'build-manifest.json').write_text(json.dumps(self.manifest))
        (self.version / 'stage-result.json').write_text(json.dumps(dict(state='staged', build_hash=self.build, manifest=self.manifest)))
        library = self.version / 'build' / 'new_libplc.so'
        library.parent.mkdir()
        library.write_bytes(b'compiled library')
        self.compiled = dict(state='compiled', build_hash=self.build, exports_verified=True,
                             library_sha256=hashlib.sha256(library.read_bytes()).hexdigest(), axis_ready=False)
        (self.version / 'compile-result.json').write_text(json.dumps(self.compiled))
        self.config = dict(axis_backend='EcMotion', axis_socket_path='/home/a/.local/share/st-workbench/is620n-axis/axis.sock')
        self.hello = dict(runtimeReady=True, ioReady=True, ethercatTopologyFingerprint=self.topology['topologyFingerprint'])
        self.sample = dict(runtimeReady=True, ioReady=True, outputsAtConfiguredSafeState=True, sampleCycle=20, sampleMonotonicNs=200,
                           axes=[dict(name='Axis1', state='Disabled', fault=None, motionBusy=False, actualPosition=0., actualVelocity=0.)])

    def module(self):
        import configuration_download
        return configuration_download

    @contextlib.contextmanager
    def device(self, digest=None, fingerprint=None):
        m = self.module()
        hello = dict(self.hello)
        if fingerprint is not None: hello['ethercatTopologyFingerprint'] = fingerprint
        samples = 0
        def native(endpoint, kind, payload, expected):
            nonlocal samples
            if kind == 'HelloRequest': return hello
            samples += 1
            return dict(self.sample, sampleCycle=20+samples, sampleMonotonicNs=200+samples*100000000)
        with mock.patch.object(hw, 'native_request', side_effect=native), \
             mock.patch.object(m, 'worker_digest', return_value=digest or hashlib.sha256(self.raw).hexdigest()), \
             mock.patch.object(m, 'runtime_environment', return_value='STWORKBENCH_AXIS_BACKEND=EcMotion STWORKBENCH_AXIS_SOCKET=' + self.config['axis_socket_path']), \
             mock.patch.object(m.time, 'sleep'):
            yield m

    def test_plan_none_axis_hardware_is_readonly_and_exact(self):
        for expected, digest, fingerprint in [('none', None, None), ('axis', '0'*64, None), ('hardware', None, 'f'*64)]:
            with self.device(digest, fingerprint) as m, mock.patch.object(m, 'service') as service, mock.patch.object(hw, 'broker_request') as broker:
                plan = m.plan(self.version, self.config)
                self.assertEqual(expected, plan['kind'])
                self.assertEqual(self.build, plan['build_hash'])
                self.assertRegex(plan['identity'], '^[0-9a-f]{64}$')
                service.assert_not_called()
                broker.assert_not_called()

    def test_plan_rejects_unverified_compilation_and_mutated_inventory(self):
        with self.device() as m:
            (self.generated / 'axis-manifest.json').write_bytes(b'{}')
            with self.assertRaisesRegex(ValueError, 'inventory|Inventory'): m.plan(self.version, self.config)
            (self.generated / 'axis-manifest.json').write_bytes(self.raw)
            (self.version / 'compile-result.json').write_text('{}')
            with self.assertRaisesRegex(ValueError, 'compil'): m.plan(self.version, self.config)

    def test_unknown_native_readiness_does_not_become_hardware_plan(self):
        with self.device() as m:
            self.hello['runtimeReady'] = False
        with self.device() as m, mock.patch.object(hw, 'broker_request') as broker:
            with self.assertRaisesRegex(ValueError, 'ready'): m.plan(self.version, self.config)
            broker.assert_not_called()

    def test_plan_identity_changes_with_kind(self):
        with self.device() as m: before = m.plan(self.version, self.config)
        with self.device('0'*64) as m: after = m.plan(self.version, self.config)
        self.assertNotEqual(before['identity'], after['identity'])

    def test_axis_change_requires_disabled_stationary_feedback(self):
        for changes in [dict(state='Standstill'), dict(motionBusy=True), dict(fault='overspeed'), dict(state='Unknown')]:
            with self.device('0'*64) as m:
                self.sample['axes'] = [dict(name='Axis1', state='Disabled', fault=None, motionBusy=False, actualPosition=0., actualVelocity=0.) | changes]
                with mock.patch.object(m, 'manifest_destination'), mock.patch.object(m, 'service') as service:
                    with self.assertRaisesRegex(ValueError, 'disabled|stationary|fault'): m.apply_axis(self.version, self.config, 'test-axis')
                    service.assert_not_called()
            self.sample['axes'] = [dict(name='Axis1', state='Disabled', fault=None, motionBusy=False, actualPosition=0., actualVelocity=0.)]

    def test_position_stability_uses_confirmed_applied_policy_despite_velocity_noise(self):
        with self.device('0'*64) as m:
            self.sample['axes'][0]['actualVelocity'] = .014999
            m.verify_stationary(self.version, self.config)

    def test_stationary_policy_cannot_use_unapplied_wider_candidate(self):
        with self.device('0'*64, 'f'*64) as m:
            with self.assertRaisesRegex(ValueError, 'hardware identity differs'):
                m.verify_stationary(self.version, self.config)

    def test_stationary_policy_rejects_position_drift_and_unsafe_outputs(self):
        with self.device('0'*64) as m:
            self.sample['outputsAtConfiguredSafeState'] = False
            with self.assertRaisesRegex(ValueError, 'unsafe'): m.verify_stationary(self.version, self.config)
        self.sample['outputsAtConfiguredSafeState'] = True
        with self.device('0'*64) as m:
            original = hw.native_request
            def drift(endpoint, kind, payload, expected):
                reply = original(endpoint, kind, payload, expected)
                if kind == 'ReadSnapshotRequest':
                    reply = dict(reply, axes=[dict(reply['axes'][0], actualPosition=reply['sampleCycle'] * .01)])
                return reply
            with mock.patch.object(hw, 'native_request', side_effect=drift):
                with self.assertRaisesRegex(ValueError, 'not stationary'): m.verify_stationary(self.version, self.config)

    def test_axis_update_refuses_changed_native_process_before_manifest_write(self):
        with self.device('0'*64) as m:
            with mock.patch.object(m, 'manifest_destination'), mock.patch.object(m, 'service') as service, \
                 mock.patch.object(m, 'native_pid', side_effect=[123,124]), mock.patch.object(m, 'install_manifest') as install:
                with self.assertRaisesRegex(ValueError, 'identity changed'): m.apply_axis(self.version, self.config, 'test-axis')
                install.assert_not_called()
                self.assertEqual([mock.call('stop')], service.call_args_list)

    def test_axis_update_only_restarts_bridge_and_verifies_readiness(self):
        with self.device('0'*64) as m, tempfile.TemporaryDirectory() as private:
            destination = pathlib.Path(private) / 'axis-manifest.json'
            with mock.patch.object(m, 'manifest_destination', return_value=destination), mock.patch.object(m, 'service') as service, \
                 mock.patch.object(m, 'native_pid', return_value=123), mock.patch.object(m, 'wait_ready') as ready, mock.patch.object(hw, 'broker_request') as broker:
                m.apply_axis(self.version, self.config, 'test-axis')
                self.assertEqual(self.raw, destination.read_bytes())
                self.assertEqual([mock.call('stop'), mock.call('start')], service.call_args_list)
                ready.assert_called_once()
                broker.assert_not_called()

    def test_axis_update_unknown_readiness_does_not_retry_start(self):
        with self.device('0'*64) as m, tempfile.TemporaryDirectory() as private:
            with mock.patch.object(m, 'manifest_destination', return_value=pathlib.Path(private) / 'axis-manifest.json'), \
                 mock.patch.object(m, 'service') as service, mock.patch.object(m, 'native_pid', return_value=123), \
                 mock.patch.object(m, 'wait_ready', side_effect=ValueError('unconfirmed')):
                with self.assertRaisesRegex(ValueError, 'unconfirmed'): m.apply_axis(self.version, self.config, 'test-axis')
                self.assertEqual([mock.call('stop'), mock.call('start')], service.call_args_list)


class ConfigurationActionTests(unittest.TestCase):
    setUp = coordinator_fixtures.Tests.setUp
    tearDown = coordinator_fixtures.Tests.tearDown
    request = coordinator_fixtures.Tests.request

    def plan(self, kind='axis'):
        return dict(build_hash='A'*64, kind=kind, identity='b'*64)

    def baseline(self):
        self.coordinator.config['safe_mode_verified'] = True
        return self.request('adopt-empty-baseline')['active']

    def active_axis_program(self):
        library = self.runtime / 'build' / 'libplc_existing.so'
        library.write_bytes(b'old axis program')
        active = dict(build_hash='C'*64, library_path=str(library), library_sha256=hashlib.sha256(library.read_bytes()).hexdigest())
        coordinator.atomic_json(self.coordinator.active_path, active)
        version = self.coordinator.version(active['build_hash'])
        version.mkdir(parents=True)
        coordinator.atomic_json(version / 'stage-result.json', dict(manifest={'AxisInterfaceVersion':'workbench-axis-v1'}))
        return active

    def test_status_disables_run_after_axis_mapping_changes_without_hardware_change(self):
        self.active_axis_program()
        with mock.patch.object(coordinator.configuration_download, 'runtime_environment', return_value='private-env'), \
             mock.patch.object(coordinator, 'verify_axis_ready', side_effect=coordinator.DeploymentError('ManifestSha256 differs')):
            status = self.request('status')
            self.assertEqual('stopped', status['state'])
            self.assertTrue(status['success'])
            self.assertFalse(status['hardware_matches_active'])
        self.assertNotIn(b'START\n', self.daemon.commands)

    def test_start_directly_checks_active_axis_manifest_even_after_ui_status(self):
        active = self.active_axis_program()
        with mock.patch.object(self.coordinator, 'active_hardware_matches', return_value=True), \
             mock.patch.object(coordinator.configuration_download, 'runtime_environment', return_value='private-env'), \
             mock.patch.object(coordinator, 'verify_axis_ready', side_effect=coordinator.DeploymentError('ManifestSha256 differs')) as verify:
            with self.assertRaisesRegex(coordinator.DeploymentError, 'ManifestSha256'):
                self.request('start', expected_active_build_hash=active['build_hash'])
            verify.assert_called_once()
        self.assertNotIn(b'START\n', self.daemon.commands)

    def test_matching_axis_management_readiness_allows_start_without_motion_ready_gate(self):
        active = self.active_axis_program()
        with mock.patch.object(coordinator.configuration_download, 'runtime_environment', return_value='private-env'), \
             mock.patch.object(coordinator, 'verify_axis_ready') as verify:
            self.assertTrue(self.request('status')['hardware_matches_active'])
            self.assertEqual('running', self.request('start', expected_active_build_hash=active['build_hash'])['state'])
            self.assertGreaterEqual(verify.call_count, 2)

    def test_plan_route_is_readonly_and_never_requires_a_plc_stop(self):
        self.daemon.state = 'RUNNING'
        with mock.patch.object(coordinator.configuration_download, 'plan', return_value=self.plan()), \
             mock.patch.object(coordinator.configuration_download, 'service') as service:
            result = self.request('configuration-plan', build_hash='A'*64)
            self.assertEqual(result['state'], 'configuration-planned')
            self.assertEqual(result['configuration_plan'], self.plan())
            self.assertEqual([], self.daemon.commands)
            service.assert_not_called()

    def test_apply_refuses_running_before_any_mutation(self):
        self.daemon.state = 'RUNNING'
        with mock.patch.object(coordinator.configuration_download, 'plan') as plan:
            with self.assertRaisesRegex(coordinator.DeploymentError, 'STOPPED'):
                self.request('configuration-apply', build_hash='A'*64, configuration_identity='b'*64)
            plan.assert_not_called()

    def test_changed_plan_or_active_baseline_refuses_all_mutations(self):
        self.baseline()
        for expected, plan_hash in [('c'*64,'b'*64),(None,'c'*64)]:
            with mock.patch.object(coordinator.configuration_download, 'plan', return_value=dict(self.plan(), identity=plan_hash)), \
                 mock.patch.object(coordinator.configuration_download, 'apply_axis') as apply:
                with self.assertRaisesRegex(coordinator.DeploymentError, 'identity changed|identity'):
                    self.request('configuration-apply', build_hash='A'*64, configuration_identity='b'*64, expected_active_build_hash=expected)
                apply.assert_not_called()

    def test_success_updates_readiness_and_preserves_stopped_baseline(self):
        active = self.baseline()
        version = self.coordinator.version('A'*64)
        version.mkdir(parents=True)
        compiled = dict(state='compiled', library_sha256='d'*64, axis_ready=False, exports_verified=True)
        for kind in ['axis', 'hardware', 'none']:
            with mock.patch.object(coordinator.configuration_download, 'plan', return_value=self.plan(kind)), \
                 mock.patch.object(coordinator.configuration_download, 'apply_axis') as axis, \
                 mock.patch.object(self.coordinator, 'apply_hardware') as hardware, \
                 mock.patch.object(coordinator.configuration_download, 'wait_ready') as ready, \
                 mock.patch.object(coordinator.configuration_download, 'candidate', return_value=(None, {'AxisInterfaceVersion':'workbench-axis-v1'}, dict(compiled))):
                result = self.request('configuration-apply', build_hash='A'*64, configuration_identity='b'*64, expected_active_build_hash=None)
                self.assertEqual(result['state'], 'configuration-applied')
                self.assertTrue(result['axis_ready'])
                self.assertEqual(result['active'], active)
                self.assertEqual(axis.call_count, int(kind=='axis'))
                self.assertEqual(hardware.call_count, int(kind=='hardware'))
                self.assertEqual(ready.call_count, int(kind!='axis'))
                saved = coordinator.read_json(version / 'compile-result.json')
                self.assertTrue(saved['axis_ready'])
                self.assertEqual(saved['library_sha256'], compiled['library_sha256'])
        self.assertNotIn(b'START\n', self.daemon.commands)

    def test_failure_is_not_retried_or_marked_ready(self):
        self.baseline()
        version = self.coordinator.version('A'*64)
        version.mkdir(parents=True)
        coordinator.atomic_json(version / 'compile-result.json', dict(state='compiled', axis_ready=False))
        with mock.patch.object(coordinator.configuration_download, 'plan', return_value=self.plan()), \
             mock.patch.object(coordinator.configuration_download, 'apply_axis', side_effect=ValueError('unconfirmed')) as apply:
            with self.assertRaisesRegex(coordinator.DeploymentError, 'unconfirmed'):
                self.request('configuration-apply', build_hash='A'*64, configuration_identity='b'*64, expected_active_build_hash=None)
            self.assertEqual(apply.call_count, 1)
            self.assertFalse(coordinator.read_json(version / 'compile-result.json')['axis_ready'])
        self.assertNotIn(b'START\n', self.daemon.commands)


if __name__ == '__main__': unittest.main()
