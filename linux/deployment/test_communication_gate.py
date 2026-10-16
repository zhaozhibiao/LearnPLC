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
import zipfile
from unittest.mock import patch

import coordinator
import communication_gate as gate


def document():
    return dict(SchemaVersion=1, InterfaceVersion='workbench-communication-v1', PlcIntervalMilliseconds=10,
        Channels=[dict(Index=1, Configuration=dict(Name='Client', Protocol='ModbusTcp', Host='127.0.0.1',
        Port=502, Device='/dev/ttyUSB0', BaudRate=9600, DataBits=8, Parity='Even', StopBits=1,
        Physical='RS232', Direction='Automatic', BeforeSendMicroseconds=0, AfterSendMicroseconds=0,
        TimeoutMilliseconds=1000, Framing='Raw', DelimiterHex='0D0A', FixedLength=1, IdleMilliseconds=20))])


class CommunicationGateTests(unittest.TestCase):
    def bundle(self, root, data=None, flagged=True, omit=None, constants=None):
        files = {n: b'fixture' for n in set(('generated.cpp', 'generated.hpp', 'generated_debug.cpp',
            'defines.h', 'configuration.st', 'strucpp_runtime/include/debug_table.hpp')) | gate.FILES}
        files['debug-map.json'] = b'{}'
        files['communication-manifest.json'] = json.dumps(data or document()).encode()
        files['communication-constants.st'] = constants or b'VAR_GLOBAL CONSTANT\n    COMM_Client : COMM_REF := 1;\nEND_VAR\n'
        if omit: del files[omit]
        manifest = dict(BuildHash='A'*64, RuntimeCommit=coordinator.RUNTIME_COMMIT,
            CompilerVersion='STruC++ version 0.7.0', SymbolTableHash=hashlib.sha256(b'{}').hexdigest(),
            Files={k: hashlib.sha256(v).hexdigest().upper() for k,v in files.items()})
        if flagged: manifest['CommunicationInterfaceVersion'] = gate.INTERFACE
        path = root/'comm.zip'
        with zipfile.ZipFile(path, 'w') as z:
            for k,v in files.items(): z.writestr(k,v)
            z.writestr('build-manifest.json',json.dumps(manifest))
        return path, coordinator.digest(path), 'A'*64

    def test_archive_requires_complete_explicit_bundle_and_exact_bindings(self):
        with tempfile.TemporaryDirectory() as d:
            root=pathlib.Path(d)
            coordinator.validate_archive(*self.bundle(root))
            for kwargs in ({'flagged':False}, {'omit':'communication_bridge.cpp'},
                {'omit':'communication/LICENSE'}, {'constants':b'wrong'}):
                with self.subTest(kwargs=kwargs), self.assertRaises(coordinator.DeploymentError):
                    coordinator.validate_archive(*self.bundle(root,**kwargs))

    def test_manifest_rejects_server_unknown_fields_and_unsafe_settings(self):
        gate.validate_manifest(document())
        for key,value in [('Protocol','ModbusTcpServer'),('Port',True),('Host','localhost'),
            ('TimeoutMilliseconds',10001),('Name','bad-name'),('Device','/dev/../etc/passwd')]:
            data=document(); data['Channels'][0]['Configuration'][key]=value
            if key=='Device': data['Channels'][0]['Configuration']['Protocol']='Serial'
            with self.subTest(key=key), self.assertRaises(ValueError): gate.validate_manifest(data)
        data=document(); data['Channels'][0]['Configuration']['Extra']=1
        with self.assertRaises(ValueError): gate.validate_manifest(data)
        for key,value in [('SchemaVersion',True),('PlcIntervalMilliseconds',0),('Channels',[])]:
            data=document(); data[key]=value
            with self.assertRaises(ValueError): gate.validate_manifest(data)

    def test_private_fresh_capability_validated_without_network_or_serial_io(self):
        with tempfile.TemporaryDirectory() as d:
            root=pathlib.Path(d); raw=json.dumps(document()).encode(); (root/'communication-manifest.json').write_bytes(raw)
            endpoint='/tmp/comm.sock'; config=dict(communication_backend='NativeClient',communication_socket_path=endpoint)
            env='STWORKBENCH_COMM_SOCKET='+endpoint
            cap=dict(SchemaVersion=1,InterfaceVersion=gate.INTERFACE,ManifestSha256=hashlib.sha256(raw).hexdigest(),PID=1234,WorkerReady=True,LastScan=0,ScanHealthy=False)
            def info(path): return types.SimpleNamespace(st_uid=42,st_mode=(stat.S_IFDIR|0o700) if path.name=='tmp' else (stat.S_IFSOCK|0o600))
            with patch('os.getuid',return_value=42,create=True),patch('os.kill') as kill, \
                patch('pathlib.Path.lstat',info),patch('pathlib.Path.stat',info), \
                patch.object(gate,'read_private_json',side_effect=lambda p:copy.deepcopy(cap)):
                gate.verify_ready({'CommunicationInterfaceVersion':gate.INTERFACE},root,config,env)
                kill.assert_called_with(1234,0)
                for key,value in [('PID',True),('PID',-1),('WorkerReady',False),('ManifestSha256','0'*64)]:
                    old=cap[key];cap[key]=value
                    with self.subTest(key=key,value=value),self.assertRaises(ValueError): gate.verify_ready({'CommunicationInterfaceVersion':gate.INTERFACE},root,config,env)
                    cap[key]=old
                with self.assertRaisesRegex(ValueError,'environment'): gate.verify_ready({'CommunicationInterfaceVersion':gate.INTERFACE},root,config,'')
                with self.assertRaisesRegex(ValueError,'explicit'): gate.verify_ready({'CommunicationInterfaceVersion':gate.INTERFACE},root,{},env)

    def test_candidate_hook_is_added_alone_and_with_axis_and_path(self):
        original=b'extern "C" strucpp::ConfigurationInstance* strucpp_get_config(void) {\n    return &g_config;\n}'
        manifest={'CommunicationInterfaceVersion':gate.INTERFACE}
        for combined in (False,True):
            if combined: manifest.update(AxisInterfaceVersion='workbench-axis-v1',MotionInterfaceVersion='workbench-continuous-v1')
            result=coordinator.motion_candidate_source('core/strucpp_runtime/runtime_v4_entry.cpp',original,manifest)
            self.assertIn(b'#include "communication_lifecycle.inc"',result)
            self.assertIn(b'if (!workbench_comm_bootstrap()) return nullptr;',result)
            if combined:
                self.assertIn(b'workbench_axis_bootstrap()',result); self.assertIn(b'workbench_motion_bootstrap()',result)

    def test_private_capability_refuses_stale_or_duplicate_data(self):
        with tempfile.TemporaryDirectory() as d:
            path=pathlib.Path(d)/'cap.json';owner=getattr(os,'getuid',lambda:0)()
            def info(fd):return types.SimpleNamespace(st_uid=owner,st_mode=stat.S_IFREG|0o600,st_size=path.stat().st_size,st_mtime=path.stat().st_mtime)
            with patch('os.getuid',return_value=owner,create=True),patch('os.fstat',side_effect=info):
                path.write_text('{"WorkerReady":true}')
                self.assertEqual({'WorkerReady':True},gate.read_private_json(path))
                os.utime(path,(time.time()-10,time.time()-10))
                with self.assertRaisesRegex(ValueError,'fresh'):gate.read_private_json(path)
                path.write_text('{"PID":1,"PID":2}')
                with self.assertRaisesRegex(ValueError,'Duplicate'):gate.read_private_json(path)

    def test_serial_settings_and_mapping_order_are_validated(self):
        data=document();c=data['Channels'][0]['Configuration'];c.update(Protocol='ModbusRtu',Parity='None',StopBits=2)
        gate.validate_manifest(data)
        for key,value in [('BaudRate',123),('StopBits',1),('Direction','Manual'),('BeforeSendMicroseconds',-1),('FixedLength',513)]:
            bad=copy.deepcopy(data);bad['Channels'][0]['Configuration'][key]=value
            with self.subTest(key=key),self.assertRaises(ValueError):gate.validate_manifest(bad)
        second=copy.deepcopy(data['Channels'][0]);second['Index']=2;data['Channels'].append(second)
        with self.assertRaisesRegex(ValueError,'duplicate'):gate.validate_manifest(data)

    def test_activation_refuses_unverified_and_rechecks_fresh_readiness(self):
        from test_coordinator import Tests
        f=Tests();f.setUp()
        try:
            path,checksum,build=self.bundle(f.root)
            f.request('stage',package_path=str(path),package_sha256=checksum,build_hash=build)
            active=f.runtime/'build/libplc_existing.so'; active.write_bytes(b'baseline')
            baseline=f.request('adopt-baseline')['active'];version=f.coordinator.version(build)
            (version/'build').mkdir();library=version/'build/new_libplc.so';library.write_bytes(b'candidate')
            result=dict(state='compiled',exports_verified=True,library_sha256=coordinator.digest(library),communication_ready=False)
            for verified in (False,True):
                result['communication_ready']=verified;coordinator.atomic_json(version/'compile-result.json',result)
                f.daemon.commands.clear()
                with patch.object(f.coordinator,'service') as service, \
                    patch.object(coordinator.subprocess,'run',return_value=types.SimpleNamespace(stdout='')), \
                    patch.object(coordinator,'verify_communication_ready',side_effect=coordinator.DeploymentError('fresh readiness invalid')) as verify:
                    with self.assertRaisesRegex(coordinator.DeploymentError,'readiness'):
                        f.request('activate',build_hash=build,expected_active_build_hash=baseline['build_hash'])
                    self.assertEqual(verified,verify.called);service.assert_not_called()
                self.assertEqual([],f.daemon.commands);self.assertEqual(b'baseline',active.read_bytes())
        finally:f.tearDown()

    def test_compile_reports_readiness_without_starting_worker_or_connecting_devices(self):
        from test_coordinator import Tests
        f=Tests();f.setUp()
        try:
            path,checksum,build=self.bundle(f.root)
            f.request('stage',package_path=str(path),package_sha256=checksum,build_hash=build)
            version=f.coordinator.version(build);(version/'build').mkdir();(version/'build/new_libplc.so').write_bytes(b'candidate')
            original=coordinator.read_json
            def read(path): return {'source_hashes':{}} if pathlib.Path(path).name=='runtime-profile.json' else original(path)
            def command(args,directory,output,timeout):
                pathlib.Path(output).write_text('\n'.join(coordinator.REQUIRED_EXPORTS) if args[0]=='nm' else 'compiled');return 0
            with patch.object(coordinator,'read_json',side_effect=read),patch.object(coordinator,'run_bounded',side_effect=command):
                result=f.request('compile',build_hash=build);self.assertFalse(result['communication_ready'])
                f.coordinator.config['communication_backend']='NativeClient'
                with patch.object(coordinator.subprocess,'run',return_value=types.SimpleNamespace(stdout='configured-env')), \
                    patch.object(coordinator,'verify_communication_ready') as verify:
                    result=f.request('compile',build_hash=build)
                verify.assert_called_once();self.assertTrue(result['communication_ready'])
            self.assertEqual([],f.daemon.commands)
        finally:f.tearDown()


if __name__=='__main__': unittest.main()
