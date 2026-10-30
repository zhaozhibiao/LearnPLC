import subprocess,json,pathlib
if not __debug__:raise SystemExit('Assertions required')
def run(a):
 p=subprocess.run(a,capture_output=True,text=True,timeout=8);return {'code':p.returncode,'stdout':p.stdout.strip(),'stderr':p.stderr.strip()}
a=['/opt/etherlab/bin/ethercat'];s=run(a+['upload','-p','0','-t','uint16','0x6041','0']);assert s['code']==0 and not(int(s['stdout'].split()[-1])&4)
r={}
for address,t in [('0x0981','uint8'),('0x0990','uint64'),('0x09a0','uint32'),('0x0910','uint64')]:r[address]=run(a+['reg_read','-p','0','-t',t,address])
for idx in ('0x1c32','0x1c33'):
 for sub in ('12','13'):r[idx+':'+sub]=run(a+['upload','-p','0','-t','uint32',idx,sub])
p=pathlib.Path('/home/a/st-workbench-i210/csp-timing-20261029/sync-registers.json');p.write_text(json.dumps(r,indent=2));print(json.dumps(r))
