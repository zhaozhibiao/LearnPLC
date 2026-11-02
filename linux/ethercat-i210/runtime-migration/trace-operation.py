#!/usr/bin/python3 -I
"""Freeze/export or rearm only the fixed formal core's preallocated diagnostic buffer."""
import argparse,hashlib,importlib.machinery,importlib.util,json,os,pathlib,re,signal,sys,time
def main(mode):
    if os.geteuid()!=0:raise ValueError('Root required')
    path=pathlib.Path('/usr/local/libexec/stworkbench-ethercat-apply')
    for p in (path,*path.parents):
        st=p.lstat()
        if p.is_symlink() or st.st_uid!=0 or st.st_mode&0o022:raise ValueError('Untrusted root helper')
    loader=importlib.machinery.SourceFileLoader('trace_root_helper',str(path));spec=importlib.util.spec_from_loader(loader.name,loader);helper=importlib.util.module_from_spec(spec);loader.exec_module(helper)
    with helper.apply_lock():
        helper.preflight()
        pid=int(helper.command(['systemctl','show',helper.SERVICE,'--property=MainPID','--value']).stdout.strip())
        if pid<=0:raise ValueError('No running formal core')
        proc=pathlib.Path('/proc')/str(pid);installed=helper.parse_json(helper.read_regular(helper.INSTALLATION))
        if os.readlink(proc/'exe')!=installed['binary'] or hashlib.sha256((proc/'exe').read_bytes()).hexdigest()!=installed['binarySha256']:raise ValueError('Mapped core differs from installed release')
        arguments=(proc/'cmdline').read_bytes().decode().split('\0')
        if arguments.count('--axis-trace-csv')!=1:raise ValueError('Formal core has no active trace buffer')
        output=arguments[arguments.index('--axis-trace-csv')+1]
        if re.fullmatch(r'/var/lib/st-workbench-ethercat/traces/[0-9a-f]{64}-[0-9a-f]{32}\.csv',output) is None:raise ValueError('Trace output outside fixed namespace')
        directory=pathlib.Path(output).parent;helper.root_owned(directory)
        existing=set(directory.glob(pathlib.Path(output).name+'.capture-*.csv'))
        os.kill(pid,signal.SIGUSR1)
        deadline=time.monotonic()+15;exported=None
        while time.monotonic()<deadline:
            candidates=set(directory.glob(pathlib.Path(output).name+'.capture-*.csv'))-existing
            if len(candidates)>1:raise ValueError('Ambiguous trace export')
            if candidates:
                candidate=candidates.pop()
                opened=False
                for fd in (proc/'fd').iterdir():
                    try:opened|=os.readlink(fd)==str(candidate)
                    except FileNotFoundError:pass
                if not opened and candidate.stat().st_size>0:exported=candidate;break
            time.sleep(.02)
        if exported is None:raise ValueError('Trace export did not complete')
        helper.root_owned(exported)
        result=dict(action=mode,pid=pid,path=str(exported),sha256=hashlib.sha256(exported.read_bytes()).hexdigest(),size=exported.stat().st_size)
        if mode=='prepare':os.kill(pid,signal.SIGUSR2);time.sleep(.03);result['rearmed']=True
        print(json.dumps(result),flush=True)
if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('mode',choices=['prepare','capture','console']);args=p.parse_args()
    if args.mode=='console':
        if os.geteuid()!=0:raise SystemExit('Root required')
        print('Trace diagnostic console ready: prepare, capture, exit',flush=True)
        for line in sys.stdin:
            action=line.strip()
            if action=='exit':break
            if action not in ['prepare','capture']:raise ValueError('Only fixed trace actions allowed')
            main(action)
    else:main(args.mode)
