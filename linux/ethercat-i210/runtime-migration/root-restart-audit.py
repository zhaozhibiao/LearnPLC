#!/usr/bin/python3 -I
"""Verify the mapped release and exercise the fixed restore entry without axis commands."""
import hashlib,importlib.machinery,importlib.util,json,os,pathlib,subprocess,sys,time
def load(path,name):
    for part in (path,*path.parents):
        info=part.lstat()
        if part.is_symlink() or info.st_uid!=0 or info.st_mode&0o022:raise ValueError('Untrusted root helper')
    loader=importlib.machinery.SourceFileLoader(name,str(path))
    spec=importlib.util.spec_from_loader(name,loader);module=importlib.util.module_from_spec(spec);loader.exec_module(module)
    return module
def process(helper):
    pid=int(helper.command(['systemctl','show',helper.SERVICE,'--property=MainPID','--value']).stdout.strip())
    if pid<=0:raise ValueError('No running core')
    proc=pathlib.Path('/proc')/str(pid)
    installed=helper.parse_json(helper.read_regular(helper.INSTALLATION))
    binary=os.readlink(proc/'exe')
    digest=hashlib.sha256((proc/'exe').read_bytes()).hexdigest()
    if binary!=installed['binary'] or digest!=installed['binarySha256']:raise ValueError('Mapped release differs from installation')
    descriptors=[]
    for entry in (proc/'fd').iterdir():
        try:
            if os.readlink(entry)=='/dev/cpu_dma_latency':descriptors.append(int(entry.name))
        except FileNotFoundError:pass
    if not descriptors:raise ValueError('Core does not own its CPU latency request')
    if os.sched_getscheduler(pid)!=os.SCHED_FIFO or os.sched_getparam(pid).sched_priority!=80 or os.sched_getaffinity(pid)!={2}:
        raise ValueError('Realtime scheduling differs')
    return dict(pid=pid,binary=binary,sha256=digest,cpuLatencyDescriptors=descriptors,cpu=2,priority=80)
def main():
    if os.geteuid()!=0 or sys.argv[1:] not in ([],['--restart']):raise ValueError('Root required; optional --restart only')
    helper=load(pathlib.Path('/usr/local/libexec/stworkbench-ethercat-apply'),'migration_root_helper')
    restorer=load(pathlib.Path('/usr/local/libexec/stworkbench-ethercat-restore'),'migration_restore')
    with helper.apply_lock():
        helper.preflight()
        before=process(helper)
        result=dict(before=before,restarted=False)
        if '--restart' in sys.argv:
            helper.command(['systemctl','stop',helper.SERVICE])
            helper.command(['systemctl','reset-failed',helper.SERVICE],check=False)
            helper.wait_for_core_release()
            result['restoreResult']=restorer.start_saved_core(helper)
            result['after']=process(helper)
            if result['after']['pid']==before['pid']:raise ValueError('Core process was not replaced')
            result['restarted']=True
        deadline=time.monotonic()+20
        while True:
            sample=helper.native_request(helper.CONTROLLER_SOCKET,'ReadSnapshotRequest',{'includeDiagnostics':False},'SnapshotResponse')
            helper.disabled_snapshot(sample,require_zero=True)
            if all(a.get('timingReady') is True and not a.get('timingFault') and a.get('state')=='Disabled' and a.get('fault') is None for a in sample['axes']):break
            if time.monotonic()>=deadline:raise ValueError('Restored motion timing not ready')
            time.sleep(.05)
        if helper.plc_status()!='STOPPED':raise ValueError('PLC state changed')
        result.update(plcState='STOPPED',axisState='Disabled',timingReady=True,axisCommandSequence=sample['lastAppliedCommandSequence'])
        print(json.dumps(result),flush=True)
if __name__=='__main__':main()
