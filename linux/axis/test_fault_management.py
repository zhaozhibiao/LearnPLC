import unittest
import axis_worker as w
from test_axis_worker import Fake,manifest,packet

class FaultManagementTests(unittest.TestCase):
 def test_fault_keeps_monitoring_and_rejects_motion_before_lease(self):
  fake=Fake(physical=True);fake.axis.update(state='ErrorStop',fault='overspeed')
  worker=w.Worker(w.Manifest.from_object(manifest()),fake,'EcMotion')
  worker.probe()
  self.assertTrue(worker.capabilities()['ControllerReady'])
  for scan,kind in enumerate((1,4,5,6),1):
   worker.scan(packet(scan=scan,request=scan,kind=kind))
   self.assertEqual(worker.slots[0].error,2005)
  self.assertFalse(any(k=='AcquireControlRequest' for k,_ in fake.calls))
  self.assertEqual(worker.failed,0)
  self.assertTrue(worker.capabilities()['PhysicalBasicAxis'])
  self.assertFalse(worker.capabilities()['MotionReady'])
  fake.axis.update(state='Disabled',fault=None)
  worker.poll()
  self.assertTrue(worker.capabilities()['ControllerReady'])
  self.assertTrue(worker.capabilities()['MotionReady'])

 def test_fault_after_accepted_move_keeps_actual_fault_readable(self):
  fake=Fake(physical=True)
  worker=w.Worker(w.Manifest.from_object(manifest()),fake,'EcMotion')
  worker.probe();worker.scan(packet(kind=5))
  fake.axis.update(state='ErrorStop',fault='following error',motionAborted=True)
  worker.poll()
  self.assertEqual(worker.failed,0)
  self.assertEqual(worker.slots[0].error,2005)
  self.assertFalse(worker.slots[0].done)
  self.assertEqual(worker.slots[0].sample['fault'],'following error')
  self.assertTrue(worker.capabilities()['PhysicalBasicAxis'])
  self.assertFalse(worker.capabilities()['MotionReady'])

 def test_explicit_backend_rejection_does_not_kill_management_or_retry(self):
  class Reject(Fake):
   def request(self,kind,payload,expected=None):
    if kind=='AxisCommandRequest' and payload.get('command')=='Enable':
     self.calls.append((kind,payload.copy()))
     raise w.BackendError('invalid_state')
    return super().request(kind,payload,expected)
  fake=Reject(physical=True)
  worker=w.Worker(w.Manifest.from_object(manifest()),fake,'EcMotion')
  worker.probe();worker.scan(packet(kind=1))
  self.assertEqual(worker.slots[0].error,2005)
  self.assertEqual(worker.failed,0)
  self.assertTrue(worker.capabilities()['ControllerReady'])
  worker.scan(packet(scan=2,kind=1))
  self.assertEqual(sum(k=='AxisCommandRequest' and p.get('command')=='Enable' for k,p in fake.calls),1)
  worker.scan(packet(scan=3,request=2,kind=3))
  self.assertTrue(any(k=='AxisCommandRequest' and p.get('command')=='Reset' for k,p in fake.calls))

if __name__=='__main__': unittest.main()
