import json,re,sys
sys.path.insert(0,'/home/a/st-workbench-i210/is620n-native-candidate-20261023')
from stepped_motion_test import ControllerClient
c=ControllerClient('/run/st-workbench-motion-stage300/controller.sock');c.hello();s=c.snapshot();c.sock.close()
assert s['runtimeReady'] and s['ioReady'] and not s['axes'][0]['motionBusy'] and 'bus_ready=1' in s['diagnostics']
m=re.search(r'statusword=0x([0-9a-fA-F]+)',s['diagnostics']);assert m and not(int(m.group(1),16)&4)
assert 'error=0x0;' in s['diagnostics']
print(json.dumps(s))
