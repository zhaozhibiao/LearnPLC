"""Read-only migration status, deliberately excluding private configuration."""
import json,pathlib,sys
sys.path.insert(0,'/home/a/st-workbench-deployment')
import coordinator,ethercat_hardware
config=json.loads(pathlib.Path('/home/a/st-workbench-deployment/config.json').read_text())
with coordinator.RestRuntimeConnection(config) as connection: state=connection.status()
endpoint='/run/st-workbench-is620n/controller.sock'
hello=ethercat_hardware.native_request(endpoint,'HelloRequest',{'clientName':'migration-audit','clientVersion':'1'},'HelloResponse')
snapshot=ethercat_hardware.native_request(endpoint,'ReadSnapshotRequest',{'includeDiagnostics':False},'SnapshotResponse')
print(json.dumps(dict(plcState=state,hello=hello,snapshot=snapshot),ensure_ascii=False))
