import unittest
from unittest import mock
import axis_worker as w

class CapabilityHeartbeatTests(unittest.TestCase):
 def test_same_state_is_bounded_but_revocation_is_immediate(self):
  clock=[0.0]
  with mock.patch.object(w,'publish_capabilities') as write:
   publish=w.CapabilityPublisher('/private/cap.json',now=lambda:clock[0])
   ready=dict(ControllerReady=True,PhysicalBasicAxis=True)
   publish(ready)
   for n in range(1,100):
    clock[0]=n/100;publish(dict(ready))
   self.assertEqual(write.call_count,1)
   clock[0]=1.0;publish(ready)
   self.assertEqual(write.call_count,2)
   publish(dict(ControllerReady=False,PhysicalBasicAxis=False))
   self.assertEqual(write.call_count,3)
   self.assertFalse(write.call_args.args[1]['ControllerReady'])

if __name__=='__main__':unittest.main()
