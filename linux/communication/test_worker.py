import importlib.util
import json
import pathlib
import socket
import struct
import threading
import time
import unittest
import os
import tempfile
import subprocess
import sys

import communication_worker as w


def config(protocol='ModbusTcp', **values):
    result=dict(Name='Test', Protocol=protocol, Host='127.0.0.1', Port=502,
        Device='/dev/ttyUSB0',BaudRate=9600,DataBits=8,Parity='None',StopBits=2,
        Physical='RS232',Direction='Automatic',BeforeSendMicroseconds=0,AfterSendMicroseconds=0,
        TimeoutMilliseconds=150,Framing='Raw',DelimiterHex='0D0A',FixedLength=1,IdleMilliseconds=20)
    result.update(values)
    return result


def command(request=1, action=1, function=3, unit=1, address=0,
            quantity=2, read_address=0, read_quantity=0, data=b''):
    return w.Command(request, action, function, unit, address, quantity,
                     read_address, read_quantity, len(data), data)


class ProtocolTests(unittest.TestCase):
    def test_crc_golden(self):
        self.assertEqual(w.crc16(bytes.fromhex('01030000000A')), 0xCDC5)

    def test_modbus_limits_and_echo(self):
        self.assertEqual(w.encode_modbus(command()).hex(), '0300000002')
        self.assertEqual(w.decode_modbus(command(), bytes.fromhex('030400010002')), b'\x00\x01\x00\x02')
        for cmd in (command(quantity=126), command(address=65535, quantity=2),
                    command(function=5, quantity=1, data=b'\x01\x00')):
            with self.assertRaises(w.CommunicationError):
                w.encode_modbus(cmd)
        with self.assertRaises(w.CommunicationError) as error:
            w.decode_modbus(command(), bytes.fromhex('8302'))
        self.assertEqual(error.exception.code, 6202)
        cmd = command(function=6, quantity=1, data=b'\x00\x02')
        self.assertEqual(w.decode_modbus(cmd, w.encode_modbus(cmd)), b'')
        with self.assertRaises(w.CommunicationError):
            w.decode_modbus(cmd, bytes.fromhex('0600000003'))

    def test_image_sizes(self):
        self.assertEqual(w.INPUT_SIZE, 9360)
        self.assertEqual(len(w.Engine([]).image(struct.pack('<QQ', 1, 1) + bytes(9344))), 9088)

    def test_all_supported_functions_and_boundaries(self):
        vectors=[
            (command(function=1,quantity=9),'0100000009','01020301',bytes.fromhex('0301')),
            (command(function=2,quantity=8),'0200000008','0201ff',b'\xff'),
            (command(function=4,quantity=1),'0400000001','04021234',bytes.fromhex('1234')),
            (command(function=5,quantity=1,data=b'\xff\x00'),'050000ff00','050000ff00',b''),
            (command(function=15,quantity=9,data=b'\xff\x01'),'0f0000000902ff01','0f00000009',b''),
            (command(function=16,quantity=2,data=bytes.fromhex('12345678')),'10000000020412345678','1000000002',b''),
            (command(function=22,quantity=1,data=bytes.fromhex('ff0000ff')),'160000ff0000ff','160000ff0000ff',b''),
            (command(function=23,quantity=1,read_address=10,read_quantity=1,data=bytes.fromhex('1234')),'17000a000100000001021234','1702abcd',bytes.fromhex('abcd')),
        ]
        for cmd,request,response,value in vectors:
            with self.subTest(function=cmd.function):
                self.assertEqual(w.encode_modbus(cmd),bytes.fromhex(request))
                self.assertEqual(w.decode_modbus(cmd,bytes.fromhex(response)),value)
        for cmd in (command(function=15,quantity=1969,data=bytes(247)),
                    command(function=16,quantity=124,data=bytes(248)),
                    command(function=23,quantity=122,data=bytes(244),read_quantity=1),
                    command(function=23,quantity=1,data=bytes(2),read_quantity=126),
                    command(function=99),command(quantity=0)):
            with self.assertRaises(w.CommunicationError): w.encode_modbus(cmd)

    def test_manifest_rejects_server_dns_duplicate_device(self):
        for channels in ([config('ModbusServer')], [dict(config(), Host='example.com')],
                         [config('Serial', Device='/dev/ttyS0'), config('Serial', Device='/dev/ttyS0')]):
            with self.assertRaises(ValueError):
                w.validate_manifest({'SchemaVersion': 1, 'InterfaceVersion': w.INTERFACE,
                    'PlcIntervalMilliseconds': 10, 'Channels': [dict(Index=i+1, Configuration=c) for i,c in enumerate(channels)]})

    def test_manifest_ip_direction_strict_schema_agrees_with_gate(self):
        sys.path.insert(0,str(pathlib.Path(__file__).resolve().parents[1]/'deployment'))
        try:
            import communication_gate as gate
        except ImportError:
            gate=None
        for cfg in (config(Host='127.1'),config(Host='2130706433'),config(Host='127.00.0.1'),
                    config(Host='fe80::1%lo'),config('Serial',Direction='KernelRts'),
                    config('ModbusRtu',Framing='Delimiter'),config('ModbusRtu',StopBits=1)):
            manifest=dict(SchemaVersion=1,InterfaceVersion=w.INTERFACE,PlcIntervalMilliseconds=10,
                Channels=[dict(Index=1,Configuration=cfg)])
            for validator in ([w.validate_manifest,gate.validate_manifest] if gate else [w.validate_manifest]):
                with self.subTest(cfg=cfg,validator=validator.__module__):
                    with self.assertRaises(ValueError): validator(manifest)
        for host in ('127.0.0.1','0.0.0.0','::1','::ffff:192.168.1.1','2001:db8::1'):
            manifest=dict(SchemaVersion=1,InterfaceVersion=w.INTERFACE,PlcIntervalMilliseconds=10,
                Channels=[dict(Index=1,Configuration=config(Host=host))])
            w.validate_manifest(manifest)
            if gate: gate.validate_manifest(manifest)


