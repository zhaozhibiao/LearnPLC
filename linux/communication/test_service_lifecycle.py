import importlib.util
import pathlib
import tempfile
import unittest
import contextlib
import types
import sys
from unittest.mock import Mock,patch


class LifecycleTests(unittest.TestCase):
    def test_units_reset_worker_before_every_runtime_start(self):
        spec=importlib.util.spec_from_file_location('install_client',pathlib.Path(__file__).with_name('install-client.py'))
        module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        service,dropin=module.units('/home/test/private/communication')
        self.assertIn('run-selected',service)
        self.assertIn('Restart=no',service)
        self.assertIn('start-active',dropin)
        self.assertIn('ExecStopPost=',dropin)
        self.assertIn('STWORKBENCH_COMM_SOCKET=',dropin)

    def test_selection_resolves_only_valid_build_identity(self):
        import service_lifecycle as lifecycle
        with tempfile.TemporaryDirectory() as d:
            root=pathlib.Path(d)
            with self.assertRaises(ValueError):lifecycle.candidate(root,'../escape')
            self.assertEqual(root/'.versions'/('A'*64)/'core/generated',lifecycle.candidate(root,'a'*64))

    def test_preparation_requires_live_runtime_and_confirmed_stopped_plc(self):
        import service_lifecycle as lifecycle
        for active,stopped in ((False,False),(True,False),(True,True)):
            owner=Mock();owner.lock.return_value=contextlib.nullcontext();owner.connection.return_value=contextlib.nullcontext(Mock())
            if not stopped:owner.stopped.side_effect=ValueError('PLC is RUNNING')
            factory=Mock(return_value=owner)
            with self.subTest(active=active,stopped=stopped),patch.dict(sys.modules,{'coordinator':types.SimpleNamespace(Coordinator=factory)}),patch.object(lifecycle,'control',return_value=types.SimpleNamespace(returncode=0 if active else 3)),patch.object(lifecycle,'reset') as reset:
                if active and stopped:
                    lifecycle.prepare(pathlib.Path('/private'),{'coordinator_directory':'/installed'},'A'*64)
                    reset.assert_called_once()
                else:
                    with self.assertRaises(ValueError):lifecycle.prepare(pathlib.Path('/private'),{'coordinator_directory':'/installed'},'A'*64)
                    reset.assert_not_called()


if __name__=='__main__':unittest.main()
