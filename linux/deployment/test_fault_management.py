import tempfile,unittest
from unittest.mock import patch
import ethercat_hardware as hw
from test_ethercat_hardware import fixture

class FaultManagementDeploymentTests(unittest.TestCase):
 def test_axis_fault_does_not_block_hardware_identity_proof(self):
  with tempfile.TemporaryDirectory() as directory:
   _,generated,topology=fixture(directory)
   hello=dict(runtimeReady=True,motionReady=False,ethercatTopologyFingerprint=topology['topologyFingerprint'])
   def sample(cycle):return dict(runtimeReady=True,motionReady=False,ioReady=True,sampleCycle=cycle,sampleMonotonicNs=cycle*100,axes=[dict(name='Axis1',state='ErrorStop',fault='overspeed')])
   with patch.object(hw,'native_request',side_effect=[hello,sample(1),sample(2)]):
    self.assertTrue(hw.verify_applied(generated,{})['verified'])
   with patch.object(hw,'native_request',side_effect=[hello,sample(1),sample(1)]):
    with self.assertRaises(ValueError):hw.verify_applied(generated,{})
   bad=sample(1);bad['ioReady']=False
   with patch.object(hw,'native_request',side_effect=[hello,bad]):
    with self.assertRaises(ValueError):hw.verify_applied(generated,{})

if __name__=='__main__':unittest.main()
