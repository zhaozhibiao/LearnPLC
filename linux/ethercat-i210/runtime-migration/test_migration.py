import importlib.util
import json
import os
import pathlib
import tempfile
import unittest
from unittest import mock


def load(name):
    spec = importlib.util.spec_from_file_location(name, pathlib.Path(__file__).with_name(name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class MigrationTests(unittest.TestCase):
    def test_non_enabled_standard_fault_allows_readonly_backup(self):
        self.p.disabled([0x218,0x218])
        for value in [0x27,0x0f,0,True]:
            with self.assertRaises(ValueError):self.p.disabled([value,value])
    def setUp(self):
        self.p = load('preflight')
        self.r = load('rollback')
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = pathlib.Path(self.tmp.name)
        self.layout = self.p.Layout(self.root, owner=None)
        self.fingerprint = 'a' * 64
        self.binary = '/opt/st-workbench-ethercat/ecmotion-rt-123456abcdef'
        self.put(self.binary, b'original-binary')
        self.put(self.p.INSTALLATION, json.dumps(dict(binary=self.binary, binarySha256=self.p.sha(b'original-binary'))).encode())
        self.put(self.p.ACTIVE, json.dumps(dict(topologyFingerprint=self.fingerprint)).encode())
        for name in self.p.VERSION_FILES:
            self.put(self.p.VERSIONS + '/' + self.fingerprint + '/' + name, b'{}')
        for name in self.p.HELPERS:
            self.put(name, b'# helper')

    def put(self, name, data):
        target = self.layout.path(name)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        target.chmod(0o600)

    def backup(self):
        backup = self.p.create_backup(self.layout, dict(statuswords=[0x40, 0x40], services={},
            positionFeedback=dict(object='0x6064:0', slave=0, samples=[100] * 20,
                sampleMonotonicNs=[1_000_000_000 + i * 25_000_000 for i in range(20)])))
        if os.name == 'nt':
            # Windows chmod exposes only the read-only bit; Linux source metadata is exercised explicitly.
            manifest = json.loads((backup / 'manifest.json').read_text())
            for record in manifest['files']: record['mode'] = 0o600
            (backup / 'manifest.json').write_text(json.dumps(manifest))
        return backup

    def payload(self, backup):
        manifest = json.loads((backup / 'manifest.json').read_text())
        return backup / next(r['payload'] for r in manifest['files'] if r['path'] == self.binary)

    def test_complete_backup_and_restore(self):
        backup = self.backup()
        self.put(self.binary, b'changed')
        self.r.check_backup(backup, self.layout)
        self.r.restore_backup(backup, self.layout, lambda: None)
        self.assertEqual(self.layout.path(self.binary).read_bytes(), b'original-binary')

    def test_corrupt_payload_rejected_before_any_restore(self):
        backup = self.backup()
        self.payload(backup).write_bytes(b'corrupt')
        with self.assertRaises(ValueError): self.r.restore_backup(backup, self.layout, lambda: None)

    def test_missing_payload_rejected(self):
        backup = self.backup()
        self.payload(backup).unlink()
        with self.assertRaises((ValueError, OSError)): self.r.check_backup(backup, self.layout)

    def test_path_escape_rejected(self):
        backup = self.backup()
        manifest = json.loads((backup / 'manifest.json').read_text())
        manifest['files'][0]['path'] = '/etc/../tmp/escape'
        (backup / 'manifest.json').write_text(json.dumps(manifest))
        with self.assertRaises(ValueError): self.r.check_backup(backup, self.layout)

    def test_unlisted_destination_rejected(self):
        backup = self.backup()
        manifest = json.loads((backup / 'manifest.json').read_text())
        manifest['files'][2]['path'] = '/etc/sudoers'
        (backup / 'manifest.json').write_text(json.dumps(manifest))
        with self.assertRaises(ValueError): self.r.check_backup(backup, self.layout)

    def test_nonroot_original_metadata_rejected(self):
        backup = self.backup()
        manifest = json.loads((backup / 'manifest.json').read_text())
        manifest['files'][0]['uid'] = 1000
        (backup / 'manifest.json').write_text(json.dumps(manifest))
        self.layout.owner = 0
        # Skip platform metadata enforcement to isolate the saved original-owner check.
        with mock.patch.object(self.layout, 'secure'), mock.patch.object(self.layout, 'read', side_effect=lambda path: (path.read_bytes(), path.stat())):
            with self.assertRaisesRegex(ValueError, 'Original file'): self.r.check_backup(backup, self.layout)

    def test_incomplete_manifest_rejected(self):
        backup = self.backup()
        manifest = json.loads((backup / 'manifest.json').read_text())
        manifest['files'].pop()
        (backup / 'manifest.json').write_text(json.dumps(manifest))
        with self.assertRaises(ValueError): self.r.check_backup(backup, self.layout)

    def test_payload_traversal_rejected(self):
        backup = self.backup()
        manifest = json.loads((backup / 'manifest.json').read_text())
        manifest['files'][0]['payload'] = '../installation.json'
        (backup / 'manifest.json').write_text(json.dumps(manifest))
        with self.assertRaises(ValueError): self.r.check_backup(backup, self.layout)

    def test_backup_extra_payload_rejected(self):
        backup = self.backup()
        (backup / 'extra').write_bytes(b'extra')
        with self.assertRaises(ValueError): self.r.check_backup(backup, self.layout)

    def test_source_missing_creates_no_complete_backup(self):
        self.layout.path(self.p.HELPERS[0]).unlink()
        with self.assertRaises(OSError): self.backup()
        self.assertFalse(self.layout.path(self.p.BACKUPS).exists())

    def test_symlink_rejected(self):
        backup = self.backup()
        target = self.payload(backup)
        target.unlink()
        try: target.symlink_to(self.layout.path(self.binary))
        except OSError: self.skipTest('OS does not permit symlinks')
        with self.assertRaises(ValueError): self.r.check_backup(backup, self.layout)

    def test_incomplete_backup_cannot_be_used(self):
        with mock.patch.object(self.p, 'write_file', side_effect=OSError('injected failure')):
            with self.assertRaises(OSError): self.backup()
        for path in self.layout.path(self.p.BACKUPS).iterdir():
            with self.assertRaises((ValueError, OSError)): self.r.check_backup(path, self.layout)

    def test_restore_partial_failure_reported(self):
        backup = self.backup()
        with mock.patch.object(self.r.os, 'replace', side_effect=OSError('injected failure')):
            with self.assertRaises(OSError): self.r.restore_backup(backup, self.layout, lambda: None)

    def test_second_replace_failure_can_retry_same_backup(self):
        backup = self.backup()
        manifest = json.loads((backup / 'manifest.json').read_text())
        for record in manifest['files']: self.put(record['path'], b'changed')
        real_replace = self.r.os.replace
        count = 0
        def fail_second(source, destination):
            nonlocal count
            count += 1
            if count == 2: raise OSError('second replacement failed')
            return real_replace(source, destination)
        with mock.patch.object(self.r.os, 'replace', side_effect=fail_second):
            with self.assertRaisesRegex(OSError, 'second replacement'): self.r.restore_backup(backup, self.layout, lambda: None)
        self.assertEqual(count, 2)
        self.r.restore_backup(backup, self.layout, lambda: None)
        for record in manifest['files']:
            self.assertEqual(self.p.sha(self.layout.path(record['path']).read_bytes()), record['sha256'])
        self.assertFalse(list(self.root.rglob('.migration-restore-*')))

    def test_new_unit_at_originally_absent_path_is_rejected(self):
        backup = self.backup()
        self.put(self.p.UNIT_FILES[0], b'[Service]\nExecStart=/bin/true\n')
        with self.assertRaisesRegex(ValueError, 'absent'):
            self.r.restore_backup(backup, self.layout, lambda: None)

    def test_missing_position_audit_blocks_backup(self):
        with self.assertRaisesRegex(ValueError, 'position'):
            self.p.create_backup(self.layout, dict(statuswords=[0x40, 0x40]))

    def test_moving_position_blocks_backup(self):
        feedback = dict(object='0x6064:0', slave=0, samples=list(range(20)),
            sampleMonotonicNs=[1_000_000_000 + i * 25_000_000 for i in range(20)])
        with self.assertRaisesRegex(ValueError, 'position'):
            self.p.create_backup(self.layout, dict(statuswords=[0x40, 0x40], positionFeedback=feedback))

    def test_short_position_observation_is_rejected(self):
        feedback = dict(object='0x6064:0', slave=0, samples=[100] * 20,
            sampleMonotonicNs=list(range(20)))
        with self.assertRaisesRegex(ValueError, 'position'):
            self.p.create_backup(self.layout, dict(statuswords=[0x40, 0x40], positionFeedback=feedback))

    def test_position_observation_collects_twenty_readonly_samples(self):
        timestamps = [1_000_000_000 + i * 25_000_000 for i in range(20)]
        with mock.patch.object(self.p, 'read_position', side_effect=[100, 104] * 10) as read, \
                mock.patch.object(self.p.time, 'monotonic_ns', side_effect=timestamps), \
                mock.patch.object(self.p.time, 'sleep') as sleep:
            feedback = self.p.observe_position()
        self.assertEqual(read.call_count, 20)
        self.assertEqual(sleep.call_args_list, [mock.call(.025)] * 19)
        self.assertEqual(feedback['samples'], [100, 104] * 10)
        self.assertEqual(feedback['sampleMonotonicNs'], timestamps)

    def test_position_upload_is_signed_and_readonly(self):
        response = type('Result', (), dict(stdout='0xffffff9c -100\n', returncode=0))()
        with mock.patch.object(self.p, 'command', return_value=response) as command:
            self.assertEqual(self.p.read_position(), -100)
        self.assertEqual(command.call_args.args[0], ['/opt/etherlab/bin/ethercat', 'upload', '-p', '0', '-t', 'int32', '0x6064', '0'])

    def test_enable_blocks_backup(self):
        with self.assertRaises(ValueError): self.p.create_backup(self.layout, dict(statuswords=[0x27, 0x27], services={}))

    def test_service_guard_runs_before_writes(self):
        backup = self.backup()
        def refuse(): raise ValueError('running')
        with mock.patch.object(self.r.os, 'replace') as replace:
            with self.assertRaises(ValueError): self.r.restore_backup(backup, self.layout, refuse)
            replace.assert_not_called()

    def test_systemd_socket_has_no_main_pid_property(self):
        def response(args):
            unit = args[2]
            output = 'LoadState=loaded\nActiveState=inactive\nSubState=dead\nFragmentPath=\n'
            if unit.endswith('.service'): output += 'MainPID=0\n'
            return type('Result', (), dict(stdout=output, returncode=0))()
        with mock.patch.object(self.p, 'command', side_effect=response):
            self.assertEqual(self.p.service_states()['stworkbench-hardware.socket']['MainPID'], '0')


if __name__ == '__main__': unittest.main()
