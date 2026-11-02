"""Native wire protocol faults must terminate a channel without replaying commands."""
import json,struct,socket,unittest
import axis_worker as v1
import axis_worker_v2 as v2

class Stream:
    def __init__(self, response=None):
        self.response=response;self.buffer=b'';self.sent=[];self.closed=False
    def sendall(self, data):
        self.sent.append(data)
        request=json.loads(data[4:]);reply=dict(protocolVersion=4,kind='CommandResponse',requestId=request['requestId'],sequence=request['sequence'],payload={'accepted':True})
        if self.response:reply=self.response(request,reply)
        raw=reply if isinstance(reply,bytes) else json.dumps(reply).encode()
        self.buffer=struct.pack('<I',len(raw))+raw
    def recv(self,size):
        if self.response=='timeout':raise socket.timeout('injected timeout')
        result,self.buffer=self.buffer[:size],self.buffer[size:];return result
    def close(self):self.closed=True

class ProtocolRecoveryTests(unittest.TestCase):
    def test_fault_closes_connection_and_next_command_cannot_send(self):
        mutations=[lambda q,r:dict(r,requestId='wrong'),lambda q,r:dict(r,sequence=True),
                   lambda q,r:b'{"protocolVersion":4,"protocolVersion":4}',
                   lambda q,r:b'not JSON']
        for module in (v1,v2):
            for mutate in mutations:
                with self.subTest(module=module.__name__,fault=mutate):
                    stream=Stream(mutate);client=module.ProtocolClient(stream)
                    with self.assertRaises(module.ProtocolError):client.request('AxisCommandRequest',{'command':'Stop'})
                    self.assertTrue(stream.closed)
                    with self.assertRaises(module.ProtocolError):client.request('AxisCommandRequest',{'command':'Stop'})
                    self.assertEqual(1,len(stream.sent),'unknown motion must never be replayed')
    def test_timeout_and_truncation_close_and_latch_first_cause(self):
        for module in (v1,v2):
            for cause in ('timeout','eof'):
                with self.subTest(module=module.__name__,cause=cause):
                    stream=Stream()
                    def receive(size):
                        if cause=='timeout':raise socket.timeout('secret must not enter diagnostics')
                        return b''
                    stream.recv=receive;client=module.ProtocolClient(stream)
                    with self.assertRaises((module.ProtocolError,OSError)):client.request('AxisCommandRequest',{'command':'Jog'})
                    self.assertTrue(stream.closed)
                    before=client.diagnostics()
                    self.assertEqual('Disconnected',before['State'])
                    self.assertNotIn('secret',json.dumps(before))
                    with self.assertRaises(module.ProtocolError):client.request('ReadSnapshotRequest',{})
                    self.assertEqual(before,client.diagnostics())
                    self.assertEqual(1,len(stream.sent))
    def test_valid_business_rejection_does_not_poison_connection(self):
        for module in (v1,v2):
            def rejection(q,r):return dict(r,kind='ErrorResponse',payload={'code':'invalid_state','message':'not stationary'})
            stream=Stream(rejection);client=module.ProtocolClient(stream)
            with self.assertRaises(module.BackendError):client.request('AxisCommandRequest',{'command':'Home'})
            self.assertFalse(stream.closed)
            stream.response=lambda q,r:dict(r,kind='SnapshotResponse',payload={'runtimeReady':True})
            self.assertTrue(client.request('ReadSnapshotRequest',{},'SnapshotResponse')['runtimeReady'])
            self.assertEqual('Connected',client.diagnostics()['State'])
            self.assertEqual(1,client.diagnostics()['Requests']['AxisCommandRequest']['Failures'])
    def test_local_invalid_payload_does_not_send_or_close(self):
        for module in (v1,v2):
            stream=Stream();client=module.ProtocolClient(stream)
            with self.assertRaises(ValueError):client.request('AxisCommandRequest',{'position':float('nan')})
            self.assertFalse(stream.closed);self.assertFalse(stream.sent)
            self.assertTrue(client.request('AxisCommandRequest',{'command':'Stop'})['accepted'])
    def test_missing_or_nonboolean_command_receipt_is_unknown_not_rejected(self):
        for module in (v1,v2):
            for payload in ({},{'accepted':'true'},{'accepted':1}):
                with self.subTest(module=module.__name__,payload=payload):
                    stream=Stream(lambda q,r:dict(r,payload=payload));client=module.ProtocolClient(stream)
                    with self.assertRaises(module.ProtocolError):client.request('AxisCommandRequest',{'command':'Stop'})
                    self.assertTrue(stream.closed)
                    self.assertEqual(1,len(stream.sent))
    def test_valid_duplicate_envelope_is_rejected(self):
        for module in (v1,v2):
            stream=Stream(lambda q,r:(json.dumps(r)[:-1]+',"sequence":'+str(q['sequence'])+'}').encode())
            with self.assertRaises(module.ProtocolError):module.ProtocolClient(stream).request('AxisCommandRequest',{})
            self.assertTrue(stream.closed)
    def test_metrics_request_cardinality_is_bounded(self):
        for module in (v1,v2):
            client=module.ProtocolClient(Stream())
            for n in range(100):client.request('Unexpected'+str(n),{})
            self.assertEqual({'Other'},set(client.diagnostics()['Requests']))

    def test_excessive_nesting_or_overflow_number_invalidates_transport(self):
        for module in (v1,v2):
            for mutate in (lambda q,r:b'['*1100+b'0'+b']'*1100,
                lambda q,r:(json.dumps(r)[:-1]+',"extra":1e9999}').encode(),
                lambda q,r:(json.dumps(r)[:-1]+',"extra":'+('9'*10000)+'}').encode()):
                with self.subTest(module=module.__name__,fault=mutate):
                    stream=Stream(mutate);client=module.ProtocolClient(stream)
                    with self.assertRaises(module.ProtocolError):client.request('AxisCommandRequest',{})
                    self.assertTrue(stream.closed)
                    with self.assertRaises(module.ProtocolError):client.request('ReadSnapshotRequest',{})
                    self.assertEqual(1,len(stream.sent))

    def test_timing_counts_bounded_request_labels(self):
        for module in (v1,v2):
            times=iter([1.,1.025,2.,2.010]);client=module.ProtocolClient(Stream(),now=lambda:next(times))
            client.request('AxisCommandRequest',{});client.request('AxisCommandRequest',{})
            stats=client.diagnostics()['Requests']['AxisCommandRequest']
            self.assertEqual((2,0),(stats['Count'],stats['Failures']))
            self.assertAlmostEqual(25.,stats['MaxMilliseconds']);self.assertAlmostEqual(17.5,stats['MeanMilliseconds'])

if __name__=='__main__':unittest.main()
