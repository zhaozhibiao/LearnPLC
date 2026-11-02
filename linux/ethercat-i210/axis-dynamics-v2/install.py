#!/usr/bin/python3 -I
"""Install the reviewed release and reload its broker; never start/stop PLC or motion."""
import hashlib,json,os,pathlib,re,stat,subprocess,tempfile
SOURCE=pathlib.Path('/home/a/st-workbench-i210/axis-dynamics-v2-release-20261101')
SAFE_ENV={'PATH':'/usr/sbin:/usr/bin:/sbin:/bin','LANG':'C.UTF-8'}
# Reviewed isolated Linux Release binary; never derived from staged input.
BINARY_SHA256 = 'f0de2ddd4c943231099b322041471c8748380175229a7582982b08680511a9b4'
EXPECTED={
 'ecmotion-rt':BINARY_SHA256,
 'apply-runtime-hardware.py':'cd4e2945c39c8d3a56b9f95e28b9a87e7543450925d3796da69a7010cc9a3350',
 'restore-runtime.py':'ab8f9d7cf483b8ff4c4ff8d6b1f8301458b826731c919d2a5ac9b48cd7f8d217',
 'hardware-broker.py':'84bf1a6fb7b66e555a61918551df6f388e58ecab7076bfcb766b1b491c9cc274',
 'rollback.py':'300252058ddac743b009873ede30617a2781c3409163b7ae19ccd6f33fe59cb2'}
REQUIRED_CAPABILITIES=frozenset({'workbench_motion_migration_v1','workbench_axis_trace_v1',
 'workbench_axis_move_profile_v2','workbench_axis_velocity_profile_v2'})

ORIGINAL_BACKUPS=pathlib.Path('/var/backups/st-workbench-runtime')
BROKER_BACKUPS=pathlib.Path('/var/backups/st-workbench-axis-dynamics-v2')
BROKER=pathlib.Path('/usr/local/libexec/stworkbench-hardware-broker')
ROLLBACK=pathlib.Path('/usr/local/libexec/stworkbench-axis-dynamics-v2-rollback')


def check_release_pins(expected):
    import re
    if (set(expected) != {'ecmotion-rt','apply-runtime-hardware.py','restore-runtime.py','hardware-broker.py','rollback.py'}
            or any(type(value) is not str or re.fullmatch('[0-9a-f]{64}',value) is None for value in expected.values())):
        raise ValueError('Reviewed release SHA256 constants must be pinned before installation')


def check_capabilities(capability):
    if not isinstance(capability,dict) or set(capability) != {'runtimeCapabilities'}:
        raise ValueError('Unexpected release capability')
    actual=capability['runtimeCapabilities']
    if (not isinstance(actual,list) or len(actual) != 4 or any(type(value) is not str for value in actual)
            or set(actual) != REQUIRED_CAPABILITIES):
        raise ValueError('Unexpected release capability')


def capture(directory, expected):
    check_release_pins(expected)
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
    fd,tmp=tempfile.mkstemp(prefix='.axis-dynamics-v2-',dir=path.parent)
    try:
        with os.fdopen(fd,'wb') as out:
            os.fchmod(out.fileno(),mode);out.write(data);out.flush();os.fsync(out.fileno())
        os.replace(tmp,path)
        directory=os.open(path.parent,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW)
        try:os.fsync(directory)
        finally:os.close(directory)
    finally:
        if os.path.lexists(tmp):os.unlink(tmp)

def read_trusted(path):
    trusted(path)
    fd=os.open(path,os.O_RDONLY|getattr(os,'O_NOFOLLOW',0)|getattr(os,'O_NONBLOCK',0))
    with os.fdopen(fd,'rb') as stream:
        info=os.fstat(stream.fileno())
        if not stat.S_ISREG(info.st_mode) or not 0<=info.st_size<=128*1024*1024:
            raise ValueError('Expected bounded regular backup file')
        data=stream.read(128*1024*1024+1)
    if len(data)>128*1024*1024:raise ValueError('Backup file exceeds capacity')
    return data,info


def original_backup(value):
    path=pathlib.Path(value).absolute()
    if (path.parent!=ORIGINAL_BACKUPS.absolute()
            or re.fullmatch(r'[0-9]{8}T[0-9]{6}\.[0-9]{6}Z-[0-9a-f]{32}',path.name) is None):
        raise ValueError('Original backup must be a direct fixed root backup version')
    trusted(path)
    return path


