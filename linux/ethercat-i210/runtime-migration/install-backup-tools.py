import hashlib,json,os,pathlib,stat,subprocess,sys
assert os.geteuid()==0
src=pathlib.Path('/home/a/st-workbench-i210/runtime-migration-tools')
dst=pathlib.Path('/usr/local/libexec/stworkbench-runtime-migration')
expected={'preflight.py': 'a1660f595e6352396546b4a968a37e1793a7122078b376c728b4a20961da2494', 'rollback.py': '886879fed50c24ac5f451f613f50ab244bdfca184df35a1253505e6c1df7e32e', 'user-root-snapshot.py': 'b7e30d5eefe902af15ed416b9fd9c24a3f874fbbd26d35ae5128364fd91af15d', 'user-rollback.py': '2e60dd36235ab84c56e41229433bc560aa8bbd90473abbe2fdacfdee461d55f6'}
verified={}
for n,h in expected.items():
 p=src/n
 assert not p.is_symlink()
 data=p.read_bytes();assert hashlib.sha256(data).hexdigest()==h
 verified[n]=data
for p in [dst,*dst.parents]:
 if p.exists():
  st=p.lstat();assert not stat.S_ISLNK(st.st_mode) and st.st_uid==0 and not(st.st_mode&0o022)
dst.mkdir(mode=0o700,exist_ok=True)
for n in expected:
 p=dst/n
 assert not p.is_symlink()
 temporary=dst/(n+'.staged')
 fd=os.open(temporary,os.O_WRONLY|os.O_CREAT|os.O_EXCL|getattr(os,'O_NOFOLLOW',0),0o600)
 with os.fdopen(fd,'wb') as out:out.write(verified[n]);out.flush();os.fsync(out.fileno())
 os.replace(temporary,p)
r=subprocess.run(['/usr/bin/python3','-I',str(dst/'preflight.py')],check=True,capture_output=True,text=True)
print(r.stdout,flush=True);backup=json.loads(r.stdout)['backup']
r=subprocess.run(['/usr/bin/python3','-I',str(dst/'rollback.py'),backup],check=True,capture_output=True,text=True)
print(r.stdout,flush=True)

r=subprocess.run(['/usr/bin/python3','-I',str(dst/'user-root-snapshot.py'),'/home/a/.local/share/st-workbench/runtime-migration-backups/20261101T103616Z-c7cfdd37ec2a4570b6b153214fc1d75d','3e261192071ecec9b17a01b0f28e94e3f7d494d33045985800fb6d42b5f21484'],check=True,capture_output=True,text=True)
print(r.stdout,flush=True)
archive=json.loads(r.stdout)['archive']
r=subprocess.run(['/usr/bin/python3','-I',str(dst/'user-rollback.py'),archive],check=True,capture_output=True,text=True)
print(r.stdout,flush=True)
