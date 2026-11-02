import sys,unittest
sys.path.insert(0,'linux/deployment')
import coordinator
from unittest import mock
class Tests(unittest.TestCase):
 def test_rest_live_capability(self):
  connection=coordinator.RestRuntimeConnection.__new__(coordinator.RestRuntimeConnection)
  connection.call=mock.Mock(return_value={'interface':'workbench-task-scheduling-v1','supported':True})
  self.assertTrue(connection.task_scheduling_supported())
  connection.call.assert_called_once_with('/api/workbench-task-scheduling')
 def test_rest_capability_fail_closed(self):
  connection=coordinator.RestRuntimeConnection.__new__(coordinator.RestRuntimeConnection)
  for response in [{},{'supported':True},{'interface':'workbench-task-scheduling-v1','supported':1}]:
   connection.call=mock.Mock(return_value=response)
   self.assertFalse(connection.task_scheduling_supported())
 def test_new_library_must_export_policy(self):
  with self.assertRaises(coordinator.DeploymentError):coordinator.verify_candidate_exports({'TaskSchedulingInterfaceVersion':'workbench-task-scheduling-v1'},coordinator.REQUIRED_EXPORTS)
  coordinator.verify_candidate_exports({'TaskSchedulingInterfaceVersion':'workbench-task-scheduling-v1'},coordinator.REQUIRED_EXPORTS|{'workbench_task_scheduling_v1'})
 def test_legacy(self):coordinator.verify_task_scheduling(object(),{})
 def test_missing(self):
  with self.assertRaises(coordinator.DeploymentError):coordinator.verify_task_scheduling(object(),{'TaskSchedulingInterfaceVersion':'workbench-task-scheduling-v1'})
 def test_supported(self):
  class C:
   def task_scheduling_supported(self):return True
  coordinator.verify_task_scheduling(C(),{'TaskSchedulingInterfaceVersion':'workbench-task-scheduling-v1'})
 def test_rejected(self):
  class C:
   def task_scheduling_supported(self):return False
  with self.assertRaises(coordinator.DeploymentError):coordinator.verify_task_scheduling(C(),{'TaskSchedulingInterfaceVersion':'workbench-task-scheduling-v1'})
if __name__=='__main__':unittest.main()
