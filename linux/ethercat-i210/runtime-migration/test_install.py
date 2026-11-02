import hashlib,importlib.util,pathlib,tempfile,unittest
spec=importlib.util.spec_from_file_location('installer',pathlib.Path(__file__).with_name('install.py'))
installer=importlib.util.module_from_spec(spec);spec.loader.exec_module(installer)
class InstallTests(unittest.TestCase):
    def test_validates_captured_bytes(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=pathlib.Path(tmp);(root/'core').write_bytes(b'core')
            data=installer.capture(root,{'core':hashlib.sha256(b'core').hexdigest()})
            (root/'core').write_bytes(b'changed')
            self.assertEqual(data['core'],b'core')
    def test_hash_mismatch_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=pathlib.Path(tmp);(root/'core').write_bytes(b'bad')
            with self.assertRaises(ValueError):installer.capture(root,{'core':'0'*64})
    def test_symbolic_source_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=pathlib.Path(tmp);(root/'real').write_bytes(b'core')
            try:(root/'core').symlink_to(root/'real')
            except OSError:self.skipTest('symlink permission unavailable')
            with self.assertRaises((ValueError,OSError)):installer.capture(root,{'core':hashlib.sha256(b'core').hexdigest()})
if __name__=='__main__':unittest.main()
