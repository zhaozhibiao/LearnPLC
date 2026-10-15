import hashlib
import importlib.util
import pathlib
import tempfile
import unittest

path = pathlib.Path(__file__).with_name('candidate-store.py')
spec = importlib.util.spec_from_file_location('candidate_store', path)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

class CandidateStoreTests(unittest.TestCase):
    def test_staging_records_bytes_without_touching_active_source(self):
        with tempfile.TemporaryDirectory() as folder:
            root = pathlib.Path(folder)
            source = root / 'source/build/libplc_active.so'
            source.parent.mkdir(parents=True)
            source.write_bytes(b'active')
            package = root / 'input.zip'
            package.write_bytes(b'package bytes')
            digest = hashlib.sha256(package.read_bytes()).hexdigest()
            record = module.stage(package, root / 'candidates', digest)
            self.assertEqual('StagedArchiveUnverified', record['state'])
            self.assertEqual(b'package bytes', (root / 'candidates' / digest / 'package.zip').read_bytes())
            self.assertEqual(b'active', source.read_bytes())
            with self.assertRaises(FileExistsError):
                module.stage(package, root / 'candidates', digest)

    def test_wrong_hash_is_rejected_before_candidate_creation(self):
        with tempfile.TemporaryDirectory() as folder:
            root = pathlib.Path(folder)
            package = root / 'input.zip'
            package.write_bytes(b'package bytes')
            with self.assertRaises(ValueError):
                module.stage(package, root / 'candidates', '0' * 64)
            self.assertFalse((root / 'candidates').exists())

if __name__ == '__main__': unittest.main()
