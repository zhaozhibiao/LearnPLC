import pathlib,struct,subprocess,json
R=pathlib.Path('/home/a/st-workbench-i210/csp-timing-20261029')
r=json.loads((R/'complete-retest-wave-validation.json').read_text());assert r['status']=='PASS'
with open('/dev/cpu_dma_latency','wb',buffering=0) as f:
 f.write(struct.pack('i',0))
 subprocess.run(['python3',str(R/'commission_protected_low.py')],check=True)
 subprocess.run(['python3',str(R/'verify_protected_low.py'),'0.5'],check=True)
