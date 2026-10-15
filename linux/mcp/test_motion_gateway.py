import copy
import datetime
import pathlib
import sys
import unittest
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / 'axis'))
from test_axis_worker import Fake, manifest
import motion_gateway as g

class Tests(unittest.TestCase):
    def make(self, physical=False):
        self.time = 1.
        backend = Fake(physical)
        original = backend.request
        def request(kind, payload, expected=None):
            result = original(kind,payload,expected)
            if kind == 'AcquireControlRequest':
                result.update(grantedLeaseMilliseconds=payload['requestedLeaseMilliseconds'],
                    expiresAt=(datetime.datetime.now(datetime.timezone.utc)+datetime.timedelta(milliseconds=payload['requestedLeaseMilliseconds'])).isoformat())
            return result
        backend.request=request
        gateway=g.Gateway(g.Manifest.from_object(manifest()),backend,'EcMotion' if physical else 'EcMotionSimulator',now=lambda:self.time)
        return gateway,backend
    def call(self,gateway,action,**kw):
        return gateway.handle(dict(requestId=str(len(gateway.requests)+1),action=action,**kw))
    def test_read_only_has_no_lease_and_no_secrets(self):
        gateway,b=self.make()
        self.assertTrue(self.call(gateway,'status')['success'])
        self.assertFalse(any(k=='AcquireControlRequest' for k,p in b.calls))
        self.assertNotIn('leaseToken',str(self.call(gateway,'acquire')))
    def test_requires_explicit_lease_and_deduplicates(self):
        gateway,b=self.make()
        self.assertFalse(self.call(gateway,'execute',operationId='x',axis='Axis1',kind='Enable')['success'])
        self.call(gateway,'acquire')
        self.assertTrue(self.call(gateway,'execute',operationId='y',axis='Axis1',kind='Enable')['success'])
        before=len(b.calls)
        self.assertFalse(self.call(gateway,'execute',operationId='y',axis='Axis1',kind='Enable')['success'])
        self.assertEqual(before,len(b.calls))
    def test_move_requires_matching_motion_id(self):
        gateway,b=self.make();self.call(gateway,'acquire');b.axis['homed']=True
        self.call(gateway,'execute',operationId='m',axis='Axis1',kind='MoveAbsolute',position=4,velocity=2)
        b.axis.update(motionId=99,motionDone=True)
        gateway.tick()
        self.assertNotEqual('Completed',self.call(gateway,'operation',operationId='m')['operation']['state'])
        b.axis['motionId']=41;gateway.tick()
        self.assertEqual('Completed',self.call(gateway,'operation',operationId='m')['operation']['state'])
    def test_stop_preempts_busy_and_eof_stops_owned(self):
        gateway,b=self.make();self.call(gateway,'acquire');b.axis['homed']=True
        self.call(gateway,'execute',operationId='m',axis='Axis1',kind='MoveAbsolute',position=4,velocity=2)
        self.assertTrue(self.call(gateway,'stop',axis='Axis1',operationId='s')['success'])
        self.assertEqual('Aborted',gateway.operations['m']['state'])
        gateway.close()
        self.assertTrue(any(p.get('command')=='Stop' for k,p in b.calls))
        self.assertFalse(gateway.worker.lease)
    def test_jog_device_lease_is_bounded_and_not_renewed(self):
        gateway,b=self.make();self.call(gateway,'acquire',durationMilliseconds=500);b.axis['homed']=True
        r=self.call(gateway,'execute',operationId='j',axis='Axis1',kind='Jog',velocity=2,durationMilliseconds=500)
        self.assertTrue(r['success'],r)
        self.time+=.3;gateway.tick()
        self.assertFalse(any(k=='HeartbeatRequest' for k,p in b.calls))
        self.time+=.3;gateway.tick()
        self.assertTrue(any(p.get('command')=='Stop' for k,p in b.calls))
    def test_rejects_limits_unknown_keys_and_explicit_mechanics(self):
        gateway,b=self.make();self.call(gateway,'acquire');b.axis['homed']=True
        for index,args in enumerate([dict(position=101,velocity=2),dict(position=1,velocity=float('nan')),dict(position=1,velocity=2,controllerSocket='bad')]):
            self.assertFalse(self.call(gateway,'execute',operationId='bad'+str(index),axis='Axis1',kind='MoveAbsolute',**args)['success'])
        data=manifest();data['Axes'][0]['Mechanics']={}
        with self.assertRaises(ValueError):g.Manifest.from_object(data)
    def test_physical_home_receipt_missing_tears_down_unknown(self):
        gateway,b=self.make(True);self.call(gateway,'acquire');b.receipt=False
        r=self.call(gateway,'execute',operationId='h',axis='Axis1',kind='Home')
        self.assertFalse(r['success']);self.assertEqual('Unknown',r['state'])
        self.assertFalse(gateway.worker.lease)
    def test_deadline_and_stale_sample_fail_closed(self):
        gateway,b=self.make();self.call(gateway,'acquire')
        self.time+=31;gateway.tick()
        self.assertFalse(gateway.worker.lease)
        gateway,b=self.make();self.call(gateway,'acquire')
        original=b.request
        def bad(k,p,e=None):
            r=original(k,p,e)
            if k=='ReadSnapshotRequest':r['timestamp']='2000-01-01T00:00:00+00:00'
            return r
        b.request=bad
        self.assertFalse(self.call(gateway,'status')['success'])
        self.assertTrue(gateway.closed)


