#!/usr/bin/env python3
"""MIT licensed, bounded client-only PLC communication worker (Python 3.11+)."""
import argparse
import dataclasses
import errno
import hashlib
import ipaddress
import json
import os
import pathlib
import select
import selectors
import socket
import stat
import struct
import sys
import threading
import time

try:
    import termios
    import fcntl
except ImportError:
    termios = fcntl = None

INTERFACE = 'workbench-communication-v1'
HEADER = struct.Struct('<QQ')
COMMAND = struct.Struct('<9Q512s')
FEEDBACK = struct.Struct('<7Q512s')
INPUT_SIZE = HEADER.size + 16 * COMMAND.size
OUTPUT_SIZE = 16 * FEEDBACK.size
CONFIG_KEYS = {'Name','Protocol','Host','Port','Device','BaudRate','DataBits','Parity','StopBits',
    'Physical','Direction','BeforeSendMicroseconds','AfterSendMicroseconds',
    'TimeoutMilliseconds','Framing','DelimiterHex','FixedLength','IdleMilliseconds'}


class CommunicationError(Exception):
    def __init__(self, code, message, unknown=False):
        super().__init__(message)
        self.code, self.unknown = code, unknown


def invalid(message):
    raise CommunicationError(6007, message)


@dataclasses.dataclass(frozen=True)
class Command:
    request: int
    action: int
    function: int
    unit: int
    address: int
    quantity: int
    read_address: int
    read_quantity: int
    length: int
    data: bytes


def crc16(data):
    value = 0xffff
    for byte in data:
        value ^= byte
        for _ in range(8):
            value = (value >> 1) ^ (0xa001 if value & 1 else 0)
    return value


def encode_modbus(c):
    if c.length > 512 or c.length > len(c.data): invalid('Payload length')
    f, q, a, data = c.function, c.quantity, c.address, c.data[:c.length]
    if not 0 <= a <= 65535: invalid('Address')
    if f in (1,2,3,4):
        maximum = 2000 if f in (1,2) else 125
        if not 1 <= q <= maximum or a+q > 65536 or data: invalid('Read range')
        return struct.pack('>BHH', f,a,q)
    if f in (5,6):
        if q != 1 or len(data) != 2 or (f == 5 and data not in (b'\xff\x00',b'\x00\x00')): invalid('Single write')
        return struct.pack('>BH',f,a)+data
    if f in (15,16,23):
        maximum = 1968 if f == 15 else (121 if f == 23 else 123)
        size = (q+7)//8 if f == 15 else q*2
        if not 1 <= q <= maximum or a+q > 65536 or len(data) != size: invalid('Write range')
        if f == 15 and q%8 and data[-1] >> (q%8): invalid('Unused coil bits')
        if f == 23:
            if not 0 <= c.read_address <= 65535 or not 1 <= c.read_quantity <= 125 or c.read_address+c.read_quantity > 65536: invalid('Read/write range')
            return struct.pack('>BHHHHB',f,c.read_address,c.read_quantity,a,q,len(data))+data
        return struct.pack('>BHHB',f,a,q,len(data))+data
    if f == 22:
        if q != 1 or len(data) != 4: invalid('Mask write')
        return struct.pack('>BH',f,a)+data
    invalid('Unsupported function')


def decode_modbus(c, pdu):
    expected = encode_modbus(c)
    if len(pdu) == 2 and pdu[0] == (c.function|0x80) and pdu[1]:
        raise CommunicationError(6200+pdu[1], 'Modbus exception')
    if not pdu or pdu[0] != c.function: raise CommunicationError(6005,'Function mismatch')
    if c.function in (1,2,3,4,23):
        quantity = c.read_quantity if c.function == 23 else c.quantity
        size = (quantity+7)//8 if c.function in (1,2) else quantity*2
        if len(pdu) != size+2 or pdu[1] != size: raise CommunicationError(6005,'Read length')
        if c.function in (1,2) and quantity%8 and pdu[-1] >> (quantity%8): raise CommunicationError(6005,'Unused response bits')
        return pdu[2:]
    echo = expected[:5] if c.function in (15,16) else expected
    if pdu != echo: raise CommunicationError(6005,'Write echo mismatch')
    return b''


