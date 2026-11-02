#!/usr/bin/python3 -I
"""Install the reviewed release and reload its broker; never start/stop PLC or motion."""
import hashlib,json,os,pathlib,stat,subprocess,tempfile
SOURCE=pathlib.Path('/home/a/st-workbench-i210/runtime-migration-release-20261101')
EXPECTED={
 'ecmotion-rt':'fc24c80c0efdaccc46f7c676bc052a4f6a963b417b637e50d1e9d752f92b6925',
 'apply-runtime-hardware.py':'eceed784bf1b96a3727bcb2754487fb6d4883d7f26e723acaa611e307a7a6a46',
 'restore-runtime.py':'313611c276ecc7b6b581625c36231f3d348148d7f91c541d2303c30ea6a529cc'}

def capture(directory, expected):
    result={}
    for name,digest in expected.items():
        path=directory/name
        if path.is_symlink():raise ValueError('Symbolic release input')
        fd=os.open(path,os.O_RDONLY|getattr(os,'O_NOFOLLOW',0)|getattr(os,'O_NONBLOCK',0))
        with os.fdopen(fd,'rb') as stream:
            info=os.fstat(stream.fileno())
            if not stat.S_ISREG(info.st_mode) or info.st_size>128*1024*1024:raise ValueError('Invalid release input')
            data=stream.read(128*1024*1024+1)
        if hashlib.sha256(data).hexdigest()!=digest:raise ValueError('Release checksum differs: '+name)
        result[name]=data
    return result

def trusted(path):
    for p in (path,*path.parents):
        info=p.lstat()
        if stat.S_ISLNK(info.st_mode) or info.st_uid!=0 or info.st_mode&0o022:raise ValueError('Destination is not root controlled')

def publish(path,data,mode):
    trusted(path.parent)
    if path.exists():trusted(path)
    fd,tmp=tempfile.mkstemp(prefix='.migration-',dir=path.parent)
    try:
        with os.fdopen(fd,'wb') as out:
            os.fchmod(out.fileno(),mode);out.write(data);out.flush();os.fsync(out.fileno())
        os.replace(tmp,path)
        directory=os.open(path.parent,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW)
        try:os.fsync(directory)
        finally:os.close(directory)
    finally:
        if os.path.lexists(tmp):os.unlink(tmp)

def install_locked():
    if os.geteuid()!=0:raise ValueError('Root required')
    data=capture(SOURCE,EXPECTED)
    tools=pathlib.Path('/usr/local/libexec/stworkbench-runtime-migration')
    for name in ['preflight.py','rollback.py']:trusted(tools/name)
    # A fresh disabled/stable-position backup is mandatory before publication.
    completed=subprocess.run(['/usr/bin/python3','-I',str(tools/'preflight.py')],capture_output=True,text=True,check=True,timeout=90)
    backup=json.loads(completed.stdout)['backup']
    subprocess.run(['/usr/bin/python3','-I',str(tools/'rollback.py'),backup,'--check'],check=True,timeout=30)
    binary=pathlib.Path('/opt/st-workbench-ethercat/ecmotion-rt-'+EXPECTED['ecmotion-rt'][:12])
    publish(binary,data['ecmotion-rt'],0o755)
    capability=json.loads(subprocess.run([str(binary),'--runtime-capabilities'],capture_output=True,text=True,check=True,timeout=10).stdout)
    if capability!={'runtimeCapabilities':['workbench_motion_migration_v1','workbench_axis_trace_v1']}:raise ValueError('Unexpected release capability')
    publish(pathlib.Path('/usr/local/libexec/stworkbench-ethercat-apply'),data['apply-runtime-hardware.py'],0o755)
    publish(pathlib.Path('/usr/local/libexec/stworkbench-ethercat-restore'),data['restore-runtime.py'],0o755)
    installation=dict(binary=str(binary),binarySha256=EXPECTED['ecmotion-rt'],allowInitialStart=True)
    publish(pathlib.Path('/etc/st-workbench-ethercat/installation.json'),json.dumps(installation).encode(),0o600)
    # The broker imports the root helper once at startup. Atomic publication
    # alone would leave a running broker executing the previous helper code.
    subprocess.run(['systemctl','try-restart','stworkbench-hardware.service'],capture_output=True,text=True,check=True,timeout=20)
    print(json.dumps(dict(status='installed-not-started',backup=backup,**installation)))

def main():
    import fcntl
    if os.geteuid()!=0:raise ValueError('Root required')
    lock=pathlib.Path('/run/stworkbench-ethercat-apply.lock')
    trusted(lock.parent)
    fd=os.open(lock,os.O_RDWR|os.O_CREAT|os.O_NOFOLLOW|os.O_NONBLOCK,0o600)
    try:
        info=os.fstat(fd)
        if not stat.S_ISREG(info.st_mode) or info.st_uid!=0 or stat.S_IMODE(info.st_mode)!=0o600:raise ValueError('Invalid hardware apply lock')
        fcntl.flock(fd,fcntl.LOCK_EX|fcntl.LOCK_NB)
        install_locked()
    finally:os.close(fd)

if __name__=='__main__':
    try:main()
    except Exception as error:raise SystemExit('Installation incomplete; inspect backup before service changes: '+str(error))
