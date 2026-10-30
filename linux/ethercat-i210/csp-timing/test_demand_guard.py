import unittest
from demand_guard import DemandGuard
class GuardTests(unittest.TestCase):
 def run_case(self,mutate=lambda i,r:r):
  g=DemandGuard(); result=None
  for i in range(1,101):
   r=dict(cycle=i,scheduled_ns=i*1000000,sent_target_raw=i*1000,demand_raw=(i-3)*1000,actual_raw=(i-3)*1000,drive_error_raw=0,valid=True)
   result=g.update(**mutate(i,r))
  return result
 def test_normal(self):self.assertIsNone(self.run_case()['fault'])
 def test_delay(self):self.assertEqual(self.run_case(lambda i,r:dict(r,demand_raw=(i-4)*1000))['fault'],'delivery')
 def test_stalled_actual(self):self.assertEqual(self.run_case(lambda i,r:dict(r,actual_raw=0))['fault'],'following')
 def test_drive_error_independent(self):self.assertEqual(self.run_case(lambda i,r:dict(r,drive_error_raw=6000))['fault'],'following')
 def test_invalid_frame(self):self.assertEqual(self.run_case(lambda i,r:dict(r,valid=i!=20))['fault'],'freshness')
 def test_missing_cycle(self):self.assertEqual(self.run_case(lambda i,r:dict(r,cycle=i+(i>=20)))['fault'],'freshness')
 def test_epoch(self):self.assertEqual(self.run_case(lambda i,r:dict(r,scheduled_ns=0 if i==20 else r['scheduled_ns']))['fault'],'freshness')
if __name__=='__main__':unittest.main()