def validate_manifest(manifest):
    if not isinstance(manifest,dict) or set(manifest)!={'SchemaVersion','InterfaceVersion','PlcIntervalMilliseconds','Channels'}: raise ValueError('Manifest schema')
    if type(manifest.get('SchemaVersion')) is not int or manifest['SchemaVersion'] != 1 or manifest.get('InterfaceVersion') != INTERFACE: raise ValueError('Manifest version')
    def number(obj,key,low,high,default):
        val=obj.get(key,default)
        if type(val) is not int or not low <= val <= high: raise ValueError(key)
        return val
    period=number(manifest,'PlcIntervalMilliseconds',1,1000,10)
    rows=manifest.get('Channels')
    if not isinstance(rows,list) or not 1 <= len(rows) <= 16: raise ValueError('Channels')
    result=[]; names=[]; devices=set()
    for index,row in enumerate(rows,1):
        if not isinstance(row,dict) or set(row)!={'Index','Configuration'} or type(row.get('Index')) is not int or row['Index'] != index: raise ValueError('Dense channel index required')
        if not isinstance(row['Configuration'],dict) or set(row['Configuration'])!=CONFIG_KEYS: raise ValueError('Channel schema')
        c=dict(row['Configuration']); name=c.get('Name','')
        if not isinstance(name,str) or not name.isascii() or not name or len(name)>64 or not (name[0].isalpha() or name[0]=='_') or not all(x.isalnum() or x=='_' for x in name): raise ValueError('Name')
        names.append(name)
        protocol=c.get('Protocol')
        if protocol not in ('ModbusTcp','ModbusRtu','Serial','Tcp'): raise ValueError('Client protocol required')
        if protocol in ('ModbusTcp','ModbusRtu') and c['Framing']!='Raw': raise ValueError('Modbus framing must be Raw')
        c['TimeoutMilliseconds']=number(c,'TimeoutMilliseconds',10,10000,1000)
        if protocol in ('Tcp','ModbusTcp'):
            if not isinstance(c['Host'],str) or '%' in c['Host']: raise ValueError('Fixed literal IP required')
            ipaddress.ip_address(c.get('Host',''))
            c['Port']=number(c,'Port',1,65535,502)
        else:
            device=c.get('Device','')
            if not isinstance(device,str) or not device.startswith('/dev/') or len(device)>133 or any(part in ('.','..') for part in device.split('/')) or not all(x.isascii() and (x.isalnum() or x in '/_.-') for x in device): raise ValueError('Device')
            if device in devices: raise ValueError('Serial device ownership conflict')
            devices.add(device)
            c['BaudRate']=number(c,'BaudRate',1200,230400,9600)
            if c['BaudRate'] not in (1200,2400,4800,9600,19200,38400,57600,115200,230400): raise ValueError('Unsupported baud')
            if c.get('DataBits',8) not in (7,8) or c.get('StopBits',1) not in (1,2) or c.get('Parity','Even') not in ('None','Even','Odd'): raise ValueError('Serial format')
            if protocol=='ModbusRtu' and (c.get('DataBits',8)!=8 or c.get('Parity','Even')=='None' and c.get('StopBits',1)!=2): raise ValueError('Modbus RTU format')
            if c.get('Physical','RS232') not in ('RS232','RS485') or c.get('Direction','Automatic') not in ('Automatic','KernelRts'): raise ValueError('Serial direction')
            if c['Physical']=='RS232' and c['Direction']=='KernelRts': raise ValueError('KernelRts requires RS485')
            number(c,'BeforeSendMicroseconds',0,100000,0); number(c,'AfterSendMicroseconds',0,100000,0)
        if c.get('Framing','Raw') not in ('Raw','Fixed','Delimiter','Length16','Idle'): raise ValueError('Framing')
        delimiter_hex=c.get('DelimiterHex','0D0A')
        if not isinstance(delimiter_hex,str) or not 2<=len(delimiter_hex)<=16 or len(delimiter_hex)%2 or not all(x in '0123456789abcdefABCDEF' for x in delimiter_hex): raise ValueError('Delimiter')
        c.setdefault('Parity','Even'); c.setdefault('DelimiterHex','0D0A')
        number(c,'FixedLength',1,512,1); number(c,'IdleMilliseconds',1,1000,10)
        result.append(c)
    if names != sorted(names) or len(set(x.lower() for x in names)) != len(names): raise ValueError('Names must be ordinal sorted and unique')
    return period,result


