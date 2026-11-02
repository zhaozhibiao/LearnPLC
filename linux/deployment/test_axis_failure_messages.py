import unittest
import axis_gate
class FailureMessages(unittest.TestCase):
 def test_known_failure_is_explained_without_leaking_payload(self):
  message=axis_gate.channel_failure_detail({'FailureCode':2004,'FailureReason':'transport_timeout','RecoveryRequired':True,'leaseToken':'never-log-this'})
  self.assertIn('通信超时',message);self.assertIn('2004',message);self.assertIn('停止',message)
  self.assertNotIn('never-log-this',message)
 def test_unknown_or_nonterminal_state_does_not_invent_recovery(self):
  message=axis_gate.channel_failure_detail({'FailureReason':'untrusted secret','RecoveryRequired':False})
  self.assertNotIn('untrusted secret',message);self.assertNotIn('手动运行',message)
 def test_malformed_reason_is_not_echoed_or_crashed(self):
  message=axis_gate.channel_failure_detail({'FailureReason':{'payload':'secret'},'RecoveryRequired':'true'})
  self.assertNotIn('secret',message);self.assertNotIn('手动运行',message)
if __name__=='__main__':unittest.main()
