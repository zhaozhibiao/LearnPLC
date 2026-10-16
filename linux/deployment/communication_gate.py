"""Read-only validation of private client/master communication dependencies.

Readiness never opens configured serial ports or connects configured peers.
"""
import hashlib
import ipaddress
import os
import pathlib
import re
import shlex
import stat

from axis_gate import strict_json, read_private_json

INTERFACE = 'workbench-communication-v1'
FILES = {'communication.st', 'communication_bridge.hpp', 'communication_bridge.cpp',
    'generated_communication_scan_shim.cpp', 'communication_lifecycle.inc',
    'communication-constants.st', 'communication-manifest.json',
    'communication/communication_worker.py', 'communication/LICENSE'}
KEYS = {'Name','Protocol','Host','Port','Device','BaudRate','DataBits','Parity','StopBits',
    'Physical','Direction','BeforeSendMicroseconds','AfterSendMicroseconds',
    'TimeoutMilliseconds','Framing','DelimiterHex','FixedLength','IdleMilliseconds'}


def integer(value, lower, upper):
    return type(value) is int and lower <= value <= upper


def validate_manifest(data):
    if (not isinstance(data, dict) or set(data) != {'SchemaVersion','InterfaceVersion','PlcIntervalMilliseconds','Channels'}
        or type(data['SchemaVersion']) is not int or data['SchemaVersion'] != 1 or data['InterfaceVersion'] != INTERFACE
        or not integer(data['PlcIntervalMilliseconds'],1,1000)):
        raise ValueError('Invalid communication manifest schema/version/PLC interval')
    channels=data['Channels']
    if not isinstance(channels,list) or not 1 <= len(channels) <= 16:
        raise ValueError('Communication channel count must be 1..16')
    names,devices=set(),set()
    for index,channel in enumerate(channels,1):
        if not isinstance(channel,dict) or set(channel) != {'Index','Configuration'} or type(channel['Index']) is not int or channel['Index'] != index:
            raise ValueError('Invalid communication channel schema/dense index')
        c=channel['Configuration']
        if not isinstance(c,dict) or set(c) != KEYS:
            raise ValueError('Invalid communication channel configuration fields')
        name=c['Name']
        if not isinstance(name,str) or len(name)>64 or re.fullmatch('[A-Za-z_][A-Za-z0-9_]*',name) is None or name.casefold() in names:
            raise ValueError('Invalid or duplicate communication name')
        names.add(name.casefold())
        if c['Protocol'] not in ('ModbusTcp','ModbusRtu','Serial','Tcp'):
            raise ValueError('Only communication masters/clients are supported')
        if c['Protocol'] in ('ModbusTcp','ModbusRtu') and c['Framing'] != 'Raw': raise ValueError('Modbus framing must be Raw')
        if not integer(c['TimeoutMilliseconds'],10,10000): raise ValueError('Invalid communication timeout')
        if c['Protocol'] in ('ModbusTcp','Tcp'):
            if not isinstance(c['Host'],str) or '%' in c['Host']: raise ValueError('Fixed literal IP required')
            ipaddress.ip_address(c['Host'])
            if not integer(c['Port'],1,65535): raise ValueError('Invalid TCP port')
        else:
            device=c['Device']
            if (not isinstance(device,str) or re.fullmatch(r'/dev/[A-Za-z0-9_./-]{1,128}',device) is None
                or any(p in ('.','..') for p in device.split('/')) or device in devices):
                raise ValueError('Invalid or duplicate serial device')
            devices.add(device)
            if (type(c['BaudRate']) is not int or c['BaudRate'] not in (1200,2400,4800,9600,19200,38400,57600,115200,230400)
                or not integer(c['DataBits'],7,8) or c['Parity'] not in ('None','Even','Odd') or not integer(c['StopBits'],1,2)):
                raise ValueError('Unsupported serial format')
            if c['Protocol']=='ModbusRtu' and (c['DataBits']!=8 or c['Parity']=='None' and c['StopBits']!=2):
                raise ValueError('Invalid Modbus RTU serial format')
            if (c['Physical'] not in ('RS232','RS485') or c['Direction'] not in ('Automatic','KernelRts')
                or not integer(c['BeforeSendMicroseconds'],0,100000) or not integer(c['AfterSendMicroseconds'],0,100000)):
                raise ValueError('Invalid serial direction settings')
            if c['Physical']=='RS232' and c['Direction']=='KernelRts': raise ValueError('KernelRts requires RS485')
        if (c['Framing'] not in ('Raw','Fixed','Delimiter','Length16','Idle')
            or not integer(c['FixedLength'],1,512) or not integer(c['IdleMilliseconds'],1,1000)
            or not isinstance(c['DelimiterHex'],str) or re.fullmatch(r'(?:[A-Fa-f0-9]{2}){1,8}',c['DelimiterHex']) is None):
            raise ValueError('Invalid bounded frame settings')
    if channels != sorted(channels,key=lambda c:c['Configuration']['Name'].encode('utf-16-be')):
        raise ValueError('Communication channels must be sorted by ordinal name')
    return data


