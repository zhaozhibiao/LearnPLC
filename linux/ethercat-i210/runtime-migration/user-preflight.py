"""Private user-side migration snapshot; no service/device mutations."""
import datetime,hashlib,json,os,pathlib,shutil,sys,uuid
if os.geteuid()==0 or pathlib.Path.home()!=pathlib.Path('/home/a'):raise SystemExit('Run as user a')
os.umask(0o077)
deploy=pathlib.Path('/home/a/st-workbench-deployment');cfg=json.loads((deploy/'config.json').read_text())
sys.path.insert(0,str(deploy));import coordinator
with coordinator.RestRuntimeConnection(cfg) as c: plc_state=c.status()
backup=pathlib.Path('/home/a/.local/share/st-workbench/runtime-migration-backups')/(datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ-')+uuid.uuid4().hex)
backup.mkdir(mode=0o700,parents=True)
paths=[deploy/'config.json',*(deploy/n for n in ['coordinator.py','axis_gate.py','ethercat_hardware.py','motion_cycle.py','configuration_download.py','scoped_transfer.py','runtime-profile.json'])]
for k in ['credentials_file','ca_file']:
 if cfg.get(k):paths.append(pathlib.Path(cfg[k]))
units=pathlib.Path('/home/a/.config/systemd/user')
for n in ['stworkbench-openplc.service','stworkbench-is620n-axis.service','stworkbench-engineering.service']:
 p=units/n
 if p.exists():paths.append(p)
 d=units/(n+'.d')
 if d.is_dir():paths.extend(d.glob('*.conf'))
axis=pathlib.Path('/home/a/.local/share/st-workbench/is620n-axis')
paths.extend(axis/n for n in ['axis_worker.py','axis-manifest.json'] if (axis/n).is_file())
prefix=pathlib.Path('/home/a/.local/share/st-workbench/runtime-v4.3.0/deployment')
paths.extend(prefix/n for n in ['active.json','hardware-active.json','state.json','runtime-session.json'] if (prefix/n).is_file())
build=pathlib.Path('/home/a/.local/share/st-workbench/runtime-v4.3.0/source/build')
paths.extend(build/n for n in ['plc_main','libplc_stworkbench.so'] if (build/n).is_file())
web=build.parent/'webserver'
paths.extend(web/n for n in ['restapi.py','workbench_capabilities.py'] if (web/n).is_file())
items=[]
for i,p in enumerate(dict.fromkeys(paths)):
 if p.is_symlink() or not p.is_file():raise ValueError('Required private backup file missing or symbolic')
 data=p.read_bytes();dst=backup/str(i);dst.write_bytes(data);dst.chmod(0o600)
 items.append(dict(path=str(p),payload=dst.name,sha256=hashlib.sha256(data).hexdigest(),mode=p.stat().st_mode&0o777))
manifest=dict(schemaVersion=1,plcState=plc_state,files=items)
(backup/'manifest.json').write_text(json.dumps(manifest,indent=2));(backup/'manifest.json').chmod(0o600)
for item in items:assert hashlib.sha256((backup/item['payload']).read_bytes()).hexdigest()==item['sha256']
print(json.dumps(dict(status='private-user-backup-verified',plcState=plc_state,backup=str(backup),files=len(items))))
