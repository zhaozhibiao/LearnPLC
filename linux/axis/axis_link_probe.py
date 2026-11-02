#!/usr/bin/env python3
"""Read-only native link timing; never obtains a lease or sends axis commands."""
import argparse
import json
import math
import os
import socket
import time
from axis_worker_v2 import ProtocolClient


def validate_options(samples,interval_ms):
    if (type(samples) is not int or not 2<=samples<=1000 or type(interval_ms) is not int
            or not 1<=interval_ms<=1000 or samples*interval_ms>60000):
        raise ValueError('Use 2..1000 samples, positive interval, total scheduled duration <=60 seconds')


def measure(client,samples=100,interval_ms=10,sleep=time.sleep,now=time.perf_counter,now_ns=time.monotonic_ns):
    validate_options(samples,interval_ms)
    hello=client.request('HelloRequest',{'clientName':'Workbench readonly link probe','clientVersion':'1'},'HelloResponse')
    if type(hello.get('protocolVersion')) is not int or hello['protocolVersion']!=4:raise ValueError('Native protocol mismatch')
    durations=[];ages=[];first=None;previous=None;last=None
    for index in range(samples):
        if index:sleep(interval_ms/1000.)
        start=now()
        sample=client.request('ReadSnapshotRequest',{'includeDiagnostics':False},'SnapshotResponse')
        durations.append((now()-start)*1000.)
        cycle,stamp=sample.get('sampleCycle'),sample.get('sampleMonotonicNs')
        observed=now_ns()
        if (type(cycle) is not int or cycle<=0 or type(stamp) is not int or stamp<=0 or stamp>observed
                or observed-stamp>500000000 or previous is not None and
                (cycle<previous[0] or stamp<previous[1] or (cycle>previous[0] and stamp<=previous[1]))):
            raise ValueError('Native feedback is stale, future, regressed or inconsistent')
        if not isinstance(sample.get('axes'),list):raise ValueError('Missing native axes')
        ages.append((observed-stamp)/1000000.)
        if first is None:first=(cycle,stamp)
        previous=(cycle,stamp);last=sample
    if previous[0]<=first[0] or previous[1]<=first[1]:raise ValueError('Native feedback did not advance')
    if any(not math.isfinite(value) or value<0 for value in durations):raise ValueError('Invalid timing clock')
    ordered=sorted(durations)
    def percentile(p):return ordered[max(0,math.ceil(p*len(ordered))-1)]
    return dict(Status='PASS',Measurement='read_only_native_round_trip',Samples=samples,
        MeanMilliseconds=sum(durations)/samples,P50Milliseconds=percentile(.5),
        P95Milliseconds=percentile(.95),P99Milliseconds=percentile(.99),MaxMilliseconds=max(durations),
        MaxSnapshotAgeMilliseconds=max(ages),FirstSampleCycle=first[0],LastSampleCycle=previous[0],
        RuntimeReady=last.get('runtimeReady') is True,IoReady=last.get('ioReady') is True,
        MotionReady=last.get('motionReady') is True,
        AxisFaultCount=sum(bool(axis.get('fault')) for axis in last['axes']))


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--controller-socket',default='/run/st-workbench-is620n/controller.sock')
    parser.add_argument('--samples',type=int,default=100)
    parser.add_argument('--interval-ms',type=int,default=10)
    args=parser.parse_args()
    validate_options(args.samples,args.interval_ms)
    if os.name!='posix':raise ValueError('Run on the Linux controller host')
    with socket.socket(socket.AF_UNIX,socket.SOCK_STREAM) as stream:
        stream.settimeout(.2);stream.connect(args.controller_socket)
        result=measure(ProtocolClient(stream),args.samples,args.interval_ms)
    print(json.dumps(result,allow_nan=False))


if __name__=='__main__':main()
