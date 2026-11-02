#!/usr/bin/env python3
"""Explicit unprivileged axis-channel recovery. Requires STOPPED; never starts PLC."""
import argparse
import json
import math
import os
import pathlib
import stat
import time
import axis_gate
import configuration_download as configuration
import ethercat_hardware as hw


def admit_resources(client,active):
    version=client.version(active['build_hash'])
    generated,manifest,compiled=configuration.candidate(version)
    if compiled.get('library_sha256')!=active.get('library_sha256'):raise ValueError('Active candidate identity differs')
    interface=manifest.get('AxisInterfaceVersion')
    if interface not in axis_gate.INTERFACES:raise ValueError('Active program has no supported axis channel')
    configuration.validate_config(client.config,interface)
    destination=configuration.manifest_destination(client.config,interface)
    raw=hw.read_regular(generated/'axis-manifest.json')
    data=axis_gate.validate_manifest(axis_gate.strict_json(raw))
    if data['InterfaceVersion']!=interface or hw.read_regular(destination)!=raw:raise ValueError('Installed axis mapping differs from active program')
    if interface==axis_gate.INTERFACE_V2:
        axis_gate.verify_v2_worker_source(manifest,generated,str(destination.with_name('axis.sock')))
        axis_gate.verify_native_profile_capabilities(data,configuration.native_state(client.config))
    hw.verify_applied(generated,client.config)
    return generated


def verify_quiescent(generated,config,now=time.monotonic,sleep=time.sleep):
    """Faults may remain latched, but torque and motion must be positively absent."""
    machine=hw.parse_json(hw.read_regular(pathlib.Path(generated)/'machine-config.json'))
    axes=machine.get('axes')
    if not isinstance(axes,list) or not 1<=len(axes)<=64:raise ValueError('Missing trusted stationary policies')
    policies={}
    for axis in axes:
        name,policy=axis.get('name'),axis.get('motionCompletion')
        if not isinstance(name,str) or not name or name in policies or not isinstance(policy,dict):raise ValueError('Invalid stationary policy')
        for key in ('positionTolerance','velocityTolerance'):
            value=policy.get(key)
            if type(value) not in (float,int) or not math.isfinite(value) or value<=0:raise ValueError('Invalid stationary tolerance')
        dwell=policy.get('dwellMilliseconds')
        if type(dwell) is not int or not 1<=dwell<=60000 or type(policy.get('positionStabilityCompletion')) is not bool:raise ValueError('Invalid stationary dwell')
        policies[name]=policy
    duration=max(p['dwellMilliseconds'] for p in policies.values())/1000.
    deadline=now()+duration+2;previous=None;start=None;spans={}
    while now()<deadline:
        sample=hw.native_request(config.get('ethercat_controller_socket',hw.CONTROLLER_SOCKET),'ReadSnapshotRequest',{'includeDiagnostics':False},'SnapshotResponse')
        hw.validate_sample(sample)
        age=time.monotonic_ns()-sample['sampleMonotonicNs']
        if not 0<=age<=500000000:raise ValueError('Recovery feedback is stale or from the future')
        if (sample.get('outputsAtConfiguredSafeState') is not True or previous is not None and
                (sample['sampleCycle']<=previous['sampleCycle'] or sample['sampleMonotonicNs']<=previous['sampleMonotonicNs'])):
            raise ValueError('Recovery feedback unsafe or not advancing')
        actual=sample.get('axes')
        if not isinstance(actual,list) or len(actual)!=len(policies):raise ValueError('Every physical axis needs stationary proof')
        names=set()
        for axis in actual:
            name=axis.get('name');status=axis.get('driveStatusword');position=axis.get('actualPosition');velocity=axis.get('actualVelocity')
            if (name not in policies or name in names or axis.get('state') not in ('Disabled','ErrorStop')
                    or axis.get('motionBusy') is not False or type(status) is not int or not 0<=status<=65535 or status&4
                    or any(type(v) not in (float,int) or not math.isfinite(v) for v in (position,velocity))):
                raise ValueError('Axis must be physically disabled and stationary before channel recovery')
            names.add(name);policy=policies[name]
            low,high=spans.get(name,(position,position));spans[name]=(min(low,position),max(high,position))
            limit=policy['positionTolerance']
            if policy['positionStabilityCompletion']:limit=min(limit,policy['velocityTolerance']*policy['dwellMilliseconds']/1000.)
            if (spans[name][1]-spans[name][0]>limit or not policy['positionStabilityCompletion'] and abs(velocity)>policy['velocityTolerance']):
                raise ValueError('Axis not stationary under its applied policy')
        if start is None:start=sample['sampleMonotonicNs']
        if sample['sampleMonotonicNs']-start>=duration*1e9:return
        previous=sample;sleep(.01)
    raise ValueError('Stationary recovery proof timed out')


def recover_active(client,expected_build_hash):
    """Caller holds coordinator lock. Never obtains motion lease or replays a request."""
    active=client.active();client.verify_active(active,expected_build_hash)
    with client.connection() as connection:
        if connection.status()!='STOPPED':raise ValueError('Axis channel recovery requires PLC STOPPED')
    if client.transaction_path.exists():
        transaction=hw.parse_json(hw.read_regular(client.transaction_path))
        if transaction.get('state') not in ('committed','rolled-back'):raise ValueError('Unfinished program transaction must be resolved first')
    pid=client.native_core_pid()
    if type(pid) is not int or pid<=0:raise ValueError('Native core identity unavailable')
    generated=admit_resources(client,active)
    verify_quiescent(generated,client.config)
    if client.native_core_pid()!=pid:raise ValueError('Native core identity changed during admission')
    if client.active()!=active:raise ValueError('Active program changed during recovery admission')
    client.service('stop')
    if client.native_core_pid()!=pid:raise ValueError('Native core identity changed; query actual state')
    verify_quiescent(generated,client.config)
    client.verify_active(active,expected_build_hash)
    if client.active()!=active:raise ValueError('Active program changed during recovery')
    client.resume_service()
    if client.native_core_pid()!=pid:raise ValueError('Native core identity changed during recovery')
    client.verify_active(active,expected_build_hash);client.verify_start_hardware(active)
    with client.connection() as connection:
        if connection.status()!='STOPPED':raise ValueError('Recovered channel but PLC state unconfirmed')
    verify_quiescent(generated,client.config)
    if client.native_core_pid()!=pid:raise ValueError('Native core identity changed after final recovery proof')
    if client.active()!=active:raise ValueError('Active program changed after recovery')
    return dict(state='axis-channel-recovered',active_build_hash=active['build_hash'],plc_state='STOPPED',motion_restarted=False)


def main():
    import coordinator
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config',default='/home/a/st-workbench-deployment/config.json')
    parser.add_argument('--expected-build',required=True)
    args=parser.parse_args();path=pathlib.Path(args.config).absolute()
    for part in (path,*path.parents):
        if part.is_symlink():raise ValueError('Symlink configuration path')
    info=path.stat()
    if not stat.S_ISREG(info.st_mode) or info.st_uid!=os.getuid() or stat.S_IMODE(info.st_mode)!=0o600:raise ValueError('Private owner-only configuration required')
    client=coordinator.Coordinator(axis_gate.strict_json(hw.read_regular(path)))
    with client.lock():result=recover_active(client,args.expected_build)
    print(json.dumps(result))


if __name__=='__main__':main()