class AdditionalTests(unittest.TestCase):
    make=Tests.make
    call=Tests.call
    def test_conflict_does_not_take_lease(self):
        gateway,b=self.make();b.fail_lease=True
        r=self.call(gateway,'acquire')
        self.assertEqual('Conflict',r['state']);self.assertFalse(gateway.worker.lease)
        self.assertFalse(gateway.closed)
    def test_invalid_grant_or_expiry_fails_closed(self):
        for mutate in [lambda r:r.update(grantedLeaseMilliseconds=40000),lambda r:r.update(expiresAt='bad')]:
            gateway,b=self.make();original=b.request
            def bad(k,p,e=None):
                r=original(k,p,e)
                if k=='AcquireControlRequest':mutate(r)
                return r
            b.request=bad
            self.assertEqual('Unknown',self.call(gateway,'acquire')['state'])
            self.assertFalse(gateway.worker.lease)
    def test_stale_physical_cycle_and_wrong_motion_id_never_complete(self):
        gateway,b=self.make(True);self.call(gateway,'acquire');b.axis['homed']=True
        b.motion=40
        self.assertEqual('Unknown',self.call(gateway,'execute',operationId='m',axis='Axis1',kind='MoveAbsolute',position=3,velocity=1)['state'])
        self.assertTrue(gateway.closed)
    def test_duplicate_request_and_capacity_never_replay(self):
        gateway,b=self.make()
        request=dict(requestId='fixed',action='acquire')
        self.assertTrue(gateway.handle(request)['success']);before=len(b.calls)
        self.assertFalse(gateway.handle(request)['success']);self.assertEqual(before,len(b.calls))
        gateway.requests=set(str(i) for i in range(g.MAX_IDS))
        self.assertFalse(gateway.handle(dict(requestId='overflow',action='acquire'))['success'])
        self.assertTrue(gateway.closed)
        self.assertFalse(gateway.worker.lease)
    def test_home_cannot_complete_before_applied_cycle(self):
        gateway,b=self.make(True);self.call(gateway,'acquire')
        original=b.request
        def future(k,p,e=None):
            r=original(k,p,e)
            if k=='PlcScanBatchRequest':r['actionReceipts'][0]['appliedCycle']=b.cycle+5
            return r
        b.request=future
        self.assertTrue(self.call(gateway,'execute',operationId='h',axis='Axis1',kind='Home')['success'])
        b.axis['homed']=True;gateway.tick()
        self.assertNotEqual('Completed',gateway.operations['h']['state'])
        for _ in range(5):gateway.tick()
        self.assertEqual('Completed',gateway.operations['h']['state'])

class ProofTests(unittest.TestCase):
    make=Tests.make
    call=Tests.call
    def test_invalid_actual_boolean_fails_closed(self):
        gateway,b=self.make();b.axis['actualPosition']=True
        self.assertEqual('Unknown',self.call(gateway,'status')['state'])
        self.assertTrue(gateway.closed)
    def test_home_requires_enabled_stationary_axis_without_submitting(self):
        gateway,b=self.make();self.call(gateway,'acquire');b.axis['state']='Disabled'
        before=sum(k in ('AxisCommandRequest','PlcScanBatchRequest') for k,p in b.calls)
        self.assertFalse(self.call(gateway,'execute',operationId='h',axis='Axis1',kind='Home')['success'])
        self.assertEqual(before,sum(k in ('AxisCommandRequest','PlcScanBatchRequest') for k,p in b.calls))


