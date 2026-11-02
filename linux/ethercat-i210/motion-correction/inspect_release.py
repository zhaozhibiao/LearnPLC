import json,pathlib,sys
sys.path.insert(0,'/home/a/st-workbench-i210/is620n-native-candidate-20261023')
from stepped_motion_test import ControllerClient
p=pathlib.Path('/home/a/.local/share/st-workbench/runtime-v4.3.0/deployment/.versions/ddb0ff07f79f6595ff1b481a6fdb2d26109c965a8ce58b78ec7bf345085efdad/core/generated')
print('FORMAL_BUNDLE_SOURCE_AVAILABLE', (p/'machine-config.json').exists())
c=ControllerClient('/run/st-workbench-motion-correction-commissioned/controller.sock');c.hello();s=c.snapshot();c.sock.close()
assert s['runtimeReady'] and s['ioReady'] and s['axes'][0]['state']=='Disabled' and not s['axes'][0].get('fault') and 'error=0x0;' in s['diagnostics']
print('FINAL_CANDIDATE_STATE',json.dumps(s))