class Channel:
    def __init__(self, config):
        self.config=dict(config); self.socket=None; self.fd=None
        self.poisoned=False; self.lock=threading.RLock(); self.generation=0
        self.buffer=bytearray(); self.transaction=0

    def close(self, reset=True):
        with self.lock:
            self.generation+=1
            if self.socket is not None:
                try: self.socket.close()
                except OSError: pass
                self.socket=None
            if self.fd is not None:
                try: os.close(self.fd)
                except OSError: pass
                self.fd=None
            self.buffer.clear()
            if reset: self.poisoned=False

    def _check(self, generation):
        if generation != self.generation: raise CommunicationError(6004,'Operation cancelled')

    def _open(self, generation, deadline):
        c=self.config
        if c['Protocol'] in ('Tcp','ModbusTcp'):
            family=socket.AF_INET6 if ':' in c['Host'] else socket.AF_INET
            with self.lock:
                self._check(generation)
                sock=socket.socket(family,socket.SOCK_STREAM); sock.setblocking(False); self.socket=sock
                code=sock.connect_ex((c['Host'],c['Port']))
            if code not in (0,errno.EINPROGRESS,errno.EWOULDBLOCK,errno.EALREADY,10035): raise CommunicationError(6004,'Connect failed')
            self._wait(False,generation,deadline)
            if sock.getsockopt(socket.SOL_SOCKET,socket.SO_ERROR): raise CommunicationError(6004,'Connect failed')
        else:
            if termios is None: raise CommunicationError(6004,'POSIX serial requires Linux')
            with self.lock:
                self._check(generation)
                fd=os.open(c['Device'],os.O_RDWR|os.O_NOCTTY|os.O_NONBLOCK)
                self.fd=fd
                if not stat.S_ISCHR(os.fstat(fd).st_mode): raise CommunicationError(6004,'Serial device must be character device')
                fcntl.flock(fd,fcntl.LOCK_EX|fcntl.LOCK_NB)
                fcntl.ioctl(fd,termios.TIOCEXCL)
                attrs=termios.tcgetattr(fd); attrs[0]=attrs[1]=attrs[3]=0
                attrs[2]=termios.CLOCAL|termios.CREAD|(termios.CS7 if c.get('DataBits',8)==7 else termios.CS8)
                if c.get('StopBits',1)==2: attrs[2]|=termios.CSTOPB
                if c.get('Parity','None') != 'None':
                    attrs[2]|=termios.PARENB
                    if c['Parity']=='Odd': attrs[2]|=termios.PARODD
                attrs[4]=attrs[5]=getattr(termios,'B'+str(c.get('BaudRate',9600)))
                attrs[6][termios.VMIN]=0; attrs[6][termios.VTIME]=0
                termios.tcsetattr(fd,termios.TCSANOW,attrs); termios.tcflush(fd,termios.TCIOFLUSH)
                if c.get('Physical','RS232')=='RS485' and c.get('Direction','Automatic')=='KernelRts':
                    # Linux serial_rs485 uses millisecond RTS delays; round up microseconds.
                    before=(c.get('BeforeSendMicroseconds',0)+999)//1000
                    after=(c.get('AfterSendMicroseconds',0)+999)//1000
                    fcntl.ioctl(fd,0x542f,struct.pack('8I',3,before,after,0,0,0,0,0))

    def _wait(self, reading, generation, deadline):
        while True:
            with self.lock:
                self._check(generation); resource=self.socket if self.socket is not None else self.fd
            remaining=deadline-time.monotonic()
            if remaining<=0: raise CommunicationError(6003,'Timeout')
            ready=select.select([resource] if reading else [],[] if reading else [resource],[],min(.02,remaining))
            if ready[0] or ready[1]: return

    def _send(self, data, generation, deadline, sent):
        automatic_serial=self.fd is not None and self.config.get('Direction','Automatic')=='Automatic'
        if automatic_serial: self._delay(self.config.get('BeforeSendMicroseconds',0)/1e6,generation,deadline)
        offset=0
        while offset<len(data):
            self._wait(False,generation,deadline)
            with self.lock:
                self._check(generation)
                try: count=self.socket.send(data[offset:]) if self.socket is not None else os.write(self.fd,data[offset:])
                except BlockingIOError: continue
                if count<=0: raise CommunicationError(6004,'Disconnected')
                offset+=count; sent[0]=True
        if self.fd is not None:
            while True:
                with self.lock:
                    self._check(generation)
                    pending=struct.unpack('I',fcntl.ioctl(self.fd,0x5411,struct.pack('I',0)))[0]
                if not pending: break
                self._delay(.001,generation,deadline)
            if automatic_serial: self._delay(self.config.get('AfterSendMicroseconds',0)/1e6,generation,deadline)

    def _delay(self, seconds, generation, deadline):
        until=time.monotonic()+seconds
        while time.monotonic()<until:
            self._check(generation)
            remaining=deadline-time.monotonic()
            if remaining<=0: raise CommunicationError(6003,'Timeout')
            time.sleep(min(.001,remaining,max(0,until-time.monotonic())))

    def _read(self, maximum, generation, deadline):
        while True:
            self._wait(True,generation,deadline)
            with self.lock:
                self._check(generation)
                try: data=self.socket.recv(maximum) if self.socket is not None else os.read(self.fd,maximum)
                except BlockingIOError: continue
                if not data: raise CommunicationError(6004,'Disconnected')
                return data

    def _exact(self, count, generation, deadline):
        while len(self.buffer)<count:
            self.buffer.extend(self._read(count-len(self.buffer),generation,deadline))
        value=bytes(self.buffer[:count]); del self.buffer[:count]
        return value

    def _rtu_intervals(self):
        baud=self.config['BaudRate']
        character=(1+self.config['DataBits']+(self.config['Parity']!='None')+self.config['StopBits'])/baud
        return (.000750,.001750) if baud>19200 else (1.5*character,3.5*character)

    def _rtu_quiet(self,generation,deadline,reject_data=False):
        # Observe receive silence, rather than merely sleeping while old bytes queue.
        interval=self._rtu_intervals()[1]
        while True:
            until=time.monotonic()+interval
            try: self._read(256,generation,min(until,deadline))
            except CommunicationError as error:
                if error.code==6003 and until<=deadline:return
                raise
            if reject_data:raise CommunicationError(6005,'RTU trailing bytes before frame boundary')

    def _rtu_receive(self,c,generation,deadline):
        frame=bytearray();intercharacter=self._rtu_intervals()[0]
        while True:
            limit=deadline if not frame else min(deadline,time.monotonic()+intercharacter)
            try: frame.extend(self._read(256-len(frame),generation,limit))
            except CommunicationError as error:
                if error.code==6003 and frame:raise CommunicationError(6005,'RTU incomplete frame across silent interval') from error
                raise
            if len(frame)>=2 and (frame[0]!=c.unit or frame[1] not in (c.function,c.function|0x80)):
                raise CommunicationError(6005,'RTU unit/function')
            if len(frame)<3:continue
            total=5 if frame[1]&0x80 else frame[2]+5 if c.function in (1,2,3,4,23) else 10 if c.function==22 else 8
            if total>256 or len(frame)>total:raise CommunicationError(6005,'RTU length')
            if len(frame)==total:
                self._rtu_quiet(generation,deadline,reject_data=True)
                if crc16(frame[:-2])!=struct.unpack('<H',frame[-2:])[0]:raise CommunicationError(6005,'RTU address/CRC')
                return bytes(frame[1:-2])

    def _receive(self,c,generation,deadline):
        framing=self.config.get('Framing','Raw')
        if framing=='Length16':
            size=struct.unpack('>H',self._exact(2,generation,deadline))[0]
            if size>512: raise CommunicationError(6005,'Frame too long')
            return self._exact(size,generation,deadline)
        if framing=='Fixed': return self._exact(self.config['FixedLength'],generation,deadline)
        if framing=='Raw':
            maximum=c.length or 512
            if self.buffer:
                data=bytes(self.buffer[:maximum]); del self.buffer[:maximum]; return data
            return self._read(maximum,generation,deadline)
        delimiter=bytes.fromhex(self.config.get('DelimiterHex','')) if framing=='Delimiter' else None
        idle=self.config.get('IdleMilliseconds',10)/1000
        while True:
            if delimiter:
                end=self.buffer.find(delimiter)
                if end>=0:
                    if end>512: raise CommunicationError(6005,'Frame exceeds 512 bytes')
                    data=bytes(self.buffer[:end]); del self.buffer[:end+len(delimiter)]; return data
            if len(self.buffer)>(512+len(delimiter)-1 if delimiter else 512): raise CommunicationError(6005,'Frame exceeds 512 bytes')
            try: chunk=self._read(1,generation,min(deadline,time.monotonic()+idle) if framing=='Idle' and self.buffer else deadline)
            except CommunicationError as error:
                if framing=='Idle' and self.buffer and error.code==6003 and time.monotonic()<deadline:
                    data=bytes(self.buffer); self.buffer.clear(); return data
                raise
            self.buffer.extend(chunk)

    def execute(self,c,expected_generation=None):
        if expected_generation is not None: self._check(expected_generation)
        if c.action==5:
            self.close(); return b'',False
        if self.poisoned: raise CommunicationError(6004,'Channel requires explicit close')
        if c.length>512 or c.length>len(c.data): invalid('Payload length')
        protocol=self.config['Protocol']; modbus=protocol in ('ModbusTcp','ModbusRtu')
        if c.action not in ((1,) if modbus else ((2,3,4) if protocol=='Tcp' else (3,4))): invalid('Action/protocol mismatch')
        pdu=encode_modbus(c) if modbus else None
        if modbus and not 0<=c.unit<=(247 if protocol=='ModbusRtu' else 255): invalid('Unit')
        if protocol=='ModbusRtu' and c.unit==0 and c.function not in (5,6,15,16,22): invalid('Broadcast is write-only')
        if not modbus and c.action==3 and c.length==0: invalid('Empty send')
        generation=self.generation if expected_generation is None else expected_generation
        deadline=time.monotonic()+self.config.get('TimeoutMilliseconds',1000)/1000
        sent=[False]; write=modbus and c.function in (5,6,15,16,22,23) or c.action==3
        try:
            if self.socket is None and self.fd is None:
                if protocol=='Tcp' and c.action!=2: raise CommunicationError(6007,'Explicit TCP connect required')
                self._open(generation,deadline)
            if c.action==2: return b'',False
            if not modbus:
                if c.action==4: return self._receive(c,generation,deadline),False
                data=c.data[:c.length]; framing=self.config.get('Framing','Raw')
                if framing=='Length16': data=struct.pack('>H',len(data))+data
                elif framing=='Delimiter': data+=bytes.fromhex(self.config['DelimiterHex'])
                elif framing=='Fixed' and len(data)!=self.config['FixedLength']: invalid('Fixed send size')
                self._send(data,generation,deadline,sent); return b'',False
            if protocol=='ModbusTcp':
                self.transaction=(self.transaction%65535)+1
                self._send(struct.pack('>HHHB',self.transaction,0,len(pdu)+1,c.unit)+pdu,generation,deadline,sent)
                header=self._exact(7,generation,deadline); tid,pid,length,unit=struct.unpack('>HHHB',header)
                if tid!=self.transaction or pid or unit!=c.unit or not 2<=length<=254: raise CommunicationError(6005,'MBAP header mismatch')
                response=self._exact(length-1,generation,deadline)
            else:
                self._rtu_quiet(generation,deadline)
                frame=bytes([c.unit])+pdu; frame+=struct.pack('<H',crc16(frame))
                self._send(frame,generation,deadline,sent)
                if c.unit==0: return b'',False
                response=self._rtu_receive(c,generation,deadline)
            return decode_modbus(c,response),True
        except (OSError,ValueError,CommunicationError) as error:
            known_exception=isinstance(error,CommunicationError) and 6201<=error.code<=6455
            if known_exception: raise
            with self.lock:
                if generation==self.generation:
                    self.close(reset=False); self.poisoned=True
            if write and sent[0]: raise CommunicationError(6008,'Write outcome unknown',True) from error
            if isinstance(error,CommunicationError): raise
            raise CommunicationError(6004,'Transport failure') from error


