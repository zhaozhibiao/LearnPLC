import csv,hashlib,json,pathlib,subprocess,sys,time
if not __debug__:raise SystemExit('Assertions required')
sys.path.insert(0,'/home/a/st-workbench-i210/is620n-native-candidate-20261023')
from stepped_motion_test import ControllerClient
ROOT=pathlib.Path('/home/a/st-workbench-i210/csp-timing-20261029')
OLD=pathlib.Path('/home/a/st-workbench-i210/motion-correction-20261029')
UNIT='st-workbench-csp-smooth-1000a';oldunit='st-workbench-csp-smooth-300a'
assert UNIT != oldunit, 'New test service must differ from predecessor'
result={'status':'STARTING','axisCommands':[]};client=None;old_stopped=False;started=False

def run(a):return subprocess.run(a,check=True,capture_output=True,timeout=20)
def read(i,s,t='uint16'):
 return int(run(['/opt/etherlab/bin/ethercat','upload','-p','0','-t',t,i,str(s)]).stdout.decode().split()[-1])
try:
 assert '--safe-mode' in run(['pgrep','-af','plc_main']).stdout.decode()
 assert read('0x2005',20)==1 and read('0x2008',20)==1000
 positions=[]
 for _ in range(30):
  assert not(read('0x6041',0)&4)
  positions.append(read('0x6064',0,'int32'));time.sleep(.02)
 assert max(positions)-min(positions)<=4,'Disabled drive position is not stable'
 machine=json.loads((OLD/'config/commissioned-machine.json').read_text())
 axis=machine['axes'][0]
 assert axis['motionSafety']['maximumFollowingError']==.005 and axis['motionSafety']['followingErrorTimeoutMilliseconds']==20
 assert axis['homing']['method']==35 and axis['homing']['allowCurrentPositionZero']
 axis.update(maximumVelocity=16.666666666666668,maximumAcceleration=2.0,maximumDeceleration=2.0,maximumJerk=20.0,driveErrorOnly=True,demandDeliveryEnforced=False,demandDeliveryTolerance=0.01,softLimitNegative=-300.0,softLimitPositive=300.0,velocityFeedforwardGain=0)
 axis['motionSafety']['maximumActualVelocity']=20.0
 axis['driveDemandProtection']=True
 axis['velocityFeedbackRawPerUnitPerSecond']=1048576
 axis['velocityOffsetRawPerUnitPerSecond']=1048576
 machine['cycleTiming']={'enabled':True,'model':'R2M','boundaryGuardNs':200000,'remainingSendBudgetNs':100000,'requiredGoodCycles':1000}
 mp=ROOT/'config/smooth-1000-machine.json';mp.write_text(json.dumps(machine,indent=2))
 ec=json.loads((OLD/'config/commissioned-ethercat.json').read_text())
 ec['slaves'][0]['startupSdos']=[{'index':8197,'subindex':20,'data':[1,0],'transition':'PS'},{'index':8200,'subindex':20,'data':[232,3],'transition':'PS'}]
 ec['slaves'][0]['startupSdos'].append({'index':0x6065,'subindex':0,'data':[0xf6,0x28,0,0],'transition':'PS'})
 ec['slaves'][0]['dc']['sync0ShiftNs']=670000
 ec['machineSha256']=hashlib.sha256(mp.read_bytes()).hexdigest()
 ec['ioMapSha256']=hashlib.sha256((ROOT/'config/is620n-io-map.json').read_bytes()).hexdigest()
 ec['topologyFingerprint']=hashlib.sha256(json.dumps({k:v for k,v in ec.items() if k!='topologyFingerprint'},sort_keys=True,separators=(',',':'),ensure_ascii=False).encode()).hexdigest()
 ep=ROOT/'config/smooth-1000-ethercat.json';ep.write_text(json.dumps(ec,indent=2))
 (ROOT/'traces').mkdir(exist_ok=True)
 assert run(['systemctl','show',UNIT,'--property=ActiveState','--value']).stdout.decode().strip() in ('','inactive','failed')
 if subprocess.run(['systemctl','is-active','--quiet',oldunit]).returncode==0:
  run(['systemctl','stop',oldunit]);old_stopped=True
 subprocess.run(['systemctl','reset-failed',UNIT],capture_output=True)
 started=True
 run(['systemd-run','--unit='+UNIT,'--service-type=exec','--property=Group=a','--property=LimitMEMLOCK=infinity','--property=LimitRTPRIO=95','--property=RuntimeDirectory='+UNIT,'--property=TimeoutStopSec=15',str(ROOT/'smooth-build/ecmotion-rt'),'--cpu','2','--machine-config',str(mp),'--io-map',str(ROOT/'config/is620n-io-map.json'),'--ethercat-config',str(ep),'--socket','/run/'+UNIT+'/controller.sock','--axis-trace-csv',str(ROOT/'traces/smooth-1000a.csv'),'--axis-trace-capacity','120000'])
 stable=0;deadline=time.monotonic()+25
 while time.monotonic()<deadline:
  try:
   if client is None:client=ControllerClient('/run/'+UNIT+'/controller.sock');client.hello()
   snap=client.snapshot();ready=snap['runtimeReady'] and snap['ioReady'] and 'bus_ready=1' in snap['diagnostics'] and snap['axes'][0]['timingReady'] and not snap['axes'][0]['timingFault']
   stable=stable+1 if ready else 0
   if stable>=30:break
  except OSError:
   if client:client.sock.close()
   client=None
  time.sleep(.05)
 assert stable>=30,'Runtime did not reach stable OP'
 assert snap['axes'][0]['timingReady'] and not snap['axes'][0]['timingFault'],'Motion clock not ready'
 pid=run(['systemctl','show',UNIT,'--property=MainPID','--value']).stdout.decode().strip()
 for sequence in (1,2):
  time.sleep(10) # Allow a full measurement window after startup lock.
  run(['kill','-USR1',pid]);csvpath=ROOT/('traces/smooth-1000a.csv.capture-'+str(sequence)+'.csv')
  deadline=time.monotonic()+10
  while time.monotonic()<deadline and not csvpath.exists():time.sleep(.05)
  time.sleep(.2)
  text=csvpath.read_text();rows=list(csv.DictReader(l for l in text.splitlines() if not l.startswith('#')))
  assert len(rows)>100 and '# dropped=0' in text
  assert all(not(int(r['statusword'])&4) and int(r['controlword'])==0 for r in rows)
  snap=client.snapshot();assert snap['runtimeReady'] and snap['ioReady'] and 'bus_ready=1' in snap['diagnostics']
  assert read('0x603f',0)==0 and not(read('0x6041',0)&4)
  valid=[r for r in rows if int(r['motion_timing_ready'])]
  assert len(valid)>1000,'Too few ready timing samples'
  result.setdefault('timingSummary',[]).append({'readySamples':len(valid),'invalidSamples':sum(not int(r['delivery_valid']) for r in rows),'preSendOverruns':sum(int(r['pre_send_budget_overrun']) for r in rows),'actualSendOverruns':sum(int(r['actual_send_budget_overrun']) for r in rows)})
  if sequence==2:
   assert all(int(r['delivery_valid']) and int(r['motion_timing_ready']) for r in rows),'Steady timing lost lock'
   assert all(not int(r['pre_send_budget_overrun']) and not int(r['actual_send_budget_overrun']) for r in rows),'Send budget overrun'
  result.setdefault('captures',[]).append({'path':str(csvpath),'rows':len(rows),'OP_after_export':True})
  run(['kill','-USR2',pid]);time.sleep(.5)
 assert read('0x6065',0,'uint32')==10486, 'Drive following window was not applied'
 result['driveParameters']={'source':read('0x2005',20),'gain':read('0x2008',20)}
 assert result['driveParameters']=={'source':1,'gain':1000}
 result['status']='PASS';result['snapshot']=snap;result['binarySha256']=hashlib.sha256((ROOT/'smooth-build/ecmotion-rt').read_bytes()).hexdigest()
except Exception as e:
 result['status']='FAILED';result['error']=repr(e)
 if started:
  result['leftRunningDisabled']=not(read('0x6041',0)&4)
  if not result['leftRunningDisabled']:run(['systemctl','stop',UNIT])

finally:
 if client:client.sock.close()
 (ROOT/'smooth-1000a-acceptance.json').write_text(json.dumps(result,indent=2));print(json.dumps(result))

sys.exit(0 if result["status"]=="PASS" else 1)
