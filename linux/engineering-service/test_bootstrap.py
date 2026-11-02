import hashlib
import importlib.util
import io
import json
import pathlib
import tempfile
import unittest
from unittest.mock import patch

PATH = pathlib.Path(__file__).with_name('bootstrap-pip.py')
spec = importlib.util.spec_from_file_location('bootstrap_pip', PATH) if PATH.exists() else None
bootstrap = importlib.util.module_from_spec(spec) if spec else None
if spec: spec.loader.exec_module(bootstrap)


class BootstrapTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(bootstrap, 'Reviewed bootstrap helper is required')
        self.wheel = b'bounded wheel fixture'
        self.record = {'packagetype': 'bdist_wheel', 'filename': 'pip-26.0-py3-none-any.whl',
                       'url': 'https://files.pythonhosted.org/packages/fixture.whl',
                       'digests': {'sha256': hashlib.sha256(self.wheel).hexdigest()}}
        self.metadata = {'info': {'version': '26.0'}, 'urls': [self.record]}

    def fetch(self, url):
        return json.dumps(self.metadata).encode() if url.startswith('https://pypi.org/') else self.wheel

    def test_verified_wheel_and_private_file(self):
        with tempfile.TemporaryDirectory() as directory:
            path, version = bootstrap.fetch_wheel(pathlib.Path(directory), self.fetch)
            self.assertEqual(self.wheel, path.read_bytes())
            self.assertEqual('26.0', version)

    def test_checksum_mismatch_rejected(self):
        self.record['digests']['sha256'] = '0' * 64
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(ValueError, 'checksum'):
                bootstrap.fetch_wheel(pathlib.Path(directory), self.fetch)

    def test_non_official_download_host_rejected(self):
        self.record['url'] = 'https://attacker.example/pip.whl'
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(ValueError):
                bootstrap.fetch_wheel(pathlib.Path(directory), self.fetch)

    def test_filename_traversal_rejected(self):
        self.record['filename'] = '../pip.whl'
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(ValueError):
                bootstrap.fetch_wheel(pathlib.Path(directory), self.fetch)

    def test_redirect_and_capacity_rejected(self):
        class Response(io.BytesIO):
            def geturl(self): return 'https://attacker.example/wheel'
        with patch('urllib.request.OpenerDirector.open', return_value=Response(b'fixture')):
            with self.assertRaises(ValueError):
                bootstrap.download('https://pypi.org/pypi/pip/json')


if __name__ == '__main__': unittest.main()
