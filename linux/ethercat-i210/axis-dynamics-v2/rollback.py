#!/usr/bin/python3 -I
"""Verify or restore the original runtime backup plus its separate broker backup."""
import hashlib,json,os,pathlib,re,stat,subprocess,tempfile
ORIGINAL_BACKUPS=pathlib.Path('/var/backups/st-workbench-runtime')
BROKER_BACKUPS=pathlib.Path('/var/backups/st-workbench-axis-dynamics-v2')
BROKER=pathlib.Path('/usr/local/libexec/stworkbench-hardware-broker')
LEGACY_ROLLBACK=pathlib.Path('/usr/local/libexec/stworkbench-runtime-migration/rollback.py')
SAFE_ENV={'PATH':'/usr/sbin:/usr/bin:/sbin:/bin','LANG':'C.UTF-8'}


def trusted(path):
    for item in (path,*path.parents):
        info=item.lstat()
        if stat.S_ISLNK(info.st_mode) or info.st_uid!=0 or info.st_mode&0o022:
            raise ValueError('Path must remain exclusively root controlled')


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


def publish(path,data,mode):
    trusted(path.parent)
    if path.exists():trusted(path)
    fd,tmp=tempfile.mkstemp(prefix='.axis-dynamics-v2-',dir=path.parent)
    try:
        with os.fdopen(fd,'wb') as stream:
            os.fchmod(stream.fileno(),mode);stream.write(data);stream.flush();os.fsync(stream.fileno())
        os.replace(tmp,path)
        directory=os.open(path.parent,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW)
        try:os.fsync(directory)
        finally:os.close(directory)
    finally:
        if os.path.lexists(tmp):os.unlink(tmp)


def rollback_locked(backup,apply=False):
    original,metadata,data=verify_broker_backup(backup)
    trusted(LEGACY_ROLLBACK)
    subprocess.run(['/usr/bin/python3','-I',str(LEGACY_ROLLBACK),str(original),'--check'],check=True,timeout=30,env=SAFE_ENV,cwd='/')
    if not apply:return 'verified-no-files-restored'
    # The original rollback independently requires native and restore services
    # stopped. It restores its unchanged exact inventory before the broker.
    subprocess.run(['/usr/bin/python3','-I',str(LEGACY_ROLLBACK),str(original),'--apply'],check=True,timeout=90,env=SAFE_ENV,cwd='/')
    # Reverify after the other restore; never trust a stale supplemental payload.
    _,metadata,data=verify_broker_backup(backup)
    publish(BROKER,data,metadata['mode'])
    os.chown(BROKER,metadata['uid'],metadata['gid'])
    subprocess.run(['systemctl','try-restart','stworkbench-hardware.service'],check=True,timeout=20,env=SAFE_ENV,cwd='/')
    return 'restored-files-and-broker; native-and-PLC-not-started'


def main():
    import argparse,fcntl
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('backup')
    mode=parser.add_mutually_exclusive_group();mode.add_argument('--check',action='store_true');mode.add_argument('--apply',action='store_true')
    args=parser.parse_args()
    if os.geteuid()!=0:raise ValueError('Root required')
    lock=pathlib.Path('/run/stworkbench-ethercat-apply.lock');trusted(lock.parent)
    fd=os.open(lock,os.O_RDWR|os.O_CREAT|os.O_NOFOLLOW|os.O_NONBLOCK,0o600)
    try:
        info=os.fstat(fd)
        if not stat.S_ISREG(info.st_mode) or info.st_uid!=0 or stat.S_IMODE(info.st_mode)!=0o600:raise ValueError('Invalid hardware apply lock')
        fcntl.flock(fd,fcntl.LOCK_EX|fcntl.LOCK_NB)
        print(rollback_locked(args.backup,args.apply),flush=True)
    finally:os.close(fd)

if __name__=='__main__':
    try:main()
    except Exception as error:raise SystemExit('V2 rollback incomplete; keep native services stopped and inspect both backups: '+str(error))
