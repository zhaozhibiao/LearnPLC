import pathlib,json,os,sys
P=pathlib.Path
sys.path.insert(0,'/home/a/st-workbench-deployment')
from coordinator import RestRuntimeConnection
cfg=json.loads(P('/home/a/st-workbench-deployment/config.json').read_text())
with RestRuntimeConnection(cfg) as c:state=c.status()
r={'bootId':P('/proc/sys/kernel/random/boot_id').read_text().strip(),'kernelCommandLine':P('/proc/cmdline').read_text().strip(),'online':P('/sys/devices/system/cpu/online').read_text().strip(),'isolated':P('/sys/devices/system/cpu/isolated').read_text().strip(),'cpu6Online':P('/sys/devices/system/cpu/cpu6/online').read_text().strip(),'workqueueMask':P('/sys/devices/virtual/workqueue/cpumask').read_text().strip(),'defaultIrqMask':P('/proc/irq/default_smp_affinity').read_text().strip(),'plcState':state,'normalProcessAffinityExceptions':[]}
for p in P('/proc').glob('[0-9]*'):
 try:
  if not (p/'cmdline').read_bytes():continue
  for t in (p/'task').iterdir():
   text=(t/'status').read_text();cpus=next(x.split(':')[1].strip() for x in text.splitlines() if x.startswith('Cpus_allowed_list:'))
   if cpus!='0-1,3-5,7':r['normalProcessAffinityExceptions'].append({'pid':p.name,'tid':t.name,'cpus':cpus})
 except (OSError,StopIteration):continue
assert r['cpu6Online']=='0' and r['plcState'] in ('STOPPED','EMPTY')
assert not r['normalProcessAffinityExceptions'],r['normalProcessAffinityExceptions']
r['verified']=True
P('/home/a/st-workbench-i210/cpu-isolation-20261030/postboot-verification.json').write_text(json.dumps(r,indent=2));print(json.dumps(r))
