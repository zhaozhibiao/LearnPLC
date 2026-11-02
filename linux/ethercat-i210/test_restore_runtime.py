"""MIT. Boot recovery restores only root-owned, previously applied hardware."""
import hashlib
import importlib.util
import json
import pathlib
import tempfile
import types
import unittest
from unittest.mock import Mock, patch
import sys

spec = importlib.util.spec_from_file_location('restore_runtime', pathlib.Path(__file__).with_name('restore-runtime.py'))
restore = importlib.util.module_from_spec(spec)
spec.loader.exec_module(restore)


class RestoreTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        base = pathlib.Path(self.folder.name)
        self.binary = base / 'binary'
        self.binary.write_bytes(b'pinned-core')
        directory = base / 'versions' / ('c' * 64)
        directory.mkdir(parents=True)
        hashes = {}
        for name in ('ethercat-runtime.json', 'machine-config.json', 'io-map.json'):
            (directory / name).write_text('{}')
            hashes[name] = hashlib.sha256(b'{}').hexdigest()
        (directory / 'build-manifest.json').write_text(json.dumps(dict(BuildHash='a'*64, Files=hashes)))
        self.active = base / 'active.json'
        self.active.write_text(json.dumps(dict(buildHash='a'*64, topologyFingerprint='c'*64, machineSha256=hashes['machine-config.json'], ioMapSha256=hashes['io-map.json'])))
        installation = base / 'installation.json'
        installation.write_text(json.dumps(dict(binary=str(self.binary), binarySha256=hashlib.sha256(self.binary.read_bytes()).hexdigest())))
        self.helper = types.SimpleNamespace(INSTALLATION=installation, ACTIVE_RECORD=self.active, VERSIONS=base/'versions',
            FILES=('ethercat-runtime.json','machine-config.json','io-map.json'), SERVICE='st-workbench-is620n.service',
            CONTROLLER_SOCKET='/run/st-workbench-is620n/controller.sock', root_owned=Mock(),
            read_regular=lambda p:pathlib.Path(p).read_bytes(), parse_json=json.loads, hex_identity=lambda value:restore.hex_identity(value),
            validate_topology=Mock(return_value='c'*64), command=Mock(), native_request=Mock())

    def test_builds_only_pinned_root_hardware_command(self):
        args = restore.restore_arguments(self.helper)
        self.assertEqual(str(self.binary), args[0])
        self.assertIn('--socket', args)
        self.assertNotIn('Enable', args)
        self.assertNotIn('START', args)
        self.helper.root_owned.assert_any_call(self.active)

    def set_trace_machine(self):
        directory = self.helper.VERSIONS / ('c'*64)
        machine = dict(axisTrace=dict(enabled=True,capacity=120000),axes=[{}],requiredRuntimeCapabilities=['workbench_motion_migration_v1','workbench_axis_trace_v1'])
        raw = json.dumps(machine).encode()
        (directory / 'machine-config.json').write_bytes(raw)
        digest = hashlib.sha256(raw).hexdigest()
        manifest_path = directory / 'build-manifest.json'
        manifest = json.loads(manifest_path.read_bytes()); manifest['Files']['machine-config.json'] = digest
        manifest_path.write_text(json.dumps(manifest))
        active = json.loads(self.active.read_bytes()); active['machineSha256'] = digest
        self.active.write_text(json.dumps(active))
        return machine

    def test_restore_uses_saved_trace_machine_and_shared_root_builder(self):
        machine = self.set_trace_machine()
        trace = ['--axis-trace-csv','/var/lib/st-workbench-ethercat/traces/unique.csv','--axis-trace-capacity','120000']
        self.helper.check_runtime_capabilities = Mock()
        self.helper.axis_trace_arguments = Mock(return_value=trace)
        self.helper.prepare_axis_trace = Mock()
        with patch.dict(sys.modules, {'grp': types.SimpleNamespace(getgrnam=lambda _:types.SimpleNamespace(gr_gid=1000))}):
            args = restore.restore_arguments(self.helper)
        self.helper.axis_trace_arguments.assert_called_once_with(machine, 'c'*64)
        self.helper.prepare_axis_trace.assert_called_once_with(trace, 1000)
        self.helper.check_runtime_capabilities.assert_called_once_with(self.binary, machine)
        self.assertEqual(args[-4:], trace)

    def test_restore_fails_closed_when_old_helper_cannot_build_trace(self):
        self.set_trace_machine()
        self.helper.check_runtime_capabilities = Mock()
        with self.assertRaisesRegex(ValueError, 'trace'): restore.restore_arguments(self.helper)

    def test_restore_v2_uses_latest_active_requirements_on_reused_topology(self):
        manifest=dict(AxisInterfaceVersion='workbench-axis-v2',AxisExportMarker='workbench_axis_v2',
                      AxisCommandBytes=4112,AxisFeedbackBytes=6656,
                      RequiredRuntimeCapabilities=['workbench_axis_move_profile_v2','workbench_axis_velocity_profile_v2'])
        active=json.loads(self.active.read_bytes());active['runtimeRequirements']=manifest
        self.active.write_text(json.dumps(active))
        self.helper.required_runtime_capabilities=Mock(return_value=manifest['RequiredRuntimeCapabilities'])
        self.helper.check_loaded_runtime_capabilities=Mock()
        self.helper.check_runtime_capabilities=Mock()
        restore.restore_arguments(self.helper)
        self.helper.check_runtime_capabilities.assert_called_once_with(self.binary,{},manifest)

    def test_running_v2_core_must_prove_loaded_hello_capabilities(self):
        manifest=dict(AxisInterfaceVersion='workbench-axis-v2',AxisExportMarker='workbench_axis_v2',
                      AxisCommandBytes=4112,AxisFeedbackBytes=6656,
                      RequiredRuntimeCapabilities=['workbench_axis_move_profile_v2'])
        active=json.loads(self.active.read_bytes());active['runtimeRequirements']=manifest
        self.active.write_text(json.dumps(active))
        self.helper.required_runtime_capabilities=Mock(return_value=manifest['RequiredRuntimeCapabilities'])
        self.helper.check_runtime_capabilities=Mock()
        self.helper.check_loaded_runtime_capabilities=Mock(side_effect=ValueError('loaded capability mismatch'))
        self.helper.command.return_value=types.SimpleNamespace(returncode=0,stdout='LoadState=loaded\nActiveState=active\nMainPID=42\n')
        self.helper.native_request.return_value=dict(runtimeCapabilities=[])
        with self.assertRaisesRegex(ValueError,'loaded capability'): restore.start_saved_core(self.helper)
        self.assertEqual(self.helper.command.call_count,1)
        self.helper.check_loaded_runtime_capabilities.assert_called_once_with(dict(runtimeCapabilities=[]),manifest['RequiredRuntimeCapabilities'])

    def test_v2_restore_refuses_old_helper_before_start(self):
        active=json.loads(self.active.read_bytes());active['runtimeRequirements']=dict(AxisInterfaceVersion='workbench-axis-v2')
        self.active.write_text(json.dumps(active))
        self.helper.command.return_value=types.SimpleNamespace(returncode=0,stdout='LoadState=loaded\nActiveState=inactive\nMainPID=0\n')
        with self.assertRaisesRegex(ValueError,'capabil'): restore.start_saved_core(self.helper)
        self.assertEqual(self.helper.command.call_count,1)
        self.helper.native_request.assert_not_called()

    def test_old_helper_cannot_silently_restore_v2(self):
        active=json.loads(self.active.read_bytes());active['runtimeRequirements']=dict(AxisInterfaceVersion='workbench-axis-v2')
        self.active.write_text(json.dumps(active))
        self.helper.check_runtime_capabilities=Mock()
        with self.assertRaisesRegex(ValueError,'capabil'): restore.restore_arguments(self.helper)
        self.helper.command.assert_not_called()

    def test_tampered_binary_or_hardware_refuses_boot(self):
        self.binary.write_bytes(b'tampered')
        with self.assertRaisesRegex(ValueError, 'binary'): restore.restore_arguments(self.helper)
        self.binary.write_bytes(b'pinned-core')
        (self.helper.VERSIONS/('c'*64)/'machine-config.json').write_text('{"changed":true}')
        with self.assertRaisesRegex(ValueError, 'checksum'): restore.restore_arguments(self.helper)

    def test_changed_root_record_identity_is_rejected(self):
        value=json.loads(self.active.read_bytes());value['machineSha256']='f'*64
        self.active.write_text(json.dumps(value))
        with self.assertRaisesRegex(ValueError, 'record'): restore.restore_arguments(self.helper)

    def test_same_physical_hardware_reused_by_another_program_build_is_valid(self):
        value=json.loads(self.active.read_bytes());value['buildHash']='b'*64
        self.active.write_text(json.dumps(value))
        self.assertEqual(str(self.binary), restore.restore_arguments(self.helper)[0])

    def test_restore_validation_receives_trace_and_failure_never_stops_or_launches(self):
        args = [str(self.binary),'--ethercat-config','/saved/ethercat-runtime.json','--machine-config','/saved/machine-config.json','--io-map','/saved/io-map.json','--socket','/run/core.sock','--cpu','2','--axis-trace-csv','/var/lib/st-workbench-ethercat/traces/test.csv','--axis-trace-capacity','100']
        self.helper.command.side_effect = [types.SimpleNamespace(returncode=0,stdout='LoadState=loaded\nActiveState=inactive\nMainPID=0\n'), ValueError('trace validation failed')]
        with patch.object(restore,'restore_arguments',return_value=args):
            with self.assertRaisesRegex(ValueError,'trace validation'): restore.start_saved_core(self.helper)
        self.assertEqual(self.helper.command.call_count,2)
        validation = self.helper.command.call_args_list[1].args[0]
        self.assertEqual(validation[-4:], args[-4:])
        self.helper.native_request.assert_not_called()

    def test_running_core_is_not_restarted(self):
        self.helper.command.return_value=types.SimpleNamespace(returncode=0,stdout='LoadState=loaded\nActiveState=active\nMainPID=42\n')
        self.assertEqual('already-running',restore.start_saved_core(self.helper))
        self.assertEqual(1,self.helper.command.call_count)

    def test_absent_core_starts_and_waits_for_feedback_without_axis_commands(self):
        def command(args,**kwargs):
            if args[:2]==['systemctl','show']:return types.SimpleNamespace(returncode=1,stdout='LoadState=not-found\nActiveState=inactive\nMainPID=0\n')
            return types.SimpleNamespace(returncode=0,stdout='')
        self.helper.command.side_effect=command
        self.helper.native_request.side_effect=[dict(runtimeReady=True,ethercatTopologyFingerprint='c'*64),
            dict(runtimeReady=True,ioReady=True,axes=[dict(state='Disabled',fault=None)],sampleCycle=1,sampleMonotonicNs=100),
            dict(runtimeReady=True,ioReady=True,axes=[dict(state='Disabled',fault=None)],sampleCycle=2,sampleMonotonicNs=200)]
        self.assertEqual('restored-disabled',restore.start_saved_core(self.helper))
        calls=[c.args[0] for c in self.helper.command.call_args_list]
        self.assertEqual('systemd-run',calls[-1][0])
        self.assertIn('--unit=st-workbench-is620n',calls[-1])
        self.assertEqual(['HelloRequest','ReadSnapshotRequest','ReadSnapshotRequest'],[c.args[1] for c in self.helper.native_request.call_args_list])
