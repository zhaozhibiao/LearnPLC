import datetime,json,pathlib,subprocess
if not __debug__:raise SystemExit('Assertions required')
ROOT=pathlib.Path('/home/a/st-workbench-i210/motion-correction-20261029')
def call(a):
 p=subprocess.run(a,capture_output=True,text=True,timeout=8)
 return {'code':p.returncode,'stdout':p.stdout.strip(),'stderr':p.stderr.strip()}
def sdo(i,s,t):return call(['/opt/etherlab/bin/ethercat','upload','-p','0','-t',t,hex(i),str(s)])
status=sdo(0x6041,0,'uint16');assert status['code']==0 and not(int(status['stdout'].split()[-1])&4)
r={'utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'status':status,'sync':{}}
for idx in (0x1c32,0x1c33):
 r['sync'][hex(idx)]={}
 for sub,t in [(0,'uint8'),(1,'uint16'),(2,'uint32'),(3,'uint32'),(4,'uint16'),(5,'uint32'),(6,'uint32'),(9,'uint32'),(12,'uint16'),(13,'uint16'),(32,'uint8')]:r['sync'][hex(idx)][str(sub)]=sdo(idx,sub,t)
for name,args in {'master':['/opt/etherlab/bin/ethercat','master'],'slave':['/opt/etherlab/bin/ethercat','slaves','-v'],'links':['ip','-brief','link'],'kernel':['uname','-a'],'cmdline':['cat','/proc/cmdline'],'regHelp':['/opt/etherlab/bin/ethercat','reg_read','--help']}.items():r[name]=call(args)
(ROOT/'timing-probe.json').write_text(json.dumps(r,indent=2));print(json.dumps(r))