def validate_bundle(archive, manifest, names):
    if any(n.startswith('communication/') and n not in FILES for n in names):
        raise ValueError('Unknown communication dependency')
    if manifest.get('CommunicationInterfaceVersion') is None:
        if FILES.intersection(names): raise ValueError('Communication dependencies require explicit interface identity')
        return
    if manifest['CommunicationInterfaceVersion'] != INTERFACE or not FILES.issubset(names):
        raise ValueError('Unapproved or incomplete communication bundle')
    if archive.getinfo('communication-manifest.json').file_size > 131072:
        raise ValueError('Communication manifest capacity exceeded')
    data=validate_manifest(strict_json(archive.read('communication-manifest.json')))
    expected='VAR_GLOBAL CONSTANT\n'+''.join('    COMM_%s : COMM_REF := %d;\n' % (c['Configuration']['Name'],c['Index']) for c in data['Channels'])+'END_VAR\n'
    if archive.read('communication-constants.st').replace(b'\r\n',b'\n') != expected.encode():
        raise ValueError('Communication constants differ from manifest bindings')
    if 'hardware-configuration.json' in names:
        hardware=strict_json(archive.read('hardware-configuration.json'))
        if not isinstance(hardware,dict): raise ValueError('Invalid communication hardware configuration')
        configuration=hardware.get('Communication')
        if (not isinstance(configuration,dict) or set(configuration) != {'Channels'}
            or not isinstance(configuration['Channels'],list)
            or any(not isinstance(c,dict) or not isinstance(c.get('Name'),str) for c in configuration['Channels'])
            or sorted(configuration['Channels'],key=lambda c:c['Name'].encode('utf-16-be')) != [c['Configuration'] for c in data['Channels']]):
            raise ValueError('Communication hardware configuration differs from manifest')


def verify_ready(manifest, generated, config, environment):
    if manifest.get('CommunicationInterfaceVersion') is None: return
    if manifest['CommunicationInterfaceVersion'] != INTERFACE or config.get('communication_backend') != 'NativeClient':
        raise ValueError('Communication NativeClient backend must be explicitly configured')
    with open(pathlib.Path(generated)/'communication-manifest.json','rb') as source: raw=source.read(131073)
    if len(raw)>131072: raise ValueError('Communication manifest capacity exceeded')
    validate_manifest(strict_json(raw))
    endpoint=config.get('communication_socket_path')
    if not isinstance(endpoint,str) or not endpoint.startswith('/') or '\x00' in endpoint or len(endpoint.encode())>=104:
        raise ValueError('Private absolute communication socket required')
    values={}
    for token in shlex.split(environment):
        if '=' in token:
            k,v=token.split('=',1)
            if k in values: raise ValueError('Duplicate Runtime environment variable')
            values[k]=v
    if values.get('STWORKBENCH_COMM_SOCKET') != endpoint:
        raise ValueError('Runtime communication environment differs from configured worker')
    info=pathlib.Path(endpoint).lstat()
    directory=pathlib.Path(endpoint).parent.lstat()
    if not stat.S_ISDIR(directory.st_mode) or directory.st_uid!=os.getuid() or stat.S_IMODE(directory.st_mode)!=0o700:
        raise ValueError('Communication socket directory must be private 0700 and owned by current user')
    if not stat.S_ISSOCK(info.st_mode) or info.st_uid!=os.getuid() or stat.S_IMODE(info.st_mode)!=0o600:
        raise ValueError('Communication socket must be private and owned by current user')
    cap=read_private_json(endpoint+'.capabilities.json')
    if not isinstance(cap,dict): raise ValueError('Invalid communication capability')
    pid=cap.get('PID')
    if type(pid) is not int or pid<=0: raise ValueError('Invalid communication worker PID')
    os.kill(pid,0)
    if (pathlib.Path('/proc')/str(pid)).stat().st_uid!=os.getuid(): raise ValueError('Communication worker owner differs')
    expected=dict(SchemaVersion=1,InterfaceVersion=INTERFACE,ManifestSha256=hashlib.sha256(raw).hexdigest(),WorkerReady=True)
    for k,v in expected.items():
        if type(cap.get(k)) is not type(v) or cap[k]!=v: raise ValueError('Communication capability differs: '+k)
    if not integer(cap.get('LastScan'),0,2**64-1) or type(cap.get('ScanHealthy')) is not bool:
        raise ValueError('Invalid communication scan capability')
