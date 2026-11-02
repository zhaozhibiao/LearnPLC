import unittest
import axis_worker_v2 as w
from test_axis_worker import Fake, manifest
class V2Tests(unittest.TestCase):
 def worker(self):
  m=manifest();m.update(InterfaceVersion='workbench-axis-v2',CommandBytes=4112,FeedbackBytes=6656,RequiredRuntimeCapabilities=['workbench_axis_move_profile_v2'])
  m['Axes'][0].update(MaximumAcceleration=10.,MaximumDeceleration=11.,MaximumJerk=12.)
  f=Fake();original=f.request
  def request(k,p,e=None):
   r=original(k,p,e)
   if k=='HelloRequest':r['runtimeCapabilities']=['workbench_axis_move_profile_v2','workbench_axis_velocity_profile_v2']
   return r
  f.request=request
  a=w.Worker(w.Manifest.from_object(m),f,'EcMotionSimulator');a.probe();return a,f
 def packet(self,req=1,a=0,d=0,j=0,kind=5):
  return w.HEADER.pack(req,1)+w.COMMAND.pack(req,kind,2.,1.,a,d,j,0)+bytes(63*w.COMMAND.size)
 def test_local_rejections_keep_active_motion_and_report_separate_receipt(self):
  for kind,acceleration,expected in ((5,-1,2006),(7,-1,2007)):
   a,f=self.worker();a.scan(self.packet());previous=a.slots[0]
   data=self.packet(req=2,a=acceleration,kind=kind);a.scan(data);a.scan(w.HEADER.pack(3,1)+data[16:])
   self.assertEqual((1,41,True),(a.slots[0].request,a.slots[0].motion_id,a.slots[0].busy))
   self.assertEqual((2,expected),w.FEEDBACK.unpack_from(a.feedback())[-2:])
   self.assertEqual(1,sum(k=='AxisCommandRequest' for k,p in f.calls))
   f.axis.update(motionId=41,motionBusy=False,motionDone=True);a.poll();self.assertTrue(a.slots[0].done)
 def test_zero_jog_stop_inherit_positive_defaults_and_require_velocity_capability(self):
  for kind in (6,7):
   a,f=self.worker();a.scan(self.packet(kind=kind))
   command=[p for k,p in f.calls if k=='AxisCommandRequest'][-1]
   self.assertEqual((2,11.,12.),tuple(command[k] for k in ('motionProfileVersion','deceleration','jerk')))
   if kind==6:self.assertEqual(10.,command['acceleration'])
   a,f=self.worker();a.hello['runtimeCapabilities'].remove(w.VELOCITY_CAPABILITY)
   a.scan(self.packet(kind=kind));self.assertEqual(2008,a.slots[0].rejected_error)
   self.assertFalse(any(k=='AxisCommandRequest' for k,p in f.calls))
   a,f=self.worker();a.scan(self.packet());a.hello['runtimeCapabilities'].remove(w.VELOCITY_CAPABILITY)
   a.scan(self.packet(req=2,kind=kind));self.assertEqual(2008 if kind==7 else 2006,a.slots[0].rejected_error)
   self.assertEqual((1,41,True),(a.slots[0].request,a.slots[0].motion_id,a.slots[0].busy))
   self.assertEqual(1,sum(k=='AxisCommandRequest' for k,p in f.calls))
 def test_native_json_shape_omits_nonapplicable_position_velocity(self):
  for kind,expected in ((5,{'position','velocity'}),(6,{'velocity'}),(7,set())):
   with self.subTest(kind=kind):
    a,f=self.worker();original=f.request
    def strict_native(k,p,e=None):
     if k=='AxisCommandRequest':
      # Native JSON numeric accessors reject explicit null, even for unused fields.
      self.assertTrue(all(v is not None for v in p.values()))
      self.assertEqual(expected,set(p)&{'position','velocity'})
     return original(k,p,e)
    f.request=strict_native;a.scan(self.packet(kind=kind))
    self.assertFalse(a.failed);self.assertTrue(a.slots[0].accepted)
    command=[p for k,p in f.calls if k=='AxisCommandRequest'][-1]
    self.assertEqual(2,command['motionProfileVersion']);self.assertEqual(12.,command['jerk'])
    self.assertFalse(any(k=='ReleaseControlRequest' for k,p in f.calls))
 def test_dimensions_and_inheritance(self):
  a,f=self.worker();self.assertEqual(4112,len(self.packet()));a.scan(self.packet())
  p=[p for k,p in f.calls if k=='AxisCommandRequest'][-1]
  self.assertEqual((2,10.,11.,12.),tuple(p[k] for k in ('motionProfileVersion','acceleration','deceleration','jerk')))
 def test_invalid_never_submits(self):
  for v in (-1.,float('nan'),float('inf'),11.):
   a,f=self.worker();a.scan(self.packet(a=v));self.assertEqual(2007,a.slots[0].rejected_error);self.assertFalse(any(k=='AxisCommandRequest' for k,p in f.calls))
 def test_same_request_immutable(self):
  a,f=self.worker();a.scan(self.packet(a=2));a.scan(self.packet(a=3));self.assertEqual(1,sum(k=='AxisCommandRequest' for k,p in f.calls))
 def test_missing_native_capability_fails_probe(self):
  a,f=self.worker();f.request=lambda k,p,e=None: dict(protocolVersion=4,capabilities=[])
  with self.assertRaises(w.ProtocolError):a.probe()
 def test_old_image_rejected(self):
  a,f=self.worker()
  with self.assertRaises(w.ProtocolError):a.scan(bytes(2576))
 def test_velocity_dynamic_capability_required(self):
  a,f=self.worker();a.hello['runtimeCapabilities'].remove(w.VELOCITY_CAPABILITY);a.scan(self.packet(a=2,kind=6));self.assertEqual(2008,a.slots[0].rejected_error)
 def test_invalid_request_does_not_release_held_stop(self):
  a,f=self.worker();a.slots[0].stop_held=True
  a.scan(self.packet(a=-1,kind=7));self.assertTrue(a.slots[0].stop_held)
 def test_all_slots_and_request_overflow_boundary(self):
  a,f=self.worker()
  data=w.HEADER.pack(1,1)+b''.join(w.COMMAND.pack(2**64-1,1,0,0,0,0,0,0) for _ in range(64))
  a.scan(data)
  self.assertTrue(all(slot.last_seen_request==2**64-1 for slot in a.slots))
  self.assertTrue(all(slot.rejected_error==2001 for slot in a.slots[1:]))
  a.scan(w.HEADER.pack(2,1)+w.COMMAND.pack(1,1,0,0,0,0,0,0)+bytes(63*w.COMMAND.size))
  self.assertEqual(2009,a.slots[0].rejected_error)
 def test_watchdog_duplicate_cannot_extend(self):
  a,f=self.worker();now=[1.];a.now=lambda:now[0];a.scan(self.packet());now[0]+=1.;a.scan(self.packet());self.assertEqual(2002,a.failed)
 def test_jerk_zero_is_explicit_legacy_default(self):
  a,f=self.worker();m=dict(a.manifest.document);m['Axes']=[dict(m['Axes'][0],MaximumJerk=0.)]
  self.assertEqual(0.,w.Manifest.from_object(m).axes[0]['MaximumJerk'])
 def test_unknown_defaults_rejected(self):
  a,f=self.worker()
  for key in ('MaximumAcceleration','MaximumDeceleration','MaximumJerk'):
   m=dict(a.manifest.document);m['Axes']=[dict(m['Axes'][0])];del m['Axes'][0][key]
   with self.assertRaises(ValueError):w.Manifest.from_object(m)
 def test_rejected_stop_and_duplicates_preserve_latch_and_ownership(self):
  for reason in ('invalid_state','invalid_argument','command_rejected'):
   for active in (False,True):
    with self.subTest(reason=reason,active=active):
     a,f=self.worker()
     if active:a.scan(self.packet())
     previous=a.slots[0]
     owned=set(a.owned);original=f.request
     def reject(k,p,e=None):
      if k=='AxisCommandRequest' and p['command']=='Stop':
       f.calls.append((k,dict(p)))
       if reason=='command_rejected':return dict(accepted=False)
       raise w.BackendError(reason)
      return original(k,p,e)
     f.request=reject
     def stop(scan,flags):
      return w.HEADER.pack(scan,1)+w.COMMAND.pack(2,7,0,0,0,0,0,flags)+bytes(63*w.COMMAND.size)
     a.scan(stop(2,1));self.assertEqual(2005,a.slots[0].rejected_error)
     self.assertFalse(a.slots[0].stop_held);self.assertEqual(owned,a.owned);self.assertFalse(a.failed)
     a.scan(stop(3,1));self.assertFalse(a.slots[0].stop_held)
     self.assertEqual((previous.request,previous.kind,previous.motion_id,previous.busy,previous.done),
                      (a.slots[0].request,a.slots[0].kind,a.slots[0].motion_id,a.slots[0].busy,a.slots[0].done))
     receipt=w.FEEDBACK.unpack_from(a.feedback());self.assertEqual((2,2005),receipt[-2:])
     if active:
      f.axis.update(motionId=previous.motion_id,motionBusy=False,motionDone=True)
      a.poll();self.assertTrue(a.slots[0].done);self.assertFalse(a.slots[0].busy)
      self.assertEqual(1,w.FEEDBACK.unpack_from(a.feedback())[0])
     self.assertEqual(1,sum(k=='AxisCommandRequest' and p['command']=='Stop' for k,p in f.calls))
     self.assertFalse(any(k=='ReleaseControlRequest' for k,p in f.calls))
     if active:
      self.assertEqual(1,sum(k=='AxisCommandRequest' and p['command']=='MoveAbsolute' for k,p in f.calls))
 def test_rejected_stop_keeps_prior_held_latch_and_timeout_fails_closed(self):
  a,f=self.worker();a.slots[0].stop_held=True
  original=f.request
  def reject(k,p,e=None):
   if k=='AxisCommandRequest':raise w.BackendError('invalid_argument')
   return original(k,p,e)
  f.request=reject;a.scan(self.packet(kind=7));self.assertTrue(a.slots[0].stop_held)
  # Ambiguous transport failure may have accepted the command: retain protective teardown.
  a,f=self.worker();original=f.request
  def timeout(k,p,e=None):
   if k=='AxisCommandRequest' and p['command']=='MoveAbsolute':raise OSError('timeout')
   return original(k,p,e)
  f.request=timeout;a.scan(self.packet());self.assertEqual(2004,a.failed)
  self.assertTrue(any(k=='AxisCommandRequest' and p['command']=='Stop' for k,p in f.calls))
  self.assertTrue(any(k=='ReleaseControlRequest' for k,p in f.calls))
 def test_accepted_stop_owner_release_after_newer_rejected_request(self):
  a,f=self.worker()
  def packet(scan,request,kind,flags):return w.HEADER.pack(scan,1)+w.COMMAND.pack(request,kind,0,1,0,0,0,flags)+bytes(63*w.COMMAND.size)
  a.scan(packet(1,1,7,1));a.scan(packet(2,2,5,1))
  self.assertEqual(2006,a.slots[0].rejected_error);self.assertTrue(a.slots[0].stop_held)
  a.scan(packet(3,1,7,0));self.assertFalse(a.slots[0].stop_held)
  self.assertEqual(2,a.slots[0].last_seen_request)
  self.assertEqual(1,sum(k=='AxisCommandRequest' for k,p in f.calls))
 def test_accepted_stop_duplicate_owner_releases_latch(self):
  a,f=self.worker()
  def stop(scan,flags):return w.HEADER.pack(scan,1)+w.COMMAND.pack(1,7,0,0,0,0,0,flags)+bytes(63*w.COMMAND.size)
  a.scan(stop(1,1));self.assertTrue(a.slots[0].stop_held)
  a.scan(stop(2,0));self.assertFalse(a.slots[0].stop_held)
if __name__=='__main__':unittest.main()
