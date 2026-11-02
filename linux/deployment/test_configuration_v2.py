import hashlib,json,os,pathlib,sys,tempfile,unittest
from unittest.mock import patch
sys.path.insert(0,str(pathlib.Path(__file__).parent))
import axis_gate,configuration_download as configuration
from test_axis_gate_v2 import manifest
class Tests(unittest.TestCase):
    def config(self):return dict(axis_backend='EcMotion',axis_socket_path=configuration.AXIS_SOCKET,axis_v2_socket_path='/home/a/.local/share/st-workbench/axis-v2/axis.sock')
    def test_validate_v2_environment_is_separate(self):
        cfg=self.config();environment='STWORKBENCH_AXIS_BACKEND=EcMotion STWORKBENCH_AXIS_SOCKET='+cfg['axis_socket_path']+' STWORKBENCH_AXIS_V2_SOCKET='+cfg['axis_v2_socket_path']
        with patch.object(configuration,'runtime_environment',return_value=environment):configuration.validate_config(cfg,axis_gate.INTERFACE_V2)
        with patch.object(configuration,'runtime_environment',return_value=environment.replace('STWORKBENCH_AXIS_V2_SOCKET','WRONG')):
            with self.assertRaises(ValueError):configuration.validate_config(cfg,axis_gate.INTERFACE_V2)
    def test_plan_rejects_old_core_before_worker_or_service(self):
        with tempfile.TemporaryDirectory() as directory:
            root=pathlib.Path(directory);(root/'axis-manifest.json').write_text(json.dumps(manifest()))
            top={'AxisInterfaceVersion':axis_gate.INTERFACE_V2}
            with patch.object(configuration,'candidate',return_value=(root,top,{})),patch.object(configuration.hw,'check_bundle',return_value={}),patch.object(configuration,'native_state',return_value={'runtimeReady':True,'runtimeCapabilities':[]}),patch.object(configuration,'worker_digest') as worker,patch.object(configuration,'service') as service:
                with self.assertRaisesRegex(ValueError,'capabilit'):configuration.plan(root,self.config())
                worker.assert_not_called();service.assert_not_called()
    def test_only_v2_service_selected(self):
        with patch.object(configuration.subprocess,'run') as run:
            configuration.service('stop',axis_gate.INTERFACE_V2)
            self.assertEqual(run.call_args.args[0],['systemctl','--user','stop','stworkbench-axis-v2.service'])
    def test_first_prepared_candidate_can_plan_axis_without_live_worker(self):
        with tempfile.TemporaryDirectory() as directory:
            root=pathlib.Path(directory);(root/'axis-manifest.json').write_text(json.dumps(manifest()))
            top=dict(AxisInterfaceVersion=axis_gate.INTERFACE_V2,BuildHash='A'*64)
            hello=dict(runtimeReady=True,runtimeCapabilities=list(axis_gate.PROFILE_CAPABILITIES),ethercatTopologyFingerprint='f'*64)
            checked=dict(topologyFingerprint='f'*64,machineSha256='a'*64,ioMapSha256='b'*64)
            with patch.object(configuration,'candidate',return_value=(root,top,{})),patch.object(configuration.hw,'check_bundle',return_value=checked),patch.object(configuration,'native_state',return_value=hello),patch.object(configuration,'validate_config'),patch.object(configuration,'worker_digest',side_effect=FileNotFoundError()),patch.object(configuration.os.path,'lexists',return_value=False),patch.object(configuration,'verify_prepared_v2') as prepared:
                result=configuration.plan(root,self.config());self.assertEqual('axis',result['kind']);self.assertIsNone(result['loaded_axis_manifest_sha256']);prepared.assert_called_once()
    def test_invalid_existing_capability_never_uses_prepared_fallback(self):
        with tempfile.TemporaryDirectory() as directory:
            root=pathlib.Path(directory);(root/'axis-manifest.json').write_text(json.dumps(manifest()))
            hello=dict(runtimeReady=True,runtimeCapabilities=list(axis_gate.PROFILE_CAPABILITIES),ethercatTopologyFingerprint='f'*64)
            with patch.object(configuration,'candidate',return_value=(root,dict(AxisInterfaceVersion=axis_gate.INTERFACE_V2),{})),patch.object(configuration.hw,'check_bundle',return_value=dict(topologyFingerprint='f'*64)),patch.object(configuration,'native_state',return_value=hello),patch.object(configuration,'validate_config'),patch.object(configuration,'worker_digest',side_effect=ValueError('stale capabilities')),patch.object(configuration,'verify_prepared_v2') as prepared:
                with self.assertRaisesRegex(ValueError,'stale'):configuration.plan(root,self.config())
                prepared.assert_not_called()
    def test_prepared_apply_uses_fixed_service_after_native_gate(self):
        with tempfile.TemporaryDirectory() as directory:
            root=pathlib.Path(directory);(root/'axis-manifest.json').write_text(json.dumps(manifest()))
            hello=dict(runtimeReady=True,runtimeCapabilities=list(axis_gate.PROFILE_CAPABILITIES))
            with patch.object(configuration,'candidate',return_value=(root,dict(AxisInterfaceVersion=axis_gate.INTERFACE_V2),{})),patch.object(configuration,'native_state',return_value=hello),patch.object(configuration,'validate_config'),patch.object(configuration.os.path,'lexists',return_value=False),patch.object(configuration,'verify_prepared_v2') as prepared,patch.object(configuration,'manifest_destination'),patch.object(configuration,'verify_stationary'),patch.object(configuration,'native_pid',return_value=123),patch.object(configuration,'service') as service,patch.object(configuration,'install_manifest') as install,patch.object(configuration,'wait_ready'):
                configuration.apply_axis(root,self.config(),'prepare-first')
                prepared.assert_called_once();self.assertEqual([('stop',axis_gate.INTERFACE_V2),('start',axis_gate.INTERFACE_V2)],[call.args for call in service.call_args_list])
                service.reset_mock();install.side_effect=OSError('receipt write failed')
                with self.assertRaisesRegex(OSError,'receipt'):configuration.apply_axis(root,self.config(),'receipt-failed')
                self.assertEqual([('stop',axis_gate.INTERFACE_V2)],[call.args for call in service.call_args_list])
    @unittest.skipUnless(os.name=='posix','POSIX private preparation permissions')
    def test_prepared_metadata_is_exact_and_malformed_rejects(self):
        with tempfile.TemporaryDirectory() as directory:
            version=pathlib.Path(directory)/('A'*64);generated=version/'core/generated';generated.mkdir(parents=True)
            target=pathlib.Path(directory)/'private';target.mkdir(mode=0o700)
            raw=json.dumps(manifest()).encode();worker=b'approved packaged worker'
            hashes={name:hashlib.sha256(value).hexdigest() for name,value in [('axis-manifest.json',raw),('axes/axis_worker_v2.py',worker)]}
            (generated/'axes').mkdir()
            for path,value in [(generated/'axis-manifest.json',raw),(generated/'axes/axis_worker_v2.py',worker),(target/'axis-manifest.json',raw),(target/'axis_worker_v2.py',worker)]:path.write_bytes(value);path.chmod(0o600)
            metadata=dict(SchemaVersion=1,InterfaceVersion=axis_gate.INTERFACE_V2,CommandBytes=4112,FeedbackBytes=6656,RequiredRuntimeCapabilities=manifest()['RequiredRuntimeCapabilities'],WorkerSha256=hashes['axes/axis_worker_v2.py'],ManifestSha256=hashes['axis-manifest.json'],BuildHash='A'*64,started=False)
            record=target/'prepared.json';record.write_text(json.dumps(metadata));record.chmod(0o600)
            result=unittest.mock.Mock(returncode=0,stdout='LoadState=loaded\nActiveState=inactive\n')
            with patch.object(configuration,'candidate',return_value=(generated,dict(AxisInterfaceVersion=axis_gate.INTERFACE_V2,BuildHash='A'*64,Files=hashes),{})),patch.object(axis_gate,'channel',return_value=dict(socket=str(target/'axis.sock'))),patch.object(configuration.subprocess,'run',return_value=result):
                self.assertEqual(metadata,configuration.verify_prepared_v2(version,self.config()))
                metadata['FeedbackBytes']=5632;record.write_text(json.dumps(metadata))
                with self.assertRaisesRegex(ValueError,'identity'):configuration.verify_prepared_v2(version,self.config())
    def test_equivalent_new_build_reuses_only_verified_preparation_provenance(self):
        with tempfile.TemporaryDirectory() as directory:
            root=pathlib.Path(directory);old=root/('A'*64);new=root/('B'*64)
            generated=new/'core/generated';previous=old/'core/generated'
            raw=json.dumps(manifest()).encode();worker=b'approved worker'
            inventory={name:hashlib.sha256(value).hexdigest() for name,value in [('axis-manifest.json',raw),('axes/axis_worker_v2.py',worker)]}
            target=root/'private';target.mkdir(mode=0o700)
            for base in (generated,previous):
                (base/'axes').mkdir(parents=True)
                (base/'axis-manifest.json').write_bytes(raw);(base/'axes/axis_worker_v2.py').write_bytes(worker)
            for name,value in [('axis-manifest.json',raw),('axis_worker_v2.py',worker)]:
                (target/name).write_bytes(value);(target/name).chmod(0o600)
            metadata=dict(SchemaVersion=1,InterfaceVersion=axis_gate.INTERFACE_V2,CommandBytes=4112,FeedbackBytes=6656,RequiredRuntimeCapabilities=manifest()['RequiredRuntimeCapabilities'],WorkerSha256=inventory['axes/axis_worker_v2.py'],ManifestSha256=inventory['axis-manifest.json'],BuildHash='A'*64,started=False)
            (target/'prepared.json').write_text(json.dumps(metadata));(target/'prepared.json').chmod(0o600)
            current_inventory=dict(inventory)
            def candidate(version):
                version=pathlib.Path(version)
                if version not in (old,new):raise FileNotFoundError('missing provenance')
                return version/'core/generated',dict(AxisInterfaceVersion=axis_gate.INTERFACE_V2,BuildHash=version.name,Files=inventory if version==old else current_inventory),{}
            uid=(target/'prepared.json').stat().st_uid
            original_mode=configuration.stat.S_IMODE
            # Windows cannot model POSIX modes; model their verified values here.
            def mode(value):return 0o700 if configuration.stat.S_ISDIR(value) else 0o600
            status=unittest.mock.Mock(returncode=0,stdout='LoadState=loaded\nActiveState=inactive\n')
            with patch.object(configuration,'candidate',side_effect=candidate) as checked,patch.object(axis_gate,'channel',return_value=dict(socket=str(target/'axis.sock'))),patch.object(configuration.subprocess,'run',return_value=status),patch.object(configuration.os,'getuid',return_value=uid,create=True),patch.object(configuration.stat,'S_IMODE',side_effect=mode):
                self.assertEqual(metadata,configuration.verify_prepared_v2(new,self.config()))
                self.assertEqual([new,old],[call.args[0] for call in checked.call_args_list])
                (previous/'axes/axis_worker_v2.py').write_bytes(b'changed provenance worker')
                with self.assertRaisesRegex(ValueError,'worker|manifest'):configuration.verify_prepared_v2(new,self.config())
                (previous/'axes/axis_worker_v2.py').write_bytes(worker)
                # A separately validated new axis configuration needs normal apply,
                # not installer re-preparation merely because its task period changed.
                changed=manifest();changed['PlcIntervalMilliseconds']=20
                changed_raw=json.dumps(changed).encode();(generated/'axis-manifest.json').write_bytes(changed_raw)
                current_inventory['axis-manifest.json']=hashlib.sha256(changed_raw).hexdigest()
                self.assertEqual(metadata,configuration.verify_prepared_v2(new,self.config()))
                self.assertEqual(raw,(target/'axis-manifest.json').read_bytes())
                hello=dict(runtimeReady=True,runtimeCapabilities=list(axis_gate.PROFILE_CAPABILITIES),ethercatTopologyFingerprint='f'*64)
                checked_bundle=dict(topologyFingerprint='f'*64,machineSha256='a'*64,ioMapSha256='b'*64)
                with patch.object(configuration.hw,'check_bundle',return_value=checked_bundle),patch.object(configuration,'native_state',return_value=hello),patch.object(configuration,'validate_config'),patch.object(configuration,'worker_digest',side_effect=FileNotFoundError()):
                    self.assertEqual('axis',configuration.plan(new,self.config())['kind'])
                # Apply updates the manifest and its preparation provenance together;
                # later inactive admission no longer depends on the original build A.
                with patch.object(configuration,'manifest_destination',return_value=target/'axis-manifest.json'):
                    configuration.install_manifest(new,self.config(),'period-update')
                refreshed=json.loads((target/'prepared.json').read_text())
                self.assertEqual('B'*64,refreshed['BuildHash']);self.assertEqual(changed_raw,(target/'axis-manifest.json').read_bytes())
                self.assertEqual(refreshed,configuration.verify_prepared_v2(new,self.config()))
                third=root/('D'*64)
                def third_candidate(version):
                    if pathlib.Path(version)==third:return generated,dict(AxisInterfaceVersion=axis_gate.INTERFACE_V2,BuildHash='D'*64,Files=current_inventory),{}
                    return candidate(version)
                checked.side_effect=third_candidate
                self.assertEqual(refreshed,configuration.verify_prepared_v2(third,self.config()))
                checked.side_effect=candidate
                # An installed worker mismatch must reject before touching either file.
                before_manifest=(target/'axis-manifest.json').read_bytes();before_receipt=(target/'prepared.json').read_bytes()
                (target/'axis_worker_v2.py').write_bytes(b'tampered worker')
                with patch.object(configuration,'manifest_destination',return_value=target/'axis-manifest.json'),self.assertRaisesRegex(ValueError,'worker'):
                    configuration.install_manifest(new,self.config(),'tampered')
                self.assertEqual(before_manifest,(target/'axis-manifest.json').read_bytes());self.assertEqual(before_receipt,(target/'prepared.json').read_bytes())
                (target/'axis_worker_v2.py').write_bytes(worker)
                # Failed receipt replacement leaves the old receipt intact and
                # prevents stale preparation from admitting the changed resources.
                changed['PlcIntervalMilliseconds']=30;failure_raw=json.dumps(changed).encode()
                (generated/'axis-manifest.json').write_bytes(failure_raw);current_inventory['axis-manifest.json']=hashlib.sha256(failure_raw).hexdigest()
                replace=configuration.os.replace
                def fail_receipt(source,destination):
                    if pathlib.Path(destination).name=='prepared.json':raise OSError('receipt replacement failed')
                    return replace(source,destination)
                with patch.object(configuration,'manifest_destination',return_value=target/'axis-manifest.json'),patch.object(configuration.os,'replace',side_effect=fail_receipt),self.assertRaisesRegex(OSError,'receipt'):
                    configuration.install_manifest(new,self.config(),'receipt-failure')
                self.assertEqual(before_receipt,(target/'prepared.json').read_bytes())
                with self.assertRaises(ValueError):configuration.verify_prepared_v2(new,self.config())
                metadata['BuildHash']='C'*64;(target/'prepared.json').write_text(json.dumps(metadata))
                with self.assertRaises(FileNotFoundError):configuration.verify_prepared_v2(new,self.config())
if __name__=='__main__':unittest.main()
