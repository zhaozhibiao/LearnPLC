import hashlib
import importlib.util
import io
import json
import os
import pathlib
import tempfile
import unittest
from unittest import mock


def load(name):
    spec = importlib.util.spec_from_file_location(name.replace('-', '_'), pathlib.Path(__file__).with_name(name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class UserMigrationTests(unittest.TestCase):
    def setUp(self):
        self.s = load('user-root-snapshot')
        self.r = load('user-rollback')
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.paths = self.s.Paths(pathlib.Path(self.tmp.name), owner=None, source_uid=None)
        self.source = self.paths.path(self.s.SOURCES + '/20261030T100000Z-' + 'a' * 32)
        self.source.mkdir(parents=True)
        self.data = b'private-token-never-print'
        self.target = '/home/a/st-workbench-deployment/config.json'
        (self.source / '0').write_bytes(self.data)
        self.manifest = json.dumps(dict(schemaVersion=1, plcState='STOPPED', files=[dict(
            path=self.target, payload='0', sha256=hashlib.sha256(self.data).hexdigest(), mode=0o600)])).encode()
        (self.source / 'manifest.json').write_bytes(self.manifest)
        self.expected = hashlib.sha256(self.manifest).hexdigest()

    def archive(self):
        return self.s.create_snapshot(self.source, self.expected, self.paths)

    def test_roundtrip_archive_and_verified_restore_data(self):
        archive = self.archive()
        records, payloads = self.r.check_archive(archive, self.paths)
        self.assertEqual(records[0]['path'], self.target)
        self.assertEqual(payloads, [self.data])
        self.assertEqual((archive / 'manifest.json').read_bytes(), self.manifest)

    def test_wrong_manifest_anchor_rejected(self):
        with self.assertRaises(ValueError): self.s.create_snapshot(self.source, 'b' * 64, self.paths)
        self.assertFalse(self.paths.path(self.s.ARCHIVES).exists())

    def test_corrupt_source_payload_rejected(self):
        (self.source / '0').write_bytes(b'corrupt')
        with self.assertRaises(ValueError): self.archive()

    def test_corrupt_root_payload_rejected(self):
        archive = self.archive()
        (archive / '0').write_bytes(b'corrupt')
        with self.assertRaises(ValueError): self.r.check_archive(archive, self.paths)

    def test_target_outside_home_rejected(self):
        changed = json.loads(self.manifest)
        changed['files'][0]['path'] = '/etc/sudoers'
        raw = json.dumps(changed).encode()
        (self.source / 'manifest.json').write_bytes(raw)
        with self.assertRaises(ValueError): self.s.create_snapshot(self.source, hashlib.sha256(raw).hexdigest(), self.paths)

    def test_root_partial_copy_has_no_complete_marker(self):
        with mock.patch.object(self.s, 'write_private', side_effect=OSError('copy failure')):
            with self.assertRaises(OSError): self.archive()
        for archive in self.paths.path(self.s.ARCHIVES).iterdir():
            with self.assertRaises((ValueError, OSError)): self.r.check_archive(archive, self.paths)

    def test_manifest_and_payload_each_read_once(self):
        real_read = self.paths.read
        calls = []
        def read(path, root=False):
            calls.append(path)
            return real_read(path, root=root)
        with mock.patch.object(self.paths, 'read', side_effect=read): self.archive()
        self.assertEqual(calls.count(self.source / 'manifest.json'), 1)
        self.assertEqual(calls.count(self.source / '0'), 1)

    def test_source_symlink_refused(self):
        other = self.source / 'other'
        other.write_bytes(self.data)
        (self.source / '0').unlink()
        try: (self.source / '0').symlink_to(other)
        except OSError: self.skipTest('OS does not permit symlinks')
        with self.assertRaises((ValueError, OSError)): self.archive()

    def test_archive_extra_file_refused(self):
        archive = self.archive()
        (archive / 'extra').write_bytes(b'extra')
        with self.assertRaises(ValueError): self.r.check_archive(archive, self.paths)

    def test_child_contains_stopped_gate_and_fixed_identity(self):
        self.assertIn("request.get('plcState') != 'STOPPED'", self.r.CHILD)
        self.assertIn('os.geteuid() != 1000', self.r.CHILD)
        self.assertIn("'inactive', 'failed'", self.r.CHILD)
        self.assertNotIn('systemctl\', \'start', self.r.CHILD)

    def test_restore_passes_cached_bytes_to_fixed_unprivileged_child(self):
        archive = self.archive()
        account = type('Account', (), dict(pw_gid=1000))()
        result = type('Result', (), dict(returncode=0))()
        runner = mock.Mock(return_value=result)
        self.r.restore_archive(archive, self.paths, account, runner=runner, disabled_guard=lambda: None)
        args, kwargs = runner.call_args
        self.assertEqual(args[0][:3], ['/usr/bin/python3', '-I', '-c'])
        self.assertEqual((kwargs['user'], kwargs['group'], kwargs['extra_groups']), (1000, 1000, []))
        self.assertNotIn(str(archive), kwargs['input'])
        request = json.loads(kwargs['input'])
        self.assertEqual(request['plcState'], 'STOPPED')
        self.assertEqual(request['files'][0]['path'], self.target)

    def test_child_refuses_active_service_before_opening_any_restore_path(self):
        result = type('Result', (), dict(returncode=0, stdout='LoadState=loaded\nActiveState=active\nSubState=running\nMainPID=12\n'))()
        with mock.patch('sys.stdin', io.StringIO(json.dumps(dict(plcState='STOPPED', files=[])))), \
                mock.patch.object(os, 'geteuid', return_value=1000, create=True), \
                mock.patch.object(os, 'getuid', return_value=1000, create=True), \
                mock.patch.object(os, 'getgroups', return_value=[], create=True), \
                mock.patch.object(os, 'umask'), \
                mock.patch.object(self.r.subprocess, 'run', return_value=result), \
                mock.patch.object(os, 'open') as opening:
            with self.assertRaisesRegex(ValueError, 'explicitly stopped'): exec(compile(self.r.CHILD, '<fixed-restore-child>', 'exec'), {})
            opening.assert_not_called()

    def test_enabled_axis_blocks_child_dispatch(self):
        archive = self.archive()
        runner = mock.Mock()
        def refuse(): raise ValueError('enabled axis')
        with self.assertRaisesRegex(ValueError, 'enabled axis'):
            self.r.restore_archive(archive, self.paths, type('Account', (), dict(pw_gid=1000))(), runner=runner, disabled_guard=refuse)
        runner.assert_not_called()

    @unittest.skipUnless(os.open in os.supports_dir_fd, 'Linux directory-handle restore test')
    def test_child_restores_in_isolated_home_with_all_services_stopped(self):
        import base64
        home = self.paths.path('/home/a')
        home.mkdir(parents=True, exist_ok=True)
        request = dict(plcState='STOPPED', files=[dict(path=self.target, mode=0o600,
            sha256=hashlib.sha256(self.data).hexdigest(), data=base64.b64encode(self.data).decode())])
        response = type('Result', (), dict(returncode=0, stdout='LoadState=loaded\nActiveState=inactive\nSubState=dead\nMainPID=0\n'))()
        real_open = os.open
        def redirect(path, *args, **kwargs): return real_open(home if path == '/home/a' else path, *args, **kwargs)
        with mock.patch('sys.stdin', io.StringIO(json.dumps(request))), mock.patch('sys.stdout', io.StringIO()), \
                mock.patch.object(os, 'geteuid', return_value=1000), mock.patch.object(os, 'getuid', return_value=1000), \
                mock.patch.object(os, 'getgroups', return_value=[]), mock.patch.object(os, 'umask'), \
                mock.patch.object(self.r.subprocess, 'run', return_value=response) as services, mock.patch.object(os, 'open', side_effect=redirect):
            exec(compile(self.r.CHILD, '<fixed-restore-child>', 'exec'), {})
        self.assertEqual(self.paths.path(self.target).read_bytes(), self.data)
        self.assertEqual(services.call_count, 6)

    def test_source_parent_namespace_cannot_be_overridden(self):
        with self.assertRaises(ValueError): self.s.create_snapshot(self.source.parent.parent / self.source.name, self.expected, self.paths)

    def test_root_archive_manifest_tamper_is_rejected(self):
        archive = self.archive()
        (archive / 'manifest.json').write_bytes(b'{}')
        with self.assertRaises(ValueError): self.r.check_archive(archive, self.paths)


if __name__ == '__main__': unittest.main()
