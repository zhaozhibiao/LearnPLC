"""Install reviewed user components only while all Runtime services are stopped."""
import hashlib,json,os,pathlib,subprocess,tempfile
HOME=pathlib.Path('/home/a')
SOURCE=HOME/'st-workbench-i210/runtime-migration-release-20261101'
RUNTIME=HOME/'.local/share/st-workbench/runtime-v4.3.0/source'
UNITS=['stworkbench-openplc.service','stworkbench-is620n-axis.service','stworkbench-engineering.service']
def stopped():
    for unit in UNITS:
        response=subprocess.run(['systemctl','--user','show',unit,'--property=MainPID','--property=ActiveState'],capture_output=True,text=True,check=True,timeout=10)
        fields=dict(line.split('=',1) for line in response.stdout.splitlines())
        if fields.get('MainPID')!='0' or fields.get('ActiveState') not in ['inactive','failed']:raise ValueError('User services must be stopped')
def main():
    if os.getuid()!=1000 or pathlib.Path.home()!=HOME:raise ValueError('Run as user a')
    stopped()
    manifest=json.loads((SOURCE/'user-release.json').read_text())
    destinations={
        'plc_main':RUNTIME/'build/plc_main',
        'restapi.py':RUNTIME/'webserver/restapi.py',
        'workbench_capabilities.py':RUNTIME/'webserver/workbench_capabilities.py',
        'coordinator.py':HOME/'st-workbench-deployment/coordinator.py',
        'ethercat_hardware.py':HOME/'st-workbench-deployment/ethercat_hardware.py'}
    if set(manifest)!=set(destinations):raise ValueError('Unexpected user release inventory')
    captured={}
    for name in destinations:
        path=SOURCE/name
        if path.is_symlink():raise ValueError('Symbolic source')
        data=path.read_bytes()
        if hashlib.sha256(data).hexdigest()!=manifest[name]:raise ValueError('User release checksum mismatch')
        captured[name]=data
    staged=[]
    try:
        for name,path in destinations.items():
            if any(p.is_symlink() for p in (path,*path.parents)):raise ValueError('Symbolic destination')
            fd,tmp=tempfile.mkstemp(prefix='.migration-',dir=path.parent)
            staged.append((tmp,path))
            with os.fdopen(fd,'wb') as stream:
                os.fchmod(stream.fileno(),0o755 if name=='plc_main' else 0o600)
                stream.write(captured[name]);stream.flush();os.fsync(stream.fileno())
        stopped()
        for tmp,path in staged:os.replace(tmp,path)
    finally:
        for tmp,_ in staged:
            if os.path.lexists(tmp):os.unlink(tmp)
    print(json.dumps(dict(status='user-release-installed-services-stopped',hashes=manifest)))
if __name__=='__main__':main()
