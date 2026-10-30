#!/usr/bin/env python3
"""Runs config-only native paths; never requests a master or touches a device."""
import hashlib
import json
import pathlib
import subprocess
import sys
import tempfile

binary, fixture, axis_fixture = sys.argv[1:]
topology = json.loads(pathlib.Path(fixture).read_text())
with tempfile.TemporaryDirectory(prefix='ecmotion-bundle-test-') as folder:
    directory = pathlib.Path(folder)
    machine = directory / 'machine.json'
    iomap = directory / 'io.json'
    config = directory / 'ethercat.json'
    machine.write_text(json.dumps({'schemaVersion': 1, 'cycleTimeMicroseconds': 1000, 'axes': []}))
    iomap.write_text(json.dumps({'schemaVersion': 1, 'groups': [{'namePrefix': 'DI', 'direction': 'Input', 'encoding': 'PackedBoolean', 'channelCount': 32, 'channelsPerEntry': 32, 'sourceEntries': ['Drive.Actual'], 'unit': 'bool', 'commissioningRequired': False}]}))
    def bind():
        topology['machineSha256'] = hashlib.sha256(machine.read_bytes()).hexdigest()
        topology['ioMapSha256'] = hashlib.sha256(iomap.read_bytes()).hexdigest()
        config.write_text(json.dumps(topology))
    def run(expected, companions=True):
        args=[binary,'--validate-ethercat-config',str(config)]
        if companions: args += ['--machine-config',str(machine),'--io-map',str(iomap)]
        result=subprocess.run(args,capture_output=True,text=True,timeout=10)
        if (result.returncode==0)!=expected:
            raise AssertionError(f'Offline validation unexpected result: {result.returncode} {result.stdout} {result.stderr}')
        if 'cannot request IgH master' in result.stderr:
            raise AssertionError('Offline validation tried to access hardware')
    bind()
    machine.write_text(machine.read_text()+' ')
    run(False)  # Hash binding must run even in config-only mode.
    bind()
    run(True)  # IO-only machine is supported.
    run(False, False)  # Bound bundles require explicit companion paths.
    machine.write_text('{"schemaVersion":99,"axes":[]}')
    bind()
    run(False)
    machine.write_text('{"schemaVersion":1,"cycleTimeMicroseconds":1000,"axes":[]}')
    io=json.loads(iomap.read_text());io['groups'][0]['sourceEntries']=['missing'];iomap.write_text(json.dumps(io))
    bind()
    run(False)
    topology.pop('machineSha256');topology.pop('ioMapSha256');config.write_text(json.dumps(topology))
    run(True,False)
    axis_machine=json.loads(pathlib.Path(axis_fixture).read_text())
    keys=axis_machine['axes'][0]['pdoEntries']
    definitions=[('controlword',24640,16),('targetPosition',24698,32),('velocityOffset',0x60B1,32),('modeOfOperation',24672,8),('statusword',24641,16),('positionActual',24676,32),('velocityActual',24684,32),('modeDisplay',24673,8),('errorCode',24639,16)]
    entries=[dict(index=index,subindex=0,bitLength=bits,key=keys[name]) for name,index,bits in definitions]
    topology['slaves'][0]['pdos'][0]['entries']=entries[:4]
    topology['slaves'][0]['pdos'][1]['entries']=entries[4:]
    machine.write_text(json.dumps(axis_machine));iomap.write_text('{"schemaVersion":1,"groups":[]}');bind();run(True)
    timing_machine=json.loads(json.dumps(axis_machine))
    saved_dc=json.loads(json.dumps(topology['slaves'][0].get('dc',{})))
    topology['slaves'][0]['dc']={'enabled':True,'assignActivate':0x300,'sync0CycleNs':1000000,'sync0ShiftNs':500000,'sync1CycleNs':0,'sync1ShiftNs':0}
    def timing_case(options, expected):
        timing_machine['cycleTiming']=options
        machine.write_text(json.dumps(timing_machine));bind();run(expected)
    valid={'enabled':True,'model':'R2M','boundaryGuardNs':200000,'remainingSendBudgetNs':100000,'requiredGoodCycles':1000}
    timing_case(valid,True)
    for key,value in [('model','M2R'),('boundaryGuardNs',-1),('remainingSendBudgetNs',1.5),('requiredGoodCycles',0),('enabled',1)]:
        bad=dict(valid);bad[key]=value;timing_case(bad,False)
    topology['slaves'][0]['dc']['sync0CycleNs']=2000000;timing_case(valid,False)
    topology['slaves'][0]['dc']=saved_dc
    axis_machine['axes'][0]['softLimitNegative']=100
    axis_machine['axes'][0]['softLimitPositive']=-100
    machine.write_text(json.dumps(axis_machine));bind();run(False)
    axis_machine=json.loads(pathlib.Path(axis_fixture).read_text());axis_machine['cycleTimeMicroseconds']=2000
    machine.write_text(json.dumps(axis_machine));bind();run(False)
print('offline companion validation passed')
