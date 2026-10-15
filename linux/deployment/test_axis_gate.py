import copy
import hashlib
import json
import os
import pathlib
import stat
import tempfile
import time
import types
import unittest
from unittest.mock import patch
import zipfile

import coordinator
import axis_gate


def axis_manifest():
    return dict(SchemaVersion=1, InterfaceVersion='workbench-axis-v1', PlcIntervalMilliseconds=10,
        Axes=[dict(Index=1, Name='X', ControllerAxis='Axis1', Unit='mm', MinPosition=-100,
        MaxPosition=100, MaximumVelocity=None, MaximumAcceleration=None,
        MaximumDeceleration=None, MaximumJerk=None)])


class AxisGateTests(unittest.TestCase):
    def test_readiness_rejects_hardware_mechanics_without_axis_function_blocks(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            hardware = {'Hardware': {'Axes': [{'Name': 'X', 'Mechanics': {'Kind': 'Linear'}}]}}
            (root / 'hardware-configuration.json').write_text(json.dumps(hardware))
            with patch.object(axis_gate, 'read_private_json') as capability:
                with self.assertRaisesRegex(ValueError, 'mechanical/rotary/homing configuration is offline only'):
                    axis_gate.verify_ready({}, root, {}, '')
                capability.assert_not_called()

    def test_readiness_accepts_legacy_hardware_without_axis_function_blocks(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            for axis in ({'Name': 'X'}, {'Name': 'X', 'Mechanics': None}):
                (root / 'hardware-configuration.json').write_text(json.dumps({'Hardware': {'Axes': [axis]}}))
                with self.subTest(axis=axis):
                    axis_gate.verify_ready({}, root, {}, '')

    def test_archive_preserves_explicit_mechanics_for_offline_compilation(self):
        with tempfile.TemporaryDirectory() as directory:
            data = axis_manifest()
            data['Axes'][0]['Mechanics'] = {'Kind': 'Linear', 'FeedbackUnit': 'EncoderCounts'}
            path, checksum, build = self.bundle(pathlib.Path(directory), data)
            coordinator.validate_archive(path, checksum, build)
            with zipfile.ZipFile(path) as archive:
                self.assertEqual(data, json.loads(archive.read('axis-manifest.json')))

    def test_readiness_rejects_explicit_mechanics_before_worker_handshake(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            for mechanics in ({'Kind': 'Rotary', 'RotaryMode': 'Modulo'}, {}, False, 0, ''):
                data = axis_manifest()
                data['Axes'][0]['Mechanics'] = mechanics
                (root / 'axis-manifest.json').write_text(json.dumps(data))
                with self.subTest(mechanics=mechanics), patch.object(axis_gate, 'read_private_json') as capability:
                    with self.assertRaisesRegex(ValueError,
                            'mechanical/rotary/homing configuration is offline only; controller apply/confirm is not implemented'):
                        axis_gate.verify_ready({'AxisInterfaceVersion': 'workbench-axis-v1'}, root, {}, '')
                    capability.assert_not_called()

    def test_manifest_accepts_legacy_missing_or_null_mechanics(self):
        data = axis_manifest()
        self.assertIs(data, axis_gate.validate_manifest(data))
        data['Axes'][0]['Mechanics'] = None
        self.assertIs(data, axis_gate.validate_manifest(data))

    def test_activation_refuses_mechanics_even_with_prior_readiness_record(self):
        from test_coordinator import Tests
        fixture = Tests(); fixture.setUp()
        try:
            data = axis_manifest()
            data['Axes'][0]['Mechanics'] = {'Kind': 'Linear', 'HomeMode': 'Switch'}
            path, checksum, build = self.bundle(fixture.root, data)
            fixture.request('stage', package_path=str(path), package_sha256=checksum, build_hash=build)
            active = fixture.runtime / 'build/libplc_existing.so'; active.write_bytes(b'baseline')
            baseline = fixture.request('adopt-baseline')['active']
            version = fixture.coordinator.version(build); (version / 'build').mkdir()
            library = version / 'build/new_libplc.so'; library.write_bytes(b'candidate')
            coordinator.atomic_json(version / 'compile-result.json', dict(state='compiled', exports_verified=True,
                library_sha256=coordinator.digest(library), axis_ready=True))
            fixture.daemon.commands.clear()
            with patch.object(coordinator.subprocess, 'run', return_value=types.SimpleNamespace(stdout='')), \
                    patch.object(fixture.coordinator, 'service') as service, \
                    patch.object(axis_gate, 'read_private_json') as capability:
                with self.assertRaisesRegex(coordinator.DeploymentError, 'mechanical/rotary/homing configuration is offline only'):
                    fixture.request('activate', build_hash=build, expected_active_build_hash=baseline['build_hash'])
                service.assert_not_called()
                capability.assert_not_called()
            self.assertEqual([], fixture.daemon.commands)
            self.assertEqual(b'baseline', active.read_bytes())
            self.assertFalse(fixture.coordinator.transaction_path.exists())
        finally:
            fixture.tearDown()

    def test_nonaxis_candidate_compiles_offline_but_activation_rechecks_hardware_mechanics(self):
        from test_coordinator import Tests
        fixture = Tests(); fixture.setUp()
        try:
            hardware = {'Hardware': {'Axes': [{'Name': 'X', 'Mechanics': {'Kind': 'Linear'}}]}}
            path, checksum, build = fixture.package({'hardware-configuration.json': json.dumps(hardware).encode()})
            fixture.request('stage', package_path=str(path), package_sha256=checksum, build_hash=build)
            active = fixture.runtime / 'build/libplc_existing.so'; active.write_bytes(b'baseline')
            baseline = fixture.request('adopt-baseline')['active']
            version = fixture.coordinator.version(build); (version / 'build').mkdir()
            library = version / 'build/new_libplc.so'; library.write_bytes(b'candidate')
            original = coordinator.read_json
            def read(path):
                return {'source_hashes': {}} if pathlib.Path(path).name == 'runtime-profile.json' else original(path)
            def compile_command(args, directory, output, timeout):
                pathlib.Path(output).write_text('\n'.join(coordinator.REQUIRED_EXPORTS) if args[0] == 'nm' else 'compiled')
                return 0
            with patch.object(coordinator, 'read_json', side_effect=read), patch.object(coordinator, 'run_bounded', side_effect=compile_command):
                result = fixture.request('compile', build_hash=build)
            self.assertEqual('compiled', result['state'])
            self.assertFalse(result['axis_ready'])
            self.assertIn('controller apply/confirm is not implemented', result['axis_message'])
            # A forged/old readiness result must not bypass the fresh activation check.
            result['axis_ready'] = True
            coordinator.atomic_json(version / 'compile-result.json', result)
            fixture.daemon.commands.clear()
            with patch.object(fixture.coordinator, 'service') as service, patch.object(axis_gate, 'read_private_json') as capability:
                with self.assertRaisesRegex(coordinator.DeploymentError, 'mechanical/rotary/homing configuration is offline only'):
                    fixture.request('activate', build_hash=build, expected_active_build_hash=baseline['build_hash'])
                service.assert_not_called()
                capability.assert_not_called()
            self.assertEqual([], fixture.daemon.commands)
            self.assertEqual(b'baseline', active.read_bytes())
            self.assertFalse(fixture.coordinator.transaction_path.exists())
        finally:
            fixture.tearDown()

    def bundle(self, root, document=None, flagged=True, omit=None, constants=None):
        files = {name: b'fixture' for name in ('generated.cpp', 'generated.hpp', 'generated_debug.cpp',
            'defines.h', 'configuration.st', 'strucpp_runtime/include/debug_table.hpp',
            'axis_control.st', 'axis_bridge.hpp', 'axis_bridge.cpp', 'generated_axis_scan_shim.cpp',
            'axis_lifecycle.inc', 'axes/axis_worker.py', 'axes/LICENSE')}
        files['debug-map.json'] = b'{}'
        files['axis-manifest.json'] = json.dumps(document or axis_manifest()).encode()
        files['axis-constants.st'] = b'VAR_GLOBAL CONSTANT\n    AXIS_X : AXIS_REF := 1;\nEND_VAR\n'
        if constants is not None: files['axis-constants.st']=constants
        if omit: del files[omit]
        manifest = dict(BuildHash='A'*64, RuntimeCommit=coordinator.RUNTIME_COMMIT,
            CompilerVersion='STruC++ version 0.7.0', SymbolTableHash=hashlib.sha256(b'{}').hexdigest(),
            Files={key: hashlib.sha256(value).hexdigest().upper() for key,value in files.items()})
        if flagged: manifest['AxisInterfaceVersion'] = 'workbench-axis-v1'
        path = root / 'candidate.zip'
        with zipfile.ZipFile(path,'w') as archive:
            for name,value in files.items(): archive.writestr(name,value)
            archive.writestr('build-manifest.json',json.dumps(manifest))
        return path, coordinator.digest(path), 'A'*64

    def test_valid_axis_archive_and_required_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            root=pathlib.Path(directory)
            self.assertEqual('workbench-axis-v1', coordinator.validate_archive(*self.bundle(root))['AxisInterfaceVersion'])
            for kwargs in ({'flagged':False}, {'omit':'axis_bridge.cpp'}, {'omit':'axes/LICENSE'},
                {'constants':b'VAR_GLOBAL CONSTANT\n    AXIS_X : AXIS_REF := 2;\nEND_VAR\n'}):
                with self.subTest(kwargs=kwargs), self.assertRaises(coordinator.DeploymentError):
                    coordinator.validate_archive(*self.bundle(root,**kwargs))

    def test_invalid_manifest_rejected(self):
        changes=[('SchemaVersion', True), ('PlcIntervalMilliseconds', True), ('PlcIntervalMilliseconds',1001),
            ('Axes',[])]
        with tempfile.TemporaryDirectory() as directory:
            for key,value in changes:
                data=axis_manifest(); data[key]=value
                with self.subTest(key=key,value=value), self.assertRaises(coordinator.DeploymentError):
                    coordinator.validate_archive(*self.bundle(pathlib.Path(directory),data))
            for key,value in [('Index',2),('MinPosition',float('nan')),('MaximumVelocity',-1),
                ('MaximumAcceleration',True),('ControllerAxis',''),('Name','bad-name')]:
                data=axis_manifest(); data['Axes'][0][key]=value
                with self.subTest(key=key), self.assertRaises(coordinator.DeploymentError):
                    coordinator.validate_archive(*self.bundle(pathlib.Path(directory),data))

    def test_manifest_binding_order_duplicates_and_caps(self):
        for change in ('duplicate', 'order', 'unknown', 'missing_cap', 'bool_cap', 'infinite', 'too_many'):
            data=axis_manifest()
            second=copy.deepcopy(data['Axes'][0]); second.update(Index=2,Name='Y',ControllerAxis='Axis2')
            data['Axes'].append(second)
            if change=='duplicate': second['ControllerAxis']='axis1'
            elif change=='order': data['Axes'][0]['Name']='Z'
            elif change=='unknown': second['Unknown']=1
            elif change=='missing_cap': del second['MaximumJerk']
            elif change=='bool_cap': second['MaximumJerk']=True
            elif change=='infinite': second['MaxPosition']=float('inf')
            else: data['Axes']*=33
            with self.subTest(change=change),self.assertRaises(ValueError): axis_gate.validate_manifest(data)
        with self.assertRaises(ValueError): axis_gate.strict_json(b'{"SchemaVersion":1,"SchemaVersion":1}')

    def test_private_capability_rejects_stale_oversized_and_duplicate_json(self):
        with tempfile.TemporaryDirectory() as directory:
            path=pathlib.Path(directory)/'capability.json'
            owner=getattr(os,'getuid',lambda:0)()
            def info():
                return types.SimpleNamespace(st_uid=owner,st_mode=stat.S_IFREG|0o600,
                    st_size=path.stat().st_size,st_mtime=path.stat().st_mtime)
            with patch('os.getuid',return_value=owner,create=True),patch('os.fstat',side_effect=lambda fd:info()):
                path.write_text('{"ready":true}')
                self.assertEqual({'ready':True},axis_gate.read_private_json(path))
                os.utime(path,(time.time()-10,time.time()-10))
                with self.assertRaisesRegex(ValueError,'fresh'): axis_gate.read_private_json(path)
                path.write_bytes(b'x'*65537)
                with self.assertRaises(ValueError): axis_gate.read_private_json(path)
                path.write_text('{"a":1,"a":2}')
                with self.assertRaisesRegex(ValueError,'Duplicate'): axis_gate.read_private_json(path)

    def test_activation_refuses_unverified_axis_before_runtime_or_disk_writes(self):
        from test_coordinator import Tests
        fixture=Tests(); fixture.setUp()
        try:
            path,checksum,build=self.bundle(fixture.root)
            fixture.request('stage',package_path=str(path),package_sha256=checksum,build_hash=build)
            active=fixture.runtime/'build/libplc_existing.so'; active.write_bytes(b'baseline')
            baseline=fixture.request('adopt-baseline')['active']
            version=fixture.coordinator.version(build); (version/'build').mkdir()
            library=version/'build/new_libplc.so'; library.write_bytes(b'candidate')
            coordinator.atomic_json(version/'compile-result.json',dict(state='compiled',exports_verified=True,
                library_sha256=coordinator.digest(library),axis_ready=False))
            fixture.daemon.commands.clear()
            with patch.object(fixture.coordinator,'service') as service:
                with self.assertRaisesRegex(coordinator.DeploymentError,'readiness'):
                    fixture.request('activate',build_hash=build,expected_active_build_hash=baseline['build_hash'])
                service.assert_not_called()
            self.assertEqual([],fixture.daemon.commands)
            self.assertEqual(b'baseline',active.read_bytes())
            self.assertFalse(fixture.coordinator.transaction_path.exists())
        finally: fixture.tearDown()

    def test_compile_reports_axis_readiness_without_applying_cycle_configuration(self):
        from test_coordinator import Tests
        fixture=Tests(); fixture.setUp()
        try:
            path,checksum,build=self.bundle(fixture.root)
            fixture.request('stage',package_path=str(path),package_sha256=checksum,build_hash=build)
            version=fixture.coordinator.version(build); (version/'build').mkdir()
            (version/'build/new_libplc.so').write_bytes(b'candidate')
            original=coordinator.read_json
            def read(path):
                return {'source_hashes':{}} if pathlib.Path(path).name=='runtime-profile.json' else original(path)
            def compile_command(args,directory,output,timeout):
                pathlib.Path(output).write_text('\n'.join(coordinator.REQUIRED_EXPORTS) if args[0]=='nm' else 'compiled')
                return 0
            with patch.object(coordinator,'read_json',side_effect=read),patch.object(coordinator,'run_bounded',side_effect=compile_command):
                result=fixture.request('compile',build_hash=build)
                self.assertFalse(result['axis_ready'])
                fixture.coordinator.config['axis_backend']='EcMotionSimulator'
                with patch.object(coordinator.subprocess,'run',return_value=types.SimpleNamespace(stdout='isolated-env')), \
                    patch.object(coordinator,'verify_axis_ready') as verify:
                    result=fixture.request('compile',build_hash=build)
                verify.assert_called_once()
                self.assertTrue(result['axis_ready'])
                self.assertIn('EcMotionSimulator',result['axis_message'])
                self.assertIn('not applied',result['axis_message'])
                self.assertNotIn('motion_cycle_ready',result)
            self.assertEqual([],fixture.daemon.commands)
        finally: fixture.tearDown()

    def test_axis_and_combined_lifecycle_hooks(self):
        original=b'extern "C" strucpp::ConfigurationInstance* strucpp_get_config(void) {\n    return &g_config;\n}'
        manifest={'AxisInterfaceVersion':'workbench-axis-v1'}
        source=coordinator.motion_candidate_source('core/strucpp_runtime/runtime_v4_entry.cpp', original, manifest)
        self.assertIn(b'#include "axis_lifecycle.inc"',source)
        self.assertIn(b'if (!workbench_axis_bootstrap()) return nullptr;',source)
        manifest['MotionInterfaceVersion']='workbench-continuous-v1'
        source=coordinator.motion_candidate_source('core/strucpp_runtime/runtime_v4_entry.cpp',original,manifest)
        self.assertIn(b'workbench_motion_bootstrap()',source)
        self.assertIn(b'workbench_axis_bootstrap()',source)
        path_only=coordinator.motion_candidate_source('core/strucpp_runtime/runtime_v4_entry.cpp',original,
            {'MotionInterfaceVersion':'workbench-continuous-v1'})
        expected=b'#include "runtime_lifecycle.inc"\n'+original.replace(b'    return &g_config;',
            b'    if (!workbench_motion_bootstrap()) return nullptr;\n    return &g_config;')
        self.assertEqual(expected,path_only)

    def test_capability_readiness_is_private_fresh_and_backend_specific(self):
        with tempfile.TemporaryDirectory() as directory:
            root=pathlib.Path(directory); raw=json.dumps(axis_manifest()).encode(); (root/'axis-manifest.json').write_bytes(raw)
            endpoint='/tmp/workbench-isolated-axis.sock'
            config=dict(axis_backend='EcMotion',axis_socket_path=endpoint)
            environment='STWORKBENCH_AXIS_BACKEND=EcMotion STWORKBENCH_AXIS_SOCKET='+endpoint
            capability=dict(SchemaVersion=1,InterfaceVersion='workbench-axis-v1',ManifestSha256=hashlib.sha256(raw).hexdigest(),
                Backend='EcMotion',PID=1234,ControllerReady=True,PhysicalBasicAxis=True,
                SimulatorBasicAxis=False,Continuous=False,MotionCycleApplied=False)
            def fake_stat(path):
                return types.SimpleNamespace(st_uid=123,st_mode=(stat.S_IFSOCK if path.as_posix()==endpoint else stat.S_IFREG)|0o600,
                    st_size=100,st_mtime=time.time())
            with patch('pathlib.Path.lstat',fake_stat), patch('pathlib.Path.stat',fake_stat), \
                patch('os.getuid',return_value=123,create=True),patch('os.kill') as kill, \
                patch('coordinator.axis_gate.read_private_json',side_effect=lambda path:copy.deepcopy(capability)):
                coordinator.verify_axis_ready({'AxisInterfaceVersion':'workbench-axis-v1'},root,config,environment)
                kill.assert_called_with(1234,0)
                for key,value in [('PhysicalBasicAxis',False),('Backend','EcMotionSimulator'),('PID',True),
                    ('ControllerReady',False),('ManifestSha256','0'*64),('MotionCycleApplied',True),('Continuous',True)]:
                    original=capability[key]; capability[key]=value
                    with self.subTest(key=key),self.assertRaises(coordinator.DeploymentError):
                        coordinator.verify_axis_ready({'AxisInterfaceVersion':'workbench-axis-v1'},root,config,environment)
                    capability[key]=original
                with self.assertRaisesRegex(coordinator.DeploymentError,'environment'):
                    coordinator.verify_axis_ready({'AxisInterfaceVersion':'workbench-axis-v1'},root,config,'')
                with self.assertRaisesRegex(coordinator.DeploymentError,'mixed'):
                    coordinator.verify_axis_ready({'AxisInterfaceVersion':'workbench-axis-v1','MotionInterfaceVersion':'workbench-continuous-v1'},root,config,environment)
                config['axis_backend']='EcMotionSimulator'
                environment=environment.replace('EcMotion ','EcMotionSimulator ')
                capability.update(Backend='EcMotionSimulator',PhysicalBasicAxis=False,SimulatorBasicAxis=True)
                coordinator.verify_axis_ready({'AxisInterfaceVersion':'workbench-axis-v1'},root,config,environment)


if __name__=='__main__': unittest.main()