class TcpTests(unittest.TestCase):
    def peer(self, handler):
        server = socket.socket()
        server.bind(('127.0.0.1', 0)); server.listen(1)
        self.addCleanup(server.close)
        def run():
            client, _ = server.accept()
            with client:
                handler(client)
        thread = threading.Thread(target=run, daemon=True); thread.start()
        return server.getsockname()[1], thread

    def test_real_modbus_fragmented_and_duplicate_held(self):
        seen = []
        def peer(client):
            data = client.recv(512); seen.append(data)
            # Independently constructed transaction 1 / unit 1 read two registers.
            self.assertEqual(data, bytes.fromhex('000100000006010300000002'))
            client.sendall(bytes.fromhex('000100000007010304'))
            time.sleep(.01); client.sendall(bytes.fromhex('00120034'))
            time.sleep(.1)
        port, thread = self.peer(peer)
        engine = w.Engine([dict(config(), Port=port)], period_ms=100)
        self.addCleanup(engine.close)
        frame = struct.pack('<QQ', 1, 1) + w.COMMAND.pack(1,1,3,1,0,2,0,0,0,bytes(512)) + bytes(15*w.COMMAND.size)
        engine.image(frame)
        for scan in range(2, 50):
            time.sleep(.005)
            result = engine.image(struct.pack('<QQ',scan,1)+frame[16:])
            fields = w.FEEDBACK.unpack_from(result)
            if fields[2]: break
        self.assertEqual(fields[0:7], (1,0,1,0,0,1,4))
        self.assertEqual(fields[7][:4], bytes.fromhex('00120034'))
        thread.join(1); self.assertEqual(len(seen), 1)

    def test_uncertain_write_poison_and_explicit_close(self):
        def peer(client):
            client.recv(512)
        port, thread = self.peer(peer)
        channel = w.Channel(dict(config(), Port=port))
        with self.assertRaises(w.CommunicationError) as error:
            channel.execute(command(function=6,quantity=1,data=b'\x00\x01'))
        self.assertEqual(error.exception.code, 6008)
        self.assertTrue(channel.poisoned)
        with self.assertRaises(w.CommunicationError): channel.execute(command(request=2))
        channel.execute(command(request=3, action=5)); self.assertFalse(channel.poisoned)

    def test_busy_new_request_rejected_without_second_network_write(self):
        def peer(client):
            client.recv(512); time.sleep(.1)
            client.sendall(bytes.fromhex('00010000000701030400010002'))
            time.sleep(.1)
        port, thread=self.peer(peer)
        engine=w.Engine([dict(config(),Port=port)],period_ms=100)
        self.addCleanup(engine.close)
        def frame(scan,request):
            return struct.pack('<QQ',scan,1)+w.COMMAND.pack(request,1,3,1,0,2,0,0,0,bytes(512))+bytes(15*w.COMMAND.size)
        engine.image(frame(1,1))
        row=w.FEEDBACK.unpack_from(engine.image(frame(2,2)))
        self.assertEqual((row[0],row[3]),(2,6006))
        time.sleep(.15)
        row=w.FEEDBACK.unpack_from(engine.image(frame(3,2)))
        self.assertEqual((row[0],row[2]),(1,1))

    def test_nonincreasing_scan_cannot_start_network_job(self):
        engine=w.Engine([config('Tcp')]); self.addCleanup(engine.close)
        empty=struct.pack('<QQ',1,1)+bytes(9344)
        engine.image(empty)
        replay=struct.pack('<QQ',1,1)+w.COMMAND.pack(1,2,0,0,0,0,0,0,0,bytes(512))+bytes(15*w.COMMAND.size)
        row=w.FEEDBACK.unpack_from(engine.image(replay))
        self.assertEqual(row[0],0)

    def test_wrong_transaction_and_read_timeout_are_not_uncertain_writes(self):
        for response,code in ((bytes.fromhex('00020000000701030400010002'),6005),(None,6003)):
            with self.subTest(code=code):
                def peer(client):
                    client.recv(512)
                    if response is not None: client.sendall(response)
                    time.sleep(.2)
                port,thread=self.peer(peer)
                channel=w.Channel(dict(config(),Port=port)); self.addCleanup(channel.close)
                with self.assertRaises(w.CommunicationError) as error: channel.execute(command())
                self.assertEqual(error.exception.code,code); self.assertFalse(error.exception.unknown)
                self.assertTrue(channel.poisoned); thread.join(.5)

    def test_tcp_binary_length16_coalesced_and_explicit_connect(self):
        def peer(client):
            self.assertEqual(client.recv(512),b'\x00\x03A\x00B')
            client.sendall(b'\x00\x03x\x00y\x00\x01z')
            time.sleep(.1)
        port,thread=self.peer(peer)
        channel=w.Channel(dict(config('Tcp'),Port=port,Framing='Length16')); self.addCleanup(channel.close)
        with self.assertRaises(w.CommunicationError): channel.execute(command(action=3,data=b'A'))
        channel.close()
        self.assertEqual(channel.execute(command(action=2)),(b'',False))
        self.assertEqual(channel.execute(command(action=3,data=b'A\x00B')),(b'',False))
        self.assertEqual(channel.execute(command(action=4)),(b'x\x00y',False))
        self.assertEqual(channel.execute(command(action=4)),(b'z',False)); thread.join(.5)

    def test_watchdog_cancels_pending_read_and_no_duplicate_replay(self):
        received=[]
        def peer(client):
            received.append(client.recv(512)); time.sleep(.4)
        port,thread=self.peer(peer)
        engine=w.Engine([dict(config(),Port=port,TimeoutMilliseconds=1000)]); self.addCleanup(engine.close)
        frame=struct.pack('<QQ',1,1)+w.COMMAND.pack(1,1,3,1,0,2,0,0,0,bytes(512))+bytes(15*w.COMMAND.size)
        engine.image(frame); time.sleep(.3); engine.tick()
        self.assertFalse(engine.healthy)
        fields=w.FEEDBACK.unpack_from(engine.image(struct.pack('<QQ',2,1)+frame[16:]))
        self.assertEqual((fields[0],fields[3]),(1,6002))
        thread.join(.5); self.assertEqual(len(received),1)

    def test_full_capacity_delimiter_and_idle_frames(self):
        for framing in ('Delimiter','Idle'):
            with self.subTest(framing=framing):
                def peer(client):
                    client.sendall(b'x'*512+(b'|' if framing=='Delimiter' else b'')); time.sleep(.1)
                port,thread=self.peer(peer)
                channel=w.Channel(dict(config('Tcp'),Port=port,Framing=framing,DelimiterHex='7C',IdleMilliseconds=10))
                self.addCleanup(channel.close)
                channel.execute(command(action=2))
                self.assertEqual(channel.execute(command(action=4)),(b'x'*512,False)); thread.join(.5)


