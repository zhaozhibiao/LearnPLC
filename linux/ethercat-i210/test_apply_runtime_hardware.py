import importlib.util
import pathlib
import tempfile
import hashlib
import json
import types
import sys
import unittest
import contextlib
import stat
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('root_apply', pathlib.Path(__file__).with_name('apply-runtime-hardware.py'))
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)

class RootHelperTests(unittest.TestCase):
    def test_trace_arguments_are_bounded_and_root_fixed(self):
        machine = dict(axisTrace=dict(enabled=True, capacity=120000), axes=[{}],
                       requiredRuntimeCapabilities=['workbench_motion_migration_v1', 'workbench_axis_trace_v1'])
        args = helper.axis_trace_arguments(machine, 'a'*64, 'b'*32)
        self.assertEqual(args, ['--axis-trace-csv', '/var/lib/st-workbench-ethercat/traces/'+'a'*64+'-'+'b'*32+'.csv', '--axis-trace-capacity', '120000'])
        self.assertEqual(helper.axis_trace_arguments({}, 'a'*64), [])
        self.assertEqual(helper.axis_trace_arguments(dict(axisTrace=dict(enabled=False)), 'a'*64), [])
        self.assertNotEqual(helper.axis_trace_arguments(machine, 'a'*64), helper.axis_trace_arguments(machine, 'a'*64))
        for capacity in [None, True, 0, -1, 120001, 1.5, '100']:
            with self.assertRaises(ValueError): helper.axis_trace_arguments(dict(machine, axisTrace=dict(enabled=True, capacity=capacity)), 'a'*64)
        for trace in [True, [], {}, dict(enabled=1, capacity=1), dict(enabled=True, capacity=1, path='/tmp/trace')]:
            with self.assertRaises(ValueError): helper.axis_trace_arguments(dict(machine, axisTrace=trace), 'a'*64)
        for axes in [[], [{}, {}], None]:
            with self.assertRaises(ValueError): helper.axis_trace_arguments(dict(machine, axes=axes), 'a'*64)
        for capabilities in [[], ['workbench_motion_migration_v1'], ['workbench_axis_trace_v1']]:
            with self.assertRaises(ValueError): helper.axis_trace_arguments(dict(machine, requiredRuntimeCapabilities=capabilities), 'a'*64)
        for identity,nonce in [('../bad','b'*32), ('a'*64,'../../tmp/trace')]:
            with self.assertRaises(ValueError): helper.axis_trace_arguments(machine, identity, nonce)

    def test_trace_directory_rejects_existing_leaves_and_unsafe_paths(self):
        args = ['--axis-trace-csv', '/var/lib/st-workbench-ethercat/traces/'+'a'*64+'-'+'b'*32+'.csv', '--axis-trace-capacity', '1']
        with patch.object(helper, 'root_owned'), patch.object(helper.pathlib.Path, 'mkdir'), patch.object(helper.pathlib.Path, 'lstat', return_value=types.SimpleNamespace(st_mode=stat.S_IFDIR | 0o750)), patch.object(helper.os, 'chmod'), patch.object(helper.os, 'chown', create=True), patch.object(helper.os.path, 'lexists', return_value=True):
            with self.assertRaisesRegex(ValueError, 'exists'): helper.prepare_axis_trace(args, 1000)
        with self.assertRaises(ValueError): helper.prepare_axis_trace(['--axis-trace-csv','/tmp/trace','--axis-trace-capacity','1'], 1000)

    def test_trace_directory_is_root_owned_group_readable_and_never_follows_symlinks(self):
        args = helper.axis_trace_arguments(dict(axisTrace=dict(enabled=True,capacity=1),axes=[{}],requiredRuntimeCapabilities=['workbench_motion_migration_v1','workbench_axis_trace_v1']), 'a'*64)
        with patch.object(helper, 'root_owned') as owned, patch.object(helper.pathlib.Path, 'mkdir'), patch.object(helper.pathlib.Path, 'lstat', return_value=types.SimpleNamespace(st_mode=stat.S_IFDIR | 0o750)), patch.object(helper.os, 'chmod') as chmod, patch.object(helper.os, 'chown', create=True) as chown, patch.object(helper.os.path, 'lexists', return_value=False):
            helper.prepare_axis_trace(args, 1000)
            chown.assert_called_once_with(pathlib.Path(str(helper.TRACE_DIRECTORY)), 0, 1000)
            chmod.assert_called_once_with(pathlib.Path(str(helper.TRACE_DIRECTORY)), 0o750)
            self.assertEqual(owned.call_count,3)
        with patch.object(helper, 'root_owned', side_effect=ValueError('symlink')), patch.object(helper.os, 'chown', create=True) as chown:
            with self.assertRaises(ValueError): helper.prepare_axis_trace(args, 1000)
            chown.assert_not_called()

    def test_faulted_axis_needs_explicit_non_enabled_drive_status(self):
        sample=dict(runtimeReady=True,ioReady=True,outputsAtConfiguredSafeState=True,axes=[dict(state='ErrorStop',fault='CiA402 error 0x0E08',motionBusy=False,driveStatusword=0x218)])
        helper.disabled_snapshot(sample)
        for raw in [None,True,0x27,0x0f,0,0x40]:
            sample['axes'][0]['driveStatusword']=raw
            with self.assertRaises(ValueError):helper.disabled_snapshot(sample)
    def test_known_torque_status_overrides_disabled_label(self):
        for raw in [0x07,0x27,True,-1,65536]:
            sample=dict(runtimeReady=True,ioReady=True,outputsAtConfiguredSafeState=True,axes=[dict(state='Disabled',fault=None,driveStatusword=raw)])
            with self.assertRaises(ValueError):helper.disabled_snapshot(sample)
    def test_waits_for_transient_name_to_be_released(self):
        retained=types.SimpleNamespace(stdout='LoadState=loaded\nActiveState=inactive\nMainPID=0\n',returncode=0)
        released=types.SimpleNamespace(stdout='LoadState=not-found\nActiveState=inactive\nMainPID=0\n',returncode=4)
        with patch.object(helper,'command',side_effect=[retained,released]), patch.object(helper.time,'sleep'), patch.object(helper.pathlib.Path,'exists',return_value=False):
            helper.wait_for_core_release()
    def test_retained_or_running_service_is_not_reused(self):
        for state,pid in [('inactive','0'),('active','42')]:
            reply=types.SimpleNamespace(stdout=f'LoadState=loaded\nActiveState={state}\nMainPID={pid}\n',returncode=0)
            with patch.object(helper,'command',return_value=reply), patch.object(helper.time,'sleep'), patch.object(helper.time,'monotonic',side_effect=[0,5]):
                with self.assertRaises(ValueError):helper.wait_for_core_release()
    def test_pinned_core_capabilities_checked_before_hardware_switch(self):
        required = {'requiredRuntimeCapabilities': ['workbench_motion_migration_v1']}
        with patch.object(helper, 'command', return_value=types.SimpleNamespace(stdout='{"runtimeCapabilities":["workbench_motion_migration_v1"]}')) as call:
            helper.check_runtime_capabilities(pathlib.Path('/opt/pinned'), required)
            call.assert_called_once_with([str(pathlib.Path('/opt/pinned')), '--runtime-capabilities'])
        with patch.object(helper, 'command', return_value=types.SimpleNamespace(stdout='{}')):
            with self.assertRaises(ValueError): helper.check_runtime_capabilities(pathlib.Path('/opt/pinned'), required)
        with patch.object(helper, 'command') as call:
            helper.check_runtime_capabilities(pathlib.Path('/opt/pinned'), {})
            call.assert_not_called()

    def test_v2_manifest_requires_marker_and_minimum_move_capability(self):
        move = 'workbench_axis_move_profile_v2'
        velocity = 'workbench_axis_velocity_profile_v2'
        manifest = dict(AxisInterfaceVersion='workbench-axis-v2', AxisExportMarker='workbench_axis_v2',
                        AxisCommandBytes=4112, AxisFeedbackBytes=6656, RequiredRuntimeCapabilities=[move,velocity])
        machine = dict(requiredRuntimeCapabilities=['workbench_motion_migration_v1'])
        self.assertEqual(set(helper.required_runtime_capabilities(machine,manifest)),
                         {move,velocity,'workbench_motion_migration_v1'})
        for change in [dict(AxisExportMarker='workbench_axis_v1'),dict(RequiredRuntimeCapabilities=[]),
                       dict(AxisCommandBytes=2576),dict(AxisFeedbackBytes=5632),dict(RequiredRuntimeCapabilities=[True]),
                       dict(AxisInterfaceVersion='workbench-axis-v3')]:
            with self.assertRaises(ValueError): helper.required_runtime_capabilities(machine,dict(manifest,**change))
        with self.assertRaises(ValueError): helper.required_runtime_capabilities({},dict(RequiredRuntimeCapabilities=[move]))

    def test_v2_pinned_binary_and_loaded_hello_both_need_combined_capabilities(self):
        move, velocity = 'workbench_axis_move_profile_v2','workbench_axis_velocity_profile_v2'
        manifest = dict(AxisInterfaceVersion='workbench-axis-v2',AxisExportMarker='workbench_axis_v2',
                        AxisCommandBytes=4112,AxisFeedbackBytes=6656,RequiredRuntimeCapabilities=[move,velocity])
        machine = dict(requiredRuntimeCapabilities=['workbench_motion_migration_v1'])
        for capabilities in [[move], [move,velocity], [move,velocity,'workbench_motion_migration_v1']]:
            result=types.SimpleNamespace(stdout=json.dumps(dict(runtimeCapabilities=capabilities)))
            with patch.object(helper,'command',return_value=result):
                if len(capabilities)==3: helper.check_runtime_capabilities('/pinned',machine,manifest)
                else:
                    with self.assertRaises(ValueError): helper.check_runtime_capabilities('/pinned',machine,manifest)
        required=helper.required_runtime_capabilities(machine,manifest)
        for hello in [{},dict(runtimeCapabilities=[move]),dict(runtimeCapabilities=[move,velocity,True])]:
            with self.assertRaises(ValueError): helper.check_loaded_runtime_capabilities(hello,required)
        helper.check_loaded_runtime_capabilities(dict(runtimeCapabilities=required),required)

    def test_v2_unsupported_pinned_core_rejects_before_any_service_or_preflight_action(self):
        with tempfile.TemporaryDirectory() as directory:
            base=pathlib.Path(directory);build='a'*64
            generated=base/'candidates'/build/'core'/'generated';generated.mkdir(parents=True)
            binary=base/'binary';binary.write_bytes(b'pinned')
            inventory={}
            for name in ('machine-config.json','io-map.json'):
                (generated/name).write_bytes(b'{}');inventory[name]=hashlib.sha256(b'{}').hexdigest()
            topology=dict(schemaVersion=1,readyForRuntime=True,blockers=[],slaves=[dict(position=0)],
                          machineSha256=inventory['machine-config.json'],ioMapSha256=inventory['io-map.json'])
            topology['topologyFingerprint']=hashlib.sha256(helper.canonical_identity(topology)).hexdigest()
            raw=json.dumps(topology).encode();(generated/'ethercat-runtime.json').write_bytes(raw)
            inventory['ethercat-runtime.json']=hashlib.sha256(raw).hexdigest()
            manifest=dict(BuildHash=build,Files=inventory,AxisInterfaceVersion='workbench-axis-v2',
                          AxisExportMarker='workbench_axis_v2',AxisCommandBytes=4112,AxisFeedbackBytes=6656,
                          RequiredRuntimeCapabilities=['workbench_axis_move_profile_v2'])
            (generated/'build-manifest.json').write_text(json.dumps(manifest))
            installation=base/'installation.json'
            installation.write_text(json.dumps(dict(binary=str(binary),binarySha256=hashlib.sha256(b'pinned').hexdigest())))
            reply=types.SimpleNamespace(stdout=json.dumps(dict(runtimeCapabilities=['workbench_motion_migration_v1'])))
            with patch.object(helper,'INSTALLATION',installation),patch.object(helper,'CANDIDATES',base/'candidates'), \
                 patch.object(helper,'root_owned'),patch.object(helper,'command',return_value=reply) as command, \
                 patch.object(helper,'preflight') as preflight,patch.object(helper,'make_root_directory') as mkdir:
                with self.assertRaisesRegex(ValueError,'capability'): helper._apply_locked(build)
            preflight.assert_not_called();mkdir.assert_not_called()
            command.assert_called_once_with([str(binary),'--runtime-capabilities'])
            proof=helper.check_bundle(base/'candidates'/build,{})
            active=base/'active.json'
            with patch.object(helper,'ACTIVE_RECORD',active),patch.object(helper,'root_owned'),patch.object(helper.os,'chown',create=True):
                helper.publish_active(proof,1000)
            self.assertEqual(json.loads(active.read_bytes())['runtimeRequirements'],proof['runtimeRequirements'])

    def test_following_window_readback_requires_exact_engineering_value(self):
        topology = {'slaves': [{'position': 3, 'startupSdos': [
            {'index': 0x6065, 'subindex': 0, 'data': [0xf6, 0x28, 0, 0]}]}]}
        with patch.object(helper, 'command', return_value=types.SimpleNamespace(stdout='0x000028f6 10486')) as call:
            helper.verify_drive_windows(topology)
            self.assertEqual(call.call_args.args[0][3], '3')
        with patch.object(helper, 'command', return_value=types.SimpleNamespace(stdout='0x0 0')):
            with self.assertRaisesRegex(ValueError, 'readback'): helper.verify_drive_windows(topology)

    def test_gate_requires_stopped_and_all_axes_disabled(self):
        with patch.object(helper, 'plc_status', return_value='STOPPED'), patch.object(helper, 'native_request', return_value={'runtimeReady':True,'ioReady':True,'outputsAtConfiguredSafeState':True,'axes':[{'state':'Disabled','fault':None}]}):
            helper.preflight()
        for status in ('RUNNING', 'EMPTY', 'TRANSITIONING'):
            with patch.object(helper, 'plc_status', return_value=status):
                with self.assertRaises(ValueError): helper.preflight()
        with patch.object(helper, 'plc_status', return_value='STOPPED'), patch.object(helper, 'native_request', return_value={'runtimeReady':True,'ioReady':True,'outputsAtConfiguredSafeState':True,'axes':[{'state':'Standstill','fault':None}]}):
            with self.assertRaises(ValueError): helper.preflight()

    def test_apply_copies_and_proves_before_success(self):
        with tempfile.TemporaryDirectory() as directory:
            base = pathlib.Path(directory)
            build = 'a' * 64
            generated = base / 'candidates' / build / 'core' / 'generated'
            generated.mkdir(parents=True)
            binary = base / 'core'
            binary.write_bytes(b'reviewed-native-binary')
            inventory = {}
            for name in ('machine-config.json', 'io-map.json'):
                (generated / name).write_bytes(b'{}')
                inventory[name] = hashlib.sha256(b'{}').hexdigest()
            topology = dict(schemaVersion=1, readyForRuntime=True, blockers=[], slaves=[{'position':0}],
                            machineSha256=inventory['machine-config.json'], ioMapSha256=inventory['io-map.json'])
            topology['topologyFingerprint'] = hashlib.sha256(helper.canonical_identity(topology)).hexdigest()
            data = json.dumps(topology).encode()
            (generated / 'ethercat-runtime.json').write_bytes(data)
            inventory['ethercat-runtime.json'] = hashlib.sha256(data).hexdigest()
            (generated / 'build-manifest.json').write_text(json.dumps(dict(BuildHash=build, Files=inventory)))
            installation = base / 'installation.json'
            installation.write_text(json.dumps(dict(binary=str(binary), binarySha256=hashlib.sha256(binary.read_bytes()).hexdigest())))
            snapshot = {'sampleCycle':1,'sampleMonotonicNs':100,'runtimeReady':True,'ioReady':True,'outputsAtConfiguredSafeState':True,'lastAppliedCommandSequence':0,'axes':[{'state':'Disabled','fault':None}]}
            with patch.object(helper, 'INSTALLATION', installation), patch.object(helper, 'CANDIDATES', base / 'candidates'), \
                 patch.object(helper, 'VERSIONS', base / 'versions'), patch.object(helper, 'root_owned'), \
                 patch.object(helper.os, 'geteuid', return_value=0, create=True), patch.object(helper.os, 'chown', create=True), \
                 patch.dict(sys.modules, {'grp':types.SimpleNamespace(getgrnam=lambda _:types.SimpleNamespace(gr_gid=1000))}), \
                 patch.object(helper, 'publish_active') as publish, patch.object(helper, 'apply_lock', return_value=contextlib.nullcontext()), patch.object(helper, 'preflight') as preflight, patch.object(helper, 'command') as command, \
                 patch.object(helper, 'wait_for_core_release') as release, patch.object(helper, 'native_request', side_effect=[{'runtimeReady':True,'ethercatTopologyFingerprint':topology['topologyFingerprint']},snapshot,dict(snapshot,sampleCycle=2,sampleMonotonicNs=200)]) as native:
                result = helper.apply(build)
                self.assertEqual(result['status'], 'applied')
                publish.assert_called_once()
                self.assertEqual(preflight.call_count, 1)
                commands = [call.args[0] for call in command.call_args_list]
                self.assertEqual(commands[0][1], '--validate-ethercat-config')
                self.assertEqual(commands[1], ['systemctl','stop',helper.SERVICE])
                self.assertEqual(commands[3][0], 'systemd-run')
                for prop in ('LimitMEMLOCK=infinity','LimitRTPRIO=95','NoNewPrivileges=yes','UMask=0027',
                             'TimeoutStopSec=5','KillMode=control-group','RuntimeMaxSec=infinity'):
                    self.assertIn('--property=' + prop, commands[3])
                self.assertEqual((base / 'versions' / topology['topologyFingerprint'] / 'ethercat-runtime.json').read_bytes(), data)
                preflight.assert_called_once_with(allow_initial_start=False)
                installation.write_text(json.dumps(dict(binary=str(binary), binarySha256=hashlib.sha256(binary.read_bytes()).hexdigest(),allowInitialStart=True)))
                preflight.reset_mock()
                preflight.return_value = True
                command.reset_mock()
                publish.reset_mock()
                native.side_effect = [{'runtimeReady':True,'ethercatTopologyFingerprint':topology['topologyFingerprint']},snapshot,dict(snapshot,sampleCycle=2,sampleMonotonicNs=200)]
                self.assertEqual(helper.apply(build)['status'], 'applied')
                self.assertEqual(release.call_count,2)
                preflight.assert_called_once_with(allow_initial_start=True)
                commands = [call.args[0] for call in command.call_args_list]
                self.assertEqual(len(commands), 2)
                self.assertEqual(commands[0][1], '--validate-ethercat-config')
                self.assertEqual(commands[1][0], 'systemd-run')

                trace_args = ['--axis-trace-csv','/var/lib/st-workbench-ethercat/traces/test.csv','--axis-trace-capacity','100']
                with patch.object(helper, 'axis_trace_arguments', return_value=trace_args), patch.object(helper, 'prepare_axis_trace'):
                    command.reset_mock()
                    native.side_effect = [{'runtimeReady':True,'ethercatTopologyFingerprint':topology['topologyFingerprint']},snapshot,dict(snapshot,sampleCycle=2,sampleMonotonicNs=200)]
                    self.assertEqual(helper.apply(build)['status'], 'applied')
                    validation = command.call_args_list[0].args[0]
                    self.assertEqual(validation[-4:], trace_args)
                    self.assertIn('--axis-trace-csv', command.call_args_list[-1].args[0])
                    preflight.reset_mock(); command.reset_mock(); native.reset_mock(); publish.reset_mock()
                    command.side_effect = ValueError('trace validation failed')
                    with self.assertRaisesRegex(ValueError,'trace validation'): helper.apply(build)
                    self.assertEqual(command.call_count,1)
                    preflight.assert_not_called(); native.assert_not_called(); publish.assert_not_called()

    def test_apply_lock_refuses_concurrent_owner_and_closes_fd(self):
        flock = unittest.mock.Mock(side_effect=BlockingIOError('busy'))
        with patch.dict(sys.modules, {'fcntl':types.SimpleNamespace(flock=flock,LOCK_EX=2,LOCK_NB=4,LOCK_UN=8)}), \
             patch.object(helper, 'root_owned'), patch.object(helper.os, 'open', return_value=42), \
             patch.object(helper.os, 'fstat', return_value=types.SimpleNamespace(st_uid=0,st_mode=stat.S_IFREG | 0o600)), \
             patch.object(helper.os, 'close') as close:
            with self.assertRaisesRegex(ValueError, 'already running'):
                with helper.apply_lock(): self.fail('Concurrent lock admitted')
            flock.assert_called_once_with(42, 6)
            close.assert_called_once_with(42)

    def test_apply_lock_unlocks_on_exception_and_rejects_unsafe_file(self):
        flock = unittest.mock.Mock()
        safe = types.SimpleNamespace(st_uid=0,st_mode=stat.S_IFREG | 0o600)
        with patch.dict(sys.modules, {'fcntl':types.SimpleNamespace(flock=flock,LOCK_EX=2,LOCK_NB=4,LOCK_UN=8)}), \
             patch.object(helper, 'root_owned'), patch.object(helper.os, 'open', return_value=42), \
             patch.object(helper.os, 'fstat', return_value=safe) as fstat, patch.object(helper.os, 'close') as close:
            with self.assertRaisesRegex(RuntimeError, 'apply failed'):
                with helper.apply_lock(): raise RuntimeError('apply failed')
            self.assertEqual(flock.call_args_list, [unittest.mock.call(42,6),unittest.mock.call(42,8)])
            close.assert_called_once_with(42)
            fstat.return_value = types.SimpleNamespace(st_uid=1000,st_mode=stat.S_IFREG | 0o600)
            with self.assertRaisesRegex(ValueError, 'root-owned'):
                with helper.apply_lock(): self.fail('User-owned lock admitted')

    def test_entire_privileged_apply_is_inside_lock(self):
        events = []
        @contextlib.contextmanager
        def lock():
            events.append('locked')
            try: yield
            finally: events.append('unlocked')
        def privileged(build):
            self.assertEqual(events, ['locked'])
            events.append('all apply operations')
            return {'status':'applied'}
        with patch.object(helper, 'apply_lock', side_effect=lock), patch.object(helper, '_apply_locked', side_effect=privileged), \
             patch.object(helper.os, 'geteuid', return_value=0, create=True):
            helper.apply('a'*64)
        self.assertEqual(events, ['locked','all apply operations','unlocked'])

    def test_outputs_must_be_explicitly_safe_even_with_no_axes(self):
        sample = dict(runtimeReady=True,ioReady=True,lastAppliedCommandSequence=0,axes=[])
        for value in (None, False, 1, 'true'):
            with self.assertRaisesRegex(ValueError, 'safe state'):
                helper.disabled_snapshot(dict(sample,outputsAtConfiguredSafeState=value), require_zero=True)
        helper.disabled_snapshot(dict(sample,outputsAtConfiguredSafeState=True), require_zero=True)

    def test_initial_start_requires_explicit_permission_and_absent_inactive_core(self):
        for load, active in [('not-found','inactive'),('loaded','inactive'),('loaded','failed')]:
            reply = types.SimpleNamespace(returncode=0,stdout=f'LoadState={load}\nActiveState={active}\nMainPID=0\n')
            with patch.object(helper, 'plc_status', return_value='STOPPED'), patch.object(helper.os.path, 'lexists', return_value=False), \
                 patch.object(helper, 'command', return_value=reply), patch.object(helper, 'native_request') as native:
                helper.preflight(allow_initial_start=True)
                native.assert_not_called()
        with patch.object(helper, 'plc_status', return_value='STOPPED'), patch.object(helper.os.path, 'lexists', return_value=False), \
             patch.object(helper, 'native_request', side_effect=OSError('absent')):
            with self.assertRaises(OSError): helper.preflight()
        reply = types.SimpleNamespace(returncode=0,stdout='LoadState=loaded\nActiveState=active\nMainPID=10\n')
        with patch.object(helper, 'plc_status', return_value='STOPPED'), patch.object(helper.os.path, 'lexists', return_value=False), \
             patch.object(helper, 'command', return_value=reply):
            with self.assertRaisesRegex(ValueError, 'inactive'): helper.preflight(allow_initial_start=True)
        with patch.object(helper, 'plc_status', return_value='STOPPED'), patch.object(helper.os.path, 'lexists', return_value=True), \
             patch.object(helper, 'native_request', side_effect=OSError('stale socket')):
            with self.assertRaises(OSError): helper.preflight(allow_initial_start=True)

    def test_failed_cleanup_preserves_unknown_outcome(self):
        with patch.object(helper, 'command', side_effect=helper.subprocess.TimeoutExpired('stop', 20)):
            result = helper.failed_result({'topologyFingerprint':'a'*64}, RuntimeError('launch failed'))
        self.assertEqual(result['status'], 'failed')
        self.assertEqual(result['nativeState'], 'unknown')
        self.assertIn('cleanupError', result)
        self.assertEqual(result['error'], 'launch failed')

    def test_active_record_publishes_only_verified_binding_atomically(self):
        with tempfile.TemporaryDirectory() as directory:
            active = pathlib.Path(directory) / 'active.json'
            verified = dict(buildHash='a'*64,topologyFingerprint='b'*64,machineSha256='c'*64,ioMapSha256='d'*64,status='validated')
            with patch.object(helper, 'ACTIVE_RECORD', active), patch.object(helper, 'root_owned'), patch.object(helper.os, 'chown', create=True):
                helper.publish_active(verified, 1000)
            self.assertEqual(json.loads(active.read_text()), {key:verified[key] for key in ('buildHash','topologyFingerprint','machineSha256','ioMapSha256')})
            self.assertEqual([entry.name for entry in active.parent.iterdir()], ['active.json'])

    def test_plc_status_uses_authenticated_fixed_https_rest(self):
        requests = []
        class Response:
            def __init__(self, data): self.data = data
            def __enter__(self): return self
            def __exit__(self, *args): pass
            def read(self, limit): return self.data[:limit]
        def open_request(request, timeout):
            requests.append((request,timeout))
            if request.full_url.endswith('/api/login'):
                self.assertEqual(json.loads(request.data), {'username':'a','password':'private'})
                return Response(b'{"access_token":"private-token"}')
            self.assertEqual(request.get_header('Authorization'),'Bearer private-token')
            return Response(b'{"status":"STATUS:STOPPED"}')
        opener = types.SimpleNamespace(open=open_request)
        with patch.dict(sys.modules, {'pwd':types.SimpleNamespace(getpwnam=lambda _:types.SimpleNamespace(pw_uid=1000))}), \
             patch.object(helper, 'read_regular', side_effect=[b'{"username":"a","password":"private"}',b'test-ca']), \
             patch.object(helper.ssl, 'create_default_context') as context, \
             patch.object(helper.urllib.request, 'build_opener', return_value=opener), \
             patch.object(helper.socket, 'socket', side_effect=AssertionError('Native PLC socket must not be used')):
            self.assertEqual(helper.plc_status(), 'STOPPED')
            context.assert_called_once_with(cadata='test-ca')
        self.assertEqual([(request.full_url,timeout) for request,timeout in requests],
                         [('https://localhost:8443/api/login',3),('https://localhost:8443/api/status',3)])

    def test_rest_status_rejects_unrecognized_status_and_oversize(self):
        class Response:
            def __init__(self, data): self.data=data
            def __enter__(self): return self
            def __exit__(self,*args): pass
            def read(self,limit): return self.data[:limit]
        for raw in (b'{"status":"STOPPED"}',b'{"status":"STATUS:STOPPED\\n"}',b'x'*(1024*1024+1)):
            opener = types.SimpleNamespace(open=unittest.mock.Mock(side_effect=[Response(b'{"access_token":"token"}'),Response(raw)]))
            with patch.dict(sys.modules, {'pwd':types.SimpleNamespace(getpwnam=lambda _:types.SimpleNamespace(pw_uid=1000))}), \
                 patch.object(helper, 'read_regular', side_effect=[b'{}',b'test-ca']), patch.object(helper.ssl,'create_default_context'), \
                 patch.object(helper.urllib.request,'build_opener',return_value=opener):
                with self.assertRaises(ValueError): helper.plc_status()

    def test_credentials_descriptor_requires_a_owner_and_mode0600(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / 'credentials.json'
            path.write_bytes(b'{}')
            for uid, mode in [(1001,0o600),(1000,0o644)]:
                info = types.SimpleNamespace(st_uid=uid,st_mode=stat.S_IFREG|mode,st_size=2)
                with patch.object(helper.os,'fstat',return_value=info):
                    with self.assertRaises(ValueError): helper.read_regular(path,required_uid=1000,required_mode=0o600)
        with self.assertRaises(ValueError): helper.NoRestRedirect().redirect_request(None,None,302,'redirect',{},'https://elsewhere/')

    def test_fixed_service_and_candidate_prefix(self):
        self.assertEqual(helper.SERVICE, 'st-workbench-is620n.service')
        self.assertEqual(helper.APPLY_LOCK.as_posix(), '/run/stworkbench-ethercat-apply.lock')
        self.assertTrue(helper.CANDIDATES.as_posix().endswith('/deployment/.versions'))
        with self.assertRaises(ValueError): helper.apply('..')

if __name__ == '__main__': unittest.main()
