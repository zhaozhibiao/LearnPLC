import unittest
from disable_confirmation import await_disabled
class Tests(unittest.TestCase):
 def verify(self,reader):
  t=[0.];calls=[0]
  def read():calls[0]+=1;return reader(calls[0])
  def wait(n):t[0]+=n
  def stable(s):return len(s)>=20 and s[-1][0]-s[0][0]>=.5 and max(x[1] for x in s)-min(x[1] for x in s)<=4 and all(abs(x[2])<=.02 for x in s)
  return await_disabled(read,lambda:None,stable,timeout=1,clock=lambda:t[0],sleep=wait)
 def test_delayed_ack(self):
  p,s=self.verify(lambda n:dict(statusword=4 if n<5 else 0,actual=0,velocity=0));self.assertEqual(p['statusword'],0)
 def test_never_disabled_fails(self):
  with self.assertRaises(TimeoutError):self.verify(lambda n:dict(statusword=4,actual=0,velocity=0))
 def test_no_standstill_fails(self):
  with self.assertRaises(TimeoutError):self.verify(lambda n:dict(statusword=0,actual=n*100,velocity=100000))
 def test_unknown_fails(self):
  def fail(n):raise OSError('unknown device outcome')
  with self.assertRaises(OSError):self.verify(fail)
if __name__=='__main__':unittest.main()
