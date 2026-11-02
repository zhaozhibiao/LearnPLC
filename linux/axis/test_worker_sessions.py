"""MIT. Private PLC connection lifecycle; fake controller only, no hardware."""
import json
import os
import socket
import tempfile
import threading
import time
import unittest
from unittest import mock

import axis_worker as w
from test_axis_worker import Fake, manifest, packet


@unittest.skipUnless(os.name == 'posix', 'Unix private bridge')
class WorkerSessionTests(unittest.TestCase):
    def test_probe_disconnect_then_new_plc_session_does_not_remove_socket_or_replay(self):
        with tempfile.TemporaryDirectory() as directory:
            controller_path = os.path.join(directory, 'controller.sock')
            bridge_path = os.path.join(directory, 'axis.sock')
            manifest_path = os.path.join(directory, 'manifest.json')
            with open(manifest_path, 'w') as stream:
                json.dump(manifest(), stream)
            listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            listener.bind(controller_path)
            listener.listen(1)
            listener.settimeout(2)
            fake = Fake(physical=True)
            args = w.argparse.Namespace(manifest=manifest_path, socket=bridge_path,
                controller_socket=controller_path, backend='EcMotion', capabilities=None)
            failures = []
            def serve():
                try:
                    w.serve(args)
                except (OSError, ValueError, w.ProtocolError) as error:
                    failures.append(error)
            def connect():
                client = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
                client.settimeout(1)
                client.connect(bridge_path)
                return client
            def scan(client, image):
                client.sendall(image)
                return w.FEEDBACK.unpack_from(w.read_exact(client, w.FEEDBACK_BYTES))
            def wait_for(predicate):
                deadline = time.monotonic() + 2
                while not predicate() and thread.is_alive() and time.monotonic() < deadline:
                    time.sleep(.005)
                self.assertTrue(predicate())
            peer = None
            current = None
            with mock.patch.object(w, 'ProtocolClient', return_value=fake) as protocol:
                thread = threading.Thread(target=serve, daemon=True)
                thread.start()
                try:
                    peer, _ = listener.accept()
                    wait_for(lambda: os.path.exists(bridge_path))
                    # Loader/probe connection: no command, then EOF.
                    current = connect()
                    scan(current, packet(request=0))
                    hello_count = sum(k == 'HelloRequest' for k, _ in fake.calls)
                    current.sendall(packet(scan=2, request=1)[:20])  # Discard incomplete old request on EOF.
                    current.close(); current = None
                    wait_for(lambda: sum(k == 'HelloRequest' for k, _ in fake.calls) > hello_count)
                    self.assertTrue(os.path.exists(bridge_path))
                    self.assertFalse(any(k == 'AcquireControlRequest' for k, _ in fake.calls))
                    # Explicit subsequent PLC connection owns its own session.
                    current = connect()
                    feedback = scan(current, packet(request=1, kind=1))
                    self.assertEqual(feedback[0], 1)
                    self.assertFalse(feedback[3])
                    self.assertEqual(sum(k == 'AcquireControlRequest' for k, _ in fake.calls), 1)
                    current.close(); current = None
                    wait_for(lambda: any(k == 'ReleaseControlRequest' for k, _ in fake.calls))
                    before = sum(k == 'AxisCommandRequest' for k, _ in fake.calls)
                    current = connect()
                    feedback = scan(current, packet(scan=1, request=0))
                    self.assertEqual(feedback[0], 0)
                    self.assertFalse(feedback[3])
                    self.assertEqual(sum(k == 'AxisCommandRequest' for k, _ in fake.calls), before)
                    self.assertEqual(sum(k == 'AcquireControlRequest' for k, _ in fake.calls), 1)
                    self.assertEqual(protocol.call_count, 1)  # Same physical connection; never reconnect.
                    with open(bridge_path + '.capabilities.json') as stream:
                        self.assertTrue(json.load(stream)['PhysicalBasicAxis'])
                finally:
                    if thread.is_alive():
                        if current is None and os.path.exists(bridge_path):
                            current = connect()
                        if current is not None:
                            try:
                                current.sendall(w.HEADER.pack(3, 2) + packet()[w.HEADER.size:])
                            except OSError:
                                pass
                    if current is not None:
                        current.close()
                    thread.join(3)
                    if peer is not None:
                        peer.close()
                    listener.close()
                self.assertFalse(thread.is_alive())


if __name__ == '__main__':
    unittest.main()
