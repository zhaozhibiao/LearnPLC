#!/usr/bin/env python3
import sys
import time
sys.path.append('/home/a/st-workbench-i210')
from stepped_motion_test import ControllerClient

def main():
    c = ControllerClient()
    try:
        c.acquire()
        print("Resetting axis...")
        c.axis_cmd('Reset')
        time.sleep(0.2)
        c.axis_cmd('Disable')
        time.sleep(0.2)
        snap = c.snapshot()
        ax = snap['axes'][0]
        print(f"Current state: {ax['state']}, fault: {ax['fault']}, pos: {ax['actualPosition']}")
    finally:
        c.close()

if __name__ == '__main__':
    main()
