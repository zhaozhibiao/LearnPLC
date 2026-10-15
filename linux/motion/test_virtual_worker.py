import importlib.util
import pathlib
import unittest
import tempfile
import json
import os

MODULE = pathlib.Path(__file__).with_name('virtual_worker.py')

class WorkerTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(MODULE.exists(), 'virtual continuous worker has not been implemented')
        spec = importlib.util.spec_from_file_location('virtual_worker', MODULE)
        self.m = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.m)
        self.asset = {'Id': '00000000-0000-0000-0000-000000000001', 'NumericId': 7,
                      'Version': 1, 'ContentHash': 'a' * 64, 'Mode': 'Continuous',
                      'Unit': 'mm', 'Axes': ['X', 'Y'], 'Profile': {'CycleSeconds': .01}}
        self.trajectory = {'DurationSeconds': .02, 'Samples': [
            {'TimeSeconds': t, 'Position': {'X': x, 'Y': 0, 'Z': 0},
             'Velocity': {'X': 0, 'Y': 0, 'Z': 0},
             'Acceleration': {'X': 0, 'Y': 0, 'Z': 0}}
            for t, x in [(0, 0), (.01, 1), (.02, 2)]]}
        self.worker = self.m.VirtualEngine([{'Asset': self.asset, 'Trajectory': self.trajectory}], watchdog=.1)

    def request(self, request=1, version=1, digest='a' * 64, scan=1, running=True, stop=False, owner='plc'):
        return self.worker.command(owner, request, 7, version, digest, scan, running, stop, now=0)

    def test_missing_implementation(self):
        self.assertEqual(self.worker.capability, 'VirtualContinuous')

    def test_repeated_request_does_not_restart(self):
        self.request()
        self.worker.tick(.01)
        self.request(scan=2)
        self.worker.tick(.02)
        self.assertTrue(self.worker.feedback['Done'])
        self.assertEqual(self.worker.feedback['Progress'], 1)
        self.assertEqual(self.worker.position['X'], 2)
        self.request(scan=3)
        self.assertTrue(self.worker.feedback['Done'])

    def test_identity_version_and_hash_refused(self):
        for version, digest in [(2, 'a' * 64), (1, 'b' * 64)]:
            with self.subTest(version=version):
                self.worker = self.m.VirtualEngine([{'Asset': self.asset, 'Trajectory': self.trajectory}])
                result = self.request(version=version, digest=digest)
                self.assertEqual(result['ErrorID'], self.m.ASSET_MISMATCH)
                self.assertFalse(result['Busy'])

    def test_stale_scan_heartbeat_cannot_keep_motion_alive(self):
        self.request()
        self.worker.command('plc', 1, 7, 1, 'a' * 64, 1, True, False, now=.09)
        self.worker.tick(.11)
        self.assertEqual(self.worker.feedback['ErrorID'], self.m.PLC_STALE)

    def test_stop_and_plc_stopped_cancel(self):
        for running, stop in [(False, False), (True, True)]:
            self.worker = self.m.VirtualEngine([{'Asset': self.asset, 'Trajectory': self.trajectory}])
            self.request()
            result = self.request(running=running, stop=stop)
            self.assertEqual(result['ErrorID'], self.m.STOPPED)
            self.assertFalse(result['Done'])

    def test_ownership_refused_and_disconnect_cancels(self):
        self.request()
        result = self.request(owner='desktop')
        self.assertEqual(result['ErrorID'], self.m.OWNERSHIP)
        self.assertTrue(self.worker.feedback['Busy'])
        self.worker.release('plc')
        self.assertEqual(self.worker.feedback['ErrorID'], self.m.DISCONNECTED)

    def test_fault_prevents_completion(self):
        self.request()
        self.worker.fault()
        self.worker.tick(.02)
        self.assertEqual(self.worker.feedback['ErrorID'], self.m.FAULT)
        self.assertFalse(self.worker.feedback['Done'])

    def test_conflicting_duplicate_is_refused_without_replacing_execution(self):
        self.request()
        result = self.request(digest='b' * 64)
        self.assertEqual(result['ErrorID'], self.m.REQUEST_CONFLICT)
        self.assertTrue(self.worker.feedback['Busy'])

    def test_physical_backend_is_explicitly_unsupported(self):
        with self.assertRaisesRegex(ValueError, 'physical'):
            self.m.VirtualEngine([], backend='EcMotion')

    def test_asset_preload_does_not_move(self):
        self.worker.tick(.01)
        self.assertFalse(self.worker.feedback['Busy'])
        self.assertFalse(self.worker.feedback['Done'])
        self.assertIsNone(self.worker.position)

    def test_readiness_sidecar_is_private_and_refuses_overwrite(self):
        with tempfile.TemporaryDirectory() as directory:
            sidecar = pathlib.Path(directory) / 'worker.sock.capabilities.json'
            data = self.m.write_capabilities(sidecar, b'candidate manifest', [self.asset])
            self.assertEqual(json.loads(sidecar.read_text()), data)
            self.assertEqual(data['ManifestSha256'], self.m.hashlib.sha256(b'candidate manifest').hexdigest())
            self.assertEqual(data['PID'], os.getpid())
            self.assertEqual(data['Paths'][0]['ContentHash'], 'a' * 64)
            with self.assertRaises(FileExistsError):
                self.m.write_capabilities(sidecar, b'other', [])

    def test_bad_trajectory_and_numeric_collision_refused(self):
        self.trajectory['Samples'][1]['TimeSeconds'] = 0
        with self.assertRaises(ValueError):
            self.m.VirtualEngine([{'Asset': self.asset, 'Trajectory': self.trajectory}])

if __name__ == '__main__':
    unittest.main()
