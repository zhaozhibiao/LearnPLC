"""MIT. A missing transient core after reboot is a confirmed absent baseline."""
import subprocess
import unittest
from unittest import mock
import coordinator
import ethercat_hardware as hardware


class NativeServiceStateTests(unittest.TestCase):
    def query(self, output, code=0):
        def run(args, **options):
            if code and options.get('check'):
                raise subprocess.CalledProcessError(code, args, output=output)
            return subprocess.CompletedProcess(args, code, output, '')
        return mock.patch.object(coordinator.subprocess, 'run', side_effect=run)

    def test_absent_transient_unit_is_zero_pid(self):
        with self.query('LoadState=not-found\nActiveState=inactive\nMainPID=0\n', 1):
            self.assertEqual(0, coordinator.Coordinator.native_core_pid())

    def test_running_unit_preserves_pid(self):
        with self.query('LoadState=loaded\nActiveState=active\nMainPID=123\n'):
            self.assertEqual(123, coordinator.Coordinator.native_core_pid())

    def test_unknown_or_contradictory_state_is_not_an_absent_core(self):
        for output, code in [('MainPID=0\n', 0), ('LoadState=not-found\nActiveState=active\nMainPID=12\n', 1),
                             ('LoadState=loaded\nActiveState=active\nMainPID=oops\n', 0),
                             ('LoadState=loaded\nActiveState=inactive\nMainPID=0\n', 1)]:
            with self.subTest(output=output), self.query(output, code):
                with self.assertRaises(coordinator.DeploymentError): coordinator.Coordinator.native_core_pid()

    def test_missing_controller_has_specific_safe_diagnostic(self):
        with mock.patch.object(hardware.socket, 'AF_UNIX', 1, create=True), mock.patch.object(hardware.socket, 'socket') as socket:
            socket.return_value.__enter__.return_value.connect.side_effect = FileNotFoundError('private path')
            with self.assertRaisesRegex(ValueError, 'Native controller socket unavailable'):
                hardware.native_request('/run/private.sock', 'HelloRequest', {}, 'HelloResponse')
