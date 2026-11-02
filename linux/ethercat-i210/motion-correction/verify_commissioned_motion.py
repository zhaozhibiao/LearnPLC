"""Bounded acceleration sensitivity diagnostic. Source2/gain0 in both groups. Restores parameters."""
import datetime, hashlib, json, pathlib, struct, subprocess, sys, time, traceback
if not __debug__: raise SystemExit('Assertions required')
sys.path.insert(0,'/home/a/st-workbench-i210/is620n-native-candidate-20261023')
from stepped_motion_test import ControllerClient
from stationary_window import stationary

ROOT=pathlib.Path('/home/a/st-workbench-i210/motion-correction-20261029')
UNIT='st-workbench-motion-correction-commissioned'
GAIN=0
ACC=float(sys.argv[1]); assert ACC == .5
OUT=ROOT/('acceleration-'+str(ACC)+'-'+time.strftime('%Y%m%d-%H%M%S')); OUT.mkdir()
record=dict(acceleration=ACC,deceleration=ACC,gainRaw=1000,source=1,hostGain=0,results=[],samples=[],commands=[],parameters=[])
client=None; started=False; changed=False; original=None; failed=False

def run(args):
    return subprocess.run(args,check=True,capture_output=True,timeout=8)

def sdo(index,sub,type='uint16',value=None):
    if client: client.heartbeat()
    args=['/opt/etherlab/bin/ethercat','upload' if value is None else 'download','-p','0','-t',type,index,str(sub)]
    if value is not None: args.append(str(value))
    response=run(args).stdout.decode().strip()
    record['parameters'].append(dict(index=index,sub=sub,type=type,write=value,response=response))
    return int(response.split()[-1]) if value is None else None

def snapshot(check=True):
    client.heartbeat(); full=client.snapshot()
    record['lastSnapshot']=full
    if check and (not full['runtimeReady'] or full['axes'][0].get('fault')):
        raise RuntimeError('Axis/bus unhealthy: '+json.dumps(full))
    return full

def command(name,**kwargs):
    client.heartbeat(); result=client.axis_cmd(name,**kwargs)
    record['commands'].append(dict(name=name,args=kwargs,result=result))
    assert result.get('accepted') is True, 'Command not accepted: '+name

def wait(predicate,timeout=5):
    end=time.monotonic()+timeout
    while time.monotonic()<end:
        full=snapshot()
        if predicate(full['axes'][0]): return full
        time.sleep(.01)
    raise RuntimeError('State timeout')

def pdo():
    client.heartbeat()
    raw=run(['/opt/etherlab/bin/ethercat','data','--domain','0']).stdout
    assert len(raw)==34
    return dict(statusword=struct.unpack_from('<H',raw,13)[0],
                velocity=struct.unpack_from('<i',raw,19)[0],
                target=struct.unpack_from('<i',raw,2)[0],
                actual=struct.unpack_from('<i',raw,15)[0],
                offset=struct.unpack_from('<i',raw,6)[0])

