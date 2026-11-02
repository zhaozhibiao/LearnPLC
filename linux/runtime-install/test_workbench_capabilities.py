import importlib.util,pathlib,unittest
from unittest.mock import Mock
spec=importlib.util.spec_from_file_location('caps',pathlib.Path(__file__).with_name('workbench_capabilities.py'))
caps=importlib.util.module_from_spec(spec);spec.loader.exec_module(caps)
class Tests(unittest.TestCase):
    def test_live_fixed_query(self):
        manager=Mock();manager.runtime_socket.send_and_receive.return_value='DEBUG:467e01\n'
        self.assertTrue(caps.task_scheduling(manager)['supported'])
        manager.runtime_socket.send_and_receive.assert_called_once_with('DEBUG:46 02\n',timeout=2.0)
    def test_fail_closed(self):
        for response in [None,'DEBUG:467e00','DEBUG:467e0100','DEBUG:00','ERROR','DEBUG:nothex']:
            manager=Mock();manager.runtime_socket.send_and_receive.return_value=response
            self.assertFalse(caps.task_scheduling(manager)['supported'])
    def test_unavailable(self):
        manager=Mock();manager.runtime_socket.send_and_receive.side_effect=OSError('offline')
        self.assertFalse(caps.task_scheduling(manager)['supported'])
if __name__=='__main__':unittest.main()
