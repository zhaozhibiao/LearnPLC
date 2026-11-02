"""Formal MC_* acceptance via authenticated online BOOL writes, never servo targets."""
import argparse,asyncio,importlib.util,json,math,pathlib,ssl,struct,subprocess,sys,time,uuid
import aiohttp
sys.path.insert(0,'/home/a/st-workbench-deployment')
import coordinator,ethercat_hardware
ROOT=pathlib.Path('/home/a/st-workbench-i210/runtime-migration-release-20261101')
BUILD='03FF0AEB1834D5CA5D3A4372E4A1BF383EC574FDB49115155227B11A565448A3'
FORMATS={'BOOL':'?','SINT':'b','USINT':'B','INT':'h','UINT':'H','DINT':'i','UDINT':'I','LINT':'q','ULINT':'Q','REAL':'f','LREAL':'d'}
class Debug:
    def __init__(self,ws):self.ws=ws
    async def packet(self):
        while True:
            msg=await self.ws.receive(timeout=5)
            if msg.type!=aiohttp.WSMsgType.TEXT:raise ValueError('Online socket closed')
            if msg.data=='2':await self.ws.send_str('3');continue
            return msg.data
    async def request(self,pdu):
        await self.ws.send_str('42/api/debug,'+json.dumps(['debug_command',{'command':pdu.hex(' ')}]))
        for _ in range(8):
            text=await self.packet()
            if text.startswith('42/api/debug,'):
                event,body=json.loads(text[len('42/api/debug,'):])
                if event=='connected':continue
                if event!='debug_response' or body.get('success') is not True:raise ValueError('Online request rejected')
                return bytes.fromhex(body['data'])
        raise ValueError('Online response missing')
    async def read(self,leaves):
        pdu=b'\x44'+struct.pack('>H',len(leaves))+b''.join(bytes([v['arrayIdx']])+struct.pack('>H',v['elemIdx']) for v in leaves)
        data=await self.request(pdu);size=sum(v['size'] for v in leaves)
        if len(data)!=10+size or data[:2]!=b'\x44\x7e' or struct.unpack('>H',data[2:4])[0]!=len(leaves)-1 or struct.unpack('>H',data[8:10])[0]!=size:raise ValueError('Online read does not match compiled debug map')
        result={};offset=10
        for v in leaves:
            fmt='<'+FORMATS[v['type']]
            if struct.calcsize(fmt)!=v['size']:raise ValueError('Unsupported debug type size')
            result[v['path'].removeprefix('INSTANCE0.')]=struct.unpack_from(fmt,data,offset)[0];offset+=v['size']
        result['_tick']=struct.unpack('>I',data[4:8])[0]
        return result
    async def write(self,md5,leaf,value):
        if leaf['type']!='BOOL' or leaf['size']!=1:raise ValueError('Only matched BOOL single writes allowed')
        pdu=b'\x46\x01'+md5.encode('ascii')+bytes([leaf['arrayIdx']])+struct.pack('>H',leaf['elemIdx'])+bytes([int(value)])
        if await self.request(pdu)!=b'\x46\x7e':raise ValueError('Online BOOL write rejected')

