#!/usr/bin/env python3
import time
import math
import json
import sys
sys.path.append('/home/a/st-workbench-i210')
from stepped_motion_test import ControllerClient, wait_for_state

def measure_speeds():
    c = ControllerClient()
    results = []
    try:
        c.acquire(lease_ms=30000)
        c.axis_cmd('Reset')
        wait_for_state(c, 'Disabled')
        c.axis_cmd('Enable')
        wait_for_state(c, 'Standstill')
        c.axis_cmd('Home')
        time.sleep(0.1)
        wait_for_state(c, 'Standstill')

        # Test speeds that are within 0.015 rev threshold:
        # e.g. 0.05, 0.10, 0.15, 0.20, 0.25, 0.30 rev/s
        speeds = [0.05, 0.10, 0.15, 0.20, 0.25, 0.30]

        for speed in speeds:
            c.heartbeat()
            # Outward move
            target_out = 0.3
            c.axis_cmd('MoveAbsolute', pos=target_out, vel=speed)
            time.sleep(0.02)
            
            # Sample during motion
            start = time.monotonic()
            samples = []
            while time.monotonic() - start < 10.0:
                c.heartbeat()
                snap = c.snapshot()
                ax = snap['axes'][0]
                if ax.get('fault'):
                    print(f"Fault tripped at speed {speed}: {ax.get('fault')}")
                    break
                vel = ax['actualVelocity']
                # Collect samples when speed has reached plateau (> 80% of commanded speed)
                if abs(vel) >= 0.8 * speed and ax['state'] != 'Standstill':
                    samples.append({
                        'time': time.monotonic() - start,
                        'pos': ax['actualPosition'],
                        'vel': vel
                    })
                if ax['state'] == 'Standstill' and (time.monotonic() - start) > 0.3:
                    break
                time.sleep(0.005)

            time.sleep(0.2)
            # Return move
            c.axis_cmd('MoveAbsolute', pos=0.0, vel=speed)
            time.sleep(0.02)
            start = time.monotonic()
            while time.monotonic() - start < 10.0:
                c.heartbeat()
                snap = c.snapshot()
                ax = snap['axes'][0]
                if ax['state'] == 'Standstill' and (time.monotonic() - start) > 0.3:
                    break
                time.sleep(0.005)

            time.sleep(0.2)
            results.append({
                'speed_rev_s': speed,
                'rpm': speed * 60,
                'sample_count': len(samples),
                'fault': ax.get('fault')
            })
            print(f"Speed: {speed:.2f} rev/s ({speed*60:.0f} rpm), Samples in cruise: {len(samples)}, Fault: {ax.get('fault')}")

        c.axis_cmd('Disable')
        wait_for_state(c, 'Disabled')
    finally:
        c.close()

    print(json.dumps(results, indent=2))

if __name__ == '__main__':
    measure_speeds()
