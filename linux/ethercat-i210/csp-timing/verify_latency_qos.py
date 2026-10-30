import csv,json,pathlib,struct,subprocess,time,os
R=pathlib.Path('/home/a/st-workbench-i210/csp-timing-20261029');unit='st-workbench-csp-shadow1'
def run(a):return subprocess.run(a,check=True,capture_output=True,timeout=10).stdout.decode().strip()
assert not(int(run(['/opt/etherlab/bin/ethercat','upload','-p','0','-t','uint16','0x6041','0']).split()[-1])&4)
pid=run(['systemctl','show',unit,'--property=MainPID','--value'])
r={'motionCommands':[],'temporaryCpuLatencyUs':0}
with open('/dev/cpu_dma_latency','wb',buffering=0) as f:
 f.write(struct.pack('i',0));run(['kill','-USR2',pid]);time.sleep(30);run(['kill','-USR1',pid])
 p=R/'traces/shadow1.csv.capture-3.csv'
 for _ in range(100):
  if p.exists():break
  time.sleep(.05)
 time.sleep(.2)
 rows=list(csv.DictReader(x for x in p.open() if not x.startswith('#')))
 r.update(frames=len(rows),invalid=sum(not int(x['delivery_valid']) for x in rows),budgetOverruns=sum(int(x['actual_send_budget_overrun']) for x in rows),maxWakeLateNs=max(int(x['monotonic_ns'])-int(x['scheduled_ns']) for x in rows))
r['latencyRequestReleased']=True
r['disabled']=not(int(run(['/opt/etherlab/bin/ethercat','upload','-p','0','-t','uint16','0x6041','0']).split()[-1])&4)
(R/'latency-comparison.json').write_text(json.dumps(r,indent=2));print(json.dumps(r))
