import pathlib
import tempfile
import unittest
from coordinator import DeploymentError, motion_candidate_source, verify_motion_ready


class MotionGateTests(unittest.TestCase):
    def test_plain_candidate_preserves_runtime_source(self):
        self.assertEqual(b"old", motion_candidate_source("core/strucpp_runtime/runtime_v4_entry.cpp", b"old", {}))

    def test_motion_only_changes_candidate_entry(self):
        original = b'extern "C" strucpp::ConfigurationInstance* strucpp_get_config(void) {\n    return &g_config;\n}'
        modified = motion_candidate_source("core/strucpp_runtime/runtime_v4_entry.cpp", original, {"MotionInterfaceVersion": "workbench-continuous-v1"})
        self.assertIn(b"workbench_motion_bootstrap()", modified)
        self.assertNotEqual(original, modified)
        with self.assertRaises(DeploymentError):
            motion_candidate_source("core/strucpp_runtime/runtime_v4_entry.cpp", b"unknown", {"MotionInterfaceVersion": "workbench-continuous-v1"})

    def test_missing_physical_capability_refuses_before_device_operations(self):
        with self.assertRaisesRegex(DeploymentError, "physical Continuous"):
            verify_motion_ready({"MotionInterfaceVersion": "workbench-continuous-v1"}, pathlib.Path("unused"), {}, "")

    def test_environment_does_not_silently_select_a_different_worker(self):
        with self.assertRaisesRegex(DeploymentError, "environment"):
            verify_motion_ready({"MotionInterfaceVersion": "workbench-continuous-v1"}, pathlib.Path("unused"), {"motion_backend": "VirtualContinuous", "motion_socket_path": "/tmp/one.sock"}, "STWORKBENCH_MOTION_BACKEND=VirtualContinuous STWORKBENCH_MOTION_SOCKET=/tmp/two.sock")

    def test_absent_worker_refuses(self):
        with tempfile.TemporaryDirectory() as temp:
            socket = "/tmp/wb-st-workbench-missing-worker.sock"
            config = {"motion_backend": "VirtualContinuous", "motion_socket_path": str(socket)}
            with self.assertRaisesRegex(DeploymentError, "unavailable"):
                verify_motion_ready({"MotionInterfaceVersion": "workbench-continuous-v1"}, pathlib.Path(temp), config, f"STWORKBENCH_MOTION_BACKEND=VirtualContinuous STWORKBENCH_MOTION_SOCKET={socket}")