def verify_broker_backup(value):
    original=original_backup(value)
    directory=BROKER_BACKUPS/original.name
    trusted(directory)
    if {entry.name for entry in directory.iterdir()}!={'manifest.json','broker.py'}:
        raise ValueError('Unexpected supplementary broker backup content')
    def pairs(items):
        result={}
        for key,value in items:
            if key in result:raise ValueError('Duplicate broker backup key')
            result[key]=value
        return result
    metadata=json.loads(read_trusted(directory/'manifest.json')[0],object_pairs_hook=pairs)
    if (not isinstance(metadata,dict) or set(metadata)!={'schemaVersion','sourceBackup','path','sha256','size','mode','uid','gid'}
            or type(metadata['schemaVersion']) is not int or metadata['schemaVersion']!=1
            or metadata['sourceBackup']!=str(original) or metadata['path']!=str(BROKER)):
        raise ValueError('Broker backup binding differs')
    data,_=read_trusted(directory/'broker.py')
    if (type(metadata['size']) is not int or metadata['size']!=len(data)
            or type(metadata['sha256']) is not str or hashlib.sha256(data).hexdigest()!=metadata['sha256']
            or any(type(metadata[k]) is not int for k in ('mode','uid','gid'))
            or metadata['uid']!=0 or metadata['gid']<0 or not 0<=metadata['mode']<=0o777
            or metadata['mode']&0o022):
        raise ValueError('Broker backup checksum or permissions differ')
    return original,metadata,data



def create_broker_backup(value):
    original=original_backup(value)
    data,info=read_trusted(BROKER)
    trusted(BROKER_BACKUPS.parent)
    BROKER_BACKUPS.mkdir(mode=0o700,exist_ok=True)
    trusted(BROKER_BACKUPS)
    directory=BROKER_BACKUPS/original.name
    directory.mkdir(mode=0o700)
    trusted(directory)
    metadata=dict(schemaVersion=1,sourceBackup=str(original),path=str(BROKER),
        sha256=hashlib.sha256(data).hexdigest(),size=len(data),mode=stat.S_IMODE(info.st_mode),uid=info.st_uid,gid=info.st_gid)
    for name,payload in [('broker.py',data),('manifest.json',json.dumps(metadata,sort_keys=True).encode())]:
        fd=os.open(directory/name,os.O_WRONLY|os.O_CREAT|os.O_EXCL|getattr(os,'O_NOFOLLOW',0),0o600)
        with os.fdopen(fd,'wb') as stream:stream.write(payload);stream.flush();os.fsync(stream.fileno())
    for path in ((directory,BROKER_BACKUPS) if os.name=='posix' else ()):
        fd=os.open(path,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW)
        try:os.fsync(fd)
        finally:os.close(fd)
    verify_broker_backup(original)
    return str(directory)


def install_locked():
    if os.geteuid()!=0:raise ValueError('Root required')
    data=capture(SOURCE,EXPECTED)
    tools=pathlib.Path('/usr/local/libexec/stworkbench-runtime-migration')
    for name in ['preflight.py','rollback.py']:trusted(tools/name)
    # A fresh disabled/stable-position backup is mandatory before publication.
    completed=subprocess.run(['/usr/bin/python3','-I',str(tools/'preflight.py')],capture_output=True,text=True,check=True,timeout=90,env=SAFE_ENV,cwd='/')
    backup=json.loads(completed.stdout)['backup']
    subprocess.run(['/usr/bin/python3','-I',str(tools/'rollback.py'),backup,'--check'],check=True,timeout=30,env=SAFE_ENV,cwd='/')
    broker_backup=create_broker_backup(backup)
    binary=pathlib.Path('/opt/st-workbench-ethercat/ecmotion-rt-'+EXPECTED['ecmotion-rt'][:12])
    publish(binary,data['ecmotion-rt'],0o755)
    capability=json.loads(subprocess.run([str(binary),'--runtime-capabilities'],capture_output=True,text=True,check=True,timeout=10,env=SAFE_ENV,cwd='/').stdout)
    check_capabilities(capability)
    # Recovery tooling is available before any active helper is replaced.
    publish(ROLLBACK,data['rollback.py'],0o755)
    publish(pathlib.Path('/usr/local/libexec/stworkbench-ethercat-apply'),data['apply-runtime-hardware.py'],0o755)
    publish(pathlib.Path('/usr/local/libexec/stworkbench-ethercat-restore'),data['restore-runtime.py'],0o755)
    publish(BROKER,data['hardware-broker.py'],0o755)
    installation=dict(binary=str(binary),binarySha256=EXPECTED['ecmotion-rt'],allowInitialStart=True)
    publish(pathlib.Path('/etc/st-workbench-ethercat/installation.json'),json.dumps(installation).encode(),0o600)
    # The broker imports the root helper once at startup. Atomic publication
    # alone would leave a running broker executing the previous helper code.
    subprocess.run(['systemctl','try-restart','stworkbench-hardware.service'],capture_output=True,text=True,check=True,timeout=20,env=SAFE_ENV,cwd='/')
    print(json.dumps(dict(status='installed-not-started',backup=backup,brokerBackup=broker_backup,rollbackHelper=str(ROLLBACK),**installation)))

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