try:
    assert '--safe-mode' in run(['pgrep','-af','plc_main']).stdout.decode()
    assert subprocess.run(['pgrep','-x','ecmotion-rt'],capture_output=True).returncode==0
    machine=json.loads((ROOT/'config/commissioned-machine.json').read_text()); axis=machine['axes'][0]
    assert machine['cycleTimeMicroseconds']==1000
    assert axis['homing']['method']==35 and axis['homing']['allowCurrentPositionZero']
    assert axis['motionSafety']['maximumFollowingError']==.005
    assert axis['motionSafety']['followingErrorTimeoutMilliseconds']==20
    assert axis['softLimitNegative']==-1 and axis['softLimitPositive']==1
    assert axis['maximumVelocity']==.5 and axis['maximumAcceleration']==ACC and axis['maximumDeceleration']==ACC
    assert axis['velocityFeedforwardGain']==0 and axis['motionSafety']['maximumActualVelocity']==1.0
    record['activeMachineSha256']=hashlib.sha256((ROOT/'config/commissioned-machine.json').read_bytes()).hexdigest()
    record['binarySha256']=hashlib.sha256((ROOT/'build/ecmotion-rt').read_bytes()).hexdigest()
    started=True # Attach to the already validated, disabled diagnostic bus.
    deadline=time.monotonic()+20
    while time.monotonic()<deadline:
        try:
            candidate=ControllerClient('/run/'+UNIT+'/controller.sock'); candidate.hello()
            full=candidate.snapshot()
            if full['runtimeReady']:
                client=candidate; break
            candidate.sock.close()
        except (OSError,RuntimeError): pass
        time.sleep(.05)
    assert client is not None, 'No ready runtime within 20 seconds'
    client.acquire(1000)
    # Require stable ready bus before considering one explicit reset of E08.
    window=[]; deadline=time.monotonic()+10
    while time.monotonic()<deadline:
        full=snapshot(False); axisNow=full['axes'][0]
        ready=full['runtimeReady'] and full['ioReady'] and 'bus_ready=1' in full['diagnostics']
        if not ready: window=[]
        else:
            window.append((time.monotonic(),round(axisNow['actualPosition']*1048576),axisNow['actualVelocity']))
            window=[s for s in window if s[0]>=window[-1][0]-.7]
            if stationary(window): break
        time.sleep(.01)
    else: raise RuntimeError('Position/velocity stationary window not confirmed')
    initial=pdo(); assert not(initial['statusword']&4), 'Unexpected initial PDO: '+str(initial)
    record['initialPdo']=initial
    error=sdo('0x603f',0)
    if full['axes'][0].get('fault'):
        assert error==0xe08, 'Only a stopped-bus E08 may be reset by this test'
        command('Reset'); wait(lambda a:a['state']=='Disabled' and not a.get('fault'))
    assert sdo('0x603f',0)==0
    original=dict(source=sdo('0x2005',20),gain=sdo('0x2008',20))
    record['original']=original
    assert original==dict(source=1,gain=1000), 'Unexpected drive configuration; stop without modifying'
    assert sdo('0x6091',1,'uint32')==sdo('0x6091',2,'uint32')==1
    assert sdo('0x607e',0,'uint8')==0, 'Unexpected polarity'
    record['effectiveParametersConfirmed']=True
    pid=run(['systemctl','show',UNIT,'--property=MainPID','--value']).stdout.decode().strip()
    def export_trace():
        existing={str(p) for p in (ROOT/'traces').glob('commissioned.csv.capture-*.csv')}
        run(['kill','-USR1',pid]);deadline=time.monotonic()+5
        while time.monotonic()<deadline:
            client.heartbeat()
            files=[p for p in (ROOT/'traces').glob('commissioned.csv.capture-*.csv') if str(p) not in existing]
            if files:
                time.sleep(.1);record.setdefault('traceCaptures',[]).append(str(files[0]));return
            time.sleep(.02)
        raise RuntimeError('Trace export acknowledgment timeout')
    export_trace();run(['kill','-USR2',pid]);time.sleep(.1)
    command('Enable'); wait(lambda a:a['state']=='Standstill')
    window=[]; deadline=time.monotonic()+5
    while time.monotonic()<deadline:
        a=snapshot()['axes'][0]
        window.append((time.monotonic(),round(a['actualPosition']*1048576),a['actualVelocity']))
        window=[s for s in window if s[0]>=window[-1][0]-.7]
        if stationary(window,32,.05): break
        time.sleep(.01)
    else: raise RuntimeError('Enabled standstill not confirmed')
    homeAccepted=False; deadline=time.monotonic()+3; attempts=0
    while time.monotonic()<deadline and attempts<5:
        a=snapshot()['axes'][0]
        if abs(a['actualVelocity'])>0.0005:
            time.sleep(.01); continue
        attempts+=1
        try:
            command('Home'); homeAccepted=True; break
        except RuntimeError as e:
            if 'invalid_state' not in str(e): raise
            record.setdefault('homeAdmissionRejections',[]).append(str(e))
            time.sleep(.02)
    assert homeAccepted, 'Home admission not accepted on zero-velocity frames'
    wait(lambda a:a['homed'] and a['state']=='Standstill')
    origin=snapshot()['axes'][0]['actualPosition']; assert abs(origin)<.001
    for speed,distance in [(.2,.2),(.4,.6),(.5,.8)]:
        stage=[]; record['activeSpeed']=speed
        for target in [origin+distance,origin]:
            before=snapshot()['axes'][0]['motionId']; previous_velocity=None
            command('MoveAbsolute',pos=target,vel=speed)
            deadline=time.monotonic()+8
            while time.monotonic()<deadline:
                full=snapshot(); a=full['axes'][0]
                sample=dict(a,speedRequested=speed,requestedTarget=target,
                            sampleCycle=full['sampleCycle'],sampleMonotonicNs=full['sampleMonotonicNs'])
                planned=abs(a['feedforward_velocity'])
                sample['phase']='not_classified_from_host_feedforward' if record['hostGain']==0 else 'hold' if planned<=1e-9 else 'cruise' if planned>=speed-1e-6 else 'acceleration' if previous_velocity is None or planned>previous_velocity+1e-9 else 'deceleration' if planned<previous_velocity-1e-9 else 'other'
                previous_velocity=planned
                stage.append(sample); record['samples'].append(sample)
                if a['motionDone'] and a['motionId']!=before:
                    assert abs(a['actualPosition']-target)<=.0001; break
                time.sleep(.003)
            else: raise RuntimeError('Move timeout')
        result=dict(speed=speed,status='PASS',peakError=max(a['peak_error'] for a in stage),
                    maxExcessMs=max(a['max_excess_ms'] for a in stage),
                    corePeakActualVelocity=max(a['peak_actual_velocity'] for a in stage),
                    sampledPhasePeak={phase:max(abs(s['cycle_error']) for s in stage if s['phase']==phase) for phase in set(s['phase'] for s in stage)})
        record['results'].append(result); print('STAGE',json.dumps(result),flush=True)
    record['status']='PASS'
