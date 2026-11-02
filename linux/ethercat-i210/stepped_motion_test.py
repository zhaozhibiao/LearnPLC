#!/usr/bin/env python3
"""Stepped speed motion verification for IS620N against /run/st-workbench-is620n/controller.sock.
Verifies tracking performance at 0.05, 0.2, 0.5, and 1.0 rev/s without following error fault.
"""
import json
import socket
import struct
import sys
import time
import uuid

def read_exact(stream, count):
    buf = bytearray()
    while len(buf) < count:
        chunk = stream.recv(count - len(buf))
        if not chunk:
            raise RuntimeError("Stream closed unexpectedly")
        buf.extend(chunk)
    return bytes(buf)

class ControllerClient:
    def __init__(self, sock_path='/run/st-workbench-is620n/controller.sock'):
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.settimeout(5.0)
        self.sock.connect(sock_path)
        self.seq = 0
        self.lease = None

    def request(self, kind, payload):
        self.seq += 1
        req_id = str(uuid.uuid4())
        msg = {
            'protocolVersion': 4,
            'kind': kind,
            'requestId': req_id,
            'sequence': self.seq,
            'payload': payload
        }
        data = json.dumps(msg, separators=(',', ':')).encode('utf-8')
        self.sock.sendall(struct.pack('<I', len(data)) + data)
        size, = struct.unpack('<I', read_exact(self.sock, 4))
        raw = read_exact(self.sock, size)
        resp = json.loads(raw.decode('utf-8'))
        if resp.get('kind') == 'ErrorResponse':
            raise RuntimeError(f"Controller error: {resp}")
        return resp.get('payload', {})

    def hello(self):
        return self.request('HelloRequest', {'clientName': 'stepped-motion-test', 'clientVersion': '1'})

    def snapshot(self):
        return self.request('ReadSnapshotRequest', {'includeDiagnostics': True})

    def acquire(self, lease_ms=10000):
        resp = self.request('AcquireControlRequest', {
            'clientName': 'stepped-motion-test',
            'requestedLeaseMilliseconds': lease_ms
        })
        self.lease = resp['leaseToken']
        return self.lease

    def heartbeat(self):
        if self.lease:
            return self.request('HeartbeatRequest', {'leaseToken': self.lease})

    def axis_cmd(self, cmd, axis='Axis1', pos=None, vel=None):
        payload = {'leaseToken': self.lease, 'axis': axis, 'command': cmd}
        if pos is not None:
            payload['position'] = pos
        if vel is not None:
            payload['velocity'] = vel
        return self.request('AxisCommandRequest', payload)

    def release(self):
        if self.lease:
            try:
                self.request('ReleaseControlRequest', {'leaseToken': self.lease})
            except Exception as e:
                print(f"Warning on release: {e}")
            self.lease = None

    def close(self):
        try:
            self.release()
        finally:
            self.sock.close()

def wait_for_state(client, expected_state, timeout=8.0, check_fault=True):
    start = time.monotonic()
    last_snap = None
    while time.monotonic() - start < timeout:
        client.heartbeat()
        snap = client.snapshot()
        axis = snap['axes'][0]
        last_snap = axis
        if check_fault and axis.get('fault'):
            raise RuntimeError(f"Axis faulted: {axis.get('fault')}")
        if axis['state'] == expected_state:
            return snap
        time.sleep(0.02)
    raise TimeoutError(f"Timed out waiting for state '{expected_state}'. Current: {last_snap}")

