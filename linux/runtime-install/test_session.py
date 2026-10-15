import importlib.util
import json
import os
import pathlib
import tempfile
import unittest
spec=importlib.util.spec_from_file_location('session',pathlib.Path(__file__).with_name('runtime-session.py'))
session=importlib.util.module_from_spec(spec)
spec.loader.exec_module(session)
class SessionTests(unittest.TestCase):
    def test_private_schema_and_bounds(self):
        with tempfile.TemporaryDirectory() as directory:
            path=pathlib.Path(directory)/'auth.json'
            path.write_text(json.dumps({'username':'test','password':'secret'}))
            path.chmod(0o600)
            self.assertEqual(session.read_private_credentials(path)['username'],'test')
            path.write_text('x'*20000)
            with self.assertRaises(ValueError): session.read_private_credentials(path)
    @unittest.skipUnless(os.name=='posix','POSIX permission boundary')
    def test_symlink_and_public_permissions(self):
        with tempfile.TemporaryDirectory() as directory:
            path=pathlib.Path(directory)/'auth.json'
            path.write_text(json.dumps({'username':'test','password':'secret'})); path.chmod(0o644)
            with self.assertRaises(ValueError): session.read_private_credentials(path)
            path.chmod(0o600)
            alias=pathlib.Path(directory)/'alias'; alias.symlink_to(path)
            with self.assertRaises(ValueError): session.read_private_credentials(alias)