class Engine:
    def __init__(self, configs, period_ms=10):
        self.channels=[Channel(c) for c in configs]
        self.feedback=[(0,0,0,0,0,0,0,b'') for _ in range(16)]
        self.highest=[0]*16; self.generations=[0]*16; self.lock=threading.RLock()
        self.scan=0; self.last_scan=None; self.timeout=max(.250,3*period_ms/1000); self.healthy=False
        self.threads=[None]*16

    def close(self):
        with self.lock:
            self.healthy=False
            for i,ch in enumerate(self.channels):
                self.generations[i]+=1; ch.close()
                old=self.feedback[i]
                if old[1]: self.feedback[i]=(old[0],0,0,6002,1,0,0,b'')

    def tick(self):
        with self.lock:
            if self.healthy and self.last_scan is not None and time.monotonic()-self.last_scan>self.timeout: self.close()

    def _run(self,i,c,generation,channel_generation):
        try:
            data,ack=self.channels[i].execute(c,channel_generation); result=(c.request,0,1,0,0,int(ack),len(data),data)
        except CommunicationError as error: result=(c.request,0,0,error.code,int(error.unknown),0,0,b'')
        except Exception: result=(c.request,0,0,6004,1,0,0,b'')
        with self.lock:
            if self.generations[i]==generation and self.healthy: self.feedback[i]=result

    def image(self,data):
        if len(data)!=INPUT_SIZE: raise ValueError('Native image length')
        scan,flags=HEADER.unpack_from(data)
        with self.lock:
            self.tick()
            fresh=flags==1 and scan>self.scan
            if not fresh:
                if flags!=1: self.close()
            else:
                self.scan=scan; self.last_scan=time.monotonic(); self.healthy=True
            if self.healthy and fresh:
                for i in range(16):
                    c=Command(*COMMAND.unpack_from(data,16+i*COMMAND.size))
                    if not c.request: continue
                    old=self.feedback[i]
                    if c.request==self.highest[i]: continue
                    if c.request<self.highest[i]:
                        if not old[1]: self.feedback[i]=(c.request,0,0,6009,0,0,0,b'')
                        continue
                    self.highest[i]=c.request
                    if i>=len(self.channels): self.feedback[i]=(c.request,0,0,6001,0,0,0,b''); continue
                    if c.action==5:
                        self.generations[i]+=1; self.channels[i].close()
                        self.feedback[i]=(c.request,0,1,0,0,0,0,b''); continue
                    if old[1] or (self.threads[i] is not None and self.threads[i].is_alive()):
                        self.feedback[i]=(c.request,0,0,6006,0,0,0,b'')
                        continue
                    self.generations[i]+=1; generation=self.generations[i]
                    self.feedback[i]=(c.request,1,0,0,0,0,0,b'')
                    thread=threading.Thread(target=self._run,args=(i,c,generation,self.channels[i].generation),daemon=True)
                    self.threads[i]=thread; thread.start()
            return b''.join(FEEDBACK.pack(*row[:-1],row[-1].ljust(512,b'\x00')) for row in self.feedback)


