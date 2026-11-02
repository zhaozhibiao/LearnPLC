"""Real socket fault injection against an isolated fake native core; never hardware."""
import json,socket,struct,threading,time,unittest
import axis_worker as v1
import axis_worker_v2 as v2

class SocketRecoveryTests(unittest.TestCase):
 def test_ambiguous_native_reply_closes_peer_and_cannot_replay(self):
  for module in (v1,v2):
   for fault in ('truncated','wrong_identity','timeout'):
    with self.subTest(module=module.__name__,fault=fault):
     local,peer=socket.socketpair();local.settimeout(.1);peer.settimeout(1)
     seen=[];closed=[];errors=[]
     def native():
      try:
       size=struct.unpack('<I',module.read_exact(peer,4))[0];request=json.loads(module.read_exact(peer,size));seen.append(request['kind'])
       response=dict(protocolVersion=4,requestId=request['requestId'],sequence=request['sequence'],kind='CommandResponse',payload={'accepted':True})
       if fault=='timeout':time.sleep(.15)
       elif fault=='truncated':peer.sendall(struct.pack('<I',99)+b'{');peer.shutdown(socket.SHUT_WR)
       else:
        response['requestId']='wrong';raw=json.dumps(response).encode();peer.sendall(struct.pack('<I',len(raw))+raw)
       closed.append(peer.recv(4096)==b'')
      except Exception as error:errors.append(error)
      finally:peer.close()
     thread=threading.Thread(target=native);thread.start()
     try:
      client=module.ProtocolClient(local)
      with self.assertRaises(module.ProtocolError):client.request('AxisCommandRequest',{'command':'MoveAbsolute'})
      with self.assertRaises(module.ProtocolError):client.request('AxisCommandRequest',{'command':'MoveAbsolute'})
     finally:local.close();thread.join(2)
     self.assertFalse(thread.is_alive());self.assertEqual([],errors);self.assertEqual([True],closed)
     self.assertEqual(['AxisCommandRequest'],seen)
 def test_slow_partial_frame_cannot_extend_the_request_deadline(self):
  for module in (v1,v2):
   local,peer=socket.socketpair();local.settimeout(.08);peer.settimeout(.5)
   def native():
    try:
     size=struct.unpack('<I',module.read_exact(peer,4))[0];module.read_exact(peer,size)
     for byte in struct.pack('<I',99):
      time.sleep(.03);peer.sendall(bytes([byte]))
    except OSError:pass
    finally:peer.close()
   thread=threading.Thread(target=native);thread.start();client=module.ProtocolClient(local)
   try:
    with self.assertRaises(module.ProtocolError):client.request('ReadSnapshotRequest',{},'SnapshotResponse')
    self.assertEqual('transport_timeout',client.diagnostics()['FailureReason'])
   finally:local.close();thread.join(2)
   self.assertFalse(thread.is_alive())

 def test_new_explicit_read_only_session_has_fresh_sequence(self):
  for module in (v1,v2):
   local,peer=socket.socketpair();local.settimeout(.5);peer.settimeout(.5);seen=[]
   def native():
    try:
     size=struct.unpack('<I',module.read_exact(peer,4))[0];request=json.loads(module.read_exact(peer,size));seen.append(request)
     raw=json.dumps(dict(protocolVersion=4,requestId=request['requestId'],sequence=request['sequence'],kind='SnapshotResponse',payload={'runtimeReady':True})).encode()
     peer.sendall(struct.pack('<I',len(raw))+raw)
    finally:peer.close()
   thread=threading.Thread(target=native);thread.start()
   try:self.assertTrue(module.ProtocolClient(local).request('ReadSnapshotRequest',{},'SnapshotResponse')['runtimeReady'])
   finally:local.close();thread.join(2)
   self.assertEqual(['ReadSnapshotRequest'],[q['kind'] for q in seen]);self.assertEqual(1,seen[0]['sequence'])

if __name__=='__main__':unittest.main()
