import importlib.util
import pathlib
import unittest
from unittest.mock import patch

spec=importlib.util.spec_from_file_location('axis_v2_rollback',pathlib.Path(__file__).with_name('rollback.py'))
rollback=importlib.util.module_from_spec(spec);spec.loader.exec_module(rollback)

class RollbackTests(unittest.TestCase):
    def fixture(self):
        original=pathlib.Path('/var/backups/st-workbench-runtime/20261101T120000.000000Z-'+'a'*32)
        metadata=dict(mode=0o755,uid=0,gid=1000)
        return original,metadata,b'original broker'

    def test_check_verifies_both_backups_without_publication(self):
        original,metadata,data=self.fixture()
        with patch.object(rollback,'verify_broker_backup',return_value=(original,metadata,data)),patch.object(rollback,'trusted'), \
             patch.object(rollback.subprocess,'run') as command,patch.object(rollback,'publish') as publish:
            self.assertEqual(rollback.rollback_locked(original),'verified-no-files-restored')
            publish.assert_not_called();self.assertEqual(command.call_count,1)
            self.assertEqual(command.call_args.args[0][-1],'--check')

    def test_apply_restores_original_inventory_then_broker_and_only_reloads_broker(self):
        original,metadata,data=self.fixture()
        with patch.object(rollback,'verify_broker_backup',return_value=(original,metadata,data)) as verify,patch.object(rollback,'trusted'), \
             patch.object(rollback.subprocess,'run') as command,patch.object(rollback,'publish') as publish, \
             patch.object(rollback.os,'chown',create=True) as chown:
            rollback.rollback_locked(original,True)
            self.assertEqual(verify.call_count,2)
            publish.assert_called_once_with(rollback.BROKER,data,0o755);chown.assert_called_once_with(rollback.BROKER,0,1000)
            calls=[c.args[0] for c in command.call_args_list]
            self.assertEqual(calls[0][-1],'--check');self.assertEqual(calls[1][-1],'--apply')
            self.assertEqual(calls[2],['systemctl','try-restart','stworkbench-hardware.service'])
            self.assertFalse(any(any(x in ('start','stop','restart','START','STOP','Enable') for x in c) for c in calls))

    def test_original_restore_failure_never_publishes_or_reloads_broker(self):
        original,metadata,data=self.fixture()
        with patch.object(rollback,'verify_broker_backup',return_value=(original,metadata,data)),patch.object(rollback,'trusted'), \
             patch.object(rollback.subprocess,'run',side_effect=[None,ValueError('guard requires stopped native')]) as command, \
             patch.object(rollback,'publish') as publish:
            with self.assertRaisesRegex(ValueError,'stopped'):rollback.rollback_locked(original,True)
            publish.assert_not_called();self.assertEqual(command.call_count,2)

    def test_invalid_supplement_prevents_original_restore(self):
        with patch.object(rollback,'verify_broker_backup',side_effect=ValueError('checksum')), \
             patch.object(rollback.subprocess,'run') as command,patch.object(rollback,'publish') as publish:
            with self.assertRaisesRegex(ValueError,'checksum'):rollback.rollback_locked('/invalid',True)
            command.assert_not_called();publish.assert_not_called()

if __name__=='__main__':unittest.main()
