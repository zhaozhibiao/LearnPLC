import json,pathlib,tempfile,unittest
from unittest.mock import Mock,patch
import axis_recovery as recovery

class RecoveryTests(unittest.TestCase):
 def client(self):
  c=Mock();c.active.return_value={'build_hash':'A'*64,'library_sha256':'b'*64};c.config={};c.transaction_path.exists.return_value=False
  c.connection.return_value.__enter__=Mock(return_value=Mock(status=Mock(return_value='STOPPED')))
  c.connection.return_value.__exit__=Mock(return_value=None);c.native_core_pid.return_value=17
  return c
 def test_recovery_does_not_start_plc_or_change_program(self):
  c=self.client()
  with patch.object(recovery,'admit_resources',return_value=pathlib.Path('unused')),patch.object(recovery,'verify_quiescent'):
   result=recovery.recover_active(c,'A'*64)
  self.assertEqual('STOPPED',result['plc_state']);self.assertFalse(result['motion_restarted'])
  c.service.assert_called_once_with('stop');c.resume_service.assert_called_once()
  c.connection.return_value.__enter__.return_value.transition.assert_not_called()
 def test_running_plc_rejected_before_any_service_or_native_change(self):
  c=self.client();c.connection.return_value.__enter__.return_value.status.return_value='RUNNING'
  with patch.object(recovery,'admit_resources') as admit,self.assertRaisesRegex(ValueError,'STOPPED'):
   recovery.recover_active(c,'A'*64)
  c.service.assert_not_called();admit.assert_not_called()
 def test_identity_or_stationary_failure_does_not_stop_service(self):
  for fault in ('identity','stationary'):
   c=self.client()
   if fault=='identity':c.verify_active.side_effect=ValueError('identity changed')
   with patch.object(recovery,'admit_resources',return_value=pathlib.Path('unused')),patch.object(recovery,'verify_quiescent',side_effect=ValueError('not stationary') if fault=='stationary' else None),self.assertRaises(ValueError):
    recovery.recover_active(c,'A'*64)
   c.service.assert_not_called()
 def test_native_identity_change_aborts_before_resume(self):
  c=self.client();c.native_core_pid.side_effect=[17,18]
  with patch.object(recovery,'admit_resources',return_value=pathlib.Path('unused')),patch.object(recovery,'verify_quiescent'),self.assertRaisesRegex(ValueError,'core identity'):
   recovery.recover_active(c,'A'*64)
  c.resume_service.assert_not_called()
 def test_resume_failure_does_not_retry_or_start(self):
  c=self.client();c.resume_service.side_effect=ValueError('unconfirmed')
  with patch.object(recovery,'admit_resources',return_value=pathlib.Path('unused')),patch.object(recovery,'verify_quiescent'),self.assertRaises(ValueError):recovery.recover_active(c,'A'*64)
  c.resume_service.assert_called_once();c.connection.return_value.__enter__.return_value.transition.assert_not_called()
 def test_changed_active_record_aborts_without_restarting_or_starting_plc(self):
  c=self.client();initial=c.active.return_value;c.active.side_effect=[initial,dict(initial,build_hash='C'*64)]
  with patch.object(recovery,'admit_resources',return_value=pathlib.Path('unused')),patch.object(recovery,'verify_quiescent'),self.assertRaisesRegex(ValueError,'Active program changed'):
   recovery.recover_active(c,'A'*64)
  c.resume_service.assert_not_called();c.service.assert_not_called()
 def test_pending_transaction_is_not_treated_as_a_channel_recovery(self):
  with tempfile.TemporaryDirectory() as directory:
   c=self.client();c.transaction_path=pathlib.Path(directory)/'transaction.json';c.transaction_path.write_text('{"state":"prepared"}')
   with patch.object(recovery,'admit_resources') as admit,self.assertRaisesRegex(ValueError,'transaction'):
    recovery.recover_active(c,'A'*64)
   admit.assert_not_called();c.service.assert_not_called()
 def test_resource_mismatch_is_rejected_before_native_or_service_changes(self):
  from test_axis_gate_v2 import manifest
  with tempfile.TemporaryDirectory() as directory:
   generated=pathlib.Path(directory);raw=json.dumps(manifest()).encode();(generated/'axis-manifest.json').write_bytes(raw)
   installed=generated/'installed-manifest.json';installed.write_bytes(b'wrong mapping')
   c=self.client()
   with patch.object(recovery.configuration,'candidate',return_value=(generated,{'AxisInterfaceVersion':'workbench-axis-v2'},{'library_sha256':'b'*64})),patch.object(recovery.configuration,'validate_config'),patch.object(recovery.configuration,'manifest_destination',return_value=installed),patch.object(recovery.hw,'verify_applied') as native:
    with self.assertRaisesRegex(ValueError,'mapping'):recovery.admit_resources(c,c.active())
    native.assert_not_called();c.service.assert_not_called()
 def test_final_state_is_proven_quiescent_after_resume(self):
  c=self.client()
  with patch.object(recovery,'admit_resources',return_value=pathlib.Path('unused')),patch.object(recovery,'verify_quiescent',side_effect=[None,None,ValueError('post-resume axis not disabled')]) as proof:
   with self.assertRaisesRegex(ValueError,'post-resume'):recovery.recover_active(c,'A'*64)
  self.assertEqual(3,proof.call_count);c.resume_service.assert_called_once()
  c.connection.return_value.__enter__.return_value.transition.assert_not_called()
 def test_faulted_but_disabled_stable_axis_allowed_enabled_or_frozen_rejected(self):
  for mode in ('faulted','enabled','frozen','stale','future','moving','busy','velocity-policy'):
   with self.subTest(mode=mode),tempfile.TemporaryDirectory() as directory:
    generated=pathlib.Path(directory)
    (generated/'machine-config.json').write_text(json.dumps({'axes':[{'name':'Axis1','motionCompletion':{'positionTolerance':.0001,'velocityTolerance':.0005,'dwellMilliseconds':100,'positionStabilityCompletion':mode!='velocity-policy'}}]}))
    clock=[0.];cycle=[0]
    def read(*args):
     cycle[0]+=1;stamp=1000000000+(0 if mode=='frozen' else cycle[0]*50000000)
     if mode=='stale':stamp-=1000000000
     if mode=='future':stamp+=1000000000
     return dict(runtimeReady=True,ioReady=True,outputsAtConfiguredSafeState=True,sampleCycle=1 if mode=='frozen' else cycle[0],sampleMonotonicNs=stamp,
      axes=[dict(name='Axis1',state='ErrorStop',fault='existing drive fault',driveStatusword=0x0027 if mode=='enabled' else 0x0008,motionBusy=mode=='busy',actualPosition=cycle[0]*.001 if mode=='moving' else 0.,actualVelocity=.015)])
    def sleep(seconds):clock[0]+=.05
    with patch.object(recovery.hw,'native_request',side_effect=read),patch.object(recovery.time,'monotonic_ns',side_effect=lambda:1000000000+cycle[0]*50000000+1000000):
     if mode=='faulted':recovery.verify_quiescent(generated,{},now=lambda:clock[0],sleep=sleep)
     else:
      with self.assertRaises(ValueError):recovery.verify_quiescent(generated,{},now=lambda:clock[0],sleep=sleep)

if __name__=='__main__':unittest.main()