def run_test():
    client = ControllerClient()
    try:
        print("[1/6] Connecting and reading snapshot...")
        hello = client.hello()
        print(f"  Topology fingerprint: {hello.get('ethercatTopologyFingerprint')}")
        snap = client.snapshot()
        axis = snap['axes'][0]
        print(f"  Initial axis state: {axis['state']}, pos: {axis['actualPosition']:.4f} rev, fault: {axis.get('fault')}")

        print("[2/6] Acquiring control lease...")
        client.acquire(lease_ms=15000)

        # Reset if faulted
        if axis.get('fault') or axis['state'] == 'ErrorStop':
            print("  Resetting axis fault...")
            client.axis_cmd('Reset')
            wait_for_state(client, 'Disabled')

        print("[3/6] Enabling axis...")
        client.axis_cmd('Enable')
        wait_for_state(client, 'Standstill')
        print("  Axis enabled in Standstill.")

        print("[4/6] Homing axis to temporary zero (method 35)...")
        client.axis_cmd('Home')
        time.sleep(0.1)
        snap = wait_for_state(client, 'Standstill')
        axis = snap['axes'][0]
        print(f"  Homed: {axis.get('homed')}, pos: {axis['actualPosition']:.6f} rev")

        # Speed steps: (speed rev/s, target_pos rev, label)
        test_steps = [
            (0.05, 0.2, "Stage 1: 0.05 rev/s (3 rpm)"),
            (0.20, 0.4, "Stage 2: 0.20 rev/s (12 rpm)"),
            (0.50, 0.5, "Stage 3: 0.50 rev/s (30 rpm)"),
            (1.00, 0.5, "Stage 4: 1.00 rev/s (60 rpm)")
        ]

        results = []

        print("[5/6] Executing stepped speed motion tests...")
        for speed, target_displacement, label in test_steps:
            print(f"\n--- {label} ---")
            current_pos = client.snapshot()['axes'][0]['actualPosition']
            target_out = current_pos + target_displacement
            target_back = current_pos

            # Move outward
            print(f"  Moving outward: target={target_out:.3f} rev, speed={speed:.2f} rev/s...")
            client.axis_cmd('MoveAbsolute', pos=target_out, vel=speed)
            time.sleep(0.02)
            
            # Sample motion tracking
            max_tracking_err = 0.0
            max_vel = 0.0
            move_start = time.monotonic()
            
            while True:
                client.heartbeat()
                snap = client.snapshot()
                ax = snap['axes'][0]
                if ax.get('fault'):
                    raise RuntimeError(f"FAULT tripped during motion at speed {speed}: {ax.get('fault')}")
                
                err = abs(ax['actualPosition'] - ax['motionTarget'])
                if err > max_tracking_err:
                    max_tracking_err = err
                if abs(ax['actualVelocity']) > max_vel:
                    max_vel = abs(ax['actualVelocity'])
                
                if ax['state'] == 'Standstill' and (time.monotonic() - move_start) > 0.1:
                    print(f"    Outward arrived at pos={ax['actualPosition']:.4f} rev (target={target_out:.4f})")
                    break
                if time.monotonic() - move_start > 15.0:
                    raise TimeoutError("Motion outward timeout")
                time.sleep(0.01)

            time.sleep(0.2)

            # Move back
            print(f"  Moving back: target={target_back:.3f} rev, speed={speed:.2f} rev/s...")
            client.axis_cmd('MoveAbsolute', pos=target_back, vel=speed)
            time.sleep(0.02)
            move_start = time.monotonic()

            while True:
                client.heartbeat()
                snap = client.snapshot()
                ax = snap['axes'][0]
                if ax.get('fault'):
                    raise RuntimeError(f"FAULT tripped during return at speed {speed}: {ax.get('fault')}")
                
                err = abs(ax['actualPosition'] - ax['motionTarget'])
                if err > max_tracking_err:
                    max_tracking_err = err
                if abs(ax['actualVelocity']) > max_vel:
                    max_vel = abs(ax['actualVelocity'])
                
                if ax['state'] == 'Standstill' and (time.monotonic() - move_start) > 0.1:
                    print(f"    Returned to pos={ax['actualPosition']:.4f} rev (target={target_back:.4f})")
                    break
                if time.monotonic() - move_start > 15.0:
                    raise TimeoutError("Motion return timeout")
                time.sleep(0.01)

            print(f"  Result: Max Speed reached = {max_vel:.3f} rev/s, Max Following Error = {max_tracking_err:.5f} rev ({max_tracking_err*360:.2f}°)")
            results.append({
                'speed': speed,
                'target': target_displacement,
                'max_velocity': max_vel,
                'max_following_error_rev': max_tracking_err,
                'max_following_error_deg': max_tracking_err * 360.0,
                'status': 'PASS'
            })
            time.sleep(0.3)

        print("\n[6/6] Shutting down cleanly (Disable & Release)...")
        client.axis_cmd('Disable')
        wait_for_state(client, 'Disabled')
        client.release()
        print("Axis safely Disabled and lease released.")

        print("\n=== SUMMARY OF STEPPED SPEED TEST ===")
        print(json.dumps(results, indent=2))
        return results

    except Exception as e:
        print(f"\n[ERROR] Test aborted with exception: {e}")
        try:
            client.axis_cmd('Stop')
            time.sleep(0.1)
            client.axis_cmd('Disable')
        except Exception:
            pass
        raise
    finally:
        client.close()

if __name__ == '__main__':
    run_test()
