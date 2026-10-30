import json,pathlib,struct,subprocess
R=pathlib.Path('/home/a/st-workbench-i210/csp-timing-20261029')
r=json.loads((R/'latency-comparison.json').read_text());assert r['frames']>=30000 and r['invalid']==0 and r['budgetOverruns']==0 and r['disabled']
with open('/dev/cpu_dma_latency','wb',buffering=0) as f:
 f.write(struct.pack('i',0))
 p=subprocess.run(['python3',str(R/'verify_shadow_low_speed.py'),'0.5'])
raise SystemExit(p.returncode)
