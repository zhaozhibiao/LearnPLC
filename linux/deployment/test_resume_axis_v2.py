"""Runtime management recovery starts only the verified active v2 bridge, never PLC START."""
import contextlib
import hashlib
import json
import pathlib
import tempfile
import unittest
from unittest import mock
import axis_gate
import coordinator
import configuration_download as configuration
from test_axis_gate_v2 import manifest
class ResumeTests(unittest.TestCase):
 def test_resume_starts_v2_after_runtime_before_http_and_keeps_plc_stopped(self):
  with tempfile.TemporaryDirectory() as directory:
   root=pathlib.Path(directory);c=coordinator.Coordinator(dict(prefix=str(root/'prefix'),runtime_source=str(root/'runtime'),runtime_commit=coordinator.RUNTIME_COMMIT,exclusive_control=True,control_transport='rest'))
   events=[]
   @contextlib.contextmanager
   def connection():
    events.append('http');yield mock.Mock()
   with mock.patch.object(c,'resume_axis_v2_version',return_value=root/'version'),mock.patch.object(c,'service',side_effect=lambda action:events.append('runtime:'+action)),mock.patch.object(configuration,'service',side_effect=lambda action,version:events.append('v2:'+action)),mock.patch.object(configuration,'wait_ready',side_effect=lambda *args:events.append('axis-ready')),mock.patch.object(c,'connection',side_effect=connection),mock.patch.object(c,'stopped',side_effect=lambda *args:events.append('stopped')):
    c.resume_service();self.assertEqual(['runtime:start','v2:start','axis-ready','http','stopped'],events)
 def test_v1_resume_does_not_start_v2(self):
  with tempfile.TemporaryDirectory() as directory:
   root=pathlib.Path(directory);c=coordinator.Coordinator(dict(prefix=str(root/'prefix'),runtime_source=str(root/'runtime'),runtime_commit=coordinator.RUNTIME_COMMIT,exclusive_control=True,control_transport='rest'))
   with mock.patch.object(c,'resume_axis_v2_version',return_value=None),mock.patch.object(c,'service'),mock.patch.object(configuration,'service') as service,mock.patch.object(c,'connection') as connection,mock.patch.object(c,'stopped'):
    c.resume_service();service.assert_not_called()
 def test_active_v2_preflight_rejects_old_core_before_runtime_start(self):
  with tempfile.TemporaryDirectory() as directory:
   root=pathlib.Path(directory);c=coordinator.Coordinator(dict(prefix=str(root/'prefix'),runtime_source=str(root/'runtime'),runtime_commit=coordinator.RUNTIME_COMMIT,exclusive_control=True,control_transport='rest'))
   active=dict(build_hash='A'*64);coordinator.atomic_json(c.active_path,active)
   version=c.version('A'*64);version.mkdir(parents=True);coordinator.atomic_json(version/'stage-result.json',dict(manifest=dict(AxisInterfaceVersion=axis_gate.INTERFACE_V2)))
   generated=version/'core/generated';generated.mkdir(parents=True);(generated/'axis-manifest.json').write_text(json.dumps(manifest()))
   with mock.patch.object(configuration,'candidate',return_value=(generated,dict(AxisInterfaceVersion=axis_gate.INTERFACE_V2),{})),mock.patch.object(c,'verify_active'),mock.patch.object(configuration,'native_state',return_value=dict(runtimeReady=True,runtimeCapabilities=[])),mock.patch.object(c,'service') as service,mock.patch.object(configuration,'service') as axis_service:
    with self.assertRaisesRegex(coordinator.DeploymentError,'v2'):c.resume_service()
    service.assert_not_called();axis_service.assert_not_called()
 def test_verified_active_v2_resources_admitted_and_tampering_rejected(self):
  with tempfile.TemporaryDirectory() as directory:
   root=pathlib.Path(directory);c=coordinator.Coordinator(dict(prefix=str(root/'prefix'),runtime_source=str(root/'runtime'),runtime_commit=coordinator.RUNTIME_COMMIT,exclusive_control=True,control_transport='rest'))
   active=dict(build_hash='A'*64,library_sha256='b'*64);coordinator.atomic_json(c.active_path,active)
   version=c.version('A'*64);version.mkdir(parents=True);coordinator.atomic_json(version/'stage-result.json',dict(manifest=dict(AxisInterfaceVersion=axis_gate.INTERFACE_V2)))
   generated=version/'core/generated';(generated/'axes').mkdir(parents=True)
   target=root/'private';target.mkdir(mode=0o700)
   raw=json.dumps(manifest()).encode();worker=b'verified worker'
   for path,data in [(generated/'axis-manifest.json',raw),(generated/'axes/axis_worker_v2.py',worker),(target/'axis-manifest.json',raw),(target/'axis_worker_v2.py',worker)]:path.write_bytes(data);path.chmod(0o600)
   hello=dict(runtimeReady=True,runtimeCapabilities=list(axis_gate.PROFILE_CAPABILITIES))
   with mock.patch.object(configuration,'candidate',return_value=(generated,dict(AxisInterfaceVersion=axis_gate.INTERFACE_V2),dict(library_sha256='b'*64))),mock.patch.object(c,'verify_active'),mock.patch.object(configuration,'native_state',return_value=hello),mock.patch.object(configuration,'validate_config'),mock.patch.object(configuration,'manifest_destination',return_value=target/'axis-manifest.json'),mock.patch.object(coordinator.os,'getuid',return_value=(target/'axis-manifest.json').stat().st_uid,create=True),mock.patch.object(coordinator.stat,'S_IMODE',return_value=0o600):
    self.assertEqual(version,c.resume_axis_v2_version())
    (target/'axis_worker_v2.py').write_bytes(b'changed worker')
    with self.assertRaisesRegex(coordinator.DeploymentError,'differs'):c.resume_axis_v2_version()
if __name__=='__main__':unittest.main()