@unittest.skipUnless(sys.platform=='linux','Linux POSIX PTY / AF_UNIX peer credentials required')
class LinuxTests(unittest.TestCase):
    def test_rtu_long_silent_gap_never_acknowledges_a_concatenated_frame(self):
        import pty
        for function in (3,6):
            with self.subTest(function=function):
                master,slave=pty.openpty()
                channel=w.Channel(config('ModbusRtu',Device=os.ttyname(slave),TimeoutMilliseconds=500))
                errors=[]
                def peer():
                    try:
                        query=os.read(master,256)
                        frame=bytes.fromhex('010304001200345be1') if function==3 else query
                        os.write(master,frame[:2]);time.sleep(.05);os.write(master,frame[2:])
                    except BaseException as error: errors.append(error)
                thread=threading.Thread(target=peer);thread.start()
                try:
                    cmd=command() if function==3 else command(function=6,quantity=1,data=b'\x12\x34')
                    with self.assertRaises(w.CommunicationError) as raised: channel.execute(cmd)
                    self.assertEqual(raised.exception.code,6005 if function==3 else 6008)
                    self.assertEqual(raised.exception.unknown,function==6)
                finally:
                    thread.join(1);channel.close();os.close(master);os.close(slave)
                if errors:raise errors[0]

    def test_rtu_crc_and_broadcast(self):
        import pty
        master,slave=pty.openpty(); path=os.ttyname(slave)
        self.addCleanup(os.close,master); self.addCleanup(os.close,slave)
        cfg=config('ModbusRtu',Device=path)
        channel=w.Channel(cfg); self.addCleanup(channel.close)
        errors=[]
        def peer():
            try:
                data=os.read(master,256)
                self.assertEqual(data,bytes.fromhex('010300000002c40b'))
                os.write(master,bytes.fromhex('01030400120034'))
                # Independent golden response CRC computed outside worker.
                os.write(master,bytes.fromhex('5be1'))
            except BaseException as error: errors.append(error)
        thread=threading.Thread(target=peer,daemon=True); thread.start()
        result=channel.execute(command())
        thread.join(1)
        if errors: raise errors[0]
        self.assertEqual(result,(bytes.fromhex('00120034'),True))
        self.assertEqual(channel.execute(command(request=2,function=6,unit=0,quantity=1,data=b'\x00\x01')),(b'',False))
        self.assertEqual(os.read(master,256)[:6],bytes.fromhex('000600000001'))

    def test_bad_rtu_crc_poison_and_kernel485_unsupported(self):
        import pty
        master,slave=pty.openpty(); path=os.ttyname(slave)
        self.addCleanup(os.close,master); self.addCleanup(os.close,slave)
        channel=w.Channel(config('ModbusRtu',Device=path)); self.addCleanup(channel.close)
        def peer():
            os.read(master,256); os.write(master,bytes.fromhex('010304000100020000'))
        threading.Thread(target=peer,daemon=True).start()
        with self.assertRaises(w.CommunicationError) as error: channel.execute(command())
        self.assertEqual(error.exception.code,6005); self.assertTrue(channel.poisoned)
        channel.close()
        master2,slave2=pty.openpty(); path2=os.ttyname(slave2)
        self.addCleanup(os.close,master2); self.addCleanup(os.close,slave2)
        unsupported=w.Channel(config('Serial',Device=path2,Physical='RS485',Direction='KernelRts'))
        self.addCleanup(unsupported.close)
        with self.assertRaises(w.CommunicationError) as error: unsupported.execute(command(action=3,data=b'x'))
        self.assertEqual(error.exception.code,6004)

    def test_serial_length16_and_close_cancels_read(self):
        import pty
        master,slave=pty.openpty(); path=os.ttyname(slave)
        self.addCleanup(os.close,master); self.addCleanup(os.close,slave)
        channel=w.Channel(dict(config('Serial',Device=path),Framing='Length16'))
        self.addCleanup(channel.close)
        channel.execute(command(action=3,data=b'A\x00B'))
        self.assertEqual(os.read(master,256),b'\x00\x03A\x00B')
        os.write(master,b'\x00\x03x\x00y\x00\x01z')
        self.assertEqual(channel.execute(command(action=4)),(b'x\x00y',False))
        self.assertEqual(channel.execute(command(action=4)),(b'z',False))
        errors=[]
        def read():
            try: channel.execute(command(action=4))
            except w.CommunicationError as error: errors.append(error)
        thread=threading.Thread(target=read,daemon=True); thread.start()
        time.sleep(.02); channel.close(); thread.join(.1)
        self.assertFalse(thread.is_alive()); self.assertTrue(errors)

    def test_unix_images_watchdog_partial_input_and_eof(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=pathlib.Path(temporary); private=root/'private'; path=private/'worker.sock'
            manifest=root/'manifest.json'
            manifest.write_text(json.dumps(dict(SchemaVersion=1,InterfaceVersion=w.INTERFACE,
                PlcIntervalMilliseconds=10,Channels=[dict(Index=1,Configuration=config('Tcp'))])))
            process=subprocess.Popen([sys.executable,str(pathlib.Path(w.__file__)), '--manifest',str(manifest),'--socket',str(path)],stderr=subprocess.PIPE)
            try:
                for _ in range(100):
                    if path.exists(): break
                    if process.poll() is not None: self.fail(process.stderr.read().decode())
                    time.sleep(.01)
                self.assertEqual(private.stat().st_mode&0o777,0o700)
                self.assertEqual(path.stat().st_mode&0o777,0o600)
                with socket.socket(socket.AF_UNIX) as client:
                    client.settimeout(1); client.connect(str(path))
                    frame=struct.pack('<QQ',1,1)+bytes(9344)
                    client.sendall(frame[:17]); time.sleep(.02); client.sendall(frame[17:])
                    data=b''
                    while len(data)<w.OUTPUT_SIZE: data+=client.recv(w.OUTPUT_SIZE-len(data))
                    self.assertEqual(len(data),9088)
                    client.sendall(b'\x00')
                    time.sleep(.4)
                    cap=json.loads(pathlib.Path(str(path)+'.capabilities.json').read_text())
                    self.assertFalse(cap['ScanHealthy'])
                    self.assertEqual(cap['LastScan'],1)
            finally:
                process.terminate(); process.wait(timeout=2); process.stderr.close()


if __name__ == '__main__': unittest.main()
