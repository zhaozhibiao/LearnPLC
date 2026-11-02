#!/usr/bin/python3 -I
"""Move the boot-order reference from the replaceable core to its permanent restorer."""
import fcntl,hashlib,json,os,pathlib,stat,subprocess,tempfile
UNIT=pathlib.Path('/etc/systemd/system/st-workbench-cpu-isolation.service')
BACKUP=pathlib.Path('/var/backups/st-workbench-isolation-order-20261101')
def patched(data):
    old=b'Before=st-workbench-is620n.service'
    new=b'Before=stworkbench-ethercat-restore.service'
    if data.count(old)!=1 or new in data:raise ValueError('Unexpected isolation ordering; no change')
    return data.replace(old,new)
def main():
    if os.geteuid()!=0:raise ValueError('Root required')
    fd=os.open('/run/stworkbench-ethercat-apply.lock',os.O_RDWR|os.O_CREAT|os.O_NOFOLLOW,0o600)
    try:
        info=os.fstat(fd)
        if not stat.S_ISREG(info.st_mode) or info.st_uid!=0 or stat.S_IMODE(info.st_mode)!=0o600:raise ValueError('Invalid lock')
        fcntl.flock(fd,fcntl.LOCK_EX|fcntl.LOCK_NB)
        result=subprocess.run(['systemctl','show','st-workbench-is620n.service','--property=MainPID','--property=ActiveState'],capture_output=True,text=True,check=True)
        fields=dict(line.split('=',1) for line in result.stdout.splitlines())
        if fields!={'MainPID':'0','ActiveState':'inactive'}:raise ValueError('Core must already be stopped')
        for p in (UNIT,*UNIT.parents):
            st=p.lstat()
            if stat.S_ISLNK(st.st_mode) or st.st_uid!=0 or st.st_mode&0o022:raise ValueError('Untrusted unit path')
        before=UNIT.read_bytes();after=patched(before)
        BACKUP.mkdir(mode=0o700)
        for name,data in [('unit.original',before),('manifest.json',json.dumps(dict(path=str(UNIT),originalSha256=hashlib.sha256(before).hexdigest(),installedSha256=hashlib.sha256(after).hexdigest())).encode())]:
            out=os.open(BACKUP/name,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600)
            with os.fdopen(out,'wb') as stream:stream.write(data);stream.flush();os.fsync(stream.fileno())
        out,tmp=tempfile.mkstemp(prefix='.isolation-order-',dir=UNIT.parent)
        try:
            with os.fdopen(out,'wb') as stream:os.fchmod(stream.fileno(),0o644);stream.write(after);stream.flush();os.fsync(stream.fileno())
            os.replace(tmp,UNIT)
        finally:
            if os.path.lexists(tmp):os.unlink(tmp)
        subprocess.run(['systemctl','daemon-reload'],check=True,timeout=20)
        print(json.dumps(dict(status='boot-order-reference-repaired',backup=str(BACKUP),cpuIsolationUnchanged=True)))
    finally:os.close(fd)
if __name__=='__main__':main()
