import copy,hashlib,json,os,pathlib,sys,tempfile,unittest
from unittest.mock import patch
sys.path.insert(0,str(pathlib.Path(__file__).parent))
import axis_gate,coordinator,ethercat_hardware

def manifest():
    return dict(SchemaVersion=1,InterfaceVersion='workbench-axis-v2',PlcIntervalMilliseconds=10,CommandBytes=4112,FeedbackBytes=6656,
        RequiredRuntimeCapabilities=['workbench_axis_move_profile_v2'],Axes=[dict(Index=1,Name='X',ControllerAxis='Axis1',Unit='rev',MinPosition=-2,MaxPosition=2,MaximumVelocity=1,MaximumAcceleration=1,MaximumDeceleration=1,MaximumJerk=0)])
class Tests(unittest.TestCase):
    def test_v2_explicit_layout_and_limits(self):
        self.assertEqual(axis_gate.validate_manifest(manifest()),manifest())
        for key,value in [('CommandBytes',2576),('CommandBytes',True),('FeedbackBytes',0),('RequiredRuntimeCapabilities',[]),('RequiredRuntimeCapabilities',['unknown'])]:
            bad=manifest();bad[key]=value
            with self.subTest(key=key,value=value),self.assertRaises(ValueError):axis_gate.validate_manifest(bad)
        for key,value in [('MaximumAcceleration',None),('MaximumDeceleration',0),('MaximumJerk',None),('MaximumJerk',-1)]:
            bad=manifest();bad['Axes'][0][key]=value
            with self.subTest(key=key),self.assertRaises(ValueError):axis_gate.validate_manifest(bad)
    def test_real_v2_archive_passes_inventory_and_interface_gate(self):
        import hashlib,zipfile
        with tempfile.TemporaryDirectory() as directory:
            root=pathlib.Path(directory);archive=root/'program.zip'
            files={name:b'content' for name in (axis_gate.FILES-{'axes/axis_worker.py'})|{'axes/axis_worker_v2.py'}}
            files['axis-manifest.json']=json.dumps(manifest()).encode()
            files['axis-constants.st']=b'VAR_GLOBAL CONSTANT\n    AXIS_X : AXIS_REF := 1;\nEND_VAR\n'
            files.update({name:b'content' for name in ('generated.cpp','generated.hpp','generated_debug.cpp','defines.h','configuration.st','strucpp_runtime/include/debug_table.hpp')})
            files['debug-map.json']=b'{}'
            top=dict(BuildHash='A'*64,RuntimeCommit=coordinator.RUNTIME_COMMIT,CompilerVersion='STruC++ version 0.7.0',
                     AxisInterfaceVersion=axis_gate.INTERFACE_V2,AxisExportMarker='workbench_axis_v2',
                     SymbolTableHash=hashlib.sha256(b'{}').hexdigest(),
                     Files={name:hashlib.sha256(raw).hexdigest() for name,raw in files.items()})
            with zipfile.ZipFile(archive,'w') as output:
                for name,raw in files.items():output.writestr(name,raw)
                output.writestr('build-manifest.json',json.dumps(top))
            self.assertEqual(top,coordinator.validate_archive(archive,hashlib.sha256(archive.read_bytes()).hexdigest(),'A'*64))

    def test_v2_requires_distinct_configured_channel(self):
        config=dict(axis_backend='EcMotion',axis_socket_path='/home/a/.local/share/st-workbench/is620n-axis/axis.sock',axis_v2_socket_path='/home/a/.local/share/st-workbench/axis-v2/axis.sock')
        self.assertEqual(axis_gate.channel('workbench-axis-v2',config)['socket'],config['axis_v2_socket_path'])
        config['axis_v2_socket_path']=config['axis_socket_path']
        with self.assertRaises(ValueError):axis_gate.channel('workbench-axis-v2',config)
    def test_v2_requires_library_export(self):
        top=dict(AxisInterfaceVersion='workbench-axis-v2',AxisExportMarker='workbench_axis_v2')
        with self.assertRaises(coordinator.DeploymentError):coordinator.verify_candidate_exports(top,coordinator.REQUIRED_EXPORTS)
        coordinator.verify_candidate_exports(top,coordinator.REQUIRED_EXPORTS|{'workbench_axis_v2'})
    def test_old_core_rejected_before_worker_contact(self):
        with tempfile.TemporaryDirectory() as directory:
            root=pathlib.Path(directory);(root/'axis-manifest.json').write_text(json.dumps(manifest()))
            config=dict(axis_backend='EcMotion',axis_v2_socket_path='/home/a/.local/share/st-workbench/axis-v2/axis.sock')
            with patch.object(ethercat_hardware,'native_request',return_value={'runtimeReady':True,'runtimeCapabilities':[]}),patch.object(axis_gate,'read_private_json') as worker:
                with self.assertRaisesRegex(ValueError,'capabilit'):axis_gate.verify_ready({'AxisInterfaceVersion':'workbench-axis-v2'},root,config,'')
                worker.assert_not_called()
    def test_v2_worker_source_identity_matches_both_private_installed_and_candidate(self):
        with tempfile.TemporaryDirectory() as directory:
            root=pathlib.Path(directory);generated=root/'generated';(generated/'axes').mkdir(parents=True)
            private=root/'private';private.mkdir(mode=0o700)
            source=b'approved worker';packaged=generated/'axes/axis_worker_v2.py';installed=private/'axis_worker_v2.py'
            packaged.write_bytes(source);installed.write_bytes(source);installed.chmod(0o600)
            top=dict(Files={'axes/axis_worker_v2.py':hashlib.sha256(source).hexdigest().upper()})
            with patch.object(axis_gate.os,'getuid',return_value=installed.stat().st_uid,create=True),patch.object(axis_gate.stat,'S_IMODE',return_value=0o600):
                axis_gate.verify_v2_worker_source(top,generated,str(private/'axis.sock'))
                installed.write_bytes(b'old worker')
                with self.assertRaisesRegex(ValueError,'worker'):axis_gate.verify_v2_worker_source(top,generated,str(private/'axis.sock'))
                installed.write_bytes(source);packaged.write_bytes(b'changed package')
                with self.assertRaisesRegex(ValueError,'worker'):axis_gate.verify_v2_worker_source(top,generated,str(private/'axis.sock'))
    @unittest.skipUnless(os.name=='posix','POSIX installed worker ownership/mode')
    def test_v2_worker_source_rejects_nonprivate_and_symlink_installed_script(self):
        with tempfile.TemporaryDirectory() as directory:
            root=pathlib.Path(directory);(root/'axes').mkdir();source=b'worker'
            packaged=root/'axes/axis_worker_v2.py';packaged.write_bytes(source)
            private=root/'private';private.mkdir(mode=0o700);installed=private/'axis_worker_v2.py';installed.write_bytes(source);installed.chmod(0o644)
            top=dict(Files={'axes/axis_worker_v2.py':hashlib.sha256(source).hexdigest()})
            with self.assertRaisesRegex(ValueError,'0600'):axis_gate.verify_v2_worker_source(top,root,str(private/'axis.sock'))
            installed.unlink();installed.symlink_to(packaged)
            with self.assertRaisesRegex(ValueError,'Symlink'):axis_gate.verify_v2_worker_source(top,root,str(private/'axis.sock'))
    def test_v2_source_mismatch_rejected_before_worker_sidecar_contact(self):
        with tempfile.TemporaryDirectory() as directory:
            root=pathlib.Path(directory);(root/'axis-manifest.json').write_text(json.dumps(manifest()))
            endpoint='/home/a/.local/share/st-workbench/axis-v2/axis.sock'
            config=dict(axis_backend='EcMotion',axis_v2_socket_path=endpoint)
            environment='STWORKBENCH_AXIS_BACKEND=EcMotion STWORKBENCH_AXIS_V2_SOCKET='+endpoint
            with patch.object(ethercat_hardware,'native_request',return_value=dict(runtimeReady=True,runtimeCapabilities=list(axis_gate.PROFILE_CAPABILITIES))),patch.object(axis_gate,'verify_v2_worker_source',side_effect=ValueError('worker mismatch')),patch.object(axis_gate,'read_private_json') as contact:
                with self.assertRaisesRegex(ValueError,'worker mismatch'):axis_gate.verify_ready(dict(AxisInterfaceVersion=axis_gate.INTERFACE_V2),root,config,environment)
                contact.assert_not_called()
if __name__=='__main__':unittest.main()