def serve(manifest_path,socket_path,capabilities_path=None):
    if sys.platform!='linux': raise RuntimeError('Native worker requires Linux')
    with open(manifest_path,'rb') as source: raw=source.read(131073)
    if len(raw)>131072: raise ValueError('Manifest too large')
    period,configs=validate_manifest(json.loads(raw))
    engine=Engine(configs,period); path=pathlib.Path(socket_path); parent=path.parent
    parent.mkdir(mode=0o700,parents=True,exist_ok=True)
    info=parent.lstat()
    if not stat.S_ISDIR(info.st_mode) or info.st_uid!=os.getuid() or stat.S_IMODE(info.st_mode)!=0o700: raise ValueError('Socket directory must be owned private 0700 directory')
    if path.exists(): raise ValueError('Socket path already exists; refuse stale/foreign replacement')
    capability=pathlib.Path(capabilities_path) if capabilities_path else pathlib.Path(str(path)+'.capabilities.json')
    if capability.parent.resolve()!=parent.resolve(): raise ValueError('Capabilities must share private socket directory')
    server=socket.socket(socket.AF_UNIX,socket.SOCK_STREAM); selector=selectors.DefaultSelector(); client=None
    incoming=bytearray(); outgoing=bytearray(); last_cap=0
    def disconnect():
        nonlocal client
        if client is not None:
            selector.unregister(client); client.close(); client=None
        incoming.clear(); outgoing.clear(); engine.close()
    try:
        server.bind(str(path)); os.chmod(path,0o600); server.listen(1); server.setblocking(False)
        selector.register(server,selectors.EVENT_READ)
        while True:
            engine.tick(); now=time.monotonic()
            if now-last_cap>.1:
                record=dict(SchemaVersion=1,InterfaceVersion=INTERFACE,ManifestSha256=hashlib.sha256(raw).hexdigest(),PID=os.getpid(),WorkerReady=True,LastScan=engine.scan,ScanHealthy=engine.healthy)
                temporary=capability.with_name(capability.name+'.'+str(os.getpid())+'.tmp')
                fd=os.open(str(temporary),os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
                with os.fdopen(fd,'w') as file: json.dump(record,file)
                os.replace(temporary,capability); last_cap=now
            for key,events in selector.select(.02):
                if key.fileobj is server:
                    accepted,_=server.accept()
                    peer_pid,uid,gid=struct.unpack('3i',accepted.getsockopt(socket.SOL_SOCKET,socket.SO_PEERCRED,12))
                    if uid!=os.getuid() or client is not None: accepted.close(); continue
                    client=accepted; client.setblocking(False); selector.register(client,selectors.EVENT_READ)
                    continue
                try:
                    if events&selectors.EVENT_WRITE and outgoing:
                        sent=client.send(outgoing); del outgoing[:sent]
                    if events&selectors.EVENT_READ:
                        chunk=client.recv(INPUT_SIZE-len(incoming))
                        if not chunk: disconnect(); continue
                        incoming.extend(chunk)
                        if len(incoming)==INPUT_SIZE:
                            if outgoing: disconnect(); continue
                            outgoing.extend(engine.image(incoming)); incoming.clear()
                    if client is not None: selector.modify(client,selectors.EVENT_READ|(selectors.EVENT_WRITE if outgoing else 0))
                except (OSError,ValueError): disconnect()
    finally:
        if client is not None: client.close()
        engine.close(); selector.close(); server.close()
        path.unlink(missing_ok=True); capability.unlink(missing_ok=True)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest',required=True); parser.add_argument('--socket',required=True)
    parser.add_argument('--capabilities')
    args=parser.parse_args(); serve(args.manifest,args.socket,args.capabilities)


if __name__=='__main__': main()
