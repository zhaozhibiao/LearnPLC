import json,pathlib,subprocess
ROOT=pathlib.Path('/home/a/st-workbench-i210/csp-timing-20261029')
def read(i,s,t):
 p=subprocess.run(['/opt/etherlab/bin/ethercat','upload','-p','0','-t',t,hex(i),str(s)],capture_output=True,text=True,timeout=5)
 return {'code':p.returncode,'out':p.stdout.strip(),'error':p.stderr.strip()}
r={'status':read(0x6041,0,'uint16')}
assert r['status']['code']==0 and not(int(r['status']['out'].split()[-1])&4)
r['objects']={}
for i,s,t in [(0x60c2,1,'uint8'),(0x60c2,2,'int8'),(0x6065,0,'uint32'),(0x6066,0,'uint16'),(0x603f,0,'uint16')]+[(i,s,'uint32') for i in [0x1c32,0x1c33] for s in [2,3,5,6,9,12,13]]:
 r['objects'][f'{i:04x}:{s}']=read(i,s,t)
(ROOT/'delay-object-probe.json').write_text(json.dumps(r,indent=2));print(json.dumps(r))
