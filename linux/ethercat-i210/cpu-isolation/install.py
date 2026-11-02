#!/usr/bin/python3
"""Install fixed isolation only after stopped controller/drive checks; never enables motion."""
import datetime,json,os,pathlib,shutil,socket,subprocess,sys
P=pathlib.Path

def run(args):return subprocess.run(args,check=True,capture_output=True,text=True,timeout=90).stdout.strip()
def preflight():
 assert os.geteuid()==0
 assert P('/sys/devices/system/cpu/cpu2/topology/thread_siblings_list').read_text().strip()=='2,6'
 assert subprocess.run(['pgrep','-x','ecmotion-rt'],capture_output=True).returncode==1,'Motion core is running'
 cfg=json.loads(P('/home/a/st-workbench-deployment/config.json').read_text())
 if cfg.get('control_transport')=='rest':
  query="import sys,json;sys.path.insert(0,'/home/a/st-workbench-deployment');from coordinator import RestRuntimeConnection;c=json.load(open('/home/a/st-workbench-deployment/config.json'));\nwith RestRuntimeConnection(c) as r:print('STATUS:'+r.status())"
  response=run(['runuser','-u','a','--','python3','-c',query]).encode()
 else:
  endpoint=cfg['socket_path']
  with socket.socket(socket.AF_UNIX,socket.SOCK_STREAM) as s:
   s.settimeout(4);s.connect(endpoint);s.sendall(b'STATUS\n');response=s.recv(128)

 assert response.strip() in (b'STATUS:STOPPED',b'STATUS:EMPTY'),repr(response)
 status=run(['/opt/etherlab/bin/ethercat','upload','-p','0','-t','uint16','0x6041','0'])
 assert not(int(status.split()[-1])&4),'Drive operation enabled'
 return {'plc':response.decode().strip(),'driveStatus':status,'bootId':P('/proc/sys/kernel/random/boot_id').read_text().strip()}

def install():
 report=preflight();base=P(__file__).resolve().parent
 stamp=datetime.datetime.now().strftime('%Y%m%d-%H%M%S');backup=P('/var/backups/st-workbench-cpu-isolation')/stamp
 backup.mkdir(parents=True,mode=0o700,exist_ok=False)
 helper=(base/'boot-helper.py').read_text(encoding='utf-8-sig')
 params='isolcpus=domain,managed_irq,2,6 nohz_full=2,6 rcu_nocbs=2,6 irqaffinity=0-1,3-5,7'
 targets={
 '/etc/default/grub.d/90-st-workbench-isolation.cfg':'# ST Workbench reserved physical core\nGRUB_CMDLINE_LINUX_DEFAULT="${GRUB_CMDLINE_LINUX_DEFAULT} '+params+'"\n',
 '/etc/systemd/system.conf.d/90-st-workbench-isolation.conf':'[Manager]\nCPUAffinity=\nCPUAffinity=0-1 3-5 7\n',
 '/etc/systemd/user.conf.d/90-st-workbench-isolation.conf':'[Manager]\nCPUAffinity=\nCPUAffinity=0-1 3-5 7\n',
 '/etc/systemd/system/user.slice.d/90-st-workbench-isolation.conf':'[Slice]\nAllowedCPUs=0-1 3-5 7\n',
 '/usr/local/libexec/st-workbench-cpu-isolation.py':helper,
 '/etc/systemd/system/st-workbench-cpu-isolation.service':'[Unit]\nDescription=Reserve physical core for ST Workbench motion\nAfter=local-fs.target\nBefore=st-workbench-is620n.service\n[Service]\nType=oneshot\nExecStart=/usr/bin/python3 -I /usr/local/libexec/st-workbench-cpu-isolation.py\nRemainAfterExit=yes\n[Install]\nWantedBy=multi-user.target\n'}
 originals={}
 for name in targets:
  dest=P(name)
  if dest.is_symlink():raise RuntimeError('Refuse symlink '+name)
  originals[name]=dest.read_text() if dest.exists() else None
 (backup/'originals.json').write_text(json.dumps(originals,indent=2));shutil.copy2('/boot/grub/grub.cfg',backup/'grub.cfg')
 rollback='''#!/usr/bin/python3
import json,pathlib,subprocess
root=pathlib.Path(__file__).resolve().parent
subprocess.run(['systemctl','disable','st-workbench-cpu-isolation.service'],check=False)
for name,text in json.loads((root/'originals.json').read_text()).items():
 p=pathlib.Path(name)
 if text is None:
  if p.exists():p.unlink()
 else:p.write_text(text)
pathlib.Path('/sys/devices/system/cpu/cpu6/online').write_text('1')
subprocess.run(['update-grub'],check=True)
subprocess.run(['systemctl','daemon-reload'],check=True)
print('Rollback configured; reboot required to clear boot isolation and manager affinity.')
'''
 (backup/'rollback.py').write_text(rollback)
 for name,text in targets.items():
  dest=P(name);dest.parent.mkdir(parents=True,exist_ok=True);dest.write_text(text);dest.chmod(0o755 if name.endswith('.py') else 0o644)
 run(['systemd-analyze','verify','/etc/systemd/system/st-workbench-cpu-isolation.service'])
 run(['update-grub']);generated=P('/boot/grub/grub.cfg').read_text()
 assert params in generated,'New kernel options absent from generated GRUB'
 run(['grub-script-check','/boot/grub/grub.cfg'])
 run(['systemctl','daemon-reload']);run(['systemctl','enable','st-workbench-cpu-isolation.service'])
 report.update(backup=str(backup),parameters=params,installed=True,rebootRequired=True)
 (backup/'installation.json').write_text(json.dumps(report,indent=2));(base/'installation-result.json').write_text(json.dumps(report,indent=2));print(json.dumps(report))
if __name__=='__main__':install()
