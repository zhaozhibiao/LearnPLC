import importlib.util
import json
import pathlib
import tempfile
import unittest

spec=importlib.util.spec_from_file_location('provision',pathlib.Path(__file__).with_name('provision-local.py'))
module=importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
class ProvisionTests(unittest.TestCase):
    def test_credentials_are_generated_once_and_contain_no_token(self):
        with tempfile.TemporaryDirectory() as folder:
            path=pathlib.Path(folder)/'auth.json'
            result=module.credentials(path)
            self.assertEqual('stworkbench-coordinator',result['username'])
            self.assertGreaterEqual(len(result['password']),40)
            self.assertNotIn('access_token',result)
            self.assertEqual(result,module.credentials(path))
            self.assertEqual(result,json.loads(path.read_text(encoding='utf-8')))
    def test_symlink_target_refused(self):
        with tempfile.TemporaryDirectory() as folder:
            path=pathlib.Path(folder)/'auth.json'
            # A malformed existing file must never be replaced or printed.
            path.write_text('secret invalid document',encoding='utf-8')
            with self.assertRaises(ValueError): module.credentials(path)
            self.assertEqual('secret invalid document',path.read_text(encoding='utf-8'))
if __name__=='__main__': unittest.main()
