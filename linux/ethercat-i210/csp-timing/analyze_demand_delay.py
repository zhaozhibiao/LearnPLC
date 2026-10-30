"""Match command to demand exactly, never optimize against actual feedback."""
import csv,json,sys,statistics
from collections import defaultdict
from pathlib import Path
results=[]
for file in sys.argv[1:]:
 rows=list(csv.DictReader(x for x in Path(file).open() if not x.startswith('#')))
 groups=defaultdict(list)
 for i in range(15,len(rows)-5):
  if not all(int(rows[j]['state'])==3 and int(rows[j]['mode'])==8 and int(rows[j]['controlword'])==15 for j in range(i-12,i+1)):continue
  step=int(rows[i]['sent_target_raw'])-int(rows[i-1]['sent_target_raw'])
  old=int(rows[i-1]['sent_target_raw'])-int(rows[i-2]['sent_target_raw'])
  if abs(step)<50:continue
  phase='acceleration' if abs(step)-abs(old)>1 else 'deceleration' if abs(step)-abs(old)<-1 else 'steady_or_quantized'
  groups[('positive' if step>0 else 'negative')+'/'+phase].append(i)
 report={'file':file,'groups':{}}
 for group,indices in groups.items():
  residual=[abs(int(rows[i]['demand_raw'])-int(rows[i]['actual_raw'])-int(rows[i]['drive_error_raw'])) for i in indices]
  report['groups'][group]={'samples':len(indices),'exact_three_cycle_matches':sum(int(rows[i]['demand_raw'])==int(rows[i-3]['sent_target_raw']) for i in indices),'peak_demand_minus_actual_rev':max(abs(int(rows[i]['demand_raw'])-int(rows[i]['actual_raw'])) for i in indices)/1048576,'drive_error_identity_residual_counts_median':statistics.median(residual),'drive_error_identity_residual_counts_max':max(residual)}
 results.append(report)
print(json.dumps(results,indent=2))