class TeardownTests(unittest.TestCase):
    make=Tests.make
    call=Tests.call
    def test_failed_deadline_stop_remains_unknown(self):
        gateway,b=self.make();self.call(gateway,'acquire');b.axis['homed']=True
        self.call(gateway,'execute',operationId='m',axis='Axis1',kind='MoveAbsolute',position=2,velocity=1)
        original=b.request
        def fail_stop(k,p,e=None):
            if p.get('command')=='Stop':raise OSError('disconnected')
            return original(k,p,e)
        b.request=fail_stop
        self.time+=31;gateway.tick()
        self.assertEqual('Unknown',gateway.operations['m']['state'])


class PhysicalJogTests(unittest.TestCase):
    make=Tests.make
    call=Tests.call
    def test_physical_jog_requires_guaranteed_stopping_bounds(self):
        gateway,b=self.make(True);self.call(gateway,'acquire',durationMilliseconds=500);b.axis['homed']=True
        r=self.call(gateway,'execute',operationId='j',axis='Axis1',kind='Jog',velocity=1,durationMilliseconds=500)
        self.assertFalse(r['success']);self.assertIn('capability_missing',r['message'])
        self.assertFalse(any(p.get('command')=='Jog' for k,p in b.calls))


class PhysicalDynamicTests(unittest.TestCase):
    def test_physical_motion_rejects_unapplied_dynamic_parameters(self):
        for key in ('MaximumAcceleration','MaximumDeceleration','MaximumJerk'):
            for kind in ('Home','MoveAbsolute'):
                data=manifest();data['Axes'][0][key]=10
                b=Fake(True)
                gateway=g.Gateway(g.Manifest.from_object(data),b,'EcMotion')
                # Fixed protocol grant with actual expiry proof.
                original=b.request
                def request(k,p,e=None):
                    r=original(k,p,e)
                    if k=='AcquireControlRequest':
                        r.update(grantedLeaseMilliseconds=1000,expiresAt=(datetime.datetime.now(datetime.timezone.utc)+datetime.timedelta(seconds=1)).isoformat())
                    return r
                b.request=request
                gateway.handle(dict(requestId='a',action='acquire'));b.axis['homed']=True
                r=gateway.handle(dict(requestId='b',action='execute',operationId='m',axis='Axis1',kind=kind,position=1,velocity=1))
                self.assertFalse(r['success']);self.assertIn('capability_missing',r['message'])
                self.assertFalse(any(k in ('PlcScanBatchRequest','AxisCommandRequest') for k,p in b.calls))


class PhysicalFreshCompletionTests(unittest.TestCase):
    make=Tests.make
    call=Tests.call
    def test_already_homed_same_cycle_does_not_complete_new_home(self):
        gateway,b=self.make(True);self.call(gateway,'acquire');b.axis['homed']=True
        original=b.request
        fixed=[None]
        def freeze(k,p,e=None):
            r=original(k,p,e)
            if k=='ReadSnapshotRequest':
                if fixed[0] is None:fixed[0]=r['sampleCycle']
                r['sampleCycle']=fixed[0]
            if k=='PlcScanBatchRequest':r['actionReceipts'][0]['appliedCycle']=fixed[0]
            return r
        b.request=freeze
        self.assertTrue(self.call(gateway,'execute',operationId='home',axis='Axis1',kind='Home')['success'])
        gateway.tick()
        self.assertNotEqual('Completed',gateway.operations['home']['state'])
        b.request=original
        b.axis.update(state='Homing',homed=False,actualVelocity=1)
        gateway.tick()
        self.assertNotEqual('Completed',gateway.operations['home']['state'])
        b.axis.update(state='Standstill',homed=True,actualVelocity=0)
        gateway.tick()
        self.assertEqual('Completed',gateway.operations['home']['state'])


class JogBudgetTests(unittest.TestCase):
    make=Tests.make
    call=Tests.call
    def test_maximum_jog_budget_accepts_shorter_remaining_device_deadline(self):
        gateway,b=self.make();self.call(gateway,'acquire',durationMilliseconds=30000);b.axis['homed']=True
        self.time+=.05
        r=self.call(gateway,'execute',operationId='j',axis='Axis1',kind='Jog',velocity=.1,durationMilliseconds=30000)
        self.assertTrue(r['success'],r)
        self.assertLessEqual(r['operation']['maximumRemainingMilliseconds'],30000)

if __name__=='__main__': unittest.main()
