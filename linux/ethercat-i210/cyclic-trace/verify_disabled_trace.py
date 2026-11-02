#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Bounded device acceptance. ReadSnapshot/Hello only; no control lease or axis commands."""
import argparse,csv,datetime,hashlib,json,pathlib,re,struct,subprocess,sys,time

if not __debug__:
    raise SystemExit('Refuse optimized Python: device acceptance assertions must remain enabled')

parser=argparse.ArgumentParser();parser.add_argument('root',type=pathlib.Path)
ROOT=parser.parse_args().root.resolve()
sys.path.insert(0,'/home/a/st-workbench-i210/is620n-native-candidate-20261023')
from stepped_motion_test import ControllerClient
UNIT='st-workbench-is620n-trace'
OUT=ROOT/('disabled-trace-'+datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%d-%H%M%S'));OUT.mkdir()
result=dict(status='STARTING',commandsSent=[],snapshots=[]);started=False;client=None
def run(args):return subprocess.run(args,check=True,capture_output=True,timeout=10)
def upload(index,sub,type='uint16'):
    reply=run(['/opt/etherlab/bin/ethercat','upload','-p','0','-t',type,index,str(sub)]).stdout.decode().strip()
    return int(reply.split()[-1])
try:
    assert '--safe-mode' in run(['pgrep','-af','plc_main']).stdout.decode()
    assert subprocess.run(['pgrep','-x','ecmotion-rt'],capture_output=True).returncode==1
    initial=dict(statusword=upload('0x6041',0),velocity=upload('0x606c',0,'int32'))
    assert not(initial['statusword']&4) and initial['velocity']==0,'Initial drive is not disabled/stationary'
    result['initial']=initial
    unit_state=run(['systemctl','show',UNIT,'--property=ActiveState','--value']).stdout.decode().strip()
    if unit_state not in ('','inactive','failed'):
        raise RuntimeError('Diagnostic unit is already active or transitioning: '+unit_state)
    if subprocess.run(['systemctl','is-failed','--quiet',UNIT]).returncode==0:run(['systemctl','reset-failed',UNIT])
    trace=OUT/'axis-trace.csv'
    arguments=[str(ROOT/'build/ecmotion-rt'),'--cpu','2',
        '--machine-config',str(ROOT/'config/is620n-machine.json'),
        '--io-map',str(ROOT/'config/is620n-io-map.json'),
        '--ethercat-config',str(ROOT/'config/axis-trace-ethercat.json'),
        '--socket','/run/'+UNIT+'/controller.sock','--axis-trace-csv',str(trace),
        '--axis-trace-capacity','60000']
    result['binarySha256']=hashlib.sha256((ROOT/'build/ecmotion-rt').read_bytes()).hexdigest()
    # Mark the launch attempt first: a client timeout can occur after systemd starts it.
    started=True
    run(['systemd-run','--unit='+UNIT,'--service-type=exec','--property=Group=a',
        '--property=LimitMEMLOCK=infinity','--property=LimitRTPRIO=95',
        '--property=RuntimeDirectory='+UNIT,'--property=RuntimeMaxSec=60','--property=TimeoutStopSec=15',*arguments])
    stable=0;deadline=time.monotonic()+20
    while time.monotonic()<deadline and stable<30:
        try:
            if client is None:
                client=ControllerClient('/run/'+UNIT+'/controller.sock');client.hello()
            snap=client.snapshot();result['snapshots'].append(snap)
            ready=snap['runtimeReady'] and snap['ioReady'] and 'bus_ready=1' in snap['diagnostics']
            # PREOP/SAFEOP process inputs are not valid motion evidence.
            if ready:
                state=re.search(r'statusword=0x([0-9a-fA-F]+)',snap['diagnostics'])
                assert state and not(int(state.group(1),16)&4),'Drive unexpectedly enabled'
                assert abs(snap['axes'][0]['actualVelocity'])<=.05,'Unexpected substantial disabled velocity'
            stable=stable+1 if ready else 0
        except OSError:
            if client:client.sock.close()
            client=None;stable=0
        time.sleep(.05)
    assert stable>=30,'Bus did not reach stable OP/complete WKC'
    assert upload('0x1c13',1)==0x1a00
    assert upload('0x1a00',0,'uint8')==8
    mapping=[upload('0x1a00',i,'uint32') for i in range(1,9)]
    assert mapping==[0x603f0010,0x60410010,0x60640020,0x606c0020,0x60610008,0x60620020,0x60f40020,0x60770010]
    result['txPdoMapping']=mapping
    raw=run(['/opt/etherlab/bin/ethercat','data','--domain','0']).stdout
    assert len(raw)==34
    result['rawPdoHex']=raw.hex()
    result['disabledPdo']=dict(statusword=struct.unpack_from('<H',raw,13)[0],velocity=struct.unpack_from('<i',raw,19)[0],
        demand=struct.unpack_from('<i',raw,24)[0],followingError=struct.unpack_from('<i',raw,28)[0],torque=struct.unpack_from('<h',raw,32)[0])
    assert not(result['disabledPdo']['statusword']&4) and result['disabledPdo']['velocity']==0
    result['pdos']=run(['/opt/etherlab/bin/ethercat','pdos','-p','0']).stdout.decode()
    result['status']='DEVICE_MAPPING_PASS'
except Exception as error:
    result['status']='FAILED';result['error']=repr(error)
finally:
    if client:client.sock.close()
    if started:
        try:
            stop=subprocess.run(['systemctl','stop',UNIT],capture_output=True,text=True,timeout=20)
            result['stopExitCode']=stop.returncode
        except Exception as error:
            result['stopError']=repr(error);result['status']='FAILED'
        try:
            result['journal']=subprocess.run(['journalctl','-u',UNIT,'-n','30','--no-pager'],capture_output=True,text=True,timeout=5).stdout
        except Exception as error:
            result['journalError']=repr(error)
    try:
        trace=OUT/'axis-trace.csv'
        text=trace.read_text();rows=list(csv.DictReader(line for line in text.splitlines() if not line.startswith('#')))
        assert len(rows)>1000,'Insufficient recorded cycles'
        cycles=[int(r['cycle']) for r in rows]
        assert cycles==list(range(cycles[0],cycles[0]+len(cycles))),'Cycle sequence gap'
        assert all(int(b['scheduled_ns'])-int(a['scheduled_ns'])==1000000 for a,b in zip(rows,rows[1:])),'Scheduled period differs from 1ms'
        assert all(int(b['monotonic_ns'])>int(a['monotonic_ns']) for a,b in zip(rows,rows[1:])),'Nonmonotonic sample time'
        assert '# dropped=0' in text,'Unexpected recorder overflow'
        valid=[r for r in rows if r['bus_ready']=='1']
        assert len(valid)>=1000,'Insufficient valid PDO frames'
        assert all(not(int(r['statusword'])&4) for r in rows),'Drive was enabled unexpectedly'
        assert all(int(r['controlword'])==0 and int(r['motion_id'])==0 and int(r['offset_raw'])==0 for r in rows),'Unexpected output or motion command'
        assert all(abs(float(r['actual_position'])-int(r['actual_raw'])/1048576)<1e-12 for r in valid),'Snapshot/PDO position mismatch'
        result['traceSummary']=dict(rows=len(rows),validRows=len(valid),firstCycle=cycles[0],lastCycle=cycles[-1],
            maximumLatenessNs=max(int(r['monotonic_ns'])-int(r['scheduled_ns']) for r in rows),
            demandRange=[min(int(r['demand_raw']) for r in valid),max(int(r['demand_raw']) for r in valid)],
            driveErrorRange=[min(int(r['drive_error_raw']) for r in valid),max(int(r['drive_error_raw']) for r in valid)],
            torqueRange=[min(int(r['torque_raw']) for r in valid),max(int(r['torque_raw']) for r in valid)])
        if result['status']=='DEVICE_MAPPING_PASS' and result.get('stopExitCode')==0:result['status']='PASS'
    except Exception as error:
        result['traceError']=repr(error);result['status']='FAILED'
    result['noAxisCoreRunning']=subprocess.run(['pgrep','-x','ecmotion-rt'],capture_output=True).returncode==1
    try:
        result['final']=dict(statusword=upload('0x6041',0),velocity=upload('0x606c',0,'int32'),
            source=upload('0x2005',20),gain=upload('0x2008',20))
        assert not(result['final']['statusword']&4) and result['final']['velocity']==0
        assert result['final']['source']==1 and result['final']['gain']==0 and result['noAxisCoreRunning']
    except Exception as error:
        result['finalError']=repr(error);result['status']='FAILED'
    (OUT/'result.json').write_text(json.dumps(result,indent=2))
    print('RESULT',OUT,flush=True)
    print(json.dumps({k:v for k,v in result.items() if k not in ('snapshots','journal','pdos')}),flush=True)
sys.exit(0 if result['status']=='PASS' else 1)