except Exception as error:
    failed=True; record['status']='FAILED'; record['error']=repr(error)
    record['failureSnapshot']=record.get('lastSnapshot')
    record['traceback']=traceback.format_exc()
    print('TEST_STOPPED',str(error),flush=True)
finally:
    if client:
        for name in ['Stop','Disable']:
            try: command(name)
            except Exception as error: record.setdefault('cleanupErrors',[]).append(repr(error))
        try:
            deadline=time.monotonic()+15; cleanup_window=[]
            while time.monotonic()<deadline:
                snapshot(False); final_pdo=pdo()
                assert not(final_pdo['statusword']&4), 'Disable was not confirmed'
                cleanup_window.append((time.monotonic(),final_pdo['actual'],final_pdo['velocity']/1048576))
                cleanup_window=[s for s in cleanup_window if s[0]>=cleanup_window[-1][0]-.7]
                if stationary(cleanup_window): break
                time.sleep(.02)
            assert stationary(cleanup_window), 'Disabled stationary window not confirmed'
            record['cleanupWindow']=cleanup_window
            record['disabledPdo']=final_pdo
            if changed:
                sdo('0x2008',20,value=0)
                sdo('0x2005',20,value=original['source'])
                sdo('0x2008',20,value=original['gain'])
                restored=dict(source=sdo('0x2005',20),gain=sdo('0x2008',20))
                assert restored==original
                record['restored']=restored
            record['finalSnapshot']=snapshot(False)
            export_trace()
        except Exception as error:
            failed=True; record['status']='CLEANUP_FAILED'; record.setdefault('cleanupErrors',[]).append(repr(error))
        try: client.close()
        except Exception: pass
    if started:
        record['journal']=subprocess.run(['journalctl','-u',UNIT,'-n','20','--no-pager'],capture_output=True,text=True).stdout
        record['busLeftRunningDisabled']=bool(record.get('disabledPdo')) and subprocess.run(['systemctl','is-active','--quiet',UNIT]).returncode==0
    record['finishedUtc']=datetime.datetime.now(datetime.timezone.utc).isoformat()
    (OUT/'result.json').write_text(json.dumps(record,indent=2))
    print('RESULT',str(OUT),flush=True)
    print('SUMMARY',json.dumps({k:record.get(k) for k in ('status','error','results','cleanupErrors','disabledPdo','busLeftRunningDisabled')}),flush=True)
sys.exit(1 if failed else 0)
