import unittest
import io,json
from unittest.mock import patch
import axis_worker as v1
import axis_worker_v2 as v2
from test_axis_worker import Fake,manifest

class RecoveryDiagnosticsTests(unittest.TestCase):
 def test_metrics_do_not_cause_high_frequency_capability_writes(self):
  for module in (v1,v2):
   clock=[0.]
   with patch.object(module,'publish_capabilities') as publish:
    writer=module.CapabilityPublisher('/private/cap.json',now=lambda:clock[0])
    for count in range(100):
     clock[0]=count/100
     writer(dict(ControllerReady=True,FailureCode=0,LinkMetrics={'Count':count}))
    self.assertEqual(1,publish.call_count)
    clock[0]=1.;writer(dict(ControllerReady=True,FailureCode=0,LinkMetrics={'Count':100}))
    self.assertEqual(2,publish.call_count)
    writer(dict(ControllerReady=False,FailureCode=2004,LinkMetrics={'Count':101}))
    self.assertEqual(3,publish.call_count)
 def test_failure_requires_explicit_channel_reset_and_keeps_first_reason(self):
  for module in (v1,v2):
   document=manifest()
   if module is v2:
    document.update(InterfaceVersion=module.INTERFACE,CommandBytes=4112,FeedbackBytes=6656,RequiredRuntimeCapabilities=[module.MOVE_CAPABILITY])
    document['Axes'][0].update(MaximumAcceleration=1,MaximumDeceleration=1,MaximumJerk=0)
   worker=module.Worker(module.Manifest.from_object(document),Fake(),'EcMotionSimulator')
   worker.shutdown(2004)
   worker.shutdown(2002)
   cap=worker.capabilities()
   self.assertTrue(cap['RecoveryRequired']);self.assertEqual(2004,cap['FailureCode'])
   self.assertEqual('stop_plc_and_reestablish_channel',cap['RecoveryAction'])
   self.assertFalse(cap['ControllerReady'])

 def test_terminal_failure_has_one_sanitized_recovery_log(self):
  for module in (v1,v2):
   document=manifest()
   if module is v2:
    document.update(InterfaceVersion=module.INTERFACE,CommandBytes=4112,FeedbackBytes=6656,RequiredRuntimeCapabilities=[module.MOVE_CAPABILITY])
    document['Axes'][0].update(MaximumAcceleration=1,MaximumDeceleration=1,MaximumJerk=0)
   worker=module.Worker(module.Manifest.from_object(document),Fake(),'EcMotionSimulator')
   output=io.StringIO()
   with patch.object(module.sys,'stderr',output):worker.shutdown(2004);worker.shutdown(2002)
   lines=output.getvalue().splitlines();self.assertEqual(1,len(lines))
   event=json.loads(lines[0]);self.assertEqual('axis_channel_failed',event['event'])
   self.assertEqual(2004,event['code']);self.assertNotIn('lease',event)

 def test_expected_session_end_can_release_without_false_failure_log(self):
  for module in (v1,v2):
   document=manifest()
   if module is v2:
    document.update(InterfaceVersion=module.INTERFACE,CommandBytes=4112,FeedbackBytes=6656,RequiredRuntimeCapabilities=[module.MOVE_CAPABILITY])
    document['Axes'][0].update(MaximumAcceleration=1,MaximumDeceleration=1,MaximumJerk=0)
   worker=module.Worker(module.Manifest.from_object(document),Fake(),'EcMotionSimulator');output=io.StringIO()
   with patch.object(module.sys,'stderr',output):worker.shutdown(report=False)
   self.assertEqual('',output.getvalue());self.assertIsNone(worker.lease)

 def test_drive_fault_keeps_read_link_available(self):
  for module in (v1,v2):
   document=manifest()
   if module is v2:
    document.update(InterfaceVersion=module.INTERFACE,CommandBytes=4112,FeedbackBytes=6656,RequiredRuntimeCapabilities=[module.MOVE_CAPABILITY])
    document['Axes'][0].update(MaximumAcceleration=1,MaximumDeceleration=1,MaximumJerk=0)
   backend=Fake();original=backend.request
   def request(kind,payload,expected=None):
    response=original(kind,payload,expected)
    if kind=='HelloRequest':response['runtimeCapabilities']=['workbench_axis_move_profile_v2']
    return response
   backend.request=request
   worker=module.Worker(module.Manifest.from_object(document),backend,'EcMotionSimulator');worker.probe()
   backend.axis.update(state='ErrorStop',fault='existing-drive-fault')
   worker.poll();cap=worker.capabilities()
   self.assertTrue(cap['ControllerReady']);self.assertFalse(cap['MotionReady']);self.assertFalse(cap['RecoveryRequired'])
   self.assertEqual(0,cap['FailureCode'])

if __name__=='__main__':unittest.main()
