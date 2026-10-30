import csv,json,sys
from pathlib import Path
sys.path.insert(0,'linux/ethercat-i210/csp-timing')
from demand_guard import DemandGuard
result=[]
for name in ['guard501','test300']:
 rows=list(csv.DictReader(x for x in open(f'artifacts/csp-timing-20261029/evidence/traces/{name}.csv.capture-4.csv') if not x.startswith('#')))
 g=DemandGuard();faults=[];n=0;peak=0
 for r in rows:
  if int(r['mode'])!=8 or int(r['controlword'])!=15 or int(r['state']) != 3:
   g=DemandGuard();continue
  fields={k:int(r[k]) for k in ['cycle','scheduled_ns','sent_target_raw','demand_raw','actual_raw','drive_error_raw']}
  v=g.update(**fields,valid=bool(int(r['bus_ready']) and int(r['delivery_valid']) and not int(r['actual_send_budget_overrun'])))
  if v['ready']:
   n+=1;peak=max(peak,v['error_raw'])
   if v['fault'] and len(faults)<10:faults.append({'cycle':r['cycle'],'fault':v['fault']})
 result.append(dict(trace=name,eligibleCycles=n,peakConservativeErrorRev=peak/1048576,faults=faults))
Path('artifacts/csp-timing-20261029/evidence/demand-guard-shadow.json').write_text(json.dumps(result,indent=2));print(json.dumps(result,indent=2))

