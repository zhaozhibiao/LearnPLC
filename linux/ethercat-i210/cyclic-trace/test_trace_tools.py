# SPDX-License-Identifier: MIT
"""Offline tooling regressions; never connects to a device."""
import contextlib,importlib.util,io,json,pathlib,runpy,subprocess,sys,tempfile,types,unittest
from unittest.mock import patch
HERE=pathlib.Path(__file__).resolve().parent

def load(name):
    spec=importlib.util.spec_from_file_location(name,HERE/(name+'.py'))
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);return module

class TraceToolsTests(unittest.TestCase):
    def test_optimized_execution_refused_before_any_action(self):
        for name in ('verify_disabled_trace.py','prepare_trace_config.py'):
            result=subprocess.run([sys.executable,'-O',str(HERE/name),'--help'],capture_output=True,text=True)
            self.assertNotEqual(result.returncode,0)
            self.assertIn('Refuse optimized Python',result.stderr)

    def test_prepare_binds_hashes_and_refuses_overwrite_or_wrong_identity(self):
        prepare=load('prepare_trace_config').prepare
        with tempfile.TemporaryDirectory() as directory:
            root=pathlib.Path(directory);folder=root/'config';folder.mkdir()
            data=dict(axes=[dict(name='Axis1',slavePosition=0)],cycleTimeMicroseconds=1000,
                slaves=[dict(vendorId='0x100000',productCode='0xc0108',revisionNumber='0x10001')])
            (folder/'is620n-machine.json').write_text(json.dumps(data))
            (folder/'is620n-io-map.json').write_text(json.dumps(dict(schemaVersion=1,groups=[])))
            result=prepare(root);topology=json.loads(result.read_text())
            self.assertEqual(len(topology['slaves'][0]['pdos'][1]['entries']),8)
            self.assertEqual([e['index'] for e in topology['slaves'][0]['pdos'][1]['entries'][-3:]],[0x6062,0x60f4,0x6077])
            import hashlib
            self.assertEqual(topology['machineSha256'],hashlib.sha256((folder/'is620n-machine.json').read_bytes()).hexdigest())
            with self.assertRaises(FileExistsError):prepare(root)
            result.unlink();data['slaves'][0]['vendorId']='0x1'
            (folder/'is620n-machine.json').write_text(json.dumps(data))
            with self.assertRaises(AssertionError):prepare(root)
            self.assertFalse(result.exists())

    def test_overlay_hash_mismatch_leaves_tree_untouched(self):
        install=load('install_trace').install
        with tempfile.TemporaryDirectory() as directory:
            root=pathlib.Path(directory);project=root/'src/EcMotion.Igh'
            (project/'src').mkdir(parents=True);(project/'include').mkdir()
            main=project/'src/main.cpp';main.write_text('unrecognized source')
            (project/'CMakeLists.txt').write_text('unrecognized CMake')
            with self.assertRaisesRegex(RuntimeError,'hash mismatch'):install(root)
            self.assertEqual(main.read_text(),'unrecognized source')
            self.assertEqual(list((project/'include').iterdir()),[])

    def test_launch_timeout_still_stops_and_stop_timeout_preserves_failure_record(self):
        for fail_stop in (False,True):
            with self.subTest(fail_stop=fail_stop),tempfile.TemporaryDirectory() as directory:
                root=pathlib.Path(directory);(root/'build').mkdir();(root/'build/ecmotion-rt').write_bytes(b'test-binary')
                calls=[]
                def fake_run(args,**kwargs):
                    calls.append(args)
                    if args[0]=='systemd-run':raise subprocess.TimeoutExpired(args,10)
                    if args[:2]==['systemctl','stop'] and fail_stop:raise subprocess.TimeoutExpired(args,20)
                    code=0;output=b''
                    if args[0]=='pgrep':
                        code=1 if '-x' in args else 0;output=b'plc_main --safe-mode' if code==0 else b''
                    elif args[0].endswith('/ethercat'):
                        value={('0x6041','0'):0x8250,('0x606c','0'):0,('0x2005','20'):1,('0x2008','20'):0}[(args[-2],args[-1])]
                        output=f'0x{value:x} {value}\n'.encode()
                    elif args[:2]==['systemctl','show']:output=b'inactive\n'
                    elif args[:2]==['systemctl','is-failed']:code=1
                    if kwargs.get('text'):output=output.decode()
                    return subprocess.CompletedProcess(args,code,output,'' if kwargs.get('text') else b'')
                module=types.ModuleType('stepped_motion_test');module.ControllerClient=object
                with patch.dict(sys.modules,{'stepped_motion_test':module}),patch.object(sys,'argv',['verify_disabled_trace.py',str(root)]),patch.object(sys,'path',list(sys.path)),patch('subprocess.run',side_effect=fake_run),contextlib.redirect_stdout(io.StringIO()):
                    with self.assertRaises(SystemExit) as end:runpy.run_path(str(HERE/'verify_disabled_trace.py'),run_name='__main__')
                self.assertEqual(end.exception.code,1)
                self.assertTrue(any(c[:2]==['systemctl','stop'] for c in calls))
                evidence=list(root.glob('disabled-trace-*/result.json'));self.assertEqual(len(evidence),1)
                result=json.loads(evidence[0].read_text());self.assertEqual(result['status'],'FAILED')
                self.assertEqual('stopError' in result,fail_stop)
                self.assertTrue(result['noAxisCoreRunning'])

if __name__=='__main__':unittest.main()
