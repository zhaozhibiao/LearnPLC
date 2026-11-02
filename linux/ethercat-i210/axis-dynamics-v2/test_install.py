import contextlib
import hashlib
import importlib.util
import io
import json
import pathlib
import tempfile
import stat
import types
import unittest
from unittest.mock import patch

spec=importlib.util.spec_from_file_location('axis_v2_install',pathlib.Path(__file__).with_name('install.py'))
installer=importlib.util.module_from_spec(spec);spec.loader.exec_module(installer)

class InstallerTests(unittest.TestCase):
    def fixture(self, directory):
        source=pathlib.Path(directory)
        content={name:('reviewed '+name).encode() for name in installer.EXPECTED}
        for name,data in content.items():(source/name).write_bytes(data)
        return source,{name:hashlib.sha256(data).hexdigest() for name,data in content.items()}

    def run_fixture(self, source, expected, capability):
        calls=[]
        def run(args,**kwargs):
            calls.append(args)
            if args[-1]=='--runtime-capabilities':return types.SimpleNamespace(stdout=json.dumps(capability))
            if pathlib.Path(args[-1]).name=='preflight.py':return types.SimpleNamespace(stdout=json.dumps(dict(backup='/root/reviewed-backup')))
            return types.SimpleNamespace(stdout='')
        stack=contextlib.ExitStack();self.addCleanup(stack.close)
        stack.enter_context(patch.object(installer,'SOURCE',source));stack.enter_context(patch.object(installer,'EXPECTED',expected))
        stack.enter_context(patch.object(installer.os,'geteuid',return_value=0,create=True))
        trusted=stack.enter_context(patch.object(installer,'trusted'))
        stack.enter_context(patch.object(installer,'create_broker_backup',return_value='/separate/broker-backup'))
        publish=stack.enter_context(patch.object(installer,'publish'))
        command=stack.enter_context(patch.object(installer.subprocess,'run',side_effect=run))
        stack.enter_context(contextlib.redirect_stdout(io.StringIO()))
        return calls,publish,command,trusted

    def test_hash_mismatch_before_any_mutation_or_preflight(self):
        with tempfile.TemporaryDirectory() as directory:
            source,expected=self.fixture(directory);(source/'restore-runtime.py').write_bytes(b'changed')
            calls,publish,command,trusted=self.run_fixture(source,expected,dict(runtimeCapabilities=list(installer.REQUIRED_CAPABILITIES)))
            with self.assertRaisesRegex(ValueError,'checksum'):installer.install_locked()
            publish.assert_not_called();command.assert_not_called();trusted.assert_not_called()

    def test_wrong_capabilities_cannot_publish_helpers_or_installation_pin(self):
        for actual in [[],['workbench_motion_migration_v1','workbench_axis_trace_v1'],
                       list(installer.REQUIRED_CAPABILITIES)+['extra'],['workbench_axis_move_profile_v2']*4]:
            with self.subTest(actual=actual),tempfile.TemporaryDirectory() as directory:
                source,expected=self.fixture(directory)
                calls,publish,command,trusted=self.run_fixture(source,expected,dict(runtimeCapabilities=actual))
                with self.assertRaisesRegex(ValueError,'capability'):installer.install_locked()
                self.assertEqual(publish.call_count,1)
                self.assertTrue(str(publish.call_args.args[0]).startswith(str(pathlib.Path('/opt/st-workbench-ethercat/ecmotion-rt-'))))
                self.assertEqual(calls[0][:2],['/usr/bin/python3','-I'])
                self.assertEqual(calls[1][-1],'--check')
                self.assertEqual(calls[2][-1],'--runtime-capabilities')
                self.assertFalse(any(c[0]=='systemctl' for c in calls))

    def test_backup_or_rollback_check_failure_prevents_all_publication(self):
        for failure_at in (0,1):
            with self.subTest(failure_at=failure_at),tempfile.TemporaryDirectory() as directory:
                source,expected=self.fixture(directory)
                calls,publish,command,trusted=self.run_fixture(source,expected,dict(runtimeCapabilities=list(installer.REQUIRED_CAPABILITIES)))
                before=command.side_effect
                def fail(args,**kwargs):
                    if len(calls)==failure_at:raise ValueError('backup proof failed')
                    return before(args,**kwargs)
                command.side_effect=fail
                with self.assertRaisesRegex(ValueError,'backup proof'):installer.install_locked()
                publish.assert_not_called()

    def test_success_pins_new_reviewed_release_and_only_restarts_broker(self):
        with tempfile.TemporaryDirectory() as directory:
            source,expected=self.fixture(directory)
            calls,publish,command,trusted=self.run_fixture(source,expected,dict(runtimeCapabilities=list(installer.REQUIRED_CAPABILITIES)))
            installer.install_locked()
            self.assertEqual(publish.call_count,6)
            pin=json.loads(publish.call_args.args[1]);self.assertEqual(pin['binarySha256'],expected['ecmotion-rt'])
            self.assertEqual(calls[-1],['systemctl','try-restart','stworkbench-hardware.service'])
            self.assertFalse(any('st-workbench-is620n.service' in c or any(v in ('start','stop','restart','START','STOP') for v in c) for c in calls))

    def test_release_pins_match_current_helpers_and_unpinned_binary_fails_closed(self):
        root=pathlib.Path(__file__).resolve().parent.parent
        for name in ('apply-runtime-hardware.py','restore-runtime.py','hardware-broker.py'):
            self.assertEqual(installer.EXPECTED[name],hashlib.sha256((root/name).read_bytes()).hexdigest())
        self.assertEqual(installer.EXPECTED['rollback.py'],hashlib.sha256(pathlib.Path(__file__).with_name('rollback.py').read_bytes()).hexdigest())
        self.assertEqual(installer.BINARY_SHA256,'f0de2ddd4c943231099b322041471c8748380175229a7582982b08680511a9b4')
        self.assertEqual(installer.SOURCE.as_posix(),'/home/a/st-workbench-i210/axis-dynamics-v2-release-20261101')
        with self.assertRaisesRegex(ValueError,'pinned'):installer.check_release_pins(dict(installer.EXPECTED,**{'ecmotion-rt':None}))

    def test_separate_broker_backup_is_verified_without_changing_original_inventory(self):
        with tempfile.TemporaryDirectory() as directory:
            base=pathlib.Path(directory);originals=base/'originals';originals.mkdir()
            original=originals/('20261101T120000.000000Z-'+'a'*32);original.mkdir()
            (original/'manifest.json').write_bytes(b'original exact manifest');(original/'files').mkdir()
            old_broker=base/'broker';old_broker.write_bytes(b'old reviewed broker');old_broker.chmod(0o700)
            backups=base/'broker-backups'
            original_read=installer.read_trusted
            def read(path):
                data,info=original_read(path)
                if path==old_broker:info=types.SimpleNamespace(st_uid=0,st_gid=0,st_mode=stat.S_IFREG|0o755)
                return data,info
            with patch.object(installer,'trusted'),patch.object(installer,'read_trusted',side_effect=read),patch.object(installer,'ORIGINAL_BACKUPS',originals), \
                 patch.object(installer,'BROKER_BACKUPS',backups),patch.object(installer,'BROKER',old_broker):
                result=pathlib.Path(installer.create_broker_backup(original))
                _,metadata,data=installer.verify_broker_backup(original)
                self.assertEqual(data,b'old reviewed broker');self.assertEqual(metadata['sourceBackup'],str(original.absolute()))
                self.assertEqual((original/'manifest.json').read_bytes(),b'original exact manifest')
                self.assertEqual({p.name for p in original.iterdir()},{'manifest.json','files'})
                (result/'broker.py').write_bytes(b'tampered')
                with self.assertRaisesRegex(ValueError,'checksum'):installer.verify_broker_backup(original)
                (result/'broker.py').write_bytes(data);(result/'extra').write_bytes(b'extra')
                with self.assertRaisesRegex(ValueError,'content'):installer.verify_broker_backup(original)

    def test_privileged_release_scripts_have_unix_shebang_and_only_lf(self):
        root=pathlib.Path(__file__).resolve().parent.parent
        scripts=[root/name for name in ('apply-runtime-hardware.py','restore-runtime.py','hardware-broker.py')]
        scripts += [pathlib.Path(__file__).with_name(name) for name in ('install.py','rollback.py')]
        for path in scripts:
            with self.subTest(script=path.name):
                raw=path.read_bytes()
                self.assertEqual(raw.splitlines(keepends=True)[0],b'#!/usr/bin/python3 -I\n')
                self.assertNotIn(b'\r',raw)

    def test_exact_capabilities_reject_bad_types_and_extra_fields(self):
        for value in [None,[],{},dict(runtimeCapabilities=True),dict(runtimeCapabilities=[True]*4),
                      dict(runtimeCapabilities=list(installer.REQUIRED_CAPABILITIES),extra=True)]:
            with self.assertRaises(ValueError):installer.check_capabilities(value)

if __name__=='__main__':unittest.main()