async def main(run,rpm=None):
    global BUILD
    profile=json.loads((ROOT/f'acceptance-profile-{rpm}.json').read_text()) if rpm else dict(distance=.01,speed=.1,rpm=6)
    if rpm:BUILD=profile['build']
    distance,speed=profile['distance'],profile['speed']
    cfg=json.loads(pathlib.Path('/home/a/st-workbench-deployment/config.json').read_text());client=coordinator.Coordinator(cfg)
    active=client.active();client.verify_active(active,BUILD)
    if active['build_hash']!=BUILD:raise ValueError('Acceptance program is not active')
    generated=client.version(BUILD)/'core/generated';debug=json.loads((generated/'debug-map.json').read_text())
    machine=json.loads((generated/'machine-config.json').read_text());axis_config=machine['axes'][0]
    position_tolerance=axis_config['motionCompletion']['positionTolerance'];counts_per_unit=axis_config['followingErrorRawPerUnit']
    spec=importlib.util.spec_from_file_location('acceptance_results',ROOT/'acceptance-results.py');assessment=importlib.util.module_from_spec(spec);spec.loader.exec_module(assessment)
    inputs={'POWERENABLE','HOMEEXECUTE','CYCLEEXECUTE','STOPEXECUTE','RESETEXECUTE','FORWARDTURNS','REVERSETURNS','SPEED','STEP','STARTPOSITION','ERRORID','DONE','CANCELLED'}
    prefixes={'POWER','HOME','RESET','STATUS','POSITION','FORWARDMOVE','REVERSEMOVE','STOP'}
    fields={'STATUS','BUSY','DONE','ERROR','ERRORID','COMMANDABORTED','VALID','HOMED','STANDSTILL','POSITION','VELOCITY','DISABLED','ERRORSTOP','HOMING','STOPPING'}
    def selected(v):
        name=v['path'].removeprefix('INSTANCE0.');parts=name.split('.')
        return name in inputs or len(parts)==2 and parts[0] in prefixes and parts[1] in fields
    leaves=[v for v in debug['leaves'] if v['type'] in FORMATS and selected(v)]
    byname={v['path'].removeprefix('INSTANCE0.'):v for v in leaves}
    if len(leaves)>1024 or sum(v['size'] for v in leaves)>4086:raise ValueError('Split debug batch before testing')
    result=dict(build=BUILD,md5=debug['md5'],rpm=profile['rpm'],distance=distance,status='STARTING',events=[],samples=[],writes=[])
    started=False;dbg=None;last=None
    def native():return ethercat_hardware.native_request('/run/st-workbench-is620n/controller.sock','ReadSnapshotRequest',{'includeDiagnostics':False},'SnapshotResponse')
    def transition(action):return client.execute(dict(action=action,request_id='st-acceptance-'+uuid.uuid4().hex,expected_active_build_hash=BUILD))
    async def sample():
        nonlocal last
        values=await dbg.read(leaves);snapshot=native();axis=snapshot['axes'][0]
        if not snapshot['runtimeReady'] or not snapshot['ioReady'] or not axis['timingReady'] or axis['timingFault'] or axis['fault']:raise ValueError('Native readiness/protection failed: '+str(axis))
        if abs(axis['actualPosition'])>distance+.03:raise ValueError('Acceptance travel envelope exceeded')
        if any(isinstance(v,float) and not math.isfinite(v) for v in values.values()):raise ValueError('Nonfinite online value')
        last=dict(time=time.monotonic(),values=values,axis=axis)
        result['samples'].append(last)
        return values
    async def wait(label,predicate,timeout=8):
        deadline=time.monotonic()+timeout
        while time.monotonic()<deadline:
            values=await sample()
            errors={k:v for k,v in values.items() if k.endswith('.ERROR') and v}
            if errors or values['ERRORID'] or values['STEP']==900:raise ValueError(label+' PLC error: '+json.dumps({k:v for k,v in values.items() if k.endswith('ERRORID') or k=='STEP'}))
            if predicate(values):result['events'].append(dict(event=label,time=time.monotonic(),tick=values['_tick']));return values
            await asyncio.sleep(.005)
        raise ValueError(label+' timed out; last='+json.dumps(last['values'] if last else None))
    async def write(name,value):
        await dbg.write(debug['md5'],byname[name],value)
        result['writes'].append(dict(name=name,value=value,time=time.monotonic()))
    with client.connection() as connection:
        if connection.status()!='STOPPED':raise ValueError('PLC must initially be stopped')
        first=native()
        if first['axes'][0]['state']!='Disabled' or first['axes'][0]['fault'] or not first['axes'][0]['timingReady']:raise ValueError('Axis must initially be disabled and ready')
        context=ssl.create_default_context(cafile=cfg['ca_file'])
        async with aiohttp.ClientSession() as session:
            async with session.ws_connect('wss://localhost:8443/socket.io/?EIO=4&transport=websocket',ssl=context,max_msg_size=1048576) as ws:
                dbg=Debug(ws)
                if not (await dbg.packet()).startswith('0'):raise ValueError('EngineIO opening missing')
                await ws.send_str('40/api/debug,'+json.dumps(dict(token=connection.token)))
                while True:
                    packet=await dbg.packet()
                    if packet.startswith('40/api/debug,'):break
                    if packet.startswith('44'):raise ValueError('Online authentication rejected')
                if await dbg.request(b'\x46\x00')!=b'\x46\x7e\x01':raise ValueError('Single-write capability missing')
                if not run:
                    result['status']='PROBE-PASS';print(json.dumps(dict(status=result['status'],build=BUILD)));return
                pid_before=subprocess.check_output(['pgrep','-u','1000','-x','plc_main'],text=True).strip()
                try:
                    started=True;transition('start')
                    identity=await dbg.request(bytes.fromhex('45 de ad 00 00'))
                    if len(identity)!=36 or identity[:2]!=b'\x45\x7e' or identity[2:34].decode()!=debug['md5'] or identity[34:]!=b'\xad\xde':raise ValueError('Online active program identity/endianness differs')
                    initial=await wait('scan-running',lambda v:v['_tick']>2)
                    if any(initial[n] for n in ['POWERENABLE','HOMEEXECUTE','CYCLEEXECUTE','STOPEXECUTE','RESETEXECUTE']):raise ValueError('Unsafe nonzero initial command input')
                    if initial['FORWARDTURNS']!=distance or initial['REVERSETURNS']!=distance or initial['SPEED']!=speed:raise ValueError('Unexpected acceptance defaults')
                    await write('RESETEXECUTE',True);await wait('reset-done',lambda v:v['RESET.DONE']);await write('RESETEXECUTE',False)
                    await write('POWERENABLE',True);await wait('power-enabled',lambda v:v['POWER.STATUS'] and v['STATUS.STANDSTILL'])
                    await write('HOMEEXECUTE',True);await wait('home-done',lambda v:v['HOME.DONE'] and v['STATUS.HOMED']);await write('HOMEEXECUTE',False)
                    await write('CYCLEEXECUTE',True)
                    cycle_start=len(result['samples']);await wait('roundtrip-done',lambda v:v['DONE'] and v['STEP']==100,120 if rpm else 12)
                    cycle=result['samples'][cycle_start:];positions=[s['axis']['actualPosition'] for s in cycle]
                    result['roundtrip']=assessment.roundtrip(cycle,distance,position_tolerance,counts_per_unit)
                    await write('CYCLEEXECUTE',False);await wait('sequence-reset',lambda v:v['STEP']==0)
                    if not rpm:
                        await write('CYCLEEXECUTE',True);await wait('second-move-busy',lambda v:v['FORWARDMOVE.BUSY'])
                        await write('STOPEXECUTE',True);await wait('stop-cancelled',lambda v:v['STEP']==100 and v['CANCELLED'])
                        await write('CYCLEEXECUTE',False);await write('STOPEXECUTE',False);await wait('stop-released',lambda v:v['STEP']==0)
                    await write('POWERENABLE',False);await wait('power-disabled',lambda v:not v['POWER.STATUS'] and last['axis']['state']=='Disabled')
                    result['status']='PASS'
                except Exception as error:
                    result['status']='FAIL';result['error']=str(error)
                    # Identity-bound ordinary writes, with normal STOP as fallback.
                    for name,value in [('STOPEXECUTE',True),('CYCLEEXECUTE',False),('HOMEEXECUTE',False),('POWERENABLE',False)]:
                        try:await write(name,value)
                        except Exception:pass
                    await asyncio.sleep(.2)
                    for name in ['STOPEXECUTE','RESETEXECUTE']:
                        try:await write(name,False)
                        except Exception:pass
                finally:
                    if started:
                        transition('stop')
                        await asyncio.sleep(.6)
                        final=native();result['finalSnapshot']=final
                        if final['axes'][0]['state']!='Disabled':result['status']='FAIL';result['cleanupError']='Axis not disabled after PLC STOP'
                        result['plcState']=connection.status()
                        result['plcProcessPreserved']=subprocess.check_output(['pgrep','-u','1000','-x','plc_main'],text=True).strip()==pid_before
                    path=ROOT/('st-acceptance-'+(str(rpm)+'rpm-' if rpm else '')+time.strftime('%Y%m%d-%H%M%S')+'.json');path.write_text(json.dumps(result,indent=2));path.chmod(0o600)
                    summary={k:v for k,v in result.items() if k not in ['samples','finalSnapshot']};summary['sampleCount']=len(result['samples']);summary['report']=str(path)
                    print(json.dumps(summary),flush=True)
    if result['status']!='PASS':raise SystemExit(1)
if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--run',action='store_true');p.add_argument('--rpm',type=int,choices=[300,1000,3000]);args=p.parse_args();asyncio.run(main(args.run,args.rpm))
