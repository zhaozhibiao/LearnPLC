#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Linux /tmp-only generated ST/native/worker/Modbus loopback integration test."""
import argparse
import json
import pathlib
import socket
import struct
import subprocess
import sys
import tempfile
import threading
import time
import zipfile

def main():
    parser=argparse.ArgumentParser();parser.add_argument('package');parser.add_argument('--harness',required=True);args=parser.parse_args()
    root=pathlib.Path(tempfile.mkdtemp(prefix='st-communication-e2e-',dir='/tmp'))
    with zipfile.ZipFile(args.package) as archive: archive.extractall(root)
    server=socket.socket();server.bind(('127.0.0.1',0));server.listen(1);server.settimeout(5)
    manifest=json.loads((root/'communication-manifest.json').read_text());manifest['Channels'][0]['Configuration'].update(Host='127.0.0.1',Port=server.getsockname()[1])
    (root/'communication-manifest.json').write_text(json.dumps(manifest))
    executable=root/'e2e'
    subprocess.run(['g++','-std=c++17','-pthread','-O1','-I'+str(root),'-I'+str(root/'strucpp_runtime/include'),
        args.harness,str(root/'generated.cpp'),str(root/'generated_debug.cpp'),str(root/'generated_communication_scan_shim.cpp'),str(root/'communication_bridge.cpp'),'-o',str(executable)],check=True,timeout=120)
    ipc=root/'ipc';ipc.mkdir(mode=0o700);endpoint=ipc/'worker.sock'
    worker=subprocess.Popen([sys.executable,str(root/'communication/communication_worker.py'),'--manifest',str(root/'communication-manifest.json'),'--socket',str(endpoint)])
    errors=[];counts=[]
    def peer():
        try:
            with server.accept()[0] as connection:
                connection.settimeout(3)
                def exact(n):
                    data=b''
                    while len(data)<n:
                        chunk=connection.recv(n-len(data))
                        if not chunk: raise AssertionError('unexpected EOF')
                        data+=chunk
                    return data
                header=exact(7);tid,pid,length,unit=struct.unpack('>HHHB',header);pdu=exact(length-1)
                assert pid==0 and unit==1 and pdu==bytes.fromhex('0300000002')
                response=struct.pack('>HHHB',tid,0,7,unit)+bytes.fromhex('030412345678')
                connection.sendall(response[:3]);time.sleep(.005);connection.sendall(response[3:])
                # No repeated transaction; EOF must follow the scan watchdog.
                assert connection.recv(1)==b'', 'held Execute unexpectedly replayed request'
                counts.append(1)
        except BaseException as error: errors.append(error)
    thread=threading.Thread(target=peer);thread.start()
    try:
        until=time.monotonic()+5
        while not endpoint.exists() and time.monotonic()<until:
            if worker.poll() is not None: raise RuntimeError('worker startup failed')
            time.sleep(.01)
        subprocess.run([str(executable),str(endpoint)],check=True,timeout=10)
        thread.join(5)
        if thread.is_alive() or errors or counts!=[1]: raise AssertionError((errors,counts))
        print('PASS independent TCP fixture observed exactly one transaction and watchdog closure; '+str(root))
    finally:
        worker.terminate();worker.wait(timeout=5);server.close()

if __name__=='__main__':main()
