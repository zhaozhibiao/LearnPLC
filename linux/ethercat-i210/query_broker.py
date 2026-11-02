#!/usr/bin/env python3
import socket
import struct
import json
import sys

def main():
    action = sys.argv[1] if len(sys.argv) > 1 else 'status'
    req = {'protocolVersion': 1, 'requestId': 'req-cli-1', 'action': action}
    if len(sys.argv) > 2:
        req['buildHash'] = sys.argv[2]
    raw = json.dumps(req).encode('utf-8')
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as s:
        s.connect('/run/st-workbench-hardware/control.sock')
        s.sendall(struct.pack('<I', len(raw)) + raw)
        length, = struct.unpack('<I', s.recv(4))
        data = b''
        while len(data) < length:
            chunk = s.recv(min(4096, length - len(data)))
            if not chunk: break
            data += chunk
        resp = json.loads(data.decode('utf-8'))
        print(json.dumps(resp, indent=2))

if __name__ == '__main__':
    main()
