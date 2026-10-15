"""Integration with the original simulator copied under ignored artifacts only."""
import json
import os
import pathlib
import queue
import socket
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from test_motion_gateway import manifest
import motion_gateway as g

ROOT=pathlib.Path(__file__).resolve().parents[2]
DLL=ROOT/'artifacts/ecmotion-simulator-build/EcMotion.Simulator.dll'

@unittest.skipUnless(DLL.exists(), 'isolated original EcMotion simulator build unavailable')
class SimulatorTests(unittest.TestCase):
    def test_real_protocol_motion_idle_deadline_and_eof(self):
        with tempfile.TemporaryDirectory(prefix='mcp-') as directory:
            endpoint=str(pathlib.Path(directory)/'s.sock')
            project=pathlib.Path(directory)/'axes.json';project.write_text(json.dumps(manifest()))
            sim=subprocess.Popen(['dotnet',str(DLL),'--socket',endpoint],stdin=subprocess.PIPE,stdout=subprocess.DEVNULL,stderr=subprocess.PIPE)
            gateway=None
            try:
                for _ in range(200):
                    if pathlib.Path(endpoint).exists():break
                    if sim.poll() is not None:self.fail(sim.stderr.read().decode())
                    time.sleep(.025)
                launcher=[sys.executable]
                if os.name=='nt' and not hasattr(socket,'AF_UNIX'):
                    shim=ROOT/'artifacts/mcp-motion-fixtures/windows_unix_socket.py'
                    if not shim.exists():self.skipTest('Windows test-only AF_UNIX fixture unavailable')
                    import importlib.util
                    spec=importlib.util.spec_from_file_location('win_unix',shim)
                    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
                    launcher.append(str(shim))
                gateway=subprocess.Popen(launcher+[str(ROOT/'linux/mcp/motion_gateway.py'),
                    '--manifest',str(project),'--controller-socket',endpoint,'--backend','EcMotionSimulator'],
                    stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,bufsize=1)
                responses=queue.Queue()
                def reader():
                    for line in gateway.stdout:responses.put(json.loads(line))
                threading.Thread(target=reader,daemon=True).start()
                count=0
                def call(action,**params):
                    nonlocal count
                    count+=1
                    gateway.stdin.write(json.dumps(dict(requestId=str(count),action=action,**params))+'\n');gateway.stdin.flush()
                    try:result=responses.get(timeout=4)
                    except queue.Empty:
                        if gateway.poll() is not None:self.fail(gateway.stderr.read())
                        raise
                    self.assertEqual(str(count),result['requestId'])
                    return result
                self.assertTrue(call('status')['success'])
                self.assertTrue(call('acquire',durationMilliseconds=500)['success'])
                self.assertTrue(call('execute',operationId='enable',axis='Axis1',kind='Enable')['success'])
                time.sleep(.1)
                self.assertEqual('Completed',call('operation',operationId='enable')['state'])
                self.assertTrue(call('execute',operationId='home',axis='Axis1',kind='Home')['success'])
                for _ in range(100):
                    time.sleep(.03)
                    if call('operation',operationId='home')['state']=='Completed':break
                else:self.fail('Home did not complete')
                self.assertTrue(call('execute',operationId='move',axis='Axis1',kind='MoveAbsolute',position=1,velocity=10)['success'])
                for _ in range(100):
                    time.sleep(.03)
                    if call('operation',operationId='move')['state']=='Completed':break
                else:self.fail('Move did not complete')
                self.assertTrue(call('execute',operationId='jog',axis='Axis1',kind='Jog',velocity=1,durationMilliseconds=500)['success'])
                # No further stdin requests: Linux adapter must stop on its own.
                time.sleep(.8)
                check=socket.socket(socket.AF_UNIX,socket.SOCK_STREAM);check.settimeout(1);check.connect(endpoint)
                backend=g.ProtocolClient(check)
                snapshot=backend.request('ReadSnapshotRequest',dict(includeDiagnostics=False),'SnapshotResponse')
                self.assertEqual(0,snapshot['axes'][0]['actualVelocity'])
                check.close()
                gateway.stdin.close()
                try:gateway.wait(timeout=4)
                except subprocess.TimeoutExpired:
                    gateway.kill();gateway.wait();self.fail(gateway.stderr.read())
                self.assertEqual(0,gateway.returncode,gateway.stderr.read())
                # A host that leaves stdin open and stops consuming stdout
                # must not block the Linux supervisory loop or retain control.
                gateway.stdout.close();gateway.stderr.close()
                gateway=subprocess.Popen(launcher+[str(ROOT/'linux/mcp/motion_gateway.py'),
                    '--manifest',str(project),'--controller-socket',endpoint,'--backend','EcMotionSimulator'],
                    stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,bufsize=1)
                for request in [dict(requestId='acq',action='acquire'),
                                dict(requestId='enable',action='execute',operationId='enable',axis='Axis1',kind='Enable')]:
                    gateway.stdin.write(json.dumps(request)+'\n');gateway.stdin.flush()
                    self.assertTrue(json.loads(gateway.stdout.readline())['success'])
                for index in range(150):
                    try:
                        gateway.stdin.write(json.dumps(dict(requestId='flood'+str(index),action='status'))+'\n')
                        gateway.stdin.flush()
                    except (BrokenPipeError,OSError):break
                    time.sleep(.03)
                    if gateway.poll() is not None:break
                gateway.wait(timeout=4)
                self.assertEqual(0,gateway.returncode,gateway.stderr.read())
                check=socket.socket(socket.AF_UNIX,socket.SOCK_STREAM);check.settimeout(1);check.connect(endpoint)
                snapshot=g.ProtocolClient(check).request('ReadSnapshotRequest',dict(includeDiagnostics=False),'SnapshotResponse')
                self.assertEqual('Disabled',snapshot['axes'][0]['state'])
                check.close()
            finally:
                if gateway is not None:
                    if gateway.poll() is None:gateway.kill();gateway.wait()
                    gateway.stdout.close();gateway.stderr.close()
                    if not gateway.stdin.closed:gateway.stdin.close()
                sim.stdin.write(b'quit\n');sim.stdin.flush()
                try:sim.wait(timeout=4)
                except subprocess.TimeoutExpired:sim.kill();sim.wait()
                sim.stdin.close();sim.stderr.close()
