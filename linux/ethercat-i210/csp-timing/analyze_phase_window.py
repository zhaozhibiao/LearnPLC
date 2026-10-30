import csv,json,pathlib
R=pathlib.Path('/home/a/st-workbench-i210/csp-timing-20261029');ranges=[]
for prefix in ['retest1','protected-low1']:
 vals=[];budget=[]
 for p in (R/'traces').glob(prefix+'.csv.capture-*.csv'):
  if int(p.name.split('-')[-1].split('.')[0])<2:continue
  rows=csv.DictReader(x for x in p.open() if not x.startswith('#'))
  for r in rows:
   if int(r['bus_ready']) and int(r['delivery_metadata_valid']) and int(r['delivery_reference_step_valid']):
    vals.extend([int(r['delivery_reference_phase_ns']),int(r['delivery_predicted_phase_ns'])]);budget.append(int(r['tx_call_end_ns'])-int(r['rx_call_end_ns']))
 ranges.append({'prefix':prefix,'phaseMin':min(vals),'phaseMax':max(vals),'sendBudgetMaxNs':max(budget),'feasibleShiftLowExclusive':max(vals)+200000,'feasibleShiftHighExclusive':min(vals)+800000})
(R/'phase-window-evidence.json').write_text(json.dumps(ranges,indent=2));print(json.dumps(ranges))
