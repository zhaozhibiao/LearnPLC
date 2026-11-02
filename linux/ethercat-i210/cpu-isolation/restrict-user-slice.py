import json,pathlib,subprocess
P=pathlib.Path
base=P('/home/a/st-workbench-i210/cpu-isolation-20261030');record=json.loads((base/'installation-result.json').read_text());backup=P(record['backup']);f=P('/etc/systemd/system/user.slice.d/90-st-workbench-isolation.conf')
assert not f.is_symlink()
m=json.loads((backup/'originals.json').read_text())
if str(f) not in m:
 m[str(f)]=f.read_text() if f.exists() else None
 (backup/'originals.json').write_text(json.dumps(m,indent=2))
f.parent.mkdir(parents=True,exist_ok=True);f.write_text('[Slice]\nAllowedCPUs=0-1 3-5 7\n');f.chmod(0o644)
subprocess.run(['systemctl','daemon-reload'],check=True)
subprocess.run(['systemctl','set-property','--runtime','user.slice','AllowedCPUs=0-1,3-5,7'],check=True)
print(P('/sys/fs/cgroup/user.slice/cpuset.cpus.effective').read_text())
