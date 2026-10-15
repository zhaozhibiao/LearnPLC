"""Local tests; never import/start Runtime or touch a remote host."""
import pathlib
import tempfile
import unittest
from isolate import apply

UPSTREAM = pathlib.Path(__file__).resolve().parents[2] / 'artifacts/upstream/openplc-runtime-v4.3.0'

class IsolationTests(unittest.TestCase):
    def test_relocates_only_expected_files_and_disables_bus_and_discovery(self):
        names = ['webserver/app.py', 'core/src/plc_app/unix_socket.h',
                 'core/src/plc_app/utils/log.h', 'core/src/plc_app/task_policy.h', 'webserver/runtimemanager.py']
        with tempfile.TemporaryDirectory() as folder:
            root = pathlib.Path(folder)
            for name in names:
                target = root / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text((UPSTREAM / name).read_text(encoding='utf-8'), encoding='utf-8')
            apply(root, '/home/a/.local/share/st-workbench/runtime-v4.3.0/run')
            # Recovery can resume an already patched source without loosening revision checks.
            apply(root, '/home/a/.local/share/st-workbench/runtime-v4.3.0/run')
            for name in names:
                text = (root / name).read_text(encoding='utf-8')
                self.assertNotIn('/run/runtime/', text)
            text = (root / 'webserver/app.py').read_text(encoding='utf-8')
            self.assertIn('runtime_path=str(Path(__file__).resolve().parents[1] / "build/plc_main")', text)
            self.assertNotIn('network_discovery_responder.start()', text)
            self.assertNotIn('etherdog_manager.start()', text)
            self.assertIn('host="127.0.0.1"', text)
            self.assertIn('port=8443', text)
            self.assertEqual(2, (root / 'webserver/runtimemanager.py').read_text(encoding='utf-8').count('cmd = [self.runtime_path, "--safe-mode"]'))
            compile(text, 'app.py', 'exec')

    def test_revision_drift_refused_without_partial_changes(self):
        with tempfile.TemporaryDirectory() as folder:
            root = pathlib.Path(folder)
            target = root / 'webserver/app.py'
            target.parent.mkdir(parents=True)
            target.write_text('unexpected upstream revision', encoding='utf-8')
            with self.assertRaises((ValueError, FileNotFoundError)):
                apply(root, '/home/a/.local/share/st-workbench/runtime-v4.3.0/run')
            self.assertEqual('unexpected upstream revision', target.read_text(encoding='utf-8'))

if __name__ == '__main__':
    unittest.main()
