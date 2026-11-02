"""Offline installer tests: no systemd or hardware operations."""
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'deployment'))
import install_axis_v2 as installer
import test_axis_worker_v2 as fixtures
class InstallTests(unittest.TestCase):
 def test_prepare_preserves_config_secrets_and_does_not_start_service(self):
  with tempfile.TemporaryDirectory() as directory:
   home=Path(directory);deployment=home/'st-workbench-deployment';deployment.mkdir(mode=0o700)
   original=dict(secret='private',unknown={'keep':[1,2]},axis_backend='EcMotion',axis_socket_path='v1.sock')
   config=deployment/'config.json';config.write_text(json.dumps(original));config.chmod(0o600)
   generated=home/'candidate';(generated/'axes').mkdir(parents=True)
   worker=Path(installer.__file__).with_name('axis_worker_v2.py').read_bytes()
   (generated/'axes/axis_worker_v2.py').write_bytes(worker)
   manifest=fixtures.V2Tests().worker()[0].manifest.document
   manifest['Axes'][0]['MaximumVelocity']=5.
   (generated/'axis-manifest.json').write_text(json.dumps(manifest))
   inventory={name:hashlib.sha256((generated/name).read_bytes()).hexdigest() for name in ('axes/axis_worker_v2.py','axis-manifest.json')}
   with mock.patch.object(installer,'verified_candidate',return_value=(generated,dict(AxisInterfaceVersion='workbench-axis-v2',Files=inventory),{})),mock.patch.object(installer,'service_stopped',return_value=True),mock.patch.object(installer.subprocess,'run') as run:
    result=installer.prepare(home/'unused',home=home)
    run.assert_not_called()
   changed=json.loads(config.read_text());self.assertEqual(original,{k:v for k,v in changed.items() if k!='axis_v2_socket_path'})
   self.assertEqual(installer.ENDPOINT,changed['axis_v2_socket_path'])
   self.assertFalse(result['started']);self.assertEqual(6656,result['FeedbackBytes'])
   unit=(home/'.config/systemd/user/stworkbench-axis-v2.service').read_text()
   self.assertNotIn('WantedBy',unit);self.assertIn('Restart=no',unit)
 def test_first_install_missing_unit_is_safe_but_unknown_error_is_rejected(self):
  result=mock.Mock(returncode=1,stdout='LoadState=not-found\nActiveState=inactive\n')
  with mock.patch.object(installer.subprocess,'run',return_value=result):self.assertTrue(installer.service_stopped())
  result.stdout='';result.returncode=1
  with mock.patch.object(installer.subprocess,'run',return_value=result),self.assertRaises(ValueError):installer.service_stopped()
 @unittest.skipUnless(installer.os.name=='posix','POSIX private file mode')
 def test_foreign_configuration_permissions_rejected(self):
  with tempfile.TemporaryDirectory() as directory:
   path=Path(directory)/'config.json';path.write_text('{}');path.chmod(0o644)
   with self.assertRaisesRegex(ValueError,'0600'):installer.regular(path,private=True)
 def test_active_service_rejected_before_reading_candidate(self):
  with tempfile.TemporaryDirectory() as directory,mock.patch.object(installer,'service_stopped',return_value=False):
   with self.assertRaisesRegex(ValueError,'stopped'):installer.prepare(Path(directory),home=Path(directory))
if __name__=='__main__':unittest.main()
