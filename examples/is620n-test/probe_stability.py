#!/usr/bin/env python3
"""MIT. Read-only sampling; never acquires control or submits an axis command."""
import json
from pathlib import Path
import socket
import sys
import time

sys.path.insert(0, '/home/a/st-workbench-i210/is620n-mcp/linux/axis')
from axis_worker import ProtocolClient

with socket.socket(socket.AF_UNIX) as connection:
    connection.settimeout(2)
    connection.connect('/run/st-workbench-is620n/controller.sock')
    client = ProtocolClient(connection)
    hello = client.request('HelloRequest', {'clientName': 'readonly-stability-probe', 'clientVersion': '1'}, 'HelloResponse')
    samples = []
    for _ in range(40):
        samples.append(client.request('ReadSnapshotRequest', {'includeDiagnostics': True}, 'SnapshotResponse'))
        time.sleep(.05)
    output = Path('/home/a/st-workbench-i210/is620n-stability.json')
    output.write_text(json.dumps({'hello': hello, 'samples': samples}, indent=2))
    print('Read-only samples saved:', output)
    print(json.dumps(samples[-1]))
