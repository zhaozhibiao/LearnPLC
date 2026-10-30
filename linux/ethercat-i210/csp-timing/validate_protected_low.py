import csv,json,pathlib
R=pathlib.Path('/home/a/st-workbench-i210/csp-timing-20261029');files=sorted((R/'traces').glob('protected-low670.csv.capture-*.csv'));report={'files':[],'eligible':0,'enabled':0,'complete':True}
for p in files:
 text=p.read_text();rows=list(csv.DictReader(x for x in text.splitlines() if not x.startswith('#')));e=[x for x in rows if int(x['shadow_eligible'])];enabled=[x for x in rows if int(x['statusword'])&4]
 d={'name':p.name,'rows':len(rows),'droppedZero':'# dropped=0\n' in text,'complete':text.endswith('# complete=1\n'),'eligible':len(e),'shadowFaults':sum(int(x['shadow_fault'])!=0 for x in e),'enabledInvalidTiming':sum(not int(x['delivery_valid']) or int(x['actual_send_budget_overrun']) for x in enabled)}
 report['files'].append(d);report['eligible']+=len(e);report['enabled']+=len(enabled)
 assert d['complete'] and d['droppedZero'] and d['shadowFaults']==0 and d['enabledInvalidTiming']==0,d
assert len(files)==10 and report['eligible']>10000
report['status']='PASS';(R/'protected-low-wave-validation.json').write_text(json.dumps(report,indent=2));print(json.dumps(report))
