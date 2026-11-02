"""Run one standard coordinator action for the immutable migration candidates."""
import argparse,hashlib,json,pathlib,sys,uuid
sys.path.insert(0,'/home/a/st-workbench-deployment')
import coordinator
root=pathlib.Path('/home/a/st-workbench-i210/runtime-migration-release-20261101')
cfg=json.loads(pathlib.Path('/home/a/st-workbench-deployment/config.json').read_text())
client=coordinator.Coordinator(cfg)
p=argparse.ArgumentParser();p.add_argument('action',choices=['capabilities','hardware-stage','stage','hardware-validate','hardware-apply','compile','activate','status','hardware-export','program-export']);p.add_argument('--acceptance',action='store_true');p.add_argument('--rpm',type=int,choices=[300,1000,3000]);args=p.parse_args()
if args.action=='capabilities':
    with client.connection() as c:print(json.dumps(dict(plcState=c.status(),taskScheduling=c.task_scheduling_supported())))
    raise SystemExit(0)
hardware='88ffa77219170e02da362a5f8a04aaf67d66c115282fb34b6be98b9db4c1c278'
program='E3FFEE922D71AFBDB72C4569BD89F5DD36A7BBCF89A913D75BC404DBE2E7F9F8'
if args.acceptance:program='03FF0AEB1834D5CA5D3A4372E4A1BF383EC574FDB49115155227B11A565448A3'
if args.rpm:
    profile=json.loads((root/f'acceptance-profile-{args.rpm}.json').read_text())
    hardware,program=profile['hardwareBuild'],profile['build']
build=hardware if args.action.startswith('hardware-') else program
request=dict(action=args.action,request_id='migration-'+uuid.uuid4().hex,build_hash=build)
if args.action in ['stage','hardware-stage']:
    path=root/(f'hardware-{args.rpm}.zip' if args.rpm and args.action=='hardware-stage' else f'acceptance-{args.rpm}.zip' if args.rpm else 'hardware.zip' if args.action=='hardware-stage' else 'acceptance.zip' if args.acceptance else 'program.zip')
    request.update(package_path=str(path),package_sha256=hashlib.sha256(path.read_bytes()).hexdigest())
if args.action in ['activate','hardware-apply']:request['expected_active_build_hash']=client.active()['build_hash']
response=client.execute(request)
out=root/('result-'+args.action+'.json');out.write_text(json.dumps(response,ensure_ascii=False,indent=2));out.chmod(0o600)
print(json.dumps({k:v for k,v in response.items() if k not in ['log','package_base64','archive_base64']},ensure_ascii=False))
if response.get('success') is not True:raise SystemExit(1)
