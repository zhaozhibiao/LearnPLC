import subprocess,time,json,pathlib
R=pathlib.Path('/home/a/st-workbench-i210/csp-timing-20261029')
def run(a):return subprocess.run(a,check=True,capture_output=True,timeout=5).stdout.decode().strip()
def read(i,t):return int(run(['/opt/etherlab/bin/ethercat','upload','-p','0','-t',t,i,'0']).split()[-1])
p=[];v=[]
for _ in range(20):
 assert not(read('0x6041','uint16')&4);v.append(read('0x606c','int32'));p.append(read('0x6064','int32'));time.sleep(.03)
assert max(p)-min(p)<=4 and max(abs(x) for x in v)<=1048576*.02, str({'positions':p,'velocities':v})
pid=run(['systemctl','show','st-workbench-csp-shadow1','--property=MainPID','--value']);run(['kill','-USR1',pid]);time.sleep(.5)
r={'disabled':True,'stationary':True,'maxVelocityRaw':max(abs(x) for x in v),'positionRangeCounts':max(p)-min(p),'errorCode':read('0x603f','uint16')};(R/'shadow-cleanup-confirmation.json').write_text(json.dumps(r));print(json.dumps(r))
