"""Bounded read-only completion check. No motion commands, no timeout suppression."""
import time

def await_disabled(read_pdo, heartbeat, stationary, timeout=15, clock=time.monotonic, sleep=time.sleep):
    deadline=clock()+timeout
    samples=[]
    while clock()<deadline:
        heartbeat()
        pdo=read_pdo()  # Unknown/error aborts verification; never infer success.
        now=clock()
        if pdo['statusword']&4:
            samples=[]
        else:
            samples.append((now,pdo['actual'],pdo['velocity']/1048576))
            samples=[s for s in samples if s[0]>=now-.7]
            if stationary(samples):return pdo,samples
        sleep(.02)
    raise TimeoutError('Disable acknowledgement or stable standstill not confirmed within hard deadline; further motion prohibited')
