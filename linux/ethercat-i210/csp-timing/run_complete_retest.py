import pathlib,struct,subprocess
R=pathlib.Path('/home/a/st-workbench-i210/csp-timing-20261029')
with open('/dev/cpu_dma_latency','wb',buffering=0) as f:
 f.write(struct.pack('i',0))
 subprocess.run(['python3',str(R/'commission_complete_retest.py')],check=True)
 subprocess.run(['python3',str(R/'verify_complete_retest.py'),'0.5'],check=True)
