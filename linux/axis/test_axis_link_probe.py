import unittest
from unittest.mock import patch
import axis_link_probe as probe

class ReadOnlyFake:
 def __init__(self):self.kinds=[];self.cycle=10
 def request(self,kind,payload,expected):
  self.kinds.append(kind)
  if kind=='HelloRequest':return {'protocolVersion':4,'runtimeReady':True}
  if kind!='ReadSnapshotRequest':raise AssertionError('Probe attempted control')
  self.cycle+=1
  return {'runtimeReady':True,'ioReady':True,'motionReady':False,'sampleCycle':self.cycle,'sampleMonotonicNs':self.cycle*1000,'axes':[{'fault':'existing drive fault'}]}

class ProbeTests(unittest.TestCase):
 def test_probe_is_read_only_and_fault_does_not_break_read_link(self):
  client=ReadOnlyFake();result=probe.measure(client,3,1,sleep=lambda t:None,now_ns=lambda:100000)
  self.assertEqual(['HelloRequest']+['ReadSnapshotRequest']*3,client.kinds)
  self.assertEqual('PASS',result['Status']);self.assertFalse(result['MotionReady']);self.assertEqual(1,result['AxisFaultCount'])
  self.assertEqual('read_only_native_round_trip',result['Measurement'])
 def test_bad_capacity_rejects_before_first_request(self):
  for samples,interval in [(1,1),(1001,1),(2,0),(1000,1000)]:
   client=ReadOnlyFake()
   with self.assertRaises(ValueError):probe.measure(client,samples,interval)
   self.assertEqual([],client.kinds)
 def test_frozen_or_regressed_feedback_is_not_a_pass(self):
  for mode in ('frozen','regressed','future'):
   client=ReadOnlyFake();original=client.request
   def request(kind,payload,expected):
    result=original(kind,payload,expected)
    if kind=='ReadSnapshotRequest':
     result['sampleCycle']=10 if mode=='frozen' else 30-client.cycle if mode=='regressed' else client.cycle
     result['sampleMonotonicNs']=200000 if mode=='future' else result['sampleCycle']*1000
    return result
   client.request=request
   with self.subTest(mode=mode),self.assertRaises(ValueError):probe.measure(client,3,1,sleep=lambda t:None,now_ns=lambda:100000)

if __name__=='__main__':unittest.main()
