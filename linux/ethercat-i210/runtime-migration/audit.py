"""Read-only formal migration acceptance; does not start PLC or command axes."""
import json,pathlib,sys,time,uuid
sys.path.insert(0,'/home/a/st-workbench-deployment')
import coordinator,ethercat_hardware
config=json.loads(pathlib.Path('/home/a/st-workbench-deployment/config.json').read_text())
client=coordinator.Coordinator(config)
result={}
for action in ['program-export','hardware-export']:
    response=client.execute(dict(action=action,request_id='migration-audit-'+uuid.uuid4().hex))
    if response.get('success') is not True:raise ValueError(action+' failed')
    result[action]=dict(state=response['state'],build=response['active']['build_hash'])
with client.connection() as connection:
    result.update(plcState=connection.status(),taskScheduling=connection.task_scheduling_supported())
samples=[]
for _ in range(100):
    samples.append(ethercat_hardware.native_request('/run/st-workbench-is620n/controller.sock','ReadSnapshotRequest',{'includeDiagnostics':False},'SnapshotResponse'))
    time.sleep(.01)
result.update(samples=len(samples),allReady=all(s['runtimeReady'] and s['ioReady'] and s['axes'][0]['timingReady'] for s in samples),
    allDisabled=all(s['axes'][0]['state']=='Disabled' and not s['axes'][0]['fault'] for s in samples),
    noAxisCommands=all(s['lastAppliedCommandSequence']==0 for s in samples),
    cycleAdvancing=all(b['sampleCycle']>a['sampleCycle'] for a,b in zip(samples,samples[1:])),
    positionSpan=max(s['axes'][0]['actualPosition'] for s in samples)-min(s['axes'][0]['actualPosition'] for s in samples))
if result['plcState']!='STOPPED' or not all(result[k] for k in ['taskScheduling','allReady','allDisabled','noAxisCommands','cycleAdvancing']):raise ValueError('Readonly acceptance incomplete: '+json.dumps(result))
path=pathlib.Path('/home/a/st-workbench-i210/runtime-migration-release-20261101/readonly-audit.json')
path.write_text(json.dumps(dict(summary=result,samples=samples),indent=2));path.chmod(0o600)
print(json.dumps(result))
