"""MIT. Scoped transfer uses actual identities and never mutates Runtime."""
import hashlib
import json
import unittest
import zipfile
from unittest import mock
import coordinator
import test_coordinator as fixtures
import test_ethercat_hardware as hardware_fixtures


class ScopedTransferTests(unittest.TestCase):
    setUp = fixtures.Tests.setUp
    tearDown = fixtures.Tests.tearDown
    request = fixtures.Tests.request
    package = fixtures.Tests.package

    def active_package(self, archive=True):
        extras = {}
        if archive:
            extras['engineering-archive.json'] = json.dumps(dict(SchemaVersion=1, Project={'Name':'Active source'}, Sources=[], SourceHash='s'*64, HardwareHash='hardware')).encode()
        package, checksum, build = self.package(extras)
        self.request('stage', package_path=str(package), package_sha256=checksum, build_hash=build)
        version = self.coordinator.version(build)
        manifest = coordinator.read_json(version / 'stage-result.json')['manifest']
        manifest['SourceHash'] = 's'*64
        coordinator.atomic_json(version / 'core/generated/build-manifest.json', manifest)
        stage = coordinator.read_json(version / 'stage-result.json'); stage['manifest'] = manifest
        coordinator.atomic_json(version / 'stage-result.json', stage)
        lib = self.runtime / 'build/libplc_active.so'; lib.write_bytes(b'active')
        active = dict(build_hash=build, library_path=str(lib), library_sha256=coordinator.digest(lib), source_hash='s'*64, hardware_hash='hardware', symbol_table_hash=manifest['SymbolTableHash'])
        coordinator.atomic_json(self.coordinator.active_path, active)
        coordinator.atomic_json(version / 'compile-result.json', dict(state='compiled', exports_verified=True, build_hash=build, library_sha256=coordinator.digest(lib)))
        return active, version

    def test_program_export_reads_active_archive_and_does_not_stop(self):
        active, version = self.active_package()
        self.daemon.state = 'RUNNING'
        result = self.request('program-export', build_hash='B'*64)
        self.assertEqual('program-exported', result['state'])
        self.assertEqual('Active source', result['transfer']['project']['Name'])
        self.assertEqual(active, result['active'])
        self.assertNotIn(b'STOP\n', self.daemon.commands)

    def test_legacy_has_explicit_no_source_but_can_export_verified_debug_map(self):
        self.active_package(False)
        with self.assertRaisesRegex(coordinator.DeploymentError, '未保存源工程|source archive'):
            self.request('program-export')
        result = self.request('program-debug-map')
        self.assertEqual({}, result['transfer']['debug_map'])
        self.assertEqual(result['transfer']['symbol_table_hash'].lower(), hashlib.sha256(result['transfer']['debug_map_json'].encode()).hexdigest())

    def test_changed_archive_inventory_is_rejected(self):
        _, version = self.active_package()
        (version / 'core/generated/engineering-archive.json').write_text('{}')
        with self.assertRaisesRegex(coordinator.DeploymentError, 'checksum|inventory'):
            self.request('program-export')

    def test_hardware_export_never_uses_unapplied_candidate(self):
        self.active_package()
        with self.assertRaisesRegex(coordinator.DeploymentError, 'applied|已应用'):
            self.request('hardware-export')

    def hardware_package(self, axes=True):
        _, generated, topology = hardware_fixtures.fixture(self.root / 'hardware-fixture')
        axis = dict(SchemaVersion=1, InterfaceVersion='workbench-axis-v1', PlcIntervalMilliseconds=10,
            Axes=[dict(Index=1, Name='X', ControllerAxis='Axis1', Unit='mm', MinPosition=-10, MaxPosition=10,
                       MaximumVelocity=1, MaximumAcceleration=1, MaximumDeceleration=1, MaximumJerk=None)])
        if not axes: axis['Axes'] = []
        (generated / 'axis-manifest.json').write_text(json.dumps(axis))
        configuration = dict(SchemaVersion=1, State='offline-engineering', Hardware={'Axes':[]})
        (generated / 'hardware-configuration.json').write_text(json.dumps(configuration))
        files = {p.name:coordinator.digest(p) for p in generated.iterdir() if p.name != 'build-manifest.json'}
        build = 'C'*64
        manifest = dict(BuildHash=build, ArtifactKind='hardware-only', Files=files,
            EtherCatRuntimeInterfaceVersion='workbench-ethercat-v1', EtherCatTopologyFingerprint=topology['topologyFingerprint'],
            HardwareHash=files['hardware-configuration.json'])
        coordinator.atomic_json(generated / 'build-manifest.json', manifest)
        path = self.root / 'hardware.zip'
        with zipfile.ZipFile(path, 'w') as archive:
            for p in generated.iterdir(): archive.write(p, p.name)
        return dict(package_path=str(path), package_sha256=coordinator.digest(path), build_hash=build)

    def test_hardware_stage_bypasses_st_compile_but_cannot_compile_or_activate(self):
        self.active_package()
        package = self.hardware_package()
        result = self.request('hardware-stage', **package)
        self.assertEqual('staged', result['state'])
        with self.assertRaisesRegex(coordinator.DeploymentError, 'compilation'):
            self.coordinator.compile(dict(build_hash=package['build_hash']))
        with self.assertRaisesRegex(coordinator.DeploymentError, 'activate'):
            self.request('activate', build_hash=package['build_hash'], expected_active_build_hash='A'*64)
        with self.assertRaises(coordinator.DeploymentError): self.request('stage', **package)

    def test_hardware_export_requires_loaded_fingerprint_and_axis_match(self):
        self.active_package()
        package = self.hardware_package(); self.request('hardware-stage', **package)
        version = self.coordinator.version(package['build_hash'])
        applied = dict(coordinator.ethercat_hardware.check_bundle(version, {}), status='applied', verified=True)
        coordinator.atomic_json(self.coordinator.prefix / 'hardware-active.json', applied)
        checksum = coordinator.digest(version / 'core/generated/axis-manifest.json')
        with mock.patch.object(coordinator.ethercat_hardware, 'verify_applied', return_value=applied), \
             mock.patch.object(coordinator.configuration_download, 'worker_digest', return_value=checksum):
            exported = self.request('hardware-export')
            self.assertEqual('hardware-exported', exported['state'])
            self.assertEqual('offline-engineering', exported['transfer']['configuration']['State'])
        with mock.patch.object(coordinator.ethercat_hardware, 'verify_applied', side_effect=ValueError('loaded fingerprint differs')):
            with self.assertRaisesRegex(coordinator.DeploymentError, 'fingerprint'): self.request('hardware-export')
        with mock.patch.object(coordinator.ethercat_hardware, 'verify_applied', return_value=applied), \
             mock.patch.object(coordinator.configuration_download, 'worker_digest', return_value='f'*64):
            with self.assertRaisesRegex(coordinator.DeploymentError, 'axis manifest'): self.request('hardware-export')

    def test_io_only_hardware_stage_and_export_do_not_require_axis_bridge(self):
        self.active_package()
        package = self.hardware_package(axes=False)
        self.request('hardware-stage', **package)
        version = self.coordinator.version(package['build_hash'])
        applied = coordinator.ethercat_hardware.check_bundle(version, {})
        coordinator.atomic_json(self.coordinator.prefix / 'hardware-active.json', applied)
        with mock.patch.object(coordinator.ethercat_hardware, 'verify_applied', return_value=applied), \
             mock.patch.object(coordinator.configuration_download, 'worker_digest', side_effect=AssertionError('IO-only needs no axis bridge')):
            self.assertEqual('hardware-exported', self.request('hardware-export')['state'])

    def test_io_only_hardware_apply_never_starts_axis_bridge_or_plc(self):
        self.active_package(); package = self.hardware_package(axes=False)
        self.request('hardware-stage', **package)
        version = self.coordinator.version(package['build_hash'])
        applied = dict(coordinator.ethercat_hardware.check_bundle(version, {}), status='applied', verified=True)
        with mock.patch.object(self.coordinator, 'native_core_pid', return_value=0), \
             mock.patch.object(coordinator.subprocess, 'run', return_value=mock.Mock(returncode=1)) as run, \
             mock.patch.object(coordinator.ethercat_hardware, 'apply_bundle', return_value=applied), \
             mock.patch.object(coordinator.ethercat_hardware, 'verify_applied', return_value=applied):
            self.assertEqual('hardware-applied', self.request('hardware-apply', build_hash=package['build_hash'], expected_active_build_hash='A'*64)['state'])
            self.assertFalse(any('start' in call.args[0] for call in run.call_args_list))
            self.assertNotIn(b'START\n', self.daemon.commands)


if __name__ == '__main__': unittest.main()
